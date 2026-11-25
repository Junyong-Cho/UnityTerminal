using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityTerminal
{
    internal struct ScreenStyle : IEquatable<ScreenStyle>
    {
        public int Foreground, Background;
        public bool Bold, Italic, Underline, Inverse;
        public static ScreenStyle Default { get { return new ScreenStyle { Foreground = -1, Background = -1 }; } }
        public bool Equals(ScreenStyle other)
        {
            return Foreground == other.Foreground && Background == other.Background && Bold == other.Bold &&
                Italic == other.Italic && Underline == other.Underline && Inverse == other.Inverse;
        }
    }

    internal struct ScreenCell
    {
        public string Text;
        public ScreenStyle Style;
        public int Width; // Zero marks the continuation of a wide glyph.
    }

    // A fixed-cell VT screen, independent of Unity. UI and parsing run on the editor thread.
    internal sealed class TerminalScreen
    {
        private static readonly int[] BaseColors = { 0x333333, 0xf44747, 0x608b4e, 0xdcdcaa, 0x569cd6, 0xc586c0, 0x4fc1ff, 0xd4d4d4,
            0x808080, 0xf44747, 0xb5cea8, 0xdcdcaa, 0x569cd6, 0xc586c0, 0x9cdcfe, 0xffffff };
        private static readonly int[] ColorLevels = { 0, 95, 135, 175, 215, 255 };
        private sealed class Page
        {
            public ScreenCell[][] Lines;
            public int X, Y, SavedX, SavedY, Top, Bottom;
            public bool WrapPending;
            public ScreenStyle SavedStyle = ScreenStyle.Default;
        }
        private enum ParseState { Text, Escape, Csi, Osc, OscEscape, String, StringEscape, Charset }
        private readonly List<ScreenCell[]> history = new List<ScreenCell[]>();
        private readonly StringBuilder sequence = new StringBuilder();
        private Page primary;
        private Page alternate;
        private Page page;
        private ScreenStyle style = ScreenStyle.Default;
        private ParseState state;
        private char highSurrogate;
        private bool originMode;
        private bool autoWrap = true;
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int Version { get; private set; }
        public int CursorX { get { return page.X; } }
        public int CursorY { get { return page.Y; } }
        public bool CursorVisible { get; private set; }
        public int CursorShape { get; private set; }
        public bool ApplicationCursor { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool Alternate { get { return page == alternate; } }
        public int HistoryCount { get { return Alternate ? 0 : history.Count; } }
        public string Title { get; private set; }
        public string LastAlternateText { get; private set; }
        public Action<string> Reply { get; set; }

        public TerminalScreen(int columns, int rows)
        {
            ValidateSize(columns, rows);
            Columns = columns;
            Rows = rows;
            primary = NewPage();
            page = primary;
            CursorVisible = true;
        }

        private static void ValidateSize(int columns, int rows)
        {
            if (columns < 2 || columns > 500 || rows < 2 || rows > 300) throw new ArgumentOutOfRangeException("Terminal dimensions");
        }

        private ScreenCell Blank() { return new ScreenCell { Text = " ", Width = 1, Style = style }; }
        private ScreenCell[] NewLine()
        {
            var line = new ScreenCell[Columns];
            for (int x = 0; x < line.Length; x++) line[x] = Blank();
            return line;
        }
        private Page NewPage()
        {
            var result = new Page { Lines = new ScreenCell[Rows][], Bottom = Rows - 1 };
            for (int y = 0; y < Rows; y++) result.Lines[y] = NewLine();
            return result;
        }

        public ScreenCell[] GetLine(int visibleRow, int scrollBack = 0)
        {
            int index = HistoryCount + visibleRow - Clamp(scrollBack, 0, HistoryCount);
            if (index < HistoryCount) return history[Math.Max(0, index)];
            return page.Lines[Clamp(index - HistoryCount, 0, Rows - 1)];
        }

        public void Feed(string text)
        {
            foreach (char c in text)
            {
                switch (state)
                {
                    case ParseState.Text:
                        if (c == '\x1b') { highSurrogate = '\0'; state = ParseState.Escape; }
                        else if (c < ' ' || c == '\x7f') Control(c);
                        else if (char.IsHighSurrogate(c)) highSurrogate = c;
                        else if (char.IsLowSurrogate(c) && highSurrogate != '\0')
                        { WriteGlyph(new string(new[] { highSurrogate, c }), char.ConvertToUtf32(highSurrogate, c)); highSurrogate = '\0'; }
                        else { highSurrogate = '\0'; WriteGlyph(c.ToString(), c); }
                        break;
                    case ParseState.Escape:
                        state = ParseState.Text;
                        if (c == '[') { sequence.Length = 0; state = ParseState.Csi; }
                        else if (c == ']') { sequence.Length = 0; state = ParseState.Osc; }
                        else if (c == 'P' || c == '_' || c == '^') state = ParseState.String;
                        else if (c == '(' || c == ')' || c == '*' || c == '+') state = ParseState.Charset;
                        else if (c == '7') SaveCursor();
                        else if (c == '8') RestoreCursor();
                        else if (c == 'D') LineFeed();
                        else if (c == 'E') { page.X = 0; LineFeed(); }
                        else if (c == 'M') { if (page.Y == page.Top) ScrollDown(1); else page.Y = Math.Max(0, page.Y - 1); }
                        else if (c == 'c') Reset();
                        else if (c == '\x1b') state = ParseState.Escape;
                        break;
                    case ParseState.Csi:
                        if (c >= '@' && c <= '~') { ExecuteCsi(c, sequence.ToString()); state = ParseState.Text; }
                        else if (c == '\x1b') state = ParseState.Escape;
                        else if (sequence.Length < 256) sequence.Append(c);
                        else state = ParseState.Text;
                        break;
                    case ParseState.Osc:
                        if (c == '\a') { ExecuteOsc(); state = ParseState.Text; }
                        else if (c == '\x1b') state = ParseState.OscEscape;
                        else if (sequence.Length < 4096) sequence.Append(c);
                        break;
                    case ParseState.OscEscape:
                        if (c == '\\' || c == '\a') { ExecuteOsc(); state = ParseState.Text; }
                        else state = ParseState.Osc;
                        break;
                    case ParseState.String:
                        if (c == '\x1b') state = ParseState.StringEscape;
                        break;
                    case ParseState.StringEscape:
                        state = c == '\\' ? ParseState.Text : ParseState.String;
                        break;
                    case ParseState.Charset: state = ParseState.Text; break;
                }
            }
            Version++;
        }

        private void Control(char c)
        {
            if (c == '\r') { page.X = 0; page.WrapPending = false; }
            else if (c == '\n' || c == '\v' || c == '\f') LineFeed();
            else if (c == '\b') { page.X = Math.Max(0, page.X - 1); page.WrapPending = false; }
            else if (c == '\t') { page.X = Math.Min(Columns - 1, (page.X / 8 + 1) * 8); page.WrapPending = false; }
        }

        private void WriteGlyph(string glyph, int codepoint)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(glyph, 0);
            if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark || codepoint == 0x200d || codepoint == 0xfe0f)
            {
                int x = page.WrapPending ? page.X : page.X - 1;
                if (x >= 0)
                {
                    if (page.Lines[page.Y][x].Width == 0) x--;
                    if (x >= 0) page.Lines[page.Y][x].Text += glyph;
                }
                return;
            }
            int width = IsWide(codepoint) ? 2 : 1;
            if (page.WrapPending || (width == 2 && page.X == Columns - 1 && autoWrap))
            { page.X = 0; LineFeed(); }
            if (width == 2 && page.X == Columns - 1) width = 1;
            UnlinkGlyph(page.Lines[page.Y], page.X);
            if (width == 2) UnlinkGlyph(page.Lines[page.Y], page.X + 1);
            page.Lines[page.Y][page.X] = new ScreenCell { Text = glyph, Style = style, Width = width };
            if (width == 2) page.Lines[page.Y][page.X + 1] = new ScreenCell { Text = "", Style = style, Width = 0 };
            if (page.X + width >= Columns) { page.X = Columns - 1; page.WrapPending = autoWrap; }
            else page.X += width;
        }

        private void UnlinkGlyph(ScreenCell[] line, int x)
        {
            if (line[x].Width == 0 && x > 0) line[x - 1] = Blank();
            if (line[x].Width == 2 && x + 1 < Columns) line[x + 1] = Blank();
        }

        private static bool IsWide(int c)
        {
            return c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a ||
                (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f) || (c >= 0xac00 && c <= 0xd7a3) ||
                (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe10 && c <= 0xfe19) ||
                (c >= 0xfe30 && c <= 0xfe6f) || (c >= 0xff01 && c <= 0xff60) ||
                (c >= 0xffe0 && c <= 0xffe6) || (c >= 0x1f300 && c <= 0x1faff) || c >= 0x20000);
        }

        private void LineFeed()
        {
            page.WrapPending = false;
            if (page.Y == page.Bottom) ScrollUp(1);
            else page.Y = Math.Min(Rows - 1, page.Y + 1);
        }

        private void ScrollUp(int count)
        {
            count = Clamp(count, 1, page.Bottom - page.Top + 1);
            for (int i = 0; i < count; i++)
            {
                if (!Alternate && page.Top == 0 && page.Bottom == Rows - 1)
                {
                    history.Add(page.Lines[0]);
                    if (history.Count > 512) history.RemoveAt(0);
                }
                Array.Copy(page.Lines, page.Top + 1, page.Lines, page.Top, page.Bottom - page.Top);
                page.Lines[page.Bottom] = NewLine();
            }
        }

        private void ScrollDown(int count)
        {
            for (int i = 0; i < Clamp(count, 1, page.Bottom - page.Top + 1); i++)
            {
                Array.Copy(page.Lines, page.Top, page.Lines, page.Top + 1, page.Bottom - page.Top);
                page.Lines[page.Top] = NewLine();
            }
        }

        private void ExecuteCsi(char command, string raw)
        {
            bool dec = raw.StartsWith("?", StringComparison.Ordinal);
            bool secondary = raw.StartsWith(">", StringComparison.Ordinal);
            string parameters = raw.TrimStart('?', '>', '<', '=').TrimEnd(' ', '$', '!');
            string[] parts = parameters.Split(';');
            var p = new int[parts.Length];
            for (int i = 0; i < p.Length; i++) int.TryParse(parts[i], out p[i]);
            int n = Math.Max(1, p[0]);
            switch (command)
            {
                case 'A': page.Y = Math.Max(originMode ? page.Top : 0, page.Y - n); break;
                case 'B': case 'e': page.Y = Math.Min(originMode ? page.Bottom : Rows - 1, page.Y + n); break;
                case 'C': case 'a': page.X = Math.Min(Columns - 1, page.X + n); break;
                case 'D': page.X = Math.Max(0, page.X - n); break;
                case 'E': page.X = 0; page.Y = Math.Min(Rows - 1, page.Y + n); break;
                case 'F': page.X = 0; page.Y = Math.Max(0, page.Y - n); break;
                case 'G': case '`': page.X = Clamp(n - 1, 0, Columns - 1); break;
                case 'd': page.Y = Clamp(n - 1 + (originMode ? page.Top : 0), 0, originMode ? page.Bottom : Rows - 1); break;
                case 'H': case 'f':
                    page.Y = Clamp(n - 1 + (originMode ? page.Top : 0), 0, originMode ? page.Bottom : Rows - 1);
                    page.X = Clamp((p.Length > 1 ? Math.Max(1, p[1]) : 1) - 1, 0, Columns - 1); break;
                case 'J': EraseDisplay(p[0]); break;
                case 'K': EraseLine(p[0]); break;
                case 'm': ApplyStyle(p); break;
                case 's': SaveCursor(); break;
                case 'u': if (dec) Respond("\x1b[?0u"); else if (!secondary) RestoreCursor(); break;
                case 'r':
                    int top = Clamp(n - 1, 0, Rows - 1), bottom = Clamp((p.Length > 1 && p[1] > 0 ? p[1] : Rows) - 1, 0, Rows - 1);
                    if (top < bottom) { page.Top = top; page.Bottom = bottom; page.X = 0; page.Y = originMode ? top : 0; }
                    break;
                case 'S': ScrollUp(n); break;
                case 'T': ScrollDown(n); break;
                case 'L': case 'M':
                    if (page.Y >= page.Top && page.Y <= page.Bottom)
                    {
                        int oldTop = page.Top; page.Top = page.Y;
                        if (command == 'L') ScrollDown(n); else ScrollUp(n);
                        page.Top = oldTop;
                    }
                    break;
                case '@': case 'P':
                    n = Math.Min(n, Columns - page.X);
                    ScreenCell[] line = page.Lines[page.Y];
                    if (command == '@')
                    { Array.Copy(line, page.X, line, page.X + n, Columns - page.X - n); Fill(line, page.X, page.X + n); }
                    else
                    { Array.Copy(line, page.X + n, line, page.X, Columns - page.X - n); Fill(line, Columns - n, Columns); }
                    NormalizeLine(line);
                    break;
                case 'X': Fill(page.Lines[page.Y], page.X, Math.Min(Columns, page.X + n)); break;
                case 'h': case 'l': if (dec) foreach (int mode in p) SetMode(mode, command == 'h'); break;
                case 'n':
                    if (p[0] == 5) Respond("\x1b[0n");
                    else if (p[0] == 6) Respond("\x1b[" + (dec ? "?" : "") + (page.Y + 1) + ";" + (page.X + 1) + "R");
                    break;
                case 'c': Respond(secondary ? "\x1b[>0;0;0c" : "\x1b[?1;2c"); break;
                case 'p': if (raw.IndexOf('$') >= 0) Respond("\x1b[?" + p[0] + ";2$y"); break;
                case 'q': if (raw.IndexOf(' ') >= 0) CursorShape = p[0]; break;
                case 't': if (p[0] == 18) Respond("\x1b[8;" + Rows + ";" + Columns + "t"); break;
            }
            if (command != 'm' && command != 'n' && command != 'c') page.WrapPending = false;
        }

        private void Fill(ScreenCell[] line, int start, int end)
        {
            for (int x = start; x < end; x++) { UnlinkGlyph(line, x); line[x] = Blank(); }
        }
        private void NormalizeLine(ScreenCell[] line)
        {
            for (int x = 0; x < line.Length; x++)
                if ((line[x].Width == 0 && (x == 0 || line[x - 1].Width != 2)) ||
                    (line[x].Width == 2 && (x + 1 == line.Length || line[x + 1].Width != 0))) line[x] = Blank();
        }
        private void EraseLine(int mode)
        {
            if (mode == 0) Fill(page.Lines[page.Y], page.X, Columns);
            else if (mode == 1) Fill(page.Lines[page.Y], 0, page.X + 1);
            else if (mode == 2) Fill(page.Lines[page.Y], 0, Columns);
        }
        private void EraseDisplay(int mode)
        {
            if (mode == 3) { history.Clear(); return; }
            if (mode == 0 || mode == 1) EraseLine(mode);
            for (int y = 0; y < Rows; y++)
                if (mode == 2 || (mode == 0 && y > page.Y) || (mode == 1 && y < page.Y)) Fill(page.Lines[y], 0, Columns);
        }
        private void SaveCursor() { page.SavedX = page.X; page.SavedY = page.Y; page.SavedStyle = style; }
        private void RestoreCursor() { page.X = Clamp(page.SavedX, 0, Columns - 1); page.Y = Clamp(page.SavedY, 0, Rows - 1); style = page.SavedStyle; }

        private void SetMode(int mode, bool enabled)
        {
            if (mode == 1) ApplicationCursor = enabled;
            else if (mode == 6) { originMode = enabled; page.X = 0; page.Y = enabled ? page.Top : 0; }
            else if (mode == 7) autoWrap = enabled;
            else if (mode == 25) CursorVisible = enabled;
            else if (mode == 2004) BracketedPaste = enabled;
            else if (mode == 47 || mode == 1047 || mode == 1049)
            {
                if (enabled && !Alternate)
                {
                    if (mode == 1049) SaveCursor();
                    alternate = NewPage(); page = alternate;
                }
                else if (!enabled && Alternate)
                {
                    LastAlternateText = PageText(alternate);
                    page = primary;
                    if (mode == 1049) RestoreCursor();
                }
            }
        }

        private void ApplyStyle(int[] p)
        {
            for (int i = 0; i < p.Length; i++)
            {
                int code = p[i];
                if (code == 0) style = ScreenStyle.Default;
                else if (code == 1) style.Bold = true;
                else if (code == 3) style.Italic = true;
                else if (code == 4) style.Underline = true;
                else if (code == 7) style.Inverse = true;
                else if (code == 22) style.Bold = false;
                else if (code == 23) style.Italic = false;
                else if (code == 24) style.Underline = false;
                else if (code == 27) style.Inverse = false;
                else if (code == 39) style.Foreground = -1;
                else if (code == 49) style.Background = -1;
                else if (code >= 30 && code <= 37) style.Foreground = Palette(code - 30);
                else if (code >= 40 && code <= 47) style.Background = Palette(code - 40);
                else if (code >= 90 && code <= 97) style.Foreground = Palette(code - 90 + 8);
                else if (code >= 100 && code <= 107) style.Background = Palette(code - 100 + 8);
                else if (code == 38 || code == 48)
                {
                    if (++i >= p.Length) break;
                    int color = -1;
                    if (p[i] == 5 && i + 1 < p.Length) color = Palette(Clamp(p[++i], 0, 255));
                    else if (p[i] == 2 && i + 3 < p.Length)
                    { color = (Clamp(p[i + 1], 0, 255) << 16) | (Clamp(p[i + 2], 0, 255) << 8) | Clamp(p[i + 3], 0, 255); i += 3; }
                    if (color >= 0) { if (code == 38) style.Foreground = color; else style.Background = color; }
                }
            }
        }

        internal static int Palette(int index)
        {
            if (index < 16) return BaseColors[index];
            if (index >= 232) { int gray = 8 + (index - 232) * 10; return (gray << 16) | (gray << 8) | gray; }
            index -= 16;
            return (ColorLevels[index / 36] << 16) | (ColorLevels[index / 6 % 6] << 8) | ColorLevels[index % 6];
        }

        private void ExecuteOsc()
        {
            string[] p = sequence.ToString().Split(';');
            if (p.Length < 2) return;
            if (p[0] == "0" || p[0] == "2") Title = string.Join(";", p, 1, p.Length - 1);
            else if ((p[0] == "10" || p[0] == "11") && p[1] == "?")
                Respond("\x1b]" + p[0] + (p[0] == "10" ? ";rgb:d4d4/d4d4/d4d4\x1b\\" : ";rgb:1e1e/1e1e/1e1e\x1b\\"));
            else if (p[0] == "4" && p.Length >= 3 && p[2] == "?")
            {
                int index;
                if (int.TryParse(p[1], out index) && index >= 0 && index <= 255)
                {
                    int color = Palette(index);
                    Respond(string.Format("\x1b]4;{0};rgb:{1:x4}/{2:x4}/{3:x4}\x1b\\", index,
                        ((color >> 16) & 255) * 257, ((color >> 8) & 255) * 257, (color & 255) * 257));
                }
            }
        }
        private void Respond(string response) { if (Reply != null) Reply(response); }

        public void Resize(int columns, int rows)
        {
            ValidateSize(columns, rows);
            if (Columns == columns && Rows == rows) return;
            Columns = columns; Rows = rows;
            ResizePage(primary);
            if (alternate != null) ResizePage(alternate);
            Version++;
        }
        private void ResizePage(Page target)
        {
            var lines = new ScreenCell[Rows][];
            for (int y = 0; y < Rows; y++)
            {
                lines[y] = NewLine();
                if (y < target.Lines.Length) Array.Copy(target.Lines[y], lines[y], Math.Min(Columns, target.Lines[y].Length));
                NormalizeLine(lines[y]);
            }
            target.Lines = lines; target.X = Clamp(target.X, 0, Columns - 1); target.Y = Clamp(target.Y, 0, Rows - 1);
            target.Top = 0; target.Bottom = Rows - 1; target.WrapPending = false;
        }
        public void Reset()
        {
            style = ScreenStyle.Default; primary = NewPage(); page = primary; alternate = null; history.Clear();
            CursorVisible = true; ApplicationCursor = false; BracketedPaste = false; originMode = false; autoWrap = true;
            state = ParseState.Text; highSurrogate = '\0'; sequence.Length = 0; LastAlternateText = null; Version++;
        }
        public string GetText() { return PageText(page); }

        internal void WriteDisplayState(BinaryWriter writer)
        {
            writer.Write(Columns); writer.Write(Rows); writer.Write(CursorX); writer.Write(CursorY);
            writer.Write(CursorVisible); writer.Write(CursorShape); writer.Write(ApplicationCursor); writer.Write(BracketedPaste); writer.Write(Alternate);
            writer.Write(Title ?? ""); writer.Write(LastAlternateText ?? "");
            writer.Write(HistoryCount);
            if (!Alternate) foreach (ScreenCell[] line in history) WriteLine(writer, line);
            foreach (ScreenCell[] line in page.Lines) WriteLine(writer, line);
        }

        private static void WriteLine(BinaryWriter writer, ScreenCell[] line)
        {
            writer.Write(line.Length);
            for (int x = 0; x < line.Length; )
            {
                ScreenCell cell = line[x];
                int count = 1;
                while (x + count < line.Length && cell.Width == line[x + count].Width && cell.Text == line[x + count].Text && cell.Style.Equals(line[x + count].Style)) count++;
                writer.Write(count); writer.Write(cell.Text ?? " "); writer.Write(cell.Width);
                writer.Write(cell.Style.Foreground); writer.Write(cell.Style.Background);
                writer.Write(cell.Style.Bold); writer.Write(cell.Style.Italic); writer.Write(cell.Style.Underline); writer.Write(cell.Style.Inverse);
                x += count;
            }
        }

        internal void ReadDisplayState(BinaryReader reader)
        {
            int columns = reader.ReadInt32(), rows = reader.ReadInt32();
            ValidateSize(columns, rows); Columns = columns; Rows = rows;
            int x = reader.ReadInt32(), y = reader.ReadInt32();
            if (x < 0 || x >= columns || y < 0 || y >= rows) throw new InvalidDataException("Invalid terminal cursor.");
            CursorVisible = reader.ReadBoolean(); CursorShape = reader.ReadInt32();
            ApplicationCursor = reader.ReadBoolean(); BracketedPaste = reader.ReadBoolean(); bool isAlternate = reader.ReadBoolean();
            Title = reader.ReadString(); LastAlternateText = reader.ReadString();
            int count = reader.ReadInt32();
            if (count < 0 || count > 512 || (isAlternate && count != 0)) throw new InvalidDataException("Invalid terminal history.");
            history.Clear();
            for (int i = 0; i < count; i++) history.Add(ReadLine(reader));
            primary = NewPage(); alternate = isAlternate ? NewPage() : null; page = isAlternate ? alternate : primary;
            for (int i = 0; i < rows; i++) page.Lines[i] = ReadLine(reader);
            page.X = x; page.Y = y; Version++;
        }

        private static ScreenCell[] ReadLine(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 2 || length > 500) throw new InvalidDataException("Invalid terminal row width.");
            var line = new ScreenCell[length];
            for (int x = 0; x < length; )
            {
                int count = reader.ReadInt32();
                if (count < 1 || count > length - x) throw new InvalidDataException("Invalid terminal cell run.");
                var cell = new ScreenCell { Text = reader.ReadString(), Width = reader.ReadInt32(), Style = new ScreenStyle {
                    Foreground = reader.ReadInt32(), Background = reader.ReadInt32(), Bold = reader.ReadBoolean(), Italic = reader.ReadBoolean(),
                    Underline = reader.ReadBoolean(), Inverse = reader.ReadBoolean() } };
                if (cell.Width < 0 || cell.Width > 2) throw new InvalidDataException("Invalid glyph width.");
                for (int i = 0; i < count; i++) line[x++] = cell;
            }
            return line;
        }
        public string GetTranscript()
        {
            var result = new StringBuilder();
            foreach (ScreenCell[] line in history) result.AppendLine(LineText(line));
            result.Append(PageText(page));
            if (!Alternate && !string.IsNullOrEmpty(LastAlternateText)) result.AppendLine().Append(LastAlternateText);
            return result.ToString().TrimEnd();
        }
        private static string PageText(Page target)
        {
            var result = new StringBuilder();
            foreach (ScreenCell[] line in target.Lines) result.AppendLine(LineText(line));
            return result.ToString().TrimEnd();
        }
        internal static string LineText(ScreenCell[] line)
        {
            var result = new StringBuilder();
            foreach (ScreenCell cell in line) if (cell.Width != 0) result.Append(cell.Text ?? " ");
            return result.ToString().TrimEnd();
        }
        private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(value, max)); }
    }
}
