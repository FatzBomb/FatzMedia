<!-- Design note for stage 006B. Pure mapping + integration plan. No behavior change. -->

# 006B — Audio Device Hotplug Mapping

Stage file: `docs/roadmap/006B_BACKLOG_AudioDeviceHotplug.md`
Hotplug policy confirmed with the user before this stage opened:
**Detect + fail loudly with diagnostics.** No automatic reinit, no
auto-resume, no switch to a new default device.

Maps the BASS device init path, the failure behavior that follows it
today, the public event surface, and the integration seam for 006B.2.

## 1. BASS device init

| Step | File | Notes |
|---|---|---|
| Native load | `FatzBombMedia.MediaControl/Runtime/Bootstrap.cs:55` (`IsBassLoaded = LoadBass()`) | `LoadLibraryW` on `bass.dll`. `Bootstrap.IsBassLoaded` flips to `true` on success. Bootstrap itself is load-once, synchronous, called from `MediaPlayerServiceProvider.RegisterServices` via `LoadLibraries` (line 48). |
| `AudioEngine.Init` | `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs:144-186` | Called once during DI composition in `MediaPlayerServiceProvider.RegisterServices` (lines 50-65) with `device = -1, sampleRate = 44100`. Honors a pre-existing global BASS instance: if `MBass.GetInfo(...)` already returns a valid `BassInfo`, the call short-circuits (line 156-163). Otherwise calls `MBass.Init(device, sampleRate)` (line 166). On success, marks the engine as initialized and stores the device / sample rate. On failure, raises a diagnostic with `MBass.LastError` (line 177) and returns `false`. |
| `VideoAudioEngine.Init` | `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:148` | Returns `true` unconditionally. The video-audio path does not call `Bass.Init` itself; it relies on the audio-only engine's prior init. `Bass.CreateStream` (line 660) is what actually pulls BASS online for the push stream. |
| DI registration | `FatzBombMedia.MediaControl/MediaPlayerServiceProvider.cs:50-65` | Singleton `IAudioEngine` factory. The `Init` return value is not propagated as a failure: the engine is still returned even when `Init` failed, so subsequent operations can run and fail individually. |

BASS keeps a **global** device state. `AudioEngine.Dispose` deliberately
does not call `BASS.Free` (see `AudioEngine.cs:425-428`: "DON'T shut down
BASS globally - other engines (like UnifiedEngine) might be using it").
Device lifetime is therefore the application lifetime, not the engine's
lifetime.

## 2. Failure behavior today

When the device is missing, unplugged, or in a state that BASS cannot
use, the following cascade runs.

1. `AudioEngine.Init` fails → `DiagnosticRouter.Raise(Error, "AudioEngine", "Failed to initialize BASS: ...")` (line 177). Returns `false`. No throw.
2. `MediaPlayerServiceProvider` ignores the `false` and registers the engine anyway. A second diagnostic is raised from the registration lambda (line 56).
3. `AudioEngine.Open(file)` calls `Init` again if `_bassInitialized` is false (line 199-210). If `Init` fails, the second init attempt raises a second error diagnostic (line 205) and `Open` returns `false`.
4. `AudioStreamManager.OpenFromPath` returns `false` because `CreateStream` returns 0. `DiagnosticRouter.Raise(Error, "AudioStreamManager", ...)` (line 105).
5. `AudioEngine.Open` reports `false` to the caller.
6. `AudioPipeline.Open` returns `false` (`FatzBombMedia.MediaControl/Pipeline/AudioPipeline.cs:61-66`). The pipeline leaves `IsOpen = false`, resets `MediaState`, and bubbles `false` up.
7. `PipelineMediaEngine.Open` (line 134-140) catches the `false` and raises a typed `MediaErrorCode.OpenFailed` (or `UnsupportedFormat` for archive / image cases).
8. `MediaControlPlayer` re-raises the error via `OnEngineErrorOccurred` → `MediaControlPlayer.MediaError` (line 266-274), marshaled through `_controlOperations`.

The **mid-playback device-loss path** is the unhandled one. The audio
stream is already open; BASS continues running with the now-defunct
device handle; new `ChannelPlay` / `ChannelSetPosition` calls start
returning `false` without any explicit failure event from BASS. The
playback loop on the BASS sync callback thread is the only place the
end-of-stream signal comes from today; device loss does not look like
end-of-stream and therefore does not raise `MediaEnded`.

There is **no dedicated BASS device-loss detection** anywhere in the
codebase. The D3D11 device-loss recovery at
`FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:1131-1255` is a
video-side pattern; it does not cover audio.

## 3. Event surface

The Core public surface (the one the WPF adapter and any future host
sees) is `IMediaControlSession` at
`FatzBombMedia.MediaControl/Api/IMediaControlSession.cs` and the
implementer `MediaControlPlayer` at
`FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs`. The adapter
boundary is preserved because the WPF `Player` at
`FatzBombMedia.MediaControl.Wpf/Player.cs:11` simply inherits from
`MediaControlPlayer`.

Events currently on the public surface:

| Event | When | Source thread | Marshaling |
|---|---|---|---|
| `MediaEnded` (line 120) | playback reached end | BASS sync callback thread for `AudioEngine`; decode thread for `VideoAudioEngine`; presentation thread for `VideoPipeline` | `OnControlsMediaEnded` → `_controlOperations.Enqueue` (`MediaControlPlayer.cs:237-244`) |
| `MediaStarted` (line 123) | `Play` returned true | caller's thread | same `_controlOperations.Enqueue` (line 246-253) |
| `MediaStopped` (line 129) | `Stop` returned true | caller's thread | same (line 255-262) |
| `MediaError` (line 137) | `IMediaErrorSource.ErrorOccurred` raised | anywhere; `OnEngineErrorOccurred` marshals | `_controlOperations.Enqueue` (line 266-274) |
| `MediaDiagnostic` (line 148) | `DiagnosticRouter.DiagnosticOccurred` raised | anywhere; `OnDiagnosticOccurred` marshals | `_controlOperations.Enqueue` (line 276-284) |
| `PresentationFramePresented` (line 126) | presentation thread raised a frame | presentation thread | direct invoke, no marshaling (line 264) |

The internal events driving the public ones (Core-only):

- `AudioEngine.StateChanged` (`FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs:113`) — set by `SetState` (line 108-115) called from `Init`, `Open`, `Play`, `Pause`, `Stop`, and the BASS end-of-stream sync (line 484).
- `AudioEngine.PlaybackEnded` (line 104) — BASS end-of-stream sync only (line 486).
- `VideoAudioEngine` deliberately suppresses `PlaybackEnded` (`FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:129-133`); its end-of-stream signal goes through the position extrapolation in `TryGetPositionSnapshot` and the snapshot reader in `VideoPipeline`.
- `VideoPipeline.PlaybackEnded` (`FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:176`) — set in `CompletePlaybackAtEnd` (line 1617), runs on the presentation thread.
- `MediaEngine.MediaEnded` (`FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs:75`) — wired from `_coordinator.PlaybackEnded` via `OnPlaybackEnded` (line 589-594), which stops the background video and then invokes the event.

The right home for the new event is `IMediaControlSession` /
`MediaControlPlayer`, with the same `_controlOperations` marshaling used
by `MediaError` (the precedent for "background-thread signal that the
host must marshal to UI" is already established). The internal Core
detector raises a Core-only event that `MediaControlPlayer` re-raises
through the public surface.

## 4. Detection options for 006B.2

| Option | Latency | Reliability | Native deps | Notes |
|---|---|---|---|---|
| Poll `Bass.GetInfo` on a background timer | 0.5–2 s typical | depends on poll rate; BASS may not expose the new device until the next `BASS_Init` | none | simplest, but coarse and not real-time |
| Win32 `IMMNotificationClient` via `IMMDeviceEnumerator` | real-time | high | `MMDeviceAPI.dll` (in-box on Windows 7+; no redist) | the standard Windows way; gives `OnDefaultDeviceChanged`, `OnDeviceStateChanged`, `OnDeviceAdded`, `OnDeviceRemoved`. The new Core type should implement `IMMNotificationClient` and register with the enumerator. |
| BASS `BASS_SetConfig(BASS_CONFIG_DEV_NONSTOP, true)` | N/A (does not detect) | N/A | none | keeps BASS running across device loss but provides no callback. Worth setting anyway as a defense-in-depth so the BASS state is recoverable when the operator reconnects. |

Recommendation: **Win32 `IMMNotificationClient`** for detection +
`BASS_CONFIG_DEV_NONSTOP` for BASS resilience. Polling is the fallback
if the Win32 init fails (very rare on Windows 7+).

## 5. Integration seam for 006B.2

The new device-monitor sits next to the audio engine in DI and is
started by `MediaPlayerServiceProvider`. On a device-change callback:

1. The Core monitor raises an internal event with a structured payload
   (`AudioDeviceChangeKind`: `DefaultChanged` | `DeviceStateChanged` |
   `DeviceRemoved`; plus the device id, friendly name, and a Win32
   `HRESULT`).
2. `MediaControlPlayer` subscribes to the internal event in its ctor
   and re-raises the payload through:
   - a new public event `AudioDeviceChanged` on `IMediaControlSession`
     (typed `EventHandler<AudioDeviceChangedEventArgs>`),
   - a `MediaError` with a new code `MediaErrorCode.AudioDeviceLost`
     when the change is a removal or a state transition to
     `DEVICE_STATE_UNPLUGGED` / `DEVICE_STATE_NOTPRESENT`,
   - a `MediaDiagnostic` (warning level) with the device id and name.
3. The Core monitor also calls `Controls.Stop()` (or its async
   equivalent) so the active pipelines shut down safely. The existing
   `Stop()` path is reused — no reinit, no resume.
4. The WPF `Player` host subscribes to `AudioDeviceChanged` and
   surfaces a user-visible message (status text or non-blocking
   dialog). Operator decides what to do next.

The new public event keeps the loud-fail policy explicit: the host
sees both a typed event and a typed error, with the same diagnostic
content in both.

## 6. Files for 006B.2 (planned)

- `FatzBombMedia.MediaControl/Core/Abstractions/IAudioDeviceMonitor.cs` — new internal interface. One event: `event EventHandler<AudioDeviceChangeInfo>? DeviceChanged`. Method `void Start()` and `void Stop()`.
- `FatzBombMedia.MediaControl/Core/Audio/CoreAudioDeviceMonitor.cs` — new internal sealed class. Implements `IAudioDeviceMonitor` and `IMMNotificationClient` via `[GeneratedComInterface]` / `[GeneratedComClass]` (`ComImport` fallback if generator unavailable). Holds an `IMMDeviceEnumerator` from `MMDeviceAPI.dll` and registers itself in `Start`. The notification callbacks (running on a Win32 STA thread) marshal to Core via `_controlOperations.Enqueue`.
- `FatzBombMedia.MediaControl/Core/Audio/PollingAudioDeviceMonitor.cs` — fallback for hosts where the COM-based path fails to initialize. Polls `Bass.GetInfo` on a 1 s timer.
- `FatzBombMedia.MediaControl/Api/MediaErrorCode.cs` — new enum value `AudioDeviceLost`.
- `FatzBombMedia.MediaControl/Api/AudioDeviceChangedEventArgs.cs` — new, with kind + device id + device name + HRESULT + suggested action.
- `FatzBombMedia.MediaControl/Api/IMediaControlSession.cs` — add `event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged`.
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs` — wire the monitor; re-raise the event through the public surface; on `DeviceRemoved` / `DEVICE_STATE_UNPLUGGED`, call `Controls.Stop()` and raise `MediaError(AudioDeviceLost)`.
- `FatzBombMedia.MediaControl/MediaPlayerServiceProvider.cs` — register `IAudioDeviceMonitor` as a singleton, started by `MediaControlPlayer` in its ctor.
- `FatzBombMedia.MediaControl/Runtime/Bootstrap.cs` (or the audio engine init path) — set `BASS_CONFIG_DEV_NONSTOP` to `true` so BASS does not panic on device loss. The fallback reinit on the next open still goes through the existing `AudioEngine.Open` path.
- `FatzBombMedia.MediaControl.Wpf/Player.cs` — no change (inherits the new event).
- `FatzBombMedia.MediaControl.Tests/AudioDeviceMonitorFallbackTests.cs` — unit tests for the polling fallback (no COM required).

## 7. Risks for 006B.2

- `IMMNotificationClient` callbacks run on a Win32 STA thread that is
  not the BASS callback thread, not the Core operation queue, and not
  the WPF UI thread. All four marshaling rules apply; the Core monitor
  must enqueue to `_controlOperations` before touching any Core state
  that is not `volatile` / `Interlocked`.
- `Bass.GetInfo` after device loss can return stale data. The polling
  fallback needs to compare against the last known good
  `BassInfo.SampleRate` and `BassInfo.Latency` — a sudden jump (or 0)
  is the signal.
- `BASS_CONFIG_DEV_NONSTOP` is a BASS 2.4+ option. Confirm against the
  version reported by `Bass.Version` (logged by the existing
  `Bootstrap.AddNativeLoadMessage` path) before relying on it. If the
  version is older, do not set it; the detection / loud-fail path
  still works.
- `PipelineMediaEngine` is the lowest layer where `Stop()` actually
  drains the background video and resets state. The device-loss
  handler should call `Controls.Stop()` (which delegates to
  `_mediaEngine.Stop()` → `_coordinator.Stop()`) and not the lower
  level directly, so the existing event chain runs.

## 8. Final Policy: Diagnostic-Only Hotplug Monitor

After field testing, `BASS_CONFIG_DEV_NONSTOP` showed that BASS/Windows can
move an active stream across a normal default-device switch without Core
rebuilding the pipelines. Every Core intervention tested so far (stop,
reload, re-seek, BASS uninit/reinit) made the user-visible behavior worse.

Current policy:

- On `DefaultDeviceChanged`, `DeviceAdded`, `DeviceStateChanged`, or
  `DeviceRemoved`: raise `MediaDiagnostic` and `AudioDeviceChanged` only.
  Do **not** call `Stop`, `Close`, `UninitBass`, `Load`, `Seek`, or `Play`.
- Let BASS/Windows own migration of the active stream.
- If BASS really fails, rely on the existing BASS/audio-engine error paths
  rather than treating transient Windows endpoint snapshots as authoritative.
- `BASS_CONFIG_DEV_NONSTOP` remains enabled from `AudioEngine.Init` as the
  resilience setting that allows BASS to survive device movement.

Still out of scope unless a future stage proves a real BASS error path needs
it:

- Automatic reinit on monitor notifications.
- Automatic switch/reload on monitor notifications.
- Resume-from-position after monitor notifications.
- Audio engine rewrite.
- Tempo / pitch / normalization behavior changes.

### Additional touch list for final policy

- `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs` — added
  `EnsureDevNonStop()` called from `Init` to set `BASS_CONFIG_DEV_NONSTOP`.
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs` — device monitor
  notifications are diagnostic/event-only. There is intentionally no
  playback-changing handler attached to these notifications.

## 9. 006B.2 — Implementation

### Touch list

- `FatzBombMedia.MediaControl/Core/Abstractions/IAudioDeviceMonitor.cs` — new. `AudioDeviceChangeKind` enum, `AudioDeviceChangeInfo` record, `IAudioDeviceMonitor` interface (event, `Start`, `Stop`, `IsRunning`, `IDisposable`).
- `FatzBombMedia.MediaControl/Core/Audio/CoreAudioDefaultEndpointReader.cs` — new internal Win32 Core Audio reader. Polls the Windows default render endpoint (`eMultimedia`, with `eConsole` fallback) and returns endpoint id + friendly name without requiring a callback registration.
- `FatzBombMedia.MediaControl/Core/Audio/PollingAudioDeviceMonitor.cs` — new internal sealed class. One-second poll loop (configurable via internal ctor for tests). Detects Windows default endpoint id/name changes in addition to BASS device index, sample rate, and friendly name changes; raises `DeviceChanged` on the polling thread. The pure detection logic is factored into internal static methods so tests can drive it without BASS or COM. This directly covers the default-device switch case where BASS remains internally healthy while Windows routes audio elsewhere.
- `FatzBombMedia.MediaControl/Api/MediaErrorCode.cs` — added `AudioDeviceLost`.
- `FatzBombMedia.MediaControl/Api/AudioDeviceChangedEventArgs.cs` — new public sealed `EventArgs` carrying the change info.
- `FatzBombMedia.MediaControl/Api/IMediaControlSession.cs` — added `event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged`.
- `FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs` — resolves the monitor from DI, subscribes, starts it, and re-raises the device-change info through `MediaDiagnostic` and the new public `AudioDeviceChanged` event. It does not change playback state from monitor notifications.
- `FatzBombMedia.MediaControl/MediaPlayerServiceProvider.cs` — registers `IAudioDeviceMonitor` as a singleton (`PollingAudioDeviceMonitor`).
- `FatzBombMedia.MediaControl.Tests/PollingAudioDeviceMonitorTests.cs` — new, 8 tests for the pure `DetectChange` function: no change, device index changed, sample rate dropped to zero (removed), sample rate rose from zero (added), sample rate changed non-zero to non-zero (state changed), only name changed, initial state, device-index precedence.

### Out of scope (deferred follow-up, not required for the loud-fail policy)

- Win32 `IMMNotificationClient` (Core Audio) callback registration for sub-second push notifications. Polling the Windows default endpoint at 1 s is the delivered detector; the hook is left in `IAudioDeviceMonitor` so a future callback implementation can replace `PollingAudioDeviceMonitor` without touching `MediaControlPlayer`.
- `BASS_CONFIG_DEV_NONSTOP` set. BASS 4.0.2 supports it, but the loud-fail path already stops the pipelines on detection, so the BASS-side resilience is not on the critical path. Tracked as a follow-up.
- Per-stage hot-reinit, auto-resume, automatic switch to the new default device. All explicitly excluded by the user policy and the backlog.

## 10. 006B.3 — Validation

- `dotnet build FatzBombMedia.sln -c Release`: clean, 0 warnings.
- `dotnet test FatzBombMedia.sln -c Release --no-build`: **160 passed, 0 failed** (22 new for 006B + 138 from before).
- `scripts/check-wpf-boundary.ps1`: passed.
- `scripts/check-presentation-parity.ps1`: passed.
- WPF `using` audit for `FatzBombMedia.MediaControl.Core.*` renderer / pipeline / internal Cdg / D3D11 namespaces: zero matches. The new monitor lives in Core and the public event is on `IMediaControlSession`; the WPF `Player` adapter inherits the event automatically and adds no WPF-specific plumbing.
- `dotnet run --project FatzBombMedia.Player -c Release` (10-second alive check): process stays running. The polling monitor starts, captures the baseline, and waits for the next tick. No crash, no exception, no log flood from the new code path.

### Manual smoke (host-driven, per `docs/VALIDATION.md` §3)

Full UI-driven smoke is the operator's responsibility. The set required
by this stage, per the 006B backlog:

1. **Audio-only.** Play audio, then unplug / disable the output device
   (or use a virtual audio endpoint that can be disconnected at
   runtime). Expect: a `MediaDiagnostic` warning within ~1 s of the
   change, `MediaError(AudioDeviceLost)`, the new
   `AudioDeviceChanged` event, and a stopped pipeline. No hang, no
   zombie BASS stream, no silent continuation.
2. **Audio + CDG.** Same expectation. CDG clock must stop cleanly
   (no orphaned rendering thread).
3. **Video-with-audio.** Same expectation. The presentation loop must
   stop, the native HWND surface must remain attached for the next
   `Load`, and the seek worker must be retired.
4. **Pitch / tempo / normalization regression.** After a clean stop
   triggered by device loss, the next `Load` + `Play` must use the
   same `SetTempo` / `SetPitch` / `Normalize` path as before; the
   device-loss path must not have changed any of these. Spot-check by
   setting tempo to ±20% and pitch to ±5 semitones before and after
   the device-loss event.
5. **Intentional device failure (BASS path).** With the polling
   fallback, the only signals Core sees are the BASS-reported
   `SampleRate` and `CurrentDevice` index. If the device is disabled
   such that BASS keeps the same device index but the sample rate
   changes, the change is detected and reported. If BASS reports a
   zero sample rate (the typical "device gone" signal), it is
   classified as `DeviceRemoved` and triggers the loud-fail path.
   Operators should be aware that polling at 1 s means the worst-case
   detection delay is 1 s; the Win32 `IMMNotificationClient` path
   (deferred follow-up) would reduce this to ~real-time.

If any of these regress, the most likely cause is the event handler
running on a thread that does not have the BASS sync callback's
expectations. Re-check the `_controlOperations.Enqueue` marshaling at
`MediaControlPlayer.cs:OnAudioDeviceMonitorChanged` and the
`HandleAudioDeviceChange` body that calls `Controls.Stop()`.
