# libwavpack (decoder only)

Vendored from https://github.com/dbry/WavPack, release 5.9.0 (tag `5.9.0`,
commit 5803634a, 2026-01-16). BSD-3-Clause, see `COPYING`. Built by
`third_party/wavpack.cmake` as a static library linked into fatzaudio; nothing
of it is exported.

Only the files the decoder needs are copied, unchanged: `include/wavpack.h`,
`src/wavpack_local.h`, `src/wavpack_version.h`, and `common_utils.c`,
`decorr_utils.c`, `entropy_utils.c`, `open_utils.c`, `read_words.c`, `tags.c`,
`unpack.c`, `unpack_floats.c`, `unpack_seek.c`, `unpack_utils.c` and
`unpack_dsd.c` from `src/`. Left out: the encoder (`pack*.c`, `write_words.c`,
`extra*.c`), the file-name and raw openers, tag editing, the legacy (< 4.0)
decoder and the x86/x64/ARM assembly.

Build settings: `ENABLE_DSD` (DSD files decode to PCM through the library's own
8x decimator), no `ENABLE_THREADS` (FatzAudio decodes on its own workers), no
`ENABLE_LEGACY`. FatzAudio's glue is `src/dec_wavpack.c`.

To update: copy the same files from the new release, run the WavPack tests in
`tests/test_demux.c`, and update the version here and in
`THIRD_PARTY_NOTICES.md`.
