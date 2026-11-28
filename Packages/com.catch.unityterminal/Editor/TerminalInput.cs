using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityTerminal
{
    internal enum TerminalKey { Character, Enter, Backspace, Tab, Escape, Up, Down, Right, Left, Home, End, Insert, Delete, PageUp, PageDown, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }

    internal static class TerminalInput
    {
        public static bool IsCodexCommand(string command)
        {
            Match match = Regex.Match(command.TrimStart(), "^(?:&\\s+)?(?:\"([^\"]+)\"|'([^']+)'|([^\\s]+))");
            if (!match.Success) return false;
            string token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            token = token.Replace('\\', '/');
            string executable = token.Substring(token.LastIndexOf('/') + 1);
            return string.Equals(executable, "codex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executable, "codex.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executable, "codex.cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executable, "codex.ps1", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInteractiveWslCommand(string command)
        {
            List<string> tokens;
            if (!TryTokenizeCommand(command, out tokens) || tokens.Count == 0) return false;
            string executable = tokens[0].Replace('\\', '/');
            executable = executable.Substring(executable.LastIndexOf('/') + 1);
            if (!string.Equals(executable, "wsl", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(executable, "wsl.exe", StringComparison.OrdinalIgnoreCase)) return false;

            for (int i = 1; i < tokens.Count; i++)
            {
                string option = tokens[i];
                if (option == "~" || option == "--system") continue;
                if (option == "-d" || option == "--distribution" || option == "-u" || option == "--user" ||
                    option == "--cd" || option == "--shell-type")
                {
                    if (++i >= tokens.Count || tokens[i].Length == 0 || tokens[i].StartsWith("-", StringComparison.Ordinal)) return false;
                    continue;
                }
                if (option.StartsWith("--distribution=", StringComparison.Ordinal) || option.StartsWith("--user=", StringComparison.Ordinal) ||
                    option.StartsWith("--cd=", StringComparison.Ordinal) || option.StartsWith("--shell-type=", StringComparison.Ordinal))
                {
                    if (option.EndsWith("=", StringComparison.Ordinal)) return false;
                    continue;
                }
                if (option == "--exec" || option == "-e" || option == "--") i++;
                return IsInteractiveLinuxCommand(tokens, i);
            }
            return true;
        }

        private static bool IsInteractiveLinuxCommand(List<string> tokens, int start)
        {
            if (start >= tokens.Count) return false;
            string executable = tokens[start];
            executable = executable.Substring(executable.LastIndexOf('/') + 1);
            if (executable == "codex") return true;
            if (executable != "bash" && executable != "sh" && executable != "zsh" && executable != "fish" &&
                executable != "dash" && executable != "ksh" && executable != "pwsh" && executable != "nu") return false;
            // A shell with a command argument is a one-shot invocation, not a nested prompt.
            for (int i = start + 1; i < tokens.Count; i++)
                if (tokens[i] != "-l" && tokens[i] != "--login" && tokens[i] != "-i" && tokens[i] != "--interactive") return false;
            return true;
        }

        private static bool TryTokenizeCommand(string command, out List<string> tokens)
        {
            tokens = new List<string>();
            var token = new StringBuilder();
            char quote = '\0';
            bool started = false;
            command = command.Trim();
            int start = command.StartsWith("&", StringComparison.Ordinal) ? 1 : 0;
            for (int i = start; i < command.Length; i++)
            {
                char c = command[i];
                if (c == '`' && quote != '\'')
                {
                    if (++i >= command.Length) return false;
                    token.Append(command[i]); started = true;
                }
                else if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (quote == '\'' && i + 1 < command.Length && command[i + 1] == '\'') { token.Append(c); i++; }
                        else quote = '\0';
                    }
                    else
                    {
                        if (quote == '"' && c == '$') return false; // Leave PowerShell expressions in their original session.
                        token.Append(c);
                    }
                }
                else if (c == '\'' || c == '"') { quote = c; started = true; }
                else if (char.IsWhiteSpace(c))
                {
                    if (c == '\n' || c == '\r') return false;
                    if (started) { tokens.Add(token.ToString()); token.Length = 0; started = false; }
                }
                else
                {
                    if (";|&<>$(){}#".IndexOf(c) >= 0) return false;
                    token.Append(c); started = true;
                }
            }
            if (quote != '\0') return false;
            if (started) tokens.Add(token.ToString());
            return true;
        }

        // Programs whose composer treats ESC CR (Alt+Enter) as "insert a newline". Measured against real
        // sessions: current Claude Code reads the Win32 Shift+Enter sequence as a plain Enter (submits),
        // and both Claude and Codex insert a newline for ESC CR. Other programs keep the Win32 sequence:
        // using the alternate screen does not mean ESC CR is safe (Vim leaves insert mode on the ESC), and
        // PSReadLine reads ESC as "clear line". Add a program here only after checking its composer.
        private static readonly string[] AltEnterNewlinePrograms = { "claude", "codex" };

        // Whether the interactive session started by this command should get ESC CR for Shift+Enter.
        // Recognizes the program directly (claude, "C:\...\codex.exe", & claude --resume) or after
        // wsl options (wsl -e codex, wsl -d Ubuntu -- claude). A program started later from inside
        // a shell session (wsl, then claude) is not known here and keeps the Win32 sequence.
        // Only the words up to the program name are examined, so arguments may use PowerShell variables
        // or expressions (claude --add-dir "$PWD"). TryTokenizeCommand is deliberately strict for the WSL
        // auto-switch and rejects such commands as a whole, so it is not used here.
        public static bool UsesAltEnterForNewline(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            var dynamicWords = new List<bool>();
            List<string> words = LeadingWords(command, MaxLeadingWords, dynamicWords);
            if (words.Count == 0) return false;
            string program = ProgramName(words[0]);
            if (program == "wsl")
            {
                int index = WslProgramIndex(words);
                if (index < 0) return false;
                // A variable or expression among the wsl options makes the position uncertain.
                for (int i = 0; i <= index; i++) if (dynamicWords[i] == true) return false;
                program = ProgramName(words[index]);
            }
            else if (dynamicWords[0] == true) return false;
            return Array.IndexOf(AltEnterNewlinePrograms, program) >= 0;
        }

        private const int MaxLeadingWords = 16;

        // Splits the start of a command line into words, keeping quoted text together; quotes are
        // removed. dynamicWords[i] tells whether word i depends on PowerShell evaluation, judged per
        // character by its quoting: single quotes are literal; in double quotes only $ and ` expand
        // (a parenthesis there is a plain character, $( is caught by $); unquoted $, ( and ` start
        // expressions. Literal paths such as 'C:\Program Files (x86)\claude.exe' stay static.
        private static List<string> LeadingWords(string command, int maxWords, List<bool> dynamicWords)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            char quote = '\0';
            bool started = false, dynamic = false;
            string text = command.Trim();
            int start = text.StartsWith("&", StringComparison.Ordinal) ? 1 : 0;
            for (int i = start; i < text.Length && words.Count < maxWords; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else
                    {
                        if (quote == '"' && (c == '$' || c == '`')) dynamic = true;
                        word.Append(c);
                    }
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; started = true; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (started == true)
                    {
                        words.Add(word.ToString()); dynamicWords.Add(dynamic);
                        word.Length = 0; started = false; dynamic = false;
                    }
                    continue;
                }
                if (c == '$' || c == '(' || c == '`') dynamic = true;
                word.Append(c); started = true;
            }
            if (started == true && words.Count < maxWords) { words.Add(word.ToString()); dynamicWords.Add(dynamic); }
            return words;
        }

        private static string ProgramName(string token)
        {
            string name = token.Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1).ToLowerInvariant();
            foreach (string extension in new[] { ".exe", ".cmd", ".bat", ".ps1" })
                if (name.EndsWith(extension, StringComparison.Ordinal)) return name.Substring(0, name.Length - extension.Length);
            return name;
        }

        // Index of the Linux program in a wsl command line, or -1 when wsl only opens its shell.
        private static int WslProgramIndex(List<string> tokens)
        {
            for (int i = 1; i < tokens.Count; i++)
            {
                string option = tokens[i];
                if (option == "~" || option == "--system") continue;
                if (option == "-d" || option == "--distribution" || option == "-u" || option == "--user" ||
                    option == "--cd" || option == "--shell-type") { i++; continue; }
                if (option.StartsWith("--", StringComparison.Ordinal) && option.Contains("=")) continue;
                if (option == "--exec" || option == "-e" || option == "--") return i + 1 < tokens.Count ? i + 1 : -1;
                return i;
            }
            return -1;
        }

        // Click-to-move: the Left/Right keys that move the app's cursor from cursorX to targetX on the same
        // row. Apps do not agree on mouse clicks (Codex moves its cursor on an SGR click, Claude Code ignores
        // it), but they all move by arrow keys. One key per glyph, so a wide (Korean) glyph is one key.
        // The target is limited to the furthest of: just after the row's last non-blank glyph (faint suggestions excluded, see TextEnd), the cursor,
        // and reachedX (the furthest column the cursor reached on this row, so typed trailing spaces stay
        // reachable). Past that, Right at the end of the input can accept an inline suggestion (PSReadLine
        // predictions, fish). Up/Down are never sent: they often mean history.
        public static string CursorMoveKeys(ScreenCell[] line, int cursorX, int targetX, bool applicationCursor, int reachedX = -1)
        {
            if (line == null || line.Length == 0) return "";
            cursorX = Math.Max(0, Math.Min(cursorX, line.Length - 1));
            targetX = Math.Max(0, Math.Min(targetX, line.Length - 1));
            int limit = Math.Max(Math.Max(TextEnd(line, cursorX), cursorX), Math.Min(reachedX, line.Length - 1));
            if (targetX > limit) targetX = limit;
            while (targetX > 0 && targetX < line.Length && line[targetX].Width == 0) targetX--; // right half of a wide glyph
            int glyphs = 0;
            int from = Math.Min(cursorX, targetX), to = Math.Max(cursorX, targetX);
            for (int x = from; x < to && x < line.Length; x++) if (line[x].Width != 0) glyphs++;
            if (glyphs == 0) return "";
            string key = Encode(targetX > cursorX ? TerminalKey.Right : TerminalKey.Left, '\0', false, false, false, applicationCursor);
            var keys = new StringBuilder(key.Length * glyphs);
            for (int i = 0; i < glyphs; i++) keys.Append(key);
            return keys.ToString();
        }

        // Click-to-move must not send Right past the end of the input (at the end it can accept a PSReadLine
        // prediction or a fish suggestion), but typed trailing spaces look exactly like empty cells. The screen
        // alone cannot count them: deleting a space and moving left over it look the same, and the screen lags
        // behind input because apps echo later, sometimes in parts. So this models the input line from the keys
        // sent: the cursor position relative to the text end and the trailing spaces, updated per key by what that
        // key does at that position (Backspace removes left of the cursor, Delete right of it). The screen is
        // only read once input has been quiet for a while (settled), when it shows every key's effect.
        // When unsure it undercounts, which just makes a click stop short.
        internal sealed class TrailingSpaces
        {
            public int Row { get { return row; } }
            public int Count { get { return count; } }

            private int row = -1, textEnd = -1, count;
            private int offset;          // cursor relative to the text end: cells when >= 0, glyphs (negative) inside the text
            private bool offsetKnown, editedSinceSettled;

            private enum Key { Space, Text, Backspace, Delete, Left, Right, End, Lost, Reset }

            // Returns true when the input can have changed the text (anything but pure cursor movement).
            public bool OnSent(string text)
            {
                bool edited = false;
                for (int i = 0; i < text.Length; )
                {
                    int length;
                    Key key = Next(text, i, out length);
                    i += length;
                    if (key != Key.Left && key != Key.Right && key != Key.End && key != Key.Lost) edited = editedSinceSettled = true;
                    Apply(key);
                    if (key == Key.Reset) break;
                }
                return edited;
            }

            // Called on every repaint; only a settled screen (no input sent for a while, so it shows every
            // key's effect) is read. While apps are still echoing, the cursor can briefly sit elsewhere
            // (Claude redraws its whole input box), so an unsettled screen says nothing reliable. On a settled
            // screen: another row starts over; a changed text end with no edit sent since the last settled
            // screen means the app redrew the row itself, which also starts over; the cursor position is then
            // taken from the screen, and a cursor past the text end proves that many spaces.
            public void OnRepaint(int cursorRow, int cursorX, ScreenCell[] line, bool settled)
            {
                if (settled == false) return;
                int end = TextEnd(line, cursorX);
                if (cursorRow != row) { row = cursorRow; count = 0; }
                else if (end != textEnd && editedSinceSettled == false) count = 0;
                textEnd = end;
                editedSinceSettled = false;
                if (cursorX >= end) offset = cursorX - end;
                else
                {
                    offset = 0;
                    for (int x = cursorX; x < end && x < line.Length; x++) if (line[x].Width != 0) offset--;
                }
                offsetKnown = true;
                if (offset > count) count = offset;   // only typed input can put the cursor there
            }

            private void Apply(Key key)
            {
                switch (key)
                {
                    case Key.Reset: count = 0; offsetKnown = false; return;
                    case Key.Lost: offsetKnown = false; return;
                    case Key.End: offset = count; offsetKnown = true; return;
                }
                if (offsetKnown == false)
                {
                    // Position unknown: only ever lower the count.
                    if (key == Key.Text) count = 0;
                    else if (key == Key.Backspace || key == Key.Delete) count = Math.Max(0, count - 1);
                    return;
                }
                switch (key)
                {
                    case Key.Left: offset--; return;
                    case Key.Right:
                        if (offset < count) offset++;
                        else { count = 0; offset = 0; }    // at the end: may have accepted a suggestion
                        return;
                    case Key.Space: if (offset >= 0) { count++; offset++; } return;     // inside the text both shift together
                    case Key.Text: if (offset >= 0) { count = Math.Max(0, count - offset); offset = 0; } return;
                    case Key.Backspace: if (offset > 0) { count--; offset--; } return;  // at the text end it removes a glyph
                    case Key.Delete:
                        if (offset >= 0) { if (offset < count) count--; }
                        else offset++;                      // removes a glyph after the cursor: the text end comes closer
                        return;
                }
            }

            private static readonly string[] Lefts = { "\x1b[D", "\x1bOD" }, Rights = { "\x1b[C", "\x1bOC" }, Ends = { "\x1b[F", "\x1bOF" }, Homes = { "\x1b[H", "\x1bOH" };

            private static Key Next(string text, int i, out int length)
            {
                char c = text[i];
                length = 1;
                if (c == '\x7f' || c == '\b') return Key.Backspace;
                if (c == '\x1b')
                {
                    length = 4;
                    if (string.CompareOrdinal(text, i, "\x1b[3~", 0, 4) == 0) return Key.Delete;
                    length = 3;
                    if (Matches(text, i, Lefts)) return Key.Left;
                    if (Matches(text, i, Rights)) return Key.Right;
                    if (Matches(text, i, Ends)) return Key.End;
                    if (Matches(text, i, Homes)) return Key.Lost;
                    // Modified Left/Right/Home (word moves): position no longer known. Modified End: still the end.
                    if (string.CompareOrdinal(text, i, "\x1b[1;", 0, 4) == 0 && i + 5 < text.Length && char.IsDigit(text[i + 4]))
                    {
                        length = 6;
                        if (text[i + 5] == 'F') return Key.End;
                        if ("DCH".IndexOf(text[i + 5]) >= 0) return Key.Lost;
                    }
                    length = text.Length - i;
                    return Key.Reset;
                }
                if (c < ' ') { length = text.Length - i; return Key.Reset; }
                return c == ' ' ? Key.Space : Key.Text;
            }

            private static bool Matches(string text, int i, string[] keys)
            {
                foreach (string key in keys) if (string.CompareOrdinal(text, i, key, 0, key.Length) == 0) return true;
                return false;
            }
        }

        // Column just after the row's last non-blank glyph that is input (0 for a blank row); no allocation.
        // Faint (SGR 2) glyphs at or after the cursor are not input: apps draw placeholders and inline
        // suggestions there that way (Claude Code's "Try ..." and next-prompt suggestion, PSReadLine
        // predictions), and Right at the real end would accept the suggestion.
        internal static int TextEnd(ScreenCell[] line, int cursorX)
        {
            for (int x = line.Length - 1; x >= 0; x--)
                if (line[x].Width != 0 && string.IsNullOrEmpty(line[x].Text) == false && line[x].Text != " " && (x < cursorX || line[x].Style.Dim == false)) return x + Math.Max(1, line[x].Width);
            return 0;
        }

        // Shift+Enter as a "new line, don't submit" key; see AltEnterNewlinePrograms.
        public static string EncodeShiftEnter(bool altEnterNewline, bool applicationCursor)
        {
            if (altEnterNewline == true) return "\x1b\r";
            return Encode(TerminalKey.Enter, '\0', true, false, false, applicationCursor);
        }

        public static string Encode(TerminalKey key, char character, bool shift, bool alt, bool control, bool applicationCursor)
        {
            int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
            string result;
            switch (key)
            {
                case TerminalKey.Enter:
                    if (shift)
                    {
                        // ConPTY does not decode CSI-u into console key records. Its Win32
                        // input sequence preserves Shift for native Windows console apps.
                        int state = 16 | (alt ? 2 : 0) | (control ? 8 : 0);
                        return "\x1b[13;28;13;1;" + state + ";1_\x1b[13;28;13;0;" + state + ";1_";
                    }
                    return alt ? "\x1b\r" : "\r";
                // Ctrl+Backspace deletes the previous word as ^W (0x17). Measured: ^W deletes a word in
                // Claude, Codex, PSReadLine (5.1/7) and WSL bash; ^H and the Win32 input sequence delete
                // only one character in bash, and ESC DEL would be read as Esc by Vim and PSReadLine.
                case TerminalKey.Backspace: result = control && !alt ? "\x17" : "\x7f"; break;
                case TerminalKey.Tab: return shift ? "\x1b[Z" : "\t";
                case TerminalKey.Escape: return "\x1b";
                case TerminalKey.Up: case TerminalKey.Down: case TerminalKey.Right: case TerminalKey.Left: case TerminalKey.Home: case TerminalKey.End:
                    string code = key == TerminalKey.Up ? "A" : key == TerminalKey.Down ? "B" : key == TerminalKey.Right ? "C" : key == TerminalKey.Left ? "D" : key == TerminalKey.Home ? "H" : "F";
                    return modifier > 1 ? "\x1b[1;" + modifier + code : (applicationCursor ? "\x1bO" : "\x1b[") + code;
                case TerminalKey.Insert: case TerminalKey.Delete: case TerminalKey.PageUp: case TerminalKey.PageDown:
                    int number = key == TerminalKey.Insert ? 2 : key == TerminalKey.Delete ? 3 : key == TerminalKey.PageUp ? 5 : 6;
                    return "\x1b[" + number + (modifier > 1 ? ";" + modifier : "") + "~";
                default:
                    if (key >= TerminalKey.F1 && key <= TerminalKey.F12)
                    {
                        int f = (int)key - (int)TerminalKey.F1;
                        if (f < 4) return modifier == 1 ? "\x1bO" + (char)('P' + f) : "\x1b[1;" + modifier + (char)('P' + f);
                        int[] functions = { 15, 17, 18, 19, 20, 21, 23, 24 };
                        return "\x1b[" + functions[f - 4] + (modifier > 1 ? ";" + modifier : "") + "~";
                    }
                    if (character == '\0') return null;
                    if (control && character >= ' ')
                    {
                        char upper = char.ToUpperInvariant(character);
                        if (upper >= '@' && upper <= '_') character = (char)(upper - '@');
                        else if (character == ' ') character = '\0';
                    }
                    result = character.ToString();
                    break;
            }
            return (alt ? "\x1b" : "") + result;
        }

        public static string Paste(string text, bool bracketed)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            // A pasted escape must not terminate the bracketed paste and become terminal input.
            if (bracketed) return "\x1b[200~" + text.Replace("\x1b", "") + "\x1b[201~";
            return text.Replace('\n', '\r');
        }
    }
}
