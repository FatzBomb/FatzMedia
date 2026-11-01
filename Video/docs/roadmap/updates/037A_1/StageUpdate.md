# Stage Update

Stage: 037A.1 - Seam, no behaviour change
Date: 2026-11-01

## Status: slice 1 implemented - pending manual smoke

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

- **Lyrics from the engine or the file.** An engine that implements `IMidiEventReaderSource`
  reads the MIDI itself (the BASS engines: on their source channel, as before, including a null
  reader for handle 0). An engine without it gets lyrics from the file or the archive entry the
  player opened, through `StandardMidiFileReader` (managed SMF format 0/1/2 and RIFF RMID, tempo
  map, SMPTE division; ticks as positions; text as UTF-8 when valid, else Latin-1; files over
  16 MB are skipped). Today only the test doubles take the file path; on BASS nothing changes.
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
- **Behaviour change for a hypothetical host engine.** A host that replaced the player's
  `IAudioEngine` with its own BASS-backed engine and kept the built-in provider would now get
  lyrics from the file instead of from its `SourceHandle`. The player creates its engines
  itself, so no known host does this.

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
- Tests: Portable 1754 passed, Linux 110 passed (with `FATZBOMB_LINUX_FFMPEG_DIR`), Vulkan 102
  passed, Avalonia 47 passed. New: `StandardMidiFileReaderTests`, `AudioEngineSeamTests`,
  `AudioLevelSourceSeamTests` (in `FatzBomb.MediaControl.Tests`, linked into Portable). No
  existing test or assertion was changed.
- `dotnet format --verify-no-changes`: clean. Boundary scripts (core, adapter, WPF, presentation
  parity): pass.
- Not verified: anything that calls BASS (absent here), the Windows test project's run, WPF and
  D3D11.
