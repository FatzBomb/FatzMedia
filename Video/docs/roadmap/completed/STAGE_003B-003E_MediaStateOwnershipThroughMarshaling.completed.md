# Completed Stage — 003B–003E MediaState Ownership Through Marshaling

Status: Complete
Completed: 2026-07-05

## Original Goal

003B: Map every `MediaState` writer, public event origin, and thread context to identify state ownership conflicts. No source edits.

003C: Add a `System.Threading.Lock` to `MediaState` and guard all setters and getters.

003D: Consolidate `SetOpened` ownership so the `PlaybackCoordinator` is the sole `Content` writer for paired audio+cdg opens.

003E: Route `MediaStarted` and `MediaStopped` through the operation queue to match `MediaEnded`, ensuring all three public events fire on the same thread and are serialized in order.

## What Changed

**003C — Thread-safe MediaState**
- `MediaState.cs`: added `private readonly Lock _gate = new();`, converted five auto-properties to explicit backing fields with lock-guarded getters, wrapped all five mutators in `lock (_gate)` including equality short-circuits.

**003D — Consolidate SetOpened ownership**
- `AudioPipeline.cs`: added `_ownStateForCurrentOpen` field; split `Open(string)` and `Open(IArchiveEntryInfo)` into public delegating (`ownState: true`) and internal `Open(..., bool ownState)` overloads; suppressed `SetOpened` in `Open` and `OnAudioOpened` when `ownState: false`.
- `PlaybackCoordinator.cs`: `OpenAudioCdg` methods call `Open(audioPath, ownState: false)` / `Open(audioEntry, ownState: false)`, making the coordinator the sole `SetOpened` writer for paired opens.

**003E — Unified event marshaling**
- `MediaControlPlayer.cs`: `OnControlsMediaStarted` and `OnControlsMediaStopped` now route through `_controlOperations.Enqueue` with null-handler guards, matching `OnControlsMediaEnded`.

## Files Changed

- `FatzBombMedia.MediaControl/Pipeline/MediaState.cs`
- `FatzBombMedia.MediaControl/Pipeline/AudioPipeline.cs`
- `FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs`
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs`

No public API changed. All `internal sealed` / assembly-scoped.

## Validation

- `dotnet build FatzBombMedia.sln -c Release` (warnings-as-errors) — 0 warnings, 0 errors on every stage.
- 003B: docs/mapping only — no source changed (verified by whole-repo grep).
- 003C: build only — Lock is additive synchronization with no logic change.
- 003D: build only — no `ownState: false` path exercised without manual smoke test (audio+cdg paired open).
- 003E: build only — `Dispatcher.BeginInvoke` in the WPF adapter confirms no existing handler depends on synchronous delivery.

Manual smoke test recommended before production deployment: standalone audio, audio+cdg paired, standalone cdg, video, image open/play/stop/loop.

## Behavior Notes

- `MediaState` getters now acquire `_gate` on every read (uncontended Lock enter/exit is ~tens of nanoseconds).
- For paired audio+cdg opens, `AudioPipeline` tracks `_sourcePath`/`IsOpen` but does not claim `Content` — the coordinator is the single owner.
- `MediaStarted`, `MediaStopped`, and `MediaEnded` all fire on the `MediaControlOperationQueue` thread in raise-order, not mixed caller/render/background threads.
- `PresentationFramePresented` remains on the render thread (intentionally not queued — frame-rate path).

## Deferred / Follow-Up Items

- Manual smoke test (audio+cdg paired open in particular) to confirm `Content` flags are correct end-to-end.
- `CdgPipeline.OpenPaired` still does not call `SetOpened` — intentional per current ownership model.

## Next Suggested Stage

**008A — Pure Logic Tests.** Can be promoted early per its stage file. Creates the smallest xUnit test project for `MediaState` transition rules, seek helpers, and other pure logic — directly covering the 003C/003D/003E changes. No native DLLs or GPU required.
