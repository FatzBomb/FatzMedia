using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Rendering.Software;

internal sealed class NoOpSubtitleRasterizer : ISubtitleRasterizer
{
    public SubtitleRasterResult Render(string text, int width, int height) => default;

    public SubtitleRasterResult RenderKaraoke(
        KaraokeCueTiming timing,
        TimeSpan position,
        int width,
        int height) => default;

    public void Dispose()
    {
    }
}
