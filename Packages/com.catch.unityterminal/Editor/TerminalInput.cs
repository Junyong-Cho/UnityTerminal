using System;
using System.IO;
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

        public static string Encode(TerminalKey key, char character, bool shift, bool alt, bool control, bool applicationCursor)
        {
            int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);
            string result;
            switch (key)
            {
                case TerminalKey.Enter: return shift ? "\x1b[13;" + modifier + "u" : alt ? "\x1b\r" : "\r";
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
