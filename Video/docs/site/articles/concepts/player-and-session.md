# Player and session

Everything you do with the engine goes through one object: a player. This page explains the types involved (`MediaControlPlayer`, the adapters' `Player` classes and the `IMediaControlSession` interface), how to create a player, what its configuration controls, how long it lives and how to dispose of it, and what state it exposes.

## The types

| Type | What it is |
| --- | --- |
| <xref:FatzBomb.MediaControl.IMediaControlSession> | The framework-neutral control surface: load, transport, seek, state, effects, backgrounds, prepared-next, events and presentation-surface calls. Adapter controls and your own code can depend on this interface instead of a concrete class, which also makes it easy to substitute a test double. |
| <xref:FatzBomb.MediaControl.MediaControlPlayer> | The engine's implementation of the session. It owns playback, composition, target fan-out and the control queue. It can be used directly, without any UI adapter. |
| <xref:FatzBomb.MediaControl.Wpf.Player>, <xref:FatzBomb.MediaControl.WinForms.Player>, <xref:FatzBomb.MediaControl.Avalonia.Player> | Adapter classes that derive from `MediaControlPlayer`. They add an async factory and `Load` overloads that take the framework's image type. All commands, properties and events are inherited. |

`MediaControlPlayer` offers a few members beyond the interface: `Effects` (volume, pitch, tempo, equalizer), `CdgVisuals`, `OpenFileDialogFilter`, `NativeDependencies` and `LastNativeLoadMessage`. The interface has the rest. Surfaces such as `MediaSurface.Player` take an `IMediaControlSession`.

## Creating a player

A player needs three things: a platform factory (which also holds the renderer), a configuration, and the folders where the native libraries live.

```csharp
var player = await FatzBomb.MediaControl.Wpf.Player.CreateAsync(
    MediaPlayerConfiguration.Auto,
    WindowsNativeLibraryPaths.GetDefaultSearchPaths(),
    new WindowsDirect3D11MediaControlPlatformFactory());
```

The three forms:

| Form | When to use it |
| --- | --- |
| `Player.CreateAsync(configuration, paths, platformFactory)` | UI code. It builds the player on a thread-pool thread, so the UI stays responsive. |
| `new Player(configuration, paths, platformFactory)` | Code that already runs off the UI thread (a console tool, a background task). It blocks while the native libraries load. |
| `new MediaControlPlayer(platformFactory, configuration, paths)` | Core-only hosts with no UI adapter. Note the argument order differs from the adapters' `Player`. |

The WPF `Player` also has overloads that take an `IMediaPresentationCoordinatorFactory` (a renderer factory) in place of the platform factory and use the Windows platform; the Avalonia and WinForms ones always need a platform factory.

> [!IMPORTANT]
> Creating a player loads BASS and FFmpeg and blocks. Calling the constructor on the UI thread freezes the window. In UI code, always use `Player.CreateAsync`.

Creation throws when it cannot proceed:

- `DirectoryNotFoundException` if a folder in the path list does not exist.
- `InvalidOperationException` if the required FFmpeg libraries cannot be loaded. The message lists what is wrong; read `platformFactory.NativeDependencies` for the details ([Native dependencies](../getting-started/native-dependencies.md#startup-report)).
- A missing BASS does not throw. The player starts without audio.

Create one player per independent playback. Players do not share state, and each should have its own platform factory and renderer factory instance.

## Configuration

<xref:FatzBomb.MediaControl.Core.Configuration.MediaPlayerConfiguration> is an immutable record struct that you pass at creation. Start from `MediaPlayerConfiguration.Auto` (or `new MediaPlayerConfiguration(VideoDecodingMethod.Auto)`) and override with `with` or an object initializer:

```csharp
var configuration = MediaPlayerConfiguration.Auto with
{
    AudioOutputDeviceIndex = -1,
    MidiSoundfontPath = @"C:\soundfonts\gm.sf2",
    ResetPitchOnMediaLoad = true,
};
```

| Property | Default | Meaning |
| --- | --- | --- |
| `VideoDecoding` | `Auto` | `Auto` uses the GPU where it can decode the codec and software otherwise. `Hardware` forces hardware decoding and falls back to software if it fails. `Software` forces CPU decoding. Use `Software` with the Software renderer, which presents CPU frames only. |
| `Mode` | `Full` | `Full`, `Background` (looping picture, no audio) or `Audio` (sound only, no renderer). See below. |
| `AudioOutputDeviceIndex` | `-1` | The BASS output device. `-1` is the system default and follows default-device changes. An explicit index is a creation-time choice; to change devices, create a new player. |
| `AudioOutputFallbackMode` | `FallbackToPrimary` | What happens when an explicit device cannot be opened. `NoFallback` does not reroute audio. |
| `SubtitleStyle`, `KaraokeStyle` | Defaults | Text styles for subtitles and MIDI/KAR lyrics (including the sung-text colours). Fixed for the player's lifetime. |
| `MidiSoundfontPath` | `null` | A MIDI soundfont (`.sf2`, `.sfz`, `.dls`). When empty, the engine looks for one. |
| `ResetPitchOnMediaLoad`, `ResetTempoOnMediaLoad` | `false` | Return pitch to 0 and tempo to 100 after every successful load. |
| `SilenceTrim` | Off | Optional trimming of leading and trailing silence for analyzed plain audio. |
| `NetworkRetry`, `NetworkTls`, `NetworkReadAhead` | `null` (defaults) | Network behaviour: retries, certificate checks, read-ahead. |
| `HdrToneMapping` | Tone mapping on | How HDR video is shown on SDR output. |
| `PresentationLeadRefreshIntervals` | `0.75` | The starting presentation lead in display refresh intervals; an adaptive controller adjusts it. |
| `EnableLogging`, `EnablePerformanceLogging`, `EnableVerboseLogging` | `false` | Extra trace output for debugging and performance work. They do not control the `MediaDiagnostic` event (see [Diagnostics](diagnostics.md)). |
| `EnableUsbBluetoothLatencyHeuristic` | `false` | Adds an estimated offset for USB and Bluetooth audio outputs. |

To change a configuration value later, call `Reset(configuration, paths)`. It rebuilds the engine, keeps your surfaces attached, and starts the runtime settings (volume, pitch, tempo, normalization, equalizer, loop) from their defaults again; no media is loaded afterwards.

### Player modes

| Mode | What it does | What it rejects |
| --- | --- | --- |
| `Full` | The complete player: audio, video, images, CD+G, subtitles, MIDI and network audio. | Nothing. |
| `Background` | A looping picture source: video (picture only, no audio decoded), images and plain colour sources. No audio output starts, and looping is always on. | Audio-only files, CD+G, MIDI and network audio (`Load` returns `false` with a diagnostic). |
| `Audio` | Sound only: audio files, MIDI, a video's soundtrack, the audio of MP3+G. No renderer, GPU device or surfaces. | Images and video without audio (`Load` returns `false`). Surface calls return `false`. |

`await player.SetModeAsync(MediaPlayerMode.Audio)` switches mode on the fly. The engine is rebuilt and the open media is reopened at its position and in its transport state, so there is a short gap like a seek. Volume, pitch, tempo, normalization, equalizer, sync offsets, display options and the prepared-next item carry over. `ModeChanged` and `HasPresentationChanged` report the change. In `Audio` mode, `HasPresentation` is `false`: your surfaces stay attached but nothing draws to them, so the adapter shows its background instead of the last frame.

## Controlling playback

```csharp
IMediaControlSession session = player;

player.PlayOnLoad = true;                       // start when a load succeeds
bool loaded = await session.LoadAsync(path);    // false: could not be opened
await session.SeekAsync(30);                    // seconds
await session.PauseAsync();
await session.PlayAsync();
await session.StopAsync();                      // stops and returns to the start
session.Close();                                // closes the media
```

- **Load** takes a file path or a <xref:FatzBomb.MediaControl.MediaInputSource> (a file, a network URI, a BGRA image or a plain colour). An optional background and an `ignoreSidecar` flag control background discovery. A `CancellationToken` overload cancels a load that is queued or in progress. A file that cannot be opened makes the load return `false`, and the engine reports why through `MediaError` and `MediaDiagnostic`.
- **Transport**: `Play`, `Pause`, `Stop`, `Seek` and their async forms.
- **Seeking and scrubbing**: `SeekAsync` queues a seek, and a newer request replaces one still waiting, so you can call it for every mouse move while the user drags a seek bar. Wrap a drag in `BeginScrub()` and `EndScrub()` (send the release seek before `EndScrub`). `ScrubAudio` chooses whether sound plays during the drag.
- **Effects**: `player.Effects.Volume` (0 to 1), `Pitch` (-60 to +60 semitones), `Tempo` (5 to 5000, where 100 is normal), plus `Normalize` and `Equalizer` on the player.
- **Sync offsets**: `AudioSyncOffset`, `CdgSyncOffset` and `SubtitleSyncOffset`, with `Adjust...SyncOffset(steps)` in 100 ms steps and `ResetSyncOffsets()`.
- **Subtitles and lyrics**: `SubtitleTracks`, `SelectSubtitleTrack(id)`, `ShowMidiLyrics`.
- **Display**: `VideoDisplayMode`, `BaseVisualTransform`, `TransparentBackground`, `AutoBackground`.
- **Playlists**: `PrepareNextAsync(source)`, `TransitionToPreparedNextAsync()`, `AutoTransitionToPreparedNext`, `PreparedNextTransitionFade`, and the `FadeOutAsync` and `FadeInAsync` calls. Your application owns the playlist; the engine holds one prepared item.

## State

| Member | Meaning |
| --- | --- |
| `State` | A <xref:FatzBomb.MediaControl.Core.Abstractions.MediaPlayerState>: `Null` (nothing open), `Playing`, `Paused`, `Stopped`, `Buffering`. |
| `IsOpen` | Whether media is open. |
| `Content` | The content roles of the open media (a flags value): audio, video, CD+G, image, archive, subtitle. `HasVideo`, `HasAudio`, `HasCdg`, `HasSubtitle` and `HasVisualContent` read from it. |
| `Position`, `Duration` | Current position (settable, same as `Seek`) and length. Live network sources report zero duration. |
| `Mode`, `HasPresentation` | The current mode, and whether a renderer presents. |
| `CurrentSourceCapabilities` | What the open source supports (seeking, known duration, live, buffering, sidecars); `null` when nothing is open. |
| `PreparedNext` | A snapshot of the prepared-next slot. |
| `VideoDecodeCapabilities` | The codecs and containers the platform can decode, and which in hardware. |
| `NativeDependencies` | What native libraries loaded ([Native dependencies](../getting-started/native-dependencies.md#startup-report)). |

Property reads are immediate and do not wait for queued operations. To react to changes, subscribe to events ([Threading and events](threading-and-events.md)) rather than polling.

## Lifetime and disposal

A player lives from creation to `Dispose`. It holds native libraries, audio and GPU resources, and worker threads, so always dispose it.

```csharp
surface.Player = null;              // detach the surface first
await Task.Run(player.Dispose);     // Dispose blocks, so keep it off the UI thread
```

What `Dispose` does:

- Cancels loads in progress or queued (they return `false`), then waits for the operations queued before it, for at most about 2 seconds.
- Stops playback and frees the engine on the calling thread. If an operation is still busy after the wait (a network connection attempt, say), `Dispose` detaches the surfaces and returns; the player rejects every call from then on and finishes freeing itself when the busy operation ends.
- Does nothing when called again. After disposal, control calls throw `ObjectDisposedException`.
- Throws `InvalidOperationException` if you call it from inside an event handler that was raised during a control operation (it would wait for itself). Schedule it after the handler returns, for example from a UI-thread callback.

Order matters in UI shutdown: detach every surface from the player (set `Player` to `null`), then dispose the player, then let the window close. `Reset` has the same restrictions: it blocks, waits at most 5 seconds, returns `false` if the player was busy and nothing was rebuilt, and throws if called from an event handler.

> [!WARNING]
> A player is not a lightweight object. Do not create one per file. Create it once, then call `Load` for each file.

## Sharing and concurrency

All control calls (load, transport, seek, close, mode switch) run one at a time on the player's control queue, in the order they were made. You can call the async methods from any thread. Property getters and setters are not queued. See [Threading and events](threading-and-events.md) for which calls block.
