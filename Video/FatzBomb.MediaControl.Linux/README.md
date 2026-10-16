# FatzBomb.MediaControl.Linux

Linux platform services for `FatzBomb.MediaControl`. It references Core only and pairs
with any renderer through `IMediaPresentationCoordinatorFactory`, for example the
portable software renderer:

```csharp
using FatzBomb.MediaControl;
using FatzBomb.MediaControl.Linux;
using FatzBomb.MediaControl.Rendering.Software;

var factory = new LinuxMediaControlPlatformFactory(
    new SoftwarePresentationCoordinatorFactory(),
    new LinuxPlatformOptions { DisplayRefreshRateHz = 60 });
using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, ["native"]);
```

What it provides:

- Host-supplied BASS (`libbass.so`, `libbass_fx.so`, optional add-ons) and FFmpeg 8.x
  (`libavcodec.so.62` ABI) loading from host paths, `native/`, or
  `runtimes/linux-<arch>/native/`; no binaries are bundled. See
  `docs/NATIVE_DEPENDENCIES.md`.
- High-resolution media-loop waits (kernel wait plus a 1 ms spin window).
- BASS output-device polling (ALSA/PulseAudio/PipeWire as BASS enumerates them).
- Software video decode with an FFmpeg codec/container capability scan. Hardware decode
  (VA-API/VDPAU) is a later slice.
- Host-configured display refresh rate for presentation pacing.
