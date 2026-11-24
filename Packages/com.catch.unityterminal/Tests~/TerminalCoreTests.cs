using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace UnityTerminal.Tests
{
    // Dependency-free regression runner. Tests~ is excluded from Unity package imports.
    public static class TerminalCoreTests
    {
        private static int passed;

        public static void Run(string fixtureDirectory, bool includeShellTests)
        {
            passed = 0;
            Directory.CreateDirectory(fixtureDirectory);
            Check("ANSI/CRLF are independent of chunk boundaries", ChunkBoundaries);
            Check("stdout and stderr keep independent parser states", IndependentStreams);
            Check("clear-screen, OSC and style reset", ControlSequences);
            Check("line, character and unbroken-line limits", BufferLimits);
            Check("rich text cache invalidation", BufferCache);
            Check("prompt brackets stay literal and HTML entities remain unchanged", LiteralOutput);
            Check("completion quotes spaces and preserves later arguments", () => QuotedCompletion(fixtureDirectory));
            Check("completion uses caret and cycles candidates", () => CaretAndCycling(fixtureDirectory));
            Check("completion escapes shell metacharacters", () => EscapedCompletion(fixtureDirectory));
            Check("prompt directory recognition", () => PromptDirectory(fixtureDirectory));
            Check("PowerShell 7 is listed only when available", ShellChoices);
            if (includeShellTests && Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                Check("failed startup cleans up safely", () => FailedStartup(fixtureDirectory));
                Check("shell drains trailing stdout and stderr before exit", () => DrainExit(fixtureDirectory));
                Check("PowerShell round-trips UTF-8 input and output", () => UnicodeRoundTrip(fixtureDirectory));
                Check("queue backpressure and repeated disposal", () => Backpressure(fixtureDirectory));
                Check("restart isolates output and terminates the previous process", () => Restart(fixtureDirectory));
                Check("cleanup terminates child processes", () => ChildCleanup(fixtureDirectory));
            }
            Console.WriteLine("PASS: " + passed + " regression groups");
        }

        private static void Check(string name, Action test)
        {
            test();
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Equal(string expected, string actual)
        {
            if (expected != actual) throw new Exception("Expected [" + expected + "], got [" + actual + "]");
        }

        private static void ChunkBoundaries()
        {
            string input = "first\r\n\x1b[31mred\x1b[0m normal\nprogress 10%\rprogress 20%\r\n" +
                "\x1b[1;3mboth\x1b[22;23mplain\x1b[38;2;10;20;30mrgb\x1b[38;5;196mpalette\x1b[0m\n";
            var reference = new TerminalBuffer();
            new AnsiParser(reference).Feed(input);
            Equal("first\nred normal\nprogress 20%\nbothplainrgbpalette\n\n", reference.GetPlainText());
            for (int split = 0; split <= input.Length; split++)
            {
                var buffer = new TerminalBuffer();
                var parser = new AnsiParser(buffer);
                parser.Feed(input.Substring(0, split));
                parser.Feed(input.Substring(split));
                Equal(reference.GetRichText(), buffer.GetRichText());
            }
            var singleCharacters = new TerminalBuffer();
            var incremental = new AnsiParser(singleCharacters);
            foreach (char c in input) incremental.Feed(c.ToString());
            Equal(reference.GetRichText(), singleCharacters.GetRichText());
        }

        private static void IndependentStreams()
        {
            var buffer = new TerminalBuffer();
            var stdout = new AnsiParser(buffer);
            var stderr = new AnsiParser(buffer, true);
            stdout.Feed("\x1b[3");
            stderr.Feed("error\n");
            stdout.Feed("2mgreen");
            Equal("error\ngreen\n", buffer.GetPlainText());
            Assert(buffer.GetRichText().Contains("<color=#f44747>error</color>"), "stderr default color missing");
            Assert(buffer.GetRichText().Contains("<color=#608b4e>green</color>"), "stdout escape was mixed with stderr");
        }

        private static void ControlSequences()
        {
            var buffer = new TerminalBuffer();
            var parser = new AnsiParser(buffer);
            parser.Feed("old\x1b[2");
            parser.Feed("J\x1b]0;title\x1b");
            parser.Feed("\\new\x1b[1mbold\x1b[22mplain\x1b[48;2;1;3;31mbackground");
            Equal("newboldplainbackground\n", buffer.GetPlainText());
            Equal("new<b>bold</b>plainbackground\n", buffer.GetRichText());
            parser.Reset();
            buffer.Clear();
            parser.Feed("reset");
            Equal("reset\n", buffer.GetRichText());
        }

        private static void BufferLimits()
        {
            var lines = new TerminalBuffer(3, 100, 100);
            new AnsiParser(lines).Feed("one\ntwo\nthree\nfour");
            Equal("two\nthree\nfour\n", lines.GetPlainText());
            var buffer = new TerminalBuffer(10, 12, 8);
            var parser = new AnsiParser(buffer);
            parser.Feed("1234567890");
            Equal("34567890\n", buffer.GetPlainText());
            parser.Feed("\nabcdefgh\nijk");
            Assert(buffer.CharacterCount <= 12, "Global character limit exceeded");
            Equal("abcdefgh\nijk\n", buffer.GetPlainText());
            parser.Feed(new string('z', 100000));
            Equal(new string('z', 8) + "\n", buffer.GetPlainText());
        }

        private static void BufferCache()
        {
            var buffer = new TerminalBuffer();
            var parser = new AnsiParser(buffer);
            parser.Feed("hello");
            string original = buffer.GetRichText();
            Assert(object.ReferenceEquals(original, buffer.GetRichText()), "Unchanged output was rebuilt");
            parser.Feed("!");
            Equal("hello!\n", buffer.GetRichText());
            buffer.Clear();
            Equal("\n", buffer.GetRichText());
        }

        private static void LiteralOutput()
        {
            var buffer = new TerminalBuffer();
            var parser = new AnsiParser(buffer);
            parser.Feed("PS C:\\Project>");
            Equal("PS C:\\Project>\n", buffer.GetRichText());
            buffer.Clear();
            parser.Feed("1 < 2 && 3 > 2");
            Equal("1 < 2 && 3 > 2\n", buffer.GetPlainText());
            Assert(!buffer.GetRichText().Contains("&lt;") && !buffer.GetRichText().Contains("&gt;"), "Output was converted into HTML entities");
            buffer.Clear();
            parser.Feed("&gt; &lt; &amp;");
            Equal("&gt; &lt; &amp;\n", buffer.GetRichText());
            buffer.Clear();
            parser.Feed("\x1b[31m<b>literal</b> </noparse>\x1b[0m");
            Assert(buffer.GetRichText().Contains("<color=#f44747>"), "ANSI color was lost");
            Assert(!buffer.GetRichText().Contains("<b>literal</b>"), "Literal markup was exposed to the formatter");
            Equal("<b>literal</b> </noparse>\n", buffer.GetPlainText());
        }

        private static void QuotedCompletion(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "hello world.txt"), "");
            var completion = new PathCompletion();
            string result;
            int caret;
            Assert(completion.TryComplete("cat hel --flag", 7, directory, "PowerShell", out result, out caret), "No match");
            Equal("cat 'hello world.txt' --flag", result);
            Assert(caret == "cat 'hello world.txt'".Length, "Caret moved into the following argument");
            completion.Reset();
            Assert(completion.TryComplete("cat \"hello w", 12, directory, "PowerShell", out result, out caret), "Unclosed quote failed");
            Equal("cat \"hello world.txt\"", result);
            completion.Reset();
            Assert(completion.TryComplete("cat \"hello w\" --flag", 12, directory, "PowerShell", out result, out caret), "Closed quote failed");
            Equal("cat \"hello world.txt\" --flag", result);
        }

        private static void CaretAndCycling(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "cycle-a.txt"), "");
            File.WriteAllText(Path.Combine(directory, "cycle-b.txt"), "");
            var completion = new PathCompletion();
            string result;
            int caret;
            Assert(completion.TryComplete("cat cyc --flag", 7, directory, "CMD", out result, out caret), "No match");
            Equal("cat cycle-a.txt --flag", result);
            Assert(completion.TryComplete(result, caret, directory, "CMD", out result, out caret), "No second match");
            Equal("cat cycle-b.txt --flag", result);
            Assert(completion.TryComplete(result, caret, directory, "CMD", out result, out caret), "No wraparound");
            Equal("cat cycle-a.txt --flag", result);
            Directory.CreateDirectory(Path.Combine(directory, "nested folder"));
            File.WriteAllText(Path.Combine(directory, "nested folder", "child.txt"), "");
            completion.Reset();
            string input = "cat 'nested folder/ch'";
            Assert(completion.TryComplete(input, input.Length - 1, directory, "PowerShell", out result, out caret), "Nested quoted path failed");
            Equal("cat 'nested folder/child.txt'", result);
        }

        private static void EscapedCompletion(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "cash$`' file.txt"), "");
            string result;
            int caret;
            var completion = new PathCompletion();
            Assert(completion.TryComplete("cat cash", 8, directory, "PowerShell", out result, out caret), "No PS match");
            Equal("cat 'cash$`'' file.txt'", result);
            completion.Reset();
            Assert(completion.TryComplete("cat cash", 8, directory, "PowerShell7", out result, out caret), "No PowerShell 7 match");
            Equal("cat 'cash$`'' file.txt'", result);
            completion.Reset();
            Assert(completion.TryComplete("cat \"cash", 9, directory, "PowerShell", out result, out caret), "No PS double-quoted match");
            Equal("cat \"cash`$``' file.txt\"", result);
            completion.Reset();
            Assert(completion.TryComplete("cat cash", 8, directory, "WSL", out result, out caret), "No WSL match");
            Equal("cat 'cash$`'\\'' file.txt'", result);
        }

        private static void PromptDirectory(string directory)
        {
            string actual;
            Assert(PathCompletion.TryReadWorkingDirectory("PS " + directory + ">", out actual), "PS prompt failed");
            Equal(directory, actual);
            Assert(PathCompletion.TryReadWorkingDirectory(directory + ">", out actual), "CMD prompt failed");
            Equal(directory, actual);
            string wsl = "/mnt/" + char.ToLowerInvariant(directory[0]) + directory.Substring(2).Replace('\\', '/');
            Assert(PathCompletion.TryReadWorkingDirectory("user@host:" + wsl + "$", out actual), "WSL prompt failed");
            Equal(directory, actual);
        }

        private static void ShellChoices()
        {
            ShellOption[] absent = ShellCatalog.BuildOptions(null);
            Assert(absent.Length == 3 && Array.FindIndex(absent, option => option.Id == "PowerShell7") < 0, "Uninstalled PowerShell 7 was listed");
            Assert(Array.FindIndex(absent, option => option.Id == "PowerShell" && option.Label == "Windows PowerShell 5.1") >= 0,
                "The legacy PowerShell preference/label was lost");
            ShellOption[] present = ShellCatalog.BuildOptions("pwsh.exe");
            Assert(present.Length == 4 && Array.FindIndex(present, option => option.Id == "PowerShell7" && option.Label == "PowerShell 7") >= 0,
                "Installed PowerShell 7 is not selectable");
            Assert(ShellCatalog.IsPowerShell("PowerShell") && ShellCatalog.IsPowerShell("PowerShell7") && !ShellCatalog.IsPowerShell("CMD"), "PowerShell versions use different input semantics");
            Equal("powershell.exe", ShellCatalog.GetExecutable("PowerShell"));
            Equal("PowerShell7", ShellCatalog.ChooseInitialShell(null, present));
            Equal("PowerShell7", ShellCatalog.ChooseInitialShell("", present));
            Equal("PowerShell", ShellCatalog.ChooseInitialShell(null, absent));
            Equal("PowerShell", ShellCatalog.ChooseInitialShell("PowerShell7", absent));
            Equal("PowerShell", ShellCatalog.ChooseInitialShell("PowerShell", present));
            Equal("CMD", ShellCatalog.ChooseInitialShell("CMD", present));
            Equal("WSL", ShellCatalog.ChooseInitialShell("WSL", present));
        }

        private static void FailedStartup(string directory)
        {
            bool failed = false;
            try { using (ShellSession.Start("CMD", Path.Combine(directory, "does-not-exist"))) { } }
            catch (System.ComponentModel.Win32Exception) { failed = true; }
            Assert(failed, "Invalid working directory should fail startup");
        }

        private static void DrainExit(string directory)
        {
            using (var session = ShellSession.Start("CMD", directory))
            {
                Send(session, "echo STDOUT-LAST& echo STDERR-LAST 1>&2& exit /b 7");
                string errors;
                int code;
                string text = DrainUntilExit(session, out errors, out code);
                Assert(text.Contains("STDOUT-LAST"), "Trailing stdout was lost");
                Assert(errors.Contains("STDERR-LAST"), "Trailing stderr was lost");
                Assert(code == 7, "Wrong exit code");
            }
        }

        private static void UnicodeRoundTrip(string directory)
        {
            using (var session = ShellSession.Start("PowerShell", directory))
            {
                Send(session, "[Console]::WriteLine(('UTF8-' + '\uD55C\uAE00')); exit 0");
                string errors;
                int code;
                string text = DrainUntilExit(session, out errors, out code);
                Assert(text.Contains("UTF8-\uD55C\uAE00"), "Unicode input/output was corrupted: " + text);
                Assert(code == 0, "PowerShell failed");
            }
        }

        private static void Backpressure(string directory)
        {
            var session = ShellSession.Start("CMD", directory);
            try
            {
                Send(session, "for /l %i in (1,1,10000) do @echo LINE-%i-abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789");
                Send(session, "exit /b 0");
                Thread.Sleep(300);
                var queue = (System.Collections.ICollection)typeof(ShellSession).GetField("output", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                object sync = typeof(ShellSession).GetField("queueLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                lock (sync) Assert(queue.Count <= 256, "Output queue is unbounded");
                string errors;
                int code;
                string text = DrainUntilExit(session, out errors, out code);
                int lines = 0;
                foreach (string line in text.Split('\n')) if (line.TrimEnd('\r').StartsWith("LINE-")) lines++;
                Assert(lines == 10000, "Backpressure lost output: " + lines);
                Assert(text.Contains("LINE-10000-"), "Final bulk output was lost");
            }
            finally { session.Dispose(); session.Dispose(); }
            string error;
            Assert(!session.TrySend("echo after-close", out error), "Disposed session accepted input");

            // Closing while producers wait for queue space must also release both workers.
            var blocked = ShellSession.Start("CMD", directory);
            try
            {
                Send(blocked, "for /l %i in (1,1,100000) do @echo LINE-%i-abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789");
                var queue = (System.Collections.ICollection)typeof(ShellSession).GetField("output", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(blocked);
                object sync = typeof(ShellSession).GetField("queueLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(blocked);
                var timer = Stopwatch.StartNew();
                bool full = false;
                while (!full && timer.ElapsedMilliseconds < 5000)
                {
                    lock (sync) full = queue.Count == 256;
                    if (!full) Thread.Sleep(10);
                }
                Assert(full, "Backpressure was not reached");
                Thread output = (Thread)typeof(ShellSession).GetField("outputThread", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(blocked);
                Thread errors = (Thread)typeof(ShellSession).GetField("errorThread", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(blocked);
                blocked.Dispose();
                Assert(!output.IsAlive && !errors.IsAlive, "Reader threads survived disposal under backpressure");
            }
            finally { blocked.Dispose(); }
        }

        private static void Restart(string directory)
        {
            for (int i = 0; i < 3; i++)
            {
                var previous = ShellSession.Start("CMD", directory);
                Process process = (Process)typeof(ShellSession).GetField("process", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(previous);
                int pid = process.Id;
                Send(previous, "echo OLD-SESSION");
                previous.Dispose();
                Assert(!IsAlive(pid), "Previous shell survived disposal");
                using (var current = ShellSession.Start("CMD", directory))
                {
                    Send(current, "echo NEW-SESSION& exit /b 0");
                    string errors;
                    int code;
                    string text = DrainUntilExit(current, out errors, out code);
                    Assert(text.Contains("NEW-SESSION") && !text.Contains("OLD-SESSION"), "Output leaked between sessions");
                }
            }
        }

        private static void ChildCleanup(string directory)
        {
            var session = ShellSession.Start("PowerShell", directory);
            int childId = 0;
            try
            {
                Send(session, "$info = New-Object Diagnostics.ProcessStartInfo; $info.FileName = 'cmd.exe'; $info.Arguments = '/d /q /k'; $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $child = [Diagnostics.Process]::Start($info); [Console]::WriteLine(('CHILD-ID:' + $child.Id))");
                var text = new StringBuilder();
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 10000 && childId == 0)
                {
                    ShellSession.OutputChunk chunk;
                    while (session.TryDequeue(out chunk)) text.Append(chunk.Text);
                    var match = System.Text.RegularExpressions.Regex.Match(text.ToString(), @"CHILD-ID:(\d+)\r?\n");
                    if (match.Success) childId = int.Parse(match.Groups[1].Value);
                    Thread.Sleep(10);
                }
                Assert(childId != 0, "No child process was started");
                session.Dispose();
                var exitTimer = Stopwatch.StartNew();
                while (IsAlive(childId) && exitTimer.ElapsedMilliseconds < 2000) Thread.Sleep(10);
                Assert(!IsAlive(childId), "Child process survived disposal. Cleanup error: " + session.CleanupError);
            }
            finally
            {
                session.Dispose();
                if (childId != 0 && IsAlive(childId))
                {
                    using (Process child = Process.GetProcessById(childId)) child.Kill();
                }
            }
        }

        private static bool IsAlive(int pid)
        {
            try { using (Process process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }

        private static void Send(ShellSession session, string command)
        {
            string error;
            Assert(session.TrySend(command, out error), "Send failed: " + error);
        }

        private static string DrainUntilExit(ShellSession session, out string errors, out int code)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var timer = Stopwatch.StartNew();
            code = 0;
            while (timer.ElapsedMilliseconds < 15000)
            {
                ShellSession.OutputChunk chunk;
                while (session.TryDequeue(out chunk)) (chunk.IsError ? stderr : stdout).Append(chunk.Text);
                if (session.TryGetExitCode(out code)) { errors = stderr.ToString(); return stdout.ToString(); }
                Thread.Sleep(5);
            }
            throw new Exception("Shell did not finish within 15 seconds. stdout: " + stdout + " stderr: " + stderr);
        }
    }
}
