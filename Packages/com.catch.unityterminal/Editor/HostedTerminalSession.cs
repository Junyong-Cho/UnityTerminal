using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnityTerminal
{
    internal sealed class HostedShellSession : IDisposable
    {
        internal readonly TerminalHostConnection Connection;
        private bool detached;
        private HostedShellSession(TerminalHostConnection connection) { Connection = connection; }
        public static HostedShellSession Start(string shellType, string directory)
        {
            var package = PackageInfo.FindForAssembly(typeof(UnityTerminalWindow).Assembly);
            string root = package != null ? package.resolvedPath : Path.GetFullPath(Path.Combine(Application.dataPath, "../Packages/com.catch.unityterminal"));
            // Host cache key. Must list every file Host~/Build-Host.ps1 compiles (plus the script itself);
            // a file missing here leaves a stale cached host after that file changes.
            string[] sources = { "Editor/ShellSession.cs", "Editor/ShellCatalog.cs", "Editor/ConPtySession.cs", "Editor/TerminalScreen.cs", "Editor/TerminalBuffer.cs",
                "Editor/AnsiParser.cs", "Editor/TerminalHostProtocol.cs", "Host~/TerminalSessionHost.cs", "Host~/Build-Host.ps1" };
            string hash;
            using (var bytes = new MemoryStream())
            using (var sha = SHA256.Create())
            {
                foreach (string source in sources) { byte[] data = File.ReadAllBytes(Path.Combine(root, source)); bytes.Write(data, 0, data.Length); }
                bytes.Position = 0; hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            using (var owner = Process.GetCurrentProcess())
            {
                var info = new HostResumeInfo { PipeName = "UnityTerminal." + owner.Id + "." + Guid.NewGuid().ToString("N"),
                    OwnerPid = owner.Id, OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks, ShellId = shellType,
                    ShellExecutable = ShellCatalog.GetExecutable(shellType), Directory = directory,
                    BuildScript = Path.Combine(root, "Host~/Build-Host.ps1"),
                    HostExecutable = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UnityTerminal/Host/" + hash + "/TerminalSessionHost.exe")) };
                return new HostedShellSession(new TerminalHostConnection(info, false));
            }
        }
        internal static HostedShellSession Attach(HostResumeInfo info) { return new HostedShellSession(new TerminalHostConnection(info, true)); }
        public bool IsReady { get { return Connection.IsReady; } }
        public string Failure { get { return Connection.Failure; } }
        public string CleanupError { get { return null; } }
        internal int ProcessId { get { var state = Connection.State; return state == null ? 0 : state.PrimaryPid; } }
        internal int HostProcessId { get { var state = Connection.State; return state == null ? 0 : state.HostPid; } }
        public bool TrySend(string command, out string error)
        {
            var state = Connection.State;
            if (state != null && state.PrimaryExited) { error = "PowerShell has exited. Use Restart."; return false; }
            return Connection.Send(HostCommandKind.PrimaryInput, "", command, 0, 0, out error);
        }
        internal bool ApplyBuffer(TerminalBuffer buffer) { return Connection.ApplyBuffer(buffer); }
        public bool TryGetExitCode(out int code)
        {
            var state = Connection.State; code = state == null ? 0 : state.PrimaryExitCode;
            return state != null && state.PrimaryExited;
        }
        internal void Clear() { string error; Connection.Send(HostCommandKind.ClearPrimary, "", "", 0, 0, out error); }
        internal void AppendSystemMessage(string text) { string error; Connection.Send(HostCommandKind.SystemMessage, "", text, 0, 0, out error); }
        internal HostedInteractiveSession StartInteractive(string command, int columns, int rows)
        {
            string id = Guid.NewGuid().ToString("N"), error;
            if (!Connection.Send(HostCommandKind.StartInteractive, id, command, columns, rows, out error)) throw new IOException(error);
            return new HostedInteractiveSession(Connection, id);
        }
        internal HostedInteractiveSession RestoreInteractive(string id) { return new HostedInteractiveSession(Connection, id); }
        internal HostResumeInfo Detach() { detached = true; return Connection.Detach(); }
        public void Dispose() { if (!detached) Connection.Dispose(); }
    }

    internal sealed class HostedInteractiveSession : IDisposable
    {
        private readonly TerminalHostConnection connection;
        internal readonly string Id;
        private bool disposed;
        internal HostedInteractiveSession(TerminalHostConnection host, string id) { connection = host; Id = id; }
        internal int ProcessId { get { var state = connection.State; return state != null && state.InteractiveId == Id ? state.InteractivePid : 0; } }
        internal int HostProcessId { get { var state = connection.State; return state == null ? 0 : state.HostPid; } }
        internal string Notice { get { var state = connection.State; return state == null ? null : state.Notice; } }

        // State summary for the interactive view's "Copy diagnostics".
        internal string Describe()
        {
            int pid = ProcessId;
            string alive = "unknown";
            if (pid != 0)
            {
                try { using (var process = Process.GetProcessById(pid)) alive = process.HasExited ? "exited" : "running"; }
                catch (ArgumentException) { alive = "not found"; }
                catch (InvalidOperationException) { alive = "not found"; }
            }
            return "session id=" + Id + " disposed=" + disposed + " appPid=" + pid + " (" + alive + ")\n" + connection.Describe();
        }
        public string Failure
        {
            get
            {
                if (connection.Failure != null) return connection.Failure;
                var state = connection.State;
                return state != null && state.InteractiveId == Id && state.InteractiveFailure != "" ? state.InteractiveFailure : null;
            }
        }
        public bool TrySend(string text, out string error)
        {
            if (disposed) { error = "The interactive session has ended."; return false; }
            return connection.Send(HostCommandKind.InteractiveInput, Id, text, 0, 0, out error);
        }
        internal bool ApplyScreen(TerminalScreen screen) { return connection.ApplyScreen(Id, screen); }
        public void Resize(int columns, int rows)
        {
            string error;
            if (!connection.Send(HostCommandKind.Resize, Id, "", columns, rows, out error)) throw new IOException(error);
        }
        public bool TryGetExitCode(out int code)
        {
            var state = connection.State; code = state == null ? 0 : state.InteractiveExitCode;
            return state != null && state.InteractiveId == Id && state.InteractiveExited;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            string ignored; connection.Send(HostCommandKind.StopInteractive, Id, "", 0, 0, out ignored);
        }
    }
}
