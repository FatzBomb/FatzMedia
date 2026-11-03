# FatzBomb.MediaControl: reply on K4, K6, K8 and the FatzMedia names

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-03. Answers
`2026-11-03_FatzAudio_Note_K4K6K8.md` (checked against FatzAudio `main` @ 5182a4e: PRs 195, 196
and 197 merged).

Thank you: with K4, K6 and K8 every request from our 2026-11-01 note is done or dropped.

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

   **New request 10: CD+G files with no audio.** We play a `.cdg` on its own today (no audio
   file next to it, or a ZIP holding only the `.cdg`), on a silent clock with play, pause, seek
   and tempo. In FatzAudio the graphics are a side stream of an audio stream, so a lone `.cdg`
   has nothing to open. Please let `fa_stream_open_path` / `fa_stream_open_memory` of a lone
   `.cdg` (and of a ZIP with only a `.cdg`) open a silent stream whose length is the graphics'
   (`pack_count / 300`), and that plays, pauses, seeks and changes tempo like any file stream, so
   its clock drives the surface exactly as with an MP3+G. Without it we would keep our own
   silent clock for these files after BASS goes, which is the second path we want to remove.
   (Attaching the graphics to a silent `fa_stream_open_callback` stream with
   `fa_stream_load_cdg_path` would work, but as far as we can see that stream has no length or
   seek.)

2. **Frame timer: .NET `Stopwatch` on every platform we ship.**
   - **Windows, Direct3D11:** a per-monitor vblank clock waits on `IDXGIOutput.WaitForVBlank`
     and keeps the blank times in `Stopwatch` ticks (QPC). We will pass the next blank through
     `AudioEngine.MonotonicTimeFromStopwatch`. We do not read `DXGI_FRAME_STATISTICS` today.
   - **Linux (Vulkan), Software renderer, Avalonia and WinForms hosts:** no vblank time. The
     host's compositor shows our shared image or bitmap, so we pace on `Stopwatch`
     (`CLOCK_MONOTONIC` on Linux, your time base as it is) and target "now + presentation lead"
     (0.5 to 1.25 refreshes, at most 30 ms), the same time the video frame uses.
   - **macOS, iOS, Android:** not supported by our engine today.

3. **Naming: it fits our layering, and our engine is the `FatzMedia.Video` in your list.**

   | Package | What it owns |
   |---|---|
   | `FatzMedia.Audio` | Decode, mix, output, analysis, and the CD+G and MIDI lyric surfaces |
   | `FatzMedia.Video` (today FatzBomb.MediaControl) | Playback with a picture: video decode, A/V timing, composition, karaoke presentation, renderers, platform and UI adapters; audio through `FatzMedia.Audio` |
   | `FatzMedia.Player` (later) | Player controls and the app, grown from our test players and their shared view model |
   | `FatzMedia` | The suite: `FatzMedia.Audio` + `FatzMedia.Video`. Renderers and UI adapters stay separate packages that the app chooses. |

   - Our renderer, platform and adapter packages follow it: `FatzMedia.Video.Rendering.Vulkan`,
     `FatzMedia.Video.Windows`, `FatzMedia.Video.Wpf`, and so on.
   - Agreed that audio-only apps reference `FatzMedia.Audio` directly. An app that wants a
     picture for audio (CD+G, MIDI karaoke, a visual background) or one API for audio and video
     still comes to `FatzMedia.Video`.
   - Agreed that CD+G and lyrics stay in Audio. That is our slice 3 design: we draw from your
     surfaces and keep only composition and presentation.
   - FatzBomb Entertainment in Authors, Company, copyright, LICENSE and assembly metadata: agreed.
   - We rename in a stage of its own, separate from the 037A slices and before we publish a
     package. When `FatzAudio` becomes `FatzMedia.Audio`, please tell us the first version under
     the new ID, so we update our pin and feed mapping in one step.
