# ENG-039 batch 2: Windows and GPU bugs

2026-10-28. Branch `claude/festive-newton-yvvib5-b2` (on top of batch 1). Status: implemented,
pending manual smoke.

## What changed

**GPU device loss is found and recovered on every renderer** (the smoke's test 3: black or
frozen, nothing logged):
- Contract: `IMediaRendererBackend.ProbeDeviceLost(out reason)` (additive default member).
- Core (`MediaPresentationCoordinator.DeviceHealth.cs`): a 1 s health timer probes the renderer
  while a surface is attached; a failed present, native frame, attach or resize probes at once.
  One entry point logs "GPU device lost (reason); recovering." under the player's owner. The
  category-match trigger stays as a backup. Recovery backs off 0.5-8 s over 6 attempts, then
  retries every 10 s while a surface is attached; hardware frames are dropped meanwhile. After a
  recovery the player reopens video content at the same position and state, once
  (`DeviceRecovered`). A renderer call not returning for 3 s logs "Renderer call has not returned".
- Direct3D11: `ProbeDeviceLost` reads `GetDeviceRemovedReason` without the present lock; every
  failure path names its step and the removal reason (3a's `E_INVALIDARG` now reads "... input
  view failed (Device removed: ...)") and is latched; a removed or video-less decoder device is
  refused before teardown.
- Vulkan: a lost flag replaces the backend's self-cleanup (one re-creation per loss, ENG-038's
  stranded decoder); the probe checks the mark, the Direct3D11 interop device and a heartbeat
  (empty submit, 250 ms wait); every Vulkan failure probes; out-of-memory right after a consumer
  context loss, or 3 in a row, counts as lost (3d). Fence waits time out after 2 s, the keyed
  mutex after 50 ms; slow calls are logged. Only a driver-confirmed loss skips the idle wait.
- Avalonia: a lost compositor context (`PlatformGraphicsContextLostException`, a lost interop, an
  import unfinished after 2 s) is reported to the producer, and the surface returns to shared
  images once Avalonia's interop works again, instead of staying on the frame sink.
- Decoder: checks its hardware device once a second; corrupt frames count as failures.

**The UI no longer waits on a stuck renderer call** (3c froze): presents serialize on their own
gate and never hold the coordinator lock; surface calls take only that lock and defer their
present when a renderer call is running; host setters queue one coalesced refresh on the control
queue. The test players write each diagnostic to the debug log before the UI-thread hop.

**Other fixes:**
- G2 `DWM_TIMING_INFO` packed (292 bytes): the platform refresh rate is read (was always the
  60 Hz fallback; ENG-018 note). A failure is logged once.
- G4 no black frame per resize step (Direct3D11 shared textures and frame sink); G6 one
  "exported" line per consumer or selection (Direct3D11 and Vulkan).
- G8 a hardware background is retained only after a present carried it (test 6's "background
  layer could not be prepared" warnings).
- G5: NT handle closed for an unpublished shared texture; latched swap-chain Present warnings;
  Vulkan `Upload` unmaps on failure, a failed shared present aborts its ring write, the decoded
  image barrier uses the colour aspect, a partial Avalonia import reports how many handles it
  took (`CompleteImport(int)`).
- G7 CD+G before a memory preset: parity tests (Software, Direct3D11 WARP, Vulkan lavapipe).
- WPF: the overlay window follows the surface's visibility; session events only while loaded;
  the composited host's `Clear` drops a pending frame. The WPF test player starts from a
  read-only install.

Deferred: the per-player audio device monitor (needs BASS device selection on the poll thread;
not seen in the smoke); the frame-latency waitable swap chain (only if a smoke names Present).

## Manual smoke (Windows)

`git pull`, then from the repository root:

1. **GPU reset:** `powershell -ExecutionPolicy Bypass -File scripts\smoke-audit.ps1 -Steps 3`.
   Expected in every run (3a-3d): the log shows "GPU device lost (...); recovering", then "GPU
   device recovered after N attempt(s)", and the picture comes back (video via "Reopening ...
   after the GPU device was recovered"); 3c's UI stays responsive.
2. **Resize:** `-Steps 4`. No black flashes in either player; about one export line per run.
3. **Refresh rate:** `-Steps 2`. Unchanged probe output; in a player on a non-60 Hz monitor with no
   per-monitor rate, the cadence line names the real rate and no `DisplayTiming` warning appears.
4. **Background loop:** `-Steps 6`: no "background layer could not be prepared" warnings.
5. **Regression check:** drag-resize, the display-mode and colour controls, sync-offset keys and
   subtitle switching redraw at once in all three players; normal Avalonia Vulkan playback logs no
   "replaced" (keyed-mutex) warnings.
