# FatzBomb.MediaControl.Player

Small WPF test shell for `FatzBomb.MediaControl`.

## Purpose

This project exists to test the reusable control without launching the final host app.

It may later become a small release/debug player, but currently it is intentionally lightweight.

## How MediaControl is incorporated

Project reference:

```xml
<ProjectReference Include="..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj" />
<ProjectReference Include="..\FatzBomb.MediaControl.Wpf\FatzBomb.MediaControl.Wpf.csproj" />
```

WPF adapter namespace in code:

```csharp
using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Wpf;
```

## MVVM shape

The player logic is shared with the Avalonia and WinForms test players (Stage 044A): it lives in
`FatzBomb.MediaControl.Player.Shared` (`PlayerViewModel`), and this project supplies the WPF
pieces.

| Layer | Responsibility |
|---|---|
| `MainWindow.xaml`, `Controls/PlayerFlyoutPanel.xaml` | Declare buttons, sliders, flyouts, status text, and the `MediaSurface`. |
| `MainWindow.xaml.cs` | View-only concerns: seek bar drag, file drops, fullscreen chrome, the secondary window. |
| `ViewModels/MainWindowViewModel.cs` | Derives from the shared `PlayerViewModel`; adds the WPF-only presentation mode and surface auto-hide. |
| `Hosting/` | The WPF dispatcher and timer, clipboard, open-file dialog, and the engine factory (renderer chain, Windows platform, native paths). |
| Shared `PlayerViewModel` | Engine creation, transport, scrubbing, sync, effects, equalizer, subtitles, visual transforms, prepared next, network sources, player mode, log. |

The player owns its display surface in XAML. The WPF adapter supplies native HWND surfaces to the Core session; Core owns composition, swap chains, retained layers, target fan-out, and presentation.
The test app uses `MediaSurface` as a native HWND interop control:

```xml
<fbm:MediaSurface Player="{Binding Player}"
                           VideoDisplayMode="{Binding VideoDisplayMode}"
                           PresentationMode="{Binding PresentationMode}"
                           OverlayHitTestVisible="False">
    <fbm:MediaSurface.OverlayContent>
        <Grid IsHitTestVisible="False">
            <Border>
                <TextBlock Text="Native HWND Overlay" />
            </Border>
        </Grid>
    </fbm:MediaSurface.OverlayContent>
</fbm:MediaSurface>
```

Overlay mode means:

| Checkbox | Presentation mode | Expected backend |
|---|---|---|
| Off | `NativeHwnd` | `NativeHwndSwapChain` |
| On | `NativeHwndOverlay` | `NativeHwndSwapChain` plus transparent owned overlay window |

Expected diagnostics when overlay is on:

```text
[NativeMediaPresentationCoordinator] Refreshing Core presentation attachments
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

The host player owns native DLL deployment. The built-in host resolver uses this
design-time folder:

```text
FatzBomb.MediaControl.Player/native/
```

The player project copies them at build/publish time to:

```text
FatzBomb.MediaControl.Player/bin/x64/Debug/net10.0-windows7.0/native/
```

The host passes the resolved directory list when it creates the player:

```csharp
var platform = new WindowsDirect3D11MediaControlPlatformFactory();
var player = await Player.CreateAsync(configuration, [nativePath], platform);
```

The loader also recognizes `runtimes/win-x64/native` beneath the application base.
A real host may pass separate BASS and FFmpeg directories in search-priority order.
The MediaControl packages do not copy or redistribute native DLLs. See
`docs/NATIVE_DEPENDENCIES.md` for the complete host distribution policy.

## Command line

The player understands the flags all test players share (`PlayerStartupOptions`): a media file
or http(s) URL, `--url=`, `--mode=full|background|audio`, `--native=<dir>`, `--net-retries=`,
`--net-ca-file=`, `--net-allow-invalid-certs`, `--no-hdr-tonemap`, `--hdr-tonemap-cpu`, `--hdr-output`,
`--trim-silence`, `--reset-pitch-on-load`, `--reset-tempo-on-load`, `--eq=g1,...,g10`,
`--prepare-next=`, `--auto-next`, `--fade-next=`, `--visual-transform-demo`, plus its own
`--renderer=`/`--software` (below) and `--no-auto-hide`. The **PLAYER** box in the **A/V** flyout
switches the player mode on the fly (Stage 043A).

## Renderer choice

`--renderer=auto|gpu|warp|software` picks the renderer (Stage 036A). `auto`, the default,
uses Direct3D11 on the GPU, then Direct3D11 on WARP (Windows' software rasterizer), then the
software renderer, choosing at startup. `gpu` and `warp` force one Direct3D11 device (`warp`
decodes in software); `software` (or the older `--software`) forces the software renderer
with the WPF-composited surface and software decoding. The status line names the renderer
and device, and the Log panel's `Renderer` entries say what was tried and why.

## HDR video

HDR10 video (Stage 038A) and HLG video (Stage 042A) are tone mapped to SDR: the Log's
`Decoding ...` line says `HDR10/PQ, tone mapped to SDR` or `HLG HDR, tone mapped to SDR`, and an
Info line names the peak brightness used (for HLG, BT.2100's 1000-nit nominal display).
With the Direct3D11 renderer and hardware decoding, a shader does it on the GPU (a `D3D11.Hdr`
line says so); otherwise the engine's CPU tone mapper does, with the same curve.
`--hdr-tonemap-cpu` uses the CPU tone mapper on Direct3D11 too, and `--no-hdr-tonemap` shows HDR
as before (washed out), for comparison. `--hdr-output` (Direct3D11, stage 042A slice 2) shows
HDR video in HDR on a monitor with Windows HDR on, instead of tone mapping it; a
`D3D11.HdrOutput` line names the output and, when it stays SDR, why.

## Subtitles

The **Sync** flyout has a **SUBTITLES** picker: Off plus the loaded media's embedded tracks
and sidecar files (`.srt`, `.ass`, `.ssa` next to the media). It refreshes whenever media
loads or closes and is disabled when the media has no subtitles. The SUB buttons beside it
shift subtitle timing.

## Display-locked timing

**Display-Locked** in the **A/V** flyout (off by default) times video frames to the refreshes of
the monitor showing the video instead of the video's own frame times: an even 3:2 for 24 fps on
60 Hz, with no stray 3:3:1. It needs the Direct3D11 renderer with native windows (the default
MODE). With the Secondary Window on a monitor with another refresh rate, each monitor follows
its own refreshes. The Log panel's `VideoFrameTiming` entries say when it is on, or why it is
unavailable, and summarize the cadence every minute. It sets `MediaControlPlayer.VideoFrameTiming`.

## Silent drag

**Silent Drag** in the **Audio** flyout (on by default) mutes audio while the seek bar is
dragged; it plays again when the drag rests for about a tenth of a second or is released. Off,
drags sound as before. It sets `MediaControlPlayer.ScrubAudio`; the volume setting is not
touched.

## Log panel

The **Log** button in the toolbar opens every engine diagnostic (info, warnings, and
errors), newest first, with Copy all, Clear, and Open log file. The latest warning or error
also shows in the status line. Each entry is written to
`%TEMP%\FatzBombMedia\player-debug.log`, which is overwritten on each start, so copy it
before relaunching if you need it for a bug report.

## Smoke Test Checklist

1. Audio file plays.
2. Volume works.
3. Pitch works.
4. Tempo works.
5. Normalization can toggle.
6. MP3+CD+G renders Cdg lyrics.
7. ZIP audio+CD+G renders  Cdg lyrics.
8. Video plays.
9. Video tempo affects BASS-processed audio and video stays synced.
10. Direct image files render as a still surface.
11. Same-name and internal zip karaoke backgrounds appear under audio+Cdg.
12. Seek bar supports click/drag testing for audio, Cdg, and video.
13. Native HWND output appears in the main surface.
14. Full-screen overlay controls appear through the transparent owned overlay window.

For the current presentation regression pass after refactors, use:

```text
docs/VALIDATION.md
```

## Secondary-Output Validation

The Player includes a validation button for secondary/projector output. The Player does not call renderer or swap-chain APIs directly. It asks the WPF `MediaSurface` to open or close a secondary output:

```csharp
MediaSurface.ShowSecondaryOutput();
MediaSurface.CloseSecondaryOutput();
```

`MediaSurface` creates the WPF validation window and supplies its HWND to the shared session as a neutral presentation-surface descriptor. The shared media-control implementation owns the secondary target, swap-chain lifetime, compositor fan-out, and final presents.

Stage 10X smoke tests:

- Primary-only output.
- Primary + secondary/projector output.
- Audio-only/no visual clears both targets.
- Image-only appears on both targets.
- Video-only hardware and software paths appear on both targets.
- CD+G/Cdg-only appears on both targets.
- CD+G/Cdg + image background appears on both targets.
- CD+G/Cdg + video background appears on both targets.
- Wash opacity/color changes appear on both targets.
- Resize primary during playback.
- Resize secondary during playback.
- Close secondary while playing and verify primary remains stable.
- Repeated open/stop/type switches leave no stale layer artifacts.
