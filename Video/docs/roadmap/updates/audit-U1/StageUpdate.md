# Stage Update

Item: audit 2026-11-12, U1 - remove the managed CD+G engine and the managed MIDI lyric cue path
Date: 2026-11-12

## Status: implemented, pending manual smoke

FatzAudio is now the only CD+G and MIDI lyric implementation.

Removed (a copy is in `work\fatzmedia\removed-code\2026-11-12-managed-cdg-lyrics`, outside the repo):
`CdgEngine`, `CdgStateMachine`, `CdgColorManager`, `CdgPacket`, `CdgSnapshotStatistics`;
`MidiLyricCueBuilder`, `MidiLyricProvider`, `IMidiLyricProvider`, `MidiMetadataExtractor`, the
`IMidiEventReader` / `IMidiEventReaderSource` seam and `FatzAudioMidiEventReader`; their tests
(`CdgEngineCheckpointTests`, `CdgStateMachineTests`, `CdgSnapshotOwnershipTests`,
`MidiLyricCueBuilderTests`, `MidiMetadataExtractorTests`, the reader seam tests).

Kept: `CdgConstants` (now also `PacketCountAt`, the one pack-timing rule), the snapshot/dirty-rect types,
`ICdgEngine`, `ICdgEngineSource`, `KaraokeMetadata` (the Core renderers still draw an intro card from it).

Decisions:
- Without an audio engine the player gets `UnavailableCdgEngine`: CD+G shows nothing and one Warning
  (`CDG` category) says FatzAudio is not running. MIDI audio cannot open without the engine, so there is
  nothing to warn about for lyrics beyond the existing `Bootstrap` warning.
- `FatzAudioCdgEngineNativeTests` compared FatzAudio's picture with the managed state machine; they now
  compare a position reached by playing or seeking with a fresh FatzAudio engine rendered straight there.
- `ShowMidiLyrics` keeps existing but no longer selects a cue track; FatzAudio-drawn lyrics were never
  switched by it.

Manual smoke:
1. Play a `.cdg` + audio pair and a `.kar` file: picture and lyrics show as before.
2. Rename `fatzaudio.dll` next to the player (copy of the folder only), start a player and open a `.cdg`:
   no picture, one CD+G Warning in the diagnostics, no crash.
