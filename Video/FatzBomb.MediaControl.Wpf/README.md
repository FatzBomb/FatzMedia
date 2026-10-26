# FatzBomb.MediaControl.Wpf

Renderer-neutral WPF surfaces, player facade, and lightweight self-contained controls.
The host supplies an `IMediaControlPlatformFactory`, matching the existing `Player`
construction pattern; the WPF package does not select or reference a renderer.

## Package

Targets `net10.0-windows7.0` and supports **x64 only** (this matches the current
Windows native-media distribution). Install with:

```powershell
dotnet add package FatzBomb.MediaControl.Wpf
```

The package is a strict adapter over `FatzBomb.MediaControl.Windows` and Core. It owns
no renderer, pipeline, or native presentation logic. Its Windows dependency ships a
`buildTransitive` guard, so a consumer that explicitly targets a non-x64
`RuntimeIdentifier` receives an MSBuild error at build time. Renderer packages are
selected separately by the host; the Direct3D11 package applies the same guard when it
is selected. Hosts still supply BASS and FFmpeg native DLLs themselves; see
`docs/NATIVE_DEPENDENCIES.md` in the source repository.

## Presentation Modes

- `NativeHwndOverlay` is the default and provides the best video performance.
- `WpfComposited` reads the final Direct3D11 composition into the WPF visual tree. It
  supports normal WPF clipping, transforms, opacity, and z-order at the cost of a
  synchronous GPU readback and lower practical frame rate.

Set the mode before loading media. Mixed presentation modes within one player session
and live mode switching while media is open are not supported.

## MediaSurface and the second screen (Stage 046A)

`MediaSurface` has the same surface members as the Avalonia and WinForms adapters:

- `Role` (default `Primary`) and `SurfaceId`, with `EffectiveSurfaceId`, `IsSurfaceAttached` and
  `IsPrimarySurfaceAttached`. Only a primary surface forwards `VideoDisplayMode`.
- `AutoHide` (default true), `IsContentVisible` and `ContentVisibilityChanged`, decided by Core's
  `MediaSurfaceContentTracker`. `IsPresentationSuspended` while the player is in Audio mode:
  the host hides instead of showing the last frame.
- `PresentationPath` (`NativeWindow` or `FrameSink`), `PresentationPathReason`,
  `PresentationPathChanged`, an Info diagnostic owned by the player, and one Warning when the
  player refuses the surface. Notices use the `Wpf.MediaSurface` diagnostic category.

`SecondaryPresentationWindow` hosts a projector `MediaSurface` (`Surface`) in the primary's
presentation mode:

```csharp
var singer = new SecondaryPresentationWindow(player, "secondary");
PresentationScreen? other = PresentationScreen.All.FirstOrDefault(screen => !screen.Equals(PresentationScreen.FromWindow(this)));
singer.ShowOnScreen(other, fullScreen: other is not null, owner: this);
// F11 or a double-click toggles full screen, Escape leaves it; closing it detaches.
```

`MediaSurface.ShowSecondaryOutput()` opens one with the surface's mode and `AutoHide`.

## BackgroundSurface

`BackgroundSurface` accepts an image path, video path, `ImageSource`, or opaque `Color`.
Video is always muted and loops. `OverlayContent` is presented above the background.

```xml
<media:BackgroundSurface
    AutoPlay="True"
    PlatformFactory="{Binding MediaPlatformFactory}"
    PresentationMode="NativeHwndOverlay"
    Source="{Binding BackgroundPath}">
    <media:BackgroundSurface.OverlayContent>
        <Grid><!-- application UI --></Grid>
    </media:BackgroundSurface.OverlayContent>
</media:BackgroundSurface>
```

## KaraokeSurface

`KaraokeSurface` accepts CDG, audio+CDG, archive CDG, and MIDI/KAR sources. It rejects
video and non-karaoke audio after inspection and presents an opaque 4:3 canvas.

```xml
<media:KaraokeSurface
    AutoPlay="True"
    CanvasColor="Black"
    PlatformFactory="{Binding MediaPlatformFactory}"
    PresentationMode="WpfComposited"
    Source="{Binding KaraokePath}" />
```

Both controls create and dispose their own player session after the host supplies a
platform factory. For the current Windows/Direct3D11 stack:

```csharp
MediaPlatformFactory = new WindowsDirect3D11MediaControlPlatformFactory();
```

Configure `DLLPath.Bass` and `DLLPath.FFmpeg`, or use the host's default native
directory, before loading media.
