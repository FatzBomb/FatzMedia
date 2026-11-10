# FatzAudio hand tests (037A slices 3-5)

For Keith, on the Windows PC with speakers and two screens. About 30 minutes. Each step: run it once with
`--audio=fatz` and, where it says "compare", once without (BASS). Note pass or fail and anything odd.

## Setup

```powershell
cd D:\development\FatzBomb\FatzMedia
git checkout main
git pull
dotnet build Video\FatzMedia.Video.sln -c Release
dotnet run --project Video\FatzMedia.Video.Sample.Player -c Release -- --audio=fatz
```

The Log should say `Audio engine: FatzAudio (files, video audio and CD+G; BASS is not used)`.

## Video sync (the main ones)

- [ ] **Tempo 0.75 to 1.5.** Play a music video with clear lip sync or drum hits. Set tempo to 75%, 100%, 125% and
  150% (pitch stays). At each, watch for 20 seconds: lips and hits stay on the sound, no drift. Compare with BASS.
- [ ] **Seeks.** Click and drag on the position bar, forward and back, about ten times while playing. After each,
  picture and sound restart together within a moment; no burst of old audio. Then pause, seek, and play: it starts
  where you sought.
- [ ] **Loops.** Turn loop on with a short video (under a minute). Let it wrap three times: no gap, no click at
  the wrap, the picture wraps with the sound. Turn loop off: it ends once.
- [ ] **Pause/resume.** Pause and resume ten times at random moments: no click, and sync holds after each resume.
- [ ] **Two screens, 1080p, first minute.** Start with `--audio=fatz --second-screen=2` (the number of your second screen), play a 1080p video with sound on both screens. Listen to the whole first minute: no clicks, pops or
  dropouts. (This is ENG-047's second case.)

## Also

- [ ] **CD+G.** An MP3+G and a ZIP karaoke song: graphics in time with the words; seek, pause and loop keep the
  picture right. A song you know scrolls smoothly should now glide.
- [ ] **Audio-mode soundtrack.** `--audio=fatz --mode=audio` with a video file: the sound plays, the position bar
  moves, seeks work.
- [ ] **Network audio.** A plain `http://` radio stream and an `https://` one: they play, song titles appear.
- [ ] **MIDI.** A `.kar`: plays with lyrics; the Log has no `BASSMIDI` lines.
- [ ] **Loudness.** A quiet and a loud music video one after the other: about the same loudness. The Log shows
  `Soundtrack loudness ... LUFS`.
- [ ] **No BASS at all.** Copy the player's output folder to `D:\development\FatzBomb\work\fatzmedia\Video\scratch`,
  delete every `bass*.dll` from the copy, run the copy with `--audio=fatz`, and play an MP3, an MP3+G and a video
  with sound. All play. (Without `--audio=fatz` the copy should start with no sound and say BASS was not found.)
  Delete the copy afterwards.

Report back with the list ticked, and for any fail: the file, what you heard or saw, and the Log lines around it.
