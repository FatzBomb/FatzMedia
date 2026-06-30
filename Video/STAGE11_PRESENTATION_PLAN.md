# Stage 11 Presentation Plan

This is the current source of truth for active presentation/compositor work. Stage 10X is complete and kept here as history; Stage 11 is the active stabilization and timing stage.

## Non-Negotiable Goal

`FatzBombMedia.MediaControl` is the media-control engine. It owns playback, layers, compositor, swap chains, presentation targets, multi-target fan-out, and final presentation.

Adapters are framework interop packages only.

## Active Projects

- `FatzBombMedia.MediaControl`: Core engine and public session/player.
- `FatzBombMedia.MediaControl.Wpf`: strict WPF adapter.
- `FatzBombMedia.Player`: validation host.

Future adapters should be separate projects such as `FatzBombMedia.MediaControl.WinUI`, `FatzBombMedia.MediaControl.Avalonia`, and `FatzBombMedia.MediaControl.WinForms`.

## Current State

- Stage 10X implementation is complete.
- `FatzBombMedia.MediaControl.Wpf` is the active strict adapter. It contains no renderer, pipeline, DxCdg, video-frame, or D3D11 presentation references.
- Core owns sidecar image/video backgrounds, retained layer state, the D3D11 compositor, swap-chain policy, presentation targets, and final presentation.
- DirectComposition base/overlay splitting is Core-owned and disabled/falls back where primary/secondary parity or resize stability requires one final composed target frame.
- Stage 11 source-cadence video pacing, retained DxCdg overlay cadence, stopped/idle transparency, async control queueing, and source-level parity checks are active.
- Runtime pixel parity for primary/secondary output is still a validation item.

## Completed In Stage 10X-0

- Deleted the old broad WPF adapter.
- Renamed the strict adapter to `FatzBombMedia.MediaControl.Wpf`.
- Added Core-owned `MediaControlPlayer`.
- Added WPF `Player : MediaControlPlayer` for adapter-specific conversions without duplicating Core commands.
- Switched Player to the strict WPF adapter.
- Kept media overlay, wash, background, composition, and presentation in Core.
- Fixed video sidecar backgrounds in Core background-video warm-up.
- Removed unused old native-composite remnants.
- Updated boundary check to enforce no WPF UI/bitmap types in Core.

## Completed In Stage 10X-1

- Suppressed no-op presentation-surface resize requests before they reach Core swap-chain work.
- Added Core compositor diagnostics for retained reuse, skipped retained refreshes, target resizes, layer uploads/renders, and pass timing.
- Added native `ResizeBuffers` timing diagnostics to verify resize cost by actual target size.
- Activated the first retained-refresh optimization: unchanged retained frames now skip final draw/present when layers and target metadata have no work.
- Revalidated that the WPF adapter remains interop-only with no renderer/pipeline/DxCdg/D3D11 presentation references.

## Completed In Stage 10X-2

- Moved DxCdg invalidation presentation to a Core-owned pending overlay present instead of immediate event-side final composition.
- Paced pending DxCdg overlay presentation at the CDG sector cadence: 300 packets/sec grouped into 75 presentation ticks/sec.
- Added a Core compositor priority guard so pending DxCdg overlay presents defer to the next CDG tick when the final compositor is already busy with video/background work.
- Preserved immediate DxCdg presentation for paused/stopped refreshes, seeks, surface changes, and explicit retained refreshes.

## Completed In Stage 10X-3

- Changed the retained DxCdg index texture upload path to use dirty rectangles when available.
- Kept full-frame DxCdg index texture upload as the fallback for initial frames, full-frame changes, and snapshots without region metadata.
- Preserved the existing retained final-composition contract: adapters still receive only Core-owned final output targets.

## Completed In Stage 10X-4

- Split DxCdg frame revision from DxCdg palette revision in Core snapshots.
- Updated the D3D11 DxCdg upload path so tile-only overlay updates no longer re-upload the palette texture.
- Updated the retained overlay fast path to use the same palette revision check as the upload path.

## Completed In Stage 10X-5

- Added Core compositor diagnostics for DxCdg overlay-only candidate passes over retained background/wash content.
- Added target redraw counts for those candidate passes so the remaining overlay cadence cost can be measured per active output target.

## Completed In Stage 10X-6

- Removed the unused DirectComposition BGRA overlay upload/blit route from Core presentation services.
- Kept DirectComposition base/overlay swap-chain ownership in Core, but removed the stale CPU-frame overlay entry point so the next overlay path uses retained Core DxCdg resources.

## Completed In Stage 10X-7

- Added explicit Core target metadata for DirectComposition overlay swap-chain targets.
- Exposed the DirectComposition overlay swap-chain as a Core-owned presentation target snapshot without changing draw behavior yet.

## Completed In Stage 10X-8

- Refactored DxCdg foreground drawing into a shared Core D3D11 helper used by final composition.
- Added a Core compositor entry point that can draw retained DxCdg foreground into a transparent DirectComposition overlay target.
- Kept the new overlay-target draw path inactive until presentation scheduling can preserve secondary-output parity.

## Completed In Stage 10X-9

- Activated the Core-owned DirectComposition base/overlay split for the safe single-output case.
- DirectComposition base frames now suppress CDG wash/foreground drawing while retaining DxCdg GPU resources for overlay reuse.
- DxCdg overlay ticks can now draw/present only the DirectComposition overlay swap chain when the background is retained or supplied as a Core-owned BGRA sidecar frame.
- Full final composition remains the fallback for secondary/projector targets, non-DirectComposition targets, and unsupported frame shapes; the DirectComposition overlay is cleared before fallback.

## Completed In Stage 10X-10

- Added Core diagnostics for DirectComposition overlay split presents, full-final fallbacks, and overlay-present failures.
- Revalidated build, WPF boundary, and WPF adapter renderer/DxCdg/D3D11 scans after activation.
- Stage 10X implementation is complete; remaining 10X items are validation/regression checks.

## Stage 10X Closed Validation

- Continue runtime validation from `docs/VALIDATION.md`.
- Ensure WPF `MediaSurface` remains HWND/control interop only.
- Keep WPF UI chrome overlay separate from media overlay.
- Verify primary/secondary output parity for every visual mode.
- Validate the activated DirectComposition overlay split with sidecar video plus DxCdg.
- Use the new Core diagnostics to compare resize behavior by actual control size, especially across monitors.
- Keep adapter scan free of renderer/pipeline/DxCdg/video-frame/D3D11 presentation references.

## Active Guardrails

- Do not add renderer, pipeline, layer, timing, or final-presentation logic to adapters.
- Do not add WPF, WinUI, Avalonia, WinForms, or other framework UI types to Core.
- Do not create media-type-specific final presentation paths.
- Fix missing behavior in Core after verifying Core does not already own it.

## Stage 11 Work

- Status: Stage 11 core implementation is nearly complete; remaining work is validation automation and runtime confirmation.
- Done in code, still validating at runtime: multi-rate scheduling for independent layer cadences. Video uses source cadence when FPS is known, DxCdg overlay work is paced/retained independently, and retained refreshes skip work when layers/targets have not changed.
- Done as a Core-owned contract: one Core-owned final presentation path per target. DirectComposition may internally split base/overlay swap chains for the safe primary case, but the adapter still receives only a neutral presentation surface and Core owns all composition/presentation decisions.
- Deferred until adapter expansion is actually needed: create additional adapter projects such as WinUI, Avalonia, and WinForms. The current rule is documented and enforced: adapters stay framework interop only.
- Pending: runtime validation automation for primary/secondary pixel parity and media switch cleanup. Stage 11-18 added source-level parity automation, but not pixel capture/comparison yet.
- Stage 11-1: primary video presentation loop now uses source-frame cadence when FPS is known instead of constant display-refresh cadence.
- Stage 11-2: DirectComposition overlay split now includes Core-owned BGRA sidecar video/image backgrounds, so sidecar video/image can enter the split path in the safe single-output case.
- Stage 11-3: DirectComposition overlay split now tracks the presented base background/target revision, so CDG-only ticks skip the final/base redraw when the sidecar background has not changed.
- Stage 11-4: sidecar background video playback is now tied to Core playback state: it starts on play, stops on pause/stop/end, resets on stop/end, and loops internally regardless of the main media loop setting.
- Stage 11-5: sidecar background video now requests hardware D3D11 frame output and routes hardware frames through Core's existing retained D3D11 background path, with BGRA retained as fallback.
- Stage 11-6: DirectComposition primary activation is re-enabled with base swap-chain present/commit/back-buffer reacquire parity, after runtime validation showed the overlay/wash was visible but the base video layer was not.
- Stage 11-7: D3D11 compositor resources now reset when Core attaches to a different FFmpeg D3D11 hardware device, preventing stale device-owned shader buffers from being used after switching background videos.
- Stage 11-8: DirectComposition target revisions now track structural target changes instead of normal flip-model back-buffer rotation, so CDG-only overlay ticks can keep reusing the retained base.
- Stage 11-9: DirectComposition overlay-target drawing now uses premultiplied-alpha shader/blend state separate from the opaque final compositor path, preventing intermittent faded/alpha-looking overlay frames.
- Stage 11-10: DirectComposition resize now resizes Core-owned base/overlay swap-chain buffers instead of applying a non-uniform visual transform, preserving background UniformToFill and DxCdg UniformFit aspect behavior while resizing/fullscreening.
- Stage 11-11: primary DirectComposition resize requests are now queued/coalesced and applied at Core presentation boundaries instead of blocking the WPF size event; the DirectComposition split also keeps CDG wash on the base target and uses the overlay swap chain only for opaque DxCdg foreground pixels.
- Stage 11-12: while DirectComposition resize is pending or has just applied, Core suppresses the overlay split and presents one final composed frame per presentation target; the split can resume after resize settles.
- Stage 11-13: adapter controls now forward host live-resize state to Core automatically; Core keeps DirectComposition overlay split disabled for the entire live resize loop and refreshes once when resizing exits.
- Stage 11-14: DirectComposition split now tracks base and overlay cadence independently; unchanged DxCdg overlay content is retained across video/base presents, and video/background frames can coalesce pending DxCdg work into the same Core presentation.
- Stage 11-15: stopped/idle CDG+background presentation now clears Core visual layers to transparent and suppresses stopped reset-frame invalidations, preventing the empty CDG box or last background frame from remaining visible when playback is not active.
- Stage 11-16: WPF adapter native HWND visibility is now tied to Core playback/presentation lifecycle only; the HWND stays hidden while idle/stopped so host backgrounds show through, and playback reveals it only after Core presents the first frame.
- Stage 11-17: Core now exposes serialized async load/play/pause/stop controls backed by a Core-owned control queue; the Player app initializes the WPF facade off the dispatcher and awaits async transport commands instead of blocking UI commands.
- Stage 11-18: restored WPF/Core boundary automation and added a source-level primary/secondary parity check that enforces neutral adapter surfaces plus Core-owned multi-target fan-out.
- Stage 11-19: primary video adaptive lead now reacts to real, source-frame-aware clock-late samples instead of lead-adjusted target-late diagnostics, preventing healthy 24fps playback from slowly increasing lead and then dropping frames.
- Stage 11-20: primary video frame selection now uses a conservative source-frame future cap, holds adaptive lead steady for source-cadence 24/30fps playback, and avoids skipping to the next queued frame while the current source frame is still inside its normal display window.
- Stage 11-21: corrected the remaining 23.98fps `selectionSkips` stutter. The frame selector was not at fault — the source-cadence latch was already implemented and was stricter than required. The real defect was presentation-loop pacing: `WaitHandle.WaitOne(TimeSpan)` is bound by the ~15.6ms process timer resolution, so 41.7ms waits overshot to ~46.8ms, drifting the presentation point behind the audio-mastered clock until a one-frame catch-up skip was forced. Added a Core-owned high-resolution waitable-timer helper (`Core/Threading/PrecisionWaiter.cs`, `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, no process-global `timeBeginPeriod`, fallback to coarse wait) and routed the primary video, DxCdg overlay, and background video loops through it. No frame-selection or `VideoPresentationTiming` logic changed.
- Stage 11-22: runtime logs proved Stage 11-21 fixed timer precision (`avgTickGap=41.71ms`, `timer=high-res`), but primary video still free-ran at a fixed source interval and slowly drifted behind the audio-mastered clock until full-frame catch-up skips were legitimate (`maxDroppedClockLate >= videoFrame`). Primary source-cadence presentation is now phase-locked to the live presentation clock: after each present, the loop peeks the next queued frame and waits until that frame enters the selector window relative to the real clock instead of blindly adding another fixed interval. This keeps the existing retained selection/latch behavior but prevents small audio-clock/frame-rate mismatches from accumulating into recurring skips.

## Stage 11 Validation

- Validate 23.976/24fps and 29.97/30fps primary video no longer reports a 16.7ms presentation-loop target.
- Validate 60fps video still uses approximately 16.7ms source cadence.
- Validate video+DxCdg sidecar playback remains in sync after source-cadence pacing.
- Validate native HWND sidecar video + DxCdg keeps the background visible behind the wash/lyrics after hardware background activation.
- Validate sidecar video loops before the song ends, pauses/stops with the CDG file, and resets when stopped.
- Validate sidecar video diagnostics show `Background/D3D11Texture` or retained GPU background when hardware output is available; `Background/BgraPixels` should be fallback only.
- Validate loading a different sidecar video no longer crashes at `D3D11MediaCompositionPresenter.SetQuadConstants` / `UpdateSubresource`.
- Validate DirectComposition primary activation shows the background/video base layer behind wash/lyrics, not wash-only output.
- Validate sidecar video + DxCdg increments DirectComposition split presents and base skips instead of full-final fallbacks during lyric updates.
- Validate the DirectComposition overlay no longer intermittently appears faded or alpha-doubled during lyric updates.
- Validate resize/fullscreen keeps the background UniformToFill and DxCdg UniformFit instead of stretching either layer.
- Validate manual resize/fullscreen no longer stalls the UI for several seconds while playback is active.
- Validate sidecar video plus DxCdg manual resize no longer flickers from DirectComposition base/overlay split reshaping.
- Validate the host app does not need to call resize-loop APIs directly; WPF `MediaSurface` should manage live-resize state internally.
- Validate sidecar video plus DxCdg diagnostics show overlay skips during video/base presents and fewer duplicate DxCdg overlay presents when video and CDG updates arrive together.
- Validate loading/stopping CDG with sidecar image/video no longer flashes the empty CDG frame or leaves the last background frame visible while stopped.
- Validate `MediaSurface.Background` remains visible while stopped/idle, and CDG+background startup does not briefly expose the native HWND before Core has presented a real frame.
- Validate first Player window paint is no longer delayed by native bootstrap, and load/pause/stop remain responsive with sidecar video while Core waits for decode/presentation/background schedulers.
- Validate primary and secondary/projector windows visually match for audio+DxCdg, sidecar image, sidecar video, and video-only playback.
- Validate 23.98fps primary video now holds `selectionSkips≈0` and `lateDrops=0` with a full decode queue during steady playback after the high-resolution presentation timer (Stage 11-21); only small `selectionSkips` spikes immediately after seek/resize are acceptable.
- Validate the high-resolution timer is active by confirming the Player log does not print the `[VideoPipeline] High-resolution presentation timer unavailable` fallback warning on supported Windows.

## References

- `docs/CURRENT_ARCHITECTURE.md`
- `docs/VALIDATION.md`
