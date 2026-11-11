# Normalization and silence trimming

Normalization evens out loudness between files so the user does not reach for the volume control at every track change. FatzAudio measures each song's integrated loudness (ITU-R BS.1770 / EBU R128) and brings it to a target, with a true-peak limiter so a boost never clips. This article explains the switch, the options, how to read the result, and how the optional silence trimming and the standalone analysis fit in. The old peak-based rule went away with BASS in 037A.

## Switching it on and off

`player.Normalize` is `true` by default.

```csharp
player.Normalize = false;   // play files at their original level

player.NormalizeChanged += (_, e) =>
    dispatcher.Post(() => Console.WriteLine(
        $"Normalize {e.PreviousValue} -> {e.CurrentValue}, current gain x{e.NormalizationGain:F2}"));
```

Turning normalization off stops the gain being applied, so turning it back on is immediate and needs no new scan.

## How the gain is found

`player.Effects.NormalizationGain` returns the current gain as a plain multiplier, with 1.0 meaning no change. `player.Effects.NormalizationGainChanged` fires once the loudness is final, and `NormalizeChanged` carries the value too.

```csharp
player.Effects.NormalizationGainChanged += (_, gain) =>
    Console.WriteLine($"Normalization gain x{gain:F2}");
```

> [!NOTE]
> The XML documentation of `NormalizationGain` calls the value dB. The value is a linear multiplier.

## Options

```csharp
var configuration = new MediaPlayerConfiguration
{
    Normalization = new NormalizationOptions { TargetLufs = -16 },   // default -14
};
```

| Option | Default | Meaning |
|---|---|---|
| `TargetLufs` | -14 | The loudness every song is brought to (Spotify, YouTube). A karaoke host might use about -16 for microphone headroom. -60 to 0. |
| `TruePeakCeilingDbtp` | -1 | The limiter's ceiling. -20 to 0. |
| `CacheDirectory` | `null` | Where measured loudness is kept. `null` is a per-user folder (`%LOCALAPPDATA%\FatzMedia.Video\AudioCache` on Windows, `$XDG_CACHE_HOME/fatzmedia-video/audio` or `~/.cache/fatzmedia-video/audio` elsewhere); an empty string turns the cache off. |

- **Where the loudness comes from:** ReplayGain 2 or R128 tags, then the cache, then a scan of the whole file on all cores (a 4-minute song takes about 0.1-0.35 s, a 10-minute one about 0.5 s). The gain is target minus loudness, limited to -30 to +12 dB.
- **The first play:** playback starts at once on FatzAudio's running estimate and glides to the measured level when the scan finishes (at most 12 dB/s down, 6 dB/s up). Tagged and cached songs are at the right level from the first sample. Play-on-load does not wait.
- **The cache:** one tiny file per song, keyed by its content (size and three 64 KB blocks), so a renamed or moved file is still found and a replaced file is measured again. Entries are never deleted by the engine.
- **Reading it:** `NormalizationGain` is FatzAudio's gain for the song as a multiplier (with `Normalize` off, the gain it would apply); `NormalizationGainChanged` fires once the loudness is final.
- **`Normalize = false`:** FatzAudio applies no gain and its limiter is off; turning it back on glides to the gain.

## What it applies to

| Source | Normalized |
|---|---|
| Local audio files | Yes |
| Audio inside archives | Yes |
| Soundtracks of video files | Yes |
| Network streams | No: unity gain, no analysis |
| Images, CD+G without audio | Nothing to normalize |

Normalization is a separate gain stage. It multiplies with `Volume` and with fades, and none of them changes the others.

## Silence trimming

Silence trimming skips silence at the start and end of a file. It is off by default and is set when you create the player:

```csharp
var configuration = new MediaPlayerConfiguration
{
    SilenceTrim = new SilenceTrimOptions
    {
        Enabled = true,
        ThresholdDecibels = -50,                         // at or below this is silent
        MinimumSilenceDuration = TimeSpan.FromSeconds(0.5), // shorter edge silence is kept
        Padding = TimeSpan.FromSeconds(0.1),              // silence left at each trimmed edge
        TrimLeading = true,
        TrimTrailing = true,
    },
};
```

<xref:FatzMedia.Video.SilenceTrimOptions> accepts a threshold from -96 to 0 dBFS and durations from 0 to 30 seconds. The values above are the defaults, except that `Enabled` is `false` by default.

When trimming is active for a file:

- Playback starts at the end of the leading silence and ends at the start of the trailing silence, each with `Padding` left in.
- `Position` and `Duration` are in trimmed time: position 0 is the first audible moment and `Duration` is the playable length.
- Only silence at the edges is trimmed. Quiet passages in the middle are not touched.

Trimming applies to plain audio files only. It is not applied to media that is synchronized with something else (audio with CD+G, video soundtracks), to archive entries, to tracker modules or to network streams.

> [!IMPORTANT]
> A file is trimmed when its analysis is already available at load time. The first time a file plays without earlier analysis, the scan runs in the background and the file plays in full; the result is cached for the next load. To have the very first play trimmed, analyse the file ahead of time with `AnalyzeAsync`, or prepare it with `PrepareNextAsync`, as described below.

## Analysing without playing

`AnalyzeAsync` analyses a local plain-audio file on its own stream, without touching the current media, and stores the result in the caches:

```csharp
MediaAnalysisResult? analysis = await player.AnalyzeAsync(path);
if (analysis is not null)
{
    Console.WriteLine($"Source {analysis.SourceDuration:F1}s, playable {analysis.PlayableStart:F1}s to {analysis.PlayableEnd:F1}s");
    Console.WriteLine($"Effective duration {analysis.EffectiveDuration:F1}s, gain x{analysis.NormalizationGain:F2}");
    foreach (SilenceRegion region in analysis.SilenceRegions)
        Console.WriteLine($"Silence {region.StartSeconds:F1}s to {region.EndSeconds:F1}s");
}
```

<xref:FatzMedia.Video.MediaAnalysisResult> carries the source and effective durations, the playable window, every detected silence region and the normalization gain. The method returns `null` for sources it cannot analyse: video, archives, images, CD+G, tracker modules, network sources and missing files. Use it for a library scan or to fill a progress bar with the trimmed length. `PrepareNextAsync` runs the same analysis for the prepared item. See [Prepared next item and transitions](../playback/prepared-next-and-transitions.md).

## Things to watch

- The analysis reads the whole file once. For long files on slow media the scan takes noticeable time; playback does not wait for it.
- Loudness is measured over the whole song, so a short loud passage does not pull the level of the rest of a song down; the limiter keeps peaks under the ceiling.
- A file that changes on disk gets a new cache entry, because size and content are part of the key.
