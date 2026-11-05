#!/usr/bin/env python3
"""Writes tests/archive_fixtures.h: tar, tar.gz, tar.bz2, tar.xz and single-file gz/bz2/xz archives of a
small song and its graphics (the same bytes in every container), and a large one for the stream mode.
Needs gzip, bzip2 and xz on the PATH, and the py7zr package for the 7z archives another writer made. Usage: make_archive_fixtures.py > archive_fixtures.h"""
import gzip, io, lzma, math, struct, subprocess, sys, tarfile, zlib

def wav(samples):
    data = b"".join(struct.pack("<h", int(6000 * math.sin(i * 0.05))) for i in range(samples))
    return (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, 8000, 16000, 2, 16)
            + b"data" + struct.pack("<I", len(data)) + data)

def big_wav(seconds):
    # 8 kHz mono 16-bit; the samples repeat with a long period so that every codec packs them small, yet
    # bzip2 still ends up with many blocks and xz with a long dictionary reach.
    n = 8000 * seconds * 2
    data = bytes(((i % 251) + (i >> 16)) & 255 for i in range(n))
    return (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, 8000, 16000, 2, 16)
            + b"data" + struct.pack("<I", len(data)) + data)

def tar(files):
    b = io.BytesIO()
    with tarfile.open(fileobj=b, mode="w", format=tarfile.GNU_FORMAT) as t:
        for name, d in files:
            ti = tarfile.TarInfo(name); ti.size = len(d); ti.mtime = 1700000000
            t.addfile(ti, io.BytesIO(d))
    return b.getvalue()

def run(cmd, data):
    return subprocess.run(cmd, input=data, stdout=subprocess.PIPE, check=True).stdout

def fnv(d):
    h = 0xcbf29ce484222325
    for c in d:
        h = ((h ^ c) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def array(name, d):
    out = ["static const unsigned char %s[%d] = {" % (name, len(d))]
    for i in range(0, len(d), 24):
        out.append("    " + ",".join(str(c) for c in d[i:i + 24]) + ",")
    out.append("};")
    return "\n".join(out)

small = [("songs/a/song.wav", wav(2000)), ("songs/a/song.cdg", bytes(24 * 20)), ("songs/other.wav", wav(500)), ("notes.txt", b"hello\n" * 40)]
big = [("big.wav", big_wav(600)), ("big.cdg", bytes(24 * 300)), ("small.wav", wav(300))]
parts = []
def emit(prefix, files):
    t = tar(files)
    if prefix == "fx_small":
        parts.append(array(prefix + "_tar", t))
    parts.append(array(prefix + "_tar_gz", gzip.compress(t, 9, mtime=0)))
    parts.append(array(prefix + "_tar_bz2", run(["bzip2", "-9" if prefix == "fx_big" else "-1", "-c"], t)))
    if prefix == "fx_big":
        # Several xz blocks, so the blocks can be decoded side by side and entered at their starts.
        parts.append(array(prefix + "_tar_xz", run(["xz", "-6", "-T2", "--block-size=1MiB", "-c"], t)))
    else:
        parts.append(array(prefix + "_tar_xz", run(["xz", "-6", "-c"], t)))
    parts.append("#define %s_COUNT %d" % (prefix.upper(), len(files)))
    for i, (n, d) in enumerate(files):
        parts.append("#define %s_SIZE_%d %du\n#define %s_FNV_%d 0x%xull" % (prefix.upper(), i, len(d), prefix.upper(), i, fnv(d)))
emit("fx_small", small)
emit("fx_big", big)
w = small[0][1]
parts.append(array("fx_one_gz", gzip.compress(w, 9, mtime=0)))
parts.append(array("fx_one_bz2", run(["bzip2", "-c"], w)))
parts.append(array("fx_one_xz", run(["xz", "-c"], w)))
parts.append("#define FX_ONE_SIZE %du\n#define FX_ONE_FNV 0x%xull" % (len(w), fnv(w)))

# ---- 7z -----------------------------------------------------------------------------------------------
# Our own small 7z writer (Copy, LZMA, LZMA2; plain or LZMA-packed header), because no 7z program is at hand;
# it is checked against archives that py7zr wrote.

def num7(n):
    for i in range(9):
        if n < (1 << (7 * (i + 1))):
            first = (0xFF00 >> i) & 0xFF
            return bytes([first | (n >> (8 * i))]) + (n & ((1 << (8 * i)) - 1)).to_bytes(i, "little")
    return b"\xFF" + n.to_bytes(8, "little")

def lzma2_prop(dict_size):
    for p in range(40):
        if (2 | (p & 1)) << (p // 2 + 11) >= dict_size:
            return p
    return 40

def lzma2_pack(data, dict_size, piece=None):
    """LZMA2 of data; with `piece`, every piece starts with a dictionary reset (as 7-Zip's threaded encoder makes)."""
    out = b""
    step = piece or len(data) or 1
    for at in range(0, max(len(data), 1), step):
        c = lzma.LZMACompressor(lzma.FORMAT_RAW, filters=[{"id": lzma.FILTER_LZMA2, "preset": 6, "dict_size": dict_size}])
        part = c.compress(data[at:at + step]) + c.flush()
        out += part[:-1]   # without the end byte
    return out + b"\x00"

def lzma1_pack(data, dict_size):
    c = lzma.LZMACompressor(lzma.FORMAT_RAW, filters=[{"id": lzma.FILTER_LZMA1, "preset": 6, "dict_size": dict_size, "lc": 3, "lp": 0, "pb": 2}])
    return c.compress(data) + c.flush()

def lzma1_props(dict_size):
    return bytes([(2 * 5 + 0) * 9 + 3]) + dict_size.to_bytes(4, "little")

def coder(kind, dict_size):
    if kind == "copy":
        return num7(1) + bytes([0x01, 0x00])
    if kind == "lzma2":
        return num7(1) + bytes([0x21, 0x21, 1, lzma2_prop(dict_size)])
    if kind == "lzma":
        return num7(1) + bytes([0x23, 3, 1, 1, 5]) + lzma1_props(dict_size)
    if kind == "aes":
        return num7(1) + bytes([0x24, 0x06, 0xF1, 0x07, 0x01, 1, 0x19])
    raise ValueError(kind)

def pack_folder(kind, data, dict_size, piece):
    if kind in ("copy", "aes"):
        return data
    if kind == "lzma2":
        return lzma2_pack(data, dict_size, piece)
    return lzma1_pack(data, dict_size)

def make7z(folders, empties=(), dict_size=1 << 20, piece=None, encode_header=False, crc_every=True):
    """folders: [(kind, [(name, data), ...]), ...]; empties: [(name, is_dir)]. Returns the archive bytes."""
    packs = []
    for kind, files in folders:
        packs.append(pack_folder(kind, b"".join(d for _, d in files), dict_size, piece))
    body = b"".join(packs)
    h = bytearray([1, 4, 6]) + num7(0) + num7(len(packs)) + bytes([9]) + b"".join(num7(len(x)) for x in packs) + bytes([0])
    h += bytes([7, 0x0B]) + num7(len(folders)) + b"\x00"
    for kind, files in folders:
        h += coder(kind, dict_size)
    h += bytes([0x0C]) + b"".join(num7(sum(len(d) for _, d in files)) for _, files in folders) + bytes([0])
    h += bytes([8, 0x0D]) + b"".join(num7(len(files)) for _, files in folders)
    sizes = b""
    for _, files in folders:
        sizes += b"".join(num7(len(d)) for _, d in files[:-1])
    if sizes:
        h += bytes([9]) + sizes
    crcs = [zlib.crc32(d) for _, files in folders for _, d in files]
    h += bytes([0x0A, 1]) + b"".join(struct.pack("<I", c) for c in crcs) + bytes([0])
    h += bytes([0])
    names = [n for _, files in folders for n, _ in files]
    allnames = names + [n for n, _ in empties]
    h += bytes([5]) + num7(len(allnames))
    if empties:
        nbit = len(allnames)
        bits = bytearray((nbit + 7) // 8)
        for i in range(len(names), nbit):
            bits[i // 8] |= 0x80 >> (i % 8)
        h += bytes([0x0E]) + num7(len(bits)) + bytes(bits)
        ebits = bytearray((len(empties) + 7) // 8)
        for j, (_, is_dir) in enumerate(empties):
            if not is_dir:
                ebits[j // 8] |= 0x80 >> (j % 8)
        h += bytes([0x0F]) + num7(len(ebits)) + bytes(ebits)
    nm = b"\x00" + b"".join(n.encode("utf-16-le") + b"\x00\x00" for n in allnames)
    h += bytes([0x11]) + num7(len(nm)) + nm
    ft = (1700000000 + 11644473600) * 10000000
    mt = b"\x01\x00" + b"".join(struct.pack("<Q", ft + i * 10000000) for i in range(len(allnames)))
    h += bytes([0x14]) + num7(len(mt)) + mt
    attrs = b""
    for i, n in enumerate(allnames):
        is_dir = i >= len(names) and empties[i - len(names)][1]
        attrs += struct.pack("<I", 0x10 | 0x8000 | (0o040755 << 16) if is_dir else 0x20 | 0x8000 | (0o100644 << 16))
    at = b"\x01\x00" + attrs
    h += bytes([0x15]) + num7(len(at)) + at
    h += bytes([0, 0])
    header = bytes(h)
    if encode_header:
        hp = lzma1_pack(header, 1 << 16)
        eh = bytes([0x17, 6]) + num7(len(body)) + num7(1) + bytes([9]) + num7(len(hp)) + bytes([0])
        eh += bytes([7, 0x0B]) + num7(1) + b"\x00" + coder("lzma", 1 << 16) + bytes([0x0C]) + num7(len(header))
        eh += bytes([0x0A, 1]) + struct.pack("<I", zlib.crc32(header)) + bytes([0, 0])
        body += hp
        header = eh
    start = struct.pack("<QQI", len(body), len(header), zlib.crc32(header))
    return b"7z\xBC\xAF\x27\x1C\x00\x04" + struct.pack("<I", zlib.crc32(start)) + start + body + header

def crc_defs(prefix, files):
    out = ["#define %s_COUNT %d" % (prefix.upper(), len(files))]
    for i, (n, d) in enumerate(files):
        out.append("#define %s_SIZE_%d %du\n#define %s_CRC_%d 0x%08xu" % (prefix.upper(), i, len(d), prefix.upper(), i, zlib.crc32(d)))
    return "\n".join(out)

wavs = small[0][1]
sz_files = [small[0], small[1], small[2], small[3]]
parts.append(array("fx_7z_solid", make7z([("lzma2", sz_files)], empties=[("songs", True), ("songs/empty.txt", False)], encode_header=True)))
parts.append(array("fx_7z_plain_header", make7z([("lzma2", sz_files)])))
parts.append(array("fx_7z_copy", make7z([("copy", sz_files)])))
parts.append(array("fx_7z_split", make7z([("lzma", [f]) for f in sz_files], encode_header=True)))
parts.append(array("fx_7z_lzma1", make7z([("lzma", sz_files)])))
parts.append(crc_defs("fx_7z", sz_files))

def blocks(seed, size):
    # A 4 KB block of noise repeated: the archive is small and every file differs.
    x = seed * 2654435761 + 12345
    b = bytearray()
    for _ in range(4096):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        b.append((x >> 16) & 255)
    return (bytes(b) * (size // 4096 + 1))[:size]

# Three 24 MB files in one folder with a dictionary reset every 20 MB, so one reset falls inside the second file.
big7 = [("a.bin", blocks(1, 24 << 20)), ("b.bin", blocks(2, 24 << 20)), ("c.bin", blocks(3, 24 << 20))]
parts.append(array("fx_7z_big", make7z([("lzma2", big7)], dict_size=1 << 21, piece=20 << 20)))
parts.append(crc_defs("fx_7z_big", big7))
parts.append("#define FX_7Z_BIG_RESET 20971520u")

# Random bytes LZMA2 stores as uncompressed chunks (a 64 KB chunk and a short one), between two files that compress.
def noise(n):
    x = 12345
    b = bytearray()
    for _ in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        b.append((x >> 16) & 255)
    return bytes(b)
noisy = [("a.txt", b"first file\n" * 50), ("noise.bin", noise(66000)), ("z.txt", b"last file\n" * 70)]
parts.append(array("fx_7z_noise", make7z([("lzma2", noisy)])))
parts.append(crc_defs("fx_7z_noise", noisy))
parts.append(array("fx_7z_aes", make7z([("aes", sz_files)])))

# The same song in an archive another writer made (py7zr), and two folders this reader lists but cannot decode.
import py7zr
def py7z(filters):
    b = io.BytesIO()
    with py7zr.SevenZipFile(b, "w", filters=filters) as z:
        for n, d in sz_files:
            z.writestr(d, n)
    return b.getvalue()
parts.append(array("fx_7z_py", py7z([{"id": py7zr.FILTER_LZMA2, "preset": 6}])))
parts.append(array("fx_7z_py_ppmd", py7z([{"id": py7zr.FILTER_PPMD, "order": 6, "mem": 22}])))
parts.append(array("fx_7z_py_bcj", py7z([{"id": py7zr.FILTER_X86}, {"id": py7zr.FILTER_LZMA2, "preset": 6}])))
sys.stdout.write("/* Generated by make_archive_fixtures.py: do not edit. */\n" + "\n".join(parts) + "\n")
