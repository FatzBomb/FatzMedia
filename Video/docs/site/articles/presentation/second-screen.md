# Second screen

A karaoke host usually needs two pictures: the operator's window and the singer's screen. One player can feed several surfaces at once, each in its own window and at its own size. This article covers the ready-made secondary window, placing it on a chosen monitor, and building your own second surface.

## How it works

The player has one primary surface (the control your main window shows) and any number of extra surfaces with the role `Projector`. The renderer composes every frame once per surface at that surface's size, so a 1080p projector and a small preview in the main window are both sharp. All surfaces show the same media at the same time, from the same clock.

- The main window's `MediaSurface` keeps its default `Role` (`Primary`).
- An extra surface has `Role = MediaPresentationSurfaceRole.Projector` and a `SurfaceId` that is unique within the player. If `SurfaceId` is blank it is `secondary`.
- Only the primary surface forwards its `VideoDisplayMode` to the player, so a second screen never changes it. The display mode, color transform and timing are per player.
- Each surface picks its own [presentation path](surfaces.md). With Avalonia, for example, one surface can use a shared GPU image while another falls back to a frame sink.

## SecondaryPresentationWindow

Each adapter has a window that holds a projector `MediaSurface`: <xref:FatzMedia.Video.Wpf.SecondaryPresentationWindow>, <xref:FatzMedia.Video.Avalonia.SecondaryPresentationWindow> and <xref:FatzMedia.Video.WinForms.SecondaryPresentationWindow>. They share the same shape:

| Member | Meaning |
| --- | --- |
| Constructor | `(IMediaControlSession session, string? surfaceId = null)`. The WPF constructor takes `string surfaceId` plus optional `MediaPresentationSurfaceRole role` and `WpfPresentationMode presentationMode`. |
| `Surface` | The window's projector `MediaSurface`. It takes the session when the window is shown, so a window that is never shown does not follow the player. |
| `ShowOnScreen(screen, fullScreen, owner)` | Places the window on `screen` and shows it. Call it instead of `Show`. With `fullScreen` the window covers the screen; otherwise it is centered in the screen's working area. A `null` screen keeps the default start position. |
| `IsFullScreen` | Gets or sets full screen (borderless, covering the screen). |
| `DefaultSize` / `DefaultClientSize` | 960 x 540 when not full screen. The minimum size is 320 x 180. |

The window handles its own keys: F11 or a double-click toggles full screen, Escape leaves full screen, and in a normal window Escape closes it. Closing the window detaches its surface from the player.

### WPF

```csharp
PresentationScreen? other = PresentationScreen.All
    .FirstOrDefault(screen => !screen.Equals(PresentationScreen.FromWindow(this)));

var singer = new SecondaryPresentationWindow(player, "singer");
singer.ShowOnScreen(other, fullScreen: other is not null, owner: this);
```

<xref:FatzMedia.Video.Wpf.PresentationScreen> describes a monitor. WPF has no screen type of its own, so the adapter enumerates the monitors:

| Member | Meaning |
| --- | --- |
| `PresentationScreen.All` | Every monitor, the primary one first. |
| `PresentationScreen.FromWindow(window)` | The monitor holding most of the window, or `null` before the window has a handle. |
| `DeviceName`, `Bounds`, `WorkingArea` | The device name (such as `\\.\DISPLAY2`), the bounds and the working area (without the taskbar), in device pixels. |
| `IsPrimary`, `Scaling` | Whether it is the primary monitor, and its scale factor (effective DPI / 96). |

The secondary window must show frames the way the primary surface does. Pass the primary's `PresentationMode` to the constructor when you use `WpfComposited` (required for the Software and Vulkan renderers):

```csharp
var singer = new SecondaryPresentationWindow(
    player, "singer", MediaPresentationSurfaceRole.Projector, WpfPresentationMode.WpfComposited);
```

> [!NOTE]
> `MediaSurface.ShowSecondaryOutput`, `CloseSecondaryOutput` and `IsSecondaryOutputOpen` on the WPF surface are obsolete and will be removed. Use a `SecondaryPresentationWindow`.

### Avalonia

```csharp
Screen? other = main.Screens?.All.FirstOrDefault(screen => screen != main.Screens.ScreenFromWindow(main));

var singer = new SecondaryPresentationWindow(player, "singer");
singer.ShowOnScreen(other, fullScreen: other is not null, owner: main);
```

`Screen` is Avalonia's `Avalonia.Platform.Screen`.

### WinForms

```csharp
Screen? other = Screen.AllScreens.FirstOrDefault(screen => !screen.Equals(Screen.FromControl(this)));

var singer = new SecondaryPresentationWindow(player, "singer");
singer.ShowOnScreen(other, fullScreen: other is not null, owner: this);
```

## Your own second surface

You do not have to use the helper window. Any window can hold a `MediaSurface` with the projector role, for example a preview pane inside your main window or a custom layout on the singer screen:

```csharp
var projector = new MediaSurface
{
    Role = MediaPresentationSurfaceRole.Projector,
    SurfaceId = "projector",
    Player = player,
};
```

Changing `Role` or `SurfaceId` re-attaches the surface. Give every extra surface of a player its own id.

## Things to know

- **One id per surface.** The player tracks its surfaces by id, so give every extra window or surface its own `surfaceId`.
- **AutoHide applies to each surface.** The second screen shows its background until a frame of the current media arrives, and again after stop. In `Audio` mode nothing presents to any surface.
- **Monitors with different refresh rates.** In `DisplayLocked` timing, each display runs its own timing. See [Frame timing](../video/frame-timing.md).
- **HDR output** is applied to the primary window's native swap chain only. Second screens stay SDR. See [Display and color](../video/display-and-color.md).
- **Diagnostics** name the surface: `Surface #2 'singer': ...`. See [Surfaces](surfaces.md).
- **Closing.** A secondary window detaches its surface when it closes. Close it together with the main window, before you dispose the player.
