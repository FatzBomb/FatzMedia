# Quickstart for WinForms

This quickstart builds a small WinForms window that plays a file: it creates the player without blocking the UI, shows the picture in a <xref:FatzMedia.Video.WinForms.MediaSurface>, loads and plays a file you choose, and disposes everything when the form closes. It uses the Direct3D11 renderer.

## 1. Create the project

Create a WinForms app targeting `net10.0-windows7.0` and x64, and add the packages (see [Installation](installation.md)):

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows7.0</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
  <UseWindowsForms>true</UseWindowsForms>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="FatzMedia.Video.WinForms" Version="0.1.0-alpha" />
  <PackageReference Include="FatzMedia.Video.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup>
  <None Update="native\**\*.dll" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Put the x64 FFmpeg DLLs in a `native` folder in the project ([Native dependencies](native-dependencies.md)).

## 2. The program

```csharp
namespace Quickstart;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

## 3. The form

```csharp
using FatzMedia.Video;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.WinForms;

namespace Quickstart;

public sealed class MainForm : Form
{
    private readonly MediaSurface _surface = new() { Dock = DockStyle.Fill };
    private Player? _player;
    private bool _closing;

    public MainForm()
    {
        Text = "Quickstart";
        ClientSize = new Size(960, 600);

        var open = new Button { Text = "Open...", AutoSize = true };
        var play = new Button { Text = "Play", AutoSize = true };
        var pause = new Button { Text = "Pause", AutoSize = true };
        var stop = new Button { Text = "Stop", AutoSize = true };
        open.Click += async (_, _) => await OpenAsync();
        play.Click += async (_, _) => { if (_player is not null) await _player.PlayAsync(); };
        pause.Click += async (_, _) => { if (_player is not null) await _player.PauseAsync(); };
        stop.Click += async (_, _) => { if (_player is not null) await _player.StopAsync(); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        buttons.Controls.AddRange([open, play, pause, stop]);

        Controls.Add(_surface);
        Controls.Add(buttons);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // Native loading blocks, so CreateAsync builds the player on a pool thread.
            _player = await Player.CreateAsync(
                MediaPlayerConfiguration.Auto,
                WindowsNativeLibraryPaths.GetDefaultSearchPaths(),
                new WindowsDirect3D11PlatformFactory());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Player could not start");
            return;
        }

        if (_closing)
        {
            // The form closed while the player was loading.
            await Task.Run(_player.Dispose);
            return;
        }

        // Events arrive on background threads: marshal before touching the UI.
        _player.MediaDiagnostic += (_, args) =>
            BeginInvoke(() => Text = $"[{args.Level}] {args.Category}: {args.Message}");
        _player.MediaEnded += (_, _) => BeginInvoke(() => Text = "Finished");

        _surface.Player = _player;
    }

    private async Task OpenAsync()
    {
        if (_player is null)
            return;

        using var dialog = new OpenFileDialog { Filter = _player.OpenFileDialogFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!await _player.LoadAsync(dialog.FileName))
        {
            MessageBox.Show(this, "The file could not be loaded.");
            return;
        }

        await _player.PlayAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _closing = true;
        if (_player is not { } player)
            return;

        _player = null;
        // Let go of the surface first, then dispose off the UI thread (Dispose blocks).
        _surface.Player = null;
        _ = Task.Run(player.Dispose);
    }
}
```

## What the code does

- **Create.** <xref:FatzMedia.Video.WinForms.Player.CreateAsync*> builds the player on a pool thread, because loading FFmpeg and FatzAudio blocks. The WinForms adapter has no default platform factory, so you pass one: here the Windows services paired with the Direct3D11 renderer.
- **Attach.** Setting `_surface.Player` attaches the surface. Its default is a native child window that the renderer draws into. If the renderer cannot present into a window (the Software renderer, for example), the surface switches to a frame sink and draws the frames itself. `PresentationPath` reports which one is in use.
- **Events.** `MediaDiagnostic` and `MediaEnded` fire on background threads, so the handlers use `BeginInvoke`. See [Threading and events](../concepts/threading-and-events.md).
- **Load and play.** `LoadAsync` returns `false` if the file could not be opened. `OpenFileDialogFilter` is an Open dialog filter built from the formats the loaded libraries support.
- **Close.** Clear `_surface.Player` first, then dispose. `Dispose` blocks while it stops playback and frees the engine, so it runs on a pool thread. The `_closing` check handles the case where the form closes before `CreateAsync` has finished.

> [!TIP]
> For a real application, dispose the player and wait for it before the process exits so the audio device and native resources are released cleanly. Here the disposal runs in the background for brevity.

## Next

- Second screen: [Second screen](../presentation/second-screen.md) and <xref:FatzMedia.Video.WinForms.SecondaryPresentationWindow>.
- Ready-made karaoke and background controls: [Karaoke surface](../lightweight-surfaces/karaoke-surface.md) and [Background surface](../lightweight-surfaces/background-surface.md).
- The player and its session: [Player and session](../concepts/player-and-session.md).
