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
        // IMGUI control IDs are positional: when the controls drawn before this view change (the toolbar
        // switches layout at a width threshold), the input control gets a new ID while
        // GUIUtility.keyboardControl keeps the old one, and every key was dropped until Stop.
        // The view therefore owns focus by name, not by ID.
        private bool ownsFocus = true;
        private int scrollBack;
        private float scrollRemainder;
        private bool scrollingApplication;
        private int selectionStart = -1, selectionEnd = -1;
        private ScreenCell[][] selectionLines;
        private bool selecting;
        private string status;
        public Action<string> Send;
        // Set from the launch command (TerminalInput.UsesAltEnterForNewline): Shift+Enter sends ESC CR.
        public bool AltEnterNewline;
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
            lastWindowFocused = windowFocused;
            // Sample before this event's TextField and focus recovery run: both can change focus, which
            // would hide a lost-focus state from the diagnostics and the input trace.
            preKeyboardControl = GUIUtility.keyboardControl;
            preOwnsFocus = ownsFocus;
            focusRecovered = false;
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 1 && rect.Contains(current.mousePosition))
                diagnosticsAtClick = BuildDiagnostics();
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
            if (Event.current.type != EventType.Layout) KeepKeyboardFocus(windowFocused);
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

        // Called after the input control is drawn in this event. If focus is on the old ID of this
        // control (the ID shifted) or on nothing, take it back by name. If another control took focus
        // (a different, non-zero ID), the user moved focus on purpose: give up ownership until the
        // next click inside the view.
        private void KeepKeyboardFocus(bool windowFocused)
        {
            if (GUI.GetNameOfFocusedControl() == "InteractiveTerminal") { ownsFocus = true; return; }
            int focused = GUIUtility.keyboardControl;
            // Before the first grab (control == 0) focus may still be on the primary input field.
            if (control != 0 && focused != 0 && focused != control) ownsFocus = false;
            if (ownsFocus == true && (windowFocused == true || control == 0))
            {
                GUI.FocusControl("InteractiveTerminal");
                focusRecovered = control != 0;
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
        // Recent input decisions for Tools > Unity Terminal > Capture Diagnostics: for each key, click,
        // command or IME commit, what arrived, the focus/IME state, and what (if anything) reached the app.
        private struct InputTrace
        {
            public DateTime Time;
            public EventType Type;
            public KeyCode Key;
            public char Character;
            public EventModifiers Modifiers;
            public int KeyboardControl, Control;
            public bool OwnsFocus, FocusRecovered, Composing, Handled;
            public string Committed, Sent;
        }
        private const int InputTraceLimit = 256;
        private readonly InputTrace[] inputTrace = new InputTrace[InputTraceLimit];
        private int inputTraceNext, inputTraceCount;
        private bool tracingInput;
        // Focus state at the start of Draw (before focus recovery), and whether this event recovered focus.
        private int preKeyboardControl;
        private bool preOwnsFocus, focusRecovered;
        private string sentThisEvent;

        public bool HandleInput()
        {
            Event source = inputEvent ?? Event.current;
            bool traced = source.type == EventType.KeyDown || source.type == EventType.MouseDown ||
                source.type == EventType.ExecuteCommand || string.IsNullOrEmpty(committedText) == false;
            if (traced == false) return HandleInputCore();
            var entry = new InputTrace
            {
                Time = DateTime.Now, Type = source.type, Key = source.keyCode, Character = source.character, Modifiers = source.modifiers,
                KeyboardControl = preKeyboardControl, Control = control, OwnsFocus = preOwnsFocus, FocusRecovered = focusRecovered,
                Composing = composing, Committed = committedText
            };
            sentThisEvent = null;
            tracingInput = true;
            try { entry.Handled = HandleInputCore(); }
            finally { tracingInput = false; }
            entry.Sent = sentThisEvent;
            inputTrace[inputTraceNext] = entry;
            inputTraceNext = (inputTraceNext + 1) % InputTraceLimit;
            if (inputTraceCount < InputTraceLimit) inputTraceCount++;
            return entry.Handled;
        }

        internal string DumpInputTrace()
        {
            var text = new StringBuilder();
            int start = (inputTraceNext - inputTraceCount + InputTraceLimit) % InputTraceLimit;
            for (int i = 0; i < inputTraceCount; i++)
            {
                InputTrace entry = inputTrace[(start + i) % InputTraceLimit];
                text.Append(entry.Time.ToString("HH:mm:ss.fff")).Append(' ').Append(entry.Type)
                    .Append(" key=").Append(entry.Key).Append(" char=").Append((int)entry.Character).Append(" mods=").Append(entry.Modifiers)
                    .Append(" kb=").Append(entry.KeyboardControl).Append(" control=").Append(entry.Control).Append(" owns=").Append(entry.OwnsFocus).Append(" recovered=").Append(entry.FocusRecovered)
                    .Append(" composing=").Append(entry.Composing).Append(" committed=[").Append(TerminalDiagnostics.Escape(entry.Committed))
                    .Append("] handled=").Append(entry.Handled).Append(" sent=").Append(entry.Sent == null ? "none" : "[" + TerminalDiagnostics.Escape(entry.Sent) + "]")
                    .AppendLine();
            }
            return text.ToString();
        }

        // The view's current lines (or the selection snapshot shown instead), then the scrollback.
        internal string DumpScreen()
        {
            var text = new StringBuilder();
            text.AppendLine("visible rows (scrollBack=" + scrollBack + ", showing " + (selectionLines != null && (selecting || HasSelection) ? "selection snapshot" : "live screen") + "):");
            for (int y = 0; y < screen.Rows; y++) text.Append(y.ToString("00")).Append("| ").AppendLine(TerminalScreen.LineText(SelectionLine(y)).TrimEnd());
            text.AppendLine("live screen rows:");
            for (int y = 0; y < screen.Rows; y++) text.Append(y.ToString("00")).Append("| ").AppendLine(TerminalScreen.LineText(screen.GetLine(y)).TrimEnd());
            text.AppendLine("scrollback (" + screen.HistoryCount + " lines, oldest first):");
            for (int i = screen.HistoryCount; i > 0; i--) text.AppendLine(TerminalScreen.LineText(screen.GetLine(0, i)).TrimEnd());
            return text.ToString();
        }

        private bool HandleInputCore()
        {
            Event e = Event.current;
            // The IME TextField may consume mouse events before this handler runs.
            Event mouse = inputEvent ?? e;
            if (mouse.type == EventType.MouseDown && bounds.Contains(mouse.mousePosition))
            {
                // Focus by name: the saved ID may be stale after the controls before the view changed.
                ownsFocus = true;
                GUI.FocusControl("InteractiveTerminal");
                if (GUI.GetNameOfFocusedControl() == "InteractiveTerminal") control = GUIUtility.keyboardControl;
                if (mouse.button == 1)
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Copy"), false, Copy);
                    menu.AddItem(new GUIContent("Paste"), false, Paste);
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Copy diagnostics"), false, () => { GUIUtility.systemCopyBuffer = diagnosticsAtClick; });
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
                SendText(TerminalInput.EncodeShiftEnter(AltEnterNewline, screen.ApplicationCursor));
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

        // Diagnostics for an intermittent "keys do nothing" state: right-click > Copy diagnostics copies
        // the state captured at the click (focus, IME composition, selection/scroll, last input, session).
        public Func<string> SessionDiagnostics;
        private string diagnosticsAtClick = "";
        private DateTime lastSendUtc;
        private int sendCount;
        private bool lastWindowFocused;

        internal string BuildDiagnostics()
        {
            var text = new StringBuilder();
            text.AppendLine("Unity Terminal interactive diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("focus ownsFocus=" + ownsFocus + " control=" + control + " keyboardControl=" + GUIUtility.keyboardControl +
                " focusedName=[" + GUI.GetNameOfFocusedControl() + "] windowFocused=" + lastWindowFocused);
            text.AppendLine("ime composing=" + composing + " compositionString=[" + Input.compositionString + "] mode=" + Input.imeCompositionMode +
                " " + composition.Describe());
            text.AppendLine("view scrollBack=" + scrollBack + "/" + screen.HistoryCount + " selection=" + selectionStart + ".." + selectionEnd +
                " selecting=" + selecting + " snapshot=" + (selectionLines != null) + " status=[" + (status ?? "") + "] altEnterNewline=" + AltEnterNewline);
            text.AppendLine("screen " + screen.Columns + "x" + screen.Rows + " version=" + screen.Version + " alternate=" + screen.Alternate +
                " cursor=" + screen.CursorX + "," + screen.CursorY + " visible=" + screen.CursorVisible + " appCursor=" + screen.ApplicationCursor +
                " title=[" + screen.Title + "]");
            text.AppendLine("input sent=" + sendCount + " last=" + (sendCount == 0 ? "never" : (DateTime.UtcNow - lastSendUtc).TotalMilliseconds.ToString("0") + "ms ago"));
            if (SessionDiagnostics != null)
            {
                try { text.AppendLine(SessionDiagnostics()); }
                catch (Exception ex) { text.AppendLine("session diagnostics failed: " + ex.Message); }
            }
            return text.ToString();
        }

        private void SendText(string text)
        {
            sendCount++;
            lastSendUtc = DateTime.UtcNow;
            if (tracingInput == true) sentThisEvent = sentThisEvent == null ? text : sentThisEvent + text;
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
