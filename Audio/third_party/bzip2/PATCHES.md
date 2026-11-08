# bzip2 patches

libbzip2 1.0.8 (13 July 2019, Julian Seward, BSD-style licence in `LICENSE`), the decompressor only.
Taken from the `bzip2-1.0.8` sources shipped inside the `bzip2-sys` crate 0.1.13.

Files kept: `bzlib.c`, `bzlib.h`, `bzlib_private.h`, `decompress.c`, `huffman.c`, `randtable.c`,
`crctable.c`. Not kept: the compressor (`blocksort.c`, `compress.c`) and the command line tools.

Altered, as the licence requires to be marked:
- `bzlib.c`: the compression functions (`BZ2_bzCompressInit`, `BZ2_bzCompress`, `BZ2_bzCompressEnd`,
  `BZ2_bzBuffToBuffCompress` and the helpers only they use) are wrapped in
  `#ifndef BZ_DECOMPRESS_ONLY`. FatzAudio builds with `BZ_DECOMPRESS_ONLY` and `BZ_NO_STDIO`.
