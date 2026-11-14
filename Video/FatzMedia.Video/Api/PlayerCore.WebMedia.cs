using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Threading;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Web media routing (Stage 060A slice 4). A host attaches the <see cref="IWebMediaPlayer"/> of its web view; a
/// load of a YouTube address (<see cref="WebMediaProviders"/>) then plays through it, and the usual
/// <c>Load</c>, <c>Play</c>, <c>Pause</c>, <c>Stop</c>, <c>Seek</c>, <c>Position</c>, <c>Duration</c>, <c>State</c>
/// and events work for it. Every other source takes the engine path unchanged.
/// </summary>
/// <remarks>
/// <para>The web view lives on the host's UI thread and a synchronous call such as <c>Load</c> blocks that thread,
/// so a web item never waits for the web view: the attached player marshals its own calls to the UI thread, the
/// synchronous calls start the command and return, and the answer arrives as events (<c>MediaLoaded</c>,
/// <c>StateChanged</c>, ...). The asynchronous <c>LoadAsync</c> completes when the item is ready.</para>
/// <para>While an item is active the engine is idle: pitch, tempo, equalizer, fades, normalization, CD+G and
/// prepared-next do not apply to it (<see cref="WebMediaSupport"/>). The web view, its player and its profile
/// belong to the host; the player never disposes them.</para>
/// </remarks>
public partial class PlayerCore
{
    readonly Lock _webGate = new();
    IWebMediaPlayer? _webPlayer;
    volatile bool _webActive;
    WebMediaReference? _webItem;
    MediaSourceInfo? _webInfo;
    int _webGeneration;
    int _webState = (int)MediaPlayerState.Stopped;
    // Controls issued while the item loads (the page ignores commands before it is ready) are applied when it is.
    bool _webReady;
    bool? _webPlayWhenReady;
    TimeSpan? _webSeekWhenReady;

    /// <summary>
    /// Attaches the web player that plays web media (YouTube) loaded into this player, or detaches it with
    /// <see langword="null"/>. Without one, loading a web address raises <see cref="MediaErrorCode.WebMediaUnavailable"/>.
    /// Detaching ends an item that is playing. The host keeps ownership of the web player.
    /// </summary>
    public void AttachWebMediaPlayer(IWebMediaPlayer? player)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SwapWebPlayer(player);
    }

    void SwapWebPlayer(IWebMediaPlayer? player)
    {
        IWebMediaPlayer? previous;
        lock (_webGate)
        {
            previous = _webPlayer;
            if (ReferenceEquals(previous, player))
                return;
        }

        if (previous is not null)
        {
            // The item ends while the old player is still attached, so it receives the pause.
            DeactivateWeb(pause: true);
            previous.StateChanged -= OnWebStateChanged;
            previous.PositionChanged -= OnWebPositionChanged;
            previous.Error -= OnWebError;
        }

        if (player is not null)
        {
            player.StateChanged += OnWebStateChanged;
            player.PositionChanged += OnWebPositionChanged;
            player.Error += OnWebError;
        }

        lock (_webGate)
            _webPlayer = player;
    }

    /// <summary>Gets whether the loaded item plays in the attached web view instead of the media engine.</summary>
    public bool IsWebMedia => _webActive;

    /// <summary>
    /// Makes a source from the text a host passes to <c>Load</c>: a YouTube address is a web source, anything
    /// else a file path exactly as before. An address of a supported service that names no single video
    /// (playlist, channel, search) throws <see cref="NotSupportedException"/> with the reason.
    /// </summary>
    static MediaInputSource SourceFromText(string text)
    {
        var web = WebMediaProviders.Parse(text);
        return web.Outcome switch
        {
            WebMediaParseOutcome.Recognized => MediaInputSource.FromWebMedia(web.Reference!),
            WebMediaParseOutcome.Unsupported => throw new NotSupportedException(web.Message),
            _ => MediaInputSource.FromFile(text),
        };
    }

    /// <summary>Resolves a load's source; a rejected address raises <see cref="MediaError"/> and returns null.</summary>
    MediaInputSource? ResolveSource(Func<MediaInputSource> source)
    {
        try
        {
            return source();
        }
        catch (NotSupportedException ex)
        {
            RaiseWebError(MediaErrorCode.UnsupportedFormat, ex.Message, null);
            return null;
        }
    }

    // ---- loading -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts a web load from a synchronous <c>Load</c>: it returns once the command is issued, never waiting for
    /// the web view (the caller may be the UI thread). The result arrives as <see cref="MediaLoaded"/>.
    /// </summary>
    bool LoadWebStart(MediaInputSource source, out Task? loading, out int generation)
    {
        loading = null;
        generation = 0;
        var player = PrepareWebLoad(source);
        if (player is null)
            return false;

        generation = ActivateWeb(source);
        loading = player.LoadAsync(source.WebMedia!, autoplay: PlayOnLoad);
        return true;
    }

    /// <summary>Loads a web item for <c>LoadAsync</c> and completes when it is ready.</summary>
    async Task<bool> LoadWebAsync(MediaInputSource source, CancellationToken cancellationToken)
    {
        if (!LoadWebStart(source, out var loading, out var generation))
            return false;

        try
        {
            await loading!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _webGeneration))
                DeactivateWeb(pause: true);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load (the player cancelled this one): not an error, the load did not complete.
            return false;
        }
        catch (Exception)
        {
            // The load failed; end the item so the session does not stay "active" with nothing loaded.
            AbandonWebLoad(generation);
            return false;
        }

        return CompleteWebLoad(generation);
    }

    IWebMediaPlayer? PrepareWebLoad(MediaInputSource source)
    {
        IWebMediaPlayer? player;
        lock (_webGate)
            player = _webPlayer;

        if (player is null)
        {
            RaiseWebError(
                MediaErrorCode.WebMediaUnavailable,
                $"{source.DisplayName} is an online item; attach a web media player (a web view control) with AttachWebMediaPlayer to play it.",
                source.DisplayName);
            return null;
        }

        if (!MediaPlayerModePolicy.AcceptsSource(Mode, MediaInputSourceKind.WebMedia))
        {
            RaiseWebError(
                MediaErrorCode.UnsupportedFormat,
                MediaPlayerModePolicy.RejectionMessage(Mode, MediaPlayerModePolicy.Describe(MediaInputSourceKind.WebMedia), source.DisplayName),
                source.DisplayName);
            return null;
        }

        return player;
    }

    int ActivateWeb(MediaInputSource source)
    {
        // The item it replaces: engine media closes through the normal path (MediaStopped, MediaClosed).
        if (_webActive)
            DeactivateWeb(pause: true);
        else if (_mediaEngine.IsOpen)
            File.Close();

        lock (_webGate)
        {
            var generation = Interlocked.Increment(ref _webGeneration);
            _webItem = source.WebMedia;
            _webInfo = new MediaSourceInfo(source, MediaContent.None, MediaSourceCapabilityMatrix.WebStream);
            Volatile.Write(ref _webState, (int)MediaPlayerState.Stopped);
            _webReady = false;
            _webPlayWhenReady = null;
            _webSeekWhenReady = null;
            _webActive = true;
            return generation;
        }
    }

    /// <summary>Raises <see cref="MediaLoaded"/> for the item the web player reported ready.</summary>
    bool CompleteWebLoad(int generation)
    {
        if (generation != Volatile.Read(ref _webGeneration) || !_webActive || _webInfo is not { } info)
            return false;

        var player = _webPlayer;
        var duration = player?.Duration ?? TimeSpan.Zero;
        bool? playWhenReady;
        TimeSpan? seekWhenReady;
        lock (_webGate)
        {
            _webReady = true;
            playWhenReady = _webPlayWhenReady;
            seekWhenReady = _webSeekWhenReady;
            _webPlayWhenReady = null;
            _webSeekWhenReady = null;
        }

        HostEventDispatcher.Raise(this, MediaLoaded, new MediaLoadedEventArgs(info, duration), nameof(MediaLoaded));
        HostEventDispatcher.Raise(
            this,
            PositionChanged,
            new MediaPositionChangedEventArgs(TimeSpan.Zero, duration),
            nameof(PositionChanged));

        // The volume the host set on Effects applies to the web item too.
        if (player is not null && Effects.Volume is var volume and >= 0 and <= 1)
            ObserveWeb(player.SetVolumeAsync(volume));

        // Controls the host issued while the item was loading.
        if (player is not null)
        {
            if (seekWhenReady is { } seek)
                ObserveWeb(player.SeekAsync(seek));
            if (playWhenReady == true)
                ObserveWeb(player.PlayAsync());
        }

        return true;
    }

    /// <summary>Observes a synchronous load's completion without blocking the caller.</summary>
    void WatchWebLoad(Task loading, int generation) =>
        _ = loading.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                    CompleteWebLoad(generation);
                else if (task.IsFaulted)
                {
                    _ = task.Exception; // observed
                    AbandonWebLoad(generation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Ends a web item whose load failed, unless a newer load or an error already replaced it.</summary>
    void AbandonWebLoad(int generation)
    {
        if (generation == Volatile.Read(ref _webGeneration))
            DeactivateWeb(pause: false);
    }

    /// <summary>Ends the web item: pauses the web view and raises <see cref="MediaStopped"/> and <see cref="MediaClosed"/>.</summary>
    void DeactivateWeb(bool pause)
    {
        MediaSourceInfo? info;
        bool wasPlaying;
        lock (_webGate)
        {
            if (!_webActive)
                return;

            info = _webInfo;
            wasPlaying = (MediaPlayerState)Volatile.Read(ref _webState) is MediaPlayerState.Playing or MediaPlayerState.Buffering;
            _webActive = false;
            _webItem = null;
            _webInfo = null;
            Volatile.Write(ref _webState, (int)MediaPlayerState.Stopped);
            Interlocked.Increment(ref _webGeneration);
        }

        if (pause && _webPlayer is { } player)
            ObserveWeb(player.PauseAsync());

        if (wasPlaying)
            HostEventDispatcher.Raise(this, MediaStopped, nameof(MediaStopped));
        if (info is not null)
            HostEventDispatcher.Raise(this, MediaClosed, new MediaClosedEventArgs(info), nameof(MediaClosed));
    }

    // ---- controls ----------------------------------------------------------------------------------------------

    /// <summary>Remembers a control issued before the item is ready; false when the item is ready (send it now).</summary>
    bool DeferWebControl(bool? play, TimeSpan? seek)
    {
        lock (_webGate)
        {
            if (_webReady)
                return false;
            if (play is { } p)
                _webPlayWhenReady = p;
            if (seek is { } s)
                _webSeekWhenReady = s;
            return true;
        }
    }

    bool WebPlay()
    {
        if (_webPlayer is not { } player)
            return false;
        if (!DeferWebControl(play: true, seek: null))
            ObserveWeb(player.PlayAsync());
        return true;
    }

    bool WebPause()
    {
        if (_webPlayer is not { } player)
            return false;
        if (!DeferWebControl(play: false, seek: null))
            ObserveWeb(player.PauseAsync());
        return true;
    }

    bool WebStop()
    {
        if (_webPlayer is not { } player)
            return false;
        if (!DeferWebControl(play: false, seek: TimeSpan.Zero))
        {
            ObserveWeb(player.PauseAsync());
            ObserveWeb(player.SeekAsync(TimeSpan.Zero));
        }

        if (SetWebState(MediaPlayerState.Stopped))
            HostEventDispatcher.Raise(this, MediaStopped, nameof(MediaStopped));
        return true;
    }

    bool WebSeek(double seconds)
    {
        if (_webPlayer is not { } player || double.IsNaN(seconds) || seconds < 0)
            return false;
        var target = TimeSpan.FromSeconds(seconds);
        if (!DeferWebControl(play: null, seek: target))
            ObserveWeb(player.SeekAsync(target));
        HostEventDispatcher.Raise(this, SeekCompleted, new MediaSeekCompletedEventArgs(target, target), nameof(SeekCompleted));
        return true;
    }

    /// <summary>The web view completes a command later; a failure is a diagnostic, not an exception on the caller.</summary>
    void ObserveWeb(Task task) =>
        _ = task.ContinueWith(
            t => DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, nameof(PlayerCore), $"Web media command failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    MediaPlayerState WebPlayerState => (MediaPlayerState)Volatile.Read(ref _webState);

    // ---- events from the web view --------------------------------------------------------------------------------

    void OnWebStateChanged(object? sender, WebMediaStateChangedEventArgs e)
    {
        if (!_webActive || sender is not IWebMediaPlayer { Current: { } current } || !ReferenceEquals(current, _webItem))
            return;

        switch (e.State)
        {
            case WebMediaState.Playing:
                if (SetWebState(MediaPlayerState.Playing))
                    HostEventDispatcher.Raise(this, MediaStarted, nameof(MediaStarted));
                break;
            case WebMediaState.Buffering:
                SetWebState(MediaPlayerState.Buffering);
                break;
            case WebMediaState.Paused:
                if (SetWebState(MediaPlayerState.Paused))
                    HostEventDispatcher.Raise(this, MediaPaused, new MediaPausedEventArgs(e.Position), nameof(MediaPaused));
                break;
            case WebMediaState.Ended:
                OnWebEnded();
                break;
            default:
                SetWebState(MediaPlayerState.Stopped);
                break;
        }
    }

    void OnWebEnded()
    {
        if (LoopMedia && _webPlayer is { } player)
        {
            ObserveWeb(player.SeekAsync(TimeSpan.Zero));
            ObserveWeb(player.PlayAsync());
            HostEventDispatcher.Raise(this, MediaLooped, new MediaLoopedEventArgs(TimeSpan.Zero), nameof(MediaLooped));
            return;
        }

        SetWebState(MediaPlayerState.Stopped);
        HostEventDispatcher.Raise(this, MediaEnded, nameof(MediaEnded));
    }

    void OnWebPositionChanged(object? sender, TimeSpan position)
    {
        if (!_webActive || sender is not IWebMediaPlayer { Current: { } current } player || !ReferenceEquals(current, _webItem))
            return;
        HostEventDispatcher.Raise(
            this,
            PositionChanged,
            new MediaPositionChangedEventArgs(position, player.Duration),
            nameof(PositionChanged));
    }

    void OnWebError(object? sender, WebMediaErrorEventArgs e)
    {
        var item = _webItem;
        if (!_webActive || item is null || sender is not IWebMediaPlayer { Current: { } current } || !ReferenceEquals(current, item))
            return;

        var code = e.Kind == WebMediaErrorKind.NotFound ? MediaErrorCode.FileNotFound : MediaErrorCode.OpenFailed;
        RaiseWebError(code, e.Message, item.DisplayName);
        DeactivateWeb(pause: false);
    }

    /// <summary>Sets the mirrored state and raises <see cref="StateChanged"/>; false when it did not change.</summary>
    bool SetWebState(MediaPlayerState state)
    {
        var previous = (MediaPlayerState)Interlocked.Exchange(ref _webState, (int)state);
        if (previous == state)
            return false;
        HostEventDispatcher.Raise(this, StateChanged, new MediaStateChangedEventArgs(previous, state), nameof(StateChanged));
        return true;
    }

    void RaiseWebError(MediaErrorCode code, string message, string? source) =>
        HostEventDispatcher.Raise(this, MediaError, new MediaErrorEventArgs(code, message, source), nameof(MediaError));
}
