# FatzBomb.MediaControl: requests 13, 14 and 15 (formats and normalization for fed audio)

To: FatzAudio. From: FatzBomb.MediaControl, 2026-11-05 (passed on by the owner). Context: MediaControl
plays every format FFmpeg reads; after 037A slice 5, video audio reaches FatzAudio as a packet stream
(FatzAudio decodes the codec) or a push stream (FFmpeg decodes: MP2 today, DTS, TrueHD, ...). Owner goal:
no format a user can play in MediaControl lacks a feature because of which library decodes it. We will
not pass FFmpeg handles; FatzAudio's own structs stay the interface.

## Request 13: normalization for audio we feed you

Packet and push streams are never scanned today, so they get only the running estimate and no cache.

1. **Scan from fed audio:** an analysis input we can feed in the background, faster than real time,
   with packets (you decode them) or decoded frames, giving the same loudness result as a scan by path,
   stored in your cache under an identity we give (the original file's path or fingerprint).
2. **Source identity on packet and push streams:** the same identity on the playing stream, so the
   scan, the cache and the start wait (request 12) attach to it as for a file you opened yourself.

## Request 14: MPEG audio layers, old video containers by path, DTS core, WMA Standard

Many existing karaoke and trivia files are AVI/DivX, MPG/MPEG and TS; trivia clips are often cut from
movie files. These items let FatzAudio play, scan and cache such files by itself, and keep request 13 for
the rare codecs only.

1. **Decode MP1 and MP2** (MPEG-1/2 Audio Layers I and II): patent-free and small (minimp3 decodes
   Layers I-III unless built MP3-only); in files (`.mp2`, `.mpa`) and packet streams.
2. **Confirm MP3 coverage for old video files:** MPEG-2 and MPEG-2.5 Layer III (22.05/24 kHz and
   8-12 kHz, common in DivX-era AVIs), VBR without a Xing/Info header, and MP3 packets from an AVI demuxer.
3. **Open by path for their audio track:** AVI (RIFF, as WAV), MPEG-PS (`.mpg`, `.mpeg`, `.vob`, `.dat`)
   and MPEG-TS (`.ts`, `.m2ts`, `.mts`; TS is already read for HLS). FLV optional.
4. **DTS core decoding** (in MKV, MP4, AVI, TS and `.dts`): patents long expired; DTS-HD lossless
   extensions can stay out (the core inside them plays).
5. **WMA Standard (v1/v2) decoding on every platform**, in `.wma` files and WMV/ASF soundtracks. Today WMA
   decodes only through Windows' Media Foundation, which Windows "N" editions lack unless the Media
   Feature Pack is installed, and which Linux and macOS do not have. WMA Standard is what nearly all WMA
   files use; its patents date from about 1999 (please confirm they have expired). WMA Pro, Lossless and
   Voice can stay on the system decoder (and FFmpeg push in MediaControl).

Please give effort and binary size for each item before building.

## Request 15: the rest of the audio codecs (owner principle, 2026-11-05)

FatzAudio is the audio engine, MediaControl the video engine: every audio codec a user meets should be
FatzAudio's, and MediaControl's FFmpeg push is the fallback only where FatzAudio cannot take a codec
(licensing), not by choice. After request 14, please plan (effort, binary size, licence for each):

1. **TrueHD** (MLP lossless; Blu-ray movie rips) and the **DTS-HD lossless extensions** (on top of
   request 14's DTS core).
2. **Lossless and audiophile music formats:** Monkey's Audio (APE; common in Asian music rips, so in
   karaoke libraries), WavPack, Musepack, TTA, and DSD (DSF, DFF; converted to PCM).
3. **Small ones:** Sun `.au`, µ-law/A-law in CAF (as in WAV), Speex.
4. **Older formats still found in collections:** RealAudio (`.rm`/`.ra`, cook), old QuickTime audio (QDesign,
   IMA ADPCM in `.mov`).
5. **Containers by path:** FLV, RealMedia (`.rm`, `.rmvb`), WTV and DVR-MS (Windows Media Center recordings),
   and confirm 3GP opens as MP4.

Expected to stay outside FatzAudio for licensing (FFmpeg or the OS decoder in MediaControl): xHE-AAC (USAC)
and AAC-LD/ELD (Fraunhofer licensing), AC-4 and MPEG-H 3D Audio (live patent pools, rare in files), and WMA
Pro/Lossless/Voice if your patent check says so. Tell us if your licence review moves any item either way.
