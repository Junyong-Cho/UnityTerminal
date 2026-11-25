using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class WslTerminalTests
    {
        // Requires an installed distribution. Only prints, completes a project directory and exits.
        public static void Run(string directory, string distribution)
        {
            ShellCatalog.Refresh();
            var timer = Stopwatch.StartNew();
            while (!ShellCatalog.DiscoveryComplete && timer.ElapsedMilliseconds < 30000) Thread.Sleep(10);
            foreach (ShellOption option in ShellCatalog.GetOptions())
            {
                using (var primary = ShellSession.Start(option.Id, directory))
                {
                    string error;
                    Require(primary.TrySend("$global:UnityWslParent = 'kept'; wsl.exe --list --quiet; [Console]::WriteLine('WSL-LIST-FINISHED')", out error), error);
                    string listing = ReadPrimary(primary, "\nWSL-LIST-FINISHED\n");
                    Require(listing.Contains(distribution) && !listing.Contains("\0"), "Distribution listing is missing or incorrectly decoded: " + listing);
                    Console.WriteLine("PASS: " + option.Id + " WSL listing stays in the primary stream and uses UTF-8");
                    string launch = "wsl.exe -d '" + distribution.Replace("'", "''") + "'";
                    Require(TerminalInput.IsInteractiveWslCommand(launch), "WSL launcher did not select the interactive page");
                    using (var nested = ConPtySession.Start(option.Id, launch, directory, 100, 30))
                    {
                        var screen = new TerminalScreen(100, 30);
                        screen.Reply = reply => Send(nested, reply);
                        Send(nested, "printf '\\n%s%s\\n' UNITY_ WSL_READY; printf 'PWD:%s\\n' \"$PWD\"\r");
                        ReadUntil(nested, screen, "\nUNITY_WSL_READY\n");
                        string linuxPath = "/mnt/" + char.ToLowerInvariant(directory[0]) + directory.Substring(2).Replace('\\', '/');
                        ReadUntil(nested, screen, "PWD:" + linuxPath);
                        Console.WriteLine("PASS: " + option.Id + " enters the Linux prompt in the project directory");
                        Thread.Sleep(300); Pump(nested, screen);
                        Send(nested, "ls");
                        Thread.Sleep(300); Pump(nested, screen);
                        ScreenCell[] promptLine = screen.GetLine(screen.CursorY);
                        int last = promptLine.Length - 1;
                        while (last >= 0 && (promptLine[last].Text == " " || promptLine[last].Width == 0)) last--;
                        Require(last >= 1 && screen.CursorX == last + Math.Max(1, promptLine[last].Width),
                            "WSL ls cursor is separated from the last glyph: x=" + screen.CursorX + ", last=" + last + ", line=" + TerminalScreen.LineText(promptLine));
                        Console.WriteLine("PASS: " + option.Id + " real WSL cursor is immediately after ls without extra spaces");
                        Send(nested, "\x15");
                        Send(nested, "test -d Pack\t && printf '\\n%s%s\\n' WSL_ TAB_OK\r");
                        ReadUntil(nested, screen, "\nWSL_TAB_OK\n");
                        Send(nested, "printf '\\n%s%s\\n' '\uD55C\uAE00-' WSL\r");
                        ReadUntil(nested, screen, "\n\uD55C\uAE00-WSL\n");
                        Console.WriteLine("PASS: " + option.Id + " Linux shell handles Tab completion and Korean input");
                        Send(nested, "exit 0\r");
                        timer.Restart();
                        int code;
                        while (!nested.TryGetExitCode(out code) && timer.ElapsedMilliseconds < 15000) { Pump(nested, screen); Thread.Sleep(10); }
                        Require(nested.TryGetExitCode(out code) && code == 0, "WSL did not exit normally");
                    }
                    Require(primary.TrySend("[Console]::WriteLine(('PARENT-' + $global:UnityWslParent)); exit 0", out error), error);
                    ReadPrimary(primary, "\nPARENT-kept\n");
                    Console.WriteLine("PASS: " + option.Id + " original PowerShell process and variables survive WSL exit");
                }
            }
        }
        private static string ReadPrimary(ShellSession session, string marker)
        {
            var text = new StringBuilder();
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 30000)
            {
                ShellSession.OutputChunk chunk;
                while (session.TryDequeue(out chunk)) text.Append(chunk.Text);
                string result = text.ToString().Replace("\r\n", "\n");
                if (result.Contains(marker)) return result;
                Thread.Sleep(10);
            }
            throw new Exception("PowerShell timed out waiting for " + marker + ": " + text);
        }
        private static void ReadUntil(ConPtySession session, TerminalScreen screen, string marker)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 30000)
            {
                Pump(session, screen);
                if (screen.GetTranscript().Replace("\r\n", "\n").Contains(marker)) return;
                int code;
                if (session.TryGetExitCode(out code)) throw new Exception("WSL exited with " + code + ": " + screen.GetTranscript());
                Thread.Sleep(10);
            }
            throw new Exception("WSL timed out waiting for " + marker + ": " + screen.GetTranscript());
        }
        private static void Pump(ConPtySession session, TerminalScreen screen) { string text; while (session.TryDequeue(out text)) screen.Feed(text); }
        private static void Send(ConPtySession session, string text) { string error; Require(session.TrySend(text, out error), error); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
