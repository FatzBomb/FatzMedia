# Current Architecture

## Projects

- `FatzBomb.MediaControl`: OS- and UI-agnostic Core media-control engine (`net10.0`).
- `FatzBomb.MediaControl.Rendering.Direct3D11`: Direct3D 11 renderer backend and subtitle rasterizer.
- `FatzBomb.MediaControl.Rendering.Software`: portable deterministic software renderer — in-memory premultiplied BGRA8 composition, no GPU or window (`net10.0`, references Core only).
- `FatzBomb.MediaControl.Windows`: Windows platform composition, native integration, and hardware-decode policy.
- `FatzBomb.MediaControl.Windows.Direct3D11`: optional convenience bundle pairing Windows services with the current renderer.
- `FatzBomb.MediaControl.Wpf`: strict WPF adapter and controls; receives a platform/renderer factory from its host.
- `FatzBomb.MediaControl.Player`: WPF validation host.

UI adapters, renderer backends, and platform services are separate projects using the same
package pattern. Status as of 2026-10-16:

- UI adapters: `FatzBomb.MediaControl.Avalonia` (composited frame-sink, preview) and
  `FatzBomb.MediaControl.WinForms` (HWND) implemented; `FatzBomb.MediaControl.WinUI` planned.
- Renderer backends: `FatzBomb.MediaControl.Rendering.Vulkan` (headless offscreen preview,
  frame-sink and shared-image surfaces, any number of them) implemented; `Rendering.OpenGL` and `Rendering.Metal` planned.
- Platform services: `FatzBomb.MediaControl.Linux` (baseline) implemented;
  `FatzBomb.MediaControl.MacOS` planned.

See
`roadmap/complete/024A_COMPLETED_SoftwareRenderer.md`,
`roadmap/active/026A_ACTIVE_CrossPlatformRendererBackends.md`,
`roadmap/active/026B_ACTIVE_CrossPlatformPlatformServices.md`, and
`roadmap/active/027A_ACTIVE_UiAdapterImplementations.md`.

## Platform Composition

`MediaControlPlayer` receives an `IMediaControlPlatformFactory`, which selects the
native media-engine services for the current operating system. Core owns session
lifecycle and Core-service composition through `MediaControlServiceProvider`; an OS
package owns native-library loading, platform-specific registrations through
`IMediaControlPlatformServices`, and the concrete factory.
`WindowsMediaControlPlatformFactory` now lives in `FatzBomb.MediaControl.Windows`.
Windows accesses Core only through public platform contracts.

Renderer selection is independent of operating-system selection. The Player host
currently injects `WindowsDirect3D11MediaControlPlatformFactory` from the thin
Windows+Direct3D11 integration bundle. `FatzBomb.MediaControl.Windows` references
Core only, and the WPF adapter contains no renderer types. Hosts can inject another
`IMediaPresentationCoordinatorFactory` — including the portable software renderer's
`SoftwarePresentationCoordinatorFactory` — without changing WPF, Core playback, or the
Windows native-service layer.

### Renderer Configuration

`MediaPlayerConfiguration` contains renderer-neutral session policy. It does not select
a concrete rendering backend and contains no DXGI swap-chain mode. The host selects a
renderer by injecting its factory. Direct3D11-specific creation policy lives in
`Direct3D11PresentationOptions`, supplied to `Direct3D11PresentationCoordinatorFactory`
or the `WindowsDirect3D11MediaControlPlatformFactory` convenience bundle. Its native
swap-chain present mode defaults to VSync; Immediate remains an explicit diagnostic mode.

### Software Renderer (Stage 024A)

`FatzBomb.MediaControl.Rendering.Software` is a portable, deterministic renderer
package (`net10.0`) that references Core only and implements the same public
`IMediaRendererBackend` / `IMediaPresentationCoordinatorFactory` seams as Direct3D11.
Hosts opt in by injecting `SoftwarePresentationCoordinatorFactory`, which supplies
Core's `MediaPresentationCoordinator` with the internal `SoftwareRendererBackend`, a
no-op `ISubtitleRasterizer` (`NoOpSubtitleRasterizer`), and
`SystemPrecisionWaiterFactory`. No Core, Windows, or WPF selection logic changes.

**Output contract** — `SoftwareCompositionRenderer.Render(MediaCompositionFrame, width,
height)` returns an immutable `SoftwareRenderOutput` with a typed
`SoftwareRenderStatus` (`Rendered`, `UnsupportedFrame`, `InvalidFrame`). Successful
output is premultiplied BGRA8 with tightly packed rows (`stride = width * 4`); the same
frame and size always produce byte-identical pixels. Deterministic rules:

- **Clear modes** — `Black`, `Transparent`, and `ConfiguredColor` from `ClearMode` /
  `ClearArgb`; `PreservePrevious` clears to transparent because rendering is stateless.
- **Software BGRA background** — `BackgroundBgra` / `BackgroundWidth` /
  `BackgroundHeight` / `BackgroundStride` stretched by `EffectiveBackgroundStretchMode`
  (`Original`, `UniformFit`, `Fill`, `UniformToFill`; `Auto` resolves through the
  effective-mode property). Scaling uses nearest-neighbor sampling at destination pixel
  centers, and destination edges round half-away-from-zero (Core `NearestSampledPlacement`,
  shared with the Vulkan renderer).
- **Wash** — `WashArgb` straight-alpha blended over the background.
- **CDG overlay** — index-based transparency: `CdgRenderSnapshot.BackgroundColorIndex`
  is skipped and every other palette entry is forced opaque, matching the Direct3D11
  shader. The snapshot is composited with `UniformFit`.
- **Pre-rasterized subtitles** — `SubtitleBgra` placed via `SubtitleCanvas*` (canvas
  mapping through `ViewportGeometry.MapCanvasRegionToViewport`) or `UniformFit` when no
  canvas is set; premultiplied blend.
- **Layer order** — clear, background, wash, CDG, subtitles.
- **Unsupported inputs** — frames requesting native or retained backgrounds
  (`BackgroundNativeResource`, `UseRetainedBackground`) return `UnsupportedFrame`;
  invalid frames (non-positive output size, bad background/subtitle buffers or stride,
  malformed CDG snapshot, oversized buffer) return `InvalidFrame`; neither throws.
- **Backend** — the internal `SoftwareRendererBackend` accepts one
  `ManagedBgraFrameSink` presentation surface per surface id (the primary plus any
  secondary/projector windows, ENG-016), renders each composition once per target at
  that target's pixel size, and publishes it through `IMediaPresentationFrameSink`. With
  a primary attached, a present succeeds only when the primary presented. Native-frame and
  device methods report unsupported without throwing. It retains no presentation
  stretch or background-frame state: effective stretch travels on each composition
  frame, and every render uses only the frame supplied by Core.

## Ownership

Renderer fallback (Stage 036A): Direct3D11 tries the GPU, then WARP, inside its own device
creation. Across renderers, Core's `FallbackPresentationCoordinatorFactory` uses the first
candidate whose `IMediaPresentationAvailabilityProbe` reports it available (for example
Direct3D11, then the software renderer), once, at first use; the probe result says what the
host must pair with it (`RequiresFrameSinkSurface`, `RequiresSoftwareDecoding`). The host
composes the chain and builds surfaces and decoding to match; Core does not switch renderers
mid-session.

Core owns playback, commands, media state, sidecar detection, background image/video lifecycle, Cdg state, wash, `MediaPresentationCoordinator`, retained composition planning, target lifecycle policy, renderer-neutral configuration, and platform/renderer contracts. The Direct3D11 renderer owns its native device, GPU resources, renderer-specific options, swap chains, DirectComposition implementation, subtitle rasterization, and final GPU execution. The Software renderer owns deterministic in-memory composition of software-backed layers and its managed frame-sink targets.

Adapters own only framework interop: control lifecycle, native surface creation, DPI/size, visibility, focus/input bridge, and framework-specific source conversion helpers.

Adapters must not own renderer selection, layer roles, frame processing, Cdg invalidation, background/video presentation, compositor timing, swap-chain policy, or final present logic.

### WPF Presentation Modes

`WpfPresentationMode.NativeHwndOverlay` remains the default and fastest path. It uses the
existing child HWND plus transparent overlay-window strategy. `WpfComposited` is an
explicit WPF-only alternative: Direct3D11 renders the same final Core composition into
an offscreen target, reads BGRA pixels back through `IMediaPresentationFrameSink`, and
the WPF adapter copies the completed frame into a `WriteableBitmap`. This avoids HWND
airspace limitations but introduces synchronous GPU-readback cost.

The mode is selected before media is loaded and applies to the whole player session.
Other UI adapters should use their best framework-native presentation path and only add
an equivalent choice when their framework has a comparable limitation.

If a WPF binding or imperative setter requests another presentation mode while media
is open, the three WPF surfaces keep the applied mode without throwing or rebuilding
their hosts. WPF retains the requested base value and the surface re-coerces it when
the session closes or the lightweight player is released, applying the deferred mode
only after the creation-time constraint is no longer active. In-place media
replacement keeps the same session open, so it deliberately preserves the applied
mode and carries the latest deferred request to the next true close/release boundary.
This is pinned by an STA WPF dependency-property regression test using a fake Core
platform factory; no native media or window handle is required.

`BackgroundSurface` and `KaraokeSurface` live in the renderer-neutral WPF project and own
their sessions after the host supplies an `IMediaControlPlatformFactory`, matching the
existing WPF `Player` construction contract. The former is restricted to
color/image/silent looping video backgrounds; the latter is restricted to CDG and
MIDI/KAR on an opaque 4:3 canvas.

Managed frame sinks are registered through opaque, non-reused tokens rather than raw
managed object handles. A token remains resolvable until its owner detaches the surface
and disposes the token handle. Managed readback failures participate in the same
`D3D11.DeviceLost` recovery path as swap-chain failures. Primary backend recreation keeps
secondary HWND resources alive; they are suspended while the no-mixed-target managed
mode is active and resume when a native primary returns. Offscreen secondaries (Stage 027A
slice 5: a managed frame sink or shared textures per secondary surface, target ids
`offscreen-secondary:<id>`) are presented beside any primary, or without one.

## Runtime Flow

1. Host places a framework control, such as WPF `MediaSurface`.
2. Adapter creates or discovers a framework/native presentation surface.
3. Adapter passes a neutral `MediaPresentationSurfaceDescriptor` to Core.
4. Core's `MediaPresentationCoordinator` owns surface lifecycle, frame routing, pacing, retained-layer intent, and recovery policy.
5. The selected renderer backend executes Core's composition plan in background/base,
   wash, CDG, and subtitle draw order.
6. The renderer presents that frame to all active targets.

## Native Presentation Surfaces

`MediaPresentationSurfaceDescriptor` carries an immutable
`MediaNativeSurfaceDescriptor` rather than a closed platform enum or one raw pointer.
The native payload uses stable extensible surface/handle IDs and supports multiple
named handles for contracts such as Win32 HWND, X11 display/window, Wayland
display/surface, AppKit view, and Android native window.

Native handles are borrowed. The adapter/platform owns their lifetime; Core and the
renderer must not close, release, retain, or destroy them. They remain valid from a
successful attach until detach or replacement for the same surface ID. Core validates
neutral descriptor invariants, while the selected renderer validates the concrete
surface type it supports. Direct3D11 currently accepts `win32-hwnd` and consumes the
named `window` handle.

## Video/Audio Demux Ownership

- Main video decoding owns the Core-managed video demux cursor used for video seek, buffering, and proactive loop pre-seek.
- `VideoAudioEngine` opens an independent FFmpeg cursor for the same source. This preserves codec priming/preroll semantics; routing initial audio packets through the video cursor caused a persistent startup offset with AV1 + E-AC-3 until the first seek.
- Coordinated seek still repositions both cursors to the same target before video warm-up and audio resume.
- Seek-bar drags are bracketed by `BeginScrub`/`EndScrub` (035A): playback carries on, and `MediaControls` ignores a scrub seek that repeats the previous one (the release seek at the last drag position), and `SeekPump` (the latest-wins seek worker, 015A slice 2; CD+G shares it since ENG-013) settles an active scrub on the frame its last preview showed. A video `Play` that arrives while a seek is queued or settling is applied by the settle, which resumes from the target.
- Audio-backed gapless looping remains independent: video pre-seeks its cursor while `VideoAudioEngine` wraps its own decoder without stopping the BASS channel.

## Native Video Frames And Hardware Decode

- Core exposes producer-owned `NativeVideoDeviceResource` and `NativeVideoFrameResource` descriptors. Renderers may use the handles while Core retains the cloned FFmpeg frame, but never release or retain them independently.
- `IVideoHardwareAccelerationServices` supplies platform FFmpeg device preference, pixel-format preference, native device/frame extraction, and device-health checks.
- Core `VideoDecoder` owns codec selection, decode queues, cloned-frame lifetime, software transfer fallback, seeking, and recovery policy. It contains no D3D11/DXVA device constants or D3D11 FFmpeg-structure parsing.
- Windows currently supplies D3D11VA, D3D12VA, and DXVA2 preference; only D3D11 frames are exported for zero-copy presentation, while other hardware outputs transfer through Core's software frame path.

## MIDI Playback

- BASS optional plugins (bassmidi, bassflac, bassopus, etc.) are loaded via `Bass.PluginLoad` so BASS recognizes their formats automatically.
- `BassMidiSoundfontManager` resolves the default MIDI soundfont with this priority:
  1. `MediaPlayerConfiguration.MidiSoundfontPath` (user-configured; always wins if set and exists)
  2. Windows `gm.dls` (converted to SF2 via `DlsToSf2Converter`, cached in LocalAppData)
  3. System `.sf2` files (sgm.sb, etc.)
  4. Bundled `native/TimGM6mb.sf2` (5.7 MB compact GM soundfont; always present)
- DLS-to-SF2 conversion is handled by a self-contained `DlsToSf2Converter` that reads DLS RIFF chunks directly with `BinaryReader` and writes a minimal valid SF2 with `BinaryWriter`. No external DLS2/SF2 library or `EndianBinaryIO` dependency is required. The converted SF2 is cached in LocalAppData.
- The converter reads the DirectX DLS `INSTHEADER`, `RGNHEADER`, `WAVELINK`,
  `WAVESAMPLE`, and `WAVELOOP` layouts at their specified field widths and writes a
  32-bit RIFF payload size. A deterministic minimal DLS fixture pins bank/preset,
  key/velocity ranges, sample rate/root key, and the resulting SF2 container.
- MIDI files (`.mid`, `.midi`, `.kar`, `.rmi`) play through the existing `AudioEngine` path with pitch, tempo, normalization, volume, and loop support.
- Tracker formats (`.mod`, `.it`, `.s3m`, `.xm`, `.mo3`, `.mtm`, `.umx`) are natively supported by BASS via `BASS_MusicLoad`. `AudioStreamManager` loads tracker playback sources with decode + float + prescan flags so byte-position seeking works through the BASS_FX tempo stream, and releases their `HMUSIC` handles with `Bass.MusicFree`.

## MIDI/KAR Karaoke Lyrics

- Lyric markers (MIDI meta event 5) and text markers (meta event 1) are extracted via `BassMidi.StreamGetMarks` from the open BASS MIDI channel by `MidiLyricExtractor`.
- `MidiMetadataExtractor` (internal) reads Soft Karaoke `@T` (title) and `@I` (information) markers plus standard MIDI meta events (TrackName, Copyright, KeySignature, TimeSignature, Tempo). Produces `KaraokeMetadata` with filename+duration fallback when no embedded metadata exists.
- Supported formats: `.kar`, `.mid`, `.midi`, `.rmi`. Lyrics load for any of these when the file contains lyric or karaoke text markers.
- Soft Karaoke (`/` newline, `\` clear screen), Tune1000 (`\r` line feed, `\n` new paragraph), and Solton (`<` line prefix) display controls are honored.
- Syllable-level timing is preserved through the subtitle pipeline as `KaraokeCueTiming` metadata on `SubtitleCue`. The renderer uses absolute MIDI marker timestamps for wipe highlighting, independent of scroll animation timing.
- MIDI markers are shifted 400ms earlier so syllable wipe highlighting leads the corresponding note, giving the singer visual preparation similar to CDG timing.
- `ShowMidiLyrics` (default `true`) toggles MIDI lyric display at runtime without affecting ordinary SRT/ASS subtitles. Exposed on `IMediaControlSession` and `MediaControlPlayer`; the Player host always sets it to `true` (the former host checkbox was removed in Stage 030A).
- MIDI/KAR image/video sidecar rendering is supported, including archive KAR+background combinations.

### Karaoke Text Style

- `KaraokeTextStyle` (in `MediaPlayerConfiguration.KaraokeStyle`) provides independent font, color, outline, and shadow settings for karaoke lyrics, separate from `SubtitleTextStyle`.
- Base (unsung) and wipe (sung) colors, outlines, widths, and shadows are independently configurable.
- `FontSize` default is 72 (changed from 52).
- `LineTransitionMilliseconds` (default 1000ms) controls page/fade scheduling duration. Set to 0 to disable animation.
- `WipeTransitionMilliseconds` (default 600ms) controls per-syllable wipe transition duration, constrained by the gap to the next marker. The actual duration is the minimum of the configured value and 80% of the inter-marker gap. Gaps below 150ms snap to instant cut.
- `NextLineOpacity` (default 0.65) controls the opacity of upcoming lines.

### Karaoke Metadata Presentation

- `KaraokeMetadata` is attached to each `KaraokeCueTiming` and rendered as centered smoked-glass intro/end cards, or as split top/bottom bands when the first presentation occurs before 8 seconds.
- **Split metadata bands**: A translucent dark band at the top (~17% height) shows title and artist/year; a bottom band (~16% height) shows length, key, tempo, time signature, and copyright. Used when the first lyric or countdown presentation occurs within the first 8 seconds, keeping metadata visible without blocking the lyric area.
- **Centered card**: A smoked-glass rounded panel centered on screen showing title, artist, writer/lyricist credits, year, length, key, tempo, time signature, additional info, and copyright. Used when lyrics start later.
- Intro metadata begins fading in at 1 second and clears before the first lyric/countdown as timing permits. Fallback metadata (filename-derived) has no end card.
- End card appears after the final lyric fades out when embedded metadata exists and sufficient duration remains (minimum 3-second window after lyric fade). Renders centered card with track details.

### Countdown Arrows

- When the gap from the previous line's last syllable to the next line's first syllable exceeds 6 seconds, the upcoming line shows four countdown arrows above it.
- Arrows transition from red to green one per second. During the final second before the lyric, all four green arrows fade out.
- The four arrows use a separate smaller DirectWrite layout and are positioned above the lyric line. They are not lyric text.

### Karaoke Scrolling Behavior

- Up to five lines are displayed: two previous, active centered, two upcoming.
- Non-active lines fade based on distance from the active line using a Direct2D group-opacity layer. The layer applies opacity after text, wipe, outline, and shadow composition, preserving original RGB values while exposing the background through alpha.
- Non-active lines also scale by distance from center: active line at 1.0, immediately adjacent at 0.8, and outer lines at 0.6. Scaling interpolates smoothly during scroll transitions.
- Normal scroll duration is always 1500ms. When the gap to the next line's first syllable or countdown is sufficient, the scroll ends exactly at that presentation boundary. When the gap is tight, the full 1500ms scroll window is centered over the available gap, allowing overlap with outgoing highlighting and incoming presentation. Position and opacity use SmoothStep easing throughout the scroll.
- Page changes (clear-screen / new-paragraph transitions): the outgoing page fades out and the incoming page fades in. Each fade takes up to 1 second with cubic easing (out: cubic ease-in; in: cubic ease-in-out). When room allows, the outgoing page holds (up to 2s) then fades out before the incoming page fades in (sequential). When the gap is tight, outgoing fade-out and incoming fade-in crossfade, including sub-second gaps. The incoming page may also apply a zoom effect (1.5x scaling at start shrinking to 1.0x).
- The first page fade-in completes by the time the first lyric is ready (allowing for countdown lead or a 1-second preview window). Fade-in starts at most 1 second before that completion time. With a very short lead-in, fading starts at playback time zero.
- Syllable wipe highlighting uses absolute MIDI marker timestamps (shifted 400ms earlier). Active syllable wipe transitions default to 600ms and are constrained by the inter-marker gap — the actual duration is min(configured, 80% gap-to-next). Wipe rendering uses a two-pass approach: fully-highlighted preceding syllables at full wipe color, plus a transitioning region that interpolates directly from base to wipe color per-channel, avoiding alpha-compositing artifacts.
- End-lyric fade: lyrics begin fading 5 seconds after the last syllable, or during the final second when the media ends sooner, and complete over at most 1 second. After lyric fade, the end metadata card appears when at least a 3-second card window remains.

### Karaoke Rendering

- Karaoke text is rasterized into a cropped bitmap covering only the visible text area, not a full 1920x1080 buffer. When metadata cards or split bands are active, a full-canvas bitmap is used instead. Crop position within the logical canvas travels as composition metadata.
- The D3D11 compositor maps the cropped texture through `ViewportGeometry.MapCanvasRegionToViewport`, preserving 16:9 aspect ratio at any window size.
- The subtitle presentation loop runs at 75 Hz while progress revisions are quantized at 60 Hz. It only presents when subtitle text, wipe progress, scroll position, fade state, metadata opacity, countdown state, or end-lyric fade actually changes; no-op frames are skipped.
- Backward seeks immediately recalculate active cue, syllable highlighting/wipe, scroll position, page fade/scale state, metadata opacity, countdown state, and end-lyric fade state.
- Progress revision hashing now includes syllable wipe fraction, countdown revision, intro/end metadata opacity, end-lyric opacity, media-end state, and lyric-fade-end state to ensure no stale frame is skipped.

## Display Refresh Rate

Video presentation timing resolves the display rate in this order (Stage 017A): the host's
`SetDisplayRefreshRate`, then the renderer's `IMediaRendererBackend.GetSurfaceRefreshRateHz`
for the primary surface (Direct3D11 reads the window's monitor from `QueryDisplayConfig`,
cached and refreshed off the presentation thread), then the platform's
`IVideoPipelinePlatformServices` rate (DWM's global composition rate on Windows, a
configured rate on Linux), then 60 Hz. `MediaPresentationCoordinator` hands the video
pipeline the primary surface's rate through the internal `IRendererRefreshRateSink`.

## Device-Lost Recovery

The Direct3D11 renderer and Core video pipeline detect and recover their independently owned D3D11 devices (Stages 005A/005B/005C); adapters are never involved.

- The Direct3D11 backend classifies presentation-device removal/reset conditions. Windows hardware-decode services independently query the FFmpeg-owned D3D11 device.
- Two independent recovery orchestrators match the two independently owned devices: Core `MediaPresentationCoordinator` drives backend teardown/rebuild through `IMediaRendererBackend`; Core `VideoPipeline` performs runtime software-decode fallback with background hardware re-probe/restore while audio and CDG keep running.
- Diagnostic categories follow a fixed naming convention: `"D3D11.DeviceLost"` (presentation) and `"Decoder.DeviceLost"` (decode), raised at Error on loss and Info on successful recovery, with a `.RecoveryFailed` suffix for the terminal diagnostic after bounded retries are exhausted.
- Both recovery paths reuse existing rebuild machinery (the proven surface-recreate and seek stop-reposition-restart sequences) — no new swap-chain, compositor, or renderer abstraction was introduced.

## Media-Loop Pacing (Stage 016B)

Core owns all media-loop deadline management through `MediaLoopPacer` in `Core.Threading`:

- **Fixed-cadence pacing** (`WaitNext`) — accumulates a fixed interval after each prior deadline; the remaining wait shrinks by the actual work time spent, so a loop that finishes early waits longer and vice versa.
- **Media-clock-derived pacing** (`WaitFor`) — waits a relative duration and resets the fixed-cadence accumulator afterward, for intervals that change every iteration (e.g. source-frame video cadence).
- **Lateness resynchronization** — when work has already passed the deadline, the cadence resets to `now` instead of issuing catch-up ticks that would cascade into unnecessary frame drops.
- **Optional late yield** — a short `lateYield` parameter prevents a persistently late loop from spinning; the platform waiter still respects cancellation.

Platform packages supply only the cancellable native wait primitive through `IPrecisionWaiter` / `IPrecisionWaiterFactory`:

- `WindowsPrecisionWaiter` — Windows high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803+), transparent fallback to `WaitHandle.WaitOne`.
- `SystemPrecisionWaiter` — portable fallback using `WaitHandle.WaitOne`.

All three presentation/render loops use `MediaLoopPacer`:
- Primary video presentation loop (`VideoPipeline`)
- Background video presentation loop (`PipelineMediaEngine`)
- Core CDG/subtitle presentation loop (`MediaPresentationCoordinator`)

Tests are deterministic via `TimeProvider` injection with a `ManualTimeProvider` that advances on each wait.

## Audio Device Idle Recovery

- `AudioEngine.Play()` reasserts the BASS device context (`BassAudioOutputDevices.TrySetCurrentDevice`) before calling `ChannelPlay`.
- If `ChannelPlay` fails after extended idle (e.g. media ended hours ago, Windows released the audio device), the engine attempts a full BASS reinitialization and retries the play. A warning diagnostic is raised on the initial failure.
- This avoids permanent audio stall without requiring the user to restart the player or reload media.

## Audio Output Latency Feedback (Stage 006A)

- `IOutputLatencyProvider` interface: `GetOutputLatencySeconds()` returns the BASS-reported output latency for the current device.
- `BassOutputLatencyProvider`: reads `MBass.GetInfo().Latency`; opt-in heuristic adds conservative per-class offsets for USB DAC and Bluetooth endpoints via `MediaPlayerConfiguration.EnableUsbBluetoothLatencyHeuristic`.
- Both `AudioEngine` and `VideoAudioEngine` apply output latency at position-read time: `Position` getter, snapshot publish, and seek-position publish all subtract the latency so `AudioPresentationClock` and hosts see corrected time.

## Audio Device Hotplug Monitoring (Stage 006B)

- `IAudioDeviceMonitor` / `PollingAudioDeviceMonitor`: 1-second poll of BASS state and Windows default endpoint via `CoreAudioDefaultEndpointReader`.
- `AudioDeviceChanged` event on `IMediaControlSession` with `AudioDeviceChangedEventArgs`.
- `BASS_CONFIG_DEV_NONSTOP` enabled at BASS init to keep the device buffer alive during transient loss.
- Policy is **diagnostic-only**: no `Stop`, `Close`, `Load`, `Seek`, or `Play` is triggered by device-change notifications. BASS/Windows handle active-stream migration; Core only raises diagnostics and the public event.
- Lost BASS output (ENG-032): `BassOutputWatchdog` (Core, BASS only, platform neutral) is one process-wide 1-second timer that runs while any `AudioEngine`/`VideoAudioEngine` is playing. When BASS stops the output or parks a playing channel as `BASS_ACTIVE_PAUSED_DEVICE` (another application holding the device exclusively, a USB device unplugged), it raises one unowned `AudioOutput` warning and `AudioDeviceChanged` (`DeviceRemoved`) on every player, retries `BASS_Start` / `BASS_Init(BASS_DEVICE_REINIT)` once a second per device, resumes only the channels whose engine is still `Playing`, then raises an Info and `AudioDeviceChanged` (`DeviceAdded`). The decisions live in the pure `AudioOutputRecoveryTracker`. A `Play` whose `BASS_ChannelPlay` fails restarts the output once (same rate limit) and reports an Error naming the unavailable output if it still fails.

## Audio Output Device Selection (Stage 006C)

- `AudioOutputDeviceInfo` is a Core public record; `AudioOutputDevices.Enumerate(...)` lives in the Windows package and enumerates BASS output devices through Windows composition.
- `MediaPlayerConfiguration.AudioOutputDeviceIndex`: BASS device index, `-1` = Primary (system default). Creation-time choice; runtime device changes require a new player or `Reset`.
- `MediaPlayerConfiguration.AudioOutputFallbackMode`: `FallbackToPrimary` (default) or `NoFallback`.
- If an explicit device is unavailable at init and fallback is enabled, the engine retries on Primary.
- Both `AudioEngine` and `VideoAudioEngine` resolve and select the configured BASS device before stream creation and latency queries.

## Per-Load Effect Reset

`MediaPlayerConfiguration.ResetPitchOnMediaLoad` and
`ResetTempoOnMediaLoad` independently opt into returning pitch to `0` and tempo to
`100` after each successful load. Both default to `false`. Resets run after the new
active audio/clock engine is refreshed and before `MediaLoaded` or autoplay;
`PitchChanged` / `TempoChanged` carry `MediaChangeReason.LoadReset` only when the
value actually changes. Normal setter changes carry `MediaChangeReason.User`.

`MediaChangeReason` (`User`/`LoadReset`) rides on `MediaValueChangedEventArgs<T>`,
which gained an additive `Reason` property and a three-argument constructor
overload; the existing two-argument constructor is preserved, so both source and
binary compatibility hold for existing construction sites. The
`MediaPlayerConfiguration` record struct gains two `init` properties
(source-compatible; binary/ABI-relevant pre-1.0 because the struct layout grows).

Stage 030A (2026-08-10) additionally exposed both options as runtime
`IMediaControlSession` properties — `MediaControlPlayer.LoadBehavior.cs` (new
partial) forwards them to mutable `MediaFile.ResetPitchOnMediaLoad` /
`ResetTempoOnMediaLoad` options applied by `MediaFile.LoadCore` at each load —
so hosts can toggle the resets live; the config flags seed the initial values.

Implemented and closed 2026-08-08 (Release 0 warnings/errors, 397 tests, strict
review no blockers; manual Player reset smoke passed with both startup flags —
Core pitch/tempo reset to 0/100 and both WPF sliders visibly synchronized). See
`roadmap/complete/029B_COMPLETED_LoadResetPitchTempoOptions.md`.

## Control And Completion Serialization

Public load, play, pause, stop, seek, close, and reset operations are admitted through
one `MediaControlOperationQueue`. Natural-end handling uses that same queue rather
than restarting the engine directly from a BASS/pipeline callback: end detection is
queued first, host `MediaEnded` delivery is ordered next, and loop completion is
queued afterward. `MediaControls` carries a transport-generation token so a Play,
Pause, Stop, or Seek that wins the race invalidates stale end work before it can
restart playback. This preserves the Stage 029A `MediaEnded` → `MediaLooped` ordering
while allowing a host's `MediaEnded` handler to cancel a pending loop with Stop.

`Dispose` shares the same lifecycle admission gate: it waits behind admitted work,
runs teardown in an exclusive queue turn, drains terminal event deliveries, closes the
old queue, and then publishes disposed state. Synchronous disposal from inside a
serialized control callback is rejected because it cannot safely wait for its own
active queue turn.

ENG-024 (2026-10-24) narrowed the admission gate to taking a place in the queue: a
synchronous call takes its exclusive turn under the gate and waits for it outside, so
the `Async` methods never block behind a long synchronous call, `Reset` or `Dispose`.
`Reset` and `Dispose` are serialized by their own lifecycle lock, cancel loads (a
player-lifetime `CancellationTokenSource`, which `MediaOpenCancellation` carries to the
engine's open so network retry delays end at once), and wait for their turn with a
bound: `Dispose` 2 s, then it releases the surfaces and leaves the teardown to the
queue; `Reset` 5 s, then it returns `false` with nothing changed. "Inside an operation"
is a per-operation scope (bound to the worker thread for synchronous operations) that
ends when the operation returns, and every host event is raised through
`HostEventDispatcher` in a handler scope bound to its thread, so work a handler hands
elsewhere is serialized like any other caller.

Video/CDG seeks retain latest-wins settle signaling. After a successful seek, the
player also checks the current engine position through near-exact equality; an
already-settled/no-op seek therefore raises `SeekCompleted` without requiring a new
decoder/CDG event. Real moves still complete from `IVideoEngine.PositionChanged` or
`ICdgEngine.FrameInvalidated` through the wider 250 ms settle tolerance. Separating
the two tolerances prevents a small asynchronous video seek from completing against
its stale pre-seek position. Stop, natural end, close, reset, and failed seek clear
pending completion state so stale signals cannot complete an obsolete request.

## WPF Surface AutoHide (Stage 029C)

Closed 2026-08-08 — Release 0 warnings/errors, 406 tests, format/Core/WPF/parity
gates, Core and WPF nupkg/snupkg packs, WPF specialist and final diff review no
blockers, and the manual Player AutoHide smoke passed (visual↔audio-only,
video/CDG replacement, default behavior, background/transparency semantics,
public `IsContentVisible`/`ContentVisibilityChanged` behavior). See
`roadmap/complete/029C_COMPLETED_EventDrivenWpfAutoHide.md`.

Stage 029C gives the Stage 028A WPF surfaces an event-driven visibility policy
decided by the Stage 029A session lifecycle plus `PresentationFramePresented` as
the positive proof of visual output — never file extensions or content flags:

- Additive Core capability: `IMediaControlSession.HasVisualContent` — true when
  the loaded media's content role includes a visual main (`Video`, `Cdg`,
  `Image`, or `Subtitle`) or when a resolved composition background is present
  (`MediaControlPlayer`: content flags OR `_mediaEngine.Background != null`).
  It arms hidden targets so presentation can restart; it never makes a show
  decision by itself.
- `MediaSurface.AutoHide` dependency property, default `true` (standard media-player behavior).
  It governs only the internal native/composited presentation host and never writes
  the outer control's `Visibility`, background, or transparency, keeping the control
  laid out so new frames can
  still arrive while content is hidden. When `false`, current behavior is preserved
  exactly. `BackgroundSurface` / `KaraokeSurface`
  forward the same `AutoHide`, `IsContentVisible`, and `ContentVisibilityChanged`
  surface for their inner media layer; their solid-color / canvas fallbacks are
  independent of inner-media AutoHide.
- Internal pure `SurfaceAutoHidePolicy` (`Idle → PendingShow → Shown → Hidden`)
  in `FatzBomb.MediaControl.Wpf/SurfaceAutoHidePolicy.cs`, consumed by
  `MediaSurface` (the `MediaSurface.AutoHide.cs` partial) with marshaled,
  coalesced dispatcher transitions. Load only arms `PendingShow`; the surface
  shows on the first presented frame. Explicit stop/close/failure hide; pause
  stays shown; non-loop natural end hides; looping stays shown.
- **Pre-`MediaLoaded` frame latch/replay (static images)** — the present fires
  from Core's native present/render thread and `MediaLoaded` from the control
  pipeline, marshaled separately, and a static image may present exactly one
  frame. The adapter latches "a frame was presented" in an interlocked flag and,
  when the queued `MediaLoaded` transition re-arms, replays that proof to go
  straight to `Shown` without waiting for another present. The latch is cleared
  on session change, hide transitions, and non-loop natural end, so it cannot
  resurrect a stale show.
- **Replacement contract (corrected)** — WPF-only signals cannot distinguish an
  explicit `Stop` from the internal stop a replacement performs (both raise
  `MediaStopped`), so a transient hide during a slow replacement is allowed. The
  guarantees are only that the newest `MediaLoaded` re-arms, the first presented
  frame of the new content shows, and the surface is never stuck hidden. No
  no-flicker guarantee is made. A session-generation check plus the
  `HasVisualContent` snapshot filter stale replacement frames: a present from a
  replaced item (visual-capability flag 0 or old generation) cannot reveal a new
  audio-only session.
- The Wpf project exposes the internal policy via
  `InternalsVisibleTo("FatzBomb.MediaControl.Tests")`; the Tests project added a
  Wpf reference and `SurfaceAutoHidePolicyTests` covers the policy
  deterministically. The `scripts/check-core-boundary.ps1` allow-list was updated
  for the new references. The Player host enables AutoHide by default and exposes
  `--no-auto-hide` for validation of the manual-control opt-out.

## Base-Visual Transform Pipeline (Stage 030A)

Implemented 2026-08-09; contract revised 2026-08-10 (independent per-channel
RGB gains replace the former tint and color-wash members; `Lightness` removed)
and 2026-08-11 (fixed order changed to hue → saturation → TV-style contrast
gain → brightness offset → per-channel RGB gains; `IncludeOverlays`
whole-composition mode added; Player host reworked to persistent chrome plus
one-at-a-time Sync/Video/Audio/A-V flyouts; MIDI lyrics checkbox removed with
`ShowMidiLyrics = true` always). Stage 030A fixes landed 2026-08-11 (second
pass): the DirectComposition split-path cached base state now carries
`BaseVisualTransform` (root-cause fix for static images — see below), and Loop
moved from the A/V flyout to the persistent transport strip as a stateful icon
button. Closed 2026-08-17 after automated validation (Release 0
warnings/errors, 438 tests, gates, packs, focused final review PASS) and
user-confirmed manual static-image Include Overlays off/on, loop icon, flyout,
and both-renderer smoke. The completed specification lives at
`roadmap/complete/030A_COMPLETED_VisualTransforms.md`; the fixed operation order and
pinned coefficients below are the exact reference implemented by every path.

`IMediaControlSession` (and `MediaControlPlayer`) exposes
`BaseVisualTransform { get; set; }` plus a change-only `BaseVisualTransformChanged`
event carrying previous/current `MediaValueChangedEventArgs<BaseVisualTransform>`
(raised through `HostEventDispatcher`). `BaseVisualTransform` is a public
immutable readonly record struct in Core (`Api/BaseVisualTransform.cs`):
`RedGain` / `GreenGain` / `BlueGain` (each [0,2], default `1`),
`HueDegrees` / `Brightness` (default `0`), `Contrast` / `Saturation`
(default `1`), and `IncludeOverlays` (default `false`). There is **no
`Lightness` member**, no tint member, and no color-wash member or preset type.
Init accessors clamp to the pinned ranges and treat NaN as the identity
default, so `default`/`new()` is always `BaseVisualTransform.Identity`
(`IsIdentity == true`); gains at exactly `1` with neutral
hue/brightness/contrast/saturation are identity, and `IncludeOverlays = true`
with all-neutral color math remains identity (byte-identical output).

**Ownership and flow** — Core owns the model, the exact per-stage math
(`Core.Graphics.Composition.BaseVisualTransformMath`, internal), and the
composition-plan carry. The session property delegates to
`MediaPresentationCoordinator.BaseVisualTransform` (lock-protected,
change-only); a change calls `PresentCurrentComposition("base visual transform
changed")`, which re-renders the current composition **without re-uploading
source pixels**. The transform rides on the Layer 0 `Background` layer
descriptor (`MediaCompositionFrame.BaseVisualTransform` /
`MediaCompositionLayer.BaseVisualTransform`) and reaches both renderers through
the same public composition contract — no renderer internals leak into Core.

**Default scope (Layer 0 only)** — with `IncludeOverlays = false` the transform
applies exclusively to the base visual (main video/image and background
video/image on the composition `Background` layer). Clear, wash, CDG overlay,
subtitle, and karaoke text are never transformed, and no extra composition pass
runs: the software renderer engages the transform only for the background/base
draw and passes `BaseVisualTransform.Identity` on the subtitle draw; the D3D11
subtitle pass keeps the legacy `_backgroundPixelShader`.

**Whole-composition mode (`IncludeOverlays = true` + non-identity)** — the same
math is applied to the completed composition as a whole — wash, CDG overlay,
subtitle overlay, and any other overlays included — through a final postprocess
pass. The transform is **moved** off Layer 0 for that frame: both renderers draw
the base visual with identity and apply the transform exactly once to the final
buffer, never double-applying it. `IncludeOverlays = true` with all-neutral
color math stays byte-identical (identity bypass, no extra pass).

**Software exact reference** — `FatzBomb.MediaControl.Rendering.Software`'s
`SoftwareBaseVisualTransform` mirrors the Core reference math exactly
(unpremultiply to straight sRGB → YIQ hue rotation → Rec.709 saturation →
TV-style contrast gain (`rgb *= Contrast`, black stays black) → brightness
offset (`rgb += Brightness`) → independent per-channel RGB gain →
re-premultiply → round-to-nearest-away-from-zero BGRA8) and is applied only when
the transform is non-identity, so identity output is byte-identical. In
whole-composition mode the renderer skips the per-layer transform and runs
`ApplyFinalTransform` over the completed premultiplied buffer
(`SoftwareBaseVisualTransform.ApplyPremultipliedToPremultiplied`).
`BaseVisualTransformTests.SoftwareMathMatchesCoreReference` and
`SoftwarePremultipliedMathMatchesCoreReference` cross-check the software paths
against the Core reference, and the renderer fixtures pin byte-exact outputs
(`Render_IdentityTransform_IsByteIdentical`,
`Render_TransformDoesNotReachSubtitle`, per-stage fixtures, combined order,
`Render_IncludeOverlaysTransformsCompletedComposition`,
`Render_IncludeOverlaysDoesNotDoubleApplyBackgroundTransform`,
`Render_IncludeOverlaysWithNeutralMathIsByteIdentical`).

**Per-channel RGB gain math** (final stage, per pixel, after all prior stages) —
`R' = clamp(R * RedGain)`, `G' = clamp(G * GreenGain)`,
`B' = clamp(B * BlueGain)`. Gain `0` removes the channel entirely
(`GreenGain=0` + `BlueGain=0` leaves red-only; `RedGain=0` leaves cyan/aqua;
all three `0` is black), gain `1` is neutral, and gain `2` doubles the channel
and clips at 1.

**Direct3D11** — two dedicated pixel shaders implement the same equations
(constant buffer fields `TransformAdjustments` / `TransformBlend` /
`TransformRgbGains`): `PSBaseVisual` (`_baseVisualPixelShader`), bound only when
the layer transform is non-identity in per-layer mode, and
`PSBaseVisualPremultiplied` (`_baseVisualPostPixelShader`), the whole-composition
overwrite pass. Identity backgrounds and the subtitle layer keep `PSBackground`
(`_backgroundPixelShader`), so the identity path is unchanged. In
whole-composition mode the presenter composites the full frame — background
drawn with identity — into a per-target cached offscreen B8G8R8A8 render target
(`_postProcessTargets`, keyed by target ID, recreated on resize, disposed on
reset), then runs `PSBaseVisualPremultiplied` as a no-blend overwrite onto the
final target; the DirectComposition overlay split is disabled in this mode
(`D3D11RendererPresentationServices.CanUseDirectCompositionOverlaySplit`). A
transform change marks `MediaCompositionLayerDirtyFlags.TransformChanged |
RenderRequired` (never `UploadRequired`) in `D3D11CompositionLayerState` —
render-without-upload (covered by `D3D11CompositionLayerStateTests`).

**Split-path base state carries the transform (root-cause fix, 2026-08-11)** —
the DirectComposition overlay split path (`IncludeOverlays = false`) re-presents
its static base only when the cached `DirectCompositionOverlaySplitBaseState`
changes. That state originally omitted `BaseVisualTransform`, so transform
changes were skipped for static images/backgrounds (which present a single
frame), while video — whose frame revisions changed every frame — masked the
bug. The record struct and `CreateDirectCompositionOverlaySplitBaseState` now
include `frame.BaseVisualTransform`, so any transform change invalidates the
cached base state and forces a re-present; pinned by
`D3D11RendererPresentationServicesTests.DirectCompositionBaseStateChangesWhenStaticImageTransformChanges`
(two base states built from the same frame/target differ when only the
transform changes).

**Hardware primary routing** — in `MediaPresentationCoordinator.PresentPrimaryVideoFrame`,
a hardware frame with a non-identity transform routes through
`RenderHardwareVideoCompositionFrame` (composition/shader path) instead of the
direct zero-copy `PresentNativeFrame`; identity keeps the direct native path.
The route is chosen from the transform state at frame-present time.

**Player host UI** — the host keeps a persistent bottom chrome (timeline/seek,
transport Open/Play/Pause/Stop, volume, fullscreen, status strip) and exposes
four **one-at-a-time** flyouts via toggle buttons (Sync, Video, Audio, A/V;
`ActiveFlyout`, active button highlighted). Flyout content is extracted to
`Controls/PlayerFlyoutPanel.xaml`. The **Video flyout** drives the pipeline
live: HUE (−180°…180°), SATURATION (0–2), BRIGHTNESS (−0.25–0.25), CONTRAST
(0.5–1.5), and RED GAIN / GREEN GAIN / BLUE GAIN (0–2) sliders (no Lightness, no
color wash, no preset type) plus an **Include Overlays** checkbox
(`BaseVisualTransform.IncludeOverlays`) assemble a `BaseVisualTransform`
straight into the session property (change-only, render-without-upload), and a
Reset Visual command restores identity (gains `1/1/1`, hue/brightness `0`,
contrast/saturation `1`, Include Overlays unchecked). The **Audio flyout** has
pitch/tempo/normalize and runtime Reset Pitch on Load / Reset Tempo on Load
checkboxes backed by additive `IMediaControlSession.ResetPitchOnMediaLoad` /
`ResetTempoOnMediaLoad` properties (`MediaControlPlayer.LoadBehavior.cs` partial
→ mutable `MediaFile` options applied per load), so the Stage 029B config flags
are now also live session toggles. The **A/V flyout** carries Auto Play, Auto
Hide (Stage 029C runtime toggle), the secondary/projector window toggle, video
display mode, presentation mode, and CDG background opacity. **Loop Playback**
lives on the persistent transport strip as a stateful icon button
(`ToggleChromeButtonStyle`, accent highlight when `LoopPlayback` is on,
`ToggleLoopCommand`); the duplicate A/V flyout Loop checkbox was removed
(2026-08-11). The **Sync flyout** keeps the per-item A/V/CDG/subtitle 100ms
offset buttons and reset. Every control works without startup flags; the flags
(`--visual-transform-demo`, `--reset-pitch-on-load`, `--reset-tempo-on-load`,
`--no-auto-hide`) initialize their corresponding controls for compatibility.
`--visual-transform-demo` seeds the demo values (`RedGain=0.85`,
`GreenGain=0.95`, `BlueGain=1.2`, `HueDegrees=30`, `Brightness=0.05`,
`Contrast=1.15`, `Saturation=1.2`; no lightness, Include Overlays off) into the
controls, which then flow through the same live path. The host sliders expose
practical ranges — brightness −0.25…0.25, contrast 0.5…1.5 — while the public
model retains −1…1 and 0…2. **MIDI lyrics** — the host always sets
`ShowMidiLyrics = true`; the former MIDI lyrics checkbox was removed. The status
line reports `Visual transform=IDENTITY|ACTIVE`, `AutoHide=`, and
`Load resets: Pitch=…, Tempo=…` alongside renderer/presentation. Validation
tooling only, not production UI.

## Silence Trimming Analysis Cache (Stage 030B, completed)

Stage 030B is completed (2026-10-16): the contract/cache-foundation, analyzer-scan, and
plain-audio transport-application slices are implemented and owner-confirmed. The public API adds
immutable `SilenceTrimOptions`
(disabled by default: `Enabled = false`; pinned defaults threshold `-50 dBFS`,
minimum qualifying edge silence `0.5 s`, retained edge padding `0.1 s`, independent
`TrimLeading` / `TrimTrailing`), `SilenceRegion` (source-time start/end seconds),
and `MediaAnalysisResult` (source duration, playable start/end with computed
playable duration, effective duration, detected silence regions, normalization
gain). `MediaPlayerConfiguration.SilenceTrim` defaults to the disabled record
value, so current duration, seek, loop, normalization, startup, and end behavior
are unchanged.

Internal `Core/Audio` members implement the pure edge-trim math
(`SilenceTrimCalculator`), result validation (`MediaAnalysisResultValidator`), a
versioned local-file cache key (`MediaAnalysisCacheKey` — normalized full path,
file length, UTC last-write ticks, and normalized trim options; creation fails
closed on missing file or invalid input), and a process-local, thread-safe,
single-analyzer-version, 256-entry bounded LRU `MediaAnalysisCache`
(`System.Threading.Lock`, usage-ordered eviction) that treats version mismatches
and invalid records as misses. The shared process-lifetime instance
(`MediaAnalysisCache.Shared`) is registered as a singleton into every Core DI
container (`MediaControlServiceProvider`) and therefore survives player `Reset`.
The pinned contract lives at
`roadmap/complete/030B_COMPLETED_SilenceTrimmingAnalysisCache.md`.

**Analyzer slice (2026-08-18)** — for enabled plain local non-tracker audio, a
cancellable sequential 0.1-second silence scan (`AudioSilenceAnalyzer`) runs inside
the existing background normalization-analysis task, sampling levels at actual BASS
decode byte positions. The task captures the decode cursor up front and restores it
in a `finally`; the existing `CancelNormalizationAnalysis` synchronously joins the
previous task before any handle reuse or free, and partial/faulted/stale scans are
never cached. The existing normalization scan, gain math, and gain cache are
preserved: a normalization-gain cache hit skips peak rescans but still performs a
missing silence scan. Results are validated through `SilenceTrimCalculator`
(including normalization gain) and stored in the shared `MediaAnalysisCache`; a
cache hit applies the stored gain and skips rescanning entirely. CDG-paired opens
explicitly disable silence analysis (`OpenAudioCdg` passes `analyzeSilence: false`),
and video, archives, tracker/MOD formats, and live/stream inputs remain excluded.
Transport/duration/seek/loop/end trimming is still NOT implemented, so playback
behavior is unchanged when the scan is enabled.

**Transport slice (2026-08-18)** — trim is applied to transport only when a
validated `MediaAnalysisResult` is already a cache hit synchronously during a local
plain non-tracker audio `Open`; a late background scan only warms the cache for a
subsequent open and never tightens duration during active/current playback. The
internal `IAudioTrimWindowProvider` (on `IAudioEngine`) exposes the active window
(`MediaAnalysisResult? ActiveTrimWindow`) to `AudioPipeline`, which maps
source/effective coordinates: effective duration, effective position
(`SourceToEffective`), effective→source seek (`EffectiveToSource`), and
progress/end publication in effective space. Stop/replay and pause/resume behave
consistently, and a zero playable duration makes `Play` return `false`.
`AudioEngine.Play` positions at `PlayableStart` when the source position is outside
the playable window, suppresses the native source-end loop while a trim window is
active, attaches a BASS `Position | Mixtime | Onetime` sync at `PlayableEnd`
(official BASS custom-loop guidance), stops at the custom end, and custom loops
seek to `PlayableStart` with the `BASS_POS_FLUSH` raw flag `0x1000000`
(ManagedBass omits the enum member; `AudioStreamManager.FlushPositionFlag`).
Tempo changes alter wall-clock arrival of the position sync but do not require
rearming it. CDG-paired opens pass `analyzeSilence: false`; video, archives,
tracker/MOD formats, and live/stream inputs remain excluded; disabled/default
behavior remains identity. The first uncached `Open` only warms the cache — a
Stage 031A's prepared-next analysis supplies first-play cache.

## Prepared-Next Slot (Stage 031A, implemented — pending manual smoke)

Core owns a depth-one prepared-next slot; hosts own playlists and queues.
`IMediaControlSession` exposes `PreparedNext` (immutable `PreparedNextInfo` snapshot),
`AnalyzeAsync`, `PrepareNextAsync`, `ClearPreparedNext`,
`TransitionToPreparedNextAsync`, `AutoTransitionToPreparedNext` (default off), and the
`PreparedNextChanged` / `PreparedNextTransitioned` events. The internal
`Core/Prepared/PreparedNextSlot` state machine (Empty → Analyzing → Ready →
Transitioning → Empty; Ready → Stale when the prepared file's path/length/last-write
identity changes) guards every completion with a generation so cleared or replaced
items never resurrect. Analysis goes through the internal `IMediaFileAnalyzer`, which
the BASS `AudioEngine` implements on its own decode stream (never the current media's
handles) and which warms the shared 030B `MediaAnalysisCache` and normalization-gain
cache, so the later transition load hits the cache instead of rescanning. Only local
plain non-tracker audio is analyzed; other sources reach Ready with no analysis.
Transitions run on the control queue (load, then play); automatic transitions are
queued behind the host `MediaEnded` delivery and Core end completion and are skipped
when the host intervened (transport generation, load generation, slot generation,
loop, or auto flag changed). `Reset` and `Dispose` clear the slot. See
`roadmap/complete/031A_COMPLETED_PreparedNextPlaylistSlot.md`.

## Audio Fades (Stage 031B, implemented — pending manual smoke)

Public `AudioFadePoint`, immutable sorted `AudioFadeEnvelope` (exact-position upsert,
linear interpolation, `AudibleStart` for the leading zero plateau), and
`AudioCrossfadeOptions` (shared duration, disabled by default). Internal
`Core/Audio/Fade/AudioFadeSchedule` turns an envelope into chained linear ramps and
`AudioFadeRunner` applies them (latest wins) to an `IAudioFadeGainTarget`. The BASS
`AudioEngine` implements the target with a dedicated `BASS_FX_VOLUME` stage, so final
gain = envelope x channel volume attribute (user volume) x normalization gain FX.
Fade sessions end on Open/Close/Stop (Stop restores unity). `MediaControlPlayer`
exposes `FadeOutAsync`, `FadeInAsync`, `CancelFade`, and `PreparedNextTransitionFade`,
which turns 031A transitions into fade-through transitions; waits run off the control
queue. Overlapping two-handle crossfades are deferred. See
`roadmap/complete/031B_COMPLETED_AudioFadeCrossfadeModel.md`.

## Network Sources (Stage 014A, implemented — pending manual smoke)

`MediaInputSource.FromUri` creates `NetworkUri` sources (http/https only, optional
`MediaNetworkContent` hint). Behavior branches on `MediaSourceCapabilities` rows from
the public `MediaSourceCapabilityMatrix` (LocalFile, Archive, InMemoryImage,
NetworkFile, NetworkLive), never on URL text. `PipelineMediaEngine` implements internal
`IMediaSourceCapabilityProvider`; `MediaSourceInfo.Capabilities` and
`MediaControlPlayer.CurrentSourceCapabilities` expose the row. HTTP audio opens through
the BASS `AudioEngine`'s internal `INetworkAudioSource` (`BASS_StreamCreateURL` into the
normal tempo/gain/fade chain, bounded `NetworkRetryPolicy`, probe -> row, buffering
monitor and ICY/Ogg metadata syncs) and raises `BufferingChanged` /
`StreamMetadataChanged` through the control queue. Only buffered rows gate seeking.
Network sources skip sidecars, silence trim, normalization pre-analysis, and
prepared-next analysis. Network video is deferred (file-path demuxer). See
`roadmap/complete/014A_COMPLETED_StreamingNetworkPlayback.md`.

## Linux Platform Services (Stage 026B, implemented — pending BASS/player smoke)

`FatzBomb.MediaControl.Linux` (net10.0, references Core only) supplies
`LinuxMediaControlPlatformFactory` and the `IMediaControlPlatformServices`
registrations: host-supplied BASS/FFmpeg `.so` loading (dlopen by absolute path, a
ManagedBass `DllImport` resolver, `BASS_PluginLoad` add-ons, FFmpeg preloaded in
dependency order), a kernel-wait-plus-spin precision waiter, host-configured refresh
timing, a BASS-snapshot device monitor using Core's `AudioDeviceChangeDetector`, the
Core MIDI lyric extractor, software-only hardware-decode services, and an FFmpeg
software capability scan. Renderer choice stays with the host (software renderer
today). Sidecar extension matching in Core is case-insensitive on case-sensitive file
systems. See `roadmap/active/026B_ACTIVE_CrossPlatformPlatformServices.md`.

## Vulkan Renderer Preview (Stage 026A, slice 1)

`FatzBomb.MediaControl.Rendering.Vulkan` (net10.0, Core only, Silk.NET.Vulkan) offers
`VulkanPresentationCoordinatorFactory` beside the software and Direct3D11 factories. Slice
1 renders to a primary managed frame-sink surface: GPU clear, nearest-filter background
blit using the software renderer's placement rules, and readback. Overlay, transform,
native-resource, and window-swapchain frames are rejected with a diagnostic until later
slices. A lavapipe conformance suite compares output with `SoftwareCompositionRenderer`.
See `roadmap/active/026A_ACTIVE_CrossPlatformRendererBackends.md`.

## Avalonia Adapter (Stage 027A, slice 1)

`FatzBomb.MediaControl.Avalonia` (net10.0, Core only) provides a composited
`MediaSurface` that attaches one managed frame-sink surface to any
`IMediaControlSession`, resizes it with layout bounds x render scaling, and draws the
final frames the host-selected renderer produces; plus `AvaloniaMediaInputSources`.
Since slice 5 its `Role` and `SurfaceId` let a second `MediaSurface` (or the
`SecondaryPresentationWindow` helper, placed full screen on a chosen screen) show the same
session on another screen; every renderer accepts secondary frame-sink and shared-image
surfaces, each rendered at its own size. While the session is in Audio mode (no renderer)
the surface shows only its background.
It follows the same adapter rules as WPF, enforced by
`scripts/check-adapter-boundary.ps1`. See
`roadmap/active/027A_ACTIVE_UiAdapterImplementations.md`.

Shared GPU image surfaces (Stage 027A slice 2):

- Besides the HWND and frame-sink payloads, Core defines a `shared-gpu-image` surface. Its
  token resolves to an adapter-implemented `IMediaSharedGpuImageConsumer`.
- The renderer negotiates against the consumer's `MediaSharedGpuImageRequest` (handle kinds,
  sync kinds, GPU identity). It then exports a double-buffered `MediaSharedGpuImageSet` of
  plain OS handles: Direct3D11 keyed-mutex shared textures, or Vulkan opaque-fd images with
  optional exported semaphores.
- It announces each frame, and the consumer releases it once its compositor has read it.
  The shared `MediaSharedGpuImageRing` keeps the producer side, so a frame is never written
  while the compositor may sample it.
- A renderer that cannot serve the consumer, or rejects the surface, tells it why
  (`OnUnavailable`). The Avalonia `MediaSurface` then attaches its frame sink instead and
  logs the reason.
- Renderers own every GPU object. Adapters see only handles and Avalonia's
  `ICompositionGpuInterop`.
- Each surface, primary or secondary, has its own exported set and ring (slice 5).

## Audio Filter Framework (Stage 020A, slice 1)

The BASS playback chain is decode -> tempo/pitch -> filter stages -> normalization gain
-> fade envelope -> channel volume -> output. Internal `IAudioFilter` stages (priority
order, bypass = no native stage, reported latency, lifecycle per stream) are hosted by
the `AudioEngine` through `IAudioFilterHost`. The first filter is a BASS_FX PEAKEQ
equalizer driven by public `AudioEqualizerSettings` (`MediaEffects.Equalizer`,
`IMediaControlSession.Equalizer`, `EqualizerChanged`). See
`roadmap/complete/020A_COMPLETED_AudioFilterFramework.md`.

## CDG Snapshot Ownership (Stage 018A, slice 1)

`CdgRenderSnapshot` is immutable and garbage-collected; it is never pooled. The
presentation coordinator caches the latest one for retained refreshes and video
composition, and renderers only read it during `PresentComposition` (Direct3D11's layer
cache drops the payload). `CdgStateMachine.CreateSnapshot` returns the previous snapshot
when nothing changed and shares immutable buffers otherwise where possible. See
`roadmap/complete/018A_COMPLETED_CDGSnapshotOwnershipRedesignPooling.md`.

## Session Reset

- `IMediaControlSession.Reset(MediaPlayerConfiguration, IEnumerable<string>)` stops all playback, disposes the internal DI container and all engines/coordinators, rebuilds with the new config, and reattaches any existing presentation surfaces.
- Hosts call this instead of creating a new control instance when changing devices or fallback policy.

## Sidecar and Archive Background Policy

- Automatic image/video sidecar backgrounds apply to every non-video, non-image playable main: audio (MP3, FLAC, etc.), MIDI/KAR/MID with or without lyrics, MOD/tracker, CDG, audio+CDG pairs, and future non-visual content.
- Sidecar discovery is format-agnostic: it checks `FileTypes.Video` and `FileTypes.Image` extensions beside the main file (same base name).
- `ignoreSidecar: true` or `MediaFile.BackgroundAutoDetect = false` disables automatic discovery without disabling an explicitly supplied image/video background.
- Image main files never auto-detect a background. Their background must be explicitly supplied through a load overload.
- Video mains reject backgrounds entirely (both auto and explicit).
- Archive classification precedence: matching audio+CDG pair > audio > standalone CDG > video-only. When audio or CDG exists inside an archive, image/video entries become automatic background candidates rather than taking the main role.
- Supported automatic archive backgrounds include MP3+CDG+image/video, KAR+image/video, audio+image/video, MOD+video, and standalone CDG+image/video.
- Archive-backed backgrounds are stored as archive-path/entry-key references and streamed to a temporary decoder file on demand, avoiding full materialization of large archived videos into managed memory.

`FatzBomb.MediaControl.Wpf.Player` derives from Core `MediaControlPlayer`.

This makes Core commands, properties, and events automatically available through the WPF adapter while keeping adapter-specific additions limited to WPF conversions such as `ImageSource` to `MediaInputSource`.

`MediaSurface` accepts an `IMediaControlSession`/player and supplies HWND surfaces to Core. Its `OverlayContent` is WPF UI chrome only; it is not a media overlay. Media overlay, wash, background, and final composition stay in Core.
