using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.Shared.Presentation;

/// <summary>
/// The players of one test-player window (WPF, Avalonia, WinForms; ENG-039 R1): it builds them
/// again when the toolbar's player count or renderer changes, shows one alone full screen (F11,
/// double-click, Escape), feeds every one a process reading once a second, and disposes them all
/// before the window closes. The host (<see cref="IPlayerSetHost{TSlot}"/>) creates and places the
/// players and owns the window state.
/// </summary>
/// <remarks>
/// UI thread only: the awaits continue on the caller's context, so the host's UI thread drives
/// every step. <see cref="RebuildAsync"/> and <see cref="ShutdownAsync"/> never throw; failures go
/// to <see cref="IPlayerSetHost{TSlot}.ReportFailure"/>.
/// </remarks>
public sealed class PlayerSetController<TSlot>
    where TSlot : class, IPlayerSlot
{
    /// <summary>How often the players' stats lines are refreshed.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    private readonly IPlayerSetHost<TSlot> _host;
    private readonly List<TSlot> _slots = [];
    private readonly IPlayerTimer _statsTimer;
    private readonly Func<ProcessSample> _sample;
    private readonly ProcessSampler? _ownedSampler;
    private Task _rebuild = Task.CompletedTask;
    private bool _rebuilding;
    private bool _rebuildAgain;
    private bool _firstBuild = true;
    private TSlot? _fullScreen;

    /// <param name="host">The window.</param>
    /// <param name="dispatcher">The window's UI thread; its timer runs the stats tick.</param>
    /// <param name="sample">The process reading for the stats lines; a <see cref="ProcessSampler"/> the controller owns when null.</param>
    /// <param name="statsInterval">The stats tick's interval; <see cref="StatsInterval"/> when null.</param>
    public PlayerSetController(
        IPlayerSetHost<TSlot> host,
        IPlayerDispatcher dispatcher,
        Func<ProcessSample>? sample = null,
        TimeSpan? statsInterval = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (sample is null)
        {
            _ownedSampler = new ProcessSampler();
            sample = _ownedSampler.Sample;
        }

        _sample = sample;
        _statsTimer = dispatcher.CreateTimer(statsInterval ?? StatsInterval, OnStatsTick);
    }

    /// <summary>Gets the players, in order (player 1 first).</summary>
    public IReadOnlyList<TSlot> Slots => _slots;

    /// <summary>Gets the player shown alone full screen, or null.</summary>
    public TSlot? FullScreenSlot => _fullScreen;

    /// <summary>Gets whether <see cref="StartAsync"/> ran: toolbar changes rebuild only from then on.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>Gets whether <see cref="ShutdownAsync"/> started: nothing is built any more.</summary>
    public bool IsShuttingDown { get; private set; }

    /// <summary>Gets whether <see cref="ShutdownAsync"/> finished: every player is disposed and the window may close.</summary>
    public bool IsShutDown { get; private set; }

    /// <summary>Starts the stats tick and builds the first players (the only build with <c>atStartup</c>). Returns that build.</summary>
    public Task StartAsync()
    {
        if (IsStarted || IsShuttingDown)
            return _rebuild;

        IsStarted = true;
        _statsTimer.Start();
        return RebuildAsync();
    }

    /// <summary>
    /// Disposes the players and creates <see cref="IPlayerSetHost{TSlot}.RequestedPlayerCount"/>
    /// new ones, initializing them one after the other (native loading is process-wide and happens
    /// on the first). A request during a rebuild runs once more after it, however many came.
    /// Returns the rebuild in progress, which never faults. Before <see cref="StartAsync"/> and after
    /// <see cref="ShutdownAsync"/> it does nothing.
    /// </summary>
    public Task RebuildAsync()
    {
        if (!IsStarted || IsShuttingDown)
            return _rebuild;

        if (_rebuilding)
        {
            _rebuildAgain = true;
            return _rebuild;
        }

        _rebuilding = true;
        _rebuild = RebuildLoopAsync();
        return _rebuild;
    }

    /// <summary>
    /// Stops the stats tick, leaves full screen, waits for the rebuild in progress (it builds
    /// nothing more) and disposes every player. Returns true when the host may close: once this
    /// call finished (and on any call after), false for a call made while another one runs (that
    /// one closes). Never throws.
    /// </summary>
    public async Task<bool> ShutdownAsync()
    {
        if (IsShutDown)
            return true;
        if (IsShuttingDown)
            return false;

        IsShuttingDown = true;
        try
        {
            _statsTimer.Stop();
            ExitFullScreen();
            // A toolbar rebuild may still be disposing the old players or starting the new ones: let it
            // finish (it stops at IsShuttingDown) so no player outlives the window.
            await _rebuild;

            TSlot[] slots = [.. _slots];
            _slots.Clear();
            foreach (TSlot slot in slots)
                await DisposeSlotAsync(slot);
        }
        catch (Exception ex)
        {
            _host.ReportFailure("Closing the players failed", ex);
        }
        finally
        {
            _statsTimer.Dispose();
            _ownedSampler?.Dispose();
            IsShutDown = true;
        }

        return true;
    }

    /// <summary>Shows <paramref name="slot"/> alone full screen, or leaves full screen when it already is.</summary>
    public void ToggleFullScreen(TSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (ReferenceEquals(_fullScreen, slot))
            ExitFullScreen();
        else
            EnterFullScreen(slot);
    }

    /// <summary>Shows <paramref name="slot"/> alone full screen: the host's layout, then the window, then the player.</summary>
    public void EnterFullScreen(TSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (IsShuttingDown || !_slots.Contains(slot))
            return;

        ExitFullScreen();
        _fullScreen = slot;
        _host.Layout(_slots, slot);
        _host.SetWindowFullScreen(true);
        slot.SetFullScreen(true);
    }

    /// <summary>Leaves full screen, when on: the player, then the window, then the host's layout.</summary>
    public void ExitFullScreen()
    {
        if (_fullScreen is not { } slot)
            return;

        _fullScreen = null;
        slot.SetFullScreen(false);
        _host.SetWindowFullScreen(false);
        _host.Layout(_slots, null);
    }

    /// <summary>
    /// F11: leaves full screen, or shows the focused player (else player 1) full screen. Returns
    /// whether the key was used (false without players).
    /// </summary>
    public bool HandleFullScreenKey()
    {
        if (_slots.Count == 0 || IsShuttingDown)
            return false;

        if (_fullScreen is not null)
            ExitFullScreen();
        else
            EnterFullScreen(_slots.FirstOrDefault(static slot => slot.IsKeyboardFocusWithin) ?? _slots[0]);
        return true;
    }

    /// <summary>
    /// Escape, one rule for every host: closes the full-screen player's flyout, else the focused
    /// player's, else any open one; with no flyout to close, leaves full screen. Returns whether the
    /// key was used.
    /// </summary>
    public bool HandleEscapeKey()
    {
        TSlot? target = _fullScreen
            ?? _slots.Where(static slot => slot.HasOpenFlyout)
                .OrderByDescending(static slot => slot.IsKeyboardFocusWithin)
                .FirstOrDefault();
        if (target?.CloseFlyout() == true)
            return true;

        if (_fullScreen is null)
            return false;

        ExitFullScreen();
        return true;
    }

    private async Task RebuildLoopAsync()
    {
        try
        {
            do
            {
                _rebuildAgain = false;
                try
                {
                    await RebuildOnceAsync();
                }
                catch (Exception ex)
                {
                    // The players built so far stay; the next rebuild or the shutdown disposes them.
                    _host.ReportFailure("Rebuilding the players failed", ex);
                }
            }
            while (_rebuildAgain && !IsShuttingDown);
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private async Task RebuildOnceAsync()
    {
        ExitFullScreen();
        TSlot[] old = [.. _slots];
        _slots.Clear();
        foreach (TSlot slot in old)
            _host.Detach(slot);
        foreach (TSlot slot in old)
            await DisposeSlotAsync(slot);

        // Closing started while the old players were disposed: build nothing (ShutdownAsync waits for this rebuild).
        if (IsShuttingDown)
            return;

        var count = Math.Clamp(_host.RequestedPlayerCount, 1, PlayerStartupOptions.MaxPlayers);
        // Cleared before anything is created or initialized: a superseded first build still was the first.
        var atStartup = _firstBuild;
        _firstBuild = false;
        for (var index = 1; index <= count; index++)
        {
            TSlot slot = _host.CreateSlot(index, atStartup);
            slot.FullScreenToggleRequested += OnFullScreenToggleRequested;
            _slots.Add(slot);
            _host.Attach(slot);
        }

        _host.Layout(_slots, null);

        // Created one after the other: native loading is process-wide and happens on the first.
        foreach (TSlot slot in _slots.ToList())
        {
            if (IsShuttingDown || _rebuildAgain || !_slots.Contains(slot))
                return;

            await slot.InitializeAsync();
        }
    }

    private async Task DisposeSlotAsync(TSlot slot)
    {
        slot.FullScreenToggleRequested -= OnFullScreenToggleRequested;
        try
        {
            await slot.DisposeAsync();
        }
        catch (Exception ex)
        {
            // One player's failed teardown must not keep the others alive or the window open.
            _host.ReportFailure("Closing a player failed", ex);
        }
    }

    private void OnFullScreenToggleRequested(object? sender, EventArgs e)
    {
        if (sender is TSlot slot)
            ToggleFullScreen(slot);
    }

    private void OnStatsTick()
    {
        if (IsShuttingDown)
            return;

        ProcessSample sample = _sample();
        foreach (TSlot slot in _slots)
            slot.UpdateStatus(sample);
    }
}
