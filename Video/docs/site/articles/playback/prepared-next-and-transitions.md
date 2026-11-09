# Prepared next item and transitions

The player does not own a playlist, but it can hold one item ready: the "prepared next" slot. The engine analyses that item ahead of time (loudness, silence) so that moving to it is quick, and it can start the item by itself when the current one ends, optionally with a fade. This article covers the slot, automatic and manual transitions, fades, and how looping interacts with them.

Your application owns the queue. Typically you call `PrepareNextAsync` for the next entry as soon as the current one starts, then either let the player move on at the end or trigger the transition yourself.

## The slot

The slot holds at most one item. `player.PreparedNext` is a <xref:FatzMedia.Video.PreparedNextInfo> snapshot with a `State`, the `Source` and the `Analysis` (when there is one).

| `PreparedNextState` | Meaning |
|---|---|
| `Empty` | Nothing is prepared. |
| `Analyzing` | The item was accepted and is being analysed. |
| `Ready` | The item can be started at once. |
| `Transitioning` | The item is being loaded as the current media. |
| `Stale` | The file changed on disk after preparation (size or modified time). Prepare it again. |

```csharp
bool ready = await player.PrepareNextAsync(nextPath);   // true when the item reached Ready

player.PreparedNextChanged += (_, e) =>
    Console.WriteLine($"{e.Previous.State} -> {e.Current.State}");
player.PreparedNextTransitioned += (_, e) =>
    Console.WriteLine($"Now playing {e.Source.DisplayName} (automatic: {e.IsAutomatic})");
```

`PrepareNextAsync` accepts a path or a <xref:FatzMedia.Video.MediaInputSource>. It returns `false` when the file does not exist, when the source is a network address, or when the preparation was cleared, replaced or cancelled before it finished. Preparing a new item replaces the old one; `ClearPreparedNext()` empties the slot and cancels any analysis.

What preparation does depends on the item:

- **Plain audio files** are analysed: loudness for [normalization](../audio/normalization.md) and silence at the start and end for [trimming](../audio/normalization.md#silence-trimming). The result is cached, so the later load applies it without scanning again. `PreparedNext.Analysis` returns the <xref:FatzMedia.Video.MediaAnalysisResult>.
- **Audio the FFmpeg fallback plays** is converted ahead of time, so the switch does not wait for the conversion.
- **Video, archives, images, CD+G and tracker modules** are accepted without analysis (`Analysis` is `null`) and load normally at the transition.

You can also analyse a file without preparing it with `AnalyzeAsync(path)`; it warms the same cache and returns `null` for sources that cannot be analysed.

## Transitions

### Manual

`TransitionToPreparedNextAsync()` loads the `Ready` item as the current media and starts it.

```csharp
if (player.PreparedNext.State == PreparedNextState.Ready)
    await player.TransitionToPreparedNextAsync();
```

It returns `false` when no item is `Ready`, the file went `Stale`, or the load failed. The slot is empty afterwards in every case except `Stale`. The transition raises the normal `MediaClosed` and `MediaLoaded` events for the old and new item, then `PreparedNextTransitioned`.

### Automatic

Set `AutoTransitionToPreparedNext = true` and the player starts the prepared item when the current one ends:

```csharp
player.AutoTransitionToPreparedNext = true;
await player.PrepareNextAsync(nextPath);
```

The transition runs after your `MediaEnded` handlers. It is skipped, and the player simply ends as usual, when:

- the slot is not `Ready` at that moment (prepare early enough),
- `LoopMedia` is on,
- `AutoTransitionToPreparedNext` was switched off,
- or your code changed course in between: it paused, stopped, seeked, loaded other media, or replaced or cleared the prepared item.

After the transition the slot is empty. Prepare the following item from your `PreparedNextTransitioned` handler to keep a playlist running.

> [!TIP]
> Call `PrepareNextAsync` as soon as an item starts playing, so the slot is `Ready` well before the end.

The transition is a normal load and play. It does not splice the two items sample-accurately. Expect a short pause between songs unless you use fades.

## Fades

Fades change the loudness of the sound only; video frames are not affected. They work for local audio and for the soundtrack of video files, and they are a separate gain stage that multiplies with volume and normalization, so they do not disturb either.

### Fading on demand

```csharp
await player.FadeOutAsync(TimeSpan.FromSeconds(2));   // full to silent, playback continues
await player.StopAsync();

await player.PlayAsync();
await player.FadeInAsync(TimeSpan.FromSeconds(2));    // silent to full
```

`FadeOutAsync` and `FadeInAsync` return `true` when the whole fade ran. They return `false` when the media has no audio, the duration is zero, or the fade was cancelled or superseded. Starting a new fade replaces the running one; `CancelFade()` stops it and leaves the gain where it reached. `Stop`, a new load, `Close` and the natural end restore full gain. The duration is limited to 60 seconds (`AudioCrossfadeOptions.MaximumDuration`).

A fade does not stop playback. After `FadeOutAsync` you decide whether to pause, stop or load something else.

### Fading on transitions

`PreparedNextTransitionFade` applies a fade to prepared-next transitions. It is off by default.

```csharp
player.PreparedNextTransitionFade = new AudioCrossfadeOptions
{
    Duration = TimeSpan.FromSeconds(3),
};
```

With the fade enabled:

- **Manual transition:** the current item fades out over `Duration`. Then the prepared item loads with its fade gain already at the start of its envelope, so the first samples are not at full volume, and it fades in over `Duration`.
- **Automatic transition:** the old item has already ended, so only the incoming fade applies.

> [!NOTE]
> Despite the type name, the two items never overlap. The outgoing item fades out completely before the incoming item starts. If the outgoing fade is interrupted (stop, `CancelFade`, a newer fade, a pause, seek or load), the transition is abandoned. If the load fails after the outgoing item faded out, its volume is restored so it does not keep playing silently.

### Envelopes

Each fade follows an <xref:FatzMedia.Video.AudioFadeEnvelope>: a list of <xref:FatzMedia.Video.AudioFadePoint> values (position `0..1` across the fade, gain `0..1`) with linear interpolation. The defaults are straight ramps: `AudioFadeEnvelope.Outgoing` (full to silent) and `AudioFadeEnvelope.Incoming` (silent to full). Envelopes are immutable; `WithPoint`, `WithPoints`, `CreateOutgoing` and `CreateIncoming` return new ones with points added over the defaults. A point at an existing position replaces it.

```csharp
// The new song stays quiet for the first half of the fade, then comes up.
AudioFadeEnvelope slowStart = AudioFadeEnvelope.CreateIncoming(
[
    new AudioFadePoint(0.5, 0.2),
]);

player.PreparedNextTransitionFade = new AudioCrossfadeOptions
{
    Duration = TimeSpan.FromSeconds(4),
    Outgoing = AudioFadeEnvelope.Outgoing,
    Incoming = slowStart,
};

await player.FadeInAsync(TimeSpan.FromSeconds(2), slowStart);   // envelopes also work for on-demand fades
```

If an incoming envelope starts with a silent stretch, `AudibleStart` is the position where it becomes audible. The incoming item is held back for `Duration × AudibleStart` before it loads, so silence does not consume the start of the song.

## Looping and gapless behaviour

Looping and the prepared-next slot are separate. With `LoopMedia = true` the current item repeats and the automatic transition never fires. Local audio and video with sound loop by wrapping the stream inside the engine instead of reopening it; see [Transport](transport.md#looping). A host that wants "loop a playlist" turns `LoopMedia` off and prepares the first item again at the end of the list.

## Things to watch

- Only one item is prepared at a time. Preparing a second replaces the first.
- A prepared file that changes before the transition becomes `Stale`; the transition returns `false`. Prepare it again.
- Events from the slot arrive on background threads; marshal to the UI thread.
- A mode switch keeps the prepared item (see [Player modes](player-modes.md)); `Reset()` discards it, because its analysis belongs to the old engine.
