# Code Cleanup Audit — FatzBombMedia

Re-audited 2026-06-30. Cross-referenced against prior trace and `AGENTS.md` known-dead list.

## Summary

| Layer | Dead items | Key files |
|-------|-----------|-----------|
| API | 36 | `MediaControlPlayer.cs` (26), `MediaControlSession.cs` (entire class), `AcceptedMediaTypes.cs` (6), `MediaPresentationSurfaceRole.cs` (2), `MediaInputSource.cs` (1) |
| Controllers | 15 | `MediaControls.cs` (8), `MediaFile.cs` (2), `MediaMetadata.cs` (3), `CdgVisuals.cs` (2) |
| Pipeline | 23 | `VideoPipeline.cs` (3), `AudioPipeline.cs` (3), `CdgPipeline.cs` (2), `ClockAudioEngine.cs` (8), `MediaState.cs` (3), `PipelineMediaEngine.cs` (1), `MediaSession.cs` (1) |
| Core + WPF | 62 | `MediaPlayerConfiguration.cs` (6), `GpuRenderingCapabilities.cs` (5), `NativeD3D11PresentationBridge.cs` (5), `DirectoryScanner.cs` (5), `VideoPresentationTiming.cs` (1), `VideoAudioEngine.cs` (2), compisition scaffolding (12), `Wpf/Player.cs` (5), `Wpf/MediaPresentationMode.cs` (2), various enums (6) |
| **Total** | **~136** | |

**Previously-known dead code (AGENTS.md 6 items): ALL re-confirmed still dead. Zero items from the prior trace have been fixed.**

---

## Cleanup Progress

- [x] Stage 1 — completed 2026-06-30: marked complete from prior work.
- [x] Stage 2 — completed 2026-06-30: marked complete from prior work.
- [x] Stage 3 — completed 2026-06-30: removed 26 dead members from MediaControlPlayer, 8 from MediaControls, 2 from MediaFile, 2 from CdgVisuals, 6 enum values. Fixed WPF Player ctor chain. Builds: Debug pass, Release pass.
- [x] Stage 4 — completed 2026-06-30: removed 6 dead config properties (GpuRenderingMinResolution, MaxGpuMemoryMB, MaxUiSharedSurfaceWidth, MaxUiSharedSurfaceHeight, UiSharedSurfacePresentationMode, UiSharedSurfaceSizingMode) + 2 enum types (UiSharedSurfacePresentationMode, UiSharedSurfaceSizingMode) from MediaPlayerConfiguration. Removed GetOptimalRenderingMethod + 4 dead GpuCapabilityInfo properties (LegacyUiRenderingTier, SupportsHardwareAcceleration, EstimatedGpuMemoryMB, GpuDescription) from GpuRenderingCapabilities. Removed Player-side assignments in MainWindowViewModel. GpuRenderingCapabilities.cs remains as a zombie class (~173 lines) — no external consumers; schedule full-file deletion for follow-up. Builds: Debug pass, Release pass.
- [x] Stage 5 — completed 2026-06-30: removed `MediaControlPlayer._metadata` construction/field, `MediaFile._mediaMetadata` field/ctor param and both `Refresh()` calls; deleted `Controllers\MediaMetadata.cs` and `Core\Metadata\MediaMetadata.cs`. BASS calls remain only in real audio code. Builds: Debug pass, Release pass.
- [x] Stage 6 — completed 2026-06-30: removed `MediaState.Changed`/`OnChanged()`/`LastError`/`SetError()` and 3 `AudioPipeline.SetError` calls; removed no-subscriber `IAudioEngine.SeekCompleted`/`PlaybackStarted` and `ICdgEngine.PositionChanged` from interface and all active implementations/invocations. Preserved `IAudioEngine.PositionChanged`. Builds: Debug pass, Release pass.
- [x] Stage 7 — completed 2026-06-30: guarded `MediaEffects` volume/pitch/tempo/normalize/gain behind `HasAudio`; made unsupported `ClockAudioEngine` members (`Init`, `Open`, `SetVolume`, `SetPitch`, `AnalyzePeak*`) throw `NotSupportedException`; removed fake `NormalizationGainChanged` invocations from clock analysis. Preserved clock transport. Builds: Debug pass, Release pass.
- [x] Stage 8 — completed 2026-06-30: removed 6 dead bridge members (IsReady, UploadFrame, RefreshNativePresentation, ClearPresentedFrame, ResizeNativeHwndPresentation, PresentDxCdgSnapshotVisible) from NativeD3D11PresentationBridge; removed 9 composition dead items (FutureVisualizer, Diagnostics/UiOverlay from LayerKind/LayerId, HasNaturalSize, HasAnyVisibleLayer, LayerSummary, CreateLayerDescriptors). Excluded HasMultipleTargets (live); deferred RetainedOffscreenLayer/FutureSwapChain (Cluster F scaffolding). No cascade into downstream renderer chains. Builds: Debug pass, Release pass.

---

## 1. API Layer — Confirmed Dead Code

### 1.1 `MediaControlPlayer.cs` — 26 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 1 | `MediaControlPlayer()` parameterless ctor | 25-28 | Never instantiated externally; only 6th ctor is live |
| 2 | `MediaControlPlayer(MediaPlayerConfiguration)` ctor | 30-33 | Never instantiated |
| 3 | `MediaControlPlayer(string?)` ctor | 35-38 | Never instantiated |
| 4 | `MediaControlPlayer(IEnumerable<string>)` ctor | 40-43 | Never instantiated |
| 5 | `MediaControlPlayer(MediaPlayerConfiguration, string?)` ctor | 45-48 | Never instantiated |
| 6 | `static Create(string?)` factory | 161 | **AGENTS.md known-dead** — still dead |
| 7 | `static Create(MediaPlayerConfiguration, string?)` factory | 163-164 | **AGENTS.md known-dead** — still dead |
| 8 | `static Create(IEnumerable<string>)` factory | 166 | **AGENTS.md known-dead** — still dead |
| 9 | `static CreateAsync(string?)` factory | 168-169 | Never called; WPF Player shadows with `new` |
| 10 | `static CreateAsync(MediaPlayerConfiguration, string?)` factory | 171-172 | Never called; WPF Player shadows with `new` |
| 11 | `static CreateAsync(IEnumerable<string>)` factory | 174-175 | Never called; WPF Player shadows with `new` |
| 12 | `static CreateAsync(MediaPlayerConfiguration, IEnumerable<string>)` factory | 177-178 | Never called; WPF Player shadows with `new` |
| 13 | `SeekAsync(TimeSpan)` | 236 | **AGENTS.md known-dead** — not on `IMediaControlSession`; zero callers |
| 14 | `SeekAsync(double)` | 238-252 | **AGENTS.md known-dead** — not on `IMediaControlSession`; zero callers |
| 15 | `LastNativeLoadMessages` static property | 93 | Only `LastNativeLoadMessage` (singular) is used |
| 16 | `File` public property | 77 | Not in `IMediaControlSession`; never accessed externally |
| 17 | `Metadata` public property | 83 | Not in `IMediaControlSession`; never accessed externally |
| 18 | `AcceptedTypes` public property | 85 | Not in `IMediaControlSession`; never accessed externally |
| 19 | `Filters` public property | 87 | Not in `IMediaControlSession`; never accessed externally |
| 20 | `Configuration` public property | 95 | Not in `IMediaControlSession`; zero external reads |
| 21 | `ActualVideoDecoding` property | 97 | Set in `ResolveActualMethods`; never read externally |
| 22 | `ActualRendering` property | 99 | Set in `ResolveActualMethods`; never read externally |
| 23 | `PresentationBackend` property | 101 | Computed by coordinator; never read externally |
| 24 | `BackgroundAutoDetect` property | 121-125 | Not in `IMediaControlSession`; never set/read externally |
| 25 | `NormalizationGain` property | 133 | Not in `IMediaControlSession`; never read externally |
| 26 | `NormalizationGainChanged` event | 309-313 | Not in `IMediaControlSession`; zero subscribers |

### 1.2 `MediaControlSession.cs` — entire class

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 27 | **Entire class** `MediaControlSession` | 14-194 | **AGENTS.md known-dead** — `new MediaControlSession(` occurs zero times in codebase. 194 lines of dead facade code. |

### 1.3 `MediaInputSource.cs` — 1 item

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 28 | `FromBgra32Image(ReadOnlySpan<byte>, ...)` factory | 37-42 | Span-based overload never called; all callers use `FromOwnedBgra32Image` |

### 1.4 `AcceptedMediaTypes.cs` — 6 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 29 | `Audio` property | 10 | Never read externally. **Consumed internally** by `Filters` (line 18-26, both "Supported media" and "Audio" entries) and `All` (line 16). Do NOT remove — verified 2026-06-30. |
| 30 | `Video` property | 11 | Never read externally. **Consumed internally** by `Filters` ("Supported media" and "Video" entries). Do NOT remove — verified 2026-06-30. |
| 31 | `Archive` property | 12 | Never read externally. **Consumed internally** by `Filters` ("Supported media" and "Archive" entries). Do NOT remove — verified 2026-06-30. |
| 32 | `Image` property | 13 | Never read externally. **Consumed internally** by `Filters` ("Supported media" and "Image" entries). Do NOT remove — verified 2026-06-30. |
| 33 | `Cdg` property | 14 | Never read externally. **Consumed internally** by `Filters` ("Supported media" and "CD+G" entries). Do NOT remove — verified 2026-06-30. |
| 34 | `All` computed property | 16 | Computed by concatenating Audio+Video+Archive+Image+Cdg; `Filters` initializer re-concatenates the individuals directly rather than using `All`. Zero internal or external consumers. Safe to remove. |

### 1.5 `MediaPresentationSurfaceRole.cs` — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 35 | `Secondary` enum value (1) | 12 | Never referenced anywhere — verified 2026-06-30 |
| 36 | `Preview` enum value (2) | 15 | Never referenced anywhere — verified 2026-06-30 |

---

## 2. Controllers — Confirmed Dead Code

### 2.1 `MediaControls.cs` — 8 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 37 | `Seek(TimeSpan)` method | 111 | Only reachable via dead `Position` setter; zero external callers. **NOTE: `Seek(double)` at line 116 IS LIVE** — verified called by `MainWindowViewModel.cs:222`. Remove only the TimeSpan overload. |
| 38 | `Position` setter | 128 | Getter is used; setter is dead |
| 39 | `State` property | 192 | Duplicated by `MediaControlPlayer.State`; zero external callers |
| 40 | `IsOpen` property | 197 | Duplicated by `MediaControlPlayer.IsOpen`; zero external callers |
| 41 | `Content` property | 202 | Duplicated by `MediaControlPlayer.Content`; zero external callers |
| 42 | `HasVideo` property | 207 | Duplicated by `MediaControlPlayer.HasVideo`; zero external callers |
| 43 | `HasAudio` property | 212 | Duplicated by `MediaControlPlayer.HasAudio`; zero external callers |
| 44 | `HasCdg` property | 217 | Duplicated by `MediaControlPlayer.HasCdg`; zero external callers |

### 2.2 `MediaFile.cs` — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 45 | `ContentType` property | 200 | Never read externally |
| 46 | `IsLoaded` property | 205 | Never read externally |

### 2.3 `MediaMetadata.cs` — 3 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 47 | `Current` property | 23 | Never read externally — renders `PopulateFromEngines` (87 lines of BASS/FFmpeg/CDG computation) unused |
| 48 | `Summary` property | 28 | Never read externally |
| 49 | `IsResampling` property | 33 | Never read externally |

### 2.4 `CdgVisuals.cs` — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 50 | `BackgroundOverrideArgb` property | 28 | Controller passthrough — never set or read externally. Underlying `ICdgEngine.BackgroundOverrideArgb` is LIVE (used by DxCdgStateMachine/DxCdgColorManager). |
| 51 | `BackgroundWashArgb` property | 42 | Controller passthrough — never read externally. Underlying `ICdgEngine.BackgroundWashArgb` is LIVE (used by compositor/renderer). |

---

## 3. Pipeline — Confirmed Dead Code

### 3.1 `VideoPipeline.cs` — 3 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 52 | `PresentFrameForPosition(double)` convenience overload | 1469 | Only 2-param `(targetPosition, clockPosition)` overload is called; this one-param wrapper is dead |
| 53 | `_presentationDiagnosticMaxTargetLateMs` field | 92 | Written per-frame; only read in `Debug.WriteLine` — zero operational gating |
| 54 | `_presentationDiagnosticLeadMs` field | 94 | Written per-frame; only read in `Debug.WriteLine` — zero operational gating |

### 3.2 `AudioPipeline.cs` — 3 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 55 | `SetVolume(double)` method | 165-169 | Never called; `MediaEffects` calls `Engine.SetVolume` directly on `IAudioEngine` |
| 56 | `SetPitch(int)` method | 171-175 | Never called; `MediaEffects` calls `Engine.SetPitch` directly |
| 57 | `SetTempo(int)` method | 177-181 | Never called; `MediaEffects` calls `Engine.SetTempo` directly |

### 3.3 `CdgPipeline.cs` — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 58 | `RenderAt(double)` convenience overload | 305 | Zero callers; external rendering goes through `RenderPipeline.RenderCdgAtCurrentPosition` → `Engine.RenderAtSeconds` |
| 59 | `Clock` property | 27 | Declared `public`; zero external reads |

### 3.4 `ClockAudioEngine.cs` — 8 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 60 | `PlaybackEnded` event (explicit interface impl) | 36-39 | Empty `add {} remove {}` — no subscriber can ever receive this event |
| 61 | `Init(int, int)` method | 42 | Returns `true`; never called on `ClockAudioEngine` |
| 62 | `Open(string)` method | 43 | Returns `false`; never called on `ClockAudioEngine` |
| 63 | `Open(IArchiveEntryInfo)` method | 44 | Returns `false`; never called on `ClockAudioEngine` |
| 64 | `Normalize` property | 12 | Settable but effectless on clock engine |
| 65 | `NormalizationGain` property | 13 | Always returns `1.0`; never meaningful |
| 66 | `SetPitch(int)` method | 77 | Always returns `false` |
| 67 | `NormalizationGainChanged` event | 10 | Wired into no-op analysis methods that always fire with constant `1.0` |

### 3.5 `MediaState.cs` — 3 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 68 | `LastError` property | 13 | Written via `SetError` on open failures; never read by any code |
| 69 | `Changed` event | 17 | Fired on every state mutation; zero subscribers |
| 70 | `SourcePath` property | 10 | Written on every `SetOpened`; only read once (as fallback in `AudioPipeline.cs:209`) |

### 3.6 `PipelineMediaEngine.cs` — 1 item

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 71 | `SeekAsync(double, TimeSpan?)` method | 202 | Fake async — delegates synchronously via `Task.FromResult(Seek(seconds))`; shadowed by dead `MediaControlPlayer.SeekAsync` |

### 3.7 `MediaSession.cs` — 1 item

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 72 | `MediaSession(MediaState?)` ctor parameter | 17-21 | Zero callers pass a `MediaState` argument; only parameterless ctor is used |

---

## 4. Core — Confirmed Dead Code

### 4.1 `MediaPlayerConfiguration.cs` — 6 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 73 | `GpuRenderingMinResolution` property | 139 | Always set to `0`; never read by consuming code |
| 74 | `MaxGpuMemoryMB` property | 144 | Always set to `1024`; never read by consuming code |
| 75 | `MaxUiSharedSurfaceWidth` property | 202 | Always set to `1920`; never read by consuming code |
| 76 | `MaxUiSharedSurfaceHeight` property | 209 | Always set to `1080`; never read by consuming code |
| 77 | `UiSharedSurfacePresentationMode` property | 187 | Always set; never read by consuming code |
| 78 | `UiSharedSurfaceSizingMode` property | 195 | Always set; never read by consuming code |

### 4.2 `GpuRenderingCapabilities.cs` — 5 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 79 | `GetOptimalRenderingMethod` static method | 41 | Never called |
| 80 | `GpuCapabilityInfo.LegacyUiRenderingTier` property | 193 | Set to `0` internally; never read externally |
| 81 | `GpuCapabilityInfo.SupportsHardwareAcceleration` property | 198 | Set internally; never read externally |
| 82 | `GpuCapabilityInfo.EstimatedGpuMemoryMB` property | 213 | Set internally; only read in local `Debug.WriteLine` and `ToString` |
| 83 | `GpuCapabilityInfo.GpuDescription` property | 218 | Set internally; never read externally |

### 4.3 `NativeD3D11PresentationBridge.cs` — 5 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 84 | `IsReady` property | 30-31 | Never read externally |
| 85 | `UploadFrame` method | 151-157 | Never called externally |
| 86 | `RefreshNativePresentation` method | 203-215 | Never called externally |
| 87 | `ClearPresentedFrame` method | 217-223 | Never called externally |
| 88 | `ResizeNativeHwndPresentation` method | 143-149 | Never called externally |
| 89 | `PresentDxCdgSnapshotVisible` method | 187-193 | Never called externally |

### 4.4 `DirectoryScanner.cs` — 5 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 90 | `LocateFile(string, string, ...)` single-path overload | 93 | Never called |
| 91 | `LocateFile(IEnumerable<string>, string, ...)` multi-path overload | 109 | Never called |
| 92 | `CleanPaths` method | 276 | Never called |
| 93 | `CompatibilityLevel.ExactMatch` enum value | 17-18 | Set but never passed to any method; only `Compatible` is used |
| 94 | `CompatibilityLevel.Incompatible` enum value | 27-28 | Never used |

### 4.5 Core Audio/Video — 7 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 95 | `IAudioEngine.SeekCompleted` event | IAudioEngine.cs:144 | Fired by implementations but never subscribed anywhere |
| 96 | `IAudioEngine.PlaybackStarted` event | IAudioEngine.cs:148 | Fired by implementations but never subscribed anywhere |
| 97 | `ICdgEngine.PositionChanged` event | ICdgEngine.cs:65 | Fired by `DxCdgEngine`; no external subscribers |
| 98 | `VideoAudioEngine.IsOpen` property | VideoAudioEngine.cs:62-71 | Declared public; never called externally |
| 99 | `AudioStreamManager.IsOpen` property | AudioStreamManager.cs:53 | Declared public; never called externally |
| 100 | `AudioEngine.AnalyzePeakQuickAsync` public method | AudioEngine.cs:405 | Never called externally (only internal path uses it) |
| 101 | `AudioEngine.AnalyzePeakAsync` public method | AudioEngine.cs:412 | Never called externally (only internal path uses it) |
| 102 | `AudioNormalizer.CalculateEffectiveVolume` method | AudioNormalizer.cs:193 | Never called |

### 4.6 Core Video — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 103 | `VideoEngineState.Error` enum value | VideoEngineState.cs:11 | Never set by any code |
| 104 | `VideoPresentationTiming.GetBackgroundFrameLeadTolerance` method | VideoPresentationTiming.cs:21 | Never called externally |

### 4.7 Core DxCdg — 3 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 105 | `DxCdgConstants.Command.None` enum value | DxCdgConstants.cs:170 | Never referenced |
| 106 | `DxCdgConstants.Command.Mask` enum value | DxCdgConstants.cs:172 | Never referenced |
| 107 | `DxCdgEngineState.Error` enum value | DxCdgEngineState.cs:11 | Never set by any code |

### 4.8 Core Files — 1 item

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 108 | `FileTypes.GetFileType` static method | Filetypes.cs:69 | Public but never called; `FileTreeBuilder.GetFileTree` does the same analysis inline |

### 4.9 Core Composition Scaffolding — 12 items

| # | Item | File | Line | Explanation |
|---|------|------|------|-------------|
| 109 | `MediaCompositionLayerSourceKind.FutureVisualizer` | MediaCompositionLayerSourceKind.cs | 17 | Never used |
| 110 | `MediaCompositionLayerKind.Diagnostics` | MediaCompositionLayerKind.cs | 11 | Never used |
| 111 | `MediaCompositionLayerKind.UiOverlay` | MediaCompositionLayerKind.cs | 12 | Never used |
| 112 | `MediaCompositionLayerId.Diagnostics` | MediaCompositionLayerId.cs | 12 | Never referenced |
| 113 | `MediaCompositionLayerId.UiOverlay` | MediaCompositionLayerId.cs | 13 | Never referenced |
| 114 | `MediaCompositionLayer.HasNaturalSize` property | MediaCompositionLayer.cs | 45 | Never read |
| 115 | `MediaCompositionFrame.HasAnyVisibleLayer` property | MediaCompositionFrame.cs | 73 | Identical to `HasShaderVisibleLayer`; never read |
| 116 | `MediaCompositionFrame.LayerSummary` property | Layers.cs | 108-115 | Never read |
| 117 | `MediaCompositionFrame.CreateLayerDescriptors` method | Layers.cs | 105-106 | Only called by dead `LayerSummary` |
| 118 | `D3D11CompositionTargetKind.RetainedOffscreenLayer` | D3D11CompositionTargetKind.cs | 11 | Never referenced |
| 119 | `D3D11CompositionTargetKind.FutureSwapChain` | D3D11CompositionTargetKind.cs | 12 | Never referenced |
| 120 | `D3D11CompositionTargetPlan.HasMultipleTargets` property | D3D11CompositionTargetPlan.cs | 22 | Never read |

---

## 5. WPF Adapter — Confirmed Dead Code

### 5.1 `Player.cs` — 5 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 121 | `Player(string? userLibraryPath)` ctor | 22-25 | Never instantiated |
| 122 | `Player(IEnumerable<string>)` ctor | 27-29 | Never instantiated |
| 123 | `Player(MediaPlayerConfiguration, string?)` ctor | 32-35 | Never instantiated |
| 124 | `Player(MediaPlayerConfiguration, IEnumerable<string>)` ctor | 37-39 | Only used from `CreateAsync`; zero direct instantiations |
| 125 | `CreateAsync` static method | 42-43 | Hides base with `new`; never called; caller uses `FatzMediaPlayer.CreateAsync` which resolves to base class |
| 126 | `Player(MediaPlayerConfiguration)` ctor | 19-21 | Consider dead — zero callers found |

### 5.2 `MediaPresentationMode.cs` — 2 items

| # | Item | Line | Explanation |
|---|------|------|-------------|
| 127 | `NativeHwnd` enum value | 5 | Never selected; only `NativeHwndOverlay` is used in production |
| 128 | `D3DImage` enum value | 7 | **AGENTS.md known-dead** — no backend exists; never referenced |

---

## 6. Code Smells & Risks (Not Dead, But Problematic)

### 6.1 Fire-and-Forget Discards

| # | File | Line | Pattern | Risk |
|---|------|------|---------|------|
| S1 | `MediaFile.cs` | 115 | `_ = Task.Run(HandlePlayOnLoadAsync)` | Unobserved task from sync `Load()` path; runs on possibly-disposed engine |
| S2 | `MediaFile.cs` | 215 | `_ = (_mediaControls?.Stop())` | Stop failure silently ignored before loading new media |
| S3 | `MediaFile.cs` | 239 | `_ = _mediaEngine.SetBackground(background)` | Background set failure silently ignored |
| S4 | `VideoPipeline.cs` | 399 | `_ = _audioEngine!.Play()` | After seek, if audio play fails, decode/presentation loops proceed believing `wasPlaying=true` |
| S5 | `VideoPipeline.cs` | 1776 | `_ = Directory.CreateDirectory(tempDir)` | If creation fails, next line `File.WriteAllBytes` throws |

### 6.2 Abstraction Leaks

| # | File | Line | Issue |
|---|------|------|-------|
| S6 | `RenderPipeline.cs` | 58 | Casts `_video is VideoPipeline` to call `RenderCurrentFrame`; won't work with non-`VideoPipeline` `IVideoEngine` |
| S7 | `ClockAudioEngine.cs` | Full file | Implements `IAudioEngine` but ~60% of members are no-ops or stubs; `Normalize`, `SetPitch`, `Init`, `Open` are all effectless |

### 6.3 Redundant / Over-Engineered

| # | File | Line | Issue |
|---|------|------|-------|
| S8 | `MediaMetadata.cs` | 52-139 | `PopulateFromEngines()` computes 87 lines of metadata (BASS audio info, aspect ratio, CDG stats, etc.) into a `Core.Metadata.MediaMetadata` object that is never read because `Current`/`Summary`/`IsResampling` are all dead |
| S9 | `MediaState.cs` | 13-17 | `LastError` and `Changed` form an error-reporting + notification pipeline with zero consumers |
| S10 | `PipelineMediaEngine.cs` | 569-570 | `ImagePath` set, cleared by `Close()`, set again — double assignment |
| S11 | `PipelineMediaEngine.cs` | 236-240 | Empty `finally` block with explanatory comment about concerns already handled elsewhere |

### 6.4 Overly-Broad Visibility

| # | File | Member | Current | Recommended |
|---|------|--------|---------|-------------|
| S12 | `CdgPipeline.cs` | `Clock` property | `public` | `private` (zero external reads) |
| S13 | `RenderPipeline.cs` | `CurrentPosition` property | `public` | `private` (zero external reads) |
| S14 | `Bootstrap.cs` | `IsBassLoaded`, `IsFfmpegLoaded`, `RootPath`, `BassPath` | `public` | `internal` or `private` (only read within partial class) |

### 6.5 Duplicated Code

| # | Files | Lines | Issue |
|---|-------|-------|-------|
| S15 | `MediaSurface.cs:388` + `SecondaryPresentationWindow.cs:86` | ~100 lines each | `NativeHwndSurfaceHost` is fully duplicated `HwndHost` subclass with identical constants, `WndProc`, `FillNativeBackground` |

---

## 7. Items Re-Confirmed from Prior Trace (Not Fixed)

| # | AGENTS.md Entry | Status |
|---|-----------------|--------|
| A1 | `MediaControlSession.cs` — never instantiated | **Still dead** (zero instantiations) |
| A2 | `MediaControlPlayer.Create(...)` — 3 static factories | **Still dead** (zero callers) |
| A3 | `MediaControlPlayer.SeekAsync(...)` — not in interface | **Still dead** (zero callers) |
| A4 | `MediaPresentationMode.D3DImage` — no backend | **Still dead** (never referenced) |
| A5 | `MediaSurface.BitmapScalingMode` DP — never read | **Still dead** (zero reads) |
| A6 | `Bootstrap.LibraryLoader.cs` `totalLibraries` — computed but unused | **Still dead** (value unused) |
| A7 | `MediaControlSession.cs` entire class — 174 lines dead facade | **Still dead** (re-confirmed) |

---

## 8. Safe Cleanup Priority

### Tier 1 — Zero Risk (properties/methods with zero callers)

Remove: items 1-14, 15-26, 27, 28, 29-34, 35-36, 37-44, 45-46, 47-49, 50-51, 52-54, 55-57, 58, 60-67, 68-69, 71, 72, 73-78, 79-83, 84-89, 90-94, 95-97, 103-104, 105-108, 109-120, 121-126, 127-128

### Tier 2 — Requires Verification (dead code with side effects)

- `MediaMetadata.PopulateFromEngines()` — remove with `Current`/`Summary`/`IsResampling` (S8)
- `MediaState.Changed` event — remove with `OnChanged()` calls (S9)
- `MediaState.LastError` — remove with `SetError()` calls (S9)

### Tier 3 — Refactoring (abstraction fixes)

- Extract duplicated `NativeHwndSurfaceHost` (S15)
- Fix `RenderPipeline` cast to `VideoPipeline` (S6)
- Add `SetOpened` call to `PlaybackCoordinator.OpenStandaloneCdg` (state tracking gap)
- Reduce visibility on `Bootstrap`/`CdgPipeline`/`RenderPipeline` members (S12-14)

---

## 9. Completely Dead Files & Self-Referential Code

Files where every type, method, property, and member is either never called externally or only referenced within the file itself — meaning the file contributes zero behavior to the running application.

### 9.1 Completely Dead Files (Entire File Has Zero External Effect)

| # | File | Lines | Explanation |
|---|------|-------|-------------|
| F1 | `FatzBombMedia.MediaControl\Api\MediaControlSession.cs` | 174 | Entire class never instantiated. Zero `new MediaControlSession(` matches in codebase. All external references to `MediaControlSession` are actually to `IMediaControlSession` (the interface), not this concrete class. Intended as future "Stage 10S" facade but has no active path. |

### 9.2 Dead Types Within Otherwise-Alive Files

Types that are declared but only reference themselves — zero callers from any other file:

| # | File | Dead Type | Lines | Explanation |
|---|------|-----------|-------|-------------|
| F2 | `Core\Metadata\MediaMetadata.cs` | `PerformanceAssessment` class | ~41-106 | Computed by `GetPerformanceAssessment()` which is never called externally. The class holds `PerformanceScore`, `Recommendations`, `HardwareAccelStatus` — all dead. |
| F3 | `Core\Metadata\MediaMetadata.cs` | `HardwareAccelStatus` enum | ~22-25 | `None`, `Available`, `Enabled` — only referenced by dead `PerformanceAssessment` |
| F4 | `Core\Metadata\MediaMetadata.cs` | `PerformanceLevel` enum | ~27-30 | `Optimal`, `Good`, `Marginal`, `Poor` — only referenced by dead `PerformanceAssessment` |
| F5 | `Core\Metadata\MediaMetadata.cs` | `GetPerformanceAssessment()` method | ~138 | Only caller is itself, via `PopulatePerformanceAssessment()` at ~129. Neither is ever invoked externally. |
| F6 | `Core\Metadata\MediaMetadata.cs` | `GetHardwareAccelStatus()` method | ~146 | Only called by dead `GetPerformanceAssessment()` |

### 9.3 Files With All Public Members Dead (But Type Is Referenced As Interface/Base)

Files where the declared type name is used (e.g., as a type parameter or field type), but every public method/property/event on the type is never called:

*None found beyond F1.* All other checked files (`NativeD3D11PresentationBridge`, `DirectoryScanner`, `GpuRenderingCapabilities`, etc.) have at least one live public member that is actively called from another file.

### 9.4 Files With >50% Dead API Surface

Files that still have live functionality but where most of their public/protected surface is unused:

| # | File | Dead / Total Members | Live Members Keeping It Alive |
|---|------|----------------------|-------------------------------|
| F7 | `Core\Audio\AudioNormalizer.cs` | 1 / 7 | `AnalyzePeakQuickAsync`, `AnalyzePeakAsync`, `SetNormalizationGainFromPeak`, `SetupGainFx`, `ResetGain`, `SetNormalizeEnabled` — all called via `AudioEngine` |
| F8 | `Core\Files\Filetypes.cs` | 1 / ~8 | `IsAudio`, `IsVideo`, `IsImage`, `IsCdg`, `Audio`, `Video`, `Image`, `Cdg` extension sets — all actively used by `FileTreeBuilder`, `Bootstrap`, `MediaBackgroundResolver` |
| F9 | `Core\Files\DirectoryScanner.cs` | 3 / ~6 | `LocateFiles`, `LocateFilesDirectory`, `MatchesCompatibility`, `CleanList` — all called by `Bootstrap.LibraryLoader` |
| F10 | `Core\Graphics\D3D\NativeD3D11PresentationBridge.cs` | 5 / ~10 | `Initialize`, `Dispose`, `PresentFrame` — called by `NativeMediaPresentationCoordinator` |
| F11 | `Pipeline\ClockAudioEngine.cs` | 7 / ~16 | `Position`, `Duration`, `Play`, `Pause`, `Stop`, `Seek`, `SetTempo`, `Dispose`, `Normalize` setter — used as active audio engine for karaoke/clock-driven playback |
| F12 | `Wpf\Player.cs` | 5 / ~12 | `Player(MediaPlayerConfiguration, IEnumerable<string>)` ctor + `CreateAsync` + `ToMediaInputSource` + `Load`/`LoadAsync` overloads — used by `MainWindowViewModel` |
| F13 | `Core\Configuration\MediaPlayerConfiguration.cs` | 6 / ~30 | `VideoDecodingMethod`, `RenderingMethod`, `EnableLogging`, `EnablePerformanceLogging`, `NativeSwapChainPresentMode`, `PresentationLeadRefreshIntervals` + all constructor params — actively consumed by `VideoPipeline` and `D3D11SwapChainPresenter` |
| F14 | `Core\Graphics\GpuRenderingCapabilities.cs` | 10 / ~10 | **ALL dead — zero external consumers in solution.** Whole-solution trace (2026-06-30) found no references to `GpuRenderingCapabilities` from `MediaControlPlayer` or any file outside `GpuRenderingCapabilities.cs` itself. The `Capabilities` property, `DetectCapabilities()`, `CheckD3D11Support()`, `DetectGpuInfoViaDxgi()`, `GpuRenderingMethod` enum, and all `GpuCapabilityInfo` members are self-referencing only — the class produces zero runtime behavior. After Stage 4 removes `GetOptimalRenderingMethod`, the file becomes a zombie class. Remove the entire file in a follow-up stage. |
| F15 | `Controllers\MediaMetadata.cs` | 3 / 5 | `Refresh()` method — called by `MediaFile`. `Current`, `Summary`, `IsResampling` are dead properties whose data is computed (87 lines in `PopulateFromEngines`) but never read. |
| F16 | `Api\MediaControlPlayer.cs` | 26 / ~45 | `Controls`, `Effects`, `CdgVisuals`, `OpenFileDialogFilter`, `LastNativeLoadMessage`, `Normalize`, `PlayOnLoad`, `LoopMedia`, `VideoDisplayMode`, `Position`, `Duration`, `Play`/`Pause`/`Stop`/`Seek`/`Load`/`LoadAsync`, interface events, surface management methods — all actively used by WPF `Player` and `MainWindowViewModel` |
| F17 | `Core\Graphics\Composition\D3D11\D3D11CompositionTargetKind.cs` | 3 / 5 | `FinalPresentation`, `DirectCompositionOverlay` — used by compositor infrastructure |
| F18 | `Core\Graphics\Composition\MediaCompositionLayerKind.cs` | 2 / 5 | `Background`, `Wash`, `Overlay` — used by composition pipeline |
| F19 | `Core\Graphics\Composition\MediaCompositionLayerId.cs` | 2 / 5 | `Background`(1), `Wash`(10), `Overlay`(100) — used across composition infrastructure |

### 9.5 Recommendation

**Safe to delete now:**
- `F1` — `MediaControlSession.cs` (174 lines). Remove the file; zero behavioral impact. Recoverable from git if ever needed for Stage 10S.
- `F2-F6` — Dead performance-assessment types in `Core\Metadata\MediaMetadata.cs`. Remove `PerformanceAssessment` class (~65 lines), `HardwareAccelStatus` enum, `PerformanceLevel` enum, `GetPerformanceAssessment()`, `PopulatePerformanceAssessment()`, `GetHardwareAccelStatus()` (~30 lines total). The live data properties (`AudioSampleRate`, `VideoWidth`, `HasCdg`, `MasterDurationSeconds`, `GetSummary()`) must remain.

**Consider for cleanup:**
- `F9` — `DirectoryScanner.LocateFile` (2 dead overloads) and `CleanPaths` can be removed without any caller impact.
- `F10` — `NativeD3D11PresentationBridge.UploadFrame`, `RefreshNativePresentation`, `ClearPresentedFrame`, `ResizeNativeHwndPresentation`, `PresentDxCdgSnapshotVisible` — all dead, but verify these are not planned for a future rendering stage before removing.
- `F11` — `ClockAudioEngine.Init`, `Open`, `SetPitch`, `NormalizationGain`, `NormalizationGainChanged` no-op paths — could be removed or replaced with `NotSupportedException` to make dead paths fail loudly instead of silently.

---

## 10. Staged Cleanup Roadmap

This roadmap orders cleanup by dependency, traceability, and regression risk. Do not combine public API trimming, renderer cleanup, threading fixes, and WPF refactors in one pass.

### Roadmap Rules

- **Trace before each stage**: run targeted `rg` checks immediately before deleting or changing any symbol. Treat a new caller as a blocker and move the item to a later stage.
- **Build after each stage**: run `dotnet build FatzBombMedia.sln --no-restore`; run Release build for public API/interface stages because warnings are errors in Release.
- **Keep renderer changes isolated**: anything under `Core\Graphics`, `Core\Graphics\D3D`, or `Core\Graphics\Composition` needs manual Player smoke testing after build.
- **Respect package risk**: `FatzBombMedia.MediaControl` is packable. In-repo zero callers means runtime-safe, but removing `public` members may still be source-breaking for external consumers.
- **Preserve WPF adapter boundary**: after any WPF/Core movement, verify `FatzBombMedia.MediaControl.Wpf` does not import Core pipeline, renderer, DxCdg, video-frame, or D3D11 namespaces.

### Stage 0 — Baseline & Trace Guards

**Goal:** freeze a known-good baseline and create guard checks before removing anything.

**Risk:** none.

**Commands:**

```powershell
dotnet restore FatzBombMedia.sln
dotnet build FatzBombMedia.sln --no-restore
dotnet build -c Release FatzBombMedia.sln --no-restore
```

**Trace guards:**

```powershell
rg -n "new MediaControlSession|MediaControlPlayer\.Create\(|SeekAsync\(|D3DImage|BitmapScalingMode|totalLibraries" .
rg -n "PerformanceAssessment|HardwareAccelStatus|PerformanceLevel|GetPerformanceAssessment|PopulatePerformanceAssessment|GetHardwareAccelStatus" FatzBombMedia.MediaControl
rg -n "NativeHwndSurfaceHost" FatzBombMedia.MediaControl.Wpf
```

**Exit criteria:** Debug and Release builds pass; trace results still match this audit.

### Stage 1 — Whole-File & Self-Contained Legacy Deletions

**Goal:** remove items with no runtime path and no dependency chain.

**Risk:** very low runtime risk; low package/API risk for `MediaControlSession` because it is public but never instantiated in-repo.

**Scope:**

- Delete `FatzBombMedia.MediaControl\Api\MediaControlSession.cs`.
- Remove dead performance-assessment types from `Core\Metadata\MediaMetadata.cs`: `PerformanceAssessment`, `HardwareAccelStatus`, `PerformanceLevel`, `GetPerformanceAssessment()`, `PopulatePerformanceAssessment()`, `GetHardwareAccelStatus()`.
- Remove `totalLibraries` locals from `Bootstrap.LibraryLoader.cs` only if current code still leaves them unused.

**Trace guards:**

```powershell
rg -n "new MediaControlSession|MediaControlSession\(" .
rg -n "PerformanceAssessment|HardwareAccelStatus|PerformanceLevel|GetPerformanceAssessment|PopulatePerformanceAssessment|GetHardwareAccelStatus" FatzBombMedia.MediaControl
rg -n "totalLibraries" FatzBombMedia.MediaControl\Runtime\Bootstrap.LibraryLoader.cs
```

**Exit criteria:** Debug and Release builds pass. No WPF adapter changes.

### Stage 2 — Internal Convenience Wrappers & Dead Helpers

**Goal:** remove private/internal or effectively internal dead helpers that do not alter public behavior.

**Risk:** low.

**Scope:**

- `VideoPipeline.PresentFrameForPosition(double)` convenience overload.
- `CdgPipeline.RenderAt(double)` convenience overload.
- `AudioPipeline.SetVolume`, `SetPitch`, `SetTempo` wrappers; `MediaEffects` already calls `IAudioEngine` directly.
- `MediaSession(MediaState?)` optional constructor path if no caller passes a state.
- `DirectoryScanner.LocateFile` overloads and `CleanPaths` if guards confirm no callers.
- `FileTypes.GetFileType` if `FileTreeBuilder` remains the only actual file-type decision path.
- `AudioNormalizer.CalculateEffectiveVolume` if still not called.
- `VideoPresentationTiming.GetBackgroundFrameLeadTolerance` if still not called.

**Trace guards:**

```powershell
rg -n "PresentFrameForPosition\(" FatzBombMedia.MediaControl\Pipeline\VideoPipeline.cs
rg -n "\.RenderAt\(" FatzBombMedia.MediaControl
rg -n "\.SetVolume\(|\.SetPitch\(|\.SetTempo\(" FatzBombMedia.MediaControl
rg -n "new MediaSession\(" FatzBombMedia.MediaControl
rg -n "LocateFile\(|CleanPaths\(|GetFileType\(|CalculateEffectiveVolume\(|GetBackgroundFrameLeadTolerance\(" FatzBombMedia.MediaControl
```

**Exit criteria:** Build passes; media load/play paths still compile without changing interfaces.

### Stage 3 — Public API Surface Trim (Package-Breaking Group)

**Goal:** remove public members that are unused in-repo but could be observed by external package consumers.

**Risk:** low runtime risk; medium API/package risk.

**Verification date:** 2026-06-30 — all items re-traced against solution. See exclusions below.

**Scope:**

- `MediaControlPlayer` dead constructors (items 1-5), `Create*` factories (items 6-8), `CreateAsync*` factories (items 9-12), `SeekAsync*` (items 13-14), `LastNativeLoadMessages` (item 15), `ActualVideoDecoding` (item 21), `ActualRendering` (item 22), `PresentationBackend` (item 23), `BackgroundAutoDetect` (item 24), `NormalizationGain` (item 25), `NormalizationGainChanged` (item 26), and public controller passthroughs: `File` (item 16), `Metadata` (item 17), `AcceptedTypes` (item 18), `Filters` (item 19), `Configuration` (item 20).
- `MediaControls` dead passthrough properties: `State` (item 39), `IsOpen` (item 40), `Content` (item 41), `HasVideo` (item 42), `HasAudio` (item 43), `HasCdg` (item 44), `Position` setter (item 38), and `Seek(TimeSpan)` wrapper (item 37). **DO NOT remove `Seek(double)` — verified LIVE** at `MainWindowViewModel.cs:222`.
- `MediaFile.ContentType` (item 45), `MediaFile.IsLoaded` (item 46).
- `CdgVisuals.BackgroundOverrideArgb` (item 50), `CdgVisuals.BackgroundWashArgb` (item 51) — only `CdgVisuals` controller passthroughs; the underlying `ICdgEngine`/`DxCdgEngine`/`DxCdgStateMachine` members are LIVE and must stay.
- Dead enum values: `MediaPresentationSurfaceRole.Preview` (item 36), `MediaPresentationSurfaceRole.Secondary` (item 35), `VideoEngineState.Error` (item 103), `DxCdgEngineState.Error` (item 107), `DxCdgConstants.Command.None` (item 105), `DxCdgConstants.Command.Mask` (item 106).

**Safe exclusions (do NOT remove):**

- **`AcceptedMediaTypes.Audio`, `Video`, `Archive`, `Image`, `Cdg` (items 29-33)** — verified consumed internally by the LIVE `Filters` property (line 18-26) which feeds `OpenFileDialogFilter` (used by `MainWindow.xaml.cs:67`). These record positional parameters are internal dependencies of live behavior. If removed, the filter string "Supported media|Audio|Video|Archive|Image|CD+G|All files" must be rebuilt from `FileTypes.*` static sets.
- **`MediaControls.Seek(double)`** — verified LIVE, called by `MainWindowViewModel.cs:222` via `Player?.Controls.Seek(target)`.
- **`MediaControlPlayer.Filters`** (item 19) — confirmed zero external callers at the *MediaControlPlayer* level (passthrough), but NOTE that `AcceptedMediaTypes.Filters` (line 18) remains live. Removing `MediaControlPlayer.Filters` is safe.

**Trace guards:**

```powershell
# Dead constructors / factories — verify zero callers
rg -n "new MediaControlPlayer\(\)|new MediaControlPlayer\(MediaPlayerConfiguration\)|new MediaControlPlayer\(string" .
rg -n "MediaControlPlayer\.Create\(|MediaControlPlayer\.CreateAsync\(" .

# Dead API surface on MediaControlPlayer — verify zero external readers
rg -n "LastNativeLoadMessages|ActualVideoDecoding|ActualRendering|NormalizationGainChanged|BackgroundAutoDetect" .
rg -n "\.NormalizationGain\b" .
rg -n "\.SeekAsync\(" .
rg -n "player\.(File|Metadata|AcceptedTypes|Configuration|Filters)\b|Player\?\.(File|Metadata|AcceptedTypes|Configuration|Filters)\b" .

# MediaControls dead properties — verify no external reads through Controls.*
rg -n "Controls\.(State|IsOpen|Content|HasVideo|HasAudio|HasCdg)\b" .
rg -n "Controls\.Seek\(TimeSpan\)" .

# MediaFile dead properties
rg -n "\.ContentType\b|\.IsLoaded\b" .

# CdgVisuals dead passthroughs — verify no external reads through CdgVisuals.*
rg -n "\.BackgroundOverrideArgb\b|\.BackgroundWashArgb\b" .

# AcceptedMediaTypes — verify OpenFileDialogFilter survival path
rg -n "OpenFileDialogFilter" .
rg -n "Audio\b.*Video\b.*Archive\b.*Image\b.*Cdg\b" FatzBombMedia.MediaControl\Api\AcceptedMediaTypes.cs

# Enum value removal — verify zero references, no switch exhaustiveness fallout
rg -n "MediaPresentationSurfaceRole\.(Preview|Secondary)" .
rg -n "VideoEngineState\.Error" .
rg -n "DxCdgEngineState\.Error" .
rg -n "Command\.(None|Mask)" .

# WPF adapter — verify Player still compiles after base member removal
rg -n "new.*CreateAsync|override.*SeekAsync" FatzBombMedia.MediaControl.Wpf
```

**Exit criteria:** Debug and Release builds pass. `OpenFileDialogFilter` still produces valid file picker filter string. WPF Player host compiles and launches. If package compatibility matters, mark this as a breaking-change stage in release notes.

### Stage 4 — Configuration & Capability Pruning

**Goal:** remove configuration/capability knobs that are set but never consumed.

**Risk:** medium, because some properties are set by the Player host and may represent unfinished stage work.

**Scope:**

- `MediaPlayerConfiguration.GpuRenderingMinResolution`, `MaxGpuMemoryMB`, `MaxUiSharedSurfaceWidth`, `MaxUiSharedSurfaceHeight`, `UiSharedSurfacePresentationMode`, `UiSharedSurfaceSizingMode` — confirmed zero consumers (2026-06-30).
- Enum types `UiSharedSurfacePresentationMode` and `UiSharedSurfaceSizingMode` — only used as types for the dead config properties above; remove with them.
- `GpuRenderingCapabilities.GetOptimalRenderingMethod` and dead `GpuCapabilityInfo` properties (`LegacyUiRenderingTier`, `SupportsHardwareAcceleration`, `EstimatedGpuMemoryMB`, `GpuDescription`).
- Remove Player-side assignments for removed config properties (`MainWindowViewModel.cs:300-301`).
- **After this stage:** `GpuRenderingCapabilities.cs` becomes a zombie class with zero external consumers. The remaining `Capabilities`, `DetectCapabilities`, `CheckD3D11Support`, `DetectGpuInfoViaDxgi`, `GpuRenderingMethod` enum, `GpuCapabilityInfo.SupportsD3D11`, and `HasDedicatedGpu` are self-referencing only. Schedule full-file deletion for a follow-up.

**Exclusions (do NOT remove in this stage):**

- `GpuCapabilityInfo.SupportsD3D11` and `HasDedicatedGpu` — out of scope despite being externally dead (used internally by `DetectCapabilities` and `ToString`). Defer to follow-up zombie-class deletion.
- `GpuRenderingMethod` enum — out of scope. Only used by dead `GetOptimalRenderingMethod`; defer to follow-up.

**Legacy connection:** these are likely remnants of earlier UI shared-surface/D3DImage or adaptive rendering plans. They should not be removed in the same stage as live compositor code. The full `GpuRenderingCapabilities.cs` file (240 lines) is entirely dead — zero external consumers in the solution — but file deletion is deferred to keep Stage 4 scoped to config/property-level pruning.

**Trace guards:**

```powershell
# Existing guards from plan:
rg -n "GpuRenderingMinResolution|MaxGpuMemoryMB|MaxUiSharedSurfaceWidth|MaxUiSharedSurfaceHeight|UiSharedSurfacePresentationMode|UiSharedSurfaceSizingMode" .
rg -n "GetOptimalRenderingMethod|LegacyUiRenderingTier|SupportsHardwareAcceleration|EstimatedGpuMemoryMB|GpuDescription" .

# Added 2026-06-30 — whole-solution guards for enum types and zombie-class scope:
rg -n "UiSharedSurfacePresentationMode" . -g "*.cs"
rg -n "UiSharedSurfaceSizingMode" . -g "*.cs"
rg -n "HostRenderEvent|SourceCapped|DisplayRectangle" .
rg -n "GpuRenderingMethod" . -g "*.cs"
rg -n "GpuCapabilityInfo" . -g "*.cs"
rg -n "GpuRenderingCapabilities" . -g "*.cs"
# Confirm no XAML/config/reflection hidden references:
rg -n "UiSharedSurface|GpuRenderingMin|MaxGpuMemory|SharedSurfaceSizing" . -g "*.xaml"
rg -n "UiSharedSurface|GpuRenderingMin|MaxGpuMemory" . -g "*.json"

# Post-removal sanity — verify GpuRenderingCapabilities.cs zombie status:
rg -n "GpuRenderingCapabilities|GpuCapabilityInfo|GpuRenderingMethod" . -g "*.cs"
# Expected result: plan doc + GpuRenderingCapabilities.cs only (zero external callers)
```

**Exit criteria:** Debug and Release builds pass; Player still constructs `MediaPlayerConfiguration` cleanly; `UiSharedSurfacePresentationMode` and `UiSharedSurfaceSizingMode` enum types are fully removed (not just the config properties); `GpuRenderingCapabilities.cs` compiles without `GetOptimalRenderingMethod` and the 4 dead `GpuCapabilityInfo` properties; `ToString()` is updated; no dead property assignments remain in `MainWindowViewModel.cs`. WPF adapter boundary remains clean.

### Stage 5 — Metadata Controller Decoupling Decision

**Goal:** remove the active-solution metadata controller/model path unless a new real consumer appears immediately before implementation. If metadata is wanted for UI/diagnostics, defer cleanup and implement the revive path as a separate feature stage.

**Risk:** medium. Runtime risk is low because the active solution has no metadata consumer; package/API risk remains because `Core.Metadata.MediaMetadata` is public and the removal is source-breaking for hypothetical external consumers.

**Options:**

- **Delete path (recommended):** remove the remaining active metadata wiring: `MediaControlPlayer._metadata` construction/field, the `MediaFile` metadata constructor parameter/field and `_mediaMetadata?.Refresh()` calls, `Controllers\MediaMetadata.cs`, and `Core\Metadata\MediaMetadata.cs` if whole-solution guards still show no non-controller consumers. `MediaControlPlayer.Metadata` was already removed in Stage 3, so do not look for that as a current implementation target.
- **Revive path (defer unless product wants metadata UI now):** keep metadata, expose it intentionally, and decouple ManagedBass reads by adding metadata properties to `IAudioEngine`/video abstractions instead of calling `Bass.ChannelGetInfo` from the controller. This is a feature stage, not cleanup.

**Exclusions (do NOT remove in this stage):**

- `legacy/**` metadata copies. They are in the repo, but not in `FatzBombMedia.sln`; treat legacy cleanup as a separate request.
- Unrelated renderer/composition uses of the word `Metadata`, such as `PropertyMetadata`, `MetadataWorkTargetCount`, `HasMetadataWork`, comments, and plan summaries.
- Active `ManagedBass` package references and BASS calls in real audio engine/normalizer code. This stage removes only the controller-level metadata probe (`Bass.ChannelGetInfo` / `Bass.GetInfo` inside `Controllers\MediaMetadata.cs`).
- Do not add metadata UI, bindings, or new audio/video abstraction members on the delete path.

**Recommendation:** proceed with the delete path, with the exclusions above. The current active solution computes metadata during load/close and writes debug lines, but no caller reads the cached result.

**Trace guards:**

```powershell
# Whole-repo/source guards from repo root. Include active projects, WPF/XAML, docs, project/config files, scripts, nameof/reflection strings, and legacy hits for exclusion accounting.
rg -n "MediaMetadata|Core\.Metadata\.MediaMetadata|PopulateFromEngines|GetSummary\(|GetDurationMismatchInfo\(|IsResampling" . -g "*.cs" -g "*.xaml" -g "*.csproj" -g "*.props" -g "*.targets" -g "*.sln" -g "*.slnx" -g "*.ps1" -g "*.json" -g "*.config" -g "*.md"
rg -n "\b_metadata\b|_mediaMetadata|new MediaMetadata\(|Core\.Metadata\.MediaMetadata" . -g "*.cs"
rg -n "\.Metadata\b|Path=Metadata|\{Binding Metadata\b|nameof\([^)]*Metadata|\"Metadata\"" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.md"
rg -n "\.Summary\b|Path=Summary|\{Binding Summary\b|nameof\([^)]*Summary|\"Summary\"" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.md"
rg -n "\.IsResampling\b|Path=IsResampling|\{Binding IsResampling\b|nameof\([^)]*IsResampling|\"IsResampling\"" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.md"
rg -n "ChannelGetInfo|Bass\.GetInfo|ManagedBass" FatzBombMedia.MediaControl -g "*.cs" -g "*.csproj"

# Active-project follow-up guards after deletion. Expected: no metadata controller/model refs in active projects; BASS refs remain only in real audio code.
rg -n "MediaMetadata|Core\.Metadata\.MediaMetadata|PopulateFromEngines|GetSummary\(|GetDurationMismatchInfo\(|IsResampling|\b_metadata\b|_mediaMetadata" FatzBombMedia.MediaControl FatzBombMedia.MediaControl.Wpf FatzBombMedia.Player -g "*.cs" -g "*.xaml" -g "*.csproj"
rg -n "ChannelGetInfo|Bass\.GetInfo" FatzBombMedia.MediaControl -g "*.cs"
```

**Exit criteria:** metadata is intentionally removed from the active solution, or it has a real consumer and no controller-level ManagedBass dependency. Debug and Release builds pass. Player load/close/open-next-media paths still work without metadata refresh side effects. Legacy files remain untouched.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

**Latest review findings — 2026-06-30:**

- **Safety:** medium. Active runtime impact should be low because whole-solution searches found no active UI/XAML/config/command/DI/reflection consumer for `Controllers\MediaMetadata.Current`, `Summary`, `IsResampling`, `PopulateFromEngines()`, `Core.Metadata.MediaMetadata`, `GetSummary()`, or `GetDurationMismatchInfo()`. API/package impact is medium because `Core.Metadata.MediaMetadata` is public.
- **Safe items:** active `Controllers\MediaMetadata.cs` is self-contained except for construction in `MediaControlPlayer` and `Refresh()` calls from `MediaFile`; active `Core\Metadata\MediaMetadata.cs` is referenced only by the controller. Delete-path implementation should remove `MediaControlPlayer._metadata`, stop passing metadata into `MediaFile`, remove the `MediaFile` metadata parameter/field and refresh calls, then delete both active metadata files if guards still match.
- **Exclusions/defer items:** do not touch `legacy/**` metadata copies found by whole-repo search; do not touch unrelated `PropertyMetadata` or compositor target/layer `MetadataWork` terminology; do not remove `ManagedBass` package references or BASS calls in `AudioEngine`, `AudioNormalizer`, `VideoAudioEngine`, or `AudioStreamManager`; do not implement the revive path in this cleanup stage.
- **Corrected whole-solution guards:** use the guard block above from repo root before deleting. The original broad `\.Current` / `\.Summary` guard was too noisy and missed the active internal wiring that remains after Stage 3 removed `MediaControlPlayer.Metadata`.
- **Likely files affected:** `FatzBombMedia.MediaControl\Api\MediaControlPlayer.cs`, `FatzBombMedia.MediaControl\Controllers\MediaFile.cs`, delete `FatzBombMedia.MediaControl\Controllers\MediaMetadata.cs`, delete `FatzBombMedia.MediaControl\Core\Metadata\MediaMetadata.cs`. No `.csproj` edit is expected unless explicit file includes are later added.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Smoke tests:** launch Player, load/play/stop/close an audio file, video file, CDG/audio+CDG file, and image/background sidecar case; load one file after another to cover the removed close/load metadata refresh calls; confirm no metadata UI is expected; verify the WPF adapter boundary remains clean with `rg -n "using FatzBombMedia\.MediaControl\.(Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video)" FatzBombMedia.MediaControl.Wpf`.
- **Recommendation:** proceed with exclusions. If a new active consumer appears in the guards, stop and switch to the revive path as a separate feature stage.

### Stage 6 — State/Event Cleanup

**Goal:** remove confirmed no-subscriber notification and error channels while preserving live state, position, lifecycle, and playback-ended paths.

**Risk:** medium, because event removal touches interfaces and implementation contracts.

**Scope:**

- Remove `MediaState.Changed`, `OnChanged()` calls, `LastError`, and `SetError()` if errors remain communicated by existing bool return values/logging only.
- Remove no-subscriber `IAudioEngine.SeekCompleted` and `IAudioEngine.PlaybackStarted` from the interface and all active implementations/invocations.
- Remove no-subscriber `ICdgEngine.PositionChanged` from the interface and `DxCdgEngine` invocation.
- Remove only the corresponding `ClockAudioEngine.SeekCompleted` / `PlaybackStarted` declarations and invokes. Keep `ClockAudioEngine.PositionChanged` because `IAudioEngine.PositionChanged` is live through `AudioPipeline`.

**Exclusions (do NOT remove in this stage):**

- `IAudioEngine.PositionChanged` — live. `AudioPipeline` subscribes/unsubscribes and uses it to update `MediaState.PositionSeconds`.
- `IAudioEngine.StateChanged`, `Opened`, `Closed`, and `PlaybackEnded` — live through `AudioPipeline` state/lifecycle/end handling.
- `IVideoEngine.PositionChanged`, `VideoPipeline.PositionChanged`, and seek comments that reference video position events.
- `ICdgEngine.PlaybackStarted`, `StateChanged`, `Opened`, `Closed`, `FrameInvalidated`, and `BackgroundWashChanged` — out of scope for this stage even if some have no current subscribers.
- `ClockAudioEngine` normalization no-op/fake-analysis paths and unsupported operations (`Normalize`, `NormalizationGainChanged`, `AnalyzePeak*`, `Init`, `Open`, `SetPitch`) — defer to Stage 7 clock/audio interface split.
- Unrelated `SetLastError`, `MBass.LastError`, WPF `SizeChanged`, MVVM `PropertyChanged`, and other framework/renderer events.

**Trace guards:**

```powershell
# Whole-repo/source guards from repo root. Include active projects, WPF/XAML, docs, project/config files, scripts, nameof/reflection strings, and bindings.
rg -n "\bMediaState\b|LastError|SetError\(|OnChanged\(|Changed\s*(\+=|-=)|\.Changed\b|event\s+EventHandler\??\s+Changed" . -g "*.cs" -g "*.xaml" -g "*.csproj" -g "*.props" -g "*.targets" -g "*.sln" -g "*.slnx" -g "*.ps1" -g "*.json" -g "*.config" -g "*.md"
rg -n "SeekCompleted|PlaybackStarted|PositionChanged" . -g "*.cs" -g "*.xaml" -g "*.csproj" -g "*.props" -g "*.targets" -g "*.sln" -g "*.slnx" -g "*.ps1" -g "*.json" -g "*.config" -g "*.md"
rg -n "Path=LastError|\{Binding LastError|Path=Changed|\{Binding Changed|Path=SeekCompleted|Path=PlaybackStarted|Path=PositionChanged" . -g "*.xaml" -g "*.json" -g "*.config"
rg -n "nameof\((SeekCompleted|PlaybackStarted|PositionChanged|LastError|Changed|SetError)\)|\"(SeekCompleted|PlaybackStarted|PositionChanged|LastError|SetError)\"" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.md"

# Subscriber guards. Expected before removal: zero hits for SeekCompleted/PlaybackStarted subscribers; PositionChanged hits only in AudioPipeline and must stay.
rg -n "SeekCompleted\s*(\+=|-=)" . -g "*.cs"
rg -n "PlaybackStarted\s*(\+=|-=)" . -g "*.cs"
rg -n "PositionChanged\s*(\+=|-=)" . -g "*.cs"

# Post-removal active-project guards. Expected: no SeekCompleted/PlaybackStarted, no ICdgEngine/DxCdg PositionChanged; IAudioEngine/IVideoEngine PositionChanged still exists.
rg -n "SeekCompleted|PlaybackStarted|LastError|SetError\(|OnChanged\(|event\s+EventHandler\??\s+Changed" FatzBombMedia.MediaControl FatzBombMedia.MediaControl.Wpf FatzBombMedia.Player -g "*.cs" -g "*.xaml" -g "*.csproj"
rg -n "ICdgEngine.*PositionChanged|DxCdgEngine.*PositionChanged|PositionChanged\?\.Invoke\(this, field\)" FatzBombMedia.MediaControl -g "*.cs"
```

**Exit criteria:** build passes; open/load failure still reaches the caller through existing bool/exception/status paths.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

**Latest review findings — 2026-06-30:**

- **Safety:** medium. Whole-solution guards found no subscribers for `IAudioEngine.SeekCompleted`, `IAudioEngine.PlaybackStarted`, or `ICdgEngine.PositionChanged`; no XAML/config/reflection/nameof/binding consumers were found. `MediaState.Changed` has no subscribers, and `MediaState.LastError` is only written/reset internally. Removal touches public Core interfaces, so build coverage across all three active projects is required.
- **Safe items:** remove `MediaState.Changed`, `OnChanged()`, `LastError`, and `SetError()` plus the three `AudioPipeline.State.SetError(...)` calls, preserving existing `false` returns. Remove `SeekCompleted`/`PlaybackStarted` from `IAudioEngine`, `AudioEngine`, `VideoAudioEngine`, and `ClockAudioEngine`, including invokes. Remove `PositionChanged` only from `ICdgEngine`/`DxCdgEngine`.
- **Exclusions/defer items:** preserve `IAudioEngine.PositionChanged`, `IAudioEngine.StateChanged`, `Opened`, `Closed`, and `PlaybackEnded`; preserve `IVideoEngine.PositionChanged`; preserve `ClockAudioEngine.PositionChanged`; defer clock normalization/no-op unsupported-operation cleanup to Stage 7; ignore unrelated `SetLastError`, `MBass.LastError`, `SizeChanged`, `PropertyChanged`, and renderer/framework events.
- **Corrected whole-solution guards:** use the guard block above. The original `Changed +=` / `PositionChanged +=` guard was too broad and did not distinguish live `IAudioEngine.PositionChanged` from dead `ICdgEngine.PositionChanged`.
- **Likely files affected:** `FatzBombMedia.MediaControl\Pipeline\MediaState.cs`, `FatzBombMedia.MediaControl\Pipeline\AudioPipeline.cs`, `FatzBombMedia.MediaControl\Core\Abstractions\IAudioEngine.cs`, `FatzBombMedia.MediaControl\Core\Abstractions\ICdgEngine.cs`, `FatzBombMedia.MediaControl\Core\Audio\AudioEngine.cs`, `FatzBombMedia.MediaControl\Core\Audio\VideoAudioEngine.cs`, `FatzBombMedia.MediaControl\Pipeline\ClockAudioEngine.cs`, `FatzBombMedia.MediaControl\Core\DxCdg\DxCdgEngine.cs`.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Smoke tests:** launch Player; verify invalid/missing audio open still returns failure without crashing; load/play/pause/seek/stop audio; load/play/seek video with audio; load/play/seek standalone CDG and audio+CDG; confirm playback end still fires for audio/video/CDG paths; confirm Player position/status continues updating after audio seeks.
- **Recommendation:** proceed with exclusions. If a new subscriber appears for `SeekCompleted`, `PlaybackStarted`, or `ICdgEngine.PositionChanged`, stop and keep that event or wire it intentionally.

### Stage 7 — ClockAudioEngine Interface Split

**Goal:** stop representing a virtual clock as a full real audio engine with no-op BASS-like features.

**Risk:** medium-high because `PlaybackCoordinator.ActiveAudio` currently returns `IAudioEngine` and may hand out `ClockAudioEngine`.

**Scope:**

- Keep `ClockAudioEngine : IAudioEngine` for this stage; do not introduce a new clock abstraction yet. The safer stage-sized change is to stop routing audio effects into the clock engine and make unsupported clock members explicit.
- Audit `MediaEffects` so volume/pitch/tempo/normalize and normalization-gain subscriptions only apply when the active media content includes real audio (`MediaContent.Audio`). Preserve the stored UI values so they apply when real audio becomes active.
- Make unsupported `ClockAudioEngine` real-audio operations fail explicitly instead of silently pretending to work: `Init`, both `Open(...)` overloads, `SetVolume`, `SetPitch`, fake normalization analysis (`AnalyzePeakQuickAsync`, `AnalyzePeakAsync`), and the explicit no-op `IAudioEngine.PlaybackEnded` event path.
- Keep `ClockAudioEngine.SetTempo` only if implementation intentionally supports clock tempo independent of audio; otherwise guard `MediaEffects` so the UI tempo control does not apply to non-audio content. Do not change clock tempo behavior accidentally.

**Legacy connection:** `ClockAudioEngine` exists to satisfy `IAudioEngine` where only timing is needed. The extra no-op members are legacy leakage from using one interface for real audio and virtual timing.

**Exclusions (do NOT remove in this stage):**

- Do not change `IAudioEngine` or `IMediaEngine.Audio` signatures in this stage; broad interface split is deferred unless a later stage explicitly approves it.
- Preserve `ClockAudioEngine` transport behavior: `Play`, `Pause`, `Stop`, `Close`, `Position`, `PositionChanged`, `StateChanged`, `Opened`, and `Closed` are used for standalone CDG/video-without-audio timing.
- Preserve real audio effects on `AudioEngine` and `VideoAudioEngine`.
- Preserve Player UI bindings for volume, pitch, tempo, and normalize; this stage changes whether those effects are applied to non-audio content, not the UI surface.
- Do not touch renderer, WPF adapter, fire-and-forget, or Stage 8+ renderer cleanup.

**Trace guards:**

```powershell
# Whole-repo/source guards from repo root. Include active projects, WPF/XAML, docs, project/config files, scripts, nameof/reflection strings, and bindings.
rg -n "ClockAudioEngine|ClockAudio|ActiveAudio|IAudioEngine\s+Audio|SetPitch\(|SetTempo\(|SetVolume\(|Normalize\b|AnalyzePeak|NormalizationGain|IsNormalizationReady|PlaybackEnded" . -g "*.cs" -g "*.xaml" -g "*.csproj" -g "*.props" -g "*.targets" -g "*.sln" -g "*.slnx" -g "*.ps1" -g "*.json" -g "*.config" -g "*.md"
rg -n "nameof\((ClockAudioEngine|ClockAudio|ActiveAudio|SetPitch|SetTempo|SetVolume|Normalize|AnalyzePeak|NormalizationGain|IsNormalizationReady|PlaybackEnded)\)|\"(ClockAudioEngine|ClockAudio|ActiveAudio|SetPitch|SetTempo|SetVolume|Normalize|AnalyzePeak|NormalizationGain|IsNormalizationReady|PlaybackEnded)\"" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.md"

# Focused active-project guards.
rg -n "ClockAudioEngine|ClockAudio|ActiveAudio" FatzBombMedia.MediaControl -g "*.cs"
rg -n "SetVolume\(|SetPitch\(|SetTempo\(|Normalize\s*=|\.Normalize\b|NormalizationGainChanged|IsNormalizationReady|AnalyzePeakQuickAsync\(|AnalyzePeakAsync\(" FatzBombMedia.MediaControl -g "*.cs"
rg -n "Volume|Pitch|Tempo|Normalize" FatzBombMedia.Player -g "*.cs" -g "*.xaml"

# Post-change guards. Expected: MediaEffects does not call effect/normalization members on non-audio content; ClockAudioEngine fake analysis no longer raises NormalizationGainChanged.
rg -n "_subscribedAudioEngine\.(SetVolume|SetPitch|SetTempo|Normalize)|Audio\.(SetVolume|SetPitch|SetTempo|Normalize|CurrentTempo|NormalizationGain)" FatzBombMedia.MediaControl\Controllers\MediaEffects.cs
rg -n "NormalizationGainChanged\?\.Invoke|Task\.FromResult\(NormalizationGain\)|SetVolume\(double|SetPitch\(int|AnalyzePeakQuickAsync|AnalyzePeakAsync|Init\(|Open\(" FatzBombMedia.MediaControl\Pipeline\ClockAudioEngine.cs
```

**Exit criteria:** standalone CDG and video-without-audio still seek/play/stop correctly; volume/pitch/tempo UI does not silently claim success on non-audio content.

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

**Latest review findings — 2026-06-30:**

- **Safety:** medium-high. Whole-repo guards found live UI bindings for volume/pitch/tempo/normalize and live controller code that applies these through `MediaEffects` to `_mediaEngine.Audio`, which can be `ClockAudioEngine` for standalone CDG, video-without-audio, and idle states. Directly throwing from `ClockAudioEngine` without first guarding `MediaEffects` would regress UI interactions.
- **Safe items:** update `MediaEffects` to apply volume/pitch/tempo/normalize and subscribe to `NormalizationGainChanged` only when `IMediaEngine.Content` has `MediaContent.Audio`. Keep stored effect values so they apply on the next real-audio engine. After that guard, make `ClockAudioEngine.Init`, `Open(...)`, `SetVolume`, `SetPitch`, and fake analysis methods fail explicitly. Remove the fake `NormalizationGainChanged?.Invoke(...)` calls from clock analysis methods.
- **Exclusions/defer items:** keep `ClockAudioEngine` as `IAudioEngine` for this stage; preserve clock transport (`Play`, `Pause`, `Stop`, `Position`, `SetTempo` if intentionally used for clock pacing, state/lifecycle events); preserve all real audio behavior in `AudioEngine`/`VideoAudioEngine`; leave UI bindings in place; defer a true smaller clock abstraction or public interface split to a later explicit stage.
- **Corrected whole-solution guards:** use the guard block above. The original guard was too narrow and did not catch Player UI bindings or the `MediaEffects` path that currently applies effects to `ClockAudioEngine`.
- **Likely files affected:** `FatzBombMedia.MediaControl\Controllers\MediaEffects.cs`, `FatzBombMedia.MediaControl\Pipeline\ClockAudioEngine.cs`. Possible small supporting edits in `FatzBombMedia.MediaControl\Pipeline\PlaybackCoordinator.cs` or `PipelineMediaEngine.cs` only if needed to expose/check real-audio state without referencing pipeline types from controllers.
- **Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore`.
- **Smoke tests:** launch Player; adjust volume/pitch/tempo/normalize with no media loaded, standalone CDG, video without audio, audio-only, audio+CDG, and video with audio; verify standalone CDG and video-without-audio still play/pause/seek/stop; verify audio effects still apply to real audio; verify PlayOnLoad normalization wait still works for real audio and does not block non-audio content.
- **Recommendation:** proceed with exclusions. Do not perform a broad `IAudioEngine`/clock abstraction split in this stage; guard `MediaEffects` first, then make only unsupported `ClockAudioEngine` audio-effect/open/analysis members explicit.

### Stage 8 — Renderer-Adjacent Dead API Cleanup

**Goal:** remove dead D3D/composition members without changing active presentation behavior.

**Risk:** high. Keep isolated from all non-renderer cleanup.

**Scope (corrected 2026-06-30 — bridge-level only, no downstream cascade):**

- `NativeD3D11PresentationBridge` dead public members only (items 84-89): `IsReady` (line 30), `ResizeNativeHwndPresentation` (143), `UploadFrame` (151), `PresentDxCdgSnapshotVisible` (187), `RefreshNativePresentation` (203), `ClearPresentedFrame` (217). Bridge is `internal sealed`, instantiated at `NativeMediaPresentationCoordinator.cs:59`; the coordinator never calls these six. **Do NOT cascade into the downstream `D3D11RendererPresentationServices` / `D3D11PresentationSurfaceController` / `D3D11PresentationBackendSet` chains** (`UploadCpuFrame`, services/controller `ClearPresentedFrame`/`ResizeNativeHwndPresentation`/`PresentDxCdgSnapshotVisible`, `services.IsReady`). Those become orphaned-but-compiling and belong to Renderer Stages A/C/D in §11.9.
- Composition dead values/properties with zero callers (items 109-117): `MediaCompositionLayerSourceKind.FutureVisualizer` (109), `MediaCompositionLayerKind.Diagnostics`/`UiOverlay` (110-111), `MediaCompositionLayerId.Diagnostics`/`UiOverlay` (112-113), `MediaCompositionLayer.HasNaturalSize` (114), `MediaCompositionFrame.HasAnyVisibleLayer` (115), and `MediaCompositionFrame.LayerSummary` + `CreateLayerDescriptors` (116-117, removed together). Private `EnumerateLayers()` is LIVE (used by `D3D11CompositionLayerCache` + `D3D11MediaCompositionPresenter`) and must stay.
- `D3D11CompositionTargetKind.RetainedOffscreenLayer` (118) and `FutureSwapChain` (119): zero usages, build-safe to remove, but they are §11.7 Cluster F offscreen/future scaffolding. Remove only if abandoning the offscreen retained-layer plan; otherwise defer to Renderer Stage F.

**Excluded (audit error — do NOT remove in this stage):**

- `D3D11CompositionTargetPlan.HasMultipleTargets` (item 120) is **NOT dead**. It is read by the live `D3D11CompositionTargetPlan.Summary` getter (line 42, `multiTarget={HasMultipleTargets}`), which is consumed by `D3D11MediaCompositionPresenter` (lines 501, 1029-1030). Removing it requires also editing the `Summary` interpolation. Leave it in place, or trim it together with the `Summary` string in a deliberate diagnostics change — not as a "dead member" deletion.

**Legacy connection:** many names point to earlier or future DirectComposition/shared-surface plans (`FutureSwapChain`, `RetainedOffscreenLayer`, `UiOverlay`, `Diagnostics`). They are likely legacy scaffolding, but renderer paths are high blast-radius.

**Trace guards (whole-solution, from repo root `C:\Programming\FatzBombMedia`):**

```powershell
# Bridge members — verify zero external callers (definitions/internal delegations only):
rg -n "\bIsReady\b|UploadFrame|RefreshNativePresentation|ClearPresentedFrame|ResizeNativeHwndPresentation|PresentDxCdgSnapshotVisible" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config" -g "*.csproj"
# Composition members — verify zero callers:
rg -n "FutureVisualizer|RetainedOffscreenLayer|FutureSwapChain|HasMultipleTargets|LayerSummary|CreateLayerDescriptors|HasAnyVisibleLayer|HasNaturalSize" . -g "*.cs" -g "*.xaml" -g "*.json" -g "*.config"
# Ambiguous enum members — scope precisely (substring 'Diagnostics'/'UiOverlay' collide with namespaces):
rg -n "LayerKind\.Diagnostics|LayerId\.Diagnostics|\.UiOverlay\b" . -g "*.cs"
# Guard live members that MUST survive:
rg -n "EnumerateLayers" . -g "*.cs"                 # live: cache + presenter — keep
rg -n "\.Summary\b" FatzBombMedia.MediaControl\Core\Graphics -g "*.cs"   # D3D11CompositionTargetPlan.Summary is live — keep HasMultipleTargets
rg -n "D3D11CompositionTargetKind\." . -g "*.cs"    # confirm only None/FinalPresentation/DirectCompositionOverlay used
```

**Likely files affected:**

- `FatzBombMedia.MediaControl\Core\Graphics\D3D\NativeD3D11PresentationBridge.cs` (remove 6 members)
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionLayerSourceKind.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionLayerKind.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionLayerId.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionLayer.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionFrame.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\MediaCompositionFrame.Layers.cs`
- `FatzBombMedia.MediaControl\Core\Graphics\Composition\D3D11\D3D11CompositionTargetKind.cs` (only if abandoning offscreen/future scaffolding)
- No `.csproj`/XAML/JSON edits expected (no references found in those file types).

**Build commands:** `dotnet build FatzBombMedia.sln --no-restore`; `dotnet build -c Release FatzBombMedia.sln --no-restore` (Release = warnings-as-errors; confirmed no private-member orphaning because `EnumerateLayers` stays live and the removed bridge downstream targets are public, not private).

**Status:** Completed — 2026-06-30. Debug and Release builds pass.

**Manual verification after this stage:** run Player and validate primary output, secondary/projector output, resize/fullscreen, video, image, audio+CDG, and sidecar background cases from `docs/VALIDATION.md`.

**Latest review findings — 2026-06-30:**

- **Safety: Medium.** Build risk is low — every confirmed item (84-89, 109-117) has zero callers across the whole solution (all `.cs`; no XAML/JSON/config/reflection/`nameof` hits). But this is renderer-adjacent (high blast-radius per the stage's own label), so manual `docs/VALIDATION.md` smoke testing is mandatory, and one audit item is mischaracterized.
- **Safe items (confirmed zero callers, build-safe):** bridge members 84-89 (`IsReady`, `UploadFrame`, `RefreshNativePresentation`, `ClearPresentedFrame`, `ResizeNativeHwndPresentation`, `PresentDxCdgSnapshotVisible`); composition 109 (`FutureVisualizer`), 110/111 (`MediaCompositionLayerKind.Diagnostics`/`UiOverlay`), 112/113 (`MediaCompositionLayerId.Diagnostics`/`UiOverlay`), 114 (`HasNaturalSize`), 115 (`HasAnyVisibleLayer`), and 116+117 (`LayerSummary` + `CreateLayerDescriptors`, removed as a pair).
- **Exclude/defer:**
  - **Exclude 120 `D3D11CompositionTargetPlan.HasMultipleTargets`** — not dead; read by live `Summary` (consumed by presenter at 501/1029-1030). Audit's "Never read" is wrong.
  - **Defer 118/119 (`RetainedOffscreenLayer`, `FutureSwapChain`)** unless the offscreen retained-layer future (§11.7 Cluster F) is explicitly abandoned. Build-safe to remove, but they are intentional scaffolding tied to `D3D11CompositionLayerRenderTarget`/`OffscreenRenderTarget`.
  - **Do not cascade** the 6 bridge removals into the downstream services/controller/backendset chains (Renderer Stages A/C/D).
  - Keep private `EnumerateLayers()` — live.
- **Corrected whole-solution guards:** the original guards were project-local (`FatzBombMedia.MediaControl` only) and used bare `Diagnostics`/`UiOverlay`/`IsReady` substrings that collide with `System.Diagnostics`, the `D3D.Diagnostics` namespace, and three unrelated `IsReady` properties. Use the repo-root guard block above, which scopes the enum members (`LayerKind.Diagnostics`, `LayerId.Diagnostics`, `.UiOverlay`) and adds positive guards for the live members that must survive (`EnumerateLayers`, `D3D11CompositionTargetPlan.Summary`).
- **Smoke tests:** launch Player; validate primary DirectComposition output, native HWND fallback, secondary/projector output, hardware video, image/BGRA + software fallback, audio+CDG, video+CDG, sidecar image/video background, resize/live-resize/fullscreen, and audio-only/stopped/idle clear behavior (the removed bridge clear/refresh/resize/upload/DXCDG-snapshot paths must show no regression because the live coordinator uses the queued/composition paths instead).
- **Recommendation: proceed with exclusions.** Remove bridge items 84-89 and composition items 109-117 only; exclude 120; defer 118/119 to the Cluster F decision; do not touch downstream renderer chains. Build Debug + Release, then run the manual renderer checklist. If any new caller appears for a bridge member or composition value, stop and keep that member.

### Stage 9 — WPF Adapter Cleanup

**Goal:** remove WPF adapter legacy surface and duplicated native host code without violating adapter boundaries.

**Risk:** medium.

**Scope:**

- Remove `MediaPresentationMode.D3DImage`; consider removing `NativeHwnd` if `NativeHwndOverlay` is the only supported mode.
- Remove `MediaSurface.BitmapScalingMode` DP and Player/ViewModel bindings if still no renderer reads it.
- Remove dead WPF `Player` constructors/static factories if external API compatibility is not required.
- Extract duplicated `NativeHwndSurfaceHost` from `MediaSurface.cs` and `SecondaryPresentationWindow.cs` to one internal WPF class.

**Trace guards:**

```powershell
rg -n "D3DImage|NativeHwnd\b|BitmapScalingMode|new Player\(|Player\.CreateAsync|NativeHwndSurfaceHost" .
rg -n "using FatzBombMedia\.MediaControl\.(Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video)" FatzBombMedia.MediaControl.Wpf
```

**Exit criteria:** build passes; WPF adapter imports remain boundary-clean; primary and secondary HWND surfaces attach/detach/resize.

### Stage 10 — Fire-and-Forget & Failure-Signal Cleanup

**Goal:** clean risky discards after dead-code pruning makes behavior easier to reason about.

**Risk:** medium-high because this can change runtime behavior.

**Scope:**

- `MediaFile` sync `Load()` fire-and-forget `Task.Run(HandlePlayOnLoadAsync)`.
- Discarded bool returns from `_mediaControls.Stop()` and `_mediaEngine.SetBackground()`.
- `VideoPipeline` post-seek `_audioEngine.Play()` discard.
- `Directory.CreateDirectory(tempDir)` discard.

**Recommendation:** convert to explicit handling, logging, or async-only control flow. Do not mix with API deletions.

**Trace guards:**

```powershell
rg -n "_ = Task\.Run|_ = \(_mediaControls\?\.Stop\(\)\)|_ = _mediaEngine\.SetBackground|_ = _audioEngine!\.Play\(|Directory\.CreateDirectory" FatzBombMedia.MediaControl
```

**Exit criteria:** build passes; seek after playback, load with PlayOnLoad, and sidecar background failure cases do not silently regress.

### Stage 11 — Release Build, Smoke Test, and Documentation Sync

**Goal:** close the cleanup loop after code changes.

**Risk:** none; validation only.

**Commands:**

```powershell
dotnet build FatzBombMedia.sln --no-restore
dotnet build -c Release FatzBombMedia.sln --no-restore
```

**Manual checklist:** use `docs/VALIDATION.md` after Stages 8-10, and after any stage that touches WPF surface management or native presentation.

**Docs to update:**

- Remove completed items from `AGENTS.md` `Known Dead Code`.
- Mark completed sections in this file or move them to a changelog.
- If public API was removed, document package-breaking changes.

---

## 11. Focused Renderer Legacy Audit

Rechecked `Core\Graphics\D3D\` and `Core\Graphics\Composition\D3D11\` specifically for leftover code from earlier presentation approaches, including pre-control rendering, D3DImage/shared-surface experiments, direct CPU frame upload, direct single-target CDG presentation, and future/offscreen planning scaffolding.

### 11.1 Current Live Control-Owned DX11 Path

The current path is control-owned and hardware/DX11-centered:

1. `MediaSurface` / `SecondaryPresentationWindow` create native child HWNDs only.
2. WPF passes `MediaPresentationSurfaceDescriptor.NativeHwnd(...)` to `MediaControlPlayer`.
3. `NativeMediaPresentationCoordinator` owns D3D attach/resize/detach and tries DirectComposition first for the primary surface, with native HWND swap-chain fallback and native HWND secondary targets.
4. `NativeD3D11PresentationBridge` delegates to `D3D11RendererPresentationServices`.
5. `D3D11RendererPresentationServices` owns `D3D11DeviceResources`, `D3D11MediaCompositionPresenter`, `D3D11PresentationSurfaceController`, `D3D11PresentationBackendSet`, DirectComposition backend, and native HWND backend.
6. `D3D11MediaCompositionPresenter.PresentToTargets(...)` draws composed background/wash/CDG layers into backend target textures.
7. `D3D11PresentationBackendSet.NotifyTargetsPresented(...)` presents/commits the active backend swap chains.

Live renderer files that should not be deleted casually:

- `Core\Graphics\NativeMediaPresentationCoordinator.cs`
- `Core\Graphics\D3D\NativeD3D11PresentationBridge.cs`
- `Core\Graphics\D3D\Device\D3D11DeviceResources.cs`
- `Core\Graphics\D3D\Video\D3D11VideoProcessorPresenter.cs`
- `Core\Graphics\D3D\SwapChain\D3D11SwapChainFactory.cs`
- `Core\Graphics\D3D\SwapChain\D3D11SwapChainPresenter.cs`
- `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs`
- `Core\Graphics\D3D\Presentation\D3D11PresentationSurfaceController.cs`
- `Core\Graphics\D3D\Presentation\D3D11PresentationBackendSet.cs`
- `Core\Graphics\D3D\Presentation\D3D11PresentationTargetSnapshot.cs`
- `Core\Graphics\D3D\Presentation\D3D11PresentationBackendTarget.cs`
- `Core\Graphics\D3D\Presentation\DirectComposition\D3D11DirectCompositionSingleTargetBackend.cs`
- `Core\Graphics\D3D\Presentation\DirectComposition\D3D11DirectCompositionTarget.cs`
- `Core\Graphics\D3D\Presentation\NativeHwnd\D3D11NativeHwndSwapChainBackend.cs`
- `Core\Graphics\Composition\D3D11MediaCompositionPresenter.cs`
- `Core\Graphics\Composition\D3D11\D3D11CompositionLayerState.cs`
- `Core\Graphics\Composition\D3D11\D3D11CompositionLayerCache.cs`
- `Core\Graphics\Composition\D3D11\D3D11CompositionTargetRegistry.cs`
- `Core\Graphics\Composition\D3D11\D3D11CompositionTargetState.cs`
- `Core\Graphics\Composition\D3D11\D3D11CompositionTargetPlan.cs`

### 11.2 Legacy Cluster A — Old CPU Frame Upload Path

This appears to be leftover from a CPU/BGRA upload path before the current composition/hardware-present path. No repo caller reaches `NativeD3D11PresentationBridge.UploadFrame(...)`.

Dead chain:

- `Core\Graphics\D3D\NativeD3D11PresentationBridge.cs` — `UploadFrame(...)`
- `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs` — `UploadCpuFrame(...)`
- `Core\Graphics\D3D\Presentation\D3D11PresentationFramePresenter.cs` — `UploadCpuFrame(...)`
- `Core\Graphics\D3D\Frame\D3D11FramePresenter.cs` — `UploadCpuFrame(...)`
- `Core\Graphics\D3D\Upload\D3D11FrameUploadPipeline.cs` — `UploadCpuFrame(...)`
- `Core\Graphics\D3D\Upload\D3D11TextureUploadCache.cs` — `Ensure(...)`
- `Core\Graphics\D3D\Upload\D3D11FrameUploadResources.cs` — `TextureCache`, `RecordUploadWithCurrentTarget(...)`

Notes:

- `D3D11FrameUploadPipeline.cs` is likely a complete dead file if `UploadFrame(...)` is removed.
- `D3D11TextureUploadCache.cs` is likely a complete dead file if the CPU upload path is removed.
- `D3D11FrameUploadResources.RecordUpload(...)` is still live for hardware-frame diagnostics, so do not delete the whole resources file without splitting it.

Risk: medium renderer risk. Build catches references, but smoke-test image/BGRA fallback, software-decoded fallback, hardware video, and CDG after removal.

### 11.3 Legacy Cluster B — UI Shared-Surface / D3DImage-Era Backend

Current WPF surfaces always attach as native HWND descriptors. No live caller invokes `CreateUiSharedSurface(...)`; the bridge injects `NullD3D11UiSharedSurfacePresentationBackend` only as a placeholder.

Likely legacy pieces:

- `Core\Graphics\D3D\Presentation\ID3D11UiSharedSurfacePresentationBackend.cs`
- `Core\Graphics\D3D\Presentation\NullD3D11UiSharedSurfacePresentationBackend.cs`
- `Core\Graphics\D3D\Presentation\D3D11RendererPresentationServices.cs` — `CreateUiSharedSurface(...)`
- `Core\Graphics\D3D\Presentation\D3D11PresentationSurfaceController.cs` — `CreateUiSharedSurface(...)`, `ActivateUiSharedSurface(...)`, `EnsureUiSharedSurface(...)`
- `Core\Graphics\D3D\Presentation\D3D11PresentationBackendKind.cs` — `UiSharedSurface`
- `FatzBombMedia.MediaControl.Wpf\MediaPresentationMode.cs` — `D3DImage`

Legacy connection: names and null backend match older D3DImage/shared-surface work before the control itself owned native HWND/DX11 presentation.

Risk: medium-high API and renderer risk. Remove only with WPF adapter cleanup because `MediaPresentationMode.D3DImage` and config values are public API-shaped.

### 11.4 Legacy Cluster C — Immediate Resize/Clear/Refresh APIs

The current coordinator queues native and DirectComposition resizes and applies them at presentation boundaries. Clearing now uses an empty/transparent composition frame. Older immediate APIs appear unreachable.

Likely dead chains:

- Immediate native resize:
  - `NativeD3D11PresentationBridge.ResizeNativeHwndPresentation(...)`
  - `D3D11RendererPresentationServices.ResizeNativeHwndPresentation(...)`
  - `D3D11PresentationSurfaceController.ResizeNativeHwndPresentation(...)`
  - `D3D11NativeHwndSwapChainBackend.Resize(...)` as an immediate helper; queued `ApplyPendingResizeIfNeeded(...)` is live.
- Direct clear/refresh:
  - `NativeD3D11PresentationBridge.RefreshNativePresentation(...)`
  - `NativeD3D11PresentationBridge.ClearPresentedFrame(...)`
  - `D3D11RendererPresentationServices.ClearPresentedFrame(...)`
  - `D3D11PresentationSurfaceController.ClearPresentedFrame(...)`
  - `D3D11PresentationBackendSet.ClearPresentedTargets(...)`
  - `D3D11PresentationBackendSet.ClearPresentedFrame(...)`

Risk: medium. Smoke-test resize, live resize, audio-only clear, stopped/idle CDG clear, secondary output close/open.

### 11.5 Legacy Cluster D — Direct DXCDG / Single-Target Presentation

Current CDG overlay path builds a `MediaCompositionFrame` and uses `PresentMediaCompositionVisible(...)` or DirectComposition overlay split. Older direct snapshot/single-target methods appear unreachable.

Likely dead pieces:

- `NativeD3D11PresentationBridge.PresentDxCdgSnapshotVisible(...)`
- `D3D11RendererPresentationServices.PresentDxCdgSnapshotVisible(...)`
- `D3D11MediaCompositionPresenter.PresentToTarget(...)` overloads
- `D3D11PresentationBackendKind.ExternalTexture` if only used by unused `PresentToTarget(...)`

Risk: medium-high. Smoke-test CDG-only, audio+CDG, video+CDG, DirectComposition overlay split, and secondary output before removal.

### 11.6 Legacy Cluster E — Direct Blit / Texture Helpers

These helpers look like old direct texture/blit presentation paths not used by the current composition fan-out.

Likely dead pieces:

- `D3D11PresentationBlitter.BlitToActivePresentation(...)`
- `D3D11PresentationFramePresenter.PresentTextureFrame(...)`
- `D3D11PresentationBackendSet.Present(...)`
- `D3D11PresentationBackendSet.HasNativePresentingBackend`
- `D3D11PresentationBackendSet.HasUiSharedSurfaceBackend`
- `D3D11PresentationRenderTargetViewCache.Current` and the mostly no-op `Release()` path, unless future code starts assigning `Current`.

Risk: medium. Remove only after Cluster A/C/D so the remaining blit path is easier to trace.

### 11.7 Legacy Cluster F — Future/Offscreen Retained-Layer Scaffolding

The current compositor draws all layers into final target textures. Offscreen retained layer render targets are instantiated/released as metadata scaffolding but never allocated or rendered.

Likely future-only/dead pieces:

- `Core\Graphics\Composition\D3D11\D3D11CompositionLayerRenderTarget.cs` — `Ensure(...)`, `MarkRendered(...)`, resource properties; functionally unused because no caller allocates it.
- `D3D11CompositionLayerState.OffscreenRenderTarget`
- `D3D11CompositionLayerState.HasOffscreenRenderTarget`
- `D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture`
- `D3D11CompositionTargetKind.RetainedOffscreenLayer`
- `D3D11CompositionTargetKind.FutureSwapChain`
- `D3D11CompositionLayerUpdateCadence.DisplayRate`
- `D3D11CompositionLayerUpdateAction.ClearOnly`
- `D3D11CompositionLayerSchedule.SourceFrameInterval`
- `D3D11CompositionTargetDescriptor.PrimaryFinalTarget(...)` factory
- `D3D11CompositionTargetRegistry.Capture(D3D11CompositionTargetDescriptor)` single-target overload
- `D3D11CompositionLayerCache.Layers`
- `D3D11CompositionLayerCache.ActiveLayerSummary`
- `D3D11CompositionTargetPlan.Targets`, `HasTargets`, `HasWork`, `ActivePresentationTargetIds`

Risk: high if removed broadly. Much of this feeds diagnostics and plan summaries, even if not behavior. Remove only after deciding not to pursue offscreen retained-layer rendering.

### 11.8 Stale Naming / Comments

Several renderer files still reference `VorticeD3D11Renderer` in comments or summary text, but no `VorticeD3D11Renderer` implementation was found. Treat this as pre-control refactor naming residue.

Trace guard:

```powershell
rg -n "VorticeD3D11Renderer|D3DImage|UiSharedSurface|UploadCpuFrame|PresentDxCdgSnapshotVisible|RetainedOffscreen|FutureSwapChain|ExternalTexture" FatzBombMedia.MediaControl
```

### 11.9 Recommended Renderer Cleanup Order

Do not remove all renderer legacy code in one commit. Suggested order:

1. **Renderer Stage A — remove old CPU upload chain**: remove `UploadFrame` chain, `D3D11FrameUploadPipeline`, and `D3D11TextureUploadCache`; preserve upload diagnostics still used by hardware frame path.
2. **Renderer Stage B — remove UI shared-surface/D3DImage backend**: remove null backend/interface and `CreateUiSharedSurface` branches after public API decision for `D3DImage` and config values.
3. **Renderer Stage C — remove immediate clear/refresh/resize APIs**: keep queued resize and composition-empty clearing only.
4. **Renderer Stage D — remove direct DXCDG/single-target present path**: keep composition-frame and DirectComposition overlay split paths.
5. **Renderer Stage E — prune direct blit helpers**: remove unused `PresentTextureFrame`, `BlitToActivePresentation`, backend direct `Present` wrappers.
6. **Renderer Stage F — decide offscreen retained-layer future**: either keep as documented future scaffold or delete the render-target/scheduling metadata.

Manual verification after each renderer stage:

- Primary DirectComposition playback
- Native HWND fallback if possible
- Secondary/projector output
- Hardware video
- Image/BGRA and software fallback
- Audio+CDG
- Video+CDG
- Sidecar image background
- Sidecar video background
- Resize/live resize/fullscreen
- Audio-only/stopped clear behavior
