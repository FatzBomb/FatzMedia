# Active Stage 041A - Engine-Internal Follow-Ups

Prepared: 2026-10-18 (idea), activated 2026-10-18 (owner: "do 041a").
Status: slices 1-3 (the three 015A seams: loop-boundary rules, presentation-lead rules,
post-seek recovery) implemented; owner smoke passed 2026-10-18 (`updates/041A_1` to
`updates/041A_3`).
The 010A/019A, 017A and 022A items are not started.

Small optional follow-ups left by stages completed on 2026-10-18, collected here
so the stages could close. Each item is its own bounded slice. Pick one at a time, measure
first where noted, and leave out any that stop paying for themselves.

## From 015A (VideoPipeline responsibility split)

Next seams, in order:
1. Loop boundary math: `GetLoopOverflowPosition`, the end tolerance, natural-end detection.
2. The presentation-lead controller: `UpdateAdaptivePresentationLead` and its thresholds.
3. The post-seek recovery state machine.

Each is an extraction with focused tests and no behavior change.
- Slice 1 (seam 1) implemented: `Pipeline/VideoLoopBoundary.cs` (`updates/041A_1`).
- Slice 2 (seam 2) implemented: `Pipeline/PresentationLeadController.cs` (`updates/041A_2`).
- Slice 3 (seam 3) implemented: `Pipeline/PostSeekRecovery.cs` (`updates/041A_3`).

## From 010A and 019A (software video copy path, frame ownership)

- Remove the decoder-to-coordinator copy ("copy 3"): an explicit frame lease from
  `VideoDecoder` instead of copy and return. 019A's written ephemeral contract is the
  starting point.
- Direct3D11: map-discard dynamic texture upload from the retained buffer. Measure first.

## From 017A (display-rate presentation)

- Display-locked timing as the default for Direct3D11 native windows, only if later smokes
  show it ahead of source cadence.
- Vertical-blank timing for WPF-composited and Vulkan surfaces.

## From 022A (thread model)

- Re-capture `cdg-2` and `video-hw-2` with `scripts/capture-thread-baseline.ps1`, then choose
  the next simplification from the new data, if any.
