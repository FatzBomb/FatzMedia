using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Audio.Fade;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Network;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Plays the soundtrack of a video file with no video decoder (Stage 043A, Audio mode). It wraps
/// the <see cref="VideoAudioEngine"/> that video playback already uses (FFmpeg decode into a BASS
/// push stream, with tempo, pitch, equalizer, fades and normalization) and runs the decode pump
/// that <see cref="VideoPipeline"/> otherwise drives, so the file plays as plain audio content.
/// </summary>
/// <remarks>
/// The push stream never ends by itself, so the end is detected here: the position reaches the
/// duration, or it stops advancing while playing for <see cref="EndStallSeconds"/> (the audio ran
/// out before the container's duration, which includes the picture). A gapless loop wrap
/// (<see cref="VideoAudioEngine.CompletedLoopCount"/>) is reported through
/// <see cref="PlaybackEnded"/>, as the plain audio engine reports its loop wraps.
/// <para>
/// A network video's soundtrack (Stage 039A) opens through FFmpeg's network path with no
/// normalization pre-analysis, never loops (as 014A network audio), and reports buffering from
/// its read-ahead (slice 2).
/// </para>
/// <para>
/// Network audio that opens through FFmpeg (https, so the certificate is verified, and HLS;
/// ENG-034) plays here too, in Full and Audio mode: its capability row follows the 014A audio
/// rule for stream metadata, and in-stream titles (ICY, Ogg comments) are raised through
/// <see cref="StreamMetadataChanged"/> when playback reaches them. Prebuffering a network source
/// on the caller's thread waits at most <see cref="NetworkPrebufferWait"/>; the pump fills the
/// rest.
/// </para>
/// </remarks>
internal sealed class SoundtrackAudioEngine : IAudioEngine, IAudioLoopControl, IAudioFilterHost, IAudioFadeGainTarget, IAudioVolumeSlide, IMidiEventReaderSource
{
    internal const double EndToleranceSeconds = 0.05;
    internal const double EndStallSeconds = 1.0;
    private const double StallPositionEpsilonSeconds = 0.0005;
    // Open and Play prebuffer on the caller's thread (the control queue): a stalled download must
    // not hold it (ENG-034).
    private static readonly TimeSpan NetworkPrebufferWait = TimeSpan.FromMilliseconds(500);

    private readonly VideoAudioEngine _audio;
    private readonly PlaybackScheduler _pump = new("SoundtrackDecode");
    private readonly Lock _gate = new();
    private int _generation;
    private int _observedLoopCount;
    private bool _ended;
    private bool _loopPlayback;
    private bool _disposed;
    private volatile bool _isNetwork;
    private MediaSourceCapabilities? _networkCapabilities;
    private NetworkBufferingMonitor? _bufferingMonitor;

    public SoundtrackAudioEngine(IOutputLatencyProvider outputLatencyProvider, int bassDeviceIndex)
    {
        _audio = new VideoAudioEngine(outputLatencyProvider, bassDeviceIndex);
        _audio.StateChanged += OnAudioStateChanged;
        _audio.Opened += OnAudioOpened;
        _audio.Closed += OnAudioClosed;
        _audio.PositionChanged += OnAudioPositionChanged;
        _audio.NormalizationGainChanged += OnAudioNormalizationGainChanged;
    }

    public AudioEngineState State => _audio.State;
    public int PlayHandle => _audio.PlayHandle;
    public int SourceHandle => _audio.SourceHandle;

    IMidiEventReader? IMidiEventReaderSource.GetMidiEventReader() => ((IMidiEventReaderSource)_audio).GetMidiEventReader();

    public bool IsOpen => _audio.IsOpen;

    public bool Normalize
    {
        get => _audio.Normalize;
        set => _audio.Normalize = value;
    }

    public double NormalizationGain => _audio.NormalizationGain;
    public bool IsNormalizationReady => _audio.IsNormalizationReady;
    public int CurrentTempo => _audio.CurrentTempo;
    public double Duration => _audio.Duration;

    public double Position
    {
        get => _audio.Position;
        set => Seek(value);
    }

    /// <summary>Gets or sets whether the soundtrack loops. A network soundtrack never loops.</summary>
    public bool LoopPlayback
    {
        get => Volatile.Read(ref _loopPlayback) && !_isNetwork;
        set
        {
            Volatile.Write(ref _loopPlayback, value);
            _audio.LoopPlayback = LoopPlayback;
        }
    }

    /// <summary>Gets the capability row of an open network soundtrack, or <see langword="null"/>.</summary>
    public MediaSourceCapabilities? NetworkCapabilities => _isNetwork && _audio.IsOpen ? Volatile.Read(ref _networkCapabilities) : null;

    /// <summary>Gets FFmpeg's reason for the last failed network open, or <see langword="null"/>.</summary>
    public string? LastNetworkError => _audio.LastNetworkError;

    /// <summary>Raised from a timer thread when the network buffering state changes.</summary>
    public event EventHandler<MediaBufferingChangedEventArgs>? NetworkBufferingChanged;

    /// <summary>Raised from a timer thread when playback reaches a new in-stream title of network audio (ENG-034).</summary>
    public event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    public event EventHandler<double>? NormalizationGainChanged;
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;

    /// <summary>Audio output is initialized by the player's audio engine; nothing to do here.</summary>
    public bool Init(int device = -1, int sampleRate = 44100) => true;

    public bool Open(string filePath) => OpenCore(() => _audio.Open(filePath));

    public bool Open(IArchiveEntryInfo archiveEntry) => OpenCore(() => _audio.Open(archiveEntry));

    /// <summary>
    /// Makes one attempt to open the soundtrack of a network video (Stage 039A), or network audio
    /// through FFmpeg when <paramref name="audioSource"/> is set (ENG-034: its row may carry stream
    /// metadata, and its titles are raised).
    /// </summary>
    internal bool OpenNetwork(string url, FfmpegNetworkOptions options, bool audioSource = false)
    {
        if (!OpenCore(() => _audio.OpenNetwork(url, options), network: true))
            return false;

        NetworkVideoProbe probe = _audio.GetNetworkProbe();
        Volatile.Write(ref _networkCapabilities, audioSource ? NetworkAudioPolicy.Resolve(probe) : NetworkVideoPolicy.Resolve(probe));
        MediaSourceCapabilities? opened = _networkCapabilities;
        // The soundtrack's own cursor has a read-ahead too (slice 2): buffering starts when its
        // queue runs dry and ends at the resume threshold, as for network video.
        _bufferingMonitor = new NetworkBufferingMonitor(
            () => new NetworkBufferingSample(
                _audio.State == AudioEngineState.Playing,
                _audio.NetworkReadStall,
                _audio.NetworkReadAheadFill,
                _audio.NetworkEndOfStream,
                PlaybackEnded: IsEnded),
            args =>
            {
                if (IsCurrent(opened))
                    NetworkBufferingChanged?.Invoke(this, args);
            },
            NetworkBufferingMonitor.ReadAheadExitFill,
            opened is { HasStreamMetadata: true } ? () => PublishStreamMetadata(opened) : null);
        return true;
    }

    private bool IsCurrent(MediaSourceCapabilities? opened) =>
        ReferenceEquals(Volatile.Read(ref _networkCapabilities), opened) && _audio.IsOpen;

    private bool IsEnded
    {
        get
        {
            lock (_gate)
                return _ended;
        }
    }

    /// <summary>Raises the in-stream title playback has reached, if it changed (from the monitor's timer thread).</summary>
    private void PublishStreamMetadata(MediaSourceCapabilities? opened)
    {
        if (!IsCurrent(opened) || !_audio.TryTakeStreamMetadata(out StreamMetadata metadata) || !IsCurrent(opened))
            return;

        StreamMetadataChanged?.Invoke(this, new MediaStreamMetadataChangedEventArgs(metadata.Title, metadata.Artist, metadata.RawTitle));
    }

    private bool OpenCore(Func<bool> open, bool network = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AbortNetworkRead();
        StopPump();
        _isNetwork = network;

        // Open closes the previous file, which clears the inner loop flag and loop count.
        if (!open())
            return false;

        ResetEndTracking();
        _audio.LoopPlayback = LoopPlayback;
        Prebuffer();
        return true;
    }

    private void Prebuffer()
    {
        using (_isNetwork ? NetworkReadAhead.WaitAtMost(NetworkPrebufferWait) : default)
            _ = _audio.PrebufferAudio();
    }

    public bool Play()
    {
        if (!_audio.IsOpen)
            return false;

        bool restartFromBeginning;
        lock (_gate)
            restartFromBeginning = _ended;
        if (restartFromBeginning)
        {
            _ = _audio.Seek(0, resumePlayback: false);
            ResetEndTracking();
        }

        Prebuffer();
        if (!_audio.Play())
            return false;

        StartPump();
        return true;
    }

    public bool Pause()
    {
        StopPump();
        return _audio.Pause();
    }

    public bool Stop()
    {
        StopPump();
        ResetEndTracking();
        return _audio.Stop();
    }

    private void Seek(double seconds)
    {
        ResetEndTracking();
        // The pump may be waiting for a network packet while holding the engine's I/O gate: give
        // the wait up so the seek does not wait for a stalled download (ENG-035, slice 2).
        _audio.SupersedeNetworkOperationsStartedBefore(Stopwatch.GetTimestamp());
        _ = _audio.Seek(seconds);
    }

    public void Close()
    {
        AbortNetworkRead();
        StopPump();
        ResetEndTracking();
        _audio.Close();
        _isNetwork = false;
    }

    /// <summary>
    /// A network read can block for the read timeout: retire the pump's generation (so the aborted
    /// read is not taken for the end), then abort the read before joining the pump.
    /// </summary>
    private void AbortNetworkRead()
    {
        lock (_gate)
            _generation++;
        _audio.AbortNetwork();
        StopNetworkMonitoring();
    }

    private void StopNetworkMonitoring()
    {
        _bufferingMonitor?.Dispose();
        _bufferingMonitor = null;
        Volatile.Write(ref _networkCapabilities, null);
    }

    public void SetVolume(double volume01) => _audio.SetVolume(volume01);

    public void SetPitch(int semitones) => _audio.SetPitch(semitones);

    public void SetTempo(int percent) => _audio.SetTempo(percent);

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => _audio.AnalyzePeakQuickAsync(cancellationToken);

    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => _audio.AnalyzePeakAsync(cancellationToken);

    AudioEqualizerSettings IAudioFilterHost.Equalizer => ((IAudioFilterHost)_audio).Equalizer;

    bool IAudioFilterHost.SetEqualizer(AudioEqualizerSettings settings) => ((IAudioFilterHost)_audio).SetEqualizer(settings);

    int IAudioFadeGainTarget.BeginFadeSession() => ((IAudioFadeGainTarget)_audio).BeginFadeSession();

    bool IAudioFadeGainTarget.SetFadeGain(int session, double gain, TimeSpan ramp) => ((IAudioFadeGainTarget)_audio).SetFadeGain(session, gain, ramp);

    void IAudioVolumeSlide.SlideVolume(double volume01, TimeSpan duration) => ((IAudioVolumeSlide)_audio).SlideVolume(volume01, duration);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        AbortNetworkRead();
        _pump.Dispose();
        _audio.StateChanged -= OnAudioStateChanged;
        _audio.Opened -= OnAudioOpened;
        _audio.Closed -= OnAudioClosed;
        _audio.PositionChanged -= OnAudioPositionChanged;
        _audio.NormalizationGainChanged -= OnAudioNormalizationGainChanged;
        _audio.Dispose();
    }

    private void StartPump()
    {
        int generation;
        lock (_gate)
            generation = ++_generation;
        _pump.Start(token => PumpLoop(generation, token));
    }

    private void StopPump()
    {
        lock (_gate)
            _generation++;
        _pump.StopAndWait();
    }

    private void ResetEndTracking()
    {
        lock (_gate)
        {
            _ended = false;
            _observedLoopCount = _audio.CompletedLoopCount;
        }
    }

    private void PumpLoop(int generation, CancellationToken cancellationToken)
    {
        // A network read waits for the read-ahead's packets (so a stall is not taken for the end
        // below), but Pause, Stop and Close cancel the wait instead of waiting for the network.
        using NetworkReadAhead.WaitScope waits = NetworkReadAhead.CancelWaitsWith(cancellationToken);
        var lastPosition = double.NaN;
        var stallStarted = 0L;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
        {
            var decoded = _audio.DecodeAndPushAudio();
            if (ReportLoopWraps(generation))
            {
                lastPosition = double.NaN;
                stallStarted = 0;
            }
            else if (!LoopPlayback && _audio.State == AudioEngineState.Playing)
            {
                // Only a stall with nothing left to decode is the end: a stall while the decoder
                // still delivers audio, or while BASS has parked the channel because the output
                // device was lost (ENG-032), restarts the stall timer.
                var position = _audio.Position;
                if (double.IsNaN(lastPosition)
                    || Math.Abs(position - lastPosition) > StallPositionEpsilonSeconds
                    || decoded
                    || _audio.IsChannelPausedByDevice())
                {
                    lastPosition = position;
                    stallStarted = Stopwatch.GetTimestamp();
                }

                var stalledSeconds = stallStarted == 0 ? 0 : Stopwatch.GetElapsedTime(stallStarted).TotalSeconds;
                if (HasReachedEnd(position, Duration, stalledSeconds))
                {
                    CompleteAtEnd(generation);
                    return;
                }
            }

            _ = cancellationToken.WaitHandle.WaitOne(decoded ? 1 : 10);
        }
    }

    /// <summary>
    /// The soundtrack has ended when the position reaches the duration, or when the position has
    /// not moved for <see cref="EndStallSeconds"/> while playing (the decoder ran out of audio).
    /// </summary>
    internal static bool HasReachedEnd(double position, double duration, double stalledSeconds) =>
        (duration > 0 && position >= duration - EndToleranceSeconds) || stalledSeconds >= EndStallSeconds;

    private bool IsCurrent(int generation)
    {
        lock (_gate)
            return generation == _generation;
    }

    private bool ReportLoopWraps(int generation)
    {
        var completed = _audio.CompletedLoopCount;
        lock (_gate)
        {
            if (generation != _generation || completed <= _observedLoopCount)
                return false;

            _observedLoopCount = completed;
        }

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CompleteAtEnd(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _ended)
                return;

            _ended = true;
            _generation++;
        }

        _ = _audio.Pause();
        _audio.EndFadeSessionAtEnd();
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnAudioStateChanged(object? sender, AudioEngineState state) => StateChanged?.Invoke(this, state);

    private void OnAudioOpened(object? sender, EventArgs e) => Opened?.Invoke(this, e);

    private void OnAudioClosed(object? sender, EventArgs e) => Closed?.Invoke(this, e);

    private void OnAudioPositionChanged(object? sender, double position) => PositionChanged?.Invoke(this, position);

    private void OnAudioNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);
}
