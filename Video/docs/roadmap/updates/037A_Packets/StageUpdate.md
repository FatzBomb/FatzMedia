# Stage Update

Stage: 037A - Video soundtracks decoded by FatzAudio from FFmpeg's packets (the slice 5 piece left at first)
Date: 2026-11-10

## Status: implemented, opt-in, pending manual smoke

The slice 5 plan (owner 2026-11-05) had FFmpeg demux every video and FatzAudio decode the audio when it can (packet
stream), FFmpeg decoding only what FatzAudio cannot (push stream). Slice 5 shipped the push path for everything.
This adds the packet path behind a switch, off by default, until the owner's ears confirm it:
`MediaPlayerConfiguration.AudioEngineDecodesSoundtracks` (test players: `--video-audio=fatz`).

## Design

| Part | Where | What |
|---|---|---|
| `IPushAudioOutput.CreateForPackets` / `PushPacket`, `PacketCodecInfo` (internal) | Core | An output may take compressed packets. `PacketCodecInfo` is plain data from FFmpeg's codec parameters (name, rate, channels, configuration bytes, block align, bits, bit rate): no FFmpeg object crosses. BASS declines. |
| `VideoAudioEngine` packet mode | Core | When the output accepts the codec, FFmpeg only demuxes: each packet goes out stamped on the same continuous timeline as decoded frames (loop wraps, throttle and position work unchanged). After a seek, packets that end before the target are dropped; the one spanning it plays from its start, so a seek lands up to one packet (20-40 ms) early, and the clock reports exactly what is heard. |
| `FatzAudioPushAudioOutput.CreateForPackets` | `FatzMedia.Video.Audio` | Opens a FatzAudio packet stream (timestamps in microseconds) for MP3, MP2, AAC, AC-3, E-AC-3, FLAC, Opus and Vorbis, the codecs it decodes on every platform; anything else (DTS, TrueHD, ...) stays decoded by FFmpeg. Same bus, fades, EQ, tempo, loudness identity as the push path. |

Why it stays off by default: it changes who decodes every film's sound, and encoder delay handling (AAC priming in
MP4) and the seek landing differ slightly from FFmpeg's sample-exact path. The measured checks below pass; the
owner's hand test decides the default.

## Tests

`FatzAudioPacketSoundtrackNativeTests` (4, real sample files from `shared\sample-files`, FatzAudio's null device): an
AAC music video (MP4), an AC-3 file and an MP3 play from packets, the clock follows real time, a seek lands within
one packet, tempo 1.5 scales the clock; without the switch FFmpeg decodes. Option parsing.

## Manual smoke (owner)

Check M9b of the hand-test script: the M1 music video with `--video-audio=fatz` keeps sync as well as without it,
seeks and tempo included. If it does, flip the default.
