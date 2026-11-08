# Stage Update

Stage: 037A.0 - FatzAudio verification spike (package, playback, clock, karaoke measurements)
Date: 2026-11-01

## Status: slice 0 done on Linux - pending the owner's Windows smoke

Every done-when item of slice 0 that Linux can answer passes. Two findings need FatzAudio before
slice 3 (karaoke surfaces): render-ahead at 4K with effects and two streams starved audio decode
(the clocks lost 170-580 ms in 30 s, which means underruns), and full redraws on the caller still
stall for 50-320 ms at 4K. Slices 1 and 2 are not affected. No code changed in either repo, and no
project was added to the solution. All spike code and media are outside both repos.

## What was built and how

FatzAudio `main` @ 17d2284 (PR 170), version 0.2.0 (`FA_VERSION_*`). Linux sandbox: Ubuntu 24.04,
glibc 2.39, GCC 13.3.0, CMake 3.28.3, Ninja, .NET SDK 10.0.403 (runtime 10.0.14), 4 shared vCPUs.
FatzAudio's null output was used. There was no audio hardware.

1. **Native library** (Release, `-O3`, LTO on, built out of tree in the existing build folder):
   `cmake -S /home/user/fatzbomb75/fatzaudio -B /root/fa-build -G Ninja -DCMAKE_BUILD_TYPE=Release`
   (already configured), then `ninja -C /root/fa-build fatzaudio`. Output:
   `bin/libfatzaudio.so.0.2.0`, 5.1 MB, sha256 `9dadc3a3...ba763`. It needs only `libc` and
   `libm`, but because it was built on 24.04 it needs **glibc 2.38** (CI builds on 22.04 for 2.35).
2. **Package**: the read-only checkout stays untouched. I cloned it to `/root/fa-src` (same commit),
   copied the library to `bindings/dotnet/src/FatzAudio/runtimes/linux-x64/native/libfatzaudio.so`
   and ran
   `dotnet pack bindings/dotnet/src/FatzAudio -c Release --version-suffix local.17d2284 -o /root/fa-feed`.
   Result: `/root/fa-feed/FatzAudio.0.2.0-local.17d2284.nupkg` (sha256 `d8d060e6...6e5f`), with
   `lib/net8.0` and `lib/net10.0` `FatzAudio.Net.dll` and `runtimes/linux-x64/native` only. There is
   no win-x64 native, because it cannot be built here.
3. **Spike** (`/root/fa-spike`): a `net10.0` console app. Its `nuget.config` clears every source
   except `/root/fa-feed`, and it pins the exact version with
   `<PackageReference Include="FatzAudio" Version="[0.2.0-local.17d2284]" />`.
   `dotnet build Spike -c Release`, then `dotnet Spike/bin/Release/net10.0/linux-x64/Spike.dll <mode>`
   (`version`, `basic`, `sjis`, `decodeonly`, `twostreams`, `surfaces`). Raw output is in
   `/root/fa-spike/results/`.
4. **Media**: `/opt/fatzbomb-native/media` has no MIDI or CD+G, and FatzAudio's tests have no
   lyric song, so `tools/gen_media.py` generates them:
   - `song.kar`: format 1, a `Words` track, 48 lines in 12 paragraphs, about 213 s.
   - `sjis.kar`: the same layout, 12 Japanese lines encoded in cp932.
   - `song.cdg`: 180 s; a 4-line page every 15 s, drawn with tiles and wiped with XOR tiles; a
     25 Hz palette cycle from 150 to 160 s.

   `ffmpeg` made `song.mp3` (180 s, next to `song.cdg`) and `loop.wav` (6 s). The soundfont is
   FatzAudio's 3.6 KB fuzz seed `tests/fuzz/seeds/soundfont/bank.sf2`, because this machine has no
   GM bank. MIDI synthesis cost is therefore far below a real bank's.

## Done-when items

| Item | Result | Evidence |
|---|---|---|
| A test project references the `FatzAudio` .NET package (built from source until the token exists) | **Pass** | Restores the exact-pinned `0.2.0-local.17d2284` from the local folder feed; native library resolved from `runtimes/linux-x64/native` |
| Opens a file, plays to a null device, seeks, loops, reads the clock on Linux | **Pass** | Next table |
| The binding's native version check passes against the pinned version | **Pass** | `BindingVersion 0.2.0`, `NativeVersion 0.2.0`, `AudioEngine.Create` OK. Mismatch behaviour below |
| Karaoke measurements Linux can produce are recorded | **Pass** (palette upload MB/s, A/V skew and Bluetooth/USB are not measurable here) | Tables below |
| Decide how CI gets the package | **Recommended**; owner setup needed | Last section |
| Shift-JIS lyrics (037A known gap) | **Pass** | Below |

### Playback and clock (MP3 and WAV, null output, `Clock` sampled every 5 ms against `Stopwatch`)

| Check | Result |
|---|---|
| Steady play, 8 s | Slope 0.999999 (-1 ppm); residual RMS 1.1 ms, max 2.9 ms (null-device callback granularity); never stepped backwards |
| Seek, `Position =` while playing | Call 2-6 ms; `Clock` reads 100.000 at once, holds about 35-40 ms (buffer refill plus device buffer), then runs at 1.0 |
| Seek, `RequestSeek` | Call 1.3 ms; `Clock` reads the target at once and starts moving after 35-41 ms; afterwards 43 ppm, max deviation 2.4 ms |
| Pause | `Clock` runs on for 32-37 ms (audio already in the device buffer), then holds exactly; `State` is Paused |
| Resume | Clock is 35-38 ms behind wall time after 1 s (restart latency), then runs at 1.0 (-10 ppm) |
| Tempo 1.25 | Clock slope 1.249999 (source time, as documented) |
| Loop, whole 6 s file, played from 4 s for 13 s | 2 `Looped` events (one per wrap). Clock jumps 5.9995 -> 0.0017 and 5.998 -> 0.0001; unwrapped clock against wall time within 2.65 ms. Each event is raised about 35-40 ms before the wrap is heard (as documented) |
| Loop, cue region 30-32 s (`Markers.CueIn/CueOut` + `Loop`) | 3 `Looped` events in 5.2 s, clock stays within 30.000-32.000 |

### Version check and mismatch

- Pinned package and its own native library: `AudioEngine.Create` succeeds. The informational
  version is `0.2.0-local.17d2284+17d22848...`.
- I copied the spike output and swapped in a stand-in `libfatzaudio.so` whose `fa_version` returns
  0.3.0. `AudioEngine.NativeVersion` still reports 0.3.0, because it does not check. The first real
  call (`AudioEngine.Create`) throws `InvalidOperationException`: "The native fatzaudio library is
  version 0.3.0, but this FatzAudio package needs 0.2.x. Use the native library from the same
  package." This matches the binding's documentation.
- A stand-in reporting 0.2.7 passes the check, because patch versions may differ (the call then
  fails with `EntryPointNotFoundException` only because the stand-in has no other exports).

### Shift-JIS

`sjis.kar` was opened decode-only with the test bank. `MidiCredits.CodePage` is `ShiftJis`, the
title reads `スパイク`, and all 66 lyric events match the cp932 source text exactly (no U+FFFD). A
lyric surface drawn with IPA Gothic (`LyricStyle.FontPath`) renders the kana and kanji correctly
(`/root/fa-spike/results/sjis_frame.png`). ENG-031's Shift-JIS item is fixed in FatzAudio 0.2.

### Soundfonts and decode-only streams (BASS-coexistence mode)

- No GM bank exists on this machine (`/usr/share/sounds/sf2`, `/usr/share/soundfonts` are missing).
  `SoundFont.FromSystem()` returns **null** (it does not throw).
- Opening `song.kar` decode-only without a soundfont throws
  `FatzAudioException: no soundfont for MIDI (NoSoundFont)`; `NoSystemSoundFont` gives the same.
  So in BASS-coexistence mode a lyric surface needs a soundfont even though no audio is rendered
  (request 3 is still open).
- With the test bank, a decode-only KAR opens in 5.8 ms and an MP3 with CD+G in 1.2 ms
  (`HasCdg` true). Both surfaces draw with explicit times from a simulated host clock, and
  render-ahead works on decode-only streams (frames report `Behind`).

## Karaoke surface measurements

Setup: one surface per run, drawn by a dedicated thread (`karaoke`, AboveNormal priority) at
60 Hz, `UpdateInto` called through the binding into 3 native buffers in rotation. The explicit
time is `Clock + 1/60 s × Tempo`. CD+G is scaled (`Presentation.Scale`), BGRA.
- **Effects off**: no `Effects`; for CD+G also Smoothness 0.
- **Effects max**: outline (size 1), shadow (size 1, distance 1, opacity 1) and bevel
  (strength 1, size 1); for CD+G also Smoothness 10 with edge blend.

Each run follows the same 18.5 s timeline:

| Phase | What happens | Frames counted |
|---|---|---|
| steady | Normal play | All frames not in another phase |
| page | Page build. CD+G: clear and redraw (the first 1.3 s of each page). Lyrics: 1.5 s before to 0.5 s after each paragraph start | Those windows |
| seek | `RequestSeek` at 4 s | 20 frames |
| loop | 1.5 s cue loop at 7 s, 2 wraps | 20 frames after each wrap |
| resize | `PresentationSize` to 3/4 at 11 s and back at 12.5 s | 20 frames after each change |
| tempo | Tempo ramp 1.0 -> 1.3 over 3 s, then back over 1 s | From 14 s to the end |
| palette | CD+G clock in 150-160 s (palette cycling) | Those frames |

Times are wall-clock times of the `UpdateInto` call in ms, as p99 / max.

| Config | First frame | Steady | Page | Seek | Loop | Resize | Tempo ramp | Palette |
|---|---|---|---|---|---|---|---|---|
| CD+G classic (Indexed8) 1080p | 2.3 | 0.2 / 0.3 | 0.7 / 0.7 | 0.1 / 0.1 | 0.2 / 0.2 | 0.0 / 0.0 | 0.1 / 0.1 | 0.1 / 0.5 |
| CD+G 1080p, fx off, ahead off | 7.8 | 5.6 / 6.1 | 1.4 / 1.4 | 1.7 / 1.7 | 2.2 / 2.2 | 5.4 / 5.4 | 2.2 / 3.0 | 2.3 / 4.4 |
| CD+G 1080p, fx off, ahead on | 9.2 | 1.2 / 1.3 | 1.3 / 1.3 | 2.3 / 2.3 | 4.1 / 4.1 | 6.7 / 6.7 | 2.6 / 2.9 | 3.0 / 4.1 |
| CD+G 1080p, fx max, ahead off | 33.1 | 9.9 / 9.9 | 15.0 / 15.0 | 15.7 / 15.7 | 13.0 / 13.0 | 33.5 / 33.5 | 15.5 / 17.6 | 15.1 / 16.2 |
| CD+G 1080p, fx max, ahead on | 41.2 | 6.7 / 8.0 | 1.9 / 1.9 | 15.1 / 15.1 | 14.1 / 14.1 | 37.4 / 37.4 | 13.5 / 14.5 | 13.1 / 18.7 |
| CD+G 4K, fx off, ahead off | 29.2 | 19.7 / 21.8 | 4.4 / 4.4 | 5.9 / 5.9 | 6.6 / 6.6 | 29.3 / 29.3 | 3.9 / 5.0 | 4.9 / 11.5 |
| CD+G 4K, fx off, ahead on | 29.4 | 14.3 / 17.7 | 4.9 / 4.9 | 9.4 / 9.4 | 13.3 / 13.3 | 24.4 / 24.4 | 5.2 / 5.7 | 5.6 / 11.1 |
| CD+G 4K, fx max, ahead off | 75.0 | 26.7 / 26.8 | 27.5 / 27.5 | 34.6 / 34.6 | 37.8 / 37.8 | 86.9 / 86.9 | 51.7 / 72.0 | 38.2 / 41.3 |
| CD+G 4K, fx max, ahead on | 75.7 | 32.4 / 35.7 | 34.9 / 34.9 | 30.4 / 30.4 | 44.3 / 44.3 | 79.9 / 79.9 | 47.7 / 69.1 | 45.5 / 53.1 |
| Lyrics 1080p, fx off, ahead off | 14.3 | 5.1 / 39.0 | 2.4 / 6.2 | 30.0 / 30.0 | 3.5 / 3.5 | 31.1 / 31.1 | 5.2 / 6.1 | - |
| Lyrics 1080p, fx off, ahead on | 18.3 | 0.6 / 1.6 | 0.3 / 0.5 | 38.9 / 38.9 | 4.4 / 4.4 | 25.1 / 25.1 | 4.9 / 6.0 | - |
| Lyrics 1080p, fx max, ahead off | 29.4 | 7.5 / 67.4 | 4.5 / 13.8 | 60.3 / 60.3 | 6.4 / 6.4 | 55.7 / 55.7 | 7.2 / 8.2 | - |
| Lyrics 1080p, fx max, ahead on | 42.1 | 1.0 / 2.9 | 0.5 / 0.6 | 51.7 / 51.7 | 7.5 / 7.5 | 39.6 / 39.6 | 7.2 / 9.3 | - |
| Lyrics 4K, fx off, ahead off | 92.5 | 23.4 / 142.1 | 12.0 / 40.6 | 136.6 / 136.6 | 12.5 / 12.5 | 96.2 / 96.2 | 19.8 / 24.9 | - |
| Lyrics 4K, fx off, ahead on | 103.9 | 3.0 / 23.0 | 1.1 / 1.3 | 102.2 / 102.2 | 17.1 / 17.1 | 126.5 / 126.5 | 22.0 / 22.2 | - |
| Lyrics 4K, fx max, ahead off | 114.2 | 24.0 / 240.4 | 13.6 / 51.1 | 316.2 / 316.2 | 21.3 / 21.3 | 175.9 / 175.9 | 30.4 / 32.6 | - |
| Lyrics 4K, fx max, ahead on | 169.3 | 3.0 / 243.3 | 2.1 / 3.2 | 230.3 / 230.3 | 29.2 / 29.2 | 198.3 / 198.3 | 35.1 / 40.2 | - |

Steady play in detail (p50 / p99 / max in ms; frames drawn of about 1,110 in 18.5 s; engine
worker load):

| Config | Steady p50 / p99 / max | Frames drawn | Worker load | Frames over 8 ms (all phases) | Behind frames |
|---|---|---|---|---|---|
| CD+G classic 1080p | 0.01 / 0.21 / 0.28 | 1111 | 0.9% | 0 | 0 |
| CD+G 1080p fx off, ahead off / on | 0.02 / 5.6 / 6.1 ; 0.02 / 1.2 / 1.3 | 1110 / 1108 | 1.6% / 3.9% | 0 / 1 | 0 / 0 |
| CD+G 1080p fx max, ahead off / on | 0.02 / 9.9 / 9.9 ; 0.09 / 6.7 / 8.0 | 1105 / 1100 | 32% / 56% | 343 / 48 | 0 / 0 |
| CD+G 4K fx off, ahead off / on | 0.01 / 19.7 / 21.8 ; 0.02 / 14.3 / 17.7 | 1102 / 1102 | 3% / 9% | 6 / 11 | 0 / 0 |
| CD+G 4K fx max, ahead off / on | 0.01 / 26.7 / 26.8 ; 0.32 / 32.4 / 35.7 | 698 / 697 | 75% / 133% | 458 / 157 | 0 / 0 |
| Lyrics 1080p fx off, ahead off / on | 1.69 / 5.1 / 39.0 ; 0.30 / 0.64 / 1.6 | 1105 / 1101 | 1% / 10% | 7 / 4 | 0 / 13 |
| Lyrics 1080p fx max, ahead off / on | 2.38 / 7.5 / 67.4 ; 0.36 / 1.0 / 2.9 | 1089 / 1100 | 1% / 16% | 13 / 6 | 0 / 11 |
| Lyrics 4K fx off, ahead off / on | 5.74 / 23.4 / 142 ; 0.78 / 3.0 / 23.0 | 1036 / 1045 | 1% / 32% | 358 / 64 | 0 / 37 |
| Lyrics 4K fx max, ahead off / on | 5.57 / 24.0 / 240 ; 0.30 / 3.0 / 243 | 949 / 932 | 1% / 42% | 358 / 54 | 0 / 96 |

Decode-only streams with explicit times (simulated host clock), 4K, max effects, 8 s runs
(p99 / max in ms):

| Config | First frame | Steady | Page | Seek (time jump) | Palette |
|---|---|---|---|---|---|
| Lyrics, ahead off | 203 | 124 / 420 | 13 / 64 | 228 / 228 | - |
| Lyrics, ahead on | 166 | 36 / 311 | 2.0 / 2.3 | 278 / 278 | - |
| CD+G, ahead off | 88 | 25 / 26 | 24 / 24 | 40 / 40 | 32 / 36 |
| CD+G, ahead on | 88 | 18 / 25 | 35 / 35 | 37 / 37 | 41 / 44 |

**Whether the call blocked:** wall time minus the reported `draw_ns` is at most 0.1 ms on every
frame except the first call of a process (JIT, 0.5-6 ms). The binding adds nothing measurable, and
`draw_ns` is a faithful measure of how long our thread is held. The public numbers cannot tell
whether a long frame was a draw on the caller or a wait on a worker (`work_lock`). With
render-ahead, `render_ns` reports the worker's time, so a long `draw_ns` with a short frame shown
points to the caller drawing itself.

**Managed allocations:** 0 bytes per `UpdateInto` in steady state in every configuration
(`GC.GetAllocatedBytesForCurrentThread`). The first call allocates 40 B.

### Audio underruns with render-ahead (two streams, 4K, max effects)

The MP3+CD+G and KAR streams play together on the null device for 30 s with no seeks. Each
surface has its own thread drawing at about 60 Hz. The stream clocks were compared with wall
time. FatzAudio counts underruns per stream (`stream.c:4699`) but does not expose the count, and
it raises `Stalled` only for push, packet and web streams. So a clock that falls steadily behind
wall time is the evidence: frames the mixer did not get are not counted in the clock.

| Surfaces | Run | Clock vs wall, max deviation | End offset after 30 s | Worker load |
|---|---|---|---|---|
| None | 1 / 2 | 2.8-3.1 ms | 0.0 / +3.1 ms | 0.8% |
| CD+G + lyrics, ahead off | 1 / 2 | 14.1 / 5.9 ms | +0.2 / +1.5 ms | 14-20% |
| CD+G + lyrics, ahead on | 1 | CD+G 4.0, KAR **183.9 ms** | KAR **-181 ms** | 90% |
| CD+G + lyrics, ahead on | 2 | CD+G **170 ms**, KAR **580 ms** | CD+G **-169 ms**, KAR **-579 ms** | 118% |

Render-ahead at 4K with effects and two streams starved decoding on this 4-vCPU machine. Both
runs produced audible-scale gaps, contrary to the analysis's "low risk to audio [I]". Without
render-ahead the audio held (caller stalls up to 157-301 ms instead).

## Surprises

1. **Render-ahead takes decode time from audio** (previous table). Render-ahead jobs are meant
   to run only when no stream needs decoding (`engine.c:162-205`). Even so, with 4K effects
   frames of 30-70 ms each keeping the workers 90-118% busy, decoding fell behind [I: the exact
   cause is not visible through the public API]. This is a new request for FatzAudio: let decode pre-empt render-ahead, or cap render-ahead's
   share of workers, and expose a per-stream underrun count (for example in `EngineStats` or a
   stream stat).
2. **CD+G render-ahead barely helps.** Scaled CD+G never reported `Behind`, and at 4K its p99 did
   not drop (26.7 -> 32.4 ms with effects). Each change to the CD+G picture is still drawn on the
   caller. Lyrics benefit a lot (steady p99 24 -> 3 ms at 4K).
3. **Full redraws still stall the caller** with render-ahead: lyric seek 230 ms, resize 198 ms
   and first frame 169 ms at 4K with effects; CD+G resize 80 ms. This is request 1 (never block),
   now measured through .NET.
4. **4K with effects cannot hold 60 Hz here.** Scaled CD+G at 4K with max effects drew 698 of
   about 1,110 frames, and 4K lyrics 932-1,045. The 1080p configurations hold 60 Hz.
5. **Tempo ramp:** render-ahead slots are keyed to tempo, but render-ahead still cut the lyric
   ramp cost at 1080p (p50 2.5 -> 0.4 ms). At 4K the ramp p99 is 22-35 ms either way.
6. `SoundFont.FromSystem()` returns null where there is no bank, while opening a MIDI file throws
   `NoSoundFont`. Check for null.
7. A library built on Ubuntu 24.04 needs glibc 2.38. Use CI's 22.04 build for anything shipped.
8. Harness note: runs started detached (`&` or a background task) stalled in the spike's
   `Thread.Sleep` pacing loop in this sandbox and never finished (cause not found; no FatzAudio
   frame was on the stack). All numbers above come from foreground runs.

## Not measurable on Linux (owner's Windows smoke)

- The win-x64 native and the package with both natives (CI's `nuget` artifact): the version check
  and a file playing on WASAPI.
- The clock on a real device: latency, device change, and Bluetooth/USB against our heuristic.
- A/V skew between the video frame and the karaoke layer.
- GPU upload cost: palette-cycling MB/s in scaled CD+G (CPU cost is in the Palette column), and
  D3D11 dirty-rect uploads.
- MIDI with the real `gm.dls` (the system bank on Windows): open time, lyric-surface open, and
  synthesis load next to 4K render-ahead (here the test bank costs almost nothing, so the
  underrun result is a lower bound).
- The same 4K numbers on the owner's CPU (this sandbox has 4 shared vCPUs).

## CI package access (recommendation)

Done 2026-11-01: the owner added FatzBombMedia with Read under the package's Manage Actions
access, so option 1 applies; slice 2 adds the workflow permission and `nuget.config`.

1. **Use the GitHub Packages feed with the workflow's own `GITHUB_TOKEN`.** The owner opens the
   `FatzAudio` package's settings on GitHub (Package settings, Manage Actions access) and adds
   `FatzBomb75/FatzBomb.MediaControl` with Read. Our workflows then declare
   `permissions: packages: read`, and the `nuget.config` from FatzAudio's `docs/building.md` reads
   the token from an environment variable set to `${{ secrets.GITHUB_TOKEN }}`. No personal token
   is stored, and it works for pull requests from this repository. I could not check this setting
   from here. If it does not take for NuGet packages, store a classic personal access token with
   only `read:packages` as the repository secret `FATZAUDIO_PACKAGES_TOKEN` (fine-grained tokens
   cannot read GitHub Packages).
2. **Pin an exact version** in `Directory.Packages.props`: the stable `0.2.0` once the owner
   publishes it (FatzAudio's `build` workflow, "release"). Until then, pin an exact
   `0.2.0-ci.N`, not `0.2.0-ci.*`, because a minor bump before 1.0 may break the API. Add
   `packageSourceMapping` so only `FatzAudio` comes from that feed.
3. **Developers** set `FATZAUDIO_PACKAGES_TOKEN` (classic, `read:packages`) locally. Without a
   token, a folder feed with CI's `nuget` artifact (or a package packed as above) works.
4. The package stays out of the solution until slice 2 adds
   `FatzBomb.MediaControl.Audio.FatzAudio`. Slice 1 (the seam) needs no package.

## Manual smoke (owner, Windows)

1. Download a FatzAudio CI run's `nuget` artifact (or `0.2.0` once published) into a folder feed.
   Reference it exact-pinned from a scratch `net10.0` console app outside the repo.
2. Print `AudioEngine.NativeVersion` and create an engine on the default device. Play an MP3 for
   10 s, seek, pause and resume, and loop a 6 s file. Check the `Clock` against what you hear, and
   that there is one `Looped` per wrap.
3. With a real CD+G song and a KAR (gm.dls present), draw a 4K scaled CD+G surface with effects
   and a lyric surface with render-ahead on a karaoke thread. Listen for dropouts while both
   play, and compare the frame times with the tables above.

## Next

- Send FatzAudio the new findings: render-ahead must not starve decoding (surprise 1); expose
  per-stream underruns; CD+G render-ahead effectiveness (surprise 2); measured stalls for
  request 1; lyrics without a soundfont (request 3).
- Slice 1 (seam, no behaviour change) can start. Slice 3 waits on the render-ahead finding.
