
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class PlaybackCoordinator : IDisposable
{
    private readonly MediaSession _session;

    public PlaybackCoordinator(
        IAudioEngine audioEngine,
        ICdgEngine cdgEngine,
        MediaPlayerConfiguration configuration,
        bool ownsAudioEngine = false)
    {
        _session = new MediaSession();
        var audio = new AudioPipeline(audioEngine, _session.State, ownsAudioEngine);
        var cdg = new CdgPipeline(cdgEngine, _session.Clock, _session.State);
        var bassDeviceIndex = audioEngine is IAudioOutputDeviceProvider deviceProvider
            ? deviceProvider.BassDeviceIndex
            : configuration.AudioOutputDeviceIndex;
        var video = new VideoPipeline(_session.Clock, _session.State, configuration, bassDeviceIndex);

        audio.PlaybackEnded += OnPlaybackEnded;
        cdg.PlaybackEnded += OnPlaybackEnded;
        video.PlaybackEnded += OnPlaybackEnded;

        _session.AttachAudio(audio);
        _session.AttachCdg(cdg);
        _session.AttachVideo(video);
    }

    public MediaState State => _session.State;
    public event EventHandler? PlaybackEnded;


    public ICdgEngine? Cdg => _session.Cdg?.IsOpen == true ? _session.Cdg.Engine : null;
    public IVideoEngine? Video => _session.Video?.IsOpen == true ? _session.Video : null;

    public bool LoopPlayback
    {
        get => _session.Video?.LoopPlayback == true;
        set => _session.Video?.LoopPlayback = value;
    }

    /// <summary>
    /// Supplies the actual monitor refresh rate to the underlying video pipeline regardless of
    /// whether a file is currently open, since the pipeline instance itself is stable for the
    /// coordinator's lifetime. See IVideoEngine.SetDisplayRefreshRate for details.
    /// </summary>
    public void SetDisplayRefreshRate(double? refreshHz) => _session.Video?.SetDisplayRefreshRate(refreshHz);
    public IAudioEngine ActiveAudio => _session.Video?.IsOpen == true
        ? _session.Video.HasAudio ? _session.Video.Audio! : _session.ClockAudio
        : _session.Audio?.IsOpen == true
            ? _session.Audio.Engine
            : _session.Cdg?.IsOpen == true ? _session.ClockAudio : _session.Audio?.Engine ?? _session.ClockAudio;

    public double DurationSeconds
    {
        get
        {
            var duration = 0.0;
            if (_session.Audio?.IsOpen == true)
                duration = Math.Max(duration, _session.Audio.DurationSeconds);
            if (_session.Cdg?.IsOpen == true)
                duration = Math.Max(duration, _session.Cdg.DurationSeconds);
            if (_session.Video?.IsOpen == true)
                duration = Math.Max(duration, _session.Video.Duration);
            if (duration <= 0)
                duration = _session.Clock.DurationSeconds;
            return duration;
        }
    }

    public double PositionSeconds
    {
        get => _session.Video?.IsOpen == true
              ? _session.Video.Position
              : _session.Audio?.IsOpen == true ? _session.Audio.PositionSeconds : _session.Clock.PositionSeconds;
        set => Seek(value);
    }

    public bool OpenAudio(string path) => _session.Audio?.Open(path) == true;

    public bool OpenAudio(IArchiveEntryInfo archiveEntry) => _session.Audio?.Open(archiveEntry) == true;

    public bool OpenAudioCdg(string audioPath, string cdgPath)
    {
        if (_session.Audio?.Open(audioPath, ownState: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgPath, _session.Audio.PlayHandle);
        _session.State.SetOpened(MediaContent.Audio | MediaContent.Cdg, audioPath, DurationSeconds);
        return true;
    }

    public bool OpenAudioCdg(IArchiveEntryInfo audioEntry, IArchiveEntryInfo cdgEntry)
    {
        if (_session.Audio?.Open(audioEntry, ownState: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgEntry, _session.Audio.PlayHandle);
        _session.State.SetOpened(MediaContent.Audio | MediaContent.Cdg | MediaContent.Archive, audioEntry.Key, DurationSeconds);
        return true;
    }

    public bool OpenStandaloneCdg(string cdgPath)
    {
        if (_session.Cdg == null)
            return false;

        _session.Cdg.OpenStandalone(cdgPath);
        return true;
    }

    public bool OpenStandaloneCdg(IArchiveEntryInfo cdgEntry)
    {
        if (_session.Cdg == null)
            return false;

        _session.Cdg.OpenStandalone(cdgEntry);
        return true;
    }

    public bool OpenVideo(string videoPath) => _session.Video?.Open(videoPath) == true;

    public bool OpenVideo(IArchiveEntryInfo videoEntry) => _session.Video?.Open(videoEntry) == true;

    public bool Play()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Play();

        var hasAudio = _session.Audio?.IsOpen == true;
        var hasCdg = _session.Cdg?.IsOpen == true;
        var success = false;

        if (hasAudio)
            success |= _session.Audio!.Play();
        if (hasCdg)
            success |= _session.Cdg!.Play();

        return success;
    }

    public bool Pause()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Pause();

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Pause();
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Pause();
        return success;
    }

    public bool Stop()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Stop();

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Stop();
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Stop();
        return success;
    }

    public bool Seek(double positionSeconds)
    {
        var target = SeekHelpers.NormalizeSeekTarget(positionSeconds, DurationSeconds);

        if (_session.Video?.IsOpen == true)
            return _session.Video.Seek(target);

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Seek(target);
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Seek(target);
        return success;
    }

    public void Close() => _session.Clear();

    public void Dispose()
    {
        _session.Audio?.PlaybackEnded -= OnPlaybackEnded;
        _session.Cdg?.PlaybackEnded -= OnPlaybackEnded;
        _session.Video?.PlaybackEnded -= OnPlaybackEnded;

        _session.Dispose();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) => PlaybackEnded?.Invoke(this, EventArgs.Empty);
}
