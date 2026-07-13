# Current Architecture

## Projects

- `FatzBombMedia.MediaControl`: UI-agnostic Core media-control engine.
- `FatzBombMedia.MediaControl.Wpf`: strict WPF adapter for control/HWND lifecycle interop.
- `FatzBombMedia.Player`: WPF validation host.

Future adapters should be separate projects using the same package pattern:

- `FatzBombMedia.MediaControl.WinUI`
- `FatzBombMedia.MediaControl.Avalonia`
- `FatzBombMedia.MediaControl.WinForms`

## Ownership

Core owns playback, commands, media state, sidecar detection, background image/video lifecycle, Cdg, wash, retained layers, D3D11 composition, swap-chain creation, target registration, multi-target fan-out, and final presentation.

Adapters own only framework interop: control lifecycle, native surface creation, DPI/size, visibility, focus/input bridge, and framework-specific source conversion helpers.

Adapters must not own renderer selection, layer roles, frame processing, Cdg invalidation, background/video presentation, compositor timing, swap-chain policy, or final present logic.

## Runtime Flow

1. Host places a framework control, such as WPF `MediaSurface`.
2. Adapter creates or discovers a framework/native presentation surface.
3. Adapter passes a neutral `MediaPresentationSurfaceDescriptor` to Core.
4. Core attaches/resizes/detaches that surface.
5. Core builds the final composed frame from Layer 0 background/base, Layer 1 wash, and Layer 2 Cdg overlay.
6. Core presents the composed frame to all active targets.

## Video/Audio Demux Ownership

- Main video decoding owns the Core-managed video demux cursor used for video seek, buffering, and proactive loop pre-seek.
- `VideoAudioEngine` opens an independent FFmpeg cursor for the same source. This preserves codec priming/preroll semantics; routing initial audio packets through the video cursor caused a persistent startup offset with AV1 + E-AC-3 until the first seek.
- Coordinated seek still repositions both cursors to the same target before video warm-up and audio resume.
- Audio-backed gapless looping remains independent: video pre-seeks its cursor while `VideoAudioEngine` wraps its own decoder without stopping the BASS channel.

## MIDI Playback

- BASS optional plugins (bassmidi, bassflac, bassopus, etc.) are loaded via `Bass.PluginLoad` so BASS recognizes their formats automatically.
- `BassMidiSoundfontManager` resolves the default MIDI soundfont with this priority:
  1. `MediaPlayerConfiguration.MidiSoundfontPath` (user-configured; always wins if set and exists)
  2. Windows `gm.dls` (converted to SF2 via `DlsToSf2Converter`, cached in LocalAppData)
  3. System `.sf2` files (sgm.sb, etc.)
  4. Bundled `native/TimGM6mb.sf2` (5.7 MB compact GM soundfont; always present)
- DLS-to-SF2 conversion uses embedded Kermalis DLS2 and SoundFont2 libraries (MIT). The RIFF word-alignment bug in their `Chunk.cs` is fixed locally.
- MIDI files (`.mid`, `.midi`, `.kar`, `.rmi`) play through the existing `AudioEngine` path with pitch, tempo, normalization, volume, and loop support.
- Tracker formats (`.mod`, `.it`, `.s3m`, `.xm`, `.mo3`, `.mtm`, `.umx`) are natively supported by BASS via `BASS_MusicLoad`. `AudioStreamManager` routes tracker formats to `Bass.MusicLoad` instead of `Bass.CreateStream`.

## MIDI/KAR Karaoke Lyrics

- Lyric markers (MIDI meta event 5) and text markers (meta event 1) are extracted via `BassMidi.StreamGetMarks` from the open BASS MIDI channel by `MidiLyricExtractor`.
- Supported formats: `.kar`, `.mid`, `.midi`, `.rmi`. Lyrics load for any of these when the file contains lyric or karaoke text markers.
- Soft Karaoke (`/` newline, `\` clear screen), Tune1000 (`\r` line feed, `\n` new paragraph), and Solton (`<` line prefix) display controls are honored.
- Syllable-level timing is preserved through the subtitle pipeline as `KaraokeCueTiming` metadata on `SubtitleCue`. The renderer uses absolute MIDI marker timestamps for wipe highlighting, independent of scroll animation timing.
- `ShowMidiLyrics` (default `true`) toggles MIDI lyric display at runtime without affecting ordinary SRT/ASS subtitles. Exposed on `IMediaControlSession`, `MediaControlPlayer`, and the Player host UI.

### Karaoke Text Style

- `KaraokeTextStyle` (in `MediaPlayerConfiguration.KaraokeStyle`) provides independent font, color, outline, and shadow settings for karaoke lyrics, separate from `SubtitleTextStyle`.
- Base (unsung) and wipe (sung) colors, outlines, widths, and shadows are independently configurable.
- `LineTransitionMilliseconds` (default 450ms) controls scroll/fade animation duration. Set to 0 to disable animation.
- `NextLineOpacity` (default 0.65) controls the opacity of upcoming lines.

### Karaoke Scrolling Behavior

- Up to five lines are displayed: two previous, active centered, two upcoming.
- Non-active lines fade based on distance from the active line.
- Normal line transitions complete 1.5 seconds before the next line's first wipe when sufficient time exists. If the gap is shorter, the transition starts immediately after the current line's final syllable and completes as early as possible.
- The first page fade-in completes 3 seconds before the first lyric wipe when lead-in permits. With short lead-in, fading starts at playback time zero. If the first wipe is at time zero, lyrics appear immediately without fading.
- Clear-screen/new-paragraph transitions: the outgoing page holds for 2 seconds after its last wipe, then fades out. The new page fades in and completes 2 seconds before its first wipe. When the available gap is under 4 seconds, timing is split to prioritize incoming-page visibility.
- Syllable wipe highlighting uses absolute MIDI marker timestamps and does not begin until the actual marker time, even if the line has scrolled into position early.

### Karaoke Rendering

- Karaoke text is rasterized into a cropped bitmap covering only the visible text area, not a full 1920x1080 buffer. Crop position within the logical canvas travels as composition metadata.
- The D3D11 compositor maps the cropped texture through `ViewportGeometry.MapCanvasRegionToViewport`, preserving 16:9 aspect ratio at any window size.
- The subtitle presentation loop runs at 75 Hz for timing accuracy but only presents when subtitle text, wipe progress, scroll position, or fade state actually changes. No-op frames are skipped.
- Backward seeks immediately recalculate active cue, syllable highlighting, scroll position, and page fade state.

## Device-Lost Recovery

Core detects and recovers D3D11 device loss (Stages 005A/005B/005C) entirely inside Core; adapters are never involved.

- `D3D11DeviceLostClassifier` classifies device-removed/reset conditions on both sides: HRESULT-based for the Core-owned presentation device, and an `IsDeviceLost(IntPtr)` overload for the FFmpeg-owned hardware-decode device (a raw native pointer, wrapped best-effort and never false-positive).
- Two independent recovery orchestrators, matching the two independently owned devices: the presentation side in `NativeMediaPresentationCoordinator` (rebuilds device, swap chains, DirectComposition target, and secondary outputs), and the decode side in `VideoPipeline` (runtime software-decode fallback with background hardware re-probe/restore; audio and CDG keep running).
- Diagnostic categories follow a fixed naming convention: `"D3D11.DeviceLost"` (presentation) and `"Decoder.DeviceLost"` (decode), raised at Error on loss and Info on successful recovery, with a `.RecoveryFailed` suffix for the terminal diagnostic after bounded retries are exhausted.
- Both recovery paths reuse existing rebuild machinery (the proven surface-recreate and seek stop-reposition-restart sequences) — no new swap-chain, compositor, or renderer abstraction was introduced.

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

- `AudioOutputDeviceInfo` (public record) and `AudioOutputDevices.Enumerate(...)` enumerate BASS output devices through MediaControl.
- `MediaPlayerConfiguration.AudioOutputDeviceIndex`: BASS device index, `-1` = Primary (system default). Creation-time choice; runtime device changes require a new player or `Reset`.
- `MediaPlayerConfiguration.AudioOutputFallbackMode`: `FallbackToPrimary` (default) or `NoFallback`.
- If an explicit device is unavailable at init and fallback is enabled, the engine retries on Primary.
- Both `AudioEngine` and `VideoAudioEngine` resolve and select the configured BASS device before stream creation and latency queries.

## Session Reset

- `IMediaControlSession.Reset(MediaPlayerConfiguration, IEnumerable<string>)` stops all playback, disposes the internal DI container and all engines/coordinators, rebuilds with the new config, and reattaches any existing presentation surfaces.
- Hosts call this instead of creating a new control instance when changing devices or fallback policy.

## Sidecar and Archive Background Policy

- Automatic image/video sidecar backgrounds apply to all non-video, non-image playable mains: audio (MP3, FLAC, etc.), MIDI/KAR/MID, MOD/tracker, CDG, and audio+CDG pairs.
- Sidecar discovery is format-agnostic: it checks `FileTypes.Video` and `FileTypes.Image` extensions beside the main file (same base name).
- Image main files never auto-detect sidecar backgrounds. An image background must be explicitly supplied through the load overload.
- Video mains reject backgrounds entirely (both auto and explicit).
- Archive classification precedence: matching audio+CDG pair > audio > standalone CDG > video-only. When audio or CDG exists inside an archive, any video or image entries become background candidates rather than taking the main role.
- Supported archive combinations: MP3+CDG+image/video, KAR+image/video, audio+image/video, MOD+video, standalone CDG+image/video, video-only archives.
- Archive-backed backgrounds are stored as archive-path/entry-key references and streamed to a temporary decoder file on demand, avoiding full materialization of large archived videos into managed memory.

`FatzBombMedia.MediaControl.Wpf.Player` derives from Core `MediaControlPlayer`.

This makes Core commands, properties, and events automatically available through the WPF adapter while keeping adapter-specific additions limited to WPF conversions such as `ImageSource` to `MediaInputSource`.

`MediaSurface` accepts an `IMediaControlSession`/player and supplies HWND surfaces to Core. Its `OverlayContent` is WPF UI chrome only; it is not a media overlay. Media overlay, wash, background, and final composition stay in Core.
