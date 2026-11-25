using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Copy to Assets/Editor and create Temp/RunTerminalMultilineProbe, then Refresh.
    // Uses real IMGUI controls and an in-memory app; no commands are executed.
    public sealed class TerminalMultilineRegressionProbe : UnityTerminalWindow
    {
        private static readonly Type Base = typeof(UnityTerminalWindow);
        private const string ShiftEnter = "\x1b[13;28;13;1;16;1_\x1b[13;28;13;0;16;1_";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly StringBuilder report = new StringBuilder(), sent = new StringBuilder();
        private object view, screen;
        private bool interactive, finished, painted, inputFocused;
        private int stage, cursor, selection;
        private float singleHeight, inputHeight;
        private double next, deadline;
        private EditorWindow previous;

        [InitializeOnLoadMethod]
        private static void Requested()
        {
            const string marker = "Temp/RunTerminalMultilineProbe";
            if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
            File.Delete(marker); EditorApplication.update += Open;
        }
        private static void Open()
        {
            EditorApplication.update -= Open;
            try
            {
                EditorWindow old = focusedWindow;
                var probe = CreateInstance<TerminalMultilineRegressionProbe>();
                probe.previous = old; probe.titleContent = new GUIContent("Terminal multiline check");
                probe.position = new Rect(100, 100, 760, 400); probe.ShowUtility(); probe.Focus();
            }
            catch (Exception ex) { File.WriteAllText("Temp/TerminalMultilineProbe.result.txt", "FAIL: startup " + ex); }
        }
        private void OnEnable()
        {
            Set("currentWorkingDirectory", Path.GetFullPath("."));
            SetInput("alpha\uD55C\uAE00omega", 7);
            next = EditorApplication.timeSinceStartup + 0.5; deadline = next + 45;
            EditorApplication.update += Tick;
        }
        private void OnDisable() { EditorApplication.update -= Tick; Call("OnDisable"); }
        private void OnGUI()
        {
            if (finished) return;
            try
            {
                if (!interactive)
                {
                    Call("OnGUI");
                    inputFocused = GUI.GetNameOfFocusedControl() == "TerminalInput";
                    if (GUI.GetNameOfFocusedControl() == "TerminalInput")
                    {
                        var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                        cursor = editor.cursorIndex; selection = editor.selectIndex; inputHeight = editor.position.height;
                    }
                }
                else
                {
                    var font = ((GUIStyle)Get("terminalStyle")).font;
                    view.GetType().GetMethod("Draw").Invoke(view, new object[] { new Rect(0, 0, position.width, position.height), font, 12, focusedWindow == this });
                    view.GetType().GetMethod("HandleInput").Invoke(view, null);
                    Call("UpdateInputMethod");
                    inputFocused = GUI.GetNameOfFocusedControl() == "InteractiveTerminal";
                }
                if (Event.current.type == EventType.Repaint) painted = true;
            }
            catch (Exception ex) { Finish("FAIL: GUI " + ex); }
        }
        private void Tick()
        {
            if (finished || EditorApplication.timeSinceStartup < next) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Timed out at stage " + stage);
                if (!painted || !inputFocused) { Repaint(); return; }
                switch (stage++)
                {
                    case 0:
                        singleHeight = inputHeight; Press(KeyCode.Return, '\r', true);
                        Require(Input == "alpha\uD55C\uAE00\nomega", "Shift+Enter submitted, duplicated or misplaced the newline: " + Escape(Input));
                        Require(cursor == 8 && selection == 8, "Caret did not follow the inserted newline"); break;
                    case 1:
                        Require(inputHeight >= singleHeight * 1.9f, "Multiline input did not grow");
                        Press(KeyCode.Z, 'z', false);
                        Require(Input == "alpha\uD55C\uAE00\nzomega", "Typing after a newline replaced text");
                        SetInput("ab\uD55C\uAE00cd", 2, 4); break;
                    case 2:
                        Press(KeyCode.KeypadEnter, '\r', true);
                        Require(Input == "ab\ncd" && cursor == 3 && selection == 3, "Shift+KeypadEnter did not replace the selection once");
                        report.AppendLine("PASS: Shift+Enter/KeypadEnter insert once at the caret or over a Korean selection; input grows");
                        Call("AddCommandHistory", "history-command"); SetInput("top\nbottom", 7); break;
                    case 3:
                        Press(KeyCode.UpArrow, '\0', false);
                        Require(Input == "top\nbottom" && cursor <= 3, "Up did not move within multiline input: text=" + Escape(Input) + ", cursor=" + cursor);
                        Press(KeyCode.DownArrow, '\0', false);
                        Require(Input == "top\nbottom" && cursor > 3, "Down did not move to the next input line");
                        SetInput("", 0); break;
                    case 4:
                        Press(KeyCode.UpArrow, '\0', false);
                        Require(Input == "history-command", "Single-line history navigation stopped working: text=" + Escape(Input) + ", index=" + Get("historyIndex"));
                        SetInput("one\ntwo", 7); break;
                    case 5:
                        Press(KeyCode.Return, '\r', false);
                        Require(Input == "one\ntwo", "Enter appended a newline instead of submitting the original text");
                        string buffer = (string)Get("buffer").GetType().GetMethod("GetPlainText").Invoke(Get("buffer"), null);
                        Require(buffer.Contains("The shell session is not running"), "Enter did not reach normal execution");
                        report.AppendLine("PASS: multiline Up/Down edit the draft; single-line history and normal Enter execution remain intact");
                        SetInput("echo first\ncd Pac", "echo first\ncd Pac".Length); break;
                    case 6:
                        SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.None, character = '\n' });
                        Require(Input == "echo first\ncd Pac", "An IME return text tail added a newline or removed an existing input line");
                        Press(KeyCode.Tab, '\t', false);
                        Require(Input == "echo first\ncd Packages/" && cursor == Input.Length && selection == cursor, "Multiline Tab completion corrupted the input or selection: " + Escape(Input));
                        SetInput("echo first\ncd Pac tail", "echo first\ncd Pac".Length); break;
                    case 7:
                        Press(KeyCode.Tab, '\t', false);
                        Require(Input == "echo first\ncd Packages/ tail" && cursor == "echo first\ncd Packages/".Length && selection == cursor,
                            "Tab in the middle of a multiline draft lost its suffix or caret: " + Escape(Input));
                        SetInput(new string('\n', 40), 40); break;
                    case 8:
                        Require(inputHeight <= position.height * 0.4f + 1, "Large drafts displaced the whole terminal output");
                        report.AppendLine("PASS: multiline completion preserves selection and large drafts have bounded height");
                        screen = Activator.CreateInstance(Base.Assembly.GetType("UnityTerminal.TerminalScreen"), new object[] { 100, 20 });
                        screen.GetType().GetMethod("Feed").Invoke(screen, new object[] { "\x1b[?2004h" });
                        view = Activator.CreateInstance(Base.Assembly.GetType("UnityTerminal.InteractiveTerminalView"), new[] { screen });
                        view.GetType().GetField("Send").SetValue(view, (Action<string>)(value => sent.Append(value)));
                        interactive = true; break;
                    case 9:
                        Press(KeyCode.Return, '\r', true);
                        Require(sent.ToString() == ShiftEnter, "Interactive Shift+Enter became submit or duplicate text: " + Escape(sent.ToString()));
                        sent.Length = 0; Press(KeyCode.KeypadEnter, '\r', true);
                        Require(sent.ToString() == ShiftEnter, "Interactive keypad newline was not inserted once");
                        sent.Length = 0; Press(KeyCode.Return, '\r', false);
                        Require(sent.ToString() == "\r", "Normal interactive Enter no longer submits");
                        screen.GetType().GetMethod("Feed").Invoke(screen, new object[] { "\x1b[?2004l" }); break;
                    default:
                        sent.Length = 0; Press(KeyCode.Return, '\r', true);
                        Require(sent.ToString() == ShiftEnter, "Apps without bracketed paste lost their Shift+Enter key");
                        report.AppendLine("PASS: interactive Shift+Enter retains its modifier and normal Enter remains a separate key");
                        Finish("PASS: primary and interactive multiline input"); return;
                }
                painted = false; next = EditorApplication.timeSinceStartup + 0.25; Repaint();
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }
        private void SetInput(string text, int caret, int select = -1)
        { Set("currentInput", text); Set("pendingCaret", caret); Set("pendingSelection", select); Set("focusInputPending", true); painted = false; }
        private string Input { get { return (string)Get("currentInput"); } }
        private object Get(string name) { return Base.GetField(name, Flags).GetValue(this); }
        private void Set(string name, object value) { Base.GetField(name, Flags).SetValue(this, value); }
        private object Call(string name, params object[] args) { return Base.GetMethod(name, Flags).Invoke(this, args); }
        private void Press(KeyCode key, char character, bool shift)
        {
            Event down = key == KeyCode.UpArrow ? Event.KeyboardEvent("up") : key == KeyCode.DownArrow ? Event.KeyboardEvent("down") :
                new Event { type = EventType.KeyDown, keyCode = key, character = character, shift = shift };
            SendEvent(down);
            SendEvent(new Event { type = EventType.KeyUp, keyCode = key, character = character, shift = shift });
        }
        private static string Escape(string value) { return value.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\x1b", "<ESC>"); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private void Finish(string value)
        {
            if (finished) return;
            finished = true; report.AppendLine(value); File.WriteAllText("Temp/TerminalMultilineProbe.result.txt", report.ToString());
            EditorApplication.update -= Tick; EditorApplication.delayCall += () => { Close(); if (previous != null) previous.Focus(); };
        }
    }
}
