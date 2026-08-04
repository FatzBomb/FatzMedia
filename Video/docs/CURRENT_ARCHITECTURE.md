# Current Architecture

## Projects

- `FatzBomb.MediaControl`: OS- and UI-agnostic Core media-control engine (`net10.0`).
- `FatzBomb.MediaControl.Rendering.Direct3D11`: Direct3D 11 renderer backend and subtitle rasterizer.
- `FatzBomb.MediaControl.Windows`: Windows platform composition, native integration, and hardware-decode policy.
- `FatzBomb.MediaControl.Windows.Direct3D11`: optional convenience bundle pairing Windows services with the current renderer.
- `FatzBomb.MediaControl.Wpf`: strict WPF adapter for control/HWND lifecycle interop; receives a renderer factory from its host.
- `FatzBomb.MediaControl.Player`: WPF validation host.

Future adapters should be separate projects using the same package pattern:

- `FatzBomb.MediaControl.WinUI`
- `FatzBomb.MediaControl.Avalonia`
- `FatzBomb.MediaControl.WinForms`

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
Core only, and the WPF adapter contains no renderer types. Future hosts can inject another
`IMediaPresentationCoordinatorFactory` without changing WPF, Core playback, or the
Windows native-service layer.

### Renderer Configuration

`MediaPlayerConfiguration` contains renderer-neutral session policy. It does not select
a concrete rendering backend and contains no DXGI swap-chain mode. The host selects a
renderer by injecting its factory. Direct3D11-specific creation policy lives in
`Direct3D11PresentationOptions`, supplied to `Direct3D11PresentationCoordinatorFactory`
or the `WindowsDirect3D11MediaControlPlatformFactory` convenience bundle. Its native
swap-chain present mode defaults to VSync; Immediate remains an explicit diagnostic mode.

## Ownership

Core owns playback, commands, media state, sidecar detection, background image/video lifecycle, Cdg state, wash, `MediaPresentationCoordinator`, retained composition planning, target lifecycle policy, renderer-neutral configuration, and platform/renderer contracts. The Direct3D11 renderer owns its native device, GPU resources, renderer-specific options, swap chains, DirectComposition implementation, subtitle rasterization, and final GPU execution.

Adapters own only framework interop: control lifecycle, native surface creation, DPI/size, visibility, focus/input bridge, and framework-specific source conversion helpers.

Adapters must not own renderer selection, layer roles, frame processing, Cdg invalidation, background/video presentation, compositor timing, swap-chain policy, or final present logic.

## Runtime Flow

1. Host places a framework control, such as WPF `MediaSurface`.
2. Adapter creates or discovers a framework/native presentation surface.
3. Adapter passes a neutral `MediaPresentationSurfaceDescriptor` to Core.
4. Core's `MediaPresentationCoordinator` owns surface lifecycle, frame routing, pacing, retained-layer intent, and recovery policy.
5. The selected renderer backend executes Core's composition plan for Layer 0 background/base, Layer 1 wash, and Layer 2 Cdg overlay.
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
- MIDI files (`.mid`, `.midi`, `.kar`, `.rmi`) play through the existing `AudioEngine` path with pitch, tempo, normalization, volume, and loop support.
- Tracker formats (`.mod`, `.it`, `.s3m`, `.xm`, `.mo3`, `.mtm`, `.umx`) are natively supported by BASS via `BASS_MusicLoad`. `AudioStreamManager` loads tracker playback sources with decode + float + prescan flags so byte-position seeking works through the BASS_FX tempo stream, and releases their `HMUSIC` handles with `Bass.MusicFree`.

## MIDI/KAR Karaoke Lyrics

- Lyric markers (MIDI meta event 5) and text markers (meta event 1) are extracted via `BassMidi.StreamGetMarks` from the open BASS MIDI channel by `MidiLyricExtractor`.
- `MidiMetadataExtractor` (internal) reads Soft Karaoke `@T` (title) and `@I` (information) markers plus standard MIDI meta events (TrackName, Copyright, KeySignature, TimeSignature, Tempo). Produces `KaraokeMetadata` with filename+duration fallback when no embedded metadata exists.
- Supported formats: `.kar`, `.mid`, `.midi`, `.rmi`. Lyrics load for any of these when the file contains lyric or karaoke text markers.
- Soft Karaoke (`/` newline, `\` clear screen), Tune1000 (`\r` line feed, `\n` new paragraph), and Solton (`<` line prefix) display controls are honored.
- Syllable-level timing is preserved through the subtitle pipeline as `KaraokeCueTiming` metadata on `SubtitleCue`. The renderer uses absolute MIDI marker timestamps for wipe highlighting, independent of scroll animation timing.
- MIDI markers are shifted 400ms earlier so syllable wipe highlighting leads the corresponding note, giving the singer visual preparation similar to CDG timing.
- `ShowMidiLyrics` (default `true`) toggles MIDI lyric display at runtime without affecting ordinary SRT/ASS subtitles. Exposed on `IMediaControlSession`, `MediaControlPlayer`, and the Player host UI.
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

## Audio Output Device Selection (Stage 006C)

- `AudioOutputDeviceInfo` is a Core public record; `AudioOutputDevices.Enumerate(...)` lives in the Windows package and enumerates BASS output devices through Windows composition.
- `MediaPlayerConfiguration.AudioOutputDeviceIndex`: BASS device index, `-1` = Primary (system default). Creation-time choice; runtime device changes require a new player or `Reset`.
- `MediaPlayerConfiguration.AudioOutputFallbackMode`: `FallbackToPrimary` (default) or `NoFallback`.
- If an explicit device is unavailable at init and fallback is enabled, the engine retries on Primary.
- Both `AudioEngine` and `VideoAudioEngine` resolve and select the configured BASS device before stream creation and latency queries.

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
