using FatzMedia.Video.Sample.Player.Shared.Presentation;

namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>
/// One player of a test-player window (the WPF <c>PlayerView</c>, the Avalonia and WinForms
/// <c>PlayerPanel</c>), as <see cref="PlayerSetController{TSlot}"/> drives it. UI thread only.
/// </summary>
public interface IPlayerSlot
{
    /// <summary>Raised when the user asks for full screen (double-click, button); the controller owns the decision.</summary>
    event EventHandler? FullScreenToggleRequested;

    /// <summary>Raised when this player's renderer lost its GPU device for good (ENG-050); the controller asks the window to fall back.</summary>
    event EventHandler? RendererDeviceLost;

    /// <summary>Gets whether this player has the keyboard focus (F11 shows the focused player).</summary>
    bool IsKeyboardFocusWithin { get; }

    /// <summary>Gets whether one of this player's flyouts is open (Escape closes it).</summary>
    bool HasOpenFlyout { get; }

    /// <summary>Creates the engine and loads the startup media.</summary>
    Task InitializeAsync();

    /// <summary>Lets go of the surfaces and disposes the engine (off the UI thread).</summary>
    Task DisposeAsync();

    /// <summary>Refreshes the stats line with the window's process reading.</summary>
    void UpdateStatus(ProcessSample process);

    /// <summary>Enters or leaves this player's full-screen layout (the window state is the host's).</summary>
    void SetFullScreen(bool fullScreen);

    /// <summary>Closes the open flyout; returns whether one was open.</summary>
    bool CloseFlyout();
}

/// <summary>
/// The window that shows 1-<see cref="PlayerStartupOptions.MaxPlayers"/> players for a
/// <see cref="PlayerSetController{TSlot}"/>: it creates them, places them and owns the window
/// state. UI thread only.
/// </summary>
public interface IPlayerSetHost<TSlot>
    where TSlot : class, IPlayerSlot
{
    /// <summary>Gets how many players the toolbar asks for now (1 to <see cref="PlayerStartupOptions.MaxPlayers"/>).</summary>
    int RequestedPlayerCount { get; }

    /// <summary>
    /// Creates player <paramref name="index"/> (1-based) with the toolbar's current renderer.
    /// <paramref name="atStartup"/> is true only for the window's first build (see
    /// <see cref="PlayerStartupOptions.ForBuild"/> and <see cref="PlayerStartupOptions.OpensSecondScreenAtStartup"/>).
    /// </summary>
    TSlot CreateSlot(int index, bool atStartup);

    /// <summary>Adds a created player to the window.</summary>
    void Attach(TSlot slot);

    /// <summary>Removes a player from the window, before it is disposed.</summary>
    void Detach(TSlot slot);

    /// <summary>
    /// Places <paramref name="slots"/>: all of them in the grid with the toolbar, or only
    /// <paramref name="fullScreen"/> without the toolbar.
    /// </summary>
    void Layout(IReadOnlyList<TSlot> slots, TSlot? fullScreen);

    /// <summary>Makes the window full screen (saving its state) or restores it.</summary>
    void SetWindowFullScreen(bool fullScreen);

    /// <summary>
    /// A player's renderer lost its GPU device for good (ENG-050): switch the window to the next renderer
    /// (<see cref="PlayerRendererRules.FallbackAfterDeviceLoss"/>) and rebuild the players, or report that
    /// there is none. Called at most once per build.
    /// </summary>
    void FallBackRenderer();

    /// <summary>Reports a failure the controller caught (a rebuild, a player's teardown) on the status line and the log.</summary>
    void ReportFailure(string what, Exception exception);
}
