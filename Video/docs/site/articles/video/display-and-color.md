# Display and color

This article covers how the picture is fitted into the surface (<xref:FatzMedia.Video.VideoDisplayMode>), how to adjust its color (<xref:FatzMedia.Video.BaseVisualTransform>) and how HDR video is shown, on an ordinary SDR monitor and on an HDR monitor.

## Display modes

<xref:FatzMedia.Video.MediaControlPlayer.VideoDisplayMode> controls how primary video is mapped into the presentation surface. It is a runtime property of the player, takes effect on the next presented frame, and applies to every surface of the player.

| Mode | Behavior |
| --- | --- |
| `Original` | The video at its native pixel size, centered. |
| `Zoom` (default) | Scaled uniformly so the whole frame is visible. Bars appear when the aspect ratios differ. |
| `Stretch` | Scaled non-uniformly to fill the surface exactly. This distorts the aspect ratio. |
| `ZoomToFill` | Scaled uniformly until the surface is filled. Excess edges are cropped. |

Background media (a looping video or image shown behind CD+G or overlays) ignores this setting and always uses `ZoomToFill`.

```csharp
player.VideoDisplayMode = VideoDisplayMode.ZoomToFill;
```

Each adapter's `MediaSurface` has a `VideoDisplayMode` property (default `Zoom`) and forwards it to its player. Only a primary surface forwards it, so a second-screen surface never changes the mode the main window chose:

```xaml
<fbm:MediaSurface Player="{Binding Player}" VideoDisplayMode="ZoomToFill" />
```

## Color adjustments

<xref:FatzMedia.Video.MediaControlPlayer.BaseVisualTransform> adjusts the color of the base visual layer: the video or image, not the overlays drawn on top of it (CD+G graphics, subtitles and lyrics) unless you ask for them. It is a value type; assign a new one to change it. `BaseVisualTransform.Identity` (the default) leaves the picture unchanged.

| Property | Range | Neutral value |
| --- | --- | --- |
| `Brightness` | -1 to 1 | 0 |
| `Contrast` | 0 to 2 | 1 |
| `Saturation` | 0 to 2 | 1 |
| `HueDegrees` | -180 to 180 | 0 |
| `RedGain`, `GreenGain`, `BlueGain` | 0 to 2 | 1 |
| `IncludeOverlays` | `true` or `false` | `false` |

Values outside the range are clamped, and `NaN` falls back to the neutral value. With `IncludeOverlays` on, the transform applies to the whole composition including overlays.

```csharp
player.BaseVisualTransform = new BaseVisualTransform
{
    Brightness = 0.1f,
    Contrast = 1.15f,
    Saturation = 0.9f,
    HueDegrees = -5f,
};

player.BaseVisualTransform = BaseVisualTransform.Identity; // back to normal

player.BaseVisualTransformChanged += (_, e) =>
    Console.WriteLine($"brightness {e.PreviousValue.Brightness} -> {e.CurrentValue.Brightness}");
```

The change event is raised on the thread that set the value (after `Reset`, on an engine thread); marshal to the UI thread. A player reset (`Reset`) returns the transform to the identity and raises the event with `MediaChangeReason.PlayerReset`.

> [!WARNING]
> The Vulkan renderer does not support color transforms: a frame with a non-identity transform is not drawn. Use Direct3D11 or the Software renderer when you need them. See [Renderers](renderers.md).

## HDR video

HDR10 (PQ) and HLG video is tone mapped to SDR by default, using the BT.2390 curve, so that it is neither washed out nor clipped. A hardware-decoded frame is tone mapped on the GPU when the renderer can; software frames are tone mapped on the CPU. The decode diagnostic says which kind of video you have, for example `HDR10/PQ, tone mapped to SDR`.

Tune it with <xref:FatzMedia.Video.Core.Configuration.HdrToneMappingOptions> in the [configuration](../reference/configuration.md):

| Option | Default | Meaning |
| --- | --- | --- |
| `Disabled` | `false` | Show HDR video without tone mapping. It looks washed out. For comparison and diagnosis. |
| `ReferenceWhiteNits` | `0` (use 203) | The SDR target in nits. The source's range is compressed into `[0, ReferenceWhiteNits]`. Lower values make HDR video brighter, higher values darker. `0` means `HdrToneMappingOptions.DefaultReferenceWhiteNits` (203, BT.2408's HDR reference white). |
| `PreferCpu` | `false` | Tone map on the CPU even when the renderer could on the GPU; hardware-decoded frames are copied back. For comparison and diagnosis. |

```csharp
var configuration = new MediaPlayerConfiguration
{
    HdrToneMapping = new HdrToneMappingOptions(ReferenceWhiteNits: 250),
};
```

For HDR10 the source peak comes from the stream's content light level, else from the mastering display's peak, else 1000 nits is assumed.

### HDR output

On a monitor that Windows has switched to HDR mode, you can show HDR video as HDR instead of tone mapping it. This is an option of the Direct3D11 renderer:

```csharp
var options = new Direct3D11PresentationOptions { HdrOutput = true };
var platform = new WindowsDirect3D11PlatformFactory(options);
```

With it on, the primary window's swap chain switches to FP16 scRGB while hardware-decoded HDR10 or HLG video plays on a monitor with HDR on, and back to SDR otherwise. SDR layers (CD+G, subtitles, backgrounds) are shown at the system's SDR white level.

HDR output stays off, and the video is tone mapped as usual, when:

- the surface is a secondary window, a WPF-composited surface or a shared GPU image (Avalonia);
- the video is SDR, or the monitor is not in HDR mode;
- the video is software decoded.

The renderer logs each change once in the `D3D11.HdrOutput` category, with the reason, for example `scRGB (the monitor is in HDR mode (peak 1000 nits, SDR white 80 nits)).` or `SDR (the monitor is not in HDR mode).` A failure to switch is logged as a warning and playback continues in SDR.
