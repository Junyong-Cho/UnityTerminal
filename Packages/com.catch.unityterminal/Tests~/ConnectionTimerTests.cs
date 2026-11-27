using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace UnityTerminal.Tests
{
    // The connection's 1 ms timer request must follow activity, not the worker's progress: a host that
    // stops answering (the worker blocks in a response read) must not keep the request alive after the
    // activity window, and detaching must release it. Uses an in-process fake host on a named pipe.
    public static class ConnectionTimerTests
    {
        public static void Run(string directory)
        {
            string name = "UnityTerminal.TimerTest." + Guid.NewGuid().ToString("N");
            var stalled = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            Exception failure = null;
            using (var owner = Process.GetCurrentProcess())
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                int pid = owner.Id; long startTicks = owner.StartTime.ToUniversalTime().Ticks;
                var thread = new Thread(delegate()
                {
                    try
                    {
                        server.WaitForConnection();
                        long acknowledged = 0;
                        bool warmed = false;
                        while (!warmed)
                        {
                            int buffer, screen; string id;
                            List<HostCommand> commands;
                            using (var reader = TerminalHostProtocol.ReadFrame(server)) commands = TerminalHostProtocol.ReadCommands(reader, out buffer, out screen, out id);
                            foreach (HostCommand command in commands) { acknowledged = command.Sequence; warmed = true; }
                            TerminalHostProtocol.WriteFrame(server, delegate(BinaryWriter writer)
                            { TerminalHostProtocol.WriteState(writer, new HostState { HostPid = pid, HostStartTicks = startTicks, Acknowledged = acknowledged }); });
                        }
                        using (var reader = TerminalHostProtocol.ReadFrame(server)) { int b, s; string id; TerminalHostProtocol.ReadCommands(reader, out b, out s, out id); }
                        stalled.Set();
                        release.WaitOne(10000); // From here on the host never answers.
                    }
                    catch (Exception ex) { failure = ex; stalled.Set(); }
                }) { IsBackground = true };
                thread.Start();
                var client = new TerminalHostConnection(new HostResumeInfo { PipeName = name, HostPid = pid, HostStartTicks = startTicks, OwnerPid = pid,
                    OwnerStartTicks = startTicks, Directory = directory, HostExecutable = "", BuildScript = "", ShellExecutable = "", ShellId = "" }, true);
                try
                {
                    Wait(() => client.IsReady, "fake host connection");
                    string error;
                    Require(client.Send(HostCommandKind.InteractiveInput, "", "x", 0, 0, out error), error);
                    Require(stalled.WaitOne(5000) && failure == null, "The fake host did not stall: " + failure);
                    Wait(() => client.Describe().Contains("fastPoll=True highResTimer=True"), "fast mode after input");
                    Console.WriteLine("PASS: input switches the connection to fast polling with a 1 ms timer request");
                    Thread.Sleep(2200);
                    string idle = client.Describe();
                    Require(!client.RecentlyActive && idle.Contains("highResTimer=False"), "The timer request outlived the activity while the host was not answering: " + idle);
                    Console.WriteLine("PASS: the timer request is released after the activity window even while a response read is blocked");
                }
                finally
                {
                    client.Detach(); // Never shut down or kill this fake in-process host.
                    release.Set(); thread.Join(3000);
                    stalled.Dispose(); release.Dispose();
                }
                Require(client.Describe().Contains("highResTimer=False"), "Detach left the timer request active");
                Console.WriteLine("PASS: detaching releases the timer request");
            }
        }

        private static void Wait(Func<bool> condition, string description)
        {
            var timer = Stopwatch.StartNew();
            while (!condition()) { if (timer.ElapsedMilliseconds > 5000) throw new Exception("Timed out: " + description); Thread.Sleep(10); }
        }

        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
