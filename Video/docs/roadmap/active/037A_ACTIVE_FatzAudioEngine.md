# Stage 037A - FatzAudio As A Second Audio Engine

Status: Active (owner decision 2026-11-01: start 037A, karaoke drawing goes to FatzAudio). Slice 0
done on Linux (`updates/037A_0`); slice 1 merged, owner smoke passed (`updates/037A_1`). FatzAudio
1.0.0-rc.1 (2026-11-02, main @ 59741a5) answers our blocking requests 1 and 8 and request 9
(silence detection); our reply is `docs/fatzaudio/2026-11-03_MediaControl_Reply_Rc1.md`. Slice 2
(FatzAudioEngine for files and archives, opt-in with `MediaPlayerConfiguration.AudioEngineFactory`
and `--audio=fatz`, pinned to `FatzAudio` `1.0.0-rc.1`) merged 2026-11-03, owner smoke passed
(`updates/037A_2`). K4 (clock at a present time), K6 (lyrics without a soundfont) and K8
(shaping) merged in FatzAudio after rc.1 (main @ 5182a4e); slice 3 moves the pin to the build that
carries them (reply `docs/fatzaudio/2026-11-03_MediaControl_Reply_K4K6K8.md`). Loudness (LUFS)
normalization on the FatzAudio engine implemented 2026-11-04, pending owner smoke
(`updates/037A_Normalization`; FatzAudio request 12 for the first-play start,
`docs/fatzaudio/2026-11-04_MediaControl_Request_NormalizationStart.md`).
Written 2026-10-20 as an idea from FatzAudio's answer to our two notes; revised 2026-11-01 against
FatzAudio `main` @ 17d2284 (PR 170), read and measured on Linux:
`docs/fatzaudio/2026-11-01_MediaControl_Analysis_KaraokeHandOff.md` (sync, cost, stalls,
coupling) and the requests it raised, `docs/fatzaudio/2026-11-01_MediaControl_Request_SurfaceTiming.md`.
A read-only checkout of FatzAudio is at `/home/user/fatzbomb75/fatzaudio`.

Goal (owner decision, 2026-10-24): replace BASS with FatzAudio if it is proven to work, so end
users need no BASS DLLs and no BASS licence. First an opt-in `FatzAudioEngine` next to the BASS
engines, selected by configuration (and a Player flag), with BASS the default and fallback until
FatzAudio passes the verification spike and owner smoke; then FatzAudio becomes the default and
BASS is removed. It is a library swap behind `IAudioEngine`, not a rewrite of playback, timing or
composition. FFmpeg (video) stays a host-supplied native dependency either way.

## What FatzAudio says it now covers

Blockers 1-5 (EQ and user DSP, loop on/off with a `Looped` event, tempo/rate/pitch on push and
packet streams, tracker modules with exact seek and order/row position, MIDI with reverb/chorus,
SysEx, tempo/time/key marks, SF3 and direct DLS loading); parity 6-11 (default-device following
with `DeviceChanged`, levels and spectrum, tags and stream info, async seek, a lock-free clock);
all six CD+G surface asks; a GitHub Packages NuGet feed (`FatzAudio` 0.2.0-ci.N, win-x64 and
linux-x64, net10.0). Raw CD+G packs were deliberately not built.

## Fit with this repo

- `IAudioEngine` leaks BASS integer handles (`PlayHandle`, `SourceHandle`). Consumers used them
  for MIDI lyrics (`MidiLyricExtractor`), the fade gain stage, the EQ filter, normalization and
  silence analysis. A FatzAudio engine has no such handles, so those consumers needed a seam
  first. Slice 1 added them (`IMidiEventReaderSource`/`IMidiEventReader`, `IAudioLevelSource`, with
  the existing `IAudioFadeGainTarget`, `IAudioFilterHost` and trim interfaces); internal code no
  longer reads the handles (`updates/037A_1`).
- `VideoAudioEngine` (1,107 lines) pushes decoded FFmpeg audio into a BASS push stream and reads
  the position back for A/V sync. FatzAudio's push/packet streams with tempo and a clock replace
  it, but this is the timing-critical path: last, and gated by owner smoke.
- Core must not reference FatzAudio. The engine lives in a new package
  (`FatzMedia.Video.Audio`); `scripts/check-core-boundary.ps1` pins the
  reference DAG and is updated deliberately.

## Slices

Each slice has its own update note (`updates/037A_<n>/StageUpdate.md`) and owner smoke.

0. **Verify (spike).** Rerun the dropout measurement on 1.0.0-rc.1 (Linux here, Windows by the
   owner) before slice 3. Done when: a test project references the `FatzAudio` .NET package (the
   GitHub Packages feed with a `read:packages` token, or a package built from FatzAudio's source
   until the token exists), opens a file, plays it to a null device, seeks, loops and reads the
   clock on Linux; the binding's native version check passes against the pinned version; and the
   karaoke measurements in the analysis's "To measure" list that Linux can produce are recorded
   (draw time per frame at 1080p and 4K, effects off and on, for steady play, seek, loop wrap,
   page build, resize and a tempo ramp; audio underruns with render-ahead). Decide how CI gets
   the package. Nothing else starts until this passes.
1. **Seam, no behaviour change.** Remove BASS handles from the engine contract: lyrics from the
   file (or a FatzAudio-neutral reader) instead of a source handle; fade, EQ, normalization and
   silence analysis behind small interfaces that the BASS engines implement as today. BASS output
   must stay bit-for-bit as before; existing audio tests pass unchanged.
2. **FatzAudioEngine for files and archives.** Open (path, archive entry), play/pause/stop/seek,
   volume, pitch, tempo, loop with a per-wrap `PlaybackEnded`/`MediaLooped`, position from
   `fa_stream_get_clock`, EQ from the built-in 16 bands, fades from volume ramps, device
   following. Opt-in: `MediaPlayerConfiguration` audio engine choice and Player `--audio=fatz`.
   Silence trim from FatzAudio's silence detection (request 9; owner decision 2026-11-02: it
   belongs in the audio engine), applied from the first play.
3. **Karaoke surfaces.** CD+G and MIDI lyrics drawn by FatzAudio's surfaces on every renderer,
   following the analysis's integration rules: a dedicated karaoke thread calling `update_into`
   with triple buffers (never the presentation or video thread); an explicit draw time (clock plus
   the expected present time, the video frame's target, our offsets folded in with the sign
   flipped and FatzAudio's CD+G offset at 0); classic indexed CD+G as the default; render-ahead in
   scaled mode; next song's surfaces prepared early; a governor on `draw_ns`/`render_ns`/`behind`;
   linear sampling with mips for scaled bitmaps. With the FatzAudio engine the surfaces use its
   stream; with BASS they use a FatzAudio stream for the same file that is never played (MIDI
   lyrics: `StreamOptions.MidiLyricsOnly`, K6, no soundfont needed). The draw time comes from
   `GetClockAt` (K4) at the frame's present time, passed through
   `AudioEngine.MonotonicTimeFromStopwatch` (the next vblank on Direct3D11, now plus the
   presentation lead elsewhere). A `.cdg` with no audio, or a MIDI/KAR with lyrics and no
   notes, needs FatzAudio request 10 (a playable silent stream); until it exists those files keep
   our own clock. Silence trim for karaoke has three modes (owner 2026-11-03): no trim (default;
   today an MP3+G is never trimmed), silence with no drawing (needs request 11: graphics and
   lyric times next to the audible times), and silence regardless of drawing (audible times only). Our own CD+G engine and both lyric
   renderers (DirectWrite on Direct3D11, SkiaSharp on Software and Vulkan) stay as the fallback
   until the decision slice.
4. **MIDI, trackers, network.** Soundfont/DLS direct (drops the DLS-to-SF2 step and ENG-028),
   MIDI texts/marks, `GetTrackerTime` for the tracker timeline, network playback.
5. **Video audio.** (Smoke includes ENG-047's second case: 1080p video with sound on two screens, no clicks in the first minute.) No separate read of the file for loudness (ENG-052: today's BASS soundtrack path decodes the whole soundtrack a second time for its deep peak pass); FatzAudio measures from the fed audio (request 13) or its cache. Push or packet streams with FatzAudio tempo and clock replacing the BASS
   push stream; the video clock follows `fa_stream_get_clock`. Owner smoke for sync at tempo
   0.75-1.5, seeks, loop wraps and pause/resume. Plan (owner 2026-11-05): FFmpeg demuxes every video
   file (AVI/DivX, MPG, TS included); the audio goes to FatzAudio as a packet stream when it decodes
   the codec (MP3, AC-3, AAC, ...) and as a push stream of FFmpeg-decoded frames otherwise (MP2 until
   request 14, DTS, TrueHD, ...). Never FFmpeg handles across the libraries. The same push path
   replaces the whole-file WAV conversion for audio files FatzAudio cannot decode (no open delay).
   Normalization for these streams needs request 13 (scan from fed audio, source identity); FatzAudio
   request 14 asks for MP1/MP2, old-MP3 coverage, AVI/MPEG-PS/TS by path and DTS core
   (`docs/fatzaudio/2026-11-05_MediaControl_Requests_13_14.md`). FatzAudio suggests `BufferMs` 250
   for video soundtracks. Also the explicit output device for FatzAudio (owner 2026-11-03:
   `AudioOutputDeviceIndex` or a device id mapped to FatzAudio's device list) and one device-event
   source instead of two.
Normalization (owner decision 2026-11-03; implemented 2026-11-04 as `MediaPlayerConfiguration.Normalization`,
`updates/037A_Normalization`; the measured scan times and the owner's 2026-11-04 decision moved the
first-play wait into FatzAudio, request 12, so our player does not wait; the text below is the
original plan): loudness (LUFS) is the only normalization on the
FatzAudio engine, replacing our peak rule there before slice 6: FatzAudio's normalization and
limiter, the quick turn-down-only pass on a first play, then a glide to the measured gain, with a
cap on boosts. Target: a configuration setting whose default is the streaming-service standard,
-14 LUFS integrated with peaks limited to -1 dBTP. This is a media engine, so it ships the
universal default; an app (a karaoke host wanting about -16 LUFS for mic headroom) sets its own. No
FatzAudio request needed: its normalization already takes the loudness from (best first) the app,
ReplayGain 2 / R128 tags, its scan cache, then a scan split across all cores, gliding from a
running estimate. To be at the right level from the first sample, our play-on-load waits for
`FA_EVENT_LOUDNESS_FINAL` up to a short budget (about 300 ms, measured first) before starting a
local file, as it waits for the quick pass today; a scan that takes longer starts on the estimate
and glides. Apps with a library can pre-scan (`fa_scan_loudness_path`) so every play is instant. BASS keeps the peak rule
until it is removed; the public API is unchanged.

6. **Decision (separate from the build).** Whether FatzAudio becomes the default, whether BASS is
   then removed, and whether our CD+G engine and lyric renderers (DirectWrite and SkiaSharp) are
   deleted. No karaoke drawing code is removed before this decision.

## Karaoke presentation (agreed with the owner 2026-10-20)

FatzAudio draws CD+G and MIDI lyrics; we place, scale, fill the background and composite. The
request is `docs/fatzaudio/2026-10-20_MediaControl_Request_KaraokeSurfaces.md`. Our side:

- **Public API and Player options:** CD+G PresentationScale (off = classic native indices, the
  default and today's path), smoothness 0-10, edge blend, safety zone (Show, Remove, Auto,
  Super), padding; shared effects (outline, shadow, bevel; size, distance, fade 0.0-1.0); MIDI
  lyric design preset, colours, font, panel, lines shown, subtitle mode, wipe style; a karaoke
  render quality setting (100%, 75%, 50%).
- **Presentation size:** per surface, in physical pixels (DPI-aware), in the window's exact
  shape; the largest window's size, or a reduced same-shape size (render quality, governor);
  passed only when a resize has finished. One render feeds every window; our GPU scales to each.
- **Fit:** uniform fit of the returned bitmap, centred, background fill around it. Exact 1:1
  fits use nearest sampling; other fits shrink with smooth interpolation. Classic native indices
  stay nearest-sampled with the palette lookup on the GPU, as today.
- **Transparent edge sampling:** texture addressing treats anything outside a bitmap as
  transparent (not edge-repeat), so a bitmap's outermost pixels never blend with garbage.
- **Governor:** if FatzAudio's reported update time stays over a budget (about 8 ms) for a few
  seconds, lower the CD+G presentation size first (half, then a quarter), lyrics only if they are
  the slow part; log it, and show render cost in the Player diagnostics.
- **Layout revisions:** reallocate from our buffer pool when a frame reports a new
  `layout_revision`; draw into our pooled or mapped upload buffers.
- **Background:** drawn by us from the reported background colour and its change revision; the
  border made transparent and drawn by us when shown.

## DJ mixer and microphones (agreed with the owner 2026-10-23)

FatzAudio planned a deck mixer, beat-matching and mic input
(`docs/fatzaudio/2026-10-23_FatzAudio_Plan_DjMixer.md`); our reply cuts the scope
(`docs/fatzaudio/2026-10-23_MediaControl_Reply_DjMixerPlan.md`): everything karaoke needs plus common DJ
items (2 decks, isolator EQ, crossfader, headphone cue, BPM/key, sync, loops, simple auto-mix, 2 mics with
effects and ducking, recording); the rest is pinned for later. Mic latency is measured first.

Our side, after the karaoke surfaces are integrated: a thin mixer layer in MediaControl where a player's
audio attaches to a deck and its video, CD+G and lyrics follow that deck (fits the player Modes stage).
Requirement passed to FatzAudio: a deck's heard position includes any delay added to the master output.
The app keeps the playlist, singer rotation, controller mapping and song database.

## Known gaps to decide on (from FatzAudio's own list)

- ~~MIDI lyrics in Shift-JIS come out garbled~~: supported in FatzAudio `main` @ 17d2284
  (`dec_midi.c:464-502`); verified in slice 0 (`updates/037A_0`).
- **Complex scripts:** FatzAudio's font engine does cmap and kerning only (no ligatures or bidi);
  Arabic, Hebrew and Indic lyrics look worse than with DirectWrite (request 6).
- MIDI: SysEx effect parameters ignored (one fixed reverb and chorus); a seek clears reverb
  tails; no SFZ; no real-time MIDI input.
- Trackers: no live tempo/speed or channel/instrument volume attributes; MO3 with compressed
  samples unsupported.
- Not decoded by FatzAudio (its answer of 2026-11-05, checked on its current build): DTS, TrueHD,
  WMA/ASF off Windows, xHE-AAC without an OS decoder, the rare AAC profiles (SSR, LD/ELD), MPEG
  Layer I/II, WavPack, TTA, Monkey's Audio, Musepack, DSD, Speex, Sun `.au`, µ-law/A-law in CAF, and
  the MPEG-TS, MPEG-PS (`.mpg`, `.vob`), AVI and FLV containers. ALAC in `.caf` does decode. Our
  FFmpeg fallback (033A) converts these when the open fails with `UnsupportedFormat`.
- Reverse playback and BPM detection are not built.
- Decode-only streams are not equalized and report no levels, so normalization keeps measuring
  decoded samples itself; silence detection is requested from FatzAudio for files and for pushed
  audio (request 9).
- Not yet run on real hardware by FatzAudio: WASAPI rerun, WMA, real CD drive, mobile. We run
  the Windows smoke.

## What we do not need to change yet

Until slice 3, we keep our own CD+G engine. When adopting FatzAudio's: `fa_cdg_surface_update_at`
counts packs with ceil (ours floor) and its offset sign is the opposite of ours, so its offset
stays 0 and ours is folded into the time we pass.
We never called `FFmpeg.Load`. Rebuilding against the 0.2 header and the NuGet feed token are
slice 0.

## Risks

- FatzAudio is pre-1.0: a minor bump may break the API (`CHANGELOG.md`), and its binding throws
  at the first call on a native major.minor mismatch. Pin an exact version.
- Linux natives need glibc 2.35 or later; a new native dependency is documented in
  `docs/NATIVE_DEPENDENCIES.md`.
- A/V sync, loop gaplessness and latency are only judged on the owner's machine.
- Surface stalls: until FatzAudio's `update_into` never blocks (request 1), full redraws (first
  frame, seek, loop wrap, resize) run on our karaoke thread: 50-210 ms at 4K with effects.
- While BASS is the default, lyric and CD+G surfaces need a second, decode-only FatzAudio stream
  (a second parse, and a soundfont for MIDI).

## Must Preserve

- BASS remains the default and a working fallback; the existing audio, loop, fade, EQ,
  normalization, tracker and MIDI behaviour on BASS is unchanged by slices 0-1.
- Core stays free of platform and FatzAudio references.
