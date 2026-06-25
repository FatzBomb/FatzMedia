# Graph Report - .  (2026-06-24)

## Corpus Check
- cluster-only mode — file stats not available

## Summary
- 1118 nodes · 2423 edges · 50 communities (34 shown, 16 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS · INFERRED: 6 edges (avg confidence: 0.83)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 40|Community 40]]
- [[_COMMUNITY_Community 41|Community 41]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 43|Community 43]]
- [[_COMMUNITY_Community 44|Community 44]]
- [[_COMMUNITY_Community 45|Community 45]]
- [[_COMMUNITY_Community 46|Community 46]]
- [[_COMMUNITY_Community 48|Community 48]]

## God Nodes (most connected - your core abstractions)
1. `VideoPipeline` - 100 edges
2. `OptimizedGraphicsRenderer` - 69 edges
3. `VorticeD3D11Renderer` - 66 edges
4. `VideoDecoder` - 65 edges
5. `FatzBombMediaSurface` - 61 edges
6. `MediaRenderer` - 53 edges
7. `VideoAudioEngine` - 50 edges
8. `BackgroundVideoRenderer` - 38 edges
9. `AudioEngine` - 35 edges
10. `CdgPipeline` - 34 edges

## Surprising Connections (you probably didn't know these)
- `MediaPlayer` --references--> `BASS Native Library`  [EXTRACTED]
  FatzBombMedia.MediaControl/Api/MediaPlayer.cs → docs/05-Native-Libraries-And-NuGet.md
- `MediaPlayer` --references--> `FFmpeg Native Library`  [EXTRACTED]
  FatzBombMedia.MediaControl/Api/MediaPlayer.cs → docs/05-Native-Libraries-And-NuGet.md
- `MediaRenderer` --implements--> `D3DImage Presentation`  [EXTRACTED]
  FatzBombMedia.MediaControl/Controllers/MediaRenderer.cs → README.md
- `MediaRenderer` --implements--> `NativeHwndOverlay`  [EXTRACTED]
  FatzBombMedia.MediaControl/Controllers/MediaRenderer.cs → README.md
- `FatzBombMedia.Player` --calls--> `DLLPath`  [EXTRACTED]
  FatzBombMedia.Player/README.md → FatzBombMedia.MediaControl/Api/DLLPath.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Media Pipeline Orchestration** — mediacontrol_pipelinemediaengine, mediacontrol_cdgengine, mediacontrol_videoaudioengine [EXTRACTED 0.90]
- **Native Library Integration** — mediacontrol_dllpath, bass_native, ffmpeg_native [EXTRACTED 1.00]

## Communities (50 total, 16 thin omitted)

### Community 1 - "Community 1"
Cohesion: 0.08
Nodes (13): DependencyObject, DependencyPropertyChangedEventArgs, EventArgs, Grid, HandleRef, HwndHost, MediaPresentationMode, SizeChangedInfo (+5 more)

### Community 2 - "Community 2"
Cohesion: 0.08
Nodes (10): VideoFrameEventArgs, BitmapSource, GpuRenderingMethod, OptimizedGraphicsRenderer, height, Lock, ReadOnlySpan, RenderingMethod (+2 more)

### Community 3 - "Community 3"
Cohesion: 0.07
Nodes (24): AlphaMode, VorticeD3D11Renderer, D3DImage, Func, ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, ID3D11Texture2D (+16 more)

### Community 4 - "Community 4"
Cohesion: 0.05
Nodes (13): IArchiveEntryInfo, ICdgEngine, SharpArchiveEntryInfo, Cdg, CdgEngine, CdgPacketProcessor, CdgEngineState, CdgPacket (+5 more)

### Community 5 - "Community 5"
Cohesion: 0.06
Nodes (18): BitmapScalingMode, CornerRadius, DateTime, DispatcherTimer, MainWindow, KeyEventArgs, MouseButtonEventArgs, MouseEventArgs (+10 more)

### Community 6 - "Community 6"
Cohesion: 0.05
Nodes (14): IMediaEngine, bool, byte, CdgColorManager, CdgConstants, Color, CdgVisuals, MediaControls (+6 more)

### Community 7 - "Community 7"
Cohesion: 0.06
Nodes (9): IVideoEngine, BitmapImage, MediaRenderer, ImageSource, long, D3DImage Presentation, NativeHwndOverlay, RenderPipeline (+1 more)

### Community 8 - "Community 8"
Cohesion: 0.05
Nodes (6): IAudioEngine, AudioEngineState, MediaPlayerState, AudioPipeline, ClockAudioEngine, MediaSession

### Community 9 - "Community 9"
Cohesion: 0.09
Nodes (13): ArrayPool, AVBufferRef, AVCodec, AVCodecContext, AVCodecContext_get_format, AVCodecID, AVHWDeviceType, AVPixelFormat (+5 more)

### Community 10 - "Community 10"
Cohesion: 0.06
Nodes (14): IArchiveHandle, IArchiveService, SharpArchiveAdapter, SharpArchiveHandle, PlayerNativeLibraries, FileTree, FileTreeBuilder, ArchiveAnalysis (+6 more)

### Community 11 - "Community 11"
Cohesion: 0.11
Nodes (14): D3D9InteropSurface, DEVMODE, DllImport, DWM_TIMING_INFO, IntPtr, LibraryImport, MarshalAs, MONITORINFOEX (+6 more)

### Community 12 - "Community 12"
Cohesion: 0.08
Nodes (5): BackgroundVideoRenderer, double, PlaybackClock, Stopwatch, VideoPresentationTiming

### Community 13 - "Community 13"
Cohesion: 0.09
Nodes (6): VideoAudioEngine, AVFormatContext, AVFrame, AVPacket, AVStream, SwrContext

### Community 14 - "Community 14"
Cohesion: 0.16
Nodes (3): AutoResetEvent, CdgPipeline, Thread

### Community 15 - "Community 15"
Cohesion: 0.11
Nodes (8): Brush, DependencyProperty, Image, object, Panel, Window, ImageSurfaceAttachment, RenderSurface

### Community 16 - "Community 16"
Cohesion: 0.16
Nodes (5): MediaBackgroundCandidate, MediaBackgroundResolver, MediaBackgroundKind, MediaContent, MediaState

### Community 17 - "Community 17"
Cohesion: 0.17
Nodes (3): AudioStreamManager, BassFlags, MethodImpl

### Community 18 - "Community 18"
Cohesion: 0.17
Nodes (4): AudioEngine, ConcurrentDictionary, PlaybackState, SyncProcedure

### Community 19 - "Community 19"
Cohesion: 0.12
Nodes (5): MediaPlayer, BASS Native Library, FFmpeg Native Library, IServiceProvider, MediaPlayerConfiguration

### Community 20 - "Community 20"
Cohesion: 0.17
Nodes (4): AudioNormalizer, LevelRetrievalFlags, ThreadLocal, VolumeParameters

### Community 21 - "Community 21"
Cohesion: 0.12
Nodes (15): Docs, net10.0-windows7.0, Microsoft.NET.Sdk, net10.0-windows7.0, Microsoft.NET.Sdk, CommunityToolkit.Mvvm (8.4.2), FFmpeg.AutoGen (8.1.0), ManagedBass (4.0.2) (+7 more)

### Community 22 - "Community 22"
Cohesion: 0.24
Nodes (6): CompatibilityLevel, Dictionary, EnumerationOptions, DirectoryScanner, FatzBombMedia.MediaControl.Core.Files, IEnumerable

### Community 24 - "Community 24"
Cohesion: 0.19
Nodes (3): MediaFile, MediaMetadata, TimeSpan

### Community 25 - "Community 25"
Cohesion: 0.32
Nodes (4): Action, CancellationTokenSource, Cts, PlaybackScheduler

### Community 28 - "Community 28"
Cohesion: 0.25
Nodes (6): compat, CompatibilityInfo, CpuType, FileCpuType, BinaryCompatibility, match

### Community 29 - "Community 29"
Cohesion: 0.24
Nodes (4): HardwareAccelStatus, List, MediaMetadata, PerformanceAssessment

### Community 31 - "Community 31"
Cohesion: 0.46
Nodes (3): MediaPlayerServiceProvider, IReadOnlyList, IServiceCollection

### Community 32 - "Community 32"
Cohesion: 0.50
Nodes (3): AcceptedMediaTypes, FromCurrentRuntime(), Sorted()

### Community 33 - "Community 33"
Cohesion: 0.40
Nodes (3): DLLPath, FatzBombMedia.MediaControl, FatzBombMedia.Player

## Knowledge Gaps
- **30 isolated node(s):** `FatzBombMedia.MediaControl.Core.Cdg`, `FatzBombMedia.MediaControl.Core.Files`, `FatzBombMedia.MediaControl.Core.Video`, `net10.0-windows7.0`, `ManagedBass (4.0.2)` (+25 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **16 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `VideoPipeline` connect `Community 0` to `Community 2`, `Community 6`, `Community 7`, `Community 8`, `Community 9`, `Community 10`, `Community 11`, `Community 12`, `Community 13`, `Community 14`, `Community 16`, `Community 19`, `Community 24`, `Community 25`, `Community 26`?**
  _High betweenness centrality (0.156) - this node is a cross-community bridge._
- **Why does `VideoAudioEngine` connect `Community 13` to `Community 0`, `Community 2`, `Community 6`, `Community 7`, `Community 8`, `Community 9`, `Community 10`, `Community 12`, `Community 23`?**
  _High betweenness centrality (0.111) - this node is a cross-community bridge._
- **Why does `OptimizedGraphicsRenderer` connect `Community 2` to `Community 3`, `Community 6`, `Community 7`, `Community 10`, `Community 11`, `Community 12`, `Community 19`?**
  _High betweenness centrality (0.104) - this node is a cross-community bridge._
- **What connects `FatzBombMedia.MediaControl.Core.Cdg`, `FatzBombMedia.MediaControl.Core.Files`, `FatzBombMedia.MediaControl.Core.Video` to the rest of the system?**
  _30 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.0669753086419753 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.07572298325722983 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.08085317460317461 - nodes in this community are weakly interconnected._