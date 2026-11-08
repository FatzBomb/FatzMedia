/* 7z archives (sevenz.c): the container (signature header, plain or LZMA-packed header, folders,
 * substreams, file names and times) and the decoders for what music archives use: Copy, LZMA and
 * LZMA2 in a folder of one coder. Other methods (PPMd, BCJ and other filters, BCJ2, Deflate,
 * BZip2) are listed but not readable; AES is listed as encrypted.
 *
 * A folder is one solid run of bytes; its files are slices of it. Three ways to read one:
 *  - Copy: the file is stored in the archive (method 0, plays in place).
 *  - One file in the folder: decoded straight into the buffer the caller gets (dictionary and output are
 *    the same block), nothing kept.
 *  - Several files: the decoder stays where the last read ended (the cursor), so entries read in archive
 *    order cost one pass in total. A folder that fits the memory limit decodes into one growing buffer that
 *    is also the dictionary (nothing is copied twice); a bigger one decodes through a window of the
 *    dictionary's size. A cursor that is behind a LZMA2 dictionary reset skips ahead to it, and a read
 *    that starts before the cursor restarts at the nearest reset before it. */
#ifndef FA_SEVENZ_H
#define FA_SEVENZ_H

#include "archive/codec.h"
#include "cdg.h"

/* fa_zip_entry.method of an entry in a 7z folder this build decodes, and of one it cannot. */
#define FA_AM_7Z 1001
#define FA_AM_7Z_BAD 1002

/* Entries before the archive is refused as hostile, and the biggest header read into memory. */
#define FA_7Z_MAX_ENTRIES (1u << 18)
#define FA_7Z_MAX_HEADER ((uint64_t)256 << 20)

typedef struct fa_7z fa_7z;

/* Lists the 7z archive in `src` (seekable, known length). On FA_OK *entries (count fa_zip_entry:
 * header_offset = folder, data_offset = offset in the folder, or in the source for a stored file) and
 * *names (UTF-8, '/'-separated) are malloc'ed for the caller. FA_ERROR_PROTECTED_CONTENT when the header
 * itself is encrypted; FA_ERROR_TOO_LARGE for a hostile size or count. */
fa_result fa_7z_open(fa_source* src, uint64_t memory_limit, const volatile int32_t* cancel, fa_7z** out,
                     fa_zip_entry** entries, char*** names, uint32_t* count);
void fa_7z_free(fa_7z* z);

/* fa_archive_method of the entry's folder. */
int32_t fa_7z_public_method(const fa_7z* z, const fa_zip_entry* e);
/* 1 when other files are in the entry's folder. */
int fa_7z_is_solid(const fa_7z* z, const fa_zip_entry* e);
/* Packed size of the folder when the entry is alone in it, else -1. */
int64_t fa_7z_packed_size(const fa_7z* z, const fa_zip_entry* e);
/* Bytes that must be decoded before the entry's first byte when the archive is opened (the offset in the
 * folder, less what a LZMA2 dictionary reset before it saves). */
uint64_t fa_7z_cost(const fa_7z* z, const fa_zip_entry* e);
/* 1 when the entry is alone in its folder and the folder is decoded in one go: it can be unpacked
 * with fa_7z_unpack_alone on another source while the archive is used for something else. */
int fa_7z_independent(const fa_7z* z, const fa_zip_entry* e);

/* Delivers bytes [from, to) of the entry to sink (a non-zero return stops quietly). `src` is the archive's
 * source; the caller holds whatever lock keeps others off the cursor. */
fa_result fa_7z_read(fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t from, uint64_t to, fa_sink_fn sink, void* ctx,
                     const volatile int32_t* cancel);
/* The whole entry to sink, its checksum verified (FA_ERROR_UNSUPPORTED_FORMAT when it differs). */
fa_result fa_7z_extract(fa_7z* z, fa_source* src, const fa_zip_entry* e, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel);
/* The whole entry in a malloc'ed block (checked); FA_ERROR_TOO_LARGE past `limit`. */
fa_result fa_7z_unpack(fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t limit, const volatile int32_t* cancel, uint8_t** out);
/* The same for an entry that is fa_7z_independent: touches nothing the archive's cursor uses, so a second thread
 * may run it with a source of its own. */
fa_result fa_7z_unpack_alone(const fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t limit, const volatile int32_t* cancel,
                             uint8_t** out);

/* What the kept cursor holds in memory (its buffer or dictionary), for the engine's cache. */
uint64_t fa_7z_held_bytes(const fa_7z* z);
/* Bytes the decoders have produced for this archive so far (tests and tuning). */
uint64_t fa_7z_decoded(const fa_7z* z);

#endif
