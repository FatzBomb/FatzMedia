# Transport: play, pause, stop, seek

The transport controls start, pause, stop and move playback, and the transport events tell your UI what happened. This article covers the control methods, seeking and seek-bar scrubbing, looping, the position and state properties, and the difference between the synchronous and asynchronous methods.

## Controls

| Member | What it does |
|---|---|
| `Play()` / `PlayAsync()` | Starts or resumes playback. |
| `Pause()` / `PauseAsync()` | Pauses at the current position. |
| `Stop()` / `StopAsync()` | Stops and returns the position to the start. The media stays open. |
| `Seek(TimeSpan)`, `Seek(double seconds)`, `SeekAsync(double seconds)` | Moves to a position. |
| `Position` | The current position. Setting it seeks. |
| `Duration` | The length of the open media; zero when unknown (live streams). |
| `LoopMedia` | Restarts the media when it reaches the end. |
| `Close()` | Closes the open media. |

Each control method returns `true` when the engine accepted the request. Controls that fail return `false` rather than throwing; `MediaError` and `MediaDiagnostic` explain why. Calls made after `Dispose()` throw `ObjectDisposedException`, and calls made while `Reset()` runs throw `InvalidOperationException`.

```csharp
await player.LoadAsync(path);
await player.PlayAsync();

await player.SeekAsync(30);   // 30 seconds in
await player.PauseAsync();
await player.StopAsync();     // back to 0, media still open

player.LoopMedia = true;
Console.WriteLine($"{player.Position} / {player.Duration}, state {player.State}");
```

Seek targets past the end are clamped to `Duration`, and negative targets to the start. Check `player.CurrentSourceCapabilities?.IsSeekable` before offering a seek bar for network sources: live streams and servers without range support cannot seek.

## State

`player.State` is a `MediaPlayerState`: `Null` (nothing open), `Stopped`, `Playing`, `Paused` or `Buffering` (a network source waiting for data). `StateChanged` reports every change with the previous and current state.

## Synchronous and asynchronous methods

All control calls run one at a time on the player's control queue, in the order they were issued.

- The `Async` methods only add the call to the queue and return a task. They never wait for other calls. Use them from UI code.
- The synchronous methods (`Play`, `Pause`, `Stop`, `Seek`, `Load`, `Close`, ...) wait for their turn and then run on the calling thread. A long operation ahead of them, such as a network load or seek, blocks the caller. Their events are raised on the calling thread.
- `SeekAsync` coalesces: if a seek is still waiting in the queue, a newer `SeekAsync` replaces its target and both tasks complete with the result of that one seek. You can call it on every mouse move of a seek bar.
- Property setters (`Position` excepted, it seeks synchronously) do not go through the queue.

> [!IMPORTANT]
> Do not call the synchronous methods from the UI thread when the media is a network source. Use `PlayAsync`, `PauseAsync`, `StopAsync`, `LoadAsync` and `SeekAsync`.

A handler running inside a control call (for example a `MediaEnded` handler) may call the synchronous controls; they run at once. `Reset()` and `Dispose()` called from such a handler throw `InvalidOperationException`.

## Seek bars and scrubbing

For a draggable seek bar, wrap the drag in `BeginScrub` and `EndScrub` and send a `SeekAsync` for every move:

```csharp
// pointer pressed on the seek bar (BeginScrub waits for queued calls, so keep it off the UI thread)
await Task.Run(() => player.BeginScrub());

// pointer moved: one request per move; only the newest waiting one is kept
_ = player.SeekAsync(positionSeconds);

// pointer released: send the final seek first, then end the scrub
await player.SeekAsync(positionSeconds);
await Task.Run(() => player.EndScrub());
```

During a scrub, playback carries on and the video shows preview frames instead of waiting for each seek to settle. A seek to the same target as the previous seek of the scrub is ignored. This matters on release: a seek bar normally sends one more seek there, and without the scrub that seek would restart playback from the last drag position, jumping back by the time the button was held. `BeginScrub` returns `false` when no media is open.

`BeginScrub` and `EndScrub` are synchronous and wait for the calls queued before them (a network seek can take seconds), so call them off the UI thread, in order, as the test players do.

Audio during the drag is set with `ScrubAudio`:

| Value | Behaviour |
|---|---|
| `MediaScrubAudio.Audible` (default) | Audio plays from every drag position. |
| `MediaScrubAudio.SilentWhileMoving` | Audio is muted while the drag moves and returns once the drag rests briefly or ends. `Volume` is unchanged. |

`SeekCompleted` is raised when a seek has settled, with the requested and the resulting position. For video and CD+G, the resulting position is the frame actually shown, which can differ slightly from the request.

## Looping

Set `LoopMedia = true` to restart when the media ends. The order of events for each lap is `MediaEnded`, then the restart, then `MediaLooped` (with the position the new lap started at). `LoopMediaChanged` reports the property changing.

- A `MediaEnded` handler can call `Stop()` to cancel the loop restart.
- Network sources never loop; reaching the end is a stop.
- In `Background` mode looping is always on and `LoopMedia = false` is ignored with a diagnostic. See [Player modes](player-modes.md).
- Local audio and video with sound loop by wrapping the stream inside the engine instead of reopening it. When a prepared-next item is waiting, looping takes precedence over the automatic transition. See [Prepared next and transitions](prepared-next-and-transitions.md).

## Events

| Event | Raised when |
|---|---|
| `MediaStarted` | Playback starts or resumes, before the first presented frame. |
| `MediaPaused` | Playback paused; carries the position. |
| `MediaStopped` | `Stop()` was called (or media was replaced while open). Not raised at the natural end. |
| `MediaEnded` | The media reached its end. |
| `MediaLooped` | A loop lap started; carries the start position. |
| `StateChanged` | The state changed (previous and current). |
| `PositionChanged` | About every 250 ms while playing, and immediately after seeks, stops and loops. Carries position and duration. |
| `SeekCompleted` | An accepted seek settled. |

Handlers run on background threads. Marshal to the UI thread:

```csharp
player.PositionChanged += (_, e) => dispatcher.Post(() => UpdateSeekBar(e.Position, e.Duration));
player.StateChanged += (_, e) => dispatcher.Post(() => UpdateButtons(e.CurrentState));
player.MediaEnded += (_, _) => dispatcher.Post(PlayNextInList);
```

An exception thrown by a handler is caught and reported as a `HostEvent` warning diagnostic; other handlers still run.

## Things to watch

- `MediaEnded` is not raised when you stop playback yourself. Use `MediaStopped` for that.
- To go to the next item at the end, handle `MediaEnded` and load the next file, or use the [prepared-next slot](prepared-next-and-transitions.md) for an immediate transition.
- Position and duration of plain audio follow the trimmed window when [silence trimming](../audio/normalization.md#silence-trimming) is on.
