# Karaoke surface

`KaraokeSurface` is a ready-made control that plays CD+G and MIDI/KAR karaoke. It creates its own
player, shows the song on a centred 4:3 area, and releases the player when it leaves the screen.
You give it a platform factory and a file; there is no player, no `MediaSurface` and no event
wiring to write. It exists in the WPF, Avalonia and WinForms adapters with the same behaviour
and nearly the same properties. Use it to put karaoke in part of a window. If you need full
control (a custom lyric style, a soundfont, a second screen, backgrounds), use your own
<xref:FatzMedia.Video.PlayerCore> with a `MediaSurface`.

| Adapter | Class |
| --- | --- |
| WPF | <xref:FatzMedia.Video.Wpf.KaraokeSurface> |
| Avalonia | <xref:FatzMedia.Video.Avalonia.KaraokeSurface> |
| WinForms | <xref:FatzMedia.Video.WinForms.KaraokeSurface> |

## What it plays

The control accepts:

- CD+G with audio (an audio file with a `.cdg` beside it), CD+G alone, and CD+G inside an
  archive. See [CD+G](../karaoke/cdg.md).
- MIDI and KAR files that contain lyrics. See [MIDI and KAR](../karaoke/midi-and-kar.md).

It rejects everything else after looking at the file: video files, plain audio and MIDI files
without lyrics. A rejected file is closed and `LoadAsync` returns `false`.

> [!NOTE]
> MIDI lyrics are text. Direct3D 11 draws it with DirectWrite and the Software and Vulkan
> renderers with SkiaSharp, using the system's fonts. CD+G graphics show with every renderer.

Songs load without sidecar search, so a picture or video with the song's name is not shown behind
it. Put a [Background surface](background-surface.md) beside the karaoke control if you want one.

## Quick start

Set the platform factory in code before the first load, and give the control a file in `Source`.
The factory is the same one you would give a `PlayerCore`: on Windows
`WindowsDirect3D11PlatformFactory`.

# [WPF](#tab/wpf)

```xaml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:fbm="clr-namespace:FatzMedia.Video.Wpf;assembly=FatzMedia.Video.Wpf"
        x:Class="MyApp.MainWindow">
    <fbm:KaraokeSurface x:Name="Karaoke"
                        Source="C:\Karaoke\song.zip"
                        CanvasColor="Black"
                        AutoPlay="True"
                        AutoHide="True" />
</Window>
```

```csharp
public MainWindow()
{
    InitializeComponent();
    Karaoke.PlatformFactory = new WindowsDirect3D11PlatformFactory();
    Karaoke.NativeLibraryPaths = [Path.Combine(AppContext.BaseDirectory, "native")];
}

protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    Karaoke.Dispose();
}
```

# [Avalonia](#tab/avalonia)

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:fbm="using:FatzMedia.Video.Avalonia"
        x:Class="MyApp.MainWindow">
    <fbm:KaraokeSurface x:Name="Karaoke"
                        Source="/karaoke/song.zip"
                        CanvasColor="Black"
                        AutoPlay="True"
                        AutoHide="True" />
</Window>
```

```csharp
public MainWindow()
{
    InitializeComponent();
    // Any IPlatformFactory: for example the Linux factory with the software renderer.
    Karaoke.PlatformFactory = new LinuxPlatformFactory(new SoftwarePresentationCoordinatorFactory());
}

protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    Karaoke.Dispose();
}
```

You can also bind it: `PlatformFactory="{Binding Platform}"`.

# [WinForms](#tab/winforms)

```csharp
var karaoke = new KaraokeSurface(new WindowsDirect3D11PlatformFactory())
{
    Dock = DockStyle.Fill,
    CanvasColor = Color.Black,
    AutoPlay = true,
    AutoHide = true,
    NativeLibraryPaths = [Path.Combine(AppContext.BaseDirectory, "native")],
};
karaoke.ContentVisibilityChanged += (_, _) => Text = karaoke.IsContentVisible ? "Singing" : "Waiting";
Controls.Add(karaoke);

// Source loads once the control has a window handle.
karaoke.Source = @"C:\Karaoke\song.zip";
```

The WinForms control is a normal `Control`; disposing the form disposes it.

---

The constructor overload that takes the platform factory does the same as setting
`PlatformFactory`.

## Properties

| Property | Default | Meaning |
| --- | --- | --- |
| `Source` | `null` | Path of the song to play. Changing it loads the new song; several changes in one turn load once. Setting `null` releases the player. |
| `PlatformFactory` | `null` | How the control's player is built. Required before the first load. It can only be changed before the player exists; later changes throw `InvalidOperationException`. |
| `NativeLibraryPaths` | `null` | Folders that hold the BASS and FFmpeg libraries. `null` uses the [`DLLPath` settings](../getting-started/native-dependencies.md#the-dllpath-settings) and the default folders. Read when the player is created. |
| `AutoPlay` | `true` | Start the song when it has loaded. When `false`, call `PlayAsync` yourself. |
| `AutoHide` | `true` | Hide the video surface until the first frame is drawn and again after stop, close, an error or the end of the song, so the canvas colour shows meanwhile. |
| `IsContentVisible` | | Read-only. `true` while the song is drawn. `ContentVisibilityChanged` is raised when it changes. |
| `CanvasColor` | black | The opaque colour around the 4:3 area. The alpha value is ignored. |
| `Session` | `null` | The control's player as an `IPlayerSession`, once it exists. Use it to read state, change sync offsets or subscribe to events. |

Adapter-specific properties:

| Adapter | Property | Default | Meaning |
| --- | --- | --- | --- |
| WPF | `PresentationMode` | `NativeHwndOverlay` | `NativeHwndOverlay` or `WpfComposited`. A change made while a song is open is applied after it closes. |
| Avalonia | `PreferGpuSharedSurface` | `true` | Lets the surface use the shared GPU image path when it is available. |
| WinForms | `PreferNativeWindow` | `true` | Lets the renderer present into a child window. |

## Loading and controlling from code

```csharp
bool accepted = await Karaoke.LoadAsync(@"C:\Karaoke\song.mp3");   // UI thread
await Karaoke.PauseAsync();
await Karaoke.PlayAsync();
await Karaoke.StopAsync();
```

- `LoadAsync(path)` must be called on the UI thread. It returns `true` when the song was accepted
  and no newer load replaced it, and `false` when the file was rejected, a newer load or a release
  overtook it, no platform factory was set (a warning is raised) or the player could not be
  created (an error is raised).
- `PlayAsync`, `PauseAsync` and `StopAsync` return `false` while the control has no player.
- A song loaded with `LoadAsync` is not remembered. If the control leaves the screen and returns,
  only `Source` is loaded again.

Use `Session` for everything else. It is created on the first load, and a new one is created
when the control returns to the screen.

```csharp
private IPlayerSession? _subscribed;

private async Task PlayNextAsync()
{
    if (!_songs.TryDequeue(out string? path))
        return;

    if (!await Karaoke.LoadAsync(path))
    {
        await PlayNextAsync();                       // not karaoke: skip it
        return;
    }

    // One player is kept between songs, so subscribe once per player.
    if (Karaoke.Session is { } session && !ReferenceEquals(session, _subscribed))
    {
        _subscribed = session;
        // Player events arrive on a background thread: marshal to the UI thread.
        session.MediaEnded += (_, _) => Dispatcher.BeginInvoke(async () => await PlayNextAsync());
    }
}

private void Nudge(int steps) => Karaoke.Session?.AdjustCdgSyncOffset(steps);   // 100 ms per step
```

## Lifetime

- **Created on demand.** The first load creates the player on a background thread (native
  libraries load there), so the UI does not freeze. The control then adopts it.
- **Released when the control goes away.** The player is released and disposed when a WPF control
  is unloaded, an Avalonia control leaves the visual tree, a WinForms control's handle is
  destroyed, or you call `Dispose`. When the control comes back, `Source` loads again.
- **Failure is reported, not thrown.** A failed reload (for example a missing file) is reported as
  a diagnostic with the category `Wpf.KaraokeSurface`, `Avalonia.KaraokeSurface` or
  `WinForms.KaraokeSurface` (the `DiagnosticCategory` constant of each class), and the control
  stays usable.
- **Dispose.** Call `Dispose` when you are done (on the UI thread for WPF and Avalonia).
  Disposing releases the player and blocks until it is gone.

## How the player is set up

The control builds the player with hardware video decoding, in the normal Full mode, and with
looping off, MIDI lyrics on and the picture zoomed to fit. These are not configurable on the
control. This means:

- the lyric style and the soundfont are the defaults (see
  [MIDI and KAR](../karaoke/midi-and-kar.md));
- at the end of a song the control does not repeat it; handle `MediaEnded` to queue the next.

## Layout

The control draws the song in the largest 4:3 rectangle that fits, centred, and fills the rest
with `CanvasColor`. The same rule is available as `KaraokeLayout.Fit(width, height)` if you want to
place something relative to the picture.

## See also

- [Background surface](background-surface.md)
- [Karaoke overview](../karaoke/overview.md)
