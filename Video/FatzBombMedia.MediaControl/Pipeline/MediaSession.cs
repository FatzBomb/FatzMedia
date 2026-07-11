using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class MediaSession : IDisposable
{
    public MediaState State { get; }
    public PlaybackSyncState Sync { get; } = new();
    public IClockSource Clock { get; } = new PlaybackClock();
    public ClockAudioEngine ClockAudio { get; }
    public AudioPipeline? Audio { get; private set; }
    public CdgPipeline? Cdg { get; private set; }
    public SubtitlePipeline? Subtitle { get; private set; }
    public VideoPipeline? Video { get; private set; }

    public MediaSession()
    {
        State = new MediaState();
        ClockAudio = new ClockAudioEngine(Clock);
    }

    public void AttachAudio(AudioPipeline audio) => Audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public void AttachCdg(CdgPipeline cdg) => Cdg = cdg ?? throw new ArgumentNullException(nameof(cdg));

    public void AttachSubtitle(SubtitlePipeline subtitle) => Subtitle = subtitle ?? throw new ArgumentNullException(nameof(subtitle));

    public void AttachVideo(VideoPipeline video) => Video = video ?? throw new ArgumentNullException(nameof(video));

    public void Clear()
    {
        Video?.Close();
        Audio?.Close();
        Cdg?.Close();
        Subtitle?.Close();
        Sync.Reset();
        Clock.Stop();
        State.Reset();
    }

    public void Dispose()
    {
        Video?.Dispose();
        Audio?.Dispose();
        Cdg?.Close();
        Subtitle?.Dispose();
        ClockAudio.Dispose();
    }
}
