using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.WinForms;

internal static class Program
{
    /// <summary>Gets the debug log file: every diagnostic of this run (see <see cref="PlayerDebugLog"/>).</summary>
    public static string LogFilePath { get; } = PlayerDebugLog.DefaultPath("player-winforms-debug.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // The flags every test player shares (see PlayerStartupOptions); --renderer=auto|gpu|warp.
        PlayerStartupOptions options = PlayerStartupOptions.Parse(args);
        using PlayerDebugLog debugLog = PlayerDebugLog.Attach(LogFilePath, "Player.WinForms");

        // Per-monitor DPI so the surface's ClientSize is in device pixels (see the adapter README).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Dark, like the WPF and Avalonia players.
        Application.SetColorMode(SystemColorMode.Dark);
        Application.Run(new MainForm(options));
    }
}
