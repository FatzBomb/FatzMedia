# Idea Stage 037A - FatzAudio As A Second Audio Engine

Status: Idea. Written 2026-10-20 from FatzAudio's answer to our two notes ("answer to
FatzBomb.MediaControl", checked by FatzAudio against its `main` @ c6e4446, PR 78). That note is
FatzAudio's own claim: nothing here is verified against the package until slice 0 runs. The
local checkout at `/home/user/fatzbomb75/fatzaudio` is older (8849f04, PR 65).

Goal: an opt-in `FatzAudioEngine` next to the BASS engines, selected by configuration (and a
Player flag), with BASS kept as the default and the fallback until the owner decides otherwise.
It is a library swap behind `IAudioEngine`, not a rewrite of playback, timing or composition.

## What FatzAudio says it now covers

Blockers 1-5 (EQ and user DSP, loop on/off with a `Looped` event, tempo/rate/pitch on push and
packet streams, tracker modules with exact seek and order/row position, MIDI with reverb/chorus,
SysEx, tempo/time/key marks, SF3 and direct DLS loading); parity 6-11 (default-device following
with `DeviceChanged`, levels and spectrum, tags and stream info, async seek, a lock-free clock);
all six CD+G surface asks; a GitHub Packages NuGet feed (`FatzAudio` 0.2.0-ci.N, win-x64 and
linux-x64, net10.0). Raw CD+G packs were deliberately not built.

## Fit with this repo

- `IAudioEngine` leaks BASS integer handles (`PlayHandle`, `SourceHandle`). Consumers use them
  for MIDI lyrics (`MidiLyricExtractor`), the fade gain stage, the EQ filter, normalization and
  silence analysis. A FatzAudio engine has no such handles, so those consumers need a seam first.
- `VideoAudioEngine` (1,107 lines) pushes decoded FFmpeg audio into a BASS push stream and reads
  the position back for A/V sync. FatzAudio's push/packet streams with tempo and a clock replace
  it, but this is the timing-critical path: last, and gated by owner smoke.
- Core must not reference FatzAudio. The engine lives in a new package
  (`FatzBomb.MediaControl.Audio.FatzAudio`); `scripts/check-core-boundary.ps1` pins the
  reference DAG and is updated deliberately.

## Slices

0. **Verify (spike).** Pull `FatzAudio` 0.2.0-ci.N; a test project opens a file, plays it to a
   null/dummy device, seeks, loops and reads the clock on Linux. Decide how CI authenticates to
   GitHub Packages (a `read:packages` secret). Check FatzAudio's header version check against
   the pinned package. Nothing else starts until this passes.
1. **Seam, no behaviour change.** Remove BASS handles from the engine contract: lyrics from the
   file (or a FatzAudio-neutral reader) instead of a source handle; fade, EQ, normalization and
   silence analysis behind small interfaces that the BASS engines implement as today. BASS output
   must stay bit-for-bit as before; existing audio tests pass unchanged.
2. **FatzAudioEngine for files and archives.** Open (path, archive entry), play/pause/stop/seek,
   volume, pitch, tempo, loop with a per-wrap `PlaybackEnded`/`MediaLooped`, position from
   `fa_stream_get_clock`, EQ from the built-in 16 bands, fades from volume ramps, device
   following. Opt-in: `MediaPlayerConfiguration` audio engine choice and Player `--audio=fatz`.
3. **MIDI, trackers, network.** Soundfont/DLS direct (drops the DLS-to-SF2 step and ENG-028),
   MIDI texts/marks for lyrics, `GetTrackerTime` for the tracker timeline, network playback.
4. **Video audio.** Push or packet streams with FatzAudio tempo and clock replacing the BASS
   push stream; the video clock follows `fa_stream_get_clock`. Owner smoke for sync at tempo
   0.75-1.5, seeks, loop wraps and pause/resume.
5. **Decision (separate from the build).** Whether FatzAudio becomes the default, and whether
   BASS is then removed.

## Karaoke presentation features (requested from FatzAudio 2026-10-20)

See `docs/fatzaudio/2026-10-20_MediaControl_Request_KaraokeSurfaces.md`. Whoever renders them,
the Player and the public API should expose: CD+G smoothness (0 = original to max = HD), depth
(shadow and bevel), export scale (1x-5x, forced 1x at smoothness 0), safety zone (Show, Remove,
Auto, Super), padding (ours, in the compositor); MIDI lyrics in our karaoke style with lines
shown (1-4) and a subtitle mode; and the background always filled by us from the reported
background colour and its change notifications, with the surfaces transparent. Hardware
acceleration stays in our renderers (caller-owned upload buffers, dirty rectangles, INDEXED8).

## Known gaps to decide on (from FatzAudio's own list)

- **MIDI lyrics in Shift-JIS come out garbled** (only UTF-8 and Windows-1252 are recognized).
  Karaoke MIDI is a core use case; keep our own `MidiLyricExtractor`/`MidiMetadataExtractor`
  reading the file rather than relying on FatzAudio's text decoding until this is fixed.
- MIDI: SysEx effect parameters ignored (one fixed reverb and chorus); a seek clears reverb
  tails; no SFZ; no real-time MIDI input.
- Trackers: no live tempo/speed or channel/instrument volume attributes; MO3 with compressed
  samples unsupported.
- Not supported at all: ALAC in `.caf`, DTS, TrueHD, WMA off Windows, xHE-AAC without an OS
  decoder. Our FFmpeg fallback (033A) covers these by pushing decoded audio.
- Reverse playback and BPM detection are not built.
- Decode-only streams are not equalized and report no levels, so silence analysis and
  normalization keep measuring decoded samples themselves.
- Not yet run on real hardware by FatzAudio: WASAPI rerun, WMA, real CD drive, mobile. We run
  the Windows smoke.

## What we do not need to change yet

FatzAudio's "five changes" mostly concern adopting its CD+G surface: we keep our own CDG engine
(`fa_cdg_surface_update_at` uses ceil, not floor, and a CD+G offset of 0 if we apply our own).
We never called `FFmpeg.Load`. Rebuilding against the 0.2 header and the NuGet feed token are
slice 0.

## Risks

- FatzAudio is pre-1.0: a minor bump may break the API (`CHANGELOG.md`), and its binding throws
  at the first call on a native major.minor mismatch. Pin an exact version.
- Linux natives need glibc 2.35 or later; a new native dependency is documented in
  `docs/NATIVE_DEPENDENCIES.md`.
- A/V sync, loop gaplessness and latency are only judged on the owner's machine.

## Must Preserve

- BASS remains the default and a working fallback; the existing audio, loop, fade, EQ,
  normalization, tracker and MIDI behaviour on BASS is unchanged by slices 0-1.
- Core stays free of platform and FatzAudio references.
