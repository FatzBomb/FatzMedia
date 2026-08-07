# Idea Stage 030B - Silence Trimming and Media Analysis Cache

Status: Good idea. Recommended after Stage 030A.

## Why This Is Worth Doing

Silent lead-in and lead-out waste venue time and create dead air between tracks, and
per-load analysis is too slow to run synchronously at open time. A deliberate trim
feature backed by a versioned analysis cache lets normalization and trim be computed
ahead of playback and reused, while keeping the default behavior unchanged.

## Recommended Direction

- Audio-only first. Video and synchronized (audio-with-video) media are excluded until
  offset application can be verified safe.
- Trim options: threshold, minimum, padding, and independent leading/trailing control -
  threshold (silence level), minimum (ignore trim segments shorter than this),
  padding (keep a defined amount of silence at each edge), and leading/trailing
  enabling each edge separately.
- Disabled by default; trimming only happens when explicitly configured.
- Analysis runs ahead of playback: normalization and trim are computed by the analysis
  path (feeding Stage 031A's `AnalyzeAsync` and the prepared-next workflow) rather than
  during transport startup.
- Report distinct durations with documented definitions: source duration (measured file
  duration), playable duration (what the transport actually plays after trimming), and
  effective duration (the host-facing value accounting for configured trim, used for
  progress and scheduling).
- Versioned cache semantics: entries are keyed by media identity plus the analyzer
  version, stale entries are invalidated when the version changes, and cached results
  are validated before use rather than trusted blindly.

## Recommended Immediate Action

Define the analysis record (source/playable/effective durations, silence regions,
normalization gain), the cache key and versioning rule, and the trim option model with
defaults before implementing either side.

## Must Preserve

- Existing normalization behavior and the signal chain mapped in Stage 020A.
- Audio/video synchronization for non-trimmed media.
- Local-file startup performance; cache lookups must not add measurable open cost.
- No trimming, no behavior change, when the feature is disabled (the default).

## Explicit Non-Goals

- Trimming video or synchronized media in the first slice.
- Auto-enabling trim based on content heuristics.
- An unbounded or unversioned cache that can serve stale analysis.
- Trimming live or streaming inputs; that belongs with Stage 014A.
