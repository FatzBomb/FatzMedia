# Stage Update

Stage: 037A - Loudness (LUFS) normalization on the FatzAudio engine (BASS unchanged)
Date: 2026-11-04

## Status: implemented, pending manual smoke

With `--audio=fatz` (`MediaPlayerConfiguration.AudioEngineFactory = new FatzAudioEngineFactory()`),
normalization is FatzAudio's loudness normalization and true-peak limiter. Our peak rule (quick and
deep pass on a decode-only stream, gain on the mix bus) is gone from the FatzAudio engine. The BASS
engine, the default, is unchanged and ignores the new options.

## Decisions (owner, 2026-11-03/04)

- **FatzAudio does all the audio work.** It measures, applies the gain and limits; MediaControl only
  passes the settings and reports the gain. Nothing on our side waits for the measurement.
- **Setting:** `MediaPlayerConfiguration.Normalization` (`NormalizationOptions`, a code setting, no
  Player flag): `TargetLufs` (default -14, the streaming standard; -60..0), `TruePeakCeilingDbtp`
  (default -1; -20..0) and `CacheDirectory`. `default(NormalizationOptions)` is the default (values
  stored as offsets, like `SilenceTrimOptions`). The existing `Normalize` switch turns it on and off.
- **Boost cap:** FatzAudio's fixed -30..+12 dB; no setting.
- **Cache on by default:** `null` uses `%LOCALAPPDATA%\FatzBomb.MediaControl\AudioCache` (Windows) or
  `$XDG_CACHE_HOME/fatzbomb-mediacontrol/audio`, else `~/.cache/fatzbomb-mediacontrol/audio`; an
  empty string turns it off. FatzAudio writes one 39-byte file per song (integrated loudness, range,
  true peak; no path or tags), keyed by content (size plus the first, middle and last 64 KB). Named
  `AudioCache` because FatzAudio keeps its other analysis results in the same folder.
- **First play:** starts at once on FatzAudio's running estimate and glides (12 dB/s down, 6 dB/s up)
  to the measured value. A short start wait with a provisional value from the partial scan belongs in
  FatzAudio: request 12, `docs/fatzaudio/2026-11-04_MediaControl_Request_NormalizationStart.md`. When
  it exists, it is one more option here.

## Measured (Linux, 4-core 2.1 GHz container, null output, -14 LUFS)

Open to `LoudnessFinal`, median of 5:

| File | rc.1 (pinned) | rc.1, 2 cores | FatzAudio main (PR 201) |
|---|---|---|---|
| 4 min MP3 192k | 208 ms | 387 | 174 |
| 4 min FLAC | 169 | 335 | 140 |
| 4 min AAC | 245 | 389 | 233 |
| 4 min Ogg Vorbis | 261 | 373 | 219 |
| 4 min Opus | 343 | 462 | 321 |
| 4 min WAV | 146 | 201 | 106 |
| 10 min MP3 | 520 | 712 | 435 |
| 60 min MP3 | 2942 | - | 2486 |
| Tagged (ReplayGain) or cached | 0-2 | - | - |

Cache checks: a quiet file replaced by a normal one under the same name is scanned again (correct);
a rename is a hit. A same-size WAV edited in place outside the three fingerprint blocks returns the
old value (accepted; noted in request 12).

## What changed

| Where | What |
|---|---|
| Core `Api/NormalizationOptions.cs` (public, new) | The options above. |
| Core `MediaPlayerConfiguration.Normalization` (public, new) | The setting. |
| `FatzAudioEngine` | Engine created with the cache folder; `SetLimiter(Auto, ceiling)`; `SetNormalization(Track or Off, target)` from `Normalize`; streams open without `NoNormalize`; the bus carries only the user volume. `NormalizationGain` is the open stream's `GainDb` as a multiplier (with `Normalize` off, the gain it would apply: target minus loudness within -30..+12 dB, as BASS reports its gain whatever the switch); `NormalizationGainChanged` fires once per open when the loudness is final (also when tags or the cache answered before the stream was current); `IsNormalizationReady` is always true. The peak analysis task, the shared gain cache use and the slide bookkeeping it needed are removed. `AnalyzePeakQuickAsync`/`AnalyzePeakAsync` (on demand, `IAudioEngine`) still measure the peak. |
| `FatzAudioMappings` | `ResolveCacheDirectory` (relative paths made absolute), `TrackGainDecibels`, `GainFromDecibels` (pure). |
| Docs | `docs/site/articles/audio/normalization.md` (new section), `audio-engine.md`, the package README, `CURRENT_ARCHITECTURE.md`, 037A. |

Not changed: BASS, `MediaFile`'s play-on-load (it returns at once because the engine is ready), the
test players (the target is a code setting), the package pin (rc.1; FatzAudio's faster scan arrives
with the pin move in slice 3).

## Tests

- Portable: `NormalizationOptionsTests` (defaults, clamps, the configuration default).
- Linux, real FatzAudio library: `Normalization_IsFatzAudiosLoudness_AtTheConfiguredTarget_AndCached`
  (a -20 LUFS tone gets x2 at -14, the bus keeps the user volume, off and on again, the cache folder
  written, a new engine reading the cache at -20 LUFS gets x1 with one event);
  `FatzAudioMappingsTests` (cache folder rules, dB to gain). Engine tests turn the cache off unless
  they set their own folder, so no test writes to the user's cache.

## Verification on Linux

FatzAudio 1.0.0-rc.1 built from source (59741a5) and packed locally, as in `updates/037A_2` (this
sandbox has no package token). Release build 0 warnings; Portable 1766, Linux 145, Vulkan 102 and
Avalonia 47 tests pass; `dotnet format --verify-no-changes` and the four boundary scripts pass. No
test wrote to the user's cache folder. Not verified here: Windows, WASAPI, a real device, by ear.

## Review notes (pre-PR, 2026-11-04)

Fixed before the PR: the post-open loudness check can no longer fail a good open (it runs inside
the event guard); `NormalizationGain` with `Normalize` off reports the gain FatzAudio would apply
(as BASS reports its gain whatever the switch), not 1; a relative `CacheDirectory` is made absolute
when the player is created.

## Manual Windows smoke (owner)

Run from the repository root in PowerShell, with the BASS and FFmpeg DLLs in each player's `native\` folder as today. Media in `C:\media`; pick a loud modern master, a quiet
older track (or classical) and one with ReplayGain tags if you have it.

```powershell
cd C:\development\nuget\FatzbombMedia
git fetch origin
git checkout claude/modest-clarke-5i5piu
git pull
$env:FATZAUDIO_PACKAGES_TOKEN = '<your read:packages token>'
dotnet build FatzBomb.MediaControl.sln -c Release
```

1. **Clean first play.** Clear the cache so every song is a first play:

   ```powershell
   Remove-Item "$env:LOCALAPPDATA\FatzBomb.MediaControl\AudioCache" -Recurse -Force -ErrorAction SilentlyContinue
   dotnet run --project FatzBomb.MediaControl.Player -c Release -- --audio=fatz
   ```

   Play the loud song, then the quiet one, switching back and forth. Expect: both at about the same
   perceived loudness; the loud one turned down, the quiet one up. The Log shows
   `FatzAudio 1.0.0 ready ... normalization -14 LUFS, limiter -1 dBTP, cache: ...AudioCache` and,
   per song, `Loudness -x.x LUFS, true peak -x.x dBTP (Scan); normalization gain ±x.x dB`.
   On a first play, listen to the first second: any correction should be a smooth glide, not a jump.
2. **Cache.** Close the player and check the folder:

   ```powershell
   Get-ChildItem "$env:LOCALAPPDATA\FatzBomb.MediaControl\AudioCache" | Format-Table Name, Length
   ```

   Expect one `*.loudness` file (about 39 bytes) per song played. Start the player again with
   `--audio=fatz` and play the same songs: the Log says `(Cache)` and the level is right from the
   first sample.
3. **Normalize off and on.** Toggle Normalize while the quiet song plays. Expect: off plays it at its
   own (quiet) level, on glides back up within about a second; no click.
4. **Tags.** Play a song with ReplayGain tags: the Log says `(Tags)`.
5. **Loud and boosted peaks.** Play the quiet song with a loud section (or a classical track). Expect
   no clipping or crackle at the loud parts (the limiter holds -1 dBTP).
6. **Archive.** Play an MP3+G or audio ZIP entry: normalized like a file (`(Scan)` then `(Cache)`).
7. **BASS unchanged.** Run without the flag and play the same songs:

   ```powershell
   dotnet run --project FatzBomb.MediaControl.Player -c Release
   ```

   Expect the BASS behaviour as before (peak rule: `Quick normalization complete`, `Deep
   normalization complete` in the log). Levels differ from FatzAudio by design.
8. **Avalonia and WinForms.** Repeat step 1 briefly with each:

   ```powershell
   dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -- --audio=fatz
   dotnet run --project FatzBomb.MediaControl.Player.WinForms -c Release -- --audio=fatz
   ```

Report: each step pass/fail, and for step 1 whether a first play's start was noticeably off or
gliding (that decides how urgent request 12 is).
