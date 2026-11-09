namespace FatzMedia.Video.Core.Subtitle;

public interface ISubtitleRasterizer : IDisposable
{
    SubtitleRasterResult Render(string text, int width, int height);
    SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height);

    /// <summary>
    /// Renders <paramref name="text"/> into memory the caller supplies: the rasterizer calls
    /// <paramref name="rentPixels"/> once with the byte count it needs and returns a result whose
    /// <see cref="SubtitleRasterResult.Pixels"/> lie in that memory. The caller owns the memory.
    /// The default renders through <see cref="Render(string, int, int)"/> and copies.
    /// </summary>
    SubtitleRasterResult Render(string text, int width, int height, Func<int, Memory<byte>> rentPixels) =>
        CopyInto(Render(text, width, height), rentPixels);

    /// <summary>
    /// Renders a karaoke frame into memory the caller supplies, as
    /// <see cref="Render(string, int, int, Func{int, Memory{byte}})"/> does. The default renders
    /// through <see cref="RenderKaraoke(KaraokeCueTiming, TimeSpan, int, int)"/> and copies.
    /// </summary>
    SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height, Func<int, Memory<byte>> rentPixels) =>
        CopyInto(RenderKaraoke(timing, position, width, height), rentPixels);

    private static SubtitleRasterResult CopyInto(SubtitleRasterResult result, Func<int, Memory<byte>> rentPixels)
    {
        ArgumentNullException.ThrowIfNull(rentPixels);
        if (result.Pixels.IsEmpty)
            return result;

        var pixels = rentPixels(result.Pixels.Length)[..result.Pixels.Length];
        result.Pixels.CopyTo(pixels);
        return result with { Pixels = pixels };
    }
}
