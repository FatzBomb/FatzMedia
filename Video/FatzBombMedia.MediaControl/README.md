# FatzBombMedia.MediaControl

Reusable WPF media-control library for karaoke, audio, CDG, and video playback.

## Install

For local development, prefer a project reference from the test player:

```xml
<ProjectReference Include="..\FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj" />
```

For NuGet consumption, pack the control and add the package from a local source:

```powershell
dotnet pack .\FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj -c Release -o C:\LocalNuget
dotnet nuget add source C:\LocalNuget -n LocalFatzBombMedia
dotnet add package FatzBombMedia.MediaControl --source C:\LocalNuget
```

## Basic WPF Use

Add the WPF adapter namespace:

```xml
xmlns:media="clr-namespace:FatzBombMedia.MediaControl.Wpf;assembly=FatzBombMedia.MediaControl"
```

Use the drop-in surface control. The host binds a player to the surface; it does not manually
assign an `ImageSource` or keep an attachment object:

```xml
<media:FatzBombMediaSurface Player="{Binding Player}"
                            VideoDisplayMode="Zoom"
                            BitmapScalingMode="HighQuality"
                            PresentationMode="D3DImage" />
```

Create and configure the player from the host/view-model:

```csharp
var nativePath = ResolveHostNativeLibraryPath();
DLLPath.Bass = nativePath;
DLLPath.FFmpeg = nativePath;

var player = new MediaPlayer
{
    BackgroundColor = Colors.Transparent,
    BackgroundAutoDetect = true,
    PlayOnLoad = true,
    Normalize = true
};

player.Effects.Volume = 1.0;
player.Effects.Pitch = 0;
player.Effects.Tempo = 100;
await player.LoadAsync(path);
```

`FatzBombMediaSurface` owns the WPF presentation details. Its default path is WPF `D3DImage`,
which keeps D3D11 hardware frames on the GPU where possible while avoiding WPF airspace issues.
`PresentationMode=NativeHwnd` requests the strict child-HWND DXGI swap-chain target.
Use it to A/B test WPF `D3DImage` versus native presentation. When active, logs should show
`Presentation backend: NativeHwndSwapChain` and should not create a new `D3DImage shared presentation surface`
for the active video target.

The older `renderer.AttachSurface(Image, Panel)` adapter remains available as a thin compatibility
helper, but new hosts should prefer `FatzBombMediaSurface`.

Background defaults:

| Property | Default | Purpose |
|---|---:|---|
| `BackgroundColor` | `Colors.Transparent` | Base color behind all media/background layers. |
| `BackgroundSource` | `null` | Host-provided fallback image source. |
| `BackgroundStretch` | `Stretch.Uniform` | Stretch mode for image/video backgrounds. |
| `BackgroundAutoDetect` | `true` | Lets karaoke loads override the fallback background when a sidecar/zipped image or video is found. |
| `BackgroundVisibleWhenStopped` | `true` | Keeps the last background video frame visible after rendering stops. Decoding is stopped either way. |

Auto-detect is intended for karaoke/audio use. A sidecar background must use the same base filename as the loaded media file, such as `Song.zip` with `Song.jpg` or `Song.mp4`. For archives, an image or video found inside the zip overrides the matching sidecar background. When no auto background is found, the manual `BackgroundSource` and `BackgroundColor` remain in effect.

Background videos are silent, independent visual loops. Seeking audio/CDG media does not seek the background video.

## Native DLLs

Native DLLs are required at runtime and must be x64. `FatzBombMedia.MediaControl` does not include, package, or copy BASS/FFmpeg native DLLs. The host app must provide native DLL folders and set the BASS/FFmpeg paths before creating the player/control.

Recommended host layout:

```text
ConfiguredNativePath/
  bass.dll
  bass_fx.dll
  avcodec-<major>.dll
  avdevice-<major>.dll
  avfilter-<major>.dll
  avformat-<major>.dll
  avutil-<major>.dll
  swresample-<major>.dll
  swscale-<major>.dll
```

Required BASS files:

| File | Version family |
|---|---|
| `bass.dll` | Latest x64 BASS release compatible with the referenced `ManagedBass` package |
| `bass_fx.dll` | Latest x64 BASS_FX release compatible with the referenced `ManagedBass.Fx` package |

Optional BASS plugin files enable more audio formats:

```text
bass_aac.dll
bass_ac3.dll
bassalac.dll
basscd.dll
bassflac.dll
bassmidi.dll
bassopus.dll
basswma.dll
```

MIDI playback is not currently claimed as supported. `bassmidi.dll` is only one part of that path; the control still needs MIDI stream handling, sound-font setup, accepted-type validation, and player smoke testing.

Required FFmpeg files must match the referenced `FFmpeg.AutoGen` package. The loader derives the required major version from `FFmpeg.AutoGen` at runtime, so the exact names change when that package is updated.

| File pattern | Source |
|---|---|
| `avcodec-<major>.dll` | `ffmpeg.LIBAVCODEC_VERSION_MAJOR` |
| `avdevice-<major>.dll` | `ffmpeg.LIBAVDEVICE_VERSION_MAJOR` |
| `avfilter-<major>.dll` | `ffmpeg.LIBAVFILTER_VERSION_MAJOR` |
| `avformat-<major>.dll` | `ffmpeg.LIBAVFORMAT_VERSION_MAJOR` |
| `avutil-<major>.dll` | `ffmpeg.LIBAVUTIL_VERSION_MAJOR` |
| `swresample-<major>.dll` | `ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR` |
| `swscale-<major>.dll` | `ffmpeg.LIBSWSCALE_VERSION_MAJOR` |

## Setting the DLL Path

Resolve the native path or paths inside the host app and set them before creating `MediaPlayer`:

```csharp
DLLPath.Bass = @"C:\MyApp\native";
DLLPath.FFmpeg = @"C:\MyApp\native";
var player = new MediaPlayer();
```

Or, if you prefer an alias in the host app:

```csharp
using Control = FatzBombMedia.MediaControl;

Control.DLLPath.Bass = nativePath;
Control.DLLPath.FFmpeg = nativePath;
var player = new Control.MediaPlayer();
```

Set these before creating the first player. Native libraries are loaded once for the app process. If `DLLPath.Bass` and `DLLPath.FFmpeg` are empty, the loader falls back to the host app's default `native` folder under the app output directory. Constructor paths still work for compatibility and override the global setting for that instance.

Avoid hard-coded developer paths in released apps. Prefer host-owned deployment folders resolved by the app.

## Rendering

Rendering and decode selection are automatic. Consumers should not expose DirectX/software choices in user-facing UI.

The current WPF integration is a host-owned `Image` surface, with an optional host-owned background panel:

```csharp
DLLPath.Bass = nativePath;
DLLPath.FFmpeg = nativePath;
var player = new MediaPlayer();
player.BackgroundSource = myBackgroundImage;
player.BackgroundColor = Colors.Transparent;
player.BackgroundStretch = Stretch.Uniform;
player.BackgroundAutoDetect = true;
player.BackgroundVisibleWhenStopped = true;
var attachment = player.Renderer.AttachSurface(MyImage, MyBackgroundPanel);
```

The old packaged `MediaPlayerControl` was removed. Apps should define their own surface and attach to it.

FFmpeg video decode attempts hardware acceleration automatically where supported. D3D11 hardware frames can stay on the GPU and present through a WPF `D3DImage` shared surface; software and non-D3D11 fallback paths remain available when that bridge is not available.

## Accepted File Types

After the player is created, the control exposes the file types accepted by the native DLLs that actually loaded:

```csharp
var player = new MediaPlayer();

IReadOnlyList<string> accepted = player.AcceptedTypes.All;
IReadOnlyList<MediaFileFilter> filters = player.Filters;
string openDialogFilter = player.OpenFileDialogFilter;
```

`OpenFileDialogFilter` is ready for WPF's `OpenFileDialog.Filter`. `Filters` is the readable version for hosts that want to build their own picker UI.

## Main Entry Points

| Type | Use |
|---|---|
| `FatzBombMedia.MediaControl.MediaPlayer` | Core media-player API. |
| `FatzBombMedia.MediaControl.DLLPath` | Host-owned BASS/FFmpeg native path settings. |
| `FatzBombMedia.MediaControl.AcceptedMediaTypes` | Readable accepted extension groups for the current runtime. |
| `FatzBombMedia.MediaControl.MediaFileFilter` | File picker filter group with display name and extensions. |
| `FatzBombMedia.MediaControl.Wpf.RenderSurface` | WPF adapter for attaching renderer output to a host-owned `Image`. |

## Protected Areas

The active control project runs through `Pipeline/`. The old concrete `Core.MediaEngine` and `AudioVideoEngine` orchestration was removed from the active project; use the root `legacy/` copy for reference if needed.

Do not casually remove these retained active areas:

- `Pipeline/*`
- `Core/DxCdg/*`
- `Core/Audio/AudioEngine.cs`
- `Core/Audio/AudioStreamManager.cs`
- `Core/Audio/AudioNormalizer.cs`
- `Core/Audio/VideoAudioEngine.cs`
- `Core/Video/VideoDecoder.cs`
- `Core/Files/*`
- `Core/Graphics/*`
- `Controllers/MediaRenderer.cs`
- `Runtime/Bootstrap*`

Read the root `docs/` folder before refactoring. Start with `docs/00-START-HERE.md` and `docs/02-Features-And-Backlog.md`.

## Native HWND with overlay content

For high-performance WPF playback with overlay controls, use `NativeHwndOverlay`. This presents video through the native DXGI swap-chain path and hosts the overlay content in a transparent owned WPF window that follows the control.

```xml
<media:FatzBombMediaSurface Player="{Binding Player}"
                            PresentationMode="NativeHwndOverlay"
                            VideoDisplayMode="Zoom">
    <media:FatzBombMediaSurface.OverlayContent>
        <Grid>
            <!-- host-owned overlay controls -->
        </Grid>
    </media:FatzBombMediaSurface.OverlayContent>
</media:FatzBombMediaSurface>
```

Use `D3DImage` when you need true same-window WPF visual-tree behavior. Use `NativeHwndOverlay` when native playback smoothness matters more and the overlay can be hosted by the control's follower window.

## Renderer architecture documents

The current renderer split is documented in the solution `docs` folder:

| Document | Purpose |
|---|---|
| `docs/FatzBombMedia_Renderer_Current_Architecture.md` | Current post-refactor renderer architecture and ownership map. |
| `docs/FatzBombMedia_Renderer_Smoke_Test_Checklist.md` | Manual regression checklist for video, CDG, background media, resize, backend, and fallback paths. |
| `docs/FatzBombMedia_Renderer_Refactor_and_Implementation_Plan.md` | Historical refactor plan and current completion status. |

### DXCDG active CDG path

The active CDG path is now DXCDG under `Core/DxCdg`. It uses indexed-color state plus a D3D11 palette shader instead of generating full ARGB CDG bitmap frames. Legacy bitmap-first CDG code is preserved only under `LegacyCdg/` as disabled reference material.

No `AppContext` switches are required for normal CDG playback. A CDG packet stream now flows through:

```text
DxCdgEngine
  -> DxCdgStateMachine
  -> DxCdgRenderSnapshot
  -> DxCdgD3D11TexturePresenter
  -> active D3D11 presentation backend
```

See `docs/FatzBombMedia_DXCDG_Roadmap.md`.
