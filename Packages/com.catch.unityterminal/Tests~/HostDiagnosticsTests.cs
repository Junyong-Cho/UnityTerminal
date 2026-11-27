using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace UnityTerminal.Tests
{
    // Runs the host Server in this process (no Unity client) to check Capture Diagnostics answers:
    // while another thread holds the host gate (a stuck Apply or Pump), and after it is released.
    public static class HostDiagnosticsTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Run(string directory)
        {
            Type type = typeof(TerminalSessionHost).GetNestedType("Server", BindingFlags.NonPublic);
            string fixture = Path.Combine(Path.Combine(directory, "Temp"), "HostDiagnosticsTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            object server = Activator.CreateInstance(type, Flags, null, new object[] {
                "UnityTerminal.DiagnosticsTest." + Guid.NewGuid().ToString("N"), Path.Combine(fixture, "NoShell.exe"), "test", fixture }, null);
            string request = Path.Combine(fixture, "diagnostics.request");
            type.GetField("diagnosticsRequest", Flags).SetValue(server, request);
            object gate = type.GetField("gate", Flags).GetValue(server);
            Exception runFailure = null;
            var owner = Process.GetCurrentProcess();
            var runner = new Thread(delegate()
            {
                try { type.GetMethod("Run", Flags).Invoke(server, new object[] { owner }); }
                catch (Exception ex) { runFailure = ex; }
            }) { IsBackground = true };
            bool held = false;
            try
            {
                Monitor.Enter(gate); held = true;
                type.GetField("applyingSinceTicks", Flags).SetValue(server, DateTime.UtcNow.Ticks);
                type.GetField("applyingCommand", Flags).SetValue(server, "Test blocked Apply");
                runner.Start();
                string busyTarget = Path.Combine(fixture, "busy.txt");
                File.WriteAllText(request, busyTarget, new UTF8Encoding(false));
                string busy = WaitForFile(busyTarget, 4000);
                Require(busy != null, "No capture while the host gate was held: " + runFailure);
                Require(busy.Contains("applying=[Test blocked Apply]"), "Busy capture lacks the command being applied");
                Require(busy.Contains("lock has been busy"), "Busy capture does not report the busy lock");
                Require(busy.Contains("clientConnected=False"), "Busy capture reports a Unity client that is not connected");
                Console.WriteLine("PASS: diagnostics answer while the host gate is held, naming the stuck command");

                type.GetField("applyingCommand", Flags).SetValue(server, null);
                Monitor.Exit(gate); held = false;
                string freeTarget = Path.Combine(fixture, "free.txt");
                File.WriteAllText(request, freeTarget, new UTF8Encoding(false));
                string free = WaitForFile(freeTarget, 4000);
                Require(free != null, "No capture after the gate was released: " + runFailure);
                Require(free.Contains("applying=none") && free.Contains("--- recent commands from Unity") && free.Contains("--- recent interactive app output"),
                    "Full capture is incomplete");
                Require(Directory.GetFiles(fixture, "*.partial").Length == 0, "A temporary .partial file was left behind");
                Console.WriteLine("PASS: diagnostics publish a complete file under its final name only");
            }
            finally
            {
                if (held) Monitor.Exit(gate);
                type.GetMethod("Dispose", Flags).Invoke(server, null);
                if (runner.IsAlive) runner.Join(2000);
                owner.Dispose();
                try { Directory.Delete(fixture, true); } catch (IOException) { }
            }
        }

        // A ConPTY input write that does not return must read as "writing", even when it started in the
        // same DateTime tick as the previous write ended; a writer thread that ended must read as "exited".
        public static void ConPtyProgress()
        {
            var session = (ConPtySession)Activator.CreateInstance(typeof(ConPtySession), true);
            var blocked = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            typeof(ConPtySession).GetField("writer", Flags).SetValue(session, new BlockingWriter(session, blocked, release));
            var thread = new Thread(delegate() { typeof(ConPtySession).GetMethod("WriteInput", Flags).Invoke(session, null); }) { IsBackground = true };
            for (int i = 0; i < 10000; i++) { string error; Require(session.TrySend("x", out error), "Input rejected: " + error); }
            thread.Start();
            try
            {
                Require(blocked.WaitOne(5000), "The test writer never blocked");
                Thread.Sleep(300);
                string stuck = session.DescribeProgress();
                Require(stuck.Contains("writer=writing (started "), "A blocked ConPTY write is not reported as writing: " + stuck);
                Console.WriteLine("PASS: a blocked ConPTY input write reads as writing (also when its start tick equals the previous end)");
            }
            finally
            {
                typeof(ConPtySession).GetField("stopping", Flags).SetValue(session, true);
                release.Set();
                thread.Join(3000);
                blocked.Dispose(); release.Dispose();
            }
            string ended = session.DescribeProgress();
            Require(ended.Contains("writer=exited"), "An ended ConPTY writer thread is not reported as exited: " + ended);
            Console.WriteLine("PASS: an ended ConPTY writer thread reads as exited");
        }

        // Blocks the first write whose start tick equals the previous end tick (the case that used to read
        // as idle), or the 200th write if that never happens, until released.
        private sealed class BlockingWriter : StreamWriter
        {
            private readonly ConPtySession session;
            private readonly ManualResetEvent blocked, release;
            private readonly FieldInfo start, end;
            private int writes;
            public BlockingWriter(ConPtySession session, ManualResetEvent blocked, ManualResetEvent release) : base(new MemoryStream())
            {
                this.session = session; this.blocked = blocked; this.release = release;
                start = typeof(ConPtySession).GetField("writeStartTicks", Flags);
                end = typeof(ConPtySession).GetField("writeEndTicks", Flags);
            }
            public override void Write(string value)
            {
                long begin = (long)start.GetValue(session), previousEnd = (long)end.GetValue(session);
                if (++writes == 200 || (begin != 0 && begin == previousEnd)) { blocked.Set(); release.WaitOne(10000); }
            }
        }

        // The final name appears only after the host closed the file, so the first read must succeed.
        private static string WaitForFile(string path, int milliseconds)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                if (File.Exists(path)) return File.ReadAllText(path);
                Thread.Sleep(20);
            }
            return null;
        }

        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
