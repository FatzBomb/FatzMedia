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
    /// Gets whether the software renderer is expected before <see cref="RendererSelection"/>
    /// resolves: only an explicit <c>--software</c> / <c>--renderer=software</c> is known up front.
    /// </summary>
    public static bool ExpectsSoftwareRenderer { get; private set; }

    /// <summary>
    /// Gets the renderer chosen at startup (Stage 036A: GPU, then WARP, then software). The auto
    /// chain creates a probe device, so it resolves off the UI thread and the window shows at once;
    /// the main view model waits for it before creating the engine.
    /// </summary>
    internal static Task<PlayerRendererSelection> RendererSelection { get; private set; } = null!;

    /// <summary>Gets whether the surface hides until visual content is ready (<c>--no-auto-hide</c> turns it off, as in every player).</summary>
    public static bool AutoHideSurfaces { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PlayerStartupOptions options = PlayerStartupOptions.Parse(e.Args);
        StartupOptions = options;
        PlayerRendererChoice rendererChoice = options.SoftwareRenderer
            ? PlayerRendererChoice.Software
            : PlayerRendererChoice.Auto;
        var invalidRendererArgument = false;
        if (options.Renderer is { } rendererArgument)
        {
            if (PlayerRendererSelection.TryParse(rendererArgument, out PlayerRendererChoice parsed))
                rendererChoice = parsed;
            else
                invalidRendererArgument = true;
        }

        AutoHideSurfaces = !PlayerStartupOptions.HasFlag(e.Args, "--no-auto-hide");

        _debugLog = PlayerDebugLog.Attach(LogFilePath, "Player");

        ExpectsSoftwareRenderer = rendererChoice == PlayerRendererChoice.Software;
        var invalidRendererValue = invalidRendererArgument ? options.Renderer : null;
        RendererSelection = Task.Run(() => ResolveRenderer(rendererChoice, invalidRendererValue));
    }

    private static PlayerRendererSelection ResolveRenderer(PlayerRendererChoice choice, string? invalidValue)
    {
        PlayerRendererSelection selection;
        try
        {
            selection = PlayerRendererSelection.Resolve(choice, StartupOptions.HdrOutput);
        }
        catch (Exception ex)
        {
            selection = PlayerRendererSelection.SoftwareAfterFailure(ex);
        }

        if (invalidValue is not null)
            selection.AddLogLine(MediaDiagnosticLevel.Warning, $"Unknown --renderer value '{invalidValue}' (use auto, gpu, warp or software); using {choice.ToString().ToLowerInvariant()}.");

        Trace.WriteLine($"[Player] Renderer: {selection.Summary}");
        return selection;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _debugLog?.Dispose();
        base.OnExit(e);
    }
}
