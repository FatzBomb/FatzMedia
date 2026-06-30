# FatzBombMedia — Cleanup Plan (Reorganized)

Re-audited and reorganized 2026-06-30. Phase 1 (Stages 0-11) is complete. Phase 2 covers all remaining deferred, zombie, renderer, visibility, and code-smell items.

---

## Phase 1 — Completed Audit & Cleanup

<details>
<summary>Click to expand — ~136 dead items cataloged across API, Controllers, Pipeline, Core, and WPF layers. 11 stages implemented.</summary>

### Cleanup Progress

- [x] Stage 1 — completed: whole-file/self-contained legacy deletions.
- [x] Stage 2 — completed: internal convenience wrappers & dead helpers.
- [x] Stage 3 — completed: public API surface trim (26 MediaControlPlayer members, 8 MediaControls, 2 MediaFile, 2 CdgVisuals, 6 enum values).
- [x] Stage 4 — completed: config/capability pruning (6 config properties + 2 enum types + GpuRenderingCapabilities partial).
- [x] Stage 5 — completed: metadata controller decoupling (deleted Controllers\MediaMetadata.cs, Core\Metadata\MediaMetadata.cs).
- [x] Stage 6 — completed: state/event cleanup (MediaState.Changed/LastError/SetError, IAudioEngine.SeekCompleted/PlaybackStarted, ICdgEngine.PositionChanged).
- [x] Stage 7 — completed: ClockAudioEngine interface split (guarded MediaEffects, made unsupported clock members throw).
- [x] Stage 8 — completed: renderer-adjacent dead API cleanup (6 bridge members, 9 composition items).
- [x] Stage 9 — completed: WPF adapter cleanup (enum values, BitmapScalingMode DP, NativeHwndSurfaceHost extraction).
- [x] Stage 10 — completed: fire-and-forget observe-and-log (S2/S3/S4; S1 deferred; S5 excluded).
- [x] Stage 11 — completed: final validation build (Debug + Release, 0 warnings, 0 errors).

Audit catalog (items 1-128, S1-S15, A1-A7, F1-F19, Renderer Clusters A-F) preserved in git history; this file now focuses on Phase 2 only.

### Completed Fire-and-Forget Items

| Item | Status | File |
|------|--------|------|
| S2 | Observe-and-log implemented | MediaFile.cs:202 |
| S3 | Observe-and-log implemented | MediaFile.cs:225 |
| S4 | Observe-and-log implemented (both .Play() discards) | VideoPipeline.cs:238, :399 |
| S1 | Deferred (self-handling try/catch) | MediaFile.cs:113 |
| S5 | Excluded (throws on failure) | VideoPipeline.cs:1774 |
| S15 | Extracted to shared file | NativeHwndSurfaceHost.cs |

### Completed WPF Adapter Items

| Item | Status |
|------|--------|
| 127 (MediaPresentationMode.NativeHwnd) | Removed |
| 128 (MediaPresentationMode.D3DImage) | Removed |
| BitmapScalingMode DP + wiring | Removed (DP, XAML binding, QUALITY ComboBox, VM props) |
| WPF Player dead ctors (121-126) | 121/122/123/126 already gone; 124/125 live — excluded |

</details>

---

## Phase 2 — Remaining Cleanup Roadmap

### Roadmap Rules

- **Trace before each stage**: verify zero callers for every symbol before removal.
- **Build after each stage**: `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore` (Release = warnings-as-errors).
- **Keep renderer changes isolated**: anything under `Core\Graphics`, `Core\Graphics\D3D`, or `Core\Graphics\Composition` needs manual Player smoke testing per `docs/VALIDATION.md` after build.
- **Respect package risk**: `FatzBombMedia.MediaControl` is packable; removing `public` members may be source-breaking for external consumers.
- **Preserve WPF adapter boundary**: verify no `Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video` imports in `FatzBombMedia.MediaControl.Wpf`.

---

### Cleanup Progress

- [x] Stage 1 — completed 2026-06-30: removed DirectoryScanner single-root convenience overloads, ExactMatch/Incompatible enum values + doc refs; deleted GpuRenderingCapabilities.cs zombie. Debug pass, Release pass.

---

### Stage 1 — Internal Dead Helpers & Zombie-File Deletions

**Goal:** remove confirmed-dead scanner convenience overloads, unused compatibility enum values, and a completely dead zombie file. Low risk, no renderer or threading impact.

**Risk:** Low.

**Scope:**

**1a — DirectoryScanner dead items (items 90-94, corrected by 2026-06-30 review):**
- `LocateFilesDirectory(string rootPath, IEnumerable<string> fileNames, ...)` single-root convenience overload — never called outside its own delegation.
- `LocateFiles(string rootPath, IEnumerable<string> fileNames, ...)` single-root convenience overload — never called outside its own delegation.
- `CompatibilityLevel.ExactMatch` enum value — never passed by any repo caller; only used inside `MatchesCompatibility` branch logic.
- `CompatibilityLevel.Incompatible` enum value — never passed by any repo caller; only used inside `MatchesCompatibility` branch logic.
- Keep `LocateFilesDirectory(IEnumerable<string> rootPaths, ...)` and `LocateFiles(IEnumerable<string> rootPaths, ...)`; both are live from `Bootstrap.LibraryLoader.cs`.
- Keep `CleanList` unless it is deliberately inlined into the live `LocateFiles(IEnumerable<string>, ...)` method; the earlier `CleanPaths` item is stale and no such method exists.
- File: `Core\Files\DirectoryScanner.cs`

**1b — FileTypes.GetFileType (item 108):**
- Excluded as stale. `FileTypes.GetFileType` no longer exists in source, and whole-repo searches found no `GetFileType` references.
- Do not touch `Core\Files\Filetypes.cs` for this item; the `FileTypes` class and its extension sets/helpers are live.

**1c — GpuRenderingCapabilities.cs zombie class (F14):**
- After Stage 4 removed `GetOptimalRenderingMethod` and 4 dead `GpuCapabilityInfo` properties, the remaining file (~173 lines) has zero external consumers. `Capabilities`, `DetectCapabilities()`, `CheckD3D11Support()`, `DetectGpuInfoViaDxgi()`, `GpuRenderingMethod` enum, `GpuCapabilityInfo.SupportsD3D11`, `HasDedicatedGpu` are all self-referencing only.
- Delete the entire file: `Core\Graphics\GpuRenderingCapabilities.cs`
- Remove any remaining `using`/references to `GpuRenderingCapabilities` or `GpuCapabilityInfo` from other files.

**Trace guards:**

```powershell
# 1a — DirectoryScanner dead items, whole repo (include C#, XAML, project/config, docs/scripts/JSON)
git grep -n -E "LocateFile|LocateFiles|CleanPaths|CleanList|CompatibilityLevel|ExactMatch|Incompatible" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
# Confirm only these live external calls remain before implementation:
# - Bootstrap.LibraryLoader.cs -> DirectoryScanner.LocateFiles(searchPaths, ..., CompatibilityLevel.Compatible)
# - Bootstrap.LibraryLoader.cs -> DirectoryScanner.LocateFilesDirectory(searchPaths, ..., CompatibilityLevel.Compatible)

# 1b — stale FileTypes.GetFileType item
git grep -n -E "GetFileType|FileTypes" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
# `GetFileType` should have zero matches. `FileTypes` has many live matches and must remain.

# 1c — GpuRenderingCapabilities zombie
git grep -n -E "GpuRenderingCapabilities|GpuCapabilityInfo|GpuRenderingMethod|DetectCapabilities|CheckD3D11Support|DetectGpuInfoViaDxgi|HasDedicatedGpu|SupportsD3D11" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
# Matches should be limited to Core\Graphics\GpuRenderingCapabilities.cs and this plan file before deletion.
```

**Latest review findings — 2026-06-30:**

- **Safety:** Low, if the live multi-root scanner overloads and live `FileTypes` members are excluded.
- **Safe items:** remove the two single-root `DirectoryScanner` convenience overloads; remove `CompatibilityLevel.ExactMatch` and `CompatibilityLevel.Incompatible` plus their branch/doc references; delete `Core\Graphics\GpuRenderingCapabilities.cs` because all remaining matches are self-contained in that file.
- **Exclusions/defer:** do not remove `LocateFiles(IEnumerable<string> rootPaths, ...)` or `LocateFilesDirectory(IEnumerable<string> rootPaths, ...)`; both are live from `Bootstrap.LibraryLoader.cs`. Do not remove `CleanList` unless it is inlined into the live `LocateFiles` implementation. Do not touch `Filetypes.cs` for `GetFileType`; that method is already absent and `FileTypes` is live. Do not touch WPF/adapter files.
- **Corrected whole-solution guards:** use the `git grep` commands above, or equivalent OpenCode whole-repo grep, across C#, XAML, project/config, docs/scripts, JSON/config, and reflection/name strings before implementation. Project-local cleanup is allowed only after those whole-repo guards match the reviewed results.
- **Likely files affected:** `Core\Files\DirectoryScanner.cs`; delete `Core\Graphics\GpuRenderingCapabilities.cs`. `Core\Files\Filetypes.cs` should not be modified for this stage.
- **Smoke tests:** build is the primary verification. If native DLLs are available, run the Player and confirm startup/native bootstrap still locates BASS/FFmpeg DLLs and supported audio/video extensions populate normally.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** `Core\Files\DirectoryScanner.cs`, delete `Core\Graphics\GpuRenderingCapabilities.cs`. No `Filetypes.cs`, WPF, or adapter changes.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Exit criteria:** Debug and Release builds pass; `GpuRenderingCapabilities.cs` is deleted; only live multi-root directory-scanner logic remains; `FileTypes` live members continue to compile and function.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 2 — Renderer Stage A: Remove CPU Upload Chain

**Goal:** remove the old CPU/BGRA frame upload path (`UploadFrame` chain) — dead since the bridge member was removed in Phase 1 Stage 8.

**Risk:** Medium (renderer risk). Build catches references, but smoke-test image/BGRA fallback, software-decoded fallback, hardware video, and CDG after removal.

**Scope:**

The bridge member `NativeD3D11PresentationBridge.UploadFrame(...)` was removed in Phase 1 Stage 8. Now remove the downstream chain:

- `D3D11RendererPresentationServices.UploadCpuFrame(...)` — `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`
- `D3D11PresentationFramePresenter.UploadCpuFrame(...)` — `Core\Graphics\D3D\Presentation\D3D11PresentationFramePresenter.cs`
- `D3D11FramePresenter.UploadCpuFrame(...)` — `Core\Graphics\D3D\Frame\D3D11FramePresenter.cs`
- Delete `D3D11FrameUploadPipeline.cs` — `Core\Graphics\D3D\Upload\D3D11FrameUploadPipeline.cs` (complete dead file if `UploadCpuFrame` is removed)
- Delete `D3D11TextureUploadCache.cs` — `Core\Graphics\D3D\Upload\D3D11TextureUploadCache.cs` (complete dead file if CPU upload path is removed)
- `D3D11FrameUploadResources.TextureCache` and stale upload helpers — `Core\Graphics\D3D\Upload\D3D11FrameUploadResources.cs` **Do NOT delete the whole file** — `RecordUpload(...)` is still live for hardware-frame diagnostics. Remove only `TextureCache`, `RecordUploadWithCurrentTarget(...)`, and any methods that are only called by the dead UploadCpuFrame chain.

**Exclusions (do NOT touch):**
- `D3D11FrameUploadResources.RecordUpload(...)` — live (hardware-frame diagnostics)
- Any remaining `D3D11FrameUploadResources` members that have live callers
- All other renderer clusters (B/C/D/E/F) — out of scope for this stage

**Trace guards:**

```powershell
rg -n "UploadCpuFrame" . -g "*.cs"
rg -n "D3D11FrameUploadPipeline|D3D11TextureUploadCache|TextureCache|RecordUploadWithCurrentTarget" . -g "*.cs"
# Confirm RecordUpload is live (must remain):
rg -n "RecordUpload\(" . -g "*.cs"
```

**Likely files affected:** 5 files (see scope); delete 2 files.

**Manual verification:** run Player — image/BGRA playback, software-decoded video fallback, hardware video, CDG, sidecar image background.

---

### Stage 3 — Renderer Stage B: Remove D3DImage/Shared-Surface Backend

**Goal:** remove the UI shared-surface / D3DImage-era backend scaffolding. The current WPF surfaces always attach as native HWND descriptors; no live caller invokes `CreateUiSharedSurface(...)`.

**Risk:** Medium-High (API + renderer risk). The `ID3D11UiSharedSurfacePresentationBackend` interface is public.

**Scope:**

- Delete `ID3D11UiSharedSurfacePresentationBackend.cs` — `Core\Graphics\D3D\Presentation\ID3D11UiSharedSurfacePresentationBackend.cs`
- Delete `NullD3D11UiSharedSurfacePresentationBackend.cs` — `Core\Graphics\D3D\Presentation\NullD3D11UiSharedSurfacePresentationBackend.cs`
- Remove `CreateUiSharedSurface(...)` from `D3D11RendererPresentationServices.cs`
- Remove `CreateUiSharedSurface(...)`, `ActivateUiSharedSurface(...)`, `EnsureUiSharedSurface(...)` from `D3D11PresentationSurfaceController.cs`
- Remove `D3D11PresentationBackendKind.UiSharedSurface` enum value from `D3D11PresentationBackendKind.cs`
- Update `NativeD3D11PresentationBridge.cs` ctor — remove the `new NullD3D11UiSharedSurfacePresentationBackend()` parameter

**Trace guards:**

```powershell
rg -n "UiSharedSurface|ID3D11UiSharedSurface|NullD3D11UiSharedSurface|CreateUiSharedSurface|ActivateUiSharedSurface|EnsureUiSharedSurface" . -g "*.cs" -g "*.xaml" -g "*.json"
rg -n "D3D11PresentationBackendKind\.UiSharedSurface" . -g "*.cs"
```

**Likely files affected:** ~5 files; delete 2 files.

**Manual verification:** run Player — primary DirectComposition, native HWND fallback, secondary output. Confirm WPF adapter boundary clean.

---

### Stage 4 — Renderer Stage C: Remove Immediate Clear/Refresh/Resize APIs

**Goal:** remove immediate resize/clear/refresh APIs orphaned after Phase 1 Stage 8 removed the bridge methods. The live coordinator queues resizes and uses composition-empty clearing.

**Risk:** Medium.

**Scope:**

Bridge members `ResizeNativeHwndPresentation(...)`, `RefreshNativePresentation(...)`, `ClearPresentedFrame(...)` were removed in Phase 1 Stage 8. Now remove the downstream chains:

- `D3D11RendererPresentationServices.ClearPresentedFrame(...)` / `ResizeNativeHwndPresentation(...)`
- `D3D11PresentationSurfaceController.ClearPresentedFrame(...)` / `ResizeNativeHwndPresentation(...)`
- `D3D11PresentationBackendSet.ClearPresentedFrame(...)` / `ClearPresentedTargets(...)`
- `D3D11NativeHwndSwapChainBackend.Resize(...)` — immediate-helper overload; keep `ApplyPendingResizeIfNeeded(...)` (live/queued).

**Trace guards:**

```powershell
rg -n "ClearPresentedFrame\b|ResizeNativeHwndPresentation|RefreshNativePresentation" FatzBombMedia.MediaControl -g "*.cs"
# Verify queued resize path stays live:
rg -n "ApplyPendingResizeIfNeeded|QueueNativeHwndResize" FatzBombMedia.MediaControl -g "*.cs"
```

**Likely files affected:** 4 files.

**Manual verification:** resize, live resize, audio-only clear, stopped/idle CDG clear, secondary output close/open.

---

### Stage 5 — Renderer Stage D: Remove Direct DXCDG/Single-Target Present

**Goal:** remove direct DXCDG snapshot presentation path orphaned after Phase 1 Stage 8 removed `PresentDxCdgSnapshotVisible`. The live path uses `PresentMediaCompositionVisible(...)` and the composition-frame fan-out.

**Risk:** Medium-High.

**Scope:**

Bridge member `PresentDxCdgSnapshotVisible(...)` was removed in Phase 1 Stage 8. Now remove the downstream chain:

- `D3D11RendererPresentationServices.PresentDxCdgSnapshotVisible(...)`
- `D3D11MediaCompositionPresenter.PresentToTarget(...)` single-target overloads
- `D3D11PresentationBackendKind.ExternalTexture` only if unused after `PresentToTarget` removal

**Trace guards:**

```powershell
rg -n "PresentDxCdgSnapshotVisible|PresentToTarget\(" FatzBombMedia.MediaControl -g "*.cs"
rg -n "ExternalTexture" FatzBombMedia.MediaControl -g "*.cs"
```

**Likely files affected:** 2-3 files.

**Manual verification:** CDG-only, audio+CDG, video+CDG, DirectComposition overlay split, secondary output.

---

### Stage 6 — Renderer Stage E: Prune Direct Blit/Texture Helpers

**Goal:** remove unused direct-blit and helper-present paths not consumed by the current composition fan-out.

**Risk:** Medium (depends on Clusters A/C/D being removed first for trace clarity).

**Scope:**

- `D3D11PresentationBlitter.BlitToActivePresentation(...)`
- `D3D11PresentationFramePresenter.PresentTextureFrame(...)`
- `D3D11PresentationBackendSet.Present(...)`
- `D3D11PresentationBackendSet.HasNativePresentingBackend`
- `D3D11PresentationBackendSet.HasUiSharedSurfaceBackend`
- `D3D11PresentationRenderTargetViewCache.Current` and no-op `Release()` path (confirm no future code assigns `Current`)

**Trace guards:**

```powershell
rg -n "BlitToActivePresentation|PresentTextureFrame|HasNativePresentingBackend|HasUiSharedSurfaceBackend|PresentationRenderTargetViewCache" FatzBombMedia.MediaControl -g "*.cs"
```

**Likely files affected:** ~4 files.

**Manual verification:** same as Clusters A/C/D. Any media mode that exercises the composition fan-out.

---

### Stage 7 — Renderer Stage F: Decide Offscreen Retained-Layer Future

**Goal:** either keep the offscreen retained-layer scaffolding as documented future architecture, or delete it. This is a decision stage.

**Risk:** High if deleted broadly. Much of this feeds diagnostics and plan summaries.

**Deferred items from Phase 1 Stage 8:** `D3D11CompositionTargetKind.RetainedOffscreenLayer` (118), `D3D11CompositionTargetKind.FutureSwapChain` (119).

**Scope (if DELETE is chosen):**

- `D3D11CompositionLayerRenderTarget.cs` — `Ensure(...)`, `MarkRendered(...)`, resource properties
- `D3D11CompositionLayerState.OffscreenRenderTarget`, `HasOffscreenRenderTarget`
- `D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture`
- `D3D11CompositionTargetKind.RetainedOffscreenLayer`, `FutureSwapChain`
- `D3D11CompositionLayerUpdateCadence.DisplayRate`
- `D3D11CompositionLayerUpdateAction.ClearOnly`
- `D3D11CompositionLayerSchedule.SourceFrameInterval`
- `D3D11CompositionTargetDescriptor.PrimaryFinalTarget(...)` factory
- `D3D11CompositionTargetRegistry.Capture(D3D11CompositionTargetDescriptor)` single-target overload
- `D3D11CompositionLayerCache.Layers`, `ActiveLayerSummary`
- `D3D11CompositionTargetPlan.Targets`, `HasTargets`, `HasWork`, `ActivePresentationTargetIds`

**Scope (if KEEP is chosen):** do nothing. Document that offscreen retained-layer is a planned feature with scaffolding in place.

**Trace guards:**

```powershell
rg -n "RetainedOffscreen|FutureSwapChain|OffscreenRenderTarget|DisplayRate|ClearOnly|SourceFrameInterval|PrimaryFinalTarget|RetainedOffscreenTexture" FatzBombMedia.MediaControl -g "*.cs"
```

**Recommendation:** defer the decision. Keep the scaffolding until there is a clear product decision to abandon offscreen retained-layer rendering.

---

### Stage 8 — Visibility & Code-Smell Fixes

**Goal:** reduce overly-broad visibility and fix minor code smells without changing behavior.

**Risk:** Low-Medium. No renderer or threading impact.

**Scope:**

**8a — Visibility reductions:**
- `CdgPipeline.Clock` — `public` → `private` (item S12; zero external reads)
- `RenderPipeline.CurrentPosition` — `public` → `private` (item S13; zero external reads)
- `Bootstrap.IsBassLoaded`, `IsFfmpegLoaded`, `RootPath`, `BassPath` — `public` → `internal` (item S14; only read within partial class)

**8b — Minor code-smell fixes:**
- `PipelineMediaEngine.cs:569-570` — double assignment of `ImagePath` (item S10; set → cleared by `Close()` → set again)
- `PipelineMediaEngine.cs:236-240` — empty `finally` block (item S11; remove if the concerns are already handled elsewhere)
- `RenderPipeline.cs:58` — cast `_video is VideoPipeline` to call `RenderCurrentFrame` (item S6; won't work with non-VideoPipeline IVideoEngine — fix or document)

**8c — Additional dead items from original audit (if still present):**
- `VideoPipeline._presentationDiagnosticMaxTargetLateMs` (item 53)
- `VideoPipeline._presentationDiagnosticLeadMs` (item 54)
- `VideoPresentationTiming.GetBackgroundFrameLeadTolerance` (item 104)
- `AudioNormalizer.CalculateEffectiveVolume` (item 102)
- `PipelineMediaEngine.SeekAsync` (item 71)

**Trace guards:**

```powershell
rg -n "CdgPipeline.*\.Clock\b" . -g "*.cs"
rg -n "\.CurrentPosition\b" . -g "*.cs"
rg -n "IsBassLoaded|IsFfmpegLoaded|RootPath|BassPath" . -g "*.cs"
rg -n "_presentationDiagnosticMaxTargetLateMs|_presentationDiagnosticLeadMs" FatzBombMedia.MediaControl
rg -n "GetBackgroundFrameLeadTolerance" FatzBombMedia.MediaControl
rg -n "CalculateEffectiveVolume" FatzBombMedia.MediaControl
```

**Likely files affected:** `CdgPipeline.cs`, `RenderPipeline.cs`, `Bootstrap.cs`, `PipelineMediaEngine.cs`, `VideoPipeline.cs`, `VideoPresentationTiming.cs`, `AudioNormalizer.cs`.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

---

### Stage 9 — Final Validation Build & Documentation Sync

**Goal:** close out Phase 2.

**Risk:** none (validation only).

**Commands:**

```powershell
dotnet build FatzBombMedia.sln --no-restore
dotnet build -c Release FatzBombMedia.sln --no-restore
```

**Manual checklist:** use `docs/VALIDATION.md` after any stage that touched WPF surface management or native presentation.

**Docs to update:**

- Remove completed items from `AGENTS.md` `Known Dead Code` (A4 `D3DImage`, A5 `BitmapScalingMode` DP, A6 `totalLibraries` — all now gone).
- Mark this plan file as complete.

---

## Quick Reference — Remaining Deferred Items

| Ref | Description | Status |
|-----|-------------|--------|
| S1 | `MediaFile.cs Task.Run(HandlePlayOnLoadAsync)` | Deferred (self-handling try/catch; minimal disposed-guard possible) |
| S6 | `RenderPipeline` cast to `VideoPipeline` | In Stage 8 |
| S7 | `ClockAudioEngine` ~60% no-op members | Deferred (no broad IAudioEngine split unless explicitly approved) |
| S10 | `PipelineMediaEngine` double `ImagePath` assignment | In Stage 8 |
| S11 | `PipelineMediaEngine` empty `finally` block | In Stage 8 |
| S12-S14 | Visibility reductions | In Stage 8 |
| F11 | `ClockAudioEngine` no-op members (Full-file cleanup) | Deferred |
| §11.7 | Renderer Cluster F (offscreen scaffolding) | In Stage 7 (decision pending) |
| 90-94 | DirectoryScanner dead members + ExactMatch/Incompatible | Removed (Stage 1) |
| F14 | `GpuRenderingCapabilities.cs` zombie file | Removed (Stage 1) |
| 108 | `FileTypes.GetFileType` | Stale (method already absent) |
