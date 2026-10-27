using System.Diagnostics;
using System.Windows;
using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player;

public partial class App : Application
{
    private PlayerDebugLog? _debugLog;

    public static string LogFilePath { get; } = PlayerDebugLog.DefaultPath("player-debug.log");

    /// <summary>Gets the command-line options shared by the test players (see <see cref="PlayerStartupOptions"/>).</summary>
    public static PlayerStartupOptions StartupOptions { get; private set; } = new();

    /// <summary>
    /// Gets whether a frame-sink renderer is expected before <see cref="RendererSelection"/>
    /// resolves: only an explicit software or Vulkan request is known up front.
    /// </summary>
    public static bool ExpectsSoftwareRenderer { get; private set; }

    /// <summary>
    /// Gets the renderer for new players (Stage 036A: GPU, then WARP, then software; the shared
    /// request since Stage 046A slice 3; the toolbar's choice once it changes, see
    /// <see cref="UseRenderer"/>). The auto chain creates a probe device, so it resolves off the UI
    /// thread and the window shows at once; each player's view model waits for it before creating
    /// its engine.
    /// </summary>
    internal static Task<PlayerRendererSelection> RendererSelection { get; private set; } = null!;

    /// <summary>Gets whether the surface hides until visual content is ready (<c>--no-auto-hide</c> turns it off, as in every player).</summary>
    public static bool AutoHideSurfaces { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PlayerStartupOptions options = PlayerStartupOptions.Parse(e.Args);
        StartupOptions = options;
        AutoHideSurfaces = options.AutoHideSurfaces;

        _debugLog = PlayerDebugLog.Attach(LogFilePath, "Player");

        UseRenderer(options.RendererRequest);
        if (options.LightweightSurfacesWindow)
            _ = ShowLightweightSurfacesWindowAsync();
    }

    /// <summary>Stage 046A slice 2: the background and karaoke smoke window, with the startup renderer.</summary>
    private async Task ShowLightweightSurfacesWindowAsync()
    {
        PlayerRendererSelection selection = await RendererSelection;

        // An owner must have been shown: wait for the main window (StartupUri) to load.
        if (MainWindow is { IsLoaded: false } main)
        {
            var loaded = new TaskCompletionSource();
            main.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }

        var window = new LightweightSurfacesWindow(
            () => new WindowsMediaControlPlatformFactory(selection.CreateFactory()),
            PlayerNativePaths.Combine(StartupOptions.NativeDirectories, PlayerNativeLibraries.ResolveSearchPaths()),
            selection.UsesSoftwareRenderer || StartupOptions.PreferFrameSinkSurface ? Wpf.WpfPresentationMode.WpfComposited : Wpf.WpfPresentationMode.NativeHwndOverlay,
            AutoHideSurfaces)
        {
            Owner = MainWindow,
        };
        window.Show();
    }

    /// <summary>
    /// Starts resolving <paramref name="request"/> for the players created from now on: at startup
    /// the command line's choice, later the toolbar's (the window then recreates its players).
    /// </summary>
    internal static void UseRenderer(PlayerRendererRequest request)
    {
        // Only an explicit frame-sink renderer is known up front; auto may still land on software.
        ExpectsSoftwareRenderer = request is PlayerRendererRequest.Software or PlayerRendererRequest.Vulkan;
        RendererSelection = ResolveRendererAsync(StartupOptions, request);
    }

    private static async Task<PlayerRendererSelection> ResolveRendererAsync(PlayerStartupOptions options, PlayerRendererRequest request)
    {
        PlayerRendererSelection selection = await PlayerRendererSelection.ResolveInBackground(options, request).ConfigureAwait(false);
        Trace.WriteLine($"[Player] Renderer: {selection.Summary}");
        return selection;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _debugLog?.Dispose();
        base.OnExit(e);
    }
}
