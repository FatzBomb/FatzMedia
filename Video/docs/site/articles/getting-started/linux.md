# Running on Linux

The library runs on Linux x64 with the Avalonia adapter, the Linux platform package and the Software or Vulkan renderer. This page covers the setup, how the native libraries are found, and what is not available compared with Windows. Linux support is a preview.

## What you reference

| Package | Role |
| --- | --- |
| `FatzBomb.MediaControl` | Core engine |
| `FatzBomb.MediaControl.Linux` | Platform services: library loading, timing, audio device monitoring |
| `FatzBomb.MediaControl.Avalonia` | The UI adapter |
| `FatzBomb.MediaControl.Rendering.Vulkan` and/or `FatzBomb.MediaControl.Rendering.Software` | The renderer |

For a complete window, follow [Quickstart for Avalonia](quickstart-avalonia.md). The `net10.0` build of that project is the Linux build.

## Choose a renderer

Linux has no Direct3D11, so the choices are Vulkan and Software:

- **Software** (`SoftwarePresentationCoordinatorFactory`) composes on the CPU. It works everywhere, including machines with no GPU or driver. It presents CPU frames only and always decodes video in software; create the player with `new MediaPlayerConfiguration(VideoDecodingMethod.Software)` to make that explicit.
- **Vulkan** (`VulkanPresentationCoordinatorFactory`) composes on the GPU and hands frames to Avalonia as a shared GPU image when the compositor supports it, or through a frame sink otherwise. It decodes on the GPU through Vulkan Video when the driver has a decode queue and FFmpeg is built with Vulkan; otherwise it asks for software decoding.

Probe before you choose. `VulkanPresentationCoordinatorFactory.IsSupported` tells you whether a usable device exists; `ProbeAvailability()` also reports whether it will decode in hardware. A fallback chain (<xref:FatzBomb.MediaControl.Controllers.FallbackPresentationCoordinatorFactory>) takes the first candidate that is available.

```csharp
using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Linux;
using FatzBomb.MediaControl.Rendering.Software;
using FatzBomb.MediaControl.Rendering.Vulkan;

IMediaPresentationCoordinatorFactory renderer =
    VulkanPresentationCoordinatorFactory.IsSupported(out _)
        ? new VulkanPresentationCoordinatorFactory()
        : new SoftwarePresentationCoordinatorFactory();

var platform = new LinuxMediaControlPlatformFactory(
    renderer,
    new LinuxPlatformOptions { DisplayRefreshRateHz = 144 });

// In an Avalonia app, pass `platform` to FatzBomb.MediaControl.Avalonia.Player.CreateAsync.
// Here, a UI-free audio-only player from Core:
var configuration = MediaPlayerConfiguration.Auto with { Mode = MediaPlayerMode.Audio };
using var player = new MediaControlPlayer(platform, configuration, []);
```

## Platform options

<xref:FatzBomb.MediaControl.Linux.LinuxPlatformOptions> has two settings:

| Option | Default | Range | Meaning |
| --- | --- | --- | --- |
| `DisplayRefreshRateHz` | 60 | 24 to 360 | The refresh rate used for presentation pacing. Linux has no compositor-independent query for it, so you supply it. |
| `AudioDevicePollInterval` | 1 second | 0.25 to 10 seconds | How often BASS output-device state is polled for device changes. |

## Native libraries

The packages contain no binaries. You supply Linux x64 builds of BASS and FFmpeg 9.0.x, and you license them (see [Native dependencies](native-dependencies.md), including the file lists and licensing).

The engine searches your paths first, then the application folder, `native`, and `runtimes/linux-x64/native` beneath it. It does not search system library directories on its own, and it does not search subfolders. Put FFmpeg's libraries in one directory. A path you pass to the player must exist, or the constructor throws.

Check what loaded with <xref:FatzBomb.MediaControl.MediaControlPlayer.NativeDependencies>; its `Describe()` text names missing and wrong-version files (for example `libavcodec.so.62` found where `libavcodec.so.63` is needed).

Notes:

- FFmpeg libraries are preloaded in dependency order (avutil, swresample, swscale, avcodec, avformat, then the optional avfilter and avdevice) because typical Linux builds refer to each other by soname without an `$ORIGIN` run path.
- BASS add-ons that enable formats on Windows exist only partly on Linux: AAC, AC-3, FLAC, MIDI, Opus and WavPack have add-on names (`libbass_aac.so`, `libbass_ac3.so`, `libbassflac.so`, `libbassmidi.so`, `libbassopus.so`, `libbasswv.so`). Formats without an add-on still play through the FFmpeg fallback where FFmpeg can decode them.
- For HTTPS network video, the OpenSSL-based FFmpeg builds read their trusted roots from the system certificate store (`/etc/ssl` on Debian and Ubuntu), or from a PEM bundle you set with `MediaPlayerConfiguration.NetworkTls`. Check your build's TLS backend.
- Audio goes through whatever BASS enumerates (ALSA, PulseAudio, PipeWire).

## What is different from Windows

| Feature | Linux |
| --- | --- |
| Direct3D11 renderer, HDR output, `WindowsNativeLibraryPaths` | Not available. |
| Hardware decoding | Only through the Vulkan renderer, with Vulkan Video and an FFmpeg built with `--enable-vulkan`. The Software renderer always decodes in software. |
| Text subtitles | Drawn by the Software and Vulkan renderers through SkiaSharp. Fonts come from fontconfig: the configured family (Segoe UI by default) when installed, otherwise the system default font, so glyph shapes and widths differ from Windows. MIDI/KAR karaoke lyrics are not drawn on Linux yet; FatzAudio integration will bring them to every renderer. |
| MIDI | Needs `libbassmidi.so` and a soundfont (`MediaPlayerConfiguration.MidiSoundfontPath`, or one of the file names listed in [MIDI soundfont](native-dependencies.md#midi-soundfont)). There is no system `gm.dls`. |
| `basscd`, `bassalac`, `basswma` add-ons | Not part of the Linux set. ALAC and WMA files can still open through the FFmpeg fallback. |
| WPF and WinForms | Windows only. Use Avalonia. |

> [!TIP]
> The repository's Avalonia test player, `FatzBomb.MediaControl.Player.Avalonia`, is a complete Linux example with a renderer picker, up to four players side by side, and status lines. Run it with `dotnet run -f net10.0` after placing the libraries in its `native` folder.
