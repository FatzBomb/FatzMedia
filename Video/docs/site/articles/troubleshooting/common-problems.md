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
| BASS did not load | `NativeDependencies.IsAudioAvailable` is `false`; diagnostic `BASS not loaded; audio is unavailable.` or `bass.dll not found (audio unavailable)`. Loading an audio file fails with `Audio engine unavailable: BASS libraries not found; <file> was not loaded.` (`NativeLibraryMissing`). Video plays without sound. | Deploy `bass.dll` and `bass_fx.dll` (`libbass.so` and `libbass_fx.so` on Linux) where the player looks. See [Native libraries not found](#native-libraries-not-found). |
| The player is in `Background` mode | Background mode never starts the audio engine. | Use `Full` mode. |
| The volume is zero | `player.Effects.Volume` is 0. | Raise it. |
| Another application holds the device | Warning in `AudioOutput`: `Audio output unavailable (another application may be using the device exclusively); retrying`, and `AudioDeviceChanged` with `DeviceRemoved`. | Close the other application. The player retries once a second and resumes with `Audio output available again; playback resumed`. |
| The selected device is missing | Warning in `Bootstrap`: `Audio output device N could not be initialized (...); falling back to Primary.` | See the next section. |
| A MIDI file is silent | Warning in `BASSMIDI`: `No soundfont found; MIDI playback will be silent.` | Set `MidiSoundfontPath` in the configuration to an `.sf2` or `.dls` file, or install a system soundfont. |
| The format needs a BASS add-on that is not deployed | `Load` returns `false` or an `UnsupportedFormat` error. | Deploy the add-on, or let FFmpeg decode it: audio that BASS cannot open is decoded through FFmpeg (`AudioFallback` category). MIDI and audio CD still need their BASS add-ons. |

## Native libraries not found

The library ships no BASS or FFmpeg binaries; your application deploys them. FFmpeg is required for playback of video and most formats. BASS is needed for audio.

Look at `player.NativeDependencies.Describe()`. Each line names a file and its status, for example:

```text
FFmpeg avcodec-63.dll (required) is missing. Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.
BASS bass.dll (optional) was found at C:\app\native\bass.dll but failed to load.
Audio is unavailable: the BASS libraries did not load. Video plays without sound; audio media will not open.
```

Typical causes:

- **The files are not in a searched folder.** The player searches the folders you pass, then the defaults (`native` and `runtimes/win-x64/native` under the application folder). On Windows the search includes subfolders. On Linux it searches your paths, then the application folder, `native/` and `runtimes/linux-<arch>/native/`, without subfolders. `NativeDependencies.SearchPaths` shows exactly where it looked.
- **A path you passed does not exist.** The `Bootstrap` category reports `Specified path does not exist: '<path>'`, and a missing directory passed to `MediaControlPlayer` raises `DirectoryNotFoundException` before native initialization.
- **A different FFmpeg version.** The library needs FFmpeg 9.0.x shared libraries: `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` and `swscale-10.dll`. Older DLLs do not load. Static executables are not enough.
- **The FFmpeg files are split across folders.** The log says `FFmpeg DLLs are split across folders; all seven must be in one folder.`
- **Wrong architecture.** The Windows build is x64. A 32-bit DLL, or a build for another CPU, fails to load.
- **A dependency of a DLL is missing**, which shows as `was found at <path> but not loaded because other FFmpeg files are missing` or `failed to load`.

See [Native dependencies](../getting-started/native-dependencies.md) for what to deploy and where, and [Test players](../reference/test-players.md) for the folder layout the repository's players use.

## Wrong audio device after a device change

By default (`AudioOutputDeviceIndex = -1`) the player uses the system's primary device, and Windows moves playback when the default changes. If you select a specific device by index, the player keeps that device. Device choices are made when the player is created or reset; a running player does not move to another explicit device.

- To follow the system default, leave `AudioOutputDeviceIndex` at `-1`.
- To switch to another device, create a new player with the new index, or call `Reset` with a new configuration and load the media again. List devices with `AudioOutputDevices.Enumerate()` (Windows). See [Output devices](../audio/output-devices.md).
- To decide what happens when a chosen device is unavailable, set `AudioOutputFallbackMode`: `FallbackToPrimary` (default) switches to the primary device and logs a warning; `NoFallback` does not route audio elsewhere.
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
| The text is drawn in an unexpected font | The Software and Vulkan renderers use the system's fonts through SkiaSharp. When the configured family (`SubtitleStyle.FontFamily`, Segoe UI by default) is not installed, the default font is used. Install the font or configure one that is installed. |
| SkiaSharp's native library is missing | SkiaSharp and `libSkiaSharp` ship automatically with the Software and Vulkan renderer packages, so this only happens with a broken deployment: the `runtimes/<rid>/native` folder was not copied, or the runtime is unsupported (for example `linux-arm64` or musl-based Linux such as Alpine). Fix the deployment (keep the `runtimes` folder, publish for a supported RID: `win-x64` or `linux-x64`). Meanwhile subtitle rasterization fails with a `Subtitle` warning diagnostic and no text is drawn. |
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
- The configuration, the OS, the GPU and driver, and the FFmpeg and BASS versions.
