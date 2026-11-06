# Handoff

Updated: 2026-11-06. Branch `claude/zen-tesla-ux51s6`, restarted from `main` after PR #72 merged; it holds
one unmerged commit (a Full-mode first-frame native test and these notes): open a PR with the next batch.
`main` holds everything through PR #72.

## State

- **051A first-frame-ready: slice 1 implemented, pending the owner's Windows smoke**
  (`active/051A_ACTIVE_FirstFrameReadySignal.md`, `updates/051A_1/StageUpdate.md`).
  `MediaSurface.FirstFrameReady` on WPF, Avalonia, WinForms (once per load, UI thread; WPF composited in
  the bitmap-writing operation, after the host is visible); Core's tracker numbers loads
  (`FirstFrameLoad`). WPF `CompositedUiThreadTime`/`CompositedFramesPublished`, shown in the WPF test
  player's stats line (047A's before numbers come from the smoke). The 500 ms `StartupMediaDelay` is
  gone (binding is synchronous; attach re-presents). New findings ENG-058, ENG-059. WPF player
  `--ab-demo=` (A/B swap demo) and `scripts/smoke-051A.ps1` (6 tests). **PR #72 merged 2026-11-06, CI green.**
  Sandbox after the merge: Linux 212/212 with real FFmpeg, BASS and FatzAudio (Full mode with sound,
  load before attach, both engines). Waiting only for the owner's Windows run of the script.

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
  - Sent 2026-11-05: requests 13 (scan from fed audio, source identity on packet/push streams) and 14
    (MP1/MP2, old-MP3 coverage, AVI/MPEG-PS/TS by path, DTS core, WMA Standard everywhere) and 15
    (every other audio codec FatzAudio can license: TrueHD, DTS-HD lossless, APE, WavPack, Musepack, TTA,
    DSD, RealAudio, more containers; FFmpeg push stays only where licensing blocks FatzAudio):
    `docs/fatzaudio/2026-11-05_MediaControl_Requests_13_14.md`; the 037A slice 5 plan uses them.
  - FatzAudio is adding native builds for win-arm64, linux-arm64, osx (one universal arm64+x64 dylib),
    android and iOS (xcframework) to its CI and package, after rc.4 (rc.4 does not wait); layout agreed
    (`runtimes/<rid>/native/`, `runtimes/osx/native/` for the universal library). Our side: 059A (ARM),
    058A (FFmpeg for the same targets), macOS platform package and renderer still to do (026A/026B).
  - Pending from FatzAudio: the public archive API (zip, 7z, tar family; **no rar**), the public
    fingerprint (owner decides), pairing rules, archive timings.
- **Queue (owner-agreed 2026-11-05), each a roadmap idea in `docs/roadmap/future/`:**
  1. `051A` first-frame-ready signal + A/B swap pattern (TriviaBomb) - **slice 1 done, pending smoke**.
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
     its position) to the audio engine, renderer, device and other settings. **Slice 1 (owner
     2026-11-05): renderer fallback in place**: the swap in place, then the automatic switch after an
     unrecoverable device loss (media kept at its position; moves out of the test players), then an
     `Auto` renderer choice in the package.
  6b. `057A` UI-friendly async API (audit 2026-11-05): `BeginScrubAsync`/`EndScrubAsync`, `DisposeAsync`,
     the players use Core's events instead of polling. Audit items also in 055A slice 1 (surface and
     decoding paired by the package), 051A (the 500 ms startup delay) and ENG-054 to ENG-057.
  7. After rc.4: rc.4 pin move + start wait/cache size pass-through; `050A` tags and file info;
     `056A` archives through FatzAudio's reader, SharpCompress and rar dropped.
- Still active from before: 026A, 026B, 027A, 033A, 041A, 042A, 043A, 044A (see the manifest).
- The October code audit (ENG-039, round 2) is done: five batches implemented, each pending the
  owner's smoke (`updates/ENG-039/`). The old "next: code audit" item is closed.
- **First owner run of the backlog script: 2026-11-05** (`updates/Smoke_2026-11-05/Results.md`): most
  failures were the script's (fixed); product findings ENG-044 to ENG-048 (ENG-048, the loudness cache
  folder, fixed). Waiting for: the run's `logs\` folder, test 11's results, and a rerun of 1, 2, 4, 7, 9, 10.
- **Owner smoke backlog: `scripts/smoke-backlog.ps1`** (2026-11-05) runs it, 12 tests, mostly
  automatic (it makes its own test media with FFmpeg, reads the players' logs for the expected lines,
  measures CPU, serves a local web file; you answer only what needs eyes or ears): formats on both
  engines (ENG-043, 033A_2), normalization (037A), network (039A_3), HDR (042A), modes (043A), Avalonia
  paths and second screen (027A_2/5), Vulkan tests on the real GPU (026A), text on Software/Vulkan
  (ENG-040, ENG-039 4c), missing natives (ENG-039), player set (ENG-039 4a), the audit script's GPU
  reset/resize/memory tests (ENG-039 1, 2, 4b, 4c, 5) and the remaining guided checks. The test
  players now trace their status and stats lines (`[Status]`, `[Stats]`) to the debug log for it.
  026B steps 1-5 ran in the sandbox on 2026-11-05 (BASS for Linux + PulseAudio null sinks): pass, except
  that no `AudioDeviceChanged` is raised on Linux (the sound server reroutes; noted in ENG-040);
  repeatable as `LinuxBassNativeTests` (`FATZBOMB_LINUX_BASS_DIR`; `www.un4seen.com` is allowed now). 037A slice 0's console smoke is closed (superseded by slice 2's).
  Linux native tests added 2026-11-05 (opt-in; results in `updates/033A_2`, `043A_1`, `043A_2`, `039A_3`):
  `LinuxFormatMatrixNativeTests` (24 formats x BASS and FatzAudio, 48/48 play; long-file temp WAV removed;
  FLAC without BASSFLAC through FFmpeg), `LinuxPlayerModesNativeTests` (4/4), `LinuxInternetStreamNativeTests`
  (https radio with titles, public HLS; `FATZBOMB_LINUX_NETWORK=1`). Windows still owns the players, the
  Windows decoders and DLLs, certificates, and everything by eye or ear.
  Stages move to `complete/` as these pass.

## Decisions this session (owner)

- FatzAudio does all audio work; we pass settings through and add no waits of our own.
- Loudness is the only normalization on FatzAudio; the target is a code setting (no player flag).
- Tags and full file information are exposed (audio from FatzAudio, video from FFmpeg, hashes ours).
- One archive reader, FatzAudio's; rar is dropped entirely, SharpCompress goes with it.
- The control ships FFmpeg (LGPL only, never GPL or nonfree) in runtime packages: stage 058A, after the
  rc.4 work (2026-11-06). Codec patents: the owner checks with a lawyer before a paid release.
- The control is sold under a proprietary licence; FFmpeg ships as a trimmed, decode-only, pinned LGPL
  build (058A). A background video loops on its own by default; `BackgroundFollowsMedia` restores the old
  transport-following behaviour; `PauseBackground`/`ResumeBackground` pause it explicitly (ENG-051).
- BASS-only audio issues are not fixed (BASS is going away); audio bugs are checked on FatzAudio.
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

- Last results (2026-11-06, after PR #72): Portable 1786, Avalonia 52, Linux 212 (all native suites, with
  `FATZBOMB_LINUX_BASS_DIR=/opt/fatzbomb-native/bass` and PulseAudio: `pulseaudio --start --exit-idle-time=-1`,
  `pactl load-module module-null-sink sink_name=null`), Vulkan 103 (`apt-get install mesa-vulkan-drivers`);
  whole solution 0 warnings.
- FatzAudio for a sandbox build without the token (2026-11-06): `add_repo` FatzBomb75/FatzAudio (public: the
  git proxy serves it), `git fetch --depth=1000 origin`, check out 59741a5 (rc.1),
  `dotnet pack bindings/dotnet/src/FatzAudio -c Release -p:VersionSuffix=rc.1 -o <feed>`, restore with a
  `nuget.config` outside the repo mapping `FatzAudio` to that feed. For the native FatzAudio tests, first
  `cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release && ninja -C build fatzaudio` (about 5 min), copy
  `build/bin/libfatzaudio.so.1` to `bindings/dotnet/src/FatzAudio/runtimes/linux-x64/native/libfatzaudio.so`
  before packing, delete `~/.nuget/packages/fatzaudio`, and `dotnet restore <project> --force` (a plain
  restore keeps the old package contents).
- Tests set `CacheDirectory = ""` unless they use their own folder: no test writes to the user's cache.

Windows (owner): repo at `C:\development\nuget\FatzbombMedia`, media in `C:\media`, RTX 5090, three
monitors; smoke steps in each stage's `StageUpdate.md`; full PowerShell command sets; dark UI. **Owner preference (2026-11-06):** full commands for every smoke step, and a `.ps1` script (on `scripts/smoke-common.ps1`, like `smoke-backlog.ps1` and `smoke-051A.ps1`) whenever there are many steps or they can be automated; the owner is still learning git and PowerShell, so scripts do the git steps too.

## Next steps

1. Owner: the normalization smoke (`updates/037A_Normalization`, with rc.4). The 054A desktop
   measurement is done (2026-11-06: Full costs about 0.02 of a core more than Background, noise); the laptop
   question is answered by the old trivia app comparison (ours lower on CPU and memory at higher
   resolution; GPU memory flat at 199 MB, while the old MediaElement app leaks about 140 MB/s; 054A note). No HDR display (owner): 042A slice 2's HDR-display check stays untested. 047A's before numbers come after 051A
   adds the UI-thread counter.
2. Owner: the 051A smoke (`scripts/smoke-051A.ps1`; `updates/051A_1`) on the desktop (test 5 is the A/B demo;
   test 3 with `-SimulateLaptop` adds a 4-core stand-in). **No laptop run** (owner 2026-11-06: not until
   software uses it); the owner's comparison with the old trivia app on laptops (ours clearly better) is the
   laptop answer for now. Then start 047A slice 1 with the desktop numbers as the baseline.
3. When rc.4 ships: pin move, start wait and cache size into `NormalizationOptions`, log the
   provisional start, rerun the timings.
   Cache (FatzAudio's 2026-11-05 answer, on its main after rc.3): unset `CacheDirectory` then means
   FatzAudio's shared per-user default (one cache for every app; its folder name follows the library
   rename, which happens before release, so no user cache ever needs moving), an empty string maps to `AudioEngineOptions.NoCache = true`, a path stays a
   private folder; log `engine.GetCacheInfo()` after creating the engine (folder at Info, a warning when
   `Status` is not Ok); remove our own `EnsureCacheDirectory` (ENG-048) and per-user default folder.

## Open questions for the owner


- Provisional boost cap default: 6 dB (our suggestion) or FatzAudio's 12 (none)?
- FatzAudio's public fingerprint: build it (half a day on their side)?

## Next-session prompt

> Read HANDOFF.md. 051A slice 1 is merged (PR #72) and waits only for the owner's Windows run of
> `scripts/smoke-051A.ps1` (desktop all tests, laptop `-Steps 3`). Read the results the owner pastes, fix
> what they find, and when the smoke passes move 051A to `complete/`. Then start queue item 2, stage 047A
> slice 1 (WPF shared GPU surface), with the smoke's composited UI-thread numbers as the before baseline.
