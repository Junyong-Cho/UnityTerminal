using System;
using System.Text;

namespace UnityTerminal
{
    // Each output stream has its own parser, so partial stderr/stdout codes cannot mix.
    internal sealed class AnsiParser
    {
        private enum State { Text, Escape, Csi, Osc, OscEscape }
        private static readonly string[] Colors = {
            "#333333", "#f44747", "#608b4e", "#dcdcaa", "#569cd6", "#c586c0", "#4fc1ff", "#d4d4d4",
            "#808080", "#f44747", "#b5cea8", "#dcdcaa", "#569cd6", "#c586c0", "#9cdcfe", "#ffffff"
        };
        private readonly TerminalBuffer buffer;
        private readonly bool isError;
        private readonly StringBuilder sequence = new StringBuilder();
        private State state;
        private TerminalStyle style;

        public AnsiParser(TerminalBuffer buffer, bool isError = false)
        {
            this.buffer = buffer;
            this.isError = isError;
        }

        public void Reset()
        {
            state = State.Text;
            sequence.Length = 0;
            style = default(TerminalStyle);
        }

        public void Feed(string text)
        {
            var plain = new StringBuilder();
            foreach (char c in text)
            {
                switch (state)
                {
                    case State.Text:
                        if (c == '\x1b')
                        {
                            Flush(plain);
                            state = State.Escape;
                        }
                        else plain.Append(c);
                        break;
                    case State.Escape:
                        if (c == '[') { sequence.Length = 0; state = State.Csi; }
                        else if (c == ']') state = State.Osc;
                        else state = c == '\x1b' ? State.Escape : State.Text;
                        break;
                    case State.Csi:
                        if (c >= '@' && c <= '~')
                        {
                            if (c == 'm') ApplyStyles(sequence.ToString());
                            else if (c == 'J' && (sequence.ToString() == "2" || sequence.ToString() == "3")) buffer.Clear();
                            state = State.Text;
                        }
                        else if (c == '\x1b') state = State.Escape;
                        else if (sequence.Length < 256) sequence.Append(c);
                        else { sequence.Length = 0; state = State.Text; }
                        break;
                    case State.Osc:
                        if (c == '\a') state = State.Text;
                        else if (c == '\x1b') state = State.OscEscape;
                        break;
                    case State.OscEscape:
                        state = c == '\\' || c == '\a' ? State.Text : (c == '\x1b' ? State.OscEscape : State.Osc);
                        break;
                }
            }
            Flush(plain);
        }

        private void Flush(StringBuilder plain)
        {
            if (plain.Length == 0) return;
            TerminalStyle effective = style;
            if (isError && effective.Color == null) effective.Color = Colors[1];
            buffer.Append(plain.ToString(), effective);
            plain.Length = 0;
        }

        private void ApplyStyles(string parameters)
        {
            string[] parts = parameters.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                int code;
                if (parts[i].Length == 0) code = 0;
                else if (!int.TryParse(parts[i], out code)) continue;
                if (code == 0) style = default(TerminalStyle);
                else if (code == 1) style.Bold = true;
                else if (code == 3) style.Italic = true;
                else if (code == 22) style.Bold = false;
                else if (code == 23) style.Italic = false;
                else if (code == 39) style.Color = null;
                else if (code >= 30 && code <= 37) style.Color = Colors[code - 30];
                else if (code >= 90 && code <= 97) style.Color = Colors[code - 90 + 8];
                else if (code == 38 || code == 48)
                {
                    // Consume extended-color payloads as a unit, including ignored backgrounds.
                    int mode;
                    if (++i >= parts.Length || !int.TryParse(parts[i], out mode)) continue;
                    int count = mode == 2 ? 3 : mode == 5 ? 1 : 0;
                    if (count == 0) continue;
                    if (i + count >= parts.Length) break;
                    int red, green, blue;
                    string color = null;
                    if (mode == 2 && int.TryParse(parts[i + 1], out red) && int.TryParse(parts[i + 2], out green) &&
                        int.TryParse(parts[i + 3], out blue) && ValidColor(red) && ValidColor(green) && ValidColor(blue))
                        color = string.Format("#{0:x2}{1:x2}{2:x2}", red, green, blue);
                    else if (mode == 5 && int.TryParse(parts[i + 1], out red) && ValidColor(red))
                        color = PaletteColor(red);
                    if (code == 38 && color != null) style.Color = color;
                    i += count;
                }
            }
        }

        private static bool ValidColor(int value) { return value >= 0 && value <= 255; }

        private static string PaletteColor(int index)
        {
            if (index < 16) return Colors[index];
            if (index >= 232)
            {
                int gray = 8 + (index - 232) * 10;
                return string.Format("#{0:x2}{0:x2}{0:x2}", gray);
            }
            index -= 16;
            int[] levels = { 0, 95, 135, 175, 215, 255 };
            return string.Format("#{0:x2}{1:x2}{2:x2}", levels[index / 36], levels[index / 6 % 6], levels[index % 6]);
        }
    }
}
