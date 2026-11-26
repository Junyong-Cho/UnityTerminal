using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor, create Temp/RunTerminalCopyProbe and refresh.
    // Uses only its own GUI and an in-memory screen; no live CLI receives input.
    public sealed class TerminalCopyRegressionProbe : EditorWindow
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private object screen, view;
        private Font font;
        private EditorWindow previous;
        private string clipboard;
        private readonly StringBuilder sent = new StringBuilder(), report = new StringBuilder();
        private int stage;
        private bool painted, finished;
        private double next, deadline;
        private Rect area;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunTerminalCopyProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            File.Delete(marker);
            File.WriteAllText("Temp/TerminalCopyProbe.trace.txt", "Requested\n");
            EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
                File.AppendAllText("Temp/TerminalCopyProbe.trace.txt", "Opening\n");
                var old = focusedWindow;
                var window = CreateInstance<TerminalCopyRegressionProbe>();
                window.previous = old;
                window.titleContent = new GUIContent("Terminal copy check");
                window.position = new Rect(100, 100, 800, 360);
                window.ShowUtility(); window.Focus();
            }
            catch (Exception ex) { File.WriteAllText("Temp/TerminalCopyProbe.result.txt", "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            File.AppendAllText("Temp/TerminalCopyProbe.trace.txt", "Enabled\n");
            var assembly = typeof(UnityTerminalWindow).Assembly;
            screen = Activator.CreateInstance(assembly.GetType("UnityTerminal.TerminalScreen"), new object[] { 80, 20 });
            view = Activator.CreateInstance(assembly.GetType("UnityTerminal.InteractiveTerminalView"), new[] { screen });
            view.GetType().GetField("Send").SetValue(view, (Action<string>)(text => sent.Append(text)));
            font = Font.CreateDynamicFontFromOSFont("Consolas", 14);
            clipboard = GUIUtility.systemCopyBuffer;
            next = EditorApplication.timeSinceStartup + 0.4; deadline = next + 25;
            EditorApplication.update += Tick;
            File.AppendAllText("Temp/TerminalCopyProbe.trace.txt", "Ready\n");
        }
        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            GUIUtility.systemCopyBuffer = clipboard;
            if (font != null) DestroyImmediate(font);
        }
        private void OnGUI()
        {
            if (finished) return;
            try
            {
                area = new Rect(0, 0, position.width, position.height);
                Call(view, "Draw", area, font, 14, focusedWindow == this);
                Call(view, "HandleInput");
                if (Event.current.type == EventType.Repaint) painted = true;
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
        }
        private void Tick()
        {
            if (finished || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Timeout at " + stage);
                Repaint();
                if (!painted || (int)Get("control") == 0) return;
                File.AppendAllText("Temp/TerminalCopyProbe.trace.txt", "Stage " + stage + "\n");
                switch (stage++)
                {
                    case 0:
                        Feed("\x1b[2J\x1b[Hbefore-\uD55C\uAE00-copy\r\nsecond row");
                        break;
                    case 1:
                        Mouse(EventType.MouseDown, 0, 0);
                        Mouse(EventType.MouseDrag, 5, 1);
                        Mouse(EventType.MouseUp, 5, 1);
                        Require((int)Get("selectionStart") == 0 && (int)Get("selectionEnd") > 0, "Drag did not select cells");
                        Feed("\x1b[Hchanged output\r\nchanged row");
                        break;
                    case 2:
                        Require((int)Get("selectionStart") == 0, "App redraw cleared selection");
                        SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.C, modifiers = EventModifiers.Control });
                        Require(GUIUtility.systemCopyBuffer == "before-\uD55C\uAE00-copy\nsecond", "Copy did not preserve selected Unicode output: " + GUIUtility.systemCopyBuffer);
                        Require(sent.Length == 0, "Copy interrupted the app");
                        report.AppendLine("PASS: multiline Unicode drag survives app redraw; Ctrl+C copies the selected snapshot without sending a key");
                        SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.C, modifiers = EventModifiers.Control | EventModifiers.Shift });
                        Require(GUIUtility.systemCopyBuffer == "before-\uD55C\uAE00-copy\nsecond", "Ctrl+Shift+C differs");
                        Call(view, "Copy");
                        Require(GUIUtility.systemCopyBuffer == "before-\uD55C\uAE00-copy\nsecond", "Context copy differs");
                        report.AppendLine("PASS: Ctrl+Shift+C and context Copy use the same selection");
                        // Start inside the real IME receiver, where TextField consumes the click.
                        int x = (int)screen.GetType().GetProperty("CursorX").GetValue(screen, null);
                        int y = (int)screen.GetType().GetProperty("CursorY").GetValue(screen, null);
                        Mouse(EventType.MouseDown, x, y);
                        Mouse(EventType.MouseDrag, x + 3, y);
                        Mouse(EventType.MouseUp, x + 3, y);
                        Require((int)Get("selectionStart") != (int)Get("selectionEnd"), "IME receiver swallowed drag");
                        report.AppendLine("PASS: dragging over the IME receiver also selects terminal cells");
                        Call(view, "SendText", "");
                        SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.C, modifiers = EventModifiers.Control });
                        Require(sent.ToString() == "\x03", "Ctrl+C without selection no longer interrupts");
                        Finish("PASS: interactive drag and copy regression checks");
                        return;
                }
                next = EditorApplication.timeSinceStartup + 0.2;
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void Mouse(EventType type, int x, int y)
        {
            float width = (float)Get("cellWidth"), height = (float)Get("cellHeight");
            SendEvent(new Event { type = type, button = 0, mousePosition = new Vector2(area.x + 4 + (x + 0.5f) * width, area.y + 2 + (y + 0.5f) * height) });
        }
        private object Get(string name) { return view.GetType().GetField(name, Hidden).GetValue(view); }
        private static object Call(object target, string name, params object[] args)
        { return target.GetType().GetMethod(name, Hidden | BindingFlags.Public).Invoke(target, args); }
        private void Feed(string text) { Call(screen, "Feed", text); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private void Finish(string result)
        {
            if (finished) return;
            finished = true; report.AppendLine(result);
            File.WriteAllText("Temp/TerminalCopyProbe.result.txt", report.ToString());
            Close(); if (previous != null) previous.Focus();
        }
    }
}
