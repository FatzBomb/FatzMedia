# Engineering Issues

Updated: 2026-10-23

This register tracks verified engineering follow-ups that are not product-feature
stages. Items remain here until fixed, deliberately accepted, or promoted into a
bounded roadmap stage.

## In Progress

None.

## Open

These came out of the 2026-10-18 code audit (ENG-020). Each was verified by reading the code
but deferred: the fix is larger than the audit's bounded commits, needs a design decision, or
needs Windows to verify.

### ENG-021 - BASS output latency and device recovery are largely inert

Reported: 2026-10-18 (audit). Candidate for the FatzAudio stage (037A), which replaces this path.
- BASS is initialised without `DeviceInitFlags.Latency`, so `BASS_INFO.latency` is 0 and
  output-latency compensation (and the USB/Bluetooth heuristic) does nothing.
- ~~The "device recovery" in `AudioEngine.Play()` re-inits a device that is still initialised;
  `Bass.Free` is never called, so it cannot recover a lost device.~~ Replaced by ENG-032's
  output watchdog (`BASS_Start`, then `BASS_DEVICE_REINIT`); switching to a different device
  still needs the player-level Reset.
- `VideoAudioEngine` publishes position snapshots that mix raw and latency-corrected values.

### ENG-022 - Subtitle content flag is never revoked

Reported: 2026-10-18 (audit). `SubtitlePipeline` only ever adds `MediaContent.Subtitle`: turning
subtitles off, or loading embedded tracks without selecting one (MIDI with lyrics hidden),
leaves it set. The coordinator then runs its 75 Hz render loop for audio-only media and
presents empty compositions on seeks, and `HasSubtitle`/`HasVisualContent` read true.
Not fixed in the audit because the flag also means "tracks available" to the picker, and the
render loop is decided at play: tying the coordinator to the selected track needs the
coordinator to follow `ISubtitleEngine.CuesChanged` (start or stop the loop), or the flag
split into "available" and "showing".

### ENG-023 - Software-path compositions are built and presented unordered

Reported: 2026-10-18 (audit; Likely, not reproduced). On the software path the coordinator builds
a composition under its lock and presents it outside it; the video-frame thread and the render
loop can overlap, so an older composition can be presented after a newer one and stay up while
paused. Not a simple fix: per-target cadence (017A) presents compositions to target subsets,
so "drop older" would wrongly skip one target's frame, and a separate build-and-present lock
needs its order against the coordinator lock proven for every caller.

### ENG-024 - Player API threading contract gaps

Reported: 2026-10-18 (audit).
- `Reset` silently reverts runtime settings (volume, pitch, tempo, EQ, loop...) to defaults and
  raises no change events.
- `Dispose`, `Reset` and the sync API block the calling (UI) thread with no bound; some
  "Async" methods block too.
- `Reset` is not serialized against surface attach/detach calls and some property reads.
- The worker-context flag is an `AsyncLocal` and flows into child tasks, so a task started
  from an event handler can bypass serialization or make `Dispose`/`Reset` throw wrongly.
- Event delivery is inconsistent: some events swallow handler exceptions, others let them
  escape onto engine threads.

### ENG-026 - Renderer robustness and per-frame costs (Direct3D11, Vulkan)

Reported: 2026-10-18 (audit; perf items not measured).
- Most D3D11 failure logs are `Debug.WriteLine`, compiled out of Release: route the ones a
  user would need through `DiagnosticRouter` (latched, since some repeat per frame).
- A secondary (projector) window resize recreates its swap chain instead of queuing
  `ResizeBuffers` like the primary.
- Per-frame allocations: a render-target view per target per frame, target snapshot lists and
  LINQ in the backend set, the DirectComposition back buffer re-wrapped after every present.
- The managed frame sink reallocates on resize churn; Vulkan readback memory may be uncached
  and the background is re-uploaded every frame.
- Post-process targets are never pruned for removed secondary windows.

### ENG-027 - Hardware decoding does not reserve extra pool surfaces

Reported: 2026-10-18 (audit; Suspected, needs Windows). The pipeline holds up to 12 cloned
hardware frames plus the current one, each pinning a surface of FFmpeg's d3d11va pool, but
`extra_hw_frames` is never set. If the pool (sized from the codec's reference needs) runs out,
decoding fails with a surface-pool error that counts as a hardware failure. Fix to try: set
`extra_hw_frames` to the pipeline's queue depth plus 2 before `avcodec_open2` on the hardware
path, and check VRAM use at 4K.

### ENG-028 - MIDI silent after the audit's DLS converter changes

Reported: 2026-10-19 (owner smoke of the ENG-020 audit branch): everything else works, MIDI
plays with no audio. Not reproduced on Linux (no `gm.dls`). Suspected cause: the cache key
included the DLS's size and write time, so Windows' `gm.dls` was converted again by the
current converter instead of reusing the older cached `gm.dls.sf2`; either that conversion
failed (`No soundfont found`) or its SF2 plays silently. The cache name is back to
`{name}.sf2`, so an older cached SF2 is used again; the converter's other changes (sample IDs,
loop ends, chunk padding) are untested against a real `gm.dls` and remain the suspect if MIDI
stays silent after the old cache is gone.

Checked 2026-10-20 with the owner's real `gm.dls` (3.3 MB): the current converter and the
pre-audit one both convert it without error, to the same size; the sample data and every
instrument, region and bag record are identical, and only 439 sample headers differ, by a loop
end one sample earlier (the file's authored loop end). So the SF2 is not the cause. The other
MIDI-specific change, dropping `FxFreeSource` (= `BASS_MIDI_NOTEOFF1`) from the source-stream
flags, is restored as it was. The audio engine, normalizer and end-sync changes are unlikely
but not excluded; the `BASSMIDI` log lines would settle it. Needed: the Log's `BASSMIDI` lines and
the sizes in `%LOCALAPPDATA%\FatzBombMedia\soundfonts`. Workaround: rename the old
`gm.dls.sf2` to the new cache name. Owner is waiting on FatzAudio (037A), which may replace
this MIDI path.

### ENG-029 - CD+G: smooth scroll, memory-preset repeats and pack timing

Reported: 2026-10-20 (comparison with FatzAudio's `cdg.c`, differential test over 9,500 fuzzed
packs; see `docs/fatzaudio/2026-10-20_MediaControl_Review_MidiAndCdg.md`). Both decoders agree
on every pixel and palette entry except:
- **Smooth scroll ignored.** `CdgStateMachine.ScrollUnsafe` never reads the horizontal (0-5) and
  vertical (0-11) offset fields; a scroll pack with only an offset change does nothing. Songs that
  glide a line at a time jump a whole tile. Keep the offsets in state and checkpoints and shift the
  288x192 picture area when rendering (the border stays). Verified by test.
- **Memory preset with repeat != 0 never clears pixels** (`CdgStateMachine.cs:286`, pinned by a
  test). A clear that survives only in its repeat packets leaves stale pixels. Act on a repeat
  unless the previous pack was the same preset. Verified by test.
- **Pack timing truncates a double.** `CdgEngine.cs:181` uses `(long)(seconds * 300)`; for about
  5% of exact boundaries that lands one pack (3.3 ms) early-or-late depending on rounding. Use an
  integer rule (FatzAudio uses packs [0, ceil(g x 300)) in integer nanoseconds). Verified by
  arithmetic.
- Smaller: the transparent-colour pack is a no-op and index 0 always takes the background
  opacity; each seek marks the whole frame dirty; `CdgPacket` allocates a small array per pack
  (about 14 MB for a 4-minute file) and every changed snapshot copies 64.8 KB. FatzAudio's seeks
  measured about 15x cheaper on the same input.

### ENG-030 - DLS-to-SF2 converter drops loops and articulation

Reported: 2026-10-20 (comparison with FatzAudio's `dls.c`; Windows `gm.dls` scanned). Our
converter writes loop points but never sampleModes (generator 54), so nothing loops (1,114 of
1,498 `gm.dls` regions should); applies region fine tune twice (shdr correction and generator 52;
1,025 regions); drops all art1 articulation (envelopes, vibrato, tremolo), region gain and pan
(432 regions), and key groups (120 regions); ignores ptbl (safe for `gm.dls`); writes `ifil` as
the wrong bytes. These degrade the sound but do not silence it (ENG-028). Likely superseded by
037A slice 3 (FatzAudio loads DLS directly); fix only the cheap items (sampleModes, double fine
tune) if MIDI stays on BASS for long.

### ENG-031 - MIDI lyrics: encoding and text-event fallback

Reported: 2026-10-20 (same comparison). `MidiLyricExtractor` gets text from BASS marks, which
ManagedBass decodes with the system ANSI code page: Shift-JIS is right only on a Japanese-locale
PC and UTF-8 is garbled elsewhere. The text-event fallback takes every track's meta-1 text with
no karaoke check, so ordinary MIDI files can show instrument names as lyrics. The 42-character
line break only triggers when the previous syllable ends in a space. `MidiMetadataExtractor`
label matching has no word boundary ("Songbird" gives title "bird"). FatzAudio has the same
encoding gap, so this stays ours under 037A.

## Completed

### ENG-025 - Process-global state shared across players

Resolved: 2026-10-23, pending manual smoke (two players open, device loss on one).

Reported: 2026-10-18 (audit).
- `DiagnosticRouter` is a static event; every player (and coordinator) subscribes, so one
  player's device-lost diagnostic makes every coordinator start recovery.
- `FfmpegAudioFallback` keeps one process-wide prepared conversion shared by all players.
- `FileTypes.Audio`/`Video`/`FfmpegAudio` are public mutable static `HashSet`s written during
  native bootstrap and read from any thread.

Fix:
- `DiagnosticRouter` keeps an `AsyncLocal` owner. A player enters itself as owner in its
  constructor and around surface attach/resize/detach/live-resize, so the threads and tasks
  it starts inherit it; `MediaDiagnosticInfo.Owner` carries it and `IsFor(owner)` is true for
  the owner and for unowned (process-wide) diagnostics. `MediaControlPlayer.MediaDiagnostic`
  and the coordinator's device-lost recovery ignore other owners' diagnostics.
- Paths that drop the execution context re-enter the owner: `AudioEngine`'s BASS end-sync
  callback (native thread) and the faded prepared-next transition (`SuppressFlow`).
  `MonitorRefreshRateCache`'s refresh work stays unowned (process-wide, goes to every player).
- `FfmpegAudioFallback` keeps up to four prepared conversions keyed by source
  (`PreparedConversionCache`), so one player preparing no longer evicts another's.
- `FileTypes`: not changed. The sets are written once, under the bootstrap lock, before any
  player loads media, and re-adding an existing extension does not mutate a `HashSet`, so
  concurrent readers never see a write in progress.

Tests: `DiagnosticOwnershipTests`, `MediaControlPlayerTests.MediaDiagnostic_ReachesOnlyTheOwningPlayer_UnownedReachesAll`,
`MediaPresentationCoordinatorTests.DeviceLostDiagnostic_FromAnotherOwner_IsIgnored`.

Known limits (code review 2026-10-23, not fixed):
- Synchronous public setters (`LoopMedia`, `Effects.*`, `TransparentBackground`) do not enter the
  player's owner, so a diagnostic they raise while called from another player's event handler is
  attributed to that other player. Fix if it shows up: enter the owner on every public entry point.
- Prepared conversions are keyed by file, not by player: two players preparing the same file share
  one entry, and one player's clear disposes it, so the other converts again at transition time.
  Fix if it shows up: reference-count entries per player.

### ENG-032 - Playback never resumes after another application takes the audio device

Resolved: 2026-10-23, pending manual smoke.

Reported: 2026-10-23 (owner). Two players in the Avalonia test player (Windows, BASS audio).
Another application on the PC (an audio engine running speaker tests, probably opening the
device in WASAPI exclusive mode) took the output device. Both players stopped, audio and video
(video follows the audio clock); the UI stayed responsive and nothing was logged. Playback
never resumed after the other application finished, and loading a new file did not play either.

Cause: nothing in our code noticed BASS losing the output. BASS pauses the device's output and
parks its playing channels as `BASS_ACTIVE_PAUSED_DEVICE` (state 4, not in ManagedBass 4.0.2's
`PlaybackState`), and only `BASS_Start` (or a reinit) brings them back. The device monitors
(`PollingAudioDeviceMonitor` on Windows, `BassDeviceAudioMonitor` on Linux) only detect
default-device and sample-rate changes. The "device recovery" in `AudioEngine.Play()` re-ran
`BASS_Init` on a device that was still initialized (`BASS_ERROR_ALREADY`, so a no-op) and failed
quietly with a generic warning; `VideoAudioEngine.Play()` did not report the failure at all.

Fix (Core only, BASS only, so Windows and Linux share it):
- **Decision unit:** `Core/Audio/AudioOutputRecoveryTracker` is pure. Given a device sample
  (output status from `BASS_IsStarted`, how many channels intend to play, how many BASS reports
  as paused by the device) and the time, it returns the next action: none, report lost, start,
  reinit, resume channels, report recovered. It reports a loss once per outage, retries once a
  second, follows a failed `BASS_Start` with a reinit and a failed reinit with a plain start,
  resumes parked channels once the output is back (escalating to a reinit if they stay
  parked), and reports the recovery once. Channels whose engine is not `Playing` never count.
- **Watchdog:** `Core/Audio/BassOutputWatchdog` is the thin BASS layer. `AudioEngine` and
  `VideoAudioEngine` register while their state is `Playing` and unregister on pause, stop,
  close and dispose. One process-wide timer runs only while something is registered: every
  second it groups the playing channels by `BASS_ChannelGetDevice`, samples each device, and
  carries out the tracker's action, so a device is restarted once per process however many
  players share it. Restarts are `BASS_Start`, then `BASS_Init(device, rate,
  BASS_DEVICE_REINIT)` on the same device and last known sample rate. Each engine resumes only
  its own channel (`BASS_ChannelPlay` without restart, so at its position), under its transport
  lock and only if it is still `Playing`, so a channel the host paused or stopped is never
  touched. When the last engine unregisters the timer stops and the outage is forgotten.
  `BASS_IsStarted` is not wrapped by ManagedBass 4.0.2: it is called through a function pointer
  resolved from the BASS module ManagedBass itself loads (the same `DllImport` resolution,
  including the Linux resolver); if the export is missing, detection falls back to channel
  state alone.
- **Reporting:** one unowned `DiagnosticRouter` Warning per outage (category `AudioOutput`):
  "Audio output unavailable (another application may be using the device exclusively);
  retrying (output: ...)", and an Info "Audio output available again; playback resumed" on
  recovery. Every player also raises the existing public `AudioDeviceChanged` event (no new
  API): `DeviceRemoved` with that failure message on the loss, `DeviceAdded` on the recovery.
- **Play while unavailable:** when `BASS_ChannelPlay` fails with an output error (`BASS_ERROR_START`,
  `BASS_ERROR_REINIT`, busy, device, driver, unknown), both engines restart the output once
  (sharing the watchdog's per-device rate limit) and retry. If it still fails they raise an
  Error: "Audio output unavailable (another application may be using the device exclusively);
  playback could not start (...)". This replaces the inert re-init in `AudioEngine.Play()`
  (the ENG-021 bullet). If the play succeeds but BASS parks the channel, the watchdog resumes
  it like any other.

Tests: `AudioOutputRecoveryTrackerTests` (21) covers detection (stopped output, parked channel,
unknown output status, nothing playing), one report per outage, the one-second retry, the
start/reinit alternation, resume and its escalation, recovery, a second outage, the shared
rate limit with Play, and a full outage scenario. The BASS layer cannot run in tests.

Manual smoke (Windows):
1. Play a file in each player of the Avalonia test player (audio and a video).
2. In Windows Sound settings, open the output device's properties > Advanced and enable "Allow
   applications to take exclusive control of this device".
3. Start an application that holds the device exclusively (an ASIO/WASAPI-exclusive player or
   a speaker test), or unplug a USB audio device.
4. Expect one Warning in each player's log, "Audio output unavailable (another application
   may be using the device exclusively); retrying", and `AudioDeviceChanged` if the host shows it.
5. Close the other application (or plug the USB device back in). Within about a second both
   players resume from where they stopped, video with them, and the log shows "Audio output
   available again; playback resumed".
6. Pause one player during an outage, then end the outage: the paused player stays paused.
7. During an outage, load a new file and press Play: expect either an Error naming the
   unavailable output (Play failed; press Play again after the outage) or playback that starts
   by itself when the outage ends, never silent failure.
8. Linux (when BASS player smoke runs): unplug and replug a USB audio device while playing.

### ENG-020 - October 2026 code audit

Fixed: 2026-10-18. Needs the owner's general smoke (see the PR's smoke steps); no single
behaviour change is user-visible except where noted.

Six read-only audits (video, audio, presentation, API/adapters, renderers, dead code) found the
items below; each was verified in the code before it was fixed. Deferred items are ENG-021 to
ENG-027. Commits:
- **Video:** a decoder swap kept the old timeline origin and left the loops stopped on failure;
  post-seek warm-up spun at end of stream; the audio engine's own demuxer read every stream;
  archive videos were copied twice in memory.
- **Audio:** only the first loop wrap was reported (MediaLooped); a throwing event handler in the
  BASS end sync could end the process; the normalizer gain FX was unsynchronised; a failed video
  audio seek left playback paused and flushed; network streams could get the loop flag; stale
  `.media` scratch files were never swept; the DLS converter mis-numbered samples after a skipped
  wave, ignored loop ends, and could cache a truncated SF2 forever.
- **Presentation:** SRT cues merged on whitespace-only separator lines and timing lines with
  position hints were dropped; cue lookup was a scan per frame; the D3D11 renderer could upload
  only a CD+G snapshot's dirty regions into a texture that had missed earlier ones (stale lyrics
  pixels); the pooled CPU frame stayed rented on the hardware path; a seek presented video twice;
  the CD+G opacity getter did not round-trip.
- **API/adapters:** a failed `Reset` left the player permanently unusable; a failed constructor
  leaked the engine; two players starting together could fail native loading with an empty
  message; prepared-next stat'ed files under a lock the UI takes; WPF lightweight surfaces could
  subscribe twice and land a slow load over a later one; Player host startup and disposal gaps.
- **Renderers:** a failed `ResizeBuffers` was treated as success; failed draws left the swap-chain
  buffer bound; the Vulkan factory had no availability probe.
- **Dead code and duplicates:** see the refactor commit.

A review of the fix commits found no regression of note; its follow-ups are in the last fix
commit: a decoder swap retried after a failed attempt kept the timeline origin, a swap that
throws leaves the loops stopped, a failing `ResizeBuffers` is retried every half second
rather than every frame, and a failed Reset no longer leaks an operation queue.

### ENG-014 - Vulkan duplicates software-renderer placement and CDG validation

Fixed: 2026-10-18. No owner smoke needed: the Windows Player does not use Vulkan; the
software and Vulkan conformance tests (lavapipe) cover it.

Reported: 2026-10-17 (local review of 026A slice 2). `VulkanCompositionPlanner` copied the
software renderer's nearest-sampling placement (`SoftwareStretch`: edge rounding, clamping,
source pixel) and its CDG snapshot validation, because Vulkan may not reference the software
project. Parity tests caught drift, but nothing linked the copies. Two smaller items from
the same review:
- The committed SPIR-V was regenerated by hand, with no CI check that it matches the GLSL.
- Vulkan expanded and uploaded the CDG overlay (about 260 KB) on every frame that drew it.

Fix:
- **Placement:** Core `Geometry/NearestSampledPlacement` replaces `SoftwareStretch` and the
  Vulkan copy. `VulkanOverlayQuad` wraps it and keeps its texture-coordinate and source-pixel
  members; `PlanBackground` takes its clipped rectangle from it.
- **CDG validation:** `CdgRenderSnapshot.IsWellFormed` (no pixel data, or a full frame of
  indices and a palette). The software and Vulkan renderers both use it. Direct3D11 keeps its
  own stricter check (a full 16-entry palette for its palette texture).
- **Shaders:** `scripts/check-vulkan-shaders.sh` compiles every shader and compares the
  result with the committed `.spv`, ignoring the SPIR-V header's compiler-version word. The
  Linux CI job installs `glslang-tools` and runs it. Ubuntu 24.04's glslang 15.1.0
  reproduces the committed files exactly.
- **CDG upload:** the Vulkan compositor keeps the CDG texture while the frame's snapshot is
  the same object as the last one uploaded. Snapshots are immutable, and Core reuses the
  object while the CDG picture is unchanged (018A).

Tests:
- `NearestSampledPlacementTests` covers rounding, clipping, canvas placement and
  `IsWellFormed`.
- The existing software pixel tests and Vulkan parity tests are unchanged and pass.
- New: `UnchangedCdgSnapshot_ReusesTheUploadedTexture` checks one upload for repeated frames,
  identical pixels, and a new upload for a new snapshot.
- The shader check fails on an edited shader and passes on the committed set.

### ENG-019 - Video color left to defaults: washed out or dull on some setups

Fixed: 2026-10-18. Owner smoke passed the same day: "It looks right now."

Reported: 2026-10-18 (owner). Since early in the 2026-10-16 to 10-18 run, videos looked
slightly washed out; raising contrast and lowering brightness corrected them.

Investigation:
- No commit in the run touched color handling, shaders or swap-chain formats.
- FFmpeg 8.1 and 9.0 swscale give identical YUV-to-BGRA levels (16 to 0, 235 to 255),
  tested with both library sets.

What was wrong in both versions: nothing told either conversion to RGB the video's color
space.
- **GPU path (D3D11VA frames through the D3D11 video processor).** Neither the stream's
  nor the output's color space was set, so the driver chose both. A driver that assumes
  full-range input or limited-range output shows video washed out. A GPU control panel's
  video "dynamic range" setting or a driver update can change that choice without any change
  here.
- **CPU path (swscale).** Every frame was converted as BT.601 limited range.
  - HD video uses BT.709, so its colors came out dull: a BT.709 pure red decoded to 230,0,0.
  - Full-range video was clipped: full-range gray 32 decoded to 17.

Fix:
- Core reads each frame's FFmpeg color tags into `VideoColorSpace` (matrix and range).
  Untagged parts are filled in by frame size: BT.709 for HD, BT.601 for SD, and limited
  range.
- The software conversion passes the tags to swscale (`sws_setColorspaceDetails`). The same
  clips now decode to 251,0,0 and 31.
- Native hardware frames carry the color space (`NativeVideoFrameResource.ColorSpace`).
  - The Direct3D11 video processor sets the stream's DXGI color space and full-range BT.709
    RGB output (`VideoProcessorSetStreamColorSpace1` / `SetOutputColorSpace1`), once the
    driver confirms the conversion.
  - Otherwise it uses the older color-space description.
- The per-file "Decoding ..." log line names the color space and flags HDR (PQ/HLG) video.
  Such video is shown without tone mapping, so it looks washed out on any path.

Not covered: HDR tone mapping and HDR output.

Tests: `VideoColorSpaceTests` (tag mapping, size-based defaults, swscale tables, log text) and
`D3D11VideoColorSpaceTests` (DXGI mapping, Windows-only). The decoder's CPU conversion was
checked against FFmpeg 9 on Linux with BT.709 and full-range clips (values above).

Smoke: `docs/VALIDATION.md`, "Video color (ENG-019)".

### ENG-018 - Cadence report sometimes names the 60 Hz fallback instead of the monitor

Fixed: 2026-10-18. Owner smoke passed the same day: five loads (AV1, HEVC 10-bit, H.264),
the window moved to the other monitor before each; every report named the monitor's own rate
(60 Hz and 59.973 Hz alternating with the monitor), none the platform fallback.

Reported: 2026-10-18 (owner logs, 036A smoke). The once-per-open `VideoCadence` line said
`60 Hz ... reported by the platform` in some runs and `59.973 Hz ... of the monitor showing
the video` in others, with the same file, monitor and renderer.

Cause: the report is decided once, on the first presentation pass after playback starts,
from whatever rate is resolved at that instant. Two things can leave only the platform rate
there for a moment: the renderer's rate source attaching just after playback starts, and the
refresh-rate cache (017A slice 3) answering the first read after an idle spell with the value
cached when the window was attached, refreshing in the background (empty if that first query
failed). Presentation timing re-resolves the rate every 100 ms, so only the report was wrong.

Fix: `VideoPipeline` checks the pending report on every presentation pass and, while the rate
comes from the platform and a renderer rate source exists, waits up to 1 s for the renderer's
rate before reporting the platform one (`ShouldWaitForRendererRefreshRate`, tested in
`VideoCadenceReportTimingTests`). Host-render mode, with no presentation loop, reports at once
as before.

### ENG-017 - Software renderer drops frames at large output sizes

Fixed: 2026-10-18. Owner smoke passed the same day (`--software` fullscreen on an ultrawide,
with the secondary window maximized, for video and CD+G).

Reported: 2026-10-18 (owner smoke, `video-sw-4` capture). With `--software`, fullscreen
video on an ultrawide monitor dropped frames visibly, and more with the secondary window
open (ENG-016 renders each composition once per window).

Cause: `SoftwareCompositionRenderer` mapped and blended every output pixel generically: two
double divisions and a floor for the source pixel, checked offsets, and a divide-by-255
blend per channel, even for opaque video. The capture shows the presentation thread 78.5%
busy, 89% of it in `SoftwareCompositionRenderer.Render`. Measured in the sandbox (4 cores),
3440x1440 output from 1920x1080 video: 69 ms per frame (about 14 fps at most); with a
partial wash and CD+G, 120 ms.

Fix, with output byte-identical to before (checked during the change with a 400-scene
golden hash over random sizes, stretch modes, alpha values, wash, CD+G, subtitles and
transforms; not kept as a test):
- each layer maps its source columns once (`SoftwareStretch.MapColumns`) and each row once,
  instead of both per pixel;
- opaque source pixels are copied and transparent ones skipped, which is what the blend
  formulas give for alpha 255 and 0 (premultiplied: only when the colour is zero too); CD+G
  palette entries are always opaque, so they are written directly;
- the wash blends whole vectors with an exact integer divide by 255 (the same arithmetic
  for the remainder); clear and opaque wash are fills;
- outputs over 512K pixels are drawn in parallel row bands (each band writes only its own
  rows, so the result does not depend on scheduling), at most 8 bands of at least 128K
  pixels.

Result: 3440x1440 video 69 -> about 6 ms per frame; with wash and CD+G 120 -> about 9 ms.
Owner capture `video-sw-5` (same steps as `video-sw-4`: fullscreen ultrawide plus the
maximized secondary window): presentation thread 78.5% -> 11.9% busy; the drawing moved to
thread-pool bands (about 0.4 of a core in total). That capture also showed one band per
core (21 pool threads, each about 2% busy, about 1000 work items/s) costing about a fifth
of the drawing time in fork/join and wakeups, so bands are now capped at 8; owner smoke
after the cap: noticeably less stutter, only occasional. What remains with both windows full
size is accepted as the known limit of `--software` (a test/fallback mode); the trace points at WPF itself (the UI thread spends
about 2 s per minute in `WriteableBitmap.WritePixels`, and WPF's native render thread, which
the trace cannot sample, uploads two ultrawide bitmaps per frame).
Test: `SoftwareCompositionRendererTests.Render_LargeFrame_MatchesThePerPixelBlendFormulas`
checks every pixel of a banded 1200x864 render of background, wash, CD+G and premultiplied
subtitles against the per-pixel formulas. Pixels are processed as little-endian BGRA words;
a big-endian platform gets `UnsupportedFrame` instead of wrong colours.

Not changed: the base-visual transform paths still blend per pixel (only while a picture
adjustment is active); the WPF sink still copies each frame once and uploads it with
`WritePixels`; the whole output is cleared even under an opaque background (opacity is per
pixel, so it is not known up front); and presents that overlap (video and CD+G threads,
several windows) each fan out to up to one band per core on the thread pool.

### ENG-016 - Secondary window always blank in software mode

Fixed: 2026-10-18. Owner smoke passed the same day (with ENG-017: video and CD+G in the
secondary window under `--software`).

Reported: 2026-10-18 (owner smoke). With `--software`, the secondary/projector window stayed
blank for video and for CD+G.

Cause: two halves. `SecondaryPresentationWindow` always attached a native HWND payload, which
the software renderer cannot draw into; and `SoftwareRendererBackend` accepted only a
`Primary` managed frame sink and held a single surface. Present since the software renderer
was introduced (7f0eaf3, 2026-08-07; stage 024A).

Fix: the secondary window takes the primary surface's `WpfPresentationMode`; in
`WpfComposited` it hosts a `WpfCompositedSurfaceHost` and attaches its managed frame-sink
payload, at its pixel size (also refreshed on a DPI change between monitors). The software
backend keeps one target per attached surface id, each with its own reusable composition
buffer, and renders every composition once per target at the target's size. A presentation
mode change (only allowed with no media open) reopens an open secondary window in the new
mode, in the same place. Cost: one extra software composition per frame while a secondary
window is open. Test: `SoftwareRendererBackendTests.SecondaryWindow_ReceivesEachCompositionAtItsOwnSize`
(runs in both test projects; the portable project links the Windows-free test files).
`scripts/check-presentation-parity.ps1` now accepts either neutral payload for the secondary
window.

Not covered (unchanged behaviour): Direct3D11 in `WpfComposited` mode with a secondary window.
The D3D11 backend set's no-mixed-target policy makes the managed sink the only target, and the
bridge accepts a managed frame sink only for the primary, so the secondary stays blank there,
as it did with the HWND payload. Use `NativeHwndOverlay` for a secondary window with Direct3D11.

Review (local) decisions: with a primary attached, a software present succeeds only when the
primary presented (as `D3D11PresentationBackendSet.NotifyTargetsPresented` does), so a
secondary never hides a failed primary frame; a reopened secondary is placed before it shows.
Not changed: two targets of the same size are still rendered separately (rare: the windows
differ in size in practice); the Vulkan backend still accepts only a primary frame sink, but
no Avalonia/WinForms adapter opens a secondary window yet, so fold multi-target support into
ENG-014 when one does; `SecondaryPresentationWindow` gained an optional constructor parameter
(source-compatible; no package has shipped yet).

### ENG-015 - Secondary window blank or frozen with plain hardware video

Resolved: 2026-10-18. Owner smoke passed the same day (secondary on the same and on another monitor, through seeks).

Reported: 2026-10-18 (owner smoke). With hardware-decoded video and the secondary/projector
window open, the secondary stayed blank on another monitor, or played until the first seek
and then froze on one frame; `main` showed the same, so it predates this session's work.

Cause: `MediaPresentationCoordinator.PresentPrimaryVideoFrame` sends a hardware frame with no
subtitles and no picture adjustments to `IMediaRendererBackend.PresentNativeFrame`. Direct3D11
draws that straight into the primary target and presents only the primary; fan-out to
secondary targets happens only in `PresentComposition`. Frames reached the secondary only
while something forced composition (CD+G, subtitles, a base visual transform), which is why
the CD+G and transform checks passed. Present since the native branch was introduced
(cbca3a9, 2026-07-18).

Fix: while any non-primary surface is attached, hardware video is composed
(`RenderHardwareVideoCompositionFrame`, as for subtitles), which presents every target; with
only the primary attached the native fast path is unchanged. Cost: one extra blit and shader
draw per target, only while a secondary window is open. Test:
`MediaPresentationCoordinatorTests.HardwareVideoFrame_IsComposedWhileASecondaryWindowIsAttached`.
The secondary count is read under the coordinator lock that attach/detach update it under,
so no frame takes the native path after a secondary attached.

Review (local) decisions: the fix stays in Core rather than making Direct3D11's native path
fan out, since Core owns target fan-out and composition already presents every target; a
failed backend detach still drops the surface from the coordinator first (existing
behaviour); switching back to the native path leaves the last retained composition
background in place, as the subtitle path already does when subtitles end.

### ENG-013 - CdgPipeline duplicates the latest-wins seek worker

Resolved: 2026-10-17. Owner smoke passed 2026-10-18 (CD+G seek-bar drag: lyrics follow
the drag and settle where it stops).

Reported: 2026-10-17 (review of 015A slice 2). `CdgPipeline` had its own copy of the seek
worker, about 120 lines, without the pump's lingering-worker guard.

Fix:
- `VideoSeekPump` is renamed `SeekPump` (`ISeekPumpHost`) and takes its thread name and
  diagnostic source, so one implementation serves both pipelines. Thread names are
  unchanged (`VideoSeek`, `CdgSeek`).
- `CdgPipeline` implements the host: every preview is a full render (`SeekCore`); the
  settle that follows always targets the last preview within the change threshold, so it
  only ends the seek sequence instead of drawing the same frame twice. The 50 ms interval
  and the 25 ms target-change threshold are the same as before. Join timeout on close
  goes from 1 s to 2 s.
- Tests: `CdgPipelineSeekTests` (one render per target with a rebuild, clamping, refused
  before open).

### ENG-012 - "Composition present failed" warnings on audio-only seeks

Resolved: 2026-10-17 (owner smoke: "all seems good" after seeking an MP3 with no
background).

Reported: 2026-10-17 (owner smoke log, confirmed still present after the 035A fixes).
Seeking an audio-only MP3 logged `Composition present failed reason=current position
refreshed` as a warning on every seek, and `presentation started` once on load. Playback
was unaffected.

Fix:
- `RefreshCurrentPosition` no longer presents when the media has nothing visual (no video,
  image, CD+G, subtitles, or composition background). It used to send an empty frame on
  every seek, which Direct3D11 refused.
- The warning now quotes why the renderer refused: `IMediaRendererBackend` gains an
  optional `LastPresentFailureReason` (default null), which Direct3D11 fills from each of
  its failure points (no active presentation target, a layer that could not be prepared,
  the primary target draw, the swap chain present). Those reasons used to go only to
  `Debug.WriteLine`, which Release drops.

### ENG-011 - AV1 hardware decode falls back to software on some files

Resolved: 2026-10-17 (owner smoke: `Scream 7.mkv` logs `decoder av1 via d3d11va, metadata OBUs
removed`).

Reported: 2026-10-17 (owner smoke, FFmpeg 9). `Scream 7.mkv` logged "Hardware video decode
produced no usable startup frame; falling back to software"; other AV1 files decode on
the GPU. The log did not say why, because the decoder's reasons went only to
`Debug.WriteLine`, which Release builds drop.

Done so far:
- Every video open now logs `VideoDecoder: Decoding <codec> <profile> <bit depth> <size>
  (<pixel format>), decoder <name> via <device>` or `in software`.
- The fallback warning now names the stream and the reason: FFmpeg withdrew the GPU format
  after the GPU decoder failed to start (unsupported profile, bit depth, or size), never
  offered it, or the last FFmpeg error text.
- Fixed a latent case on the way: if no hardware device could be created, the native `av1`
  decoder (which cannot decode in software) was opened anyway and produced no video, with
  no fallback. The software decoder (`libdav1d`) is now used instead.

Owner's log with the new warning: `Stream: AV1 Main 8-bit 1920x1080 (yuv420p), decoder av1
in software (d3d11va configured but not used). Reason: the decoder never asked for an output
format (no decodable picture in the first packets); last FFmpeg error: Invalid data found
when processing input.` So a plain 8-bit 1080p stream, which the GPU supports: FFmpeg's
native `av1` decoder rejected the bitstream before it reached the sequence header, while
`libdav1d` decodes the same packets. The GPU is not the problem.

Since then the warning also quotes FFmpeg's own log lines for that decoder (up to six,
through `FfmpegLogCapture`, which installs one process-wide FFmpeg log callback and passes
everything else to FFmpeg's default logger).

FFmpeg's lines for `Scream 7.mkv`: `trailing_zero_bit out of range: 1, but must be in
[0,0]`, `Failed to read unit 1 (type 5): Invalid data`, `trailing_one_bit out of range`,
then `Missing reference frame needed for show_existing_frame`. Type 5 is a metadata OBU
(HDR10+, ITU-T T.35, timecode). The file's encoder wrote metadata OBUs with bad trailing
bits; FFmpeg's native `av1` decoder parses metadata strictly and drops the whole packet,
while `libdav1d` ignores them.

Fix: `Av1MetadataObuFilter` runs FFmpeg's `filter_units` bitstream filter
(`remove_types=5`) on packets for the native `av1` decoder. It splits packets into OBUs by
their size fields without parsing them, so the malformed bytes are never read. Nothing the
picture needs is lost. The decode-path log line ends in `metadata OBUs removed` when it is
active. A build without `filter_units` keeps the old behaviour (and the software fallback).
Tested natively (Linux, real FFmpeg 9) with hand-built AV1 packets carrying malformed
metadata.

### ENG-001 - Reset raced queued and in-flight control operations

Resolved: 2026-08-17

Public control admissions now share a lifecycle gate with `MediaControlPlayer.Reset`.
Reset waits behind already-admitted work, rejects new work while teardown is active,
rebuilds the player, and disposes the old operation queue only after the exclusive
turn completes. Caller-thread nested queue execution now runs inline instead of
deadlocking. Focused regression tests cover reset waiting for an admitted load and
nested synchronous queue execution.

Residual constraint: `Reset` is rejected from inside a serialized media control
operation or its inline event handler; hosts must schedule lifecycle replacement after
the callback returns. Background position-notifier handlers are not part of that
reentrancy guard.

### ENG-002 - Player diagnostics blocked media threads on the dispatcher

Resolved: 2026-08-17

The Player host now marshals diagnostic and error updates with guarded
`Dispatcher.BeginInvoke` calls. Background media callbacks no longer wait for the UI
thread, and queued updates are ignored after the view model is disposed.

### ENG-003 - Natural-end loop restart bypassed control serialization

Resolved: 2026-08-17

Natural-end handling now uses the same operation queue as host transport commands.
The engine callback queues end detection, public `MediaEnded` delivery is ordered
next, and loop completion is queued after delivery. A transport-generation token
cancels stale end work when Play, Pause, Stop, or Seek wins the race. Tests pin both
host cancellation from `MediaEnded` and an end callback arriving during Stop.

### ENG-004 - Pure binary and MIDI parsing coverage gaps

Resolved: 2026-08-17

Soft Karaoke `@T`/`@I` parsing now has a pure marker-text seam with focused title,
artist, credit, labeled-field, year, deduplication, and additional-information tests.
The DLS converter has malformed-input and successful minimal DLS-to-SF2 fixtures.
Those fixtures exposed and fixed the DLS `insh`, `rgnh`, `wlnk`, and `wsmp` field
layouts plus an invalid 64-bit RIFF payload-size write and omitted LIST type bytes in
the generated SF2 size declaration.

### ENG-005 - Presentation coordinator integration coverage

Resolved: 2026-08-17

A recording renderer backend and stub subtitle rasterizer now exercise the real
`MediaPresentationCoordinator` without GPU/native dependencies. Tests cover primary
surface attach/resize/live-resize/detach, retained subtitle refresh, frame-event
forwarding, and successful device-loss cleanup/reinitialization/surface recreation.

### ENG-006 - WPF presentation-mode callback threw through bindings

Resolved: 2026-08-17

`MediaSurface`, `BackgroundSurface`, and `KaraokeSurface` now coerce
`PresentationMode` to the applied value while media is open instead of throwing from
a dependency-property callback. The requested binding/base value remains deferred
across in-place replacement and is re-coerced after explicit close/release. An STA WPF
regression test pins that complete transition without native media dependencies.

### ENG-007 - Software backend contract no-ops needed clarification

Resolved: 2026-08-17

`SoftwareRendererBackend` now documents that presentation stretch is carried by each
Core composition frame and that no retained background resource exists to clear. The
no-op methods are intentional stateless renderer behavior, not missing implementation.

### ENG-009 - Settled video/CDG seek could miss its completion event

Resolved: 2026-08-17

Successful video/CDG seeks now immediately test the current position through
near-exact equality, completing no-op seeks without waiting for a signal. Real moves
still complete from video position or CDG invalidation events through the wider settle
tolerance, so a small asynchronous video seek cannot complete from its stale pre-seek
position. Stop and natural end clear pending completion state. Tests cover settle
success, no-op video/CDG, tolerance rejection, small-delta asynchronous settle,
latest-wins, and terminal clearing.

### ENG-010 - Dispose did not share reset admission serialization

Resolved: 2026-08-17

`MediaControlPlayer.Dispose` now waits behind admitted work and shares the reset
admission gate. Teardown runs in an exclusive queue turn, terminal event deliveries
drain before the queue closes, and the disposed flag is published afterward. Tests
cover an admitted load, concurrent reset followed by disposal of the rebuilt engine,
idempotency, and explicit rejection from an inline control event.

### ENG-008 - Stage and navigation documentation drift

Resolved: 2026-08-17

Stage 030A was archived after its manual smoke passed, active/completed roadmap links
were updated, and this engineering register was added.

## Current Validation Baseline

- Release build: 0 warnings/errors.
- Automated tests: 464 passed.
- Formatting, Core boundary, WPF boundary, and presentation-parity gates: passed.
