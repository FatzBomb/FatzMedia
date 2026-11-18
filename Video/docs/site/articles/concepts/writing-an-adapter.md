# Writing a UI adapter

The WPF, Avalonia and WinForms packages are UI adapters. An adapter owns the control and the window: it creates a
surface, tells the player about it, and passes input and size changes on. It does not own media logic, rendering,
timing, CD+G or presentation policy; Core and the renderer do. This article lists the types an adapter author
uses. For the three existing adapters see their READMEs, which also list how they differ.

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
  semaphores per image, or none (the producer finishes its GPU work first and the consumer calls `ReleaseFrame`).
- `MediaSharedGpuImageRing` is the producer's bookkeeping, the same for every renderer: it hands out the least
  recently presented free image and tracks the keys and pending semaphore signals, so the compositor never samples a
  half-written frame.
- Alpha is always premultiplied (`MediaSharedGpuImageFormat`).

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
