/* Monkey's Audio (.ape) through the vendored Monkey's Audio SDK
 * (third_party/monkeysaudio, BSD-3-Clause). codec_ape.cpp wraps the SDK's C++
 * decoder in these C calls; dec_ape.c is the decoder that uses them. */
#ifndef FA_CODEC_APE_H
#define FA_CODEC_APE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* The bytes of the file. read: bytes read, 0 at the end, < 0 on an error.
 * seek: 0 on success. size: the length in bytes (the decoder needs it, for the
 * seek table and the tags at the end). */
typedef struct fa_ape_io {
    int64_t (*read)(void* user, void* buf, size_t n);
    int (*seek)(void* user, int64_t offset);
    int64_t (*tell)(void* user);
    int64_t size;
    void* user;
} fa_ape_io;

typedef struct fa_ape_info {
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t bits;          /* 8, 16, 24 or 32 */
    int is_float;           /* 32-bit IEEE float samples (Monkey's Audio 10+) */
    uint32_t block_align;   /* bytes of one frame of PCM from fa_ape_read */
    int64_t total_blocks;   /* frames */
    int32_t file_version;   /* e.g. 3990 */
    int32_t compression;    /* 1000 fast ... 5000 insane */
    uint32_t bitrate;       /* average, bits per second */
    int64_t audio_bytes;    /* coded audio (the file without headers and tags) */
} fa_ape_info;

typedef struct fa_ape fa_ape;

/* NULL with *error: 1 not Monkey's Audio, 2 a version this SDK refuses,
 * 3 out of memory, 4 damaged or unreadable. `io` is copied; its user must live
 * until fa_ape_close. */
fa_ape* fa_ape_open(const fa_ape_io* io, fa_ape_info* out_info, int* error);
/* Little-endian interleaved PCM of `info.block_align` bytes a frame: unsigned
 * 8-bit, signed 16/24/32-bit, or float when is_float. *got = frames written;
 * 0 at the end. Returns 0, or nonzero on a decoding error (the SDK then gives
 * silence for the damaged frame, which *got counts). */
int fa_ape_read(fa_ape* a, void* out, int64_t frames, int64_t* got);
int fa_ape_seek(fa_ape* a, int64_t frame);   /* 0 on success */
void fa_ape_close(fa_ape* a);

#ifdef __cplusplus
}
#endif

#endif
