#!/bin/sh
# Regenerates the DTS test files of test_dts.c (needs ffmpeg, whose experimental DTS
# encoder writes them). FatzAudio has no DTS decoder: the tests hand the frames to a
# stand-in for the system's decoder, so only the framing, lengths and seeks are
# checked. 0.6 s of 48 kHz 5.1 at 768 kbit/s (the encoder's least for 5.1).
set -e
cd "$(dirname "$0")/dts"
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }
sine="aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t)|0.2*sin(2*PI*550*t)|0.1*sin(2*PI*330*t)|0.1*sin(2*PI*770*t)|0.05*sin(2*PI*60*t):s=48000:d=0.6:c=5.1"
vid="-f lavfi -i color=c=blue:s=64x48:r=25:d=0.6"
dca="-c:a dca -strict -2 -b:a 768k -fflags +bitexact"
ff -f lavfi -i "$sine" $dca -f dts tone.dts
ff $vid -f lavfi -i "$sine" -c:v mpeg2video $dca -f vob ps_dts.vob
ff -f lavfi -i "$sine" $dca -f avi dts.avi
ff -f lavfi -i "$sine" $dca dts.mkv
ls -l
