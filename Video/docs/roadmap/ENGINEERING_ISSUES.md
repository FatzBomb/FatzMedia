# Engineering Issues

Updated: 2026-10-31

This register tracks verified engineering follow-ups that are not product-feature
stages. Items remain here until fixed, deliberately accepted, or promoted into a
bounded roadmap stage.

## In Progress

### ENG-039 - October 2026 code audit, round 2

Reported: 2026-10-28. Plan: `updates/ENG-039/AuditPlan.md` (every finding, verified against a real
caller, with the owner's Windows smoke results from `scripts/smoke-audit.ps1`). Owner decisions
(2026-10-28): all section-0 recommendations accepted; 047A not pursued; fix in five PRs.

Smoke results that changed the plan:
- GPU reset (`dxcap -forcetdr`) never recovers on any renderer and logs no device loss: Direct3D11
  reports `DXGI_ERROR_DEVICE_REMOVED` as a Composition warning, the Vulkan path reports
  `ErrorOutOfDeviceMemory` after a 14.5 s stall, and a CD+G run froze the UI (batch 2).
- `DWM_TIMING_INFO` packing confirmed (320 bytes vs 292; the query always failed: ENG-018).
- Black flash on every resize step (Avalonia shared texture, WPF frame sink); 423 export log
  lines in 15 s. KAR lyrics allocate 234 MB/s. An MKV with text subtitles is read whole on open.
- Not reproduced (ranked lower): the background-video loop glitch, the lightweight-control
  unload freeze (171 ms worst). CD+G before the first preset is already transparent everywhere.

Batches:
1. **Core and player bugs: implemented, pending manual smoke** (`updates/ENG-039/Batch1.md`). Software renderer forces software
   decoding (ENG-037); sync `Load` with PlayOnLoad starts in queue order; diagnostic owner on sync
   control calls, Reset/Dispose, PrepareNext/Analyze, fades and the BASS metadata callback
   (ENG-025); FFmpeg audio fallback cancellable, refuses >4 GB before decoding, writes large
   conversions straight to a file; FFmpeg-only audio inside archives; background-video restart
   on its worker (never on BASS's thread, never freed while in use); position-notifier start
   race; audio trimmed to the seek target (no lead after a seek); a refused packet is resent;
   network decoder swap re-seeks the shared audio, no re-probe reconnects; `CdgPipeline`
   disposed; one normalization rule and a bounded gain cache; Avalonia test player close during
   a rebuild and `--second-screen` on rebuilds; test-player handler exceptions, drag-seek
   failures and the startup delay; karaoke `Source = null` releases the player.
2. **Windows/GPU bugs: implemented, pending manual smoke** (`updates/ENG-039/Batch2.md`): device loss probed and recovered on every renderer, video reopened after recovery, no UI wait on a stuck renderer call, DWM packing, resize flash, export log noise, retained background, WPF overlay. Deferred: the per-player audio device monitor.
3. **Dead code and public API: implemented** (`updates/ENG-039/Batch3.md`): `MediaControlPlayer.Controls`/`MediaControls`/`MediaFile` internal; `.Video/.Audio/.Cdg/.Subtitle`, `RunOnRenderThread`, the host-render-event mode and `SharpArchiveAdapter.IsArchive` removed; `ShowSecondaryOutput` family, `ICdgEngine.LoadPaired` and `MidiLyricExtractor` `[Obsolete]`; unused internals deleted.
4. Refactors (player controller, decode seam, duplicates): 4a **implemented, pending manual smoke** (`updates/ENG-039/Batch4a.md`): the three test players share `PlayerSetController`. 4b **implemented, pending manual smoke** (`updates/ENG-039/Batch4b.md`): decode-device seam, one decoder-replace helper, one session teardown, and the ENG-037 run-time software fallback. 4c **implemented, pending manual smoke** (`updates/ENG-039/Batch4c.md`): adapter, renderer and audio duplicates moved into shared helpers.
5. **Performance: implemented, pending manual smoke** (`updates/ENG-039/Batch5.md`): KAR raster
   caching and pooled subtitle buffers, embedded subtitles scanned after the open
   (`SubtitleTracksChanged`), D3D11 shader and video-processor view caches, local demux discard,
   allocation-free seek checks and display-locked passes, no-BASS bootstrap cached, archive audio
   held once, Vulkan plane views cached. P5-11 measured and skipped (about 2 KB/frame, D3D11 only;
   removing it breaks public API). Open: `LinuxNetworkVideoNativeTests` network-timing cases fail
   now and then under load (seen from the batch 3 base on); the D3D11 video processor's cached
   input views keep the last decoder texture array alive until a reset or a new array (after a
   switch to software decoding, until the next open).

## Open

These came out of the 2026-10-18 code audit (ENG-020). Each was verified by reading the code
but deferred: the fix is larger than the audit's bounded commits, needs a design decision, or
needs Windows to verify.

### ENG-053 - WinForms: other players stutter when one goes full screen

Reported: 2026-11-05, owner smoke rerun (`smoke-backlog.ps1` test 10, WinForms test player, 2-4 players,
RTX 5090): when one player switches to full screen, the others stutter briefly. Not seen in WPF or
Avalonia. Likely the layout change (the others hidden, the panel resized, the form made borderless)
resizing every surface's swap chain on the UI thread in one pass. Minor (test host); check whether hiding
the other panels before the resize, or suspending layout around the switch, removes it.

### ENG-052 - Embedded subtitle scan reads a large MKV twice

Reported: 2026-11-05, owner smoke (`smoke-audit.ps1` test 10): a 3,090 MB MKV with text subtitles
showed its picture at once (the scan runs after the open, ENG-039 P5-1), but the process read 6,672 MB
within 5 s: the whole file twice. On a laptop disk or a network share that competes with playback. The
scan should read the file once at most (one pass for all text tracks, sharing the open demuxer or
reading only the subtitle packets' clusters), and at a lower priority than playback.

### ENG-051 - Background video hitches at the song's loop point

Reported: 2026-11-05, owner smoke (`smoke-audit.ps1` test 6, WPF, BASS): a song looping with a same-name
background video: the video hitches at each loop point, with and without `--trim-silence` (the earlier
audit could not reproduce it). The background restarts from frame 1 at the wrap as designed; the restart
itself is visible. Look at pre-seeking the background to its start ahead of the wrap, as the primary video
loop does (011A).

### ENG-050 - Vulkan does not recover from a GPU reset on Windows

Reported: 2026-11-05, owner smoke (`smoke-audit.ps1` test 3d, Avalonia, Vulkan, RTX 5090): after
`dxcap -forcetdr` the device loss is detected (`vkWaitForFences ... ErrorDeviceLost`), the presentation
call blocks 13.2 s, and every recovery attempt fails with `vkCreateInstance failed: ErrorIncompatibleDriver`
(6 attempts, then every 10 s): the picture stays black until the player restarts; sound continues. The
driver's Vulkan state in the process does not come back, so retrying cannot fix it. Proposed: when Vulkan
recovery fails, fall back through the renderer chain (036A) to Direct3D11 on Windows (frame sink or shared
texture), with a Warning; also cut the 13 s stall. Direct3D11 recovered in 1 attempt in the same run.
ENG-038's GPU-reset step is covered by this.

Fix (2026-11-05, pending the owner's GPU-reset check): when every recovery attempt fails, the coordinator
raises `DeviceRecoveryFailed` once (not on the 10 s retries) and the player raises `MediaError` with the
new `MediaErrorCode.RendererDeviceLost` (public, additive). The test players' window then rebuilds its
players on the next renderer (`PlayerRendererRules.FallbackAfterDeviceLoss`: Vulkan to Direct3D11 on
Windows, a GPU Direct3D11 to WARP, WARP to software; elsewhere to software), once per build; loaded files
are not kept, as with any renderer change. A deeper Vulkan clean-up was not tried: the decoder had already
left the device and `vkCreateInstance` itself fails, so the driver's state in the process is gone. Still
open: the 13 s stall before the failure is known. Check: `smoke-audit.ps1 -Steps 3` (3d).

Owner check (2026-11-05, Avalonia, Vulkan, RTX 5090): the error was raised (`RendererDeviceLost` in the
Log, sound continued) but the window did not switch: the slots forward their view model's event, so the
sender was the view model and the controller's "is this one of my slots" check dropped it (the unit test
raised it with the slot as sender). Fixed: the controller no longer checks the sender; the test now raises
it as the real slots do. Also seen: after the reset, Vulkan stays unusable for the rest of the process
(`vkCreateInstance ... ErrorIncompatibleDriver` even after picking Vulkan again by hand); a new process
works. Direct3D11, picked by hand, showed the picture at once.
Rechecked with the fix (2026-11-05, fresh process, `dxcap -forcetdr`): pass. The window switched to
Direct3D11 by itself and recreated the player (surface #2 on the RTX 5090, shared GPU image); the file
played again on Direct3D11. Still open: the 14 s stall before the first recovery attempt.

### ENG-049 - Vulkan CD+G nearest sampling differs from Software at odd scales on NVIDIA

Reported: 2026-11-05, owner smoke (Vulkan tests on an RTX 5090): `CdgOnly_MatchesSoftwareExactly`
at 333x250 matched 99.1% of pixels exactly; some edge pixels take the neighbouring CD+G pixel (channel
delta 255). lavapipe matches exactly, so the GPU's nearest-texel rounding at texel boundaries differs
from the Software renderer's integer mapping. Not visible in practice. Fix: compute the source texel in
the shader with the Software renderer's integer formula (`texelFetch`) instead of a nearest sampler.

### ENG-048 - FatzAudio loudness cache never written on a fresh machine (fixed)

Reported: 2026-11-05, owner smoke (`updates/Smoke_2026-11-05`): the cache folder stayed empty and a
new run scanned again. FatzAudio creates only the last level of `cache_directory`; our default
`%LOCALAPPDATA%\FatzBomb.MediaControl\AudioCache` needs two. Fixed: `FatzAudioEngine` creates the folder
(a warning and no cache when it cannot). The engine test uses a two-level folder (fails without the fix).

### ENG-047 - Clicks and pops on loop wraps and seeks

Reported: 2026-11-05, owner smoke (loop and seek checks; the default BASS engine). Engine and files not
yet narrowed down: compare BASS and `--audio=fatz` on the same file, and with fades on and off.

### ENG-046 - WPF frame-sink (composited) resize lags behind the drag

Reported: 2026-11-05, owner smoke (`smoke-audit.ps1` test 4b: WPF with `--surface=framesink`): the video
catches up after the window, no black flashes. On this path each frame is read back and copied into a
`WriteableBitmap` on the UI thread at the surface's size, so a resize storm reallocates and copies at
every size: the cost 047A slice 1 removes. Re-check after 047A; until then compare with the native window.

### ENG-045 - Embedded subtitle choice returns to the default track

Reported: 2026-11-05, owner smoke (an MKV with embedded subtitles, WPF): choosing a track or Off keeps
reverting to the default embedded track (ENG-039 P5-1 expected the choice to stick, also when made
before the scan finishes).

Engine checked (2026-11-05): with a real MKV of two embedded tracks, a chosen track and Off stay chosen
after the scan, over time and across a seek, and Off chosen right after an open survives the scan
(`LinuxSubtitleSelectionNativeTests`). So the engine keeps the choice; open questions for the owner: the
exact steps (WPF picker, or a reopen: a reopen starts again from the file's default track by design).

Closed 2026-11-05, not a bug: the owner changed the track or chose Off, then reloaded the file, and the
reload chose the first embedded track again, as designed. Found while checking: the default choice is
the first embedded track, not the track the file marks as default, and "forced" tracks (shown even with
subtitles off) are not recognised; both are possible follow-ups, not decided.

### ENG-044 - F11 full screen picks player 1 and sometimes does nothing

Reported: 2026-11-05, owner smoke (ENG-039 batch 4a, WPF and Avalonia): F11 always makes player 1 full
screen instead of the focused player, and sometimes does nothing; double-click works as expected.
WinForms passed.

Cause and fix (2026-11-05, pending the owner's check): the picture's viewport cannot take keyboard focus,
and the native video window passes clicks through, so a click on a picture never moved focus and F11 fell
back to player 1; and the WPF window handled F11 in `KeyDown`, after child controls, so a control that
handled the key swallowed it. Now a click on the picture focuses that player (WPF `PlayerView`, Avalonia
`PlayerPanel`), and the WPF window handles F11 in `PreviewKeyDown` (Avalonia already handled it on the
tunnel). Check: two players, click player 2's picture, F11: player 2 goes full screen; F11 again restores;
repeat with focus in a drop-down or the seek bar.

### ENG-043 - Audio formats that fail on both engines, and a BASS crash on a WMA

Reported: 2026-11-03, owner format sweep during the 037A slice 2 smoke (Windows, WPF player, the
same files on BASS and FatzAudio). Not caused by slice 2: each fails the same way on BASS.
- **ALAC, M4R, audio-only MP4, TS:** nothing played; the player stayed responsive. Owner log:
  `sample.alac` and `sample.m4r` were "Rejected (Unsupported)" (the extensions were not registered);
  `sample.mp4` and `sample.ts` failed with OpenFailed (registered as video, so the video pipeline
  opened them, found no picture and failed). Fixed (pending Windows check): `.alac` and `.m4r` join
  the FFmpeg audio extensions (FatzAudio decodes both natively; BASS plays them through the FFmpeg
  conversion), and a local video file with no video stream (cover art does not count) plays its
  audio through the soundtrack path, as Audio mode does, with an Info line saying so. Verified on
  Linux with real FFmpeg and FatzAudio on generated AAC-in-MP4, AAC-in-TS, cover-art-only MP4,
  M4R and ALAC files.
  Linux run 2026-11-05 (`LinuxFormatMatrixNativeTests`, BASS and FatzAudio engines): `.alac`-family M4A
  and CAF, `.m4r`, audio-only MP4 and TS and a cover-art-only MP4 all load and play on both engines;
  Windows check still pending (`smoke-backlog.ps1` test 1).
- **WMA on BASS:** the player froze and closed before its log appeared (FatzAudio played the
  file). Owner decision 2026-11-03: not chased, since FatzAudio replaces BASS.
- **Images opened on their own:** an image loaded after any non-image media stayed hidden. Cause:
  the replaced item's `MediaStopped` reached the surface's auto-hide after the new `MediaLoaded`.
  Fixed in PR 52, owner verified on Windows 2026-11-03.

### ENG-040 - Library gaps found while writing the user guide

Reported: 2026-10-31, while writing `docs/site` (the DocFX user guide). Each was found by reading
the code for the guide; only the first (text on Software and Vulkan) is fixed. The guide documents
the current behaviour.

**Behaviour a buyer will hit:**
- ~~**Text only on Direct3D11.**~~ Resolved 2026-11-01, pending Windows smoke: the Software
  and Vulkan renderers draw subtitles, MIDI/KAR lyrics, countdowns and title cards with
  `SkiaSubtitleRasterizer` (new package `FatzBomb.MediaControl.Rendering.Text`, SkiaSharp
  3.119.4); Vulkan gained a subtitle overlay pipeline. Linux verified with lavapipe and the
  software renderer; Windows Software/Vulkan hosts (libSkiaSharp.dll loading, Segoe UI) still to
  smoke. Known differences from DirectWrite: no shaping (ligatures, complex scripts), system
  font fallback.
- **WPF + Software/Vulkan needs `PresentationMode="WpfComposited"`**; WPF does not fall back to a
  frame sink by itself (WinForms and Avalonia do). The Software renderer also needs
  `VideoDecodingMethod.Software`; nothing enforces it.
- **Silence trim only from the second play:** the trim window is used only when a cached
  analysis exists at open, so a first play is untrimmed (`PrepareNextAsync`/`AnalyzeAsync`
  fill the cache). Owner decision 2026-11-02: silence detection belongs in the audio engine;
  requested from FatzAudio (request 9 in `docs/fatzaudio/2026-11-01_MediaControl_Request_SurfaceTiming.md`),
  and the FatzAudio engine (037A slice 2) trims on the first play with it. Implemented
  2026-11-03 for the FatzAudio engine (`--audio=fatz`), pending the owner's smoke
  (`updates/037A_2`); the BASS path keeps today's behaviour.
- **A found sidecar background beats an explicit one** (`ResolveCoreBackground` returns
  `sidecar ?? explicit`); `ignoreSidecar` only affects backgrounds, not `.cdg` or subtitle pairing.
- **Sidecar probing is case-sensitive on Linux** (`Song.CDG` next to `Song.mp3` is not found);
  an archive whose audio and `.cdg` names differ plays audio only, silently.
- **Lightweight surfaces cannot be configured** (karaoke/subtitle style, soundfont) and never load
  a sidecar background.
- **MIDI soundfont is process-wide** (`BassMidi.DefaultFont`): players with different
  `MidiSoundfontPath` override each other. The MIDI lyric track is found by display name.
- **Relative native paths resolve against the working directory** and a missing folder throws;
  the AppBase-relative branch of the Windows bootstrap is unreachable. `DLLPath` is read only by
  the lightweight surfaces and `AudioOutputDevices`.

**Missing public API:** no stream/byte-array `MediaInputSource` (planned: 053A, owner 2026-11-05); no mute; no equalizer presets;
no `DisposeAsync`; CD+G background override colour (`ICdgEngine.BackgroundOverrideArgb`) and
`CdgVisuals` not on `IMediaControlSession`; no display refresh-rate override on the player
(`IMediaEngine.SetDisplayRefreshRate` only; Linux has `LinuxPlatformOptions`); `DeviceRecovered`
not on the player (hosts see recovery only in diagnostics); `AcceptedMediaTypes` unused publicly.

**Names and docs:** "crossfade" (`AudioCrossfadeOptions`) never overlaps (fade out, gap, fade
in); `MediaEffects.NormalizationGain` is documented as dB but is a linear multiplier;
`MediaErrorCode.AudioDeviceLost` and `AudioDeviceChangeKind.Unrecoverable` are never raised;
`SubtitleTracksChanged` is not raised when the scan finds no tracks; `EnableLogging` /
`EnableVerboseLogging` write mostly via `Debug.WriteLine` (dropped in Release builds); stage and
ENG ids appear in public XML docs (`NetworkTlsPolicy`, `NetworkReadAheadPolicy`,
`MediaSurfacePresentationPath`, `MediaPlayerConfiguration.Mode`, the WPF `ShowSecondaryOutput`
obsolete message) and so in the generated API reference; WPF `WpfPresentationMode`,
`PresentationMode`, `OverlayContent`, `OverlayHitTestVisible` have no XML docs; the Vulkan and
Linux READMEs are stale (subtitle frames "rejected", hardware decode "a later slice").

**Found in the review:** `default(MediaPlayerConfiguration)` zeroes every field
(`AudioOutputDeviceIndex = 0` is BASS's no-sound device, presentation lead 0);
`VideoDecodingMethod.Hardware` behaves like `Auto`; `IMediaControlSession` has the volume, pitch,
tempo and normalization change events but not the members, so interface-only hosts (e.g. via
`KaraokeSurface.Session`) cannot set them; value-changed events fire on the setter's thread while
the rest come from engine threads.

**Found in the 026B Linux smoke (2026-11-05):** on Linux no `AudioDeviceChanged` is raised when the
output device changes: PulseAudio and PipeWire move the stream themselves (playback continues correctly)
and BASS keeps seeing the same ALSA default device. A Linux event would need the sound server's own
notifications (PulseAudio/PipeWire), outside BASS and FatzAudio.

**Not a feature yet:** audio CD and CD+G disc playback (only `basscd` registering `.cda`; idea
`032A`); DASH (only `.m3u8` is detected).

### ENG-021 - BASS output latency and device recovery are largely inert

Reported: 2026-10-18 (audit). Candidate for the FatzAudio stage (037A), which replaces this path.
- BASS is initialised without `DeviceInitFlags.Latency`, so `BASS_INFO.latency` is 0 and
  output-latency compensation (and the USB/Bluetooth heuristic) does nothing.
- ~~The "device recovery" in `AudioEngine.Play()` re-inits a device that is still initialised;
  `Bass.Free` is never called, so it cannot recover a lost device.~~ Replaced by ENG-032's
  output watchdog (`BASS_Start`, then `BASS_DEVICE_REINIT`); switching to a different device
  still needs the player-level Reset.
- `VideoAudioEngine` publishes position snapshots that mix raw and latency-corrected values.

### ENG-026 - Renderer robustness and per-frame costs (Direct3D11, Vulkan)

Reported: 2026-10-18 (audit; perf items not measured). Partly resolved: 2026-10-24, pending
manual smoke; the items below "Still open" need a measurement on Windows first.

Resolved (each re-checked against the code first):
- **D3D11 failure logs.** Failures a user would need now go through `DiagnosticRouter` with a
  per-site `RendererDiagnosticLatch` (Core, `Core/Graphics/Rendering`): one report per
  episode, re-armed on the next success. Covered: `ResizeBuffers` and resize failures (native
  HWND, DirectComposition base and overlay), DirectComposition `Commit`, root transform (removed in ENG-039 R7: it was always identity) and
  overlay present/clear failures, frame-sink readback and creation failures, shared-texture
  present failures and stuck-mutex re-exports (Info), composition layer upload/allocation
  failures, per-target draw failures, the DirectComposition overlay draw, video processor blit
  and hardware-frame failures, and the black clear of a new target. Raised once, unlatched:
  the composition pipeline failure (it stops retrying), "video processor unavailable" (once per
  device) and the legacy Discard swap-chain fallback (once per swap chain). The remaining
  `Debug.WriteLine` calls are verbose traces (created/resized, periodic stats, skip reasons
  already returned as `LastPresentFailureReason`).
- **Secondary window resize** queues `ResizeBuffers` for the next frame boundary, as the primary
  does (`D3D11PresentationBackendSet.ApplyPendingSecondaryResizes`, applied with the primary's
  pending resize). Only when the resize throws is the swap chain re-created, as before.
- **Per-frame allocations (D3D11).** Each backend owns a `D3D11TargetViewCache` for its target
  texture, released before the texture is disposed or its swap chain resized; snapshots carry
  it to the compositor, and the video processor's pre-blit clear has one too. The backend set
  keeps secondaries in sorted lists and fills a caller's list (`CollectActiveTargetBackends`)
  instead of `OrderBy`/iterators; the surface controller reuses its snapshot lists; the present
  loop, `OwnsTexture`, `HasMultipleActiveTargets`, `CompositionTargetRegistry` and
  `CompositionTargetPlan.ShouldPresentTarget` have no LINQ or capturing lambdas. DirectComposition
  no longer calls `GetBuffer(0)` after every present: buffer 0 of a Direct3D 11 flip-model chain
  always names the current back buffer (the native HWND backend already relied on this).
- **Managed frame sink** keeps its readback buffer through re-creation (Core
  `RetainedPixelBuffer`: grown as needed, replaced only below a quarter of its size); the
  primary's is released when all surfaces are cleaned up, a secondary's when it is removed.
- **Vulkan readback** prefers host-cached memory (cached+coherent, then cached with an
  invalidate before the read, then coherent). lavapipe's only type is cached and coherent.
- **Vulkan background** is uploaded once per change, keyed by the frame's `BackgroundRevision`,
  its pixel buffer and the source image; other targets and repeated frames reuse it. Revision
  -1 (unknown) always uploads.
- **Post-process targets** of targets that are gone are released on the next composition
  (`TargetResourcePruning.RetainOnly`).

Tests: Portable `RendererResourcePolicyTests` (latch, pruning, buffer policy, registry);
Vulkan `VulkanResourceReuseTests` (memory-type choice, cached readback on lavapipe, one upload
for several targets, re-upload on new revision, buffer, size or unknown revision, two frame
sinks sharing one upload); Windows CI `D3D11RendererCostTests` on WARP (view reuse until the
target is re-created, readback buffer through resize churn, post-process target released after
a secondary detaches). On the Linux Avalonia player (Vulkan on lavapipe, 1080p H.264, software
decode) CPU and fps did not change measurably: 30 fps, 21-24 % of 4 cores with one screen,
31-32 % with a second screen, before and after; lavapipe's upload is a memcpy beside software
decoding and rasterisation.

Still open (measure on Windows before changing):
- The managed frame sink still re-creates its target and staging textures at every size of a
  live resize. Measure: WPF composited mode, drag-resize a 1080p video window, watch
  frame drops and the composition timings (`EnablePerformanceLogging`). A fix would render into a sub-rectangle of a
  larger texture.
- Two targets of the same size are composed separately (D3D11 and Vulkan). Measure GPU time per
  frame with two same-size screens (PIX or the composition diagnostics) before sharing one
  composition.
- Smaller per-frame allocations remain: the compositor's drawn-target list, the target plan's
  array, the registry capture's `Select`, and the services' present callback. Measure the gen0
  rate with `dotnet-counters` during 60 fps playback before chasing them.
- The non-coherent cached readback branch is unit-tested only. Measure readback time on a
  discrete GPU (NVIDIA, AMD) with Vulkan frame sinks, before and after.

Manual smoke (Windows, native DLLs in place):
1. WPF player, native HWND mode, open the projector window and drag-resize it for a few
   seconds: it keeps playing with no black flashes; the log shows no `D3D11` warnings.
2. DirectComposition mode with a CD+G file (base and overlay layers): plays, resizes, pauses
   and seeks as before; CPU no higher than before.
3. WPF composited (frame-sink) mode: drag-resize the window; playback continues.
4. Avalonia player, Direct3D11, `--second-screen`: both screens play, close and reopen the
   second screen a few times with a whole-composition video effect on; memory does not grow.
5. Avalonia player, `--renderer=vulkan` on a discrete GPU with `--surface=framesink`: plays;
   note CPU against the previous build.

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
- **Memory preset with repeat != 0 never clears pixels** - **Fixed 2026-10-23, pending manual
  smoke with a CD+G that clears with repeat packs.** A repeat pack now clears unless the previous
  pack was a Memory Preset of the same colour (then it is skipped and reports unchanged). The
  previous-preset colour is part of the replay checkpoint, restored on seek, and cleared on
  reset/load. Verified by tests.
- **Pack timing truncates a double** - **Fixed 2026-10-23, pending manual smoke.**
  `CdgEngine.PacketCountAt` counts floor(position x 300) packs in integer nanoseconds (same
  meaning of position as before: pack n applies at (n + 1) / 300 s), so exact boundaries no
  longer land one pack off. `RenderPipeline` uses the same helper. FatzAudio's ceil rule would
  shift every position by one pack and was not adopted. Verified by tests.
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

### ENG-034 - Network video follow-ups (039A)

Reported: 2026-10-24 (039A slice 1). Deferred findings from network video; none blocks the slice.
- **HTTPS certificates are not verified.** Resolved 2026-10-25, pending Windows smoke.
  Finding: FFmpeg 9 already defaults `tls_verify` to on (FFmpeg 8 defaulted it off), but its HLS
  and DASH demuxers open segments with a fixed option subset, so a host's `tls_verify`/`ca_file`
  never reached segments. The engine now sets `tls_verify=1` explicitly on every https open and,
  through a wrapped `AVFormatContext.io_open` (`FfmpegTlsIoOpen`), on every nested playlist,
  segment and key open. Hosts get `MediaPlayerConfiguration.NetworkTls` (`NetworkTlsPolicy`:
  `AllowInvalidCertificates`, default false; `CertificateAuthorityFile`, a PEM passed as
  `ca_file`). A rejected certificate fails the open with "certificate verification failed for
  <host>: <FFmpeg TLS text> (<FFmpeg error>)" and is not retried. A build that cannot verify as
  configured (OpenSSL on Windows or mbedTLS without a CA file; Schannel given a CA file) gets a
  Warning diagnostic before connecting; verification is never turned off for it. Linux BtbN
  OpenSSL: default roots from `/etc/ssl` (`SSL_CERT_FILE`/`SSL_CERT_DIR` honoured), host name
  checked. Recommended Windows build (TqkLibrary LGPL3 9.0.2): Schannel, Windows store. BASS
  (014A audio) exposes no certificate setting and does not verify (owner smoke); since 039A.3
  https audio opens through FFmpeg too (below). See `docs/NATIVE_DEPENDENCIES.md` for the backend
  table and the Windows checks.
- **https network audio was not verified (BASS).** Resolved 2026-10-26 (039A.3), pending Windows
  smoke. https and HLS audio open through FFmpeg's network path into the soundtrack engine
  (`NetworkAudioPolicy`; FFmpeg decode into the BASS output), now attached in Full mode too:
  `NetworkTlsPolicy`, retries, cancellation, the read-ahead's buffering, ICY and Ogg titles timed
  to playback. Plain http stays on BASS. See `docs/roadmap/updates/039A_3/StageUpdate.md`.
- **Multi-variant HLS downloaded every variant.** Resolved 2026-10-25, pending Windows smoke.
  Owner's smoke (`https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`, up to 1080p): long waits
  after a seek and occasional stutter. Confirmed cause: FFmpeg's HLS (and DASH) demuxer fetches
  segments for every variant whose streams are not discarded, and the picture cursor
  (`SharedDemuxer`, not owned by `VideoDecoder`) discarded nothing. Fix: for network sources the
  picture cursor plays one video rendition chosen by `NetworkVariantPolicy` (the highest
  advertised bandwidth at or below 1080p; when all are taller, the smallest; an unknown height
  counts as within the cap; the display size is not known at open) and discards every stream no
  consumer registered (`SharedDemuxer.DiscardUnregisteredNetworkStreams`). The audio cursor picks
  the audio of the same variant (`av_find_best_stream` with the chosen video as the related
  stream, so its program; a shared `EXT-X-MEDIA` rendition belongs to each program that uses it)
  and already discarded the rest. Local files are unchanged (FFmpeg's best streams, nothing
  discarded on the shared cursor; embedded subtitles use their own demuxer). Measured on a local
  3-variant master playlist (480p/720p/1080p, 2 s segments, 150 ms server delay per segment):
  segment requests after the open went from every variant (playback 2/2/2, two seeks 8/8/8) to
  the 1080p variant only (2, 8); seek latency 916-929 ms before, 307-325 ms after. The open's
  probe still reads the first segments of every variant (2 each), which `avformat_find_stream_info`
  needs to describe them. No adaptive switching: the variant stays fixed for the session.
- **Two connections per network video with sound.** Resolved 2026-10-25 (039A slice 2), pending
  Windows smoke with BASS. As for local video, `VideoAudioEngine` read on its own cursor, so a
  progressive file or an HLS VOD was downloaded twice, and a live HLS playlist could start the two
  connections one segment apart. Network video's audio now shares the picture's `SharedDemuxer`
  and takes its packets from the read-ahead (`NetworkReadAhead`): one connection, every segment
  fetched once (demuxer-level test; playing the shared audio needs BASS).
- **Pause and Stop during a stall wait for the read.** Resolved 2026-10-25 (039A slice 2). The
  read-ahead's reader thread owns the network I/O; the decode and presentation loops never wait
  for packets, a seek's waits give up when Pause or Stop is queued, and network Stop waits at
  most 0.5 s for its first frame. Measured with every segment hung for 20 s: Pause 2-11 ms, Stop
  0.4-0.5 s, Close 30-100 ms.
- **A network seek downloads its target twice.** Resolved 2026-10-25 (039A slice 2). A lone
  network seek settles on its exact preview (`VideoPipeline.CanSettleOnPreview`) instead of
  seeking again, and the seek's decode budget no longer counts the wait for the download (which
  made the preview give up and the settle fetch the segment again). Local seeks are unchanged.
  Measured (150 ms per segment): the target segment is fetched once per seek instead of twice.
- **Live progressive streams are not reconnected mid-stream.** FFmpeg reconnects a streamed (no
  range) source from byte 0, so `reconnect_streamed` stays off; a dropped live MPEG-TS-over-HTTP
  stream ends. HLS live recovers through its playlist reloads. Left (2026-10-26): it would need
  FFmpeg's option set per source after the open (only a live source may restart at byte 0) and
  checking FFmpeg's reconnect conditions; BASS does not reconnect live radio either, so https
  radio through FFmpeg keeps 014A's behaviour.
- **A moov-at-end MP4 needs a range-capable server.** Resolved 2026-10-26 (039A.3), pending
  Windows smoke: a non-seekable MP4 must yield its first packet during the open (kept for the
  first read); otherwise the open fails, not retried, with "this MP4 keeps its index (moov) after
  the media data and the server does not support byte ranges, so it cannot be streamed; serve it
  from a server with range support, or remux it with -movflags +faststart". Faststart MP4s, MKV
  and TS stream fine from such servers.
- **Audio-only HLS does not play.** Resolved 2026-10-26 (039A.3), pending Windows smoke: an
  `.m3u8` that connects with no playable video plays as audio through the soundtrack engine in
  Full mode (one more connect), and the Audio hint on an `.m3u8` opens through FFmpeg instead of
  BASS. Audio mode already played it.
- **Network soundtrack buffering (Audio mode) is coarse.** Resolved 2026-10-25 (039A slice 2),
  pending Windows smoke: the soundtrack's own cursor has a read-ahead too, and its buffering comes
  from it (starved for 0.5 s starts, the resume threshold ends) like network video's.
- **Read-ahead follow-ups (039A slice 2).**
  - A forward seek is served from the buffer only when the sound has not already taken the
    target's packets. The audio consumer runs up to about 4 s ahead of the picture (the BASS push
    buffer), so with BASS a forward seek shorter than that goes to the network. Keeping a short
    tail of taken audio packets would cover it. Left (2026-10-26): costs one extra request, and
    sizing the tail needs BASS measurements on Windows.
  - Buffering is still an event only, as in slice 1: the clock is not held while the buffer
    refills. Resolved 2026-10-26 for picture-only playback (see the reconnect follow-up below);
    with sound, BASS runs dry and the position waits naturally.
  - The starvation timer restarts with every packet that arrives, so a trickle slower than
    playback but faster than one packet per 0.5 s stutters without raising buffering. Left
    (2026-10-26): a stricter rule (starved until the queue has a lead) would flag a live stream
    that plays at its edge; needs real slow-network samples first.
  - A lone network seek now decodes to its exact frame before the pump interval and the settle:
    on localhost (150 ms per segment) a seek outside the buffer takes 370-440 ms against
    305-318 ms before, while downloading half as much. A remote segment download dominates
    either way. Accepted (a measurement, not a defect).
  - Network Stop shows the first frame only if it arrives within 0.5 s; otherwise the next Play
    or render shows it (by design). Audio-mode Play still prebuffers on the caller's thread and
    can wait for a stalled download: resolved 2026-10-26 (039A.3), a network soundtrack's Open
    and Play wait at most 0.5 s there; the pump fills the rest.

Code review 2026-10-25 (batch PR):
- `NetworkTlsRules.IsCertificateFailure` treats any `[tls @` log line mentioning "certificate" or
  "verify" as a certificate rejection, so an unrelated TLS warning followed by a transient failure
  is reported as a certificate failure and not retried. Tighten it once each backend's real
  messages are captured (Schannel on Windows, OpenSSL here). Left: needs Schannel's messages.
- The BASS network-audio retry loop (`AudioEngine`) duplicates `NetworkOpenRetry.Run`; the two
  differ (audio always waits the delay). Fold audio into the shared helper when 037A touches it.
  Left: belongs to 037A (https audio no longer uses it).
- Reset leaves the previous `_loadLifetime` CancellationTokenSource undisposed (one per Reset);
  disposing it on the timeout path is unsafe while the running open may still wait on it.
  Resolved 2026-10-26: `ResetCore` disposes it once the new graph is built (the operation that
  used it has returned and the old graph is torn down); the give-up path still leaves it.

Read-ahead code review (2026-10-25):
- A stream whose track ends before the source does (audio 30 s shorter than video) counts as
  starved until the reader reaches the end: the reader ignores the duration cap (up to the 2x byte
  limit) and, after a seek into that tail, buffering never ends. Resolved 2026-10-26, pending
  Windows smoke: a stream registered with its own end (`AVStream.duration`) gets its end marker
  once the reader is 2 s past that end (`ReadAheadBuffer.StreamEndToleranceSeconds`); a packet
  of the stream itself (the next lap) re-arms it. Streams without a known duration (TS, HLS) end
  with the source as before.
- Picture and sound share one data-arrived event, so one consumer's reset can delay the other's
  wake-up by up to one 50 ms wait slice. Resolved 2026-10-26: one event per registered stream;
  abort, a superseded wait, the end and a stream end wake all.
- If the audio engine fails after re-enabling its stream, that stream is not discarded again, so
  HLS/DASH keep downloading (and dropping) its segments for the session. Resolved 2026-10-26:
  `SharedDemuxer.UnregisterAudioStream` discards it again, applied by the reader between its reads
  (directly when no reader runs).

Owner smoke (2026-10-25):
- An MP4 with its index (`moov`) at the end, served without byte ranges (for example
  `python -m http.server`), cannot play. Resolved 2026-10-26 (039A.3), pending Windows smoke:
  see "A moov-at-end MP4 needs a range-capable server" above.

Reconnect follow-up (2026-10-26):
- Buffering is an event only: the clock is not held while the picture waits. Resolved
  2026-10-26 (039A.3), pending Windows smoke: picture-only network video (no sound, Background
  mode) holds the playback clock while the picture has no frame and its queue has been empty for
  250 ms, and releases it at the resume threshold (`NetworkClockHold`, `HoldableClock`; every
  transport change ends a hold). With sound BASS waits by itself (check on Windows).
- After a reconnect, the end of a progressive file read as a new drop (found with the clock
  hold): FFmpeg keeps the dropped connection's I/O error on the `AVIOContext` and returns it in
  place of the end. Resolved 2026-10-26: a successful network seek clears it.
- When playback ends while buffering (the clock ran out during an outage), no
  `Buffering=False` follows: the monitor stops sampling once playback stops. Resolved 2026-10-26:
  the sample carries `PlaybackEnded`, which ends a spell in progress.
- An HLS outage that ends while FFmpeg is still skipping segments makes a gap (the skipped
  segments) instead of an end, so the reader does not reconnect for them. Left: decided inside
  FFmpeg's HLS demuxer (`seg_max_retry`); would need segment-level reconnects.
- `python -m http.server`-style servers (no ranges) are never reconnected: only seekable sources.
  Left: a reconnect would have to reopen the source and re-download it from byte 0.
- The reconnect window is checked between attempts, so a reconnect seek blocked in a connect can
  overrun `ReconnectFor` by up to the connect timeout (15 s by default). Resolved 2026-10-26,
  pending Windows smoke: each reconnect seek, and every read while a reconnect is in progress, is
  bounded by what is left of the window (measured: ends 3.2 s after the drop with a 2 s window and
  a hung server, before at least 15 s).

HTTPS-audio code review (2026-10-26), not fixed:
- The open-time probe of a non-seekable MP4 treats any quick read failure (a connection reset
  right after the header) as "needs byte ranges or faststart", so that open is not retried.
  Telling it from a network error needs a check against FFmpeg's behaviour (e.g. a network error
  when `pb->error < 0` or the deadline passed).
- An Ogg file with a video track before its audio can announce the video track's comments as the
  first title (stream selection has not discarded it yet).
- A seek that fails on a non-seekable MP4 drops the packet kept from the open probe; the picture
  starts at the next keyframe.
- The soundtrack engine records the BASS output device when it is built, and Full mode now builds
  it for https/HLS audio: check on Windows that a device change while playing is followed.

### ENG-038 - Vulkan Video follow-ups from 045A slices 2-3

Reported: 2026-10-27 (045A slices 2 and 3, GPU decoding for the Vulkan renderer). Not fixed.
- **Not verified on a real decode queue in the sandbox.** Lavapipe has no video queue. The
  owner's Windows smoke (`updates/045A_3`) is the first real run. Mesa RADV and ANV and NVIDIA
  on Linux (X11, Wayland; Avalonia's OpenGL and Vulkan backends) are still to check.
- **Frames from a replaced device.** After a renderer device loss the compositor gets a new
  device. A decoder whose old device still decodes (a false alarm, so no decode failures) keeps
  producing frames the renderer skips (one warning), and nothing triggers the decoder's
  recovery. A real loss fails the decoder too and recovers. Possible fix: have the renderer
  request a decoder swap, through the per-decoder fallback token proposed in ENG-037.
- **Scaling quality.** The video layer is composed with the Vulkan renderer's nearest blit, as
  software frames are, to match the software renderer pixel for pixel. Large downscales (4K into
  a small window) alias. A linear blit, or drawing the layer with a filtered quad, is the fix
  for both paths.
- **Chroma siting.** The shader samples chroma centre-sited with bilinear filtering, as the
  Direct3D11 tone-map shader does. MPEG-style left-sited chroma is shifted by a quarter of a
  chroma sample; the video processor and swscale honour the siting.
- **VAAPI with DRM PRIME import** (the plan's alternative) stays a later option for Linux
  drivers without Vulkan Video.
- **Windows export paths not run here.** The keyed-mutex Direct3D11 texture import (ANGLE) and
  the opaque NT-handle export (Avalonia's Vulkan backend) compile and negotiate in tests, but
  only the owner's smoke runs them. The Avalonia test player does not offer Avalonia's Vulkan
  backend on Windows, so the NT-handle path has no smoke yet; a host option for
  `Win32RenderingMode.Vulkan` would cover it.
- **WPF and WinForms with `--renderer=vulkan`** decode on the GPU but read each frame back for
  their frame sink. A Vulkan swap chain on the native window (`VK_KHR_win32_surface`) remains a
  possible later step; Direct3D11 already covers the native window.
- **Keyed-mutex wait under the shared queue lock** (code review). On the Windows ANGLE path the
  render submit acquires the Direct3D11 texture's keyed mutex inside `vkQueueSubmit` (up to
  `KeyedMutexTimeoutMilliseconds`, 500 ms) while holding the queue lock FFmpeg also takes for
  decode submissions. A compositor that releases late stalls decoding with it. Watch for
  stutter in the owner's smoke; the fix is to wait for the mutex before taking the queue lock
  (or a separate render queue).
- **R16 plane filtering required for every stream.** The probe requires linear filtering on
  `R16`/`R16G16` (P010 planes) even when only 8-bit NV12 would be decoded, so a GPU without it
  gets no Vulkan Video at all. Requiring it only for 10-bit streams is the refinement.
- **Renderer-device decoding is reached through downcasts.** `VideoDecoder` and the coordinator
  test for `RendererVideoDecodeServices` and `PipelineMediaEngine` instead of a contract member,
  so another services wrapper or engine silently keeps platform decoding.

### ENG-037 - Hardware-frame follow-ups from 045A slice 1

Reported: 2026-10-26 (045A slice 1, GPU decoding on the Avalonia Direct3D11 path). Not fixed;
details in `complete/045A_COMPLETED_GpuDecodingEveryPath.md` (Not in this stage).
- **Hardware frame a renderer cannot take.** When `EnsureNativeFrameDevice` returns false (a
  host pairing hardware decoding with the software or Vulkan renderer, or a failed device
  adoption), Core drops the frame on every path. A paused first frame stays black. Since ENG-039
  batch 1 the software renderer offers no decode device, so its players decode in software
  whatever the configuration asks (the default `Auto` on Windows was black). The run-time case
  (a failed adoption, WARP) is implemented, pending manual smoke (ENG-039 batch 4b, G3x): a
  per-decoder `NativeVideoFrameResource.DecodeFallback` token that the coordinator requests and
  `VideoPipeline` acts on, swapping to software on its recovery worker for the rest of that
  media. Not covered: the CD+G background video decoder (`PipelineMediaEngine`) sets no token,
  so a refused background hardware frame is still dropped.
- **Compositor on another GPU.** The decoder, the renderer and ANGLE all use the default
  adapter. A compositor elsewhere makes the shared textures fail the LUID check, and the
  surface uses its frame sink. Choosing the adapter by the compositor's LUID is deferred.
- **Removed device during re-export.** A GPU reset between adopting a decoder device and
  re-exporting the shared textures leaves the Avalonia surface on its frame sink for the
  session (adoption of an already-removed device is refused). `OnUnavailable` cannot yet
  tell a transient failure from a permanent one.

## Completed

### ENG-041 - Video audio logged as normalized at gain 1.000 when it has no audible audio

Reported: 2026-11-02, owner smoke of 037A slice 1. One video logged `Quick normalization complete,
gain=1.000` and `Deep normalization complete, gain=1.000`, which looked like a scan that read no
audio. Resolved the same day, not a bug: it was a background video with a silent audio track, so the
peak was 0 and the gain correctly stayed 1.0 (the owner confirmed normalization works on other
videos). Diagnostics only: the deep line now says "no audible audio found; gain left at 1.0" for a
peak of 0, and the normalization lines carry the file as their source path (the log did not name
the video).

Status: resolved 2026-11-02, not a bug (moved to Completed 2026-11-05).

### ENG-042 - Cached karaoke raster differed from an uncached one once on Windows CI

Resolved: 2026-11-02.

Reported: 2026-11-02. `D3D11SubtitleTextRendererTests.RenderKaraoke_CachedPath_MatchesUncachedPixels`
(ENG-039 P5-2) failed once on PR #45 (run 41408629494, attempt 1: "Cached raster pixels differ
from an uncached raster" in the karaoke loop over frames 30, 45 and 90; which one was not reported) and passed on the re-run with no change. It
cannot run in the Linux sandbox and has not reproduced since.

Investigation (static, against Vortice 3.8.3): the frame inputs are pure (immutable style,
position-only timing, no system or random state), cache keys carry every layout parameter
(text, format, max width and height, alignment), cached layouts and brushes are never mutated,
eviction after `Remove` during `foreach` is safe on .NET Core, and the render target's transform,
antialias and text-rendering state are never changed after creation. No other code in the test
process uses Direct2D or DirectWrite. The one way found for the cached path to emit pixels that are
not the frame's drawing: Vortice's `ID2D1RenderTarget.EndDraw()` returns a `Result` instead of
throwing, and the renderer discarded it (`_ = target.EndDraw()`). When Direct2D's flush fails
(`D2DERR_RECREATE_TARGET`, out of memory under a loaded runner), the WIC bitmap keeps a partial
frame or the previous frame's pixels, `CopyPixels` returns them as the new frame, and the cached
target keeps being reused. A fresh renderer starts on a new target, so the two disagree; in the
player it would be a stale or half-drawn lyric frame.

Fix: `SubtitleTextRenderer` checks EndDraw; a failure releases the target (and its brushes) and
the frame is drawn once more on a new one; a second failure throws with the HRESULT instead of
returning wrong pixels. The test now compares every frame 30-90 instead of three, and its failure
message names the frame, counts the differing pixels, gives their bounds, the maximum channel
delta and sample BGRA values, and renders the frame with a second fresh renderer: if two fresh
renderers disagree, Direct2D rasterization itself is not deterministic on that runner (then
document it and compare with a tolerance justified by the reported delta); if they agree, the
cached path drew something else.

Status: fixed in PR #47 (merged 2026-11-02). Windows CI run 41415123312 passed on the first
attempt with the stronger test (frames 30-90). The Linux sandbox cannot run the test. The EndDraw
failure is the only mechanism found, not one observed: the failing run left no pixel evidence. If
the test fails again, its message decides between the two outcomes above; reopen this entry then.

### ENG-036 - Adapter diagnostics reached every player in the process

Resolved: 2026-10-26, pending Windows smoke (steps below).

Reported: 2026-10-26 (044A, Avalonia test player with `--players=2`; on Windows P1's log showed
P2's `Surface #3 'second-screen-2'` lines). The Avalonia `MediaSurface` raised its path lines with
`DiagnosticRouter.Raise` on the UI thread, which has no owner (`EnterOwner` is internal to Core),
so the diagnostic was unowned and every player's `MediaDiagnostic` reported it (ENG-025).

Fix:
- New public overload `DiagnosticRouter.Raise(IMediaControlSession? owner, level, category,
  message, sourcePath, innerException)`: the diagnostic belongs to `owner` (only that player's
  `MediaDiagnostic` reports it; an explicit owner wins over an ambient one); a null owner raises a
  process-wide diagnostic. Additive; the engine's ambient ownership is unchanged. An adapter knows
  the session its surface is attached to, so it names it.
- Avalonia: `MediaSurface` (presentation path, Audio-mode clear) and `AvaloniaSharedImageSurface`
  (shared images imported; it now takes the session) raise for their session.
  `SecondaryPresentationWindow` raises through its `MediaSurface`. The WPF and WinForms adapters,
  the test players and `Player.Shared` raise no diagnostics of their own (they only log
  `MediaDiagnostic`), so nothing else changed.
- Tests: Portable (two players: a diagnostic raised for one reaches only it, even under the other's
  ambient owner; a null owner reaches both); Avalonia headless (two sessions, each with a surface
  and a second screen: every surface line is owned by its own session). Core now exposes internals
  to `Avalonia.Tests` (allow-listed in `check-core-boundary.ps1`). The 044A view-model diagnostics
  test uses the new overload. The harness still leaves posted work to the tests: other tests raise
  process-wide diagnostics on purpose, and those still reach every player.

Windows smoke: Avalonia test player `--players=2`, open a file in each, open a second screen for
each. Each player's Log shows only its own `Surface #n` lines (P1: `'primary'` and
`'second-screen-1'`; P2: `'primary'` and `'second-screen-2'`), and switching P1 to Audio mode adds
the "cleared" lines only to P1's log. Process-wide lines (native loading) still appear in both.

### ENG-035 - Network seeks blocked the host's UI thread

Resolved: 2026-10-25, pending Windows smoke (steps below).

Reported: 2026-10-25, owner's Windows smoke of PR #13 (head 89209e8): Avalonia test player,
`--url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` (multi-variant HLS up to 1080p),
Direct3D11, BASS present. The first seek worked after a delay; on the second the whole window
froze and had to be ended from Task Manager. The player seeks with `SeekAsync` on release and
reads `State`, `Duration` and `Position` every 200 ms on the UI thread.

Findings (three locks held across network I/O, and seeks that could not be cut short):
- **`Position` waited for the audio cursor's downloads (BASS; read in the code, the most likely
  cause of the freeze).** `VideoAudioEngine` held its `_sync` lock across every packet read and
  container seek of its own network cursor: the decode loop's reads, `Seek` (`av_seek_frame`) and
  the post-seek `PrebufferAudio` (up to 4096 reads, from the segment start to the target: the audio
  cursor downloads the whole muxed TS of the 1080p variant). `Position`, `Normalize` and the
  volume, pitch and tempo setters take `_sync`, so the UI timer's `Position` read blocked for every
  segment download after a seek, and for the read timeout (15 s) on a stalled segment.
- **The redraw after a seek waited for the seek's downloads (reproduced).** `SeekCore` holds the
  presentation and decode locks while it decodes to the target, which for a network source
  includes downloading segments. `RenderCurrentFrame` (playing) waited for the presentation lock;
  it is reached from the control queue (`SeekAsync`'s refresh, so every queued operation waited
  behind it) and from sync setters on the UI thread (`AudioSyncOffset` and the other offsets,
  `ShowMidiLyrics`; a paused surface resize returns early). Stack dump (old engine, 10 s stalled
  segment): the control queue in `RenderCurrentFrame` <- `RefreshCurrentPosition` <-
  `SeekOperation`, the setter likewise, the seek worker in `av_read_frame` <- `DecodeForSeekTarget`
  <- `SeekCore`. Measured: the setter blocked 1.28 s during ordinary seeks (150-1000 ms per
  segment) and 9.2 s during the stall.
- **A newer seek waited for the older seek's stalled read (reproduced).** Nothing interrupted a
  network read except Close's abort, so a seek during a stalled segment settled only after the
  stall: 9.8-10 s for a 10 s stall (HLS and progressive MP4).
- **Close waited for a stalled seek (reproduced).** Close took the presentation lock before
  aborting, and the seek held it: 9.7 s during a 10 s stall.
- No deadlock was found. The Avalonia adapter only posts to the UI thread, and the shared-GPU-image
  ring waits are bounded; the admission gate (ENG-024) does not block `SeekAsync`'s caller.

Fix:
- `VideoAudioEngine` has a second lock, `_ioGate`, taken before `_sync`, that owns the demuxer
  cursor (open, read, seek, close). Reads, seeks and the network open run under `_ioGate` only;
  `_sync` is held for decoding, BASS calls and position bookkeeping. The loop wrap records its lap
  boundary only after its seek succeeded. Local files keep the same behaviour (one more
  uncontended lock per packet).
- A newer seek supersedes the network I/O of the older one: `FfmpegInterrupt` interrupts an
  operation that started before a given time (`SupersedeOperationsStartedBefore`), and
  `VideoPipeline.Seek` applies it to both cursors with the time taken just before the request, so
  the new seek's own I/O is never cut; `SeekCore` does it again before stopping the decode loop. A
  superseded read returns `AVERROR_EXIT` and is not taken for a dropped stream (no "Network read
  failed", no end of stream); the audio cursor does not take it for the end (no loop wrap).
- `RenderCurrentFrame` does not wait for the presentation lock while a seek sequence is active
  (the seek presents its own frame); otherwise it waits as before.
- Close makes the seek in progress give up (`SeekPump.BeginShutdown`: the seek's superseding
  checks turn true) and supersedes its I/O until it releases the presentation lock, then aborts.
- Avalonia test player: the 200 ms timer reads `State`, `Duration` and `Position` on the thread
  pool and skips a tick while a read is out (a slow, stalled or failing read is logged), and each
  player has **Copy log** (the whole log, oldest first, to the clipboard).

Measured (Linux, FFmpeg 9, no BASS, local 3-variant HLS from `LocalHttpMediaServer`):

| | before | after |
|---|---|---|
| Longest UI call, seeks at 150-1000 ms per segment | 1280 ms (sync-offset setter) | 4 ms |
| Longest UI call, 10 s stalled segment | 9156 ms | 7 ms |
| Newer seek during a 10 s stalled segment (HLS / MP4 range request) | 9963 / 9821 ms | 617-711 / 324-427 ms |
| Close during a stalled seek | 9664 ms | 102 ms |

`State`, `Position`, `Duration`, `IsOpen` and a surface resize stayed under 2 ms throughout without
BASS (the picture-only clock is lock-free), so the `Position` freeze itself needs Windows to see.

Tests (opt-in native, `LinuxNetworkVideoNativeTests.Seeks.cs`; a UI thread polls the getters every
200 ms and, once a second, resizes the surface and sets the sync offset; a watchdog writes
`dotnet-stack report` stacks to the output if a call blocks for 2 s or a seek does not settle):
`HlsMultiVariant_RapidAndSpacedSeeks_KeepTheGettersResponsive`,
`HlsMultiVariant_SeekDuringAStalledSegment_IsSupersededAndClosesPromptly` and
`ProgressiveMp4_SeekDuringAStalledRangeRequest_IsSupersededAndPlaysOn`. The audio cursor's
`_ioGate` split is not exercised here (no BASS): it is verified by reading and by the smoke below.

Windows smoke (Avalonia test player, BASS and FFmpeg 9 in `native/`):
1. `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 --
   --renderer=direct3d11 --url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`. Sound plays.
2. Seek about ten times by clicking and by dragging the seek bar: forward, back, twice in quick
   succession, and once more before the previous seek has shown its frame. The window must stay
   responsive throughout (move it, drag the volume slider, change the tempo); each seek shows its
   frame and plays on with sound in sync, and the log shows no "Status read" warning.
3. Pause, seek twice, Play: it resumes at the last target.
4. Seek, then close the window at once: it closes within about a second.
5. Repeat 1-2 with `--renderer=software` and with a local MP4 (no regression: seeks settle as
   before, the loop with Loop on is gapless).
6. **Copy log**, paste into a text editor: the whole log, oldest first.
7. If the window ever freezes again: before ending the task, run
   `dotnet tool install -g dotnet-stack` (once) and
   `dotnet-stack report -p <pid> > stacks.txt` (the pid is in Task Manager's Details tab), and
   attach `stacks.txt` and the copied log.


### ENG-027 - Hardware decoding does not reserve extra pool surfaces

Resolved: 2026-10-24, pending manual smoke on Windows.

Reported: 2026-10-18 (audit; Suspected, needs Windows). Real, and the hold was larger than
reported. FFmpeg's d3d11va (and dxva2) pool is a fixed texture array: `ff_dxva2_common_frame_params`
asks for 1 work surface plus 16 for H.264/HEVC (8 for VP9/AV1, 2 otherwise), and
`avcodec_get_hw_frames_parameters`/`ff_decode_get_hw_frames_ctx` add 3 work surfaces, one per
frame thread and `extra_hw_frames`. So a 4K HEVC pool was 20 + threads surfaces, of which the DPB
can use up to 16. The FFmpeg 9 Windows DLLs carry the failure strings ("Static surface pool size
exceeded.", "Failed to allocate a %s/%s frame from a fixed pool of hardware frames. Consider
setting extra_hw_frames..."). `extra_hw_frames` was never set.

The pipeline's hold: every queued d3d11va frame is an `av_frame_clone` that keeps its surface.
Steady playback buffers `GetTargetBufferedVideoFrames` (12 at most; 4 at 4K, because the target
is sized as if frames were BGRA), but the decoder's own cap is `MaxFramesCap` = 24 and the
hardware path always uses it: seek-to-target decoding (`DecodeForSeekTarget`), EOF draining and
a multi-frame packet fill the queue up to 24. Presentation holds the frame on screen and, during
the hand-over on the presentation thread, the previous one, while the decode thread refills the
queue. Worst case: 24 + 2 = 26 surfaces outside FFmpeg's decode set, more than the DPB slack.

Fix (`VideoFrameBufferPolicy`, shared by `VideoPipeline` and `VideoDecoder`): the native hardware
queue is capped at the pipeline's buffer target with a floor of 8 (`MinHardwareQueueFrames`), not
`MaxFramesCap` 24, and `TryCreateHardwareDevice` sets `extra_hw_frames` before `avcodec_open2` to
that cap plus `PresentationHeldHardwareFrames` (2). The pipeline target formula moved into the
policy unchanged (4 at 4K, 12 at 1080p60, 6 at 1080p24), so the decoder sizes the cap from the
stream before opening. Results: 4K gets a cap of 8 and a reserve of 10; the largest is a cap of 12
and a reserve of 14 (1080p and smaller at 48 fps or more). Without native output (frames copied to the CPU and
released at once) there is no cap and no reserve; software decoding keeps 24. FFmpeg accepts any
non-negative value; the D3D11 array limit is 2048. After a successful hardware open the decoder
logs one Info line: "Hardware decoding (d3d11va) reserves 10 extra pool surfaces: 8 queued frames
and 2 held by presentation." There is no VAAPI path: Linux registers
`SoftwareOnlyVideoHardwareAccelerationServices`; d3d12va and dxva2 get the same reserve.

Every path that fills the queue respects the cap:
- Steady playback (`DecodeVideoBatch`), post-seek warm-up and loop top-up stop at the pipeline
  target, which is never above the cap.
- A packet that yields several frames: `DrainDecodedFrames` stops at the cap and leaves the rest in
  FFmpeg until the next call, as before.
- End of file: the old `ProcessRemainingFrames` flushed the decoder once, took what fit and set
  `EndOfStream`, so the frames that did not fit were lost. This was already true with 24 and a
  smaller cap makes it likelier: the sample 1080p H.264 clip lost its last 5 of 300 frames with
  the queue full at end of file. Now the decoder stays in a draining state and takes the flushed
  frames as the queue frees up, then sets `EndOfStream` (the loop pre-seek waits for it, as before).
- Seek to target (`DecodeForSeekTarget`): exact seeks discard pre-target frames in the decoder, so
  the queue head is already acceptable and the cap does not matter. Preview seeks queue from the
  keyframe; before, a full queue (24) with no acceptable frame ended the decode and showed the
  closest frame, which with a cap of 8 would land up to 8 frames past the keyframe. Now a full
  queue drops its head when the head and its successor are both before the target (the successor
  is at least as close, so the best and fallback picks are unchanged) and decoding goes on, still
  bounded by the seek's time budget and attempt count. This applies to software decoding too.

Tests: `VideoFrameBufferPolicyTests` (target, cap, reserve, seek drop rule) and
`LinuxNativeSmokeTests.RealFfmpeg_FullQueueAtEndOfFileKeepsTheLastFrames` (fails on the old drain
with 295/300). The hardware path itself is not verified here (no GPU).

Cost: about 12.5 MB per 4K NV12 surface (128-aligned 3840x2176) and 25 MB per P010 surface. At 4K
the reserve of 10 adds about 125 MB (8-bit) or 250 MB (10-bit) of VRAM per player, against about
325/650 MB for the first fix's reserve of 26. At 1080p (1920x1152 for HEVC) the reserve of 14 adds
about 46 MB (8-bit) or 93 MB (10-bit).

Manual smoke (Windows, hardware decoding on, Log open at Info):
1. Play a long 4K HEVC file (10-bit HDR if available) with hardware decoding. Check the Log
   shows the reserve line once per open ("reserves 10 extra pool surfaces: 8 queued frames")
   and `hw=d3d11va` with `queue=n/8` in the decode diagnostics; note GPU memory in Task Manager
   against the previous build.
2. Seek storm: drag the progress bar back and forth for 30 s, then click-seek 20 times quickly,
   including near the end. Video must keep up with no freeze, no fallback to software, and no
   "Static surface pool size exceeded", "fixed pool of hardware frames", `get_buffer` or
   `Decoder.DeviceLost` lines. Scrub previews should still land near the pointer on a long-GOP
   file.
3. Pause for a minute, resume, then pause/resume ten times quickly. Playback resumes at once.
4. Open the secondary screen (composition path) and repeat steps 2-3.
5. Let the file reach the end with loop on and off: the last frames show (no cut-off), the loop
   wraps, and `MediaEnded` fires once.
6. Repeat step 2 with a 4K AV1 or VP9 file (smaller base pool: 12 + threads).

### ENG-022 - Subtitle content flag is never revoked

Resolved: 2026-10-24, pending manual smoke.

Reported: 2026-10-18 (audit). Still real: `SubtitlePipeline` only ever added
`MediaContent.Subtitle`, so turning subtitles off, or loading embedded tracks without selecting
one (MIDI lyrics hidden), left it set; the coordinator ran its 75 Hz loop for audio-only media,
presented empty compositions on seeks, and `HasSubtitle`/`HasVisualContent` read true.

Fix: the flag now means "a subtitle track is showing". `SubtitlePipeline` sets it when a track
is selected and clears it on `SelectTrack(null)` and `Close()` (new `MediaState.RemoveContent`);
loading tracks no longer sets it. The picker reads `SubtitleTracks`, not the flag, so "tracks
available" is unaffected. `MediaPresentationCoordinator` decides the render loop through
`SyncRenderLoop`, called from `StartRendering` and `RefreshCurrentPosition` (which
`SelectSubtitleTrack` and `ShowMidiLyrics` already call), so selecting a track while playing
starts the loop and deselecting stops it. Tests: `SubtitlePipelineTests.ContentFlag_FollowsTheSelectedTrack`,
`MediaPresentationCoordinatorTests.SelectingASubtitle_WhilePlayingStartsAndStopsTheRenderLoop`.
Manual smoke: play audio, select and deselect a sidecar subtitle (the overlay appears and goes,
CPU idles after off); play a MIDI with lyrics hidden (no loop, `HasSubtitle` false).

### ENG-023 - Software-path compositions are built and presented unordered

Resolved: 2026-10-24, pending manual smoke.

Reported: 2026-10-18 (audit; Likely). Still real: `BuildAndPresentSoftwareComposition` built
under `_lock` and presented outside it, so the video-frame thread and the render loop could
present an older composition after a newer one.

Fix: no "drop older" (per-target cadence would lose a target's frame). Build and present now
both run under a new `_softwarePresentGate`, taken before `_lock`, so compositions reach the
renderer in build order and every target subset still gets its frame. No caller holds `_lock`
when entering `BuildAndPresentSoftwareComposition` (checked), so the order cannot invert. The CDG
and hardware paths are unchanged. Test:
`MediaPresentationCoordinatorTests.SoftwareCompositions_ArePresentedInTheOrderTheyWereBuilt`
(blocks the first present, starts a second; failed before). The Linux real-media sink receives
bare pixels with no timestamp, so frame order is not observable there; the opt-in Linux tests
were run to check the gate causes no stall.

### ENG-033 - BASS required at startup

Resolved: 2026-10-24, pending manual smoke.

Reported: 2026-10-24 (owner). FatzAudio (037A) is planned to replace BASS, so end users should
not need the BASS DLLs. A player could not start at all without them: on Linux the engine
threw "Failed to load required native libraries. Missing required BASS library: libbass.so...",
even in Background mode (043A), which never uses audio. Windows `Bootstrap` did the same
(`_isInitialized = IsBassLoaded && IsFfmpegLoaded`), and also called `BASS_PluginLoad` for the
plugins after `bass.dll` was missing, which throws once nothing can resolve `bass`.

Fix:
- **Platforms (native loading):** FFmpeg is the only required set. A missing or failed BASS
  core (`bass.dll`/`bass_fx.dll`, `libbass.so`/`libbass_fx.so`) is a Warning; native loading
  succeeds without it. Plugins (and on Linux the `DllImport` resolver) are attempted only after
  the whole core loaded, and BASS audio extensions join `FileTypes.Audio` only then, so no BASS
  function is called without BASS. A later player creation retries BASS; the loaded FFmpeg is
  kept (Linux no longer re-activates it). `LastNativeLoadMessage` reads "Native libraries loaded
  without BASS: audio is unavailable. ..." The device monitors (`PollingAudioDeviceMonitor`,
  `BassDeviceAudioMonitor`) are registered only when BASS loaded, and Windows
  `AudioOutputDevices.Enumerate` returns an empty list instead of calling BASS.
- **Report:** BASS checks are never `IsRequired`; `NativeDependencyReport.IsAudioAvailable`
  (new, `init`, default true) is false and `Describe()` adds "Audio is unavailable: the BASS
  libraries did not load. Video plays without sound; audio media will not open." Hosts that log
  required problems as Errors and the rest as Warnings now show BASS as Warnings.
- **Core contract:** `IMediaControlPlatformServices.IsAudioAvailable` (new, default `true`, so
  existing implementations compile). Core reads it after `EnsureNativeLibrariesLoaded`; Core
  still has no native loading. Without audio, `MediaControlServiceProvider` uses
  `SilentAudioEngine` in every mode (so no `AudioEngine.Init`, MIDI soundfont or DLS cache, and
  `BassOutputWatchdog` never gets a client) and raises one Warning per graph in Full and Audio
  mode.
- **Loads:** `PipelineMediaEngine` refuses media that needs audio before touching an engine
  (`MediaPlayerModePolicy.NeedsAudioEngine`: audio files, MP3+G, their archives, Audio-mode
  soundtracks, network audio): `Load` returns false with an Error diagnostic and the existing
  error event (`NativeLibraryMissing`): "Audio engine unavailable: BASS libraries not found;
  <file> was not loaded." Audio mode creates no soundtrack engine.
- **Video in Full mode (decision):** plays its picture on the playback clock without sound.
  The change was small: `VideoPipelineFactory` passes `usesAudio` (audio available and not
  Background mode) to `VideoPipeline`, which already ran on `PlaybackClock` in Background mode
  and for video without an audio stream. Images and CD+G graphics without audio open normally.

Tests: `AudioUnavailableTests` (policy, service graph, loads that need audio in Full and Audio
mode, network audio, player-level `MediaError`), `NativeDependencyReportTests`
(`Report_WithoutBass_...`), `LinuxNativeLibraryBootstrapperTests` (no BASS starts without audio,
skips resolver and plugins, truthful report, BASS retry without a second FFmpeg activation),
`LinuxPlatformCompositionTests` (no monitor without BASS). Opt-in native: `LinuxNoBassNativeTests`
(real FFmpeg 9, no BASS, H.264 clip, software renderer frame sink) plays in Background and Full
mode and observes presented frames. Avalonia test player under Xvfb (Linux, no BASS): Background
and Full play the H.264 clip; Audio mode logs the Error and `MediaError` above.

Manual smoke (Windows):
1. Rename `bass.dll` and `bass_fx.dll` (and the plugins) in the host's `native/` folder, for
   example to `bass.dll.off`, for `FatzBomb.MediaControl.Player` and `.Player.Avalonia`.
2. Start the WPF player: it opens with no startup error; the log shows the BASS files as
   Warnings and "Audio is unavailable: the BASS libraries did not load...". Load a video: the
   picture plays without sound. Load an MP3: it fails with an Error "Audio engine unavailable:
   BASS libraries not found; <file> was not loaded."
3. Start the Avalonia player in Full mode: same messages (BASS files as Warnings, the
   "without BASS" native message); a video plays its picture, an MP3 fails with the Error.
4. Start the Avalonia player with `--mode=background` and a video: it plays and loops.
5. Start it with `--mode=audio` and a video or MP3: the player opens; the load fails with the
   Error above.
6. Restore the DLL names; start both players again: audio plays as before and no BASS Warning
   is logged.

### ENG-024 - Player API threading contract gaps

Resolved: 2026-10-24, pending manual smoke (steps below).

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

The earlier audit text also named: `Reset` and the sync API hold `_operationAdmissionGate` for
the whole operation; `LoadAsync` has no `CancellationToken` (closing a window during a slow
network load froze the UI for up to 15 s plus retries); a failed `Reset` leaves the player
unusable; two players created together get an empty `Bootstrap` message (Windows).

Re-verified against the code after 043A slices 1-2, ENG-025 and ENG-032. Fixed:
- **Admission (fixed).** `_operationAdmissionGate` is held only to check the lifecycle and take a
  place in the queue. A sync call takes its exclusive turn under the gate
  (`MediaControlOperationQueue.BeginExclusiveTurn`) and waits for it and runs outside it, so the
  `Async` methods never wait for a long sync call. `Reset` and `Dispose` are serialized by a
  separate `_lifecycleGate` and also hold the admission gate only to take their turn; calls made
  meanwhile fail at once (`InvalidOperationException`, as before) instead of blocking.
- **Bounded `Dispose`/`Reset` and load cancellation (fixed).** A player-lifetime
  `CancellationTokenSource` is cancelled by `Reset` and `Dispose`; every load (sync, async,
  prepared-next, the mode switch's reopen) runs with it, and `MediaOpenCancellation` (an
  `AsyncLocal` set around the engine open) carries it to the BASS network open, whose retry
  delays now end at once and whose next attempts are skipped. A connect attempt in progress
  cannot be interrupted (BASS has no cancel for `BASS_StreamCreateURL`). Such a load returns
  `false`. `Dispose` then waits at most 2 s for its turn; past that it marks the player disposed,
  detaches the surfaces (the host may destroy their windows) and queues the teardown behind the
  operation in progress. `Reset` waits at most 5 s; past that it raises a Warning and returns
  `false` with nothing rebuilt (the player stays usable). New overloads:
  `LoadAsync(string, CancellationToken)`, `LoadAsync(string, bool, CancellationToken)`,
  `LoadAsync(string, string?, bool, CancellationToken)`, `LoadAsync(MediaInputSource,
  CancellationToken)`, `LoadAsync(MediaInputSource, MediaInputSource?, bool, CancellationToken)`;
  the first and last are also on `IMediaControlSession` with default implementations. Cancelled
  while queued: skipped, nothing changes. Cancelled during the open: the player ends closed.
  Either way the task is cancelled. Cancelled after `MediaLoaded`: the load stands and only the
  PlayOnLoad start is skipped.
- **Worker-context flag (fixed).** The `AsyncLocal` bool is replaced by per-operation scopes. A
  synchronous operation's scope applies on the worker thread only; an asynchronous one's follows
  its flow; each ends when its operation returns. `HostEventDispatcher` runs each host handler in
  a handler scope bound to its thread that ends when it returns. So a task, timer, engine thread
  or UI-thread callback started from an operation or handler is outside it once it runs on
  another thread or after it returned: its sync calls queue, and its `Dispose`/`Reset` work
  instead of throwing. Queue workers no longer inherit another queue's scope.
- **Event delivery (fixed).** Every public player event goes through `HostEventDispatcher`: one
  throwing handler does not stop the others, nothing escapes onto engine, render or queue
  threads, and the failure is a `HostEvent` Warning (for `MediaDiagnostic` itself only a debug
  line, to avoid a loop). `MediaEnded`, `MediaStopped`, `MediaError`, `MediaDiagnostic`,
  `AudioDeviceChanged`, `MediaStarted` and `PresentationFramePresented` (render thread) were the
  inconsistent ones.
- **Live diagnostics (follow-up 2026-10-25).** `MediaDiagnostic` was delivered on the control
  queue, so diagnostics raised during a long operation (the network open's "Network retry 1/3 in
  1.0s" ...) reached the host together after the load ended. Each player now delivers them in
  order on its own worker (`SerialEventDispatcher`), still through `HostEventDispatcher` and the
  ENG-025 owner filter; `Dispose` waits up to 1 s for those already queued and delivers nothing
  after it returns (the `DisposeLater` warning is the last one). A diagnostic handler no longer
  runs inside a control operation, so it may call `Dispose`/`Reset`. `BufferingChanged`,
  `StreamMetadataChanged`, `MediaError` and the lifecycle events stay on the control queue: they
  are ordered against the load (`MediaLoaded` first, stale-source checks), and network video
  raises buffering only after its open. Tests: `SerialEventDispatcherTests` (5),
  `PlayerThreadingContractTests` (+2: delivery during a long load, in order; nothing after Dispose),
  and the opt-in native `LinuxNetworkVideoNativeTests.RetryDiagnostics_ReachTheHostWhileTheLoadRuns`
  (refused connect, two retries: delivered at 35 and 535 ms, the load ended at 1539 ms).
- **Reset's settings (fixed, defaults kept).** `Reset` still starts from the defaults (the 043A
  decision); now each of volume, pitch, tempo, normalization, equalizer, loop and base visual
  transform that changed raises its change event once, on the new control queue after `Reset`
  returns, with the new `MediaChangeReason.PlayerReset`.
- **Failed `Reset` (already fixed by ENG-020; tidied).** The player stays rebuildable: control
  calls throw `ObjectDisposedException`, surface calls keep the surfaces, and a later `Reset`
  reattaches them. New: the parts a failed build created are freed at once, and neither a retry
  nor `Dispose` frees the old graph a second time.
- **Concurrent player creation (already fixed by ENG-020).** `Bootstrap` loads under a lock with
  a double-checked flag, so the loser waits and reuses the result.

Documented as the contract (XML docs on `IMediaControlSession`, `Reset`, `Dispose`, `LoadAsync`):
- The sync methods block the caller until their turn and the operation finish, without a bound;
  UI threads should use the `Async` methods (as AGENTS.md says).
- `Reset` and `Dispose` still throw `InvalidOperationException` from inside a control operation
  or a handler raised in one, since they would wait for themselves.
- Surface calls during a `Reset` or mode switch: already safe since 043A slice 2 (`_surfaceGate`,
  held and reattached). Property getters/setters and the sync-offset and subtitle methods are not
  queued: they act on the current graph, also during a rebuild, so a value set during a rebuild
  may be lost. Queuing every property was rejected (setters would become asynchronous).
- An asynchronous operation's scope follows its flow, so engine threads it starts count as
  inside it until it completes (in practice the end of a load, after any PlayOnLoad wait).

Tests: `PlayerThreadingContractTests` (14): async calls during a slow sync load return at once;
async calls during a slow Reset fail at once; Dispose during a cancellable open returns well
within the bound and cancels the load; Dispose during an uncancellable open returns after the
bound, refuses calls, releases the surfaces and tears down when the open ends; `LoadAsync`
cancelled while queued, during the open, and with an already-cancelled token; Reset giving up
after its bound; a failed Reset is clearly faulted, then rebuildable or disposable; Reset change
events; surface calls racing mode switches and Resets; Dispose from a task a handler started;
handler exceptions. `MediaControlOperationQueueTests` (+5): scope ends with the operation, other
threads are outside, handler scopes, abandoned turns, cancellation. Two `MediaControlPlayerTests`
now expect the load that Reset/Dispose waited for to report `false` (cancelled). The new
concurrency tests passed 20 runs in a row.

Manual smoke (Windows and Linux players):
1. In the WPF Player, open a network stream that does not answer (for example
   `http://10.255.255.1/stream.mp3`) with retries enabled, then close the window at once: it
   closes within about 2 s (before: up to 15 s per attempt plus retries). The log may show
   "Dispose did not get its turn within 2 s".
2. Same with the Avalonia test player (`--mode=full`), closing one player's window or the app.
3. Normal use in the WPF and Avalonia players: load, play, seek-drag, pause, stop, natural end
   with loop on and off, load another file, prepared-next transition: no change in behaviour, and
   no new `HostEvent` warnings in the log. (No test host calls `Reset`; it is covered by tests.)
4. Start a slow load (a large archive or a network stream) and press Play/Seek meanwhile: the UI
   stays responsive; the commands run after the load.
5. Switch modes (Avalonia `--mode`, or the API) during playback and resize the window during the
   switch: the picture comes back at the new size.

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
- Synchronous public setters that run outside a control operation (`LoopMedia`, `Effects.*`,
  `TransparentBackground`) do not enter the player's owner, so a diagnostic they raise while
  called from another player's event handler is attributed to that other player. Since ENG-039
  batch 1 the synchronous control calls (which run in a queue turn), Reset and Dispose,
  `PrepareNextAsync`, `AnalyzeAsync`, the fades and the faded manual transition, and the BASS
  network-metadata callback enter it.
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

Follow-up (ENG-039 G2, 2026-10-28): the "platform" rate was never DWM's. `DWM_TIMING_INFO` was
declared with the default layout (320 bytes) instead of the packed native one (292), so
`DwmGetCompositionTimingInfo` failed every time (`0x88980090`) and the platform rate was always
the 60 Hz fallback. The struct is now packed (`DwmCompositionTimingTests`), and a failure is
logged once per process (category `DisplayTiming`). On a 60 Hz monitor nothing changes; on
another rate, video without a per-monitor rate (no renderer rate source yet) now gets DWM's
composition rate instead of 60 Hz.

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
Fixed by 027A slice 5 (2026-10-24, pending owner smoke): Direct3D11 now gives a secondary frame
sink its own offscreen target beside the managed primary.

Review (local) decisions: with a primary attached, a software present succeeds only when the
primary presented (as `D3D11PresentationBackendSet.NotifyTargetsPresented` does), so a
secondary never hides a failed primary frame; a reopened secondary is placed before it shows.
Not changed: two targets of the same size are still rendered separately (rare: the windows
differ in size in practice); the Vulkan backend still accepts only a primary frame sink, but
no Avalonia/WinForms adapter opens a secondary window yet, so fold multi-target support into
ENG-014 when one does (done in 027A slice 5: Vulkan and Direct3D11 accept secondary frame-sink
and shared-image surfaces); `SecondaryPresentationWindow` gained an optional constructor parameter
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

CI is the record (Windows and Linux jobs in `.github/workflows/ci.yml`); counts are not kept here
because they go stale. Release builds are warning-free, and every test project, the format check
and the `check-*.ps1` gates must pass.
