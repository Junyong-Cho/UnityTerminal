using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityTerminal
{
    internal sealed class InteractiveTerminalView
    {
        private readonly TerminalScreen screen;
        private GUIStyle textStyle;
        private GUIStyle compositionStyle;
        private Event inputEvent;
        private string committedText;
        private float cellWidth = 8, cellHeight = 16;
        private Rect bounds;
        private int control;
        private bool focusPending = true;
        private int scrollBack;
        private int selectionStart = -1, selectionEnd = -1;
        private int selectionVersion;
        private bool selecting;
        private string status;
        public Action<string> Send;
        public Action<int, int> Resize;

        public InteractiveTerminalView(TerminalScreen screen) { this.screen = screen; }

        public void Draw(Rect rect, Font font, int fontSize, bool windowFocused)
        {
            bounds = rect;
            if (textStyle == null) textStyle = new GUIStyle { clipping = TextClipping.Clip, wordWrap = false, richText = false, alignment = TextAnchor.UpperLeft };
            textStyle.font = font;
            textStyle.fontSize = fontSize;
            textStyle.fontStyle = FontStyle.Normal;
            cellWidth = Mathf.Max(1, textStyle.CalcSize(new GUIContent("M")).x);
            cellHeight = Mathf.Max(fontSize + 3, textStyle.lineHeight);
            if (Event.current.type == EventType.Repaint)
            {
                int columns = Mathf.Clamp(Mathf.FloorToInt((rect.width - 8) / cellWidth), 2, 500);
                int rows = Mathf.Clamp(Mathf.FloorToInt((rect.height - 22) / cellHeight), 2, 300);
                if (columns != screen.Columns || rows != screen.Rows)
                {
                    if (Resize != null) Resize(columns, rows);
                    screen.Resize(columns, rows);
                    selectionStart = selectionEnd = -1;
                }
                Paint(windowFocused);
            }
            // A real IMGUI text control handles IME composition and Unicode commits. Only
            // committed text is forwarded; application navigation keys use the captured event.
            inputEvent = Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp ||
                Event.current.type == EventType.ValidateCommand || Event.current.type == EventType.ExecuteCommand ? new Event(Event.current) : null;
            if (compositionStyle == null) compositionStyle = new GUIStyle { clipping = TextClipping.Clip, richText = false, wordWrap = false };
            compositionStyle.font = font;
            compositionStyle.fontSize = fontSize;
            compositionStyle.normal.textColor = Rgb(0xd4d4d4);
            GUI.SetNextControlName("InteractiveTerminal");
            Rect compositionRect = new Rect(rect.x + 4 + screen.CursorX * cellWidth, rect.y + 2 + screen.CursorY * cellHeight,
                Mathf.Max(160, rect.width - 8 - screen.CursorX * cellWidth), cellHeight);
            committedText = GUI.TextField(compositionRect, "", compositionStyle);
            if (focusPending && Event.current.type != EventType.Layout)
            {
                GUI.FocusControl("InteractiveTerminal");
                focusPending = false;
            }
            if (GUI.GetNameOfFocusedControl() == "InteractiveTerminal")
            {
                control = GUIUtility.keyboardControl;
                if (committedText.Length > 0)
                {
                    var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), control);
                    editor.text = "";
                    editor.cursorIndex = editor.selectIndex = 0;
                }
            }
        }

        private void Paint(bool windowFocused)
        {
            EditorGUI.DrawRect(bounds, Rgb(0x1e1e1e));
            if (selectionVersion != screen.Version && !selecting) selectionStart = selectionEnd = -1;
            scrollBack = Mathf.Clamp(scrollBack, 0, screen.HistoryCount);
            // BeginClip adds no control ID, so repaint-only drawing cannot shift the input ID.
            GUI.BeginClip(bounds);
            for (int y = 0; y < screen.Rows; y++)
            {
                ScreenCell[] line = screen.GetLine(y, scrollBack);
                for (int x = 0; x < Math.Min(screen.Columns, line.Length); )
                {
                    ScreenCell cell = line[x];
                    if (cell.Width == 0) { x++; continue; }
                    int width = Math.Max(1, cell.Width);
                    int count = width;
                    string text = cell.Text ?? " ";
                    bool selected = Selected(x, y);
                    // Batch ASCII runs while positioning Unicode glyphs in their own cells.
                    if (width == 1 && text.Length == 1 && text[0] < 127)
                    {
                        var run = new StringBuilder(text);
                        while (x + count < Math.Min(screen.Columns, line.Length))
                        {
                            ScreenCell next = line[x + count];
                            if (next.Width != 1 || next.Text == null || next.Text.Length != 1 || next.Text[0] >= 127 ||
                                !cell.Style.Equals(next.Style) || selected != Selected(x + count, y)) break;
                            run.Append(next.Text); count++;
                        }
                        text = run.ToString();
                    }
                    int foreground = cell.Style.Foreground < 0 ? 0xd4d4d4 : cell.Style.Foreground;
                    int background = cell.Style.Background < 0 ? 0x1e1e1e : cell.Style.Background;
                    if (cell.Style.Inverse) { int swap = foreground; foreground = background; background = swap; }
                    if (selected) { foreground = 0xffffff; background = 0x264f78; }
                    var area = new Rect(4 + x * cellWidth, 2 + y * cellHeight, count * cellWidth, cellHeight);
                    if (background != 0x1e1e1e) EditorGUI.DrawRect(area, Rgb(background));
                    textStyle.fontStyle = cell.Style.Bold ? (cell.Style.Italic ? FontStyle.BoldAndItalic : FontStyle.Bold) : cell.Style.Italic ? FontStyle.Italic : FontStyle.Normal;
                    textStyle.normal.textColor = Rgb(foreground);
                    GUI.Label(area, text, textStyle);
                    if (cell.Style.Underline) EditorGUI.DrawRect(new Rect(area.x, area.yMax - 2, area.width, 1), Rgb(foreground));
                    x += count;
                }
            }
            if (scrollBack == 0 && screen.CursorVisible && windowFocused && GUIUtility.keyboardControl == control && (int)(EditorApplication.timeSinceStartup * 2) % 2 == 0)
            {
                float width = screen.CursorShape >= 5 ? 2 : cellWidth;
                float height = screen.CursorShape == 3 || screen.CursorShape == 4 ? 2 : cellHeight;
                EditorGUI.DrawRect(new Rect(4 + screen.CursorX * cellWidth, 2 + screen.CursorY * cellHeight + cellHeight - height, width, height), new Color(0.8f, 0.8f, 0.8f, 0.45f));
            }
            textStyle.fontStyle = FontStyle.Normal;
            textStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            GUI.Label(new Rect(4, bounds.height - 19, bounds.width - 8, 19), status ?? "Ctrl+C interrupt   Ctrl+V paste   Ctrl+Shift+C copy   Script reload waits for exit", textStyle);
            GUI.EndClip();
        }

        // Called after the window has finished registering all GUILayout controls.
        public bool HandleInput()
        {
            Event e = Event.current;
            if (e.type == EventType.MouseDown && bounds.Contains(e.mousePosition))
            {
                GUIUtility.keyboardControl = control;
                if (e.button == 1)
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Copy"), false, Copy);
                    menu.AddItem(new GUIContent("Paste"), false, Paste);
                    menu.ShowAsContext();
                }
                else if (e.button == 0)
                {
                    selectionVersion = screen.Version;
                    selectionStart = selectionEnd = CellAt(e.mousePosition);
                    selecting = true;
                    GUIUtility.hotControl = control;
                }
                e.Use(); return true;
            }
            if (selecting && e.type == EventType.MouseDrag)
            { selectionEnd = CellAt(e.mousePosition); e.Use(); return true; }
            if (selecting && e.type == EventType.MouseUp)
            { selecting = false; GUIUtility.hotControl = 0; e.Use(); return true; }
            if (e.type == EventType.ScrollWheel && bounds.Contains(e.mousePosition))
            {
                scrollBack = Mathf.Clamp(scrollBack + (int)(e.delta.y * 3), 0, screen.HistoryCount);
                selectionStart = selectionEnd = -1;
                e.Use(); return true;
            }
            if (control == 0 || GUIUtility.keyboardControl != control) return false;
            EditorGUIUtility.editingTextField = true;
            Event original = inputEvent ?? e;
            if ((original.type == EventType.ValidateCommand || original.type == EventType.ExecuteCommand) && (original.commandName == "Paste" || original.commandName == "Copy"))
            {
                if (original.type == EventType.ExecuteCommand) { if (original.commandName == "Paste") Paste(); else Copy(); }
                e.Use(); return true;
            }
            if (inputEvent == null) return false;
            Event keyEvent = inputEvent;
            if (keyEvent.type == EventType.KeyUp) { e.Use(); return true; }
            if (keyEvent.type != EventType.KeyDown) return false;
            if ((keyEvent.control || keyEvent.command) && keyEvent.keyCode == KeyCode.V) { Paste(); e.Use(); return true; }
            if ((keyEvent.control || keyEvent.command) && keyEvent.shift && keyEvent.keyCode == KeyCode.C) { Copy(); e.Use(); return true; }
            TerminalKey key = MapKey(keyEvent.keyCode);
            char character = keyEvent.character;
            if (keyEvent.control && character == '\0' && keyEvent.keyCode >= KeyCode.A && keyEvent.keyCode <= KeyCode.Z)
                character = (char)('a' + (int)keyEvent.keyCode - (int)KeyCode.A);
            string text;
            if (!string.IsNullOrEmpty(committedText) && !keyEvent.control && !keyEvent.alt) text = committedText;
            else text = key == TerminalKey.Character && !keyEvent.control && !keyEvent.alt ? null :
                TerminalInput.Encode(key, character, keyEvent.shift, keyEvent.alt, keyEvent.control, screen.ApplicationCursor);
            if (text != null)
            {
                scrollBack = 0; selectionStart = selectionEnd = -1; status = null;
                if (Send != null) Send(text);
            }
            e.Use(); return true;
        }

        private static TerminalKey MapKey(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Return: case KeyCode.KeypadEnter: return TerminalKey.Enter;
                case KeyCode.Backspace: return TerminalKey.Backspace;
                case KeyCode.Tab: return TerminalKey.Tab;
                case KeyCode.Escape: return TerminalKey.Escape;
                case KeyCode.UpArrow: return TerminalKey.Up;
                case KeyCode.DownArrow: return TerminalKey.Down;
                case KeyCode.LeftArrow: return TerminalKey.Left;
                case KeyCode.RightArrow: return TerminalKey.Right;
                case KeyCode.Home: return TerminalKey.Home;
                case KeyCode.End: return TerminalKey.End;
                case KeyCode.Insert: return TerminalKey.Insert;
                case KeyCode.Delete: return TerminalKey.Delete;
                case KeyCode.PageUp: return TerminalKey.PageUp;
                case KeyCode.PageDown: return TerminalKey.PageDown;
                default:
                    return key >= KeyCode.F1 && key <= KeyCode.F12 ? TerminalKey.F1 + (key - KeyCode.F1) : TerminalKey.Character;
            }
        }

        private int CellAt(Vector2 mouse)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((mouse.x - bounds.x - 4) / cellWidth), 0, screen.Columns - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((mouse.y - bounds.y - 2) / cellHeight), 0, screen.Rows - 1);
            return y * screen.Columns + x;
        }
        private bool Selected(int x, int y)
        {
            int cell = y * screen.Columns + x;
            return selectionStart >= 0 && selectionStart != selectionEnd && cell >= Math.Min(selectionStart, selectionEnd) && cell <= Math.Max(selectionStart, selectionEnd);
        }
        private void Copy()
        {
            if (selectionStart < 0 || selectionStart == selectionEnd) { GUIUtility.systemCopyBuffer = screen.GetText(); return; }
            int first = Math.Min(selectionStart, selectionEnd), last = Math.Max(selectionStart, selectionEnd);
            var result = new StringBuilder();
            for (int y = first / screen.Columns; y <= last / screen.Columns; y++)
            {
                ScreenCell[] line = screen.GetLine(y, scrollBack);
                var row = new StringBuilder();
                for (int x = y == first / screen.Columns ? first % screen.Columns : 0; x <= (y == last / screen.Columns ? last % screen.Columns : screen.Columns - 1); x++)
                    if (x < line.Length && line[x].Width != 0) row.Append(line[x].Text ?? " ");
                result.Append(row.ToString().TrimEnd());
                if (y != last / screen.Columns) result.Append('\n');
            }
            GUIUtility.systemCopyBuffer = result.ToString();
        }
        private void Paste()
        {
            scrollBack = 0;
            selectionStart = selectionEnd = -1;
            if (Send != null) Send(TerminalInput.Paste(GUIUtility.systemCopyBuffer, screen.BracketedPaste));
        }
        public void ShowError(string error) { status = error; }
        private static Color Rgb(int color) { return new Color(((color >> 16) & 255) / 255f, ((color >> 8) & 255) / 255f, (color & 255) / 255f); }
    }
}
