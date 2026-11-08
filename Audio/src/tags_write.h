/* Writing loudness tags (fa_tags_write_loudness): shared pieces of the format
 * writers. A writer reads the original through a fa_source and describes the
 * new file as edits (byte ranges of the original replaced by new bytes, in file
 * order); tags_write.c copies everything else around them. Ogg pages are
 * renumbered, so the Ogg writer writes the whole file itself. */
#ifndef FA_TAGS_WRITE_H
#define FA_TAGS_WRITE_H

#include "fa_internal.h"
#include <stdio.h>

/* The values to write, as text, and which older keys they replace. */
typedef struct fa_tw_item {
    const char* key;                /* upper case; MP4 writes it in lower case */
    char value[40];
} fa_tw_item;

typedef struct fa_tw_set {
    fa_tw_item items[4];
    int count;
    int album;                      /* the album keys are replaced too */
} fa_tw_set;

/* 1 when `key` (n bytes, any case) is a loudness key this write replaces: the
 * track ReplayGain and R128 keys, and the album ones with set->album. */
int fa_tw_replaces(const fa_tw_set* set, const char* key, size_t n);

/* A growing byte buffer; `failed` once an allocation failed. */
typedef struct fa_tw_buf {
    uint8_t* p;
    size_t n, cap;
    int failed;
} fa_tw_buf;

void fa_tw_put(fa_tw_buf* b, const void* p, size_t n);
void fa_tw_u8(fa_tw_buf* b, uint32_t v);
void fa_tw_be32(fa_tw_buf* b, uint32_t v);
void fa_tw_le32(fa_tw_buf* b, uint32_t v);
void fa_tw_zeros(fa_tw_buf* b, size_t n);
void fa_tw_free(fa_tw_buf* b);

/* `n` bytes at `pos` of the source, malloc'ed; NULL if they cannot be read. */
uint8_t* fa_tw_read(fa_source* src, int64_t pos, size_t n);

#define FA_TW_MAX_EDITS 4

typedef struct fa_tw_edit {
    int64_t from, to;               /* the original's bytes [from, to) ... */
    fa_tw_buf data;                 /* ... are replaced by these */
} fa_tw_edit;

typedef struct fa_tw_plan {
    fa_tw_edit edits[FA_TW_MAX_EDITS];
    int count;
    int32_t format;                 /* fa_tag_format written */
} fa_tw_plan;

/* A new edit at the end of the plan (edits are added in file order); NULL when full. */
fa_tw_edit* fa_tw_add(fa_tw_plan* plan, int64_t from, int64_t to);
void fa_tw_plan_free(fa_tw_plan* plan);

/* A Vorbis comment block (vendor, count, items) without the loudness keys the set
 * replaces, plus the set's items. `old` may be NULL (vendor "FatzAudio"); `tail`
 * gets the bytes after the items (Opus allows binary data there), may be NULL. */
fa_result fa_tw_vorbis_comments(const uint8_t* old, size_t n, const fa_tw_set* set, fa_tw_buf* out, size_t* tail);

/* The writers (tags_write_fmt.c, tags_write_mp4.c, tags_write_ogg.c).
 * FA_ERROR_UNSUPPORTED_FORMAT leaves the file alone. */
fa_result fa_tw_plan_id3v2(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan);   /* + an APEv2 tag that exists */
fa_result fa_tw_plan_flac(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan);
fa_result fa_tw_plan_ape(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan, int create);
fa_result fa_tw_plan_mp4(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan);
/* Ogg Vorbis or Opus: `opus` tells which (the set holds the matching keys). */
fa_result fa_tw_write_ogg(fa_source* src, const fa_tw_set* set, int opus, FILE* out);
/* 1 = Opus, 0 = Vorbis, -1 = another Ogg codec. */
int fa_tw_ogg_kind(fa_source* src);

#endif
