# FatzMedia.Video.Wpf

Renderer-neutral WPF surfaces, player facade, and lightweight self-contained controls.
The host supplies an `IMediaControlPlatformFactory`, matching the existing `Player`
construction pattern; the WPF package does not select or reference a renderer.

## Package

Targets `net10.0-windows7.0` and supports **x64 only** (this matches the current
Windows native-media distribution). Install with:

```powershell
dotnet add package FatzMedia.Video.Wpf
```

The package is a strict adapter over `FatzMedia.Video.Windows` and Core. It owns
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

### First frame ready and the A/B swap (Stage 051A)

`FirstFrameReady` is raised on the UI thread once per load of media with visual content (and once
when `Player` is set to a session that already has such media open), when a frame of that load is on
the surface and its presentation host is shown. In `WpfComposited` mode the bitmap holds that frame
(a frame still waiting is written in the same dispatcher operation, after the host is made visible),
so whatever the handler changes renders in the same WPF frame. In `NativeHwndOverlay` mode it is
raised after the swap chain presented the frame (best effort: the desktop shows it at the next
refresh). It is not raised for audio-only media or in Audio mode. `PresentationFramePresented` (per
session, every frame, background thread) is unchanged.

A/B swap without a black flash: two players, two surfaces stacked in one grid cell, the next clip
loaded into the hidden one.

```csharp
// Hidden, not Collapsed: a collapsed surface has no size, so its first frame would be rendered at 1x1.
surfaceB.Visibility = Visibility.Hidden;

async Task SwapToAsync(string clip)
{
    void OnReady(object? sender, EventArgs e)
    {
        surfaceB.FirstFrameReady -= OnReady;
        // One UI-thread turn: the new clip, the old one gone and the rest of the screen change together.
        surfaceB.Visibility = Visibility.Visible;
        surfaceA.Visibility = Visibility.Hidden;
        questionPanel.Visibility = Visibility.Visible;
        _ = playerA.StopAsync();
        (playerA, playerB, surfaceA, surfaceB) = (playerB, playerA, surfaceB, surfaceA);
    }

    surfaceB.FirstFrameReady += OnReady;   // subscribe before loading
    await playerB.LoadAsync(clip);
    await playerB.PlayAsync();
}
```

Use `PresentationMode="WpfComposited"` on both surfaces: in native mode the video is a separate
window, so WPF content cannot change in the same frame. The same works for an inset clip (a smaller
surface shown over the main one on its first frame). `Background`-mode players keep the hidden one
cheap. `CompositedUiThreadTime` and `CompositedFramesPublished` total the UI-thread time spent writing
composited frames (047A's before and after numbers); the WPF test player shows them per second.

`SecondaryPresentationWindow` hosts a projector `MediaSurface` (`Surface`) in the primary's
presentation mode:

```csharp
var singer = new SecondaryPresentationWindow(player, "secondary");
PresentationScreen? other = PresentationScreen.All.FirstOrDefault(screen => !screen.Equals(PresentationScreen.FromWindow(this)));
singer.ShowOnScreen(other, fullScreen: other is not null, owner: this);
// F11 or a double-click toggles full screen; Escape leaves it, or closes a windowed one;
// closing it detaches.
```

`MediaSurface.ShowSecondaryOutput()` (and `CloseSecondaryOutput`, `IsSecondaryOutputOpen`,
`SecondaryOutputsChanged`) is `[Obsolete]` (ENG-039) and will be removed: open a
`SecondaryPresentationWindow` as above.

`WpfMediaInputSources.FromImageSource(ImageSource, displayName)` converts a WPF bitmap to a Core
BGRA source (Stage 046A slice 2; `Player`'s `ImageSource` overloads use it), the same shape as
`AvaloniaMediaInputSources.FromBitmap` and `WinFormsMediaInputSources.FromBitmap`.

## BackgroundSurface

`BackgroundSurface` accepts an image path, video path, `ImageSource`, `MediaInputSource`, or
opaque `Color`. Video loops without sound: since Stage 046A slice 2 its player runs in
`MediaPlayerMode.Background`, so a background never starts the audio engine (before, a muted
Full-mode player did). `OverlayContent` is presented above the background.

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
platform factory. Their rules (what each accepts, the player's mode and settings, creation,
reload and release) are Core's `LightweightPlayerPolicy` and `LightweightPlayerController`
(Stage 046A slice 2), shared with the Avalonia and WinForms adapters, which offer the same two
controls. `NativeLibraryPaths` (default `null`: `DLLPath` and the platform's default folders)
sets the folders their player loads BASS and FFmpeg from. For the current Windows/Direct3D11
stack:

```csharp
MediaPlatformFactory = new WindowsDirect3D11MediaControlPlatformFactory();
```

Configure `DLLPath.Bass` and `DLLPath.FFmpeg`, or use the host's default native
directory, before loading media.
