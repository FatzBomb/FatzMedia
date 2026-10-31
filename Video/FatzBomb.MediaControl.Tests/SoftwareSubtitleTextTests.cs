using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// End to end through the software renderer's factory: a text cue reaches the frame sink as text
/// pixels (it drew nothing before the SkiaSharp rasterizer replaced NullSubtitleRasterizer).
/// </summary>
public sealed class SoftwareSubtitleTextTests
{
    [Fact]
    public void SoftwareFactory_DrawsTheActiveCueIntoTheFrameSink()
    {
        const int Width = 640;
        const int Height = 360;
        var sink = new RecordingFrameSink();
        using var sinkHandle = new MediaPresentationFrameSinkHandle(sink);
        using var subtitles = new SubtitleEngine();
        subtitles.LoadCues([new SubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(10), "Software subtitle")]);
        var engine = new TestMediaEngine { Content = MediaContent.Audio | MediaContent.Subtitle, Subtitle = subtitles, Position = 1 };
        var configuration = MediaPlayerConfiguration.Auto with
        {
            SubtitleStyle = new SubtitleTextStyle { FontSize = 44, TextArgb = 0xFF00FF00 },
        };
        using IMediaPresentationCoordinator coordinator = new SoftwarePresentationCoordinatorFactory().Create(engine, configuration);

        Assert.True(coordinator.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            "primary",
            MediaPresentationSurfaceRole.Primary,
            sinkHandle.CreateSurfaceDescriptor(),
            Width,
            Height)));
        coordinator.RefreshCurrentPosition();

        Assert.Equal((Width, Height), (sink.Width, sink.Height));
        int top = Height, bottom = -1, green = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * sink.Stride) + (x * 4);
                if (sink.Pixels[offset + 1] > 200 && sink.Pixels[offset + 2] < 40 && sink.Pixels[offset] < 40)
                {
                    green++;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        // The cue's green text sits in the lower part of the frame; the rest stays black.
        Assert.True(green > 50, $"{green} text pixels");
        Assert.True(top > Height / 2, $"text top {top}");
        Assert.True(bottom < Height, $"text bottom {bottom}");
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, sink.Pixels.AsSpan(0, 4).ToArray());
    }

    private sealed class RecordingFrameSink : IMediaPresentationFrameSink
    {
        public byte[] Pixels { get; private set; } = [];
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            Pixels = bgraPixels.ToArray();
            Width = width;
            Height = height;
            Stride = stride;
        }
    }
}
