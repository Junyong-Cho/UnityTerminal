using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal
{
    // Tools > Unity Terminal > Capture Diagnostics: writes everything the terminal knows at that moment
    // to <project>/Logs/UnityTerminal/diagnostics-<time>.txt (Logs is Unity's log folder; Temp is wiped
    // on restart). For each terminal window: window, primary and interactive state, the interactive
    // view's focus/IME/selection state, its recent input decisions and screen text, and the host's own
    // state with its recent commands and raw app output. The file can contain whatever was on screen.
    [InitializeOnLoad]
    internal static class TerminalDiagnostics
    {
        // Recent Unity errors and exceptions (any source) since the last script reload, for the capture.
        private const int RecentErrorLimit = 50;
        private static readonly object recentErrorsGate = new object();
        private static readonly Queue<string> recentErrors = new Queue<string>();

        static TerminalDiagnostics()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() == true) return;
            Application.logMessageReceivedThreaded += OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string[] stack = (stackTrace ?? "").Split('\n');
            var entry = new StringBuilder();
            entry.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(type).Append(": ").Append(condition);
            for (int i = 0; i < stack.Length && i < 6; i++) if (stack[i].Trim().Length > 0) entry.Append("\n    ").Append(stack[i].Trim());
            lock (recentErrorsGate)
            {
                recentErrors.Enqueue(entry.ToString());
                while (recentErrors.Count > RecentErrorLimit) recentErrors.Dequeue();
            }
        }

        private const string MenuRoot = "Tools/Unity Terminal/";
        private const int HostWaitMilliseconds = 3000;
        private const int KeepFiles = 20;

        internal static string Folder
        {
            get { return Path.Combine(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs"), "UnityTerminal"); }
        }

        [MenuItem(MenuRoot + "Capture Diagnostics", false, 1100)]
        private static void CaptureMenu()
        {
            string path = Capture();
            GUIUtility.systemCopyBuffer = path;
            UnityEngine.Debug.Log("Unity Terminal diagnostics saved (path copied to clipboard): " + path);
        }

        [MenuItem(MenuRoot + "Open Diagnostics Folder", false, 1101)]
        private static void OpenFolderMenu()
        {
            Directory.CreateDirectory(Folder);
            EditorUtility.RevealInFinder(Folder);
        }

        internal static string Capture()
        {
            Directory.CreateDirectory(Folder);
            // Milliseconds plus a counter, so quick repeated captures never overwrite each other.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(Folder, "diagnostics-" + stamp + ".txt");
            for (int n = 1; File.Exists(path); n++)
            {
                stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + n;
                path = Path.Combine(Folder, "diagnostics-" + stamp + ".txt");
            }
            var text = new StringBuilder();
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TerminalDiagnostics).Assembly);
            text.AppendLine("Unity Terminal diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            text.AppendLine("unity=" + Application.unityVersion + " os=" + SystemInfo.operatingSystem + " package=" +
                (package == null ? "unknown" : package.version + " (" + package.source + ")"));
            text.AppendLine("focusedWindow=[" + (EditorWindow.focusedWindow == null ? "" : EditorWindow.focusedWindow.titleContent.text) + "]" +
                " imeMode=" + Input.imeCompositionMode + " compositionString=[" + Escape(Input.compositionString) + "]");

            UnityTerminalWindow[] windows = Resources.FindObjectsOfTypeAll<UnityTerminalWindow>();
            lock (recentErrorsGate)
            {
                text.AppendLine("--- recent Unity errors and exceptions since the last script reload (" + recentErrors.Count + ") ---");
                foreach (string entry in recentErrors) text.AppendLine(entry);
            }
            text.AppendLine("terminal windows=" + windows.Length);
            var hostFiles = new string[windows.Length];
            var sections = new StringBuilder[windows.Length];
            for (int i = 0; i < windows.Length; i++)
            {
                sections[i] = new StringBuilder();
                int hostPid;
                try { hostPid = windows[i].AppendDiagnostics(sections[i]); }
                catch (Exception ex) { sections[i].AppendLine("window diagnostics failed: " + ex); hostPid = 0; }
                if (hostPid != 0) hostFiles[i] = RequestHostDiagnostics(hostPid, Path.Combine(Folder, "diagnostics-" + stamp + "-host" + i + ".txt"));
            }
            WaitForHosts(hostFiles);
            for (int i = 0; i < windows.Length; i++)
            {
                text.AppendLine();
                text.AppendLine("========== terminal window " + i + " ==========");
                text.Append(sections[i]);
                text.AppendLine();
                text.AppendLine("========== terminal window " + i + ": host ==========");
                text.AppendLine(ReadHostDiagnostics(hostFiles[i]));
            }
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            PruneOldFiles();
            return path;
        }

        private static string RequestHostDiagnostics(int hostPid, string target)
        {
            try
            {
                string folder = Path.Combine(Path.GetTempPath(), "UnityTerminal");
                Directory.CreateDirectory(folder);
                if (File.Exists(target)) File.Delete(target);
                File.WriteAllText(Path.Combine(folder, "diagnostics-" + hostPid + ".request"), target);
                return target;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // The host checks for its request file every 500 ms.
        private static void WaitForHosts(string[] hostFiles)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < HostWaitMilliseconds)
            {
                bool pending = false;
                foreach (string file in hostFiles) if (file != null && File.Exists(file) == false) pending = true;
                if (pending == false) return;
                Thread.Sleep(50);
            }
        }

        private static string ReadHostDiagnostics(string file)
        {
            if (file == null) return "(no host: the primary session is not running)";
            if (File.Exists(file) == false)
                return "(no answer within " + HostWaitMilliseconds + " ms: the host may predate this feature; Restart the terminal to start a host that supports it)";
            // The host publishes the final name only after closing the file; retry briefly anyway in
            // case another process (an antivirus scanner, for example) holds it open.
            string content = null, error = null;
            for (int attempt = 0; attempt < 10 && content == null; attempt++)
            {
                try { content = File.ReadAllText(file); }
                catch (IOException ex) { error = ex.Message; Thread.Sleep(50); }
                catch (UnauthorizedAccessException ex) { error = ex.Message; Thread.Sleep(50); }
            }
            if (content == null) return "(the host answered, but " + file + " could not be read: " + error + ")";
            try { File.Delete(file); }
            catch (IOException) { content += "\n(note: could not delete " + file + ")"; }
            catch (UnauthorizedAccessException) { content += "\n(note: could not delete " + file + ")"; }
            return content;
        }

        private static void PruneOldFiles()
        {
            try
            {
                string[] files = Directory.GetFiles(Folder, "diagnostics-*.txt");
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length - KeepFiles; i++) File.Delete(files[i]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Makes control characters visible: ESC as \e, other C0 controls as \xNN.
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var escaped = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                if (c == '\x1b') escaped.Append("\\e");
                else if (c == '\n') escaped.Append("\\n");
                else if (c == '\r') escaped.Append("\\r");
                else if (c < ' ' || c == '\x7f') escaped.Append("\\x").Append(((int)c).ToString("x2"));
                else escaped.Append(c);
            }
            return escaped.ToString();
        }
    }
}
