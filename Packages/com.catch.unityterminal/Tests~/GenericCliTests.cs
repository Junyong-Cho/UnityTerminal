using System;
using System.Diagnostics;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class GenericCliTests
    {
        // Opens only the installed CLI's startup screen. No prompts, auth or trust decisions.
        public static void ClaudeStartup(string directory)
        {
            ShellCatalog.Refresh();
            var timer = Stopwatch.StartNew();
            while (!ShellCatalog.DiscoveryComplete && timer.ElapsedMilliseconds < 15000) Thread.Sleep(10);
            string shell = ShellCatalog.ChooseInitialShell(null, ShellCatalog.GetOptions());
            using (var session = ConPtySession.Start(shell, "claude --safe-mode --permission-mode plan", directory, 120, 35))
            {
                var screen = new TerminalScreen(120, 35);
                screen.Reply = text => { string ignored; session.TrySend(text, out ignored); };
                timer.Restart();
                while (timer.ElapsedMilliseconds < 45000)
                {
                    string chunk;
                    while (session.TryDequeue(out chunk)) screen.Feed(chunk);
                    string text = screen.GetTranscript();
                    if (text.Contains("Claude Code") || text.Contains("Welcome to Claude"))
                    {
                        Console.WriteLine("PASS: installed Claude renders its startup UI through the shared ConPTY engine");
                        Console.WriteLine("INFO: no agent prompt or authentication/trust approval was submitted");
                        return;
                    }
                    int code;
                    if (session.TryGetExitCode(out code)) throw new Exception("Claude exited before its startup UI, code " + code);
                    if (session.Failure != null) throw new Exception(session.Failure);
                    Thread.Sleep(10);
                }
                throw new Exception("Claude startup UI was not observed within 45 seconds.");
            }
        }
    }
}
