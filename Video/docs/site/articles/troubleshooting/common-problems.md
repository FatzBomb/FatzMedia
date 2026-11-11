# Common problems

Most problems show up in the player's diagnostics before they show up anywhere else. This page lists the usual symptoms, their causes and fixes, and the messages to look for. Start by subscribing to the diagnostics and the error event, and log them somewhere you can read (see [Diagnostics](../concepts/diagnostics.md)).

```csharp
player.MediaDiagnostic += (_, e) =>
    Log($"[{e.Level}] {e.Category}: {e.Message}");

player.MediaError += (_, e) =>
    Log($"error {e.Code}: {e.Message} ({e.SourcePath})");
```

Both events fire on engine threads; marshal before you touch the UI. Messages in `Warning` and `Error` level matter most; `Info` messages tell you which path was chosen. After the player is created, `player.NativeDependencies.Describe()` lists every native library that did not load, and `player.LastNativeLoadMessage` gives a one-line summary.

> [!TIP]
> The test players have a Log panel with every diagnostic, and they write the same text to a file. If a problem reproduces there, you can copy the log into a bug report. See [Test players](../reference/test-players.md).

## Black video

The window stays black while the media plays, or nothing shows after load.

| Cause | How to tell | Fix |
| --- | --- | --- |
| The surface has no player | `IsSurfaceAttached` is `false`. | Set the surface's `Player`. |
| The renderer does not match the surface | Warning in `Wpf.MediaSurface`, `Avalonia.MediaSurface` or `WinForms.MediaSurface`: `the player did not accept the surface, so nothing is shown on it`. | With the Software or Vulkan renderer use `WpfComposited` in WPF; WinForms and Avalonia switch to a frame sink by themselves. See [Surfaces](../presentation/surfaces.md). |
| The surface is waiting for a frame (AutoHide) | `IsContentVisible` is `false`; no `PresentationFramePresented` event. | Check the diagnostics for a decode or renderer failure. Set `AutoHide = false` to see whatever the renderer last presented. |
| The player is in `Audio` mode | `IsPresentationSuspended` is `true`; log says `cleared, the player is in Audio mode`. | Switch to `Full` with `SetModeAsync(MediaPlayerMode.Full)`. |
| The file has no video | `HasVideo` is `false` and the file is audio. | Nothing to fix. Show cover art or a background yourself. |
| The file cannot be opened or decoded | `MediaError` with `OpenFailed`, `DecodeFailed` or `UnsupportedFormat`; or `Load` returned `false`. | Check the message. Make sure FFmpeg loaded (see below). |
| FFmpeg did not load | `NativeLibraryMissing`, or the `FFmpeg` category: `Missing required FFmpeg DLLs: ...`. | See [Native libraries not found](#native-libraries-not-found). |
| The GPU device was lost | `DeviceRecovery` Error: `GPU device lost (...); recovering.` | Wait: the player recovers. See [GPU device loss](../presentation/gpu-device-loss.md). |
| Vulkan cannot draw the frame | Warning in `Vulkan`: `Vulkan preview slice cannot draw this frame yet (...)`. | Frames with subtitles or a color transform are refused. Use the Direct3D11 renderer, or remove the transform. |
| Hardware decoding produced no frame | Warning in `VideoPipeline`: `Hardware video decode produced no usable startup frame; falling back to software ...`. | The player already fell back. If it does not recover, set `VideoDecodingMethod.Software`. |
| A WPF control is hidden behind the video | Controls placed over the surface do not show. | A native window draws above WPF content. Put the content in `OverlayContent`, or use `WpfComposited`. |
| The renderer call is stuck | Error in `Presentation`: `Renderer call has not returned for N s (...); presentation is blocked.` | Usually a hung graphics driver. Update the driver; try the Software renderer to confirm. |

Check also that the surface has a non-zero size: a collapsed or zero-sized control is never attached.

## No audio

| Cause | How to tell | Fix |
| --- | --- | --- |
| FatzAudio did not start | Warning in `Bootstrap`: `Audio engine FatzAudio could not start (...). Audio engine unavailable: video plays without sound; audio media will not open.` Loading an audio file fails with `NativeLibraryMissing`. Video plays without sound. | Check that `fatzaudio.dll` (`libfatzaudio.so` on Linux) is next to the application and is the version the `FatzMedia.Audio` binding expects; it is built from `Audio\` and copied by the build. See [Audio engine](../audio/audio-engine.md). |
| The player is in `Background` mode | Background mode never starts the audio engine. | Use `Full` mode. |
| The volume is zero | `player.Effects.Volume` is 0. | Raise it. |
| The selected device is missing | Warning in `FatzAudioEngine`: `Audio output device '...' was not found; playing on the system default. Devices: ...` (with `NoFallback` the player throws instead). | Pick a device from `FatzAudioOutputDevices.Enumerate()`. See [Output devices](../audio/output-devices.md). |
| A MIDI file is silent | The system has no General MIDI bank and no soundfont is set. | Set `MidiSoundfontPath` in the configuration to an `.sf2`, `.sf3` or `.dls` file. |
| The format is not one FatzAudio opens | `Load` returns `false` or an `UnsupportedFormat` error. | Let FFmpeg decode it: audio that FatzAudio cannot open is decoded through FFmpeg (`AudioFallback` category), so deploy FFmpeg. See [Supported formats](../playback/supported-formats.md). |

## Native libraries not found

The library ships no FFmpeg binaries; your application deploys them. FFmpeg is required for playback of video and for audio formats FatzAudio cannot open itself. FatzAudio's library is built with the repository and copied for you. BASS was removed in 037A, so an old `bass.dll` in your folder does nothing.

Look at `player.NativeDependencies.Describe()`. Each line names a file and its status, for example:

```text
FFmpeg avcodec-63.dll (required) is missing. Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.
```

Typical causes:

- **The files are not in a searched folder.** The player searches the folders you pass, then the defaults (`native` and `runtimes/win-x64/native` under the application folder). On Windows the search includes subfolders. On Linux it searches your paths, then the application folder, `native/` and `runtimes/linux-<arch>/native/`, without subfolders. `NativeDependencies.SearchPaths` shows exactly where it looked.
- **A path you passed does not exist.** The `Bootstrap` category reports `Specified path does not exist: '<path>'`, and a missing directory passed to `PlayerCore` raises `DirectoryNotFoundException` before native initialization.
- **A different FFmpeg version.** The library needs FFmpeg 9.0.x shared libraries: `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` and `swscale-10.dll`. Older DLLs do not load. Static executables are not enough.
- **The FFmpeg files are split across folders.** The log says `FFmpeg DLLs are split across folders; all seven must be in one folder.`
- **Wrong architecture.** The Windows build is x64. A 32-bit DLL, or a build for another CPU, fails to load.
- **A dependency of a DLL is missing**, which shows as `was found at <path> but not loaded because other FFmpeg files are missing` or `failed to load`.

See [Native dependencies](../getting-started/native-dependencies.md) for what to deploy and where, and [Test players](../reference/test-players.md) for the folder layout the repository's players use.

## Wrong audio device after a device change

By default (`AudioOutputDeviceId = null`) the player uses the system's default device and follows it when the default changes. If you select a specific device, the player keeps that device, and moves to the default only if the device goes away. Device choices are made when the player is created or reset; a running player does not move to another explicit device.

- To follow the system default, leave `AudioOutputDeviceId` at `null`.
- To switch to another device, create a new player with the new device id, or call `Reset` with a new configuration and load the media again. List devices with `FatzAudioOutputDevices.Enumerate()`. See [Output devices](../audio/output-devices.md).
- To decide what happens when a chosen device is unavailable, set `AudioOutputFallbackMode`: `FallbackToPrimary` (default) switches to the default device and logs a warning; `NoFallback` does not route audio elsewhere.
- Subscribe to `AudioDeviceChanged`. Its `Kind` is `DefaultDeviceChanged`, `DeviceStateChanged`, `DeviceRemoved`, `DeviceAdded` or `Unrecoverable`, and `DeviceName` and `FailureMessage` say what happened. The player does not change playback itself on such a notification. It is for your UI.

```csharp
player.AudioDeviceChanged += (_, e) =>
    Log($"audio device {e.Kind}: {e.DeviceName} {e.FailureMessage}");
```

## Stutter

Frames are dropped or shown unevenly.

| Cause | How to tell | Fix |
| --- | --- | --- |
| Software decoding of a demanding video | The `VideoDecoder` line says `in software`. | Use hardware decoding where available. See [Hardware decoding](../video/hardware-decoding.md). |
| A software rasterizer | Warning in `D3D11`: `No GPU device could be created (...); using WARP`, or the Software renderer is in use. | Use a GPU renderer. See [Renderers](../video/renderers.md). |
| WPF-composited surface at high resolution | `PresentationPath` is `FrameSink`. | Use `NativeHwndOverlay`, or a smaller video. |
| Frame rate does not divide the refresh rate | Info in `VideoCadence`: `Video 23.976 fps on a 60 Hz display: ... Motion can look uneven (judder)`, with a list of smooth rates. | Change the display's refresh rate, or try `VideoFrameTiming.DisplayLocked`. See [Frame timing](../video/frame-timing.md). |
| The refresh rate is wrong | `VideoCadence` ends with `Display rate reported by the platform, or 60 Hz when it cannot tell`. | On Linux set `LinuxPlatformOptions.DisplayRefreshRateHz`. |
| Network source | Info in `Network`: `Waiting for the network: the picture holds at ...`; the `BufferingChanged` event. | Raise `NetworkReadAheadPolicy`, or check the connection. |
| The CPU or GPU is saturated | High CPU in your process; many players. | Reduce load. See [Multiple players](../presentation/multiple-players.md). |
| Immediate present mode | `NativeSwapChainPresentMode.Immediate` set. | Use `VSync` (the default). |

Turn on `EnablePerformanceLogging` in the configuration to get per-second counters (`[VideoPipeline:Perf]`: presented, dropped, late) on `System.Diagnostics.Trace`. Add a `TraceListener` to read them.

## High CPU

- **Software decoding.** The `VideoDecoder` diagnostic says `in software`. Hardware decoding moves the work to the GPU. See [Hardware decoding](../video/hardware-decoding.md).
- **Software or WARP rendering.** All drawing runs on the CPU. See [Renderers](../video/renderers.md).
- **Frame-sink presentation.** A `FrameSink` path copies every frame from the GPU to the CPU. For full-HD and larger, prefer a native window (WPF, WinForms) or a shared GPU image (Avalonia).
- **Verbose logging.** `EnableVerboseLogging` and `EnablePerformanceLogging` add work. Leave them off in production.
- **Many players or large surfaces.** Each player and each surface (including second screens) is rendered separately.
- **Software tone mapping of HDR video.** HDR video decoded in software is tone mapped on the CPU; so is any video when `HdrToneMappingOptions.PreferCpu` is set.

## Subtitles missing

See also [Subtitles](../subtitles/subtitles.md).

| Cause | Fix |
| --- | --- |
| The text is drawn in an unexpected font | The Software and Vulkan renderers use the system's fonts through SkiaSharp. When the configured family (`SubtitleStyle.FontFamily`, `KaraokeStyle.FontFamily`, Segoe UI by default) is not installed, the default font is used. Install the font or configure one that is installed. |
| SkiaSharp's native library is missing | This only happens with a broken deployment: SkiaSharp and `libSkiaSharp` (`libSkiaSharp.dll` on Windows x64, `libSkiaSharp.so` on Linux x64) ship with the packages and the build copies them to the application folder or `runtimes/<rid>/native`. It goes missing when an installer or hand-made deployment leaves out the `runtimes` folder, or on an unsupported platform (for example Linux on ARM, or Alpine/musl). Playback continues; one `Subtitle` warning diagnostic names the library, and no text is drawn by the Software and Vulkan renderers. Deploy the full build output. |
| No track is selected | `SubtitleTracks` lists the tracks and `SelectedSubtitleTrackId` the chosen one (`null` is off). Call `SelectSubtitleTrack(id)`. |
| The sidecar file is not found | A sidecar must sit next to a local video file (not inside an archive), have the same name and end in `.srt`, `.ass` or `.ssa`. `ignoreSidecar` does not affect subtitles. |
| Embedded tracks have not arrived yet | Embedded text tracks of a local video are read after the video opens, so they appear later. Wait for `SubtitleTracksChanged`. If you select an embedded track on `MediaLoaded`, it is applied when the scan delivers it. |
| The player is in `Audio` mode | Nothing is presented in `Audio` mode. Switch to `Full`. |
| Timing is off | Adjust `SubtitleSyncOffset`, or `AdjustSubtitleSyncOffset(steps)`. |
| The style hides the text | Check `SubtitleStyle` in the configuration: font, size (6 to 240), colors (ARGB). A color with alpha `00` is invisible. |
| The surface is hidden | Same as black video: check `IsContentVisible` and the surface path. |

## Collecting information for a bug report

- The full diagnostics log, with levels and categories.
- `player.NativeDependencies.Describe()` and `SearchPaths`.
- The renderer in use, the surface's `PresentationPath` and `PresentationPathReason`, and the `VideoDecoder` line for the file.
- The configuration, the OS, the GPU and driver, and the FFmpeg and FatzAudio versions.
