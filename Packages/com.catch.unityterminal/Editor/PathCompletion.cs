using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityTerminal
{
    internal sealed class PathCompletion
    {
        private const int MaxMatches = 256;
        private readonly List<string> matches = new List<string>();
        private string before;
        private string after;
        private string expectedInput;
        private string directory;
        private string shell;
        private int expectedCaret;
        private int index;

        public void Reset()
        {
            expectedInput = null;
            matches.Clear();
        }

        public bool TryComplete(string input, int caret, string workingDirectory, string shellType,
            out string completed, out int completedCaret)
        {
            if (ShellCatalog.IsPowerShell(shellType)) shellType = "PowerShell";
            completed = input;
            completedCaret = caret;
            caret = Math.Max(0, Math.Min(caret, input.Length));
            if (expectedInput == input && expectedCaret == caret && directory == workingDirectory && shell == shellType && matches.Count > 0)
            {
                index = (index + 1) % matches.Count;
            }
            else
            {
                Reset();
                int start, end;
                FindToken(input, caret, shellType, out start, out end);
                string rawPrefix = input.Substring(start, caret - start);
                char quote = rawPrefix.Length > 0 && IsQuote(rawPrefix[0], shellType) ? rawPrefix[0] : '\0';
                string prefix = DecodePrefix(rawPrefix, shellType);
                int slash = prefix.LastIndexOfAny(new[] { '/', '\\' });
                string pathPrefix = slash < 0 ? "" : prefix.Substring(0, slash + 1);
                string namePrefix = prefix.Substring(slash + 1);
                try
                {
                    string searchDirectory = GetSearchDirectory(workingDirectory, pathPrefix, shellType);
                    if (searchDirectory == null || !Directory.Exists(searchDirectory)) return false;
                    StringComparison comparison = shellType == "WSL" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    foreach (string entry in Directory.EnumerateFileSystemEntries(searchDirectory))
                    {
                        string name = Path.GetFileName(entry);
                        if (!name.StartsWith(namePrefix, comparison)) continue;
                        if (Directory.Exists(entry)) name += "/";
                        matches.Add(Quote(pathPrefix + name, quote, shellType));
                        if (matches.Count == MaxMatches) break;
                    }
                    matches.Sort(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) &&
                        !(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is SecurityException)) throw;
                    Reset();
                    return false;
                }
                if (matches.Count == 0) return false;
                before = input.Substring(0, start);
                after = input.Substring(end);
                directory = workingDirectory;
                shell = shellType;
                index = 0;
            }
            completed = before + matches[index] + after;
            completedCaret = before.Length + matches[index].Length;
            expectedInput = completed;
            expectedCaret = completedCaret;
            return true;
        }

        private static bool IsQuote(char c, string shellType)
        {
            return c == '"' || (c == '\'' && shellType != "CMD");
        }

        private static bool IsEscape(char c, string shellType, char quote)
        {
            if (quote == '\'') return false;
            return shellType == "PowerShell" ? c == '`' : shellType == "WSL" ? c == '\\' : c == '^';
        }

        private static void FindToken(string input, int caret, string shellType, out int start, out int end)
        {
            start = 0;
            end = input.Length;
            char quote = '\0';
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (IsEscape(c, shellType, quote) && i + 1 < input.Length) { i++; continue; }
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (shellType == "PowerShell" && quote == '\'' && i + 1 < input.Length && input[i + 1] == '\'') i++;
                        else quote = '\0';
                    }
                }
                else if (IsQuote(c, shellType)) quote = c;
                else if (char.IsWhiteSpace(c))
                {
                    if (i >= caret) { end = i; return; }
                    start = i + 1;
                }
            }
        }

        private static string DecodePrefix(string raw, string shellType)
        {
            var result = new StringBuilder();
            char quote = '\0';
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (IsEscape(c, shellType, quote) && i + 1 < raw.Length)
                {
                    result.Append(raw[++i]);
                }
                else if (quote != '\0' && c == quote)
                {
                    if (shellType == "PowerShell" && quote == '\'' && i + 1 < raw.Length && raw[i + 1] == '\'')
                    { result.Append(c); i++; }
                    else quote = '\0';
                }
                else if (quote == '\0' && IsQuote(c, shellType)) quote = c;
                else result.Append(c);
            }
            return result.ToString();
        }

        private static string Quote(string value, char quote, string shellType)
        {
            if (quote == '\0' && value.IndexOfAny(new[] { ' ', '\t', '\'', '"', '`', '$', '&', ';', '|', '(', ')', '<', '>', '[', ']', '*', '?' }) < 0)
                return value;
            if (shellType == "CMD") return "\"" + value + "\"";
            if (quote == '"')
            {
                string escape = shellType == "PowerShell" ? "`" : "\\";
                return "\"" + value.Replace(escape, escape + escape).Replace("\"", escape + "\"")
                    .Replace("$", escape + "$").Replace("`", shellType == "WSL" ? "\\`" : "`") + "\"";
            }
            return "'" + value.Replace("'", shellType == "PowerShell" ? "''" : "'\\''") + "'";
        }

        private static string GetSearchDirectory(string current, string prefix, string shellType)
        {
            if (shellType == "WSL" && prefix.StartsWith("/", StringComparison.Ordinal))
            {
                Match mount = Regex.Match(prefix, @"^/mnt/([a-zA-Z])(?:/|$)(.*)$");
                if (!mount.Success) return null;
                return mount.Groups[1].Value.ToUpperInvariant() + ":\\" + mount.Groups[2].Value.Replace('/', '\\');
            }
            return Path.GetFullPath(Path.Combine(current, prefix.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static bool TryReadWorkingDirectory(string prompt, out string directory)
        {
            directory = null;
            Match windows = Regex.Match(prompt.Trim(), @"^(?:PS\s+)?([a-zA-Z]:\\[^>]*)>");
            if (windows.Success) directory = windows.Groups[1].Value.Trim();
            else
            {
                Match wsl = Regex.Match(prompt, @"/mnt/([a-zA-Z])(/[^$#]*)?[$#]\s*$");
                if (wsl.Success)
                    directory = wsl.Groups[1].Value.ToUpperInvariant() + ":\\" + wsl.Groups[2].Value.Trim().TrimStart('/').Replace('/', '\\');
            }
            return directory != null && Directory.Exists(directory);
        }
    }
}
