#!/bin/sh
# Writes tests/data/ape: Monkey's Audio files for tests/test_ape.c. The audio is
# make_ape_wav.py's integer signal, so the test checks every sample exactly.
#
# The encoder is JMAC (Java, Ubuntu/Debian package libjmac-java), an encoder
# written apart from the Monkey's Audio SDK that FatzAudio decodes with; it
# writes file version 3.99, 8/16/24-bit, mono or stereo. FFmpeg (if found)
# checks that it decodes each file to the WAV's PCM.
#   sudo apt-get install libjmac-java default-jre-headless
set -e
here=$(cd "$(dirname "$0")" && pwd)
out="$here/ape"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$out"
jar=/usr/share/java/jmac.jar
wav() { python3 "$here/make_ape_wav.py" "$tmp/$1.wav" "$2" "$3" "$4" "$5"; }
ape() { java -jar "$jar" "c$3" "$tmp/$1.wav" "$out/$2.ape" >/dev/null 2>&1; }

wav s16 16 2 44100 8000
for level in 1 2 3 4 5; do ape s16 "s16_c$level" "$level"; done
# Longer than one Monkey's Audio frame (73728 frames at levels 1-3).
wav long 16 2 22050 80000
ape long s16_long 2
wav u8 8 1 8000 8000
ape u8 u8 2
wav s24 24 2 48000 8000
ape s24 s24 3

# The same audio behind an ID3v2 tag, and with an APEv2 tag at the end.
python3 - "$out" <<'EOF'
import struct, sys
out = sys.argv[1]
data = open(f'{out}/s16_c2.ape', 'rb').read()
frame = b'TIT2' + struct.pack('>I', 9) + b'\0\0' + b'\3ID3 Test'
size = len(frame) + 100   # and 100 bytes of padding
syncsafe = bytes((size >> s) & 0x7F for s in (21, 14, 7, 0))
open(f'{out}/id3v2.ape', 'wb').write(b'ID3\4\0\0' + syncsafe + frame + b'\0' * 100 + data)
items = b''
for key, value in ((b'Title', b'APE Test'), (b'Artist', b'FatzAudio')):
    items += struct.pack('<II', len(value), 0) + key + b'\0' + value
footer = b'APETAGEX' + struct.pack('<IIII', 2000, len(items) + 32, 2, 0) + b'\0' * 8
open(f'{out}/apev2.ape', 'wb').write(data + items + footer)
EOF

if command -v ffmpeg >/dev/null 2>&1; then
    for f in s16_c1 s16_c2 s16_c3 s16_c4 s16_c5 id3v2 apev2; do
        cmp -s "$(ffmpeg -v error -i "$out/$f.ape" -f s16le "$tmp/$f.pcm" && echo "$tmp/$f.pcm")" \
            "$(ffmpeg -v error -i "$tmp/s16.wav" -f s16le -y "$tmp/ref.pcm" && echo "$tmp/ref.pcm")" ||
            { echo "FFmpeg decodes $f.ape differently" >&2; exit 1; }
    done
fi
ls -l "$out"
