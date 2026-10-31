# Threading and events

The engine runs its work on its own threads, so most events do not arrive on your UI thread. This page tells you which calls block, which return immediately, which thread each event arrives on, and how to marshal safely. Read it before you wire a player to a UI.

## The control queue

Each player has a control queue. Load, transport, seek, close and mode-switch calls run one at a time on it, in the order they were made. That gives you ordering for free: `LoadAsync` followed by `PlayAsync` plays the file that was loaded.

There are two ways to make a control call:

| Style | Examples | Behaviour |
| --- | --- | --- |
| Asynchronous | `LoadAsync`, `PlayAsync`, `PauseAsync`, `StopAsync`, `SeekAsync`, `SetModeAsync`, `PrepareNextAsync`, `TransitionToPreparedNextAsync` | Queues the operation and returns a task. Never waits for another call. Safe on the UI thread. |
| Synchronous | `Load`, `Play`, `Pause`, `Stop`, `Seek`, `Close`, `BeginScrub`, `EndScrub` | Waits for its turn, then runs on your thread. Blocks for as long as the queue is busy and the operation takes. Not safe on the UI thread when media may be slow to open. |

> [!IMPORTANT]
> In UI code, use the async methods. A synchronous `Load` of a network stream, or a `Pause` queued behind a slow load, freezes the window until it finishes.

## What blocks

| Call | Blocks? |
| --- | --- |
| `new Player(...)` / `new MediaControlPlayer(...)` | Yes: loads the native libraries. Use `Player.CreateAsync` in UI code. |
| `Dispose()` | Yes: cancels loads, waits up to about 2 s for queued operations, then frees the engine. Run it off the UI thread. |
| `Reset(configuration, paths)` | Yes: waits up to 5 s; returns `false` (nothing changed) if the player was too busy. |
| `Load`, `Play`, `Pause`, `Stop`, `Seek`, `Close`, `BeginScrub`, `EndScrub` | Yes, until their turn on the queue and while they run. |
| `LoadAsync`, `PlayAsync`, `PauseAsync`, `StopAsync`, `SeekAsync`, `SetModeAsync`, prepared-next calls | No. |
| Property getters and setters, sync-offset members, `SelectSubtitleTrack`, `ShowMidiLyrics` | No: they act on the current engine at once and are not queued. |
| `AttachPresentationSurface`, `ResizePresentationSurface`, `DetachPresentationSurface` | No: they are not queued. Adapters call them on the UI thread. |

Two consequences of "not queued":

- A property you set while a `Reset` or mode switch is rebuilding the engine may be lost. Set it again after the rebuild finished (after the call returned or the event arrived).
- A property read may show a value that a queued operation is about to change. Use events if you need the result of an operation.

### Calls from inside an event handler

A handler raised during a control operation counts as part of that operation. Synchronous calls from it run at once (they would otherwise deadlock). `Reset` and `Dispose` throw `InvalidOperationException` there, because they would wait for themselves. Hand the work to the UI thread (`Dispatcher.BeginInvoke`, `BeginInvoke`, `Dispatcher.UIThread.Post`) and call them from there.

## Events fire on background threads

None of the player events is raised on your UI thread. Handlers must marshal before touching UI state.

```csharp
player.MediaEnded += (_, _) => Dispatcher.BeginInvoke(() => NextTrack());                   // WPF
player.MediaEnded += (_, _) => BeginInvoke(() => NextTrack());                              // WinForms, inside a Form
player.MediaEnded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => NextTrack()); // Avalonia
```

A handler that throws does not stop the other handlers or the engine. The engine reports the exception as a `MediaDiagnostic` Warning in the `HostEvent` category. A throwing `MediaDiagnostic` handler is not reported that way (it would raise itself again).

Keep handlers short. Most events run on the control queue, so a slow handler delays every call queued behind it.

## The events

The thread column says where the handler runs. "Control queue" means the player's single control thread, in order with your control calls. "Operation thread" means the thread that ran the operation: for a synchronous call, your thread; for an asynchronous one, the control queue.

| Event | Arguments | Fires when | Thread |
| --- | --- | --- | --- |
| `MediaLoaded` | `MediaLoadedEventArgs` (`Source`, `Duration`) | A load succeeded, before `PlayOnLoad` starts playback. | Operation thread |
| `MediaClosed` | `MediaClosedEventArgs` (`Source`) | The media was closed or replaced by a new load. | Operation thread |
| `MediaStarted` | none | Playback started or resumed, before the first presented frame. | Operation thread |
| `MediaPaused` | `MediaPausedEventArgs` (`Position`) | Playback paused. | Operation thread |
| `MediaStopped` | none | Playback was stopped. | Control queue |
| `MediaEnded` | none | The media reached its natural end. | Control queue |
| `MediaLooped` | `MediaLoopedEventArgs` (`Position`) | Loop playback began its next pass. | Control queue |
| `StateChanged` | `MediaStateChangedEventArgs` (`PreviousState`, `CurrentState`) | The playback state changed. | Control queue |
| `PositionChanged` | `MediaPositionChangedEventArgs` (`Position`, `Duration`) | About every 250 ms while playing, and right after loads, seeks, loops and stops. Not raised while paused. | A background thread (not the control queue) |
| `SeekCompleted` | `MediaSeekCompletedEventArgs` (`RequestedPosition`, `ResultingPosition`) | A seek settled on its resulting position (for video and CD+G, when the new frame is ready). | Control queue |
| `BufferingChanged` | `MediaBufferingChangedEventArgs` (`IsBuffering`, `BufferFill`) | A network source started or stopped buffering, or its fill moved. | Control queue |
| `StreamMetadataChanged` | `MediaStreamMetadataChangedEventArgs` (`Title`, `Artist`, `RawTitle`) | A network stream delivered new in-stream metadata (internet radio titles). | Control queue |
| `SubtitleTracksChanged` | `SubtitleTracksChangedEventArgs` | Embedded text subtitle tracks of a local video finished decoding in the background and arrived after `MediaLoaded`. | Control queue |
| `PreparedNextChanged` | `PreparedNextChangedEventArgs` (`Previous`, `Current`) | The prepared-next slot changed state. | Control queue |
| `PreparedNextTransitioned` | `PreparedNextTransitionedEventArgs` | A prepared item became the current media. | Control queue |
| `ModeChanged` | `MediaValueChangedEventArgs<MediaPlayerMode>` | `SetModeAsync` switched to another mode. | Control queue |
| `HasPresentationChanged` | `MediaValueChangedEventArgs<bool>` | A mode switch turned presentation on or off (Audio mode). | Control queue |
| `VolumeChanged` | `MediaValueChangedEventArgs<double>` | The volume changed. | The thread that changed it |
| `PitchChanged`, `TempoChanged` | `MediaValueChangedEventArgs<int>` | Pitch or tempo changed. | The thread that changed it |
| `NormalizeChanged` | `MediaNormalizeChangedEventArgs` | Normalization was switched on or off. | The thread that changed it |
| `EqualizerChanged` | `MediaValueChangedEventArgs<AudioEqualizerSettings>` | The equalizer settings changed. | The thread that changed it |
| `LoopMediaChanged` | `MediaValueChangedEventArgs<bool>` | Looping was switched on or off. | The thread that changed it |
| `MediaError` | `MediaErrorEventArgs` (`Code`, `Message`, `SourcePath`, `InnerException`) | A high-value engine failure, such as a file that could not be opened. | Control queue |
| `MediaDiagnostic` | `MediaDiagnosticEventArgs` | The engine produced a diagnostic. See [Diagnostics](diagnostics.md). | The player's own diagnostic thread, in order |
| `AudioDeviceChanged` | `AudioDeviceChangedEventArgs` | The audio output was lost, the default device changed, or output came back. | Control queue |
| `PresentationFramePresented` | none | The renderer presented a frame to an attached surface. | The render thread: keep the handler tiny |

A few details that matter in practice:

- **Value-changed events after `Reset`.** A `Reset` returns the runtime settings to their defaults. Each one that changed raises its event once, with `Reason` set to `MediaChangeReason.PlayerReset`, on the control queue after `Reset` returns. A new load can also reset pitch and tempo when `ResetPitchOnMediaLoad` and `ResetTempoOnMediaLoad` are on (`Reason` is `LoadReset`).
- **`PositionChanged` is for display.** It is throttled. For a seek bar, update the slider from it, and use `SeekCompleted` to know when a seek landed.
- **`MediaEnded` and looping.** With looping on, the media does not end; `MediaLooped` fires for each pass.
- **`MediaDiagnostic` is its own channel.** It does not wait behind long operations, so a diagnostic raised during a slow network connect reaches your handler while the connect runs. Delivery stops when the player is disposed (the player drains queued diagnostics for up to a second first).
- **After a load.** `MediaLoaded` is raised before `PlayOnLoad` starts playback. `SubtitleTracksChanged` and `StreamMetadataChanged` arrive after `MediaLoaded`, never before it.

## Patterns

### Marshal once

Keep UI updates in one method that always runs on the UI thread, and call it from handlers:

```csharp
private void OnStateChanged(object? sender, MediaStateChangedEventArgs e) =>
    Dispatcher.BeginInvoke(() => StateText.Text = e.CurrentState.ToString());
```

### A seek bar

```csharp
// UI thread: user drags the slider.
await player.SeekAsync(slider.Value);        // newer requests replace one still waiting

// Handler (background thread): move the slider while playing.
player.PositionChanged += (_, e) =>
    Dispatcher.BeginInvoke(() => slider.Value = e.Position.TotalSeconds);
```

Use `BeginScrub()` and `EndScrub()` around a drag so the engine knows it is a scrub, and see [Player and session](player-and-session.md).

### Shutdown

```csharp
surface.Player = null;
await Task.Run(player.Dispose);
```

Do not call `Dispose` from a `MediaEnded`, `MediaStopped` or similar handler directly; post to the UI thread first.
