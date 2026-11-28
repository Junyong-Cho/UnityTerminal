using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal.Tests
{
// Copy to Assets/Editor and create Temp/RunTerminalClickProbe. Checks click-to-move in the interactive
// view with real IMGUI mouse events on an in-memory screen: a plain click on the cursor row sends
// Left/Right keys; other rows, drags, double clicks and modified clicks send nothing. No app runs.
internal sealed class TerminalClickRegressionProbe : EditorWindow
{
    [InitializeOnLoadMethod]
    private static void Requested()
    {
        const string marker = "Temp/RunTerminalClickProbe";
        if (AssetDatabase.IsAssetImportWorkerProcess() || !File.Exists(marker)) return;
        File.Delete(marker);
        EditorApplication.delayCall += Open;
    }

    private static readonly string ResultPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp/TerminalClickProbe.result.txt");
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private object view, screen;
    private Type viewType;
    private readonly StringBuilder sent = new StringBuilder();
    private readonly StringBuilder log = new StringBuilder();
    private int stage, failures;
    private double next, delay;
    private bool painted, primed;
    private Font font;
    private Rect area;

    private static void Open()
    {
        var window = CreateInstance<TerminalClickRegressionProbe>();
        window.titleContent = new GUIContent("Terminal click check");
        window.position = new Rect(150, 150, 700, 400);
        window.ShowUtility();
        window.Focus();
    }

    private void OnEnable()
    {
        Assembly assembly = Assembly.Load("com.catch.unityterminal.Editor");
        Type screenType = assembly.GetType("UnityTerminal.TerminalScreen");
        viewType = assembly.GetType("UnityTerminal.InteractiveTerminalView");
        screen = Activator.CreateInstance(screenType, new object[] { 80, 20 });
        // Row 0: output; row 1: prompt with the cursor after "def".
        screenType.GetMethod("Feed").Invoke(screen, new object[] { "output line\r\n> abc def" });
        view = Activator.CreateInstance(viewType, new[] { screen });
        viewType.GetField("Send").SetValue(view, (Action<string>)(text => sent.Append(text)));
        font = Font.CreateDynamicFontFromOSFont("Consolas", 12);
        next = EditorApplication.timeSinceStartup + 0.5;
        EditorApplication.update += Tick;
    }

    private void OnDisable() { EditorApplication.update -= Tick; }

    private void OnGUI()
    {
        area = new Rect(0, 0, position.width, position.height);
        viewType.GetMethod("Draw").Invoke(view, new object[] { area, font, 12, focusedWindow == this });
        viewType.GetMethod("HandleInput").Invoke(view, null);
        if (Event.current.type == EventType.Repaint) painted = true;
    }

    // Centre of a cell, matching InteractiveTerminalView.CellAt.
    private Vector2 Cell(int column, int row)
    {
        float width = (float)viewType.GetField("cellWidth", Private).GetValue(view), height = (float)viewType.GetField("cellHeight", Private).GetValue(view);
        return new Vector2(area.x + 4 + (column + 0.5f) * width, area.y + 2 + (row + 0.5f) * height);
    }

    private void Mouse(EventType type, Vector2 point, int clicks = 1, EventModifiers modifiers = EventModifiers.None)
    {
        SendEvent(new Event { type = type, mousePosition = point, button = 0, clickCount = clicks, modifiers = modifiers });
    }

    private void Click(Vector2 point, int clicks = 1, EventModifiers modifiers = EventModifiers.None)
    {
        Mouse(EventType.MouseDown, point, clicks, modifiers);
        Mouse(EventType.MouseUp, point, clicks, modifiers);
    }

    private void Feed(string text) { screen.GetType().GetMethod("Feed").Invoke(screen, new object[] { text }); }

    // Input sent through the view's own path, as typed text is.
    private void SendText(string text) { viewType.GetMethod("SendText", Private).Invoke(view, new object[] { text }); }

    private void Key(KeyCode key)
    {
        SendEvent(new Event { type = EventType.KeyDown, keyCode = key });
        SendEvent(new Event { type = EventType.KeyUp, keyCode = key });
    }

    private void Check(string name, string expected)
    {
        bool ok = sent.ToString() == expected;
        if (ok == false) failures++;
        log.AppendLine((ok ? "PASS: " : "FAIL: ") + name + " sent=[" + sent.ToString().Replace("\x1b", "ESC") + "] expected=[" + expected.Replace("\x1b", "ESC") + "]");
        sent.Length = 0;
    }

    private void Tick()
    {
        if (EditorApplication.timeSinceStartup < next) return;
        if (!painted || focusedWindow != this) { Focus(); Repaint(); return; }
        // Stages are 1.2 s apart so the view sees the screen as settled (input quiet for 1 s) between them; a
        // stage that sets a shorter delay simulates an app whose echo has not caught up yet. Each stage is
        // preceded by a repaint, so the view has looked at the screen just before it.
        if (primed == false) { primed = true; Repaint(); next = EditorApplication.timeSinceStartup + 0.1; return; }
        primed = false;
        delay = 1.2;
        try
        {
            switch (stage++)
            {
                case 0: Repaint(); return;
                case 1: Click(Cell(4, 1)); Check("click on the cursor row moves left by glyphs", "\x1b[D\x1b[D\x1b[D\x1b[D\x1b[D"); return;
                case 2: Click(Cell(3, 0)); Check("click on another row sends nothing", ""); return;
                case 3:
                    Mouse(EventType.MouseDown, Cell(2, 1)); Mouse(EventType.MouseDrag, Cell(6, 1)); Mouse(EventType.MouseUp, Cell(6, 1));
                    Check("drag selects instead of moving", "");
                    Click(Cell(40, 1)); sent.Length = 0; // clear the selection (click past the text: nothing to send)
                    return;
                case 4: Click(Cell(4, 1), 2); Check("the second click of a double click sends nothing", ""); return;
                case 5: Click(Cell(4, 1), 1, EventModifiers.Shift); Check("shift+click sends nothing", ""); return;
                case 6: Click(Cell(40, 1)); Check("click past the text stays at the end", ""); return;
                case 7:
                    Mouse(EventType.MouseDown, Cell(4, 1)); Mouse(EventType.MouseDrag, Cell(7, 1)); Mouse(EventType.MouseDrag, Cell(4, 1)); Mouse(EventType.MouseUp, Cell(4, 1));
                    Check("drag that returns to the start cell sends nothing", "");
                    return;
                case 8:
                    Click(Cell(4, 1), 1); Click(Cell(4, 1), 2);
                    Check("a real double click moves once (first click only)", "\x1b[D\x1b[D\x1b[D\x1b[D\x1b[D");
                    return;
                case 9: Feed("\r\x1b[K> abc   "); Repaint(); return;       // typed trailing spaces: cursor reached column 8
                case 10: Feed("\x1b[6D"); Repaint(); return;               // cursor back to column 2
                case 11: Click(Cell(8, 1)); Check("typed trailing spaces stay reachable", "\x1b[C\x1b[C\x1b[C\x1b[C\x1b[C\x1b[C"); return;
                case 12: Feed("\r\x1b[K> ab"); Repaint(); return;          // text shrank (deletion): the old reach is dropped
                case 13: Click(Cell(8, 1)); Check("after deletion the old reach is not reused", ""); return;
                // "abc   " -> cursor to the start -> Delete "a": the three spaces remain and stay reachable.
                case 14: Feed("\r\x1b[K> abc   "); Repaint(); return;
                case 15: Feed("\x1b[6D"); Repaint(); return;
                case 16: Key(KeyCode.Delete); sent.Length = 0; Feed("\x1b[P"); Repaint(); return;
                case 17: Click(Cell(7, 1)); Check("Delete before the spaces keeps them reachable", "\x1b[C\x1b[C\x1b[C\x1b[C\x1b[C"); return;
                // "abc   " -> Backspace the three spaces: their old cells are no longer reachable.
                case 18: Feed("\r\x1b[K> abc   "); Repaint(); return;
                case 19: Key(KeyCode.Backspace); Key(KeyCode.Backspace); Key(KeyCode.Backspace); sent.Length = 0; Feed("\b \b\b \b\b \b"); Repaint(); return;
                case 20: Click(Cell(8, 1)); Check("Backspaced spaces are not reachable", ""); return;
                // Same, but a repaint happens before the app echoes the Backspaces (stale cursor at the old end).
                case 21: Feed("\r\x1b[K> abc   "); Repaint(); return;
                case 22: Key(KeyCode.Backspace); Key(KeyCode.Backspace); Key(KeyCode.Backspace); sent.Length = 0; Repaint(); delay = 0.3; return;
                case 23: Feed("\b \b\b \b\b \b"); Repaint(); return;
                case 24: Click(Cell(8, 1)); Check("Backspaced spaces stay unreachable when the echo is late", ""); return;
                // "abc   " -> Left x3 to just before the spaces -> Backspace removes "c": all three spaces remain.
                case 25: Feed("\r\x1b[K> abc   "); Repaint(); return;
                case 26: Key(KeyCode.LeftArrow); Key(KeyCode.LeftArrow); Key(KeyCode.LeftArrow); sent.Length = 0; Feed("\x1b[3D"); Repaint(); return;
                case 27: Key(KeyCode.Backspace); sent.Length = 0; Feed("\b\x1b[P"); Repaint(); return;
                case 28: Click(Cell(7, 1)); Check("Backspace of the glyph before the spaces keeps them", "\x1b[C\x1b[C\x1b[C"); return;
                // Same position, Delete instead: it removes the first space, two remain.
                case 29: Feed("\r\x1b[K> abc   "); Repaint(); return;
                case 30: Key(KeyCode.LeftArrow); Key(KeyCode.LeftArrow); Key(KeyCode.LeftArrow); sent.Length = 0; Feed("\x1b[3D"); Repaint(); return;
                case 31: Key(KeyCode.Delete); sent.Length = 0; Feed("\x1b[P"); Repaint(); return;
                case 32: Click(Cell(8, 1)); Check("Delete at the text end removes one space", "\x1b[C\x1b[C"); return;
                // A space and a Backspace sent together, echoed separately with a repaint in between.
                case 33: Feed("\r\x1b[K> abcd"); Repaint(); return;
                case 34: SendText(" "); Key(KeyCode.Backspace); sent.Length = 0; Feed(" "); Repaint(); delay = 0.3; return;
                case 35: Feed("\b \b"); Repaint(); return;
                case 36: Click(Cell(9, 1)); Check("space and Backspace echoed separately leave no space", ""); return;
                default:
                    log.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures + ")");
                    File.WriteAllText(ResultPath, log.ToString());
                    Close();
                    return;
            }
        }
        catch (Exception ex) { File.WriteAllText(ResultPath, log + "ERROR " + ex); Close(); }
        finally { next = EditorApplication.timeSinceStartup + delay; }
    }
}
}
