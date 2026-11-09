namespace FatzMedia.Video.Core.Subtitle;

/// <summary>
/// A subtitle rasterizer that draws nothing, for renderers without text rendering. Every call
/// returns an empty result and disposing it does nothing, so one instance serves every caller.
/// </summary>
public sealed class NullSubtitleRasterizer : ISubtitleRasterizer
{
    private NullSubtitleRasterizer()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NullSubtitleRasterizer Instance { get; } = new();

    public SubtitleRasterResult Render(string text, int width, int height) => default;

    public SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height) => default;

    public void Dispose()
    {
    }
}
