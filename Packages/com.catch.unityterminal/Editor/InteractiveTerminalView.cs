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
        private bool composing;
        private readonly TerminalComposition composition = new TerminalComposition();
        private float cellWidth = 8, cellHeight = 16;
        private Font measuredFont;
        private int measuredFontSize;
        private float measuredPixelScale;
        private Rect bounds;
        private int control;
        private bool focusPending = true;
        private int scrollBack;
        private float scrollRemainder;
        private bool scrollingApplication;
        private int selectionStart = -1, selectionEnd = -1;
        private ScreenCell[][] selectionLines;
        private bool selecting;
        private string status;
        public Action<string> Send;
        public Action<int, int> Resize;

        public InteractiveTerminalView(TerminalScreen screen) { this.screen = screen; }

        public void ResetComposition() { composition.Reset(); }

        [Serializable] internal sealed class ViewState
        {
            public int ScrollBack, SelectionStart, SelectionEnd;
            // The selected screen as it looked when selected. JsonUtility cannot store jagged
            // arrays, so rows are flattened into SnapshotCells with SnapshotRowLengths.
            public int SnapshotColumns;
            public int[] SnapshotRowLengths;
            public ScreenCell[] SnapshotCells;
        }

        internal ViewState CaptureState()
        {
            var state = new ViewState { ScrollBack = scrollBack, SelectionStart = selectionStart, SelectionEnd = selectionEnd };
            if (HasSelection == true && selectionLines != null)
            {
                int total = 0;
                foreach (ScreenCell[] line in selectionLines) total += line.Length;
                state.SnapshotColumns = screen.Columns;
                state.SnapshotRowLengths = new int[selectionLines.Length];
                state.SnapshotCells = new ScreenCell[total];
                int offset = 0;
                for (int y = 0; y < selectionLines.Length; y++)
                {
                    ScreenCell[] line = selectionLines[y];
                    state.SnapshotRowLengths[y] = line.Length;
                    Array.Copy(line, 0, state.SnapshotCells, offset, line.Length);
                    offset += line.Length;
                }
            }
            return state;
        }

        internal void RestoreState(ViewState state)
        {
            scrollBack = Mathf.Clamp(state.ScrollBack, 0, screen.HistoryCount);
            int last = screen.Columns * screen.Rows - 1;
            selectionStart = Mathf.Clamp(state.SelectionStart, -1, last); selectionEnd = Mathf.Clamp(state.SelectionEnd, -1, last);
            selecting = false;
            selectionLines = null;
            if (HasSelection == false) return;
            // Rebuilding the snapshot from the current screen would select text that arrived after
            // the user's selection; without the saved snapshot, drop the selection instead.
            if (RestoreSnapshot(state) == false) selectionStart = selectionEnd = -1;
        }

        private bool RestoreSnapshot(ViewState state)
        {
            int[] lengths = state.SnapshotRowLengths;
            ScreenCell[] cells = state.SnapshotCells;
            if (lengths == null || cells == null) return false;
            if (state.SnapshotColumns != screen.Columns || lengths.Length != screen.Rows) return false;
            var lines = new ScreenCell[lengths.Length][];
            int offset = 0;
            for (int y = 0; y < lengths.Length; y++)
            {
                if (lengths[y] < 0 || offset + lengths[y] > cells.Length) return false;
                var line = new ScreenCell[lengths[y]];
                Array.Copy(cells, offset, line, 0, line.Length);
                // JsonUtility stores a null string as "". "" is real only on wide-glyph continuation
                // cells (Width 0); elsewhere restore null so Copy and Paint treat the cell as blank.
                for (int x = 0; x < line.Length; x++)
                    if (line[x].Width != 0 && line[x].Text == "") line[x].Text = null;
                lines[y] = line;
                offset += line.Length;
            }
            if (offset != cells.Length) return false;
            selectionLines = lines;
            return true;
        }

        internal static Vector2 MeasureCellSize(Font font, int fontSize)
        {
            var style = new GUIStyle { font = font, fontSize = fontSize, richText = false, wordWrap = false };
            return MeasureCellSize(style);
        }

        private static Vector2 MeasureCellSize(GUIStyle style)
        {
            // Measure advances, excluding per-label rounding/padding. A long sample also
            // avoids accumulating a rounded single-character width across the row.
            float width = (style.CalcSize(new GUIContent(new string('M', 33))).x - style.CalcSize(new GUIContent("M")).x) / 32;
            return new Vector2(Mathf.Max(1, width), Mathf.Max(style.fontSize + 3, style.lineHeight));
        }

        internal static Vector2Int MeasureGridSize(Vector2 area, Vector2 cell)
        {
            return new Vector2Int(Mathf.Clamp(Mathf.FloorToInt((area.x - 8) / cell.x), 2, 500),
                Mathf.Clamp(Mathf.FloorToInt((area.y - 22) / cell.y), 2, 300));
        }

        public void Draw(Rect rect, Font font, int fontSize, bool windowFocused)
        {
            bounds = rect;
            if (textStyle == null) textStyle = new GUIStyle { clipping = TextClipping.Clip, wordWrap = false, richText = false, alignment = TextAnchor.UpperLeft };
            textStyle.font = font;
            textStyle.fontSize = fontSize;
            textStyle.fontStyle = FontStyle.Normal;
            if (measuredFont != font || measuredFontSize != fontSize || measuredPixelScale != EditorGUIUtility.pixelsPerPoint)
            {
                Vector2 cell = MeasureCellSize(textStyle);
                cellWidth = cell.x; cellHeight = cell.y;
                measuredFont = font; measuredFontSize = fontSize; measuredPixelScale = EditorGUIUtility.pixelsPerPoint;
            }
            if (Event.current.type == EventType.Repaint)
            {
                Vector2Int grid = MeasureGridSize(rect.size, new Vector2(cellWidth, cellHeight));
                int columns = grid.x, rows = grid.y;
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
                Event.current.type == EventType.ValidateCommand || Event.current.type == EventType.ExecuteCommand ||
                Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag ||
                Event.current.type == EventType.MouseUp ? new Event(Event.current) : null;
            if (compositionStyle == null) compositionStyle = new GUIStyle { clipping = TextClipping.Clip, richText = false, wordWrap = false };
            compositionStyle.font = font;
            compositionStyle.fontSize = fontSize;
            compositionStyle.normal.textColor = Rgb(0xd4d4d4);
            GUI.SetNextControlName("InteractiveTerminal");
            Rect cursorCell = GetCursorCellRect();
            Rect compositionRect = new Rect(rect.x + cursorCell.x, rect.y + cursorCell.y,
                Mathf.Max(160, rect.width - 8 - screen.CursorX * cellWidth), cellHeight);
            composition.BeginEvent();
            Color caretColor = GUI.skin.settings.cursorColor;
            try
            {
                // This control receives IME text; the VT cursor below represents the app.
                // Keep Unity's caret only while its composition text is actually visible.
                if (Input.compositionString.Length == 0 || !screen.CursorVisible || scrollBack != 0)
                    GUI.skin.settings.cursorColor = Color.clear;
                committedText = GUI.TextField(compositionRect, "", compositionStyle);
            }
            finally { GUI.skin.settings.cursorColor = caretColor; }
            composing = composition.ConsumesNavigation(inputEvent ?? Event.current);
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
            scrollBack = Mathf.Clamp(scrollBack, 0, screen.HistoryCount);
            // BeginClip adds no control ID, so repaint-only drawing cannot shift the input ID.
            GUI.BeginClip(bounds);
            for (int y = 0; y < screen.Rows; y++)
            {
                ScreenCell[] line = SelectionLine(y);
                for (int x = 0; x < Math.Min(screen.Columns, line.Length); )
                {
                    ScreenCell cell = line[x];
                    if (cell.Width == 0) { x++; continue; }
                    int width = Math.Max(1, cell.Width);
                    int count = width;
                    string text = cell.Text ?? " ";
                    bool selected = Selected(x, y);
                    // Unity adds spacing to synthetic bold/italic text, even for a mono
                    // font. Keep styled glyphs on individual cells without stretching them.
                    if (width == 1 && text.Length == 1 && text[0] < 127 && !cell.Style.Bold && !cell.Style.Italic)
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
            bool blinkingCursor = screen.CursorShape == 0 || screen.CursorShape == 1 || screen.CursorShape == 3 || screen.CursorShape == 5;
            if (!HasSelection && scrollBack == 0 && screen.CursorVisible && windowFocused && GUIUtility.keyboardControl == control &&
                Input.compositionString.Length == 0 && (!blinkingCursor || (int)(EditorApplication.timeSinceStartup * 2) % 2 == 0))
            {
                EditorGUI.DrawRect(GetCursorPaintRect(), new Color(0.8f, 0.8f, 0.8f, 0.45f));
            }
            textStyle.fontStyle = FontStyle.Normal;
            textStyle.normal.textColor = new Color(0.65f, 0.65f, 0.65f);
            string footer = scrollBack > 0 ? "Scrollback " + scrollBack + "/" + screen.HistoryCount + "   Type to return to latest output" :
                HasSelection ? "Ctrl+C / Ctrl+Shift+C copy   Right-click for Copy" : "Ctrl+C interrupt   Ctrl+V paste   Ctrl+Shift+C copy";
            GUI.Label(new Rect(4, bounds.height - 19, bounds.width - 8, 19), status ?? footer, textStyle);
            GUI.EndClip();
        }

        private Rect GetCursorCellRect()
        {
            ScreenCell cell = screen.GetLine(screen.CursorY)[screen.CursorX];
            return new Rect(4 + screen.CursorX * cellWidth, 2 + screen.CursorY * cellHeight,
                Math.Max(1, cell.Width) * cellWidth, cellHeight);
        }

        private Rect GetCursorPaintRect()
        {
            Rect cursor = GetCursorCellRect();
            if (screen.CursorShape >= 5) cursor.width = Mathf.Max(1, 1 / EditorGUIUtility.pixelsPerPoint);
            if (screen.CursorShape == 3 || screen.CursorShape == 4) { cursor.y = cursor.yMax - 2; cursor.height = 2; }
            return cursor;
        }

        // Called after the window has finished registering all GUILayout controls.
        public bool HandleInput()
        {
            Event e = Event.current;
            // The IME TextField may consume mouse events before this handler runs.
            Event mouse = inputEvent ?? e;
            if (mouse.type == EventType.MouseDown && bounds.Contains(mouse.mousePosition))
            {
                GUIUtility.keyboardControl = control;
                if (mouse.button == 1)
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Copy"), false, Copy);
                    menu.AddItem(new GUIContent("Paste"), false, Paste);
                    menu.ShowAsContext();
                }
                else if (mouse.button == 0)
                {
                    SnapshotSelection();
                    selectionStart = selectionEnd = CellAt(mouse.mousePosition);
                    selecting = true;
                    GUIUtility.hotControl = control;
                }
                e.Use(); return true;
            }
            if (selecting && mouse.type == EventType.MouseDrag)
            { selectionEnd = CellAt(mouse.mousePosition); e.Use(); return true; }
            if (selecting && mouse.type == EventType.MouseUp)
            { selecting = false; GUIUtility.hotControl = 0; if (!HasSelection) selectionLines = null; e.Use(); return true; }
            if (e.type == EventType.ScrollWheel && bounds.Contains(e.mousePosition))
            {
                ScrollWheel(e.delta.y);
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
            if (!composing && original.type == EventType.KeyDown && original.shift && !original.control && !original.alt && !original.command &&
                (original.keyCode == KeyCode.Return || original.keyCode == KeyCode.KeypadEnter))
            {
                // Preserve the modified key instead of forwarding a raw return produced
                // by TextField. Newline-only paste is discarded by some terminal apps.
                SendText(TerminalInput.Encode(TerminalKey.Enter, '\0', true, false, false, screen.ApplicationCursor));
                e.Use(); return true;
            }
            // IME commits can arrive independently of a navigation KeyDown. Never forward
            // composition editing keys (including the Enter that accepts a syllable).
            if (!string.IsNullOrEmpty(committedText) &&
                (inputEvent == null || (!inputEvent.control && !inputEvent.alt && !inputEvent.command)))
            {
                SendText(committedText);
                if (e.type != EventType.Layout && e.type != EventType.Repaint) e.Use();
                return true;
            }
            if (composing) { if (inputEvent != null) e.Use(); return inputEvent != null; }
            if (inputEvent == null) return false;
            Event keyEvent = inputEvent;
            if (keyEvent.type == EventType.KeyUp) { e.Use(); return true; }
            if (keyEvent.type != EventType.KeyDown) return false;
            if (ScrollHistoryKey(keyEvent)) { e.Use(); return true; }
            if ((keyEvent.control || keyEvent.command) && keyEvent.keyCode == KeyCode.V) { Paste(); e.Use(); return true; }
            if ((keyEvent.control || keyEvent.command) && keyEvent.keyCode == KeyCode.C && (keyEvent.shift || HasSelection))
            { Copy(); e.Use(); return true; }
            TerminalKey key = MapKey(keyEvent.keyCode);
            char character = keyEvent.character;
            if (keyEvent.control && character == '\0' && keyEvent.keyCode >= KeyCode.A && keyEvent.keyCode <= KeyCode.Z)
                character = (char)('a' + (int)keyEvent.keyCode - (int)KeyCode.A);
            string text = key == TerminalKey.Character && !keyEvent.control && !keyEvent.alt ? null :
                TerminalInput.Encode(key, character, keyEvent.shift, keyEvent.alt, keyEvent.control, screen.ApplicationCursor);
            if (text != null) SendText(text);
            e.Use(); return true;
        }

        private void SendText(string text)
        {
            scrollRemainder = 0;
            scrollBack = 0; selectionStart = selectionEnd = -1; status = null;
            if (Send != null) Send(text);
        }

        private void ScrollWheel(float delta)
        {
            if (delta == 0 || float.IsNaN(delta) || float.IsInfinity(delta)) return;
            bool application = screen.Alternate;
            // Alternate-screen apps own their history. Page keys let the existing app
            // scroll it without requiring a new host or changing its terminal protocol.
            float amount = application ? Mathf.Clamp(delta, -1, 1) : delta * 3;
            if (application != scrollingApplication || (scrollRemainder != 0 && Mathf.Sign(amount) != Mathf.Sign(scrollRemainder)))
                scrollRemainder = 0;
            scrollingApplication = application;
            scrollRemainder += amount;
            int steps = (int)scrollRemainder;
            scrollRemainder -= steps;
            if (steps == 0) return;
            selectionStart = selectionEnd = -1;
            if (application)
                SendText(TerminalInput.Encode(steps < 0 ? TerminalKey.PageUp : TerminalKey.PageDown, '\0', false, false, false, screen.ApplicationCursor));
            else
                scrollBack = Mathf.Clamp(scrollBack - steps, 0, screen.HistoryCount);
        }

        private bool ScrollHistoryKey(Event key)
        {
            if (screen.Alternate || !key.shift || key.control || key.alt || key.command) return false;
            int page = Mathf.Max(1, screen.Rows - 1);
            switch (key.keyCode)
            {
                case KeyCode.PageUp: scrollBack = Mathf.Min(screen.HistoryCount, scrollBack + page); break;
                case KeyCode.PageDown: scrollBack = Mathf.Max(0, scrollBack - page); break;
                case KeyCode.Home: scrollBack = screen.HistoryCount; break;
                case KeyCode.End: scrollBack = 0; break;
                default: return false;
            }
            scrollRemainder = 0;
            selectionStart = selectionEnd = -1;
            return true;
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
        private bool HasSelection { get { return selectionStart >= 0 && selectionStart != selectionEnd; } }
        private void SnapshotSelection()
        {
            // Keep the visible text stable while the app redraws or moves its cursor.
            // Copy must return the text the user selected, including in scrollback.
            selectionLines = new ScreenCell[screen.Rows][];
            for (int y = 0; y < screen.Rows; y++)
                selectionLines[y] = (ScreenCell[])screen.GetLine(y, scrollBack).Clone();
        }
        private ScreenCell[] SelectionLine(int y)
        {
            return (selecting || HasSelection) && selectionLines != null && y < selectionLines.Length
                ? selectionLines[y] : screen.GetLine(y, scrollBack);
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
                ScreenCell[] line = SelectionLine(y);
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
