#!/usr/bin/env python3
"""Writes a small DSF (Sony DSD stream) file for the WavPack DSD test: DSD64
(2.8224 MHz), stereo, 0.1 s, a 1 kHz sine at 0.5 on the left and at 0.3 on the
right, from a 2nd-order sigma-delta modulator. Usage: make_dsf.py OUT.dsf"""
import math, struct, sys

RATE, SECONDS, BLOCK = 2822400, 0.1, 4096
frames = int(RATE * SECONDS)
frames -= frames % 8
chans = []
for amp in (0.5, 0.3):
    bits, i1, i2, y = [], 0.0, 0.0, 1.0
    w = 2 * math.pi * 1000 / RATE
    for n in range(frames):
        x = amp * math.sin(w * n)
        i1 += x - y
        i2 += i1 - y
        y = 1.0 if i2 >= 0 else -1.0
        bits.append(1 if y > 0 else 0)
    data = bytearray()
    for k in range(0, frames, 8):                 # DSF: least significant bit first
        b = 0
        for j in range(8):
            b |= bits[k + j] << j
        data.append(b)
    data += bytes((-len(data)) % BLOCK)             # whole blocks, padded with zero bytes
    chans.append(bytes(data))
blocks = len(chans[0]) // BLOCK
audio = b"".join(chans[c][i * BLOCK:(i + 1) * BLOCK] for i in range(blocks) for c in range(2))
fmt = struct.pack("<4sQIIIIIIQII", b"fmt ", 52, 1, 0, 2, 2, RATE, 1, frames, BLOCK, 0)
data = struct.pack("<4sQ", b"data", 12 + len(audio)) + audio
total = 28 + len(fmt) + len(data)
head = struct.pack("<4sQQQ", b"DSD ", 28, total, 0)
with open(sys.argv[1], "wb") as f:
    f.write(head + fmt + data)
