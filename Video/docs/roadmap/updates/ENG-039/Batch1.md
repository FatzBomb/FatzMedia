# ENG-039 batch 1: Core and player bugs

2026-10-28. Branch `claude/festive-newton-yvvib5`. Status: implemented, pending manual smoke.

## What changed

- **Software renderer, default config:** `SoftwareRendererBackend` implements
  `IMediaRendererVideoDecodeDevice` with no device type, so its players decode in software
  whatever `VideoDecoding` says (Windows `Auto` showed black video). Test:
  `RendererVideoDecodeServicesTests.TheSoftwareRenderer_OffersNoHardwareDevice_AndSaysWhyOnce`.
- **Sync `Load` with PlayOnLoad:** the start is queued from inside the load's turn, so a `Pause`
  or `Stop` issued after `Load` returns runs after it and wins. Tests: `SyncLoad_PlayOnLoad_*`.
- **Diagnostic owner (ENG-025):** synchronous control calls, Reset and Dispose turns,
  `PrepareNextAsync`, `AnalyzeAsync`, the fades, the faded manual transition and the BASS
  network-metadata callback enter the player's owner. Tests: `SyncControlCall_*`,
  `PrepareNext_AnalysisFailure_ReachesOnlyItsPlayer`.
- **FFmpeg audio fallback:** uses the load's token (Reset, Dispose and the host cancel a long
  conversion); refuses audio that cannot fit a 4 GB WAV before decoding; writes a conversion
  estimated above the memory limit straight to a file. Tests: `PlanOutput_*`,
  `RealFfmpeg_CancelledConversion_Throws`.
- **FFmpeg-only audio inside archives** pairs with its CD+G and plays through the fallback;
  audio a BASS plugin decodes is preferred. Tests: `FileTree_Archive*`.
- **Background video** (audio with a video sidecar): the restart after a Stop, an end or a loop
  runs on the background worker, never on BASS's mixing thread; a Play right after a Stop still
  starts from the first frame; `ClearBackground`/`Close` never frees a decoder its worker still
  uses. `PlaybackScheduler.StopAndWait` returns whether the loop stopped.
- **Seek sync:** the audio frame spanning a seek target is trimmed to start at the target, so the
  sound no longer leads the reported position by up to one audio frame. Tests: `AudioSeekTrimTests`.
- **Video decoder:** a packet the decoder refuses while the frame queue is full is kept and
  resent, not dropped.
- **Network decoder swap:** a seek to the same position realigns the shared audio; the hardware
  re-probe skips network sources (it reconnected every 5 s).
- **Smaller fixes:** the position notifier's start race; `CdgPipeline` disposes its seek worker;
  no present from a refresh while a seek runs during playback; the position snapshot never posts
  to the caller's context; one normalization rule for audio and video (the quick pass never
  boosts; final peak = max of both) with one bounded gain cache (`NormalizationGainTests`).
- **Test players:** Avalonia waits for an in-flight rebuild before closing, catches handler
  exceptions, and opens `--second-screen` only on the first build; WPF and WinForms catch
  rebuild exceptions; drag-seek failures are logged; closing during the 500 ms startup delay no
  longer waits it out (`DisposeAsync_DuringTheStartupMediaDelay_DoesNotWaitItOut`).
- **Adapters:** setting a `KaraokeSurface`'s `Source` to null releases the song it loaded.

## Manual smoke (Windows)

From the repository root (`git pull`, then build):

1. **Avalonia close during a rebuild:** `scripts\smoke-audit.ps1 -Steps 8`. Expected: 8a exits
   with no player created after the others were disposed (log tail); 8b Avalonia does **not**
   reopen the second screen after a Renderer or Players change (same as WPF).
2. **Background video:** a song with a same-name `.mp4` beside it, WPF player. Play 20 s, Stop,
   Play at once: the background starts from its first frame. Loop on: at each loop point the
   background restarts; no `Composition present failed` burst at the loop.
3. **Seek sync:** an A/V sync clip (flash + beep, or speech close-up). Seek 10 times while
   playing: picture and sound stay aligned after each seek.
4. **Karaoke surface:** `--lightweight-surfaces`, Browse to a CD+G and press **Karaoke**; once it
   plays, clear the path box and press **Karaoke** again (Source = null): the song stops.
5. **FFmpeg fallback:** with `bassflac.dll` removed from `native\`, open a long FLAC and Stop or
   open another file during the conversion: the load ends at once. A zip with `song.flac` +
   `song.cdg` plays with sound.

## Linux run (2026-11-05)

Smoke step 5 (BASSFLAC missing): on Linux a FLAC plays through the FFmpeg conversion when `libbassflac.so`
is absent (`LinuxFormatMatrixNativeTests.Flac_WithoutBassFlac_PlaysThroughTheFfmpegConversion`, run on its
own: BASS plug-ins stay loaded for the process). Not run: the cancel-during-conversion part and the
song.flac + song.cdg zip; both stay in the Windows smoke.

