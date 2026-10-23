namespace FatzBomb.MediaControl.Player.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Per-monitor DPI so the surface's ClientSize is in device pixels (see the adapter README).
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal))));
    }
}
