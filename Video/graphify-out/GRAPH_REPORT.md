# Graph Report - .  (2026-07-03)

## Corpus Check
- cluster-only mode — file stats not available

## Summary
- 1471 nodes · 3215 edges · 103 communities (77 shown, 26 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS · INFERRED: 6 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `b6b4c7eb`
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

## God Nodes (most connected - your core abstractions)
1. `VideoPipeline` - 123 edges
2. `NativeMediaPresentationCoordinator` - 83 edges
3. `VideoDecoder` - 73 edges
4. `VideoAudioEngine` - 58 edges
5. `D3D11MediaCompositionPresenter` - 52 edges
6. `MediaSurface` - 49 edges
7. `PipelineMediaEngine` - 48 edges
8. `MainWindow` - 47 edges
9. `D3D11DeviceResources` - 43 edges
10. `D3D11RendererPresentationServices` - 37 edges

## Surprising Connections (you probably didn't know these)
- `MediaSurface` --references--> `IMediaControlSession`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/MediaSurface.cs → FatzBombMedia.MediaControl/Api/IMediaControlSession.cs
- `Player` --inherits--> `MediaControlPlayer`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/Player.cs → FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs
- `SecondaryPresentationWindow` --references--> `IMediaControlSession`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/SecondaryPresentationWindow.cs → FatzBombMedia.MediaControl/Api/IMediaControlSession.cs
- `PipelineMediaEngine` --implements--> `ILoopPlaybackControl`  [EXTRACTED]
  FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs → FatzBombMedia.MediaControl/Core/Abstractions/IMediaEngine.cs
- `Player (WPF Host)` --implements--> `MediaControlPlayer (Base Class)`  [EXTRACTED]
  FatzBombMedia.MediaControl.Wpf/Player.cs → FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Core Engine Playback Pipelines** — engine_playbackcoordinator, engine_mediasession, engine_video_pipeline, engine_audio_pipeline, engine_cdg_pipeline, engine_playback_clock [EXTRACTED 1.00]
- **Visual Composition and Presentation Path** — engine_native_media_presentation_coordinator, engine_d3d11mediacompositionpresenter, engine_d3d11videoprocessorpresenter, engine_video_decoder, engine_video_pipeline [INFERRED 0.80]
- **Karaoke Media Loading and Sync Flow** — engine_cdg_pipeline, engine_cdgstatemachine, engine_audio_engine, engine_media_file, engine_native_media_presentation_coordinator [EXTRACTED 1.00]

## Communities (103 total, 26 thin omitted)

### Community 0 - "Video Pipeline Seeks"
Cohesion: 0.05
Nodes (3): DWM_TIMING_INFO, VideoPipeline, VideoEngineState

### Community 1 - "FFmpeg Video Decoding"
Cohesion: 0.05
Nodes (17): ArrayPool, AVBufferRef, AVCodec, AVCodecContext, AVCodecContext_get_format, AVCodecID, AVFrame, AVHWDeviceType (+9 more)

### Community 2 - "Presentation Surface Management"
Cohesion: 0.08
Nodes (10): VideoFrameEventArgs, NativeHwnd(), WithSize(), CompositionPresentResult, NativeMediaPresentationCoordinator, MediaCompositionStretchMode, MediaPresentationSurfaceDescriptor, MediaPresentationSurfaceRole (+2 more)

### Community 3 - "D3D11 Rendering Bridge"
Cohesion: 0.06
Nodes (8): D3D11PresentationTargetSnapshot, NativeD3D11PresentationBridge, DirectCompositionOverlaySplitBaseState, DirectCompositionOverlaySplitOverlayState, Lock, MediaCompositionFrame, D3D11RendererPresentationServices, ID3D11PresentationSurfaceProvider

### Community 4 - "Video Audio Engine"
Cohesion: 0.09
Nodes (6): VideoAudioEngine, AVFormatContext, AVPacket, AVStream, FFmpeg, SwrContext

### Community 5 - "Main Window FullScreen"
Cohesion: 0.08
Nodes (14): CornerRadius, DragEventArgs, MainWindow, Grid, KeyEventArgs, MouseButtonEventArgs, MouseEventArgs, Panel (+6 more)

### Community 6 - "Audio Engine Core"
Cohesion: 0.09
Nodes (7): AudioEngine, AudioStreamManager, BassFlags, ConcurrentDictionary, BASS, MethodImpl, SyncProcedure

### Community 7 - "Media Types & Composition"
Cohesion: 0.07
Nodes (16): AcceptedMediaTypes, FromCurrentRuntime(), Sorted(), CompatibilityLevel, D3D11CompositionTargetRegistry, D3D11CompositionTargetState, D3D11CompositionTargetDescriptor, D3D11CompositionTargetDirtyFlags (+8 more)

### Community 8 - "Presentation Diagnostics & Clock"
Cohesion: 0.09
Nodes (6): D3D11PresentationDiagnostics, double, AudioPresentationClock, PlaybackClock, Stopwatch, VideoPresentationTiming

### Community 9 - "Audio Pipeline Libraries"
Cohesion: 0.09
Nodes (7): AudioEngineState, PlayerNativeLibraries, MediaContent, MediaPlayerState, AudioPipeline, MediaState, string

### Community 11 - "D3D11 Video Processing"
Cohesion: 0.12
Nodes (12): Destination, ID3D11Texture2D, ID3D11VideoContext, ID3D11VideoProcessor, ID3D11VideoProcessorEnumerator, ID3D11VideoProcessorOutputView, Rational, RawRect (+4 more)

### Community 12 - "Win32 Native Methods"
Cohesion: 0.18
Nodes (6): DllImport, NativeMethods, IntPtr, MarshalAs, PAINTSTRUCT, RECT

### Community 14 - "Media Background Resolver"
Cohesion: 0.14
Nodes (8): FromArchiveEntry(), FromPath(), MediaBackgroundResolver, ToCandidate(), FileTypes, HashSet, MediaBackgroundKind, MediaBackgroundMatch

### Community 15 - "Media Surface Window Hooks"
Cohesion: 0.16
Nodes (5): DependencyProperty, MediaSurface, HwndSource, RoutedEventArgs, Window

### Community 16 - "Main Window ViewModel"
Cohesion: 0.13
Nodes (5): DateTime, DispatcherTimer, FatzMediaPlayer, ObservableObject, MainWindowViewModel

### Community 17 - "Media Control Session"
Cohesion: 0.09
Nodes (5): IMediaControlSession, FatzBombMedia.MediaControl (Core Engine), FatzBombMedia.MediaControl.Wpf (WPF Adapter), MediaControlPlayer (Base Class), Player (WPF Host)

### Community 18 - "CD-G State Machine"
Cohesion: 0.19
Nodes (7): byte, ClampToFrame(), CdgStateMachine, CdgDirtyRect, CdgPacket, Span, uint

### Community 19 - "D3D11 Composition Presenter"
Cohesion: 0.14
Nodes (9): Color4, D3D11MediaCompositionPresenter, ID3D11BlendState, ID3D11Buffer, ID3D11PixelShader, ID3D11RasterizerState, ID3D11SamplerState, ID3D11VertexShader (+1 more)

### Community 20 - "Project Configuration & Dependencies"
Cohesion: 0.11
Nodes (17): Docs, net10.0-windows7.0, Microsoft.NET.Sdk, net10.0-windows7.0, Microsoft.NET.Sdk, net10.0-windows7.0, Microsoft.NET.Sdk, CommunityToolkit.Mvvm (8.4.2) (+9 more)

### Community 21 - "D3D11 Backend Set"
Cohesion: 0.16
Nodes (4): D3D11PresentationBackendKind, Func, D3D11PresentationBackendSet, ID3D11PresentationBackend

### Community 22 - "D3D11 Layer State"
Cohesion: 0.22
Nodes (4): BindFlags, D3D11CompositionLayerState, ID3D11Device, MediaCompositionLayerDirtyFlags

### Community 25 - "Media Surface Overlay"
Cohesion: 0.19
Nodes (5): ContentPresenter, DependencyObject, DependencyPropertyChangedEventArgs, OverlayWindow, FrameworkElement

### Community 26 - "DirectComposition Single Backend"
Cohesion: 0.20
Nodes (3): D3D11DirectCompositionSingleTargetBackend, IDXGISwapChain, D3D11SwapChainPresenter

### Community 27 - "Playback Coordinator"
Cohesion: 0.15
Nodes (4): IArchiveEntryInfo, Dispose, PlaybackCoordinator, void

### Community 28 - "CD-G Color Manager"
Cohesion: 0.23
Nodes (3): CdgColorManager, Color, ReadOnlySpan

### Community 29 - "Media Engine Interface"
Cohesion: 0.12
Nodes (3): ILoopPlaybackControl, IMediaEngine, CdgVisuals

### Community 30 - "Composition Layer Update Plan"
Cohesion: 0.18
Nodes (3): D3D11CompositionLayerUpdatePlan, D3D11CompositionTargetPlan, D3D11CompositionLayerUpdate

### Community 31 - "Audio Peak Analysis"
Cohesion: 0.26
Nodes (3): CancellationToken, LevelRetrievalFlags, Task

### Community 32 - "Bootstrap Library Loading"
Cohesion: 0.21
Nodes (4): bool, List, Bootstrap, Bootstrap

### Community 34 - "D3D11 Device Resources"
Cohesion: 0.25
Nodes (4): D3D11DeviceResources, ID3D11Multithread, MediaPlayerConfiguration, D3D11PresentationFramePresenter

### Community 35 - "Native HWND Surface Host"
Cohesion: 0.20
Nodes (6): NativeHwndSurfaceHost, HandleRef, height, HwndHost, SizeChangedInfo, width

### Community 37 - "Archive Service Adapter"
Cohesion: 0.18
Nodes (6): IArchiveHandle, IArchiveService, SharpArchiveAdapter, SharpArchiveEntryInfo, SharpArchiveHandle, Stream

### Community 39 - "DirectComposition Target"
Cohesion: 0.22
Nodes (6): D3D11DirectCompositionTarget, IDCompositionDevice, IDCompositionTarget, IDCompositionVisual, IDXGIDevice, IDXGISwapChain1

### Community 40 - "Audio Normalizer"
Cohesion: 0.23
Nodes (3): AudioNormalizer, ThreadLocal, VolumeParameters

### Community 41 - "Playback Scheduler"
Cohesion: 0.35
Nodes (3): CancellationTokenSource, Cts, PlaybackScheduler

### Community 42 - "Native HWND Swap Chain"
Cohesion: 0.21
Nodes (3): CdgConstants, int, D3D11NativeHwndSwapChainBackend

### Community 44 - "Service Provider & Libraries"
Cohesion: 0.26
Nodes (5): DLLPath, MediaPlayerServiceProvider, IReadOnlyList, IServiceCollection, IServiceProvider

### Community 45 - "Media Control Operation Queue"
Cohesion: 0.18
Nodes (6): BlockingCollection, FileTree, FileTreeBuilder, IDisposable, Thread, MediaControlOperationQueue

### Community 46 - "D3D11 Rendering Draw Calls"
Cohesion: 0.32
Nodes (4): ID3D11DeviceContext, ID3D11RenderTargetView, ID3D11ShaderResourceView, Vector4

### Community 47 - "CDG Composition Frame"
Cohesion: 0.18
Nodes (4): Cdg, Empty(), WithoutTransientPayload(), MediaCompositionClearMode

### Community 48 - "Binary Compatibility Analysis"
Cohesion: 0.25
Nodes (6): compat, CompatibilityInfo, CpuType, FileCpuType, BinaryCompatibility, match

### Community 49 - "Viewport Geometry Mapping"
Cohesion: 0.35
Nodes (3): ViewportGeometry, RectD, ViewportMapping

### Community 52 - "Swap Chain Factory"
Cohesion: 0.22
Nodes (6): AlphaMode, IDXGIFactory, D3D11SwapChainFactory, SwapChainDescription, SwapChainDescription1, SwapEffect

### Community 53 - "Application Startup & Exit"
Cohesion: 0.22
Nodes (6): Application, ExitEventArgs, App, StartupEventArgs, StreamWriter, TextWriterTraceListener

### Community 54 - "Composition Layer Schedule"
Cohesion: 0.27
Nodes (7): EnumerateLayers(), StableColorRevision(), TryGetBackgroundLayer(), TryGetOverlayLayer(), From(), D3D11CompositionLayerSchedule, MediaCompositionLayer

### Community 56 - "Precision Timer"
Cohesion: 0.36
Nodes (5): LibraryImport, SafeWaitHandle, PrecisionWaiter, TimerWaitHandle, WaitHandle

## Knowledge Gaps
- **18 isolated node(s):** `net10.0-windows7.0`, `Microsoft.NET.Sdk`, `net10.0-windows7.0`, `ManagedBass (4.0.2)`, `ManagedBass.Fx (4.0.2)` (+13 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **26 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VideoPipeline` connect `Video Pipeline Seeks` to `Media Session Attachments`, `Bootstrap Library Loading`, `Presentation Surface Management`, `D3D11 Rendering Bridge`, `Video Audio Engine`, `D3D11 Device Resources`, `FFmpeg Video Decoding`, `Presentation Diagnostics & Clock`, `Audio Pipeline Libraries`, `Native HWND Swap Chain`, `Playback Scheduler`, `CD-G Pipeline Playback`, `Media Control Operation Queue`, `Video Engine Presentation`, `Render Pipeline CDG Video`, `Video Engine Interface`, `Playback Coordinator`?**
  _High betweenness centrality (0.176) - this node is a cross-community bridge._
- **Why does `NativeMediaPresentationCoordinator` connect `Presentation Surface Management` to `Video Pipeline Seeks`, `D3D11 Rendering Bridge`, `Media Types & Composition`, `Audio Pipeline Libraries`, `D3D11 Surface Controller`, `CD-G State Machine`, `D3D11 Composition Presenter`, `Media Control Player`, `Media Engine Interface`, `Bootstrap Library Loading`, `D3D11 Device Resources`, `Media Control Event Handlers`, `Playback Scheduler`, `Native HWND Swap Chain`, `Media Control Operation Queue`, `CDG Composition Frame`, `Render Pipeline CDG Video`, `Video Engine Interface`, `CDG Engine Interface`, `CDG Render Snapshot`, `Render Coordinator Interface`?**
  _High betweenness centrality (0.129) - this node is a cross-community bridge._
- **Why does `D3D11MediaCompositionPresenter` connect `D3D11 Composition Presenter` to `Bootstrap Library Loading`, `Presentation Surface Management`, `Media Types & Composition`, `Presentation Diagnostics & Clock`, `Audio Pipeline Libraries`, `Native HWND Swap Chain`, `D3D11 Rendering Draw Calls`, `Viewport Geometry Mapping`, `Video Engine Presentation`, `Composition Layer Cache`, `CDG Render Snapshot`, `Render Pipeline CDG Video`, `Composition Layer Update Plan`?**
  _High betweenness centrality (0.103) - this node is a cross-community bridge._
- **What connects `net10.0-windows7.0`, `Microsoft.NET.Sdk`, `net10.0-windows7.0` to the rest of the system?**
  _18 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Video Pipeline Seeks` be split into smaller, more focused modules?**
  _Cohesion score 0.05339805825242718 - nodes in this community are weakly interconnected._
- **Should `FFmpeg Video Decoding` be split into smaller, more focused modules?**
  _Cohesion score 0.0547008547008547 - nodes in this community are weakly interconnected._
- **Should `Presentation Surface Management` be split into smaller, more focused modules?**
  _Cohesion score 0.07847082494969819 - nodes in this community are weakly interconnected._