# Frame timing

Video looks smooth when each decoded frame stays on screen for the same number of display refreshes. 24 fps film on a 60 Hz monitor cannot be perfect, but it should repeat frames in a steady 3:2 pattern, not drift. This article explains the two timing modes, how the library learns the display's refresh rate, and what you can set.

## Timing modes

<xref:FatzMedia.Video.MediaControlPlayer.VideoFrameTiming> chooses how the presentation loop decides when to show each frame. It is a runtime property.

| Mode | Behavior |
| --- | --- |
| `SourceCadence` (default) | The loop wakes on the video's own frame times and presents each frame as it comes due. The display shows it at its next refresh. Works with every renderer and surface. |
| `DisplayLocked` | The loop wakes just before each refresh of the display showing the video and presents the frame meant for that refresh, using the display's vertical-blank timing and a jitter-filtered media clock. Frames keep the same number of refreshes where the rates allow it (3:2 for 24 fps on 60 Hz, never 3:3:1). Each display runs its own timing when outputs are on monitors with different rates. |

```csharp
player.VideoFrameTiming = VideoFrameTiming.DisplayLocked;
```

`DisplayLocked` needs the renderer to report vertical-blank timing for the display that shows the video. The Direct3D11 renderer on a native window does. When there is no timing, the player stays on source cadence. This is the case for the Software renderer and for WPF-composited surfaces. The player reports it, once the state has held for half a second:

```text
[Info] VideoFrameTiming: Display-locked video timing unavailable: the renderer reports no vertical-blank timing for the video's display (software output or a WPF-composited surface).
```

Other reasons it reports are `the video's frame rate is unknown` and `the display's vertical-blank timing is not current`. When timing is in use, the player reports `Display-locked video timing on: frames follow the 59.94 Hz refreshes of the video's display` (several displays are listed with their rates).

> [!TIP]
> `DisplayLocked` is most useful on Windows with the Direct3D11 renderer in a native window. Try both modes on your target hardware before you make it the default.

## How the refresh rate is found

The loop needs the refresh rate of the monitor that shows the video. It uses, in this order:

1. A rate supplied by the host. On Linux this is <xref:FatzMedia.Video.Linux.LinuxPlatformOptions.DisplayRefreshRateHz>, because Linux has no compositor-neutral way to ask.
2. The rate the renderer reports for the monitor showing the primary surface.
3. The platform's own detection (on Windows, the desktop compositor's rate), and 60 Hz when nothing is known.

A rate from the platform that falls outside 23 to 600 Hz is replaced by the 60 Hz fallback.

```csharp
// Linux: tell the engine the real refresh rate (default 60, clamped to 24..360)
var platform = new LinuxMediaControlPlatformFactory(
    new SoftwarePresentationCoordinatorFactory(),
    new LinuxPlatformOptions { DisplayRefreshRateHz = 144 });
```

When a video opens, the player compares the video's frame rate with the display rate and reports the result in the `VideoCadence` category. The text names where the rate came from, so you can see when it is a guess:

```text
[Info] VideoCadence: Video 23.976 fps on a 60 Hz display: frames are shown for 3 and 2 refreshes in turn (3:2 pulldown). ...
```

A report like `frames are shown for 2 refreshes, so motion is smooth` means the rates match well. Reports about uneven holds or skipped frames end with a list of display rates that would be smooth for that video.

## Presentation lead

`PresentationLeadRefreshIntervals` in the [configuration](../reference/configuration.md) is the initial presentation lead, measured in display refresh intervals. The default is 0.75 and suits 23.976 and 24 fps material on 60 Hz displays. It is the starting point of an adaptive controller: the player raises the lead when frames arrive late or are dropped, and lowers it when playback is consistently early. Most hosts leave it alone.

## Present mode of the Direct3D11 swap chain

`Direct3D11PresentationOptions.NativeSwapChainPresentMode` defaults to `VSync`, which lets DXGI and the desktop compositor pace frames to the monitor. `Immediate` presents without waiting and can show tearing; it exists for diagnostics.

## Frame rate and CPU tools

To see how a video is doing, turn on `EnablePerformanceLogging` in the configuration. Presentation counters (frames presented, dropped, late) are written to `System.Diagnostics.Trace` with the prefix `[VideoPipeline:Perf]`, and tick-gap warnings with `[VideoPipeline:Loop]`. Add a `TraceListener` to see them. The player's `PresentationFramePresented` event fires for each presented frame, which is enough to compute a frame rate:

```csharp
Trace.Listeners.Add(new TextWriterTraceListener("player-trace.log"));
var configuration = new MediaPlayerConfiguration { EnablePerformanceLogging = true };
```

The test players show presented frames per second, process CPU and memory in their status line (see [Test players](../reference/test-players.md)).
