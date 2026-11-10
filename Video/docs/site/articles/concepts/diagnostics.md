# Diagnostics

Every player reports what it is doing and what went wrong through one event, `MediaDiagnostic`. It carries a severity level, a category that names the subsystem, a message, and optionally a source path and an exception. This page explains what is reported, how to log it, and how reports are attributed when you run several players in one process.

Use diagnostics to find out why a file did not play, which decoder path was chosen, which native library is missing, or why a device was lost. Use <xref:FatzMedia.Video.PlayerCore.MediaError> when you need to react in your UI to a failure; it carries a typed error code.

## The event

```csharp
player.MediaDiagnostic += (_, e) =>
    Console.WriteLine($"[{e.Level}] {e.Category}: {e.Message}");
```

<xref:FatzMedia.Video.MediaDiagnosticEventArgs> has these properties:

| Property | Meaning |
| --- | --- |
| `Level` | A <xref:FatzMedia.Video.MediaDiagnosticLevel>: `Info`, `Warning` or `Error`. |
| `Category` | The subsystem that produced it, as a string such as `FFmpeg` or `Composition`. See [Categories](#categories). |
| `Message` | A readable description. |
| `SourcePath` | The file or identifier concerned, when there is one. |
| `InnerException` | The exception behind the report, when one was caught. |

### Levels

| Level | Use it for | Examples |
| --- | --- | --- |
| `Info` | What the engine chose and did. No effect on playback. | The decode path, the Vulkan device, "Audio engine: FatzAudio", a surface's presentation path. |
| `Warning` | A non-fatal problem that may reduce quality or completeness. | A failed equalizer update, a device change, a handler that threw. |
| `Error` | A subsystem could not do what was asked. | A file that failed to load, a missing required FFmpeg library, a renderer that could not start. |

Diagnostics are raised in Debug and Release builds. They are always on: the `EnableLogging`, `EnablePerformanceLogging` and `EnableVerboseLogging` switches in <xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration> add extra trace output for debugging and measurements, but do not turn the event on or off.

### Threads

`MediaDiagnostic` is delivered in order on a worker thread that belongs to the player. It is not queued behind your control calls, so a diagnostic raised during a long operation (a network retry while loading, say) reaches your handler while the operation is still running. Handlers must marshal to the UI thread before touching the UI, and must be thread-safe if you write to a shared log. When you dispose the player, queued diagnostics are delivered first (for up to a second), and none arrive afterwards.

A `MediaDiagnostic` handler that throws is ignored. It is not reported as another diagnostic, because that would raise the event again.

## Logging diagnostics

A small logger that filters by level, writes to a file and keeps the player's name in each line:

```csharp
public sealed class PlayerLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly PlayerCore _player;
    private readonly string _name;
    private readonly MediaDiagnosticLevel _minimum;

    public PlayerLog(PlayerCore player, string name, string path,
        MediaDiagnosticLevel minimum = MediaDiagnosticLevel.Info)
    {
        _player = player;
        _name = name;
        _minimum = minimum;
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        player.MediaDiagnostic += OnDiagnostic;
    }

    private void OnDiagnostic(object? sender, MediaDiagnosticEventArgs e)
    {
        if (e.Level < _minimum)
            return;

        lock (_gate)
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {_name} [{e.Level}] {e.Category}: {e.Message}");
            if (e.SourcePath is not null)
                _writer.WriteLine($"    source: {e.SourcePath}");
            if (e.InnerException is not null)
                _writer.WriteLine($"    {e.InnerException}");
        }
    }

    public void Dispose()
    {
        _player.MediaDiagnostic -= OnDiagnostic;
        lock (_gate)
            _writer.Dispose();
    }
}
```

To forward to `Microsoft.Extensions.Logging`, map `Info`, `Warning` and `Error` to `LogLevel.Information`, `LogLevel.Warning` and `LogLevel.Error`, and use `Category` as part of the log category or as a structured property.

Subscribe before you load media. The diagnostics about loading the native libraries are raised while the player is being created, before you have an object to subscribe to, so read `NativeDependencies` after creation instead ([Native dependencies](../getting-started/native-dependencies.md#startup-report)).

## Categories

The category is a free-form string, and new ones appear as the engine grows, so match on prefixes and treat unknown values as information. These are the main ones:

| Area | Categories | Typical content |
| --- | --- | --- |
| Native loading | `Bootstrap`, `LinuxBootstrap`, `FFmpeg` | Which libraries were found, loaded or are missing; "audio unavailable". |
| Audio | `FatzAudioEngine`, `FatzAudioCdgEngine`, `FatzAudioVideoAudio`, `AudioFallback`, `AudioDeviceMonitor` | Opening audio, normalization, equalizer, the FFmpeg audio fallback, device changes. |
| MIDI and karaoke | `MIDI` | Soundfont selection and MIDI loading problems. |
| Video | `VideoPipeline`, `VideoDecoder`, `VideoCadence`, `PlaybackScheduler` | The decode path (hardware or software), display rate, scheduling problems. |
| Network | `Network` | Waiting for the network, audio that did not open, retries. |
| Compositing and rendering | `Composition`, `Presentation`, `D3D11`, `D3D11.DeviceLost`, `D3D11.Hdr`, `D3D11.HdrOutput`, `Vulkan`, `Subtitle`, `CdgEngine` | Device creation, present failures, HDR output, subtitle rasterization. |
| GPU device loss | `DeviceRecovery`, `D3D11.DeviceLost`, `Vulkan.DeviceLost`, `Software.DeviceLost`, `Decoder.DeviceLost` | Device loss and recovery. See [GPU device loss](../presentation/gpu-device-loss.md). |
| Session | `MediaFile`, `MediaControls`, `PlayerCore`, `PlayerOperationQueue`, `PreparedNext`, `PipelineMediaEngine` | Load failures, reset and dispose problems, prepared-next analysis and transitions. |
| Host events | `HostEvent` | One of your event handlers threw. |
| Adapter surfaces | `Wpf.MediaSurface`, `Avalonia.MediaSurface`, `WinForms.MediaSurface` | Which presentation path a surface uses, and a Warning when the player refuses a surface. |

Each adapter surface exposes its category as `MediaSurface.DiagnosticCategory`.

## One player or several: who owns a diagnostic

Each report is attributed to the player that caused it. When you run several players in one process (a main player and a preview, or several side by side), a handler on player A sees A's diagnostics and not B's. This works because:

- When a player is created, and for every call you make on it, the engine marks the current execution context with that player as the owner (an `AsyncLocal` under the hood). Threads and tasks the player starts inherit the mark, so decoder threads, the render loop and the control queue report for the right player.
- The player re-checks each report: it delivers a diagnostic only when the owner is itself or when there is no owner.

A diagnostic with no owner is process-wide, and every player reports it. That is correct for things that belong to the process rather than to one player: native library loading, and work that runs in native callbacks, which lose the execution context. If you see the same line from two players, it is process-wide.

### Raising your own diagnostics

Your code can raise diagnostics into the same stream, which keeps your application's messages in the same log, in order, with the engine's. Pass the player so only its handlers receive it:

```csharp
DiagnosticRouter.Raise(
    player,
    MediaDiagnosticLevel.Warning,
    "MyApp.Playlist",
    "Skipped an unreadable entry.",
    sourcePath: @"C:\music\bad.mp3");
```

The overload without a player, `DiagnosticRouter.Raise(level, category, message)`, raises a process-wide diagnostic that every player reports. <xref:FatzMedia.Video.DiagnosticRouter> is static and has no event of its own. Subscribe to `MediaDiagnostic` on each player.

Use a distinctive category (for example your application's name) so you can filter your messages from the engine's.

## Diagnostics and errors

| | `MediaDiagnostic` | `MediaError` |
| --- | --- | --- |
| Purpose | A log stream | Reacting to a failure |
| Volume | Many messages, including informational ones | Few: high-value failures |
| Type | Level, category string, message | A typed <xref:FatzMedia.Video.MediaErrorCode> (`FileNotFound`, `NativeLibraryMissing`, `UnsupportedFormat`, `OpenFailed`, `DecodeFailed` and others) |
| Thread | The player's diagnostic thread | The control queue |

A load that fails often produces both: a `MediaFile` Error diagnostic that names the file, and a `MediaError` with a code. Show the user the `MediaError` message and write the diagnostics to your log.
