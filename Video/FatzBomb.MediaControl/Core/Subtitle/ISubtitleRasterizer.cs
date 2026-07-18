using FatzBomb.MediaControl.Core.Audio;

namespace FatzBomb.MediaControl.Core.Subtitle;

public interface ISubtitleRasterizer : IDisposable
{
    SubtitleRasterResult Render(string text, int width, int height);
    SubtitleRasterResult RenderKaraoke(KaraokeCueTiming timing, TimeSpan position, int width, int height);
}
