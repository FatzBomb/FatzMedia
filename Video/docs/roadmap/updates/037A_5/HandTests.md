# FatzAudio hand tests (037A)

Run the script: it measures what it can by itself, then walks you through the rest.

```powershell
cd D:\development\FatzBomb\FatzMedia\Video
powershell -ExecutionPolicy Bypass -File scripts\smoke-037A.ps1
```

About 30 minutes. It builds first, then:

- **Part 1, measured (about 3 minutes, nothing to do).** Test tones through the video-audio path (FFmpeg decode
  into FatzAudio), on your default output device (quiet tones; `-SilentPart1` uses FatzAudio's null device
  instead). It measures sync drift at tempo 0.75, 1.0, 1.25 and 1.5, seeks (landing on the target), loop wraps,
  pause/resume and one steady minute, and records the output to count clicks and dropouts.
- **Part 2, your eyes and ears.** For each check the script starts the WPF player with `--audio=fatz`, the right
  flags and a file you picked, tells you what to look and listen for, and asks Pass, Fail or Skip. On a Fail you
  can type a comment. It closes the player after each check.
  1. sync at tempo 0.75 to 1.5 (lips or drum hits against the sound)
  2. seeks
  3. loops
  4. pause and resume
  5. two screens, 1080p, the first minute: no clicks
  6. CD+G karaoke (FatzAudio draws it; smooth scrolling should glide)
  7. Audio-mode soundtrack
  8. MIDI karaoke (FatzAudio draws the lyrics now)
  9. loudness (a quiet and a loud file about equally loud)
  10. network radio (paste a URL)
  11. output device (`--audio-device=`) and plugging devices in and out
  12. no BASS at all: the script makes a copy of the player without any `bass*.dll` and plays from it

Results go to `D:\development\FatzBomb\work\fatzmedia\Video\test-results\FatzBomb-037A-<date>\`
(`results.txt`, `results.json` and the player logs) and the summary is put on your clipboard: paste it into the
chat.

Part 1 was run on 2026-11-10 on this PC (null device): sync drift at most 11.7 ms over 12 s at any tempo, seeks
landed on the target, loops and pause/resume and seeks had no clicks once the push-stream fade-in fix was in.
