namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class MediaSession : IDisposable
{
    public MediaState State { get; }
    public PlaybackClock Clock { get; } = new();
    public ClockAudioEngine ClockAudio { get; }
    public AudioPipeline? Audio { get; private set; }
    public CdgPipeline? Cdg { get; private set; }
    public VideoPipeline? Video { get; private set; }

    public MediaSession()
        : this(null)
    {
    }

    public MediaSession(MediaState? state)
    {
        State = state ?? new MediaState();
        ClockAudio = new ClockAudioEngine(Clock);
    }

    public void AttachAudio(AudioPipeline audio) => Audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public void AttachCdg(CdgPipeline cdg) => Cdg = cdg ?? throw new ArgumentNullException(nameof(cdg));

    public void AttachVideo(VideoPipeline video) => Video = video ?? throw new ArgumentNullException(nameof(video));

    public void Clear()
    {
        Video?.Close();
        Audio?.Close();
        Cdg?.Close();
        Clock.Stop();
        State.Reset();
    }

    public void Dispose()
    {
        Video?.Dispose();
        Audio?.Dispose();
        Cdg?.Close();
        ClockAudio.Dispose();
    }
}
