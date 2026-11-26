// Host-shared source: Host~/Build-Host.ps1 also compiles this file with Windows PowerShell 5.1 Add-Type (C# 5).
// Keep it C# 5 compatible (no string interpolation, ?., nameof, expression-bodied members, etc.). See README "호스트 빌드 제약".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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

            internal Server(string name, string file, string shell, string directory)
            {
                pipeName = name; executable = file;
                using (var process = Process.GetCurrentProcess()) { hostPid = process.Id; hostStart = process.StartTime.ToUniversalTime().Ticks; }
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
                var check = Stopwatch.StartNew();
                while (!stopping)
                {
                    if (check.ElapsedMilliseconds >= 500) { if (owner.HasExited) break; check.Restart(); }
                    lock (gate) Pump();
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
                    while (characters < 65536 && interactive.TryDequeue(out text)) { screen.Feed(text); characters += text.Length; }
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
                                        Apply(command); acknowledged = command.Sequence;
                                        if (command.Kind == HostCommandKind.Shutdown) shutdown = true;
                                    }
                                    Pump();
                                    state = Snapshot(knownBuffer, knownScreen, knownApp);
                                }
                                TerminalHostProtocol.WriteFrame(connected, writer => TerminalHostProtocol.WriteState(writer, state));
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
