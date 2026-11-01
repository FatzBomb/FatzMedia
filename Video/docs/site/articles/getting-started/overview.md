# Overview

FatzBomb.MediaControl is a .NET 10 media engine that you embed in your own player application. It plays audio, video, images and karaoke content, composes the picture (video, backgrounds, CD+G graphics, subtitles and lyrics) and presents it on one or more screens. You pick the UI framework (WPF, Avalonia or WinForms), the renderer and the operating system package; the engine does the rest.

This page lists what the library does, where it runs, and which package you reference for each kind of host.

## What it does

| Area | What you get |
| --- | --- |
| Audio | Local audio files through BASS, plus an FFmpeg fallback for formats whose BASS plug-in is missing. Volume, pitch (-60 to +60 semitones), tempo (5 to 5000 percent), loudness normalization, a graphic equalizer, and scrubbing with optional silent seeking. |
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
| WPF | Windows | `FatzBomb.MediaControl.Wpf` | Direct3D11 (default choice), Software, Vulkan |
| WinForms | Windows | `FatzBomb.MediaControl.WinForms` | Direct3D11 (default choice), Software, Vulkan |
| Avalonia | Windows | `FatzBomb.MediaControl.Avalonia` | Direct3D11, Vulkan, Software |
| Avalonia | Linux | `FatzBomb.MediaControl.Avalonia` | Vulkan, Software |

With Direct3D11 the WPF and WinForms adapters present into a native child window, which gives the best video performance. The Software and Vulkan renderers deliver finished frames to the adapter, which draws them (a "frame sink"). The WinForms and Avalonia surfaces pick the right path themselves. A WPF host that uses the Software or Vulkan renderer sets `MediaSurface.PresentationMode` to `WpfComposited`. See [Architecture](../concepts/architecture.md).

> [!NOTE]
> Plain subtitles are drawn by all three renderers: Direct3D11 through DirectWrite, Software and Vulkan through SkiaSharp (`FatzBomb.MediaControl.Rendering.Text`). MIDI/KAR karaoke lyrics are drawn only by Direct3D11 for now; FatzAudio integration will bring them to every renderer. CD+G graphics, backgrounds and video work on all three.

macOS, WinUI and ARM are not supported. Avalonia and Linux support is a preview.

## Packages

Every package is optional apart from Core, which every other one builds on. A host references one adapter, one platform package and (usually) one renderer.

| Package | Targets | What it is |
| --- | --- | --- |
| `FatzBomb.MediaControl` | net10.0 | The Core engine: playback, timing, composition policy, state, CD+G, subtitles, backgrounds, player modes, archives, and the contracts renderers and platforms implement. Contains <xref:FatzBomb.MediaControl.MediaControlPlayer>. |
| `FatzBomb.MediaControl.Windows` | net10.0-windows7.0 | Windows platform services: BASS and FFmpeg loading, audio device handling, precision timing, hardware decode policy. Contains <xref:FatzBomb.MediaControl.WindowsMediaControlPlatformFactory>. |
| `FatzBomb.MediaControl.Linux` | net10.0 | Linux platform services: shared library loading, timing, BASS device monitoring, software decode. Contains <xref:FatzBomb.MediaControl.Linux.LinuxMediaControlPlatformFactory>. |
| `FatzBomb.MediaControl.Rendering.Direct3D11` | net10.0-windows7.0 | The Direct3D 11 renderer: GPU composition, swap chains, subtitle and lyric text, HDR output. |
| `FatzBomb.MediaControl.Windows.Direct3D11` | net10.0-windows7.0 | A convenience bundle: the Windows package plus the Direct3D11 renderer, with <xref:FatzBomb.MediaControl.WindowsDirect3D11MediaControlPlatformFactory> to create both. |
| `FatzBomb.MediaControl.Rendering.Software` | net10.0 | A portable CPU renderer that needs no GPU. Deterministic output; every platform. Draws text through `FatzBomb.MediaControl.Rendering.Text`. |
| `FatzBomb.MediaControl.Rendering.Vulkan` | net10.0 | A Vulkan renderer for Windows and Linux, with GPU video decoding through Vulkan Video where the driver supports it. Preview. |
| `FatzBomb.MediaControl.Rendering.Text` | net10.0 | The SkiaSharp plain-subtitle rasterizer the Software and Vulkan renderers use. They reference it, so you do not list it yourself. SkiaSharp 3.119.4 and its native library ship automatically with it. |
| `FatzBomb.MediaControl.Wpf` | net10.0-windows7.0 | The WPF adapter: `MediaSurface`, `Player`, `SecondaryPresentationWindow`, `BackgroundSurface`, `KaraokeSurface`. Depends on the Windows package. |
| `FatzBomb.MediaControl.WinForms` | net10.0-windows7.0 | The WinForms adapter with the same set of controls. Depends on Core only. |
| `FatzBomb.MediaControl.Avalonia` | net10.0 | The Avalonia adapter with the same set of controls. Depends on Core and Avalonia only. |

The repository also holds three test players (WPF, Avalonia and WinForms) that share one view model. They are not packages, but they are the best place to see every feature in use: `FatzBomb.MediaControl.Player`, `FatzBomb.MediaControl.Player.Avalonia` and `FatzBomb.MediaControl.Player.WinForms`.

## Which packages does my host reference?

| Host | Reference |
| --- | --- |
| WPF on Windows | `FatzBomb.MediaControl.Wpf` and `FatzBomb.MediaControl.Windows.Direct3D11` |
| WinForms on Windows | `FatzBomb.MediaControl.WinForms` and `FatzBomb.MediaControl.Windows.Direct3D11` |
| Avalonia on Windows | `FatzBomb.MediaControl.Avalonia`, `FatzBomb.MediaControl.Windows`, and a renderer (`FatzBomb.MediaControl.Windows.Direct3D11`, `FatzBomb.MediaControl.Rendering.Vulkan` or `FatzBomb.MediaControl.Rendering.Software`) |
| Avalonia on Linux | `FatzBomb.MediaControl.Avalonia`, `FatzBomb.MediaControl.Linux`, and a renderer (`FatzBomb.MediaControl.Rendering.Vulkan` or `FatzBomb.MediaControl.Rendering.Software`) |
| No UI (audio only, tests, services) | `FatzBomb.MediaControl` and a platform package. See [Player and session](../concepts/player-and-session.md). |

The details, including target frameworks, are in [Installation](installation.md).

## Next steps

1. [Installation](installation.md) for packages and project setup.
2. [Native dependencies](native-dependencies.md): the BASS and FFmpeg libraries are not included, and you supply and license them.
3. A quickstart for your UI: [WPF](quickstart-wpf.md), [Avalonia](quickstart-avalonia.md) or [WinForms](quickstart-winforms.md).
