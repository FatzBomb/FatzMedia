/* tar archives (tar.c): ustar, GNU and pax headers, long names. Read as a list of regular
 * files, either by seeking from header to header or as a stream of decoded bytes. */
#ifndef FA_TAR_H
#define FA_TAR_H

#include "cdg.h"

/* Entries a list holds before the archive is refused as hostile. */
#define FA_TAR_MAX_ENTRIES (1u << 18)

typedef struct fa_tarlist {
    fa_zip_entry* e;          /* regular files: method 0, data_offset and size */
    char** names;             /* UTF-8, malloc'ed, one per entry */
    uint32_t n, cap;
    int too_many;
    int oom;
    /* Header state between records. */
    char* long_name;          /* GNU 'L' record for the next entry */
    char* pax_path;           /* pax 'path' for the next entry */
    uint64_t pax_size;
    int has_pax_size;
    int64_t pax_mtime_ns;     /* INT64_MIN: none */
} fa_tarlist;

void fa_tarlist_free(fa_tarlist* t);

/* 1 when the 512 bytes at hdr are a tar header: a valid checksum and a name. */
int fa_tar_valid_header(const uint8_t* hdr);

enum { FA_TAR_ENTRY, FA_TAR_META, FA_TAR_SKIP, FA_TAR_END, FA_TAR_BAD };
/* Takes the header block that sits just before `data_pos`. FA_TAR_ENTRY: a regular file was
 * added to the list; its *data_len bytes follow. FA_TAR_META: *data_len bytes of a long-name or
 * pax record follow, to be given to fa_tar_meta with *meta_kind. FA_TAR_SKIP: *data_len bytes
 * of something else follow. FA_TAR_END: a block of zeros. */
int fa_tar_header(fa_tarlist* t, const uint8_t* hdr, uint64_t data_pos, uint64_t* data_len, int* meta_kind);
void fa_tar_meta(fa_tarlist* t, int meta_kind, const uint8_t* data, size_t n);

/* Lists a tar of `size` bytes in a seekable source from its start: reads one header per entry
 * and seeks over the data. */
fa_result fa_tar_list_source(fa_source* src, uint64_t size, fa_tarlist* t, const volatile int32_t* cancel);

/* ---- As a stream ------------------------------------------------------------------------ */

/* Bytes of each entry's start kept for telling audio from the rest (like fa_zip_extract_prefix). */
#define FA_TAR_PREFIX_BYTES 65536
#define FA_TAR_PREFIX_BUDGET ((size_t)4 << 20)

typedef struct fa_tarstream {
    fa_tarlist list;
    uint8_t** prefix;         /* per entry, FA_TAR_PREFIX_BYTES or the entry's size if less, or NULL */
    uint32_t* prefix_len;
    uint32_t prefix_cap;
    size_t prefix_used;
    int state;
    uint8_t block[512];
    size_t have;
    uint64_t pos;             /* decoded bytes seen */
    uint64_t left;            /* bytes of the current record or entry still to come */
    uint64_t pad;             /* padding to the 512 boundary after it */
    int meta_kind;
    uint8_t* meta;
    size_t meta_n;
    int cur;                  /* entry index whose data is arriving, -1 */
    int ended;                /* two zero blocks, or a bad header */
    int bad;
} fa_tarstream;

void fa_tarstream_init(fa_tarstream* s);
void fa_tarstream_free(fa_tarstream* s);   /* frees the prefixes; the list is the caller's to take (list.e/names) */
/* A fa_sink_fn: non-zero once the archive has ended. */
int fa_tarstream_feed(void* ctx, const uint8_t* data, size_t n);
void fa_tarstream_finish(fa_tarstream* s);

#endif
