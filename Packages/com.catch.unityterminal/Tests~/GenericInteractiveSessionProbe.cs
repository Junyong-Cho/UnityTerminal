using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunGenericInteractiveProbe, then Refresh.
    // Uses its own PowerShell sessions, synthetic GUI events and one script reload.
    public sealed class GenericInteractiveSessionProbe : UnityTerminalWindow
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type Base = typeof(UnityTerminalWindow);
        private static readonly FieldInfo DrawCallback = typeof(GUIStyle).GetField("onDraw", BindingFlags.Static | BindingFlags.NonPublic);
        private const string Result = "Temp/GenericInteractiveProbe.result.txt";
        [SerializeField] private int stage, hostPid, shellPid, appPid;
        [SerializeField] private string report = "";
        [NonSerialized] private bool finished, awaitingReload, painted, inputFocused;
        [NonSerialized] private double deadline, next;
        [NonSerialized] private Rect button;
        [NonSerialized] private Delegate drawObserver, previousDraw;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunGenericInteractiveProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            File.Delete(marker); EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
                var window = CreateInstance<GenericInteractiveSessionProbe>();
                window.titleContent = new GUIContent("Generic interactive check");
                window.position = new Rect(100, 100, 960, 540);
                window.ShowUtility(); window.Focus();
            }
            catch (Exception ex) { File.WriteAllText(Result, "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            Call("OnEnable"); awaitingReload = false;
            next = EditorApplication.timeSinceStartup + 0.3;
            deadline = next + 90;
            EditorApplication.update += Tick;
            File.WriteAllText(Result, report + "Enabled stage " + stage + "\n");
        }
        private void OnDisable() { EditorApplication.update -= Tick; Call("OnDisable"); }
        private void OnGUI()
        {
            if (finished) return;
            try
            {
                if (drawObserver == null && DrawCallback != null)
                {
                    var signature = DrawCallback.FieldType.GetMethod("Invoke").GetParameters();
                    var args = new ParameterExpression[signature.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = Expression.Parameter(signature[i].ParameterType);
                    drawObserver = Expression.Lambda(DrawCallback.FieldType,
                        Expression.Call(Expression.Constant(this), GetType().GetMethod("ObserveDraw", Flags),
                            args[0], args[1], args[2], Expression.Convert(args[3], typeof(object))), args).Compile();
                }
                if (DrawCallback != null) { previousDraw = (Delegate)DrawCallback.GetValue(null); DrawCallback.SetValue(null, drawObserver); }
                Call("OnGUI");
                inputFocused = GUI.GetNameOfFocusedControl() == "TerminalInput";
                if (Event.current.type == EventType.Repaint) painted = true;
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
            finally { if (DrawCallback != null) DrawCallback.SetValue(null, previousDraw); }
        }
        private bool ObserveDraw(GUIStyle style, Rect rect, GUIContent content, object state)
        {
            if (content.text == "Interactive") button = rect;
            return previousDraw != null && (bool)previousDraw.DynamicInvoke(style, rect, content, state);
        }
        private static string GenericCommand()
        {
            string code = "$global:GenericKeep='kept-\uD55C\uAE00'; " +
                "[Console]::WriteLine(('GENERIC-'+'READY')); " +
                "[Console]::WriteLine(('TTY-'+[Console]::IsInputRedirected)); " +
                "[Console]::WriteLine(('QUOTED-'+'spaces; and ''quotes'''));";
            return "# Multiline launch\npowershell.exe -NoLogo -NoProfile -NoExit -Command '" + code.Replace("'", "''") + "'";
        }
        private void Tick()
        {
            if (finished || awaitingReload || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Timed out at stage " + stage + ": " + Screen());
                Repaint();
                object shell = Get("session");
                if (!painted || shell == null || !(bool)Property(shell, "IsReady")) return;
                if (Property(shell, "Failure") != null) throw new Exception((string)Property(shell, "Failure"));
                switch (stage)
                {
                    case 0:
                        hostPid = Pid(shell, "HostProcessId"); shellPid = Pid(shell, "ProcessId");
                        Require((bool)Call("SendCommand", "$global:GenericParent='saved'; [Console]::WriteLine(('PRIMARY-'+'READY'))"), "Ordinary command rejected");
                        Require(Get("interactiveSession") == null, "Ordinary PowerShell command opened an app page");
                        stage++; break;
                    case 1:
                        if (!Primary().Contains("\nPRIMARY-READY\n")) return;
                        SetInput(GenericCommand()); stage++; break;
                    case 2:
                        if (!inputFocused) return;
                        SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return, character = '\r', control = true });
                        SendEvent(new Event { type = EventType.KeyUp, keyCode = KeyCode.Return, character = '\r', control = true });
                        Require(Get("interactiveSession") != null, "Ctrl+Enter did not launch an unregistered CLI");
                        Require((string)Get("currentInput") == "", "Ctrl+Enter left the submitted command in the input field");
                        stage++; break;
                    case 3:
                        if (!Screen().Contains("GENERIC-READY") || !Screen().Contains("TTY-False") ||
                            !Screen().Contains("QUOTED-spaces; and 'quotes'")) return;
                        appPid = Pid(Get("interactiveSession"), "ProcessId");
                        Record("PASS: Ctrl+Enter starts an arbitrary CLI with real console input, Unicode and quoted arguments");
                        Call("SendInteractive", "[Console]::WriteLine(('AFTER-' + $global:GenericKeep))");
                        stage++; break;
                    case 4:
                        if (!Screen().Contains("$global:GenericKeep")) return;
                        stage++; awaitingReload = true;
                        EditorUtility.RequestScriptReload(); break;
                    case 5:
                        if (!Screen().Contains("$global:GenericKeep")) return;
                        Require(Pid(shell, "HostProcessId") == hostPid && Pid(shell, "ProcessId") == shellPid &&
                            Pid(Get("interactiveSession"), "ProcessId") == appPid, "Reload restarted a session process");
                        Call("SendInteractive", "\r"); stage++; break;
                    case 6:
                        if (!Screen().Contains("AFTER-kept-\uD55C\uAE00")) return;
                        Record("PASS: script reload preserves the arbitrary CLI process, input draft and Korean shell variable");
                        Call("StopInteractive", (object)null); stage++; break;
                    case 7:
                        Require(Get("interactiveSession") == null && Pid(shell, "ProcessId") == shellPid &&
                            (string)Get("currentInput") == "", "Stop did not restore the original empty PowerShell prompt");
                        Require((bool)Call("SendCommand", "[Console]::WriteLine(('PARENT-' + $global:GenericParent))"), "Primary shell did not accept input after Stop");
                        stage++; break;
                    case 8:
                        if (!Primary().Contains("\nPARENT-saved\n")) return;
                        Record("PASS: Stop restores the original PowerShell process and its variables");
                        SetInput(GenericCommand()); button = Rect.zero; stage++; break;
                    case 9:
                        if (!inputFocused || button.width < 50) return;
                        Require(button.xMin >= 0 && button.xMax <= position.width + 1, "A long command pushed the Interactive button outside the window: " + button);
                        SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = button.center });
                        SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = button.center });
                        Require(Get("interactiveSession") != null && (string)Get("currentInput") == "", "Interactive button did not submit the command");
                        stage++; break;
                    case 10:
                        if (!Screen().Contains("GENERIC-READY")) return;
                        Record("PASS: the Interactive button launches the same generic command without a persistent execution mode");
                        Call("SendInteractive", "exit 0\r"); stage++; break;
                    default:
                        if (Get("interactiveSession") != null) return;
                        Require(Pid(shell, "ProcessId") == shellPid && (string)Get("currentInput") == "", "Natural exit replaced the shell or restored the launch command");
                        Require((bool)Call("SendCommand", "[Console]::WriteLine(('NORMAL-'+'AGAIN'))"), "Ordinary execution no longer works");
                        Require(Get("interactiveSession") == null, "Manual launch changed subsequent commands into interactive execution");
                        Finish("PASS: generic interactive dispatch, button, Ctrl+Enter, quoting, reload and return to normal execution"); break;
                }
                next = EditorApplication.timeSinceStartup + 0.2;
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void SetInput(string value)
        { Set("currentInput", value); Set("pendingCaret", value.Length); Set("pendingSelection", -1); Set("focusInputPending", true); painted = false; }
        private string Primary() { object buffer = Get("buffer"); return (string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null); }
        private string Screen() { object screen = Get("interactiveScreen"); return screen == null ? "" : (string)screen.GetType().GetMethod("GetTranscript").Invoke(screen, null); }
        private static object Property(object value, string name) { return value.GetType().GetProperty(name, Flags).GetValue(value, null); }
        private static int Pid(object value, string name) { return (int)Property(value, name); }
        private object Get(string name) { return Base.GetField(name, Flags).GetValue(this); }
        private void Set(string name, object value) { Base.GetField(name, Flags).SetValue(this, value); }
        private object Call(string name, params object[] args) { return Base.GetMethod(name, Flags).Invoke(this, args); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private void Record(string value) { report += value + "\n"; File.WriteAllText(Result, report); }
        private void Finish(string value)
        { if (finished) return; finished = true; Record(value); EditorApplication.update -= Tick; EditorApplication.delayCall += Close; }
    }
}
