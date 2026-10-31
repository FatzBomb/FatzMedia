using SkiaSharp;

namespace FatzBomb.MediaControl.Rendering.Text;

/// <summary>
/// A laid-out block of text, the counterpart of a DirectWrite text layout: explicit line breaks,
/// greedy word wrapping inside <c>maxWidth</c> (a word wider than a line breaks between
/// characters), leading or centred lines, and metrics measured as DirectWrite reports them
/// (width of the widest line without its trailing whitespace; height of every line). Glyphs are
/// placed by their advances without shaping, and each run of one typeface becomes a cached
/// <see cref="SKTextBlob"/>, so drawing a layout allocates nothing.
/// </summary>
internal sealed class SkiaTextLayout : IDisposable
{
    private readonly string _text;
    private readonly float[] _advances;
    private readonly Line[] _lines;

    private SkiaTextLayout(string text, float[] advances, Line[] lines, float width, float height)
    {
        _text = text;
        _advances = advances;
        _lines = lines;
        Width = width;
        Height = height;
    }

    /// <summary>Width of the widest line, trailing whitespace excluded.</summary>
    public float Width { get; }

    /// <summary>Height of all lines.</summary>
    public float Height { get; }

    public int LineCount => _lines.Length;

    public static SkiaTextLayout Create(string text, SkiaFontFace face, float size, float maxWidth, bool center)
    {
        text = text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
        var fontIndices = new int[text.Length];
        for (var i = 0; i < text.Length;)
        {
            (var codePoint, var length) = SkiaFontFace.ReadCodePoint(text, i);
            var index = face.GetTypefaceIndex(codePoint);
            for (var j = 0; j < length; j++)
                fontIndices[i + j] = index;
            i += length;
        }

        var fonts = new SKFont?[face.TypefaceCount];
        try
        {
            SKFont GetFont(int index) => fonts[index] ??= face.CreateFont(index, size);

            // Advances per UTF-16 unit; the second unit of a surrogate pair advances nothing.
            var advances = new float[text.Length];
            for (var i = 0; i < text.Length;)
            {
                (_, var length) = SkiaFontFace.ReadCodePoint(text, i);
                if (text[i] != '\n')
                    advances[i] = GetFont(fontIndices[i]).MeasureText(text.AsSpan(i, length));
                i += length;
            }

            // Line height from every font the text uses, as DirectWrite takes the tallest.
            float ascent = 0, descent = 0, leading = 0;
            _ = GetFont(0);
            foreach (SKFont? font in fonts)
            {
                if (font is null)
                    continue;
                font.GetFontMetrics(out SKFontMetrics metrics);
                ascent = Math.Max(ascent, -metrics.Ascent);
                descent = Math.Max(descent, metrics.Descent);
                leading = Math.Max(leading, metrics.Leading);
            }

            var lineHeight = ascent + descent + leading;
            var ranges = new List<(int Start, int End)>();
            var paragraphStart = 0;
            while (true)
            {
                var newline = text.IndexOf('\n', paragraphStart);
                var paragraphEnd = newline < 0 ? text.Length : newline;
                Wrap(text, advances, paragraphStart, paragraphEnd, maxWidth, ranges);
                if (newline < 0)
                    break;
                paragraphStart = newline + 1;
            }

            var lines = new Line[ranges.Count];
            var width = 0f;
            for (var l = 0; l < ranges.Count; l++)
            {
                (var start, var end) = ranges[l];
                var visibleEnd = TrimEnd(text, start, end);
                var lineWidth = Sum(advances, start, visibleEnd);
                width = Math.Max(width, lineWidth);
                var runs = new List<Run>();
                for (var runStart = start; runStart < visibleEnd;)
                {
                    var runEnd = runStart + 1;
                    while (runEnd < visibleEnd && fontIndices[runEnd] == fontIndices[runStart])
                        runEnd++;
                    SKTextBlob? blob = SKTextBlob.Create(text.AsSpan(runStart, runEnd - runStart), GetFont(fontIndices[runStart]));
                    if (blob is not null)
                        runs.Add(new Run(blob, Sum(advances, start, runStart)));
                    runStart = runEnd;
                }

                lines[l] = new Line(lineWidth, (l * lineHeight) + ascent, [.. runs]);
            }

            // DirectWrite centres each line in the layout box (trailing whitespace excluded).
            var boxWidth = float.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : width;
            for (var l = 0; l < lines.Length; l++)
                lines[l] = lines[l] with { Left = center ? (boxWidth - lines[l].Width) / 2 : 0 };

            return new SkiaTextLayout(text, advances, lines, width, lines.Length * lineHeight);
        }
        finally
        {
            foreach (SKFont? font in fonts)
                font?.Dispose();
        }
    }

    /// <summary>Draws every line with the layout box's top-left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Draw(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        foreach (Line line in _lines)
        {
            foreach (Run run in line.Runs)
                canvas.DrawText(run.Blob, x + line.Left + run.X, y + line.Baseline, paint);
        }
    }

    /// <summary>
    /// For a single-line layout, the width of <c>text[..length]</c> without its trailing
    /// whitespace: what DirectWrite's hit test from the first character's leading edge to the
    /// last kept character's trailing edge returns.
    /// </summary>
    public float MeasurePrefixWidth(int length)
    {
        var end = TrimEnd(_text, 0, Math.Min(length, _text.Length));
        return end == 0 ? 0 : Sum(_advances, 0, end);
    }

    public void Dispose()
    {
        foreach (Line line in _lines)
        {
            foreach (Run run in line.Runs)
                run.Blob.Dispose();
        }
    }

    private static void Wrap(string text, float[] advances, int start, int end, float maxWidth, List<(int Start, int End)> lines)
    {
        var wrap = float.IsFinite(maxWidth) && maxWidth > 0;
        var lineStart = start;
        var position = start;
        while (position < end)
        {
            var wordStart = position;
            while (position < end && !char.IsWhiteSpace(text[position]))
                position++;
            var wordEnd = position;
            while (position < end && char.IsWhiteSpace(text[position]))
                position++;

            if (!wrap)
                continue;

            // The word does not fit after what the line holds: it starts the next line.
            if (wordStart > lineStart && Sum(advances, lineStart, wordEnd) > maxWidth)
            {
                lines.Add((lineStart, wordStart));
                lineStart = wordStart;
            }

            // A word wider than a whole line breaks between characters (at least one per line).
            while (Sum(advances, lineStart, wordEnd) > maxWidth)
            {
                var split = lineStart + 1;
                while (split < wordEnd && Sum(advances, lineStart, split + 1) <= maxWidth)
                    split++;
                if (split < wordEnd && char.IsLowSurrogate(text[split]))
                    split = split - 1 > lineStart ? split - 1 : split + 1;
                if (split >= wordEnd)
                    break;
                lines.Add((lineStart, split));
                lineStart = split;
            }
        }

        lines.Add((lineStart, end));
    }

    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        return end;
    }

    private static float Sum(float[] advances, int start, int end)
    {
        var sum = 0f;
        for (var i = start; i < end; i++)
            sum += advances[i];
        return sum;
    }

    private readonly record struct Run(SKTextBlob Blob, float X);

    private readonly record struct Line(float Width, float Baseline, Run[] Runs)
    {
        public float Left { get; init; }
    }
}
