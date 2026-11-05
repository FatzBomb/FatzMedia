/* Decompression for archives: bzip2, LZMA (ZIP method 14) and xz, from the vendored decoders,
 * and the pool that decodes independent blocks on several cores. See codec.h. */
#include "archive/codec.h"
#include "cdg.h"
#include "bzlib.h"
#include "LzmaDec.h"
#include "Xz.h"
#include "7zCrc.h"
#include "XzCrc64.h"
#include <stdlib.h>
#include <string.h>

/* bzlib.c reports an inconsistency it cannot go on from (never damaged input: that is a
 * return code) by calling this; the library cannot continue. */
void bz_internal_error(int errcode)
{
    (void)errcode;
    abort();
}

/* The SDK allocates the LZMA2 dictionary a block header names, whatever block that is: nothing bigger than
 * FA_CODEC_MAX_DICT (plus a little) is handed out, so a later block cannot ask for gigabytes. */
static void* sz_alloc(ISzAllocPtr p, size_t n)
{
    (void)p;
    if ((uint64_t)n > FA_CODEC_MAX_DICT + ((uint64_t)1 << 20)) return NULL;
    return malloc(n ? n : 1);
}
static void sz_free(ISzAllocPtr p, void* a) { (void)p; free(a); }
static const ISzAlloc g_alloc = { sz_alloc, sz_free };

#define CODEC_CHUNK ((size_t)1 << 20)        /* output handed to a sink at a time */
#define CODEC_IN_CHUNK ((size_t)256 << 10)   /* input read from a source at a time */

static int cancelled(const volatile int32_t* cancel) { return cancel && *cancel; }

/* ---- Input ----------------------------------------------------------------------- */

typedef struct feed {
    fa_cin in;
    uint8_t* buf;            /* source input lands here */
    const uint8_t* p;        /* the input not used yet */
    size_t n;
    int err;
} feed;

static fa_result feed_init(feed* f, fa_cin in)
{
    memset(f, 0, sizeof(*f));
    f->in = in;
    if (!in.mem) {
        f->buf = (uint8_t*)malloc(CODEC_IN_CHUNK + 16);
        if (!f->buf) return FA_ERROR_OUT_OF_MEMORY;
    }
    return FA_OK;
}

static void feed_free(feed* f) { free(f->buf); f->buf = NULL; }

/* Makes at least `want` bytes available, or what is left. Returns the bytes at hand. */
static size_t feed_ensure(feed* f, size_t want)
{
    if (f->n >= want || f->in.left == 0) return f->n;
    if (f->in.mem) {
        uint64_t take = f->in.left;
        if (f->n == 0) f->p = f->in.mem;
        if (take > ((uint64_t)1 << 28)) take = (uint64_t)1 << 28;
        f->n += (size_t)take;
        f->in.mem += take;
        f->in.left -= take;
        return f->n;
    }
    if (f->n && f->p != f->buf) memmove(f->buf, f->p, f->n);
    f->p = f->buf;
    while (f->n < want && f->n < CODEC_IN_CHUNK && f->in.left) {
        size_t room = CODEC_IN_CHUNK - f->n;
        int64_t got;
        if (room > f->in.left) room = (size_t)f->in.left;
        got = fa_source_read(f->in.src, f->buf + f->n, room);
        if (got <= 0) { f->err = 1; break; }
        f->n += (size_t)got;
        f->in.left -= (uint64_t)got;
    }
    return f->n;
}

static void feed_use(feed* f, size_t used) { f->p += used; f->n -= used; }

/* ---- Output ----------------------------------------------------------------------- */

typedef struct outsink {
    fa_sink_fn fn;
    void* ctx;
    uint64_t produced, limit;   /* limit: the exact size when known, else UINT64_MAX */
    int stopped, overflow;
} outsink;

/* Delivers n bytes; returns 1 when decoding must end (the sink stopped it or too much came out). */
static int out_put(outsink* o, const uint8_t* data, size_t n)
{
    if (n == 0) return 0;
    if (o->produced + n > o->limit) { o->overflow = 1; return 1; }
    o->produced += n;
    if (o->fn(o->ctx, data, n)) { o->stopped = 1; return 1; }
    return 0;
}

typedef struct memsink { uint8_t* out; size_t cap, pos; } memsink;
static int memsink_put(void* ctx, const uint8_t* data, size_t n)
{
    memsink* m = (memsink*)ctx;
    if (n > m->cap - m->pos) return 1;
    memcpy(m->out + m->pos, data, n);
    m->pos += n;
    return 0;
}

/* ---- bzip2, one stream at a time ----------------------------------------------------- */

static fa_result bz_result(int r)
{
    return r == BZ_MEM_ERROR ? FA_ERROR_OUT_OF_MEMORY : FA_ERROR_UNSUPPORTED_FORMAT;
}

static fa_result bz_sequential(fa_cin in, outsink* o, const volatile int32_t* cancel)
{
    feed f;
    bz_stream z;
    uint8_t* out;
    fa_result res = FA_OK;
    int streams = 0, active = 0;
    res = feed_init(&f, in);
    if (res != FA_OK) return res;
    out = (uint8_t*)malloc(CODEC_CHUNK);
    if (!out) { feed_free(&f); return FA_ERROR_OUT_OF_MEMORY; }
    for (;;) {
        int r;
        size_t before;
        if (cancelled(cancel)) { res = FA_ERROR_CANCELLED; break; }
        if (!active) {
            /* Another stream: pbzip2 and friends write several one after the other. */
            if (feed_ensure(&f, 4) < 4) {
                if (f.err) res = FA_ERROR_IO;
                else if (streams == 0) res = FA_ERROR_UNSUPPORTED_FORMAT;
                break;
            }
            if (memcmp(f.p, "BZh", 3) != 0 || f.p[3] < '1' || f.p[3] > '9') {
                if (streams == 0) res = FA_ERROR_UNSUPPORTED_FORMAT;   /* else trailing bytes: ignored, as bzip2 does */
                break;
            }
            memset(&z, 0, sizeof(z));
            if (BZ2_bzDecompressInit(&z, 0, 0) != BZ_OK) { res = FA_ERROR_OUT_OF_MEMORY; break; }
            active = 1;
            streams++;
        }
        if (f.n == 0 && feed_ensure(&f, 1) == 0) { res = f.err ? FA_ERROR_IO : FA_ERROR_UNSUPPORTED_FORMAT; break; }   /* truncated */
        z.next_in = (char*)(uintptr_t)f.p;
        z.avail_in = (unsigned int)(f.n > (size_t)1 << 30 ? (size_t)1 << 30 : f.n);
        before = z.avail_in;
        z.next_out = (char*)out;
        z.avail_out = CODEC_CHUNK;
        r = BZ2_bzDecompress(&z);
        feed_use(&f, before - z.avail_in);
        if (r != BZ_OK && r != BZ_STREAM_END) { res = bz_result(r); break; }
        if (out_put(o, out, CODEC_CHUNK - z.avail_out)) {
            if (o->overflow) res = FA_ERROR_UNSUPPORTED_FORMAT;
            break;
        }
        if (r == BZ_STREAM_END) { BZ2_bzDecompressEnd(&z); active = 0; }
    }
    if (active) BZ2_bzDecompressEnd(&z);
    free(out);
    feed_free(&f);
    return res;
}

/* ---- bzip2, blocks -------------------------------------------------------------------- */

#define BZ_BLOCK_MAGIC UINT64_C(0x314159265359)
#define BZ_END_MAGIC UINT64_C(0x177245385090)
#define BZ_MAX_BLOCK_OUT ((size_t)256 << 20)

/* The 48 bits that start at bit `bit` of mem (bit 0 = the top bit of byte 0). */
static uint64_t bits48(const uint8_t* mem, size_t size, uint64_t bit)
{
    uint64_t byte = bit >> 3, w = 0;
    unsigned shift = (unsigned)(bit & 7), i;
    for (i = 0; i < 8; i++) w = (w << 8) | (byte + i < size ? mem[byte + i] : 0);
    return (w << shift) >> 16;
}

/* The first bit >= from where a block or end magic starts, or UINT64_MAX; *which is 0 for a
 * block, 1 for the end of a stream. Looks at 8 shifts of each 8-byte window. */
static uint64_t bz_find_magic(const uint8_t* mem, size_t size, uint64_t from, int* which)
{
    uint64_t total = (uint64_t)size * 8, bit = from;
    while (bit + 48 <= total) {
        uint64_t byte = bit >> 3, w;
        unsigned s = (unsigned)(bit & 7);
        if (byte + 8 <= size) {
            w = ((uint64_t)mem[byte] << 56) | ((uint64_t)mem[byte + 1] << 48) | ((uint64_t)mem[byte + 2] << 40) |
                ((uint64_t)mem[byte + 3] << 32) | ((uint64_t)mem[byte + 4] << 24) | ((uint64_t)mem[byte + 5] << 16) |
                ((uint64_t)mem[byte + 6] << 8) | mem[byte + 7];
            for (; s < 8 && ((byte << 3) | s) + 48 <= total; s++) {
                uint64_t v = (w << s) >> 16;
                if (v == BZ_BLOCK_MAGIC) { *which = 0; return (byte << 3) | s; }
                if (v == BZ_END_MAGIC) { *which = 1; return (byte << 3) | s; }
            }
        } else {
            for (; s < 8 && ((byte << 3) | s) + 48 <= total; s++) {
                uint64_t v = bits48(mem, size, (byte << 3) | s);
                if (v == BZ_BLOCK_MAGIC) { *which = 0; return (byte << 3) | s; }
                if (v == BZ_END_MAGIC) { *which = 1; return (byte << 3) | s; }
            }
        }
        bit = (byte + 1) << 3;
    }
    return UINT64_MAX;
}

fa_result fa_bz_scan(const uint8_t* mem, size_t size, fa_bzblock** out, uint32_t* count)
{
    fa_bzblock* b = NULL;
    uint32_t n = 0, cap = 0;
    uint64_t pos = 0;
    *out = NULL;
    *count = 0;
    while (pos + 4 <= size && memcmp(mem + pos, "BZh", 3) == 0 && mem[pos + 3] >= '1' && mem[pos + 3] <= '9') {
        uint8_t level = mem[pos + 3];
        uint64_t bit = (pos + 4) * 8;
        int which = 0;
        int open = 0;   /* a block started and its end is not known yet */
        uint32_t first = n;
        for (;;) {
            uint64_t m = bz_find_magic(mem, size, bit, &which);
            if (m == UINT64_MAX) { free(b); return FA_ERROR_UNSUPPORTED_FORMAT; }   /* no end of stream: truncated */
            if (open) b[n - 1].bit_end = m;
            if (which == 1) {
                /* After the end magic: a 32-bit CRC and padding to a byte. */
                pos = (m + 48 + 32 + 7) / 8;
                break;
            }
            if (n == cap) {
                fa_bzblock* nb;
                cap = cap ? cap * 2 : 64;
                nb = (fa_bzblock*)realloc(b, (size_t)cap * sizeof(*b));
                if (!nb) { free(b); return FA_ERROR_OUT_OF_MEMORY; }
                b = nb;
            }
            memset(&b[n], 0, sizeof(b[n]));
            b[n].bit_start = m;
            b[n].level = level;
            n++;
            open = 1;
            bit = m + 48 + 32;   /* past the magic and the block CRC */
        }
        (void)first;
    }
    if (n == 0) { free(b); return FA_ERROR_UNSUPPORTED_FORMAT; }
    *out = b;
    *count = n;
    return FA_OK;
}

typedef struct bitwriter { uint8_t* p; uint64_t bit; } bitwriter;
static void bw_put(bitwriter* w, uint64_t v, unsigned nbits)
{
    while (nbits) {
        unsigned room = 8 - (unsigned)(w->bit & 7), take = nbits < room ? nbits : room;
        uint8_t bits = (uint8_t)((v >> (nbits - take)) & ((1u << take) - 1u));
        if ((w->bit & 7) == 0) w->p[w->bit >> 3] = 0;
        w->p[w->bit >> 3] |= (uint8_t)(bits << (room - take));
        w->bit += take;
        nbits -= take;
    }
}

/* One block as a bzip2 stream of its own: the header, the block's bits, the end magic and a
 * combined CRC that, for a single block, is the block's own. */
static fa_result bz_pack_block(const uint8_t* mem, size_t size, const fa_bzblock* b, uint8_t** out, size_t* out_n)
{
    uint64_t nbits = b->bit_end - b->bit_start, i;
    size_t total = 4 + (size_t)((nbits + 48 + 32 + 7) / 8) + 8;
    uint8_t* p = (uint8_t*)malloc(total);
    bitwriter w;
    uint32_t crc;
    if (!p) return FA_ERROR_OUT_OF_MEMORY;
    memset(p, 0, total);
    p[0] = 'B'; p[1] = 'Z'; p[2] = 'h'; p[3] = b->level;
    w.p = p + 4;
    w.bit = 0;
    for (i = 0; i + 32 <= nbits; i += 32) bw_put(&w, bits48(mem, size, b->bit_start + i) >> 16, 32);
    if (i < nbits) bw_put(&w, bits48(mem, size, b->bit_start + i) >> (48 - (nbits - i)), (unsigned)(nbits - i));
    crc = (uint32_t)(bits48(mem, size, b->bit_start + 48) >> 16);
    bw_put(&w, BZ_END_MAGIC, 48);
    bw_put(&w, crc, 32);
    *out = p;
    *out_n = 4 + (size_t)((w.bit + 7) / 8);
    return FA_OK;
}

typedef struct bzjob {
    const uint8_t* mem;
    size_t size;
    fa_bzblock* blocks;
    uint32_t first;
    fa_sink_fn sink;
    void* sink_ctx;
    uint64_t base;   /* decoded offset of the first block of this run */
    uint64_t pos;
} bzjob;

static fa_result bz_work(void* ctx, uint32_t i, uint8_t** buf, size_t* len)
{
    bzjob* j = (bzjob*)ctx;
    fa_bzblock* b = &j->blocks[j->first + i];
    uint8_t* packed = NULL;
    uint8_t* out;
    size_t packed_n, cap, used = 0;
    bz_stream z;
    fa_result res = FA_OK;
    int r;
    *buf = NULL;
    *len = 0;
    if (b->bit_end <= b->bit_start + 80) return FA_ERROR_UNSUPPORTED_FORMAT;
    res = bz_pack_block(j->mem, j->size, b, &packed, &packed_n);
    if (res != FA_OK) return res;
    cap = (size_t)(b->level - '0') * 100000 + 4096;
    out = (uint8_t*)malloc(cap);
    if (!out) { free(packed); return FA_ERROR_OUT_OF_MEMORY; }
    memset(&z, 0, sizeof(z));
    if (BZ2_bzDecompressInit(&z, 0, 0) != BZ_OK) { free(packed); free(out); return FA_ERROR_OUT_OF_MEMORY; }
    z.next_in = (char*)packed;
    z.avail_in = (unsigned int)packed_n;
    for (;;) {
        if (used == cap) {
            uint8_t* nb;
            if (cap >= BZ_MAX_BLOCK_OUT) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
            cap *= 2;
            nb = (uint8_t*)realloc(out, cap);
            if (!nb) { res = FA_ERROR_OUT_OF_MEMORY; break; }
            out = nb;
        }
        z.next_out = (char*)out + used;
        z.avail_out = (unsigned int)(cap - used > (size_t)1 << 30 ? (size_t)1 << 30 : cap - used);
        r = BZ2_bzDecompress(&z);
        used = (size_t)((uint8_t*)z.next_out - out);
        if (r == BZ_STREAM_END) break;
        if (r != BZ_OK) { res = bz_result(r); break; }
        if (z.avail_in == 0 && z.avail_out != 0) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
    }
    BZ2_bzDecompressEnd(&z);
    free(packed);
    if (res != FA_OK) { free(out); return res; }
    *buf = out;
    *len = used;
    return FA_OK;
}

static int bz_emit(void* ctx, uint32_t i, uint8_t* buf, size_t len)
{
    bzjob* j = (bzjob*)ctx;
    fa_bzblock* b = &j->blocks[j->first + i];
    b->out_size = len;
    b->out_offset = j->base + j->pos;
    j->pos += len;
    return len ? j->sink(j->sink_ctx, buf, len) : 0;
}

fa_result fa_bz_decode_blocks(const uint8_t* mem, size_t mem_size, fa_bzblock* blocks, uint32_t first, uint32_t n, uint32_t threads,
                              fa_sink_fn sink, void* ctx, const volatile int32_t* cancel, int* stopped)
{
    bzjob j;
    fa_pjob p;
    j.mem = mem;
    j.size = mem_size;
    j.blocks = blocks;
    j.first = first;
    j.sink = sink;
    j.sink_ctx = ctx;
    j.base = blocks[first].out_offset;
    j.pos = 0;
    p.count = n;
    p.threads = threads;
    p.window = threads * 2 + 2;
    p.ctx = &j;
    p.work = bz_work;
    p.emit = bz_emit;
    return fa_parallel_ordered(&p, cancel, stopped);
}

/* ---- The block pool ------------------------------------------------------------------ */

uint32_t fa_codec_threads(uint32_t blocks)
{
    uint32_t t = fa_cpu_count();
    if (t > 8) t = 8;
    if (t > blocks) t = blocks;
    return t < 1 ? 1 : t;
}

typedef struct pslot {
    uint32_t idx;
    int state;            /* 0 free, 1 working, 2 done */
    uint8_t* buf;
    size_t len;
    fa_result res;
} pslot;

typedef struct pool {
    const fa_pjob* job;
    pslot* ring;
    fa_mutex lock;
    fa_cond cond;
    uint32_t next, emitted;
    int abort;
} pool;

static void pool_worker(void* arg)
{
    pool* p = (pool*)arg;
    const fa_pjob* job = p->job;
    fa_mutex_lock(&p->lock);
    for (;;) {
        uint32_t i;
        pslot* s;
        uint8_t* buf = NULL;
        size_t len = 0;
        fa_result r;
        while (!p->abort && p->next < job->count && p->next >= p->emitted + job->window)
            fa_cond_wait_ms(&p->cond, &p->lock, UINT32_MAX);
        if (p->abort || p->next >= job->count) break;
        i = p->next++;
        s = &p->ring[i % job->window];
        s->idx = i;
        s->state = 1;
        fa_mutex_unlock(&p->lock);
        r = job->work(job->ctx, i, &buf, &len);
        fa_mutex_lock(&p->lock);
        s->buf = buf;
        s->len = len;
        s->res = r;
        s->state = 2;
        fa_cond_broadcast(&p->cond);
    }
    fa_cond_broadcast(&p->cond);
    fa_mutex_unlock(&p->lock);
}

fa_result fa_parallel_ordered(const fa_pjob* job, const volatile int32_t* cancel, int* stopped)
{
    pool p;
    fa_thread th[8];
    uint32_t nth = job->threads > 8 ? 8 : job->threads, started = 0, i;
    fa_result res = FA_OK;
    *stopped = 0;
    if (job->count == 0) return FA_OK;
    if (job->window < 2 || nth < 1) return FA_ERROR_INVALID_ARG;
    memset(&p, 0, sizeof(p));
    p.job = job;
    p.ring = (pslot*)calloc(job->window, sizeof(pslot));
    if (!p.ring) return FA_ERROR_OUT_OF_MEMORY;
    if (fa_mutex_init(&p.lock) != 0) { free(p.ring); return FA_ERROR_GENERIC; }
    if (fa_cond_init(&p.cond) != 0) { fa_mutex_destroy(&p.lock); free(p.ring); return FA_ERROR_GENERIC; }
    for (i = 0; i < nth; i++) {
        if (fa_thread_create(&th[started], pool_worker, &p) != 0) break;
        started++;
    }
    if (started == 0) {
        /* No thread could start: do the work here, in order. */
        for (i = 0; i < job->count && res == FA_OK && !*stopped; i++) {
            uint8_t* buf = NULL;
            size_t len = 0;
            if (cancelled(cancel)) { res = FA_ERROR_CANCELLED; break; }
            res = job->work(job->ctx, i, &buf, &len);
            if (res == FA_OK) *stopped = job->emit(job->ctx, i, buf, len);
            free(buf);
        }
    } else {
        for (i = 0; i < job->count; i++) {
            pslot* s = &p.ring[i % job->window];
            uint8_t* buf;
            size_t len;
            fa_result r;
            fa_mutex_lock(&p.lock);
            while (!(s->state == 2 && s->idx == i)) {
                if (cancelled(cancel)) break;
                fa_cond_wait_ms(&p.cond, &p.lock, 20);
            }
            if (!(s->state == 2 && s->idx == i)) {
                fa_mutex_unlock(&p.lock);
                res = FA_ERROR_CANCELLED;
                break;
            }
            buf = s->buf; len = s->len; r = s->res;
            s->buf = NULL;
            s->state = 0;
            p.emitted = i + 1;
            fa_cond_broadcast(&p.cond);
            fa_mutex_unlock(&p.lock);
            if (r != FA_OK) { free(buf); res = r; break; }
            if (job->emit(job->ctx, i, buf, len)) { *stopped = 1; free(buf); break; }
            free(buf);
        }
    }
    fa_mutex_lock(&p.lock);
    p.abort = 1;
    fa_cond_broadcast(&p.cond);
    fa_mutex_unlock(&p.lock);
    for (i = 0; i < started; i++) fa_thread_join(&th[i]);
    for (i = 0; i < job->window; i++) free(p.ring[i].buf);
    fa_cond_destroy(&p.cond);
    fa_mutex_destroy(&p.lock);
    free(p.ring);
    return res;
}

/* ---- xz ------------------------------------------------------------------------------- */

/* The CRC tables of the LZMA SDK are built once per process, before any xz code runs. */
static int xz_crc_once(void* ctx)
{
    (void)ctx;
    CrcGenerateTable();
    Crc64GenerateTable();
    return 1;
}
static void xz_crc_init(void)
{
    static fa_atomic32 state;
    fa_once(&state, xz_crc_once, NULL);
}

/* The LZMA2 dictionary a block header asks for, checked against FA_CODEC_MAX_DICT. */
static fa_result xz_check_block_header(const uint8_t* hdr, size_t n)
{
    CXzBlock blk;
    unsigned k;
    if (n < 8 || (size_t)(hdr[0] + 1) * 4 > n) return FA_ERROR_UNSUPPORTED_FORMAT;
    xz_crc_init();
    if (XzBlock_Parse(&blk, hdr) != SZ_OK) return FA_ERROR_UNSUPPORTED_FORMAT;
    for (k = 0; k < XzBlock_GetNumFilters(&blk); k++) {
        if (blk.filters[k].id == XZ_ID_LZMA2) {
            unsigned p = blk.filters[k].props[0];
            uint64_t dict;
            if (blk.filters[k].propsSize != 1 || p > 40) return FA_ERROR_UNSUPPORTED_FORMAT;
            dict = p == 40 ? UINT64_C(0xFFFFFFFF) : (uint64_t)(2 | (p & 1)) << (p / 2 + 11);
            if (dict > FA_CODEC_MAX_DICT) return FA_ERROR_TOO_LARGE;
        }
    }
    return FA_OK;
}

static fa_result xz_sequential(fa_cin in, outsink* o, const volatile int32_t* cancel)
{
    xz_crc_init();
    feed f;
    CXzUnpacker x;
    uint8_t* out;
    fa_result res;
    int checked = 0;
    res = feed_init(&f, in);
    if (res != FA_OK) return res;
    out = (uint8_t*)malloc(CODEC_CHUNK);
    if (!out) { feed_free(&f); return FA_ERROR_OUT_OF_MEMORY; }
    XzUnpacker_Construct(&x, &g_alloc);
    XzUnpacker_Init(&x);
    for (;;) {
        SizeT dest_len = CODEC_CHUNK, src_len;
        ECoderStatus st;
        SRes r;
        int at_end;
        if (cancelled(cancel)) { res = FA_ERROR_CANCELLED; break; }
        if (f.n == 0) feed_ensure(&f, 1);
        if (f.err) { res = FA_ERROR_IO; break; }
        if (!checked && feed_ensure(&f, XZ_STREAM_HEADER_SIZE + XZ_BLOCK_HEADER_SIZE_MAX) >= XZ_STREAM_HEADER_SIZE + 8) {
            /* Refuse a dictionary that is too large before the decoder allocates it. */
            if (memcmp(f.p, XZ_SIG, XZ_SIG_SIZE) == 0 && f.p[XZ_STREAM_HEADER_SIZE] != 0) {
                size_t avail = f.n - XZ_STREAM_HEADER_SIZE;
                fa_result c = xz_check_block_header(f.p + XZ_STREAM_HEADER_SIZE, avail);
                if (c != FA_OK) { res = c; break; }
            }
            checked = 1;
        }
        src_len = f.n > (size_t)1 << 30 ? (size_t)1 << 30 : f.n;
        at_end = f.in.left == 0 && src_len == f.n;
        r = XzUnpacker_Code(&x, out, &dest_len, f.p, &src_len, at_end, CODER_FINISH_ANY, &st);
        feed_use(&f, src_len);
        if (r == SZ_ERROR_MEM) { res = FA_ERROR_OUT_OF_MEMORY; break; }
        if (r != SZ_OK) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        if (out_put(o, out, dest_len)) {
            if (o->overflow) res = FA_ERROR_UNSUPPORTED_FORMAT;
            break;
        }
        if (at_end && f.n == 0 && dest_len == 0) {
            if (!XzUnpacker_IsStreamWasFinished(&x)) res = FA_ERROR_UNSUPPORTED_FORMAT;   /* truncated */
            break;
        }
        if (src_len == 0 && dest_len == 0) {
            if (at_end && XzUnpacker_IsStreamWasFinished(&x)) break;
            if (f.n == 0 && f.in.left == 0) { if (!XzUnpacker_IsStreamWasFinished(&x)) res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
            if (f.n != 0) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }   /* no progress */
        }
    }
    XzUnpacker_Free(&x);
    free(out);
    feed_free(&f);
    return res;
}

static unsigned read_varint(const uint8_t* p, size_t n, uint64_t* v)
{
    unsigned used = 0;
    uint64_t r = 0;
    while (used < n && used < 9) {
        uint8_t b = p[used];
        r |= (uint64_t)(b & 0x7F) << (7 * used);
        used++;
        if (!(b & 0x80)) { *v = r; return used; }
    }
    return 0;
}

fa_result fa_xz_index(fa_source* src, uint64_t size, fa_xzblock** blocks, uint32_t* count, uint16_t* flags)
{
    xz_crc_init();
    uint8_t head[12], foot[12], *idx = NULL;
    uint64_t end = size, index_size, backward, n, i, in_off = 12, out_off = 0, pos;
    fa_xzblock* b = NULL;
    int64_t got;
    *blocks = NULL;
    *count = 0;
    if (size < 12 + 12 + 8) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, head, 12) != 12) return FA_ERROR_IO;
    if (memcmp(head, XZ_SIG, XZ_SIG_SIZE) != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    /* Stream padding (zeros in fours) may follow the footer. */
    for (;;) {
        uint8_t z[4];
        if (end < 12 + 12 + 4) return FA_ERROR_UNSUPPORTED_FORMAT;
        if (fa_source_seek(src, (int64_t)end - 4) != FA_OK || fa_source_read(src, z, 4) != 4) return FA_ERROR_IO;
        if (z[0] || z[1] || z[2] || z[3]) break;
        end -= 4;
    }
    if (fa_source_seek(src, (int64_t)end - 12) != FA_OK || fa_source_read(src, foot, 12) != 12) return FA_ERROR_IO;
    if (foot[10] != 'Y' || foot[11] != 'Z' || foot[8] != head[6] || foot[9] != head[7]) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (fa_crc32(foot + 4, 6) != fa_le32(foot)) return FA_ERROR_UNSUPPORTED_FORMAT;
    backward = ((uint64_t)fa_le32(foot + 4) + 1) * 4;
    if (backward < 8 || backward > ((uint64_t)1 << 28) || backward + 12 + 12 > end) return FA_ERROR_UNSUPPORTED_FORMAT;
    index_size = backward;
    idx = (uint8_t*)malloc((size_t)index_size);
    if (!idx) return FA_ERROR_OUT_OF_MEMORY;
    if (fa_source_seek(src, (int64_t)(end - 12 - index_size)) != FA_OK) { free(idx); return FA_ERROR_IO; }
    got = fa_source_read(src, idx, (size_t)index_size);
    if (got != (int64_t)index_size) { free(idx); return FA_ERROR_IO; }
    if (idx[0] != 0 || fa_crc32(idx, (size_t)index_size - 4) != fa_le32(idx + index_size - 4)) { free(idx); return FA_ERROR_UNSUPPORTED_FORMAT; }
    pos = 1;
    {
        unsigned used = read_varint(idx + pos, (size_t)(index_size - 4 - pos), &n);
        if (!used || n == 0 || n > index_size) { free(idx); return FA_ERROR_UNSUPPORTED_FORMAT; }
        pos += used;
    }
    b = (fa_xzblock*)calloc((size_t)n, sizeof(*b));
    if (!b) { free(idx); return FA_ERROR_OUT_OF_MEMORY; }
    for (i = 0; i < n; i++) {
        uint64_t unpadded, usize;
        unsigned u1 = read_varint(idx + pos, (size_t)(index_size - 4 - pos), &unpadded), u2;
        if (!u1) goto bad;
        pos += u1;
        u2 = read_varint(idx + pos, (size_t)(index_size - 4 - pos), &usize);
        if (!u2) goto bad;
        pos += u2;
        if (unpadded < 8 || unpadded > end) goto bad;
        b[i].in_offset = in_off;
        b[i].in_size = (unpadded + 3) & ~(uint64_t)3;
        b[i].out_offset = out_off;
        b[i].out_size = usize;
        in_off += b[i].in_size;
        out_off += usize;
        if (in_off > end - 12 - index_size || out_off < usize) goto bad;
    }
    /* The blocks must end exactly where the index starts. */
    if (in_off != end - 12 - index_size) goto bad;
    free(idx);
    *blocks = b;
    *count = (uint32_t)n;
    *flags = (uint16_t)(head[6] << 8 | head[7]);
    return FA_OK;
bad:
    free(idx);
    free(b);
    return FA_ERROR_UNSUPPORTED_FORMAT;
}

/* One block into `out` (exactly its unpacked size). */
static fa_result xz_block_into(const uint8_t* in, const fa_xzblock* b, uint16_t flags, uint8_t* out)
{
    xz_crc_init();
    CXzUnpacker* x;
    SizeT dest_len = (SizeT)b->out_size, src_len = (SizeT)b->in_size;
    ECoderStatus st;
    SRes r;
    fa_result res = FA_OK;
    if (b->out_size > (uint64_t)SIZE_MAX / 2 || b->in_size > (uint64_t)SIZE_MAX / 2) return FA_ERROR_TOO_LARGE;
    res = xz_check_block_header(in, (size_t)b->in_size);
    if (res != FA_OK) return res;
    x = (CXzUnpacker*)malloc(sizeof(*x));
    if (!x) return FA_ERROR_OUT_OF_MEMORY;
    XzUnpacker_Construct(x, &g_alloc);
    XzUnpacker_Init(x);
    x->streamFlags = flags;
    XzUnpacker_PrepareToRandomBlockDecoding(x);
    XzUnpacker_SetOutBuf(x, out, (size_t)b->out_size);
    r = XzUnpacker_Code(x, NULL, &dest_len, in, &src_len, True, CODER_FINISH_END, &st);
    if (r == SZ_ERROR_MEM) res = FA_ERROR_OUT_OF_MEMORY;
    else if (r != SZ_OK || dest_len != b->out_size || !XzUnpacker_IsBlockFinished(x)) res = FA_ERROR_UNSUPPORTED_FORMAT;
    XzUnpacker_Free(x);
    free(x);
    return res;
}

typedef struct xzjob {
    const uint8_t* mem;
    const fa_xzblock* blocks;
    uint16_t flags;
    uint32_t first;
    fa_sink_fn sink;
    void* sink_ctx;
} xzjob;

static fa_result xz_work(void* ctx, uint32_t i, uint8_t** buf, size_t* len)
{
    xzjob* j = (xzjob*)ctx;
    const fa_xzblock* b = &j->blocks[j->first + i];
    uint8_t* out;
    fa_result r;
    *buf = NULL;
    *len = 0;
    if (b->out_size > FA_CODEC_MAX_DICT) return FA_ERROR_TOO_LARGE;
    out = (uint8_t*)malloc(b->out_size ? (size_t)b->out_size : 1);
    if (!out) return FA_ERROR_OUT_OF_MEMORY;
    r = xz_block_into(j->mem + b->in_offset, b, j->flags, out);
    if (r != FA_OK) { free(out); return r; }
    *buf = out;
    *len = (size_t)b->out_size;
    return FA_OK;
}

static int xz_emit(void* ctx, uint32_t i, uint8_t* buf, size_t len)
{
    xzjob* j = (xzjob*)ctx;
    (void)i;
    return len ? j->sink(j->sink_ctx, buf, len) : 0;
}

fa_result fa_xz_decode_blocks(const uint8_t* mem, const fa_xzblock* blocks, uint16_t flags, uint32_t first, uint32_t n,
                              uint32_t threads, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel, int* stopped)
{
    xzjob j;
    fa_pjob p;
    j.mem = mem; j.blocks = blocks; j.flags = flags; j.first = first; j.sink = sink; j.sink_ctx = ctx;
    p.count = n;
    p.threads = threads;
    p.window = threads * 2 + 2;
    p.ctx = &j;
    p.work = xz_work;
    p.emit = xz_emit;
    return fa_parallel_ordered(&p, cancel, stopped);
}

/* ---- LZMA (ZIP method 14) ------------------------------------------------------------- */

/* The LZMA properties of a ZIP entry: version (2 bytes), size of properties (2 bytes, 5), then
 * the 5-byte LZMA header. Returns the bytes used or 0. */
static size_t lzma_zip_header(const uint8_t* p, size_t n, uint8_t props[5])
{
    if (n < 4 + LZMA_PROPS_SIZE) return 0;
    if (p[2] != LZMA_PROPS_SIZE || p[3] != 0) return 0;
    memcpy(props, p + 4, LZMA_PROPS_SIZE);
    return 4 + LZMA_PROPS_SIZE;
}

/* Never ask for a dictionary bigger than the output when it is known (the header of a
 * -mx9 stream says 64 MB for a 5 MB song). */
static fa_result lzma_dict_limit(uint8_t props[5], uint64_t usize)
{
    uint32_t dict = fa_le32(props + 1);
    if (usize != UINT64_MAX && dict > usize) {
        dict = usize < 4096 ? 4096 : (uint32_t)usize;
        props[1] = (uint8_t)dict; props[2] = (uint8_t)(dict >> 8); props[3] = (uint8_t)(dict >> 16); props[4] = (uint8_t)(dict >> 24);
    }
    return dict > FA_CODEC_MAX_DICT ? FA_ERROR_TOO_LARGE : FA_OK;
}

static fa_result lzma_zip_sink(fa_cin in, uint64_t usize, outsink* o, const volatile int32_t* cancel)
{
    feed f;
    CLzmaDec d;
    uint8_t props[5], *out;
    size_t hdr;
    fa_result res;
    uint64_t done = 0;
    if (usize == UINT64_MAX) return FA_ERROR_UNSUPPORTED_FORMAT;
    res = feed_init(&f, in);
    if (res != FA_OK) return res;
    if (feed_ensure(&f, 9) < 9 || (hdr = lzma_zip_header(f.p, f.n, props)) == 0) { feed_free(&f); return f.err ? FA_ERROR_IO : FA_ERROR_UNSUPPORTED_FORMAT; }
    feed_use(&f, hdr);
    res = lzma_dict_limit(props, usize);
    if (res != FA_OK) { feed_free(&f); return res; }
    out = (uint8_t*)malloc(CODEC_CHUNK);
    if (!out) { feed_free(&f); return FA_ERROR_OUT_OF_MEMORY; }
    LzmaDec_CONSTRUCT(&d);
    if (LzmaDec_Allocate(&d, props, LZMA_PROPS_SIZE, &g_alloc) != SZ_OK) { free(out); feed_free(&f); return FA_ERROR_OUT_OF_MEMORY; }
    LzmaDec_Init(&d);
    while (done < usize) {
        SizeT dest_len = (SizeT)(usize - done < CODEC_CHUNK ? usize - done : CODEC_CHUNK), src_len;
        ELzmaStatus st;
        SRes r;
        if (cancelled(cancel)) { res = FA_ERROR_CANCELLED; break; }
        if (f.n == 0) feed_ensure(&f, 1);
        if (f.err) { res = FA_ERROR_IO; break; }
        src_len = f.n > (size_t)1 << 30 ? (size_t)1 << 30 : f.n;
        r = LzmaDec_DecodeToBuf(&d, out, &dest_len, f.p, &src_len, LZMA_FINISH_ANY, &st);
        feed_use(&f, src_len);
        if (r != SZ_OK) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        done += dest_len;
        if (out_put(o, out, dest_len)) { if (o->overflow) res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        if (dest_len == 0 && src_len == 0) {
            if (f.n == 0 && f.in.left == 0) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }   /* truncated */
            if (st == LZMA_STATUS_FINISHED_WITH_MARK) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }   /* ended early */
            if (f.n != 0) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        }
    }
    LzmaDec_Free(&d, &g_alloc);
    free(out);
    feed_free(&f);
    return res;
}

static fa_result lzma_zip_buffer(const uint8_t* mem, size_t n, uint8_t* out, size_t out_size)
{
    CLzmaDec d;
    uint8_t props[5];
    size_t hdr = lzma_zip_header(mem, n, props), done = 0;
    SRes r;
    fa_result res = FA_OK;
    if (!hdr) return FA_ERROR_UNSUPPORTED_FORMAT;
    mem += hdr;
    n -= hdr;
    res = lzma_dict_limit(props, out_size);
    if (res != FA_OK) return res;
    LzmaDec_CONSTRUCT(&d);
    if (LzmaDec_AllocateProbs(&d, props, LZMA_PROPS_SIZE, &g_alloc) != SZ_OK) return FA_ERROR_OUT_OF_MEMORY;
    /* The output is the dictionary: nothing to copy and no window to allocate. */
    d.dic = out;
    d.dicBufSize = out_size;
    LzmaDec_Init(&d);
    d.dicPos = 0;
    while (done < out_size) {
        SizeT src_len = n;
        ELzmaStatus st;
        size_t before = d.dicPos;
        r = LzmaDec_DecodeToDic(&d, out_size, mem, &src_len, LZMA_FINISH_ANY, &st);
        mem += src_len;
        n -= src_len;
        done = d.dicPos;
        if (r != SZ_OK) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        if (done == before && src_len == 0) { res = FA_ERROR_UNSUPPORTED_FORMAT; break; }
    }
    LzmaDec_FreeProbs(&d, &g_alloc);
    return res;
}

static int osink_put(void* ctx, const uint8_t* data, size_t n) { return out_put((outsink*)ctx, data, n); }

/* The compressed input as one block of memory (read from the source when need be), for the
 * decoders that work on its blocks on several cores. *held is what to free. */
static fa_result load_input(fa_cin in, fa_cin* out, uint8_t** held)
{
    *held = NULL;
    *out = in;
    if (in.mem) return FA_OK;
    *held = (uint8_t*)malloc(in.left ? (size_t)in.left : 1);
    if (!*held) return FA_ERROR_OUT_OF_MEMORY;
    if (fa_source_read(in.src, *held, (size_t)in.left) != (int64_t)in.left) { free(*held); *held = NULL; return FA_ERROR_IO; }
    out->mem = *held;
    out->src = NULL;
    return FA_OK;
}

static int worth_parallel(fa_cin in)
{
    return in.left >= FA_CODEC_PARALLEL_MIN && in.left <= FA_CODEC_PARALLEL_INPUT_MAX && fa_cpu_count() > 1;
}

/* Passes on what comes after the first `skip` bytes (a decode that starts over). */
typedef struct skipper { outsink* o; uint64_t skip; } skipper;
static int skipper_put(void* ctx, const uint8_t* d, size_t n)
{
    skipper* k = (skipper*)ctx;
    if (k->skip >= n) { k->skip -= n; return 0; }
    d += k->skip;
    n -= (size_t)k->skip;
    k->skip = 0;
    return out_put(k->o, d, n);
}

static fa_result bz_sink(fa_cin in, outsink* o, const volatile int32_t* cancel)
{
    fa_cin m;
    uint8_t* held;
    fa_bzblock* blocks = NULL;
    uint32_t nb = 0;
    fa_result r;
    if (!worth_parallel(in)) return bz_sequential(in, o, cancel);
    r = load_input(in, &m, &held);
    if (r != FA_OK) return r;
    r = fa_bz_scan(m.mem, (size_t)m.left, &blocks, &nb);
    if (r == FA_OK && nb > 1) {
        int stopped = 0;
        r = fa_bz_decode_blocks(m.mem, (size_t)m.left, blocks, 0, nb, fa_codec_threads(nb), osink_put, o, cancel, &stopped);
        free(blocks);
        /* A magic number that only looked like a block start fails the parallel decode, maybe late: the plain
         * decoder starts over and skips what was delivered. */
        if (r == FA_ERROR_UNSUPPORTED_FORMAT && !o->overflow && !o->stopped) {
            skipper k;
            outsink o2;
            k.o = o; k.skip = o->produced;
            o2.fn = skipper_put; o2.ctx = &k; o2.produced = 0; o2.limit = UINT64_MAX; o2.stopped = 0; o2.overflow = 0;
            r = bz_sequential(m, &o2, cancel);
        } else if (o->overflow && r == FA_OK) r = FA_ERROR_UNSUPPORTED_FORMAT;
        free(held);
        return r;
    }
    free(blocks);
    r = bz_sequential(m, o, cancel);
    free(held);
    return r;
}

static fa_result xz_sink(fa_cin in, outsink* o, const volatile int32_t* cancel)
{
    fa_cin m;
    uint8_t* held;
    fa_xzblock* blocks = NULL;
    uint32_t nb = 0, k;
    uint16_t flags = 0;
    fa_source tmp;
    fa_result r;
    int ok = 1;
    if (!worth_parallel(in)) return xz_sequential(in, o, cancel);
    r = load_input(in, &m, &held);
    if (r != FA_OK) return r;
    r = FA_ERROR_UNSUPPORTED_FORMAT;
    if (fa_source_open_memory_ref(&tmp, m.mem, (size_t)m.left) == FA_OK) {
        r = fa_xz_index(&tmp, m.left, &blocks, &nb, &flags);
        fa_source_close(&tmp);
    }
    if (r == FA_OK && nb > 1) {
        for (k = 0; k < nb; k++) if (blocks[k].out_size > ((uint64_t)64 << 20)) ok = 0;
        if (ok) {
            int stopped = 0;
            r = fa_xz_decode_blocks(m.mem, blocks, flags, 0, nb, fa_codec_threads(nb), osink_put, o, cancel, &stopped);
            if (o->overflow && r == FA_OK) r = FA_ERROR_UNSUPPORTED_FORMAT;
            free(blocks);
            free(held);
            return r;
        }
    }
    free(blocks);
    r = xz_sequential(m, o, cancel);
    free(held);
    return r;
}

/* ---- Entry points ------------------------------------------------------------------------ */

fa_result fa_codec_to_sink(int codec, fa_cin in, uint64_t usize, fa_sink_fn sink, void* ctx,
                           const volatile int32_t* cancel, uint64_t* produced, int* stopped)
{
    outsink o;
    fa_result r;
    o.fn = sink; o.ctx = ctx; o.produced = 0; o.limit = usize; o.stopped = 0; o.overflow = 0;
    if (cancelled(cancel)) return FA_ERROR_CANCELLED;
    if (codec == FA_CODEC_BZIP2) r = bz_sink(in, &o, cancel);
    else if (codec == FA_CODEC_BZIP2_PLAIN) r = bz_sequential(in, &o, cancel);
    else if (codec == FA_CODEC_LZMA_ZIP) r = lzma_zip_sink(in, usize, &o, cancel);
    else if (codec == FA_CODEC_XZ) r = xz_sink(in, &o, cancel);
    else r = FA_ERROR_INVALID_ARG;
    if (produced) *produced = o.produced;
    if (stopped) *stopped = o.stopped;
    if (r == FA_OK && !o.stopped && usize != UINT64_MAX && o.produced != usize) r = FA_ERROR_UNSUPPORTED_FORMAT;
    return r;
}

static fa_result xz_buffer(const uint8_t* mem, size_t n, uint8_t* out, size_t out_size)
{
    CXzUnpacker* x;
    SizeT dest_len = out_size, src_len = n;
    ECoderStatus st;
    SRes r;
    fa_result res = FA_OK;
    xz_crc_init();
    if (n >= XZ_STREAM_HEADER_SIZE + 8 && memcmp(mem, XZ_SIG, XZ_SIG_SIZE) == 0 && mem[XZ_STREAM_HEADER_SIZE] != 0) {
        res = xz_check_block_header(mem + XZ_STREAM_HEADER_SIZE, n - XZ_STREAM_HEADER_SIZE);
        if (res != FA_OK) return res;
    }
    x = (CXzUnpacker*)malloc(sizeof(*x));
    if (!x) return FA_ERROR_OUT_OF_MEMORY;
    XzUnpacker_Construct(x, &g_alloc);
    /* The output is the LZMA2 dictionary: no second buffer and no copy. */
    r = XzUnpacker_CodeFull(x, out, &dest_len, mem, &src_len, CODER_FINISH_END, &st);
    if (r == SZ_ERROR_MEM) res = FA_ERROR_OUT_OF_MEMORY;
    else if (r != SZ_OK || dest_len != out_size || !XzUnpacker_IsStreamWasFinished(x)) res = FA_ERROR_UNSUPPORTED_FORMAT;
    XzUnpacker_Free(x);
    free(x);
    return res;
}

fa_result fa_codec_to_buffer(int codec, fa_cin in, uint8_t* out, size_t out_size, const volatile int32_t* cancel)
{
    memsink m;
    uint64_t made = 0;
    int stopped = 0;
    fa_result r;
    if (cancelled(cancel)) return FA_ERROR_CANCELLED;
    if (in.mem && in.left < ((uint64_t)1 << 30) && out_size) {
        if (codec == FA_CODEC_LZMA_ZIP) return lzma_zip_buffer(in.mem, (size_t)in.left, out, out_size);
        if (codec == FA_CODEC_XZ && !worth_parallel(in)) return xz_buffer(in.mem, (size_t)in.left, out, out_size);
    }
    m.out = out; m.cap = out_size; m.pos = 0;
    r = fa_codec_to_sink(codec, in, out_size, memsink_put, &m, cancel, &made, &stopped);
    if (r == FA_OK && (stopped || m.pos != out_size)) r = FA_ERROR_UNSUPPORTED_FORMAT;
    return r;
}
