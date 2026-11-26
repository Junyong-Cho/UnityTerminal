// Host-shared source: Host~/Build-Host.ps1 also compiles this file with Windows PowerShell 5.1 Add-Type (C# 5).
// Keep it C# 5 compatible (no string interpolation, ?., nameof, expression-bodied members, etc.). See README "호스트 빌드 제약".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityTerminal
{
    internal sealed class ShellSession : IDisposable
    {
        internal struct OutputChunk
        {
            public string Text;
            public bool IsError;
        }

        private const int MaxQueuedChunks = 256;
        private readonly object queueLock = new object();
        private readonly Queue<OutputChunk> output = new Queue<OutputChunk>();
        private readonly Process process;
        private readonly string newline;
        private StreamWriter input;
        private StreamReader outputReader;
        private StreamReader errorReader;
        private Thread outputThread;
        private Thread errorThread;
        private volatile bool stopping;
        private volatile bool outputFinished;
        private volatile bool errorFinished;
        private bool started;
        private bool disposed;

        public string CleanupError { get; private set; }

        private ShellSession(string shellType, string workingDirectory, string executablePath = null)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("PowerShell sessions currently require Windows.");

            string executable;
            string arguments;
            switch (shellType)
            {
                case "PowerShell":
                case "PowerShell7":
                    executable = executablePath ?? ShellCatalog.GetExecutable(shellType);
                    // Configure stdin before PowerShell starts reading redirected commands.
                    arguments = "-NoLogo -NoExit -Command \"[Console]::InputEncoding = [System.Text.Encoding]::UTF8; " +
                        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [Console]::OutputEncoding\"";
                    break;
                default: throw new ArgumentException("Unknown shell: " + shellType);
            }
            newline = "\r\n";
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.EnvironmentVariables["FORCE_COLOR"] = "1";
            startInfo.EnvironmentVariables["CLICOLOR_FORCE"] = "1";
            startInfo.EnvironmentVariables["TERM"] = "xterm-256color";
            startInfo.EnvironmentVariables["WSL_UTF8"] = "1";
            // Preserve any Git configuration supplied by the host environment.
            string gitConfig = startInfo.EnvironmentVariables["GIT_CONFIG_PARAMETERS"];
            startInfo.EnvironmentVariables["GIT_CONFIG_PARAMETERS"] =
                (string.IsNullOrEmpty(gitConfig) ? "" : gitConfig + " ") + "'color.ui=always'";
            process = new Process { StartInfo = startInfo };
        }

        public static ShellSession Start(string shellType, string workingDirectory)
        {
            return StartCore(new ShellSession(shellType, workingDirectory));
        }

        internal static ShellSession StartExecutable(string executable, string workingDirectory)
        {
            return StartCore(new ShellSession("PowerShell", workingDirectory, executable));
        }

        internal int ProcessId { get { return started && !disposed ? process.Id : 0; } }

        private static ShellSession StartCore(ShellSession session)
        {
            try
            {
                session.process.Start();
                session.started = true;
                session.input = new StreamWriter(session.process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                // Capture the readers now; workers never refer to a replaceable window field.
                StreamReader stdout = session.outputReader = session.process.StandardOutput;
                StreamReader stderr = session.errorReader = session.process.StandardError;
                session.outputThread = new Thread(() => session.ReadStream(stdout, false)) { IsBackground = true };
                session.errorThread = new Thread(() => session.ReadStream(stderr, true)) { IsBackground = true };
                session.outputThread.Start();
                session.errorThread.Start();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private void ReadStream(StreamReader reader, bool isError)
        {
            var chars = new char[2048];
            try
            {
                int count;
                // Read through EOF, including bytes buffered after the process exits.
                while (!stopping && (count = reader.Read(chars, 0, chars.Length)) > 0)
                {
                    var chunk = new OutputChunk { Text = new string(chars, 0, count), IsError = isError };
                    lock (queueLock)
                    {
                        // Backpressure bounds memory without dropping escape sequences or output.
                        while (!stopping && output.Count >= MaxQueuedChunks) Monitor.Wait(queueLock);
                        if (stopping) break;
                        output.Enqueue(chunk);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (isError) errorFinished = true;
                else outputFinished = true;
            }
        }

        public bool TryDequeue(out OutputChunk chunk)
        {
            lock (queueLock)
            {
                if (output.Count == 0) { chunk = default(OutputChunk); return false; }
                chunk = output.Dequeue();
                Monitor.PulseAll(queueLock);
                return true;
            }
        }

        public bool TryGetExitCode(out int exitCode)
        {
            exitCode = 0;
            if (disposed || !started || !outputFinished || !errorFinished) return false;
            lock (queueLock)
            {
                if (output.Count != 0 || !process.HasExited) return false;
                exitCode = process.ExitCode;
                return true;
            }
        }

        public bool TrySend(string command, out string error)
        {
            error = null;
            try
            {
                if (stopping || !started || process.HasExited)
                {
                    error = "The shell session is not running. Use Restart to start a new session.";
                    return false;
                }
                // Redirected PowerShell reads input like its console: a multi-line block (if, function,
                // loop, here-string) waits at the >> prompt until an empty line, so end multi-line input
                // with one. Blank lines inside an open block do not end it early.
                string text = command;
                if (text.IndexOf('\n') >= 0) text = text.Replace("\r\n", "\n").Replace("\n", newline) + newline;
                input.Write(text + newline);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is InvalidOperationException)) throw;
                error = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            lock (queueLock) Monitor.PulseAll(queueLock);
            try
            {
                if (started && !process.HasExited) KillProcessTree();
            }
            catch (Exception ex) { CleanupError = ex.Message; }
            finally
            {
                // Killing the tree closes the pipe writers and unblocks pending reads.
                JoinReader(outputThread);
                JoinReader(errorThread);
                try { if (input != null) input.Dispose(); }
                catch (IOException) { }
                if (outputReader != null) outputReader.Dispose();
                if (errorReader != null) errorReader.Dispose();
                process.Dispose();
                lock (queueLock) output.Clear();
            }
        }

        private void KillProcessTree()
        {
            try
            {
                var killInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/F /T /PID " + process.Id,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process killer = Process.Start(killInfo))
                {
                    // Drain both pipes while waiting; a large process tree may produce many lines.
                    var stdout = killer.StandardOutput.ReadToEndAsync();
                    var stderr = killer.StandardError.ReadToEndAsync();
                    if (!killer.WaitForExit(1500))
                    {
                        killer.Kill();
                        CleanupError = "Timed out while terminating the shell process tree.";
                    }
                    else if (killer.ExitCode != 0 && !process.HasExited)
                        CleanupError = "taskkill failed with code " + killer.ExitCode + ". " +
                            (stderr.Wait(200) ? stderr.Result.Trim() : "");
                }
            }
            catch (Exception ex) { CleanupError = ex.Message; }
            finally
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(500);
            }
        }

        private static void JoinReader(Thread thread)
        {
            if (thread != null && thread.IsAlive) thread.Join(500);
        }
    }
}
