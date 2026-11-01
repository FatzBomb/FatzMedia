# Native dependencies

FatzBomb.MediaControl plays media through two native libraries: BASS for audio and FFmpeg for video decoding and demuxing. Neither is included in the packages. You download them, license them, ship them with your application and tell the engine where to find them. This page lists the files, the folders the engine searches, and your licensing responsibilities.

## What you need

| Library | Needed for | Required? |
| --- | --- | --- |
| FFmpeg 9.0.x shared libraries | Video, embedded subtitles, network video, and the fallback decoder for audio formats BASS cannot open | Yes. Player creation throws without it. |
| BASS and BASS_FX | Audio output and every audio feature | No. Without the core libraries the player starts with no audio (see [Running without BASS](#running-without-bass)). |
| BASS add-ons | Extra audio formats, MIDI, audio CD | No. Each one adds the formats it handles. |
| A MIDI soundfont | MIDI and KAR playback | Only for MIDI. |

### Windows (x64)

| Kind | Files |
| --- | --- |
| FFmpeg, all seven required, all in one folder | `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll`, `swscale-10.dll` |
| BASS core | `bass.dll`, `bass_fx.dll` |
| BASS add-ons (optional) | `bass_aac.dll`, `bass_ac3.dll`, `bassalac.dll`, `basscd.dll`, `bassflac.dll`, `bassmidi.dll`, `bassopus.dll`, `basswma.dll` |

The version numbers in the FFmpeg file names are the library major versions of FFmpeg 9.0.x. FFmpeg 8 libraries do not load. If the folder holds a different version of a library, the startup report says so, for example `Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.`

Use a shared (DLL) build of FFmpeg. A static `ffmpeg.exe` is not enough.

### Linux (x64)

| Kind | Required | Optional |
| --- | --- | --- |
| FFmpeg 9.0.x | `libavutil.so.61`, `libswresample.so.7`, `libswscale.so.10`, `libavcodec.so.63`, `libavformat.so.63` | `libavfilter.so.12`, `libavdevice.so.63` |
| BASS | `libbass.so`, `libbass_fx.so` (audio is off without them) | `libbass_aac.so`, `libbass_ac3.so`, `libbassflac.so`, `libbassmidi.so`, `libbassopus.so`, `libbasswv.so` |

Put all FFmpeg libraries in one directory. The engine preloads them in dependency order because typical builds refer to each other by soname without an `$ORIGIN` run path. See [Running on Linux](linux.md).

### What the optional pieces change

- Each BASS add-on enables the file extensions it handles (`bass_aac`: `.aac`, `.m4a`, `.m4b`; `bass_ac3`: `.ac3`; `bassflac`: `.flac`; `bassmidi`: `.mid`, `.midi`, `.kar`, `.rmi`; `bassopus`: `.opus`; `basscd`: `.cda`).
- When FFmpeg is loaded, audio formats whose add-on is missing (AAC, M4A, ALAC, FLAC, Opus, AC-3, WMA and others FFmpeg decodes) still open through an FFmpeg fallback that feeds the same BASS playback chain. MIDI needs `bassmidi` and has no fallback.
- `OpenFileDialogFilter` on the player (<xref:FatzBomb.MediaControl.MediaControlPlayer.OpenFileDialogFilter>) lists what the loaded libraries accept, so your Open dialog matches the install.

### MIDI soundfont

MIDI synthesis needs a soundfont. The engine uses, in this order:

1. <xref:FatzBomb.MediaControl.Core.Configuration.MediaPlayerConfiguration.MidiSoundfontPath> when you set it (`.sf2`, `.sfz` or `.dls`).
2. On Windows, the system `gm.dls` (converted to SF2 once and cached).
3. The first that exists of `System32\drivers\sgm.sb` (Windows), and `soundfont.sf2`, `native/soundfont.sf2` and `native/TimGM6mb.sf2` under the application folder.

If none is found, MIDI plays silently and the engine raises a Warning diagnostic in the `BASSMIDI` category. On Linux there is no `gm.dls`, so ship an `.sf2`. The soundfont is process-wide; see [MIDI and KAR](../karaoke/midi-and-kar.md#soundfonts).

## Where the engine looks

The engine searches the folders you pass to the player, then the default folders. A folder you pass must exist, or the player constructor throws `DirectoryNotFoundException`. On Windows the search is recursive, so BASS and FFmpeg can sit in separate subfolders of one root. On Linux each folder is searched without its subfolders.

| Platform | Default folders (under the application base directory), searched after yours |
| --- | --- |
| Windows | `native`, `runtimes/win-x64/native` (only those that exist) |
| Linux | the application folder itself, `native`, `runtimes/linux-x64/native` |

On Linux the system library directories (`/usr/lib` and so on) are not searched implicitly. The host owns the binaries.

### Passing paths to the player

The player constructor and `CreateAsync` take a list of folders in priority order. Pass absolute paths: a relative path is resolved against the current working directory, not the application folder.

```csharp
IReadOnlyList<string> nativePaths =
[
    @"C:\Program Files\MyHost\media-native\bass",
    @"C:\Program Files\MyHost\media-native\ffmpeg"
];

var player = await FatzBomb.MediaControl.Wpf.Player.CreateAsync(
    MediaPlayerConfiguration.Auto,
    nativePaths,
    new WindowsDirect3D11MediaControlPlatformFactory());
```

To use only the default folders, pass an empty list or <xref:FatzBomb.MediaControl.WindowsNativeLibraryPaths.GetDefaultSearchPaths>, which returns the default folders that exist.

> [!NOTE]
> Native libraries load once per process. A later player created with the same paths reuses the result. If BASS was missing the first time, a later player created with different paths tries BASS again, and a player created with the same paths keeps the "no BASS" result without scanning again. FFmpeg, once loaded, stays loaded.

### The DLLPath settings

<xref:FatzBomb.MediaControl.DLLPath> (`Bass`, `FFmpeg`) is a static setting that is used only where no explicit paths are given: the ready-made `BackgroundSurface` and `KaraokeSurface` controls (unless you set their `NativeLibraryPaths`), and `AudioOutputDevices` on Windows. A player you create yourself with a path list does not read it.

## Startup report

Read <xref:FatzBomb.MediaControl.MediaControlPlayer.NativeDependencies> (or `NativeDependencies` on the platform factory) to see what was found. It returns a <xref:FatzBomb.MediaControl.NativeDependencyReport> with one <xref:FatzBomb.MediaControl.NativeLibraryCheck> per library: exact file name, required or not, status (`Loaded`, `Missing`, `NotLoaded`, `FailedToLoad`), where it was found, and a hint. `Describe()` returns one readable line per problem, ready to show to a user.

When a required FFmpeg library fails, the player constructor throws, and the exception message includes the hints. The player does not exist then, so read the report from the platform factory you passed in:

```csharp
var platform = new WindowsDirect3D11MediaControlPlatformFactory();
try
{
    player = await FatzBomb.MediaControl.Wpf.Player.CreateAsync(
        MediaPlayerConfiguration.Auto, WindowsNativeLibraryPaths.GetDefaultSearchPaths(), platform);
}
catch (Exception ex)
{
    var lines = platform.NativeDependencies.Describe();
    // Show ex.Message and lines to the user.
}
```

### Running without BASS

When the BASS core libraries are missing or fail to load, the player still starts in every mode. The loader reports a Warning, `NativeDependencyReport.IsAudioAvailable` is false, and:

- Video plays its pictures on the playback clock without sound. Images and CD+G graphics without audio open normally.
- Media that needs audio (audio files, MP3+G, network audio, MIDI, audio mode) fails to load: `Load` returns false, with an Error diagnostic and a `MediaError` carrying `NativeLibraryMissing`.
- `AudioOutputDevices.Enumerate` returns an empty list.

## Licensing is your responsibility

The packages do not redistribute BASS or FFmpeg. You are responsible for obtaining, licensing, validating, deploying and updating them. This guide is not legal advice.

### BASS

- Download BASS only from Un4seen Developments (<https://www.un4seen.com/bass.html>). The supported ABI line is BASS 2.4.
- BASS is free only for qualifying non-commercial use. A commercial application needs a BASS licence from Un4seen, plus the terms of each add-on you ship.
- Un4seen's licences apply to your end-user products and do not permit reselling or sublicensing BASS. The control does not do either; you hold the licence.

### FFmpeg

- Use a shared build of FFmpeg 9.0.x. For a proprietary application, use a build compiled without `--enable-gpl` and without `--enable-nonfree` (an LGPL build) and review every external library it enables. Builds that enable GPL are suitable for local testing only, unless you accept GPL terms for your product. Check a build's configuration with `ffmpeg -hide_banner -buildconf`.
- If you ship an LGPL build: ship the libraries unmodified and loaded dynamically (as the engine does) so users can replace them; include the LGPL licence text and credit FFmpeg in your documentation or About box; make the matching source available; do not forbid, in your licence agreement, the reverse engineering needed to debug modified FFmpeg libraries. FFmpeg publishes its own checklist at <https://ffmpeg.org/legal.html>.
- Ship FFmpeg libraries that match the control's version (9.0.x). When an update of the control moves to a new FFmpeg major version, your next update must carry the new libraries.
- Codec patents (H.264, HEVC, AAC and others) are a separate question from FFmpeg's copyright licence and depend on your market.

### Feature-specific FFmpeg requirements

- **Network video** uses FFmpeg's own protocols: the build needs the `http`, `tcp` and `hls` components (on by default) and a TLS backend for `https` (OpenSSL, GnuTLS, mbedTLS or Schannel). Certificates are verified by default; see `MediaPlayerConfiguration.NetworkTls`.
- **GPU decoding with the Vulkan renderer** needs an FFmpeg build configured with `--enable-vulkan`. Without it the player logs a diagnostic and decodes in software.
- **Direct3D11 hardware decoding** (D3D11VA) needs a Windows FFmpeg build with D3D11VA support. Without it, or when the GPU cannot decode a stream, the player decodes in software.

## Where to get binaries

Download BASS from Un4seen. For FFmpeg, ffmpeg.org links to third-party shared builds; review each build's configuration and licence before you redistribute it. Keep your own lock file with the source URL, exact version, SHA-256, build configuration, licence class and approver for each binary set you ship.
