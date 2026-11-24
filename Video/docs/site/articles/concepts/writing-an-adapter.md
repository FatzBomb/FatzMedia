# Writing a UI adapter

The WPF, Avalonia and WinForms packages are UI adapters. An adapter owns the control and the window: it creates a
surface, tells the player about it, and passes input and size changes on. It does not own media logic, rendering,
timing, CD+G or presentation policy; Core and the renderer do. This article lists the types an adapter author
uses. For the three existing adapters see their READMEs, which also list how they differ. Other public types that only exist for renderers and platform packages are named in [Extension and engine types](../reference/extension-and-engine-types.md).

## The shape of an adapter

1. Create or find a platform surface (a window handle, a bitmap sink or a GPU image consumer).
2. Describe it with a <xref:FatzMedia.Video.MediaPresentationSurfaceDescriptor> and give it to the player with
   `IPlayerSession.AttachPresentationSurface`. Call `ResizePresentationSurface` when it changes size and
   `DetachPresentationSurface` when the control goes away. These calls are not queued; make them on the UI thread.
3. Forward the player's state to the control (content visible or hidden, first frame ready, which presentation path
   is in use) using the shared Core helpers instead of writing the rules again.

## Describing a surface

<xref:FatzMedia.Video.MediaPresentationSurfaceDescriptor> carries the surface id (`PrimarySurfaceId` is
`"primary"`), its role (primary, or a second screen), the native surface, the size in physical pixels and the DPI
scale. The native surface is a <xref:FatzMedia.Video.MediaNativeSurfaceDescriptor>: a stable type id plus named,
borrowed handles.

| Type id (`MediaNativeSurfaceTypeIds`) | Handles (`MediaNativeHandleIds`) | Meaning |
| --- | --- | --- |
| `Win32Hwnd` | `Window` | A native Windows window the renderer presents into. |
| `ManagedBgraFrameSink` | `FrameSink` | The renderer reads frames back; the adapter draws a bitmap. |
| `SharedGpuImage` | `SharedImageConsumer` | The renderer exports GPU images that the adapter's compositor shows. |
| `X11Window`, `WaylandSurface`, `AppKitView`, `AndroidNativeWindow` | `Window`, `Display`, `Surface`, `View` | Reserved ids for platform packages that do not exist yet. |

The type id and each handle name are <xref:FatzMedia.Video.MediaNativeSurfaceTypeId> and <xref:FatzMedia.Video.MediaNativeHandleId>: small structs around a non-blank string (`Value`, trimmed; `IsValid`). `MediaNativeSurfaceTypeIds` and `MediaNativeHandleIds` hold the known ones, and a platform package may define ids of its own without a change in Core.

A <xref:FatzMedia.Video.MediaNativeHandle> is a named handle value that is **borrowed**: the player never releases or
destroys it, and it must stay valid from a successful attach until detach or replacement. A frame sink and a GPU
image consumer are managed objects, so they travel as opaque tokens (`MediaPresentationFrameSinkHandle` and
`MediaSharedGpuImageConsumerHandle`; dispose the token only after the surface is detached).

## Shared GPU images

The fastest path for a compositing UI (Avalonia uses it) is a set of GPU images that the renderer writes and the
adapter's compositor reads, with no CPU copy. The adapter implements `IMediaSharedGpuImageConsumer` and wraps it in a
`MediaSharedGpuImageConsumerHandle`, whose `CreateSurfaceDescriptor()` makes the `SharedGpuImage` descriptor.

- `MediaSharedGpuImageHandleKind` says how an image's memory is handed over: a DXGI shared handle (NT or legacy) of a
  Direct3D11 texture, or an opaque file descriptor or Win32 handle of Vulkan device memory. The consumer opens
  (duplicates) producer-owned handles and never closes them; importing a POSIX file descriptor transfers ownership
  to the importer.
- `MediaSharedGpuImageSyncKinds` says how producer and consumer take turns: a DXGI keyed mutex, two exported
  semaphores per image, or host synchronization (no GPU primitive: the producer finishes its GPU work first and
  the consumer's `ReleaseFrame` call ends its use). With semaphores, <xref:FatzMedia.Video.MediaSharedGpuSemaphoreHandleKind>
  says how they are shared: an opaque POSIX file descriptor (importing it transfers ownership) or an opaque Win32
  handle (producer-owned).
- `MediaSharedGpuImageRing` is the producer's bookkeeping, the same for every renderer: it hands out the least
  recently presented free image and tracks the keys and pending semaphore signals, so the compositor never samples a
  half-written frame.
- Alpha is always premultiplied (`MediaSharedGpuImageFormat`).

The pieces fit together like this:

| Type | Role |
| --- | --- |
| `IMediaSharedGpuImageConsumer` | What the adapter implements: a `Request`, then `OnImagesCreated`, `OnFrameReady` and `OnUnavailable(reason)`. |
| <xref:FatzMedia.Video.MediaSharedGpuImageRequest> | What the consumer can import: its image handle kinds in order of preference, the semaphore handle kinds, and its GPU's identity (LUID and UUID) so a renderer on another adapter declines instead of exporting images the consumer cannot open. |
| <xref:FatzMedia.Video.MediaSharedGpuImageCapability> | One entry of that list: an image handle kind with the synchronization kinds the consumer supports for it. |
| <xref:FatzMedia.Video.MediaSharedGpuImageSelection> | What the producer chose for one image set: the handle kind, the synchronization and, with semaphores, the semaphore handle kind. `MediaSharedGpuImageRequest.TrySelect` makes the choice, or says why there is none. |
| `MediaSharedGpuImageSet` | A double-buffered set of exported images, handed over in `OnImagesCreated`; a newer set replaces it. The consumer calls `CompleteImport` once, `ReleaseFrame` once per frame, and the producer `Retire`s the set. |
| <xref:FatzMedia.Video.MediaSharedGpuImageBuffer> | One exported image: its handle (for Vulkan also the allocation size and offset) and, with semaphores, the render-complete and consumer-release semaphore handles. |
| <xref:FatzMedia.Video.MediaSharedGpuImageFrame> | A finished frame announced in `OnFrameReady`: the set's generation, the image index, a frame number and, with a keyed mutex, the keys to acquire and release. |
| <xref:FatzMedia.Video.MediaSharedGpuImageWrite> | The producer's side of the ring: which image to write next, the key to acquire it with and whether to wait for a pending consumer-release signal first. |

Keep a frame-sink fallback. The Avalonia surface switches to it when the GPU path is off, unsupported or has
failed, and says why in `PresentationPathReason`.

## Platform services

<xref:FatzMedia.Video.IPlatformServices> supplies the operating-system pieces Core needs: it loads the native media
libraries before Core creates its services and registers platform-specific services such as video decoding and
timing. The host picks the platform package (`FatzMedia.Video.Windows`, `FatzMedia.Video.Linux`) and hands it to the
player; an adapter does not implement it.

## Shared rules, so adapters stay thin

- **Content visibility** (`AutoHide`, `IsContentVisible`, `ContentVisibilityChanged`, `IsPresentationSuspended`):
  Core's `MediaSurfaceContentTracker`.
- **Presentation path** (`PresentationPath`, `PresentationPathReason`, `PresentationPathChanged`):
  <xref:FatzMedia.Video.MediaSurfacePresentationPath> from Core.
- **First frame ready**: raise `FirstFrameReady` once per load, on the UI thread, when a frame of that load is on the
  surface.
- **Ready-made karaoke and background controls**: Core's `LightweightPlayerPolicy` and `LightweightPlayerController`;
  <xref:FatzMedia.Video.LightweightPlayerKind> says which kind (karaoke or background) a control is.

## Helpers a host uses with an adapter

- **File pickers.** `PlayerCore.OpenFileDialogFilter` is a ready filter string, built from
  <xref:FatzMedia.Video.AcceptedMediaTypes>, a snapshot of the extensions the current runtime accepts (audio, video,
  archives, images, CD+G). Its `Filters` are <xref:FatzMedia.Video.MediaFileFilter> groups (a name and extensions)
  for pickers that take a list instead of a string.
- **Network sources.** <xref:FatzMedia.Video.NetworkStreamProbe> is the result of connecting to a source (length,
  byte-range support, ICY metadata); the player uses it to choose what a network source can do
  (`MediaSourceCapabilities.ForNetwork`).
- **Startup.** `NativeDependencyReport` lists each native library with a <xref:FatzMedia.Video.NativeLibraryStatus>;
  show its `RequiredProblems` and `OptionalProblems` when the player starts without a library.

## Rules the boundary scripts enforce

Adapters must not reference renderer or platform projects, and Core must not reference an adapter. The reference graph
is pinned in `scripts/check-core-boundary.ps1`, and `check-adapter-boundary.ps1`, `check-wpf-boundary.ps1` and
`check-presentation-parity.ps1` guard the rest. See [Architecture](architecture.md).
