using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatzMedia.Video.Wpf;

namespace FatzMedia.Video.Sample.Player;

/// <summary>
/// Smoke window for the adapter's <see cref="BackgroundSurface"/> and <see cref="KaraokeSurface"/>
/// (<c>--lightweight-surfaces</c>, Stage 046A slice 2; the Avalonia and WinForms players have the
/// same window). Each control creates its own player with the renderer chosen at startup.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Released by the Closed handler; the window itself is not IDisposable.")]
sealed class LightweightSurfacesWindow : Window
{
    static readonly Color[] Colours = [Colors.DarkSlateBlue, Colors.DarkGreen, Colors.Black];

    readonly BackgroundSurface _background;
    readonly KaraokeSurface _karaoke;
    readonly TextBox _path = new() { MinWidth = 380, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };
    int _colour;

    public LightweightSurfacesWindow(
        Func<IPlatformFactory> createPlatformFactory,
        IReadOnlyList<string> nativeLibraryPaths,
        WpfPresentationMode presentationMode,
        bool autoHide)
    {
        Title = "Background and karaoke surfaces (--lightweight-surfaces)";
        Width = 1100;
        Height = 520;
        Background = Brushes.Black;
        _background = new BackgroundSurface(createPlatformFactory())
        {
            NativeLibraryPaths = nativeLibraryPaths,
            PresentationMode = presentationMode,
            AutoHide = autoHide,
            OverlayContent = new TextBlock
            {
                Text = "Overlay above the background",
                Foreground = Brushes.White,
                FontSize = 22,
                Margin = new Thickness(16),
            },
        };
        _karaoke = new KaraokeSurface(createPlatformFactory())
        {
            NativeLibraryPaths = nativeLibraryPaths,
            PresentationMode = presentationMode,
            AutoHide = autoHide,
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.DimGray };
        bar.Children.Add(_path);
        bar.Children.Add(Button("Browse...", Browse));
        bar.Children.Add(Button("Background", () => _background.Source = PathOrNull()));
        bar.Children.Add(Button("Karaoke", () => _karaoke.Source = PathOrNull()));
        bar.Children.Add(Button("Colour", () => _background.SetColor(Colours[_colour++ % Colours.Length])));
        bar.Children.Add(Button("Stop both", () =>
        {
            _ = _background.StopAsync();
            _ = _karaoke.StopAsync();
        }));

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_karaoke, 1);
        _ = panes.Children.Add(_background);
        _ = panes.Children.Add(_karaoke);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        _ = root.Children.Add(bar);
        _ = root.Children.Add(panes);
        Content = root;
        Closed += (_, _) =>
        {
            _background.Dispose();
            _karaoke.Dispose();
        };
    }

    string? PathOrNull() => string.IsNullOrWhiteSpace(_path.Text) ? null : _path.Text.Trim().Trim('"');

    void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Background (image, video) or karaoke (CDG, MP3+G, ZIP, KAR, MID)" };
        if (dialog.ShowDialog(this) == true)
            _path.Text = dialog.FileName;
    }

    static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, _) => click();
        return button;
    }
}
