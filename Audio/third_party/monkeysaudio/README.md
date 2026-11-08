# Monkey's Audio SDK

The official Monkey's Audio SDK by Matthew T. Ashland, decoder part only, BSD-3-Clause
(see `License.txt`, read from this release).

| Library | Version | Source |
|---|---|---|
| Monkey's Audio SDK | 13.45 | https://monkeysaudio.com/files/MAC_1345_SDK.zip (SHA-256 `b12bda0eb9e31dbe01d54a970d387add3a8c1079901141021d257de17a6a9106`, downloaded 2026-11-06) |

Releases from 10.18 (August 2023) on are BSD-3-Clause; older ones had a licence of their own. Do
not replace this with a release before 10.18.

## What was kept

Only the files the decoder needs, from `Source/` of the SDK, at the same paths:

- `Source/MACLib/`: `APEDecompress`, `APEDecompressCore`, `APEHeader`, `APEInfo`, `APETag`,
  `FloatTransform`, `Interim`, `NewPredictor`, `NNFilter` (with the generic, SSE2, SSE4.1, AVX2,
  AVX-512 and Neon filters; the CPU's features pick one at run time), `Prepare`, `UnBitArray`,
  `UnBitArrayBase`, their headers, `MACLib.h` and the headers they include, and `Old/` (files of
  versions 3.80 to 3.92).
- `Source/Shared/`: `CharacterHelper`, `CircleBuffer`, `CPUFeatures`, `CRC`, `GlobalFunctions`,
  `MemoryIO`, `Semaphore`, `Thread`, `WholeFileIO` and the shared headers.
- `Source/MACLib/MACLibDecode.cpp` is ours: the decoder's share of `MACLib.cpp`, copied unchanged
  (`CreateIAPEDecompressCore`, `CreateIAPEDecompressEx2`, the WAV header helpers), plus a
  `CreateIAPEIO` that returns no I/O object. `MACLib.cpp` itself also holds the encoder and file
  conversion, so it is left out.

Left out: the encoder, the command-line tool, the DLL, file I/O, APL link files, MD5
verification, the Delphi, MFC and sample folders.

`third_party/monkeysaudio.cmake` builds these with `src/codec_ape.cpp` (the C interface
`src/codec_ape.h`) as the static library `fa_mac` (C++11, hidden visibility, exceptions and RTTI
on, `-fwrapv`, `-Os`) and links it into `fatzaudio`. `src/dec_ape.c` is the decoder. No SDK symbol
is exported from `fatzaudio`.

## Changes (each marked `FatzAudio` in the source)

1. `FA_MAC_SYNC_DECODE` (`APEDecompress.cpp`, `APEDecompressCore.cpp`): frames decode on the
   thread that reads, one at a time, when they are needed. The SDK starts a worker thread per
   decoder and decodes one frame ahead; in a player with its own decode threads that is a thread
   per open file and twice the work on every seek. A frame whose decoding throws (damaged
   data) counts as a damaged frame; on the SDK's own thread the exception would end the
   process. (Found by fuzzing.)
2. `All.h`: `DLLEXPORT` is defined only if it is not defined already, so the build can leave it
   empty (nothing is exported).
3. `APEHeader.cpp`: a file whose seek table has fewer entries than it has frames is refused (the
   SDK read past the end of the table; found by fuzzing).

Two more checks are in `src/codec_ape.cpp`, outside the SDK: the header's frame size and each
frame's place and size in the seek table must make sense before the SDK allocates for them (a
damaged table asked for 4 GB; found by fuzzing), and an exception that still leaves the SDK
marks the decoder broken, so it is never called again in a half-updated state.

To update: unpack the new SDK, copy the files listed above over these, keep `MACLibDecode.cpp` in
step with the new `MACLib.cpp`, and apply the changes again (search for `FatzAudio`).
