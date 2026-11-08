# Stage Update

Stage: 015A.1 - Seek policy and seek-pump timing extraction
Date: 2026-10-16

## Status: IMPLEMENTED - behavior-preserving; covered by the 035A video-seek smoke, 2026-10-17 (stage completed 2026-10-18)

Delivered: `Pipeline/VideoSeekPolicy.cs` (`VideoSeekPolicy`, `AdaptiveSeekPumpTiming`),
`VideoPipeline` delegating to them (-75 lines), 24 cases pinning the values and rules.

## Decisions made without the owner

- Extract only pure/isolated-state seams; no logic edits inside the extracted code.
- Original private method names kept as delegates to minimise call-site churn in a file
  that cannot be exercised natively in the Linux session.

## Verification

- Linux: Release build clean; portable tests 654/654.

## Manual smoke (Windows, native DLLs)

1. Video file: scrub the seek bar quickly back and forth (preview frames keep up; latest
   position wins), release (commit lands at or after the target, no earlier keyframe).
2. Long-GOP file: commit seek mid-GOP resumes at the target.
3. Hardware and `--software` decode both behave as before.
