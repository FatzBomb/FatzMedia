# Idea Stage 032A - Audio CD and CD+G Disc Playback

Status: Good idea. Owner decision (2026-10-17): audio CD and CD+G discs are in scope;
DVD and Blu-ray stay non-goals. Recommended after the active stages' manual smoke.

## Why This Is Worth Doing

Karaoke hosts still play CD+G discs directly, and the engine already owns everything
downstream of the disc: BASS audio playback, the CDG state machine and snapshots, and
CDG composition on every renderer. A disc source is the missing input. Future players
built on the control get disc support without re-implementing drive access, subchannel
decoding, or audio/graphics sync.

## Recommended Direction

- Use BASSCD (`basscd.dll`, already an optional plugin in the Windows bootstrap) for
  drive enumeration, the table of contents, and audio tracks. The managed binding is
  the `ManagedBass.Cd` package, alongside the existing `ManagedBass.*` 4.0.2 packages.
- A disc track is a new input source class (for example `MediaSourceClass.Disc`) in
  the 014A capability matrix: seekable, duration known, local, not interruptible
  during spin-up. No special-casing by path string.
- Plain audio CD tracks play through `AudioEngine`, so volume, tempo, pitch,
  normalization, equalizer, fades, and prepared-next work unchanged.
- CD+G: read the R-W subchannel alongside the audio (BASSCD's subchannel stream flag).
  Each sector carries 96 subchannel bytes, which is four 24-byte CDG packets, at 75
  sectors per second. That is the same 300 packets per second a `.cdg` file holds.
  Feed the packets to the existing `CdgStateMachine.ProcessPacket`, clocked by the
  audio stream position, so graphics stay in sync by construction.
- Detect CD+G per track (subchannel packets present with the CD+G command byte) and
  report it through the capability matrix and `MediaLoaded`; fall back to audio only.
- Drive and disc events (inserted, ejected, not ready) raise diagnostics and
  session events; the host decides what to play. Drive enumeration stays a host call,
  not an engine-owned library.

## Recommended Immediate Action

Before changing the pipeline, verify on real hardware: (1) which drives return usable
R-W subchannel data through BASSCD, with and without the drive's own de-interleaving;
(2) whether CD+G packets need Reed-Solomon error correction in practice or whether
dropping bad packets is enough; (3) seek accuracy and the latency between the audio
position and subchannel delivery. Record the results as a capability table per drive.

## Must Preserve

- `.cdg` file, MP3+G archive, and audio-plus-sidecar workflows.
- CDG timing, snapshot reuse (018A), and renderer parity.
- Core boundary: BASSCD is a BASS plugin like the others; no OS drive APIs in Core. If
  drive change notification needs OS integration, it belongs in the platform packages.

## Explicit Non-Goals

- DVD, Blu-ray, or video discs of any kind.
- Ripping, CD-Text editing, CDDB/MusicBrainz lookups, or disc library management.
- Burning or writing discs.
- Copy-protection workarounds.
