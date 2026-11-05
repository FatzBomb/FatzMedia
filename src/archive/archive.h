/* The archive layer: one place that lists an archive, tells which entry is the song
 * and which its graphics (the pairing rules of the "Archives" section of the public
 * header), and opens entries as sources. ZIP is the first backend (zip.c); others plug
 * in through the `format` switches in archive.c. This directory includes nothing of
 * the engine or the decoders but fa_source and the one format probe the pairing needs,
 * so it could be built as a library of its own. */
#ifndef FA_ARCHIVE_H
#define FA_ARCHIVE_H

#include "cdg.h"
#include "archive/solid.h"
#include "archive/tar.h"
#include "archive/sevenz.h"

/* ---- Names (names.c) ------------------------------------------------------- */

uint32_t fa_utf8_next(const char** p);
int fa_name_is_utf8(const char* s);
/* UTF-8, '/'-separated, no leading "./" (malloc'ed). `force_utf8` skips the CP437 guess. */
char* fa_name_to_utf8(const char* in, int force_utf8);
uint32_t fa_fold(uint32_t codepoint);
int fa_name_equal_fold(const char* a, const char* b);
const char* fa_name_ext(const char* name);
const char* fa_name_base(const char* name);
int32_t fa_name_kind(const char* name);
int fa_name_ignored(const char* name);
int fa_name_same_stem(const char* audio_name, const char* cdg_name);

/* ---- The archive ------------------------------------------------------------ */

#define FA_ARCHIVE_DEFAULT_MEMORY_LIMIT FA_ZIP_DEFAULT_MEMORY_LIMIT
/* Candidates returned by fa_archive_choices: enough for an open to try in turn. */
#define FA_ARCHIVE_MAX_CHOICES 8
/* Entries whose first bytes the pairing reads to tell audio from the rest. */
#define FA_ARCHIVE_MAX_PROBES 256

struct fa_archive {
    fa_atomic32 refs;
    fa_mutex lock;              /* one reader of `*srcp` at a time */
    fa_source own;              /* the archive's source when it owns one */
    fa_source* srcp;            /* &own, or a source the caller keeps (fa_archive_borrow) */
    int owns_source;
    int32_t format;
    int32_t filter;             /* FA_FILTER_*: gzip, bzip2 or xz around the tar (format TAR) or file (GZIP, BZIP2, XZ) */
    uint32_t count;
    fa_zip_entry* z;            /* the entries as the backends read them: count entries */
    fa_7z* sz;                  /* a 7z archive: entries are read through it (method FA_AM_7Z) or stored (0) */
    fa_solid* sol;              /* entries are read out of a compressed stream (method FA_AM_SOLID), not stored */
    uint8_t** prefixes;         /* solid: the first bytes of each entry seen while listing (or NULL) */
    uint32_t* prefix_lens;
    int detached;               /* the source it was made from is no longer read: the archive holds a decoded copy */
    fa_archive_entry* pub;      /* the public view of each */
    char** names;               /* UTF-8, owned; pub[i].name points here */
    uint8_t* audio;             /* per entry: 0 not probed, 1 audio, 2 not audio */
    uint32_t prefix_cap;
    int64_t length;             /* of the file or memory the archive was made from */
    uint32_t probes;
    char* path;                 /* UTF-8 path of the archive when it is a file, else NULL */
    uint64_t memory_limit;
    char* temp_dir;
    const volatile int32_t* cancel;
    int encrypted_candidates;   /* entries that could be songs but are encrypted */
};

/* Entries read out of a compressed stream (tar.gz and friends too large to hold decoded) use this
 * as fa_zip_entry.method; the data_offset is in the decoded stream. */
#define FA_AM_SOLID 1000
/* 1 when `data` starts like an archive this build reads (ZIP, tar, gzip, bzip2, xz, 7z). */
int fa_archive_openable(const uint8_t* data, size_t size);
/* 1 when entry `i` is stored plain in the archive's source (it plays in place). */
int fa_archive_is_stored(const fa_archive* a, uint32_t i);

/* Borrows `*src` (the caller keeps it, open and seekable, until fa_archive_release), lists it. */
fa_result fa_archive_borrow(fa_source* src, const fa_archive_options* options, fa_archive** out);
/* Takes over the source `*src` (zeroed on success; left alone on failure). */
fa_result fa_archive_adopt(fa_source* src, const char* path, const fa_archive_options* options, fa_archive** out);
fa_archive* fa_archive_retain(fa_archive* a);
void fa_archive_release(fa_archive* a);   /* the last release frees it (fa_archive_close is the same) */

/* A song and its graphics: indexes into the archive. audio is the .cdg's own index and
 * cdg -1 for graphics alone. */
typedef struct fa_archive_choice {
    uint32_t audio;
    int32_t cdg;
    int graphics_only;
} fa_archive_choice;

/* The entries an open tries in turn, best first, for `entry_name` (NULL: the pairing
 * rules without a name). Returns how many were put in `out` (at most `cap`); *error is
 * why none: FA_ERROR_NOT_FOUND, FA_ERROR_INVALID_ARG (ambiguous name),
 * FA_ERROR_PROTECTED_CONTENT, FA_ERROR_UNSUPPORTED_FORMAT (no song, no graphics). Takes the
 * archive's lock. */
uint32_t fa_archive_choices(fa_archive* a, const char* entry_name, fa_archive_choice* out, uint32_t cap, fa_result* error);
fa_result fa_archive_lookup(const fa_archive* a, const char* name, uint32_t* out_index);

/* The first FA_ZIP_PROBE_BYTES of entry `i`, unpacked or not, into buf: *got bytes. */
fa_result fa_archive_prefix(fa_archive* a, uint32_t i, uint8_t* buf, size_t cap, size_t* got);
/* Where a stored entry starts in the archive's source (FA_ERROR_UNSUPPORTED_FORMAT when
 * it is compressed). */
fa_result fa_archive_entry_offset(fa_archive* a, uint32_t i, int64_t* out_offset);
/* Unpacks entry `i` into a malloc'ed block (CRC checked); FA_ERROR_TOO_LARGE past `limit`. */
fa_result fa_archive_unpack(fa_archive* a, uint32_t i, uint64_t limit, const volatile int32_t* cancel, uint8_t** out);
fa_result fa_archive_unpack_file(fa_archive* a, uint32_t i, const volatile int32_t* cancel, FILE* f);

/* Visits the songs of an archive in the order an open tries them, as the tags and the
 * fingerprint read them: visit(ctx, entry_source, name) with a window on the archive
 * for a stored entry or an unpacked copy (memory up to the archive's limit; larger ones
 * are skipped) and returns 1 to stop. Returns 1 when visit stopped. *out_has_cdg is 1 when
 * a .cdg goes with the song. `entry_name` NULL = the pairing rules. */
int fa_archive_visit(fa_archive* a, const char* entry_name, int (*visit)(void* ctx, fa_source* entry, const char* name),
                     void* ctx, int* out_has_cdg, fa_result* error);

#endif
