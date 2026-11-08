# Stage Update

Stage: 037A.1 - Seam, no behaviour change
Date: 2026-11-01

## Status: slice 1 implemented; owner smoke passed 2026-11-02 (findings below are older behaviour)

Internal code no longer reads the BASS handles on `IAudioEngine` (`PlayHandle`, `SourceHandle`).
Each use is behind a small internal seam that the BASS engines implement with exactly the BASS
calls they made before, so a second engine (slice 2) can implement the same seams. Nothing was
removed: every engine, renderer, the CD+G engine, both lyric renderers, the extractors and all
tests stay. No FatzAudio reference was added. The public API is unchanged: `PlayHandle` and
`SourceHandle` stay (not `[Obsolete]`; that is slice 6's decision), and their XML docs now say
they are BASS-specific and unused internally.

BASS is not available in the Linux sandbox, so none of the BASS paths below ran here. They were
checked by reading (call by call against the old code) and by tests that record the order of
reads through the seams, which maps one-to-one onto the BASS calls.

## What moved behind which seam

| Use (before) | Seam (internal) | BASS implementation | Consumers |
| --- | --- | --- | --- |
| MIDI lyrics: `PlaybackCoordinator` passed `Engine.SourceHandle` to `IMidiLyricProvider` | `IMidiEventReaderSource.GetMidiEventReader()` on the engine; `IMidiLyricReaderProvider.ExtractLyrics(IMidiEventReader?, path, transition)` | `AudioEngine`, `VideoAudioEngine`, `SoundtrackAudioEngine` return `BassMidiEventReader.ForHandle(SourceHandle)` (null for 0); `BassMidiLyricProvider` implements both provider interfaces | `PlaybackCoordinator.TryLoadMidiLyrics` |
| Lyric cues: `MidiLyricCueBuilder` called `BassMidi.StreamGetMarks`, `ChannelGetLength`, `ChannelBytes2Seconds` on the handle | `IMidiEventReader` (`GetMarks(kind)`, `GetTempoEvents()`, `GetDurationSeconds()`, `PositionToSeconds(position)`) with neutral `MidiMark`, `MidiTempoEvent`, `MidiMarkKind` | `BassMidiEventReader` | `MidiLyricCueBuilder.ExtractLyrics(reader, ...)`; the old `ExtractLyrics(int, ...)` forwards to it |
| Karaoke metadata: `MidiMetadataExtractor` called `StreamGetMarks`/`StreamGetEvents(Tempo)` on the handle | `IMidiEventReader` | `BassMidiEventReader` | `MidiMetadataExtractor.Extract(reader, ...)` |
| Normalization peak analysis: `AudioNormalizer.AnalyzePeak*Async(int handle)` (`ChannelGetLevel`, `ChannelGetLength`, `ChannelBytes2Seconds`, `ChannelSeconds2Bytes`, `ChannelSetPosition`) | `IAudioLevelSource` (`TryReadPeak`, `TryGetDuration`, `TryGetPosition`, `TrySeek`, `TrySeekToStart`, `LastError`) | `BassChannelLevelSource.ForHandle(handle)` (null for 0) | `AudioNormalizer.AnalyzePeakQuickAsync`/`AnalyzePeakAsync`, called by `AudioEngine` (playback analysis, `AnalyzePeak*Async`, standalone `IMediaFileAnalyzer`) |
| Silence analysis: `AudioSilenceAnalyzer.ScanAsync(int handle)` (`ChannelSetPosition`, `ChannelGetPosition`, `ChannelBytes2Seconds`, `ChannelGetLevel`) | `IAudioLevelSource` | `BassChannelLevelSource` | `AudioSilenceAnalyzer.ScanAsync`, called by `AudioEngine` |
| Soundtrack end detection: `SoundtrackAudioEngine` read `_audio.PlayHandle` and called `Bass.ChannelIsActive` | `VideoAudioEngine.IsChannelPausedByDevice()` (moved, not an interface: `SoundtrackAudioEngine` wraps the concrete BASS push engine, which slice 5 replaces) | `VideoAudioEngine` | `SoundtrackAudioEngine` decode pump |

Already handle-free before this slice, and confirmed with a handleless test engine:

- **Fades:** `IAudioFadeGainTarget` (`BassFadeGainStage` inside the BASS engines), driven by
  `AudioFadeRunner` from `MediaControlPlayer.Fades`.
- **Equalizer:** `IAudioFilterHost` (`BassEqualizerFilter` inside the BASS engines), driven by
  `MediaEffects`.
- **Normalization:** `IAudioEngine.Normalize`, `NormalizationGain`, `AnalyzePeak*Async`; the gain
  FX stays inside the engine.
- **Trim:** `IAudioAnalysisOpenControl` and `IAudioTrimWindowProvider`, read by `AudioPipeline`.
- **CD+G pairing:** `CdgPipeline` pairs by position and state, never by handle.
- **Network metadata:** `INetworkAudioSource.StreamMetadataChanged`; the BASS tag reads stay in
  `AudioStreamManager`.

## Decisions

- **Lyrics from the engine's reader.** An engine that implements `IMidiEventReaderSource`
  reads the MIDI itself (the BASS engines: on their source channel, as before, including a null
  reader for handle 0). Any other engine is read through its `SourceHandle` with
  `BassMidiEventReader`, exactly as before the seam. MIDI parsing is FatzAudio's job: a FatzAudio
  engine will implement the reader with FatzAudio's MIDI event API (slice 2), so MediaControl has
  no MIDI file parser of its own.
- **Host providers keep the handle.** `IMidiLyricProvider` is public and unchanged. The built-in
  `BassMidiLyricProvider` also implements the internal `IMidiLyricReaderProvider`; a
  host-supplied provider that does not is still called with `Engine.SourceHandle`, as before.
- **Seams stay internal.** No host needs to implement them: the engines are created by the
  player. `IMidiEventReader` and `IAudioLevelSource` are shaped so a second engine (or FatzAudio
  decode-only stream) can implement them, and so the BASS implementations call BASS in the old
  order (the builders and analyzers call each member at the same point and as often as they
  called BASS).
- **The BASS effect stages keep their handles.** `BassFadeGainStage.Attach`,
  `BassEqualizerFilter.Apply`, `AudioNormalizer.SetupGainFx`, `BassChannelVolume`,
  `BassOutputWatchdog`, `AudioStreamManager` and the engines' own transport calls are the BASS
  implementations behind `IAudioEngine` and the seams above; the handle never leaves the BASS
  engines.

## Left on handles, and why

- `IAudioEngine.PlayHandle`/`SourceHandle` (public; slice 6 decides) and their implementations.
- `AudioPipeline.PlayHandle`: an internal forwarder with no caller. Left in place (nothing working
  is deleted in this slice); remove it with slice 6.
- `MidiLyricExtractor` (public, already `[Obsolete]`) and `MidiLyricCueBuilder.ExtractLyrics(int,
  ...)`: BASS-handle entry points, now forwarding to the reader path.
- Everything inside `AudioEngine`, `AudioStreamManager`, `VideoAudioEngine` and the `Bass*`
  helpers.
- Bootstrap/device code (`MediaControlServiceProvider` BASS init, `BassMidiSoundfontManager`,
  `PollingAudioDeviceMonitor`, `BassAudioOutputDevices`): not handle uses; slice 2 picks an engine
  per configuration.

## BASS call paths touched (for the Windows smoke)

1. MIDI/KAR lyrics and metadata on open (`PlaybackCoordinator.OpenAudio` path and archive):
   `AudioEngine.GetMidiEventReader` -> `BassMidiEventReader` -> `BASS_MIDI_StreamGetMarks`
   (Lyric, Text), `BASS_ChannelGetLength` + `BASS_ChannelBytes2Seconds`, then TrackName,
   Copyright, KeySignature, TimeSignature marks, `BASS_MIDI_StreamGetEvents(Tempo)`, then
   `BASS_ChannelBytes2Seconds` per lyric marker. Marker `Text` is now read when the markers are
   fetched, not later.
2. Normalization on open (quick and deep scans), `AnalyzePeakQuickAsync`/`AnalyzePeakAsync`, and
   the standalone `AnalyzeAsync`/prepared-next analysis: `AudioNormalizer` ->
   `BassChannelLevelSource` -> `BASS_ChannelGetLevel` (stereo), `BASS_ChannelGetLength`,
   `BASS_ChannelBytes2Seconds`, `BASS_ChannelSeconds2Bytes`, `BASS_ChannelSetPosition`.
3. Silence analysis/trim cache warm-up: `AudioSilenceAnalyzer` -> `BassChannelLevelSource` ->
   `BASS_ChannelSetPosition(0)`, `BASS_ChannelGetPosition`, `BASS_ChannelBytes2Seconds`,
   `BASS_ChannelGetLevel`; failures throw the same messages (with `Bass.LastError`).
4. Soundtrack end detection while the output device is lost (Audio mode video soundtrack, network
   audio through FFmpeg): `VideoAudioEngine.IsChannelPausedByDevice` -> `BASS_ChannelIsActive`.

Not touched: fades, crossfades, EQ, volume, pitch, tempo, loop, seek, device recovery, CD+G,
video audio push, network streams (their calls are unchanged; smoke them as regression checks).

## Manual Windows smoke

With the BASS and FFmpeg natives in the host's `native/` folder, in the WPF Player (repeat the
first three on Avalonia or WinForms):

1. **MIDI/KAR lyrics:** open a `.kar` with lyrics and a `.mid` with text-event lyrics: lyrics
   show as before (same line breaks, wipe timing 400 ms ahead of the note, page transitions);
   the karaoke metadata overlay shows the same title, artist, key, tempo and time signature. A
   Shift-JIS `.kar` looks as it did before (no change expected). A `.rmi` with lyrics still
   loads. A `.mid` without lyrics shows no lyric track.
2. **Archive:** open a `.kar` from a zip (with an image background): lyrics and background as
   before.
3. **Fades and crossfades:** `FadeOutAsync`/`FadeInAsync` and a prepared-next fade-through
   transition: same ramps, no step, Stop restores full gain.
4. **EQ:** turn the equalizer on, change bands while playing, turn it off: no gap, as before.
5. **Normalization:** with Normalize on, open a quiet track: the quick then deep gain arrive
   (diagnostics "Quick normalization complete" and "Deep normalization complete"), same gain as
   before; open it again: cached gain.
6. **Trim:** with silence trim on, open a file with leading/trailing silence twice: the second
   open starts at the trimmed start and ends at the trimmed end ("Silence analysis complete: N
   region(s)" in diagnostics, same counts as before). Prepared-next `AnalyzeAsync` on the next
   file warms the cache ("Standalone analysis complete").
7. **CD+G pairs:** open an MP3+CDG pair and a zip with an MP3+CDG pair: graphics in sync,
   seek and loop as before.
8. **Video soundtrack (Audio mode) and network audio:** play a video in Audio mode to its end
   (ends once), unplug the output device mid-play and plug it back (no false end); play an https
   or HLS audio stream and an http (BASS) stream: titles and buffering as before.
9. **MIDI playback itself:** tempo and pitch changes on a `.kar`, loop on, seek: unchanged.

## Verification on Linux

- `dotnet build FatzBomb.MediaControl.sln -c Release -p:EnableWindowsTargeting=true`: 0 warnings,
  0 errors.
- Tests: Portable 1743 passed, Linux 110 passed (with `FATZBOMB_LINUX_FFMPEG_DIR`), Vulkan 102
  passed, Avalonia 47 passed. New: `AudioEngineSeamTests`,
  `AudioLevelSourceSeamTests` (in `FatzBomb.MediaControl.Tests`, linked into Portable). No
  existing test or assertion was changed.
- `dotnet format --verify-no-changes`: clean. Boundary scripts (core, adapter, WPF, presentation
  parity): pass.
- Not verified: anything that calls BASS (absent here), the Windows test project's run, WPF and
  D3D11.

## Owner smoke, 2026-11-02 (Windows, WPF player)

All eleven checks behaved as before: KAR, MIDI and RMI lyrics and metadata, pitch, tempo, loop
and seek, KAR and MP3+CDG from a zip, normalization with the cache, trim on the second open, EQ,
fades and the prepared-next transition, video in Audio mode, network audio. Three findings, none
caused by this slice (the code involved is unchanged):

- **31 Hz EQ band seems to do nothing.** The BASS_FX peaking band at 31 Hz is unchanged; little
  music or playback hardware has much energy there. Re-check on FatzAudio's EQ in slice 2 with a
  bass-heavy track; if it still does nothing, investigate the band's bandwidth.
- **A video logged gain 1.000 for both passes:** not a bug. It was a background video with a
  silent audio track (peak 0, so the gain stays 1.0); normalization works on videos with audio (the
  owner confirmed). ENG-041 records it and the clearer log line.
- **Silence trim only from the second play:** the trim window is used only when a cached analysis
  exists at open (ENG-040). The owner wants it analysed and applied on the first play; FatzAudio has
  no silence analysis, so this is ours (or a request to FatzAudio).
