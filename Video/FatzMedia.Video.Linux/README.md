# FatzMedia.Video.Linux

Linux platform services for `FatzMedia.Video`. It references Core only and pairs
with any renderer through `IMediaPresentationCoordinatorFactory`, for example the
portable software renderer:

```csharp
using FatzMedia.Video;
using FatzMedia.Video.Linux;
using FatzMedia.Video.Software;

var factory = new LinuxPlatformFactory(
    new SoftwarePresentationCoordinatorFactory(),
    new LinuxPlatformOptions { DisplayRefreshRateHz = 60 });
using var player = new PlayerCore(factory, MediaPlayerConfiguration.Auto, ["native"]);
```

What it provides:

- Host-supplied BASS (`libbass.so`, `libbass_fx.so`, optional add-ons) and FFmpeg 9.0.x
  (`libavcodec.so.63` ABI) loading from host paths, `native/`, or
  `runtimes/linux-<arch>/native/`; no binaries are bundled. See
  `docs/NATIVE_DEPENDENCIES.md`.
- High-resolution media-loop waits (kernel wait plus a 1 ms spin window).
- BASS output-device polling (ALSA/PulseAudio/PipeWire as BASS enumerates them).
- Software video decode with an FFmpeg codec/container capability scan. Hardware decode
  (VA-API/VDPAU) is a later slice.
- Host-configured display refresh rate for presentation pacing.
