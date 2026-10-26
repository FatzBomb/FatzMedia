# Stage Update

Stage: 039A.1 - Network video: HTTP/HTTPS progressive and HLS through FFmpeg
Date: 2026-10-24

## Status: IMPLEMENTED - owner smoke passed 2026-10-25

One slice. HTTP progressive and HLS go through the same FFmpeg open path, so splitting them
would have added nothing; both are delivered and natively tested here.

Delivered:

- **FFmpeg network open path** (`Core/Media`):
  - `MediaDemuxer.Open(url, FfmpegNetworkOptions)`: `avformat_network_init` once per process
    (`FfmpegNetwork`); an interrupt callback (`FfmpegInterrupt`) on the format context; the
    connect bounded by `NetworkRetryPolicy.ConnectTimeout`, the stream-info probe by twice that,
    every read by `rw_timeout` (the same timeout).
  - `Abort()` is lock-free and makes every blocking call return at once. `MediaDemuxer.Open(path)`
    routes http/https paths to the network open, so every FFmpeg reader can take a URL.
  - A read error other than end of file is reported once (`Network read failed: ...; the stream
    ends here.`) and returned as end of file.
  - FFmpeg warnings logged on the reading thread (HTTP reconnects, short bodies, skipped HLS
    segments) become `Network` diagnostics (`FfmpegLogCapture.ObserveNetwork`, a thread-scoped
    observer on the existing process-wide log callback). A reconnect reads
    `Network retry: [http @ ...] Will reconnect at <byte> in <n> second(s), error=...`.
  - `GetNetworkProbe()`: playlist (HLS/DASH demuxer) or progressive, length, byte seeking,
    duration.
- **Video pipeline**: `VideoPipeline` implements internal `INetworkVideoEngine.OpenNetwork` (one
  attempt): the picture on the shared demuxer, the sound on `VideoAudioEngine.OpenNetwork` (own
  connection, as for local video; no normalization pre-analysis). No `File.Exists` gate, archive
  temp file or embedded-subtitle scan. `Close` aborts both readers before joining the threads.
  `LoopPlayback` reads false for a network source that cannot loop.
- **Buffering**: `NetworkBufferingMonitor` (250 ms) feeds `NetworkStallBufferingTracker`: a read
  blocked for 0.5 s with the decoded queue empty enters buffering; a free reader with the queue
  at 25 % of its 4-frame target, or the end of the stream, leaves it. Changes go
  `VideoPipeline` -> `PlaybackCoordinator` -> `PipelineMediaEngine` (`INetworkMediaControl`)
  -> `MediaControlPlayer.BufferingChanged`, through the control queue like 014A's.
- **Retry and cancellation**: `PipelineMediaEngine.OpenNetworkVideo` runs the attempts with
  `NetworkOpenRetry` under the configured policy, with 014A's diagnostics. `MediaControlPlayer.Close`
  and `Dispose` call `CancelPendingNetworkOpen()` before queueing, so the attempt and any retry
  delay end at once. Mid-stream, FFmpeg's HTTP reconnect is on only when the host opted into
  retries (`reconnect_max_retries` = `MaxRetries`, `reconnect_delay_max` = `MaxDelay`,
  `seg_max_retry` for HLS segments).
- **Capabilities** (`NetworkVideoPolicy.Resolve`): progressive by the 014A rule; HLS VOD (FFmpeg
  reports a duration) = seekable NetworkFile, live = NetworkLive. Duration is known only when
  FFmpeg reports one. No stream metadata for video. A network video loops only when seekable and
  not live (`NetworkVideoPolicy.CanLoop`).
- **Classification** (`NetworkVideoPolicy.IsVideo`): the hint, else the URI extension through
  `FileTypes.Video`, plus `.m3u8`.
- **Modes** (`MediaPlayerModePolicy.RouteNetwork`, `NetworkNeedsAudioEngine`): Full plays
  network video (picture only without BASS); Background accepts network video (picture only,
  forced loop where seekable) and still rejects network audio; Audio plays the soundtrack
  (`SoundtrackAudioEngine.OpenNetwork` through `AudioPipeline.OpenNetworkSoundtrack`; never loops,
  stall-based buffering) and needs BASS.
- **Hosts**: the WPF validation host needed no change (`--url=`, URL box; `.mp4`/`.m3u8` classify
  as video). The Avalonia test player has a URL box with **URL...**, accepts a URL as the startup
  argument or `--url=`, and logs the capability row and buffering changes.

## Decisions made without the owner

- **One slice** for progressive and HLS (same path; both pass the native tests).
- **Second connection for sound**, as local video does (its own cursor avoids the AV1 + E-AC-3
  startup offset). Cost: the file is downloaded twice, and a live playlist can start the two
  connections a segment apart (ENG-034). Not changed now because the shared-cursor audio path
  cannot be verified without BASS here.
- **Close and Dispose cancel a network video open in progress**; other commands still queue
  behind it, as in 014A. Network audio (BASS) keeps 014A's non-interruptible open.
- **Mid-stream recovery is FFmpeg's own HTTP reconnect**, configured from the policy. Its delay
  schedule is FFmpeg's (0, 1, 3, 7 s ...), capped by `MaxDelay` and `MaxRetries`, not
  `InitialDelay`/`BackoffMultiplier`. With the default policy (no retries) nothing reconnects.
  Streamed (no-range) sources are never reconnected: FFmpeg would restart them from byte 0.
- **A failed read ends the stream** (end of file after one Error diagnostic) instead of retrying
  a dead connection forever.
- **Content that connects but has no playable stream is not retried** (`UnsupportedFormat`; in
  Audio mode the 043A "no playable audio stream" message).
- **Buffering from read stalls**: FFmpeg exposes no download buffer, so the reader's blocked time
  is the signal; a full queue during a slow read does not count.
- **HTTPS certificate verification is FFmpeg's default** (off for OpenSSL/GnuTLS builds); a
  policy option is ENG-034. Follow-up 2026-10-25 (ENG-034): verification is on by default and
  set explicitly, including for HLS/DASH segments; hosts opt out or name a CA file through
  `MediaPlayerConfiguration.NetworkTls` (`NetworkTlsPolicy`). Backend table and decisions in
  `docs/NATIVE_DEPENDENCIES.md` (HTTPS Certificate Verification). Linux: 6 new opt-in native
  cases (`LinuxNetworkVideoNativeTests.Https_*`, `Http_IsUnaffectedByTheTlsPolicy`) against an
  in-process self-signed https server.

- **One HLS/DASH variant (follow-up 2026-10-25, ENG-034).** A network source with several video
  renditions plays one, chosen by `NetworkVariantPolicy`: the highest advertised bandwidth at or
  below 1080p (the engine does not know the display size when it opens); when every variant is
  taller, the smallest. The audio cursor takes the audio of that variant's program. All other
  streams are discarded, so FFmpeg downloads one variant's segments. No adaptive switching.
- **Live diagnostics (follow-up 2026-10-25, ENG-024).** `MediaDiagnostic` is delivered on a
  per-player worker, so network retries and the connect are visible while the load runs.

## Multi-variant HLS (follow-up 2026-10-25)

Local master playlist, 30 s, three variants (480p 0.95 Mb/s, 720p 2.27 Mb/s, 1080p 4.47 Mb/s, each
a TS with its own audio, 2 s segments; `scripts/setup-linux-native.sh` makes it), served by
`LocalHttpMediaServer` with a 150 ms delay before each segment (a remote round trip).
`LinuxNetworkVideoNativeTests.HlsMultiVariant_FetchesTheSegmentsOfOneVariantOnly`, three runs each:

| | Segment requests (open / playback to 4 s / seeks to 20 s and back to 6 s) | Seek to 20 s | Back to 6 s |
|---|---|---|---|
| Before (every variant read) | v0/v1/v2: 2/2/2, 2/2/2, 8/8/8 | 921-929 ms | 916-920 ms |
| After (1080p variant only) | 2/2/2, v2: 2, v2: 8 | 310-325 ms | 307-314 ms |

The audio cursor (`HlsMultiVariant_AudioCursor_ReadsTheChosenVariantOnly`, demuxer level since it
needs BASS to play) picks the 1080p variant's audio and fetches only that variant's segments.
The open's probe reads the first two segments of every variant either way.

## Measurements (Linux sandbox, localhost, FFmpeg 9 shared libraries, no BASS, software decode, 1080p30 H.264, 10 s)

FFmpeg alone first (the idea's "before changing the pipeline" step; FFmpeg 6.1 CLI, includes
process start, `-frames:v 1 -f null`): progressive MP4 open + first frame 105-120 ms, seek to 6 s +
first frame 104-106 ms; HLS VOD open + first frame 110-115 ms, seek to 6 s + first frame 202-237 ms.

Engine (`LinuxNetworkVideoNativeTests`, five runs, typical / range):

| | Load (connect, probe, first frame decoded) | Load start to first presented frame after Play | Seek latency (Seek call to `SeekCompleted`) |
|---|---|---|---|
| Progressive MP4 (range server) | 35 ms (30-110) | 63 ms (58-151) | forward to 6 s: 21 ms (17-34); back to 1 s (new range request): 65 ms (55-155) |
| HLS VOD (5 x 2 s segments) | 140 ms (129-155) | 155 ms (141-169) | to 7 s: 70 ms (69-76) |

Stall behaviour:
- Server cuts the playback response at 45 % (clean close) and answers the reconnect after 1.5 s,
  with `MaxRetries = 3`: `Network retry: ... Will reconnect at 2080449 in 0 second(s)`, then
  `BufferingChanged` true, then false 1.1-1.4 s later, and playback continues.
- Same cut with the default policy (no retry): `Network read failed: Input/output error`, then
  `MediaEnded` at about 3 s.
- `Close` during a connect to a server that accepts and never answers: 1-98 ms; the load
  returns false with "Network open cancelled".
- `Close` of a live HLS playlist waiting for new segments: 3-4 ms.

Also observed with `python3 -m http.server` (no `Accept-Ranges`): the sample MP4 keeps its index
at the end, so FFmpeg cannot seek back to the media data: it connects (NetworkFile, not seekable)
and the first read ends the stream with `Network read failed`. The same clip remuxed with
`-movflags +faststart` plays from that server (not seekable). HLS plays from it normally.

## Verification

- Linux: `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`:
  0 warnings, 0 errors.
- `FatzBomb.MediaControl.Portable.Tests`: 1182 passed (53 new cases: `NetworkVideoPolicyTests`,
  `NetworkVideoPlaybackTests`, two player network tests, `RouteNetwork`/`NetworkNeedsAudioEngine`
  tables; two `AcceptsSource` rows changed; the old "network video is rejected" case now covers
  "no playable video stream").
- `FatzBomb.MediaControl.Linux.Tests` with `FATZBOMB_LINUX_FFMPEG_DIR=/opt/fatzbomb-native/ffmpeg/lib`:
  64 passed, including the 8 new opt-in native cases in `LinuxNetworkVideoNativeTests` (in-process
  HTTP server with ranges, `LocalHttpMediaServer`): progressive frames and seek, HLS VOD frames
  and seek, HLS live row (not seekable, no duration, Seek refused, prompt Close), Close during
  connect, mid-stream drop with retry and buffering events, drop without retry ends playback,
  Background loop over HTTP, Audio mode without BASS refused. Four clean runs after the last
  change (about 15 s each).
- `FatzBomb.MediaControl.Avalonia.Tests`: 23 passed.
- `dotnet format FatzBomb.MediaControl.sln --verify-no-changes` (with `EnableWindowsTargeting=true`):
  clean. `check-core-boundary`, `check-adapter-boundary`, `check-wpf-boundary` and
  `check-presentation-parity` (pwsh): passed; no project reference changed.
- Avalonia test player under `xvfb-run` (Linux, software renderer, no BASS), screenshots in
  `/opt/fatzbomb-native/screenshots/`:
  - `039a-hls-vod.png` - HLS VOD from `python3 -m http.server`: NetworkFile, seekable, 30 fps.
  - `039a-progressive-mp4.png` - progressive MP4 from a range-capable server (`--url=`).
  - `039a-background-hls-loop.png` - Background mode, HLS VOD, after one loop.
  - `039a-no-range-faststart-mp4.png` - faststart MP4 from `python3 -m http.server` (not seekable).
  - `039a-no-range-moov-at-end-mp4.png` - the sample MP4 from `python3 -m http.server`: the
    clear failure described above.
- Not verified here: sound, including the Audio-mode network soundtrack (no BASS in the
  sandbox), HTTPS against a real certificate, Windows (WPF, Direct3D11, D3D11VA hardware
  decode of network video), real internet links.

## Manual smoke (Windows, native DLLs)

Serve a folder with a range-capable server, for example `npx http-server <folder> -p 8080`
(sends `Accept-Ranges`), with an H.264 MP4, an MKV, and an HLS folder (`ffmpeg -i clip.mp4 -c copy
-hls_time 2 -hls_playlist_type vod hls/index.m3u8`). Run once with BASS present (sound) and once
with `bass.dll`/`bass_fx.dll` renamed (ENG-033).

1. WPF player, BASS present: `dotnet run --project FatzBomb.MediaControl.Player -- --url=http://localhost:8080/clip.mp4`.
   Expect `Network stream connected: NetworkFile, seekable=True, duration=...`, picture and
   sound in sync, the seek bar works (forward and back), natural end raises `MediaEnded`.
2. Same with the MKV and with `http://localhost:8080/hls/index.m3u8`: both play with sound; HLS
   VOD seeks.
3. Turn Loop on and play the MP4 to its end: it loops seamlessly (picture and sound).
4. A live HLS stream (an `ffmpeg -re ... -hls_list_size 5 -hls_flags delete_segments` output,
   or a public test stream): `NetworkLive`, duration 0, the seek bar refuses seeks, Stop/Close are
   instant.
5. `--net-retries=3`, then stop the server for 3 s during MP4 playback and start it again:
   `Network retry: ... Will reconnect ...` in the log, `Buffering=True` then `Buffering=False`,
   playback continues. Without `--net-retries`: `Network read failed` and the stream ends.
6. Point the URL box at an address that accepts but never answers (for example a `nc -l 9999`
   listener): press Close while it connects; it returns at once and the load reports
   "Network open cancelled".
7. HTTPS certificates (ENG-034), with the shipped FFmpeg DLLs:
   a. An https URL with a public certificate (internet) plays; the log has no TLS warning
      (with an OpenSSL-only build it would show "FFmpeg's TLS backend is OpenSSL ..." and fail).
   b. A local self-signed https server, for example
      `npx http-server <folder> -p 8443 -S -C cert.pem -K key.pem` with a certificate for
      `localhost` (`openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 7 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"`):
      `--url=https://localhost:8443/clip.mp4` fails with
      `certificate verification failed for localhost: ... Creating security context failed (0x80090325) ...`
      and one attempt only, even with `--net-retries=3`.
   c. Same with `--net-allow-invalid-certs`: plays; the log shows "Certificate verification is off".
   d. Import `cert.pem` into the current user's Trusted Root store (`certutil -user -addstore Root cert.pem`),
      run without flags: plays (MP4 and the HLS folder). Remove it afterwards
      (`certutil -user -delstore Root localhost`). Also try `--net-ca-file=cert.pem` without
      the import: expect the "Schannel ignores NetworkTlsPolicy.CertificateAuthorityFile" warning
      and the rejection from step b. If Schannel instead reports a revocation status
      (0x80092012), record it in ENG-034.
   e. BASS network audio against the same self-signed server (`--url=https://localhost:8443/song.mp3`):
      record whether BASS plays it (no verification) or refuses it; BASS has no setting for it.
8. BASS renamed: steps 1-2 play the picture without sound; no error.
9. Avalonia player: `--mode=background --url=http://localhost:8080/clip.mp4` plays and loops;
   `--mode=audio` with the same URL plays the soundtrack only (BASS present), or fails with
   "Audio engine unavailable" (BASS renamed).
10. Local files: video playback, seek, loop, archives, and network audio (an internet radio
    station, 014A step 1) behave as before.
11. Multi-variant HLS: `--url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`. Expect the
    1080p picture, seeks that settle within about a second on a normal connection, and no
    periodic stutter. With a network monitor (Resource Monitor or the browser-free `netstat`),
    the download rate is about one variant's bitrate, not the sum of all of them.
12. With `--net-retries=3`, load an address that refuses connections (for example
    `http://localhost:9/clip.mp4`): "Network retry 1/3 in 1.0s", "2/3 in 2.0s" and "3/3 in 4.0s"
    appear in the log about 1, 2 and 4 s apart while the load runs, not all at once after it.
