// Host-shared source: Host~/Build-Host.ps1 also compiles this file with Windows PowerShell 5.1 Add-Type (C# 5).
// Keep it C# 5 compatible (no string interpolation, ?., nameof, expression-bodied members, etc.). See README "호스트 빌드 제약".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UnityTerminal
{
    // This executable owns all process/PTY handles and parser state. It never loads Unity.
    public static class TerminalSessionHost
    {
        public static int Main(string[] args)
        {
            if (args.Length != 6) return 2;
            try
            {
                using (var owner = Process.GetProcessById(int.Parse(args[1])))
                using (var mutex = new Mutex(false, "Local\\" + args[0]))
                {
                    if (owner.StartTime.ToUniversalTime().Ticks != long.Parse(args[2])) return 3;
                    try { if (!mutex.WaitOne(0)) return 0; } catch (AbandonedMutexException) { }
                    try { using (var server = new Server(args[0], args[3], args[4], args[5])) server.Run(owner); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch { return 1; }
        }

        private sealed class Server : IDisposable
        {
            private readonly object gate = new object();
            private readonly string pipeName, executable;
            private readonly TerminalBuffer buffer = new TerminalBuffer();
            private readonly AnsiParser stdout, stderr;
            private readonly int hostPid;
            private readonly long hostStart;
            private ShellSession primary;
            private SafeFileHandle primaryLifetime;
            private ConPtySession interactive;
            private TerminalScreen screen;
            private NamedPipeServerStream pipe;
            private Thread acceptThread;
            private Thread diagnosticsThread;
            // Progress marks read by the diagnostics thread without the gate.
            private long lastPumpTicks, lastFrameTicks;
            private Thread primaryWriter;
            private readonly object inputGate = new object();
            private readonly Queue<string> primaryInput = new Queue<string>();
            private int inputCharacters;
            private volatile bool stopping;
            private string appId = "", failure = "", appFailure = "", notice = "";
            private bool primaryExited, appExited;
            private int primaryExitCode, appExitCode;
            private long acknowledged;
            private int encodedBufferVersion = -1, encodedScreenVersion = -1;
            private byte[] encodedBuffer, encodedScreen;
            // Diagnostics for Tools > Unity Terminal > Capture Diagnostics. Unity asks through a request file
            // (not a protocol command): an older host would treat an unknown command as a transport failure,
            // which ends the whole session. A host without this code simply ignores the file.
            private const int RawTailLimit = 65536, CommandLogLimit = 300;
            private readonly StringBuilder rawTail = new StringBuilder();
            private readonly Queue<string> commandLog = new Queue<string>();
            private long rawTotal;
            private string diagnosticsRequest;
            // The command being applied right now (read without the gate by the diagnostics writer).
            private volatile string applyingCommand;
            private long applyingSinceTicks;

            internal Server(string name, string file, string shell, string directory)
            {
                pipeName = name; executable = file;
                using (var process = Process.GetCurrentProcess()) { hostPid = process.Id; hostStart = process.StartTime.ToUniversalTime().Ticks; }
                diagnosticsRequest = Path.Combine(Path.Combine(Path.GetTempPath(), "UnityTerminal"), "diagnostics-" + hostPid + ".request");
                stdout = new AnsiParser(buffer); stderr = new AnsiParser(buffer, true);
                Message("[System] Starting " + shell + " session in:\n" + directory + "\n");
                try { primary = ShellSession.StartExecutable(file, directory); primaryLifetime = ConPtySession.ProtectProcessLifetime(primary.ProcessId); }
                catch (Exception ex) { failure = "Unable to start PowerShell: " + ex.Message; }
                WorkingDirectory = directory;
            }
            private string WorkingDirectory;

            internal void Run(Process owner)
            {
                primaryWriter = new Thread(WritePrimary) { IsBackground = true, Name = "Terminal host primary input" };
                primaryWriter.Start();
                acceptThread = new Thread(Accept) { IsBackground = true, Name = "Terminal host transport" };
                acceptThread.Start();
                // Its own thread, so a request is still answered while the main loop or Apply holds the gate.
                diagnosticsThread = new Thread(WatchDiagnostics) { IsBackground = true, Name = "Terminal host diagnostics" };
                diagnosticsThread.Start();
                var check = Stopwatch.StartNew();
                while (!stopping)
                {
                    if (check.ElapsedMilliseconds >= 500) { if (owner.HasExited) break; check.Restart(); }
                    lock (gate) Pump();
                    Interlocked.Exchange(ref lastPumpTicks, DateTime.UtcNow.Ticks);
                    Thread.Sleep(10);
                }
            }
            private void Pump()
            {
                if (primary != null)
                {
                    ShellSession.OutputChunk chunk;
                    int characters = 0;
                    while (characters < 65536 && primary.TryDequeue(out chunk))
                    { (chunk.IsError ? stderr : stdout).Feed(chunk.Text); characters += chunk.Text.Length; }
                    WorkingDirectory = WorkingDirectoryForApp;
                    int code;
                    if (!primaryExited && primary.TryGetExitCode(out code)) { primaryExited = true; primaryExitCode = code; }
                }
                if (interactive != null)
                {
                    string text; int characters = 0;
                    while (characters < 65536 && interactive.TryDequeue(out text)) { RecordRaw(text); screen.Feed(text); characters += text.Length; }
                    int code;
                    if (interactive.TryGetExitCode(out code))
                    {
                        appExitCode = code; appExited = true; interactive.Dispose(); interactive = null;
                    }
                    else if (interactive.Failure != null)
                    {
                        appFailure = interactive.Failure; appExited = true; appExitCode = 1; interactive.Dispose(); interactive = null;
                    }
                }
            }
            private void Accept()
            {
                while (!stopping)
                {
                    try
                    {
                        var security = new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        using (var connected = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous, 65536, 65536, security))
                        {
                            pipe = connected;
                            connected.WaitForConnection();
                            while (!stopping && connected.IsConnected)
                            {
                                int knownBuffer, knownScreen; string knownApp;
                                List<HostCommand> commands;
                                using (var reader = TerminalHostProtocol.ReadFrame(connected))
                                    commands = TerminalHostProtocol.ReadCommands(reader, out knownBuffer, out knownScreen, out knownApp);
                                HostState state;
                                bool shutdown;
                                lock (gate)
                                {
                                    shutdown = false;
                                    foreach (HostCommand command in commands)
                                    {
                                        if (command.Sequence <= acknowledged) continue;
                                        Interlocked.Exchange(ref applyingSinceTicks, DateTime.UtcNow.Ticks);
                                        applyingCommand = command.Kind + " seq=" + command.Sequence;
                                        try { Apply(command); }
                                        finally { applyingCommand = null; }
                                        acknowledged = command.Sequence;
                                        if (command.Kind == HostCommandKind.Shutdown) shutdown = true;
                                    }
                                    Pump();
                                    state = Snapshot(knownBuffer, knownScreen, knownApp);
                                }
                                TerminalHostProtocol.WriteFrame(connected, writer => TerminalHostProtocol.WriteState(writer, state));
                                Interlocked.Exchange(ref lastFrameTicks, DateTime.UtcNow.Ticks);
                                if (shutdown) stopping = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException) && !(ex is ObjectDisposedException) && !(ex is InvalidOperationException))
                            lock (gate) failure = "Terminal transport: " + ex.Message;
                    }
                    finally { pipe = null; }
                }
            }
            private void Apply(HostCommand command)
            {
                string error;
                notice = "";
                LogCommand(command);
                switch (command.Kind)
                {
                    case HostCommandKind.PrimaryInput:
                        lock (inputGate)
                        {
                            if (inputCharacters + command.Text.Length > 262144) Message("\n[System Error] Terminal input is full.\n");
                            else { primaryInput.Enqueue(command.Text); inputCharacters += command.Text.Length; Monitor.PulseAll(inputGate); }
                        }
                        break;
                    case HostCommandKind.StartInteractive:
                        if (interactive != null) { interactive.Dispose(); interactive = null; }
                        appId = command.Id; appExited = false; appExitCode = 0; appFailure = "";
                        encodedScreenVersion = -1; encodedScreen = null;
                        try
                        {
                            screen = new TerminalScreen(command.Columns, command.Rows);
                            interactive = ConPtySession.StartProcess(executable, "-NoLogo -Command " + ConPtySession.QuoteArgument(command.Text),
                                WorkingDirectoryForApp, command.Columns, command.Rows);
                            screen.Reply = reply => { string ignored; if (interactive != null) interactive.TrySend(reply, out ignored); };
                        }
                        catch (Exception ex) { appFailure = ex.Message; appExited = true; appExitCode = 1; }
                        break;
                    case HostCommandKind.InteractiveInput:
                        if (command.Id == appId && interactive != null && !interactive.TrySend(command.Text, out error)) notice = error;
                        break;
                    case HostCommandKind.Resize:
                        if (command.Id == appId && interactive != null)
                        {
                            try { interactive.Resize(command.Columns, command.Rows); screen.Resize(command.Columns, command.Rows); }
                            catch (Exception ex) { notice = "Resize: " + ex.Message; }
                        }
                        break;
                    case HostCommandKind.StopInteractive:
                        if (command.Id == appId && interactive != null) { interactive.Dispose(); interactive = null; appExited = true; }
                        break;
                    case HostCommandKind.ClearPrimary: buffer.Clear(); break;
                    case HostCommandKind.SystemMessage: Message(command.Text); break;
                    case HostCommandKind.Shutdown: break;
                    default: throw new InvalidDataException("Unknown terminal command.");
                }
            }
            // Updated from the live PowerShell prompt; command arguments remain untouched.
            private string WorkingDirectoryForApp
            {
                get
                {
                    string prompt = buffer.GetLastLineText().Trim();
                    int start = prompt.StartsWith("PS ", StringComparison.Ordinal) ? 3 : 0;
                    int end = prompt.LastIndexOf('>');
                    if (end > start)
                    {
                        string directory = prompt.Substring(start, end - start).Trim();
                        if (Directory.Exists(directory)) return directory;
                    }
                    return WorkingDirectory;
                }
            }
            private HostState Snapshot(int knownBuffer, int knownScreen, string knownApp)
            {
                var state = new HostState { HostPid = hostPid, HostStartTicks = hostStart, PrimaryPid = primary == null ? 0 : primary.ProcessId,
                    InteractivePid = interactive == null ? 0 : interactive.ProcessId, Acknowledged = acknowledged,
                    BufferVersion = buffer.Version, ScreenVersion = screen == null ? -1 : screen.Version,
                    PrimaryExited = primaryExited, PrimaryExitCode = primaryExitCode, InteractiveActive = interactive != null,
                    InteractiveExited = appExited, InteractiveExitCode = appExitCode, InteractiveId = appId, Failure = failure, InteractiveFailure = appFailure, Notice = notice };
                if (knownBuffer != buffer.Version)
                {
                    if (encodedBufferVersion != buffer.Version) { encodedBuffer = TerminalHostProtocol.Encode(buffer.WriteDisplayState); encodedBufferVersion = buffer.Version; }
                    state.Buffer = encodedBuffer;
                }
                if (screen != null && (knownApp != appId || knownScreen != screen.Version))
                {
                    if (encodedScreenVersion != screen.Version) { encodedScreen = TerminalHostProtocol.Encode(screen.WriteDisplayState); encodedScreenVersion = screen.Version; }
                    state.Screen = encodedScreen;
                }
                return state;
            }
            private void Message(string message) { buffer.Append(message, new TerminalStyle { Color = "#808080", Italic = true }); }

            // Called with gate held (Pump).
            private void RecordRaw(string text)
            {
                rawTail.Append(text);
                rawTotal += text.Length;
                if (rawTail.Length > RawTailLimit) rawTail.Remove(0, rawTail.Length - RawTailLimit);
            }

            // Called with gate held (Apply). Input text is kept short; it shows which keys reached the host.
            private void LogCommand(HostCommand command)
            {
                string text = command.Text ?? "";
                string preview = text.Length > 48 ? text.Substring(0, 48) + "..." : text;
                commandLog.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + " seq=" + command.Sequence + " " + command.Kind +
                    " id=" + (command.Id ?? "") + " size=" + command.Columns + "x" + command.Rows + " chars=" + text.Length + " text=[" + Escape(preview) + "]");
                while (commandLog.Count > CommandLogLimit) commandLog.Dequeue();
            }

            private static string Escape(string text)
            {
                var escaped = new StringBuilder(text.Length + 16);
                foreach (char c in text)
                {
                    if (c == '\x1b') escaped.Append("\\e");
                    else if (c == '\n') escaped.Append("\\n\n");
                    else if (c == '\r') escaped.Append("\\r");
                    else if (c < ' ' || c == '\x7f') escaped.Append("\\x").Append(((int)c).ToString("x2"));
                    else escaped.Append(c);
                }
                return escaped.ToString();
            }

            // Diagnostics thread: Unity writes the target path into diagnostics-<host pid>.request.
            private void WatchDiagnostics()
            {
                while (!stopping)
                {
                    Thread.Sleep(250);
                    try { WriteDiagnosticsIfRequested(); }
                    catch (Exception) { } // Diagnostics must never take the host down.
                }
            }

            private void WriteDiagnosticsIfRequested()
            {
                if (!File.Exists(diagnosticsRequest)) return;
                string target;
                try { target = File.ReadAllText(diagnosticsRequest).Trim(); File.Delete(diagnosticsRequest); }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                if (target.Length == 0) return;
                var text = new StringBuilder();
                long now = DateTime.UtcNow.Ticks;
                // First what can be read without the gate, so it is reported even when the gate is stuck.
                string busy = applyingCommand;
                text.AppendLine("host pid=" + hostPid + " pipe=" + pipeName + " shell=" + executable);
                text.AppendLine("applying=" + (busy == null ? "none" : "[" + busy + "] for " +
                    ((now - Interlocked.Read(ref applyingSinceTicks)) / TimeSpan.TicksPerMillisecond) + " ms") +
                    " mainLoopLastPump=" + Ago(Interlocked.Read(ref lastPumpTicks), now) + " lastFrameToUnity=" + Ago(Interlocked.Read(ref lastFrameTicks), now));
                NamedPipeServerStream currentPipe = pipe;
                bool clientConnected = false;
                try { clientConnected = currentPipe != null && currentPipe.IsConnected; }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                text.AppendLine("unity pipe object=" + (currentPipe != null) + " clientConnected=" + clientConnected);
                ConPtySession app = interactive;
                if (app != null) text.AppendLine(app.DescribeProgress());
                // Do not wait forever: a host stuck while applying a command or pumping holds the gate,
                // and that is itself the finding to report.
                if (!Monitor.TryEnter(gate, 2000))
                {
                    text.AppendLine("the host lock has been busy for over 2000 ms (stuck in Apply or Pump); the state below is unavailable.");
                    WriteDiagnosticsFile(target, text);
                    return;
                }
                try
                {
                    text.AppendLine("primary pid=" + (primary == null ? 0 : primary.ProcessId) + " exited=" + primaryExited + " code=" + primaryExitCode + " failure=[" + failure + "]");
                    text.AppendLine("interactive id=" + appId + " active=" + (interactive != null) + " pid=" + (interactive == null ? 0 : interactive.ProcessId) +
                        " exited=" + appExited + " code=" + appExitCode + " failure=[" + appFailure + "] notice=[" + notice + "]" +
                        (interactive == null ? "" : " conptyFailure=[" + (interactive.Failure ?? "") + "]"));
                    if (screen != null)
                        text.AppendLine("screen " + screen.Columns + "x" + screen.Rows + " version=" + screen.Version + " alternate=" + screen.Alternate +
                            " cursor=" + screen.CursorX + "," + screen.CursorY + " visible=" + screen.CursorVisible + " appCursor=" + screen.ApplicationCursor);
                    text.AppendLine("acknowledged=" + acknowledged);
                    lock (inputGate) text.AppendLine("primary input queued=" + primaryInput.Count + " chars=" + inputCharacters);
                    text.AppendLine("--- recent commands from Unity (oldest first, up to " + CommandLogLimit + ") ---");
                    foreach (string entry in commandLog) text.AppendLine(entry);
                    text.AppendLine("--- recent interactive app output: last " + rawTail.Length + " of " + rawTotal + " chars (\\e = ESC) ---");
                    text.AppendLine(Escape(rawTail.ToString()));
                }
                finally { Monitor.Exit(gate); }
                WriteDiagnosticsFile(target, text);
            }

            // Written to a temporary name and closed before the final name appears, so Unity can treat
            // the final file's existence as "complete".
            private static void WriteDiagnosticsFile(string target, StringBuilder text)
            {
                string partial = target + ".partial";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllText(partial, text.ToString(), new UTF8Encoding(false));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(partial, target);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            private static string Ago(long ticks, long now)
            {
                return ticks == 0 ? "never" : ((now - ticks) / TimeSpan.TicksPerMillisecond) + "ms ago";
            }
            private void WritePrimary()
            {
                while (!stopping)
                {
                    string text;
                    lock (inputGate)
                    {
                        while (!stopping && primaryInput.Count == 0) Monitor.Wait(inputGate);
                        if (stopping) return;
                        text = primaryInput.Dequeue(); inputCharacters -= text.Length;
                    }
                    string error;
                    if (primary == null || !primary.TrySend(text, out error))
                        lock (gate) Message("\n[System Error] PowerShell input is unavailable.\n");
                }
            }
            public void Dispose()
            {
                stopping = true;
                lock (inputGate) Monitor.PulseAll(inputGate);
                var connected = pipe; if (connected != null) connected.Dispose();
                lock (gate)
                {
                    if (interactive != null) { interactive.Dispose(); interactive = null; }
                    if (primary != null) { primary.Dispose(); primary = null; }
                    if (primaryLifetime != null) { primaryLifetime.Dispose(); primaryLifetime = null; }
                }
                if (acceptThread != null) acceptThread.Join(500);
                if (primaryWriter != null) primaryWriter.Join(500);
            }
        }
    }
}
