# Stage Update

Stage: 031A.1 - Prepared-next slot
Date: 2026-10-16

## Status: IMPLEMENTED - manual Player smoke pending

Delivered:

- Public `PreparedNextState`, `PreparedNextInfo`, `PreparedNextChangedEventArgs`,
  `PreparedNextTransitionedEventArgs`, and the slot API on `IMediaControlSession`.
- `Core/Prepared/PreparedNextSlot` generation-guarded state machine.
- `IMediaFileAnalyzer` on `AudioEngine`: dedicated decode stream, deep peak ->
  normalization gain, silence scan, `SilenceTrimCalculator`, cache store, handle freed
  in `finally`, joined on dispose.
- Auto transition on natural end (default off) and transition-now on the control queue.
- Player validation host: `--prepare-next=<path>` and `--auto-next`; slot changes are
  logged to the diagnostics panel/status line under category `PreparedNext`.
- New portable `net10.0` test project (`FatzBomb.MediaControl.Portable.Tests`) that
  links the Windows-independent tests and hosts the 30 new 031A cases.

## Decisions made without the owner (unattended session)

- "Stale on media change" interpreted as the prepared file changing on disk, not as
  the current media changing (the host owns ordering).
- Transition always starts playback (venue continuity), honoring `PlayOnLoad` timing
  when it is enabled.
- A failed transition load empties the slot instead of leaving it Ready.
- Analyzer failure still yields Ready with `Analysis = null`.
- `MediaControlPlayer.FlushControlOperationsAsync` added as an internal test seam.

## Verification

- Linux sandbox: Release build of the full solution (`EnableWindowsTargeting=true`)
  0 warnings/errors; `dotnet format --verify-no-changes` clean; portable tests 485/486
  (the one failure is the pre-existing case-sensitive sidecar test, fixed in 026B);
  031A cases 30/30 across 8 repeated runs.
- Windows CI: see the PR.

## Manual smoke (Windows, native DLLs)

```powershell
dotnet run --project FatzBomb.MediaControl.Player -- --trim-silence "--prepare-next=<next-audio>" "--auto-next" <current-audio>
```

1. Status/diagnostics show `Prepared next Empty -> Analyzing`, then `Analyzing -> Ready`
   with `effective=..., gain=...` and a `Standalone analysis complete` diagnostic while
   the current item keeps playing without glitches.
2. Let the current item end: `MediaEnded`, then the next item loads and plays
   immediately; diagnostics show `Transitioned to prepared next (automatic)` and the
   AudioEngine reports `Using cached media analysis` (no rescan) for the next item.
3. With silence trim on, the next item starts at its trimmed lead-in.
4. Repeat without `--auto-next`: at end the current item stops and the slot stays
   Ready (status shows no transition).
5. Repeat with a video or CDG file as the next item: Ready with `no analysis`, and the
   automatic transition loads it normally.
6. Prepare a file, modify it on disk (e.g. re-save), let the current item end with
   `--auto-next`: slot goes Ready -> Stale and nothing is loaded.
7. With `--software` repeat step 2.

## Follow-up fix (same day)

- The first 031A commit placed the standalone-analysis shutdown in `AudioEngine.Close`
  instead of `Dispose`, which would have cancelled every prepared-next analysis after
  the first media close. Moved to `Dispose`; found during self-review before any
  native run. The native smoke in this note covers it (step 1 must reach Ready with
  analysis after the current item was loaded).

## Review follow-up 2 (same day)

- Only plain-audio files are analyzed when prepared; video, archives (MP3+G zips), images,
  and CDG files go Ready without analysis and without a scan or warning.
- Analysis runs off the caller's thread, so `PrepareNextAsync` from the UI thread never
  blocks on opening/prescanning the file.
