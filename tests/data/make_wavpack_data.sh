#!/bin/sh
# Regenerates the WavPack test files of test_demux.c. Needs ffmpeg (for the WAV
# sources), python3 and the WavPack 5.x command-line encoder `wavpack` (on PATH or
# in $WAVPACK). All are sines at known levels: 440 Hz at 0.5 (left) and 660 Hz at
# 0.3 (right), 0.75 s at 8 kHz, in blocks of 1024 frames so that seeks cross blocks.
set -e
cd "$(dirname "$0")"
WAVPACK=${WAVPACK:-wavpack}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
wv() { "$WAVPACK" -q -y --blocksize=1024 "$@"; }
src="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s=8000:d=0.75"
for c in pcm_u8:u8 pcm_s16le:s16 pcm_s24le:s24 pcm_s32le:s32 pcm_f32le:f32; do
    ff -f lavfi -i "$src" -c:a "${c%%:*}" -map_metadata -1 -fflags +bitexact "$tmp/${c##*:}.wav"
    wv "$tmp/${c##*:}.wav" -o "wavpack/${c##*:}.wv"
done
wv -hh -x3 "$tmp/s16.wav" -o wavpack/s16_high.wv                 # high mode, extra filters
wv -b3 "$tmp/s16.wav" -o wavpack/hybrid.wv                       # lossy hybrid at 3 bits per sample
ff -f lavfi -i "aevalsrc=0.5*sin(2*PI*440*t):s=22050:d=0.5" -c:a pcm_s16le -fflags +bitexact "$tmp/mono.wav"
wv "$tmp/mono.wav" -o wavpack/mono22k.wv
# 5.1 in WAVE order (FL FR FC LFE BL BR): 200, 300, ..., 700 Hz, each at 0.4.
ff -f lavfi -i "aevalsrc=0.4*sin(2*PI*200*t)|0.4*sin(2*PI*300*t)|0.4*sin(2*PI*400*t)|0.4*sin(2*PI*500*t)|0.4*sin(2*PI*600*t)|0.4*sin(2*PI*700*t):s=8000:d=0.75:c=5.1" \
    -c:a pcm_s16le -fflags +bitexact "$tmp/51.wav"
wv "$tmp/51.wav" -o wavpack/51.wv
python3 make_dsf.py "$tmp/dsd.dsf"
"$WAVPACK" -q -y "$tmp/dsd.dsf" -o wavpack/dsd64.wv
ls -l wavpack
