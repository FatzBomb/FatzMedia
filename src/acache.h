/* The recently-unpacked cache of the engine (acache.c): what opening an archive song costs when the same archive or
 * song was opened a moment ago. Two single slots, each kept for archive_cache_seconds (30) after its last use and
 * each at most half the memory limit, so together they stay within it:
 *  - the archive itself with its decoder where the last read ended (7z folders, tar.gz/.bz2/.xz and the like), so a
 *    playlist that opens one entry after the other decodes the solid block once;
 *  - the last compressed entry that opened and played, as its unpacked bytes, so preview, play and replay cost
 *    a memory copy instead of an unpack. */
#ifndef FA_ACACHE_H
#define FA_ACACHE_H

#include "fa_internal.h"

typedef struct fa_acache fa_acache;
struct fa_archive;

/* Unpacked bytes shared between the cache and the streams playing them. Takes over `data` (malloc'ed). */
typedef struct fa_blob {
    fa_atomic32 refs;
    uint8_t* data;
    size_t size;
} fa_blob;
fa_blob* fa_blob_new(uint8_t* data, size_t size);   /* one reference; NULL when out of memory (data is then freed) */
fa_blob* fa_blob_retain(fa_blob* b);
void fa_blob_release(fa_blob* b);                    /* usable as a fa_source release callback */

fa_acache* fa_acache_new(uint32_t seconds, uint64_t memory_limit);   /* seconds 0 = 30; limit in bytes */
void fa_acache_free(fa_acache* c);
/* 1 when either slot holds something (tests). */
int fa_acache_is_empty(const fa_acache* c);
/* Drops what has been unused for longer than its time to live; cheap when nothing is held. */
void fa_acache_expire(fa_acache* c);

/* The archive's identity from the open file (device, file id, last write) and path; NULL when it has none
 * (memory, callbacks, a window or shared temp file, a filesystem that gives no identity). malloc'ed. */
char* fa_acache_key(const fa_source* src, const char* path);

struct fa_archive* fa_acache_get_archive(fa_acache* c, const char* key);   /* a new reference or NULL */
/* Keeps (a reference to) the archive for the time to live, replacing another; not kept when what its decoder holds
 * is over half the memory limit. */
void fa_acache_put_archive(fa_acache* c, const char* key, struct fa_archive* a);
fa_blob* fa_acache_get_entry(fa_acache* c, const char* key, const char* entry_name);   /* a new reference or NULL */
int fa_acache_entry_fits(const fa_acache* c, size_t size);
void fa_acache_put_entry(fa_acache* c, const char* key, const char* entry_name, fa_blob* b);

#endif
