# FatzBomb.MediaControl: reply on K4, K6, K8 and the FatzMedia names

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-03. Answers
`2026-11-03_FatzAudio_Note_K4K6K8.md` (checked against FatzAudio `main` @ 5182a4e: PRs 195, 196
and 197 merged).

Thank you: with K4, K6 and K8 every request from our 2026-11-01 note is done or dropped. Two new
requests (10 and 11, karaoke files with no audio and silence trim for karaoke) are at the end.

**Version pin.** Our engine slice (037A slice 2, PR 50) stays on `1.0.0-rc.1` so the owner's A/B
check runs against a fixed build. We move to the newest build when we start the karaoke slice
(slice 3), which is the first to use K4, K6 and K8. Please give that build a version that stays
on the feed (an `rc.2`, or a CI version you will not delete), since we pin exact versions.

## Your questions

1. **Lyrics-only files: a silent stream we never play is enough.** No stream-less surface is
   needed. We use it while BASS plays the audio and on machines without a GM bank; when
   FatzAudio plays the song we draw from the playing stream. Two things to confirm:
   - Opening it is cheap: it does not open a device, take a decode worker or mix, since it is
     never played.
   - Animation lengths follow tempo on a playing stream. Does `fa_stream_set_tempo` work on a
     lyrics-only stream so we can mirror the tempo BASS plays at, or do lyric animations take
     their length from something else there?

   And one question (not a request): while BASS plays an MP3+G, can a CD+G surface run on a
   stream that is opened but never played, without decoding the audio?

2. **Frame timer: .NET `Stopwatch` on every platform we ship.** `Stopwatch` is the high-resolution
   counter: QueryPerformanceCounter on Windows, `CLOCK_MONOTONIC` on Linux, your time base in
   both cases.
   - **Windows, Direct3D11:** a per-monitor vblank clock waits on `IDXGIOutput.WaitForVBlank`
     and keeps the blank times in `Stopwatch` ticks. We will pass the next blank through
     `AudioEngine.MonotonicTimeFromStopwatch`. Planned later: the swap chain's present statistics
     (`SyncQPCTime`, the time a frame actually reached the screen) to correct that estimate.
   - **Linux (Vulkan), Software renderer, Avalonia and WinForms hosts:** no vblank time. The
     host's compositor shows our shared image or bitmap, so we pace on `Stopwatch` and target
     "now + presentation lead" (0.5 to 1.25 refreshes, at most 30 ms), the same time the video
     frame uses.
   - **macOS, iOS, Android:** not supported by our engine today.

3. **Naming: it fits our layering, and our engine is the `FatzMedia.Video` in your list.**

   | Package | What it owns |
   |---|---|
   | `FatzMedia.Audio` | Decode, mix, output, analysis, and the CD+G and MIDI lyric surfaces |
   | `FatzMedia.Video` (today FatzBomb.MediaControl) | Playback with a picture: video decode, A/V timing, composition, karaoke presentation, renderers, platform and UI adapters; audio through `FatzMedia.Audio` |
   | `FatzMedia` | The suite: `FatzMedia.Audio` + `FatzMedia.Video`. Renderers and UI adapters stay separate packages that the app chooses. |

   - Our packages: `FatzMedia.Video`, `.Direct3D11`, `.Vulkan`, `.Software`, `.Text`,
     `.Windows`, `.Windows.Direct3D11`, `.Linux`, `.Wpf`, `.Avalonia`, `.WinForms`. Our test
     players become `FatzMedia.Video.Sample.Player` (WPF, Avalonia, WinForms and a shared view
     model).
   - No `FatzMedia.Player` library. A player we may sell later is an application with its own
     name and namespace, built on FatzMedia. Our control type stays `Player`: developers see that
     they add a player control, not an app.
   - Agreed that audio-only apps reference `FatzMedia.Audio` directly. An app that wants a
     picture for audio (CD+G, MIDI karaoke, a visual background) or one API for audio and video
     still comes to `FatzMedia.Video`.
   - Agreed that CD+G and lyrics stay in Audio. That is our slice 3 design: we draw from your
     surfaces and keep only composition and presentation.
   - FatzBomb Entertainment in Authors, Company, copyright, LICENSE and assembly metadata: agreed.
   - We rename in a stage of its own after our engine decision (037A slice 6), when our audio
     packages have settled. When `FatzAudio` becomes `FatzMedia.Audio`, please tell us the first
     version under the new ID, so we update our pin and feed mapping in one step.

## New requests

**10. Karaoke files with no audio.** These play on their own today and must keep doing so after
BASS goes; otherwise we keep our own silent clock for them, the second path we want to remove.
- **CD+G with no audio:** a `.cdg` alone (no audio file next to it), or a ZIP holding only the
  `.cdg`. In FatzAudio the graphics are a side stream of an audio stream, so a lone `.cdg` has
  nothing to open. Please let `fa_stream_open_path` / `fa_stream_open_memory` of a lone `.cdg`
  open a silent stream whose length is the graphics' (`pack_count / 300`) and that plays,
  pauses, seeks and changes tempo like any file stream, so its clock drives the surface exactly
  as with an MP3+G. (Attaching the graphics to a silent `fa_stream_open_callback` stream with
  `fa_stream_load_cdg_path` would work, but as far as we can see that stream has no length or
  seek.)
- **MIDI/KAR with lyrics and no notes:** a valid Standard MIDI File can carry only lyric, text
  and tempo events (lyric tracks exported for a backing track). Such a file should open as the
  same kind of playable silent stream, its length the last event, with no soundfont needed
  (today a missing soundfont fails the open even though nothing would sound). Lyrics-only
  (K6) covers drawing it; this is about playing it.
- **Question, not a request:** an audio file with its lyrics in a separate MIDI/KAR next to it
  (as an MP3 has its `.cdg`). We do not support that pairing today; do you plan it?

**11. Silence trim for karaoke files.** MP3+G songs often have silent audio intros and outros
while the graphics show a title screen or a count-in, and a KAR's title text can come before its
first note. Trimming on audio alone cuts those. Today we never trim an MP3+G (its audio is not
analysed), and a MIDI trims on its sound. Please report, next to `first_audible` /
`last_audible`:
- for CD+G: the time of the first and the last pack that changes the picture (memory presets
  that only clear to the same colour do not count);
- for MIDI/KAR: the first and last lyric or text event.

The policy stays with the host, as an option that defaults to off for karaoke: trim only where
the audio is silent *and* nothing is shown (start at the earlier of the first sound and the first
graphics or lyric, end at the later of the last ones). With the times reported, an app can also
choose its own rule.
