# From FatzAudio to FatzMedia / MediaControl: 1.0.0-rc.1 is ready to try

**Package:** NuGet id `FatzAudio`, exact version `1.0.0-rc.1` (pin it; do not float on `-ci.*` builds).
**Feed:** GitHub Packages, `https://nuget.pkg.github.com/FatzBomb75/index.json` (needs a token with `read:packages`). Built from main @ 59741a5 (PR 192); win-x64 and linux-x64 natives inside, net8.0 and net10.0.
**Handoff doc:** `/mnt/project-files/fatzaudio/mediacontrol/answer-to-mediacontrol.md` (see "Update 2026-11-02"). Docs site zip: `/mnt/project-files/fatzaudio/docs/FatzAudio-docs-2026-11-02.zip`; BASS mapping `docs/migration/bass.md`.

## Breaking changes since your last build (details in CHANGELOG.md)
- Rebuild against the 1.0.0 header, and update the package and native library together. The .NET binding refuses a native library of another major.minor; Linux SONAME is `libfatzaudio.so.1`.
- 14 output structs now start with `struct_size` (C callers set it to `sizeof`; the .NET API is unchanged). `fa_cdg_info` grew two fields.
- `DecodeError` is now `EventHandler<StreamEventArgs>`.
- Earlier ones still apply: CD+G packs use ceil, no `FFmpeg.Load`/`Info`, `fa_dsp_callback` has a `sample_rate`.
- FatzAudio's own code is proprietary (all rights reserved, LICENSE in the package).

## New for you
Karaoke timing K1-K3 (render-ahead never starves decoding, `update_into` never blocks and may return a "nothing new" frame, lyrics keep rendering through tempo ramps), silence detection (RMS, -50 dBFS, 50 ms, 200 ms shortest sound; peak mode), dropout counts (`GetStats`), loopback capture, live soundfont change, .NET TimeSpan/async additions.

## Plan
Add FatzAudio behind a switch next to BASS so you can A/B the two on the same files, then remove BASS when the checks below pass. The stable 1.0.0 follows once they do.

## Please run (real-device checklist)
`/mnt/project-files/fatzaudio/extras/2026-10-31_FatzAudio_Summary_Extras.md`, section "Pending real-world checks": reverse and slip on a deck; karaoke MIDI GS/XG effects against a real SC-88/MU; Dolby 5.1 downmix; Apple CAF files; real CD+G discs; radio/CDN/HLS; Android and iOS devices; keyboard-to-sound latency; beat/key accuracy on Keith's library; mic capture and loudness. Also rerun your slice-0 dropout test (the lower render-ahead priority only applies on Windows) and try loopback capture and the WASAPI device checks.

## Questions back to us
1. Is `dirty_count` 0 + `behind` 1 a good enough "nothing new" signal, or do you want a distinct flag?
2. Is up to 2% scroll length drift during tempo ramps (about 30 ms of a 1.5 s scroll) acceptable?
3. Is 200 ms the right shortest sound for your library?
4. K7 (INDEXED8 at an integer scale) still waits on your answer.
