# Handoff

Updated: 2026-11-05. Branch `claude/modest-clarke-5i5piu` (restarted from `main` after each merge).
`main` holds everything through PR #56.

## State

- **037A FatzAudio, normalization: done on the FatzAudio engine** (PR #55, #56;
  `docs/roadmap/updates/037A_Normalization/StageUpdate.md`, pending the owner's Windows smoke).
  - `MediaPlayerConfiguration.Normalization` (`NormalizationOptions`): `TargetLufs` -14,
    `TruePeakCeilingDbtp` -1, `CacheDirectory` (null = per-user `AudioCache` folder, "" = off).
  - FatzAudio measures, applies and limits; nothing on our side waits. Our peak rule and our own
    peak measurement are gone from that engine (peaks come from FatzAudio's true peak). BASS unchanged.
  - Pin: FatzAudio `1.0.0-rc.1`. rc.3 is on the feed (faster scan, K4/K6/K8); we wait for **rc.4**
    (request 12 start wait, single shared cache file, lone .cdg fixes). The owner decides when it ships.
- **FatzAudio threads** (the owner passes messages; our side is in `docs/fatzaudio/`):
  - Request 12 (start a first play at a settled loudness) answered and in FatzAudio PR 203: start wait
    200 ms with the 75% rule, `LoudnessSource.Partial`, `LoudnessProvisional` event, provisional boost
    cap (default 12 = none; **we suggested 6 dB as our default**), `SetCacheSize` (5 MB), `ClearCache`,
    a shared cache file safe across engines and processes, audio-only fingerprint.
  - Answered: formats it cannot decode (037A doc), web-stream titles exist, `LoadCdg(bytes)` exists,
    `Tags.Read(bytes)` = `Tags.Read(path)`, stream details in `tags.Info`, a neutral stream costs
    ~0.3-0.5% of a core (no `NoEffects` option needed).
  - Pending from FatzAudio: the public archive API (zip, 7z, tar family; **no rar**), the public
    fingerprint (owner decides), pairing rules, archive timings.
- **Queue (owner-agreed 2026-11-05), each a roadmap idea in `docs/roadmap/future/`:**
  1. `051A` first-frame-ready signal + A/B swap pattern (TriviaBomb) - **next**.
  2. `047A` WPF shared GPU surface - **planned again** (owner 2026-11-05): the cost that matters is
     UI-thread time per frame (1-2 ms per 1080p surface, estimated), for every host animating over
     video; most shows run on laptops; venue displays are 4K. Slice 1: shared GPU image with the
     R1/R5/R7 guards. Slice 2: composited video on its own dispatcher thread (`HostVisual`/`VisualTarget`),
     so a busy app UI thread no longer pauses it. 051A adds the UI-thread time counter for before/after
     numbers (desktop and laptop). Slice 3: `WpfPresentationMode.Auto` (one-way switch to composited
     when the surface's own overlay content, opacity, transform or clip needs it; decided in the idea).
  3. `052A` host image overlay layer + "change together" + `FadePictureAsync`.
  4. `053A` `MediaInputSource.FromStream` (FFmpeg custom I/O; ask FatzAudio about re-openable I/O).
  5. `054A` Full idle-cost check (no `Simple` mode: fix idle costs in `Full`).
  6. `055A` `ReconfigureAsync`: extends 043A's existing `SetModeAsync` (mode on the fly, media kept at
     its position) to the audio engine, renderer, device and other settings.
  7. After rc.4: rc.4 pin move + start wait/cache size pass-through; `050A` tags and file info;
     `056A` archives through FatzAudio's reader, SharpCompress and rar dropped.
- Still active from before: 026A, 026B, 027A, 033A, 041A, 042A, 043A, 044A (see the manifest).
- The October code audit (ENG-039, round 2) is done: five batches implemented, each pending the
  owner's smoke (`updates/ENG-039/`). The old "next: code audit" item is closed.
- **Owner smoke backlog** (implemented, waiting for a Windows run): ENG-039 batches 1, 2, 4a, 4b, 4c, 5;
  037A slice 0 (Windows rerun) and normalization; ENG-043 (ALAC/M4R/audio-only MP4/TS); 039A_3;
  026A slices 1-2 (Vulkan); 026B (BASS/player on Linux); 027A slices 2 and 5 (Avalonia);
  033A slice 2; 042A slices 1-2 (HLG, HDR output); 043A slices 1-2 (modes); ENG-040 text on
  Software/Vulkan. Stages move to `complete/` as these pass.

## Decisions this session (owner)

- FatzAudio does all audio work; we pass settings through and add no waits of our own.
- Loudness is the only normalization on FatzAudio; the target is a code setting (no player flag).
- Tags and full file information are exposed (audio from FatzAudio, video from FFmpeg, hashes ours).
- One archive reader, FatzAudio's; rar is dropped entirely, SharpCompress goes with it.
- No `Simple` player mode; idle costs are fixed in `Full`.
- TriviaBomb (a WPF trivia host replacing LibVLC) designs for `WpfComposited`; native mode cannot
  switch overlay and video in the same frame (the overlay is a separate layered window).
- `WpfPresentationMode.Auto` becomes the default once 047A has landed; apps overlaying from outside the
  surface set `WpfComposited`.
- Composited (with 047A) is the mode when WPF content goes over video; native stays the mode for HDR,
  exact frame timing, or a heavy UI thread with nothing WPF on top (a UI stall pauses composited video).

## How to verify

Linux sandbox (FatzAudio is a token-gated package: build its rc.1 from source and pack it locally, as
in `updates/037A_2`, then restore with a `nuget.config` outside the repo mapping `FatzAudio` to it):

```bash
EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release
dotnet test FatzBomb.MediaControl.Portable.Tests -c Release --no-build
FATZBOMB_LINUX_FFMPEG_DIR=/opt/fatzbomb-native/ffmpeg/lib FATZBOMB_LINUX_SAMPLE_VIDEO=/opt/fatzbomb-native/media/h264_1080p.mp4 \
  dotnet test FatzBomb.MediaControl.Linux.Tests -c Release --no-build
dotnet test FatzBomb.MediaControl.Rendering.Vulkan.Tests -c Release --no-build
dotnet test FatzBomb.MediaControl.Avalonia.Tests -c Release --no-build
EnableWindowsTargeting=true dotnet format FatzBomb.MediaControl.sln --verify-no-changes
for s in check-core-boundary check-adapter-boundary check-wpf-boundary check-presentation-parity; do
  DOTNET_ROOT=$(dirname $(readlink -f $(which dotnet))) ~/.dotnet/tools/pwsh -NoProfile -File scripts/$s.ps1; done
```

- Last results: Portable 1766, Linux 145, Vulkan 102, Avalonia 47; 0 warnings.
- Tests set `CacheDirectory = ""` unless they use their own folder: no test writes to the user's cache.

Windows (owner): repo at `C:\development\nuget\FatzbombMedia`, media in `C:\media`, RTX 5090, three
monitors; smoke steps in each stage's `StageUpdate.md`; full PowerShell command sets; dark UI.

## Next steps

1. Owner: the normalization smoke (`updates/037A_Normalization`), and the 054A measurement
   (`--mode=full` vs `--mode=background` on a plain 1080p clip). 047A's before numbers come after 051A
   adds the UI-thread counter.
2. Start `051A`: move it to `active/`, write acceptance criteria, then implement across WPF, Avalonia
   and WinForms (WPF composited: raise in `WpfCompositedSurfaceHost.PublishPendingFrame`, same
   dispatcher turn as the bitmap write, after the host is visible; check that a hidden surface is
   still presented to). Also add the composited UI-thread time counter (047A's before numbers).
3. When rc.4 ships: pin move, start wait and cache size into `NormalizationOptions`, log the
   provisional start, rerun the timings.

## Open questions for the owner


- Provisional boost cap default: 6 dB (our suggestion) or FatzAudio's 12 (none)?
- FatzAudio's public fingerprint: build it (half a day on their side)?

## Next-session prompt

> Read HANDOFF.md. Start queue item 1, stage 051A (first-frame-ready signal and the A/B swap pattern
> for TriviaBomb): move `docs/roadmap/future/051A_IDEA_FirstFrameReadySignal.md` to `active/` with
> bounded acceptance criteria, investigate how each adapter (WPF native and composited, Avalonia,
> WinForms) learns that the first frame of a new load is on screen, propose the event's name and
> exact timing before coding, then implement, validate, open a PR, and give me PowerShell smoke steps.
