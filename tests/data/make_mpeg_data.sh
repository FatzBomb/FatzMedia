#!/bin/sh
# Regenerates the MPEG program/transport stream test files of test_demux.c (needs ffmpeg). Sines at
# known frequencies and levels: 440 Hz at 0.5 (left) and 660 Hz at 0.3 (right), 2 s at 44.1 kHz
# (48 kHz where the format wants it) next to a small video track.
set -e
cd "$(dirname "$0")/mpeg"
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
vid="-f lavfi -i color=c=blue:s=64x48:r=25:d=2"
sine="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=2"
sine48="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=48000:d=2"
# Program streams.
ff $vid -f lavfi -i "$sine" -c:v mpeg2video -c:a mp2 -b:a 128k -fflags +bitexact -f mpeg ps_mp2.mpg
ff $vid -f lavfi -i "$sine48" -c:v mpeg2video -c:a ac3 -b:a 192k -fflags +bitexact -f vob ps_ac3.vob
# 640 kbit/s AC-3: a frame (2560 bytes) is longer than a PES payload, so frames straddle packets that each carry a PTS.
ff $vid -f lavfi -i "$sine48" -c:v mpeg2video -c:a ac3 -b:a 640k -fflags +bitexact -f vob ps_ac3_640.vob
ff $vid -f lavfi -i "$sine" -c:v mpeg1video -c:a mp2 -b:a 224k -fflags +bitexact -f vcd vcd.dat
ff -f lavfi -i "$sine" -c:a mp2 -b:a 128k -fflags +bitexact -f mpeg ps_audio_only.mpg
# Two audio tracks: MP2 then AC-3.
ff $vid -f lavfi -i "$sine48" -f lavfi -i "$sine" -map 0 -map 1 -map 2 -c:v mpeg2video -c:a:0 ac3 -b:a:0 192k -c:a:1 mp2 -b:a:1 128k \
    -fflags +bitexact -f vob ps_two_audio.vob
# Transport streams.
for c in mp2:"mp2 -b:a 128k" mp3:"libmp3lame -b:a 128k" aac:"aac -b:a 128k" ac3:"ac3 -b:a 192k" eac3:"eac3 -b:a 192k"; do
    ff $vid -f lavfi -i "$sine48" -c:v mpeg2video -c:a ${c#*:} -fflags +bitexact -f mpegts "ts_${c%%:*}.ts"
done
ff $vid -f lavfi -i "$sine48" -c:v mpeg2video -c:a ac3 -b:a 192k -fflags +bitexact -f mpegts -mpegts_m2ts_mode 1 m2ts_ac3.m2ts
ff $vid -f lavfi -i "$sine48" -f lavfi -i "$sine" -map 0 -map 1 -map 2 -c:v mpeg2video -c:a:0 aac -c:a:1 mp2 \
    -fflags +bitexact -f mpegts ts_two_audio.ts
ff -f lavfi -i "$sine" -c:a aac -b:a 128k -fflags +bitexact -f mpegts ts_audio_only.ts
ff $vid -f lavfi -i "$sine48" -c:v mpeg2video -c:a dca -strict -2 -b:a 384k -fflags +bitexact -f mpegts ts_dts.ts 2>/dev/null || true
# PTS that run past 2^33 (95443.7 s) half way: the muxer wraps them.
ff $vid -f lavfi -i "$sine" -c:v mpeg2video -c:a mp2 -b:a 128k -output_ts_offset 95442.7 -fflags +bitexact -f mpegts ts_wrap.ts
ff $vid -f lavfi -i "$sine" -c:v mpeg2video -c:a mp2 -b:a 128k -output_ts_offset 95442.7 -fflags +bitexact -f mpeg ps_wrap.mpg
# Windows Media Center recordings are ASF files with MP2 or AC-3 audio (no DVR-MS sample is at hand: these have the same layout).
ff -f lavfi -i "$sine48" -c:a mp2 -b:a 192k -fflags +bitexact -f asf dvrms_mp2.dvr-ms
ff -f lavfi -i "$sine48" -c:a ac3 -b:a 192k -fflags +bitexact -f asf dvrms_ac3.dvr-ms
# 12 s files, for seeks that have to search.
vidl="-f lavfi -i color=c=blue:s=64x48:r=25:d=12"
sinel="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=12"
ff $vidl -f lavfi -i "$sinel" -c:v mpeg2video -b:v 200k -c:a mp2 -b:a 128k -fflags +bitexact -f mpeg ps_long.mpg
ff $vidl -f lavfi -i "$sinel" -c:v mpeg2video -b:v 200k -c:a aac -b:a 128k -fflags +bitexact -f mpegts ts_long.ts
ls -l
