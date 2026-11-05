/* A compressed stream read as one solid run of bytes. See solid.h. */
#include "archive/solid.h"
#include "archive/archive.h"
#include <stdlib.h>
#include <string.h>

struct fa_solid {
    int filter;
    uint64_t csize;                 /* the file's size */
    uint64_t total;                 /* decoded size, UINT64_MAX until known */
    int complete;                   /* a full run finished */
    char* name;
    int64_t mtime_ns;
    /* gzip */
    uint64_t gz_data, gz_len;
    uint32_t gz_crc, gz_isize;
    fa_infcps cps;
    /* bzip2 */
    fa_bzblock* bz;
    uint32_t nbz;
    /* xz */
    fa_xzblock* xz;
    uint32_t nxz;
    uint16_t xz_flags;
};

void fa_solid_free(fa_solid* s)
{
    if (!s) return;
    fa_infcps_free(&s->cps);
    free(s->bz);
    free(s->xz);
    free(s->name);
    free(s);
}

uint64_t fa_solid_size_hint(const fa_solid* s) { return s->total; }
uint64_t fa_solid_size(const fa_solid* s) { return s->complete ? s->total : UINT64_MAX; }
int fa_solid_complete(const fa_solid* s) { return s->complete; }
const char* fa_solid_name(const fa_solid* s) { return s->name; }
void fa_solid_gzip(const fa_solid* s, uint64_t* data_offset, uint64_t* data_len, uint32_t* crc, uint32_t* isize)
{
    *data_offset = s->gz_data; *data_len = s->gz_len; *crc = s->gz_crc; *isize = s->gz_isize;
}
int64_t fa_solid_mtime_ns(const fa_solid* s) { return s->mtime_ns; }

/* ---- gzip header (RFC 1952) -------------------------------------------------------------- */

static fa_result gz_parse(fa_solid* s, fa_source* src)
{
    uint8_t head[1024], trailer[8];
    size_t got, pos = 10;
    uint8_t flg;
    uint32_t mtime;
    int64_t r;
    if (s->csize < 10 + 8) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (fa_source_seek(src, 0) != FA_OK) return FA_ERROR_IO;
    r = fa_source_read(src, head, sizeof(head));
    if (r < 10) return r < 0 ? (fa_result)r : FA_ERROR_UNSUPPORTED_FORMAT;
    got = (size_t)r;
    if (head[0] != 0x1F || head[1] != 0x8B || head[2] != 8) return FA_ERROR_UNSUPPORTED_FORMAT;
    flg = head[3];
    if (flg & 0xE0) return FA_ERROR_UNSUPPORTED_FORMAT;   /* reserved bits */
    mtime = fa_le32(head + 4);
    s->mtime_ns = mtime ? (int64_t)mtime * 1000000000 : INT64_MIN;
    if (flg & 4) {   /* FEXTRA */
        size_t xlen;
        if (got < pos + 2) return FA_ERROR_UNSUPPORTED_FORMAT;
        xlen = fa_le16(head + pos);
        pos += 2 + xlen;
    }
    if (flg & 8) {   /* FNAME */
        size_t start = pos;
        while (pos < got && head[pos]) pos++;
        if (pos >= got) {
            /* A name that is not in the first KB: the header is ours to skip, the name is not. */
            uint8_t c;
            for (;;) {
                if (pos >= s->csize) return FA_ERROR_UNSUPPORTED_FORMAT;
                if (fa_source_seek(src, (int64_t)pos) != FA_OK || fa_source_read(src, &c, 1) != 1) return FA_ERROR_IO;
                if (c == 0) break;
                pos++;
            }
        } else {
            char* n = (char*)malloc(pos - start + 1);
            if (n) { memcpy(n, head + start, pos - start); n[pos - start] = 0; s->name = fa_name_to_utf8(n, 0); free(n); }
        }
        pos++;
    }
    if (flg & 16) {   /* FCOMMENT */
        uint8_t c;
        for (;;) {
            if (pos >= s->csize) return FA_ERROR_UNSUPPORTED_FORMAT;
            if (pos < got) c = head[pos];
            else if (fa_source_seek(src, (int64_t)pos) != FA_OK || fa_source_read(src, &c, 1) != 1) return FA_ERROR_IO;
            pos++;
            if (c == 0) break;
        }
    }
    if (flg & 2) pos += 2;   /* FHCRC */
    if (pos + 8 >= s->csize) return FA_ERROR_UNSUPPORTED_FORMAT;
    s->gz_data = pos;
    s->gz_len = s->csize - pos - 8;
    if (fa_source_seek(src, (int64_t)(s->csize - 8)) != FA_OK || fa_source_read(src, trailer, 8) != 8) return FA_ERROR_IO;
    s->gz_crc = fa_le32(trailer);
    s->gz_isize = fa_le32(trailer + 4);
    s->total = s->gz_isize;
    return FA_OK;
}

fa_result fa_solid_open(fa_source* src, int filter, fa_solid** out)
{
    fa_solid* s;
    fa_result r = FA_OK;
    *out = NULL;
    if (src->length < 0) return FA_ERROR_NOT_SEEKABLE;
    s = (fa_solid*)calloc(1, sizeof(*s));
    if (!s) return FA_ERROR_OUT_OF_MEMORY;
    s->filter = filter;
    s->csize = (uint64_t)src->length;
    s->total = UINT64_MAX;
    s->mtime_ns = INT64_MIN;
    if (filter == FA_FILTER_GZIP) r = gz_parse(s, src);
    else if (filter == FA_FILTER_XZ) {
        /* A file without a usable index (several streams) is read from the start. */
        if (fa_xz_index(src, s->csize, &s->xz, &s->nxz, &s->xz_flags) == FA_OK) {
            const fa_xzblock* last = &s->xz[s->nxz - 1];
            s->total = last->out_offset + last->out_size;
        }
    } else if (filter != FA_FILTER_BZIP2) r = FA_ERROR_INVALID_ARG;
    if (r != FA_OK) { fa_solid_free(s); return r; }
    *out = s;
    return FA_OK;
}

/* ---- Runs ------------------------------------------------------------------------------------ */

typedef struct trim {
    fa_sink_fn fn;
    void* ctx;
    uint64_t pos, from, to;
    int stopped, too_much;
} trim;

static int trim_put(void* c, const uint8_t* d, size_t n)
{
    trim* t = (trim*)c;
    uint64_t end = t->pos + n;
    if (end > t->from && t->pos < t->to) {
        uint64_t lo = t->pos > t->from ? t->pos : t->from, hi = end < t->to ? end : t->to;
        if (t->fn(t->ctx, d + (lo - t->pos), (size_t)(hi - lo))) t->stopped = 1;
    }
    t->pos = end;
    if (t->pos > FA_SOLID_MAX_OUT) { t->too_much = 1; return 1; }
    return t->stopped || t->pos >= t->to;
}

/* The compressed file in memory (a memory source's own bytes, else a read), or NULL. */
static fa_result load_all(fa_source* src, uint64_t size, const uint8_t** mem, uint8_t** held)
{
    *held = NULL;
    *mem = NULL;
    if (size > FA_CODEC_PARALLEL_INPUT_MAX) return FA_OK;
    if (src->kind == FA_SOURCE_MEMORY && src->base == 0 && src->mem_size >= size) { *mem = src->mem; return FA_OK; }
    *held = (uint8_t*)malloc(size ? (size_t)size : 1);
    if (!*held) return FA_OK;   /* no memory for it: the plain decoder will do */
    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, *held, (size_t)size) != (int64_t)size) {
        free(*held);
        *held = NULL;
        return FA_ERROR_IO;
    }
    *mem = *held;
    return FA_OK;
}

static fa_result run_gzip(fa_solid* s, fa_source* src, uint64_t from, uint64_t to, trim* t, const volatile int32_t* cancel, int full)
{
    const fa_infcp* cp = NULL;
    fa_infcps* rec = NULL;
    fa_cin in;
    fa_inflate_result res;
    fa_result r;
    uint32_t i;
    for (i = 0; i < s->cps.n; i++)
        if (s->cps.v[i].out_offset <= from) cp = &s->cps.v[i];
    memset(&in, 0, sizeof(in));
    in.left = s->gz_len;
    if (src->kind == FA_SOURCE_MEMORY) in.mem = src->mem + s->gz_data;
    else {
        in.src = src;
        if (fa_source_seek(src, (int64_t)s->gz_data) != FA_OK) return FA_ERROR_IO;
    }
    if (full && !s->complete) {
        fa_infcps_free(&s->cps);
        s->cps.every = s->cps.next_at = (uint64_t)4 << 20;
        rec = &s->cps;
        cp = NULL;
    }
    t->pos = cp ? cp->out_offset : 0;
    (void)to;
    r = fa_inflate_stream(in, 0, UINT64_MAX, trim_put, t, cancel, cp, rec, &res);
    if (r != FA_OK) return r;
    if (full && !res.stopped && !t->stopped) {
        if ((uint32_t)res.produced != s->gz_isize || res.crc != s->gz_crc) return FA_ERROR_UNSUPPORTED_FORMAT;
        s->total = res.produced;
        s->complete = 1;
    }
    return FA_OK;
}

static fa_result run_bzip2(fa_solid* s, fa_source* src, uint64_t from, uint64_t to, trim* t, const volatile int32_t* cancel, int full)
{
    const uint8_t* mem = NULL;
    uint8_t* held = NULL;
    fa_result r = FA_OK;
    /* Only read the whole file when the blocks can use it; a peek or a single-core run streams it. */
    if (src->kind == FA_SOURCE_MEMORY || (s->complete && s->nbz > 1) ||
        (full && s->csize >= FA_CODEC_PARALLEL_MIN && fa_cpu_count() > 1))
        r = load_all(src, s->csize, &mem, &held);
    if (r != FA_OK) return r;
    if (mem && s->nbz > 1 && s->complete) {
        /* The blocks are known: decode those that cover [from, to). */
        uint32_t first = 0, last = s->nbz - 1;
        int stopped = 0;
        while (first + 1 < s->nbz && s->bz[first].out_offset + s->bz[first].out_size <= from) first++;
        while (last > first && s->bz[last].out_offset >= to) last--;
        t->pos = s->bz[first].out_offset;
        r = fa_bz_decode_blocks(mem, (size_t)s->csize, s->bz, first, last - first + 1, fa_codec_threads(last - first + 1), trim_put, t, cancel, &stopped);
        free(held);
        return r;
    }
    if (mem && full && s->csize >= FA_CODEC_PARALLEL_MIN && fa_cpu_count() > 1) {
        fa_bzblock* blocks = NULL;
        uint32_t nb = 0;
        if (fa_bz_scan(mem, (size_t)s->csize, &blocks, &nb) == FA_OK && nb > 1) {
            int stopped = 0;
            t->pos = 0;
            r = fa_bz_decode_blocks(mem, (size_t)s->csize, blocks, 0, nb, fa_codec_threads(nb), trim_put, t, cancel, &stopped);
            if (r == FA_OK && !stopped && !t->stopped) {
                s->bz = blocks;
                s->nbz = nb;
                s->total = t->pos;
                s->complete = 1;
                free(held);
                return FA_OK;
            }
            free(blocks);
            if (r != FA_ERROR_UNSUPPORTED_FORMAT || t->pos != 0) { free(held); return r; }
        } else {
            free(blocks);
        }
    }
    {
        fa_cin in;
        uint64_t made = 0;
        int stopped = 0;
        memset(&in, 0, sizeof(in));
        in.left = s->csize;
        if (mem) in.mem = mem;
        else if (fa_source_seek(src, 0) != FA_OK) { free(held); return FA_ERROR_IO; }
        else in.src = src;
        t->pos = 0;
        r = fa_codec_to_sink(FA_CODEC_BZIP2, in, UINT64_MAX, trim_put, t, cancel, &made, &stopped);
        free(held);
        if (r == FA_OK && full && !stopped && !t->stopped) { s->total = t->pos; s->complete = 1; }
    }
    (void)from; (void)to;
    return r;
}

static fa_result run_xz(fa_solid* s, fa_source* src, uint64_t from, uint64_t to, trim* t, const volatile int32_t* cancel, int full)
{
    fa_result r;
    if (s->nxz > 1 && s->csize <= FA_CODEC_PARALLEL_INPUT_MAX) {
        const uint8_t* mem;
        uint8_t* held;
        uint32_t first = 0, last = s->nxz - 1, k;
        int ok = 1, stopped = 0;
        for (k = 0; k < s->nxz; k++) if (s->xz[k].out_size > ((uint64_t)64 << 20)) ok = 0;
        r = load_all(src, s->csize, &mem, &held);
        if (r != FA_OK) return r;
        if (ok && mem) {
            while (first + 1 < s->nxz && s->xz[first].out_offset + s->xz[first].out_size <= from) first++;
            while (last > first && s->xz[last].out_offset >= to) last--;
            t->pos = s->xz[first].out_offset;
            r = fa_xz_decode_blocks(mem, s->xz, s->xz_flags, first, last - first + 1, fa_codec_threads(last - first + 1), trim_put, t, cancel, &stopped);
            free(held);
            if (r == FA_OK && full && first == 0 && last == s->nxz - 1 && !stopped && !t->stopped) s->complete = 1;
            return r;
        }
        free(held);
    }
    {
        fa_cin in;
        uint64_t made = 0;
        int stopped = 0;
        memset(&in, 0, sizeof(in));
        in.left = s->csize;
        if (src->kind == FA_SOURCE_MEMORY) in.mem = src->mem;
        else if (fa_source_seek(src, 0) != FA_OK) return FA_ERROR_IO;
        else in.src = src;
        t->pos = 0;
        r = fa_codec_to_sink(FA_CODEC_XZ, in, UINT64_MAX, trim_put, t, cancel, &made, &stopped);
        if (r == FA_OK && full && !stopped && !t->stopped) { s->total = t->pos; s->complete = 1; }
    }
    return r;
}

fa_result fa_solid_run(fa_solid* s, fa_source* src, uint64_t from, uint64_t to, fa_sink_fn sink, void* ctx,
                       const volatile int32_t* cancel)
{
    trim t;
    fa_result r;
    int full = from == 0 && to == UINT64_MAX;
    memset(&t, 0, sizeof(t));
    t.fn = sink; t.ctx = ctx; t.from = from; t.to = to;
    if (from >= to) return FA_OK;
    if (s->filter == FA_FILTER_GZIP) r = run_gzip(s, src, from, to, &t, cancel, full);
    else if (s->filter == FA_FILTER_BZIP2) r = run_bzip2(s, src, from, to, &t, cancel, full);
    else r = run_xz(s, src, from, to, &t, cancel, full);
    if (t.too_much) return FA_ERROR_TOO_LARGE;
    return r;
}

uint64_t fa_solid_cost(const fa_solid* s, uint64_t offset)
{
    uint64_t best = 0;
    uint32_t i;
    if (s->filter == FA_FILTER_GZIP) {
        for (i = 0; i < s->cps.n; i++) if (s->cps.v[i].out_offset <= offset) best = s->cps.v[i].out_offset;
    } else if (s->filter == FA_FILTER_BZIP2 && s->complete) {
        for (i = 0; i < s->nbz; i++) if (s->bz[i].out_offset <= offset) best = s->bz[i].out_offset;
    } else if (s->filter == FA_FILTER_XZ && s->nxz > 1) {
        for (i = 0; i < s->nxz; i++) if (s->xz[i].out_offset <= offset) best = s->xz[i].out_offset;
    }
    return offset - best;
}
