# Active Stage 030B - Silence Trimming and Media Analysis Cache

Prepared: 2026-08-18
Status: Active. First slice (contract, trim math, result validation, versioned
local-file cache key, bounded in-memory cache, disabled-by-default `SilenceTrim`
config, focused tests) implemented and validated 2026-08-18. Second slice (silence
scan inside the background normalization-analysis task plus DI-registered shared
analysis cache) implemented and validated 2026-08-18. Third slice (plain-audio
transport/load/seek/end application) implemented and validated 2026-08-18. The
Player validation host gained the `--trim-silence` startup flag after the transport
commit. The real native-file analyzer/transport smoke was not run and remains
pending (see Player Validation-Host Flag).

## Objective

Remove avoidable silent lead-in and lead-out from explicitly configured local audio
without delaying transport startup or changing default playback. A versioned media
analysis record and bounded cache will also provide the analysis seam needed by Stage
031A's prepared-next workflow.

## Scope (exact)

- Local, plain audio first. Video, audio-with-video, CDG-paired audio, live inputs, and
  streaming inputs are excluded until synchronized offset application is proven safe.
- Public trim options cover threshold, minimum edge-silence duration, padding, and
  independent leading/trailing enablement.
- Trimming is disabled by default. Disabled options preserve current duration, seek,
  loop, normalization, startup, and end-notification behavior.
- Analysis runs ahead of playback and never performs a synchronous scan during open.
- Analysis reports source, playable, and effective durations, detected silence regions,
  and normalization gain.
- Cache entries are keyed by media identity, analyzer version, and normalized analysis
  options, bounded, and validated before use.

## Pinned Decisions

1. **Defaults** - trim is disabled; threshold is `-50 dBFS`; minimum qualifying edge
   silence is `0.5 s`; retained edge padding is `0.1 s`; leading and trailing trim are
   independently enabled when the overall feature is enabled.
2. **Option normalization** - threshold clamps to `[-96, 0] dBFS`; minimum and padding
   clamp to `[0, 30] s`; non-finite values use their documented defaults.
3. **Duration definitions** - source duration is the measured file duration; playable
   duration is the source window remaining after eligible edge trim; effective duration
   is the host-facing progress/scheduling duration. Effective equals source while trim
   is disabled and equals playable when trim is enabled.
4. **Trim math** - only silence regions touching the source start/end qualify. Regions
   shorter than the configured minimum are ignored. Padding retains the requested
   amount inside each qualifying silent edge. Results always clamp to a non-negative,
   non-inverted source window.
5. **Versioning** - analyzer version is a positive integer. Version `1` pins the first
   record shape and edge-trim semantics. Detection, normalization, or record-validation
   changes that can alter reusable results require a version bump.
6. **Cache** - the first cache is process-local, thread-safe, single-analyzer-version,
   and bounded to 256 entries. Version mismatch and invalid records are misses, so a
   version bump creates a fresh cache generation rather than serving old entries.
   Persistent storage is not required for the first slice. The second slice registers
   one shared process-lifetime instance (`MediaAnalysisCache.Shared`) into each Core DI
   container, so it survives player `Reset`.
7. **Identity** - local-file identity uses normalized full path, file length, and UTC
   last-write ticks. The key also carries normalized trim options because threshold
   changes detection and the remaining options change the computed playable window.
   Cache construction may fail closed to a miss; it never trusts path text alone when
   file metadata is unavailable.
8. **Normalization** - the existing normalization scan, gain math, and cache remain
   untouched in the contract slice. A later analyzer slice may consolidate the caches
   only with regression coverage proving unchanged gain behavior.
9. **API direction** - immutable public `SilenceTrimOptions`, `SilenceRegion`, and
   `MediaAnalysisResult` types form the future `AnalyzeAsync` result/option contract.
   Cache storage and trim calculation remain internal implementation details.
10. **Transport application** - plain-audio transport maps effective position zero
    to the playable source start and synthesizes end at the playable source end.
    Implemented in the third slice (see Third Slice); the mapping applies only when
    a validated `MediaAnalysisResult` is already a cache hit synchronously during
    open.

## First Slice

- Add the public immutable option and result records with validated defaults.
- Add pure edge-trim calculation and result validation.
- Add a versioned local-file cache key and bounded validated in-memory cache.
- Add `MediaPlayerConfiguration.SilenceTrim` with disabled default semantics.
- Add focused xUnit coverage. Do not wire scanning, open, seek, loop, or playback end.

Implemented 2026-08-18 and validated (see Validation). No scanning, open, seek,
loop, or playback-end wiring is included in this slice.

## Second Slice

- Enables the silence scan for enabled plain local non-tracker audio: a cancellable,
  sequential 0.1-second scan runs inside the existing background normalization-analysis
  task, sampling levels at actual BASS decode byte positions (`ChannelGetPosition` /
  `ChannelBytes2Seconds`).
- The analysis task captures the decode cursor before scanning and restores it in a
  `finally` block; the existing `CancelNormalizationAnalysis` increments the generation,
  cancels, and synchronously joins the previous task before the caller reuses or frees
  the decode handle.
- Partial, faulted, and stale (superseded-generation) scans are never cached.
- The existing normalization scan, gain math, and gain cache are preserved. A
  normalization-gain cache hit skips the peak rescans but still performs a missing
  silence scan.
- Validated results are produced by `SilenceTrimCalculator` (including normalization
  gain) and stored through the process-lifetime shared bounded 256-entry
  `MediaAnalysisCache` (`MediaAnalysisCache.Shared`), registered as a singleton into
  every Core DI container so it survives player `Reset`.
- A media-analysis cache hit applies the stored normalization gain and skips rescanning
  entirely.
- CDG-paired opens explicitly disable silence analysis; video, archives, tracker/MOD
  formats, and live/stream inputs remain excluded.
- Transport/duration/seek/loop/end trimming is still NOT implemented: playback behavior
  remains unchanged even when the scan is enabled.
- Focused coverage: 30 030B cases total (17 existing `SilenceTrimAnalysisTests` + 11
  new `SilenceRegionDetectorTests` + 2 new `AudioPipelineAnalysisTests` gating cases).

Implemented 2026-08-18 and validated (see Validation). The native-file analyzer smoke
(scan completion, cursor restore, reopen cache hit, gain unchanged) was not run and
remains pending; no transport/load/seek/end application is included in this slice.

## Third Slice

- Transport trim applies only when a validated `MediaAnalysisResult` is already a
  cache hit synchronously during a local plain non-tracker audio `Open`. A late
  background scan only warms the cache for a subsequent open and never tightens
  duration during active/current playback.
- Internal `IAudioTrimWindowProvider` (on `IAudioEngine`) exposes the active trim
  window (`MediaAnalysisResult? ActiveTrimWindow`) to `AudioPipeline`.
- `AudioPipeline` maps source/effective coordinates: effective duration
  (`_trimWindow?.EffectiveDuration ?? Engine.Duration`), effective position
  (`SourceToEffective`), effective→source seek (`EffectiveToSource`), and
  progress/end publication in effective space. Stop/replay and pause/resume behave
  consistently; a zero playable duration makes `Play` return `false`.
- `AudioEngine` positions `Play` at `PlayableStart` when the source position is
  outside the playable window, suppresses the native source-end loop while a trim
  window is active, uses a BASS `Position | Mixtime | Onetime` sync at
  `PlayableEnd`, stops at the custom end, and custom loops seek to `PlayableStart`
  using the `BASS_POS_FLUSH` raw flag `0x1000000` (ManagedBass omits the enum
  member). Official BASS docs support mixtime `SetPosition` custom loops; tempo
  changes alter wall-clock arrival but do not require rearming the audio-position
  sync.
- CDG-paired opens explicitly analyze `false`; video, archives, tracker/MOD
  formats, and live/stream inputs remain excluded. Disabled/default behavior
  remains identity.
- Focused coverage: 44 030B cases total (17 existing `SilenceTrimAnalysisTests` +
  11 `SilenceRegionDetectorTests` + 2 `AudioPipelineAnalysisTests` gating cases +
  14 new `AudioPipelineTrimTests` transport cases).

Implemented 2026-08-18 and validated (see Validation). The real native-file smoke
(cache-warm then reopen; trimmed leading start; exact tail end; repeated custom
loops; tempo change mid-play; seek-to-effective-end then Play; normalization
unchanged; exclusions/default path unchanged) was not run and remains pending. The
first uncached `Open` only warms the cache; a future explicit pre-analysis /
prepared-next path supplies first-play cache.

## Player Validation-Host Flag (pending native smoke)

Added after the third-slice transport commit: `--trim-silence` sets
`MediaPlayerConfiguration.SilenceTrim = new SilenceTrimOptions { Enabled = true }`
and the status line reports `Silence trim=True`. Pending native-file smoke:

```powershell
dotnet run --project FatzBomb.MediaControl.Player -- --trim-silence <audio-path>
dotnet run --project FatzBomb.MediaControl.Player -- --software --trim-silence <audio-path>
```

- First open warms the cache; wait for the `Silence analysis complete` diagnostic,
  then close and reload the same file to activate the cached transport window.
- Verify: trimmed leading start, effective duration/seek, exact tail end, repeated
  loops, tempo change mid-play, seek-to-effective-end then Play, normalization
  unchanged, and exclusions/default path unchanged.
- Automated gate re-verified after the host flag: Release build 0 warnings/errors,
  508 tests, `dotnet format` verification, Core / WPF / presentation-parity gates,
  and git diff check all pass. Manual smoke has not been run; the stage stays active.

## API Compatibility Note

- Public additions are additive and trimming defaults to disabled.
- `MediaPlayerConfiguration` is a `readonly record struct`; adding one `init` property
  grows its value layout. This is source-compatible but binary/ABI-relevant pre-1.0.
- Existing `Duration` behavior is unchanged while trim is disabled; the transport
  slice (implemented and validated 2026-08-18) applies effective duration only when
  a validated cache hit activates a trim window.

## Acceptance Criteria

### Contract and Cache Foundation

- Default options disable trimming and normalize invalid values deterministically.
- Pure trim math handles leading-only, trailing-only, both-edge, minimum, padding,
  malformed-region, and non-inverted-window cases.
- Analysis results distinguish and validate all three duration definitions.
- Cache keys include analyzer version and stable local-file metadata.
- Cache reads reject version mismatches and invalid records; capacity is bounded.
- No production load/play/seek/loop/end path consumes the new options in this slice.

### Analyzer and Transport (implemented; native-file smoke pending)

- Analysis is cancellable, restores the decode-handle position, and is joined before
  handle reuse or disposal — implemented; native-file smoke of scan completion and
  cursor restore is pending.
- Cache hits avoid rescanning and do not add measurable local-file open cost —
  implemented; reopen-cache-hit and gain-unchanged native-file smoke is pending.
- Enabled trim applies only to plain local audio and maps seek/progress/end
  consistently — implemented (third slice); native-file smoke of the trimmed
  transport behavior is pending.
- Disabled trim preserves current playback behavior and normalization results —
  validated.

## Validation

Baseline before this stage: Release build 0 warnings/errors, 464 tests, format and Core /
WPF / presentation-parity gates passing.

First-slice validation 2026-08-18: Release build 0 warnings/errors, 481 tests pass
(including 17 focused `SilenceTrimAnalysisTests` cases), `dotnet format` verification,
Core / WPF / presentation-parity gates, and git diff check — all pass. Manual smoke
does not apply to this slice: no analyzer scan or transport behavior is wired yet.

Second-slice validation 2026-08-18: Release build 0 warnings/errors, 494 tests pass
(30 focused 030B cases: 17 existing `SilenceTrimAnalysisTests` + 11
`SilenceRegionDetectorTests` + 2 `AudioPipelineAnalysisTests` gating cases),
`dotnet format` verification, Core / WPF / presentation-parity gates, and git diff
check — all pass. The real native-file analyzer smoke (scan completion, cursor
restore, reopen cache hit, gain unchanged) was not run and remains pending; no
transport behavior is wired yet, so no transport smoke applies.

Third-slice validation 2026-08-18: Release build 0 warnings/errors, 508 tests pass
(44 focused 030B cases: 17 `SilenceTrimAnalysisTests` + 11
`SilenceRegionDetectorTests` + 2 `AudioPipelineAnalysisTests` gating cases + 14
`AudioPipelineTrimTests` transport cases), `dotnet format` verification, Core / WPF /
presentation-parity gates, and git diff check — all pass. The real native-file smoke
(cache-warm then reopen; trimmed leading start; exact tail end; repeated custom
loops; tempo change mid-play; seek-to-effective-end then Play; normalization
unchanged; exclusions/default path unchanged) was not run and remains pending; the
first uncached `Open` only warms the cache for a subsequent open.

```powershell
dotnet build -c Release FatzBomb.MediaControl.sln --no-restore
dotnet test -c Release FatzBomb.MediaControl.sln --no-build --no-restore
dotnet format FatzBomb.MediaControl.sln --verify-no-changes --no-restore
.\scripts\check-core-boundary.ps1
.\scripts\check-wpf-boundary.ps1
.\scripts\check-presentation-parity.ps1
```

## Risks

- BASS analysis scans move the decode-handle cursor; cancellation and cursor restoration
  must precede stream reuse or release — mitigated in the second slice by cursor restore
  in `finally` and join-before-reuse, pending native-file smoke.
- Tail trimming needs a synthetic end boundary; relying only on BASS's source-end sync
  would play the trimmed tail and break loop timing.
- Effective-space seek and source-space decode positions can diverge. Every seek, loop,
  progress, and completion path must use one pinned mapping — implemented in the third
  slice (`AudioPipeline.SourceToEffective` / `EffectiveToSource`); native-file smoke
  pending.
- CDG and video synchronization must not inherit plain-audio offsets accidentally.
- A cache entry is optimization data, never authoritative input; malformed or stale data
  must degrade to a miss.
- Fully silent media can produce a zero-length playable window (playable start equals
  end, effective duration 0) — the transport slice handles it: `Play` returns `false`
  for a zero effective duration; native-file smoke pending.

## Explicit Non-Goals

- Video, synchronized media, CDG, live, or streaming trimming.
- Auto-enabling trim based on content heuristics.
- Synchronous full-file analysis during open.
- An unbounded or unversioned cache.
- Audio filter framework work from Stage 020A.

## Coordination

- Preserves the current normalization signal chain and prepares the analysis result for
  Stage 031A's depth-one prepared-next slot.
- Stage 031A remains next after 030B is completed and manually validated.
