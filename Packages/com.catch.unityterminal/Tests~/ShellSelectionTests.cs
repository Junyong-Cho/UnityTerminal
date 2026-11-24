using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class ShellSelectionTests
    {
        public static void Run(string directory)
        {
            ShellCatalog.Refresh();
            var timer = Stopwatch.StartNew();
            while (!ShellCatalog.DiscoveryComplete && timer.ElapsedMilliseconds < 30000) Thread.Sleep(10);
            Require(ShellCatalog.DiscoveryComplete, "Shell discovery did not finish");
            ShellOption[] options = ShellCatalog.GetOptions();
            Require(Array.FindIndex(options, option => option.Id == "PowerShell7") >= 0, "PowerShell 7 was not detected");
            Require(!ShellCatalog.IsVersion7("powershell.exe"), "Windows PowerShell 5.1 was detected as 7");
            Console.WriteLine("PASS: actual PowerShell 7 discovery and 5.1 distinction");
            foreach (string shell in new[] { "PowerShell", "PowerShell7" })
            {
                string expected = shell == "PowerShell7" ? "7" : "5";
                string command = "[Console]::WriteLine(('SHELL-MAJOR:' + $PSVersionTable.PSVersion.Major)); [Console]::WriteLine('UNICODE-\uD55C\uAE00'); exit 0";
                using (var session = ShellSession.Start(shell, directory))
                {
                    string error;
                    Require(session.TrySend(command, out error), "Send failed: " + error);
                    var output = new StringBuilder();
                    timer.Restart();
                    bool exited = false;
                    while (timer.ElapsedMilliseconds < 15000)
                    {
                        ShellSession.OutputChunk chunk;
                        while (session.TryDequeue(out chunk)) output.Append(chunk.Text);
                        int code;
                        if (session.TryGetExitCode(out code)) { Require(code == 0, "Shell failed with code " + code); exited = true; break; }
                        Thread.Sleep(10);
                    }
                    Require(exited && output.ToString().Contains("SHELL-MAJOR:" + expected) && output.ToString().Contains("UNICODE-\uD55C\uAE00"),
                        "Wrong version or Unicode corruption in " + shell + ": " + output);
                }
                Console.WriteLine("PASS: " + shell + " normal session version and UTF-8 input/output");
                using (var session = ConPtySession.Start(shell, command, directory, 100, 30))
                {
                    var screen = new TerminalScreen(100, 30);
                    screen.Reply = text => { string error; session.TrySend(text, out error); };
                    timer.Restart();
                    bool exited = false;
                    while (timer.ElapsedMilliseconds < 15000)
                    {
                        string output;
                        while (session.TryDequeue(out output)) screen.Feed(output);
                        int code;
                        if (session.TryGetExitCode(out code)) { Require(code == 0, "ConPTY shell failed with code " + code); exited = true; break; }
                        Thread.Sleep(10);
                    }
                    Require(exited && screen.GetTranscript().Contains("SHELL-MAJOR:" + expected) && screen.GetTranscript().Contains("UNICODE-\uD55C\uAE00"),
                        "Wrong version or Unicode corruption in ConPTY " + shell + ": " + screen.GetTranscript());
                }
                Console.WriteLine("PASS: " + shell + " ConPTY session uses the selected version");
            }
            Console.WriteLine("PASS: 5 shell-selection integration checks");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
