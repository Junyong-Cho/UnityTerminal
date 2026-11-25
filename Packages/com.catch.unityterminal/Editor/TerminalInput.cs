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
                case TerminalKey.Backspace: result = "\x7f"; break;
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
