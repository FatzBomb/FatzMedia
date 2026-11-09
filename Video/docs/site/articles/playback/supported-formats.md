# Supported formats

What the player can open is decided by the native libraries your application deploys. Audio containers and MIDI come from BASS and its plugins, video and images come from FFmpeg, and FFmpeg also decodes audio formats that a missing BASS plugin would have played. This article lists the formats by kind, says which library each one needs, and shows how to ask a running player what it accepts.

The library ships no native binaries. You deploy BASS and FFmpeg yourself; see [Native dependencies](../getting-started/native-dependencies.md) for versions, folders and licensing.

> [!NOTE]
> The tables describe what the player registers when the libraries load. They are not a guarantee that every file with such an extension plays: the codec inside the container also has to be supported by the library.

## Audio

Plain audio plays through BASS. The extensions the player accepts depend on which BASS libraries loaded:

| Library | Extensions |
|---|---|
| `bass.dll` + `bass_fx.dll` (required for any audio) | `.aif` `.aiff` `.mp3` `.mp3pro` `.ogg` `.wav` |
| Tracker modules (also in `bass.dll`) | `.mod` `.it` `.s3m` `.xm` `.mo3` `.mtm` `.umx` |
| `bass_aac` | `.aac` `.m4a` `.m4b` |
| `bass_ac3` | `.ac3` |
| `bassalac` (Windows) | `.aac` `.m4a` |
| `basscd` (Windows) | `.cda` (playing from a disc is not covered by this guide) |
| `bassflac` | `.flac` |
| `bassopus` | `.opus` |
| `basswma` (Windows) | `.wma` |
| `basswv` (Linux) | `.wv` |
| `bassmidi` | `.mid` `.midi` `.kar` `.rmi` |

When FFmpeg is loaded, it also decodes a fixed set of audio extensions, and the file plays through the same BASS output chain: `.aac` `.ac3` `.aif` `.aifc` `.aiff` `.alac` `.amr` `.ape` `.au` `.caf` `.dff` `.dsf` `.dts` `.eac3` `.flac` `.m4a` `.m4b` `.m4r` `.mka` `.mp2` `.mp3` `.mpc` `.oga` `.ogg` `.opus` `.spx` `.tak` `.tta` `.w64` `.wav` `.wma` `.wv`. A missing plugin therefore does not make a format unplayable as long as FFmpeg is present. BASS is still needed for the sound output.

Notes:

- Without the BASS core libraries the player still starts. Video plays without sound, and images and CD+G graphics without audio still load. Audio files, MP3+G and network audio fail to load with `MediaErrorCode.NativeLibraryMissing`.
- MIDI files need the `bassmidi` plugin; FFmpeg does not play them. Lyrics inside MIDI and KAR files appear as a subtitle track. A soundfont can be set with `MediaPlayerConfiguration.MidiSoundfontPath`; otherwise the engine looks for one (see [MIDI soundfont](../getting-started/native-dependencies.md#midi-soundfont)).
- Audio inside archives is supported. See [Loading media](loading-media.md#archives).

## Video

All video decoding goes through FFmpeg. The player registers these container extensions once FFmpeg loads:

`.3g2` `.3gp` `.asf` `.avi` `.f4v` `.flv` `.m2ts` `.m2v` `.m4v` `.mkv` `.mov` `.mp4` `.mpeg` `.mpg` `.mts` `.mxf` `.ogm` `.ogv` `.qt` `.rm` `.rmvb` `.ts` `.vob` `.webm` `.wmv`

Which codecs inside those containers play is up to the FFmpeg build you ship. Hardware decoding is used when available (Direct3D 11/12 and DXVA2 on Windows) and falls back to software; set `MediaPlayerConfiguration.VideoDecoding` to `Auto` (the default), `Hardware` or `Software`. HDR10 and HLG video is tone mapped to SDR by default (`MediaPlayerConfiguration.HdrToneMapping`).

Video plays its soundtrack through BASS. Without BASS it plays silently.

A local file in a video container with no video stream, such as an audio-only `.mp4` or `.ts` (cover art does not count as video), plays as audio, the way [Audio mode](player-modes.md) plays a video's soundtrack. The log says so with an Info line.

## Subtitles

| Source | Formats |
|---|---|
| Sidecar file next to a video, same base name | `.srt` `.ass` `.ssa` |
| Embedded in the video | Text subtitle tracks |
| MIDI and KAR files | The lyrics inside the file |

Bitmap subtitle formats (DVD, PGS) are not supported. Embedded tracks are decoded after `MediaLoaded`; `SubtitleTracksChanged` tells you when they are ready. `player.SubtitleTracks` lists the tracks and `SelectSubtitleTrack(id)` picks one (`null` turns subtitles off).

## Images

`.bmp` `.gif` `.jpg` `.jpeg` `.png` `.tif` `.tiff`, decoded with FFmpeg. An animated GIF shows its first frame. Images can also be supplied from memory as BGRA pixels; see [Loading media](loading-media.md#input-sources).

## CD+G karaoke

A `.cdg` file holds the graphics of a karaoke track. It is played together with an audio file of the same base name (MP3+G is the common case), or alone. Archives that contain such a pair play as a karaoke item.

## Archives

`.zip` `.7z` `.rar` `.tar` `.gz` `.bz2` `.xz` `.tgz` `.tbz2`. The engine plays the first suitable entry: a karaoke pair, then audio, then CD+G, then video. See [Loading media](loading-media.md#archives).

## Network

HTTP and HTTPS streams: audio over BASS (plain `http`) or FFmpeg (`https` and HLS), video over FFmpeg. HLS (`.m3u8`) playlists are recognised by their extension. See [Loading media](loading-media.md#network-sources).

## Asking the player what it accepts

### File dialog filter

`player.OpenFileDialogFilter` returns a filter string for a Win32, WinForms or WPF open file dialog, built from the extensions actually registered: "Supported media", then one group each for audio, video, archives, images and CD+G, and "All files".

### Extension checks

The static class <xref:FatzMedia.Video.Core.Files.FileTypes> holds the registered sets. They are filled when the first player loads the native libraries, so read them after creating a player.

```csharp
string filter = player.OpenFileDialogFilter;

bool playableAudio = FileTypes.IsPlayableAudio(path);  // BASS or the FFmpeg fallback
bool video   = FileTypes.IsVideo(path);
bool archive = FileTypes.IsArchive(path);
bool image   = FileTypes.IsImage(path);
bool cdg     = FileTypes.IsCdg(path);
bool sub     = FileTypes.IsSubtitle(path);

// Every audio extension in play
string[] audioExtensions = [.. FileTypes.Audio.Concat(FileTypes.FfmpegAudio)];
```

`FileTypes.Audio` holds the extensions BASS plays, `FileTypes.FfmpegAudio` the FFmpeg-only ones.

### Video decoder capabilities

`player.VideoDecodeCapabilities` is a snapshot of the codecs and containers the FFmpeg build supports, and whether each can be decoded in hardware.

```csharp
VideoDecodeCapabilities caps = player.VideoDecodeCapabilities;

foreach (string codec in caps.HardwareAcceleratedCodecNames)
    Console.WriteLine($"Hardware: {codec}");

CodecCapability? h264 = caps.ForCodec("h264");
Console.WriteLine($"{h264?.FriendlyName}: hardware method {h264?.HardwareMethod}");

bool hevcInHardware = caps.IsHardwareDecodeAvailable("hevc");
ContainerCapability? mkv = caps.ForExtension(".mkv");
```

<xref:FatzMedia.Video.VideoDecodeCapabilities> also offers `SoftwareOnlyCodecNames`, `Codecs` and `Containers`. Each <xref:FatzMedia.Video.CodecCapability> says whether software and hardware decoding are available and by which method.

### Which native libraries loaded

```csharp
NativeDependencyReport report = player.NativeDependencies;
foreach (string line in report.Describe())
    Console.WriteLine(line);
if (!report.IsAudioAvailable)
    Console.WriteLine("BASS is missing: audio will not play.");
```

<xref:FatzMedia.Video.NativeDependencyReport> lists every library with its status and the folder it came from. Show the `Describe()` lines in your about box or support log; it is the quickest way to diagnose a customer machine where "MP3 does not play".

## Things to watch

- Format support differs between machines because it depends on the DLLs deployed next to the application. Build your file picker from `OpenFileDialogFilter` instead of a hard-coded list.
- Extension matching is case-insensitive and decides how a file is treated. A mis-named file fails with `UnsupportedFormat` or `OpenFailed`.
- A <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> other than `Full` accepts only some kinds. See [Player modes](player-modes.md).
