<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED.

# Completed Stage 016B — MediaLoopPacer / Core-Owned Deadline Management

## Status

Completed 2026-07-17. Core now owns all media-loop deadline accumulation, work-time compensation, lateness resynchronization, and optional late yield through `MediaLoopPacer`; platform packages supply only the native wait primitive through `IPrecisionWaiter`.

## Why This Exists

Three presentation/render loops (primary video presentation, background video, and Direct3D11 CDG/subtitle presentation) each independently managed their own deadline tracking with raw `Stopwatch` arithmetic and duplicated late-loop resync logic. Every loop also constructed its own `IPrecisionWaiter` instance. Consolidating into a single Core-owned pacer eliminates duplicated cadence math, ensures consistent lateness behavior across all loops, and makes loops testable via `TimeProvider` injection.

## Delivered

- **`MediaLoopPacer`** in Core (`FatzBomb.MediaControl/Core/Threading/MediaLoopPacer.cs`) — paces a single loop thread against monotonic absolute deadlines. Core owns:
  - Cadence accumulation (fixed-interval `WaitNext` and media-clock-derived `WaitFor`)
  - Work-time compensation (remaining wait is the deadline minus actual elapsed time)
  - Lateness resynchronization (resets cadence instead of issuing catch-up ticks)
  - Optional late yield (short spinner-mitigation wait via `lateYield` parameter)
  - `TimeProvider` injection for deterministic testing
- **`IPrecisionWaiter` / `IPrecisionWaiterFactory`** contracts in Core (`FatzBomb.MediaControl/Core/Threading/IPrecisionWaiterFactory.cs`) — platform packages implement the narrow cancellable-wait primitive; Core never calls `Stopwatch`-based wait math outside the pacer.
  - `SystemPrecisionWaiterFactory` — portable fallback using `WaitHandle.WaitOne`
- **`WindowsPrecisionWaiter`** (`FatzBomb.MediaControl.Windows/Threading/WindowsPrecisionWaiter.cs`) — Windows high-resolution waitable timer (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`), transparent fallback to `WaitHandle.WaitOne` when unavailable.
- **Migrated loops** — all three loops now use `MediaLoopPacer` instead of inline `Stopwatch` + `IPrecisionWaiter`:
  - `VideoPipeline.PresentationLoop` — primary video presentation
  - `PipelineMediaEngine.BackgroundVideoLoop` — background video presentation
  - `MediaPresentationCoordinator.RenderLoop` — Core CDG/subtitle presentation
- **Deterministic tests** — `MediaLoopPacerTests` (9 tests) via `ManualTimeProvider` + `AdvancingWaiterFactory` verify deadline compensation, lateness reset, late yield, fixed-cadence independence via `WaitFor`, high-resolution reflection, disposal, and argument validation.

## Preserved Behavior

- All three migrated loops maintain their existing cadence (source-frame interval for primary video, per-frame for background video, 75 Hz for CDG/subtitle presentation).
- Late-loop behavior is preserved: background-video and CDG/subtitle loops retain their 1 ms anti-spin yield, while primary video resynchronizes and continues immediately.
- Loop restart via seek or background-video restart correctly resets the cadence accumulator (`pacer.Reset()` replaces manual `nextTick = now` assignments).
- `IPrecisionWaiter.IsHighResolution` diagnostic logging in `VideoPipeline` is unchanged — it now reads `pacer.IsHighResolution`.
- `MediaLoopPacer` is the new public Core pacing API for platform renderers and future media loops; native waiting remains behind the existing `IPrecisionWaiter` / `IPrecisionWaiterFactory` contracts.

## Verification

- `dotnet build -c Release FatzBomb.MediaControl.sln --no-restore` — zero warnings.
- `dotnet test` — 285 tests pass.
- WPF adapter boundary confirmed unchanged — `scripts/check-wpf-boundary.ps1` passes.

## Follow-Up

- No immediate follow-up required. Future renderers or loop types should use `MediaLoopPacer` rather than duplicating deadline management.
