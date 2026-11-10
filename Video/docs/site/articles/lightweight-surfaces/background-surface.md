# Background surface

`BackgroundSurface` is a ready-made control for the picture behind your UI: a solid colour, an
image or a silent looping video. It creates its own player only when it needs one, never starts
audio, and lets you put your own controls on top. It exists in the WPF, Avalonia and WinForms
adapters with the same behaviour. Use it for menu backdrops, idle screens, a "next up" slide next
to a karaoke player, or the stage behind a singer.

| Adapter | Class |
| --- | --- |
| WPF | <xref:FatzMedia.Video.Wpf.BackgroundSurface> |
| Avalonia | <xref:FatzMedia.Video.Avalonia.BackgroundSurface> |
| WinForms | <xref:FatzMedia.Video.WinForms.BackgroundSurface> |

## What it shows

| `Source` value | WPF | Avalonia | WinForms | Result |
| --- | --- | --- | --- | --- |
| `null` (default) | yes | yes | yes | `BackgroundColor` fills the control. |
| A colour | `Color`, `SolidColorBrush` | `Color`, `ISolidColorBrush` | `Color` | The colour fills the control. No player is created. |
| A path (`string`) | yes | yes | yes | An image or video file. |
| An image object | `ImageSource` | `Bitmap` | `Bitmap` | The image, converted to a Core image when it is loaded. |
| A `MediaInputSource` | yes | yes | yes | Any Core source: a file, a colour made with `MediaInputSource.FromColor`, or a network video made with `MediaInputSource.FromUri`. |

Any other type is not supported and is reported as a warning diagnostic. Images are shown as
a still picture. Video is decoded without audio and loops forever.

The control accepts only images and video. It rejects audio, CD+G and MIDI/KAR, closes them and
returns `false` from `LoadAsync`. Use [Karaoke surface](karaoke-surface.md) for those.

## Quick start

The platform factory is the same one you would give a `PlayerCore`; on Windows
`WindowsDirect3D11PlatformFactory`. Set it before the first load.

# [WPF](#tab/wpf)

```xaml
<fbm:BackgroundSurface x:Name="Backdrop"
                       Source="C:\Media\loop.mp4"
                       BackgroundColor="#101820">
    <fbm:BackgroundSurface.OverlayContent>
        <TextBlock Text="Next up: Karaoke night" Foreground="White" FontSize="28" Margin="16" />
    </fbm:BackgroundSurface.OverlayContent>
</fbm:BackgroundSurface>
```

with `xmlns:fbm="clr-namespace:FatzMedia.Video.Wpf;assembly=FatzMedia.Video.Wpf"` on the
root element.

```csharp
public MainWindow()
{
    InitializeComponent();
    Backdrop.PlatformFactory = new WindowsDirect3D11PlatformFactory();
}

protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    Backdrop.Dispose();
}
```

# [Avalonia](#tab/avalonia)

```xml
<fbm:BackgroundSurface x:Name="Backdrop"
                       Source="/media/loop.mp4"
                       BackgroundColor="#101820">
    <fbm:BackgroundSurface.OverlayContent>
        <TextBlock Text="Next up: Karaoke night" Foreground="White" FontSize="28" Margin="16" />
    </fbm:BackgroundSurface.OverlayContent>
</fbm:BackgroundSurface>
```

with `xmlns:fbm="using:FatzMedia.Video.Avalonia"`
on the root element.

```csharp
public MainWindow()
{
    InitializeComponent();
    // Any IPlatformFactory: for example the Linux factory with the software renderer.
    Backdrop.PlatformFactory = new LinuxPlatformFactory(new SoftwarePresentationCoordinatorFactory());
}

protected override void OnClosed(EventArgs e)
{
    base.OnClosed(e);
    Backdrop.Dispose();
}
```

# [WinForms](#tab/winforms)

```csharp
var backdrop = new BackgroundSurface(new WindowsDirect3D11PlatformFactory())
{
    Dock = DockStyle.Fill,
    BackgroundColor = Color.FromArgb(16, 24, 32),
    Source = @"C:\Media\loop.mp4",          // loads once the control has a window handle
    OverlayContent = new Label
    {
        Text = "Next up: Karaoke night",
        ForeColor = Color.White,
        BackColor = Color.Transparent,
        AutoSize = true,
        Location = new Point(16, 16),
    },
};
Controls.Add(backdrop);
```

`OverlayContent` is a `Control`. You own it, so dispose it yourself when the form closes.

---

## Properties

| Property | Default | Meaning |
| --- | --- | --- |
| `Source` | `null` | What to show (see above). A change reloads; several changes in one turn load once. |
| `BackgroundColor` | black | The opaque colour shown without media and while a video has not drawn its first frame. The alpha value is ignored. |
| `PlatformFactory` | `null` | How the control's player is built. Required for files, images and video; not needed for colours. It can only be changed before the player exists; later changes throw `InvalidOperationException`. |
| `NativeLibraryPaths` | `null` | Folders that hold the FFmpeg libraries. `null` uses the [`DLLPath` settings](../getting-started/native-dependencies.md#the-dllpath-settings) and the default folders. |
| `AutoPlay` | `true` | Start a video when it has loaded. Images show without playing. When `false`, call `PlayAsync` for a video. |
| `AutoHide` | `true` | Keep the video surface hidden until the first frame is drawn and again after stop, close or an error, so `BackgroundColor` shows meanwhile. |
| `IsContentVisible` | | Read-only. `true` while media is drawn. `ContentVisibilityChanged` is raised when it changes. |
| `OverlayContent` | `null` | Your content, drawn above the background. |
| `OverlayHitTestVisible` | `false` | Whether the overlay takes mouse input. While it is `false`, clicks go through to what is behind. |
| `Session` | `null` | The control's player as an `IPlayerSession`, once it exists. |

Adapter-specific properties:

| Adapter | Property | Default | Meaning |
| --- | --- | --- | --- |
| WPF | `PresentationMode` | `NativeHwndOverlay` | `NativeHwndOverlay` or `WpfComposited`. A change made while media is open is applied after it closes. |
| Avalonia | `PreferGpuSharedSurface` | `true` | Lets the surface use the shared GPU image path when it is available. |
| WinForms | `PreferNativeWindow` | `true` | Lets the renderer present into a child window. |

## Loading and controlling from code

```csharp
Backdrop.Source = @"C:\Media\loop.mp4";                       // declarative, reloads on change
bool shown = await Backdrop.LoadAsync(@"C:\Media\loop.mp4");  // or load directly (UI thread)
await Backdrop.PauseAsync();
await Backdrop.PlayAsync();
await Backdrop.StopAsync();

Backdrop.SetColor(Colors.DarkSlateBlue);   // WPF; Color.DarkSlateBlue on WinForms
```

- `LoadAsync` has overloads for a path, an image (`ImageSource` in WPF, `Bitmap` in Avalonia and
  WinForms) and a `MediaInputSource`. It returns `true` when the media was accepted and no newer
  load replaced it.
- `SetColor(color)` sets `BackgroundColor` and `Source` together and releases the player.
- After `StopAsync`, and with `AutoHide` on, the surface hides and `BackgroundColor` shows until
  you play again.

## How it behaves

- **No audio.** The player runs in `MediaPlayerMode.Background`: the soundtrack of a video is not
  decoded, no audio device is opened and FatzAudio is not needed, so a background works on a machine
  without the audio library.
- **Always loops.** Looping cannot be turned off. A video restarts from its first frame when it
  ends.
- **Created when needed.** A colour or `null` creates no player. The first file, image or video
  creates one on a background thread. Setting a colour afterwards releases the player.
- **Released with the control.** The player is released when a WPF control is unloaded, an Avalonia
  control leaves the visual tree, a WinForms control's handle is destroyed, or you call `Dispose`.
  When the control returns, `Source` is loaded again. An image or video you loaded with
  `LoadAsync` is not remembered.
- **Overlay.** `OverlayContent` is drawn above the background in every presentation mode.
- **Failures** are reported as diagnostics with the category `Wpf.BackgroundSurface`,
  `Avalonia.BackgroundSurface` or `WinForms.BackgroundSurface`; the control stays usable.

> [!NOTE]
> The control decides what it accepts after loading the file. A video that contains an audio track
> is fine, because the audio is ignored. A file that turns out to be audio-only, CD+G or a
> MIDI/KAR file is rejected.

## See also

- [Karaoke surface](karaoke-surface.md)
- [Backgrounds behind karaoke](../karaoke/backgrounds.md): sidecar pictures and videos for a
  `PlayerCore`.
