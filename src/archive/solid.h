/* A compressed stream read as one solid run of bytes (solid.c): gzip, bzip2 or xz around a tar
 * or a single file. Bytes [from, to) of the decoded stream are delivered in order. The first run
 * over the whole stream records how to start again near any offset: DEFLATE restart points
 * (gzip), the blocks (bzip2, xz). */
#ifndef FA_SOLID_H
#define FA_SOLID_H

#include "archive/codec.h"

enum { FA_FILTER_NONE = 0, FA_FILTER_GZIP = 1, FA_FILTER_BZIP2 = 2, FA_FILTER_XZ = 3 };

/* The most a run decodes before it gives up (a decompression bomb with no end). */
#define FA_SOLID_MAX_OUT ((uint64_t)64 << 30)

typedef struct fa_solid fa_solid;

/* Parses what is cheap to parse: the gzip header, the xz index. `src` is the whole file (known
 * length), also what the runs read from; the caller keeps it open for the life of the object. */
fa_result fa_solid_open(fa_source* src, int filter, fa_solid** out);
void fa_solid_free(fa_solid* s);

/* Delivers decoded bytes [from, to) of the stream (to UINT64_MAX: to the end) into sink; a sink
 * that returns non-zero ends the run quietly. A run from 0 to the end that is not stopped also
 * checks the stream's own checksums and learns its size and restart points. */
fa_result fa_solid_run(fa_solid* s, fa_source* src, uint64_t from, uint64_t to, fa_sink_fn sink, void* ctx,
                       const volatile int32_t* cancel);

/* The decoded size: exact once known (UINT64_MAX before). gzip says it in its trailer (modulo 4 GB:
 * a hint, checked by the first full run), xz in its index. */
uint64_t fa_solid_size_hint(const fa_solid* s);
uint64_t fa_solid_size(const fa_solid* s);   /* only after a full run */
int fa_solid_complete(const fa_solid* s);    /* a full run has finished */
/* A gzip file's data: where the DEFLATE stream starts, its length, the trailer's CRC-32 and size. */
void fa_solid_gzip(const fa_solid* s, uint64_t* data_offset, uint64_t* data_len, uint32_t* crc, uint32_t* isize);
/* The name and time the file says it had (gzip), else NULL / INT64_MIN. */
const char* fa_solid_name(const fa_solid* s);
int64_t fa_solid_mtime_ns(const fa_solid* s);
/* How much of the stream must be decoded to reach `offset`: 0 when a restart point or block starts
 * there (a measure for the public solid_offset). */
uint64_t fa_solid_cost(const fa_solid* s, uint64_t offset);

#endif
