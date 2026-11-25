using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunPersistentSessionProbe, then Refresh.
    // Compiles a temporary C# asset twice. Codex input is never submitted.
    public sealed class PersistentSessionReloadProbe : UnityTerminalWindow
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type Base = typeof(UnityTerminalWindow);
        private const string Marker = "Assets/Editor/TerminalCompileMarker.cs";
        private const string Result = "Temp/PersistentSessionProbe.result.txt";
        [SerializeField] private int phase, hostPid, primaryPid, appPid, instance;
        [SerializeField] private string report = "";
        [NonSerialized] private bool awaitingReload, finished, painted;
        [NonSerialized] private double deadline;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists("Temp/RunPersistentSessionProbe")) return;
            File.Delete("Temp/RunPersistentSessionProbe");
            EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
                var probe = CreateInstance<PersistentSessionReloadProbe>();
                probe.position = new Rect(90, 90, 960, 560);
                probe.titleContent = new GUIContent("Session compile check");
                probe.ShowUtility(); probe.Focus();
            }
            catch (Exception ex) { File.WriteAllText(Result, "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            Call("OnEnable");
            awaitingReload = false; painted = false;
            deadline = EditorApplication.timeSinceStartup + 150;
            EditorApplication.update += Tick;
            File.WriteAllText(Result, report + "Enabled phase " + phase + "\n");
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            Call("OnDisable");
        }
        private void OnGUI()
        {
            if (finished) return;
            try { Call("OnGUI"); if (Event.current.type == EventType.Repaint) painted = true; }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
        }
        private void Tick()
        {
            if (finished || awaitingReload) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Timed out at phase " + phase + ": " + Screen());
                Repaint();
                object shell = Get("session");
                if (!painted || shell == null || !(bool)Property(shell, "IsReady")) return;
                if (Property(shell, "Failure") != null) throw new Exception((string)Property(shell, "Failure"));
                switch (phase)
                {
                    case 0:
                        instance = GetInstanceID(); hostPid = Pid(shell, "HostProcessId"); primaryPid = Pid(shell, "ProcessId");
                        SendPrimary("$global:ReloadKeep='saved'; [Console]::WriteLine(('BEFORE-'+'COMPILE'))");
                        phase++; break;
                    case 1:
                        if (!Primary().Contains("\nBEFORE-COMPILE\n")) return;
                        Set("currentInput", "primary-draft-\uD55C\uAE00");
                        Set("inputCursorIndex", 5); Set("inputSelectIndex", 5);
                        Require((bool)Call("SendCommand", "codex --no-daemon --sandbox read-only"), "Codex launch rejected");
                        phase++; break;
                    case 2:
                        if (!Screen().Contains("OpenAI Codex") || !Screen().Contains("Ask Codex")) return;
                        appPid = Pid(Get("interactiveSession"), "ProcessId");
                        Call("SendInteractive", "compile-draft-\uD55C\uAE00"); phase++; break;
                    case 3:
                        if (!Screen().Contains("compile-draft-\uD55C\uAE00")) return;
                        Compile(4); break;
                    case 4:
                        if (!Screen().Contains("compile-draft-\uD55C\uAE00")) return;
                        SameProcesses(shell);
                        Record("PASS: actual C# compilation preserves the Codex process and its unsubmitted Korean draft");
                        Call("SendInteractive", "\x7fX"); phase++; break;
                    case 5:
                        if (!Screen().Contains("compile-draft-\uD55CX")) return;
                        Record("PASS: the existing Codex prompt remains editable after reconnect");
                        Call("StopInteractive", "[Test] Codex compile check complete.");
                        Require((string)Get("currentInput") == "primary-draft-\uD55C\uAE00", "Primary draft lost");
                        Require((int)Get("inputCursorIndex") == 5, "Primary caret lost");
                        SendPrimary("[Console]::WriteLine(('AFTER-' + $global:ReloadKeep))"); phase++; break;
                    case 6:
                        if (!Primary().Contains("\nAFTER-saved\n")) return;
                        Require(Pid(shell, "ProcessId") == primaryPid, "Primary shell restarted");
                        Record("PASS: original PowerShell PID, variables, output and draft survive the compile");
                        Require((bool)Call("SendCommand", "wsl -d Ubuntu"), "WSL launch rejected"); phase++; break;
                    case 7:
                        if (!Screen().Contains("$")) return;
                        appPid = Pid(Get("interactiveSession"), "ProcessId");
                        Call("SendInteractive", "reload_keep=linux; printf '\\n%s%s\\n' WSL_ BEFORE\r"); phase++; break;
                    case 8:
                        if (!Screen().Contains("WSL_BEFORE")) return;
                        Call("SendInteractive", "echo linux-draft-\uD55C\uAE00"); phase++; break;
                    case 9:
                        if (!Screen().Contains("echo linux-draft-\uD55C\uAE00")) return;
                        Compile(10); break;
                    case 10:
                        if (!Screen().Contains("echo linux-draft-\uD55C\uAE00")) return;
                        SameProcesses(shell);
                        Record("PASS: a second actual compilation preserves the live WSL process and Korean input line");
                        Call("SendInteractive", "\x15printf '\\n%s-%s\\n' WSL \"$reload_keep\"\r"); phase++; break;
                    case 11:
                        if (!Screen().Contains("WSL-linux")) return;
                        Record("PASS: Linux shell variables and input remain usable after compilation");
                        Call("SendInteractive", "exit 0\r"); phase++; break;
                    default:
                        if (Get("interactiveSession") != null) return;
                        Require(Pid(shell, "ProcessId") == primaryPid, "WSL exit replaced primary shell");
                        Require((string)Get("currentInput") == "primary-draft-\uD55C\uAE00", "WSL return lost draft");
                        Finish("PASS: two real Unity compilations preserve Codex, WSL and the original PowerShell session"); break;
                }
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void Compile(int next)
        {
            phase = next; awaitingReload = true;
            File.WriteAllText(Marker, "internal static class TerminalCompileMarker { internal const int Value = " + next + "; }\n");
            AssetDatabase.ImportAsset(Marker, ImportAssetOptions.ForceUpdate);
        }
        private void SameProcesses(object shell)
        {
            Require(GetInstanceID() == instance, "Native window identity changed");
            Require(Pid(shell, "HostProcessId") == hostPid && Pid(shell, "ProcessId") == primaryPid &&
                Pid(Get("interactiveSession"), "ProcessId") == appPid, "Compilation restarted a session process");
        }
        private void SendPrimary(string text)
        {
            object[] arguments = { text, null };
            Require((bool)Get("session").GetType().GetMethod("TrySend").Invoke(Get("session"), arguments), "Primary send failed");
        }
        private string Primary() { object buffer = Get("buffer"); return (string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null); }
        private string Screen() { object screen = Get("interactiveScreen"); return screen == null ? "" : (string)screen.GetType().GetMethod("GetTranscript").Invoke(screen, null); }
        private static object Property(object target, string name) { return target.GetType().GetProperty(name, Flags).GetValue(target, null); }
        private static int Pid(object target, string name) { return (int)Property(target, name); }
        private object Get(string name) { return Base.GetField(name, Flags).GetValue(this); }
        private void Set(string name, object value) { Base.GetField(name, Flags).SetValue(this, value); }
        private object Call(string name, params object[] args) { return Base.GetMethod(name, Flags).Invoke(this, args); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private void Record(string line) { report += line + "\n"; File.WriteAllText(Result, report); }
        private void Finish(string line)
        {
            if (finished) return;
            finished = true; Record(line); EditorApplication.update -= Tick;
            EditorApplication.delayCall += Close;
        }
    }
}
