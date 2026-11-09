# Quickstart for WPF

This quickstart builds a small WPF window that plays a file: it creates the player without blocking the UI, shows the picture in a <xref:FatzMedia.Video.Wpf.MediaSurface>, loads and plays a file you choose, and disposes everything when the window closes. It uses the Direct3D11 renderer.

## 1. Create the project

Create a WPF app targeting `net10.0-windows7.0` and x64, and add the packages (see [Installation](installation.md)):

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows7.0</TargetFramework>
  <PlatformTarget>x64</PlatformTarget>
  <UseWPF>true</UseWPF>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="FatzMedia.Video.Wpf" Version="0.1.0-alpha" />
  <PackageReference Include="FatzMedia.Video.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup>
  <None Update="native\**\*.dll" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Put the x64 BASS and FFmpeg DLLs in a `native` folder in the project ([Native dependencies](native-dependencies.md)).

## 2. The window

The `MediaSurface` goes where the picture should appear. It does nothing until you give it a player.

```xaml
<Window x:Class="Quickstart.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:fbm="clr-namespace:FatzMedia.Video.Wpf;assembly=FatzMedia.Video.Wpf"
        Title="Quickstart" Width="960" Height="600"
        Loaded="Window_Loaded" Closing="Window_Closing">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <fbm:MediaSurface x:Name="Surface" />

        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="8">
            <Button Content="Open..." Click="Open_Click" Margin="0,0,8,0" />
            <Button Content="Play" Click="Play_Click" Margin="0,0,8,0" />
            <Button Content="Pause" Click="Pause_Click" Margin="0,0,8,0" />
            <Button Content="Stop" Click="Stop_Click" />
        </StackPanel>
    </Grid>
</Window>
```

## 3. The code

```csharp
using System.ComponentModel;
using System.Windows;
using FatzMedia.Video;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Wpf;
using Microsoft.Win32;

namespace Quickstart;

public partial class MainWindow : Window
{
    private Player? _player;
    private bool _closing;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
            // The window closed while the player was loading.
            await Task.Run(_player.Dispose);
            return;
        }

        // Events arrive on background threads: marshal before touching the UI.
        _player.MediaDiagnostic += (_, args) =>
            Dispatcher.BeginInvoke(() => Title = $"[{args.Level}] {args.Category}: {args.Message}");
        _player.MediaEnded += (_, _) => Dispatcher.BeginInvoke(() => Title = "Finished");

        Surface.Player = _player;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;

        var dialog = new OpenFileDialog { Filter = _player.OpenFileDialogFilter };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!await _player.LoadAsync(dialog.FileName))
        {
            MessageBox.Show(this, "The file could not be loaded.");
            return;
        }

        await _player.PlayAsync();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.PlayAsync();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.PauseAsync();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.StopAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (_player is not { } player)
            return;

        _player = null;
        // Let go of the surface first, then dispose off the UI thread (Dispose blocks).
        Surface.Player = null;
        _ = Task.Run(player.Dispose);
    }
}
```

## What the code does

- **Create.** <xref:FatzMedia.Video.Wpf.Player.CreateAsync*> builds the player on a pool thread, because loading BASS and FFmpeg blocks. `new Player(...)` would freeze the window. `WindowsNativeLibraryPaths.GetDefaultSearchPaths()` returns the existing `native` and `runtimes/win-x64/native` folders under the application folder. The platform factory pairs the Windows services with the Direct3D11 renderer.
- **Attach.** Setting `Surface.Player` attaches the surface to the player. The surface sizes itself to its layout and detaches when you set `Player` to `null`.
- **Events.** `MediaDiagnostic` and `MediaEnded` fire on background threads. Anything that touches the UI goes through `Dispatcher`. See [Threading and events](../concepts/threading-and-events.md).
- **Load and play.** `LoadAsync` returns `false` if the file could not be opened (the `MediaDiagnostic` and `MediaError` events say why). `OpenFileDialogFilter` is an Open dialog filter built from the formats the loaded libraries support. You can also set `PlayOnLoad = true` and skip the `PlayAsync` call.
- **Close.** Clear `Surface.Player` first, then dispose. `Dispose` blocks while it stops playback and frees the engine, so it runs on a pool thread. The `_closing` check handles the case where the window closes before `CreateAsync` has finished.

> [!TIP]
> For a real application, dispose the player and wait for it before the process exits (for example in `OnExit`) so the audio device and native resources are released cleanly. Here the disposal runs in the background for brevity.

## Software renderer or Vulkan

To use the software renderer, create the player with `new WindowsPlatformFactory(new SoftwarePresentationCoordinatorFactory())` (package `FatzMedia.Video.Software`) and a configuration of `new MediaPlayerConfiguration(VideoDecodingMethod.Software)`. That renderer presents CPU frames only and decodes video in software whatever the configuration says; the configuration makes that explicit. Set `PresentationMode="WpfComposited"` on the `MediaSurface`, because the renderer hands finished frames to the control instead of drawing into a window. The Vulkan renderer needs the same `PresentationMode`, and it keeps `MediaPlayerConfiguration.Auto` so it can decode on the GPU. Both renderers draw subtitle and lyric text (with SkiaSharp instead of DirectWrite).

## Next

- Second screen: [Second screen](../presentation/second-screen.md) and <xref:FatzMedia.Video.Wpf.SecondaryPresentationWindow>.
- Ready-made karaoke and background controls: [Karaoke surface](../lightweight-surfaces/karaoke-surface.md) and [Background surface](../lightweight-surfaces/background-surface.md).
- The player and its session: [Player and session](../concepts/player-and-session.md).
