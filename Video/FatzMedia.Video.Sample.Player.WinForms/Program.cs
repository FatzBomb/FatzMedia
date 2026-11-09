using FatzMedia.Video.Sample.Player.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;

namespace FatzMedia.Video.Sample.Player.WinForms;

internal static class Program
{
    /// <summary>Gets the debug log file: every diagnostic of this run (see <see cref="PlayerDebugLog"/>).</summary>
    public static string LogFilePath { get; } = PlayerDebugLog.DefaultPath("player-winforms-debug.log");

    /// <summary>
    /// Gets the renderer for new players: the command line's at startup, the toolbar's once it
    /// changes (<see cref="UseRenderer"/>). The auto chain probes a device, so it resolves off the
    /// UI thread while the window shows.
    /// </summary>
    internal static Task<PlayerRendererSelection> RendererSelection { get; private set; } = null!;

    /// <summary>Starts resolving <paramref name="request"/> for the players created from now on.</summary>
    internal static void UseRenderer(PlayerStartupOptions options, PlayerRendererRequest request) =>
        RendererSelection = PlayerRendererSelection.ResolveInBackground(options, request);

    [STAThread]
    private static void Main(string[] args)
    {
        // The flags every test player shares (see PlayerStartupOptions); this player has none of its own.
        PlayerStartupOptions options = PlayerStartupOptions.Parse(args);
        using PlayerDebugLog debugLog = PlayerDebugLog.Attach(LogFilePath, "Player.WinForms");
        UseRenderer(options, options.RendererRequest);

        // Per-monitor DPI so the surface's ClientSize is in device pixels (see the adapter README).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Dark, like the WPF and Avalonia players.
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new MainForm(options));
    }
}
