using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class InteractiveTerminalTests
    {
        private static int passed;
        public static void Run(string probe, bool native, bool codex)
        {
            passed = 0;
            Check("cursor positioning, CR, erasure and repaint", ScreenEditing);
            Check("alternate buffer restores the original screen", AlternateScreen);
            Check("all chunk boundaries preserve VT state and Unicode", SplitSequences);
            Check("scroll region, editing, resize and bounded history", Scrolling);
            Check("terminal queries and extended colors", QueriesAndColors);
            Check("keyboard encoding, paste and Codex command detection", Keyboard);
            Check("WSL prompts use ConPTY while commands and management stay in PowerShell", WslCommands);
            if (native)
            {
                Check("ConPTY has real console handles, keys, resize and tail output", () => NativeConsole(probe));
                Check("closing a full output queue terminates all worker threads", () => NativeBackpressure(probe));
                Check("job cleanup terminates descendants", () => NativeChildren(probe));
                Check("startup failure can be retried", () => NativeStartupFailure(probe));
                if (codex) Check("installed Codex starts its interactive UI without an agent prompt", CodexStartup);
            }
            Console.WriteLine("PASS: " + passed + " interactive regression groups");
        }
        private static void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS: " + name); }
        private static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }
        private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception("Expected [" + expected + "], got [" + actual + "]"); }

        private static void ScreenEditing()
        {
            var screen = new TerminalScreen(20, 5);
            screen.Feed("old text\rnew\x1b[K\r\nsecond\x1b[1;5H!\x1b[2;1H\x1b[2Kdone");
            Equal("new !\ndone", screen.GetText().Replace("\r\n", "\n"));
            screen.Feed("\x1b[2J\x1b[H12345\x1b[1;3H\x1b[2@xx");
            Equal("12xx345", screen.GetText());
            screen.Feed("\x1b[1;3H\x1b[2P");
            Equal("12345", screen.GetText());
        }
        private static void AlternateScreen()
        {
            var screen = new TerminalScreen(20, 5);
            screen.Feed("shell\x1b[?1049h\x1b[Happ\x1b[?25l\x1b[?2004h");
            Assert(screen.Alternate && !screen.CursorVisible && screen.BracketedPaste, "Alternate modes missing");
            Equal("app", screen.GetText());
            screen.Feed("\x1b[?1049l");
            Equal("shell", screen.GetText());
            Assert(screen.CursorX == 5 && screen.LastAlternateText == "app", "Primary cursor or transcript was lost");
        }
        private static void SplitSequences()
        {
            string input = "\x1b]2;test title\x1b\\one\r\n\x1b[38;2;1;2;3m\uD55C\uAE00\U0001f600e\u0301\x1b[0m\x1b[3;4Hyes";
            var reference = new TerminalScreen(30, 6);
            reference.Feed(input);
            for (int split = 0; split <= input.Length; split++)
            {
                var actual = new TerminalScreen(30, 6);
                actual.Feed(input.Substring(0, split)); actual.Feed(input.Substring(split));
                Equal(reference.GetText(), actual.GetText());
                Assert(actual.CursorX == reference.CursorX && actual.CursorY == reference.CursorY && actual.Title == reference.Title, "Chunk boundaries changed state");
            }
            var incremental = new TerminalScreen(30, 6);
            foreach (char c in input) incremental.Feed(c.ToString());
            Equal(reference.GetText(), incremental.GetText());
            ScreenCell[] row = incremental.GetLine(1);
            Assert(row[0].Width == 2 && row[1].Width == 0 && row[4].Width == 2 && row[6].Text == "e\u0301", "Wide/combining glyphs shifted the cells");
        }
        private static void Scrolling()
        {
            var screen = new TerminalScreen(20, 4);
            screen.Feed("top\x1b[2;4r\x1b[2;1Hone\r\ntwo\r\nthree\r\nfour");
            Equal("top\ntwo\nthree\nfour", screen.GetText().Replace("\r\n", "\n"));
            Assert(screen.HistoryCount == 0, "Scroll regions leaked into scrollback");
            screen.Resize(8, 3);
            Assert(screen.Columns == 8 && screen.Rows == 3 && screen.CursorY < 3, "Resize left invalid cursor bounds");
            screen.Reset();
            screen.Feed(new string('\n', 10000));
            Assert(screen.HistoryCount == 512, "Scrollback was not bounded");
        }
        private static void QueriesAndColors()
        {
            var screen = new TerminalScreen(30, 10);
            var responses = new StringBuilder(); screen.Reply = value => responses.Append(value);
            screen.Feed("\x1b[3;4H\x1b[6n\x1b[5n\x1b[c\x1b[?u\x1b]11;?\a\x1b[18t");
            Assert(responses.ToString().Contains("\x1b[3;4R") && responses.ToString().Contains("\x1b[?0u") && responses.ToString().Contains("\x1b[8;10;30t"), "Required terminal queries were not answered");
            screen.Feed("\x1b[H\x1b[38;2;1;2;3;48;5;196;1;3;4;7mX\x1b[0mY");
            ScreenStyle style = screen.GetLine(0)[0].Style;
            Assert(style.Foreground == 0x010203 && style.Background == 0xff0000 && style.Bold && style.Italic && style.Underline && style.Inverse, "SGR attributes are incorrect");
            Assert(screen.GetLine(0)[1].Style.Equals(ScreenStyle.Default), "SGR reset failed");
        }
        private static void Keyboard()
        {
            Equal("\x03", TerminalInput.Encode(TerminalKey.Character, 'c', false, false, true, false));
            Equal("\x1b[A", TerminalInput.Encode(TerminalKey.Up, '\0', false, false, false, false));
            Equal("\x1bOA", TerminalInput.Encode(TerminalKey.Up, '\0', false, false, false, true));
            Equal("\x1b[1;5D", TerminalInput.Encode(TerminalKey.Left, '\0', false, false, true, false));
            Equal("\x1b[Z", TerminalInput.Encode(TerminalKey.Tab, '\t', true, false, false, false));
            Equal("\x1b[200~one\ntwo[201~\x1b[201~", TerminalInput.Paste("one\r\ntwo\x1b[201~", true));
            Assert(TerminalInput.IsCodexCommand("codex") && TerminalInput.IsCodexCommand("codex resume") &&
                TerminalInput.IsCodexCommand("& 'C:/some folder/codex.exe' --help") && !TerminalInput.IsCodexCommand("echo codex"), "Codex detection is incorrect");
            Equal("\"a\\\"b\\\\\"", ConPtySession.QuoteArgument("a\"b\\"));
        }

        private static void WslCommands()
        {
            foreach (string command in new[] {
                "wsl", "WSL.EXE", "wsl -d Ubuntu", "wsl --distribution 'Ubuntu Test' --user root",
                "wsl --cd ~", "wsl ~", "wsl --distribution=Ubuntu --cd=/tmp", "wsl --system",
                "& 'C:/Windows/System32/wsl.exe' -d Ubuntu", "wsl -e bash", "wsl --exec /bin/bash --login",
                "wsl -- bash -i", "wsl zsh", "wsl -d Ubuntu -- codex", "wsl -e codex resume" })
                Assert(TerminalInput.IsInteractiveWslCommand(command), "WSL prompt did not use ConPTY: " + command);
            foreach (string command in new[] {
                "echo wsl", "wsl-not-a-command", "wsl --list", "wsl -l -v", "wsl --status", "wsl --version",
                "wsl --help", "wsl --install", "wsl --update", "wsl --shutdown", "wsl --terminate Ubuntu",
                "wsl --unregister Ubuntu", "wsl uname -a", "wsl -d Ubuntu --exec uname -a", "wsl bash -lc 'echo hello'",
                "wsl -d", "wsl --user", "wsl --cd=", "wsl --exec", "wsl -d ''", "wsl; echo done",
                "wsl | Out-String", "wsl > output.txt", "wsl $distribution", "wsl -d \"$distribution\"", "wsl -d 'unfinished" })
                Assert(!TerminalInput.IsInteractiveWslCommand(command), "Noninteractive command was diverted: " + command);
        }

        private static void NativeConsole(string probe)
        {
            using (var session = ConPtySession.StartProcess(probe, "", Path.GetDirectoryName(probe), 80, 24))
            {
                var screen = Attach(session, 80, 24);
                WaitFor(session, screen, "READY|TTY:False:False");
                Send(session, "\x1b[A\t\x03\r\x7f");
                WaitFor(session, screen, "KEY:Backspace");
                string text = screen.GetTranscript();
                Assert(text.Contains("KEY:UpArrow") && text.Contains("KEY:Tab") && text.Contains("KEY:C:3:Control") && text.Contains("KEY:Enter"), "ConPTY did not deliver real console key events: " + text);
                Send(session, TerminalInput.Encode(TerminalKey.PageUp, '\0', false, false, false, false) +
                    TerminalInput.Encode(TerminalKey.PageDown, '\0', false, false, false, false));
                WaitFor(session, screen, "KEY:PageDown");
                Assert(screen.GetTranscript().Contains("KEY:PageUp"), "Application scroll did not deliver console page keys");
                session.Resize(100, 30); screen.Resize(100, 30); Send(session, "r");
                WaitFor(session, screen, "SIZE:100:30");
                Send(session, "\x04");
                DrainExit(session, screen);
                Assert(screen.GetTranscript().Contains("TAIL-BEFORE-EXIT"), "Final output was lost");
            }
        }
        private static void NativeBackpressure(string probe)
        {
            var session = ConPtySession.StartProcess(probe, "flood", Path.GetDirectoryName(probe), 80, 24);
            try
            {
                var timer = Stopwatch.StartNew();
                var queue = (ICollection)typeof(ConPtySession).GetField("output", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                object sync = typeof(ConPtySession).GetField("outputLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                bool full = false;
                while (timer.ElapsedMilliseconds < 10000 && !full) { lock (sync) full = queue.Count == 128; Thread.Sleep(10); }
                Assert(full, "Native output did not reach backpressure");
                session.Dispose(); session.Dispose();
                foreach (string field in new[] { "readerThread", "writerThread", "closeThread" })
                {
                    var thread = (Thread)typeof(ConPtySession).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(session);
                    Assert(thread.Join(5000), "Native worker did not terminate: " + field);
                }
            }
            finally { session.Dispose(); }
        }
        private static void NativeChildren(string probe)
        {
            var session = ConPtySession.StartProcess(probe, "child", Path.GetDirectoryName(probe), 80, 24);
            int child = 0;
            try
            {
                var screen = Attach(session, 80, 24);
                WaitFor(session, screen, "CHILD:");
                var timer = Stopwatch.StartNew();
                Match match;
                do { Pump(session, screen); match = Regex.Match(screen.GetTranscript(), @"CHILD:(\d+)"); Thread.Sleep(10); }
                while (!match.Success && timer.ElapsedMilliseconds < 3000);
                Assert(match.Success, "Child PID was missing"); child = int.Parse(match.Groups[1].Value);
                session.Dispose();
                timer.Restart();
                while (IsAlive(child) && timer.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                Assert(!IsAlive(child), "Child survived job cleanup");
            }
            finally
            {
                session.Dispose();
                if (child != 0 && IsAlive(child)) { using (Process process = Process.GetProcessById(child)) process.Kill(); }
            }
        }
        private static bool IsAlive(int id) { try { using (Process process = Process.GetProcessById(id)) return !process.HasExited; } catch (ArgumentException) { return false; } }
        private static void NativeStartupFailure(string probe)
        {
            bool failed = false;
            try { using (ConPtySession.StartProcess(probe, "", Path.Combine(Path.GetDirectoryName(probe), "missing"), 80, 24)) { } }
            catch (System.ComponentModel.Win32Exception) { failed = true; }
            Assert(failed, "Invalid working directory did not fail");
            using (var session = ConPtySession.StartProcess(probe, "", Path.GetDirectoryName(probe), 80, 24))
            { var screen = Attach(session, 80, 24); WaitFor(session, screen, "READY|"); Send(session, "\x04"); DrainExit(session, screen); }
        }

        private static void CodexStartup()
        {
            using (var session = ConPtySession.Start("PowerShell", "codex --no-daemon --sandbox read-only", Environment.CurrentDirectory, 100, 30))
            {
                var screen = Attach(session, 100, 30);
                var timer = Stopwatch.StartNew();
                bool seen = false;
                while (timer.ElapsedMilliseconds < 15000)
                {
                    Pump(session, screen);
                    string text = screen.GetText();
                    if (text.Contains("OpenAI Codex") || text.Contains("Welcome to Codex") || text.Contains("Sign in with ChatGPT") ||
                        text.IndexOf("trust this", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("trust the contents", StringComparison.OrdinalIgnoreCase) >= 0)
                    { seen = true; break; }
                    int code;
                    if (session.TryGetExitCode(out code)) break;
                    Thread.Sleep(10);
                }
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "Temp", "CodexInteractiveSmoke.txt"), screen.GetTranscript());
                Assert(seen, "Codex did not display its interactive UI: " + screen.GetTranscript());
                Assert(!screen.GetTranscript().Contains("stdin is not a terminal"), "Codex rejected the console");
                Assert(!screen.GetTranscript().Contains("Error finding codex home"), "Codex could not find its home directory");
            }
        }
        private static TerminalScreen Attach(ConPtySession session, int columns, int rows)
        {
            var screen = new TerminalScreen(columns, rows); screen.Reply = text => Send(session, text); return screen;
        }
        private static void Send(ConPtySession session, string text) { string error; Assert(session.TrySend(text, out error), "Native input failed: " + error); }
        private static void Pump(ConPtySession session, TerminalScreen screen) { string text; while (session.TryDequeue(out text)) screen.Feed(text); }
        private static void WaitFor(ConPtySession session, TerminalScreen screen, string marker)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 10000)
            {
                Pump(session, screen);
                if (screen.GetTranscript().Contains(marker)) return;
                int code;
                if (session.TryGetExitCode(out code)) break;
                Thread.Sleep(10);
            }
            throw new Exception("Native output missing " + marker + ": " + screen.GetTranscript() + " failure=" + session.Failure);
        }
        private static void DrainExit(ConPtySession session, TerminalScreen screen)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 10000)
            {
                Pump(session, screen); int code;
                if (session.TryGetExitCode(out code)) { Assert(code == 0, "Native child failed: " + code); return; }
                Thread.Sleep(10);
            }
            throw new Exception("Native child did not exit");
        }
    }
}
