<!-- Design note for stage 006A. Pure mapping + integration plan. No behavior change. -->

# 006A — Audio Output Latency Mapping

Stage file: `docs/roadmap/006A_BACKLOG_AudioLatencyFeedback.md`
Maps the four flows named in the backlog's *First Safe Step* and identifies the
integration seam for 006A.2.

## 1. BASS position

The "BASS position" feeding the presentation clock is the BASS-reported
playback position of the active stream, exposed to Core via two paths.

| Source | File | Notes |
|---|---|---|
| Audio-only position | `FatzBombMedia.MediaControl/Core/Audio/AudioStreamManager.cs:186-195` (`GetCurrentPosition`) → `MBass.ChannelGetPosition` + `ChannelBytes2Seconds` | Pulled from `PlayHandle` (tempo stream), falling back to `DecodeHandle`. |
| Video-with-audio position | `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:139-149` (`GetPositionCore`) → `Bass.ChannelGetPosition` + `ChannelBytes2Seconds` | Adjusted by `_positionBaseSeconds` (set on seek). |
| Lock-free snapshot used by the presentation thread | `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:98-122` (`TryGetPositionSnapshot`) | Interlocked fields; extrapolated between calls using `Stopwatch.GetTimestamp()` and the current `Tempo` percent. **This is the value that reaches `AudioPresentationClock`.** |
| Public surface | `FatzBombMedia.MediaControl/Pipeline/AudioPipeline.cs:35-44` (`AudioPipeline.PositionSeconds`) | Thin wrapper over `Engine.Position`. |
| Coordinator surface | `FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs:73-79` (`PositionSeconds`) | Dispatches to `Video.Position` first, then audio, then clock. |

Position is the time origin for video frame selection, not the source of truth
for media time. The presentation clock smooths it.

## 2. Push-buffer

There are two BASS stream topologies in Core; only the video-audio path uses
push.

- **Video-audio (push).** `VideoAudioEngine.CreateBassStreams` at
  `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:652-678` creates
  a push stream with `StreamProcedureType.Push` (line 660), wraps it in a
  tempo stream, and feeds it from the decode thread via
  `Bass.StreamPutData` in `PushAudioFrame` at
  `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:735-785`. The
  decode thread runs from `VideoPipeline.DecodeLoop`
  (`FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:1109-1124`) which
  calls `_audioEngine.DecodeAndPushAudio()`.
  The push stream is throttled by `IsAudioBufferThrottledCore` at
  `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:697-722` with
  4.0 s high / 2.0 s low watermarks
  (`AudioBufferHighWatermarkSeconds` / `AudioBufferLowWatermarkSeconds`,
  lines 14–15). Buffer level is computed as
  `_lastPushedAudioEndSeconds - GetPositionCore()` at lines 724–727.

- **Audio-only (pull).** `AudioEngine` creates a BASS pull stream from file
  via `AudioStreamManager.OpenFromPath` at
  `FatzBombMedia.MediaControl/Core/Audio/AudioStreamManager.cs:81-109` and
  lets BASS manage buffering. No push.

- **BASS global buffer.** The codebase does **not** call
  `BASS_SetConfig(BASS_CONFIG_BUFFER, …)` anywhere. BASS is initialized
  with the implicit default buffer (500 ms on most platforms). This is the
  pull-side buffer and is the largest single contributor to the BASS-reported
  output latency.

The push-stream topology is a *decode* buffer, not an *output* buffer. The
corrective value for A/V sync is the *output* latency (device + pull
buffer + endpoint), not the push-stream fill level. The push buffer's role
is jitter absorption upstream of the pull buffer; it is not a meaningful
signal for "what's at the speaker right now."

## 3. Output latency

`MBass.GetInfo` is called in exactly one place:
`FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs:156` (`Init`). It reads
`BassInfo.SampleRate` only, to confirm BASS is already initialized. It does
not read `BassInfo.Latency`.

No code calls `BASS_GetLatency` or reads `BassInfo.Latency` anywhere. The
output-latency value is therefore available but unused.

What `BassInfo.Latency` contains in ManagedBass:

- The total output latency for the current device, in milliseconds.
- Includes the device buffer and the BASS playback buffer. Does *not* include
  the decode-side push buffer (which is in front of the playback buffer).
- For low-latency ASIO / DirectSound hardware on fast machines: typically
  10–30 ms.
- For WASAPI shared mode: typically 40–120 ms.
- For HDMI / USB DAC endpoint classes: typically 80–250 ms; BASS may
  underreport if the OS layer is in exclusive mode but the endpoint has its
  own buffering.

Heuristic fallback (opt-in only):

- BASS reports `< 10 ms` ⇒ suspect low-latency hardware pull; USB / BT
  endpoints with internal buffering are not visible to BASS. In that
  regime, a conservative per-class offset may be added: USB DAC +50 ms,
  Bluetooth headset +150 ms. The classification is read from
  `Bass.GetDeviceInfo(deviceIndex)` (e.g. `BassDeviceInfo.Name` is the only
  reliable field, so the heuristic is name-pattern based, not
  type-enum-based — `BassDeviceInfo` does not expose an endpoint class
  enum in ManagedBass).
- BASS reports `>= 10 ms` ⇒ trust the value; do **not** apply the
  heuristic. The reported value already covers most of the real delay.
- Heuristic is opt-in via configuration (default off) and never overrides
  a non-trivial BASS-reported value.

## 4. `AudioPresentationClock` interactions

`FatzBombMedia.MediaControl/Pipeline/AudioPresentationClock.cs` (120 lines).
Pure wall-clock estimator:

- Inputs: `audioPositionSeconds` (BASS-reported, smoothed by the caller's
  caller via `VideoAudioEngine.TryGetPositionSnapshot`) and `durationSeconds`.
- Output: a position estimate that converges to the BASS input but tolerates
  jitter via soft correction (12 ms threshold, 12% ratio) and hard
  re-anchor (90 ms threshold).
- Anchored to `Stopwatch` (not audio). Tempo/pitch are not read; this is
  the *visual timeline*, not the audio timeline.

The clock is fed by `VideoPipeline.GetPresentationPosition` at
`FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:1386-1394`:

```csharp
VideoAudioEngine? audioEngine = _audioEngine;
return audioEngine is not null && audioEngine.TryGetPositionSnapshot(out var audioPosition)
    ? _isPlaying
        ? _presentationClock.GetPosition(audioPosition, Duration)
        : audioPosition
    : _clock.PositionSeconds;
```

The returned value drives `PresentFrameForCurrentPosition` (line 1378),
which selects and presents video frames. The presentation clock is the
*video-side* timing source; the audio stream is the time origin only when
video has its own audio.

Clock lifecycle: `Reset(0)` on Open/Close/Stop/Seek; `Start(...)` on
Play/Resume/LoopRestart; `Pause(...)` on Pause. All call sites are in
`VideoPipeline.cs` (lines 204, 261, 339, 342, 381, 405, 476, 509, 792, 1300,
1374). The two-mode contract is preserved: audio-backed media uses BASS
tempo, no-audio visual media uses `PlaybackClock` (`FatzBombMedia.MediaControl/Pipeline/PlaybackClock.cs`).

## 5. Integration seam

The audio position that the presentation clock sees is the single value
that should already include the output-latency correction. Subtracting the
output latency at the snapshot output (rather than inside the presentation
clock) keeps the clock's contract — "estimate a smooth position from a
noisy input" — unchanged and ensures the clock's anchor points are set to
the corrected value the first time `Start` / `Reset` is called.

Concretely, the seam is:

- `VideoAudioEngine.TryGetPositionSnapshot` at
  `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:98-122` —
  the primary, lock-free path used by the presentation thread. Subtract
  the measured output latency from the published `position` before
  returning.
- `VideoAudioEngine.Position` getter at
  `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:84-96` —
  the public surface; must remain consistent with the snapshot so
  host-facing `PositionChanged` events report the corrected value.
- `AudioEngine.Position` getter at
  `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs:118-131` —
  the audio-only path. Apply the same correction for consistency.

The latency provider is a small Core type:

- Interface: `IOutputLatencyProvider` in
  `FatzBombMedia.MediaControl/Core/Abstractions/`.
- BASS-backed implementation: `BassOutputLatencyProvider` in
  `FatzBombMedia.MediaControl/Core/Audio/`, calling `MBass.GetInfo` and
  returning `BassInfo.Latency / 1000.0` as seconds; returns `0` when BASS
  is not initialized. Optionally augments the value when the
  USB/Bluetooth heuristic is enabled and the BASS-reported value is below
  10 ms.
- Provided to the two audio engines via constructor (DI-friendly), so
  unit tests can substitute a deterministic value without touching BASS.

Why the snapshot path, not the presentation clock:

- The presentation clock's soft/hard correction absorbs BASS-reported
  jitter; if we instead subtract latency inside the clock, the anchor
  on the first sample would be wrong (clock would self-correct over a few
  hundred ms, but that initial ramp would show as visible video delay).
- Subtracting at the snapshot means anchors are correct from the first
  sample. The presentation clock continues to behave exactly as it does
  today, with one new constant in its input.
- No WPF adapter changes. The presentation clock is already Core; the
  audio engines are already Core. The latency provider is Core. The
  configuration flag is `MediaPlayerConfiguration` (Core).

## 6. Touch points (no code changes in this substage)

- `FatzBombMedia.MediaControl/Core/Audio/AudioStreamManager.cs:186-195` — no change.
- `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs:98-122, 139-149` — 006A.2 will modify.
- `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs:118-131, 156` — 006A.2 will modify.
- `FatzBombMedia.MediaControl/Pipeline/AudioPresentationClock.cs` — no change.
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs:1386-1394` — no change.
- `FatzBombMedia.MediaControl/Core/Configuration/MediaPlayerConfiguration.cs` — 006A.2 will add a single opt-in flag.

## 7. Risks for 006A.2

- `BassInfo.Latency` is per-device, not per-stream. Re-querying on device
  change (handled in 006B) is out of scope for 006A; cache the first good
  reading and use it for the engine's lifetime. A configuration
  invalidation hook can be added later.
- The heuristic is name-pattern based against `BassDeviceInfo.Name`; it is
  inherently best-effort. Documented as such, opt-in only, and never
  overrides a non-trivial BASS value.
- BASS global buffer (default 500 ms) is the largest single contributor
  to `BassInfo.Latency` on most devices. Changing it is out of scope for
  006A — the plan reads what BASS reports, not what BASS would do with a
  different config.

## 8. 006A.2 — Implementation

### Touch list

- `FatzBombMedia.MediaControl/Core/Abstractions/IOutputLatencyProvider.cs` — new, public interface (`Core/Abstractions/`).
- `FatzBombMedia.MediaControl/Core/Audio/BassOutputLatencyProvider.cs` — new, public sealed class implementing the interface. Reads `MBass.GetInfo().Latency` (ms) divided by 1000.0; applies the opt-in USB / Bluetooth heuristic via the internal `ApplyHeuristic(reportedSeconds, deviceName, enableHeuristic)` static so unit tests can drive the heuristic without BASS.
- `FatzBombMedia.MediaControl/Core/Configuration/MediaPlayerConfiguration.cs` — added `EnableUsbBluetoothLatencyHeuristic` (`init`-only, default `false`).
- `FatzBombMedia.MediaControl/Core/Audio/VideoAudioEngine.cs` — accepts `IOutputLatencyProvider` in ctor; subtracts latency from `Position` getter and from `SeekCore` publish path. `ApplyOutputLatency` floors at 0 and short-circuits on non-positive / non-finite inputs.
- `FatzBombMedia.MediaControl/Core/Audio/AudioEngine.cs` — same provider wiring for the audio-only path; default ctor unchanged, new internal ctor for DI / tests.
- `FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs` — constructs `BassOutputLatencyProvider(configuration.EnableUsbBluetoothLatencyHeuristic)` once and passes it to `new VideoAudioEngine(provider)`.
- `FatzBombMedia.MediaControl/MediaPlayerServiceProvider.cs` — wires the configuration flag into the singleton `IAudioEngine`.
- `FatzBombMedia.MediaControl.Tests/BassOutputLatencyProviderTests.cs` — new, 11 tests covering the heuristic across on/off, threshold edge, USB (mixed case), Bluetooth (mixed case + `(BT)` short form), and unmatched / null / empty device names.

### Integration seam (recap)

Subtraction happens at the audio engine's `Position` / snapshot-publish
output. The presentation clock and the snapshot reader are unchanged; they
already receive the corrected value on the first sample. Anchors set in
`_presentationClock.Start(...)` / `_presentationClock.Reset(...)` are
therefore already correct on the first tick.

## 9. 006A.3 — Validation

- `dotnet build FatzBombMedia.sln` (Debug): clean, 0 warnings.
- `dotnet build FatzBombMedia.sln -c Release`: clean, 0 warnings (warnings-as-errors enabled for the Core project per `Directory.Build.props`).
- `dotnet test FatzBombMedia.sln -c Release --no-build`: **138 passed, 0 failed** (11 new heuristic tests + 127 existing).
- `scripts/check-wpf-boundary.ps1`: passed. `scripts/check-presentation-parity.ps1`: passed.
- WPF `using` directive audit for `FatzBombMedia.MediaControl.Core.*` renderer / pipeline / internal Cdg / D3D11 namespaces: zero matches. The adapter boundary is preserved.
- `dotnet run --project FatzBombMedia.Player -c Release` (8-second alive check): process stays running; no crash on startup, which means the new `IOutputLatencyProvider` and the new `VideoAudioEngine` / `AudioEngine` constructors all wire up without throwing on the launch path.

### Manual smoke (host-driven, per `docs/VALIDATION.md` §3)

Full UI-driven smoke is the operator's responsibility. The set required by
this stage, per the 006A backlog:

1. **Video-with-audio (default device).** A/V sync; no regression.
2. **Video-with-audio (USB DAC class).** Repeat with the heuristic enabled
   (`new MediaPlayerConfiguration { EnableUsbBluetoothLatencyHeuristic = true }`)
   and a device whose name contains "USB"; expect a small lead time (≤ 50 ms)
   and no audio glitches.
3. **Tempo change (BASS tempo, audio-backed).** Change tempo to +20% and -20%;
   A/V sync must hold. The latency correction must not break the
   `_presentationClock.GetPosition` rate-tracking loop.
4. **CDG sync against audio.** Play an MP3+CDG pair; lyrics track the beat.
5. **Audio-only (regression).** `Position` reports a finite, monotonically
   advancing value; the latency-corrected position is `raw - latency` and
   remains `≥ 0`. Audible timing is unchanged because the audio stream
   itself is not modified — only the reported position is.
6. **No-audio video (regression).** `_clock.PositionSeconds` path remains
   the time origin; the `AudioPresentationClock` is not constructed for
   this path. Latency correction has no effect on this path.

If any of these regress, the most likely cause is the latency value being
applied twice (e.g. by both the audio engine and the presentation clock) —
re-check the integration seam in §5.

