# Native dependencies

FatzMedia.Video needs one native library that you supply: FFmpeg, for video decoding and demuxing. It is not included in the packages. You download it, license it, ship it with your application and tell the engine where to find it. Audio needs nothing from you: the audio engine, FatzAudio, comes with the build. BASS was removed in 037A, so there is no BASS licence and there are no BASS libraries to ship. This page lists the files, the folders the engine searches, and your licensing responsibilities.

## What you need

| Library | Needed for | Required? |
| --- | --- | --- |
| FFmpeg 9.0.x shared libraries | Video, embedded subtitles, network video and network audio, and the fallback decoder for audio formats FatzAudio cannot open | Yes. Player creation throws without it. |
| FatzAudio's native library (`fatzaudio.dll`, `libfatzaudio.so`) | Audio output and every audio feature | Built from `Audio\` in the repository by CMake and copied to the app output by the `FatzMedia.Audio` binding project; you do not supply it. Without it the player starts with no audio (see [Running without audio](#running-without-audio)). |
| A MIDI soundfont | MIDI and KAR playback | Only for MIDI, and only when the system has no General MIDI bank. |
| SkiaSharp's native library | Subtitle and lyric text with the Software and Vulkan renderers | Comes with the packages; nothing to download. |

### Windows (x64)

| Kind | Files |
| --- | --- |
| FFmpeg, all seven required, all in one folder | `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll`, `swscale-10.dll` |

The version numbers in the FFmpeg file names are the library major versions of FFmpeg 9.0.x. FFmpeg 8 libraries do not load. If the folder holds a different version of a library, the startup report says so, for example `Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.`

Use a shared (DLL) build of FFmpeg. A static `ffmpeg.exe` is not enough.

### Linux (x64)

| Kind | Required | Optional |
| --- | --- | --- |
| FFmpeg 9.0.x | `libavutil.so.61`, `libswresample.so.7`, `libswscale.so.10`, `libavcodec.so.63`, `libavformat.so.63` | `libavfilter.so.12`, `libavdevice.so.63` |

Put all FFmpeg libraries in one directory. The engine preloads them in dependency order because typical builds refer to each other by soname without an `$ORIGIN` run path. See [Running on Linux](linux.md).

### SkiaSharp

The Software and Vulkan renderers draw subtitle and lyric text with SkiaSharp (through `FatzMedia.Video.Text`). Unlike FFmpeg it is not optional and you do not supply it: it arrives with the NuGet packages: `libSkiaSharp.dll` (Windows x64) and `libSkiaSharp.so` (Linux x64) are copied to the output by the normal runtime-asset rules (into `runtimes/<rid>/native`, or next to the application in a publish for one runtime). Keep that file when you deploy by hand. On Linux it needs fontconfig and at least one installed font. The Direct3D11 renderer uses DirectWrite and does not load it. SkiaSharp is MIT licensed.

### What the optional pieces change

- FatzAudio opens its own formats directly (see [Supported formats](../playback/supported-formats.md)). When FFmpeg is loaded, other audio formats FFmpeg decodes still open through an FFmpeg fallback that feeds the same FatzAudio playback chain.
- `OpenFileDialogFilter` on the player (<xref:FatzMedia.Video.PlayerCore.OpenFileDialogFilter>) lists what the player accepts, so your Open dialog matches the install.

### MIDI soundfont

MIDI synthesis needs a soundfont. FatzAudio uses, in this order:

1. <xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration.MidiSoundfontPath> when you set it (`.sf2`, `.sf3` or `.dls`).
2. The operating system's General MIDI bank where there is one: `gm.dls` on Windows, the system bank on macOS, an installed `.sf2` on Linux.

There is no folder search for soundfont files. On Linux, set `MidiSoundfontPath` and ship an `.sf2` unless you know the system has one. See [MIDI and KAR](../karaoke/midi-and-kar.md#soundfonts).

## Where the engine looks

The engine searches the folders you pass to the player, then the default folders. A folder you pass must exist, or the player constructor throws `DirectoryNotFoundException`. On Windows the search is recursive, so FFmpeg can sit in a subfolder of one root. On Linux each folder is searched without its subfolders.

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
    @"C:\Program Files\MyHost\media-native\ffmpeg"
];

var player = await FatzMedia.Video.Wpf.Player.CreateAsync(
    MediaPlayerConfiguration.Auto,
    nativePaths,
    new WindowsDirect3D11PlatformFactory());
```

To use only the default folders, pass an empty list or <xref:FatzMedia.Video.WindowsNativeLibraryPaths.GetDefaultSearchPaths>, which returns the default folders that exist.

> [!NOTE]
> Native libraries load once per process. A later player created with the same paths reuses the result. FFmpeg, once loaded, stays loaded.

### The DLLPath settings

<xref:FatzMedia.Video.DLLPath> (`FFmpeg`) is a static setting that is used only where no explicit paths are given: the ready-made `BackgroundSurface` and `KaraokeSurface` controls (unless you set their `NativeLibraryPaths`). A player you create yourself with a path list does not read it.

## Startup report

Read <xref:FatzMedia.Video.PlayerCore.NativeDependencies> (or `NativeDependencies` on the platform factory) to see what was found. It returns a <xref:FatzMedia.Video.NativeDependencyReport> with one <xref:FatzMedia.Video.NativeLibraryCheck> per library: exact file name, required or not, status (`Loaded`, `Missing`, `NotLoaded`, `FailedToLoad`), where it was found, and a hint. `Describe()` returns one readable line per problem, ready to show to a user.

When a required FFmpeg library fails, the player constructor throws, and the exception message includes the hints. The player does not exist then, so read the report from the platform factory you passed in:

```csharp
var platform = new WindowsDirect3D11PlatformFactory();
try
{
    player = await FatzMedia.Video.Wpf.Player.CreateAsync(
        MediaPlayerConfiguration.Auto, WindowsNativeLibraryPaths.GetDefaultSearchPaths(), platform);
}
catch (Exception ex)
{
    var lines = platform.NativeDependencies.Describe();
    // Show ex.Message and lines to the user.
}
```

### Running without audio

When FatzAudio's native library is missing or fails to start, the player still starts in every mode. It raises a Warning in the `Bootstrap` category ("Audio engine FatzAudio could not start ... Audio engine unavailable") and:

- Video plays its pictures on the playback clock without sound. Images and CD+G graphics without audio open normally.
- Media that needs audio (audio files, MP3+G, network audio, MIDI, audio mode) fails to load: `Load` returns false, with an Error diagnostic and a `MediaError` carrying `NativeLibraryMissing`.
- `FatzAudioOutputDevices.Enumerate` throws `DllNotFoundException`.

## Licensing is your responsibility

The packages do not redistribute FFmpeg. You are responsible for obtaining, licensing, validating, deploying and updating them. This guide is not legal advice.

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

For FFmpeg, ffmpeg.org links to third-party shared builds; review each build's configuration and licence before you redistribute it. Keep your own lock file with the source URL, exact version, SHA-256, build configuration, licence class and approver for each binary set you ship.
