# Loading media

`Load` and `LoadAsync` open one item on a player: a local file, an archive, an image, a solid colour, or an HTTP/HTTPS stream. This article covers the overloads, what the engine finds next to a file (karaoke graphics, subtitles, backgrounds), how network sources behave, the options that apply on load, and how to cancel a load.

The examples assume you already have a player (`PlayerCore`, or the WPF, Avalonia or WinForms `Player`, which derive from it). See the adapter articles for creating one.

## The basic load

Use `LoadAsync` from UI code. It queues the load on the player's control queue and never blocks the calling thread. `Load` does the same work on the calling thread and blocks until it is done.

```csharp
player.PlayOnLoad = true;

player.MediaLoaded += (_, e) =>
    Console.WriteLine($"{e.Source.DisplayName}: {e.Duration}, content {e.Source.Content}");
player.MediaError += (_, e) =>
    Console.WriteLine($"{e.Code}: {e.Message} ({e.SourcePath})");

bool loaded = await player.LoadAsync(path);
if (!loaded)
    Console.WriteLine("Load failed; see MediaError and MediaDiagnostic.");
```

The result is `true` when the media opened. On `false`, the reason arrives through `MediaError` (a <xref:FatzMedia.Video.MediaErrorCode> such as `FileNotFound`, `UnsupportedFormat`, `OpenFailed` or `NativeLibraryMissing`) and `MediaDiagnostic`. Loading new media while other media is open stops and closes the old item first, so you see `MediaStopped` and `MediaClosed` for the old item before `MediaLoaded` for the new one.

All overloads are members of <xref:FatzMedia.Video.PlayerCore>:

| Overload | Use |
|---|---|
| `Load(path)` / `LoadAsync(path)` | A local file or archive. |
| `Load(path, ignoreSidecar)` | Same, and skip the automatic background search. |
| `Load(path, backgroundPath, ignoreSidecar)` | A file with one explicit background (image or video). |
| `Load(path, IReadOnlyList<string> backgroundPaths, ignoreSidecar)` | A file with several background candidates; one is picked at random. |
| `Load(MediaInputSource, ignoreSidecar)` | Any [input source](#input-sources). |
| `Load(MediaInputSource, MediaInputSource? background, ignoreSidecar)` | An input source with an explicit background source. |
| `LoadAsync(..., CancellationToken)` | Cancellable async loads. See [Cancellation](#cancellation). |

The WPF `Player` adds overloads that take a WPF `ImageSource` (as a main image or as a background); the Avalonia and WinForms players add the same with `Bitmap`.

> [!NOTE]
> Host events (`MediaLoaded`, `MediaClosed`, `MediaError` and the rest) are raised on background threads. Marshal to the UI thread before you touch view state.

## Input sources

<xref:FatzMedia.Video.MediaInputSource> is the framework-neutral description of what to load. Create one with a factory method:

| Factory | Source |
|---|---|
| `FromFile(path)` | A local file or archive. The `string` overloads of `Load` use this. |
| `FromUri(uri, content)` | An absolute `http` or `https` address. Anything else throws `ArgumentException`. |
| `FromBgra32Image(pixels, width, height, stride, name)` | An image in memory (32-bit BGRA). The pixels are copied. |
| `FromOwnedBgra32Image(...)` | Same, but the engine takes the array without copying; do not change it afterwards. |
| `FromColor(argb)` | A solid colour, as `0xAARRGGBB`. Mostly useful as a background. |

```csharp
MediaInputSource logo = MediaInputSource.FromBgra32Image(bgra, width, height, stride: width * 4, "Logo");
MediaInputSource black = MediaInputSource.FromColor(0xFF000000);
MediaInputSource radio = MediaInputSource.FromUri(new Uri("https://example.com/stream.mp3"));

await player.LoadAsync(radio);
await player.LoadAsync(logo);
```

There is no overload that reads media from a `Stream` or a byte array: audio, video and CD+G come from files, archives or network addresses. In-memory input is limited to BGRA images.

## Files

The engine decides what a file is from its extension. The extension sets depend on which native libraries loaded; see [Supported formats](supported-formats.md). Besides plain audio, video and image files, a file can bring partners with it:

| You load | The engine also picks up | Result |
|---|---|---|
| An audio file | A `.cdg` file with the same name in the same folder | Audio with CD+G karaoke graphics (MP3+G). |
| A `.cdg` file | An audio file with the same name | The same pair. Without audio, the graphics play alone. |
| A video file | A `.srt`, `.ass` or `.ssa` file with the same name | Video with a selectable subtitle track. |
| A MIDI or KAR file | The lyrics inside the file | Karaoke lyrics as a subtitle track (see `ShowMidiLyrics`). |

The audio and subtitle pairing is always on. `ignoreSidecar` only affects [backgrounds](#backgrounds).

Embedded text subtitle tracks of a local video are decoded in the background and arrive after `MediaLoaded`. Listen to `SubtitleTracksChanged` to show them.

## Archives

A file with one of the archive extensions (`.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.bz2`, `.xz`, `.tgz`, `.tbz2`) is opened as an archive. The engine reads the entry list and picks what to play, in this order:

1. An audio entry and a `.cdg` entry with the same base name (a karaoke pair).
2. The first audio entry.
3. The first `.cdg` entry (graphics only).
4. The first video entry.

If none of these exist, the load fails with `UnsupportedFormat`. Entries are matched by extension, anywhere in the archive. The entry that is played is extracted to a temporary file first, so opening a large archive takes longer than opening a loose file. Image and video entries that are not the main media become [background candidates](#backgrounds). The source reports `MediaSourceClass.Archive` in its [capabilities](#network-sources).

## Backgrounds

Audio, CD+G and MIDI items have no picture of their own. The engine can put a still image or looping video behind them. Video files and image files never get a background.

**Automatic (sidecar) backgrounds.** When a load starts, the engine looks next to the media file for a video or image with the same base name (`Song.mp3` finds `Song.jpg` or `Song.mp4`). For an archive it also considers every image and video entry inside it. If several match, one is chosen at random. Turn the search off for one load with `ignoreSidecar: true`, or for every load with `player.AutoBackground = false`.

**Explicit backgrounds.** Pass a path or a `MediaInputSource` as the second argument:

```csharp
// One background.
await player.LoadAsync(@"C:\Karaoke\Song.mp3", @"C:\Backgrounds\Stage.mp4");

// Several candidates; one is chosen at random for this load.
await player.LoadAsync(
    @"C:\Karaoke\Song.mp3",
    new[] { @"C:\Backgrounds\a.jpg", @"C:\Backgrounds\b.mp4" });

// No automatic search for this load.
await player.LoadAsync(@"C:\Karaoke\Song.mp3", ignoreSidecar: true);
```

A background found next to the file or inside the archive wins over the explicit one. To force your own background, pass `ignoreSidecar: true` together with it. A background path that does not exist, or has an unknown extension, is skipped without an error. [Backgrounds](../karaoke/backgrounds.md) has the full detection rules and how a background video behaves.

## Network sources

`MediaInputSource.FromUri` opens an HTTP or HTTPS address.

```csharp
await player.LoadAsync(MediaInputSource.FromUri(new Uri("https://example.com/live/index.m3u8"),
    MediaNetworkContent.Video));
```

The `content` argument tells the engine whether the stream is audio or video. With `MediaNetworkContent.Auto` (the default) it looks at the extension of the address path: known video extensions and `.m3u8` playlists count as video, everything else as audio. Pass `Audio` or `Video` when the address has no useful extension.

What you get depends on the stream. Read `player.CurrentSourceCapabilities` (a <xref:FatzMedia.Video.MediaSourceCapabilities>) after loading:

| Stream | Seek | Duration | Loop |
|---|---|---|---|
| File with a known length on a server that accepts byte ranges | Yes | Yes | No |
| File without range support | No | Yes | No |
| Live stream (internet radio, live feed, unknown length) | No | No | No |
| HLS playlist that is complete (video on demand) | Yes | Yes | No |
| HLS playlist that is live | No | No | No |

Network sources never loop through `LoopMedia`; their end is a stop. An HLS video stream with several quality levels plays one fixed level, the best one up to 1080p; there is no adaptive switching.

Network audio and network video open through FFmpeg, which verifies certificates by default (see the TLS policy below).

Two events are specific to buffered sources:

```csharp
player.BufferingChanged += (_, e) => Console.WriteLine($"{e.IsBuffering} {e.BufferFill:P0}");
player.StreamMetadataChanged += (_, e) => Console.WriteLine($"{e.Artist} - {e.Title}");
```

Retry, TLS and read-ahead are set when you create the player, in <xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration>:

```csharp
var configuration = new MediaPlayerConfiguration
{
    NetworkRetry = new NetworkRetryPolicy { MaxRetries = 3, ConnectTimeout = TimeSpan.FromSeconds(10) },
    NetworkTls = new NetworkTlsPolicy { AllowInvalidCertificates = false },
    NetworkReadAhead = new NetworkReadAheadPolicy { MaxDuration = TimeSpan.FromSeconds(30) },
};
```

By default there is one attempt and no retry. See <xref:FatzMedia.Video.NetworkRetryPolicy>, <xref:FatzMedia.Video.NetworkTlsPolicy> and <xref:FatzMedia.Video.NetworkReadAheadPolicy> for the limits.

> [!WARNING]
> `NetworkTlsPolicy.AllowInvalidCertificates` turns off certificate checks for network video and for https and HLS audio. Use it only for servers you control.

## Options that apply on load

| Property | Default | Effect |
|---|---|---|
| `PlayOnLoad` | `false` | Starts playback after a successful load. |
| `ResetPitchOnMediaLoad` | `false` | Sets pitch back to 0 after each successful load. |
| `ResetTempoOnMediaLoad` | `false` | Sets tempo back to 100 after each successful load. |
| `AutoBackground` | `true` | Searches for sidecar and archive backgrounds. |

The reset options can also be set in `MediaPlayerConfiguration`. When a reset changes the value, `PitchChanged` or `TempoChanged` is raised with `MediaChangeReason.LoadReset` before `MediaLoaded`. Without the reset options, pitch and tempo carry over from item to item. See [Volume, pitch and tempo](../audio/volume-pitch-tempo.md).

With `PlayOnLoad` on and normalization enabled, playback waits up to two seconds for the quick normalization pass so the first seconds are not played at the wrong level. See [Normalization](../audio/normalization.md).

## Events

| Event | When |
|---|---|
| `MediaLoaded` | A load succeeded, before any automatic playback. Carries <xref:FatzMedia.Video.MediaSourceInfo> (name, path or address, content flags, capabilities) and the duration. |
| `MediaClosed` | The open item was closed, by `Close()` or because a new load replaced it. |
| `MediaError` | A failure, with a typed code. |
| `MediaDiagnostic` | Informational and warning messages, including why a load was refused. |

`player.Content` and `HasAudio`, `HasVideo`, `HasCdg`, `HasSubtitle` tell you what the open item contains. `player.Close()` closes the current item without loading another.

## Cancellation

Pass a `CancellationToken` to `LoadAsync` to give up on a slow load, typically a network source:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    bool ok = await player.LoadAsync(MediaInputSource.FromUri(uri), cts.Token);
}
catch (OperationCanceledException)
{
    // The task was cancelled; nothing is open.
}
```

What cancellation does depends on when it arrives:

- **Still waiting in the queue:** the load is skipped and nothing changes.
- **During the open:** the open stops where it can. Retry delays end at once; a connection attempt already in progress finishes first, which takes at most `NetworkRetryPolicy.ConnectTimeout`. The previous media is already closed, so the player ends closed. The task is cancelled.
- **After the media loaded:** the load stands and the task returns `true`; only the `PlayOnLoad` start is skipped.

`Reset()` and `Dispose()` cancel loads in progress; those loads return `false` instead of throwing. `Close()` and `Dispose()` also cancel a network video open at once.

The synchronous `Load` methods take no token. They can be cancelled only by `Reset` or `Dispose`.

## Things to watch

- A load can take seconds (network, large archives). Never call the synchronous `Load` on the UI thread; use `LoadAsync`.
- Loading does not start playback unless `PlayOnLoad` is set. Call `PlayAsync()` after `MediaLoaded` otherwise.
- Which extensions load depends on the native libraries found at startup. A missing FFmpeg library, or a FatzAudio that could not start, makes the matching formats fail with `UnsupportedFormat`. See [Supported formats](supported-formats.md).
- Media with audio (audio files, MP3+G, MIDI, network audio) needs the audio engine (FatzAudio). Without it the load fails with `NativeLibraryMissing`, while video (without sound), images and CD+G graphics without audio still load. See [Running without audio](../getting-started/native-dependencies.md#running-without-audio).
- A <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> other than `Full` refuses some kinds of media. See [Player modes](player-modes.md).
