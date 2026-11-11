# Supported formats

What the player can open is decided by the audio engine and the native libraries your application deploys. Audio containers, trackers and MIDI come from FatzAudio, video and images come from FFmpeg, and FFmpeg also decodes audio formats FatzAudio cannot open. This article lists the formats by kind, says which library each one needs, and shows how to ask a running player what it accepts.

The library ships no FFmpeg binaries. You deploy FFmpeg yourself; see [Native dependencies](../getting-started/native-dependencies.md) for versions, folders and licensing. FatzAudio's library is built with the repository and copied for you. BASS was removed in 037A.

> [!NOTE]
> The tables describe what the player registers when the libraries load. They are not a guarantee that every file with such an extension plays: the codec inside the container also has to be supported by the library.

## Audio

Plain audio plays through FatzAudio. It opens these formats itself (the list comes from `FatzMedia.Audio.AudioEngine.SupportedFormats`):

| Kind | Formats |
|---|---|
| PCM and lossless containers | WAV, AIFF, RF64, W64, FLAC, WavPack, APE, DSF and DFF (DSD), AU |
| MPEG and AAC | MP3, MP2, MP1, AAC and ADTS |
| Containers | MP4, M4A, M4B, CAF, MKA, WebM and Ogg, carrying Vorbis, Opus or FLAC |
| Surround | AC-3, E-AC-3, DTS (through the system decoder) |
| Windows Media | WMA and ASF |
| MIDI | MIDI, KAR and RMI, played with a soundfont (`MidiSoundfontPath` or the system bank) |
| Trackers | MOD, XM, IT, S3M, MO3, MTM, UMX and more |
| Audio CD | CD audio tracks (`.cda`); playing from a disc is not covered by this guide |

When FFmpeg is loaded, it also decodes a fixed set of audio extensions that FatzAudio does not open directly, and the file plays through the same FatzAudio output chain (`FileTypes.FfmpegAudio`). So a format FatzAudio lacks does not make a file unplayable as long as FFmpeg is present. FatzAudio is still needed for the sound output.

Notes:

- If FatzAudio cannot start, the player still starts. Video plays without sound, and images and CD+G graphics without audio still load. Audio files, MP3+G and network audio fail to load with `MediaErrorCode.NativeLibraryMissing`.
- MIDI files play only through FatzAudio; FFmpeg does not play them. Lyrics inside MIDI and KAR files appear as a subtitle track. A soundfont can be set with `MediaPlayerConfiguration.MidiSoundfontPath`; otherwise the system bank is used (see [MIDI soundfont](../getting-started/native-dependencies.md#midi-soundfont)).
- Audio inside archives is supported. See [Loading media](loading-media.md#archives).

## Video

All video decoding goes through FFmpeg. The player registers these container extensions once FFmpeg loads:

`.3g2` `.3gp` `.asf` `.avi` `.f4v` `.flv` `.m2ts` `.m2v` `.m4v` `.mkv` `.mov` `.mp4` `.mpeg` `.mpg` `.mts` `.mxf` `.ogm` `.ogv` `.qt` `.rm` `.rmvb` `.ts` `.vob` `.webm` `.wmv`

Which codecs inside those containers play is up to the FFmpeg build you ship. Hardware decoding is used when available (Direct3D 11/12 and DXVA2 on Windows) and falls back to software; set `MediaPlayerConfiguration.VideoDecoding` to `Auto` (the default), `Hardware` or `Software`. HDR10 and HLG video is tone mapped to SDR by default (`MediaPlayerConfiguration.HdrToneMapping`).

Video plays its soundtrack through FatzAudio. If FatzAudio cannot start, it plays silently.

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

HTTP and HTTPS streams: audio and video both open through FFmpeg. HLS (`.m3u8`) playlists are recognised by their extension. See [Loading media](loading-media.md#network-sources).

## Asking the player what it accepts

### File dialog filter

`player.OpenFileDialogFilter` returns a filter string for a Win32, WinForms or WPF open file dialog, built from the extensions actually registered: "Supported media", then one group each for audio, video, archives, images and CD+G, and "All files".

### Extension checks

The static class <xref:FatzMedia.Video.Core.Files.FileTypes> holds the registered sets. They are filled when the first player starts its audio engine and loads FFmpeg, so read them after creating a player.

```csharp
string filter = player.OpenFileDialogFilter;

bool playableAudio = FileTypes.IsPlayableAudio(path);  // FatzAudio or the FFmpeg fallback
bool video   = FileTypes.IsVideo(path);
bool archive = FileTypes.IsArchive(path);
bool image   = FileTypes.IsImage(path);
bool cdg     = FileTypes.IsCdg(path);
bool sub     = FileTypes.IsSubtitle(path);

// Every audio extension in play
string[] audioExtensions = [.. FileTypes.Audio.Concat(FileTypes.FfmpegAudio)];
```

`FileTypes.Audio` holds the extensions FatzAudio plays, `FileTypes.FfmpegAudio` the FFmpeg-only ones.

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
```

<xref:FatzMedia.Video.NativeDependencyReport> lists every library with its status and the folder it came from. Show the `Describe()` lines in your about box or support log; it is the quickest way to diagnose a customer machine where "MP3 does not play".

## Things to watch

- Format support differs between machines because it depends on the libraries deployed next to the application. Build your file picker from `OpenFileDialogFilter` instead of a hard-coded list.
- Extension matching is case-insensitive and decides how a file is treated. A mis-named file fails with `UnsupportedFormat` or `OpenFailed`.
- A <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> other than `Full` accepts only some kinds. See [Player modes](player-modes.md).
