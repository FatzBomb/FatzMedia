using FatzBomb.MediaControl.Core.Configuration;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// The player of a self-contained karaoke or background control (Stage 046A): creates it on
/// demand, adopts it once, abandons loads that a newer source replaced, applies
/// <see cref="LightweightPlayerPolicy"/> to every load, and releases it. Moved out of the WPF
/// adapter so the WPF, Avalonia and WinForms <c>KaraokeSurface</c> and <c>BackgroundSurface</c>
/// share it; each control keeps only its framework's properties, layout and surface binding.
/// </summary>
/// <remarks>
/// <para>
/// Threading: call the members from the control's UI thread. Creating the player (which loads
/// native libraries and blocks) runs on the thread pool; the awaits of
/// <see cref="EnsurePlayerAsync"/> and <see cref="LoadAsync(MediaInputSource, IMediaControlPlatformFactory?, Action{MediaControlPlayer}?)"/>
/// resume on the caller's synchronization context on purpose (unlike engine code), so
/// <see cref="PlayerChanged"/> and the load steps run on the UI thread. <see cref="MediaClosed"/>
/// is raised on the player's event thread: marshal it.
/// </para>
/// <para>
/// Lifetime: <see cref="ReleasePlayer"/> (a control's unload) and <see cref="Dispose"/> abandon
/// every pending load and creation; a player created for an abandoned request is disposed, never
/// adopted. Concurrent requests share one creation.
/// </para>
/// </remarks>
public sealed class LightweightPlayerController : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<MediaPlayerConfiguration, IMediaControlPlatformFactory, MediaControlPlayer> _createPlayer;
    private MediaControlPlayer? _player;
    private MediaControlPlayer? _announcedPlayer;
    private Task<MediaControlPlayer?>? _creation;
    private int _lifetime;
    private int _reloadVersion;
    private int _reloadQueued;
    private bool _autoPlay = true;
    private volatile bool _disposed;

    /// <param name="kind">Which control this is; decides the player's mode and the load rules.</param>
    /// <param name="diagnosticCategory">The category of the controller's notices (the control's).</param>
    /// <param name="createPlayer">
    /// Creates the player (on the thread pool) for a configuration and platform factory; by default
    /// a <see cref="MediaControlPlayer"/> with <see cref="NativeLibraryPaths"/>. An adapter can
    /// return its own player type.
    /// </param>
    public LightweightPlayerController(
        LightweightPlayerKind kind,
        string diagnosticCategory,
        Func<MediaPlayerConfiguration, IMediaControlPlatformFactory, MediaControlPlayer>? createPlayer = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown lightweight player kind.");
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCategory);

        Kind = kind;
        DiagnosticCategory = diagnosticCategory;
        _createPlayer = createPlayer ?? ((configuration, platformFactory) =>
            new MediaControlPlayer(platformFactory, configuration, NativeLibraryPaths ?? DLLPath.GetConfiguredNativeLibraryPaths()));
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="Player"/> was adopted (bind the surface to it) or
    /// released (unbind the surface; the player is disposed right after the handlers return).
    /// </summary>
    public event EventHandler? PlayerChanged;

    /// <summary>Raised on the player's event thread when its media closed (marshal it).</summary>
    public event EventHandler? MediaClosed;

    /// <summary>Gets which control this is.</summary>
    public LightweightPlayerKind Kind { get; }

    /// <summary>Gets the category of the controller's notices.</summary>
    public string DiagnosticCategory { get; }

    /// <summary>
    /// Gets or sets the native library folders of the default player creation
    /// (<see langword="null"/>: <see cref="DLLPath.GetConfiguredNativeLibraryPaths"/>). Read when the
    /// player is created.
    /// </summary>
    public IReadOnlyList<string>? NativeLibraryPaths { get; set; }

    /// <summary>Gets the adopted player, or <see langword="null"/> before creation and after release.</summary>
    public MediaControlPlayer? Player
    {
        get
        {
            lock (_gate)
                return _announcedPlayer;
        }
    }

    /// <summary>Gets the adopted player as a session (diagnostic owner, surface binding).</summary>
    public IMediaControlSession? Session => Player;

    /// <summary>Gets whether a player exists or is being created (its platform factory is fixed then).</summary>
    public bool HasPlayerOrPendingCreation
    {
        get
        {
            lock (_gate)
                return _player is not null || _creation is not null;
        }
    }

    /// <summary>Gets whether the player has media open.</summary>
    public bool IsMediaOpen => Player?.IsOpen == true;

    /// <summary>
    /// Gets or sets whether accepted media starts by itself (default <see langword="true"/>): the
    /// player's <see cref="MediaControlPlayer.PlayOnLoad"/> outside a load, and the start after a load.
    /// </summary>
    public bool AutoPlay
    {
        get => Volatile.Read(ref _autoPlay);
        set
        {
            Volatile.Write(ref _autoPlay, value);
            if (Player is { } player)
                player.PlayOnLoad = value;
        }
    }

    /// <summary>
    /// Marks a source reload as queued. Returns <see langword="true"/> when the caller must schedule
    /// it (on its UI thread), <see langword="false"/> when one is already scheduled: several source
    /// changes in one turn reload once.
    /// </summary>
    public bool TryQueueReload() => !_disposed && Interlocked.Exchange(ref _reloadQueued, 1) == 0;

    /// <summary>Called by the scheduled reload when it runs, before it reads the source.</summary>
    public void ClearQueuedReload() => Volatile.Write(ref _reloadQueued, 0);

    /// <summary>
    /// Starts a new reload and returns its number; every earlier one is abandoned at its next step.
    /// <see cref="LoadAsync(MediaInputSource, IMediaControlPlatformFactory?, Action{MediaControlPlayer}?)"/>
    /// calls it; a control calls it for a change that loads nothing (a solid colour).
    /// </summary>
    public int BeginReload() => Interlocked.Increment(ref _reloadVersion);

    /// <summary>Gets whether <paramref name="reload"/> is still the latest and the controller is not disposed.</summary>
    public bool IsCurrentReload(int reload) => !_disposed && Volatile.Read(ref _reloadVersion) == reload;

    /// <summary>
    /// Returns the player, creating it first if needed. Without a platform factory nothing is
    /// created: a Warning is raised (process-wide, as there is no player yet) and the result is
    /// <see langword="null"/>. A failed creation raises an Error and returns <see langword="null"/>;
    /// a request abandoned by <see cref="ReleasePlayer"/> meanwhile also returns <see langword="null"/>.
    /// </summary>
    public async Task<MediaControlPlayer?> EnsurePlayerAsync(IMediaControlPlatformFactory? platformFactory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<MediaControlPlayer?> creation;
        int lifetime;
        lock (_gate)
        {
            if (_announcedPlayer is not null)
                return _announcedPlayer;

            lifetime = _lifetime;
            if (_player is null && _creation is null)
            {
                if (platformFactory is null)
                {
                    DiagnosticRouter.Raise(
                        null,
                        MediaDiagnosticLevel.Warning,
                        DiagnosticCategory,
                        "PlatformFactory must be set before media is loaded; nothing is loaded.");
                    return null;
                }

                _creation = CreateAndAdoptAsync(platformFactory, lifetime);
            }

            creation = _creation ?? Task.FromResult(_player);
        }

        // Resumes on the caller's context: the announcement below runs on the UI thread.
        _ = await creation;
        MediaControlPlayer? announced = null;
        MediaControlPlayer? result;
        lock (_gate)
        {
            result = !_disposed && lifetime == _lifetime ? _player : null;
            if (result is not null && !ReferenceEquals(_announcedPlayer, result))
            {
                _announcedPlayer = result;
                announced = result;
            }
        }

        if (announced is not null)
            PlayerChanged?.Invoke(this, EventArgs.Empty);

        return result;
    }

    /// <summary>
    /// Loads <paramref name="source"/> as the control's media: creates the player if needed,
    /// loads without sidecars and without starting, keeps the media only when
    /// <see cref="LightweightPlayerPolicy.AcceptsLoadedMedia"/> does (a rejected item is closed),
    /// and starts it when <see cref="AutoPlay"/> and <see cref="LightweightPlayerPolicy.PlaysAfterLoad"/>
    /// say so. A newer load, a release or a dispose abandons it.
    /// </summary>
    /// <param name="source">The media.</param>
    /// <param name="platformFactory">The factory used if the player must be created.</param>
    /// <param name="beforeLoad">Runs on the UI thread once the player exists, before the load (for example to show the media layer).</param>
    /// <returns><see langword="true"/> when the media was loaded and accepted by this, still current, load.</returns>
    public async Task<bool> LoadAsync(
        MediaInputSource source,
        IMediaControlPlatformFactory? platformFactory,
        Action<MediaControlPlayer>? beforeLoad = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var reload = BeginReload();
        MediaControlPlayer? player = await EnsurePlayerAsync(platformFactory);
        if (player is null || !IsCurrentReload(reload))
            return false;

        beforeLoad?.Invoke(player);
        bool accepted;
        try
        {
            player.PlayOnLoad = false;
            var loaded = await player.LoadAsync(source, ignoreSidecar: true);
            if (!IsCurrentReload(reload))
                return false;

            accepted = loaded && LightweightPlayerPolicy.AcceptsLoadedMedia(Kind, player.Content, source.FilePath);
            if (!accepted)
            {
                if (loaded)
                    player.Close();
            }
            else if (AutoPlay && LightweightPlayerPolicy.PlaysAfterLoad(Kind, player.Content))
            {
                _ = await player.PlayAsync();
            }
        }
        catch (Exception ex) when ((ex is ObjectDisposedException or OperationCanceledException) && !IsCurrentReload(reload))
        {
            // Released (the player disposed) while loading or starting.
            return false;
        }

        if (!IsCurrentReload(reload))
            return false;

        player.PlayOnLoad = AutoPlay;
        return accepted;
    }

    /// <inheritdoc cref="LoadAsync(MediaInputSource, IMediaControlPlatformFactory?, Action{MediaControlPlayer}?)"/>
    public Task<bool> LoadAsync(
        string path,
        IMediaControlPlatformFactory? platformFactory,
        Action<MediaControlPlayer>? beforeLoad = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadAsync(MediaInputSource.FromFile(path), platformFactory, beforeLoad);
    }

    /// <summary>Starts the player's media (<see langword="false"/> without a player).</summary>
    public Task<bool> PlayAsync() => Player?.PlayAsync() ?? Task.FromResult(false);

    /// <summary>Pauses the player's media (<see langword="false"/> without a player).</summary>
    public Task<bool> PauseAsync() => Player?.PauseAsync() ?? Task.FromResult(false);

    /// <summary>Stops the player's media (<see langword="false"/> without a player).</summary>
    public Task<bool> StopAsync() => Player?.StopAsync() ?? Task.FromResult(false);

    /// <summary>
    /// Releases the player (UI thread): pending loads and creations are abandoned,
    /// <see cref="PlayerChanged"/> lets the control unbind its surface, then the player is disposed
    /// (this blocks until it is). The next load creates a new one.
    /// </summary>
    public void ReleasePlayer()
    {
        MediaControlPlayer? player;
        bool wasAnnounced;
        lock (_gate)
        {
            _lifetime++;
            _ = Interlocked.Increment(ref _reloadVersion);
            player = _player;
            wasAnnounced = _announcedPlayer is not null;
            _player = null;
            _announcedPlayer = null;
            // A creation still running settles itself: it sees the new lifetime and disposes its
            // player on the thread pool.
            _creation = null;
        }

        if (player is null)
            return;

        player.MediaClosed -= OnPlayerMediaClosed;
        if (wasAnnounced)
            PlayerChanged?.Invoke(this, EventArgs.Empty);
        player.Dispose();
    }

    /// <summary>Releases the player; the controller cannot be used afterwards.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Volatile.Write(ref _reloadQueued, 0);
        ReleasePlayer();
    }

    private async Task<MediaControlPlayer?> CreateAndAdoptAsync(IMediaControlPlatformFactory platformFactory, int lifetime)
    {
        MediaControlPlayer? player = null;
        try
        {
            MediaPlayerConfiguration configuration = LightweightPlayerPolicy.CreateConfiguration(Kind);
            // Always yields, so the caller (which holds the gate while it stores this task) never
            // runs the adoption below inline.
            player = await Task.Run(() => _createPlayer(configuration, platformFactory)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            LightweightPlayerPolicy.ConfigurePlayer(player, Kind);
            player.PlayOnLoad = AutoPlay;
            player.MediaClosed += OnPlayerMediaClosed;
        }
        catch (Exception ex)
        {
            if (player is not null)
                player.MediaClosed -= OnPlayerMediaClosed;
            player?.Dispose();
            player = null;

            // No player exists yet, so the diagnostic has no owner: every player's log reports it.
            DiagnosticRouter.Raise(
                null,
                MediaDiagnosticLevel.Error,
                DiagnosticCategory,
                $"Creating the player failed: {ex.Message}",
                innerException: ex);
        }

        var adopted = false;
        lock (_gate)
        {
            if (player is not null && !_disposed && lifetime == _lifetime)
            {
                _player = player;
                adopted = true;
            }

            if (lifetime == _lifetime)
                _creation = null;
        }

        if (!adopted && player is not null)
        {
            // Abandoned meanwhile (released or disposed): never adopted, freed here, off the UI thread.
            player.MediaClosed -= OnPlayerMediaClosed;
            player.Dispose();
            return null;
        }

        return player;
    }

    private void OnPlayerMediaClosed(object? sender, MediaClosedEventArgs e)
    {
        if (!_disposed)
            MediaClosed?.Invoke(this, EventArgs.Empty);
    }
}
