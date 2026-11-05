/* The archive layer (archive.h) and the public archive calls. */
#include "archive/archive.h"
#include <stdlib.h>
#include <string.h>

/* ---- Detection and options -------------------------------------------------- */

FA_API int32_t FA_CALL fa_archive_detect(const void* data, size_t size)
{
    const uint8_t* p = (const uint8_t*)data;
    if (!p || size < 4) return FA_ARCHIVE_NONE;
    if (fa_zip_probe(p, size)) return FA_ARCHIVE_ZIP;
    if (size >= 7 && memcmp(p, "Rar!\x1A\x07", 6) == 0 && (p[6] == 0 || (size >= 8 && p[6] == 1 && p[7] == 0))) return FA_ARCHIVE_RAR;
    if (size >= 6 && memcmp(p, "7z\xBC\xAF\x27\x1C", 6) == 0) return FA_ARCHIVE_7Z;
    if (size >= 6 && memcmp(p, "\xFD" "7zXZ\0", 6) == 0) return FA_ARCHIVE_XZ;
    if (size >= 3 && memcmp(p, "BZh", 3) == 0 && size >= 4 && p[3] >= '1' && p[3] <= '9') return FA_ARCHIVE_BZIP2;
    if (size >= 3 && p[0] == 0x1F && p[1] == 0x8B && p[2] == 8) return FA_ARCHIVE_GZIP;
    if ((size >= 262 && memcmp(p + 257, "ustar", 5) == 0) || (size >= 512 && fa_tar_valid_header(p))) return FA_ARCHIVE_TAR;
    return FA_ARCHIVE_NONE;
}

FA_API void FA_CALL fa_archive_options_init(fa_archive_options* options)
{
    if (!options) return;
    memset(options, 0, sizeof(*options));
    options->struct_size = (uint32_t)sizeof(*options);
}

/* ---- Building -------------------------------------------------------------- */

static const int64_t no_time = INT64_MIN;

/* Days since 1970-01-01 of a civil date (Howard Hinnant's algorithm). */
static int64_t days_from_civil(int64_t y, int m, int d)
{
    int64_t era, yoe, doy, doe;
    y -= m <= 2;
    era = (y >= 0 ? y : y - 399) / 400;
    yoe = y - era * 400;
    doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

/* A DOS date and time are the archiver's local time; without a time zone they read as UTC. */
static int64_t dos_to_ns(uint16_t date, uint16_t time)
{
    int y = 1980 + (date >> 9), m = (date >> 5) & 15, d = date & 31;
    int hh = time >> 11, mm = (time >> 5) & 63, ss = (time & 31) * 2;
    if (date == 0 || m < 1 || m > 12 || d < 1 || hh > 23 || mm > 59 || ss > 59) return no_time;
    return (days_from_civil(y, m, d) * 86400 + hh * 3600 + mm * 60 + ss) * 1000000000ll;
}

static int32_t zip_method_public(uint32_t m)
{
    switch (m) {
    case 0: return FA_METHOD_STORED;
    case 8: return FA_METHOD_DEFLATE;
    case 9: return FA_METHOD_DEFLATE64;
    case 12: return FA_METHOD_BZIP2;
    case 14: return FA_METHOD_LZMA;
    case 95: return FA_METHOD_LZMA2;
    default: return FA_METHOD_OTHER;
    }
}

static int32_t filter_method_public(int32_t filter)
{
    return filter == FA_FILTER_GZIP ? FA_METHOD_DEFLATE : filter == FA_FILTER_BZIP2 ? FA_METHOD_BZIP2 : FA_METHOD_LZMA2;
}

static void archive_free(fa_archive* a)
{
    uint32_t i;
    if (a->names) for (i = 0; i < a->count; i++) free(a->names[i]);
    free(a->names);
    free(a->pub);
    free(a->z);
    free(a->audio);
    free(a->path);
    free(a->temp_dir);
    if (a->prefixes) for (i = 0; i < a->prefix_cap; i++) free(a->prefixes[i]);
    free(a->prefixes);
    free(a->prefix_lens);
    fa_solid_free(a->sol);
    fa_7z_free(a->sz);
    if (a->owns_source) fa_source_close(&a->own);
    fa_mutex_destroy(&a->lock);
    free(a);
}

/* ---- Listing, one backend per format ------------------------------------------------------- */

static fa_result list_zip(fa_archive* a, fa_source* src)
{
    uint32_t i;
    fa_result r = fa_zip_list(src, &a->z, &a->count);
    if (r != FA_OK) return r;
    a->names = (char**)calloc(a->count ? a->count : 1, sizeof(*a->names));
    if (!a->names) return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < a->count; i++) {
        a->names[i] = fa_name_to_utf8(a->z[i].name, (a->z[i].flags & 0x800) != 0);
        if (!a->names[i]) return FA_ERROR_OUT_OF_MEMORY;
    }
    return FA_OK;
}

static fa_result list_7z(fa_archive* a, fa_source* src)
{
    return fa_7z_open(src, a->memory_limit, a->cancel, &a->sz, &a->z, &a->names, &a->count);
}

static fa_result take_tarlist(fa_archive* a, fa_tarlist* t)
{
    if (t->oom) { fa_tarlist_free(t); return FA_ERROR_OUT_OF_MEMORY; }
    if (t->too_many) { fa_tarlist_free(t); return FA_ERROR_TOO_LARGE; }
    a->z = t->e;
    a->names = t->names;
    a->count = t->n;
    t->e = NULL;
    t->names = NULL;
    t->n = t->cap = 0;
    if (!a->z) {
        /* No file in it: an empty archive still needs arrays that can be freed. */
        a->z = (fa_zip_entry*)calloc(1, sizeof(*a->z));
        a->names = (char**)calloc(1, sizeof(*a->names));
        if (!a->z || !a->names) { fa_tarlist_free(t); return FA_ERROR_OUT_OF_MEMORY; }
    }
    fa_tarlist_free(t);
    return FA_OK;
}

/* A single compressed file as an archive of one entry. `z` is filled by the caller. */
static fa_result one_entry(fa_archive* a, const fa_zip_entry* z, char* name)
{
    a->z = (fa_zip_entry*)calloc(1, sizeof(*a->z));
    a->names = (char**)calloc(1, sizeof(*a->names));
    if (!a->z || !a->names) { free(name); return FA_ERROR_OUT_OF_MEMORY; }
    *a->z = *z;
    a->names[0] = name;
    a->count = 1;
    return FA_OK;
}

/* The name of the file inside a .gz/.bz2/.xz: what gzip stored, else the archive's name without the
 * compression suffix, else "data". */
static char* single_name(const fa_solid* sol, const char* path, int32_t filter)
{
    const char* stored = sol ? fa_solid_name(sol) : NULL;
    const char* base;
    size_t n;
    if (stored && *stored) {
        base = fa_name_base(stored);
        if (*base) return fa_strdup(base);
    }
    if (path) {
        const char* suffix = filter == FA_FILTER_GZIP ? ".gz" : filter == FA_FILTER_BZIP2 ? ".bz2" : ".xz";
        size_t sl = strlen(suffix);
        base = fa_name_base(path);
        n = strlen(base);
        if (n > sl && fa_name_equal_fold(base + n - sl, suffix)) {
            char* name = (char*)malloc(n - sl + 1);
            if (!name) return NULL;
            memcpy(name, base, n - sl);
            name[n - sl] = 0;
            return name;
        }
        if (n) return fa_strdup(base);
    }
    return fa_strdup("data");
}

typedef struct growbuf {
    uint8_t* p;
    size_t n, cap;
    uint64_t limit;
    int over, oom;
} growbuf;

static int grow_put(void* ctx, const uint8_t* data, size_t n)
{
    growbuf* g = (growbuf*)ctx;
    if ((uint64_t)n > g->limit - g->n) { g->over = 1; return 1; }
    if (g->n + n > g->cap) {
        size_t cap = g->cap ? g->cap : ((size_t)1 << 20);
        uint8_t* np;
        while (cap < g->n + n) cap *= 2;
        if ((uint64_t)cap > g->limit) cap = (size_t)g->limit;
        np = (uint8_t*)realloc(g->p, cap);
        if (!np) { g->oom = 1; return 1; }
        g->p = np;
        g->cap = cap;
    }
    memcpy(g->p + g->n, data, n);
    g->n += n;
    return 0;
}

typedef struct peek { uint8_t b[512]; size_t n; } peek;
static int peek_put(void* ctx, const uint8_t* data, size_t n)
{
    peek* p = (peek*)ctx;
    size_t take = n < sizeof(p->b) - p->n ? n : sizeof(p->b) - p->n;
    memcpy(p->b + p->n, data, take);
    p->n += take;
    return p->n >= sizeof(p->b);
}

static int listing_put(void* ctx, const uint8_t* data, size_t n)
{
    fa_tarstream_feed(ctx, data, n);   /* the run goes on to the end of the stream so its checks are made */
    return 0;
}

/* Counts a single file's bytes and keeps its start, for a stream too large to hold. */
typedef struct rawcount { uint64_t n; uint8_t* head; size_t head_len; } rawcount;
static int raw_put(void* ctx, const uint8_t* data, size_t n)
{
    rawcount* r = (rawcount*)ctx;
    if (r->head && r->head_len < FA_TAR_PREFIX_BYTES) {
        size_t take = n < FA_TAR_PREFIX_BYTES - r->head_len ? n : FA_TAR_PREFIX_BYTES - r->head_len;
        memcpy(r->head + r->head_len, data, take);
        r->head_len += take;
    }
    r->n += n;
    return 0;
}

/* gzip, bzip2 or xz around a tar or one file. */
/* Compressed bytes from which a DEFLATE stream may expand past 4 GB (at most about 1032 to 1). */
#define GZ_SIZE_MAY_WRAP ((uint64_t)4162000)
static int drop_put(void* ctx, const uint8_t* d, size_t n) { (void)ctx; (void)d; (void)n; return 0; }

static fa_result make_filtered(fa_archive* a, fa_source* src, int owns, int32_t filter, const char* path)
{
    peek pk;
    fa_result r;
    int is_tar;
    uint64_t hint;
    growbuf g;
    memset(&pk, 0, sizeof(pk));
    memset(&g, 0, sizeof(g));
    r = fa_solid_open(src, filter, &a->sol);
    if (r != FA_OK) return r;
    a->filter = filter;
    r = fa_solid_run(a->sol, src, 0, 512, peek_put, &pk, a->cancel);
    if (r != FA_OK) return r;
    is_tar = pk.n == 512 && fa_tar_valid_header(pk.b);
    if (pk.n == 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    hint = fa_solid_size_hint(a->sol);
    if (!is_tar && filter != FA_FILTER_BZIP2) {
        /* One file in a gzip or xz: its size is in the trailer or the index, so it is read like a ZIP entry,
         * unpacked when asked for. */
        fa_zip_entry z;
        memset(&z, 0, sizeof(z));
        z.atime_ns = z.ctime_ns = INT64_MIN;
        z.mtime_ns = fa_solid_mtime_ns(a->sol);
        if (filter == FA_FILTER_GZIP) {
            uint64_t off, len;
            uint32_t crc, isize;
            fa_solid_gzip(a->sol, &off, &len, &crc, &isize);
            z.method = 8; z.data_offset = (int64_t)off; z.compressed_size = len; z.size = isize; z.crc32 = crc;
            if (len >= GZ_SIZE_MAY_WRAP) {
                /* The trailer holds the size modulo 4 GB and the data might be bigger than that: one pass tells
                 * (and checks the CRC). */
                r = fa_solid_run(a->sol, src, 0, UINT64_MAX, drop_put, NULL, a->cancel);
                if (r != FA_OK) return r;
                z.size = fa_solid_size(a->sol);
                if (z.size == UINT64_MAX) return FA_ERROR_UNSUPPORTED_FORMAT;
            }
        } else {
            if (hint == UINT64_MAX) return FA_ERROR_UNSUPPORTED_FORMAT;   /* several streams: no size */
            z.method = 95; z.data_offset = 0; z.compressed_size = (uint64_t)src->length; z.size = hint; z.flags = FA_ZE_NO_CRC;
        }
        r = one_entry(a, &z, single_name(a->sol, path, filter));
        if (r != FA_OK) return r;
        fa_solid_free(a->sol);
        a->sol = NULL;
        a->format = filter == FA_FILTER_GZIP ? FA_ARCHIVE_GZIP : FA_ARCHIVE_XZ;
        return FA_OK;
    }
    a->format = is_tar ? FA_ARCHIVE_TAR : filter == FA_FILTER_BZIP2 ? FA_ARCHIVE_BZIP2 : FA_ARCHIVE_XZ;
    /* Decode it all into one buffer when it fits the memory limit: the entries are then slices of it and
     * play in place, with nothing unpacked twice. */
    g.limit = a->memory_limit;
    if (hint != UINT64_MAX && hint <= g.limit && hint / 65536 <= (uint64_t)src->length + 1024) {
        g.p = (uint8_t*)malloc(hint ? (size_t)hint : 1);
        if (g.p) g.cap = (size_t)(hint ? hint : 1);
    }
    r = fa_solid_run(a->sol, src, 0, UINT64_MAX, grow_put, &g, a->cancel);
    if (r == FA_OK && !g.over && !g.oom) {
        fa_tarlist list;
        fa_zip_entry z;
        uint8_t* data;
        if (g.n == 0) { free(g.p); return FA_ERROR_UNSUPPORTED_FORMAT; }
        data = (uint8_t*)realloc(g.p, g.n);
        if (!data) data = g.p;
        if (owns) { fa_source_close(src); memset(src, 0, sizeof(*src)); }
        fa_source_open_memory_ref(&a->own, data, g.n);
        a->own.owned = data;
        a->srcp = &a->own;
        a->owns_source = 1;
        a->detached = 1;
        free(a->path);
        a->path = NULL;
        if (is_tar) {
            r = fa_tar_list_source(&a->own, g.n, &list, a->cancel);
            if (r != FA_OK) return r;
            r = take_tarlist(a, &list);
            if (r != FA_OK) return r;
        } else {
            memset(&z, 0, sizeof(z));
            z.method = 0; z.data_offset = 0; z.size = z.compressed_size = g.n; z.flags = FA_ZE_NO_CRC;
            z.mtime_ns = fa_solid_mtime_ns(a->sol);
            z.atime_ns = z.ctime_ns = INT64_MIN;
            r = one_entry(a, &z, single_name(a->sol, path, filter));
            if (r != FA_OK) return r;
        }
        fa_solid_free(a->sol);
        a->sol = NULL;
        return FA_OK;
    }
    free(g.p);
    if (g.oom) return FA_ERROR_OUT_OF_MEMORY;
    if (r != FA_OK) return r;
    /* Too big to hold: list it in one pass without keeping it, and decode again for each entry read. */
    fa_solid_free(a->sol);
    a->sol = NULL;
    r = fa_solid_open(src, filter, &a->sol);
    if (r != FA_OK) return r;
    if (is_tar) {
        fa_tarstream ts;
        uint32_t i;
        fa_tarstream_init(&ts);
        r = fa_solid_run(a->sol, src, 0, UINT64_MAX, listing_put, &ts, a->cancel);
        fa_tarstream_finish(&ts);
        if (r == FA_OK && ts.bad) r = FA_ERROR_UNSUPPORTED_FORMAT;
        if (r != FA_OK) { fa_tarlist_free(&ts.list); fa_tarstream_free(&ts); return r; }
        a->prefixes = ts.prefix;
        a->prefix_lens = ts.prefix_len;
        a->prefix_cap = ts.prefix_cap;
        ts.prefix = NULL;
        ts.prefix_len = NULL;
        ts.prefix_cap = 0;
        fa_tarstream_free(&ts);
        r = take_tarlist(a, &ts.list);
        if (r != FA_OK) return r;
        for (i = 0; i < a->count; i++) {
            a->z[i].method = FA_AM_SOLID;
            a->z[i].compressed_size = 0;
        }
    } else {
        fa_zip_entry z;
        rawcount rc;
        memset(&rc, 0, sizeof(rc));
        rc.head = (uint8_t*)malloc(FA_TAR_PREFIX_BYTES);
        r = fa_solid_run(a->sol, src, 0, UINT64_MAX, raw_put, &rc, a->cancel);
        if (r != FA_OK) { free(rc.head); return r; }
        memset(&z, 0, sizeof(z));
        z.method = FA_AM_SOLID; z.data_offset = 0; z.size = rc.n; z.flags = FA_ZE_NO_CRC;
        z.mtime_ns = fa_solid_mtime_ns(a->sol);
        z.atime_ns = z.ctime_ns = INT64_MIN;
        r = one_entry(a, &z, single_name(a->sol, path, filter));
        if (r != FA_OK) { free(rc.head); return r; }
        a->prefixes = (uint8_t**)calloc(1, sizeof(*a->prefixes));
        a->prefix_lens = (uint32_t*)calloc(1, sizeof(*a->prefix_lens));
        if (!a->prefixes || !a->prefix_lens) { free(rc.head); return FA_ERROR_OUT_OF_MEMORY; }
        a->prefixes[0] = rc.head;
        a->prefix_lens[0] = (uint32_t)rc.head_len;
        a->prefix_cap = 1;
    }
    return FA_OK;
}

static fa_result archive_make(fa_source* src, int owns, const char* path, const fa_archive_options* options, fa_archive** out)
{
    fa_archive* a;
    fa_archive_options o;
    uint32_t i;
    fa_result r;
    const uint8_t* head;
    size_t n;
    int32_t format;
    *out = NULL;
    fa_archive_options_init(&o);
    if (options && fa_read_sized(&o, options, sizeof(o), FA_V1(fa_archive_options)) != FA_OK) return FA_ERROR_INVALID_ARG;
    n = fa_source_peek(src, &head);
    if (n < 4) return src->error != FA_OK ? src->error : FA_ERROR_UNSUPPORTED_FORMAT;
    format = fa_archive_detect(head, n);
    if (format != FA_ARCHIVE_ZIP && format != FA_ARCHIVE_TAR && format != FA_ARCHIVE_GZIP && format != FA_ARCHIVE_BZIP2 &&
        format != FA_ARCHIVE_XZ && format != FA_ARCHIVE_7Z)
        return FA_ERROR_UNSUPPORTED_FORMAT;
    a = (fa_archive*)calloc(1, sizeof(*a));
    if (!a) return FA_ERROR_OUT_OF_MEMORY;
    if (fa_mutex_init(&a->lock) != 0) { free(a); return FA_ERROR_GENERIC; }
    a->refs.v = 1;
    a->format = format;
    a->memory_limit = o.memory_limit_mb ? (uint64_t)o.memory_limit_mb << 20 : FA_ARCHIVE_DEFAULT_MEMORY_LIMIT;
    a->cancel = o.cancel;
    a->srcp = src;
    a->length = src->length;
    if (o.temp_directory && !(a->temp_dir = fa_strdup(o.temp_directory))) { r = FA_ERROR_OUT_OF_MEMORY; goto fail; }
    if (path && !(a->path = fa_strdup(path))) { r = FA_ERROR_OUT_OF_MEMORY; goto fail; }
    if (format == FA_ARCHIVE_ZIP) r = list_zip(a, src);
    else if (format == FA_ARCHIVE_7Z) r = list_7z(a, src);
    else if (format == FA_ARCHIVE_TAR) {
        fa_tarlist list;
        if (src->length < 0) r = FA_ERROR_NOT_SEEKABLE;
        else {
            r = fa_tar_list_source(src, (uint64_t)src->length, &list, a->cancel);
            if (r == FA_OK) r = take_tarlist(a, &list);
        }
    } else {
        int32_t f = format == FA_ARCHIVE_GZIP ? FA_FILTER_GZIP : format == FA_ARCHIVE_BZIP2 ? FA_FILTER_BZIP2 : FA_FILTER_XZ;
        r = make_filtered(a, src, owns, f, path);
    }
    if (r != FA_OK) goto fail;
    a->pub = (fa_archive_entry*)calloc(a->count ? a->count : 1, sizeof(*a->pub));
    a->audio = (uint8_t*)calloc(a->count ? a->count : 1, 1);
    if (!a->pub || !a->audio) { r = FA_ERROR_OUT_OF_MEMORY; goto fail; }
    for (i = 0; i < a->count; i++) {
        const fa_zip_entry* z = &a->z[i];
        fa_archive_entry* e = &a->pub[i];
        int solid_like = a->sol != NULL || a->detached;
        e->struct_size = sizeof(*e);
        e->index = i;
        e->name = a->names[i];
        e->size = (int64_t)(z->size > (uint64_t)INT64_MAX ? (uint64_t)INT64_MAX : z->size);
        e->compressed_size = solid_like ? -1 : (int64_t)(z->compressed_size > (uint64_t)INT64_MAX ? (uint64_t)INT64_MAX : z->compressed_size);
        if (a->filter != FA_FILTER_NONE && a->format != FA_ARCHIVE_ZIP && solid_like) e->method = filter_method_public(a->filter);
        else e->method = z->method == FA_AM_SOLID ? filter_method_public(a->filter) : zip_method_public(z->method);
        e->flags = 0;
        if (!(z->flags & FA_ZE_NO_CRC)) e->flags |= FA_ENTRY_HAS_CRC32;
        if (solid_like) e->flags |= FA_ENTRY_SOLID;
        if (z->flags & 1) e->flags |= FA_ENTRY_ENCRYPTED;
        else if (e->method != FA_METHOD_OTHER) e->flags |= FA_ENTRY_READABLE;
        if (a->sz) {
            e->method = fa_7z_public_method(a->sz, z);
            e->compressed_size = fa_7z_packed_size(a->sz, z);
            if (fa_7z_is_solid(a->sz, z)) e->flags |= FA_ENTRY_SOLID;
            e->flags &= ~(uint32_t)FA_ENTRY_READABLE;
            if (!(z->flags & 1) && (z->method == 0 || z->method == FA_AM_7Z)) e->flags |= FA_ENTRY_READABLE;
            e->solid_offset = (int64_t)fa_7z_cost(a->sz, z);
        }
        e->crc32 = z->crc32;
        e->modified_ns = z->mtime_ns != no_time ? z->mtime_ns : dos_to_ns(z->dos_date, z->dos_time);
        e->created_ns = z->ctime_ns;
        e->accessed_ns = z->atime_ns;
        if (!a->sz) e->solid_offset = a->sol ? (int64_t)fa_solid_cost(a->sol, (uint64_t)z->data_offset) : 0;
        e->kind = fa_name_kind(a->names[i]);
    }
    if (owns && !a->detached) { a->own = *src; a->srcp = &a->own; a->owns_source = 1; memset(src, 0, sizeof(*src)); }
    else if (!a->detached) a->srcp = src;
    *out = a;
    return FA_OK;
fail:
    a->owns_source = a->detached;   /* the decoded copy is ours to free; the source is not */
    archive_free(a);
    return r;
}

int fa_archive_openable(const uint8_t* data, size_t size)
{
    int32_t f = fa_archive_detect(data, size);
    return f == FA_ARCHIVE_ZIP || f == FA_ARCHIVE_TAR || f == FA_ARCHIVE_GZIP || f == FA_ARCHIVE_BZIP2 || f == FA_ARCHIVE_XZ ||
           f == FA_ARCHIVE_7Z;
}

int fa_archive_is_stored(const fa_archive* a, uint32_t i)
{
    return i < a->count && a->z[i].method == 0;
}

fa_result fa_archive_borrow(fa_source* src, const fa_archive_options* options, fa_archive** out)
{
    return archive_make(src, 0, NULL, options, out);
}

fa_result fa_archive_adopt(fa_source* src, const char* path, const fa_archive_options* options, fa_archive** out)
{
    return archive_make(src, 1, path, options, out);
}

fa_archive* fa_archive_retain(fa_archive* a)
{
    if (a) fa_fetch_add32(&a->refs, 1);
    return a;
}

void fa_archive_release(fa_archive* a)
{
    if (a && fa_fetch_add32(&a->refs, -1) == 1) archive_free(a);
}

/* ---- Reading entries (callers hold the lock) ---------------------------------- */

/* ---- Solid entries: read out of the compressed stream --------------------------------------- */

typedef struct collect {
    uint8_t* buf;
    size_t cap, pos;
    FILE* file;
    int io_error;
} collect;

static int collect_put(void* ctx, const uint8_t* data, size_t n)
{
    collect* c = (collect*)ctx;
    if (c->file) {
        if (fwrite(data, 1, n, c->file) != n) { c->io_error = 1; return 1; }
        c->pos += n;
        return 0;
    }
    if (n > c->cap - c->pos) n = c->cap - c->pos;
    memcpy(c->buf + c->pos, data, n);
    c->pos += n;
    return 0;
}

static fa_result solid_read_range(fa_archive* a, uint32_t i, uint64_t want, const volatile int32_t* cancel, collect* c)
{
    const fa_zip_entry* z = &a->z[i];
    fa_result r;
    uint64_t at = (uint64_t)z->data_offset;
    if (want == 0) return FA_OK;
    r = fa_solid_run(a->sol, a->srcp, at, at + want, collect_put, c, cancel);
    if (c->io_error) return FA_ERROR_IO;
    if (r == FA_OK && c->pos != want) r = FA_ERROR_UNSUPPORTED_FORMAT;   /* the stream ended inside the entry */
    return r;
}

static fa_result solid_prefix(fa_archive* a, uint32_t i, uint8_t* buf, size_t cap, size_t* got)
{
    uint64_t want = a->z[i].size < cap ? a->z[i].size : cap;
    collect c;
    fa_result r;
    *got = 0;
    if (a->prefixes && i < a->prefix_cap && a->prefixes[i] && a->prefix_lens[i] >= want) {
        memcpy(buf, a->prefixes[i], (size_t)want);
        *got = (size_t)want;
        return FA_OK;
    }
    memset(&c, 0, sizeof(c));
    c.buf = buf;
    c.cap = (size_t)want;
    r = solid_read_range(a, i, want, NULL, &c);
    if (r == FA_OK) *got = c.pos;
    return r;
}

static fa_result solid_unpack(fa_archive* a, uint32_t i, uint64_t limit, const volatile int32_t* cancel, uint8_t** out)
{
    uint64_t size = a->z[i].size;
    collect c;
    fa_result r;
    *out = NULL;
    if (size > limit || size > (uint64_t)(SIZE_MAX - 1)) return FA_ERROR_TOO_LARGE;
    memset(&c, 0, sizeof(c));
    c.buf = (uint8_t*)malloc(size ? (size_t)size : 1);
    if (!c.buf) return FA_ERROR_OUT_OF_MEMORY;
    c.cap = (size_t)size;
    r = solid_read_range(a, i, size, cancel, &c);
    if (r != FA_OK) { free(c.buf); return r; }
    *out = c.buf;
    return FA_OK;
}

static fa_result prefix_locked(fa_archive* a, uint32_t i, uint8_t* buf, size_t cap, size_t* got)
{
    const fa_zip_entry* z = &a->z[i];
    *got = 0;
    if (z->flags & 1) return FA_ERROR_PROTECTED_CONTENT;
    if (z->method == FA_AM_SOLID) return solid_prefix(a, i, buf, cap, got);
    if (z->method == FA_AM_7Z_BAD) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (z->method == FA_AM_7Z) {
        collect c;
        uint64_t want = z->size < cap ? z->size : cap;
        fa_result r;
        memset(&c, 0, sizeof(c));
        c.buf = buf;
        c.cap = (size_t)want;
        r = fa_7z_read(a->sz, a->srcp, z, 0, want, collect_put, &c, a->cancel);
        if (r == FA_OK) *got = c.pos;
        return r;
    }
    if (z->method == 0) {
        int64_t offset, n;
        fa_result r = fa_zip_entry_offset(a->srcp, z, &offset);
        if (r != FA_OK) return r;
        if (cap > z->size) cap = (size_t)z->size;
        if (fa_source_seek(a->srcp, offset) != FA_OK) return FA_ERROR_IO;
        n = fa_source_read(a->srcp, buf, cap);
        if (n < 0) return (fa_result)n;
        *got = (size_t)n;
        return FA_OK;
    }
    return fa_zip_extract_prefix(a->srcp, z, buf, cap, got);
}

fa_result fa_archive_prefix(fa_archive* a, uint32_t i, uint8_t* buf, size_t cap, size_t* got)
{
    fa_result r;
    if (i >= a->count) return FA_ERROR_INVALID_ARG;
    fa_mutex_lock(&a->lock);
    r = prefix_locked(a, i, buf, cap, got);
    fa_mutex_unlock(&a->lock);
    return r;
}

fa_result fa_archive_entry_offset(fa_archive* a, uint32_t i, int64_t* out_offset)
{
    if (i >= a->count || a->z[i].method != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    return fa_zip_entry_offset(a->srcp, &a->z[i], out_offset);
}

fa_result fa_archive_unpack(fa_archive* a, uint32_t i, uint64_t limit, const volatile int32_t* cancel, uint8_t** out)
{
    if (i >= a->count) return FA_ERROR_INVALID_ARG;
    if (a->z[i].flags & 1) return FA_ERROR_PROTECTED_CONTENT;
    if (a->z[i].method == FA_AM_SOLID) return solid_unpack(a, i, limit, cancel, out);
    if (a->z[i].method == FA_AM_7Z) return fa_7z_unpack(a->sz, a->srcp, &a->z[i], limit, cancel, out);
    if (a->z[i].method == FA_AM_7Z_BAD) return FA_ERROR_UNSUPPORTED_FORMAT;
    return fa_zip_extract(a->srcp, &a->z[i], limit, cancel, out);
}

fa_result fa_archive_unpack_file(fa_archive* a, uint32_t i, const volatile int32_t* cancel, FILE* f)
{
    if (i >= a->count) return FA_ERROR_INVALID_ARG;
    if (a->z[i].flags & 1) return FA_ERROR_PROTECTED_CONTENT;
    if (a->z[i].method == FA_AM_SOLID) {
        collect c;
        fa_result r;
        if (a->z[i].size > FA_ZIP_TEMP_LIMIT) return FA_ERROR_TOO_LARGE;
        memset(&c, 0, sizeof(c));
        c.file = f;
        r = solid_read_range(a, i, a->z[i].size, cancel, &c);
        if (r == FA_OK && fflush(f) != 0) r = FA_ERROR_IO;
        return r;
    }
    if (a->z[i].method == FA_AM_7Z) {
        collect c;
        fa_result r;
        if (a->z[i].size > FA_ZIP_TEMP_LIMIT) return FA_ERROR_TOO_LARGE;
        memset(&c, 0, sizeof(c));
        c.file = f;
        r = fa_7z_extract(a->sz, a->srcp, &a->z[i], collect_put, &c, cancel);
        if (c.io_error) return FA_ERROR_IO;
        if (r == FA_OK && fflush(f) != 0) r = FA_ERROR_IO;
        return r;
    }
    if (a->z[i].method == FA_AM_7Z_BAD) return FA_ERROR_UNSUPPORTED_FORMAT;
    return fa_zip_extract_file(a->srcp, &a->z[i], cancel, f);
}

/* ---- Pairing ---------------------------------------------------------------- */

/* Names that can be a song: not graphics, not a folder file, not empty. */
static int is_candidate(const fa_archive* a, uint32_t i)
{
    const fa_archive_entry* e = &a->pub[i];
    return e->kind != FA_ENTRY_CDG && !fa_name_ignored(e->name) && e->size != 0;
}

/* 1 when entry i is audio: a decoder recognizes its first 64 KB (cached). */
static int is_audio(fa_archive* a, uint32_t i)
{
    uint8_t* head;
    size_t got = 0;
    fa_result r;
    int ok;
    if (a->audio[i]) return a->audio[i] == 1;
    if (a->pub[i].kind == FA_ENTRY_TEXT || a->pub[i].kind == FA_ENTRY_IMAGE || a->probes >= FA_ARCHIVE_MAX_PROBES) {
        a->audio[i] = 2;
        return 0;
    }
    a->probes++;
    head = (uint8_t*)malloc(FA_ZIP_PROBE_BYTES);
    if (!head) { a->audio[i] = 1; return 1; }   /* let the open report the lack of memory */
    r = prefix_locked(a, i, head, FA_ZIP_PROBE_BYTES, &got);
    /* A prefix that cannot be read is left to the open, which says why. */
    ok = r != FA_OK || fa_decoder_recognizes(head, got);
    free(head);
    a->audio[i] = ok ? 1 : 2;
    return ok;
}

fa_result fa_archive_lookup(const fa_archive* a, const char* name, uint32_t* out_index)
{
    uint32_t i, found = 0, hits = 0;
    char* wanted = fa_name_to_utf8(name, 1);
    fa_result r = FA_ERROR_NOT_FOUND;
    if (!wanted) return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < a->count; i++)
        if (strcmp(a->pub[i].name, wanted) == 0) { *out_index = i; free(wanted); return FA_OK; }
    for (i = 0; i < a->count; i++)
        if (fa_name_equal_fold(a->pub[i].name, wanted)) { found = i; hits++; }
    if (hits == 1) { *out_index = found; r = FA_OK; }
    else if (hits > 1) r = FA_ERROR_INVALID_ARG;
    free(wanted);
    return r;
}

/* The pairing rules (public header, "Archives"). Locked. */
uint32_t fa_archive_choices(fa_archive* a, const char* entry_name, fa_archive_choice* out, uint32_t cap, fa_result* error)
{
    uint32_t i, j, n = 0, cdgs = 0, first_cdg = 0, audio_count = 0, start;
    uint8_t* used;
    fa_result dummy;
    if (!error) error = &dummy;
    *error = FA_OK;
    fa_mutex_lock(&a->lock);
    a->encrypted_candidates = 0;
    used = (uint8_t*)calloc(a->count ? a->count : 1, 1);
    if (!used) { *error = FA_ERROR_OUT_OF_MEMORY; goto done; }
    for (i = 0; i < a->count; i++) {
        if (a->pub[i].kind == FA_ENTRY_CDG && a->pub[i].size != 0 && !fa_name_ignored(a->pub[i].name)) {
            if (a->pub[i].flags & FA_ENTRY_ENCRYPTED) {
                a->encrypted_candidates++;   /* graphics we cannot read are not graphics to play */
            } else {
                if (cdgs == 0) first_cdg = i;
                cdgs++;
            }
        } else if (is_candidate(a, i) && (a->pub[i].flags & FA_ENTRY_ENCRYPTED)) {
            a->encrypted_candidates++;
        }
    }
    if (entry_name) {
        uint32_t idx;
        fa_result r = fa_archive_lookup(a, entry_name, &idx);
        if (r != FA_OK) { *error = r; goto done; }
        if (a->pub[idx].flags & FA_ENTRY_ENCRYPTED) { *error = FA_ERROR_PROTECTED_CONTENT; goto done; }
        if (a->pub[idx].kind == FA_ENTRY_CDG) {
            /* Graphics named: their song comes with them, else they play alone. */
            for (i = 0; i < a->count; i++) {
                if (!is_candidate(a, i) || (a->pub[i].flags & FA_ENTRY_ENCRYPTED) || !fa_name_same_stem(a->pub[i].name, a->pub[idx].name)) continue;
                if (!is_audio(a, i)) continue;
                out[n].audio = i; out[n].cdg = (int32_t)idx; out[n].graphics_only = 0;
                n++;
                break;
            }
            if (n < cap) {
                out[n].audio = idx; out[n].cdg = -1; out[n].graphics_only = 1;
                n++;
            }
            goto done;
        }
        out[0].audio = idx;
        out[0].cdg = -1;
        out[0].graphics_only = 0;
        for (j = 0; j < a->count; j++)
            if (a->pub[j].kind == FA_ENTRY_CDG && !fa_name_ignored(a->pub[j].name) && a->pub[j].size != 0 &&
                !(a->pub[j].flags & FA_ENTRY_ENCRYPTED) && fa_name_same_stem(a->pub[idx].name, a->pub[j].name)) {
                out[0].cdg = (int32_t)j;
                break;
            }
        if (out[0].cdg < 0 && cdgs == 1) {
            /* One song and one .cdg: the pair, whatever they are called. */
            int others = 0;
            for (i = 0; i < a->count && !others; i++)
                if (i != idx && is_candidate(a, i) && !(a->pub[i].flags & FA_ENTRY_ENCRYPTED) && is_audio(a, i)) others = 1;
            if (!others) out[0].cdg = (int32_t)first_cdg;
        }
        n = 1;
        goto done;
    }

    /* Songs that have a same-name .cdg first, then the others, each in archive order. */
    start = n;
    for (i = 0; i < a->count && n < cap; i++) {
        if (!is_candidate(a, i) || (a->pub[i].flags & FA_ENTRY_ENCRYPTED)) continue;
        for (j = 0; j < a->count; j++)
            if (a->pub[j].kind == FA_ENTRY_CDG && !fa_name_ignored(a->pub[j].name) && a->pub[j].size != 0 &&
                !(a->pub[j].flags & FA_ENTRY_ENCRYPTED) && fa_name_same_stem(a->pub[i].name, a->pub[j].name))
                break;
        if (j == a->count || !is_audio(a, i)) continue;
        out[n].audio = i; out[n].cdg = (int32_t)j; out[n].graphics_only = 0;
        used[i] = 1;
        n++;
    }
    audio_count = n - start;
    for (i = 0; i < a->count && n < cap; i++) {
        if (used[i] || !is_candidate(a, i) || (a->pub[i].flags & FA_ENTRY_ENCRYPTED) || !is_audio(a, i)) continue;
        out[n].audio = i; out[n].cdg = -1; out[n].graphics_only = 0;
        n++;
        audio_count++;
    }
    /* One song, one .cdg: the pair, whatever they are called. */
    if (audio_count == 1 && cdgs == 1 && n == 1 && out[0].cdg < 0) out[0].cdg = (int32_t)first_cdg;
    if (cdgs > 0 && n < cap) {
        /* Nothing plays as audio: the graphics alone, last after any song that fails to open. */
        out[n].audio = first_cdg; out[n].cdg = -1; out[n].graphics_only = 1;
        n++;
    }
    if (n == 0) *error = a->encrypted_candidates ? FA_ERROR_PROTECTED_CONTENT : FA_ERROR_UNSUPPORTED_FORMAT;
done:
    free(used);
    fa_mutex_unlock(&a->lock);
    return n;
}

/* ---- Visiting (tags, fingerprint) --------------------------------------------- */

int fa_archive_visit(fa_archive* a, const char* entry_name, int (*visit)(void* ctx, fa_source* entry, const char* name),
                     void* ctx, int* out_has_cdg, fa_result* error)
{
    fa_archive_choice ch[FA_ARCHIVE_MAX_CHOICES];
    uint32_t n, k, unpacks = 0;
    int taken = 0;
    int64_t whole = a->srcp->length;
    fa_result why = FA_OK;
    if (out_has_cdg) *out_has_cdg = 0;
    n = fa_archive_choices(a, entry_name, ch, FA_ARCHIVE_MAX_CHOICES, &why);
    if (error) *error = why;
    fa_mutex_lock(&a->lock);
    for (k = 0; k < n && !taken; k++) {
        const fa_archive_entry* e = &a->pub[ch[k].audio];
        const fa_zip_entry* z = &a->z[ch[k].audio];
        if (ch[k].graphics_only) continue;   /* tags and keys are of songs; a lone .cdg is played, not read here */
        if (out_has_cdg) *out_has_cdg = ch[k].cdg >= 0;
        if (z->method == 0) {
            int64_t offset;
            if (fa_zip_entry_offset(a->srcp, z, &offset) != FA_OK ||
                fa_source_set_window(a->srcp, offset, (int64_t)z->size) != FA_OK)
                continue;
            taken = visit(ctx, a->srcp, e->name);
            fa_source_set_window(a->srcp, 0, whole);
        } else {
            uint8_t* audio;
            fa_source mem;
            if (z->size > FA_ZIP_PROBE_BYTES && unpacks >= FA_ZIP_MAX_UNPACKS) continue;
            if (fa_archive_unpack(a, ch[k].audio, a->memory_limit, a->cancel, &audio) != FA_OK) continue;
            if (z->size > FA_ZIP_PROBE_BYTES) unpacks++;
            fa_source_open_memory_ref(&mem, audio, (size_t)z->size);
            taken = visit(ctx, &mem, e->name);
            fa_source_close(&mem);
            free(audio);
        }
        if (taken && out_has_cdg) *out_has_cdg = ch[k].cdg >= 0;
    }
    fa_mutex_unlock(&a->lock);
    return taken;
}

/* ---- Public calls ------------------------------------------------------------- */

static fa_result open_public(fa_source* src, const char* path, const fa_archive_options* options, fa_archive** out)
{
    fa_result r = archive_make(src, 1, path, options, out);
    if (r != FA_OK) fa_source_close(src);
    return r;
}

FA_API fa_result FA_CALL fa_archive_open_path(const char* path, const fa_archive_options* options, fa_archive** out_archive)
{
    fa_source src;
    fa_result r;
    if (out_archive) *out_archive = NULL;
    if (!path || !out_archive) return FA_ERROR_INVALID_ARG;
    r = fa_source_open_path(&src, path);
    if (r != FA_OK) return r;
    return open_public(&src, path, options, out_archive);
}

FA_API fa_result FA_CALL fa_archive_open_memory(const void* data, size_t size, const fa_archive_options* options, fa_archive** out_archive)
{
    fa_source src;
    fa_result r;
    if (out_archive) *out_archive = NULL;
    if (!data || size == 0 || !out_archive) return FA_ERROR_INVALID_ARG;
    r = fa_source_open_memory(&src, data, size);
    if (r != FA_OK) return r;
    return open_public(&src, NULL, options, out_archive);
}

FA_API fa_result FA_CALL fa_archive_open_memory_ref(const void* data, size_t size, const fa_archive_options* options, fa_archive** out_archive)
{
    fa_source src;
    fa_result r;
    if (out_archive) *out_archive = NULL;
    if (!data || size == 0 || !out_archive) return FA_ERROR_INVALID_ARG;
    r = fa_source_open_memory_ref(&src, data, size);
    if (r != FA_OK) return r;
    return open_public(&src, NULL, options, out_archive);
}

FA_API fa_result FA_CALL fa_archive_open_io(const fa_io_callbacks* io, void* user, const fa_archive_options* options, fa_archive** out_archive)
{
    fa_source src;
    fa_result r;
    if (out_archive) *out_archive = NULL;
    if (!io || !io->read || !io->seek || !io->length || !out_archive) return FA_ERROR_INVALID_ARG;
    r = fa_source_open_io(&src, io, user);
    if (r != FA_OK) return r;
    return open_public(&src, NULL, options, out_archive);   /* the source's close ends `user` on failure too */
}

FA_API void FA_CALL fa_archive_close(fa_archive* archive) { fa_archive_release(archive); }

FA_API fa_result FA_CALL fa_archive_get_format(const fa_archive* archive, int32_t* out_format)
{
    if (!archive || !out_format) return FA_ERROR_INVALID_ARG;
    *out_format = archive->format;
    return FA_OK;
}

FA_API fa_result FA_CALL fa_archive_get_filter(const fa_archive* archive, int32_t* out_filter)
{
    if (!archive || !out_filter) return FA_ERROR_INVALID_ARG;
    *out_filter = archive->filter;
    return FA_OK;
}

FA_API uint32_t FA_CALL fa_archive_get_entry_count(const fa_archive* archive) { return archive ? archive->count : 0; }

FA_API fa_result FA_CALL fa_archive_get_entry(const fa_archive* archive, uint32_t index, fa_archive_entry* out_entry)
{
    if (!archive || !out_entry || out_entry->struct_size < sizeof(uint32_t)) return FA_ERROR_INVALID_ARG;
    if (index >= archive->count) return FA_ERROR_NOT_FOUND;
    fa_copy_sized(out_entry, &archive->pub[index], sizeof(archive->pub[index]));
    return FA_OK;
}

FA_API fa_result FA_CALL fa_archive_find(const fa_archive* archive, const char* name, uint32_t* out_index)
{
    if (!archive || !name || !out_index) return FA_ERROR_INVALID_ARG;
    return fa_archive_lookup(archive, name, out_index);
}

FA_API fa_result FA_CALL fa_archive_pick(fa_archive* archive, uint32_t* out_audio, int32_t* out_cdg)
{
    fa_archive_choice ch[FA_ARCHIVE_MAX_CHOICES];
    fa_result why;
    uint32_t n;
    if (!archive || !out_audio || !out_cdg) return FA_ERROR_INVALID_ARG;
    n = fa_archive_choices(archive, NULL, ch, FA_ARCHIVE_MAX_CHOICES, &why);
    if (n == 0) return why == FA_ERROR_UNSUPPORTED_FORMAT ? FA_ERROR_NOT_FOUND : why;
    *out_audio = ch[0].audio;
    *out_cdg = ch[0].cdg;
    return FA_OK;
}

/* A non-empty block for an empty entry, so a successful read never returns NULL. */
FA_API fa_result FA_CALL fa_archive_read(fa_archive* archive, uint32_t index, void** out_data, size_t* out_size)
{
    uint8_t* data;
    fa_result r;
    if (out_data) *out_data = NULL;
    if (out_size) *out_size = 0;
    if (!archive || !out_data || !out_size) return FA_ERROR_INVALID_ARG;
    if (index >= archive->count) return FA_ERROR_NOT_FOUND;
    fa_mutex_lock(&archive->lock);
    r = fa_archive_unpack(archive, index, archive->memory_limit, archive->cancel, &data);
    fa_mutex_unlock(&archive->lock);
    if (r != FA_OK) return r;
    *out_data = data;
    *out_size = (size_t)archive->z[index].size;
    return FA_OK;
}

FA_API void FA_CALL fa_free(void* data) { free(data); }

/* ---- Readers ---------------------------------------------------------------- */

struct fa_archive_reader {
    fa_archive* archive;
    uint32_t index;
    int64_t size, pos;
    int64_t offset;        /* stored: where the data starts in the archive */
    fa_source data;        /* compressed: the unpacked copy */
    int unpacked;
};

FA_API fa_result FA_CALL fa_archive_open_reader(fa_archive* archive, uint32_t index, fa_archive_reader** out_reader)
{
    fa_archive_reader* rd;
    fa_result r = FA_OK;
    if (out_reader) *out_reader = NULL;
    if (!archive || !out_reader) return FA_ERROR_INVALID_ARG;
    if (index >= archive->count) return FA_ERROR_NOT_FOUND;
    if (archive->z[index].flags & 1) return FA_ERROR_PROTECTED_CONTENT;
    rd = (fa_archive_reader*)calloc(1, sizeof(*rd));
    if (!rd) return FA_ERROR_OUT_OF_MEMORY;
    rd->archive = fa_archive_retain(archive);
    rd->index = index;
    rd->size = archive->pub[index].size;
    if (archive->z[index].method == 0) {
        fa_mutex_lock(&archive->lock);
        r = fa_zip_entry_offset(archive->srcp, &archive->z[index], &rd->offset);
        fa_mutex_unlock(&archive->lock);
    } else if (archive->pub[index].method == FA_METHOD_OTHER) {
        r = FA_ERROR_UNSUPPORTED_FORMAT;
    }
    if (r != FA_OK) { fa_archive_release(archive); free(rd); return r; }
    *out_reader = rd;
    return FA_OK;
}

/* Compressed entry: unpack into memory, or a temp file past the limit. */
static fa_result reader_unpack(fa_archive_reader* rd)
{
    fa_archive* a = rd->archive;
    uint8_t* data;
    fa_result r;
    fa_mutex_lock(&a->lock);
    r = fa_archive_unpack(a, rd->index, a->memory_limit, a->cancel, &data);
    if (r == FA_OK) {
        fa_source_open_memory_ref(&rd->data, data, (size_t)rd->size);   /* only reached for a non-empty entry */
        rd->data.owned = data;
    } else if ((r == FA_ERROR_TOO_LARGE || r == FA_ERROR_OUT_OF_MEMORY) && a->temp_dir && (uint64_t)rd->size <= FA_ZIP_TEMP_LIMIT) {
        size_t n = strlen(a->temp_dir);
        char* folder = (char*)malloc(n + 16);
        FILE* f;
        fa_shared_file* shared;
        if (!folder) { fa_mutex_unlock(&a->lock); return FA_ERROR_OUT_OF_MEMORY; }
        snprintf(folder, n + 16, "%s%sfatzaudio-tmp", a->temp_dir, n && (a->temp_dir[n - 1] == '/' || a->temp_dir[n - 1] == '\\') ? "" : "/");
        fa_mkdir_utf8(a->temp_dir);
        fa_mkdir_utf8(folder);
        f = fa_temp_file_create(folder);
        free(folder);
        if (!f) r = FA_ERROR_IO;
        else {
            r = fa_archive_unpack_file(a, rd->index, a->cancel, f);
            if (r != FA_OK) fclose(f);
            else {
                r = fa_shared_file_new(f, rd->size, &shared);
                if (r == FA_OK) { fa_source_open_shared(&rd->data, shared); fa_shared_file_release(shared); }
            }
        }
    }
    fa_mutex_unlock(&a->lock);
    if (r == FA_OK) rd->unpacked = 1;
    return r;
}

FA_API int64_t FA_CALL fa_archive_reader_read(fa_archive_reader* rd, void* buffer, size_t bytes)
{
    int64_t got;
    if (!rd || (!buffer && bytes)) return FA_ERROR_INVALID_ARG;
    if (bytes == 0 || rd->pos >= rd->size) return 0;
    if ((uint64_t)bytes > (uint64_t)(rd->size - rd->pos)) bytes = (size_t)(rd->size - rd->pos);
    if (rd->archive->z[rd->index].method == 0) {
        fa_archive* a = rd->archive;
        fa_mutex_lock(&a->lock);
        if (fa_source_seek(a->srcp, rd->offset + rd->pos) != FA_OK) got = FA_ERROR_IO;
        else got = fa_source_read(a->srcp, buffer, bytes);
        fa_mutex_unlock(&a->lock);
    } else {
        if (!rd->unpacked) {
            fa_result r = reader_unpack(rd);
            if (r != FA_OK) return r;
        }
        if (fa_source_seek(&rd->data, rd->pos) != FA_OK) return FA_ERROR_IO;
        got = fa_source_read(&rd->data, buffer, bytes);
    }
    if (got > 0) rd->pos += got;
    return got;
}

FA_API fa_result FA_CALL fa_archive_reader_seek(fa_archive_reader* rd, int64_t position)
{
    if (!rd || position < 0) return FA_ERROR_INVALID_ARG;
    rd->pos = position > rd->size ? rd->size : position;
    return FA_OK;
}

FA_API int64_t FA_CALL fa_archive_reader_get_size(const fa_archive_reader* rd) { return rd ? rd->size : -1; }

FA_API void FA_CALL fa_archive_reader_close(fa_archive_reader* rd)
{
    if (!rd) return;
    if (rd->unpacked) fa_source_close(&rd->data);
    fa_archive_release(rd->archive);
    free(rd);
}
