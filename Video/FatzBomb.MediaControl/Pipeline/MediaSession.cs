using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class MediaSession : IDisposable
{
    public MediaState State { get; }
    public PlaybackSyncState Sync { get; } = new();
    public IClockSource Clock { get; } = new PlaybackClock();
    public ClockAudioEngine ClockAudio { get; }

    /// <summary>The pipeline for audio files (the player's audio engine), if any.</summary>
    public AudioPipeline? FileAudio { get; private set; }

    /// <summary>
    /// The audio pipeline in use: the file audio pipeline, or the soundtrack pipeline while a video
    /// file's soundtrack (Audio mode, 043A) or network audio through FFmpeg (ENG-034) is open.
    /// </summary>
    public AudioPipeline? Audio { get; private set; }

    /// <summary>
    /// The pipeline that plays the soundtrack of video files in Audio mode (043A) and network audio
    /// through FFmpeg in Full and Audio mode (ENG-034), if any.
    /// </summary>
    public AudioPipeline? Soundtrack { get; private set; }

    public CdgPipeline? Cdg { get; private set; }
    public SubtitlePipeline? Subtitle { get; private set; }
    public IVideoEngine? Video { get; private set; }

    public MediaSession()
    {
        State = new MediaState();
        ClockAudio = new ClockAudioEngine(Clock);
    }

    public void AttachAudio(AudioPipeline audio)
    {
        FileAudio = audio ?? throw new ArgumentNullException(nameof(audio));
        Audio = audio;
    }

    public void AttachSoundtrack(AudioPipeline soundtrack) => Soundtrack = soundtrack ?? throw new ArgumentNullException(nameof(soundtrack));

    /// <summary>
    /// Selects the soundtrack pipeline (true) or the file audio pipeline (false) as
    /// <see cref="Audio"/>. Callers close the open media first.
    /// </summary>
    public void UseSoundtrack(bool soundtrack) => Audio = soundtrack ? Soundtrack : FileAudio;

    public void AttachCdg(CdgPipeline cdg) => Cdg = cdg ?? throw new ArgumentNullException(nameof(cdg));

    public void AttachSubtitle(SubtitlePipeline subtitle) => Subtitle = subtitle ?? throw new ArgumentNullException(nameof(subtitle));

    public void AttachVideo(IVideoEngine video) => Video = video ?? throw new ArgumentNullException(nameof(video));

    public void Clear()
    {
        Video?.Close();
        Audio?.Close();
        Audio = FileAudio;
        Cdg?.Close();
        Subtitle?.Close();
        Sync.Reset();
        Clock.Stop();
        State.Reset();
    }

    public void Dispose()
    {
        Video?.Dispose();
        FileAudio?.Dispose();
        Soundtrack?.Dispose();
        Cdg?.Close();
        Subtitle?.Dispose();
        ClockAudio.Dispose();
    }
}
