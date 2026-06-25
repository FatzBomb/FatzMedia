# FatzBombMedia.Player

Small WPF test shell for `FatzBombMedia.MediaControl`.

## Purpose

This project exists to test the reusable control without launching the final host app.

It may later become a small release/debug player, but currently it is intentionally lightweight.

## How MediaControl is incorporated

Project reference:

```xml
<ProjectReference Include="..\FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj" />
```

WPF adapter namespace in code:

```csharp
using FatzBombMedia.MediaControl;
using FatzBombMedia.MediaControl.Wpf;
```

## MVVM shape

The player uses a standard WPF MVVM split:

| Layer | Responsibility |
|---|---|
| `MainWindow.xaml` | Declares buttons, sliders, status text, and the `FatzBombMediaSurface`. |
| `MainWindow.xaml.cs` | Handles view-only concerns: file dialog, seek bar, and fullscreen chrome. |
| `ViewModels/MainWindowViewModel.cs` | Owns `MediaPlayer`, playback commands, effect properties, busy state, and status text. |

The player owns its display surface in XAML, but the surface owns the rendering attachment internally.
For this test app, D3DImage is intentionally bypassed so the checkbox compares only the two native paths:

```xml
<fbm:FatzBombMediaSurface Player="{Binding Player}"
                          Stretch="{Binding MediaStretch}"
                          BitmapScalingMode="{Binding BitmapScalingMode}"
                          PresentationMode="{Binding PresentationMode}"
                          OverlayHitTestVisible="False">
    <fbm:FatzBombMediaSurface.OverlayContent>
        <Grid IsHitTestVisible="False">
            <Border>
                <TextBlock Text="Native HWND Overlay" />
            </Border>
        </Grid>
    </fbm:FatzBombMediaSurface.OverlayContent>
</fbm:FatzBombMediaSurface>
```

`Enable Overlay` now means:

| Checkbox | Presentation mode | Expected backend |
|---|---|---|
| Off | `NativeHwnd` | `NativeHwndSwapChain` |
| On | `NativeHwndOverlay` | `NativeHwndSwapChain` plus transparent owned overlay window |

Expected logs when overlay is on:

```text
[FatzBombMediaSurface] PresentationMode=NativeHwndOverlay
[FatzBombMediaSurface] Native overlay window created
[FatzBombMediaSurface] Native overlay window shown
[MediaRenderer] Presentation backend requested: NativeHwnd hwnd=...
[VorticeD3D11] Presentation backend: NativeHwndSwapChain
```

The sample overlay is passive/click-through so double-click fullscreen and mouse gestures still reach the media surface. Interactive overlay controls should use `OverlayHitTestVisible="True"`.

Playback UI binds to view-model commands and properties:

```xml
<Button Content="Play" Command="{Binding PlayCommand}" />
<Slider Value="{Binding Volume, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
<CheckBox IsChecked="{Binding Normalize, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
```

## Native setup

The host player owns native DLL deployment. The built-in host resolver uses this design-time folder:

```text
FatzBombMedia.Player/native/
```

The player project copies them at build/publish time to:

```text
FatzBombMedia.Player/bin/x64/Debug/net10.0-windows7.0/native/
```

The host app sets the resolved folder before it creates the player:

```csharp
DLLPath.Bass = nativePath;
DLLPath.FFmpeg = nativePath;
var player = new MediaPlayer();
```

If the real host app wants separate BASS and FFmpeg folders, resolve each folder in the host app and set `DLLPath.Bass` and `DLLPath.FFmpeg` separately. The MediaControl project does not copy or package native DLLs.

## Smoke test checklist

1. Audio file plays.
2. Volume works.
3. Pitch works.
4. Tempo works.
5. Normalization can toggle.
6. MP3+CDG renders lyrics.
7. ZIP audio+CDG renders lyrics.
8. Video plays.
9. Video tempo affects BASS-processed audio and video stays synced.
10. Direct image files render as a still surface.
11. Same-name and internal zip karaoke backgrounds appear under audio/CDG.
12. Seek bar supports click/drag testing for audio, CDG, and video.
13. `Enable Overlay` off uses plain native HWND.
14. `Enable Overlay` on uses native HWND plus the transparent owned overlay window.
