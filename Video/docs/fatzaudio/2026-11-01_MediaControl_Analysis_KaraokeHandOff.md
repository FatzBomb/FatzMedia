# Handing CD+G and MIDI lyric drawing to FatzAudio: what we gain and lose

Written 2026-11-01 for stage 037A, answering the owner's question: do we lose sync, frames or
anything else by letting FatzAudio draw CD+G and MIDI/KAR lyrics? Compared from the code of
MediaControl `main` @ 28e71ef and FatzAudio `main` @ 17d2284 (PR 170). **[V]** verified in code,
**[M]** measured on Linux (Release -O3, 4 shared vCPUs, null output; FatzAudio built from source,
lyric benchmark: a 48-line song, Classic preset, called every 16 ms), **[I]** inference. Nothing
here was run on Windows.

FatzAudio's `main` is newer than the build 037A was written against: the surface model exists and
Shift-JIS lyrics are supported (`dec_midi.c:464-502`), so ENG-031 and 037A's Shift-JIS gap are
out of date.

## Verdict

No inherent loss in sync, correctness or features for Latin and CJK karaoke, and several gains.
Cost is equal or better for classic CD+G and at 1080p; 4K with effects is safe only with the
integration rules below. Real losses: complex-script shaping, the coupling while BASS is the
default, palette animation cost in scaled CD+G, and ownership of fixes and look.

| Area | Verdict |
|---|---|
| Clock and sync | Equal; better if we pass the present time |
| Seek, pause, loop, tempo, next song | Equal or better (loop wraps and tempo ramps cost, below) |
| Classic CD+G (indexed, GPU palette) | Equal |
| 1080p lyrics and scaled CD+G | Equal or better |
| 4K with effects | Worse unless drawn off the render thread with render-ahead |
| Palette animation in scaled CD+G | Worse (64 B today, multi-MB CPU redraws then) |
| Features | Mostly gains |
| Complex scripts (Arabic, Hebrew, Indic) | Loss: no shaping |
| Coupling while BASS is the default | The biggest practical cost |

## 1. Clock and sync

**Today [V].**
- CD+G draws from a 75 Hz loop (`MediaPresentationCoordinator.cs:26-30, 453-481`) at
  `Audio.Position + CdgOffset` (`RenderPipeline.cs:14-19, 45`); packs = floor(pos x 300)
  (`CdgEngine.PacketCountAt`).
- `Audio.Position` is the BASS channel position minus `BASS_INFO.latency`
  (`AudioEngine.cs:123-137, 213-216`; `BassOutputLatencyProvider.cs:14-29`). BASS is opened without
  `BASS_DEVICE_LATENCY` (`AudioEngine.cs:261`), so that latency is probably 0 [I]; the USB/Bluetooth
  heuristic is opt-in.
- Lyrics draw at `_mediaEngine.Position + SubtitleOffset` (`MediaPresentationCoordinator.cs:1557-1568`).
- Neither uses the presentation lead; only video frame selection does (`VideoPipeline.cs:1884-1889`,
  about 7.5-30 ms). Over video the karaoke layer is already drawn for an earlier time than the frame.

**FatzAudio [V].**
- `FA_SURFACE_NOW` reads `fa_stream_get_clock`: it includes the device buffer and the limiter delay
  (`device.c:231-237, 739-746`), is interpolated between callbacks (`device.c:84-101`), takes no
  lock (`stream.c:3095-3110`), runs in source time under tempo (`clock.c`), holds on pause and
  jumps to a seek target. OS/Bluetooth latency beyond the buffer is not included.
- Render-ahead paces from the times passed in (`cdg.c:2564-2571`). There is no public "clock at
  time T" (`fa_stream_clock_at(now_ns)` is internal, `stream.c:3078`).
- `fa_cdg_surface_update_at` counts packs with ceil (`cdg.c:2279-2286`), one pack (3.3 ms)
  earlier than ours: inaudible, but golden tests differ.
- The CD+G offset sign is the opposite of ours (FatzAudio: time - offset, `cdg.c:2345-2351`).
  Lyric surfaces have no offset.

## 2. Frame cost and stalls

**Today [V].** A changed CD+G frame copies and allocates 64.8 KB of indices
(`CdgStateMachine.cs:237`) and uploads a dirty-rect R8 texture plus a 64 B palette; the palette
lookup and nearest scaling run on the GPU (`D3D11CompositionLayerState.cs:233-267, 494-535`).
Lyrics: every progress change is a full Direct2D raster of the block on a 1920x1080 canvas,
copied and uploaded whole (`SubtitleTextRenderer.cs:171-385, 1068-1073`), on the render loop or
video thread (not measured: Windows only).

**FatzAudio CD+G [M]**, full redraw / typical frame:

| Setting | 1080p | 2160p |
|---|---|---|
| Smoothness 0 | 1.2 / 0.03 ms | 6.1 / 0.1 ms |
| Smoothness 4 + edge blend | 5.2 / 0.2 ms | 17 / 0.7 ms |
| Outline + shadow | 15 / 1.1 ms | 54 / 3.8 ms |
| All effects | 26 / 2.7 ms | 79 / 9.5 ms |

Classic (indexed) is about 0.3 ms for a full frame (FatzAudio's own table).

**FatzAudio lyrics [M]:** render-ahead off: 1080p average 1.4 ms, p99 4.3, max 34, 21 of 12,500
frames over 8 ms; 4K average 5.9 ms, p99 18.5, max 98, 40% over 8 ms. Render-ahead on: 1080p
average 0.28 ms, max 12; 4K average 0.74 ms, max 46, 3% of frames late. Dirty rectangles average
about 10% of the bitmap (about 0.8 MB per frame at 1080p, 3 MB at 4K).

**Threads [V].** Updates run on the caller's thread and share `cdg_lock` only with loads, other
surfaces and the event thread (`cdg.c:2356-2384`); lyrics take a brief `tempo_lock` shared with
the decode worker. Nothing touches the real-time audio callback. Render-ahead jobs run on decode
workers only when no stream needs decoding (`engine.c:162-205`; 500 ms decode buffer,
`stream.c:26`): low risk to audio [I], to be measured.

**Stalls on the caller [V].** A full redraw happens on the caller on the first frame, a layout
change, a seek, a backward jump (a loop wrap) or when more than 1/60 s late (`cdg.c:2595-2610`).
`update_into` can also block on `work_lock` while a worker renders: CD+G with no front frame
(`cdg.c:3247-3252`), lyrics when no slot fits, for example after a tempo step
(`cdg.c:3216-3233, 3108-3112`). This contradicts our request that it never makes our render
thread wait: at 4K with effects one such frame is a 50-210 ms stall.

## 3. Seek, pause, loop, tempo, next song

Equal or better without special code: pause holds the clock; a seek reports its target at once
(`stream.c:3072-3076`); CD+G replays at most 10 s from a checkpoint and redraws only what
differs; lyrics are a function of time; animation lengths follow tempo (ours stretch with song
time). Costs: each loop wrap is a caller-thread redraw in scaled mode with render-ahead;
render-ahead slots are keyed to tempo in 0.25% steps (`cdg.c:2970-2993`), so a continuous ramp
(DJ sync) defeats it; a new song builds checkpoints on the first surface (about 5 ms) and lyric
pages cost tens of ms on the first frame. Device-change behaviour of the clock was not checked.

## 4. Several windows and sizes

One render feeds every window in both designs. Classic indexed output keeps today's GPU palette
path. Scaled mode is CPU BGRA at 5-6x (6.5-8 MB per full frame; dirty rectangles help). Palette
changes (colour cycling, fades) become CPU redraws and multi-MB uploads (FatzAudio's figures:
5-20 ms at 4K). Downscaling a 4K bitmap to small windows needs linear sampling with mips; D3D11
uses a point sampler today (`D3D11MediaCompositionPresenter.cs:1225`). A presentation-size change
is a full redraw and reallocation: pass it only when a resize ends.

## 5. Features and control

Gains: smooth-scroll offsets and the transparent-colour pack (our ENG-029 gaps),
`FA_CDG_HIDE_UNTIL_PRESET`, safety zones, smoothing and effects, Shift-JIS, one lyric look on every
renderer, disc CD+G, cheaper seeks. Losses and risks: fixes and look are FatzAudio's (pre-1.0);
`font.c` does cmap and kerning only (no GSUB, ligatures or bidi, where DirectWrite handles Arabic,
Hebrew and Indic); font fallback and raster quality differ from DirectWrite; the BT/USB latency
heuristic must be applied in the time we pass; plain subtitles keep our own rasterizer.

## 6. Coupling

A surface needs an `fa_stream`; a lyric surface needs a MIDI stream, which needs a soundfont
(the system GM bank, else `FA_ERROR_NO_SOUNDFONT`, as on Linux without a GM package; header
:2565-2571, :3046-3052). While BASS plays the audio: (a) keep our CD+G engine and DirectWrite
for BASS (two paths, two looks), or (b) open a decode-only FatzAudio stream for the file and call
`update_into` with our clock time, which works [M] but parses the file twice, needs the soundfont
for lyrics and must mirror tempo onto that stream [I]. CD+G over FFmpeg or video audio means push
streams, whose clock is in pushed pts: a non-zero start pts shifts the graphics [I].

## Integration rules (037A)

1. Call `update_into` from a dedicated karaoke thread, never the presentation or video thread;
   triple-buffer and composite the latest finished buffer.
2. Use render-ahead in scaled mode at 4K or with effects.
3. Pass an explicit time: clock + (expected present time - now) x tempo while playing, no lead
   when paused; the same target time as the video frame; our offsets included with the sign
   flipped, FatzAudio's CD+G offset at 0.
4. Classic indexed CD+G stays the default.
5. Create the next song's surfaces and draw their first frame early, off-thread.
6. Governor on `draw_ns`, `render_ns` and `behind`.
7. Linear sampling with mips and a transparent border for scaled bitmaps.

## To measure in the 037A spike

p99 and max `draw_ns` on our karaoke thread at 1080p and 4K, effects off and maximum, for steady
play, seek, loop wrap, page build, resize and a tempo ramp; audio underruns with render-ahead at
4K with effects and two streams; a palette-cycling disc in scaled mode (CPU and upload MB/s); A/V
skew between the video frame and the karaoke layer; Bluetooth/USB sync against our heuristic.

The requests these findings raise for FatzAudio are in
`2026-11-01_MediaControl_Request_SurfaceTiming.md`.
