// Host-shared source: Host~/Build-Host.ps1 also compiles this file with Windows PowerShell 5.1 Add-Type (C# 5).
// Keep it C# 5 compatible (no string interpolation, ?., nameof, expression-bodied members, etc.). See README "호스트 빌드 제약".
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityTerminal
{
    internal struct TerminalStyle : IEquatable<TerminalStyle>
    {
        public string Color;
        public bool Bold;
        public bool Italic;

        public bool Equals(TerminalStyle other)
        {
            return Color == other.Color && Bold == other.Bold && Italic == other.Italic;
        }
    }

    // Owned by the editor thread. Background readers only write to ShellSession's queue.
    internal sealed class TerminalBuffer
    {
        private sealed class Span
        {
            public TerminalStyle Style;
            public readonly StringBuilder Text = new StringBuilder();
        }

        private sealed class Line
        {
            public readonly List<Span> Spans = new List<Span>();
            public int Length;
        }

        private readonly Queue<Line> lines = new Queue<Line>();
        private readonly int maxLines;
        private readonly int maxCharacters;
        private readonly int maxLineLength;
        private Line lastLine;
        private bool pendingCarriageReturn;
        private int characterCount;
        private int cachedVersion = -1;
        private string cachedRichText;

        public int Version { get; private set; }
        public int LineCount { get { return lines.Count; } }
        public int CharacterCount { get { return characterCount; } }

        public TerminalBuffer(int maxLines = 2000, int maxCharacters = 262144, int maxLineLength = 16384)
        {
            if (maxLines < 1 || maxCharacters < 1 || maxLineLength < 1)
                throw new ArgumentOutOfRangeException("Buffer limits must be positive.");
            this.maxLines = maxLines;
            this.maxCharacters = maxCharacters;
            this.maxLineLength = Math.Min(maxLineLength, maxCharacters);
            Clear();
        }

        public void Clear()
        {
            lines.Clear();
            lastLine = new Line();
            lines.Enqueue(lastLine);
            characterCount = 0;
            pendingCarriageReturn = false;
            Version++;
        }

        public void Append(string text, TerminalStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (pendingCarriageReturn)
                {
                    pendingCarriageReturn = false;
                    // Defer CR until the following character, even across read boundaries.
                    if (c != '\n') ClearLastLine();
                }

                if (c == '\r' || c == '\n')
                {
                    AppendSegment(text, start, i - start, style);
                    if (c == '\r') pendingCarriageReturn = true;
                    else AddLine();
                    start = i + 1;
                }
            }
            AppendSegment(text, start, text.Length - start, style);
            Version++;
        }

        private void AppendSegment(string text, int start, int count, TerminalStyle style)
        {
            if (count == 0) return;
            if (count >= maxLineLength)
            {
                ClearLastLine();
                start += count - maxLineLength;
                count = maxLineLength;
            }

            int excess = lastLine.Length + count - maxLineLength;
            while (excess > 0 && lastLine.Spans.Count > 0)
            {
                Span first = lastLine.Spans[0];
                int removed = Math.Min(excess, first.Text.Length);
                first.Text.Remove(0, removed);
                lastLine.Length -= removed;
                characterCount -= removed;
                excess -= removed;
                if (first.Text.Length == 0) lastLine.Spans.RemoveAt(0);
            }

            Span span = lastLine.Spans.Count == 0 ? null : lastLine.Spans[lastLine.Spans.Count - 1];
            if (span == null || !span.Style.Equals(style))
            {
                span = new Span { Style = style };
                lastLine.Spans.Add(span);
            }
            span.Text.Append(text, start, count);
            lastLine.Length += count;
            characterCount += count;
            Trim();
        }

        private void ClearLastLine()
        {
            characterCount -= lastLine.Length;
            lastLine.Spans.Clear();
            lastLine.Length = 0;
        }

        private void AddLine()
        {
            lastLine = new Line();
            lines.Enqueue(lastLine);
            Trim();
        }

        private void Trim()
        {
            while (lines.Count > 1 && (lines.Count > maxLines || characterCount > maxCharacters))
                characterCount -= lines.Dequeue().Length;
        }

        public string GetLastLineText()
        {
            var result = new StringBuilder(lastLine.Length);
            foreach (Span span in lastLine.Spans) result.Append(span.Text);
            return result.ToString();
        }

        public string GetPlainText()
        {
            var result = new StringBuilder();
            foreach (Line line in lines)
            {
                foreach (Span span in line.Spans) result.Append(span.Text);
                result.Append('\n');
            }
            return result.ToString();
        }

        public string GetRichText()
        {
            if (cachedVersion == Version) return cachedRichText;
            var result = new StringBuilder();
            foreach (Line line in lines)
            {
                foreach (Span span in line.Spans)
                {
                    if (span.Style.Bold) result.Append("<b>");
                    if (span.Style.Italic) result.Append("<i>");
                    if (span.Style.Color != null) result.Append("<color=").Append(span.Style.Color).Append('>');
                    // IMGUI does not decode HTML entities. Keep literal brackets and
                    // shield each '<' from rich-text parsing, including </noparse> input.
                    result.Append(span.Text.ToString().Replace("<", "<noparse><</noparse>"));
                    if (span.Style.Color != null) result.Append("</color>");
                    if (span.Style.Italic) result.Append("</i>");
                    if (span.Style.Bold) result.Append("</b>");
                }
                result.Append('\n');
            }
            cachedRichText = result.ToString();
            cachedVersion = Version;
            return cachedRichText;
        }

        internal void WriteDisplayState(BinaryWriter writer)
        {
            writer.Write(lines.Count);
            foreach (Line line in lines)
            {
                writer.Write(line.Spans.Count);
                foreach (Span span in line.Spans)
                {
                    writer.Write(span.Style.Color ?? ""); writer.Write(span.Style.Bold); writer.Write(span.Style.Italic);
                    writer.Write(span.Text.ToString());
                }
            }
        }

        internal void ReadDisplayState(BinaryReader reader)
        {
            Clear(); lines.Clear();
            int count = reader.ReadInt32();
            if (count < 1 || count > maxLines) throw new InvalidDataException("Invalid terminal buffer size.");
            for (int i = 0; i < count; i++)
            {
                lastLine = new Line(); lines.Enqueue(lastLine);
                int spans = reader.ReadInt32();
                if (spans < 0 || spans > maxLineLength) throw new InvalidDataException("Invalid terminal span count.");
                for (int j = 0; j < spans; j++)
                {
                    string color = reader.ReadString();
                    var span = new Span { Style = new TerminalStyle { Color = color == "" ? null : color, Bold = reader.ReadBoolean(), Italic = reader.ReadBoolean() } };
                    string text = reader.ReadString();
                    if (lastLine.Length + text.Length > maxLineLength) throw new InvalidDataException("Terminal line exceeds its limit.");
                    span.Text.Append(text); lastLine.Spans.Add(span); lastLine.Length += text.Length; characterCount += text.Length;
                }
            }
            if (characterCount > maxCharacters) throw new InvalidDataException("Terminal buffer exceeds its limit.");
            Version++;
        }
    }
}
