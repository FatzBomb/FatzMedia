using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FatzMedia.Video.Avalonia;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// Smoke window for the adapter's <see cref="BackgroundSurface"/> and <see cref="KaraokeSurface"/>
/// (<c>--lightweight-surfaces</c>, Stage 046A slice 2; the WPF and WinForms players have the same
/// window). Each control creates its own player with the renderer chosen at startup.
/// </summary>
internal sealed class LightweightSurfacesWindow : Window
{
    private static readonly Color[] Colours = [Colors.DarkSlateBlue, Colors.DarkGreen, Colors.Black];

    private readonly BackgroundSurface _background;
    private readonly KaraokeSurface _karaoke;
    private readonly TextBox _path = new() { Width = 380, Margin = new Thickness(4) };
    private int _colour;

    public LightweightSurfacesWindow(
        Func<IPlatformFactory> createPlatformFactory,
        IReadOnlyList<string> nativeLibraryPaths,
        bool preferGpuSurface,
        bool autoHide)
    {
        Title = "Background and karaoke surfaces (--lightweight-surfaces)";
        Width = 1100;
        Height = 520;
        Background = Brushes.Black;
        _background = new BackgroundSurface(createPlatformFactory())
        {
            NativeLibraryPaths = nativeLibraryPaths,
            PreferGpuSharedSurface = preferGpuSurface,
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
            PreferGpuSharedSurface = preferGpuSurface,
            AutoHide = autoHide,
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.DimGray };
        bar.Children.Add(_path);
        bar.Children.Add(Button("Browse...", BrowseAsync));
        bar.Children.Add(Button("Background", () => _background.Source = PathOrNull()));
        bar.Children.Add(Button("Karaoke", () => _karaoke.Source = PathOrNull()));
        bar.Children.Add(Button("Colour", () => _background.SetColor(Colours[_colour++ % Colours.Length])));
        bar.Children.Add(Button("Stop both", () =>
        {
            _ = _background.StopAsync();
            _ = _karaoke.StopAsync();
        }));

        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_karaoke, 1);
        panes.Children.Add(_background);
        panes.Children.Add(_karaoke);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(panes);
        Content = root;
        Closed += (_, _) =>
        {
            _background.Dispose();
            _karaoke.Dispose();
        };
    }

    private string? PathOrNull() => string.IsNullOrWhiteSpace(_path.Text) ? null : _path.Text.Trim().Trim('"');

    private async void BrowseAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Background (image, video) or karaoke (CDG, MP3+G, ZIP, KAR, MID)",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            _path.Text = path;
    }

    private static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(4) };
        button.Click += (_, _) => click();
        return button;
    }
}
