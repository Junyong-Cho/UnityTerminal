using System;
using System.Diagnostics;
using System.Threading;

namespace UnityTerminal.Tests
{
    public static class GenericCliTests
    {
        // Opens only the installed CLI's startup screen. No prompts, auth or trust decisions.
        public static void ClaudeStartup(string directory) { ClaudeStartup(directory, false); }
        public static void ClaudeNewline(string directory) { ClaudeStartup(directory, true); }

        private static void ClaudeStartup(string directory, bool checkNewline)
        {
            ShellCatalog.Refresh();
            var timer = Stopwatch.StartNew();
            while (!ShellCatalog.DiscoveryComplete && timer.ElapsedMilliseconds < 15000) Thread.Sleep(10);
            string shell = ShellCatalog.ChooseInitialShell(null, ShellCatalog.GetOptions());
            using (var session = ConPtySession.Start(shell, "claude --safe-mode --permission-mode plan", directory, 120, 35))
            {
                var screen = new TerminalScreen(120, 35);
                bool shifted = false;
                int initialPrompt = -1, initialCursor = 0;
                screen.Reply = text => { string ignored; session.TrySend(text, out ignored); };
                timer.Restart();
                while (timer.ElapsedMilliseconds < 45000)
                {
                    string chunk;
                    while (session.TryDequeue(out chunk)) screen.Feed(chunk);
                    string text = screen.GetTranscript();
                    if (text.Contains("Claude Code") || text.Contains("Welcome to Claude"))
                    {
                        if (!checkNewline)
                        {
                            Console.WriteLine("PASS: installed Claude renders its startup UI through the shared ConPTY engine");
                            Console.WriteLine("INFO: no agent prompt or authentication/trust approval was submitted");
                            return;
                        }
                        // The composer is ready once its prompt row (❯ or ›) is visible; recent Claude versions no longer
                        // show "for shortcuts" in plan mode.
                        if (PromptRow(screen) >= 0)
                        {
                            int prompt = PromptRow(screen);
                            if (!shifted && prompt >= 0 && screen.CursorY >= prompt)
                            {
                                initialPrompt = prompt; initialCursor = screen.CursorY;
                                string error;
                                if (!session.TrySend(TerminalInput.EncodeShiftEnter(TerminalInput.UsesAltEnterForNewline("claude --safe-mode --permission-mode plan"), false), out error)) throw new Exception(error);
                                shifted = true;
                            }
                            else if (shifted && prompt >= 0 &&
                                (prompt < initialPrompt || screen.CursorY - prompt > initialCursor - initialPrompt))
                            {
                                Console.WriteLine("PASS: Shift+Enter expands the empty Claude composer to a new line");
                                Console.WriteLine("INFO: the composer stayed empty; no agent prompt was submitted");
                                return;
                            }
                        }
                        else if (text.IndexOf("trust", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            text.IndexOf("theme", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            text.IndexOf("sign in", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Console.WriteLine("INFO: Claude setup/authentication is required; no decisions or prompts were submitted");
                            return;
                        }
                    }
                    int code;
                    if (session.TryGetExitCode(out code)) throw new Exception("Claude exited before its startup UI, code " + code);
                    if (session.Failure != null) throw new Exception(session.Failure);
                    Thread.Sleep(10);
                }
                throw new Exception(checkNewline ? "Claude multiline composer was not observed within 45 seconds." : "Claude startup UI was not observed within 45 seconds.");
            }
        }
        private static int PromptRow(TerminalScreen screen)
        {
            for (int y = screen.Rows - 1; y >= 0; y--)
            {
                string line = TerminalScreen.LineText(screen.GetLine(y)).TrimStart();
                if (line.StartsWith("\u276f", StringComparison.Ordinal) || line.StartsWith("\u203a", StringComparison.Ordinal)) return y;
            }
            return -1;
        }
    }
}
