# GPU device loss

A GPU driver update, a timeout and reset (TDR), a GPU hang, an unplugged external GPU or a remote-desktop session change can take away the graphics device the renderer is using. The player notices, rebuilds its renderer on a new device and continues. This article describes what happens, what your application sees, and what you may want to do.

## What the player does

1. **Detects the loss.** The renderer is asked every second whether its device is lost, and a failed present asks again sooner. The decoder watches its own device too.
2. **Reports it.** One Error diagnostic in the `DeviceRecovery` category: `GPU device lost (<reason>); recovering.`
3. **Recovers the renderer.** The player stops presenting, releases the renderer's resources and creates a new device, re-creating every attached surface on it. It tries up to six times, waiting 0.5, 1, 2, 4 and 8 seconds between attempts. On success it logs an Info diagnostic in the renderer's category (`GPU device recovered after N attempt(s).`), and rendering resumes with the current frame.
4. **Reopens the video.** Video decoded on the lost device is gone, whether from a hardware decoder or from the device the renderer adopted from it. After a recovery the player reopens the open video once, at its position and in its transport state, with the Info message `Reopening <name> after the GPU device was recovered.` A video that was playing keeps playing.
5. **Moves decoding to software meanwhile.** If the hardware decoder's device was lost, the player switches that video to software decoding at the current position, so the picture and audio continue. It then checks periodically whether the GPU is back and returns to hardware decoding when it is (`Hardware video decode restored after device recovery.`).

Audio, CD+G playback and the clock are not interrupted by a device loss.

## What the host sees

You do not have to do anything for a normal recovery. The signals are diagnostics, raised on the player's `MediaDiagnostic` event (on a background thread):

| Category | Level | Message |
| --- | --- | --- |
| `DeviceRecovery` | Error | `GPU device lost (<reason>); recovering.` |
| `DeviceRecovery` | Warning | `Retrying GPU device recovery (<reason>).` (a retry after a failed recovery) |
| `D3D11.DeviceLost` (Direct3D11), `Vulkan.DeviceLost`, `Software.DeviceLost` | Info | `GPU device recovered after N attempt(s).` |
| same category | Warning | `Device-lost recovery attempt N/M failed.` |
| `D3D11.DeviceLost.RecoveryFailed` (or the Vulkan or Software equivalent) | Error | `GPU device-lost recovery failed after N attempt(s); retrying every 10 s while a surface is attached.` |
| `DeviceRecovery` | Info | `Reopening <name> after the GPU device was recovered.` |
| `Decoder.DeviceLost` | Error / Info | `Hardware video decode device lost; recovering via software fallback.` then `Video decode fell back to software after device loss; audio and CD+G overlay continue uninterrupted.` |
| `Decoder.DeviceLost.RecoveryFailed` | Error | The software fallback itself failed. The video engine goes to its error state. |
| `Presentation` | Error | `Renderer call has not returned for N s (...); presentation is blocked.` A renderer call is stuck, usually a hung driver. |

```csharp
player.MediaDiagnostic += (_, e) =>
{
    if (e.Category is "DeviceRecovery" or "D3D11.DeviceLost" or "D3D11.DeviceLost.RecoveryFailed")
        Dispatcher.BeginInvoke(() => StatusText.Text = e.Message);
};
```

The picture may freeze or go black for the moment the recovery takes (typically a second or two), then returns. The surface keeps its place in your layout; you do not re-attach it.

## When the renderer cannot recover

Sometimes the device cannot be re-created in the same process. After an NVIDIA GPU reset, for example, every `vkCreateInstance` fails with `VK_ERROR_INCOMPATIBLE_DRIVER` until the process restarts. The player then raises `MediaError` once, with `MediaErrorCode.RendererDeviceLost`. Sound continues, but no picture appears until the player is recreated with another renderer. When the renderer reports that it cannot be re-created, the player stops the recovery attempts after the first one, so this error comes as soon as the driver answers. During a GPU reset the driver itself can hold every graphics call for 10-15 s.

To keep the show going, recreate the player on the next renderer (Vulkan to Direct3D11, Direct3D11 to WARP, WARP to Software), load the same media and seek to where it was:

```csharp
player.MediaError += (_, e) =>
{
    if (e.Code != MediaErrorCode.RendererDeviceLost)
        return;

    Dispatcher.BeginInvoke(async () =>
    {
        var source = currentSource;          // what you loaded
        var position = player.Position;
        var wasPlaying = player.State == MediaPlayerState.Playing;
        await RecreatePlayerAsync(nextRenderer);   // your own: dispose, create, attach the surface
        await player.LoadAsync(source);
        await player.SeekAsync(position.TotalSeconds);
        if (wasPlaying)
            await player.PlayAsync();
    });
};
```

The test players do the renderer switch (the players come back empty).

Hosts that build on the lower-level `IMediaPresentationCoordinator` interface also get its `DeviceRecovered` event, raised on the recovery thread after a successful recovery. A host using `PlayerCore` does not need it, because the player already reopens its video.

## If recovery fails

After the attempts are used up, the player gives up for now but keeps trying every 10 seconds while a surface is attached, and logs the failure as an Error in the `...DeviceLost.RecoveryFailed` category. Typical reasons are a driver that is still resetting, a GPU that has been removed, or a remote session without a GPU.

What you can do:

- Show the user the message and let them continue; recovery may succeed on a later retry without any action.
- Offer to restart playback with the Software renderer or WARP. A player keeps its renderer, so this means creating a new player with a different [renderer](../video/renderers.md).
- Start with a fallback chain if your users often run without a GPU (remote desktop, virtual machines).

## Related

- [Hardware decoding](../video/hardware-decoding.md) explains the fallback from hardware to software decoding.
- [Common problems](../troubleshooting/common-problems.md) lists the diagnostics for black video.
