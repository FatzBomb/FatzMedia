using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FatzMedia.Video.Sample.Player.Shared.Hosting;

namespace FatzMedia.Video.Sample.Player.Avalonia;

public partial class App : Application
{
    /// <summary>Gets the debug log file: every player's diagnostics of this run (see <see cref="PlayerDebugLog"/>).</summary>
    public static string LogFilePath { get; } = PlayerDebugLog.DefaultPath("player-avalonia-debug.log");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
