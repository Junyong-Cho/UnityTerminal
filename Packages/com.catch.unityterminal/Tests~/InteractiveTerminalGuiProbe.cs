using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Optional: copy to Assets/Editor, create Temp/RunInteractiveGuiProbe, Assets > Refresh.
    // Starts Codex read-only, types into its prompt WITHOUT submitting it, then closes it.
    public sealed class InteractiveTerminalGuiProbe : UnityTerminalWindow
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type WindowType = typeof(UnityTerminalWindow);
        private static string requestedShell = "PowerShell";
        private readonly StringBuilder trace = new StringBuilder();
        private EditorWindow previous;
        private double next;
        private double deadline;
        private int stage;
        private bool painted;
        private bool inputFocused;
        private bool finished;
        private string originalClipboard;
        private string primaryText;
        private string primaryInput;
        private Vector2 primaryScroll;
        private int primaryCursor;
        private int primarySelection;
        private object primaryShell;
        private string stablePrimaryText;
        private double primaryStableSince;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists("Temp/RunInteractiveGuiProbe")) return;
            File.Delete("Temp/RunInteractiveGuiProbe");
            EditorApplication.update += StartProbe;
        }
        private static void StartProbe()
        {
            EditorApplication.update -= StartProbe;
            try
            {
                EditorWindow previous = focusedWindow;
                var probe = CreateInstance<InteractiveTerminalGuiProbe>();
                probe.previous = previous;
                probe.position = new Rect(90, 90, 900, 520);
                probe.titleContent = new GUIContent("Codex terminal check");
                probe.ShowUtility(); probe.Focus();
            }
            catch (Exception ex) { File.WriteAllText("Temp/InteractiveGuiProbe.result.txt", "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Set("shellType", requestedShell);
            Set("currentWorkingDirectory", project);
            // Keep a real primary shell alive while the separate app page is displayed.
            object buffer = Get("buffer");
            Type parserType = WindowType.Assembly.GetType("UnityTerminal.AnsiParser");
            Set("outputParser", Activator.CreateInstance(parserType, new[] { buffer, (object)false }));
            Set("errorParser", Activator.CreateInstance(parserType, new[] { buffer, (object)true }));
            Type shellType = WindowType.Assembly.GetType("UnityTerminal.ShellSession");
            primaryShell = shellType.GetMethod("Start").Invoke(null, new object[] { requestedShell, project });
            Set("session", primaryShell);
            Require((bool)shellType.GetMethod("TrySend").Invoke(primaryShell, new object[] {
                "[Console]::WriteLine(('PRIMARY-SHELL-MAJOR:' + $PSVersionTable.PSVersion.Major)); echo PRIMARY-SHELL-READY", null }), "Primary shell setup failed");
            var scrollback = new StringBuilder();
            for (int i = 0; i < 120; i++) scrollback.AppendLine("Primary shell line " + i);
            WindowType.GetMethod("AppendSystemMessage", Private).Invoke(this, new object[] { scrollback.ToString() });
            Set("currentInput", "draft before Codex");
            Set("pendingCaret", 8);
            Set("pendingSelection", 3);
            Set("autoScroll", false);
            Set("scrollPosition", new Vector2(0, 150));
            originalClipboard = GUIUtility.systemCopyBuffer;
            next = EditorApplication.timeSinceStartup + 0.5;
            deadline = next + 30;
            EditorApplication.update += Tick;
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            WindowType.GetMethod("OnDisable", Private).Invoke(this, null);
            if (originalClipboard != null) GUIUtility.systemCopyBuffer = originalClipboard;
        }
        private void OnGUI()
        {
            try
            {
                WindowType.GetMethod("OnGUI", Private).Invoke(this, null);
                inputFocused = GUI.GetNameOfFocusedControl() == "InteractiveTerminal";
                if (Event.current.type == EventType.Repaint)
                {
                    painted = true;
                }
            }
            catch (TargetInvocationException ex) { Finish("FAIL: OnGUI " + ex.InnerException); }
        }
        private object Get(string name) { return WindowType.GetField(name, Private).GetValue(this); }
        private void Set(string name, object value) { WindowType.GetField(name, Private).SetValue(this, value); }
        private string ScreenText()
        {
            object screen = Get("interactiveScreen");
            return screen == null ? "" : (string)screen.GetType().GetMethod("GetText").Invoke(screen, null);
        }
        private string PrimaryText()
        {
            object buffer = Get("buffer");
            return (string)buffer.GetType().GetMethod("GetPlainText").Invoke(buffer, null);
        }
        private void CapturePrimary()
        {
            primaryText = PrimaryText();
            primaryInput = (string)Get("currentInput");
            primaryScroll = (Vector2)Get("scrollPosition");
            primaryCursor = (int)Get("inputCursorIndex");
            primarySelection = (int)Get("inputSelectIndex");
        }
        private void RequirePrimaryRestored()
        {
            if (PrimaryText() != primaryText)
            {
                File.WriteAllText("Temp/PrimaryScreenBefore.txt", primaryText);
                File.WriteAllText("Temp/PrimaryScreenAfter.txt", PrimaryText());
                throw new Exception("Interactive output or exit status changed the primary scrollback; see Temp/PrimaryScreenBefore.txt and Temp/PrimaryScreenAfter.txt");
            }
            Require((string)Get("currentInput") == primaryInput, "The primary input was not restored");
            Require((Vector2)Get("scrollPosition") == primaryScroll, "The primary scroll position changed");
            Require((int)Get("inputCursorIndex") == primaryCursor && (int)Get("inputSelectIndex") == primarySelection,
                "The primary caret/selection was not restored");
            Require(ReferenceEquals(Get("session"), primaryShell), "The primary shell session was replaced");
            Require(!(bool)Get("interactiveReloadLocked"), "Script reload remained locked after closing the session");
        }
        private void Tick()
        {
            if (finished || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("GUI verification timed out. Screen: " + ScreenText());
                switch (stage)
                {
                    case 0:
                        string readyText = PrimaryText();
                        if (!painted || !readyText.Contains("\nPRIMARY-SHELL-READY\n") ||
                            !readyText.Contains("PRIMARY-SHELL-MAJOR:" + (requestedShell == "PowerShell7" ? "7" : "5"))) return;
                        if (readyText != stablePrimaryText)
                        {
                            stablePrimaryText = readyText;
                            primaryStableSince = EditorApplication.timeSinceStartup;
                            return;
                        }
                        if (EditorApplication.timeSinceStartup - primaryStableSince < 0.25) return;
                        trace.AppendLine("PASS: shell selector and primary session use " + requestedShell);
                        CapturePrimary();
                        bool started = (bool)WindowType.GetMethod("SendCommand", Private).Invoke(this, new object[] { "codex --no-daemon --sandbox read-only" });
                        Require(started && Get("interactiveSession") != null, "Codex command did not switch the terminal into interactive mode");
                        Require((bool)Get("interactiveReloadLocked"), "Script reload can terminate the active session");
                        stage++; break;
                    case 1:
                        string text = ScreenText();
                        if (!inputFocused || !text.Contains("OpenAI Codex") || !text.Contains("Ask Codex")) return;
                        trace.AppendLine("PASS: Codex UI rendered and interactive input focused");
                        foreach (char c in "local-input-check") Press(KeyCode.None, c, EventModifiers.None);
                        stage++; break;
                    case 2:
                        if (!ScreenText().Contains("local-input-check")) return;
                        trace.AppendLine("PASS: typed text reached the real Codex prompt without submission");
                        Press(KeyCode.U, '\0', EventModifiers.Control);
                        GUIUtility.systemCopyBuffer = "paste-\uD55C\uAE00-check";
                        Press(KeyCode.V, '\0', EventModifiers.Control);
                        stage++; break;
                    case 3:
                        if (!ScreenText().Contains("paste-\uD55C\uAE00-check")) return;
                        trace.AppendLine("PASS: Unicode clipboard text reached the prompt");
                        Press(KeyCode.LeftArrow, '\0', EventModifiers.None);
                        Press(KeyCode.Backspace, '\0', EventModifiers.None);
                        stage++; break;
                    case 4:
                        if (!ScreenText().Contains("paste-\uD55C\uAE00-chek")) return;
                        trace.AppendLine("PASS: arrow and Backspace edit the Codex prompt");
                        position = new Rect(position.x, position.y, 1020, 600);
                        stage++; break;
                    case 5:
                        object screen = Get("interactiveScreen");
                        int columns = (int)screen.GetType().GetProperty("Columns").GetValue(screen, null);
                        if (columns < 120) return;
                        trace.AppendLine("PASS: resizing the Unity window resized the live terminal to " + columns + " columns");
                        WindowType.GetMethod("StopInteractive", Private).Invoke(this, new object[] { "[Test] Interactive session closed." });
                        stage++; break;
                    case 6:
                        Require(Get("interactiveSession") == null && !inputFocused, "Interactive state survived Stop");
                        RequirePrimaryRestored();
                        trace.AppendLine("PASS: Stop restored primary scrollback, draft, caret, selection, scroll and the original shell");
                        // Also exercise a natural process exit without submitting an agent task.
                        CapturePrimary();
                        Require((bool)WindowType.GetMethod("SendCommand", Private).Invoke(this, new object[] { "codex --version" }), "Natural-exit command failed to start");
                        stage++; break;
                    case 7:
                        if (Get("interactiveSession") != null || inputFocused) return;
                        RequirePrimaryRestored();
                        trace.AppendLine("PASS: natural process exit restored the primary page without appending a transcript");
                        // Launch via the actual command field: the executed command must be consumed.
                        Set("currentInput", "codex --version");
                        Set("pendingCaret", "codex --version".Length);
                        Set("pendingSelection", -1);
                        stage++; break;
                    case 8:
                        Press(KeyCode.Return, '\n', EventModifiers.None);
                        stage++; break;
                    default:
                        if (Get("interactiveSession") != null || inputFocused) return;
                        Require((string)Get("currentInput") == "" && (int)Get("inputCursorIndex") == 0 && (int)Get("inputSelectIndex") == 0,
                            "The executed Codex command was restored instead of its empty prompt");
                        Require(PrimaryText() == primaryText, "Executing Codex from the command field changed primary output");
                        Require(ReferenceEquals(Get("session"), primaryShell), "The original shell did not survive repeated app pages");
                        Finish("PASS: Codex app page, input, resize, Stop/natural-exit restoration and the consumed launch command");
                        return;
                }
                next = EditorApplication.timeSinceStartup + 0.25;
                Repaint();
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void Press(KeyCode key, char character, EventModifiers modifiers)
        {
            SendEvent(new Event { type = EventType.KeyDown, keyCode = key, character = character, modifiers = modifiers });
            SendEvent(new Event { type = EventType.KeyUp, keyCode = key, character = character, modifiers = modifiers });
        }
        private static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            trace.AppendLine(result);
            File.WriteAllText("Temp/InteractiveGuiProbe.result.txt", trace.ToString());
            EditorApplication.update -= Tick;
            Close();
            if (previous != null) previous.Focus();
        }
    }
}
