# Renderers

A renderer is the component that draws each composed frame (video, images, CD+G graphics, subtitles) and hands it to a surface. The library ships three: Direct3D11, Vulkan and Software. You pick one when you build the platform factory, and the player keeps it for its whole life.

## The three renderers

| | Direct3D11 | Vulkan | Software |
| --- | --- | --- | --- |
| Package | `FatzBomb.MediaControl.Rendering.Direct3D11` | `FatzBomb.MediaControl.Rendering.Vulkan` | `FatzBomb.MediaControl.Rendering.Software` |
| Platforms | Windows x64 (`net10.0-windows7.0`) | Linux and Windows (`net10.0`), needs a Vulkan loader and driver | Any (`net10.0`) |
| Status | Full | Preview | Full, CPU only |
| Draws into | Native window swap chains, managed frame sinks, shared GPU images | Managed frame sinks, shared GPU images (never native windows) | Managed frame sinks |
| Hardware video decoding | D3D11VA | Vulkan Video, when the GPU has a decode queue | None (software decoding) |
| Plain subtitles | Yes (DirectWrite) | Yes (SkiaSharp) | Yes (SkiaSharp) |
| MIDI/KAR karaoke lyrics | Yes (DirectWrite) | Not yet (FatzAudio) | Not yet (FatzAudio) |
| Color transform (`BaseVisualTransform`) | Yes | No, the frame is not drawn | Yes |
| HDR tone mapping | GPU for hardware-decoded frames, CPU otherwise | GPU for Vulkan Video frames, CPU otherwise | CPU |
| HDR output on an HDR display | Optional | No | No |

Plain subtitles are rasterized by a renderer-specific component. Direct3D11 uses DirectWrite. The Software and Vulkan renderers use the SkiaSharp rasterizer in `FatzBomb.MediaControl.Rendering.Text`, a port of the DirectWrite one with the same layout and style. MIDI/KAR karaoke lyrics are drawn only by Direct3D11 for now; FatzAudio integration will bring them to every renderer. Text is placed by glyph advances without shaping, so scripts that need ligatures or reordering (Arabic, Indic) look better with Direct3D11, and on a machine without the configured font family the system's default font is used.

> [!NOTE]
> With the Vulkan renderer, a frame that carries a non-identity <xref:FatzBomb.MediaControl.BaseVisualTransform> is refused. The player raises one warning in the `Vulkan` category (`Vulkan preview slice cannot draw this frame yet (...)`) and keeps the last picture.

## How a host chooses a renderer

A renderer is exposed as an `IMediaPresentationCoordinatorFactory`: <xref:FatzBomb.MediaControl.Rendering.Direct3D11.Direct3D11PresentationCoordinatorFactory>, <xref:FatzBomb.MediaControl.Rendering.Vulkan.VulkanPresentationCoordinatorFactory> or <xref:FatzBomb.MediaControl.Rendering.Software.SoftwarePresentationCoordinatorFactory>. A platform factory combines that with the operating-system services (native library loading, audio device monitoring, timers):

| Platform | Factory |
| --- | --- |
| Windows with Direct3D11 | <xref:FatzBomb.MediaControl.WindowsDirect3D11MediaControlPlatformFactory> (one package for both) |
| Windows with another renderer | `new` <xref:FatzBomb.MediaControl.WindowsMediaControlPlatformFactory>`(rendererFactory)` |
| Linux | `new` <xref:FatzBomb.MediaControl.Linux.LinuxMediaControlPlatformFactory>`(rendererFactory, options)` |

```csharp
// Windows, Direct3D11 on the GPU (falls back to WARP when there is no GPU device)
var platform = new WindowsDirect3D11MediaControlPlatformFactory();
var player = await Player.CreateAsync(MediaPlayerConfiguration.Auto, nativePaths, platform);
```

```csharp
// Linux, Vulkan if the machine supports it
if (!VulkanPresentationCoordinatorFactory.IsSupported(out string? reason))
    throw new InvalidOperationException($"No Vulkan: {reason}");

var platform = new LinuxMediaControlPlatformFactory(new VulkanPresentationCoordinatorFactory());
var player = new MediaControlPlayer(platform, MediaPlayerConfiguration.Auto, [nativeDir]);
```

The renderer does not change after the player is built. To switch, dispose the player and create another.

> [!IMPORTANT]
> The adapter has to match the renderer. The Software and Vulkan renderers cannot draw into a native window, so use a surface path that accepts a frame sink: `PresentationMode = WpfComposited` in WPF, `PreferNativeWindow = false` in WinForms (WinForms switches by itself when the renderer refuses the window). Avalonia always works, because it shows frame sinks or shared GPU images. See [Surfaces](../presentation/surfaces.md).

## Picking the first usable renderer

<xref:FatzBomb.MediaControl.Controllers.FallbackPresentationCoordinatorFactory> takes an ordered list and uses the first renderer whose availability probe succeeds. The choice is made once, on first use, and the factory records what each probe found.

```csharp
var chain = new FallbackPresentationCoordinatorFactory(
    WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(new Direct3D11PresentationOptions()),
    new SoftwarePresentationCoordinatorFactory());

foreach (MediaPresentationCandidateResult candidate in chain.Selection)
    Console.WriteLine($"{candidate.BackendName}: {candidate.Detail} (selected: {candidate.IsSelected})");

MediaPresentationAvailability chosen = chain.SelectedAvailability;
var configuration = new MediaPlayerConfiguration(
    chosen.RequiresSoftwareDecoding ? VideoDecodingMethod.Software : VideoDecodingMethod.Auto);

Surface.PresentationMode = chosen.RequiresFrameSinkSurface
    ? WpfPresentationMode.WpfComposited
    : WpfPresentationMode.NativeHwndOverlay;

var player = await Player.CreateAsync(configuration, nativePaths, new WindowsMediaControlPlatformFactory(chain));
```

The probe result (<xref:FatzBomb.MediaControl.Controllers.MediaPresentationAvailability>) tells the host how to build the rest:

| Property | Meaning |
| --- | --- |
| `IsAvailable`, `Detail` | Whether the renderer can run here, and what it found (device, adapter) or why it cannot. |
| `RequiresSoftwareDecoding` | Decode in software. Set when the renderer cannot present hardware frames, or runs on a software rasterizer (WARP). |
| `RequiresFrameSinkSurface` | The renderer can only draw into a managed frame sink, not a native window. |

If no candidate is usable, the last one is used anyway so the player still starts and reports its own failure through diagnostics.

> [!NOTE]
> The Software renderer and a Vulkan device without video decoding force software decoding themselves, whatever `VideoDecoding` says. WARP does not, so a host that uses it should set `VideoDecodingMethod.Software` (see [WARP](#warp)).

## Direct3D11

The default choice on Windows. It presents through DXGI swap chains in a native window, which gives the best video performance, and it draws hardware-decoded (D3D11VA) frames on the GPU into every target kind: swap chains, the managed frame sink (one GPU readback) and the shared textures an Avalonia compositor imports (no readback).

Options live in <xref:FatzBomb.MediaControl.Rendering.Direct3D11.Direct3D11PresentationOptions>, passed to the factory when you create it:

| Option | Default | Meaning |
| --- | --- | --- |
| `DeviceSelection` | `Auto` | `Auto` creates the device on the GPU and falls back to WARP when no GPU device can be created. `Hardware` uses the GPU only. `Warp` uses WARP only. |
| `NativeSwapChainPresentMode` | `VSync` | `VSync` presents with sync interval one so DXGI and DWM pace frames to the monitor. `Immediate` presents with interval zero and is meant for diagnostics. |
| `HdrOutput` | `false` | Shows HDR video as HDR on a monitor in Windows HDR mode. See [Display and color](display-and-color.md#hdr-output). |

```csharp
var options = new Direct3D11PresentationOptions { DeviceSelection = Direct3D11DeviceSelection.Hardware };
var platform = new WindowsDirect3D11MediaControlPlatformFactory(options);
```

On first use the renderer logs the device it got in the `D3D11` category: `Direct3D11 device: <description>.`

### WARP

WARP is Windows' built-in multi-core software rasterizer for Direct3D. The renderer uses it when `DeviceSelection` is `Auto` and no GPU device can be created (a virtual machine, a remote session, a broken driver), or when you ask for it with `Warp`. It keeps the native swap-chain path, so surfaces work as usual, but all drawing runs on the CPU: expect it to be fine for CD+G and images and slow for full-HD video.

When WARP is used under `Auto`, the player logs a warning:

```text
[Warning] D3D11: No GPU device could be created (<reason>); using WARP (Windows software rasterizer).
```

Pair WARP with software decoding. A hardware decoder brings its own GPU device, which the renderer would adopt, leaving WARP behind; the probe reports `RequiresSoftwareDecoding` so a host can set `VideoDecodingMethod.Software`:

```csharp
var options = new Direct3D11PresentationOptions { DeviceSelection = Direct3D11DeviceSelection.Warp };
var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software);
var player = await Player.CreateAsync(configuration, nativePaths, new WindowsDirect3D11MediaControlPlatformFactory(options));
```

If hardware-decoded video does bring a GPU device while the renderer is on WARP, the player logs `Hardware-decoded video brought its GPU device; presenting on it instead of the software rasterizer. Use software decoding to stay on it.`

## Vulkan

A preview renderer for Linux and Windows. It renders on the GPU and hands frames to the surface as a managed frame sink (a CPU readback) or as a shared GPU image that an Avalonia compositor imports without a CPU copy. It draws images, backgrounds, a wash color and CD+G graphics, and video decoded by Vulkan Video.

- `VulkanPresentationCoordinatorFactory.IsSupported(out reason)` checks for a loader, a driver and a device that can blit BGRA8. Its availability probe uses the same check, so it works in a fallback chain.
- On a machine without a GPU, Mesa's lavapipe software driver (`mesa-vulkan-drivers` on Debian and Ubuntu) provides a Vulkan device. It has no video decode queue, so video is decoded in software.
- Vulkan Video decoding needs a GPU with a decode queue and an FFmpeg build with `--enable-vulkan`. See [Hardware decoding](hardware-decoding.md).

## Software

Always available, portable and deterministic: it composes each frame on the CPU into premultiplied BGRA with nearest-neighbor scaling, and draws into managed frame sinks only. It cannot present hardware-decoded frames, so video is decoded in software. Use it as the last link of a fallback chain, for headless machines, or to compare against the GPU renderers.

## Several surfaces

Every renderer can serve any number of surfaces at once (a primary plus second screens). Each frame is composed once per surface, at that surface's size. See [Second screen](../presentation/second-screen.md).

## Trade-offs

| Need | Choose |
| --- | --- |
| Best video performance on Windows | Direct3D11 on the GPU, native window |
| A machine that may have no usable GPU | A fallback chain: Direct3D11 (Auto, so GPU then WARP), then Software |
| Linux with a GPU | Vulkan, with Software behind it in a chain |
| Subtitles or MIDI/KAR lyrics | Direct3D11 |
| Brightness, contrast, hue or HDR tone mapping on the GPU | Direct3D11 |
| WPF content that must be clipped, transformed or layered like normal WPF elements | Any renderer with `WpfComposited`, at the cost of a readback per frame |
| Fewest dependencies, deterministic output | Software |
