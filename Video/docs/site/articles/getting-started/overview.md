# Overview

FatzMedia.Video is a .NET 10 media engine that you embed in your own player application. It plays audio, video, images and karaoke content, composes the picture (video, backgrounds, CD+G graphics, subtitles and lyrics) and presents it on one or more screens. You pick the UI framework (WPF, Avalonia or WinForms), the renderer and the operating system package; the engine does the rest.

This page lists what the library does, where it runs, and which package you reference for each kind of host.

## What it does

| Area | What you get |
| --- | --- |
| Audio | Audio files, MIDI and trackers through the FatzAudio engine, plus an FFmpeg fallback for formats FatzAudio cannot open. Volume, pitch (-60 to +60 semitones), tempo (5 to 5000 percent), loudness normalization, a graphic equalizer, and scrubbing with optional silent seeking. |
| Video | Decoding through FFmpeg, with pictures timed to the audio clock or a virtual clock. Hardware decoding where the renderer supports it, software decoding otherwise. |
| HDR | HDR10 (PQ) and HLG video is tone mapped to SDR by default. The Direct3D11 renderer can also output HDR to an HDR display. |
| CD+G and karaoke | `.cdg` graphics with their audio file, MP3+G, and CD+G inside an archive. MIDI and KAR files with lyrics drawn in time with the music. |
| Subtitles | SRT, ASS and SSA text subtitles from sidecar files, and embedded text tracks in video files. Several tracks can be listed and selected. |
| Backgrounds | An image or looping video behind audio or CD+G, found next to the media file (sidecar) or inside its archive, or supplied by your code. |
| Archives | Media inside `.zip`, `.rar`, `.7z`, `.tar`, `.gz`, `.bz2`, `.xz`, `.tgz` and `.tbz2` files. |
| Network streams | HTTP and HTTPS audio (including internet radio with in-stream metadata) and video (including HLS), with buffering events, a bounded retry policy and certificate verification. |
| Player modes | Full (everything), Background (looping picture only, no audio) and Audio (sound only, no renderer). A session can switch mode on the fly. |
| Playlists | The engine does not own a playlist. It keeps one "prepared next" item analyzed and ready, can start it automatically when the current item ends, and can fade out and in around the change. Separate fade-out and fade-in calls are available for your own transitions. |
| Multi-screen | One player can drive any number of surfaces, for example a control window and a full-screen singer display on a second monitor. |
| Synchronization | Per-item offsets for audio against video, CD+G and subtitles, in 100 ms steps. |

> [!NOTE]
> Audio CD and CD+G disc playback are goals of the project but are not a documented feature of the current release. This guide covers files, archives and network sources.

## Supported platforms and UI frameworks

Native libraries are x64 only. The Windows packages target `net10.0-windows7.0`; the Core, Linux, Avalonia, Software and Vulkan packages target `net10.0`.

| Host UI | Operating system | Adapter package | Renderers you can use |
| --- | --- | --- | --- |
| WPF | Windows | `FatzMedia.Video.Wpf` | Direct3D11 (default choice), Software, Vulkan |
| WinForms | Windows | `FatzMedia.Video.WinForms` | Direct3D11 (default choice), Software, Vulkan |
| Avalonia | Windows | `FatzMedia.Video.Avalonia` | Direct3D11, Vulkan, Software |
| Avalonia | Linux | `FatzMedia.Video.Avalonia` | Vulkan, Software |

With Direct3D11 the WPF and WinForms adapters present into a native child window, which gives the best video performance. The Software and Vulkan renderers deliver finished frames to the adapter, which draws them (a "frame sink"). The WinForms and Avalonia surfaces pick the right path themselves. A WPF host that uses the Software or Vulkan renderer sets `MediaSurface.PresentationMode` to `WpfComposited`. See [Architecture](../concepts/architecture.md).

> [!NOTE]
> Text subtitles and MIDI/KAR lyric text are drawn by all three renderers: Direct3D11 through DirectWrite, Software and Vulkan through SkiaSharp (`FatzMedia.Video.Text`). CD+G graphics, backgrounds and video work on all three too.

macOS, WinUI and ARM are not supported. Avalonia and Linux support is a preview.

## Packages

Every package is optional apart from Core, which every other one builds on. A host references one adapter, one platform package and (usually) one renderer.

| Package | Targets | What it is |
| --- | --- | --- |
| `FatzMedia.Video` | net10.0 | The Core engine: playback, timing, composition policy, state, CD+G, subtitles, backgrounds, player modes, archives, and the contracts renderers and platforms implement. Contains <xref:FatzMedia.Video.PlayerCore>. |
| `FatzMedia.Video.Windows` | net10.0-windows7.0 | Windows platform services: FFmpeg loading, audio device handling, precision timing, hardware decode policy. Contains <xref:FatzMedia.Video.WindowsPlatformFactory>. |
| `FatzMedia.Video.Linux` | net10.0 | Linux platform services: shared library loading, timing, software decode. Contains <xref:FatzMedia.Video.Linux.LinuxPlatformFactory>. |
| `FatzMedia.Video.Direct3D11` | net10.0-windows7.0 | The Direct3D 11 renderer: GPU composition, swap chains, subtitle and lyric text, HDR output. |
| `FatzMedia.Video.Windows.Direct3D11` | net10.0-windows7.0 | A convenience bundle: the Windows package plus the Direct3D11 renderer, with <xref:FatzMedia.Video.WindowsDirect3D11PlatformFactory> to create both. |
| `FatzMedia.Video.Software` | net10.0 | A portable CPU renderer that needs no GPU. Deterministic output; every platform. Draws text through `FatzMedia.Video.Text`. |
| `FatzMedia.Video.Vulkan` | net10.0 | A Vulkan renderer for Windows and Linux, with GPU video decoding through Vulkan Video where the driver supports it. Preview. |
| `FatzMedia.Video.Text` | net10.0 | The SkiaSharp subtitle and lyric text rasterizer the Software and Vulkan renderers use. They reference it, so you do not list it yourself. Brings SkiaSharp 3.119.4 and its native library. |
| `FatzMedia.Video.Wpf` | net10.0-windows7.0 | The WPF adapter: `MediaSurface`, `Player`, `SecondaryPresentationWindow`, `BackgroundSurface`, `KaraokeSurface`. Depends on the Windows package. |
| `FatzMedia.Video.WinForms` | net10.0-windows7.0 | The WinForms adapter with the same set of controls. Depends on Core only. |
| `FatzMedia.Video.Avalonia` | net10.0 | The Avalonia adapter with the same set of controls. Depends on Core and Avalonia only. |

The repository also holds three test players (WPF, Avalonia and WinForms) that share one view model. They are not packages, but they are the best place to see every feature in use: `FatzMedia.Video.Sample.Player`, `FatzMedia.Video.Sample.Player.Avalonia` and `FatzMedia.Video.Sample.Player.WinForms`.

## Which packages does my host reference?

| Host | Reference |
| --- | --- |
| WPF on Windows | `FatzMedia.Video.Wpf` and `FatzMedia.Video.Windows.Direct3D11` |
| WinForms on Windows | `FatzMedia.Video.WinForms` and `FatzMedia.Video.Windows.Direct3D11` |
| Avalonia on Windows | `FatzMedia.Video.Avalonia`, `FatzMedia.Video.Windows`, and a renderer (`FatzMedia.Video.Windows.Direct3D11`, `FatzMedia.Video.Vulkan` or `FatzMedia.Video.Software`) |
| Avalonia on Linux | `FatzMedia.Video.Avalonia`, `FatzMedia.Video.Linux`, and a renderer (`FatzMedia.Video.Vulkan` or `FatzMedia.Video.Software`) |
| No UI (audio only, tests, services) | `FatzMedia.Video` and a platform package. See [Player and session](../concepts/player-and-session.md). |

The details, including target frameworks, are in [Installation](installation.md).

## Next steps

1. [Installation](installation.md) for packages and project setup.
2. [Native dependencies](native-dependencies.md): the FFmpeg libraries are not included, and you supply and license them.
3. A quickstart for your UI: [WPF](quickstart-wpf.md), [Avalonia](quickstart-avalonia.md) or [WinForms](quickstart-winforms.md).
