using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.RegularExpressions;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class HostPersistenceTests
    {
        public static void Run(string host, string probe, string directory)
        {
            SnapshotRoundTrip();
            ShellCatalog.Refresh();
            Wait(() => ShellCatalog.DiscoveryComplete, "PowerShell discovery");
            string executable = ShellCatalog.GetExecutable(ShellCatalog.ChooseInitialShell(null, ShellCatalog.GetOptions()));
            TerminalHostConnection client = null;
            int hostPid = 0, primaryPid = 0, appPid = 0, childPid = 0;
            try
            {
                client = new TerminalHostConnection(Options(host, executable, directory, Process.GetCurrentProcess()), false);
                WaitReady(client);
                hostPid = client.State.HostPid; primaryPid = client.State.PrimaryPid;
                var buffer = new TerminalBuffer();
                Send(client, HostCommandKind.PrimaryInput, "", "$global:HostKeep='parent'; $global:HostCount=0; [Console]::WriteLine(('PRIMARY-' + 'READY'))");
                WaitBuffer(client, buffer, "\nPRIMARY-READY\n");
                string id = Guid.NewGuid().ToString("N");
                Send(client, HostCommandKind.StartInteractive, id, "& " + "'" + probe.Replace("'", "''") + "' persist", 100, 30);
                var screen = new TerminalScreen(100, 30);
                WaitScreen(client, id, screen, "|END");
                var match = Regex.Match(screen.GetTranscript(), @"PROCESS:(\d+)\|END");
                childPid = int.Parse(match.Groups[1].Value); appPid = client.State.InteractivePid;
                Send(client, HostCommandKind.InteractiveInput, id, "ax");
                WaitScreen(client, id, screen, "COUNT:2|CHAR:120");
                Require(screen.Alternate && screen.GetText().Contains("ALT-KEEP-\uD55C\uAE00"), "Alternate Unicode page was not established");
                string before = screen.GetText(); int x = screen.CursorX, y = screen.CursorY;
                HostResumeInfo resume = client.Detach();
                client.Dispose(); // A detached transport must never kill its host.
                Thread.Sleep(500);
                Require(Alive(hostPid) && Alive(primaryPid) && Alive(appPid) && Alive(childPid), "Detach killed a process");
                client = new TerminalHostConnection(resume, true); WaitReady(client);
                Wait(() => client.ApplyScreen(id, screen), "Screen snapshot after reconnect");
                Require(client.State.HostPid == hostPid && client.State.PrimaryPid == primaryPid && client.State.InteractivePid == appPid,
                    "Reconnect restarted a process");
                Require(screen.Alternate && screen.CursorShape == 6 && screen.CursorX == x && screen.CursorY == y && screen.GetText() == before,
                    "Screen, cursor or alternate mode changed during reconnect");
                Send(client, HostCommandKind.InteractiveInput, id, "b");
                WaitScreen(client, id, screen, "COUNT:3|CHAR:98");
                Console.WriteLine("PASS: reconnect keeps host, PowerShell, app and descendant PIDs; app memory and Unicode alternate screen survive");

                Send(client, HostCommandKind.PrimaryInput, "", "[Console]::WriteLine(('KEPT-' + $global:HostKeep))");
                WaitBuffer(client, buffer, "\nKEPT-parent\n");
                Console.WriteLine("PASS: primary PowerShell variables and styled scrollback survive reconnect");

                resume = client.Detach();
                var uncertain = new HostCommand { Sequence = ++resume.NextSequence, Kind = HostCommandKind.PrimaryInput, Text = "$global:HostCount++" };
                using (var pipe = new NamedPipeClientStream(".", resume.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);
                    TerminalHostProtocol.WriteFrame(pipe, writer => TerminalHostProtocol.WriteCommands(writer, -1, -1, "", new List<HostCommand> { uncertain }));
                    // Drop the connection without reading its acknowledgment, like an unloading domain.
                }
                resume.Pending.Add(uncertain);
                client = new TerminalHostConnection(resume, true); WaitReady(client);
                Send(client, HostCommandKind.PrimaryInput, "", "[Console]::WriteLine(('EXACT-' + $global:HostCount))");
                WaitBuffer(client, buffer, "\nEXACT-1\n");
                Console.WriteLine("PASS: an input with a lost acknowledgment is executed exactly once after reconnect");

                Send(client, HostCommandKind.StopInteractive, id, "");
                Wait(() => client.State.InteractiveExited && !client.State.InteractiveActive, "Stop interactive app");
                Require(Alive(hostPid) && Alive(primaryPid), "Stopping the app killed the primary shell");
                string floodId = Guid.NewGuid().ToString("N");
                Send(client, HostCommandKind.StartInteractive, floodId, "& '" + probe.Replace("'", "''") + "' flood", 100, 30);
                Wait(() => client.State.InteractiveId == floodId && client.State.InteractiveActive, "Flood app launch");
                resume = client.Detach(); Thread.Sleep(1500);
                client = new TerminalHostConnection(resume, true); WaitReady(client);
                WaitScreen(client, floodId, screen, "FLOOD-99999-");
                Require(screen.HistoryCount <= 512, "Host history is unbounded");
                Console.WriteLine("PASS: the host drains high-volume output while the Unity transport is absent");
            }
            finally { if (client != null) client.Dispose(); }
            Wait(() => !Alive(hostPid) && !Alive(primaryPid) && !Alive(appPid) && !Alive(childPid), "Host shutdown process cleanup");
            Console.WriteLine("PASS: closing the terminal shuts down the host and its process tree");
            OwnerExit(host, probe, executable, directory);
        }

        private static void SnapshotRoundTrip()
        {
            var buffer = new TerminalBuffer();
            var parser = new AnsiParser(buffer);
            parser.Feed("plain\n\x1b[31;1m\uD55C\uAE00 <T>\x1b[0m\n");
            var restored = new TerminalBuffer();
            TerminalHostProtocol.Decode(TerminalHostProtocol.Encode(buffer.WriteDisplayState), restored.ReadDisplayState);
            Require(buffer.GetRichText() == restored.GetRichText(), "Styled buffer snapshot changed the output");
            var screen = new TerminalScreen(80, 24);
            screen.Feed("primary\x1b[?1049h\x1b[38;2;1;2;3m\uD55C\uAE00\x1b[4;8Hcursor\x1b[6 q\x1b[?2004h");
            var copy = new TerminalScreen(2, 2);
            TerminalHostProtocol.Decode(TerminalHostProtocol.Encode(screen.WriteDisplayState), copy.ReadDisplayState);
            Require(copy.GetText() == screen.GetText() && copy.CursorX == screen.CursorX && copy.CursorY == screen.CursorY &&
                copy.Alternate && copy.CursorShape == 6 && copy.BracketedPaste && copy.GetLine(0)[0].Width == 2,
                "Display state snapshot lost terminal modes, cursor or Unicode widths");
            Console.WriteLine("PASS: compressed snapshots preserve styled buffers, Unicode cells, cursor and app modes");
        }
        private static void OwnerExit(string host, string probe, string executable, string directory)
        {
            using (var owner = Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60")
                { UseShellExecute = false, CreateNoWindow = true }))
            {
                TerminalHostConnection client = null;
                Process child = null;
                int hostPid = 0, primaryPid = 0, appPid = 0, childPid = 0;
                try
                {
                    client = new TerminalHostConnection(Options(host, executable, directory, owner), false); WaitReady(client);
                    hostPid = client.State.HostPid; primaryPid = client.State.PrimaryPid;
                    string id = Guid.NewGuid().ToString("N");
                    Send(client, HostCommandKind.StartInteractive, id, "& '" + probe.Replace("'", "''") + "' child", 100, 30);
                    var screen = new TerminalScreen(100, 30);
                    WaitScreen(client, id, screen, "|END");
                    childPid = int.Parse(Regex.Match(screen.GetTranscript(), @"CHILD:(\d+)\|END").Groups[1].Value);
                    child = Process.GetProcessById(childPid);
                    appPid = client.State.InteractivePid;
                    client.Detach();
                    owner.Kill(); owner.WaitForExit(3000);
                    try { Wait(() => !Alive(hostPid) && !Alive(primaryPid) && !Alive(appPid) && !Alive(childPid), "Owner-exit cleanup"); }
                    catch { throw new Exception("Owner cleanup remaining: host=" + Alive(hostPid) + ", primary=" + Alive(primaryPid) + ", app=" + Alive(appPid) + ", child=" + Alive(childPid)); }
                    Console.WriteLine("PASS: exiting Unity's owner process also terminates the detached host and descendants");
                }
                finally
                {
                    if (client != null) client.Dispose();
                    if (!owner.HasExited) owner.Kill();
                    if (child != null)
                    {
                        try { if (!child.HasExited) child.Kill(); }
                        finally { child.Dispose(); }
                    }
                }
            }
        }
        private static HostResumeInfo Options(string host, string executable, string directory, Process owner)
        {
            return new HostResumeInfo { PipeName = "UnityTerminal.Test." + Guid.NewGuid().ToString("N"), HostExecutable = host,
                BuildScript = "",
                ShellExecutable = executable, ShellId = "PowerShell", Directory = directory, OwnerPid = owner.Id,
                OwnerStartTicks = owner.StartTime.ToUniversalTime().Ticks };
        }
        private static void Send(TerminalHostConnection client, HostCommandKind kind, string id, string text, int columns = 0, int rows = 0)
        { string error; Require(client.Send(kind, id, text, columns, rows, out error), error); }
        private static void WaitReady(TerminalHostConnection client)
        { Wait(() => { if (client.Failure != null) throw new Exception(client.Failure); return client.IsReady; }, "Host connection"); }
        private static void WaitBuffer(TerminalHostConnection client, TerminalBuffer buffer, string marker)
        { Wait(() => { client.ApplyBuffer(buffer); return buffer.GetPlainText().Contains(marker); }, "Primary output " + marker); }
        private static void WaitScreen(TerminalHostConnection client, string id, TerminalScreen screen, string marker)
        { Wait(() => { if (client.Failure != null) throw new Exception(client.Failure); client.ApplyScreen(id, screen); return screen.GetTranscript().Contains(marker); }, "App output " + marker); }
        private static void Wait(Func<bool> condition, string description)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 30000) { if (condition()) return; Thread.Sleep(10); }
            throw new Exception("Timed out: " + description);
        }
        private static bool Alive(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
