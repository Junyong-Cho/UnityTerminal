using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
    // Optional GUI regression check. Copy to Assets/Editor and create Temp/RunTerminalInputProbe,
    // then use Assets > Refresh. Results are written to Temp/TerminalInputProbe.result.txt.
    // Exercises real IMGUI events without starting a shell. Remove the copied script afterward.
    public sealed class TerminalInputRegressionProbe : UnityTerminalWindow
    {
        private static readonly Type WindowType = typeof(UnityTerminalWindow);
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly MethodInfo DrawWindow = WindowType.GetMethod("OnGUI", PrivateInstance);
        private readonly StringBuilder trace = new StringBuilder();
        private string fixture;
        private EditorWindow previousWindow;
        private int stage;
        private int paints;
        private int tabEvents;
        private bool invalidGeometry;
        private bool inputFocused;
        private int cursor;
        private int selection;
        private double nextStep;
        private bool finished;

        [InitializeOnLoadMethod]
        private static void RunIfRequested()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            const string marker = "Temp/RunTerminalInputProbe";
            if (!File.Exists(marker)) return;
            File.Delete(marker);
            File.WriteAllText("Temp/TerminalInputProbe.bootstrap.txt", "Initialized in process " + System.Diagnostics.Process.GetCurrentProcess().Id);
            EditorApplication.update += StartProbeWindow;
        }

        private static void StartProbeWindow()
        {
            EditorApplication.update -= StartProbeWindow;
            {
                try
                {
                    File.AppendAllText("Temp/TerminalInputProbe.bootstrap.txt", "\nCreating probe window");
                    EditorWindow previous = focusedWindow;
                    var probe = CreateInstance<TerminalInputRegressionProbe>();
                    probe.previousWindow = previous;
                    probe.titleContent = new GUIContent("Terminal input check");
                    probe.position = new Rect(100, 100, 720, 260);
                    probe.ShowUtility();
                    probe.Focus();
                }
                catch (Exception ex) { File.WriteAllText("Temp/TerminalInputProbe.result.txt", "FAIL: startup " + ex); }
            }
        }

        private void OnEnable()
        {
            fixture = Path.GetFullPath("Temp/TerminalInputProbeFixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(fixture, "probe-a"));
            Directory.CreateDirectory(Path.Combine(fixture, "probe-b"));
            Set("currentWorkingDirectory", fixture);
            SetInput("cd pro");
            nextStep = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            WindowType.GetMethod("OnDisable", PrivateInstance).Invoke(this, null);
        }

        private void OnGUI()
        {
            bool tab = Event.current.rawType == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab;
            try
            {
                DrawWindow.Invoke(this, null);
                inputFocused = GUI.GetNameOfFocusedControl() == "TerminalInput";
                if (inputFocused)
                {
                    var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                    cursor = editor.cursorIndex;
                    selection = editor.selectIndex;
                    if (tab)
                    {
                        tabEvents++;
                        Rect rect = editor.position;
                        trace.AppendLine("Tab: rect=" + rect + ", cursor=" + cursor + ", selection=" + selection + ", text=" + Input);
                        if (rect.width < 50 || rect.height < 8) invalidGeometry = true;
                    }
                }
                else if (tab)
                {
                    tabEvents++;
                    invalidGeometry = true;
                    trace.AppendLine("Tab: input lost its control identity");
                }
                if (Event.current.type == EventType.Repaint) paints++;
            }
            catch (TargetInvocationException ex)
            {
                Finish("FAIL: OnGUI threw " + ex.InnerException);
            }
        }

        private void Tick()
        {
            if (finished || EditorApplication.timeSinceStartup < nextStep) return;
            try
            {
                Require(paints > 0 && inputFocused, "Input is not focused after repaint");
                paints = 0;
                switch (stage++)
                {
                    case 0:
                        Press(KeyCode.Tab, '\t');
                        Require(Input == "cd probe-a/", "First Tab did not complete: " + Input);
                        Require(!invalidGeometry && tabEvents > 0, "Tab corrupted the input rectangle or control identity");
                        break;
                    case 1:
                        Require(cursor == Input.Length && selection == cursor, "Completion selected text or moved the caret");
                        Press(KeyCode.Tab, '\t');
                        Require(Input == "cd probe-b/", "Second Tab did not cycle: " + Input);
                        Require(!invalidGeometry, "Cycling corrupted the input rectangle");
                        break;
                    case 2:
                        Require(cursor == Input.Length && selection == cursor, "Cycling selected text");
                        Press(KeyCode.Z, 'z');
                        Require(Input == "cd probe-b/z", "Typing after completion replaced text: " + Input);
                        SetInput("cd no-match-here");
                        break;
                    case 3:
                        Press(KeyCode.Tab, '\t');
                        Require(Input == "cd no-match-here", "No-match Tab changed the input");
                        Require(!invalidGeometry, "No-match Tab corrupted the input rectangle");
                        break;
                    default:
                        Require(cursor == Input.Length && selection == cursor, "No-match Tab changed the selection");
                        Finish("PASS: first Tab, candidate cycling, typing afterward, no-match Tab, input geometry and selection");
                        return;
                }
                Repaint();
                nextStep = EditorApplication.timeSinceStartup + 0.15;
            }
            catch (Exception ex) { Finish("FAIL: " + ex); }
        }

        private void Press(KeyCode key, char character)
        {
            SendEvent(new Event { type = EventType.KeyDown, keyCode = key, character = character });
            SendEvent(new Event { type = EventType.KeyUp, keyCode = key, character = character });
        }

        private string Input { get { return (string)WindowType.GetField("currentInput", PrivateInstance).GetValue(this); } }

        private void Set(string name, object value)
        {
            WindowType.GetField(name, PrivateInstance).SetValue(this, value);
        }

        private void SetInput(string value)
        {
            Set("currentInput", value);
            Set("pendingCaret", value.Length);
            Set("focusInputPending", true);
            object completion = WindowType.GetField("completion", PrivateInstance).GetValue(this);
            completion.GetType().GetMethod("Reset").Invoke(completion, null);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private void Finish(string result)
        {
            if (finished) return;
            finished = true;
            trace.AppendLine(result);
            File.WriteAllText("Temp/TerminalInputProbe.result.txt", trace.ToString());
            EditorApplication.update -= Tick;
            string allowed = Path.GetFullPath("Temp") + Path.DirectorySeparatorChar;
            if (fixture != null && fixture.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fixture))
                Directory.Delete(fixture, true);
            Close();
            if (previousWindow != null) previousWindow.Focus();
        }
    }
}
