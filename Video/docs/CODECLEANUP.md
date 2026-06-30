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
- [x] Stage 2 — completed 2026-06-30: removed dead CPU/BGRA upload chain and upload-cache files; kept hardware/composition diagnostics. Builds: Debug pass, Release pass.
- [x] Stage 3 — completed 2026-06-30: removed D3DImage/UI shared-surface backend scaffolding; kept native HWND and DirectComposition paths. Builds: Debug pass, Release pass.
- [x] Stage 4 — completed 2026-06-30: removed immediate clear/resize renderer APIs; kept queued resize and composition presentation paths. Builds: Debug pass, Release pass.
- [x] Stage 5 — completed 2026-06-30: removed direct DXCDG/single-target present APIs; kept composition fan-out and overlay split paths. Builds: Debug pass, Release pass.
- [x] Stage 6 — completed 2026-06-30: removed dead direct blitter/texture presenter/cache helpers; kept hardware and composition fan-out paths. Builds: Debug pass, Release pass.
- [x] Stage 7 — completed 2026-06-30: removed unused offscreen allocation and future-only planner helpers; kept live retained-layer/fan-out metadata. Builds: Debug pass, Release pass.

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
- `D3D11FrameUploadResources.TextureCache` and stale upload helpers — `Core\Graphics\D3D\Upload\D3D11FrameUploadResources.cs` **Do NOT delete the whole file** — `RecordUpload(...)` is still live for hardware-frame diagnostics. Remove only `TextureCache`, `RecordUploadWithCurrentTarget(...)`, `ReleaseCache()`, `IDisposable`, the current-target constructor delegates, and any methods that are only called by the dead UploadCpuFrame chain.
- `D3D11PresentationResourceCleaner` upload-resources dependency and `ReleaseCache()` call — `Core\Graphics\D3D\Presentation\D3D11PresentationResourceCleaner.cs` (only because the CPU upload cache is removed; keep normal surface/resource cleanup intact)

**Exclusions (do NOT touch):**
- `D3D11FrameUploadResources.RecordUpload(...)` — live (hardware-frame diagnostics)
- Any remaining `D3D11FrameUploadResources` members that have live callers
- `D3D11PresentationDiagnostics.RecordUpload(...)` — live diagnostics sink
- `D3D11FramePresenter.PresentHardwareFrame(...)` and the hardware/video-processor path
- Composition fan-out / `PresentMediaCompositionVisible(...)`
- All other renderer clusters (B/C/D/E/F) — out of scope for this stage

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "UploadCpuFrame" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11FrameUploadPipeline|D3D11TextureUploadCache|TextureCache|RecordUploadWithCurrentTarget" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11FrameUploadResources|ReleaseCache\(|getPresentationWidth|getPresentationHeight" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "UploadFrame\(|UploadFrame\b|NativeD3D11PresentationBridge\.UploadFrame" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Confirm RecordUpload is live and must remain:
git grep -n -E "RecordUpload\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Project-local renderer follow-up guards after removal:
git grep -n -E "D3D11FrameUploadPipeline|D3D11TextureUploadCache|TextureCache|RecordUploadWithCurrentTarget|ReleaseCache\(" -- FatzBombMedia.MediaControl
git grep -n -E "RecordUpload\(|PresentHardwareFrame|PresentMediaCompositionVisible|D3D11VideoProcessorPresenter" -- FatzBombMedia.MediaControl
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium. The CPU/BGRA upload chain is internally isolated, but this is renderer code and must be smoke-tested after build.
- **Safe items:** remove `UploadCpuFrame(...)` from `D3D11RendererPresentationServices`, `D3D11PresentationFramePresenter`, and `D3D11FramePresenter`; delete `D3D11FrameUploadPipeline.cs`; delete `D3D11TextureUploadCache.cs`; remove `TextureCache`, `RecordUploadWithCurrentTarget(...)`, `ReleaseCache()`, `IDisposable`, and unused current-target delegates from `D3D11FrameUploadResources`; remove the now-dead `D3D11PresentationResourceCleaner` upload-resources dependency/call.
- **Exclusions/defer:** do not remove `D3D11FrameUploadResources.RecordUpload(...)` or `D3D11PresentationDiagnostics.RecordUpload(...)`; both remain live for hardware-frame presentation diagnostics. Do not touch `PresentHardwareFrame(...)`, `D3D11VideoProcessorPresenter`, `D3D11MediaCompositionPresenter`, composition fan-out, WPF adapter code, or renderer clusters B/C/D/E/F.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found no source/config callers of `NativeD3D11PresentationBridge.UploadFrame`; `UploadCpuFrame` matches are confined to the three renderer methods plus `D3D11FrameUploadPipeline`; upload cache matches are confined to the CPU upload chain/resources; `RecordUpload(...)` remains live and must stay.
- **Likely files affected:** `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationFramePresenter.cs`; `Core\Graphics\D3D\Frame\D3D11FramePresenter.cs`; `Core\Graphics\D3D\Upload\D3D11FrameUploadResources.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationResourceCleaner.cs`; delete `Core\Graphics\D3D\Upload\D3D11FrameUploadPipeline.cs`; delete `Core\Graphics\D3D\Upload\D3D11TextureUploadCache.cs`.
- **Smoke tests:** run Player with image/BGRA playback, software-decoded video fallback, hardware-decoded video, CDG-only/audio+CDG, sidecar image background, resize/live resize, DirectComposition primary surface, and secondary output if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 5 source files; delete 2 files. See the latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** run Player — image/BGRA playback, software-decoded video fallback, hardware video, CDG, sidecar image background.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 3 — Renderer Stage B: Remove D3DImage/Shared-Surface Backend

**Goal:** remove the UI shared-surface / D3DImage-era backend scaffolding. The current WPF surfaces attach only as native HWND descriptors; no live caller invokes `CreateUiSharedSurface(...)`.

**Risk:** Medium (renderer risk). The previous API-risk note was stale: `ID3D11UiSharedSurfacePresentationBackend` is `internal`, not public.

**Scope:**

- Delete `ID3D11UiSharedSurfacePresentationBackend.cs` — `Core\Graphics\D3D\Presentation\ID3D11UiSharedSurfacePresentationBackend.cs`
- Delete `NullD3D11UiSharedSurfacePresentationBackend.cs` — `Core\Graphics\D3D\Presentation\NullD3D11UiSharedSurfacePresentationBackend.cs`
- Remove `CreateUiSharedSurface(...)` and the `ID3D11UiSharedSurfacePresentationBackend` constructor dependency from `D3D11RendererPresentationServices.cs`
- Remove `CreateUiSharedSurface(...)`, `ActivateUiSharedSurface(...)`, `EnsureUiSharedSurface(...)`, and the `UiSharedSurface` display-name branch from `D3D11PresentationSurfaceController.cs`
- Remove UI shared-surface ownership/routing from `D3D11PresentationBackendSet.cs`: constructor parameter, `UiSharedSurface` property, `UiSharedSurfaceTargetId`, active fallback branch, texture ownership branch, target enumeration branch, `Present(...)` shared-surface skip, cleanup call, `TryGetBackend(...)` branch, `NotifyDirty(...)` special case, comments, and `HasUiSharedSurfaceBackend`.
- Remove `D3D11PresentationBackendKind.UiSharedSurface` enum value from `D3D11PresentationBackendKind.cs`
- Update `NativeD3D11PresentationBridge.cs` ctor — remove the `new NullD3D11UiSharedSurfacePresentationBackend()` parameter

**Exclusions (do NOT touch):**
- Native HWND primary and secondary presentation paths.
- DirectComposition single-target and overlay paths.
- `D3D11PresentationBackendKind.ExternalTexture` and direct/single-target present cleanup planned for later stages.
- WPF adapter source files, except only if a boundary/script reference is proven to be stage-local build fallout.
- Stage 4 immediate clear/resize cleanup and Stage 6 direct-blit/texture helper cleanup, except removal of `HasUiSharedSurfaceBackend` because it is directly tied to this stage's backend deletion.

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "UiSharedSurface|ID3D11UiSharedSurface|NullD3D11UiSharedSurface|CreateUiSharedSurface|ActivateUiSharedSurface|EnsureUiSharedSurface" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11PresentationBackendKind\.UiSharedSurface|\bUiSharedSurface\b|ui-shared-final|UiSharedSurfaceTargetId|HasUiSharedSurfaceBackend|NotifyDirty\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3DImage|SharedSurface|shared-surface|CreateUi|ActivateUi|EnsureUi" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "new D3D11RendererPresentationServices|D3D11RendererPresentationServices\(|new D3D11PresentationBackendSet|D3D11PresentationBackendSet\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# WPF adapter boundary guard:
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf

# Project-local follow-up guards after removal:
git grep -n -E "UiSharedSurface|ID3D11UiSharedSurface|NullD3D11UiSharedSurface|CreateUiSharedSurface|ActivateUiSharedSurface|EnsureUiSharedSurface|ui-shared-final|UiSharedSurfaceTargetId|HasUiSharedSurfaceBackend|NotifyDirty\(" -- FatzBombMedia.MediaControl
git grep -n -E "NativeHwndSwapChain|DirectCompositionSingleTarget|CreateNativeHwndSurface|CreateSecondaryNativeHwndSurface|CreateDirectCompositionSurface|NotifyTargetsPresented" -- FatzBombMedia.MediaControl
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium. Whole-repo searches found no XAML/config/script/project callers of the UI shared-surface creation API; matches are confined to the renderer backend scaffolding and docs/scripts references. Graphify output was not available at `C:\Programming\FatzBombMedia\graphify-out\graph.json` or elsewhere under the repo, so this review is based on source inspection and whole-repo text searches.
- **Safe items:** delete `ID3D11UiSharedSurfacePresentationBackend.cs` and `NullD3D11UiSharedSurfacePresentationBackend.cs`; remove `CreateUiSharedSurface(...)` from `D3D11RendererPresentationServices`; remove `CreateUiSharedSurface(...)`, `ActivateUiSharedSurface(...)`, and `EnsureUiSharedSurface(...)` from `D3D11PresentationSurfaceController`; remove `D3D11PresentationBackendKind.UiSharedSurface`; remove the `new NullD3D11UiSharedSurfacePresentationBackend()` bridge constructor argument; remove all `D3D11PresentationBackendSet` UI shared-surface state/routing listed in scope.
- **Exclusions/defer:** do not touch native HWND primary/secondary presentation, DirectComposition single-target/overlay presentation, `D3D11PresentationBackendKind.ExternalTexture`, immediate clear/resize APIs, direct DXCDG/single-target present, direct-blit helpers other than `HasUiSharedSurfaceBackend`, WPF adapter files, or adapter boundary scripts except for documentation/guard updates in this stage plan.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found the removal matches in `NativeD3D11PresentationBridge`, `D3D11RendererPresentationServices`, `D3D11PresentationSurfaceController`, `D3D11PresentationBackendSet`, `D3D11PresentationBackendKind`, and the two shared-surface backend files. It also found documentation/script mentions of `D3DImage`/shared-surface that should not be treated as callers.
- **Project-local follow-up guards:** after removal, the first project-local guard should return no source matches for UI shared-surface backend symbols. The second should still show live native HWND, secondary HWND, DirectComposition, and target notification paths. The WPF adapter boundary guard should remain empty.
- **Likely files affected:** `Core\Graphics\D3D\NativeD3D11PresentationBridge.cs`; `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationSurfaceController.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationBackendSet.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationBackendKind.cs`; delete `Core\Graphics\D3D\Presentation\ID3D11UiSharedSurfacePresentationBackend.cs`; delete `Core\Graphics\D3D\Presentation\NullD3D11UiSharedSurfacePresentationBackend.cs`.
- **Smoke tests:** run Player with primary DirectComposition overlay mode, native HWND fallback/overlay disabled mode, secondary output attach/detach, resize/live resize, stop/idle clear, CDG-only/audio+CDG, software-decoded video fallback, and hardware-decoded video if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 5 source files; delete 2 files. See latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** run Player — primary DirectComposition, native HWND fallback, secondary output. Confirm WPF adapter boundary clean.

**Exit criteria:** Debug and Release builds pass; whole-solution UI shared-surface guards return no source/config callers; native HWND, DirectComposition, secondary target, and WPF adapter boundary guards remain clean/live as described above.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 4 — Renderer Stage C: Remove Immediate Clear/Refresh/Resize APIs

**Goal:** remove immediate resize/clear/refresh APIs orphaned after Phase 1 Stage 8 removed the bridge methods. The live coordinator queues resizes and uses composition-empty clearing.

**Risk:** Medium.

**Scope:**

Bridge members `ResizeNativeHwndPresentation(...)`, `RefreshNativePresentation(...)`, `ClearPresentedFrame(...)` were removed in Phase 1 Stage 8. Now remove the downstream chains:

- `D3D11RendererPresentationServices.ClearPresentedFrame(...)` / `ResizeNativeHwndPresentation(...)`
- `D3D11PresentationSurfaceController.ClearPresentedFrame(...)` / `ResizeNativeHwndPresentation(...)`
- `D3D11PresentationBackendSet.ClearPresentedFrame(...)` / `ClearPresentedTargets(...)`; remove private `ClearActiveTarget(...)` if it becomes uncalled after `ClearPresentedFrame(...)` removal.
- `D3D11NativeHwndSwapChainBackend.Resize(...)` — immediate-helper overload; keep `ApplyPendingResizeIfNeeded(...)` (live/queued).
- `D3D11DirectCompositionSingleTargetBackend.Resize(...)` — related immediate-helper overload; keep `QueueResize(...)`, `ApplyPendingResizeIfNeeded(...)`, and `ResizeCore(...)` because queued DirectComposition resize is live.
- `RefreshNativePresentation(...)` is already absent from source; no source cleanup is expected for that symbol beyond the guard.

**Exclusions (do NOT touch):**
- Public/host surface resize APIs: `IMediaControlSession.ResizePresentationSurface(...)`, `MediaControlPlayer.ResizePresentationSurface(...)`, `NativeMediaPresentationCoordinator.ResizePresentationSurface(...)`, and WPF callers.
- Live resize APIs and state: `SetPresentationSurfaceLiveResize(...)`, `SetNativeLiveResizeInProgress(...)`, `IsNativeLiveResizeInProgress`.
- Queued resize paths: `QueueNativeHwndPresentationResize(...)`, `QueueDirectCompositionPresentationResize(...)`, `QueueNativeHwndResize(...)`, `QueueDirectCompositionResize(...)`, backend `QueueResize(...)`, `ApplyPendingResizeIfNeeded(...)`, `ApplyPendingPresentationResizeIfNeeded(...)`, `ApplyPendingNativeHwndResizeIfNeeded(...)`, `ApplyPendingDirectCompositionResizeIfNeeded(...)`, and backend `ResizeCore(...)`.
- Live clearing/presentation paths: `ClearActiveTargetToBlack(...)`, `ClearTargetToBlack(...)`, `ClearTarget(...)` if still used by surface activation, `ClearDirectCompositionOverlay(...)`, `ClearOverlayToTransparent(...)`, `ClearCompositionBackgroundFrame(...)`, `PresentMediaCompositionVisible(...)`, and composition-empty presentation.
- Native HWND primary/secondary attach, DirectComposition attach, secondary output routing, WPF adapter code, and renderer stages D/E/F.

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "ClearPresentedFrame\b|ResizeNativeHwndPresentation|RefreshNativePresentation" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "ClearPresentedFrame\(|ClearPresentedTargets\(|ClearActiveTarget\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "NativeHwnd\.Resize|D3D11NativeHwndSwapChainBackend\.Resize|Resize\(int width, int height\) => ResizeCore" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "DirectComposition\.Resize|D3D11DirectCompositionSingleTargetBackend\.Resize|\.Resize\(deviceResources|Resize\(D3D11DeviceResources deviceResources" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Verify live queued resize and presentation paths stay live:
git grep -n -E "ApplyPendingResizeIfNeeded|QueueNativeHwndResize|QueueDirectCompositionResize|QueueNativeHwndPresentationResize|QueueDirectCompositionPresentationResize|ResizePresentationSurface|SetPresentationSurfaceLiveResize" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "PresentCurrentComposition|PresentMediaCompositionVisible|ClearCompositionBackgroundFrame|ClearDirectCompositionOverlay|ClearActiveTargetToBlack|ClearOverlayToTransparent" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# WPF adapter boundary guard:
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf

# Project-local follow-up guards after removal:
git grep -n -E "ClearPresentedFrame\b|ResizeNativeHwndPresentation|RefreshNativePresentation|ClearPresentedTargets\(|NativeHwnd\.Resize|DirectComposition\.Resize|Resize\(D3D11DeviceResources deviceResources|Resize\(int width, int height\) => ResizeCore" -- FatzBombMedia.MediaControl
git grep -n -E "ApplyPendingResizeIfNeeded|QueueNativeHwndResize|QueueDirectCompositionResize|QueueNativeHwndPresentationResize|QueueDirectCompositionPresentationResize|ResizePresentationSurface|SetPresentationSurfaceLiveResize|PresentMediaCompositionVisible|ClearActiveTargetToBlack" -- FatzBombMedia.MediaControl FatzBombMedia.MediaControl.Wpf FatzBombMedia.Player
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium. The removable clear/resize APIs are confined to Core renderer internals, but this is presentation code and needs resize/clear smoke testing. Graphify output was not available at `C:\Programming\FatzBombMedia\graphify-out\graph.json`, under the repo, or under `C:\Programming`, so this review is based on source inspection and whole-repo text searches.
- **Safe items:** remove `D3D11RendererPresentationServices.ClearPresentedFrame(...)` and `ResizeNativeHwndPresentation(...)`; remove `D3D11PresentationSurfaceController.ClearPresentedFrame(...)` and `ResizeNativeHwndPresentation(...)`; remove `D3D11PresentationBackendSet.ClearPresentedFrame(...)`, `ClearPresentedTargets(...)`, and private `ClearActiveTarget(...)` if it becomes uncalled; remove immediate-helper `D3D11NativeHwndSwapChainBackend.Resize(...)`; remove immediate-helper `D3D11DirectCompositionSingleTargetBackend.Resize(...)`; no source cleanup is needed for `RefreshNativePresentation(...)` because it is already absent.
- **Exclusions/defer:** keep all public/host surface resize APIs and WPF callers; keep live-resize state; keep queued native HWND and DirectComposition resize paths and all `ApplyPending*ResizeIfNeeded(...)` paths; keep backend `QueueResize(...)` and private `ResizeCore(...)`; keep `ClearActiveTargetToBlack(...)` and low-level clear helpers if still used by surface activation; keep DirectComposition overlay clearing, composition background clearing, composition-empty presentation, native HWND/DirectComposition attach paths, secondary outputs, and renderer stages D/E/F.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found `ClearPresentedFrame(...)`, `ResizeNativeHwndPresentation(...)`, and `ClearPresentedTargets(...)` only in the renderer methods listed above; `RefreshNativePresentation(...)` has zero source/config matches; `D3D11NativeHwndSwapChainBackend.Resize(...)` is only reached through `D3D11PresentationSurfaceController.ResizeNativeHwndPresentation(...)`; `D3D11DirectCompositionSingleTargetBackend.Resize(...)` has no callers; queued resize and WPF surface resize paths remain live.
- **Project-local follow-up guards:** after removal, the first project-local guard should return no source matches for immediate clear/resize APIs. The second should still show live queued resize, WPF/session resize, composition presentation, and activation clear paths. The WPF adapter boundary guard should remain empty.
- **Likely files affected:** `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationSurfaceController.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationBackendSet.cs`; `Core\Graphics\D3D\Presentation\NativeHwnd\D3D11NativeHwndSwapChainBackend.cs`; `Core\Graphics\D3D\Presentation\DirectComposition\D3D11DirectCompositionSingleTargetBackend.cs`.
- **Smoke tests:** run Player with primary DirectComposition overlay mode, native HWND fallback/overlay disabled mode, resize/live resize, stop/idle clear, audio-only/visual-less transition, CDG-only/audio+CDG, secondary output attach/resize/detach, software-decoded video fallback, and hardware-decoded video if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 5 source files. See latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** resize, live resize, audio-only clear, stopped/idle CDG clear, secondary output close/open.

**Exit criteria:** Debug and Release builds pass; immediate clear/resize guards return no source/config callers; queued resize, WPF/session resize, composition presentation, activation clear, secondary output, and WPF adapter boundary guards remain clean/live as described above.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 5 — Renderer Stage D: Remove Direct DXCDG/Single-Target Present

**Goal:** remove direct DXCDG snapshot presentation path orphaned after Phase 1 Stage 8 removed `PresentDxCdgSnapshotVisible`. The live path uses `PresentMediaCompositionVisible(...)` and the composition-frame fan-out.

**Risk:** Medium-High (renderer/DxCdg presentation risk).

**Scope:**

Bridge member `PresentDxCdgSnapshotVisible(...)` was removed in Phase 1 Stage 8. Now remove the downstream chain:

- `D3D11RendererPresentationServices.PresentDxCdgSnapshotVisible(...)`
- `D3D11MediaCompositionPresenter.PresentToTarget(...)` single-target overloads: both the `DxCdgRenderSnapshot` overload and the `MediaCompositionFrame` overload.
- `D3D11PresentationBackendKind.ExternalTexture`; whole-repo review found it only exists to support the dead `PresentToTarget(...)` single-target adapter.

**Exclusions (do NOT touch):**
- Live composition fan-out path: `NativeD3D11PresentationBridge.PresentMediaCompositionVisible(...)`, `D3D11RendererPresentationServices.PresentMediaCompositionVisible(...)`, `D3D11MediaCompositionPresenter.PresentToTargets(...)`, `D3D11PresentationSurfaceController.NotifyTargetsPresented(...)`, and `D3D11PresentationBackendSet.NotifyTargetsPresented(...)`.
- Live DxCdg snapshot/render path in `NativeMediaPresentationCoordinator`: `RenderCurrentDxCdgFrame(...)`, `RenderCurrentDxCdgFrameNoLock(...)`, `PresentMediaCompositionNoLock(...)`, `BuildCompositionFrameNoLock(...)`, `TryCreateAndCacheDxCdgSnapshot(...)`, retained refresh, pending DxCdg cadence, and dirty-region consumption.
- DirectComposition overlay split path: `PresentMediaCompositionWithDirectCompositionOverlaySplit(...)`, `DrawDxCdgOverlayToTarget(...)`, `TryGetDirectCompositionOverlayTargetSnapshot(...)`, overlay/base state, overlay clearing, and DirectComposition overlay diagnostics.
- Multi-target/fan-out metadata: `D3D11PresentationTargetSnapshot`, `D3D11CompositionTargetDescriptor`, `D3D11CompositionTargetKind.FinalPresentation`, `D3D11CompositionTargetKind.DirectCompositionOverlay`, target registry/capture, and secondary native HWND output paths.
- Stage 6 direct blit/texture helper cleanup (`PresentTextureFrame(...)`, `BlitToActivePresentation(...)`, `D3D11PresentationBackendSet.Present(...)`, `HasNativePresentingBackend`, render-target-view cache) remains out of scope.

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "PresentDxCdgSnapshotVisible|PresentToTarget\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "ExternalTexture|D3D11PresentationBackendKind\.ExternalTexture" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "DxCdgSnapshot|CdgSnapshot|SnapshotVisible|Present.*Snapshot" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Verify live composition fan-out and DxCdg paths stay live:
git grep -n -E "PresentMediaCompositionVisible|PresentToTargets\(|NotifyTargetsPresented|DrawDxCdgOverlayToTarget|PresentMediaCompositionWithDirectCompositionOverlaySplit" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "RenderCurrentDxCdgFrame|PresentPendingDxCdgFrame|PresentRetainedDxCdgFrame|BuildCompositionFrameNoLock|TryCreateAndCacheDxCdgSnapshot|TryGetDxCdgSnapshotForVideoComposition" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11PresentationTargetSnapshot|D3D11CompositionTargetDescriptor|D3D11CompositionTargetKind|FinalPresentation|DirectCompositionOverlay" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# WPF adapter boundary guard:
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf

# Project-local follow-up guards after removal:
git grep -n -E "PresentDxCdgSnapshotVisible|PresentToTarget\(|ExternalTexture|D3D11PresentationBackendKind\.ExternalTexture" -- FatzBombMedia.MediaControl
git grep -n -E "PresentMediaCompositionVisible|PresentToTargets\(|NotifyTargetsPresented|DrawDxCdgOverlayToTarget|PresentMediaCompositionWithDirectCompositionOverlaySplit|RenderCurrentDxCdgFrame|PresentPendingDxCdgFrame|D3D11CompositionTargetKind" -- FatzBombMedia.MediaControl
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium-High. The direct/single-target APIs are dead and internally confined, but this touches DxCdg renderer presentation and must be smoke-tested across CDG, video+CDG, DirectComposition overlay split, and secondary output. Graphify output was not available at `C:\Programming\FatzBombMedia\graphify-out\graph.json`, under the repo, or under `C:\Programming`, so this review is based on source inspection and whole-repo text searches.
- **Safe items:** remove `D3D11RendererPresentationServices.PresentDxCdgSnapshotVisible(...)`; remove both `D3D11MediaCompositionPresenter.PresentToTarget(...)` single-target overloads; remove `D3D11PresentationBackendKind.ExternalTexture` because its only whole-repo use is inside the dead `PresentToTarget(...)` adapter.
- **Exclusions/defer:** keep `PresentMediaCompositionVisible(...)`, `PresentToTargets(...)`, `NotifyTargetsPresented(...)`, `DrawDxCdgOverlayToTarget(...)`, DirectComposition overlay split, all `NativeMediaPresentationCoordinator` DxCdg snapshot/cadence/retained-refresh paths, composition target metadata, target registry/capture, secondary native HWND outputs, and all Stage 6 direct blit/texture helper items.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found `PresentDxCdgSnapshotVisible(...)` only in `D3D11RendererPresentationServices`; `PresentToTarget(...)` only in the two single-target overloads/self-delegation in `D3D11MediaCompositionPresenter`; and `ExternalTexture` only in `D3D11PresentationBackendKind` plus the dead single-target adapter. Live composition fan-out, DxCdg coordinator presentation, and target metadata paths remain active.
- **Project-local follow-up guards:** after removal, the first project-local guard should return no source matches for `PresentDxCdgSnapshotVisible`, `PresentToTarget(`, or `ExternalTexture`. The second should still show live composition fan-out, DirectComposition overlay split, DxCdg coordinator, and composition target kind paths. The WPF adapter boundary guard should remain empty.
- **Likely files affected:** `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`; `Core\Graphics\Composition\D3D11MediaCompositionPresenter.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationBackendKind.cs`.
- **Smoke tests:** run Player with CDG-only/audio+CDG, video+CDG, sidecar image + CDG, sidecar video + CDG, DirectComposition overlay split, native HWND fallback/overlay disabled mode, resize/live resize with CDG visible, retained refresh after pause/seek/surface attach, and secondary output if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 3 source files. See latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** CDG-only, audio+CDG, video+CDG, DirectComposition overlay split, secondary output.

**Exit criteria:** Debug and Release builds pass; direct/single-target guards return no source/config callers; composition fan-out, DxCdg coordinator, DirectComposition overlay split, target metadata, secondary output, and WPF adapter boundary guards remain clean/live as described above.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 6 — Renderer Stage E: Prune Direct Blit/Texture Helpers

**Goal:** remove unused direct-blit and helper-present paths not consumed by the current composition fan-out.

**Risk:** Medium. Clusters A/C/D are already removed, so the remaining direct-blit helpers are now traceable. This still touches renderer presentation construction and cleanup, so build and Player smoke testing are required after implementation.

**Scope:**

- Remove `D3D11PresentationFramePresenter.PresentTextureFrame(...)`. It is no longer called after the direct DXCDG/single-target path was removed.
- Delete `D3D11PresentationBlitter.cs` and remove its `D3D11RendererPresentationServices` construction/dependency wiring. `BlitToActivePresentation(...)` has zero callers, and `BlitToTarget(...)` is only reachable from the dead `PresentTextureFrame(...)` method.
- Remove `D3D11PresentationBackendSet.Present(...)`. Keep `NotifyFramePresented(...)`, `NotifyTargetsPresented(...)`, and the private `NotifyBackendPresented(...)` path because they are live.
- Remove `D3D11PresentationBackendSet.HasNativePresentingBackend`. Whole-repo review found no callers.
- Delete `D3D11PresentationRenderTargetViewCache.cs` and remove the no-op cache dependency/property/callback wiring from `D3D11RendererPresentationServices` and `D3D11PresentationResourceCleaner`. `Current` is never assigned, and all `Release()` calls only dispose `null`.
- Pass no-op callbacks directly to native HWND and DirectComposition backend constructors only if their constructor parameters remain otherwise useful for future real reset hooks; do not broaden backend cleanup beyond removing the dead cache callback wiring.
- `D3D11PresentationBackendSet.HasUiSharedSurfaceBackend` is stale and already gone from Stage 3; no implementation work remains for that symbol.

**Exclusions (do NOT touch):**
- Live hardware frame path: `NativeMediaPresentationCoordinator` → `NativeD3D11PresentationBridge.PresentHardwareFrame(...)` → `D3D11RendererPresentationServices.FramePresenter.PresentHardwareFrame(...)` → `D3D11FramePresenter.PresentHardwareFrame(...)`.
- Live composition fan-out path: `PresentMediaCompositionVisible(...)`, `D3D11MediaCompositionPresenter.PresentToTargets(...)`, `D3D11PresentationSurfaceController.NotifyTargetsPresented(...)`, and `D3D11PresentationBackendSet.NotifyTargetsPresented(...)`.
- Native HWND primary/secondary backends, DirectComposition single-target/overlay backends, queued resize paths, `ClearActiveTargetToBlack(...)`, `OwnsTexture(...)`, `ActiveTargetTexture`, and `HasMultipleActiveTargets`.
- `D3D11VideoProcessorPresenter.BlitTextureToPresentation(...)`; it remains used by live hardware/composition presentation code outside this direct helper cleanup.
- WPF adapter source files.

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "PresentTextureFrame\(|BlitToActivePresentation\(|BlitToTarget\(|D3D11PresentationBlitter|new D3D11PresentationBlitter|HasNativePresentingBackend|HasUiSharedSurfaceBackend|PresentationRenderTargetViewCache|D3D11PresentationRenderTargetViewCache" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11PresentationBackendSet\.Present\(|\.Present\(liveResizeFlag, syncImmediate\)|NotifyFramePresented\(|NotifyTargetsPresented\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "RenderTargetViewCache|renderTargetViewCache|releasePresentationRenderTargetView|ID3D11RenderTargetView\?|\.Current\b" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Verify live presentation paths stay live:
git grep -n -E "PresentHardwareFrame|D3D11FramePresenter|PresentMediaCompositionVisible|PresentToTargets\(|NotifyTargetsPresented|PresentMediaCompositionWithDirectCompositionOverlaySplit|BlitTextureToPresentation" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "ActiveTargetTexture|ClearActiveTargetToBlack|OwnsTexture\(|HasMultipleActiveTargets|CreateSecondaryNativeHwndTarget|DirectCompositionOverlay|NativeHwndSwapChain" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# WPF adapter boundary guard:
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf

# Project-local follow-up guards after removal:
git grep -n -E "PresentTextureFrame\(|BlitToActivePresentation\(|BlitToTarget\(|D3D11PresentationBlitter|HasNativePresentingBackend|HasUiSharedSurfaceBackend|PresentationRenderTargetViewCache|D3D11PresentationRenderTargetViewCache" -- FatzBombMedia.MediaControl
git grep -n -E "D3D11PresentationBackendSet\.Present\(" -- FatzBombMedia.MediaControl
git grep -n -E "PresentHardwareFrame|PresentMediaCompositionVisible|PresentToTargets\(|NotifyFramePresented\(|NotifyTargetsPresented\(|BlitTextureToPresentation|ClearActiveTargetToBlack|HasMultipleActiveTargets" -- FatzBombMedia.MediaControl
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium. Whole-repo searches found no XAML/config/script/project callers for the direct texture/blit helpers. Graphify output was not available at `C:\Programming\FatzBombMedia\graphify-out\graph.json`, under the repo, or under `C:\Programming`, so this review is based on source inspection and whole-repo text searches.
- **Safe items:** remove `D3D11PresentationFramePresenter.PresentTextureFrame(...)`; delete `D3D11PresentationBlitter.cs`; remove `D3D11RendererPresentationServices` blitter construction and the `D3D11PresentationFramePresenter` constructor parameter; remove `D3D11PresentationBackendSet.Present(...)`; remove `D3D11PresentationBackendSet.HasNativePresentingBackend`; delete `D3D11PresentationRenderTargetViewCache.cs`; remove `RenderTargetViewCache` construction/property, backend release callback wiring, and `D3D11PresentationResourceCleaner` cache dependency/call.
- **Exclusions/defer:** keep `PresentHardwareFrame(...)`, `D3D11FramePresenter`, `D3D11VideoProcessorPresenter`, `PresentMediaCompositionVisible(...)`, `PresentToTargets(...)`, `NotifyFramePresented(...)`, `NotifyTargetsPresented(...)`, DirectComposition overlay split, native HWND primary/secondary paths, queued resize, `ClearActiveTargetToBlack(...)`, `OwnsTexture(...)`, `ActiveTargetTexture`, `HasMultipleActiveTargets`, and WPF adapter files. `HasUiSharedSurfaceBackend` is already absent from Stage 3 and should not drive any new edit.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found `BlitToActivePresentation(...)` only in `D3D11PresentationBlitter`; `BlitToTarget(...)` only in `D3D11PresentationBlitter` plus the dead `PresentTextureFrame(...)`; `PresentTextureFrame(...)` only in `D3D11PresentationFramePresenter`; `HasNativePresentingBackend` only in `D3D11PresentationBackendSet`; `HasUiSharedSurfaceBackend` absent; and `D3D11PresentationRenderTargetViewCache` only in its own file, `D3D11RendererPresentationServices`, and `D3D11PresentationResourceCleaner`.
- **Project-local follow-up guards:** after removal, the first project-local guard should return no source matches for the direct blitter/presenter/cache symbols. The backend-set direct `Present(...)` guard should return no matches, while `NotifyFramePresented(...)` and `NotifyTargetsPresented(...)` remain live. The live presentation-path guards should still show hardware frame, composition fan-out, video-processor, clear, multi-target, and backend notification paths. The WPF adapter boundary guard should remain empty.
- **Likely files affected:** `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationFramePresenter.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationBackendSet.cs`; `Core\Graphics\D3D\Presentation\D3D11PresentationResourceCleaner.cs`; delete `Core\Graphics\D3D\Presentation\D3D11PresentationBlitter.cs`; delete `Core\Graphics\D3D\Presentation\D3D11PresentationRenderTargetViewCache.cs`.
- **Smoke tests:** run Player with hardware-decoded video, software-decoded fallback if available, CDG-only/audio+CDG, video+CDG, sidecar image/video backgrounds, DirectComposition overlay split, native HWND fallback/overlay disabled mode, resize/live resize, stop/idle clear, and secondary output if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 4 source files; delete 2 files. See latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** same as Clusters A/C/D. Any media mode that exercises the composition fan-out.

**Exit criteria:** Debug and Release builds pass; direct blitter/presenter/cache guards return no source/config callers; hardware frame, composition fan-out, DirectComposition overlay split, native HWND primary/secondary, cleanup, and WPF adapter boundary guards remain clean/live as described above.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

---

### Stage 7 — Renderer Stage F: Decide Offscreen Retained-Layer Future

**Goal:** prune the truly unused offscreen retained-layer allocation scaffolding while keeping current retained-layer planner metadata that is actively consumed by composition diagnostics, retained-refresh decisions, and target fan-out.

**Risk:** Medium-High if scoped narrowly; High if deleted broadly. The original broad delete list is stale because much of the retained-layer and target-plan metadata is live.

**Deferred items from Phase 1 Stage 8:** `D3D11CompositionTargetKind.RetainedOffscreenLayer` (118), `D3D11CompositionTargetKind.FutureSwapChain` (119).

**Corrected scope:**

- Delete `D3D11CompositionLayerRenderTarget.cs`; its `Ensure(...)`, resource properties, and `MarkRendered(...)` have no callers.
- Remove `D3D11CompositionLayerState.OffscreenRenderTarget` and `HasOffscreenRenderTarget`; also remove offscreen participation from `HasRetainedResources`, `CreateUpdatePlanEntry(...)`, `Clear()`, and `LifecycleSummary`.
- Remove the `HasOffscreenRenderTarget` field from `D3D11CompositionLayerUpdate` and its `ToString()` summary.
- Remove `D3D11CompositionTargetKind.RetainedOffscreenLayer` and `D3D11CompositionTargetKind.FutureSwapChain`; both enum values have zero callers.
- Remove `D3D11CompositionLayerUpdateCadence.DisplayRate`; no code constructs or checks it.
- Remove `D3D11CompositionLayerUpdateAction.ClearOnly`; no code sets or checks this enum flag. Keep `D3D11CompositionLayerUpdatePlan.IsClearOnly` and clear-only guardrail logging.
- Remove `D3D11CompositionLayerSchedule.SourceFrameInterval`; no caller reads it.
- Remove `D3D11CompositionTargetDescriptor.PrimaryFinalTarget(...)` factory only; keep `PrimaryFinalTargetId`, which is live in presentation target routing.
- Remove `D3D11CompositionTargetRegistry.Capture(D3D11CompositionTargetDescriptor)` single-target overload only; keep the live `Capture(IEnumerable<D3D11CompositionTargetDescriptor>)` overload.
- Remove `D3D11CompositionLayerCache.Layers` and `ActiveLayerSummary`; both have zero callers.
- Remove `D3D11CompositionTargetPlan.Targets`, `HasTargets`, `HasWork`, and `ActivePresentationTargetIds`; all are uncalled by source. `ActivePresentationTargetIds` is referenced by `scripts/check-presentation-parity.ps1`, so update that script assertion if the property is removed.

**Exclusions (do NOT touch):**
- Keep `D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture`, `D3D11CompositionLayerSchedule.TargetKind`, and `D3D11CompositionLayerUpdatePlan.HasFutureOffscreenTargets`; they are live planner/diagnostic metadata in the current composition flow.
- Keep `D3D11CompositionLayerSchedule`, `D3D11CompositionLayerUpdateCadence.None/OnDemand/SourceRate/Static`, `CanReuseRetainedOutput`, `HasSourceRate`, and `ToString()` except for removing the unused `SourceFrameInterval` property.
- Keep `D3D11CompositionLayerUpdatePlan.IsClearOnly`, `HasWork`, `ReusableLayerCount`, `HasSourceRateLayers`, `HasReusableRetainedOutputs`, `Summary`, and clear-only guardrail logging.
- Keep live retained-layer state and resource paths: `Texture`, `ShaderResourceView`, `HasTexture`, `HasDxCdgResources`, `HasRetainedResources`, `UploadBgraPixels(...)`, `EnsureRenderTargetTexture(...)`, `UploadDxCdgSnapshot(...)`, `MarkUploaded(...)`, `MarkRendered(...)`, `MarkPlanConsumed()`, and resource release methods.
- Keep live target fan-out metadata and behavior: `D3D11CompositionTargetDescriptor`, `PrimaryFinalTargetId`, `D3D11CompositionTargetRegistry.Capture(IEnumerable<...>)`, `CreatePlan()`, `MarkPlanConsumed()`, `D3D11CompositionTargetPlan.ShouldPresentTarget(...)`, `HasMetadataWork`, `ResizeTargetCount`, `Summary`, and all `D3D11CompositionTargetUpdate*` types.
- Keep `D3D11CompositionTargetKind.FinalPresentation` and `DirectCompositionOverlay`, plus presentation target snapshot conversion and backend routing.
- Do not touch WPF adapter files.

**Trace guards:**

```powershell
# Whole-solution guards from repo root. Include source, XAML, project/config, docs/scripts/JSON, and reflection/name strings.
git grep -n -E "RetainedOffscreen|FutureSwapChain|OffscreenRenderTarget|DisplayRate|ClearOnly|SourceFrameInterval|PrimaryFinalTarget|RetainedOffscreenTexture" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "D3D11CompositionLayerRenderTarget|D3D11CompositionLayerRenderTargetKind|D3D11CompositionLayerState|D3D11CompositionTargetKind|D3D11CompositionLayerUpdateCadence|D3D11CompositionLayerUpdateAction|D3D11CompositionLayerSchedule|D3D11CompositionTargetDescriptor|D3D11CompositionTargetRegistry|D3D11CompositionLayerCache|D3D11CompositionTargetPlan" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "OffscreenRenderTarget\.|OffscreenRenderTarget\b|HasOffscreenRenderTarget\b|Ensure\(ID3D11Device device|MarkRendered\(long revision\) => Revision|D3D11CompositionTargetKind\.(RetainedOffscreenLayer|FutureSwapChain)|\bRetainedOffscreenLayer\b|\bFutureSwapChain\b" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "PrimaryFinalTarget\(|PrimaryFinalTargetId|D3D11CompositionTargetDescriptor\.PrimaryFinalTarget|Capture\(D3D11CompositionTargetDescriptor|Capture\(IEnumerable<D3D11CompositionTargetDescriptor>|ActiveLayerSummary\b|\.Layers\b|HasTargets|ActivePresentationTargetIds|Targets =>|HasWork =>" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# Verify live composition planner and target fan-out paths stay live:
git grep -n -E "D3D11CompositionLayerSchedule|RetainedOffscreenTexture|HasFutureOffscreenTargets|IsClearOnly|ReusableLayerCount|HasSourceRateLayers|HasReusableRetainedOutputs|ShouldPresentTarget|HasMetadataWork|ResizeTargetCount|PresentToTargets\(|NotifyTargetsPresented" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "FinalPresentation|DirectCompositionOverlay|PrimaryFinalTargetId|ToCompositionTargetDescriptor|Capture\(validTargets\.Select|CreatePlan\(|MarkPlanConsumed\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"

# WPF adapter boundary guard:
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf

# Project-local follow-up guards after removal:
git grep -n -E "D3D11CompositionLayerRenderTarget|OffscreenRenderTarget|HasOffscreenRenderTarget|D3D11CompositionTargetKind\.(RetainedOffscreenLayer|FutureSwapChain)|\bRetainedOffscreenLayer\b|\bFutureSwapChain\b|DisplayRate|D3D11CompositionLayerUpdateAction\.ClearOnly|SourceFrameInterval|PrimaryFinalTarget\(|Capture\(D3D11CompositionTargetDescriptor|ActiveLayerSummary\b|HasTargets|ActivePresentationTargetIds|Targets =>|HasWork =>" -- FatzBombMedia.MediaControl scripts
git grep -n -E "D3D11CompositionLayerSchedule|RetainedOffscreenTexture|HasFutureOffscreenTargets|IsClearOnly|ShouldPresentTarget|HasMetadataWork|ResizeTargetCount|FinalPresentation|DirectCompositionOverlay|PrimaryFinalTargetId|ToCompositionTargetDescriptor|PresentToTargets\(|NotifyTargetsPresented" -- FatzBombMedia.MediaControl
```

**Latest review findings — 2026-06-30:**

- **Safety:** Medium-High. The truly unused offscreen allocation class and a few future-only enum/property helpers are safe to remove, but the original broad Stage 7 delete list would break live composition planning and diagnostics. Graphify output was not available at `C:\Programming\FatzBombMedia\graphify-out\graph.json`, under the repo, or under `C:\Programming`, so this review is based on source inspection and whole-repo text searches.
- **Safe items:** delete `D3D11CompositionLayerRenderTarget.cs`; remove `D3D11CompositionLayerState.OffscreenRenderTarget` and `HasOffscreenRenderTarget`; remove the `HasOffscreenRenderTarget` field from `D3D11CompositionLayerUpdate`; remove `D3D11CompositionTargetKind.RetainedOffscreenLayer` and `FutureSwapChain`; remove unused `D3D11CompositionLayerUpdateCadence.DisplayRate`; remove unused `D3D11CompositionLayerUpdateAction.ClearOnly`; remove unused `D3D11CompositionLayerSchedule.SourceFrameInterval`; remove `D3D11CompositionTargetDescriptor.PrimaryFinalTarget(...)` factory while keeping `PrimaryFinalTargetId`; remove `D3D11CompositionTargetRegistry.Capture(D3D11CompositionTargetDescriptor)` while keeping the enumerable overload; remove `D3D11CompositionLayerCache.Layers` and `ActiveLayerSummary`; remove `D3D11CompositionTargetPlan.Targets`, `HasTargets`, `HasWork`, and `ActivePresentationTargetIds`; update `scripts/check-presentation-parity.ps1` if `ActivePresentationTargetIds` is removed.
- **Exclusions/defer:** do not delete live retained-layer schedule/update-plan metadata such as `D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture`, `D3D11CompositionLayerSchedule.TargetKind`, `CanReuseRetainedOutput`, `D3D11CompositionLayerUpdatePlan.HasFutureOffscreenTargets`, `D3D11CompositionLayerUpdatePlan.IsClearOnly`, `D3D11CompositionLayerUpdatePlan.HasWork`, update-plan `Summary`, clear-only guardrail logging, retained BGRA/DXCDG/resource state, target registry enumerable capture, target-plan `ShouldPresentTarget(...)`, `HasMetadataWork`, `ResizeTargetCount`, target-plan `Summary`, target snapshot conversion, `FinalPresentation`, `DirectCompositionOverlay`, composition fan-out, or WPF adapter files.
- **Corrected whole-solution guards:** run the `git grep` commands above, or equivalent OpenCode whole-repo grep, from `C:\Programming\FatzBombMedia` before implementation. The 2026-06-30 review found `D3D11CompositionLayerRenderTarget.Ensure(...)` and `MarkRendered(...)` uncalled; `RetainedOffscreenLayer` and `FutureSwapChain` only in their enum declaration; `DisplayRate`, `D3D11CompositionLayerUpdateAction.ClearOnly`, `SourceFrameInterval`, `PrimaryFinalTarget(...)`, single-target `Capture(...)`, `Layers`, `ActiveLayerSummary`, `Targets`, `HasTargets`, and `HasWork` uncalled by source; `ActivePresentationTargetIds` referenced only by `scripts/check-presentation-parity.ps1`.
- **Project-local follow-up guards:** after removal, the first project-local guard should return no source/script matches for removed allocation/future-only helpers except historical docs if included. The second should still show live retained-layer schedule/update-plan metadata, target fan-out, final/direct-composition target kinds, primary target id routing, target snapshot conversion, `PresentToTargets(...)`, and `NotifyTargetsPresented(...)`. The WPF adapter boundary guard should remain empty.
- **Likely files affected:** `Core\Graphics\Composition\D3D11\D3D11CompositionLayerState.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionLayerUpdate.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionTargetKind.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionLayerUpdateCadence.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionLayerUpdateAction.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionLayerSchedule.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionTargetDescriptor.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionTargetRegistry.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionLayerCache.cs`; `Core\Graphics\Composition\D3D11\D3D11CompositionTargetPlan.cs`; `scripts\check-presentation-parity.ps1`; delete `Core\Graphics\Composition\D3D11\D3D11CompositionLayerRenderTarget.cs`.
- **Smoke tests:** run Player with retained background refresh after pause/seek/surface attach, CDG-only/audio+CDG, video+CDG, sidecar image/video backgrounds, DirectComposition overlay split, native HWND fallback/overlay disabled mode, resize/live resize, stop/idle clear, secondary output attach/detach, software-decoded fallback, and hardware-decoded video if native DLLs are available.
- **Recommendation:** proceed with exclusions.

**Likely files affected:** 10 source files; delete 1 file; update 1 script if removing `ActivePresentationTargetIds`. See latest review findings for the corrected file list.

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.

**Manual verification:** run Player retained refresh, CDG/video composition, DirectComposition overlay split, native HWND fallback, secondary output, resize/live resize, and stop/idle clear scenarios.

**Exit criteria:** Debug and Release builds pass; removed offscreen/future-only guards return no source/script callers; retained-layer planner, target fan-out, final/direct-composition target metadata, and WPF adapter boundary guards remain clean/live as described above.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

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
| §11.7 | Renderer Cluster F (offscreen scaffolding) | Removed (Stage 7) |
| 90-94 | DirectoryScanner dead members + ExactMatch/Incompatible | Removed (Stage 1) |
| F14 | `GpuRenderingCapabilities.cs` zombie file | Removed (Stage 1) |
| 108 | `FileTypes.GetFileType` | Stale (method already absent) |
