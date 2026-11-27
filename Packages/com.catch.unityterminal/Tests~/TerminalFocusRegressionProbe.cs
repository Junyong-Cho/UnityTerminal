using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
// Copy to Assets/Editor and create Temp/RunTerminalFocusProbe. Checks that keys still reach the interactive
// view after the number of IMGUI controls drawn before it changes (the toolbar switches layout at a width
// threshold), and that a deliberate move of focus to another field is respected. Sends no shell command.
internal sealed class TerminalFocusRegressionProbe : EditorWindow
{
    [InitializeOnLoadMethod]
    private static void Requested()
    {
        const string marker = "Temp/RunTerminalFocusProbe";
        if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
        File.Delete(marker);
        EditorApplication.delayCall += Open;
    }

    private static readonly string ResultPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp/TerminalFocusProbe.result.txt");
    private object view;
    private Type viewType;
    private readonly StringBuilder sent = new StringBuilder();
    private readonly StringBuilder log = new StringBuilder();
    private int extraControls;
    private bool otherField;
    private bool drawOnly;
    private string otherText = "";
    private int stage;
    private double next;
    private bool painted;
    private Font font;
    private int failures;

    internal static void Open()
    {
        var window = CreateInstance<TerminalFocusRegressionProbe>();
        window.titleContent = new GUIContent("Terminal focus check");
        window.position = new Rect(150, 150, 700, 400);
        window.ShowUtility();
        window.Focus();
    }

    private void OnEnable()
    {
        Assembly assembly = Assembly.Load("com.catch.unityterminal.Editor");
        Type screenType = assembly.GetType("UnityTerminal.TerminalScreen");
        viewType = assembly.GetType("UnityTerminal.InteractiveTerminalView");
        object screen = Activator.CreateInstance(screenType, new object[] { 80, 20 });
        screenType.GetMethod("Feed").Invoke(screen, new object[] { "prompt> " });
        view = Activator.CreateInstance(viewType, new[] { screen });
        viewType.GetField("Send").SetValue(view, (Action<string>)(text => sent.Append(text)));
        font = Font.CreateDynamicFontFromOSFont("Consolas", 12);
        next = EditorApplication.timeSinceStartup + 0.5;
        EditorApplication.update += Tick;
    }

    private void OnDisable() { EditorApplication.update -= Tick; }

    private void OnGUI()
    {
        // Stand-in for the toolbar: a varying number of controls before the view.
        for (int i = 0; i < extraControls; i++) GUI.Button(new Rect(4 + i * 30, 2, 28, 18), i.ToString());
        if (otherField) { GUI.SetNextControlName("OtherField"); otherText = GUI.TextField(new Rect(200, 2, 120, 18), otherText); }
        Rect area = new Rect(0, 24, position.width, position.height - 24);
        viewType.GetMethod("Draw").Invoke(view, new object[] { area, font, 12, focusedWindow == this });
        if (drawOnly == false) viewType.GetMethod("HandleInput").Invoke(view, null);
        if (Event.current.type == EventType.Repaint) painted = true;
    }

    private void Press(char c)
    {
        SendEvent(new Event { type = EventType.KeyDown, character = c });
        SendEvent(new Event { type = EventType.KeyUp, character = c });
    }

    private void Click(Vector2 point)
    {
        SendEvent(new Event { type = EventType.MouseDown, mousePosition = point, button = 0, clickCount = 1 });
        SendEvent(new Event { type = EventType.MouseUp, mousePosition = point, button = 0, clickCount = 1 });
    }

    private void Check(string name, string expected)
    {
        bool ok = sent.ToString() == expected;
        if (ok == false) failures++;
        log.AppendLine((ok ? "PASS: " : "FAIL: ") + name + " sent=[" + sent + "] expected=[" + expected + "]");
        sent.Length = 0;
    }

    private void Tick()
    {
        if (EditorApplication.timeSinceStartup < next) return;
        if (!painted || focusedWindow != this) { Focus(); Repaint(); return; }
        next = EditorApplication.timeSinceStartup + 0.4;
        try
        {
            switch (stage++)
            {
                case 0: Repaint(); return;
                case 1: Press('a'); Check("baseline, 0 controls before the view", "a"); extraControls = 3; Repaint(); return;
                case 2: Repaint(); return;
                case 3: Press('b'); Check("after 3 controls were added before the view (ID shift)", "b"); extraControls = 0; Repaint(); return;
                case 4: Repaint(); return;
                case 5: Press('c'); Check("after the controls were removed again (ID shift back)", "c"); otherField = true; Repaint(); return;
                case 6: Repaint(); return;
                case 7: Press('d'); Check("another text field appeared before the view (not focused)", "d"); Click(new Vector2(260, 12)); return;
                case 8: Repaint(); return;
                case 9:
                    Press('e');
                    Check("user clicked the other field: keys go there, not to the view", "");
                    log.AppendLine("INFO: other field text=[" + otherText + "]");
                    // Right-click diagnostics must record the focus as it was before this click and before
                    // the view's focus recovery ran (Draw only, so no context menu opens).
                    drawOnly = true;
                    SendEvent(new Event { type = EventType.MouseDown, mousePosition = new Vector2(600, 300), button = 1, clickCount = 1 });
                    drawOnly = false;
                    string captured = (string)viewType.GetField("diagnosticsAtClick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
                    bool before = captured.Contains("ownsFocus=False") && captured.Contains("focusedName=[OtherField]");
                    if (before == false) failures++;
                    log.AppendLine((before ? "PASS: " : "FAIL: ") + "right-click diagnostics record the focus before recovery: " + captured.Split('\n')[1].Trim());
                    Click(new Vector2(600, 300));
                    return;
                case 10: Repaint(); return;
                case 11: Press('f'); Check("after clicking inside the view again", "f"); return;
                default:
                    log.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures + ")");
                    File.WriteAllText(ResultPath, log.ToString());
                    Close();
                    return;
            }
        }
        catch (Exception ex) { File.WriteAllText(ResultPath, log + "ERROR " + ex); Close(); }
    }
}
}
