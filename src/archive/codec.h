/* Decompression for archives (codec.c): bzip2 and xz/LZMA from the vendored decoders
 * (third_party/bzip2, third_party/lzma), the block layouts that let both be decoded on
 * several cores or from the middle, and the pool that runs such blocks in parallel.
 * Nothing here knows about archives or the engine. */
#ifndef FA_CODEC_H
#define FA_CODEC_H

#include "fa_internal.h"

/* Receives decoded bytes in order. Non-zero stops the decoder (not an error). */
typedef int (*fa_sink_fn)(void* ctx, const uint8_t* data, size_t n);

/* Compressed input: `left` bytes in memory at `mem`, or (mem NULL) read from `src` at its
 * current position. */
typedef struct fa_cin {
    const uint8_t* mem;
    fa_source* src;
    uint64_t left;
} fa_cin;

enum { FA_CODEC_BZIP2 = 1, FA_CODEC_LZMA_ZIP = 2, FA_CODEC_XZ = 3 };

/* The most memory one decoder is allowed to ask for its dictionary; a header that wants
 * more is refused with FA_ERROR_TOO_LARGE. */
#define FA_CODEC_MAX_DICT ((uint64_t)1 << 30)
/* Compressed input read into memory so blocks can be decoded on several cores. */
#define FA_CODEC_PARALLEL_INPUT_MAX ((uint64_t)96 << 20)
/* Below this the single-threaded decoder is as fast. */
#define FA_CODEC_PARALLEL_MIN ((uint64_t)192 << 10)

/* Decodes `in` into `sink`. `usize` is the exact decoded size when known (LZMA needs it;
 * for the others it is checked), UINT64_MAX when not. *produced is the bytes delivered,
 * *stopped is set when the sink asked to stop. Returns FA_OK when the stream ended
 * properly (or the sink stopped it), FA_ERROR_UNSUPPORTED_FORMAT for damaged data,
 * FA_ERROR_CANCELLED, FA_ERROR_IO, FA_ERROR_OUT_OF_MEMORY, FA_ERROR_TOO_LARGE. */
fa_result fa_codec_to_sink(int codec, fa_cin in, uint64_t usize, fa_sink_fn sink, void* ctx,
                           const volatile int32_t* cancel, uint64_t* produced, int* stopped);
/* Decodes exactly `out_size` bytes into `out` (the whole entry; checked). */
fa_result fa_codec_to_buffer(int codec, fa_cin in, uint8_t* out, size_t out_size, const volatile int32_t* cancel);

/* ---- DEFLATE as a stream (zip.c) ----------------------------------------------------------- */

#define FA_INF_CP_WINDOW 32768
#define FA_INF_CP_MAX 512
/* A place a DEFLATE stream can be restarted: at a block start, with the window of output
 * before it. in_bit and out_offset are from the start of the stream. */
typedef struct fa_infcp {
    uint64_t out_offset, in_bit;
    uint8_t* window;
    uint32_t window_len;
} fa_infcp;
typedef struct fa_infcps {
    fa_infcp* v;
    uint32_t n, cap;
    uint64_t every, next_at;   /* set `every` (and next_at = every) before a run that records */
} fa_infcps;
void fa_infcps_free(fa_infcps* r);

typedef struct fa_inflate_result {
    uint64_t produced;   /* bytes delivered (from the restart point on) */
    uint64_t consumed;   /* input bytes used, the partly used last one included, from the start of the stream */
    uint32_t crc;        /* CRC-32 of the bytes delivered */
    int stopped;
} fa_inflate_result;

/* Inflates raw DEFLATE (Deflate64 with d64) from `in` into sink. usize: the exact output size when
 * known, else UINT64_MAX (the last block ends it). With `resume` the run starts at that
 * checkpoint (in's position is the stream's start) and delivers from resume->out_offset on.
 * `record` collects checkpoints on the way. */
fa_result fa_inflate_stream(fa_cin in, int d64, uint64_t usize, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel,
                            const fa_infcp* resume, fa_infcps* record, fa_inflate_result* res);

/* ---- Parallel blocks -------------------------------------------------------------- */

/* Runs work(i) for i in [0, count) on `threads` threads and hands the results to emit in
 * order on the calling thread; at most `window` blocks are in flight. work allocates *buf
 * with malloc (the pool frees it after emit). Non-zero from emit stops everything. */
typedef struct fa_pjob {
    uint32_t count, threads, window;
    void* ctx;
    fa_result (*work)(void* ctx, uint32_t i, uint8_t** buf, size_t* len);
    int (*emit)(void* ctx, uint32_t i, uint8_t* buf, size_t len);
} fa_pjob;
fa_result fa_parallel_ordered(const fa_pjob* job, const volatile int32_t* cancel, int* stopped);
/* How many threads to use for `blocks` blocks (1 when it is not worth it). */
uint32_t fa_codec_threads(uint32_t blocks);

/* ---- bzip2 blocks ------------------------------------------------------------------ */

typedef struct fa_bzblock {
    uint64_t bit_start, bit_end;   /* in the compressed bytes: from the block magic to the next block or the end of stream */
    uint64_t out_offset, out_size; /* in the decoded bytes; out_size is 0 until the block has been decoded once */
    uint8_t level;                 /* '1'..'9' of the stream header */
} fa_bzblock;

/* Finds the blocks of the bzip2 stream(s) in memory. FA_ERROR_UNSUPPORTED_FORMAT when it
 * does not look like bzip2. *out is malloc'ed. */
fa_result fa_bz_scan(const uint8_t* mem, size_t size, fa_bzblock** out, uint32_t* count);
/* Decodes blocks [first, first + n) in parallel in order into sink and fills in their
 * out_size. */
fa_result fa_bz_decode_blocks(const uint8_t* mem, size_t mem_size, fa_bzblock* blocks, uint32_t first, uint32_t n, uint32_t threads,
                              fa_sink_fn sink, void* ctx, const volatile int32_t* cancel, int* stopped);

/* ---- xz blocks --------------------------------------------------------------------- */

typedef struct fa_xzblock {
    uint64_t in_offset, in_size;   /* the block with its padding and check, in the compressed bytes */
    uint64_t out_offset, out_size;
} fa_xzblock;

/* Reads the index of a single-stream xz file of `size` bytes in `src` (seekable). On
 * FA_OK *blocks is malloc'ed and *flags are the stream flags. FA_ERROR_UNSUPPORTED_FORMAT
 * when there is no usable index (several streams, damage): decode it from the start. */
fa_result fa_xz_index(fa_source* src, uint64_t size, fa_xzblock** blocks, uint32_t* count, uint16_t* flags);
/* Decodes blocks [first, first + n) of the xz file in memory in order into sink. */
fa_result fa_xz_decode_blocks(const uint8_t* mem, const fa_xzblock* blocks, uint16_t flags, uint32_t first, uint32_t n,
                              uint32_t threads, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel, int* stopped);

#endif
