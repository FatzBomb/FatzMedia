# Owner smoke run, 2026-11-05 (Windows, `scripts/smoke-backlog.ps1`)

Machine: RTX 5090 (driver 32.0.16.1088), 3 screens, Windows 10.0.26220, 20 logical cores. Branch
`claude/modest-clarke-5i5piu` at be9629d. 83 passed, 36 failed, 9 notes, before triage. Test 11 (the
`smoke-audit.ps1` tests) wrote its own results, not yet received.

## Triage

**Script faults (false failures), fixed in the script and the test players the same day:**
- Formats, "playing False" (most FatzAudio rows, MP3 and the video files on BASS): the script read only
  the last traced stats line, written every fifth second, often from before playback started. The
  owner heard every file play. The players now also trace a stats line on every state change; the
  script checks any stats line after the load.
- HDR (all HLG/HDR10 checks): the HLG and HDR10 picks were swapped (`hlg` = `hdr10.mp4`, `hdr10` =
  `hlg.mp4`), so each run looked for the other transfer's log line. Screenshots show tone mapping
  working (`--no-hdr-tonemap` visibly different) and the owner judged the pictures right. The checks are
  now transfer-agnostic and report a swapped pick.
- Avalonia Vulkan "falls back to the frame sink": stale expectation from 027A slice 2; since 045A Vulkan
  frames reach Avalonia as a shared GPU image on Windows (the run showed exactly that).
- Vulkan tests: the script's parser missed the summary and took a test name (`...OnLavapipe`) for the
  device. Rewritten; rerun needed.
- Missing natives (two players without BASS; FLAC without BASSFLAC): `--native` adds a folder, but the
  player still searches its own `native\` folder, so BASS was found. The script now runs copies of the
  player folder with the DLLs removed.
- Player set: "dispose lines 0" counted a log line the players never write; dropped.

**Product findings:**
- **Loudness cache never written (fixed):** FatzAudio creates only the last folder of its cache path;
  the default `%LOCALAPPDATA%\FatzBomb.MediaControl\AudioCache` needs two. The engine now creates the
  folder itself (warning and no cache if it cannot); the Linux test now uses a two-level folder (it failed
  without the fix, passes with it). ENG-048.
- **F11 full screen** picks player 1 regardless of focus and sometimes does nothing (WPF, Avalonia;
  double-click works). ENG-044.
- **Embedded subtitles:** the choice keeps returning to the default embedded track. ENG-045.
- **WPF native window resize lags** behind the drag (catches up; no flashes). ENG-046.
- **Clicks and pops on loop wraps and seeks** (engine not confirmed; BASS is the default). ENG-047.
- **WMA on BASS does not load** (no BASS WMA plug-in): known and accepted (ENG-043; FatzAudio plays it).

**Judged from the logs (received the same day): all pass, the script's expectations were wrong.**
- `--net-allow-invalid-certs`: "Certificate verification is off for this https source", the request got
  through the self-signed certificate and badssl.com answered 404 (it has no `stream.mp3`). Correct.
- Faststart MP4 from the script's web server: "Network stream connected: NetworkFile, seekable=False,
  duration=12.0s ... Streaming"; the `Stopped` was the stats-timing fault above.
- Avalonia WARP: the renderer chooses software decoding up front ("software decoding (the renderer runs
  on the CPU)"), so no switch and no warning. Correct.

**Vulkan tests on the RTX 5090: 99 of 102** (CI never reaches these: it has no GPU, so they return early).
- `Backend_DeclinesAConsumerThatImportsNoDescriptorKind` and
  `Factory_ProbeMatchesSupportAndAsksForFrameSinkAndSoftwareDecoding`: stale since 045A (Vulkan serves
  Direct3D11 shared textures on Windows; software decoding only without Vulkan Video). Tests fixed.
- `CdgOnly_MatchesSoftwareExactly(333x250)`: 99.1% of pixels exact, some edge pixels take the neighbouring
  CD+G pixel on NVIDIA (lavapipe is exact). Real: ENG-049.

**Passed (owner or automatic):** normalization levels, Normalize off/on, no clipping, BASS peak rule;
HDR pictures; Avalonia shared GPU image and frame sink, 4 players, second screen (all steps);
subtitles and lyrics on Software and Vulkan, Software renderer resize; WinForms player set; hardware
decoding kept on Direct3D11; loop and seek sync; background video restart; `song.PNG` sidecar (026B
step 6); karaoke surface with an empty source; WinForms frame sink; temp-file conversion and its
cleanup; https and http radio, HLS in Background mode, bad certificates refused, non-faststart refused;
all player-mode checks (startup rules and switching, MP3+G after Audio and back).

**Owner notes, explained:** the GPU reset turned the whole monitor black for a moment and moved Windows'
default sound device (audio over the GPU's HDMI): both are Windows' own reaction to a forced GPU reset.
The HLS clip (Big Buck Bunny) had no sound because Background mode never plays audio, by design.
Different volumes between the generated test tones: to check on the rerun with the cache working.

## Test 11: `smoke-audit.ps1` tests 2, 3, 4, 6, 8, 9, 10 (received the same day)

- **2 refresh-rate query: pass.** The probe output is unchanged (the shipped struct fails, packed 292 bytes
  works, which the player now uses); the cadence lines name the real 59.973 Hz.
- **3 GPU reset: 3a WPF Direct3D11, 3b Avalonia Direct3D11 shared texture, 3c CD+G: pass** ("GPU device
  recovered after 1 attempt(s)", the media reopened, picture and sound back). **3d Avalonia Vulkan: fail**,
  black, `vkCreateInstance ... ErrorIncompatibleDriver` on every attempt: ENG-050. The whole-screen black
  moment and Windows moving its default audio device (HDMI audio on the GPU) are Windows' own reaction to
  the forced reset.
- **4 resize: pass** (no flashes; one export line). The owner saw the WPF frame sink lag: ENG-046.
- **6 loop point: fail**, the background video hitches at each loop, with and without trim: ENG-051.
- **8 Avalonia close during a rebuild, second screen: pass** (clean exit in 15.5 s, nothing left running; no
  reopen after a Renderer or Players change, in Avalonia and WPF).
- **9 lyric memory: pass**, 0.33 MB/s allocated with a KAR (target under 10; the audit found 234), 0.19 MB/s
  with an MP3.
- **10 large MKV with subtitles:** picture at once (pass), but the scan read the 3 GB file twice: ENG-052.

## Rerun

`powershell -ExecutionPolicy Bypass -File scripts\smoke-backlog.ps1 -Steps "1,2,4,7,9,10"` after pulling
the fixes, with the HLG and HDR10 files picked the right way round.
