using FatzMedia.Video.WinForms;

namespace FatzMedia.Video.Sample.Player.WinForms;

/// <summary>
/// Smoke window for the adapter's <see cref="BackgroundSurface"/> and <see cref="KaraokeSurface"/>
/// (<c>--lightweight-surfaces</c>, Stage 046A slice 2; the WPF and Avalonia players have the same
/// window). Each control creates its own player with the renderer chosen at startup.
/// </summary>
sealed class LightweightSurfacesForm : Form
{
    static readonly Color[] Colours = [Color.DarkSlateBlue, Color.DarkGreen, Color.Black];

    readonly BackgroundSurface _background;
    readonly KaraokeSurface _karaoke;
    readonly TextBox _path = new() { Width = 380, Margin = new Padding(4, 6, 4, 4) };
    int _colour;

    public LightweightSurfacesForm(Func<IPlatformFactory> createPlatformFactory, IReadOnlyList<string> nativeLibraryPaths, bool autoHide, bool preferNativeWindow)
    {
        Text = "Background and karaoke surfaces (--lightweight-surfaces)";
        ClientSize = new Size(1100, 520);
        BackColor = Color.Black;
        _background = new BackgroundSurface(createPlatformFactory())
        {
            Dock = DockStyle.Fill,
            NativeLibraryPaths = nativeLibraryPaths,
            AutoHide = autoHide,
            PreferNativeWindow = preferNativeWindow,
            OverlayContent = new Label
            {
                Text = "Overlay above the background",
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font(Font.FontFamily, 16f),
                AutoSize = true,
                Location = new Point(16, 16),
            },
        };
        _karaoke = new KaraokeSurface(createPlatformFactory()) { Dock = DockStyle.Fill, NativeLibraryPaths = nativeLibraryPaths, AutoHide = autoHide, PreferNativeWindow = preferNativeWindow };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        bar.Controls.Add(_path);
        bar.Controls.Add(Button("Browse...", Browse));
        bar.Controls.Add(Button("Background", () => _background.Source = PathOrNull()));
        bar.Controls.Add(Button("Karaoke", () => _karaoke.Source = PathOrNull()));
        bar.Controls.Add(Button("Colour", () => _background.SetColor(Colours[_colour++ % Colours.Length])));
        bar.Controls.Add(Button("Stop both", () =>
        {
            _ = _background.StopAsync();
            _ = _karaoke.StopAsync();
        }));

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.Controls.Add(_background, 0, 0);
        panes.Controls.Add(_karaoke, 1, 0);
        Controls.Add(panes);
        Controls.Add(bar);
        FormClosed += (_, _) => _background.OverlayContent?.Dispose();
    }

    string? PathOrNull() => string.IsNullOrWhiteSpace(_path.Text) ? null : _path.Text.Trim().Trim('"');

    void Browse()
    {
        using var dialog = new OpenFileDialog { Title = "Background (image, video) or karaoke (CDG, MP3+G, ZIP, KAR, MID)" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _path.Text = dialog.FileName;
    }

    static Button Button(string text, Action click)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => click();
        return button;
    }
}
