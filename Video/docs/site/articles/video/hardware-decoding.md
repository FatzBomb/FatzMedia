# Hardware decoding

Video can be decoded on the GPU, which frees the CPU and makes high-resolution and high-bit-rate files practical. The library decides per file whether hardware decoding is possible, uses it when it is, and falls back to software when it is not or when it stops working. This article explains the setting, what each renderer does, how the fallback works and how to see which path a file actually uses.

## Choosing a decoding method

<xref:FatzBomb.MediaControl.Core.Configuration.VideoDecodingMethod> is part of the player's [configuration](../reference/configuration.md) and is fixed when the player is created.

| Value | Behavior |
| --- | --- |
| `Auto` (default) | Try hardware decoding for each stream; use software if it is not available or cannot be opened. |
| `Hardware` | Tries hardware exactly as `Auto` does and still falls back to software when it cannot be used; the two differ only in what is logged. It does not make the player fail. |
| `Software` | Always decode on the CPU. |

```csharp
var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Auto);
// or
var software = new MediaPlayerConfiguration { VideoDecoding = VideoDecodingMethod.Software };
```

Use `Software` to compare CPU load, to rule the GPU out when you chase a visual problem, or with a renderer that cannot present hardware frames (see below).

## What gets used

The decoding API depends on the platform and the renderer:

| Setup | Hardware decoding |
| --- | --- |
| Windows, Direct3D11 on a GPU | Direct3D 11 Video Acceleration (D3D11VA). The decoder's device is adopted by the renderer, so frames stay on the GPU. |
| Vulkan renderer, GPU with a Vulkan Video decode queue | Vulkan Video, decoded on the renderer's own device. Needs an FFmpeg build with `--enable-vulkan`. |
| Vulkan renderer without a decode queue (lavapipe, older drivers) | Software. |
| Software renderer | Software. |
| Direct3D11 on WARP | Software is expected. Set it yourself. See [Renderers](renderers.md#warp). |
| Linux platform | The Linux platform package has no hardware decoder of its own; only the Vulkan renderer decodes on the GPU. |

On Windows the decoder tries the FFmpeg device types `d3d11va`, `d3d12va` and `dxva2`, in that order. Hardware decoding also depends on the FFmpeg build you deploy and on the codec (H.264, HEVC, VP9 and AV1 are typical candidates); whether a codec decodes on the GPU depends on the GPU generation and driver.

## The automatic fallback to software

You do not have to handle failure cases. The player switches a file to software decoding on its own, keeps playing from the current position, and does not touch audio or CD+G playback:

- **The hardware decoder cannot start.** If hardware decoding produces no usable first frame, the player reopens the video in software. Warning in category `VideoPipeline`: `Hardware video decode produced no usable startup frame; falling back to software for <file>. ...`
- **The renderer cannot use the decoder's frames.** The renderer asks for software decoding when it cannot take the frames for a reason other than a lost device: it cannot adopt the decoder's GPU device, its device has no video support, or it runs on another device. The player reopens the video in software at the current position. Warning in category `VideoPipeline`: `Decoding <file> in software until it is opened again: <reason>.`
- **The renderer's device cannot decode.** With the Vulkan renderer, if its device or the FFmpeg build cannot decode, the player decodes in software and says why once. Warning in category `VideoDecoder`: `Hardware decoding on the renderer's device is unavailable (<reason>); decoding in software.`
- **The GPU device was lost.** The player switches to software, then checks periodically whether the GPU is back and returns to hardware decoding. See [GPU device loss](../presentation/gpu-device-loss.md).

A switch made because the renderer refused the frames lasts until the file is opened again. Opening a new file tries hardware decoding afresh.

## Seeing what is used

When a video opens, the player raises an Info diagnostic in category `VideoDecoder` that describes the path:

```text
Decoding H264 High 8-bit 1920x1080 (yuv420p, ...), decoder h264 via d3d11va
Decoding HEVC Main 10 10-bit 3840x2160 (yuv420p10le, ...), decoder hevc in software
```

`via <method>` means hardware decoding with that API, `in software` means the CPU. A line such as `in software (d3d11va configured but not used)` means a hardware device was set up but the decoder did not use it. Subscribe to <xref:FatzBomb.MediaControl.MediaControlPlayer.MediaDiagnostic> and watch the `VideoDecoder` and `VideoPipeline` categories:

```csharp
player.MediaDiagnostic += (_, e) =>
{
    if (e.Category is "VideoDecoder" or "VideoPipeline")
        Console.WriteLine($"[{e.Level}] {e.Category}: {e.Message}");
};
```

The handler runs on a background thread. Marshal to the UI thread before touching UI state.

### Asking what the machine supports

<xref:FatzBomb.MediaControl.MediaControlPlayer.VideoDecodeCapabilities> is a snapshot of the codecs and container extensions the platform found in the loaded FFmpeg, and which of them have a hardware decoder:

```csharp
VideoDecodeCapabilities caps = player.VideoDecodeCapabilities;
Console.WriteLine("Hardware: " + string.Join(", ", caps.HardwareAcceleratedCodecNames));
Console.WriteLine("Software only: " + string.Join(", ", caps.SoftwareOnlyCodecNames));

if (caps.ForCodec("hevc") is { } hevc)
    Console.WriteLine($"{hevc.FriendlyName}: hardware={hevc.HardwareDecodeAvailable} via {hevc.HardwareMethod}");

if (caps.ForExtension(".mkv") is { } mkv)
    Console.WriteLine($"{mkv.FriendlyName}: any hardware codec = {mkv.AnyHardwareDecodeAvailable}");
```

> [!NOTE]
> The capability snapshot says what the FFmpeg build and platform offer. It does not prove that a particular file or GPU will decode in hardware; the `Decoding ...` diagnostic reports what actually happened.

## Related settings

- `PresentationLeadRefreshIntervals` and frame timing do not depend on the decoding method. See [Frame timing](frame-timing.md).
- HDR video is tone mapped on the GPU for hardware-decoded frames when the renderer can, and on the CPU otherwise. See [Display and color](display-and-color.md).
