# Stage Update

Stage: 039A.3 - Network follow-ups: https audio through FFmpeg, ENG-034 triage
Date: 2026-10-26

## Status: IMPLEMENTED - pending Windows smoke

039A is complete; this is a follow-up note under it (ENG-034). No public API was added or
changed. One commit (the https audio route and the ENG-034 fixes share the read-ahead and the
engine open path).

## 1. https network audio through FFmpeg (owner approved)

Owner smoke showed that BASS connects to badssl.com's expired, self-signed and wrong-host sites
(the load only failed later, with FileFormat): BASS does not verify https certificates and has no
setting for it. https audio now opens through FFmpeg's network path, so `NetworkTlsPolicy`
applies exactly as for network video.

- **Routing** (`Core/Network/NetworkAudioPolicy.OpensThroughFfmpeg`, pure): network audio opens
  through FFmpeg when the URL is https, or an HLS playlist (`.m3u8`, which BASS cannot play
  without its add-on). Plain http stays on BASS: it has no certificate to check, it is the proven
  014A path, and 037A replaces BASS anyway. Gap, documented: an http URL that redirects to https
  is still followed by BASS without verification.
- **Engine path**: the existing Audio-mode soundtrack engine (`SoundtrackAudioEngine` over
  `VideoAudioEngine`: FFmpeg demux and decode, with the 039A read-ahead, into a BASS push stream
  with tempo, pitch, EQ, fades and volume). No new decoder. `PlaybackCoordinator` now attaches the
  soundtrack pipeline in Full mode too (it was Audio mode only); `PipelineMediaEngine` routes
  https/HLS audio to it through the same retried, cancellable open as network video
  (`OpenThroughFfmpeg`, formerly `OpenNetworkVideo`, with a route: video, soundtrack, audio).
  BASS stays the output behind `IAudioEngine`; no BASS-only API was added.
- **Capability row** (`NetworkAudioPolicy.Resolve`): the network-video rule (length and ranges
  for a progressive source; a playlist is live unless FFmpeg reports a duration) plus 014A's
  metadata rule: a progressive live stream, or one with ICY headers, has `HasStreamMetadata`.
  Live (no length) reports zero duration and cannot seek; a file with ranges seeks. Network
  audio never loops (as 014A).
- **Stream titles**: FFmpeg's HTTP layer already strips ICY blocks (`icy=1` is its default) and
  keeps the last one in `icy_metadata_packet`; that option is read on the reading thread only
  (FFmpeg's HTTP context is not thread-safe) after every packet, and Ogg comment updates
  (`AVSTREAM_EVENT_FLAG_METADATA_UPDATED`, also the tags read with the header) likewise. Each
  change is recorded with the time of the packet it came with; `SoundtrackAudioEngine`'s
  buffering monitor (250 ms) takes the latest change the played position has reached and raises
  it; `PlaybackCoordinator` -> `PipelineMediaEngine` (`INetworkMediaControl.NetworkStreamMetadataChanged`)
  -> `MediaControlPlayer.StreamMetadataChanged` through the control queue (the same handler as
  BASS's titles). So a title is announced when it plays, not when the read-ahead downloaded it
  (up to 20 s earlier); BASS announces on download. Precision: FFmpeg parses an ICY block when
  its 32 KB I/O buffer reads past it, so a title can be up to that buffer early (2 s at
  128 kbit/s); measured 0.1 s early on localhost.
- **Buffering**: the soundtrack's read-ahead monitor (starved 0.5 s starts, 2 s buffered ends).
  `BufferingChanged` as before; the player's `State` does not go to `Buffering` as with BASS's
  `Stalled` (only the event).
- **Retries and failures**: `NetworkRetryPolicy` through `NetworkOpenRetry`, cancellable by
  Close/Dispose/Reset; a rejected certificate fails at once with "certificate verification
  failed for <host>: ..." and is not retried; connected-but-no-audio says "No playable audio
  stream in <name>."
- **Prebuffer on the caller's thread is bounded**: the soundtrack's Open and Play prebuffered on
  the control queue and could wait for a stalled download (039A slice 2 note); a network
  source now waits at most 0.5 s there, the pump fills the rest.
- **Without BASS (ENG-033)**: there is no soundtrack engine; https audio is refused before any
  connection with "Audio engine unavailable: BASS libraries not found" (`NativeLibraryMissing`),
  exactly as http audio. Background mode still rejects network audio.
- **Audio-only HLS in Full mode** (ENG-034 item): an `.m3u8` that connects but has no playable
  video now plays as audio through the soundtrack engine (one more connection; a diagnostic says
  so). Audio mode already played it.

## 2. ENG-034 triage

Fixed (pending Windows smoke):

- **Clock held while picture-only network video buffers** (`Pipeline/NetworkClockHold`). The
  presentation loop holds the playback clock when no decoded frame is left and the picture's
  packet queue has been empty for 250 ms, and releases it once a frame is ready and the
  read-ahead reached its resume threshold (or the source ended). The video pipeline sees the
  clock through `HoldableClock`: every transport change (start, pause, stop, seek, loop restart,
  end) goes through it and ends a hold, so a hold cannot outlive the playback it was taken for;
  only the presentation loop (under the presentation lock) holds and releases. With sound
  nothing changes (BASS waits by itself). Not in host-render-event-driven presentation (no
  presentation loop there). Diagnostics: "Waiting for the network: the picture holds at m:ss",
  "The picture goes on after waiting N s".
- **Found while testing the hold**: after a reconnect, the real end of a progressive file read
  as a new drop. FFmpeg keeps the dropped connection's I/O error on the `AVIOContext` and reports
  the end of the demuxer as that error (`demux.c`: "a demuxer might have returned EOF because of
  an IO error"), so the reader reconnected for the whole window (30 s) at the end. Before, the
  picture-only clock ran out at the duration and hid it; with sound the soundtrack would have
  waited. A successful network seek now clears `pb->error`.
- **Reconnect window overrun by a hung connect**: each reconnect seek, and every read while a
  reconnect is in progress, is bounded by what is left of `ReconnectFor` (interrupt deadline). A
  read that times out reports "timed out".
- **`Buffering=False` when playback ends during buffering**: `NetworkBufferingSample` carries
  `PlaybackEnded`; a spell in progress ends with it (paused samples still leave the state as it
  is).
- **Moov-at-end MP4 on a server without byte ranges**: a non-seekable `mov` source must yield its
  first packet during the open (kept for the first read). If it cannot (and the read did not
  time out), the open fails with "this MP4 keeps its index (moov) after the media data and the
  server does not support byte ranges, so it cannot be streamed; serve it from a server with
  range support, or remux it with -movflags +faststart", not retried
  (`NetworkVideoPolicy.IsPermanentOpenError`). A faststart MP4 from the same server plays.
- **Read-ahead: a stream shorter than the source** (audio 30 s shorter than the video): a stream
  registered with its own end (`AVStream.duration`) gets its end marker once the reader is 2 s
  past it, instead of counting as starved (and keeping the reader past the caps, and after a
  seek into that tail, buffering forever).
- **Read-ahead: one data-arrived event per stream**, so one consumer's reset cannot swallow the
  other's wake-up.
- **Read-ahead: an audio engine that fails after re-enabling its stream** now has the stream
  discarded again; the reader applies it between its reads (it owns the format context).
- **Reset's undisposed `_loadLifetime`**: disposed in `ResetCore` after the old graph is torn
  down (the operation that used it has returned). The give-up path still leaves it (the open in
  progress may wait on it).
- **Audio-only HLS** and the **Audio-mode Play prebuffer** (see part 1).

Left as they are (one-line reasons in ENG-034): live progressive reconnect (FFmpeg's streamed
reconnect restarts at byte 0, needs per-source options after the open; BASS does not reconnect
either), servers without ranges are never reconnected (a reconnect would have to reopen and
re-download from 0), HLS outage gaps from skipped segments (inside FFmpeg's HLS demuxer), the
short forward seek after the audio took the target (needs BASS measurements), the starvation
timer reset per packet (a stricter rule would flag a live stream at its edge), Stop's 0.5 s
first frame (by design), `IsCertificateFailure` tightening (needs Schannel's messages), and the
BASS retry-loop fold (037A).

## Decisions made without the owner

- http audio stays on BASS (above). Routing https to FFmpeg changes the decoder for https
  sources: formats only a BASS add-on plays would fail over https (none ship today).
- The soundtrack engine now exists in Full mode (one idle `VideoAudioEngine`, no BASS stream
  until an open).
- Titles are timed to playback, not to download.
- The audio-only HLS fallback is limited to `.m3u8` URLs, so a video whose codec cannot be
  decoded does not silently play as sound only.

## Measurements (Linux sandbox, localhost, FFmpeg 9 shared, no BASS)

- ICY over https, title change every 5 s of audio, served 4x real time, reader 8 s ahead: titles
  taken at 5.93 s and 10.95 s of played audio (true positions 6 s and 11 s).
- Progressive MP4, 5 s outage, 1 s read-ahead, picture only: before, the clock ran on through
  the outage (the picture jumped ahead); now buffering starts at 3.3 s and ends at 3.5 s after a
  5.3 s hold, and playback ends at wall 15.3 s (10 s of media plus the outage). HLS: hold 2.8 s,
  no skip.
- Hung reconnects (server accepts and never answers, `ReconnectFor` 2 s, connect timeout 15 s):
  the stream ends 3.2 s after the drop (before: at least 15 s).
- Moov-at-end MP4 without ranges: the open fails in about 0.1 s with the message above, 1 attempt
  with 2 retries configured.

## Verification

- Build `EnableWindowsTargeting=true dotnet build FatzBomb.MediaControl.sln -c Release`: 0 warnings,
  0 errors. `dotnet format --verify-no-changes`: clean. Boundary scripts (pwsh): all four pass.
- Portable tests: new `NetworkFollowUpPolicyTests` (routing, rows, permanent errors, clock hold,
  `HoldableClock`, tracker and monitor end of spell), `ReadAheadBufferTests` (per-stream end),
  engine routing (https and HLS audio never reach BASS's `OpenUrl`; without BASS: audio
  unavailable), player forwarding of engine titles.
- Linux native (`LinuxNetworkVideoNativeTests.Audio.cs`, `.Outage.cs`): https audio rejected by
  default and accepted with its CA file (demuxer level); without BASS, https audio is refused
  before the TLS handshake; ICY titles over https follow the played position; Ogg title and
  artist; moov-at-end without ranges fails clearly, faststart plays; an unregistered audio stream
  is discarded by the reader; outages hold the clock and play to the end; hung reconnects end
  within the window. The Linux suite was run twice.
- Not verified here (no BASS in the sandbox): playing https audio end to end, title events from a
  real station, buffering and output with sound, Windows' Schannel messages.

## Manual smoke (Windows, BASS and FFmpeg 9 in `native/`)

Avalonia: `dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --url=<URL>`
(WPF: `dotnet run --project FatzBomb.MediaControl.Player -c Release -- --url=<URL>`). Watch the
diagnostics/log panel.

1. **Rejected certificates** (each must fail at once, no retry, error "certificate verification
   failed for <host>: ..."; with `--net-retries=3` still one attempt):
   ```powershell
   dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --url=https://expired.badssl.com/stream.mp3 --net-retries=3
   dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --url=https://self-signed.badssl.com/stream.mp3
   dotnet run --project FatzBomb.MediaControl.Player.Avalonia -c Release -f net10.0-windows7.0 -- --url=https://wrong.host.badssl.com/stream.mp3
   ```
   Then the opt-out: `--url=https://self-signed.badssl.com/stream.mp3 --net-allow-invalid-certs`
   connects (an Info line says verification is off) and then fails with "No playable audio stream"
   (the page is HTML), which shows the TLS layer let it through.
2. **https radio with titles** (any of these; the sandbox's proxy could not reach them, so check
   which one answers): `--url=https://ice.somafm.com/groovesalad` (SomaFM's official direct URL),
   `--url=https://ice2.somafm.com/groovesalad-128-mp3`, `--url=https://stream.radioparadise.com/aac-128`.
   Expect: plays within a few seconds, `CurrentSourceCapabilities` NetworkLive, titles appear
   (Artist - Title) and change with the song at the moment the song changes audibly, not up to
   20 s early; volume, EQ, tempo and fades work; Pause/Play/Stop respond at once.
3. **Plain http radio** (`--url=http://ice2.somafm.com/groovesalad-128-mp3`) still plays through
   BASS as before (log category AudioEngine).
4. **Network loss** on the https station: unplug 3 s (no gap), 30 s (`Buffering=True`, then the
   stream ends like 014A: live streams do not reconnect).
5. **https MP3 file with ranges**: seek works, the end raises MediaEnded once, Loop does not loop.
6. **Audio-only HLS in Full mode**, e.g. an audio-only `.m3u8` radio stream: plays as audio, with
   a "No video stream ... opening it as audio" Info line.
7. **Picture-only network video holds during an outage**: Background mode
   (`--mode=background --url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`), unplug 30 s:
   the picture freezes, "Waiting for the network" appears; plug back in: it continues from the
   same frame (no jump), then plays to the end.
8. **Moov-at-end MP4 without ranges**: `python -m http.server 8000` in a folder with a
   non-faststart MP4, `--url=http://localhost:8000/clip.mp4 --net-retries=2`: fails at once with
   the faststart message, one attempt. `ffmpeg -i clip.mp4 -c copy -movflags +faststart fast.mp4`,
   `--url=http://localhost:8000/fast.mp4`: plays (not seekable).
9. **CA file**: a local https server with its own certificate and `--net-ca-file=<pem>` plays.

## Linux run (2026-11-05)

Environment: Linux sandbox, BASS 2.4 for Linux (+ BASS_FX, BASSFLAC), FFmpeg 9.0 LGPL, FatzAudio 1.0.0-rc.1
built from source (null output for the FatzAudio engine), PulseAudio null sinks with ALSA routed to it;
media from `scripts/setup-linux-native.sh` (`/opt/fatzbomb-native/media/formats`). Opt-in tests:
`FATZBOMB_LINUX_BASS_DIR`, `FATZBOMB_LINUX_FFMPEG_DIR` (see `LinuxNativeTestPlayer`). Internet through the sandbox's TLS-intercepting proxy (`http_proxy` for FFmpeg,
`FATZBOMB_LINUX_NETWORK_CA_FILE` = the proxy's CA bundle, `FATZBOMB_LINUX_NETWORK=1`).

`LinuxInternetStreamNativeTests`, 2 of 2 pass:
- https radio (`https://stream.radioparadise.com/mp3-128`): plays on the live clock (5.00 s in 5 s) and
  `StreamMetadataChanged` reported "Spoon - Everything Hits At Once".
- Public HLS (`https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`) in Background mode: 149 frames in about
  2.5 s, frames not blank.

Not run here: the bad-certificate checks (step 1-2; behind a TLS-intercepting proxy a bad remote certificate
never reaches the player; the local self-signed server cases in `LinuxNetworkVideoNativeTests` cover the
logic), plain http radio through BASS (step 4; the sandbox proxy carries https only), `ice2.somafm.com`
(answers 403 here), network unplug (step 5). These stay in `smoke-backlog.ps1` test 3 on Windows.
