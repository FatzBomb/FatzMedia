# Architecture And Usage

## Current Shape

```text
Host app
  -> owns WPF layout, controls, overlays, playlists, diagnostics, and display surface
  -> MediaPlayer
      -> PipelineMediaEngine
          -> PlaybackCoordinator
          -> AudioPipeline / CdgPipeline / VideoPipeline / RenderPipeline
          -> PlaybackClock for no-audio timing
      -> MediaRenderer
          -> OptimizedGraphicsRenderer
          -> host-owned WPF surface adapter
      -> MediaControls / MediaEffects / MediaFile / MediaMetadata
```

`Pipeline/` owns orchestration. `Core/` holds low-level reusable pieces such as BASS audio, FFmpeg video decode, CDG packet processing, graphics helpers, file detection, and archive support.

## Solution Layout

```text
FatzBombMedia/
  FatzBombMedia.sln
  README.md
  docs/
  FatzBombMedia.MediaControl/
    Api/
    Adapters/
    Controllers/
    Core/
    Pipeline/
    Runtime/
  FatzBombMedia.Player/
    native/
    ViewModels/
    MainWindow.xaml
```

## Ownership Boundaries

`FatzBombMedia.MediaControl` owns playback, decoding, timing, effects, file/archive detection, native loading, render output, and a thin WPF surface adapter.

The host app owns UI composition: windows, controls, playlists, overlays, transport buttons, diagnostics, and the visual surface.

The intended WPF integration:

```xml
<Grid x:Name="MediaSurfaceHost" Background="Black">
    <Image x:Name="MediaSurface"
           Stretch="Uniform"
           RenderOptions.BitmapScalingMode="NearestNeighbor" />
</Grid>
```

```csharp
DLLPath.Bass = nativePath;
DLLPath.FFmpeg = nativePath;
var player = new MediaPlayer();
var attachment = player.Renderer.AttachSurface(MediaSurface, MediaSurfaceHost);
```

The adapter does not decode, time media, create a second render loop, or own app layout.

## Active Media Paths

| Area | Current behavior |
|---|---|
| Audio | BASS/BASS_FX path for audio files and audio sides of CDG pairs. |
| CDG | Packet/frame engine driven by the pipeline clock or active audio position. |
| Video with audio | FFmpeg decodes video frames; video audio is decoded into a BASS push/tempo stream. |
| Video without audio | Uses `PlaybackClock` through `ClockAudioEngine`. |
| Images | Direct image files render as still foreground media. |
| Archives | Audio/CDG entries run from memory; archive video entries bridge through temp files for FFmpeg. |
| Backgrounds | Audio/CDG can use explicit, sidecar, or internal zip image/video backgrounds. Background videos are silent independent loops. |

## Load Trace

```text
MediaPlayer.Load(path) / LoadAsync(path)
  -> MediaFile.Load(path)
      -> PipelineMediaEngine.Open(path)
          -> file/archive detection
          -> PlaybackCoordinator opens the needed pipelines
      -> renderer source/background update
      -> metadata update
      -> optional PlayOnLoad after normalization readiness
```

## Disposal Trace

```text
Window closes or host swaps surface
  -> surfaceAttachment.Dispose()
      -> detach renderer event handlers

MediaPlayer.Dispose()
  -> Stop()
  -> Renderer.Dispose()
  -> Controls.Dispose()
  -> File.Close()
  -> PipelineMediaEngine.Dispose()
  -> ServiceProvider.Dispose()
```

