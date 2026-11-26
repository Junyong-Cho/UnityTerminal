// Host-shared source: Host~/Build-Host.ps1 also compiles this file with Windows PowerShell 5.1 Add-Type (C# 5).
// Keep it C# 5 compatible (no string interpolation, ?., nameof, expression-bodied members, etc.). See README "호스트 빌드 제약".
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityTerminal
{
    internal sealed class ShellOption
    {
        public readonly string Id;
        public readonly string Label;
        public ShellOption(string id, string label) { Id = id; Label = label; }
    }

    // The legacy preference ID stays "PowerShell" so existing selections remain 5.1.
    internal static class ShellCatalog
    {
        private static readonly object sync = new object();
        private static string powerShell7;
        private static bool discovering;
        private static bool completed;
        private static int revision;

        public static bool DiscoveryComplete { get { lock (sync) return completed && !discovering; } }
        public static int Revision { get { lock (sync) return revision; } }

        public static bool IsPowerShell(string id) { return id == "PowerShell" || id == "PowerShell7"; }
        public static bool IsKnownShell(string id) { return IsPowerShell(id); }

        public static void Refresh()
        {
            lock (sync)
            {
                if (discovering) return;
                discovering = true;
            }
            // Starting pwsh can take a moment on first launch. Never probe in OnGUI.
            new Thread(() =>
            {
                string detected = null;
                try { detected = DetectPowerShell7(); }
                catch (Exception) { /* An optional shell must never break the editor during discovery. */ }
                finally
                {
                    lock (sync)
                    {
                        powerShell7 = detected;
                        completed = true;
                        discovering = false;
                        revision++;
                    }
                }
            }) { IsBackground = true, Name = "Unity Terminal shell discovery" }.Start();
        }

        public static ShellOption[] GetOptions()
        {
            int ignored;
            return GetOptions(out ignored);
        }

        public static ShellOption[] GetOptions(out int version)
        {
            lock (sync)
            {
                version = revision;
                return BuildOptions(powerShell7);
            }
        }

        internal static ShellOption[] BuildOptions(string powerShell7Executable)
        {
            var options = new List<ShellOption>();
            if (!string.IsNullOrEmpty(powerShell7Executable)) options.Add(new ShellOption("PowerShell7", "PowerShell 7"));
            options.Add(new ShellOption("PowerShell", "Windows PowerShell 5.1"));
            return options.ToArray();
        }

        internal static string ChooseInitialShell(string preferred, ShellOption[] available)
        {
            if (Array.Exists(available, option => option.Id == preferred)) return preferred;
            return Array.Exists(available, option => option.Id == "PowerShell7") ? "PowerShell7" : "PowerShell";
        }

        public static string GetExecutable(string id)
        {
            if (id == "PowerShell") return "powershell.exe";
            if (id != "PowerShell7") throw new ArgumentException("Unknown shell: " + id);
            lock (sync)
            {
                if (powerShell7 != null) return powerShell7;
            }
            throw new FileNotFoundException("PowerShell 7 is unavailable. Reopen Terminal to refresh the installed shells.");
        }

        internal static string DetectPowerShell7()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            // Probe PATH first. Store execution aliases can launch even when File.Exists is false.
            string resolved;
            if (TryResolveVersion7("pwsh.exe", out resolved)) return resolved;
            var candidates = new List<string>();
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            AddCandidate(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddCandidate(candidates, local, "Microsoft", "WindowsApps", "pwsh.exe");
            AddCandidate(candidates, local, "Microsoft", "WindowsApps", "Microsoft.PowerShell_8wekyb3d8bbwe", "pwsh.exe");
            foreach (string candidate in candidates)
                if (TryResolveVersion7(candidate, out resolved)) return resolved;
            return null;
        }

        private static void AddCandidate(List<string> candidates, string root, params string[] parts)
        {
            if (string.IsNullOrEmpty(root)) return;
            string path = root;
            foreach (string part in parts) path = Path.Combine(path, part);
            if (!candidates.Contains(path)) candidates.Add(path);
        }

        internal static bool IsVersion7(string executable)
        {
            string resolved;
            return TryResolveVersion7(executable, out resolved);
        }

        private static bool TryResolveVersion7(string executable, out string resolved)
        {
            resolved = null;
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::WriteLine($PSVersionTable.PSVersion.Major); " +
                            "[Console]::Write([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        process.WaitForExit(500);
                        return false;
                    }
                    if (process.ExitCode != 0 || !stdout.Wait(500)) return false;
                    string[] result = stdout.Result.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (result.Length != 2 || result[0] != "7" || !Path.IsPathRooted(result[1])) return false;
                    // Store aliases launch a brokered process outside our Windows job.
                    // Start the runtime's actual executable so lifetime and PID remain ours.
                    resolved = result[1];
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is Win32Exception) && !(ex is IOException) && !(ex is InvalidOperationException) &&
                    !(ex is UnauthorizedAccessException) && !(ex is System.Security.SecurityException)) throw;
                return false;
            }
        }
    }
}
