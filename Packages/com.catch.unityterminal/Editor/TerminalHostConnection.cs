using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace UnityTerminal
{
    [Serializable]
    internal sealed class HostResumeInfo
    {
        public string PipeName, HostExecutable, BuildScript, ShellExecutable, ShellId, Directory;
        public int OwnerPid, HostPid;
        public long OwnerStartTicks, HostStartTicks, NextSequence;
        public List<HostCommand> Pending = new List<HostCommand>();
    }

    // Transport objects are disposable across AppDomains; the host and its children are not.
    internal sealed class TerminalHostConnection : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<HostCommand> pending = new Queue<HostCommand>();
        private readonly List<HostCommand> inFlight = new List<HostCommand>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly ManualResetEvent drained = new ManualResetEvent(true);
        private readonly HostResumeInfo info;
        private readonly bool resumeOnly;
        private readonly Thread worker;
        private NamedPipeClientStream pipe;
        private Process builder;
        private volatile bool stopping;
        private bool ready;
        private string failure;
        private HostState state;
        private byte[] pendingBuffer, pendingScreen;
        private string pendingScreenId;
        private int queuedCharacters;
        // Typing latency. Polling every 33 ms with Windows' default 15.6 ms timer made each key take
        // ~78 ms to come back (measured with the real host; ConPTY + app took 0.3 ms). For one second after
        // input or a received change the worker polls every 4 ms with a 1 ms timer resolution
        // (timeBeginPeriod, released when idle and when the transport ends): ~10 ms per key.
        private const int FastPollMilliseconds = 4, IdlePollMilliseconds = 33;
        private const long ActiveTicks = TimeSpan.TicksPerSecond;
        private long lastActivityTicks;
        private readonly object timerGate = new object();
        private bool highResolutionTimer; // guarded by timerGate
        private readonly Timer highResolutionWatch;

        internal bool RecentlyActive { get { return DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityTicks) < ActiveTicks; } }

        private void MarkActivity() { Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks); }

        // Every timeBeginPeriod is paired with one timeEndPeriod under timerGate, from whichever thread.
        private void SetHighResolutionTimer(bool enabled)
        {
            lock (timerGate)
            {
                if (enabled && stopping) enabled = false;
                if (highResolutionTimer == enabled) return;
                highResolutionTimer = enabled;
                if (enabled) timeBeginPeriod(1); else timeEndPeriod(1);
            }
        }

        // The worker can block indefinitely while reading a response, so it cannot be relied on to drop the
        // request when activity ends. This watch releases it within 250 ms of going idle or stopping.
        private void WatchHighResolutionTimer(object ignored)
        {
            if (stopping || RecentlyActive == false) SetHighResolutionTimer(false);
        }

        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

        // Diagnostics only: when the last request/response with the host finished, and reconnects so far.
        private DateTime lastExchangeUtc;
        private int reconnects;

        internal TerminalHostConnection(HostResumeInfo options, bool resume)
        {
            info = options; resumeOnly = resume && options.HostPid != 0;
            if (options.Pending != null) foreach (HostCommand command in options.Pending) { pending.Enqueue(command); queuedCharacters += command.Text.Length; }
            if (pending.Count > 0) drained.Reset();
            highResolutionWatch = new Timer(WatchHighResolutionTimer, null, 250, 250);
            worker = new Thread(Run) { IsBackground = true, Name = "Unity Terminal host client" };
            worker.Start();
        }
        internal bool IsReady { get { lock (gate) return ready && !stopping; } }
        internal string Failure { get { lock (gate) return failure ?? (state != null && state.Failure != "" ? state.Failure : null); } }
        internal HostState State { get { lock (gate) return state; } }

        // One-line state summary for the interactive view's "Copy diagnostics".
        internal string Describe()
        {
            lock (gate)
            {
                var text = new System.Text.StringBuilder();
                text.Append("connection ready=").Append(ready).Append(" stopping=").Append(stopping)
                    .Append(" failure=").Append(failure ?? "-").Append(" reconnects=").Append(reconnects)
                    .Append(" pending=").Append(pending.Count).Append(" inFlight=").Append(inFlight.Count)
                    .Append(" queuedChars=").Append(queuedCharacters).Append(" fastPoll=").Append(RecentlyActive).Append(" highResTimer=").Append(highResolutionTimer)
                    .Append(" lastExchange=").Append(lastExchangeUtc == default(DateTime) ? "never" : (DateTime.UtcNow - lastExchangeUtc).TotalMilliseconds.ToString("0") + "ms ago");
                if (state != null)
                    text.Append("\nhost pid=").Append(state.HostPid).Append(" acknowledged=").Append(state.Acknowledged)
                        .Append(" screenVersion=").Append(state.ScreenVersion).Append(" interactiveId=").Append(state.InteractiveId)
                        .Append(" interactivePid=").Append(state.InteractivePid).Append(" active=").Append(state.InteractiveActive)
                        .Append(" exited=").Append(state.InteractiveExited).Append(" failure=").Append(state.Failure == "" ? "-" : state.Failure)
                        .Append(" appFailure=").Append(state.InteractiveFailure == "" ? "-" : state.InteractiveFailure)
                        .Append(" notice=").Append(state.Notice == "" ? "-" : state.Notice);
                else text.Append("\nhost state=none");
                return text.ToString();
            }
        }

        internal bool Send(HostCommandKind kind, string id, string text, int columns, int rows, out string error)
        {
            lock (gate)
            {
                error = null;
                if (stopping || failure != null) { error = failure ?? "The terminal session has ended."; return false; }
                text = text ?? "";
                if (queuedCharacters + text.Length > 262144 || pending.Count >= 1024) { error = "Input is full. Try a smaller paste."; return false; }
                pending.Enqueue(new HostCommand { Kind = kind, Id = id ?? "", Text = text, Columns = columns, Rows = rows });
                queuedCharacters += text.Length; drained.Reset(); MarkActivity(); wake.Set(); return true;
            }
        }
        internal bool ApplyBuffer(TerminalBuffer target)
        {
            byte[] bytes;
            lock (gate) { bytes = pendingBuffer; pendingBuffer = null; }
            if (bytes == null) return false;
            TerminalHostProtocol.Decode(bytes, target.ReadDisplayState); return true;
        }
        internal bool ApplyScreen(string id, TerminalScreen target)
        {
            byte[] bytes;
            lock (gate)
            {
                if (pendingScreenId != id) return false;
                bytes = pendingScreen; pendingScreen = null;
            }
            if (bytes == null) return false;
            TerminalHostProtocol.Decode(bytes, target.ReadDisplayState); return true;
        }

        private void Run()
        {
            bool started = false;
            var retry = Stopwatch.StartNew();
            while (!stopping)
            {
                try
                {
                    var connected = new NamedPipeClientStream(".", info.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    lock (gate) { if (stopping) { connected.Dispose(); return; } pipe = connected; }
                    try { connected.Connect(300); }
                    catch (TimeoutException)
                    {
                        connected.Dispose(); lock (gate) pipe = null;
                        if (!started && !resumeOnly) { EnsureHost(); started = true; retry.Restart(); }
                        if (retry.ElapsedMilliseconds > 15000) throw new IOException("The terminal host could not be reached.");
                        Thread.Sleep(50); continue;
                    }
                    int bufferVersion = -1, screenVersion = -1; string screenId = "";
                    HostState hello = Exchange(connected, bufferVersion, screenVersion, screenId, new List<HostCommand>());
                    AcceptState(hello);
                    lock (gate)
                    {
                        info.NextSequence = Math.Max(info.NextSequence, hello.Acknowledged);
                        DiscardAcknowledged(hello.Acknowledged);
                    }
                    if (hello.Buffer != null) bufferVersion = hello.BufferVersion;
                    if (hello.Screen != null) { screenVersion = hello.ScreenVersion; screenId = hello.InteractiveId; }
                    retry.Restart();
                    while (!stopping)
                    {
                        bool active = RecentlyActive;
                        SetHighResolutionTimer(active);
                        wake.WaitOne(active ? FastPollMilliseconds : IdlePollMilliseconds);
                        List<HostCommand> commands;
                        lock (gate)
                        {
                            if (stopping) break;
                            while (pending.Count > 0)
                            {
                                HostCommand command = pending.Dequeue();
                                if (command.Sequence == 0) command.Sequence = ++info.NextSequence;
                                inFlight.Add(command);
                            }
                            commands = new List<HostCommand>(inFlight);
                        }
                        HostState update = Exchange(connected, bufferVersion, screenVersion, screenId, commands);
                        AcceptState(update);
                        lock (gate) lastExchangeUtc = DateTime.UtcNow;
                        lock (gate) DiscardAcknowledged(update.Acknowledged);
                        if (update.Buffer != null) bufferVersion = update.BufferVersion;
                        if (update.Screen != null) { screenVersion = update.ScreenVersion; screenId = update.InteractiveId; }
                        if (commands.Count > 0 || update.Buffer != null || update.Screen != null) MarkActivity();
                    }
                }
                catch (Exception ex)
                {
                    if (stopping) return;
                    lock (gate) ready = false;
                    lock (gate) reconnects++;
                    if (!HostAlive() || retry.ElapsedMilliseconds > 15000)
                    { lock (gate) failure = "Terminal host connection: " + ex.Message; return; }
                    Thread.Sleep(100);
                }
                finally
                {
                    SetHighResolutionTimer(false);
                    NamedPipeClientStream previous;
                    lock (gate) { previous = pipe; pipe = null; }
                    if (previous != null) previous.Dispose();
                }
            }
        }
        private static HostState Exchange(Stream stream, int bufferVersion, int screenVersion, string id, List<HostCommand> commands)
        {
            TerminalHostProtocol.WriteFrame(stream, writer => TerminalHostProtocol.WriteCommands(writer, bufferVersion, screenVersion, id, commands));
            using (var reader = TerminalHostProtocol.ReadFrame(stream)) return TerminalHostProtocol.ReadState(reader);
        }
        private void AcceptState(HostState update)
        {
            lock (gate)
            {
                state = update; ready = true;
                info.HostPid = update.HostPid; info.HostStartTicks = update.HostStartTicks;
                if (update.Buffer != null) pendingBuffer = update.Buffer;
                if (update.Screen != null) { pendingScreen = update.Screen; pendingScreenId = update.InteractiveId; }
            }
        }
        private void DiscardAcknowledged(long acknowledged)
        {
            for (int i = inFlight.Count - 1; i >= 0; i--)
                if (inFlight[i].Sequence <= acknowledged) { queuedCharacters -= inFlight[i].Text.Length; inFlight.RemoveAt(i); }
            // Restored commands may already have reached the host before the old domain disconnected.
            int count = pending.Count;
            for (int i = 0; i < count; i++)
            {
                HostCommand command = pending.Dequeue();
                if (command.Sequence != 0 && command.Sequence <= acknowledged) queuedCharacters -= command.Text.Length;
                else pending.Enqueue(command);
            }
            if (inFlight.Count == 0 && pending.Count == 0) drained.Set();
        }
        private void EnsureHost()
        {
            if (!File.Exists(info.HostExecutable))
            {
                var start = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    ConPtySession.QuoteArgument(info.BuildScript) + " -OutputPath " + ConPtySession.QuoteArgument(info.HostExecutable))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = info.Directory };
                Process build;
                lock (gate)
                {
                    if (stopping) return;
                    build = builder = Process.Start(start);
                }
                try
                {
                    var output = build.StandardOutput.ReadToEndAsync(); var errors = build.StandardError.ReadToEndAsync();
                    var timer = Stopwatch.StartNew();
                    while (!build.WaitForExit(100))
                    {
                        if (stopping || timer.ElapsedMilliseconds > 65000) { KillTree(build.Id); throw new IOException("Terminal host build was cancelled or timed out."); }
                    }
                    if (build.ExitCode != 0) throw new IOException("Terminal host build failed: " + errors.Result + output.Result);
                }
                finally { lock (gate) builder = null; build.Dispose(); }
            }
            lock (gate)
            {
                if (stopping) return;
                string arguments = ConPtySession.QuoteArgument(info.PipeName) + " " + info.OwnerPid + " " + info.OwnerStartTicks + " " +
                    ConPtySession.QuoteArgument(info.ShellExecutable) + " " + ConPtySession.QuoteArgument(info.ShellId) + " " + ConPtySession.QuoteArgument(info.Directory);
                using (var host = Process.Start(new ProcessStartInfo(info.HostExecutable, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = info.Directory }))
                {
                    info.HostPid = host.Id;
                    try { info.HostStartTicks = host.StartTime.ToUniversalTime().Ticks; } catch (InvalidOperationException) { }
                }
            }
        }
        private bool HostAlive()
        {
            try
            {
                lock (gate)
                {
                    if (info.HostPid == 0) return false;
                    using (var host = Process.GetProcessById(info.HostPid))
                        return !host.HasExited && host.StartTime.ToUniversalTime().Ticks == info.HostStartTicks;
                }
            }
            catch (Exception ex) { if (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) return false; throw; }
        }
        internal HostResumeInfo Detach()
        {
            drained.WaitOne(500);
            return StopTransport();
        }
        private HostResumeInfo StopTransport()
        {
            NamedPipeClientStream connected; Process build;
            lock (gate)
            {
                stopping = true; ready = false;
                info.Pending = new List<HostCommand>(inFlight); info.Pending.AddRange(pending);
                connected = pipe; pipe = null; build = builder;
            }
            wake.Set();
            highResolutionWatch.Dispose();
            SetHighResolutionTimer(false);
            if (connected != null) connected.Dispose();
            if (build != null) { try { if (!build.HasExited) KillTree(build.Id); } catch (InvalidOperationException) { } }
            worker.Join(1000);
            return info;
        }
        public void Dispose()
        {
            if (stopping) return;
            string ignored;
            Send(HostCommandKind.Shutdown, "", "", 0, 0, out ignored);
            drained.WaitOne(700);
            StopTransport();
            var timer = Stopwatch.StartNew();
            while (HostAlive() && timer.ElapsedMilliseconds < 1000) Thread.Sleep(10);
            if (HostAlive()) KillTree(info.HostPid);
        }
        private static void KillTree(int pid)
        {
            try
            {
                using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    var output = killer.StandardOutput.ReadToEndAsync(); var error = killer.StandardError.ReadToEndAsync();
                    if (!killer.WaitForExit(2000)) killer.Kill();
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
