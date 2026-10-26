using FatzBomb.MediaControl.Player.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.WinForms;

internal static class Program
{
    /// <summary>Gets the debug log file: every diagnostic of this run (see <see cref="PlayerDebugLog"/>).</summary>
    public static string LogFilePath { get; } = PlayerDebugLog.DefaultPath("player-winforms-debug.log");

    /// <summary>
    /// Gets the renderer chosen at startup for every player (the shared request; the auto chain
    /// probes a device, so it resolves off the UI thread while the window shows).
    /// </summary>
    internal static Task<PlayerRendererSelection> RendererSelection { get; private set; } = null!;

    [STAThread]
    private static void Main(string[] args)
    {
        // The flags every test player shares (see PlayerStartupOptions); this player has none of its own.
        PlayerStartupOptions options = PlayerStartupOptions.Parse(args);
        using PlayerDebugLog debugLog = PlayerDebugLog.Attach(LogFilePath, "Player.WinForms");
        RendererSelection = PlayerRendererSelection.ResolveInBackground(options);

        // Per-monitor DPI so the surface's ClientSize is in device pixels (see the adapter README).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Dark, like the WPF and Avalonia players.
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new MainForm(options));
    }
}
