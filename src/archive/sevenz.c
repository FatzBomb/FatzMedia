/* 7z archives: see sevenz.h. The format is documented in the 7-Zip source's DOC/7zFormat.txt; this is
 * our own reader, written from that description. */
#include "archive/sevenz.h"
#include "archive/archive.h"
#include "LzmaDec.h"
#include "Lzma2Dec.h"
#include <stdlib.h>
#include <string.h>

/* ---- Constants ----------------------------------------------------------------------------- */

enum {
    K_END = 0, K_HEADER = 1, K_ARCHIVEPROPS = 2, K_ADDITIONAL = 3, K_MAINSTREAMS = 4, K_FILES = 5,
    K_PACKINFO = 6, K_UNPACKINFO = 7, K_SUBSTREAMS = 8, K_SIZE = 9, K_CRC = 10, K_FOLDER = 11,
    K_CODERSUNPACKSIZE = 12, K_NUMUNPACKSTREAM = 13, K_EMPTYSTREAM = 14, K_EMPTYFILE = 15, K_ANTI = 16,
    K_NAMES = 17, K_CTIME = 18, K_ATIME = 19, K_MTIME = 20, K_ATTR = 21, K_ENCODED = 23
};

enum { FK_COPY, FK_LZMA, FK_LZMA2, FK_PPMD, FK_AES, FK_OTHER };

#define SZ_SIGNATURE_SIZE 32
#define SZ_MAX_FOLDERS ((uint64_t)1 << 20)
#define SZ_MAX_SUBS ((uint64_t)1 << 24)
#define STEP ((size_t)1 << 20)         /* output decoded between two looks at the cancel flag */
#define IN_CHUNK ((size_t)256 << 10)   /* packed bytes read at a time */
/* Folders from this size are searched for LZMA2 dictionary resets (7-Zip makes one every 64 MB when it
 * encodes on several threads; a single-threaded encode makes none). */
#define RESET_SCAN_MIN ((uint64_t)32 << 20)
#define RESET_MAX ((uint32_t)1 << 20)
#define MEMO_BYTES 65536

#define BAD FA_ERROR_UNSUPPORTED_FORMAT

/* ---- Allocation for the SDK ------------------------------------------------------------------ */

static void* sz_alloc(ISzAllocPtr p, size_t n)
{
    (void)p;
    if ((uint64_t)n > FA_CODEC_MAX_DICT + ((uint64_t)1 << 20)) return NULL;
    return malloc(n ? n : 1);
}
static void sz_free(ISzAllocPtr p, void* a) { (void)p; free(a); }
static const ISzAlloc g_alloc = { sz_alloc, sz_free };

/* ---- Structures --------------------------------------------------------------------------------- */

typedef struct folder {
    int kind;
    uint8_t props[8];
    uint32_t plen;
    uint32_t nout, main_out, npack;   /* while parsing */
    uint64_t unpack;                  /* size of the folder's final output */
    uint64_t pack_pos, pack_size;     /* the (first) packed stream in the source */
    uint32_t crc;
    int has_crc;
    uint32_t nsub;                    /* files with data in it (substreams) */
    uint64_t* rpk;                    /* LZMA2 dictionary resets after the first: packed and unpacked offsets */
    uint64_t* rup;
    uint32_t nreset;
} folder;

/* The decoder of one folder and where it is. */
typedef struct cursor {
    int live;
    int window;                /* 1: decodes through the decoder's own dictionary, 0: into `buf`, which is the dictionary */
    int fixed;                 /* `buf` has exactly the size the caller gave it */
    uint32_t folder;
    int is2;
    CLzma2Dec d2;
    CLzmaDec d1;
    uint8_t* buf;              /* buffered: output offsets [base, pos) */
    size_t cap;
    uint64_t base;
    uint64_t pos;              /* where the decoder is, in the folder's output */
    uint8_t* in;
    size_t in_len, in_off;
    uint64_t in_pos;           /* packed bytes used so far, from the start of the folder's packed stream */
    uint8_t* bounce;           /* window mode */
    uint64_t* stat;            /* counts the bytes decoded (tests, tuning); may be NULL */
} cursor;

struct fa_7z {
    uint32_t nfolders;
    folder* f;
    uint64_t memory_limit;
    uint64_t decoded;          /* bytes the decoders have produced, all folders */
    cursor cur;                /* the cursor kept between reads (several files in a folder) */
    struct { uint32_t folder; uint64_t offset; size_t len; uint8_t* data; int valid; } memo;
};

/* ---- Reading header bytes ---------------------------------------------------------------- */

typedef struct rd {
    const uint8_t* p;
    size_t n, at;
    int bad;
} rd;

static size_t rd_left(const rd* r) { return r->n - r->at; }
static uint8_t r8(rd* r)
{
    if (r->at >= r->n) { r->bad = 1; return 0; }
    return r->p[r->at++];
}
static uint32_t r32(rd* r)
{
    uint32_t v;
    if (rd_left(r) < 4) { r->bad = 1; r->at = r->n; return 0; }
    v = fa_le32(r->p + r->at);
    r->at += 4;
    return v;
}
static uint64_t r64(rd* r)
{
    uint64_t v;
    if (rd_left(r) < 8) { r->bad = 1; r->at = r->n; return 0; }
    v = fa_le64(r->p + r->at);
    r->at += 8;
    return v;
}
static void rskip(rd* r, uint64_t n)
{
    if (n > rd_left(r)) { r->bad = 1; r->at = r->n; return; }
    r->at += (size_t)n;
}
/* 7z's variable-length number: the leading 1 bits of the first byte count the extra bytes. */
static uint64_t rnum(rd* r)
{
    uint8_t first = r8(r), mask = 0x80;
    uint64_t v = 0;
    int i;
    for (i = 0; i < 8; i++) {
        if ((first & mask) == 0) return v + ((uint64_t)(first & (mask - 1u)) << (8 * i));
        v |= (uint64_t)r8(r) << (8 * i);
        mask >>= 1;
    }
    return v;
}
/* n bits, most significant first, into out[0..n). */
static void rbits(rd* r, uint8_t* out, uint64_t n)
{
    uint64_t i;
    uint8_t cur = 0, mask = 0;
    if (n > (uint64_t)rd_left(r) * 8) { r->bad = 1; return; }
    for (i = 0; i < n; i++) {
        if (mask == 0) { cur = r8(r); mask = 0x80; }
        out[i] = (cur & mask) != 0;
        mask >>= 1;
    }
}
/* "All defined" byte, else a bit vector. */
static void rdefined(rd* r, uint8_t* out, uint64_t n)
{
    if (r8(r)) memset(out, 1, (size_t)n);
    else rbits(r, out, n);
}
static void rdigests(rd* r, uint64_t n, uint8_t* has, uint32_t* crc)
{
    uint64_t i;
    rdefined(r, has, n);
    for (i = 0; i < n && !r->bad; i++)
        if (has[i]) crc[i] = r32(r);
}

/* ---- Streams info --------------------------------------------------------------------------- */

typedef struct sinfo {
    uint64_t pack_pos;
    uint32_t npack;
    uint64_t* pack_size;
    uint32_t nf;
    folder* f;
    uint64_t nsubs;
    uint64_t* sub_size;
    uint32_t* sub_crc;
    uint8_t* sub_has;
} sinfo;

static void sinfo_free(sinfo* s)
{
    uint32_t i;
    free(s->pack_size);
    if (s->f) for (i = 0; i < s->nf; i++) { free(s->f[i].rpk); free(s->f[i].rup); }
    free(s->f);
    free(s->sub_size);
    free(s->sub_crc);
    free(s->sub_has);
    memset(s, 0, sizeof(*s));
}

static fa_result parse_folder(rd* r, folder* f)
{
    uint32_t nc = (uint32_t)rnum(r), i, tin = 0, tout = 0, npairs, j;
    uint64_t first_id = 0;
    uint8_t used_out[64];
    int aes = 0;
    memset(used_out, 0, sizeof(used_out));
    if (r->bad || nc == 0 || nc > 32) return BAD;
    for (i = 0; i < nc; i++) {
        uint8_t fl = r8(r);
        uint32_t idn = fl & 15u, nin = 1, nout = 1;
        uint64_t id = 0;
        if (r->bad || (fl & 0x80) || idn > 8) return BAD;
        for (j = 0; j < idn; j++) id = (id << 8) | r8(r);
        if (fl & 0x10) {
            uint64_t a = rnum(r), b = rnum(r);
            if (a == 0 || b == 0 || a > 32 || b > 32) return BAD;
            nin = (uint32_t)a;
            nout = (uint32_t)b;
        }
        if (fl & 0x20) {
            uint64_t psz = rnum(r);
            if (psz > rd_left(r)) return BAD;
            if (i == 0 && psz <= sizeof(f->props)) { memcpy(f->props, r->p + r->at, (size_t)psz); f->plen = (uint32_t)psz; }
            rskip(r, psz);
        }
        if (i == 0) first_id = id;
        if (id == 0x06F10701u) aes = 1;
        tin += nin;
        tout += nout;
        if (tin > 64 || tout > 64) return BAD;
    }
    npairs = tout - 1;
    for (i = 0; i < npairs; i++) {
        uint64_t in_i = rnum(r), out_i = rnum(r);
        if (in_i >= tin || out_i >= tout) return BAD;
        used_out[out_i] = 1;
    }
    if (tin < npairs + 1) return BAD;
    f->npack = tin - npairs;
    if (f->npack > 1)
        for (i = 0; i < f->npack; i++)
            if (rnum(r) >= tin) return BAD;
    for (i = 0; i < tout && used_out[i]; i++) {}
    if (r->bad || i == tout) return BAD;
    f->nout = tout;
    f->main_out = i;
    f->kind = FK_OTHER;
    if (aes) f->kind = FK_AES;
    else if (nc == 1 && tin == 1 && tout == 1) {
        if (first_id == 0x00) f->kind = FK_COPY;
        else if (first_id == 0x030101 && f->plen == 5) f->kind = FK_LZMA;
        else if (first_id == 0x21 && f->plen == 1 && f->props[0] <= 40) f->kind = FK_LZMA2;
        else if (first_id == 0x030401) f->kind = FK_PPMD;
    }
    return FA_OK;
}

static fa_result parse_packinfo(rd* r, sinfo* s)
{
    uint64_t n, i;
    s->pack_pos = rnum(r);
    n = rnum(r);
    if (r->bad || n > SZ_MAX_FOLDERS * 4 || n > rd_left(r)) return BAD;
    s->npack = (uint32_t)n;
    s->pack_size = (uint64_t*)calloc(n ? (size_t)n : 1, sizeof(uint64_t));
    if (!s->pack_size) return FA_ERROR_OUT_OF_MEMORY;
    for (;;) {
        uint8_t id = r8(r);
        if (r->bad) return BAD;
        if (id == K_END) break;
        if (id == K_SIZE) {
            for (i = 0; i < n; i++) s->pack_size[i] = rnum(r);
        } else if (id == K_CRC) {
            uint8_t* has = (uint8_t*)malloc(n ? (size_t)n : 1);
            uint32_t* crc = (uint32_t*)malloc((n ? (size_t)n : 1) * 4);
            if (!has || !crc) { free(has); free(crc); return FA_ERROR_OUT_OF_MEMORY; }
            rdigests(r, n, has, crc);
            free(has);
            free(crc);
        } else return BAD;
    }
    return r->bad ? BAD : FA_OK;
}

static fa_result parse_unpackinfo(rd* r, sinfo* s)
{
    uint64_t n, i, j;
    uint8_t id = r8(r);
    if (r->bad || id != K_FOLDER) return BAD;
    n = rnum(r);
    if (r->bad || n > SZ_MAX_FOLDERS || n > rd_left(r)) return BAD;
    if (r8(r) != 0) return BAD;   /* data in another stream: never written */
    s->nf = (uint32_t)n;
    s->f = (folder*)calloc(n ? (size_t)n : 1, sizeof(folder));
    if (!s->f) return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < n; i++) {
        fa_result res = parse_folder(r, &s->f[i]);
        if (res != FA_OK) return res;
    }
    id = r8(r);
    if (r->bad || id != K_CODERSUNPACKSIZE) return BAD;
    for (i = 0; i < n; i++) {
        for (j = 0; j < s->f[i].nout; j++) {
            uint64_t v = rnum(r);
            if (j == s->f[i].main_out) s->f[i].unpack = v;
        }
        if (s->f[i].unpack > ((uint64_t)1 << 62)) return BAD;
    }
    for (;;) {
        id = r8(r);
        if (r->bad) return BAD;
        if (id == K_END) break;
        if (id == K_CRC) {
            uint8_t* has = (uint8_t*)malloc((size_t)n);
            uint32_t* crc = (uint32_t*)malloc((size_t)n * 4);
            if (!has || !crc) { free(has); free(crc); return FA_ERROR_OUT_OF_MEMORY; }
            rdigests(r, n, has, crc);
            for (i = 0; i < n; i++) { s->f[i].has_crc = has[i]; s->f[i].crc = crc[i]; }
            free(has);
            free(crc);
        } else return BAD;
    }
    return r->bad ? BAD : FA_OK;
}

/* The files of each folder: counts, sizes (the last is what is left) and checksums. */
static fa_result alloc_subs(sinfo* s)
{
    uint32_t i;
    uint64_t total = 0, k = 0;
    for (i = 0; i < s->nf; i++) total += s->f[i].nsub;
    if (total > SZ_MAX_SUBS) return FA_ERROR_TOO_LARGE;
    s->nsubs = total;
    s->sub_size = (uint64_t*)calloc(total ? (size_t)total : 1, sizeof(uint64_t));
    s->sub_crc = (uint32_t*)calloc(total ? (size_t)total : 1, sizeof(uint32_t));
    s->sub_has = (uint8_t*)calloc(total ? (size_t)total : 1, 1);
    if (!s->sub_size || !s->sub_crc || !s->sub_has) return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < s->nf; i++) {
        if (s->f[i].nsub == 1) {
            s->sub_size[k] = s->f[i].unpack;
            s->sub_has[k] = (uint8_t)s->f[i].has_crc;
            s->sub_crc[k] = s->f[i].crc;
        }
        k += s->f[i].nsub;
    }
    return FA_OK;
}

static fa_result parse_substreams(rd* r, sinfo* s)
{
    uint32_t i;
    uint64_t k, n_digest = 0, j;
    int have_counts = 0;
    for (i = 0; i < s->nf; i++) s->f[i].nsub = 1;
    for (;;) {
        uint8_t id = r8(r);
        if (r->bad) return BAD;
        if (id == K_END) break;
        if (id == K_NUMUNPACKSTREAM) {
            for (i = 0; i < s->nf; i++) {
                uint64_t v = rnum(r);
                if (v > SZ_MAX_SUBS) return FA_ERROR_TOO_LARGE;
                s->f[i].nsub = (uint32_t)v;
            }
            have_counts = 1;
        } else if (id == K_SIZE) {
            fa_result res;
            if (!s->sub_size && (res = alloc_subs(s)) != FA_OK) return res;
            k = 0;
            for (i = 0; i < s->nf; i++) {
                uint64_t sum = 0;
                if (s->f[i].nsub > 1)
                    for (j = 0; j + 1 < s->f[i].nsub; j++) {
                        uint64_t v = rnum(r);
                        if (v > s->f[i].unpack - sum) return BAD;
                        s->sub_size[k + j] = v;
                        sum += v;
                    }
                if (s->f[i].nsub > 1) s->sub_size[k + s->f[i].nsub - 1] = s->f[i].unpack - sum;
                k += s->f[i].nsub;
            }
        } else if (id == K_CRC) {
            fa_result res;
            uint8_t* has;
            uint32_t* crc;
            if (!s->sub_size && (res = alloc_subs(s)) != FA_OK) return res;
            for (i = 0, n_digest = 0; i < s->nf; i++)
                if (!(s->f[i].nsub == 1 && s->f[i].has_crc)) n_digest += s->f[i].nsub;
            if (n_digest > SZ_MAX_SUBS) return FA_ERROR_TOO_LARGE;
            has = (uint8_t*)malloc(n_digest ? (size_t)n_digest : 1);
            crc = (uint32_t*)malloc((n_digest ? (size_t)n_digest : 1) * 4);
            if (!has || !crc) { free(has); free(crc); return FA_ERROR_OUT_OF_MEMORY; }
            rdigests(r, n_digest, has, crc);
            if (!r->bad) {
                uint64_t d = 0;
                k = 0;
                for (i = 0; i < s->nf; i++) {
                    if (!(s->f[i].nsub == 1 && s->f[i].has_crc))
                        for (j = 0; j < s->f[i].nsub; j++, d++) { s->sub_has[k + j] = has[d]; s->sub_crc[k + j] = crc[d]; }
                    k += s->f[i].nsub;
                }
            }
            free(has);
            free(crc);
        } else return BAD;
    }
    if (r->bad) return BAD;
    (void)have_counts;
    return s->sub_size ? FA_OK : alloc_subs(s);
}

static fa_result parse_streams(rd* r, sinfo* s)
{
    fa_result res = FA_OK;
    int have_subs = 0;
    for (;;) {
        uint8_t id = r8(r);
        if (r->bad) return BAD;
        if (id == K_END) break;
        if (id == K_PACKINFO) res = parse_packinfo(r, s);
        else if (id == K_UNPACKINFO) res = parse_unpackinfo(r, s);
        else if (id == K_SUBSTREAMS) {
            if (!s->f) return BAD;
            res = parse_substreams(r, s);
            have_subs = 1;
        } else return BAD;
        if (res != FA_OK) return res;
    }
    if (s->f && !have_subs) {
        uint32_t i;
        for (i = 0; i < s->nf; i++) s->f[i].nsub = 1;
        res = alloc_subs(s);
        if (res != FA_OK) return res;
    }
    return FA_OK;
}

/* Where each folder's packed stream is in the source, and that all of it is there. */
static fa_result place_folders(sinfo* s, uint64_t src_len)
{
    uint32_t i, k = 0, j;
    uint64_t* off = (uint64_t*)malloc(((size_t)s->npack + 1) * sizeof(uint64_t));
    uint64_t at = SZ_SIGNATURE_SIZE;
    if (!off) return FA_ERROR_OUT_OF_MEMORY;
    if (s->pack_pos > src_len || src_len - s->pack_pos < SZ_SIGNATURE_SIZE) { free(off); return BAD; }
    at += s->pack_pos;
    for (j = 0; j < s->npack; j++) {
        off[j] = at;
        if (s->pack_size[j] > src_len - at) { free(off); return BAD; }
        at += s->pack_size[j];
    }
    for (i = 0; i < s->nf; i++) {
        folder* f = &s->f[i];
        if (f->npack > s->npack - k) { free(off); return BAD; }
        f->pack_pos = off[k];
        f->pack_size = s->pack_size[k];
        if (f->npack > 1) for (j = 1; j < f->npack; j++) f->pack_size += s->pack_size[k + j];
        k += f->npack;
        if (f->kind == FK_COPY && f->pack_size < f->unpack) { free(off); return BAD; }
    }
    free(off);
    return FA_OK;
}

/* ---- Decoder: setup and input -------------------------------------------------------------------- */

static CLzmaDec* cur_d(cursor* c) { return c->is2 ? &c->d2.decoder : &c->d1; }

static uint64_t lzma2_dict(uint8_t p) { return p >= 40 ? 0xFFFFFFFFull : (uint64_t)(2 | (p & 1)) << (p / 2 + 11); }
static uint8_t lzma2_prop_for(uint64_t need)
{
    uint8_t p;
    for (p = 0; p < 40; p++) if (lzma2_dict(p) >= need) return p;
    return 40;
}

static void cur_release(cursor* c)
{
    if (!c->live) return;
    if (c->window) { if (c->is2) Lzma2Dec_Free(&c->d2, &g_alloc); else LzmaDec_Free(&c->d1, &g_alloc); }
    else { if (c->is2) Lzma2Dec_FreeProbs(&c->d2, &g_alloc); else LzmaDec_FreeProbs(&c->d1, &g_alloc); }
    c->live = 0;
}

static void cur_free(cursor* c)
{
    cur_release(c);
    free(c->buf);
    free(c->in);
    free(c->bounce);
    memset(c, 0, sizeof(*c));
}

/* A decoder for the folder: into its own buffer when `window` is 0 (the buffer is attached by the caller),
 * else with a dictionary of its own, as small as the folder allows. */
static fa_result cur_setup(cursor* c, const folder* f, uint32_t index, int window, uint64_t memory_limit)
{
    uint8_t props[5];
    SRes r;
    cur_release(c);
    c->window = window;
    c->folder = index;
    c->is2 = f->kind == FK_LZMA2;
    if (c->is2) {
        uint8_t p = f->props[0];
        if (window) {
            uint64_t need = f->unpack < 4096 ? 4096 : f->unpack;
            if (lzma2_dict(p) > need) p = lzma2_prop_for(need);
            if (lzma2_dict(p) > memory_limit || lzma2_dict(p) > FA_CODEC_MAX_DICT) return FA_ERROR_TOO_LARGE;
        }
        Lzma2Dec_CONSTRUCT(&c->d2);
        r = window ? Lzma2Dec_Allocate(&c->d2, p, &g_alloc) : Lzma2Dec_AllocateProbs(&c->d2, p, &g_alloc);
    } else {
        uint32_t dict;
        memcpy(props, f->props, 5);
        dict = fa_le32(props + 1);
        if (window) {
            uint64_t need = f->unpack < 4096 ? 4096 : f->unpack;
            if (dict > need) {
                dict = (uint32_t)need;
                props[1] = (uint8_t)dict; props[2] = (uint8_t)(dict >> 8); props[3] = (uint8_t)(dict >> 16); props[4] = (uint8_t)(dict >> 24);
            }
            if (dict > memory_limit || dict > FA_CODEC_MAX_DICT) return FA_ERROR_TOO_LARGE;
        }
        LzmaDec_CONSTRUCT(&c->d1);
        r = window ? LzmaDec_Allocate(&c->d1, props, 5, &g_alloc) : LzmaDec_AllocateProbs(&c->d1, props, 5, &g_alloc);
    }
    if (r != SZ_OK) return r == SZ_ERROR_MEM ? FA_ERROR_OUT_OF_MEMORY : BAD;
    c->live = 1;
    if (window && !c->bounce) {
        c->bounce = (uint8_t*)malloc(STEP);
        if (!c->bounce) { cur_release(c); return FA_ERROR_OUT_OF_MEMORY; }
    }
    if (!c->in) {
        c->in = (uint8_t*)malloc(IN_CHUNK);
        if (!c->in) { cur_release(c); return FA_ERROR_OUT_OF_MEMORY; }
    }
    return FA_OK;
}

/* Starts decoding at a point where nothing earlier is needed: packed offset `pk` is the start of a LZMA2 chunk
 * that resets the dictionary and `up` the output offset of it (both 0 for the start of the folder). */
static void cur_restart(cursor* c, uint64_t pk, uint64_t up)
{
    if (c->is2) Lzma2Dec_Init(&c->d2); else LzmaDec_Init(&c->d1);
    cur_d(c)->dicPos = 0;
    c->in_len = c->in_off = 0;
    c->in_pos = pk;
    c->pos = c->base = up;
}

static fa_result cur_fill(cursor* c, const folder* f, fa_source* src)
{
    uint64_t left;
    int64_t n;
    if (c->in_off < c->in_len) return FA_OK;
    left = f->pack_size > c->in_pos ? f->pack_size - c->in_pos : 0;
    if (left == 0) return BAD;   /* the packed data ends before the output does */
    if (fa_source_seek(src, (int64_t)(f->pack_pos + c->in_pos)) != FA_OK) return FA_ERROR_IO;
    n = fa_source_read(src, c->in, left < IN_CHUNK ? (size_t)left : IN_CHUNK);
    if (n < 0) return (fa_result)n;
    if (n == 0) return BAD;
    c->in_off = 0;
    c->in_len = (size_t)n;
    return FA_OK;
}

static int cancelled(const volatile int32_t* cancel) { return cancel && *cancel; }

/* Decodes into `buf` (the dictionary) until the decoder is at output offset `target`. */
static fa_result buffered_to(cursor* c, const folder* f, fa_source* src, uint64_t target, const volatile int32_t* cancel)
{
    while (c->pos < target) {
        size_t fill = (size_t)(c->pos - c->base), want, before, produced;
        SizeT slen;
        ELzmaStatus st;
        SRes r;
        CLzmaDec* d;
        fa_result res;
        if (cancelled(cancel)) return FA_ERROR_CANCELLED;
        res = cur_fill(c, f, src);
        if (res != FA_OK) return res;
        want = target - c->pos < STEP ? (size_t)(target - c->pos) : STEP;
        if (fill + want > c->cap) {
            size_t ncap;
            uint8_t* nb;
            uint64_t max = f->unpack - c->base;
            if (c->fixed) return BAD;
            ncap = c->cap * 2;
            if (ncap < fill + want) ncap = fill + want;
            if ((uint64_t)ncap > max) ncap = (size_t)max;
            if (ncap < fill + want) return BAD;
            nb = (uint8_t*)realloc(c->buf, ncap);
            if (!nb) return FA_ERROR_OUT_OF_MEMORY;
            c->buf = nb;
            c->cap = ncap;
        }
        d = cur_d(c);
        d->dic = c->buf;
        d->dicBufSize = c->cap;
        slen = (SizeT)(c->in_len - c->in_off);
        before = d->dicPos;
        if (c->is2) r = Lzma2Dec_DecodeToDic(&c->d2, fill + want, c->in + c->in_off, &slen, LZMA_FINISH_ANY, &st);
        else r = LzmaDec_DecodeToDic(&c->d1, fill + want, c->in + c->in_off, &slen, LZMA_FINISH_ANY, &st);
        c->in_off += slen;
        c->in_pos += slen;
        produced = d->dicPos - before;
        c->pos += produced;
        if (c->stat) *c->stat += produced;
        if (r != SZ_OK) return BAD;
        if (produced == 0 && slen == 0) return BAD;   /* ended early, or will not go on */
    }
    return FA_OK;
}

/* Decodes through the window from the cursor to output offset `to`, giving [from, to) to sink. */
static fa_result window_to(cursor* c, const folder* f, fa_source* src, uint64_t from, uint64_t to, fa_sink_fn sink, void* ctx,
                           const volatile int32_t* cancel)
{
    while (c->pos < to) {
        SizeT dlen = (SizeT)(to - c->pos < STEP ? to - c->pos : STEP), slen;
        ELzmaStatus st;
        SRes r;
        uint64_t start = c->pos;
        fa_result res;
        if (cancelled(cancel)) return FA_ERROR_CANCELLED;
        res = cur_fill(c, f, src);
        if (res != FA_OK) return res;
        slen = (SizeT)(c->in_len - c->in_off);
        if (c->is2) r = Lzma2Dec_DecodeToBuf(&c->d2, c->bounce, &dlen, c->in + c->in_off, &slen, LZMA_FINISH_ANY, &st);
        else r = LzmaDec_DecodeToBuf(&c->d1, c->bounce, &dlen, c->in + c->in_off, &slen, LZMA_FINISH_ANY, &st);
        c->in_off += slen;
        c->in_pos += slen;
        c->pos += dlen;
        if (c->stat) *c->stat += dlen;
        if (r != SZ_OK) return BAD;
        if (dlen == 0 && slen == 0) return BAD;
        if (c->pos > from) {
            uint64_t s = start > from ? start : from;
            if (sink(ctx, c->bounce + (s - start), (size_t)(c->pos - s))) return FA_OK;
        }
    }
    return FA_OK;
}

/* ---- LZMA2 dictionary resets --------------------------------------------------------------------- */

/* Hops over the chunk headers of a LZMA2 stream (about one per 64 KB packed), noting every chunk that
 * resets the dictionary. The table is kept only when the chunks add up to the folder's size. */
static void scan_resets(folder* f, fa_source* src, const volatile int32_t* cancel)
{
    uint64_t pk = 0, up = 0;
    uint64_t* rpk = NULL;
    uint64_t* rup = NULL;
    uint32_t n = 0, cap = 0;
    int ok = 0;
    if (f->kind != FK_LZMA2 || f->unpack < RESET_SCAN_MIN || f->nsub < 2) return;
    while (pk < f->pack_size) {
        uint8_t b[6];
        size_t want = f->pack_size - pk < 6 ? (size_t)(f->pack_size - pk) : 6, hdr;
        int64_t got;
        uint8_t ctrl;
        uint64_t usize, psize;
        int reset;
        if (cancelled(cancel)) break;
        if (fa_source_seek(src, (int64_t)(f->pack_pos + pk)) != FA_OK) break;
        got = fa_source_read(src, b, want);
        if (got < 1) break;
        ctrl = b[0];
        if (ctrl == 0) { ok = up == f->unpack; break; }
        if (ctrl == 1 || ctrl == 2) {
            hdr = 3;
            if (got < 3) break;
            usize = psize = (uint64_t)((b[1] << 8) | b[2]) + 1;
            reset = ctrl == 1;
        } else if (ctrl >= 0x80) {
            hdr = ctrl >= 0xC0 ? 6 : 5;
            if ((size_t)got < hdr) break;
            usize = (((uint64_t)(ctrl & 0x1F) << 16) | (uint64_t)(b[1] << 8) | b[2]) + 1;
            psize = (uint64_t)((b[3] << 8) | b[4]) + 1;
            reset = ctrl >= 0xE0;
        } else break;
        if (reset && up > 0) {
            if (n == cap) {
                uint32_t ncap = cap ? cap * 2 : 16;
                uint64_t* a;
                uint64_t* c2;
                if (ncap > RESET_MAX) break;
                a = (uint64_t*)realloc(rpk, ncap * sizeof(uint64_t));
                if (a) rpk = a;
                c2 = (uint64_t*)realloc(rup, ncap * sizeof(uint64_t));
                if (c2) rup = c2;
                if (!a || !c2) break;
                cap = ncap;
            }
            rpk[n] = pk;
            rup[n] = up;
            n++;
        }
        pk += hdr + psize;
        up += usize;
    }
    if (ok && n > 0) { f->rpk = rpk; f->rup = rup; f->nreset = n; }
    else { free(rpk); free(rup); }
}

/* The last reset that starts at or before output offset `at`: returns 1 and its offsets, else 0 (the start). */
static int last_reset(const folder* f, uint64_t at, uint64_t* pk, uint64_t* up)
{
    uint32_t lo = 0, hi = f->nreset;
    if (f->nreset == 0 || f->rup[0] > at) return 0;
    while (hi - lo > 1) {
        uint32_t mid = lo + (hi - lo) / 2;
        if (f->rup[mid] <= at) lo = mid; else hi = mid;
    }
    *pk = f->rpk[lo];
    *up = f->rup[lo];
    return 1;
}

/* ---- Opening ------------------------------------------------------------------------------------ */

static fa_result read_at(fa_source* src, uint64_t off, uint8_t* buf, size_t n)
{
    size_t got = 0;
    if (fa_source_seek(src, (int64_t)off) != FA_OK) return FA_ERROR_IO;
    while (got < n) {
        int64_t r = fa_source_read(src, buf + got, n - got);
        if (r < 0) return (fa_result)r;
        if (r == 0) return BAD;
        got += (size_t)r;
    }
    return FA_OK;
}

static fa_result decode_whole(const folder* f, uint32_t index, fa_source* src, uint8_t* out, size_t size, const volatile int32_t* cancel,
                              uint64_t* stat);

static void put_utf8(char* out, size_t* at, uint32_t c)
{
    if (c < 0x80) out[(*at)++] = (char)c;
    else if (c < 0x800) { out[(*at)++] = (char)(0xC0 | (c >> 6)); out[(*at)++] = (char)(0x80 | (c & 63)); }
    else if (c < 0x10000) { out[(*at)++] = (char)(0xE0 | (c >> 12)); out[(*at)++] = (char)(0x80 | ((c >> 6) & 63)); out[(*at)++] = (char)(0x80 | (c & 63)); }
    else { out[(*at)++] = (char)(0xF0 | (c >> 18)); out[(*at)++] = (char)(0x80 | ((c >> 12) & 63)); out[(*at)++] = (char)(0x80 | ((c >> 6) & 63)); out[(*at)++] = (char)(0x80 | (c & 63)); }
}

/* One UTF-16LE name (up to its zero) as UTF-8, then the archive layer's normalization. */
static char* read_name(rd* r)
{
    size_t start = r->at, units = 0, at = 0, i;
    char* tmp;
    char* out;
    while (r->at + 1 < r->n && !(r->p[r->at] == 0 && r->p[r->at + 1] == 0)) { r->at += 2; units++; }
    if (r->at + 1 >= r->n) { r->bad = 1; return NULL; }
    tmp = (char*)malloc(units * 3 + 1);
    if (!tmp) { r->bad = 1; return NULL; }
    for (i = 0; i < units; i++) {
        uint32_t c = fa_le16(r->p + start + i * 2);
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < units) {
            uint32_t d = fa_le16(r->p + start + (i + 1) * 2);
            if (d >= 0xDC00 && d < 0xE000) { c = 0x10000 + ((c - 0xD800) << 10) + (d - 0xDC00); i++; }
            else c = 0xFFFD;
        } else if (c >= 0xD800 && c < 0xE000) c = 0xFFFD;
        if (c == 0) c = '_';
        put_utf8(tmp, &at, c);
    }
    tmp[at] = 0;
    r->at += 2;
    out = fa_name_to_utf8(tmp, 1);
    free(tmp);
    if (!out) r->bad = 1;
    return out;
}

static int64_t filetime_ns(uint64_t ft)
{
    const uint64_t epoch = 116444736000000000ull;
    if (ft < epoch || ft - epoch > (uint64_t)INT64_MAX / 100) return INT64_MIN;
    return (int64_t)(ft - epoch) * 100;
}

typedef struct files {
    uint64_t n;
    uint8_t* empty;
    uint8_t* efile;
    uint8_t* anti;
    char** names;
    int64_t* mtime;
    int64_t* ctime;
    int64_t* atime;
    uint32_t* attr;
    uint8_t* attr_has;
    uint64_t nempty;
} files;

static void files_free(files* fl)
{
    uint64_t i;
    if (fl->names) for (i = 0; i < fl->n; i++) free(fl->names[i]);
    free(fl->names); free(fl->empty); free(fl->efile); free(fl->anti);
    free(fl->mtime); free(fl->ctime); free(fl->atime); free(fl->attr); free(fl->attr_has);
    memset(fl, 0, sizeof(*fl));
}

static void read_times(rd* r, uint64_t n, int64_t* out)
{
    uint8_t* has = (uint8_t*)malloc(n ? (size_t)n : 1);
    uint64_t i;
    if (!has) { r->bad = 1; return; }
    rdefined(r, has, n);
    if (r8(r) != 0) r->bad = 1;
    for (i = 0; i < n && !r->bad; i++)
        if (has[i]) out[i] = filetime_ns(r64(r));
    free(has);
}

static fa_result parse_files(rd* r, files* fl)
{
    uint64_t n = rnum(r), i;
    if (r->bad) return BAD;
    if (n > FA_7Z_MAX_ENTRIES) return FA_ERROR_TOO_LARGE;
    fl->n = n;
    fl->empty = (uint8_t*)calloc(n ? (size_t)n : 1, 1);
    fl->efile = (uint8_t*)calloc(n ? (size_t)n : 1, 1);
    fl->anti = (uint8_t*)calloc(n ? (size_t)n : 1, 1);
    fl->names = (char**)calloc(n ? (size_t)n : 1, sizeof(char*));
    fl->mtime = (int64_t*)malloc((n ? (size_t)n : 1) * 8);
    fl->ctime = (int64_t*)malloc((n ? (size_t)n : 1) * 8);
    fl->atime = (int64_t*)malloc((n ? (size_t)n : 1) * 8);
    fl->attr = (uint32_t*)calloc(n ? (size_t)n : 1, 4);
    fl->attr_has = (uint8_t*)calloc(n ? (size_t)n : 1, 1);
    if (!fl->empty || !fl->efile || !fl->anti || !fl->names || !fl->mtime || !fl->ctime || !fl->atime || !fl->attr || !fl->attr_has)
        return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < n; i++) fl->mtime[i] = fl->ctime[i] = fl->atime[i] = INT64_MIN;
    for (;;) {
        uint8_t id = r8(r);
        uint64_t size;
        rd s;
        if (r->bad) return BAD;
        if (id == K_END) break;
        size = rnum(r);
        if (r->bad || size > rd_left(r)) return BAD;
        s.p = r->p + r->at; s.n = (size_t)size; s.at = 0; s.bad = 0;
        if (id == K_EMPTYSTREAM) {
            rbits(&s, fl->empty, n);
            for (i = 0, fl->nempty = 0; i < n; i++) fl->nempty += fl->empty[i];
        } else if (id == K_EMPTYFILE || id == K_ANTI) {
            uint8_t* bits = (uint8_t*)calloc(fl->nempty ? (size_t)fl->nempty : 1, 1);
            uint64_t j = 0;
            if (!bits) return FA_ERROR_OUT_OF_MEMORY;
            rbits(&s, bits, fl->nempty);
            for (i = 0; i < n; i++)
                if (fl->empty[i]) { (id == K_EMPTYFILE ? fl->efile : fl->anti)[i] = bits[j]; j++; }
            free(bits);
        } else if (id == K_NAMES) {
            if (r8(&s) != 0) return BAD;
            for (i = 0; i < n && !s.bad; i++) fl->names[i] = read_name(&s);
        } else if (id == K_MTIME) read_times(&s, n, fl->mtime);
        else if (id == K_CTIME) read_times(&s, n, fl->ctime);
        else if (id == K_ATIME) read_times(&s, n, fl->atime);
        else if (id == K_ATTR) {
            rdefined(&s, fl->attr_has, n);
            if (r8(&s) != 0) return BAD;
            for (i = 0; i < n && !s.bad; i++)
                if (fl->attr_has[i]) fl->attr[i] = r32(&s);
        }
        if (s.bad) return BAD;
        r->at += (size_t)size;
    }
    for (i = 0; i < n; i++) if (!fl->names[i]) return BAD;
    return FA_OK;
}

/* A directory, a symlink or a deletion marker: not a file. */
static int not_a_file(const files* fl, uint64_t i)
{
    if (fl->anti[i]) return 1;
    if (fl->empty[i] && !fl->efile[i]) return 1;
    if (fl->attr_has[i]) {
        uint32_t a = fl->attr[i];
        if (a & 0x10) return 1;
        if (a & 0x8000) {
            uint32_t mode = (a >> 16) & 0xF000;
            if (mode == 0x4000 || mode == 0xA000) return 1;
        }
    }
    return 0;
}

void fa_7z_free(fa_7z* z)
{
    uint32_t i;
    if (!z) return;
    cur_free(&z->cur);
    free(z->memo.data);
    for (i = 0; i < z->nfolders; i++) { free(z->f[i].rpk); free(z->f[i].rup); }
    free(z->f);
    free(z);
}

static void put_entries(const sinfo* si, files* fl, fa_7z* z, fa_zip_entry** out_e, char*** out_names, uint32_t* out_count,
                        fa_result* res)
{
    fa_zip_entry* e;
    char** names;
    uint64_t i, kept = 0, sub = 0;
    uint32_t fi = 0, k_in = 0, ei = 0;
    uint64_t off = 0;
    *res = FA_OK;
    for (i = 0; i < fl->n; i++) if (!not_a_file(fl, i)) kept++;
    e = (fa_zip_entry*)calloc(kept ? (size_t)kept : 1, sizeof(*e));
    names = (char**)calloc(kept ? (size_t)kept : 1, sizeof(char*));
    if (!e || !names) { free(e); free(names); *res = FA_ERROR_OUT_OF_MEMORY; return; }
    for (i = 0; i < fl->n; i++) {
        fa_zip_entry tmp;
        int has_stream = !fl->empty[i], keep = !not_a_file(fl, i);
        memset(&tmp, 0, sizeof(tmp));
        tmp.mtime_ns = fl->mtime[i];
        tmp.ctime_ns = fl->ctime[i];
        tmp.atime_ns = fl->atime[i];
        tmp.data_offset = SZ_SIGNATURE_SIZE;
        tmp.flags = FA_ZE_NO_CRC;
        if (has_stream) {
            const folder* f;
            while (fi < si->nf && k_in >= si->f[fi].nsub) { fi++; k_in = 0; off = 0; }
            if (fi >= si->nf || sub >= si->nsubs) { *res = BAD; goto fail; }
            f = &si->f[fi];
            tmp.size = si->sub_size[sub];
            if (si->sub_has[sub]) { tmp.crc32 = si->sub_crc[sub]; tmp.flags = 0; }
            if (f->kind == FK_COPY) {
                tmp.method = 0;
                tmp.data_offset = (int64_t)(f->pack_pos + off);
                tmp.compressed_size = tmp.size;
            } else {
                tmp.method = (f->kind == FK_LZMA || f->kind == FK_LZMA2) ? FA_AM_7Z : FA_AM_7Z_BAD;
                tmp.header_offset = fi;
                tmp.data_offset = (int64_t)off;
                if (f->kind == FK_AES) tmp.flags |= 1;
            }
            off += tmp.size;
            k_in++;
            sub++;
        }
        if (!keep) continue;
        e[ei] = tmp;
        names[ei] = fl->names[i];
        fl->names[i] = NULL;
        ei++;
    }
    if (sub != si->nsubs) { *res = BAD; goto fail; }
    *out_e = e;
    *out_names = names;
    *out_count = ei;
    (void)z;
    return;
fail:
    { uint32_t j; for (j = 0; j < ei; j++) free(names[j]); }
    free(e);
    free(names);
}

fa_result fa_7z_open(fa_source* src, uint64_t memory_limit, const volatile int32_t* cancel, fa_7z** out,
                     fa_zip_entry** entries, char*** names, uint32_t* count)
{
    uint8_t h[SZ_SIGNATURE_SIZE];
    uint64_t len, next_off, next_size;
    uint8_t* buf = NULL;
    size_t bn;
    int depth;
    sinfo main_info;
    files fl;
    rd r;
    fa_7z* z = NULL;
    fa_result res;
    uint32_t i;
    *out = NULL;
    *entries = NULL;
    *names = NULL;
    *count = 0;
    memset(&main_info, 0, sizeof(main_info));
    memset(&fl, 0, sizeof(fl));
    if (src->length < 0) return FA_ERROR_NOT_SEEKABLE;
    len = (uint64_t)src->length;
    if (len < SZ_SIGNATURE_SIZE) return BAD;
    res = read_at(src, 0, h, sizeof(h));
    if (res != FA_OK) return res;
    if (memcmp(h, "7z\xBC\xAF\x27\x1C", 6) != 0 || h[6] != 0) return BAD;
    if (fa_crc32(h + 12, 20) != fa_le32(h + 8)) return BAD;
    next_off = fa_le64(h + 12);
    next_size = fa_le64(h + 20);
    z = (fa_7z*)calloc(1, sizeof(*z));
    if (!z) return FA_ERROR_OUT_OF_MEMORY;
    z->memory_limit = memory_limit;
    if (next_size == 0) {
        /* An archive with nothing in it. */
        *entries = (fa_zip_entry*)calloc(1, sizeof(fa_zip_entry));
        *names = (char**)calloc(1, sizeof(char*));
        if (!*entries || !*names) { free(*entries); free(*names); *entries = NULL; *names = NULL; fa_7z_free(z); return FA_ERROR_OUT_OF_MEMORY; }
        *out = z;
        return FA_OK;
    }
    if (next_size > FA_7Z_MAX_HEADER) { fa_7z_free(z); return FA_ERROR_TOO_LARGE; }
    if (next_off > len - SZ_SIGNATURE_SIZE || next_size > len - SZ_SIGNATURE_SIZE - next_off) { fa_7z_free(z); return BAD; }
    bn = (size_t)next_size;
    buf = (uint8_t*)malloc(bn);
    if (!buf) { fa_7z_free(z); return FA_ERROR_OUT_OF_MEMORY; }
    res = read_at(src, SZ_SIGNATURE_SIZE + next_off, buf, bn);
    if (res != FA_OK) goto fail;
    if (fa_crc32(buf, bn) != fa_le32(h + 28)) { res = BAD; goto fail; }
    for (depth = 0;; depth++) {
        uint8_t id;
        r.p = buf; r.n = bn; r.at = 0; r.bad = 0;
        id = r8(&r);
        if (id == K_HEADER) break;
        if (id != K_ENCODED || depth >= 4) { res = BAD; goto fail; }
        {
            sinfo es;
            uint8_t* hdr;
            memset(&es, 0, sizeof(es));
            res = parse_streams(&r, &es);
            if (res == FA_OK && (es.nf < 1 || !es.pack_size)) res = BAD;
            if (res == FA_OK) res = place_folders(&es, len);
            if (res == FA_OK) {
                const folder* f = &es.f[0];
                if (f->kind == FK_AES) res = FA_ERROR_PROTECTED_CONTENT;
                else if (f->kind != FK_COPY && f->kind != FK_LZMA && f->kind != FK_LZMA2) res = BAD;
                else if (f->unpack > FA_7Z_MAX_HEADER) res = FA_ERROR_TOO_LARGE;
            }
            if (res != FA_OK) { sinfo_free(&es); goto fail; }
            hdr = (uint8_t*)malloc(es.f[0].unpack ? (size_t)es.f[0].unpack : 1);
            if (!hdr) { sinfo_free(&es); res = FA_ERROR_OUT_OF_MEMORY; goto fail; }
            res = decode_whole(&es.f[0], 0, src, hdr, (size_t)es.f[0].unpack, cancel, NULL);
            if (res == FA_OK && es.f[0].has_crc && fa_crc32(hdr, (size_t)es.f[0].unpack) != es.f[0].crc) res = BAD;
            bn = (size_t)es.f[0].unpack;
            sinfo_free(&es);
            if (res != FA_OK) { free(hdr); goto fail; }
            free(buf);
            buf = hdr;
        }
    }
    /* The header proper. */
    for (;;) {
        uint8_t id = r8(&r);
        if (r.bad) { res = BAD; goto fail; }
        if (id == K_END) break;
        if (id == K_ARCHIVEPROPS) {
            for (;;) {
                uint8_t t = r8(&r);
                if (r.bad) { res = BAD; goto fail; }
                if (t == 0) break;
                rskip(&r, rnum(&r));
            }
        } else if (id == K_ADDITIONAL) { res = BAD; goto fail; }
        else if (id == K_MAINSTREAMS) {
            res = parse_streams(&r, &main_info);
            if (res != FA_OK) goto fail;
            if (main_info.nf > 0 && !main_info.pack_size) { res = BAD; goto fail; }
            if (main_info.nf > 0) { res = place_folders(&main_info, len); if (res != FA_OK) goto fail; }
        } else if (id == K_FILES) {
            res = parse_files(&r, &fl);
            if (res != FA_OK) goto fail;
        } else { res = BAD; goto fail; }
    }
    if (!fl.names && main_info.nsubs > 0) { res = BAD; goto fail; }
    if (fl.n == 0) {
        *entries = (fa_zip_entry*)calloc(1, sizeof(fa_zip_entry));
        *names = (char**)calloc(1, sizeof(char*));
        if (!*entries || !*names) { free(*entries); free(*names); *entries = NULL; *names = NULL; res = FA_ERROR_OUT_OF_MEMORY; goto fail; }
    } else {
        put_entries(&main_info, &fl, z, entries, names, count, &res);
        if (res != FA_OK) goto fail;
    }
    z->nfolders = main_info.nf;
    z->f = main_info.f;
    main_info.f = NULL;
    for (i = 0; i < z->nfolders; i++) scan_resets(&z->f[i], src, cancel);
    free(buf);
    files_free(&fl);
    sinfo_free(&main_info);
    *out = z;
    return FA_OK;
fail:
    free(buf);
    files_free(&fl);
    sinfo_free(&main_info);
    fa_7z_free(z);
    return res;
}

/* ---- Reading --------------------------------------------------------------------------------------- */

static int is_lzma(const folder* f) { return f->kind == FK_LZMA || f->kind == FK_LZMA2; }

/* A whole folder into `out` (which is its dictionary). */
static fa_result decode_whole(const folder* f, uint32_t index, fa_source* src, uint8_t* out, size_t size, const volatile int32_t* cancel,
                              uint64_t* stat)
{
    cursor c;
    fa_result res;
    if (f->kind == FK_COPY) {
        if (size == 0) return FA_OK;
        return read_at(src, f->pack_pos, out, size);
    }
    if (!is_lzma(f)) return BAD;
    memset(&c, 0, sizeof(c));
    res = cur_setup(&c, f, index, 0, UINT64_MAX);
    if (res != FA_OK) { cur_free(&c); return res; }
    c.buf = out;
    c.cap = size;
    c.fixed = 1;
    c.stat = stat;
    cur_restart(&c, 0, 0);
    res = buffered_to(&c, f, src, size, cancel);
    c.buf = NULL;   /* the caller's */
    cur_free(&c);
    return res;
}

int32_t fa_7z_public_method(const fa_7z* z, const fa_zip_entry* e)
{
    const folder* f;
    if (e->method == 0) return FA_METHOD_STORED;
    f = &z->f[e->header_offset];
    switch (f->kind) {
    case FK_LZMA: return FA_METHOD_LZMA;
    case FK_LZMA2: return FA_METHOD_LZMA2;
    case FK_PPMD: return FA_METHOD_PPMD;
    default: return FA_METHOD_OTHER;
    }
}

int fa_7z_is_solid(const fa_7z* z, const fa_zip_entry* e)
{
    if (e->method == 0) return 0;
    return z->f[e->header_offset].nsub > 1;
}

int64_t fa_7z_packed_size(const fa_7z* z, const fa_zip_entry* e)
{
    const folder* f;
    if (e->method == 0) return (int64_t)e->compressed_size;
    f = &z->f[e->header_offset];
    return f->nsub == 1 ? (int64_t)f->pack_size : -1;
}

uint64_t fa_7z_cost(const fa_7z* z, const fa_zip_entry* e)
{
    const folder* f;
    uint64_t pk, up, off = (uint64_t)e->data_offset;
    if (e->method == 0) return 0;
    f = &z->f[e->header_offset];
    if (last_reset(f, off, &pk, &up)) return off - up;
    return off;
}

int fa_7z_independent(const fa_7z* z, const fa_zip_entry* e)
{
    const folder* f;
    if (e->method != FA_AM_7Z) return 0;
    f = &z->f[e->header_offset];
    return f->nsub == 1 && is_lzma(f);
}

/* Moves the kept cursor to where a read of output offset `from` of folder `fi` should start. */
static fa_result cursor_to(fa_7z* z, uint32_t fi, uint64_t from, int window)
{
    cursor* c = &z->cur;
    const folder* f = &z->f[fi];
    uint64_t pk = 0, up = 0;
    int have_reset = last_reset(f, from, &pk, &up);
    if (c->live && c->folder == fi && c->window == window) {
        int behind = window ? c->pos > from : from < c->base;
        if (!behind && !(have_reset && up > c->pos)) return FA_OK;
        cur_restart(c, pk, up);
        return FA_OK;
    }
    {
        fa_result res;
        cur_free(c);
        res = cur_setup(c, f, fi, window, z->memory_limit);
        if (res != FA_OK) { cur_free(c); return res; }
        c->stat = &z->decoded;
        cur_restart(c, pk, up);
    }
    return FA_OK;
}

static void memo_note(fa_7z* z, const fa_zip_entry* e, const uint8_t* data, size_t n)
{
    if (!z->memo.data) {
        z->memo.data = (uint8_t*)malloc(MEMO_BYTES);
        if (!z->memo.data) return;
    }
    memcpy(z->memo.data, data, n);
    z->memo.len = n;
    z->memo.folder = (uint32_t)e->header_offset;
    z->memo.offset = (uint64_t)e->data_offset;
    z->memo.valid = 1;
}

typedef struct memo_sink {
    fa_7z* z;
    const fa_zip_entry* e;
    fa_sink_fn sink;
    void* ctx;
    size_t got;
    uint8_t first[MEMO_BYTES];
} memo_sink;

static int memo_put(void* ctx, const uint8_t* data, size_t n)
{
    memo_sink* m = (memo_sink*)ctx;
    size_t take = n < MEMO_BYTES - m->got ? n : MEMO_BYTES - m->got;
    memcpy(m->first + m->got, data, take);
    m->got += take;
    return m->sink(m->ctx, data, n);
}

fa_result fa_7z_read(fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t from, uint64_t to, fa_sink_fn sink, void* ctx,
                     const volatile int32_t* cancel)
{
    const folder* f;
    uint32_t fi;
    uint64_t a0, a, b;
    fa_result res;
    if (e->method != FA_AM_7Z) return BAD;
    if (from > to || to > e->size) return FA_ERROR_INVALID_ARG;
    if (from == to) return FA_OK;
    fi = (uint32_t)e->header_offset;
    f = &z->f[fi];
    a0 = (uint64_t)e->data_offset;
    a = a0 + from;
    b = a0 + to;
    if (f->unpack > z->memory_limit) {
        /* Too big to hold: decode through a window, from where the cursor is or a reset before. */
        memo_sink m;
        fa_sink_fn use = sink;
        void* uctx = ctx;
        int note = from == 0 && to <= MEMO_BYTES;
        res = cursor_to(z, fi, a, 1);
        if (res != FA_OK) return res;
        if (note) { m.z = z; m.e = e; m.sink = sink; m.ctx = ctx; m.got = 0; use = memo_put; uctx = &m; }
        res = window_to(&z->cur, f, src, a, b, use, uctx, cancel);
        if (res == FA_OK && note && m.got == to) memo_note(z, e, m.first, m.got);
        return res;
    }
    if (f->nsub == 1) {
        /* Alone in its folder: decode the part wanted into a block, nothing kept. */
        uint8_t* buf = (uint8_t*)malloc((size_t)b ? (size_t)b : 1);
        cursor c;
        size_t at;
        if (!buf) return FA_ERROR_OUT_OF_MEMORY;
        memset(&c, 0, sizeof(c));
        res = cur_setup(&c, f, fi, 0, UINT64_MAX);
        if (res == FA_OK) {
            c.buf = buf; c.cap = (size_t)b; c.fixed = 1; c.stat = &z->decoded;
            cur_restart(&c, 0, 0);
            res = buffered_to(&c, f, src, b, cancel);
        }
        c.buf = NULL;
        cur_free(&c);
        if (res == FA_OK) {
            for (at = (size_t)a; at < (size_t)b; ) {
                size_t n = (size_t)b - at < STEP ? (size_t)b - at : STEP;
                if (sink(ctx, buf + at, n)) break;
                at += n;
            }
        }
        free(buf);
        return res;
    }
    /* Several files in the folder: the cursor's buffer is the dictionary and holds everything decoded since the
     * last restart, so a read behind the cursor costs nothing while it is still in the buffer. */
    res = cursor_to(z, fi, a, 0);
    if (res != FA_OK) return res;
    if (z->cur.pos < b) {
        res = buffered_to(&z->cur, f, src, b, cancel);
        if (res != FA_OK) return res;
    }
    {
        size_t at = (size_t)(a - z->cur.base), end = (size_t)(b - z->cur.base);
        while (at < end) {
            size_t n = end - at < STEP ? end - at : STEP;
            if (sink(ctx, z->cur.buf + at, n)) break;
            at += n;
        }
    }
    return FA_OK;
}

typedef struct crc_sink {
    fa_sink_fn sink;
    void* ctx;
    uint32_t crc;
    int stopped;
} crc_sink;

static int crc_put(void* ctx, const uint8_t* data, size_t n)
{
    crc_sink* c = (crc_sink*)ctx;
    c->crc = fa_crc32_update(c->crc, data, n);
    if (c->sink(c->ctx, data, n)) { c->stopped = 1; return 1; }
    return 0;
}

fa_result fa_7z_extract(fa_7z* z, fa_source* src, const fa_zip_entry* e, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel)
{
    crc_sink c;
    uint64_t from = 0;
    fa_result res;
    c.sink = sink; c.ctx = ctx; c.crc = 0; c.stopped = 0;
    if (e->size == 0) return FA_OK;
    /* The start was read to tell audio from the rest and the cursor stands right after it: do not decode it twice. */
    if (z->memo.valid && z->memo.folder == (uint32_t)e->header_offset && z->memo.offset == (uint64_t)e->data_offset &&
        z->memo.len <= e->size && z->cur.live && z->cur.window && z->cur.folder == z->memo.folder &&
        z->cur.pos == z->memo.offset + z->memo.len) {
        from = z->memo.len;
        crc_put(&c, z->memo.data, z->memo.len);
        if (c.stopped) return FA_OK;
    }
    if (from < e->size) {
        res = fa_7z_read(z, src, e, from, e->size, crc_put, &c, cancel);
        if (res != FA_OK) return res;
    }
    if (!c.stopped && !(e->flags & FA_ZE_NO_CRC) && c.crc != e->crc32) return BAD;
    return FA_OK;
}

typedef struct fill {
    uint8_t* p;
    size_t n, cap;
} fill;

static int fill_put(void* ctx, const uint8_t* data, size_t n)
{
    fill* f = (fill*)ctx;
    if (n > f->cap - f->n) n = f->cap - f->n;
    memcpy(f->p + f->n, data, n);
    f->n += n;
    return 0;
}

fa_result fa_7z_unpack_alone(const fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t limit, const volatile int32_t* cancel,
                             uint8_t** out)
{
    const folder* f;
    uint8_t* buf;
    fa_result res;
    *out = NULL;
    if (!fa_7z_independent(z, e)) return BAD;
    f = &z->f[e->header_offset];
    if (e->size > limit || e->size > (uint64_t)(SIZE_MAX - 1)) return FA_ERROR_TOO_LARGE;
    buf = (uint8_t*)malloc(e->size ? (size_t)e->size : 1);
    if (!buf) return FA_ERROR_OUT_OF_MEMORY;
    res = e->size ? decode_whole(f, (uint32_t)e->header_offset, src, buf, (size_t)e->size, cancel, NULL) : FA_OK;
    if (res == FA_OK && !(e->flags & FA_ZE_NO_CRC) && fa_crc32(buf, (size_t)e->size) != e->crc32) res = BAD;
    if (res != FA_OK) { free(buf); return res; }
    *out = buf;
    return FA_OK;
}

fa_result fa_7z_unpack(fa_7z* z, fa_source* src, const fa_zip_entry* e, uint64_t limit, const volatile int32_t* cancel, uint8_t** out)
{
    fill fl;
    fa_result res;
    *out = NULL;
    if (e->method != FA_AM_7Z) return BAD;
    if (e->size > limit || e->size > (uint64_t)(SIZE_MAX - 1)) return FA_ERROR_TOO_LARGE;
    if (fa_7z_independent(z, e)) return fa_7z_unpack_alone(z, src, e, limit, cancel, out);
    fl.p = (uint8_t*)malloc(e->size ? (size_t)e->size : 1);
    if (!fl.p) return FA_ERROR_OUT_OF_MEMORY;
    fl.n = 0;
    fl.cap = (size_t)e->size;
    res = fa_7z_extract(z, src, e, fill_put, &fl, cancel);
    if (res == FA_OK && fl.n != fl.cap) res = BAD;
    if (res != FA_OK) { free(fl.p); return res; }
    *out = fl.p;
    return FA_OK;
}

uint64_t fa_7z_decoded(const fa_7z* z) { return z->decoded; }
