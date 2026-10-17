# Stage Update

Stage: 033A.1 - Equalizer, fades, and normalization for video audio
Date: 2026-10-17

## Status: IMPLEMENTED - manual Player smoke pending

Delivered (see the stage file's slice 1 criteria):

- `VideoAudioEngine` now carries the same effects chain as plain audio on its tempo stream:
  equalizer -> normalization gain FX -> fade FX -> channel volume (user volume).
- `BassFadeGainStage` (new, `Core/Audio/Fade`) is the fade stage extracted from
  `AudioEngine` without behaviour change; both engines use it.
- `FfmpegAudioReader` (new) decodes a file's best audio stream to the float PCM format the
  video audio engine plays, on its own demuxer. `FfmpegAudioPeakScanner` (new) runs the
  BASS analysis's quick and deep sampling on it. Slice 2 reuses the reader.
- The equalizer, fades, faded prepared-next transitions, and the Normalize toggle now act
  on video soundtracks through the existing public API; no API was added. The Player's
  Audio panel EQ and Next panel fade buttons work on videos with no Player change.
- Public docs for `Equalizer`, `FadeOutAsync`, and `FadeInAsync` updated.

Tests: 12 focused cases (`VideoAudioEffectsTests.cs`); opt-in native test
`FfmpegAudioReaderNativeTests` (Linux, `FATZBOMB_LINUX_FFMPEG_DIR`).

## Decisions made without the owner

- **Videos are normalized when Normalize is on.** Before this slice, the Normalize toggle
  did nothing for video (gain fixed at 1.0, unchanged since before 031B). Now a video gets
  the same peak-based gain as plain audio: 0.8 / peak, capped at 8x. Most films peak near
  full scale, so they play slightly quieter; very quiet recordings get louder. Turn
  Normalize off to keep the old behaviour.
- The deep pass decodes up to 240 half-second windows across the file in the background
  (at most about 2 minutes of audio, however long the file). As for plain audio,
  the gain switches from the quick estimate to the deep value without a ramp.
- Gain events are raised on the thread pool in order, never on the analysis thread, so a
  host handler cannot deadlock `Close` (which joins the analysis under the engine lock).
- Only the sound fades. A faded transition into or out of a video does not dissolve the
  picture (031B non-goal).
- Silence trimming stays off for video (030B scope: trimming audio alone breaks A/V sync).

## Verification

- Linux: Release build clean; `VideoAudioEffectsTests` 12/12.
- Real FFmpeg 9.0.2 on Linux: `FfmpegAudioReaderNativeTests` passes. On a generated 20 s
  stereo WAV at half scale with a 1 s burst at 0.9, the quick pass reads 0.5 and the deep
  pass finds the 0.9 burst.
- Not runnable here: BASS (the EQ, fade, and gain FX on a live stream). Covered only by the
  manual smoke below.

## Manual smoke (Windows, native DLLs)

1. Video with sound, Audio panel EQ on, raise 63 Hz: audible bass boost; Flat and off are
   neutral; seeking and looping keep the boost. Repeat with `--software`.
2. Next panel, Fade out on a playing video: the sound fades to silence over the FADE length
   while the picture keeps playing; Fade in brings it back; Cancel leaves it where it is.
3. Fade out a video near its end and let it end; press Play: the sound is audible.
4. With Loop on, fade a video out across the loop point: the next lap starts at full volume.
5. Prepare a video as the next item with FADE 4 s and press Play next now from a song: the
   song fades out, then the video's sound fades in.
6. Normalize on: a quiet video plays louder than with Normalize off; a loud film is not
   louder than before. Diagnostics show "Quick normalization complete" then "Deep
   normalization complete" from `VideoAudioEngine`; re-opening the same file shows "Using
   cached normalization gain".
7. Open a long video (1 hour+) and immediately open another: no hang, and the first file's
   gain never applies to the second.
8. A/V sync is unchanged with the EQ on and during fades (watch speech or a clapper).
9. Regression: a plain song still fades, equalizes, and normalizes exactly as before (the
   fade stage moved to a shared class).

## Review follow-up (same day)

The independent review confirmed the lock order (the analysis never takes the engine lock or
runs host code) and the FFmpeg decode loop. Known limits, not changed:

- With Normalize on, play-on-load waits for the quick pass as it does for plain audio (at
  most 2 s; normally well under that for a local file). Videos used to start without it.
- A quiet but not silent opening (peak around 0.05) gives a high quick gain, up to 8x,
  until the deep pass corrects it. Plain audio behaves the same; seeking into loud content
  in those first seconds can be loud.
- Closing or replacing a video waits for the analysis to stop. Its file open cannot be
  cancelled, so on a slow network share that wait can be noticeable.

