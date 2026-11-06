#!/usr/bin/env python3
"""Writes the WAV files that make_ape_data.sh compresses into tests/data/ape.

The signal is integer-only so that tests/test_ape.c can compute the very same
samples: per channel a triangle wave (period 200 + 50 * channel frames) plus
sparse noise from one 32-bit LCG stepped once per sample (interleaved order):
+1 when bits 16-18 of the state are 0, -1 when they are 1, else 0. Usage: make_ape_wav.py OUT.wav BITS CHANNELS RATE FRAMES
"""
import struct
import sys

AMPLITUDE = {8: 60, 16: 8000, 24: 2000000, 32: 400000000}


def samples(bits, channels, frames):
    a = AMPLITUDE[bits]
    x = 1
    for i in range(frames):
        for c in range(channels):
            period = 200 + 50 * c
            phase = i % period
            half = phase if phase < period // 2 else period - phase
            tri = (half * 4 * a) // period - a
            x = (x * 1103515245 + 12345) & 0xFFFFFFFF
            n = (x >> 16) & 7
            yield tri + (n == 0) - (n == 1)


def main():
    out, bits, channels, rate, frames = sys.argv[1], *map(int, sys.argv[2:6])
    data = bytearray()
    for v in samples(bits, channels, frames):
        if bits == 8:
            data.append(v + 128)
        elif bits == 16:
            data += struct.pack('<h', v)
        elif bits == 24:
            data += struct.pack('<i', v)[:3]
        else:
            data += struct.pack('<i', v)
    align = channels * bits // 8
    fmt = struct.pack('<HHIIHH', 1, channels, rate, rate * align, align, bits)
    with open(out, 'wb') as f:
        f.write(b'RIFF' + struct.pack('<I', 36 + len(data)) + b'WAVE')
        f.write(b'fmt ' + struct.pack('<I', len(fmt)) + fmt)
        f.write(b'data' + struct.pack('<I', len(data)) + data)


if __name__ == '__main__':
    main()
