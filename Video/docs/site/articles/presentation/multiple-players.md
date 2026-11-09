# Multiple players

You can run several players in one process: two preview panes, a karaoke player plus a background-loop player, or a wall of videos. Each <xref:FatzMedia.Video.MediaControlPlayer> is independent. This article explains what is separate, what is shared, and how to create and watch several players safely.

## What each player owns

Each player has its own:

- playback engine, transport, position and media;
- renderer instance and surfaces (a surface attaches to exactly one player);
- settings: volume, pitch, tempo, equalizer, display mode, color transform, frame timing;
- control queue, so a slow load in one player does not block another;
- events and diagnostics (see below).

The repository's test players create up to four players side by side, each with its own platform factory, so the simplest rule is one platform factory per player.

## What is shared in the process

- **Native libraries.** BASS and FFmpeg are located and loaded once per process. A later player created with the same paths reuses the result without scanning again. A later player created with other paths retries BASS; the already loaded FFmpeg is kept.
- **The MIDI soundfont.** BASSMIDI has one default soundfont per process. Each player sets it from its `MidiSoundfontPath` when its audio output starts, so the most recent player's soundfont applies to MIDI playback in every player. Give all players the same `MidiSoundfontPath`.
- **Process-wide diagnostics.** Messages from native loading and other process-wide work are not owned by a player and reach every player's `MediaDiagnostic` event.
- **The machine.** CPU, GPU and audio hardware are shared; see "Resources" below.

## Creating players

Constructing a player loads native libraries and builds the engine, which blocks. Create players off the UI thread with the adapter's `Player.CreateAsync`, one after another, and dispose them off the UI thread too.

```csharp
static async Task<Player[]> CreatePlayers(string[] nativePaths, int count)
{
    var players = new Player[count];
    for (int i = 0; i < count; i++)
    {
        int index = i;
        var platform = new WindowsDirect3D11MediaControlPlatformFactory();
        players[i] = await Player.CreateAsync(MediaPlayerConfiguration.Auto, nativePaths, platform);
        players[i].MediaDiagnostic += (_, e) =>
            Console.WriteLine($"player {index}: [{e.Level}] {e.Category}: {e.Message}");
    }

    return players;
}
```

Give each player its own `MediaSurface` and set its `Player` property to bind them. Events fire on background threads; marshal to the UI thread before you touch UI state.

## Per-player diagnostics

Diagnostics belong to the player that raised them (see also [Diagnostics](../concepts/diagnostics.md)). A player reports a message on its `MediaDiagnostic` event only if it owns the message or the message is process-wide. Engine work (decoding, presentation, audio) is attributed to its player automatically, including the threads and tasks it starts. Surfaces name the player they are attached to, so a surface's presentation-path messages appear only in that player's log.

This lets you give each player its own log, panel or file without filtering. The same holds for `MediaError` and the other events: they are raised per player.

```csharp
player.MediaDiagnostic += (_, e) => Log(playerName, e.Level, e.Category, e.Message);
```

## Resources

Several players multiply the load. Watch these when you size a layout:

| Resource | Notes |
| --- | --- |
| CPU | Software decoding and the Software renderer cost CPU per player. Hardware decoding moves the work to the GPU. See [Hardware decoding](../video/hardware-decoding.md). |
| GPU | Each player renders its own frames. Many large videos at once can exhaust GPU memory or decode engines; the player then falls back to software for a stream that cannot start in hardware. |
| Audio | Each player plays through its own audio stream. Players that should be silent (a background loop) can use `MediaPlayerMode.Background`, which never starts the audio engine, or `Audio` mode for sound only with no renderer. |
| Memory | Each player buffers decoded frames and, for network sources, read-ahead data. |

Use <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> to build only what a player needs (see [Player modes](../playback/player-modes.md)):

| Mode | Builds |
| --- | --- |
| `Full` (default) | Audio, video, images, CD+G, subtitles and MIDI. |
| `Background` | A looping picture source with no audio engine. |
| `Audio` | Audio output only: no renderer, GPU device, surfaces or video decoder. |

## Disposal

Dispose each player when you are done with it. Disposal waits for queued operations, at most about 2 seconds, so do it off the UI thread. Close the secondary windows that belong to a player first.
