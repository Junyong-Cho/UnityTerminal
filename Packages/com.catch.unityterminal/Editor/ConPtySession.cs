using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UnityTerminal
{
    // Windows 10 1809+. Each pipe is serviced independently, including during ClosePseudoConsole.
    internal sealed class ConPtySession : IDisposable
    {
        private readonly object outputLock = new object();
        private readonly object inputLock = new object();
        private readonly Queue<string> output = new Queue<string>();
        private readonly Queue<string> input = new Queue<string>();
        private IntPtr console;
        private IntPtr process;
        private IntPtr job;
        private StreamReader reader;
        private StreamWriter writer;
        private Thread readerThread;
        private Thread writerThread;
        private Thread closeThread;
        private int closing;
        private int inputCharacters;
        private volatile bool stopping;
        private volatile bool outputFinished;
        private bool disposed;
        private int columns;
        private int rows;
        public string Failure { get; private set; }

        public static ConPtySession Start(string shellType, string command, string directory, int columns, int rows)
        {
            string executable;
            string arguments;
            switch (shellType)
            {
                case "PowerShell":
                case "PowerShell7": executable = ShellCatalog.GetExecutable(shellType); arguments = "-NoLogo -Command " + QuoteArgument(command); break;
                case "CMD": executable = "cmd.exe"; arguments = "/d /s /c \"" + command + "\""; break;
                case "WSL": executable = "wsl.exe"; arguments = "--cd " + QuoteArgument(directory) + " --exec sh -lc " + QuoteArgument(command); break;
                default: throw new ArgumentException("Unknown shell: " + shellType);
            }
            return StartProcess(executable, arguments, directory, columns, rows);
        }

        // Separate entry point also permits deterministic native-console regression tests.
        internal static ConPtySession StartProcess(string executable, string arguments, string directory, int columns, int rows)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("Interactive terminals require Windows 10 version 1809 or newer.");
            var session = new ConPtySession { columns = columns, rows = rows };
            SafeFileHandle inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
            IntPtr attributes = IntPtr.Zero, environment = IntPtr.Zero, thread = IntPtr.Zero;
            bool attributesInitialized = false;
            try
            {
                Check(CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
                Check(CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));
                Marshal.ThrowExceptionForHR(CreatePseudoConsole(Size(columns, rows), inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out session.console));
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                attributesInitialized = true;
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), session.console, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));

                session.job = CreateJobObject(IntPtr.Zero, null);
                Check(session.job != IntPtr.Zero);
                var limits = new JobLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                Check(SetInformationJobObject(session.job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))));
                var startup = new StartupInfoEx { Attributes = attributes };
                startup.Startup.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                // Null handles with STARTF_USESTDHANDLES prevent Windows from duplicating
                // the host's redirected log pipes instead of connecting the child to ConPTY.
                startup.Startup.Flags = 0x00000100;
                ProcessInfo info;
                environment = BuildEnvironment();
                Check(CreateProcess(null, new StringBuilder(QuoteArgument(executable) + " " + arguments), IntPtr.Zero, IntPtr.Zero,
                    false, 0x00080000 | 0x00000400 | 0x00000004, environment, Path.GetFullPath(directory), ref startup, out info));
                session.process = info.Process;
                thread = info.Thread;
                Check(AssignProcessToJobObject(session.job, session.process));

                session.reader = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, false), new UTF8Encoding(false), false, 4096);
                outputRead = null;
                session.writer = new StreamWriter(new FileStream(inputWrite, FileAccess.Write, 4096, false), new UTF8Encoding(false)) { AutoFlush = true };
                inputWrite = null;
                session.readerThread = new Thread(session.ReadOutput) { IsBackground = true, Name = "Unity Terminal output" };
                session.writerThread = new Thread(session.WriteInput) { IsBackground = true, Name = "Unity Terminal input" };
                session.readerThread.Start();
                session.writerThread.Start();
                Check(ResumeThread(thread) != uint.MaxValue);
                return session;
            }
            catch (EntryPointNotFoundException)
            {
                session.Dispose();
                throw new PlatformNotSupportedException("Interactive terminals require Windows 10 version 1809 or newer.");
            }
            catch
            {
                session.Dispose();
                throw;
            }
            finally
            {
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (inputRead != null) inputRead.Dispose();
                if (inputWrite != null) inputWrite.Dispose();
                if (outputRead != null) outputRead.Dispose();
                if (outputWrite != null) outputWrite.Dispose();
            }
        }

        private static IntPtr BuildEnvironment()
        {
            var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) entries[(string)entry.Key] = (string)entry.Value;
            entries["TERM"] = "xterm-256color";
            entries["COLORTERM"] = "truecolor";
            var block = new StringBuilder();
            foreach (var entry in entries) block.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        internal static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private void ReadOutput()
        {
            var chars = new char[4096];
            try
            {
                int count;
                // Keep draining while the console closes, even if the UI no longer consumes output.
                while ((count = reader.Read(chars, 0, chars.Length)) > 0)
                {
                    string text = new string(chars, 0, count);
                    lock (outputLock)
                    {
                        while (!stopping && output.Count >= 128) Monitor.Wait(outputLock);
                        if (!stopping) output.Enqueue(text);
                    }
                }
            }
            catch (IOException ex) { if (!stopping && closing == 0) Failure = ex.Message; }
            catch (ObjectDisposedException) { }
            finally { reader.Dispose(); outputFinished = true; }
        }

        private void WriteInput()
        {
            try
            {
                while (true)
                {
                    string text;
                    lock (inputLock)
                    {
                        while (!stopping && closing == 0 && input.Count == 0) Monitor.Wait(inputLock);
                        if (stopping || closing != 0) break;
                        text = input.Dequeue();
                        inputCharacters -= text.Length;
                    }
                    writer.Write(text);
                }
            }
            catch (IOException ex) { if (!stopping && closing == 0) Failure = ex.Message; }
            catch (ObjectDisposedException) { }
            finally { try { writer.Dispose(); } catch (IOException) { } }
        }

        public bool TrySend(string text, out string error)
        {
            error = null;
            lock (inputLock)
            {
                if (stopping || closing != 0) { error = "The interactive session has ended."; return false; }
                if (text.Length + inputCharacters > 262144) { error = "Input is full. Paste a smaller amount of text."; return false; }
                input.Enqueue(text);
                inputCharacters += text.Length;
                Monitor.PulseAll(inputLock);
                return true;
            }
        }

        public bool TryDequeue(out string text)
        {
            lock (outputLock)
            {
                if (output.Count == 0) { text = null; return false; }
                text = output.Dequeue();
                Monitor.PulseAll(outputLock);
                return true;
            }
        }

        public void Resize(int width, int height)
        {
            if (disposed || closing != 0 || (columns == width && rows == height)) return;
            Marshal.ThrowExceptionForHR(ResizePseudoConsole(console, Size(width, height)));
            columns = width;
            rows = height;
        }

        public bool TryGetExitCode(out int code)
        {
            code = 0;
            if (disposed || process == IntPtr.Zero || WaitForSingleObject(process, 0) != 0) return false;
            uint nativeCode;
            Check(GetExitCodeProcess(process, out nativeCode));
            code = unchecked((int)nativeCode);
            BeginCloseConsole();
            lock (outputLock) return outputFinished && output.Count == 0;
        }

        private void BeginCloseConsole()
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            lock (inputLock) Monitor.PulseAll(inputLock);
            closeThread = new Thread(() =>
            {
                IntPtr handle = Interlocked.Exchange(ref console, IntPtr.Zero);
                if (handle != IntPtr.Zero) ClosePseudoConsole(handle);
            }) { IsBackground = true, Name = "Unity Terminal cleanup" };
            closeThread.Start();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            lock (outputLock) { output.Clear(); Monitor.PulseAll(outputLock); }
            lock (inputLock) { input.Clear(); Monitor.PulseAll(inputLock); }
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            // Also covers startup failure before the process was assigned to its job.
            if (process != IntPtr.Zero) { TerminateProcess(process, 1); CloseHandle(process); process = IntPtr.Zero; }
            BeginCloseConsole();
            if (readerThread == null && reader != null) reader.Dispose();
            if (writerThread == null && writer != null) writer.Dispose();
            // Never close the console on the editor thread: ClosePseudoConsole can block.
            if (closeThread != null) closeThread.Join(250);
        }

        private static Coord Size(int columns, int rows)
        {
            if (columns < 2 || columns > 500 || rows < 2 || rows > 300) throw new ArgumentOutOfRangeException("Terminal dimensions");
            return new Coord { X = (short)columns, Y = (short)rows };
        }

        private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedSize;
            public IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JobLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref JobLimits information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
