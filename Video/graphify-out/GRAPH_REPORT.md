# Graph Report - FatzBombMedia  (2026-07-06)

## Corpus Check
- 190 files · ~78,370 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 1541 nodes · 3312 edges · 111 communities (83 shown, 28 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS · INFERRED: 6 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `645f0d72`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_Video Pipeline Seeks|Video Pipeline Seeks]]
- [[_COMMUNITY_FFmpeg Video Decoding|FFmpeg Video Decoding]]
- [[_COMMUNITY_Presentation Surface Management|Presentation Surface Management]]
- [[_COMMUNITY_D3D11 Rendering Bridge|D3D11 Rendering Bridge]]
- [[_COMMUNITY_Video Audio Engine|Video Audio Engine]]
- [[_COMMUNITY_Main Window FullScreen|Main Window FullScreen]]
- [[_COMMUNITY_Audio Engine Core|Audio Engine Core]]
- [[_COMMUNITY_Media Types & Composition|Media Types & Composition]]
- [[_COMMUNITY_Presentation Diagnostics & Clock|Presentation Diagnostics & Clock]]
- [[_COMMUNITY_Audio Pipeline Libraries|Audio Pipeline Libraries]]
- [[_COMMUNITY_D3D11 Surface Controller|D3D11 Surface Controller]]
- [[_COMMUNITY_D3D11 Video Processing|D3D11 Video Processing]]
- [[_COMMUNITY_Win32 Native Methods|Win32 Native Methods]]
- [[_COMMUNITY_CD-G Pipeline Playback|CD-G Pipeline Playback]]
- [[_COMMUNITY_Media Background Resolver|Media Background Resolver]]
- [[_COMMUNITY_Media Surface Window Hooks|Media Surface Window Hooks]]
- [[_COMMUNITY_Main Window ViewModel|Main Window ViewModel]]
- [[_COMMUNITY_Media Control Session|Media Control Session]]
- [[_COMMUNITY_CD-G State Machine|CD-G State Machine]]
- [[_COMMUNITY_D3D11 Composition Presenter|D3D11 Composition Presenter]]
- [[_COMMUNITY_Project Configuration & Dependencies|Project Configuration & Dependencies]]
- [[_COMMUNITY_D3D11 Backend Set|D3D11 Backend Set]]
- [[_COMMUNITY_D3D11 Layer State|D3D11 Layer State]]
- [[_COMMUNITY_CD-G Engine|CD-G Engine]]
- [[_COMMUNITY_Media Control Player|Media Control Player]]
- [[_COMMUNITY_Media Surface Overlay|Media Surface Overlay]]
- [[_COMMUNITY_DirectComposition Single Backend|DirectComposition Single Backend]]
- [[_COMMUNITY_Playback Coordinator|Playback Coordinator]]
- [[_COMMUNITY_CD-G Color Manager|CD-G Color Manager]]
- [[_COMMUNITY_Media Engine Interface|Media Engine Interface]]
- [[_COMMUNITY_Composition Layer Update Plan|Composition Layer Update Plan]]
- [[_COMMUNITY_Audio Peak Analysis|Audio Peak Analysis]]
- [[_COMMUNITY_Bootstrap Library Loading|Bootstrap Library Loading]]
- [[_COMMUNITY_Media File Loading|Media File Loading]]
- [[_COMMUNITY_D3D11 Device Resources|D3D11 Device Resources]]
- [[_COMMUNITY_Native HWND Surface Host|Native HWND Surface Host]]
- [[_COMMUNITY_Clock Audio Engine|Clock Audio Engine]]
- [[_COMMUNITY_Archive Service Adapter|Archive Service Adapter]]
- [[_COMMUNITY_Media Control Event Handlers|Media Control Event Handlers]]
- [[_COMMUNITY_DirectComposition Target|DirectComposition Target]]
- [[_COMMUNITY_Audio Normalizer|Audio Normalizer]]
- [[_COMMUNITY_Playback Scheduler|Playback Scheduler]]
- [[_COMMUNITY_Native HWND Swap Chain|Native HWND Swap Chain]]
- [[_COMMUNITY_Audio Engine Interface|Audio Engine Interface]]
- [[_COMMUNITY_Service Provider & Libraries|Service Provider & Libraries]]
- [[_COMMUNITY_Media Control Operation Queue|Media Control Operation Queue]]
- [[_COMMUNITY_D3D11 Rendering Draw Calls|D3D11 Rendering Draw Calls]]
- [[_COMMUNITY_CDG Composition Frame|CDG Composition Frame]]
- [[_COMMUNITY_Binary Compatibility Analysis|Binary Compatibility Analysis]]
- [[_COMMUNITY_Viewport Geometry Mapping|Viewport Geometry Mapping]]
- [[_COMMUNITY_Media Controls|Media Controls]]
- [[_COMMUNITY_Video Engine Presentation|Video Engine Presentation]]
- [[_COMMUNITY_Swap Chain Factory|Swap Chain Factory]]
- [[_COMMUNITY_Application Startup & Exit|Application Startup & Exit]]
- [[_COMMUNITY_Composition Layer Schedule|Composition Layer Schedule]]
- [[_COMMUNITY_Composition Layer Cache|Composition Layer Cache]]
- [[_COMMUNITY_Precision Timer|Precision Timer]]
- [[_COMMUNITY_Render Pipeline CDG Video|Render Pipeline CDG Video]]
- [[_COMMUNITY_Video Engine Interface|Video Engine Interface]]
- [[_COMMUNITY_Secondary Presentation Window|Secondary Presentation Window]]
- [[_COMMUNITY_CDG Engine Interface|CDG Engine Interface]]
- [[_COMMUNITY_Media Input Source|Media Input Source]]
- [[_COMMUNITY_CDG Render Snapshot|CDG Render Snapshot]]
- [[_COMMUNITY_Media Player|Media Player]]
- [[_COMMUNITY_Media Session Attachments|Media Session Attachments]]
- [[_COMMUNITY_Render Coordinator Interface|Render Coordinator Interface]]
- [[_COMMUNITY_D3D11 Frame Presenter|D3D11 Frame Presenter]]
- [[_COMMUNITY_Media Effects Normalization|Media Effects Normalization]]
- [[_COMMUNITY_Presentation Resource Cleaner|Presentation Resource Cleaner]]
- [[_COMMUNITY_Frame Upload Resources|Frame Upload Resources]]
- [[_COMMUNITY_FatzMedia vs VLC Analysis|FatzMedia vs VLC Analysis]]
- [[_COMMUNITY_Player Validation Host|Player Validation Host]]
- [[_COMMUNITY_Community 103|Community 103]]
- [[_COMMUNITY_Community 104|Community 104]]
- [[_COMMUNITY_Community 105|Community 105]]
- [[_COMMUNITY_Community 106|Community 106]]
- [[_COMMUNITY_Community 107|Community 107]]
- [[_COMMUNITY_Community 108|Community 108]]

## God Nodes (most connected - your core abstractions)
1. `VideoPipeline` - 123 edges
2. `NativeMediaPresentationCoordinator` - 83 edges
3. `VideoDecoder` - 73 edges
4. `PipelineMediaEngine` - 59 edges
5. `VideoAudioEngine` - 58 edges
6. `D3D11MediaCompositionPresenter` - 52 edges
7. `MediaSurface` - 49 edges
8. `MainWindow` - 47 edges
9. `D3D11DeviceResources` - 43 edges
10. `D3D11RendererPresentationServices` - 37 edges

## Surprising Connections (you probably didn't know these)
- `MediaSurface` --references--> `IMediaControlSession`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/MediaSurface.cs → FatzBombMedia.MediaControl/Api/IMediaControlSession.cs
- `SecondaryPresentationWindow` --references--> `IMediaControlSession`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/SecondaryPresentationWindow.cs → FatzBombMedia.MediaControl/Api/IMediaControlSession.cs
- `Player` --inherits--> `MediaControlPlayer`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/Player.cs → FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs
- `PipelineMediaEngine` --implements--> `IMediaErrorSource`  [EXTRACTED]
  FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs → FatzBombMedia.MediaControl/Core/Abstractions/IMediaErrorSource.cs
- `PipelineMediaEngine` --implements--> `ILoopPlaybackControl`  [EXTRACTED]
  FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs → FatzBombMedia.MediaControl/Core/Abstractions/IMediaEngine.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Core Engine Playback Pipelines** — engine_playbackcoordinator, engine_mediasession, engine_video_pipeline, engine_audio_pipeline, engine_cdg_pipeline, engine_playback_clock [EXTRACTED 1.00]
- **Visual Composition and Presentation Path** — engine_native_media_presentation_coordinator, engine_d3d11mediacompositionpresenter, engine_d3d11videoprocessorpresenter, engine_video_decoder, engine_video_pipeline [INFERRED 0.80]
- **Karaoke Media Loading and Sync Flow** — engine_cdg_pipeline, engine_cdgstatemachine, engine_audio_engine, engine_media_file, engine_native_media_presentation_coordinator [EXTRACTED 1.00]

## Communities (111 total, 28 thin omitted)

### Community 0 - "Video Pipeline Seeks"
Cohesion: 0.05
Nodes (3): DWM_TIMING_INFO, VideoPipeline, VideoEngineState

### Community 1 - "FFmpeg Video Decoding"
Cohesion: 0.08
Nodes (15): ArrayPool, AVBufferRef, AVCodec, AVCodecContext, AVCodecContext_get_format, AVCodecID, AVFormatContext, AVHWDeviceType (+7 more)

### Community 2 - "Presentation Surface Management"
Cohesion: 0.08
Nodes (9): VideoFrameEventArgs, NativeHwnd(), WithSize(), CompositionPresentResult, NativeMediaPresentationCoordinator, MediaPresentationSurfaceDescriptor, MediaPresentationSurfaceRole, ReadOnlyMemory (+1 more)

### Community 3 - "D3D11 Rendering Bridge"
Cohesion: 0.14
Nodes (5): D3D11PresentationTargetSnapshot, DirectCompositionOverlaySplitBaseState, DirectCompositionOverlaySplitOverlayState, MediaCompositionFrame, D3D11RendererPresentationServices

### Community 4 - "Video Audio Engine"
Cohesion: 0.09
Nodes (5): VideoAudioEngine, AVFrame, AVPacket, AVStream, SwrContext

### Community 5 - "Main Window FullScreen"
Cohesion: 0.08
Nodes (14): CornerRadius, DragEventArgs, MainWindow, Grid, KeyEventArgs, MouseButtonEventArgs, MouseEventArgs, Panel (+6 more)

### Community 6 - "Audio Engine Core"
Cohesion: 0.09
Nodes (7): AudioEngine, AudioStreamManager, BassFlags, ConcurrentDictionary, BASS, MethodImpl, SyncProcedure

### Community 7 - "Media Types & Composition"
Cohesion: 0.26
Nodes (6): CompatibilityLevel, D3D11PresentationBackendTarget, EnumerationOptions, Exception, DirectoryScanner, IEnumerable

### Community 8 - "Presentation Diagnostics & Clock"
Cohesion: 0.11
Nodes (5): double, AudioPresentationClock, PlaybackClock, Stopwatch, VideoPresentationTiming

### Community 11 - "D3D11 Video Processing"
Cohesion: 0.12
Nodes (12): Destination, ID3D11Texture2D, ID3D11VideoContext, ID3D11VideoProcessor, ID3D11VideoProcessorEnumerator, ID3D11VideoProcessorOutputView, Rational, RawRect (+4 more)

### Community 12 - "Win32 Native Methods"
Cohesion: 0.25
Nodes (6): DllImport, NativeMethods, IntPtr, MarshalAs, PAINTSTRUCT, RECT

### Community 14 - "Media Background Resolver"
Cohesion: 0.25
Nodes (6): FromArchiveEntry(), FromPath(), MediaBackgroundResolver, ToCandidate(), MediaBackgroundKind, MediaBackgroundMatch

### Community 15 - "Media Surface Window Hooks"
Cohesion: 0.17
Nodes (5): DependencyProperty, MediaSurface, HwndSource, RoutedEventArgs, Window

### Community 16 - "Main Window ViewModel"
Cohesion: 0.13
Nodes (5): DateTime, DispatcherTimer, FatzMediaPlayer, ObservableObject, MainWindowViewModel

### Community 18 - "CD-G State Machine"
Cohesion: 0.06
Nodes (16): byte, CdgColorManager, CdgConstants, ClampToFrame(), CdgEngine, CdgStateMachine, ICdgSnapshotSource, CdgDirtyRect (+8 more)

### Community 19 - "D3D11 Composition Presenter"
Cohesion: 0.12
Nodes (9): Color4, D3D11MediaCompositionPresenter, ID3D11BlendState, ID3D11Buffer, ID3D11PixelShader, ID3D11RasterizerState, ID3D11SamplerState, ID3D11VertexShader (+1 more)

### Community 20 - "Project Configuration & Dependencies"
Cohesion: 0.11
Nodes (17): Docs, net10.0-windows7.0, Microsoft.NET.Sdk, net10.0-windows7.0, Microsoft.NET.Sdk, net10.0-windows7.0, Microsoft.NET.Sdk, CommunityToolkit.Mvvm (8.4.2) (+9 more)

### Community 21 - "D3D11 Backend Set"
Cohesion: 0.15
Nodes (4): D3D11PresentationBackendKind, Func, D3D11PresentationBackendSet, ID3D11PresentationBackend

### Community 22 - "D3D11 Layer State"
Cohesion: 0.22
Nodes (4): BindFlags, D3D11CompositionLayerState, ID3D11Device, MediaCompositionLayerDirtyFlags

### Community 23 - "CD-G Engine"
Cohesion: 0.12
Nodes (11): FileType, IArchiveService, IAudioEngine, ILoopPlaybackControl, MediaInputSource, PipelineMediaEngine, PlaybackCoordinator, PlaybackScheduler (+3 more)

### Community 24 - "Media Control Player"
Cohesion: 0.13
Nodes (5): MediaControlPlayer, IMediaEngine, MediaControlOperationQueue, MediaErrorInfo, NativeMediaPresentationCoordinator

### Community 25 - "Media Surface Overlay"
Cohesion: 0.19
Nodes (5): ContentPresenter, DependencyObject, DependencyPropertyChangedEventArgs, OverlayWindow, FrameworkElement

### Community 26 - "DirectComposition Single Backend"
Cohesion: 0.06
Nodes (16): AlphaMode, D3D11PresentationDiagnostics, D3D11DirectCompositionSingleTargetBackend, D3D11DirectCompositionTarget, IDCompositionDevice, IDCompositionTarget, IDCompositionVisual, IDXGIDevice (+8 more)

### Community 27 - "Playback Coordinator"
Cohesion: 0.15
Nodes (4): IArchiveEntryInfo, Dispose, PlaybackCoordinator, void

### Community 28 - "CD-G Color Manager"
Cohesion: 0.11
Nodes (18): 10. Renderer / Presentation Validation, 11. Automated Architecture Checks, 12. Long-Run Stability Validation, 13. Completion Criteria For Major Stages, 1. Build And Boundary, 2. Runtime Test Setup, 3. Routine Player Smoke Tests, 4. CDG-Specific Validation (+10 more)

### Community 29 - "Media Engine Interface"
Cohesion: 0.08
Nodes (5): ILoopPlaybackControl, IMediaEngine, CdgVisuals, MediaEffects, TimeSpan

### Community 30 - "Composition Layer Update Plan"
Cohesion: 0.20
Nodes (3): D3D11CompositionLayerUpdatePlan, D3D11CompositionTargetPlan, D3D11CompositionLayerUpdate

### Community 32 - "Bootstrap Library Loading"
Cohesion: 0.09
Nodes (9): bool, LibraryImport, D3D11NativeHwndSwapChainBackend, Bootstrap, Bootstrap, SafeWaitHandle, PrecisionWaiter, TimerWaitHandle (+1 more)

### Community 34 - "D3D11 Device Resources"
Cohesion: 0.21
Nodes (4): D3D11DeviceResources, ID3D11Multithread, MediaPlayerConfiguration, D3D11PresentationFramePresenter

### Community 35 - "Native HWND Surface Host"
Cohesion: 0.20
Nodes (6): NativeHwndSurfaceHost, HandleRef, height, HwndHost, SizeChangedInfo, width

### Community 37 - "Archive Service Adapter"
Cohesion: 0.21
Nodes (5): IArchiveHandle, IArchiveService, SharpArchiveAdapter, SharpArchiveEntryInfo, SharpArchiveHandle

### Community 39 - "DirectComposition Target"
Cohesion: 0.20
Nodes (5): D3D11CompositionTargetState, D3D11CompositionTargetDescriptor, D3D11CompositionTargetDirtyFlags, D3D11CompositionTargetUpdate, ToCompositionTargetDescriptor()

### Community 40 - "Audio Normalizer"
Cohesion: 0.18
Nodes (4): AudioNormalizer, LevelRetrievalFlags, ThreadLocal, VolumeParameters

### Community 41 - "Playback Scheduler"
Cohesion: 0.35
Nodes (3): CancellationTokenSource, Cts, PlaybackScheduler

### Community 42 - "Native HWND Swap Chain"
Cohesion: 0.24
Nodes (5): Fact, MediaErrorEventArgsTests, InlineData, MediaErrorCode, Theory

### Community 44 - "Service Provider & Libraries"
Cohesion: 0.21
Nodes (5): DLLPath, MediaPlayerServiceProvider, IReadOnlyList, IServiceCollection, IServiceProvider

### Community 45 - "Media Control Operation Queue"
Cohesion: 0.33
Nodes (3): BlockingCollection, Thread, MediaControlOperationQueue

### Community 46 - "D3D11 Rendering Draw Calls"
Cohesion: 0.20
Nodes (5): CdgRenderSnapshot, ID3D11DeviceContext, ID3D11RenderTargetView, ID3D11ShaderResourceView, Vector4

### Community 47 - "CDG Composition Frame"
Cohesion: 0.18
Nodes (4): Cdg, Empty(), WithoutTransientPayload(), MediaCompositionClearMode

### Community 48 - "Binary Compatibility Analysis"
Cohesion: 0.25
Nodes (6): compat, CompatibilityInfo, CpuType, FileCpuType, BinaryCompatibility, match

### Community 49 - "Viewport Geometry Mapping"
Cohesion: 0.25
Nodes (4): ViewportGeometry, MediaCompositionStretchMode, RectD, ViewportMapping

### Community 52 - "Swap Chain Factory"
Cohesion: 0.27
Nodes (3): MediaContent, MediaPlayerState, MediaState

### Community 53 - "Application Startup & Exit"
Cohesion: 0.22
Nodes (6): Application, ExitEventArgs, App, StartupEventArgs, StreamWriter, TextWriterTraceListener

### Community 54 - "Composition Layer Schedule"
Cohesion: 0.27
Nodes (7): EnumerateLayers(), StableColorRevision(), TryGetBackgroundLayer(), TryGetOverlayLayer(), From(), D3D11CompositionLayerSchedule, MediaCompositionLayer

### Community 55 - "Composition Layer Cache"
Cohesion: 0.24
Nodes (3): D3D11CompositionLayerCache, List, MediaCompositionLayerId

### Community 56 - "Precision Timer"
Cohesion: 0.22
Nodes (8): First Safe Step, Goal, Intended Direction, Not Allowed, Stage 004A — Typed Error Surface, Status, Validation, Why This Matters

### Community 62 - "CDG Render Snapshot"
Cohesion: 0.25
Nodes (7): Behavior Notes, Completed Stage — 004A Typed Error Surface, Deferred / Follow-Up Items, Files Changed, Original Goal, Validation, What Changed

### Community 64 - "Media Session Attachments"
Cohesion: 0.17
Nodes (4): FileTree, FileTreeBuilder, IDisposable, MediaSession

### Community 104 - "Community 104"
Cohesion: 0.50
Nodes (3): AcceptedMediaTypes, FromCurrentRuntime(), Sorted()

### Community 106 - "Community 106"
Cohesion: 0.40
Nodes (3): FatzBombMedia.MediaControl (Core Engine), FatzBombMedia.MediaControl.Wpf (WPF Adapter), Player (WPF Host)

## Knowledge Gaps
- **49 isolated node(s):** `1. Build And Boundary`, `2. Runtime Test Setup`, `3. Routine Player Smoke Tests`, `4. CDG-Specific Validation`, `5. Video Timing / Stutter Validation` (+44 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **28 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VideoPipeline` connect `Video Pipeline Seeks` to `Media Session Attachments`, `Bootstrap Library Loading`, `Presentation Surface Management`, `D3D11 Device Resources`, `Video Audio Engine`, `FFmpeg Video Decoding`, `Presentation Diagnostics & Clock`, `Playback Scheduler`, `Community 105`, `CD-G Pipeline Playback`, `Media Control Operation Queue`, `CD-G State Machine`, `Video Engine Presentation`, `Swap Chain Factory`, `Render Pipeline CDG Video`, `Video Engine Interface`, `Playback Coordinator`?**
  _High betweenness centrality (0.155) - this node is a cross-community bridge._
- **Why does `NativeMediaPresentationCoordinator` connect `Presentation Surface Management` to `Video Pipeline Seeks`, `D3D11 Surface Controller`, `CD-G State Machine`, `D3D11 Composition Presenter`, `Media Control Player`, `Media Engine Interface`, `Bootstrap Library Loading`, `D3D11 Device Resources`, `Media Control Event Handlers`, `Playback Scheduler`, `D3D11 Rendering Draw Calls`, `CDG Composition Frame`, `Viewport Geometry Mapping`, `Media Controls`, `Render Pipeline CDG Video`, `Video Engine Interface`, `CDG Engine Interface`, `Media Session Attachments`, `D3D11 Frame Presenter`, `Media Effects Normalization`, `Community 105`?**
  _High betweenness centrality (0.091) - this node is a cross-community bridge._
- **Why does `D3D11MediaCompositionPresenter` connect `D3D11 Composition Presenter` to `Bootstrap Library Loading`, `Presentation Surface Management`, `Media Effects Normalization`, `Presentation Diagnostics & Clock`, `Community 105`, `D3D11 Rendering Draw Calls`, `Viewport Geometry Mapping`, `CD-G State Machine`, `Video Engine Presentation`, `Composition Layer Cache`, `Render Pipeline CDG Video`, `Composition Layer Update Plan`?**
  _High betweenness centrality (0.081) - this node is a cross-community bridge._
- **What connects `1. Build And Boundary`, `2. Runtime Test Setup`, `3. Routine Player Smoke Tests` to the rest of the system?**
  _49 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Video Pipeline Seeks` be split into smaller, more focused modules?**
  _Cohesion score 0.05339805825242718 - nodes in this community are weakly interconnected._
- **Should `FFmpeg Video Decoding` be split into smaller, more focused modules?**
  _Cohesion score 0.08013468013468013 - nodes in this community are weakly interconnected._
- **Should `Presentation Surface Management` be split into smaller, more focused modules?**
  _Cohesion score 0.07908902691511387 - nodes in this community are weakly interconnected._