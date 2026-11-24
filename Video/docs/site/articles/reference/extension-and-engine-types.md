# Extension and engine types

Most of the public API of FatzMedia.Video is for hosts: the player, its events, the configuration. Other public types exist because a renderer, a platform package or a UI adapter lives in a different assembly from Core and has to share them with it. They also turn up in diagnostics, in stack traces and in the API reference. This article names each of them in one line: what it is, and where the engine uses it. A host application rarely creates any of these. You need this page when you write a renderer, a platform package or an adapter, or when you read the engine's logs.

The groups follow the layers of the engine (see [Architecture](../concepts/architecture.md)). A type that belongs to a topic with its own article links to it.

## Composition and layer planning

A renderer draws one `MediaCompositionFrame` for each presented frame: a few ordered layers (background, wash, overlay, subtitle) on one or more presentation targets. The types below describe layers and targets, and let a renderer work out what changed since the last frame so it uploads and draws only that. The Direct3D11 renderer uses the layer and target planning types. The Software and Vulkan renderers use only `CompositionPresentOutcome` and `ColorMath`. A host never builds any of these.

### Describing a layer

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionLayerId> | enum | The identity of a layer slot: `Background`, `Wash`, `Overlay` or `Subtitle`. | Every `MediaCompositionLayer` carries one; the Direct3D11 renderer keeps retained state per id. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionLayerKind> | enum | The logical role of a layer: `Background`, `Wash` or `Overlay`. | `MediaCompositionLayer.Kind`, read by the Direct3D11 presenter. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionLayerSourceKind> | enum | What supplies a layer's pixels: nothing, a clear, BGRA pixels, a native (hardware-decoded) texture, a retained native texture, a CD+G snapshot or a solid colour. | Layer descriptors; it also picks the update schedule (see below). |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionStretchMode> | enum | How a base layer maps into the viewport: `Auto`, `Original`, `UniformFit`, `Fill` or `UniformToFill`. | The player's `VideoDisplayMode` maps onto it (`Zoom` is `UniformFit`, `Stretch` is `Fill`, `ZoomToFill` is `UniformToFill`); see [Display and color](../video/display-and-color.md). |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionClearMode> | enum | What a frame is cleared to where no layer supplies pixels: `Black`, `Transparent`, `ConfiguredColor` or `PreservePrevious`. | `MediaCompositionFrame.ClearMode`. The player uses `Transparent` for a transparent surface background and `Black` otherwise. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionLayerChanges> | enum (flags) | Why a retained layer needs attention: activated, size, content, opacity, upload required and so on. | The Direct3D11 layer state sets the flags when it compares a layer with the last one. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.MediaCompositionTargetFilter> | class | The presentation targets one composition is for: whether the primary surface is included and which secondary surfaces are. A composition without a filter goes to every target. | Display-locked video on several monitors; see [Frame timing](../video/frame-timing.md). |

### Planning what to update

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerUpdatePlan> | class | The plan for one captured frame: a <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerUpdate> for each layer, whether the frame only clears, and counts of layers that need work or can reuse their retained output. | Built by the Direct3D11 layer cache for each frame. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerUpdate> | record struct | One layer's entry in the plan: its id, source kind, revision, dirty flags, the action to take, its schedule and whether retained resources exist. | Entries of `CompositionLayerUpdatePlan.Updates`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerUpdateAction> | enum (flags) | What to do for a layer: `Upload`, `Render`, `AllocateResources`, `ReleaseResources`. | `CompositionLayerUpdate.Action`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerSchedule> | record struct | How often a layer's content changes and where it is rendered. `CompositionLayerSchedule.From` derives it from the layer's source kind. | `CompositionLayerUpdate.Schedule`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerUpdateCadence> | enum | How often a layer's content changes: `None`, `OnDemand`, `SourceRate` (follows the video's frame rate) or `Static`. | `CompositionLayerSchedule.Cadence`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionLayerRenderTargetKind> | enum | Where a layer is rendered: `None`, `ImmediateFinalTarget` (straight into the output) or `RetainedOffscreenTexture` (a texture kept between frames). | `CompositionLayerSchedule.TargetKind`. |

### Tracking presentation targets

A target is a thing a composition is presented to: the main window's swap chain, a second screen or a DirectComposition overlay. The registry tells the renderer which targets appeared, resized, switched backend or went away since the last frame.

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetRegistry> | class | Keeps the state of every target. The renderer calls `Capture` with the targets that exist now, `CreatePlan` to get the changes and then `MarkPlanConsumed`. | One per Direct3D11 composition presenter. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetState> | class | The registry's record of one target: its last descriptor, dirty flags and whether it is active. | Inside the registry. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetDescriptor> | record struct | A target as the renderer sees it now: id, kind, size, backend name and revision. The id of the main window's final target is `PrimaryFinalTargetId` (`"primary-final"`). | Passed to `CompositionTargetRegistry.Capture`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetKind> | enum | `FinalPresentation` (a surface the final frame is presented to) or `DirectCompositionOverlay`. | `CompositionTargetDescriptor.Kind`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetChanges> | enum (flags) | What changed about a target: activated, deactivated, size, backend or revision. | `CompositionTargetState.DirtyFlags`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetPlan> | class | The plan for one frame: a <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetUpdate> for each target, with counts (active, resized, to present) and `ShouldPresentTarget`. | Returned by `CompositionTargetRegistry.CreatePlan`. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetUpdate> | record struct | One target's entry in the plan: its description, dirty flags and the action to take. | Entries of the plan. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.Planning.CompositionTargetUpdateAction> | enum (flags) | What to do for a target: `Activate`, `Resize`, `BackendChange`, `Present` or `Deactivate`. | `CompositionTargetUpdate.Action`. |

### Presenting and timing

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.Composition.CompositionPresentOutcome> | struct | Collects the results of presenting one composition to several targets and decides if the frame counts as presented. With a primary target the primary's result decides, so a secondary that presented never hides a failed primary frame; with no primary, any presented target is a success. It is mutable, so keep it in a local. | The Direct3D11, Software and Vulkan renderers. |
| <xref:FatzMedia.Video.Core.Graphics.Composition.PresentationVsyncTiming> | record struct | The vertical-blank timing of the display that shows a surface: a recent vertical-blank timestamp, the refresh period in `Stopwatch` ticks and a display key, so surfaces on one monitor share one timing. | Renderers report it for `DisplayLocked` timing; see [Frame timing](../video/frame-timing.md). |
| <xref:FatzMedia.Video.Core.Graphics.Composition.ColorMath> | static class | 8-bit colour helpers shared by renderers so their output is bit-identical; today `PremultiplyBgra`. | The Software and Vulkan renderers. |
| <xref:FatzMedia.Video.Core.Graphics.Rendering.TargetResourcePruning> | static class | Disposes the per-target resources (for example post-process textures) of targets that no longer exist. `RetainOnly` does not allocate. | The Direct3D11 presenter, after a secondary window is removed. |
| <xref:FatzMedia.Video.Core.Graphics.Rendering.RetainedPixelBuffer> | static class | The sizing rule for a CPU pixel buffer a renderer keeps across target re-creation: it grows to the largest size needed, is reused for smaller ones and is replaced when the need drops below a quarter of it (`ShrinkFactor`). | The Direct3D11 frame-sink backend, the bitmap read-back that adapters drawing a bitmap use. |
| <xref:FatzMedia.Video.Core.Graphics.Rendering.RendererDiagnosticLatch> | class | Reports a renderer failure once per episode: the first `Raise` sends a diagnostic, then it stays quiet until `Reset` (normally on the next success). Thread-safe, one per failure site. | The presentation coordinator and the Direct3D11 renderer, so a failure that repeats every frame does not flood the log. |

## Video colour, geometry and HDR

### Colour and geometry records

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.VideoColorSpace> | record struct | How a decoded YCbCr frame's values map to RGB: a matrix and a range. `Resolve(width, height)` fills in what the stream leaves unspecified: BT.709 for frames at least 1280 wide or taller than 576, otherwise BT.601, and limited range. | `NativeVideoFrameResource.ColorSpace`, so renderers convert hardware frames as the stream says instead of leaving it to a driver. |
| <xref:FatzMedia.Video.Core.Graphics.VideoColorMatrix> | enum | The YCbCr matrix: `Unspecified`, `Bt601`, `Bt709` or `Bt2020`. | `VideoColorSpace.Matrix`. |
| <xref:FatzMedia.Video.Core.Graphics.VideoColorRange> | enum | The YCbCr range: `Unspecified` (treated as limited), `Limited` (16 to 235) or `Full` (0 to 255). | `VideoColorSpace.Range`. |
| <xref:FatzMedia.Video.Core.Graphics.Geometry.RectD> | record struct | A renderer-neutral rectangle (`X`, `Y`, `Width`, `Height`) with no DirectX or WPF types. | Viewport and aspect calculations for the display modes. |
| <xref:FatzMedia.Video.Core.Graphics.Geometry.ViewportMapping> | record struct | How a source rectangle maps into a viewport once clipped: the destination is clipped to the viewport and the source cropped to match, so video-processor rectangles stay valid for `Original` and `ZoomToFill`. | Core's `ViewportGeometry` returns it; the Direct3D11 renderer uses the results. |
| <xref:FatzMedia.Video.Core.Video.DisplayRefreshInterval> | static class | Turns a refresh-rate ratio into a presentation interval in seconds. A rate outside 23 to 600 Hz, or a zero numerator or denominator, gives the 60 Hz fallback (`FallbackSeconds`). | The Windows platform's display timing; see [Frame timing](../video/frame-timing.md#how-the-refresh-rate-is-found). |

The colour records are also described in [Display and color](../video/display-and-color.md#how-a-frame-is-converted-to-rgb).

### HDR output

The HDR types are the contract the Direct3D11 renderer uses to decide between tone mapping to SDR and showing HDR on an HDR monitor. How to turn HDR output on and when it applies is in [Display and color](../video/display-and-color.md#hdr-output).

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.HdrTransferFunction> | enum | The transfer function of HDR video: `Pq` (HDR10, SMPTE ST 2084) or `Hlg` (hybrid log-gamma). | `HdrToneMapRequest.Transfer`, and the content argument of `HdrOutputPolicy.Choose`. |
| <xref:FatzMedia.Video.Core.Video.Hdr.HdrDisplayInfo> | record struct | What a monitor reports: whether Windows HDR is on, its peak luminance and the system's SDR white level, in nits (0 means not reported). | Input of `HdrOutputPolicy.Choose`. |
| <xref:FatzMedia.Video.Core.Video.Hdr.HdrOutputMode> | enum | How a target presents: `Sdr` (8-bit, HDR tone mapped) or `ScRgb` (FP16 scRGB, HDR shown as HDR). | `HdrOutputDecision.Mode`. |
| <xref:FatzMedia.Video.Core.Video.Hdr.HdrOutputDecision> | record struct | A target's decision: the mode, the display peak HDR video is mapped to, the SDR white level for SDR layers and the reason as text. | The result of `HdrOutputPolicy.Choose`; the reason is what the renderer logs in the `D3D11.HdrOutput` category. |
| <xref:FatzMedia.Video.Core.Video.Hdr.Hdr10MetadataValues> | record struct | HDR10 static metadata in DXGI's encoding (primaries, mastering luminance, content light levels). | Handed to the swap chain when it switches to HDR, built from the peak the video is mapped to. |

### Native video frames and decoder fallback

These describe a hardware-decoded frame handed from the decoder to a renderer. They are mostly of interest to platform and renderer authors; [Hardware decoding](../video/hardware-decoding.md) explains the behaviour a host sees.

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Graphics.MediaGraphicsBackendId> | record struct | A stable string id for a native graphics or decode backend; `Direct3D11` (`"d3d11"`) and `Vulkan` (`"vulkan"`) are predefined. | `NativeVideoDeviceResource.BackendId`, so a renderer takes only frames from its own backend. |
| <xref:FatzMedia.Video.Core.Graphics.NativeVideoResourceLifetime> | enum | Who owns a native frame. The only value is `ProducerOwned`: Core keeps the decoded frame, and a consumer uses the descriptor during the call and never releases or keeps the handles. | `NativeVideoFrameResource.Lifetime`. |
| <xref:FatzMedia.Video.Core.Graphics.VideoDecodeFallback> | class | A thread-safe token, one per decoder, with which the renderer hands a hardware decoder back to software when it cannot take the frames for a reason other than a lost device. `RequestSoftwareDecoding(reason)` makes the producer reopen the video in software at the current position. | Set on each hardware frame (`NativeVideoFrameResource.DecodeFallback`); the presentation coordinator calls it. This is the "renderer cannot use the decoder's frames" case in [Hardware decoding](../video/hardware-decoding.md#the-automatic-fallback-to-software). |
| <xref:FatzMedia.Video.Core.Abstractions.SoftwareOnlyVideoHardwareAccelerationServices> | class | A hardware-decode policy that offers no device types, so every frame is a CPU frame. `Instance` is the shared object. | The video decoder's default, and the Linux platform. |
| <xref:FatzMedia.Video.Core.Abstractions.VideoFrameEventArgs> | class | One decoded or supplied frame (pixels with width, height, stride and presentation time, or a native resource) delivered to a synchronous consumer. It and its payload are valid only during the callback; copy what you keep unless `PixelDataCanBeRetained` is true. | `IVideoEngine.FrameReady`, and the renderer methods that receive a frame. |

## Files, scanning and archives

### File types and scanning

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Files.FileType> | enum | What a path turned out to be: `Audio`, `AudioCdg`, `Video`, `VideoSubtitle`, `Image`, `Cdg`, `Archive`, an item inside an archive (`AudioArchive`, `VideoArchive`, `CdgArchive`, `AudioCdgArchive`), `UnsupportedArchive`, `Unsupported` or `FileNotFound`. | `FileTree.Type`; the engine's open path chooses how to play from it. The extension sets are in [Supported formats](../playback/supported-formats.md#extension-checks). |
| <xref:FatzMedia.Video.Core.Files.FileTree> | class | What the engine found for one source: the file type, the source path and the paths of the audio, CD+G, video, subtitle and image parts. An archive's handle stays open until the tree is disposed. | The result of `FileTreeBuilder.GetFileTree`; see [Loading media](../playback/loading-media.md#files). |
| <xref:FatzMedia.Video.Core.Files.FileTreeBuilder> | static class | `GetFileTree(path, archiveService, searchArchive, searchCompanionFile)` classifies a path: a loose file with its companion `.cdg` or subtitle, or an archive with its first suitable entries. | The engine's `Open`. |
| <xref:FatzMedia.Video.Core.Files.DirectoryScanner> | static class | Breadth-first search of folders for named files: `LocateFilesDirectory` finds the first folder that has all of them, and `LocateFiles` returns where each was found. Folders it cannot read are skipped and traced. | The Windows startup code that finds the native libraries; see [Native dependencies](../getting-started/native-dependencies.md). |
| <xref:FatzMedia.Video.Core.Files.CompatibilityLevel> | enum | `All` (no check) or `Compatible` (only files that can load in this process). | The `compatibility` argument of the `DirectoryScanner` methods. |
| <xref:FatzMedia.Video.Core.Files.BinaryCompatibility> | static class | `Analyze(path)` reads a file's header and says whether it fits the running process. | `DirectoryScanner` with `CompatibilityLevel.Compatible`, so a 32-bit DLL is not picked up by a 64-bit process. |
| <xref:FatzMedia.Video.Core.Files.CompatibilityInfo> | struct | The result of `Analyze`: the process CPU, the file's CPU, `IsMatch` (exact architecture) and `IsCompatible` (safe to load). Off Windows, existing files are reported as compatible. | `BinaryCompatibility.Analyze`. |
| <xref:FatzMedia.Video.Core.Files.CpuType> | enum | The CPU of the running process: `Unknown`, `X86`, `X64` or `Arm64`. | `CompatibilityInfo.CurrentProcess`. |
| <xref:FatzMedia.Video.Core.Files.FileCpuType> | enum | What a file is built for: not a Windows executable, native x86, x64 or Arm64, or managed (any CPU, x86 required, x64 or Arm64). | `CompatibilityInfo.FileCpu`. |

### Archive services

The engine reads archives through a small contract, so Core has no archive library of its own.

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Abstractions.IArchiveService> | interface | `Open(path)` opens an archive. | Registered in the player's services; `FileTreeBuilder` uses it. |
| <xref:FatzMedia.Video.Core.Abstractions.IArchiveHandle> | interface | An open archive: its `Entries`. Dispose it when done. | Held by a `FileTree` that came from an archive. |
| <xref:FatzMedia.Video.Core.Abstractions.IArchiveEntryInfo> | interface | One entry: its `Key` (the path in the archive), `Size` (or null) and `WriteTo(stream)`. | Playing an item from an archive; `MediaBackgroundCandidate.FromArchiveEntry`. |
| <xref:FatzMedia.Video.Adapters.Archives.SharpArchiveAdapter> | class | The shipped `IArchiveService`, built on SharpCompress. It lists files only, not folders. | The default registration in the player's service provider. |

### Backgrounds

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Controllers.MediaBackgroundCandidate> | class | A background chosen for overlay media (CD+G or lyrics): a kind, a display name and where it comes from: a path, a `MediaInputSource`, bytes from an archive entry, or an archive path with an entry key. Made with `FromPath`, `FromSource`, `FromArchiveEntry` or `FromArchiveEntryReference`. | `IMediaEngine.Background` and `SetBackground`; the sidecar and archive search produces one. See [Backgrounds](../karaoke/backgrounds.md). |
| <xref:FatzMedia.Video.Controllers.MediaBackgroundKind> | enum | `None`, `Image` or `Video`. | `MediaBackgroundCandidate.Kind`. |

## Engine contracts and states

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Abstractions.IMediaEngine> | interface | The contract between the player and the engine behind it: opening a source, play, pause, stop, seek, access to the audio, video, CD+G and subtitle engines, the background and the offsets. Core's implementation is internal. | `PlayerCore` gets it from the service provider; `IPlatformFactory` passes it to the renderer's presentation coordinator; the tests use a fake. |
| <xref:FatzMedia.Video.Core.Abstractions.ISubtitleEngine> | interface | A timed subtitle cue source: `Load` a file or `LoadCues`, `GetActiveCues(position)` and `CuesChanged`. | The engine's subtitle path; hosts read tracks through `SubtitleTracks` instead. See [Subtitles](../subtitles/subtitles.md). |
| <xref:FatzMedia.Video.Core.Abstractions.IAudioDeviceMonitor> | interface | Watches the audio output devices: `Start`, `Stop` and `DeviceChanged`. | Created for the player modes that use audio output; it feeds the `AudioDeviceChanged` event. See [Audio output devices](../audio/output-devices.md). |
| <xref:FatzMedia.Video.Core.Abstractions.AudioDeviceChangeInfo> | record | One device change: the kind, device id and name, a message and an error code. | The payload of `IAudioDeviceMonitor.DeviceChanged`; `AudioDeviceChangedEventArgs` exposes it as `Info` and through `Kind`, `DeviceName` and its other members. |
| <xref:FatzMedia.Video.Core.Abstractions.AudioEngineState> | enum | The audio engine's state: `Null`, `Playing`, `Paused`, `Stopped` or `Stalled`. | `IAudioEngine.State` and `StateChanged`. The player maps it to its own `MediaPlayerState`; `Stalled` becomes `Buffering`. |
| <xref:FatzMedia.Video.Core.Video.VideoEngineState> | enum | The video engine's state: `Null`, `Ready`, `Playing`, `Paused`, `Stopped`, `Closed` or `Error`. `Error` means the engine cannot present more frames until the source is closed and opened again. | `IVideoEngine.StateChanged`. |
| <xref:FatzMedia.Video.Core.Cdg.CdgEngineState> | enum | The CD+G engine's state: `Null`, `Ready`, `Playing`, `Paused`, `Stopped` or `Closed`. | `ICdgEngine.StateChanged`. |
| <xref:FatzMedia.Video.Controllers.IMediaRenderCoordinator> | interface | The small contract the playback and file controllers use to tell the renderer side to `StartRendering`, `StopRendering` and `RefreshAttachments` after a load or close. | `IMediaPresentationCoordinator` extends it, and Core's presentation coordinator implements it. |

## CD+G

See [CD+G](../karaoke/cdg.md) for how a host plays a pair. These are the engine's building blocks.

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Cdg.CdgConstants> | static class | The CD+G specification numbers: the 300 by 216 pixel frame, 16 colours, 300 packets per second, the 6 by 12 pixel tile and the borders. | The CD+G engine, the presentation coordinator and `CdgRenderSnapshot`. |
| <xref:FatzMedia.Video.Core.Cdg.CdgDirtyRect> | record struct | A changed rectangle in CD+G source pixels (`Empty` and `FullFrame` are predefined; `ClampToFrame` limits it to the frame). | `CdgRenderSnapshot.DirtyRects`, so a renderer updates only the changed tiles and scroll regions. |
| <xref:FatzMedia.Video.Core.Cdg.ICdgSnapshotSource> | interface | An optional CD+G engine feature: `TryCreateCdgSnapshot` makes the indexed-colour snapshot that renderers draw the CD+G layer from. | Implemented by the FatzAudio CD+G engine; the presentation coordinator asks for a snapshot while a CD+G item is loaded. |

## Karaoke and subtitle text

A subtitle cue (`SubtitleCue`) can carry karaoke timing (`SubtitleCue.KaraokeTiming`, a `KaraokeCueTiming`). When exactly one cue is active and it has karaoke timing, the presentation coordinator asks the rasterizer for a word-by-word highlight (`ISubtitleRasterizer.RenderKaraoke`); otherwise it draws plain text. No shipped loader fills in the timing today: the lyrics of MIDI and KAR files are drawn by FatzAudio itself, and SRT and ASS cues are plain text. The records are the contract between a cue source and the rasterizers, and the rasterizers' tests use them. How lyrics and subtitles look to a host is in [Subtitles](../subtitles/subtitles.md) and [MIDI and KAR](../karaoke/midi-and-kar.md).

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.Core.Subtitle.KaraokeLineTiming> | record | One lyric line: its text, a list of <xref:FatzMedia.Video.Core.Subtitle.KaraokeTextSegment> and whether it has a countdown. Methods answer, for a position, how many characters are highlighted, how far the current syllable's wipe has got and what the countdown shows. | The `Lines` of a `KaraokeCueTiming`; read by the rasterizers. |
| <xref:FatzMedia.Video.Core.Subtitle.KaraokeTextSegment> | record struct | A syllable: the start and length of its characters in the line text, and the time it starts to highlight. | `KaraokeLineTiming.Segments`. |
| <xref:FatzMedia.Video.Core.Subtitle.KaraokeCountdownState> | record struct | The countdown before a line's first syllable: whether it is visible, how many arrows are done, the progress of the active one and the opacity. `None` is the hidden state. | `KaraokeLineTiming.GetCountdownState`, drawn by the rasterizers. It runs for the last five seconds before the line's first lyric. |
| <xref:FatzMedia.Video.Core.Subtitle.SubtitleRasterResult> | record struct | A rasterized subtitle or lyric frame: premultiplied BGRA pixels with width, height and stride, and where the bitmap sits on the canvas. An empty result means there is nothing to draw. | Returned by `ISubtitleRasterizer.Render` and `RenderKaraoke`. The picture the FatzAudio MIDI-lyric and scaled CD+G overlays draw has the same shape. |
| <xref:FatzMedia.Video.Core.Subtitle.NullSubtitleRasterizer> | class | A rasterizer that draws nothing; `Instance` is shared. | For a renderer or a test without text rendering. The Software and Vulkan renderers use the SkiaSharp rasterizer from `FatzMedia.Video.Text` instead. |

## Platform services

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.MediaPlayerServiceProvider> | static class | In `FatzMedia.Video.Windows`: `Create` builds the player's service provider with the Windows platform services, for a configuration and the native library folders. | `WindowsPlatformFactory`. A host does not call it. |
| <xref:FatzMedia.Video.Windows.Threading.WindowsPrecisionWaiterFactory> | class | Creates the precision waiters the presentation loops sleep with. Each uses a Windows high-resolution waitable timer, so a wait for 41.7 ms (23.976 fps) is not rounded up to the system's 15.6 ms timer step; it falls back to an ordinary wait when the timer cannot be created. | Registered by the Windows platform services; see [Frame timing](../video/frame-timing.md#waiting-between-frames). |

## Web media

The web-media types name an online item, say what a web player can do and report its state. They hold no web view and no network code; the adapter's web control plays the item. Using web media is described in [Web media (YouTube)](../playback/web-media.md).

| Type | Kind | What it is | Where it is used |
| --- | --- | --- | --- |
| <xref:FatzMedia.Video.WebMediaReference> | record | A recognised online item: the provider id (`WebMediaProviders.YouTubeId`), the provider's own item id and the original address. | `IWebMediaPlayer.Current` and `LoadAsync`; a provider makes it when it recognises an address. |
| <xref:FatzMedia.Video.WebMediaParseResult> | record | What looking at an address found: an outcome, the reference when recognised and a reason when not supported. | The result of `IWebMediaProvider.Parse`. |
| <xref:FatzMedia.Video.WebMediaParseOutcome> | enum | `NotWebMedia` (play it as an ordinary source), `Recognized` or `Unsupported` (a service address that names no single item, such as a playlist, channel or search). | `WebMediaParseResult.Outcome`. |
| <xref:FatzMedia.Video.WebMediaState> | enum | The playback state a web service reports: `Unstarted`, `Ended`, `Playing`, `Paused`, `Buffering` or `Cued`. | `IWebMediaPlayer.State`. |
| <xref:FatzMedia.Video.WebMediaStateChangedEventArgs> | class | The new state and the position when it changed. | `IWebMediaPlayer.StateChanged`. |
| <xref:FatzMedia.Video.WebMediaErrorEventArgs> | class | A failure: a `WebMediaErrorKind`, a plain message and the service's own code when it gave one. | `IWebMediaPlayer.Error`. |
| <xref:FatzMedia.Video.WebMediaErrors> | static class | `FromYouTubeCode` turns a YouTube player error code into a kind and a message (for example 101 and 150: the owner does not allow embedding; 153: YouTube could not verify where the page came from). | The web control, when the page reports an error. |
| <xref:FatzMedia.Video.WebMediaFeature> | enum | The things a web item cannot do because the service owns picture and sound, such as audio routing, key change, tempo, mixing, the CD+G overlay, extraction and background playback. `Transport`, `Volume` and `Rate` are supported. | `WebMediaSupport.IsSupported`, `Require` (throws `NotSupportedException`) and `Describe` (the text for a host to show). |
| `LoopbackPageServer` | class | In `FatzMedia.Video.WebView2.Avalonia`: serves the YouTube player page from `127.0.0.1` on a free port, so the web view has a real origin and sends a Referer (without it YouTube answers error 153). It answers GET for the one page and 404 for everything else. | The Avalonia web player. An application does not use it. |

The shared GPU image and native handle types are in [Writing a UI adapter](../concepts/writing-an-adapter.md).
