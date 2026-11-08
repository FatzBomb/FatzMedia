#!/bin/sh
# Regenerates the MPEG audio Layer II, Sun AU, G.711 and IMA4 test files of
# test_demux.c (needs ffmpeg). All are sines at known frequencies and levels:
# 440 Hz at 0.5 (left) and 660 Hz at 0.3 (right), 2 s (MP2) or 0.75 s at 8 kHz (the rest).
set -e
cd "$(dirname "$0")"
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
src="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=44100:d=2"
ff -f lavfi -i "$src" -c:a mp2 -b:a 192k -fflags +bitexact mp2/stereo.mp2
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*440*t):s=22050:d=2" -c:a mp2 -b:a 48k -fflags +bitexact mp2/mono22k.mp2   # MPEG-2 Layer II
ff -f lavfi -i "$src" -c:a mp2 -b:a 192k -map_metadata -1 -fflags +bitexact mp2/stereo.mka   # Layer II packets in Matroska
ff -f lavfi -i "$src" -c:a mp2 -b:a 192k -fflags +bitexact -f mp4 mp2/stereo.mp4            # object type 0x6B in MP4
s8k="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=8000:d=0.75"
for c in pcm_s8:s8 pcm_s16be:s16 pcm_s24be:s24 pcm_s32be:s32 pcm_f32be:f32 pcm_f64be:f64 pcm_mulaw:ulaw pcm_alaw:alaw; do
    ff -f lavfi -i "$s8k" -c:a "${c%%:*}" -map_metadata -1 -fflags +bitexact -f au "au/${c##*:}.au"
done
for c in pcm_mulaw:ulaw pcm_alaw:alaw adpcm_ima_qt:ima4; do
    ff -f lavfi -i "$s8k" -c:a "${c%%:*}" -map_metadata -1 -fflags +bitexact "caf/${c##*:}.caf"
done
ff -f lavfi -i "$s8k" -c:a adpcm_ima_qt -map_metadata -1 -fflags +bitexact ima4.mov
ls -l mp2 au caf/ulaw.caf caf/alaw.caf caf/ima4.caf ima4.mov
