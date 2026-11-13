using System.Collections.Concurrent;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-043: a video container with no video stream (an audio-only .mp4 or .ts) failed to open;
/// it now plays its audio through the soundtrack path, as Audio mode plays a video's sound.
/// </summary>
[Collection("File type registry")]
public sealed class AudioOnlyVideoFallbackTests
{
    const string Extension = ".testcontainer";

    [Fact]
    public void VideoContainerWithoutVideo_FallsBackToItsAudio()
    {
        var messages = OpenWith(new TestVideoEngine { OpenFindsNoVideo = true });

        Assert.Contains(messages, message => message.Contains("has no video stream; playing its audio", StringComparison.Ordinal));
    }

    [Fact]
    public void VideoThatOpens_DoesNotFallBack()
    {
        var video = new TestVideoEngine();
        var messages = OpenWith(video);

        Assert.Single(video.OpenedPaths);
        Assert.DoesNotContain(messages, message => message.Contains("has no video stream", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".m4r")]
    [InlineData(".alac")]
    public void RingtoneAndAlacExtensions_ArePlayableThroughFfmpeg(string extension) =>
        Assert.Contains(extension, FileTypes.FfmpegAudioExtensions);

    static List<string> OpenWith(TestVideoEngine video)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fatzbomb-audio-only-{Guid.NewGuid():N}{Extension}");
        File.WriteAllBytes(path, [0]);
        var messages = new ConcurrentQueue<string>();
        void Record(MediaDiagnosticInfo info) => messages.Enqueue(info.Message);
        _ = FileTypes.Video.Add(Extension);
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using var engine = new PipelineMediaEngine(
                new TestAudioEngine(),
                new TestCdgEngine(),
                new SubtitleEngine(),
                new NotSupportedArchiveService(),
                new FixedVideoFactory(video),
                new SystemPrecisionWaiterFactory(),
                new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance),
                MediaPlayerConfiguration.Auto,
                audioAvailable: true);
            try
            {
                _ = engine.Open(MediaInputSource.FromFile(path));
            }
            catch (NotSupportedException)
            {
                // The soundtrack engine is the real FFmpeg one, not loaded in unit tests: the
                // fallback reached it, which is what these tests check (the message says so).
            }
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
            _ = FileTypes.Video.Remove(Extension);
            File.Delete(path);
        }

        return [.. messages];
    }

    sealed class FixedVideoFactory(TestVideoEngine video) : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) => video;
    }

}
