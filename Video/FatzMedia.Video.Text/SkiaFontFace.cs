using System.Text;
using SkiaSharp;

namespace FatzMedia.Video.Text;

/// <summary>
/// One requested family, weight and slant: the typeface the font manager matched for it (or the
/// platform default when the family is not installed, as on a Linux box without Segoe UI), plus
/// per-character fallback typefaces for text the primary one has no glyph for. Bold and italic
/// are simulated, as DirectWrite does, when the matched typeface lacks them.
/// </summary>
/// <remarks>
/// Typefaces are not disposed: SkiaSharp shares one managed wrapper per native typeface across
/// the process (an Avalonia host may hold the same one), and the font manager keeps them alive.
/// </remarks>
internal sealed class SkiaFontFace
{
    private readonly List<SKTypeface> _typefaces = [];
    private readonly Dictionary<int, int> _fallbacks = [];
    private readonly SKFontStyle _style;
    private readonly string _family;
    private readonly int _weight;
    private readonly bool _italic;

    public SkiaFontFace(string family, int weight, bool italic)
    {
        _family = family;
        _style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        SKTypeface primary = SKFontManager.Default.MatchFamily(family, _style)
            ?? SKTypeface.FromFamilyName(family, _style)
            ?? SKTypeface.Default;
        _typefaces.Add(primary);
        _weight = weight;
        _italic = italic;
    }

    public int TypefaceCount => _typefaces.Count;

    /// <summary>The typeface index (0 is the primary) that draws <paramref name="codePoint"/>.</summary>
    public int GetTypefaceIndex(int codePoint)
    {
        if (codePoint < 0x20 || _typefaces[0].ContainsGlyph(codePoint))
            return 0;
        if (_fallbacks.TryGetValue(codePoint, out var cached))
            return cached;

        var index = 0;
        for (var i = 1; i < _typefaces.Count; i++)
        {
            if (_typefaces[i].ContainsGlyph(codePoint))
            {
                index = i;
                break;
            }
        }

        if (index == 0 && SKFontManager.Default.MatchCharacter(_family, _style, null, codePoint) is { } matched)
        {
            index = _typefaces.IndexOf(matched);
            if (index < 0)
            {
                _typefaces.Add(matched);
                index = _typefaces.Count - 1;
            }
        }

        _fallbacks[codePoint] = index;
        return index;
    }

    /// <summary>
    /// A font for typeface <paramref name="index"/> at <paramref name="size"/>; the caller disposes
    /// it. Bold and italic are simulated per typeface, so a fallback face that lacks them is
    /// simulated even when the primary one has them (and one that has them is not simulated twice).
    /// </summary>
    public SKFont CreateFont(int index, float size)
    {
        SKTypeface typeface = _typefaces[index];
        return new SKFont(typeface, size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
            Embolden = _weight >= (int)SKFontStyleWeight.SemiBold && typeface.FontWeight < (int)SKFontStyleWeight.SemiBold,
            SkewX = _italic && typeface.FontSlant == SKFontStyleSlant.Upright ? -0.2f : 0,
        };
    }

    /// <summary>The code point at <paramref name="index"/> and its UTF-16 length (lone surrogates count as U+FFFD).</summary>
    public static (int CodePoint, int Length) ReadCodePoint(string text, int index) =>
        Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out var length) == System.Buffers.OperationStatus.Done
            ? (rune.Value, length)
            : (Rune.ReplacementChar.Value, 1);
}
