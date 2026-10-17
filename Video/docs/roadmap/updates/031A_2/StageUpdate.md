# Stage Update

Stage: 031A.2 - Player host controls for prepared-next, fades, network, and equalizer
Date: 2026-10-17

## Status: IMPLEMENTED - pending manual smoke

The validation Player previously exercised 031A/031B/014A/020A only through startup flags,
so a manual transition, manual fades, and live equalizer changes could not be tried by
hand. Host-only change (no Core/adapter code):

- **Next flyout** (new toolbar button): Choose... (file dialog -> `PrepareNextAsync`),
  state line (analyzing / ready with duration and gain / stale), Play next now
  (`TransitionToPreparedNextAsync`), Clear, Auto-next, FADE length (0-10 s, drives
  `PreparedNextTransitionFade`), Fade out / Fade in / Cancel (`FadeOutAsync`,
  `FadeInAsync`, `CancelFade`; 3 s when FADE is 0), and a URL box with Open
  (`MediaInputSource.FromUri`) plus the stream title / buffering line.
- **Audio flyout**: 10-band EQ (on/off, -12..+12 dB per band, Flat) applied live via
  `Equalizer`.
- `--prepare-next=`, `--auto-next`, `--fade-next=`, `--url=`, and `--eq=` now seed these
  controls instead of being the only way in.

## Decisions made without the owner

- Manual fades reuse the FADE slider length (3 s when it is 0) instead of a second slider.
- EQ range is +-12 dB in the host (Core allows +-15) to keep the sliders compact.
- The host has no test project (it is the manual validation app); the change is a thin
  pass-through to Core APIs that are covered by the portable tests.

## Manual smoke (Windows, native DLLs)

1. Open the Next flyout, Choose... a second song while the first plays: the state line
   goes analyzing -> ready with a duration; playback does not glitch.
2. FADE 4 s, Play next now: the current song fades out, the next fades in.
3. Repeat, but press Pause during the fade-out: the next song does not load and the
   current song returns to full volume.
4. Repeat, Choose... a third song during the fade-out: the transition is abandoned and
   the third song stays prepared.
5. Fade out (current song goes silent), Fade in (comes back), Fade out then Cancel
   halfway: the volume stays at the reached level.
6. Fade out near the end of a song and let it end; press Play: the song is audible.
7. Auto-next on, let the song end: the prepared song starts on its own.
8. URL: paste an Icecast MP3 station, Open: it plays, the title line shows
   "Artist - Title" and updates on track change; throttle the network: "Buffering N%".
9. Audio flyout: EQ on, raise 63 Hz +9 dB: audible bass; Flat: back to neutral; EQ off:
   identical to before. Change bands with nothing loaded, then load a song: the setting
   applies.
10. All five flyouts open one at a time on both renderer paths (`--software` too).
