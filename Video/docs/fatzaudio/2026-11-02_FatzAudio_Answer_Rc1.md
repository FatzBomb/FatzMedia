# FatzAudio: answer to FatzBomb.MediaControl

To: the FatzBomb.MediaControl team.
From: FatzAudio. Replies to your two notes, "what FatzBomb.MediaControl needs to replace BASS" and "what FatzBomb.MediaControl needs from the surface".
Refreshed 2026-11-02 against FatzAudio `main` @ 615ab7e (PR 191). The first answer was written at c6e4446 (2026-10-19, PR 78); everything since then is in [Update 2026-11-02](#update-2026-11-02-since-pr-78) below, and the later sections are corrected where they went out of date. CI is green on every job: Windows, Linux, macOS, Android, iOS and sanitizers.

## Short version

- Every blocker (1 to 5), every parity item (6 to 11), packaging and all three nice-to-haves are on main.
- All six CD+G surface asks are on main, plus the push model and the surface options from your addendum.
- Raw CD+G packs (item 9) were not built. Your CD+G note said that once asks 1 to 4 were in, they were only needed for diagnostics.
- Everything was added as an option. Existing defaults are unchanged, except where a "Breaking" entry below says so.
- You must change these things on your side (updated 11-02):
  - rebuild against the **1.0.0-rc.1** header, **and** the .NET package together with its native library (the `struct_size` rule is a breaking change);
  - drop `FFmpeg.Load` / `FFmpeg.Info`;
  - apply CD+G packs with `ceil` instead of `floor`;
  - set the CD+G offset to 0 if you apply your own;
  - point NuGet at the GitHub Packages feed (pinned version `1.0.0-rc.1`).

  Details are in [What you need to change](#what-you-need-to-change). The newer work (karaoke timing, silence detection, loopback capture, ABI) is in [Update 2026-11-02](#update-2026-11-02-since-pr-78).

## Update 2026-11-02 (since PR 78)

PRs 79 to 191. Nothing in the first answer changed meaning except where a note says so. Full breaking list: `CHANGELOG.md`.

### Breaking: versioned structs (0.3.0, PR 181)

- Fourteen output structs now start with `uint32_t struct_size`: `fa_stream_info`, `fa_sound_info`, `fa_device_info`, `fa_metadata`, `fa_net_status`, `fa_disc_toc`, `fa_loudness`, `fa_cdg_frame`, `fa_cdg_info`, `fa_soundfont_info`, `fa_soundfont_preset`, `fa_midi_text`, `fa_midi_info`, `fa_encoder_status`. C callers set it to `sizeof` before the call (`fa_stream_info info = { .struct_size = sizeof(info) };`). 0 is `FA_ERROR_INVALID_ARG`. **The .NET binding sets them itself; its public API did not change.**
- Input structs (options, configs) follow one rule: `struct_size` must cover the struct's first layout, the covered part is read, later fields keep defaults, and a bigger struct from a newer header is read up to the library's size. Structs filled by their `_init` function are unaffected. `fa_engine_create` now refuses `struct_size` 0.
- `fa_cdg_info` gained `disc_corrected` and `disc_dropped` at its end (.NET `CdgInfo` has two more positional parameters).
- Your `fa_cdg_frame` code: the frame now has `struct_size`, so this is the last time it can grow without breaking you.
- **Rebuild and update the package and the native library together.** The .NET binding refuses a native library of another major.minor, and the Linux SONAME follows the version.

### Karaoke timing (your 8 requests, PRs 173, 178, 180, plus 183/184 for silence)

- **K1, render-ahead never starves decoding (PR 173).** Drawing may hold at most `worker_count - 1` workers; a stream more than 10 ms short is topped up before a worker draws; render-ahead runs at lower priority (Windows BELOW_NORMAL, Apple QoS utility, Linux/Android nice +5 where it can go back). Measured on a 4-core Linux sandbox with per-surface drawing threads: before 65-158 dropouts per stream, after 0 and the clock within 3 ms. **Dropout counts:** `fa_stream_get_stats` (`underruns`, `buffered`; lock-free) and `fa_engine_stats.underruns`. .NET: `AudioStream.GetStats()` (`StreamStats`), `EngineStats.Underruns`.
- **K2, `update_into` never blocks (PR 178).** With render-ahead on and two or more workers, `fa_surface_update_into` (CD+G and lyrics) takes no lock and never draws on your thread. It shows the last finished frame flagged `behind`, or a **"nothing new" frame** (`FA_OK`, `dirty_count` 0, `behind` 1, revision unchanged) for the first frame, after a seek, and after new graphics, options or layout until a worker's frame is ready. Keep your texture on a nothing-new frame, including after a resize. A one-worker engine still draws on the caller. Stress numbers (2160p, every effect): `update_into` max 146-191 ms down to 16-36 ms; with seeks and resizes every second 356-570 ms down to 24-31 ms. .NET `CdgSurface.UpdateInto(nint, ...)` and `LyricSurface.UpdateInto` take no managed lock either. Still waiting for a render in progress (up to one 2160p frame): `set_presentation_size`, options, effects, style, `get_layout` (except right after `FA_ERROR_SIZE_MISMATCH`) and `fa_cdg_surface_update`.
- **K3, lyrics keep rendering during tempo ramps (PR 180).** A slot drawn at a tempo within 2% of the current one is shown as a normal frame, not `behind`. Your ramp (1 s steady, 1.0 to 1.3 over 3 s, back over 1 s): lyric frames behind at 1080p went from 55% to 2%, same as no ramp; at 2160p with every effect from 87% to 40-48% (28% with no ramp, so the remainder is load, not tempo).
- **K9, silence detection (PRs 183, 184; your request 9).** `fa_stream_get_silence` / .NET `AudioStream.DetectSilence` answers between open and play: from the analysis cache if there is one, else by decoding only the start up to the first sound and the end back to the last one. Result: silent regions, `first_audible`, `last_audible` (the frame **after** the last audible one, so it goes straight into CUE_OUT; `FA_FRAMES_UNSET` when all silent), and the settings that made it. Defaults as you asked: **RMS over 50 ms windows, -50 dBFS, minimum silence 1 s, audible runs shorter than `min_sound_ms` = 200 ms count as silence** (clicks, crackle and count-in ticks are not audio; a soft fade-in is). `FA_SILENCE_PEAK` is the exact sample-peak mode. The mode and all settings are reported and the cache keeps each mode apart. Also: `FA_ANALYZE_SILENCE` + `fa_analysis_get_silence` (every region, cached), and `fa_silence_detector_*` for PCM you push yourself. Push, packet, web and callback streams return `FA_ERROR_NOT_SEEKABLE` (feed a detector instead). MIDI and trackers are measured on their rendered audio, tails included. Known limits: a sparse intro of very short dry hits is trimmed like a count-in (set `min_sound_ms` 0); a short sound that ends the file is dropped. .NET: `SilenceOptions` (`Mode`, `ThresholdDb`, `MinLength`, `Window`, `MinSound`), `SilenceInfo`, `SilenceRegion`, `SilenceDetector`, `AnalysisKinds.Silence`.
- **K7 (INDEXED8 at an integer scale)** still waits on your answer. The remaining plan steps (K4 to K6, K8) are in `karaoke/2026-11-01_FatzAudio_Plan_KaraokeTiming.md`.

### Other additions since PR 78

- **Loopback capture, Windows (PR 171, BASS_DEVICE_LOOPBACK):** `FA_CAPTURE_LOOPBACK` in `fa_capture_options.flags` records what an output plays (`device_id` an output id, NULL = default output); gaps where nothing plays are filled with silence. `FA_ERROR_NOT_IMPLEMENTED` elsewhere. .NET `CaptureOptions.Loopback`. Not run on a real device yet.
- **Live soundfont change (PR 182):** `fa_stream_set_soundfont` / `fa_stream_get_soundfont` (.NET `AudioStream.SetSoundFont` / `GetSoundFont()`) on a playing MIDI stream; heard after the decode-ahead buffer with a ~12 ms cross-fade; held notes go on with the new instruments. SF2 24-bit samples (`sm24`) are played.
- **.NET polish (PR 186):** `TimeSpan` variants (`DjDeck.CuePointTime`, `LoopTime`, `SlipPositionTime`, `Disc.GetPregapTime`, `MidiEvent.FrameAt/TimeAt`), async open (`OpenMemoryAsync`, `OpenMidiAsync`, `OpenMidiEventsAsync`), `CancellationToken` on `CrossfadeToAsync` and `DjMixer.MeasureLatencyAsync`, `TrySendMidi`, `CreateSound(bytes, AudioFormat)`, `OpenCallback(AudioFormat, ...)`, `StreamEventArgs.Error`. **One source break: the `DecodeError` event is now `EventHandler<StreamEventArgs>`** (was `EventHandler`). `+=` with a lambda or an `(object?, EventArgs)` method still compiles; only code storing the delegate in an `EventHandler` variable must change.
- **Everything from the optional extras plan (PRs 143-168):** reverse playback and DJ slip mode, GS/XG effects and insertion effects, real-time MIDI stream and event API, soundfont stacks, tracker live state (the `BASS_ATTRIB_MUSIC_*` gap is closed), Dolby downmix, `fa_sound_*` voice pool, callback and capture streams, engine stats, loudness meters, MP4/Matroska/HLS network reads, CAF with AAC, tempo maps, ZIP and tag improvements, fuzzing, `.NET for iOS/Android` CI app jobs. Details: `docs/features/*` and `CHANGELOG.md`. Several "Gaps against BASS" below are now closed; see that section.
- **Final audit fixes (PRs 172-186):** flaky tests fixed, `.NET` fixes, engineering-notes defects, version 0.3.0, fuzz coverage widened (net and sound targets).
- **License (PR 190):** FatzAudio's own code is proprietary, all rights reserved (root `LICENSE`, NuGet `PackageLicenseFile`); third-party components keep their own licenses (`THIRD_PARTY_NOTICES.md`).
- **Docs site (PRs 188, 189, 191):** user documentation (getting started, concepts, 8 tutorials, cookbook, formats, platforms, limits, troubleshooting, FAQ), a generated C reference (315 functions) and BASS migration appendices A-N. Built with DocFX into a 10 MB zip with offline helpers: `/mnt/project-files/fatzaudio/docs/FatzAudio-docs-2026-11-02.zip`. Start with `docs/getting-started.md` and `docs/migration/bass.md`.

### Open questions for MediaControl

1. **"Nothing new" signal (K2).** Is `dirty_count` 0 + `behind` 1 enough, or do you want a distinct flag? A flag would be a new field at the end of the frame struct (now safe under `struct_size`).
2. **Scroll drift during ramps (K3).** A lyric slot within 2% of the current tempo is not marked behind. That is up to 2% of a scroll or page-turn length, about 30 ms of the default 1.5 s scroll. The words' times stay exact. Acceptable?
3. **Shortest sound (K9).** Is `min_sound_ms` = 200 ms right for your library?
4. **K7** (INDEXED8 at an integer scale): your answer is still needed.
5. **Slice-0 dropout rerun.** Please rerun your slice-0 test on this build; the lower render-ahead priority is only exercised on Windows.
6. After a resize the picture stays empty until the first worker frame (10-20 calls in stress). Keep the old texture; tell us if that is a problem.

## Blockers

### 1. Equalizer / user DSP (PR 57)

| | C | .NET |
|---|---|---|
| Built-in EQ: up to 16 peak or shelf bands (frequency, Q, gain dB) | `fa_stream_set_eq(stream, bands, count)`, `fa_stream_set_eq_gain`, `fa_stream_set_eq_enabled` | `stream.SetEqualizer(bands)`, `SetEqualizerGain(band, dB)` |
| 13 presets | `fa_eq_get_preset(preset, bands, &count)` | `stream.SetEqualizer(EqualizerPreset.X)`, `Equalizer.GetPreset`, `GetPresetName` |
| User DSP | `fa_stream_add_dsp(stream, cb, user, priority, &id)`, `fa_stream_remove_dsp` | `stream.AddDsp(processor, priority)` (returns `IDisposable`) |

- **Where the EQ runs.** It runs in the mixer, before the stream's volume, fades and normalization gain, so a change is heard at once.
- **Gain changes.** Changes glide over about 10 ms, so moving a slider while playing doesn't click.
- **Cost when off.** An EQ that is off or flat, or a band at 0 dB, costs nothing.
- **Push and packet streams.** The EQ works on them like on any stream played on a device.
- **Decode-only streams are not equalized.** `fa_stream_read` returns the audio as it was decoded, so use a DSP callback there if your analysis needs the EQ.
- **DSP callback.** It runs on the decode worker, after tempo/pitch/rate and before the EQ, volume, fades and normalization. It is priority-ordered, safe to add or remove while playing, and heard after the decode-ahead buffer. Don't call that stream's `fa_stream_*` functions from inside it.

### 2. Loop on and off while playing (PR 55)

- C: `fa_stream_set_loop(stream, on)`, callable at any time. It loops from CUE_OUT (or the end) back to CUE_IN (or 0) without a gap.
- C: `FA_EVENT_LOOPED`. Its `frame` is the loop end it left; its `value` is the loop start it went to.
- .NET: `stream.Loop` and the `Looped` event.
- Push, packet and forward-only web streams can't loop: `on = 1` returns `FA_ERROR_NOT_SEEKABLE`.
- Mute, which Keith asked for alongside: `fa_stream_set_mute` / `fa_device_set_mute` (.NET `Muted`). Mute keeps the volume and its ramps, and uses the same 5 ms de-zipper.

### 3. Tempo and rate on packet and push streams (PR 62)

- `FA_PARAM_TEMPO`, `FA_PARAM_RATE` and `FA_PARAM_PITCH` all work on `fa_stream_open_push` and `fa_stream_open_packets` streams.
- `fa_stream_get_clock` still reports your pts and runs at the speed heard. At tempo 1.25 it advances 1.25 s of your timeline per second, so the video can follow the clock with no scaling on your side.
- Jumps in your timestamps still show up when their audio is heard: after `fa_stream_push_flush`, or after a gap.
- Push audio as fast as it is used: at tempo 1.5, push 1.5× real time. `fa_stream_get_buffered` counts the queue in your timeline.
- Since PR 72, frames pushed at a rate other than the stream's are converted with the windowed-sinc resampler. That covers your FFmpeg fallback if it pushes at the source rate. The clock already accounts for the few frames the resampler holds back.

### 4. Tracker modules (PRs 66 and 68)

- libopenmpt 0.8.9 is built into the one binary; there is no add-on DLL.
- Formats: MOD, IT, S3M, XM, MTM, UMX, MPTM and the rest libopenmpt reads. They open with the ordinary `fa_stream_open_*` calls.
- **Exact seeking in every state**, with ordinary `fa_stream_seek`, plus seeking by order and row:
  - C: `fa_stream_seek_tracker(order, row)`, `fa_stream_get_tracker_time(order, row, &seconds)`.
  - .NET: `SeekTracker`, `GetTrackerTime`.
- **Whole-file looping:** `fa_stream_set_loop` / `Loop` from item 2. The module's own jump back to its restart order is not followed, so the length is always known. The restart position is in `fa_tracker_info` if you want to rebuild that loop yourself.
- **Order and row heard now:**
  - C: `fa_stream_get_tracker_position(stream, &order, &row)`. It is accurate to about 2 ms and lock-free like the clock.
  - .NET: `stream.TrackerPosition`, which is `null` until it is known.
- **Your TrackerPositionTimeline.** `GetTrackerTime(order, row)` gives the first-pass time of a row, so you can feed it the heard position to show first-pass time after a pattern loopback.
- **Row events:** `FA_TRACKER_ROW_EVENTS` raises `FA_EVENT_TRACKER_ROW` with `value = order << 16 | row`. In .NET that is `TrackerRowEvents` and `TrackerRowReached`. The equivalent of `BASS_SYNC_MUSICPOS`.
- **Subsongs** are the stream's audio tracks: `fa_open_options.audio_track` (.NET `StreamOptions.AudioTrack`).
- **Also available:** module info and texts (`fa_stream_get_tracker_info`, `fa_stream_get_tracker_text`; .NET `TrackerInfo`), channel mute, and interpolation, stereo separation, volume ramping and Amiga resampler settings.
- **MO3:** files with plain samples play. MO3 with MP3- or Vorbis-compressed samples returns `FA_ERROR_UNSUPPORTED_FORMAT`.
- **Binary size:** the Linux `.so` grew from about 2.05 to about 4.0 MiB.

### 5. MIDI parity with BASSMIDI (PRs 53, 56, 64, 65)

Your asks, in your order:

1. **Reverb and chorus.** On by default, driven by CC91, CC93 and the soundfont's effect sends. Turn them off with `fa_stream_set_midi_effects(stream, 0)` (.NET `MidiEffects = false`).
2. **GM/GM2/GS/XG SysEx.** Resets, drum parts on any channel, XG and GM2 drum banks, master volume and tuning.
3. **Tempo changes and time and key signatures.**
   - C: `fa_stream_get_midi_marks` (`FA_MIDI_MARK_TEMPO`, `_TIME_SIGNATURE`, `_KEY_SIGNATURE`, each with a readable `text` such as "E minor").
   - .NET: `stream.MidiMarks`.
   - Track names come through the text events with type `FA_MIDI_TRACK_NAME` (.NET `MidiTexts` with `MidiTextType.TrackName`). Title, artist and copyright are still in `fa_midi_info`.
4. **Notes held across a seek** start again at the target.
5. **SF3** loads with `fa_soundfont_open_path` / `_memory` (.NET `SoundFont.FromFile` / `FromMemory`).

Two related changes:

- **DLS.** DLS banks load directly, so you can drop your DLS-to-SF2 step. A MIDI stream opened without a soundfont uses the operating system's own bank: Windows `gm.dls`, macOS `gs_instruments.dls`, or an installed `.sf2` on Linux.
- **SF2 attenuation fix (PR 56).** It now follows the spec. Quiet instruments in some banks were up to 10× too loud before, so levels may differ from what you heard on early builds.

## Needed for parity

### 6. Output device changes (PR 63)

- **A device opened as the default** (`device_id` NULL, .NET `OpenOutput()`): when the default changes, Windows (WASAPI) and macOS/iOS move the output themselves. Every stream on it gets `FA_EVENT_DEVICE_CHANGED` with value 0 (.NET `DeviceChanged`).
- **An unplugged or disabled device**, whether one you chose or the default: the engine reopens the output on the system default. Streams stay attached and play on from where they were, the device clock keeps counting, the master volume is kept, and the event comes with value 1.
- **An explicitly chosen device** is kept until it is unplugged.
- The `fa_device*` stays valid through all of this, so "the device it moved to" is the same handle, now on the default. To show its name, enumerate the devices (`fa_device_get_info`) and take the one marked `is_default`.
- **Linux:** PulseAudio and PipeWire move streams without telling apps, so there is no event there.

### 7. Level metering (PR 60)

- `fa_stream_get_levels(stream, window, peak, rms, channels)` and `fa_device_get_levels` (.NET `GetLevels`, with allocation-free `Span<float>` overloads since PR 73), plus a spectrum (`fa_stream_get_spectrum`; .NET `GetSpectrum`).
- They measure the audio being heard.
- **Not on decode-only streams:** those return `FA_ERROR_INVALID_ARG`. As you planned, compute silence and trim levels from what `fa_stream_read` returns.

### 8. Stream info and tags (PR 51)

- `fa_stream_info` gained `bitrate`, `file_size`, `audio_bytes` and `bits_per_sample`.
- The tag reader is `fa_tags_*` (.NET `Tags`: `Artist`, `Title`, `Album`, `Track`, `Cover`, `Duration`, `Bitrate`, `Codec` and more).
- It reads ID3v1/v2, Vorbis comments, MP4, APE, and tracker titles.

### 9. Raw CD+G packs

Not built, as agreed in your CD+G note: with asks 1 to 4 below, `fa_stream_open_disc` plus an INDEXED8 surface covers discs. The per-track parity report you asked for is `fa_cdg_info.disc_parity` (see CD+G ask 4). Tell us if you still want pack access for diagnostics.

### 10. Seek cost while scrubbing (PR 62)

- `fa_stream_seek_async(stream, frame)` (.NET `RequestSeek`) returns in about 25 µs. The newest target replaces one not reached yet, so a drag costs one seek per worker pass. Position and clock report the target at once.
- If a seek fails (for example, a web server refuses the range), playback goes on where it was and `FA_EVENT_SEEK_FAILED` is raised (.NET `SeekFailed`).
- Worst cases for the blocking `fa_stream_seek` are measured in `docs/features/playback.md` ("What a seek costs"):

| Source | Worst case |
|---|---|
| AAC in MP4 | 3.4 ms |
| Ogg Vorbis | 10 ms |
| MP3 without a seek table | 61 ms, on the first far jump only |
| HTTP | a network round trip, often 50 to 500 ms |

- Volume and its ramps stay on the mix side, as you asked.

### 11. Clock calls from a render loop (PR 55)

- `fa_stream_get_clock` takes no lock and never waits for a seek, a worker or the audio thread, for attached streams and file streams. It is documented in the header as safe every vsync at 240 Hz and more, from any thread.
- One exception: a push or packet stream that is not attached takes the stream's lock briefly.

## CD+G surface asks (PRs 58 and 61)

1. **Background and border index:** `fa_cdg_frame.background_index` and `border_index`. Both are -1 before the first preset or without graphics. .NET: `CdgFrame.BackgroundIndex`, `BorderIndex`.
2. **Transparent-colour packs kept separate:**
   - `fa_cdg_frame.transparency[16]` (0..63; .NET `Transparency`).
   - Palette RGB never has alpha folded in, and `palette[i][3]` stays 255 without `FA_CDG_TRANSPARENT`.
   - `FA_CDG_TRANSPARENT` itself is unchanged. The new `FA_CDG_TRANSPARENT_BACKGROUND_ONLY` (.NET `TransparentBackgroundOnly`) makes only the background index transparent.
   - `FA_CDG_HIDE_UNTIL_PRESET` (.NET `HideUntilPreset`) is the optional flag your addendum suggested.
3. **Revision counters:** monotonic `uint64_t revision` (pixels) and `palette_revision`. The palette revision also goes up on background, border or transparency changes. .NET: `Revision`, `PaletteRevision`.
4. **Checkpoints:**
   - They are shared by all surfaces of a stream and placed every 10 s; the spacing doubles past 30 minutes, up to 180 checkpoints.
   - `.cdg` files and memory: built at the first surface create, about 4 ms for a 5-minute song.
   - Discs: built as packs arrive.
   - Any seek replays at most `fa_cdg_info.checkpoint_interval` of packs, even the first seek.
   - Each checkpoint holds the screen, colour table, background, border, transparency and scroll state.
   - `fa_stream_get_cdg_info` (.NET `stream.CdgInfo`) reports the source, pack count, checkpoint interval and count, and `disc_parity`. On discs, `disc_parity` 1 means the P/Q parity chose the layout; 0 means the packs were applied as read.
5. **Timing rule.** Documented in the header at `fa_cdg_surface_update_at`, and it is **ceil, not floor**:
   - With g = time - CD+G offset, packs [0, ceil(g × 300)) are applied. Pack n occupies [n/300, (n+1)/300) and counts as soon as its slot starts.
   - At exactly 1.0 s, packs 0..299 are applied, the same as yours. At 1.0 s + 1 ns, pack 300 is applied too, where floor would still be at 299.
   - Please switch your parity tests to ceil.
   - Yes, `update_at` subtracts the stream's CD+G offset (`fa_stream_set_cdg_offset`, .NET `CdgOffset`). Leave it at 0 and apply your own.
6. **Threads and cost:**
   - `update_at` never waits for the device or decode threads. It shares one short per-stream lock with graphics loads and disc pack writes.
   - An update with nothing new compares the palette and returns, without copying pixels.
   - `pixels` stays valid until the next update of that surface.
   - One surface is updated from one thread at a time.

From your addendum:

- **Surface options:** `FA_CDG_BACKGROUND_COLOR` / `background_color` (ARGB override) and `FA_CDG_BACKGROUND_OPACITY` / `background_opacity` (0 to 100 %). The frame's `background` field is the background colour. .NET: `BackgroundColor`, `BackgroundOpacity`, `CdgFrame.Background`.
- **Push model (PR 61):**
  - C: `fa_stream_set_cdg_events(stream, 1)` raises `FA_EVENT_CDG_CHANGED`, with the dirty area and a palette flag packed in `value` (`FA_CDG_CHANGE_*` macros).
  - .NET: subscribing to `AudioStream.CdgChanged` turns it on.
  - The pull model is unchanged.

## Packaging (PR 59)

- **Feed:** NuGet `FatzAudio`, with `win-x64` and `linux-x64` natives under `runtimes/<rid>/native/`, targeting net8.0 and net10.0. It is published to GitHub Packages at `https://nuget.pkg.github.com/FatzBomb75/index.json`, which needs a token with `read:packages`.
- **Prereleases:** every merge to main that changes code publishes `<version>-ci.<run>`. **Pin `1.0.0-rc.1` (the release candidate for 1.0.0, published 2026-11-02 from main @ 59741a5, PR 192)**; do not pin `-ci.N` builds. Stable versions are published by a manual workflow run.
- **Versioning:**
  - One version for the C ABI and the package: `FA_VERSION_*` in the header and `fa_version()` at run time. .NET: `AudioEngine.NativeVersion`.
  - Before 1.0, a minor bump may break the API; every break is listed in `CHANGELOG.md`.
  - The .NET binding throws `InvalidOperationException` on its first native call if the native library's major.minor differs, so a mismatched DLL fails at once.
- **Linux natives** are built on Ubuntu 22.04, so they need glibc 2.35 or later. The SONAME follows the version (`libfatzaudio.so.0.2` at the first answer; check the rc's file name).

## Nice to have

- **Sinc resampler for device-rate conversion (PR 69).** 44.1 kHz to 48 kHz now goes through the same windowed-sinc resampler as rate and pitch. The image of a 17 kHz tone dropped from 8 dB to 108 dB below the tone. PR 72 extends it to pushed frames at other rates and to chained Ogg rate changes.
- **Cheaper playback (PR 73).** Device-rate conversion uses kernels computed once per rate pair, about 5x cheaper (a 44.1 kHz stream on a 48 kHz device went from about 3.4% to 0.6% of a core). The limiter and EQ are about 2x faster, and Opus and FLAC streams hold about 180 KB less each. The audio is unchanged, and nothing on your side needs to change.
- **Faster HE-AAC and ALAC (PR 74).** HE-AAC decodes about 35% faster and ALAC up to 30% faster, with the same output.
- **Tracker Amiga resampler off by default (PR 75).** MOD files now use libopenmpt's normal resampler, as libopenmpt does: about 19x cheaper, cleaner and brighter. Set `TrackerAmigaResampler = true` (`FA_TRACKER_AMIGA_RESAMPLER` 1) if you want the Paula sound.
- **Faster mixing and CD+G (PR 76).** Mixing steady streams costs about half, 5.1/7.1 device resampling about a third, and a full 1080p smooth-scaled CD+G redraw drops from about 63 ms to about 11 ms, pixel for pixel the same. Nothing changes on your side.
- **Faster codecs (PR 77).** HE-AAC decodes about 25-33% faster, AAC-LC and AC-3 about 10-15%, ALAC and MIDI about 10%, and FLAC encoding about 1.8x, with the same output. Nothing changes on your side.
- **Less memory per stream (PR 78).** A web radio stream holds about 2.2 MB less, a stream with a levels or spectrum meter about 540 KB less, HE-AACv2 about 190 KB less, and every stream about 26 KB less (the stream struct is 14 KB instead of 24 KB). One behaviour change: `fa_stream_set_eq` / `fa_stream_set_eq_enabled` (.NET `SetEqualizer`, `EqualizerEnabled`) now allocate the EQ on first real use, so they can return `FA_ERROR_OUT_OF_MEMORY` (.NET throws `FatzAudioException`); calls that change nothing still always succeed.
- **RAMP_DONE when heard (PR 70).** Tempo, pitch and rate ramps raise `FA_EVENT_RAMP_DONE` when their end is heard, like volume ramps. If you await `RampAsync` on tempo, it now completes up to about 150 ms later than on early builds.
- **Transient mode (PRs 71 and 72).** `FA_STRETCH_TRANSIENTS` (.NET `StretchQuality.Transients`) in `fa_tempo_options.quality`. Pre-echo before drum hits drops from about -25 dB to about -50 dB, and hits stay on time. It is not the default: a note starting under a held chord can leave a faint seam. Since PR 72 it also covers hits in the last ~100 ms of a track.

## What you need to change

1. **Rebuild against the 1.0.0-rc.1 header** (update the package and native library together). These are the breaking changes since 0.1; the 0.3.0 `struct_size` rule is described in [Update 2026-11-02](#update-2026-11-02-since-pr-78):
   - `fa_cdg_frame` grew (new fields, no `struct_size`), so a struct compiled against the old header is too small. The .NET binding already matches.
   - `fa_dsp_callback` gained a `sample_rate` argument. .NET: `DspProcessor(Span<float> samples, int channels, int sampleRate)`.
   - The MinGW build is now `fatzaudio.dll` (was `libfatzaudio.dll`).
   - The Linux SONAME is `libfatzaudio.so.0.2`.
2. **Remove `FFmpeg.Load` / `FFmpeg.Info`** (C `fa_ffmpeg_load` / `fa_ffmpeg_get_info`). FatzAudio never loads FFmpeg.
   - Formats only FFmpeg decodes return `FA_ERROR_UNSUPPORTED_FORMAT`: DTS, TrueHD, WMA off Windows, and xHE-AAC without an OS decoder.
   - Decode those with your own FFmpeg and push the result (`fa_stream_open_push`), as your fallback already does.
3. **CD+G timing:** apply packs [0, ceil(t × 300)) instead of floor, and keep the stream's CD+G offset at 0 if you apply your own.
4. **NuGet:** add the GitHub Packages source with a `read:packages` token, and pin `FatzAudio` `1.0.0-rc.1` in `Directory.Packages.props` (a stable 1.0.0 comes after your real-device checks).
5. **Default-device following:** open with the default (NULL / `OpenOutput()`). Open by id only when the user chose a device; then FatzAudio keeps it until it is unplugged.
6. **Optional:** drop your DLS-to-SF2 conversion and your peak normalizer. Your own CD+G checkpoints are no longer needed for discs.

## Known limits, and what still needs BASS (or your FFmpeg)

Nothing in your list still needs BASS as a fallback. Below are the edges we know of.

**Not supported in FatzAudio** (BASS or your FFmpeg if you need them):
- MO3 modules with MP3- or Vorbis-compressed samples.
- DTS, TrueHD, WMA off Windows, xHE-AAC without an OS decoder: use your FFmpeg and the push stream, as above.

**Gaps against BASS:**
- **Trackers:** now exposed (extras step 18, live tempo/speed, global/channel/instrument volume, amplification); no gap left.
- **MIDI:**
  - GS/XG effect SysEx, insertion effects and drum edits are now implemented (extras steps 5-7). Closed.
  - A seek clears reverb and chorus tails.
  - Lyrics in Shift-JIS come out garbled (only UTF-8 and Windows-1252 are recognized).
  - SFZ is not supported.
  - Real-time MIDI event streams are now built (`fa_stream_open_midi`, `fa_stream_send_midi_events`; extras step 15). `BASS_MIDI_In*` (MIDI hardware input) is not.
- **bass_fx:** reverse playback (extras steps 8-9) and beat/BPM detection are now built.

**Behaviour edges:**
- **EQ:** decode-only streams are not equalized; use a DSP callback.
- **Levels:** decode-only streams don't report levels; measure from `fa_stream_read`.
- **Looping:** a looping source of unknown length (a seekable web stream with no length and no CUE_OUT) loops, but raises no `FA_EVENT_LOOPED` and its position doesn't wrap.

**Not run on real hardware yet.** Only you or Keith can run these on Windows:
- WMA through Media Foundation (`src/codec_mf.c`) has been compiled but never run.
- The WASAPI rerun of `tools/bass-compare/run.py --device-only`.
- A real CD drive and CD+G disc. Simulated tests were agreed, and the disc subcode parity convention is checked only against our own encoder.
- Android and iOS audio output have not run on a device.

Full feature docs are in `docs/features/`. The BASS-to-FatzAudio mapping is `docs/migration/bass.md`. Breaking changes are in `CHANGELOG.md`.
