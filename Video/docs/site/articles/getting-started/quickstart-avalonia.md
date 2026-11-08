# Quickstart for Avalonia

This quickstart builds a small Avalonia window that plays a file on Windows and Linux: it creates the player without blocking the UI, shows the picture in a <xref:FatzBomb.MediaControl.Avalonia.MediaSurface>, loads and plays a file you choose, and disposes everything when the window closes. It uses the Software renderer, which works everywhere.

## 1. Create the project

The Avalonia adapter targets `net10.0`, but the Windows platform package targets `net10.0-windows7.0`. A project that runs on both operating systems multi-targets, so each target framework references its own platform package (see [Installation](installation.md)):

```xml
<PropertyGroup>
  <TargetFrameworks>net10.0;net10.0-windows7.0</TargetFrameworks>
  <OutputType>WinExe</OutputType>
</PropertyGroup>

<PropertyGroup Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' == 'windows'">
  <PlatformTarget>x64</PlatformTarget>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.4" />
  <PackageReference Include="Avalonia.Desktop" Version="12.1.4" />
  <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.4" />
  <PackageReference Include="FatzBomb.MediaControl.Avalonia" Version="0.1.0-alpha" />
  <PackageReference Include="FatzBomb.MediaControl.Rendering.Software" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' == 'windows'">
  <PackageReference Include="FatzBomb.MediaControl.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' != 'windows'">
  <PackageReference Include="FatzBomb.MediaControl.Linux" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup>
  <None Update="native\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Run it with `dotnet run -f net10.0` on Linux or `dotnet run -f net10.0-windows7.0` on Windows. If you only target one operating system, use a single target framework and drop the `#if` below.

Put the BASS and FFmpeg libraries in a `native` folder in the project ([Native dependencies](native-dependencies.md), [Running on Linux](linux.md)).

## 2. The application

`Program.cs`:

```csharp
using Avalonia;

namespace Quickstart;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
}
```

`App.axaml`:

```xaml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="Quickstart.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`App.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Quickstart;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
```

## 3. The window

`MainWindow.axaml`:

```xaml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:fbm="using:FatzBomb.MediaControl.Avalonia"
        x:Class="Quickstart.MainWindow"
        Title="Quickstart" Width="960" Height="600">
  <Grid RowDefinitions="*,Auto">
    <fbm:MediaSurface x:Name="Surface" />

    <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8" Margin="8">
      <Button Content="Open..." Click="Open_Click" />
      <Button Content="Play" Click="Play_Click" />
      <Button Content="Pause" Click="Pause_Click" />
      <Button Content="Stop" Click="Stop_Click" />
    </StackPanel>
  </Grid>
</Window>
```

`MainWindow.axaml.cs`:

```csharp
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Avalonia;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Rendering.Software;
using AvaloniaWindow = Avalonia.Controls.Window;

namespace Quickstart;

public partial class MainWindow : AvaloniaWindow
{
    private Player? _player;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await StartPlayerAsync();
        Closing += (_, _) => ReleasePlayer();
    }

    // Avalonia has no default platform factory: pick the package for the OS.
    private static IMediaControlPlatformFactory CreatePlatform() =>
#if WINDOWS
        new WindowsMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory());
#else
        new FatzBomb.MediaControl.Linux.LinuxMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory());
#endif

    private async Task StartPlayerAsync()
    {
        try
        {
            // Native loading blocks, so CreateAsync builds the player on a pool thread.
            // No paths: the platform package also looks in native/ next to the app.
            // The Software renderer presents CPU frames, so decode in software too.
            var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software);
            _player = await Player.CreateAsync(configuration, [], CreatePlatform());
        }
        catch (Exception ex)
        {
            Title = $"Player could not start: {ex.Message}";
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
            Dispatcher.UIThread.Post(() => Title = $"[{args.Level}] {args.Category}: {args.Message}");
        _player.MediaEnded += (_, _) => Dispatcher.UIThread.Post(() => Title = "Finished");

        Surface.Player = _player;
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open media" });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        if (!await _player.LoadAsync(path))
        {
            Title = "The file could not be loaded.";
            return;
        }

        await _player.PlayAsync();
    }

    private async void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.PlayAsync();
    }

    private async void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.PauseAsync();
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (_player is not null)
            await _player.StopAsync();
    }

    private void ReleasePlayer()
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

- **Platform.** The Avalonia adapter has no default platform factory. `CreatePlatform` picks the Windows services on a `net10.0-windows7.0` build and the Linux services otherwise, both paired with the Software renderer. That renderer presents CPU frames only and always decodes video in software; the configuration (`VideoDecodingMethod.Software`) makes that explicit.
- **Create.** <xref:FatzBomb.MediaControl.Avalonia.Player.CreateAsync*> builds the player on a pool thread, because loading the native libraries blocks. The empty path list means "use the default folders": the platform package looks in `native` and `runtimes/<rid>/native` next to the application (see [Native dependencies](native-dependencies.md#where-the-engine-looks)).
- **Attach.** Setting `Surface.Player` attaches the surface. It sizes itself from its layout bounds and render scale, and detaches when you set `Player` to `null`.
- **Events.** `MediaDiagnostic` and `MediaEnded` fire on background threads, so the handlers post to `Dispatcher.UIThread`. See [Threading and events](../concepts/threading-and-events.md).
- **Load and play.** `LoadAsync` returns `false` if the file could not be opened.
- **Close.** Clear `Surface.Player` first, then dispose. `Dispose` blocks, so it runs on a pool thread.

## A faster renderer

The Software renderer draws on the CPU. With the Direct3D11 or Vulkan renderer, use `MediaPlayerConfiguration.Auto` so the GPU can decode. On Windows, replace it with the Direct3D11 renderer from `FatzBomb.MediaControl.Windows.Direct3D11`: `new WindowsDirect3D11MediaControlPlatformFactory()` in place of the Windows branch of `CreatePlatform`. On both operating systems you can use the Vulkan renderer (`FatzBomb.MediaControl.Rendering.Vulkan`, `new VulkanPresentationCoordinatorFactory()`), which decodes on the GPU where the driver supports Vulkan Video. A surface presents through a shared GPU image when Avalonia's compositor and the renderer support it, and through a frame sink otherwise; `MediaSurface.PresentationPath` says which.

> [!NOTE]
> All three renderers draw subtitle and lyric text. On Linux the Software and Vulkan renderers draw it with SkiaSharp (`FatzBomb.MediaControl.Rendering.Text`, which they bring in), using the system's fonts.

## Next

- Second screen: [Second screen](../presentation/second-screen.md) and <xref:FatzBomb.MediaControl.Avalonia.SecondaryPresentationWindow>.
- Ready-made karaoke and background controls: [Karaoke surface](../lightweight-surfaces/karaoke-surface.md) and [Background surface](../lightweight-surfaces/background-surface.md).
- The player and its session: [Player and session](../concepts/player-and-session.md).
