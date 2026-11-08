/* ZIP archives, for karaoke downloads that pack the audio and the .cdg together.
 * Written from the PKWARE APPNOTE (central directory, stored and deflate entries)
 * and RFC 1951 (DEFLATE). Archives are read through a source: only the directory
 * and the entries asked for are read, never the whole file. */
#include "cdg.h"
#include "archive/archive.h"
#include "archive/codec.h"
#include <stdlib.h>
#include <string.h>

#define ZIP_LOCAL_SIG 0x04034b50u
#define ZIP_CENTRAL_SIG 0x02014b50u
#define ZIP_END_SIG 0x06054b50u
#define ZIP_END_SIZE 22
#define ZIP_CENTRAL_SIZE 46
#define ZIP_LOCAL_SIZE 30
#define ZIP64_END_SIG 0x06064b50u
#define ZIP64_END_SIZE 56
#define ZIP64_LOCATOR_SIG 0x07064b50u
#define ZIP64_LOCATOR_SIZE 20
#define ZIP_MAX_DIRECTORY ((uint64_t)16 << 20)

/* ---- DEFLATE ------------------------------------------------------------------ */

#define INF_MAX_BITS 15
#define INF_MAX_LITLEN 288
#define INF_MAX_DIST 32
#define INF_DIST_CODES 30                /* DEFLATE uses 30 distance codes, Deflate64 32 */

typedef struct inf_huff {
    uint16_t count[INF_MAX_BITS + 1];   /* codes of each length */
    uint16_t symbol[INF_MAX_LITLEN];    /* symbols ordered by code */
} inf_huff;

/* Lookup tables indexed by the next `root` input bits. An entry holds the code's
 * length in bits 0-7 (0: no code of at most `root` bits starts so; the canonical
 * walk in inf_slow finds longer codes, which are rare), the extra bits in 8-12,
 * the flags below and the value (literal, length or distance base, or symbol) in
 * 16-31. */
#define INF_LIT_ROOT 10
#define INF_DIST_ROOT 8
#define INF_LEN_ROOT 7
#define INF_E_LIT 0x2000u
#define INF_E_END 0x4000u
#define INF_E_BAD 0x8000u                /* a symbol the format does not use */

enum { INF_KIND_LEN, INF_KIND_LITLEN, INF_KIND_DIST };

typedef struct inf_codes_t {
    inf_huff lit, dist;
    uint32_t lit_tab[1u << INF_LIT_ROOT];
    uint32_t dist_tab[1u << INF_DIST_ROOT];
} inf_codes_t;

#define INF_WINDOW 65536                 /* the farthest a match reaches back (Deflate64: 64 KB) */
#define INF_IN_CHUNK 65536               /* input read from a source at a time */
#define INF_OUT_CHUNK (1u << 20)         /* output kept before writing to a file */
#define INF_FAST_OUT (258 + 8)           /* the longest match, plus a word copy's overshoot */

/* Input comes from memory or is pulled from a source; output goes to one buffer
 * of the whole size, or through a 1 MB buffer into a file, keeping the last 32 KB
 * for matches to copy from. */
typedef struct inf_state {
    const uint8_t* in;
    size_t in_size, in_pos;             /* the input chunk at hand */
    fa_source* src;                     /* NULL: `in` is all of it */
    uint64_t src_left;                  /* compressed bytes not read from src yet */
    uint8_t* in_buf;
    /* Bits not used yet, first in the lowest bit. Above bitcnt it may hold the
     * bits of the input bytes at in_pos, in their places: loading those bytes
     * again ORs in the same bits. */
    uint64_t bitbuf;
    int bitcnt;
    uint8_t* out;
    size_t out_cap, out_pos;
    uint64_t flushed;                   /* bytes handed to the sink before out[0] */
    uint64_t total;                     /* the size the output must have */
    fa_sink_fn sink;                    /* where full buffers go (NULL: out holds all of it) */
    void* sink_ctx;
    size_t pending_from;                /* out[0, pending_from) is history, not yet to be delivered (a resumed run) */
    int stopped;                        /* the sink asked to stop */
    int unbounded;                      /* the output size is not known: the stream's last block ends it */
    uint32_t crc;                       /* of the delivered bytes */
    uint64_t in_total;                  /* the input's size, for positions */
    fa_infcps* rec;                     /* checkpoints to record (NULL: none) */
    uint64_t in_base_bits;              /* bits of the stream before this run's first input byte (a resumed run) */
    int error;                          /* ran past the input */
    int io_error;                       /* the source or the file failed */
    const volatile int32_t* cancel;              /* the app's flag: non-zero stops at the next chunk (NULL: never) */
    int cancelled;                      /* ... and it did */
    int d64;                            /* Deflate64 (ZIP method 9): 64 KB window, longer matches */
} inf_state;

/* A file as a sink. */
static int inf_file_sink(void* ctx, const uint8_t* data, size_t n) { return fwrite(data, 1, n, (FILE*)ctx) != n; }

static int inf_cancelled(inf_state* s)
{
    if (s->cancel && *s->cancel) s->cancelled = 1;
    return s->cancelled;
}

static int inf_refill(inf_state* s)
{
    int64_t got;
    size_t want;
    if (!s->src || s->src_left == 0 || inf_cancelled(s)) return 0;
    want = s->src_left < INF_IN_CHUNK ? (size_t)s->src_left : INF_IN_CHUNK;
    got = fa_source_read(s->src, s->in_buf, want);
    if (got <= 0) { s->io_error = got < 0; return 0; }
    s->src_left -= (uint64_t)got;
    s->in = s->in_buf;
    s->in_size = (size_t)got;
    s->in_pos = 0;
    return 1;
}

/* Room for one more output byte: writes out all but the last 32 KB when full. */
static int inf_room(inf_state* s)
{
    size_t keep, n;
    if (s->out_pos < s->out_cap) return 0;
    if (!s->sink || inf_cancelled(s)) return -1;
    keep = INF_WINDOW;
    n = s->out_pos - keep;
    if (n > s->pending_from) {
        if (s->sink(s->sink_ctx, s->out + s->pending_from, n - s->pending_from)) { s->stopped = 1; return -1; }
        s->crc = fa_crc32_update(s->crc, s->out + s->pending_from, n - s->pending_from);
    }
    s->pending_from = 0;
    memmove(s->out, s->out + n, keep);
    s->flushed += n;
    s->out_pos = keep;
    return 0;
}

static uint64_t inf_left(const inf_state* s) { return s->total - s->flushed - s->out_pos; }

/* Tops the bit buffer up to at least 56 bits, a byte at a time, or to the end of the input. */
static void inf_fill(inf_state* s)
{
    while (s->bitcnt < 56) {
        if (s->in_pos >= s->in_size && !inf_refill(s)) return;
        s->bitbuf |= (uint64_t)s->in[s->in_pos++] << s->bitcnt;
        s->bitcnt += 8;
    }
}

static uint32_t inf_bits(inf_state* s, int need)
{
    uint32_t v;
    if (s->bitcnt < need) {
        inf_fill(s);
        if (s->bitcnt < need) { s->error = 1; return 0; }
    }
    v = (uint32_t)s->bitbuf & ((1u << need) - 1u);
    s->bitbuf >>= need;
    s->bitcnt -= need;
    return v;
}

/* Canonical code from code lengths. Returns 0 for a complete or incomplete code,
 * -1 when over-subscribed (invalid). */
static int inf_build(inf_huff* h, const uint8_t* lengths, int n)
{
    uint16_t offs[INF_MAX_BITS + 1];
    int sym, len, left = 1;
    memset(h->count, 0, sizeof(h->count));
    for (sym = 0; sym < n; sym++) h->count[lengths[sym]]++;
    if (h->count[0] == n) return 0;
    for (len = 1; len <= INF_MAX_BITS; len++) {
        left <<= 1;
        left -= h->count[len];
        if (left < 0) return -1;
    }
    offs[1] = 0;
    for (len = 1; len < INF_MAX_BITS; len++) offs[len + 1] = (uint16_t)(offs[len] + h->count[len]);
    for (sym = 0; sym < n; sym++)
        if (lengths[sym]) h->symbol[offs[lengths[sym]]++] = (uint16_t)sym;
    return 0;
}

static const uint16_t len_base[29] = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
                                       35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
static const uint8_t len_extra[29] = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
                                       3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
static const uint16_t dist_base[32] = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
                                        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
                                        8193, 12289, 16385, 24577, 32769, 49153 };
static const uint8_t dist_extra[32] = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
                                        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14 };

static uint32_t inf_entry(int kind, int sym, int len, int d64)
{
    if (kind == INF_KIND_LITLEN) {
        if (sym < 256) return (uint32_t)sym << 16 | INF_E_LIT | (uint32_t)len;
        if (sym == 256) return INF_E_END | (uint32_t)len;
        sym -= 257;
        if (sym >= 29) return INF_E_BAD | (uint32_t)len;
        /* Deflate64: the last length code is 3 + 16 extra bits instead of 258. */
        if (d64 && sym == 28) return 3u << 16 | 16u << 8 | (uint32_t)len;
        return (uint32_t)len_base[sym] << 16 | (uint32_t)len_extra[sym] << 8 | (uint32_t)len;
    }
    if (kind == INF_KIND_DIST) {
        if (sym >= (d64 ? 32 : INF_DIST_CODES)) return INF_E_BAD | (uint32_t)len;
        return (uint32_t)dist_base[sym] << 16 | (uint32_t)dist_extra[sym] << 8 | (uint32_t)len;
    }
    return (uint32_t)sym << 16 | (uint32_t)len;
}

/* Fills the table for the codes of at most `root` bits. Huffman codes are packed
 * most significant bit first, so a code sits at its bit-reversed index, repeated
 * for every value of the bits after it. */
static void inf_table(uint32_t* t, int root, const inf_huff* h, int kind, int d64)
{
    uint32_t code = 0, size = 1u << root;
    int len, index = 0, i;
    memset(t, 0, size * sizeof(*t));
    for (len = 1; len <= root; len++) {
        for (i = 0; i < h->count[len]; i++, index++, code++) {
            uint32_t rev = 0, c = code, at, e = inf_entry(kind, h->symbol[index], len, d64);
            int b;
            for (b = 0; b < len; b++) { rev = (rev << 1) | (c & 1); c >>= 1; }
            for (at = rev; at < size; at += 1u << len) t[at] = e;
        }
        code <<= 1;
    }
}

/* The canonical walk for a code longer than the table's root, from at most
 * `avail` bits: build the code bit by bit and compare it with the first code of
 * each length. -1 for a code the table does not have or input that ran out. */
static int inf_slow(const inf_huff* h, uint64_t bits, int avail, int* used)
{
    int code = 0, first = 0, index = 0, len;
    for (len = 1; len <= INF_MAX_BITS && len <= avail; len++) {
        int count = h->count[len];
        code |= (int)(bits >> (len - 1)) & 1;
        if (code - first < count) { *used = len; return h->symbol[index + (code - first)]; }
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
    }
    return -1;
}

/* Reads one code: its entry, or 0 when invalid or past the input. The fast path
 * calls it with at least 15 bits at hand. */
static uint32_t inf_decode(inf_state* s, const uint32_t* t, int root, const inf_huff* h, int kind)
{
    uint32_t e, len;
    if (s->bitcnt < INF_MAX_BITS) inf_fill(s);
    e = t[s->bitbuf & ((1u << root) - 1u)];
    if (!(e & 0xFF)) {
        int used, sym = inf_slow(h, s->bitbuf, s->bitcnt, &used);
        if (sym < 0) return 0;
        e = inf_entry(kind, sym, used, s->d64);
    }
    len = e & 0xFF;
    if ((int)len > s->bitcnt) return 0;
    s->bitbuf >>= len;
    s->bitcnt -= (int)len;
    return e;
}

static int inf_codes(inf_state* s, const inf_codes_t* c)
{
    for (;;) {
        uint32_t e;
        size_t length, distance;
        uint8_t* out;
        /* With 8 input bytes, room for a whole match and that much output to go,
         * one word load gives the bits for a whole symbol and nothing needs a
         * bounds check but the match distance. */
        if (!s->d64 && s->in_size - s->in_pos >= 8 && s->out_cap - s->out_pos >= INF_FAST_OUT && inf_left(s) >= 258) {
            s->bitbuf |= fa_le64(s->in + s->in_pos) << s->bitcnt;
            s->in_pos += (size_t)(63 - s->bitcnt) >> 3;
            s->bitcnt |= 56;
            e = inf_decode(s, c->lit_tab, INF_LIT_ROOT, &c->lit, INF_KIND_LITLEN);
            if (e & INF_E_LIT) { s->out[s->out_pos++] = (uint8_t)(e >> 16); continue; }
            if (e & (INF_E_END | INF_E_BAD) || !e) return e & INF_E_END ? 0 : -1;
            length = (e >> 16) + inf_bits(s, (int)((e >> 8) & 0x1F));   /* the bits are at hand */
            e = inf_decode(s, c->dist_tab, INF_DIST_ROOT, &c->dist, INF_KIND_DIST);
            if (!e || (e & INF_E_BAD)) return -1;
            distance = (e >> 16) + inf_bits(s, (int)((e >> 8) & 0x1F));
            if (distance > s->out_pos) return -1;
            out = s->out + s->out_pos;
            s->out_pos += length;
            if (distance >= 8) {
                /* Words, overshooting by up to 7 bytes that later output overwrites. */
                const uint8_t* from = out - distance;
                uint8_t* end = out + length;
                do { memcpy(out, from, 8); out += 8; from += 8; } while (out < end);
            } else if (distance == 1) {
                memset(out, out[-1], length);
            } else {
                /* Byte by byte: the copy overlaps what it writes (runs). */
                while (length--) { *out = out[-(ptrdiff_t)distance]; out++; }
            }
            continue;
        }
        e = inf_decode(s, c->lit_tab, INF_LIT_ROOT, &c->lit, INF_KIND_LITLEN);
        if (!e) return -1;
        if (e & INF_E_LIT) {
            if (inf_left(s) == 0 || inf_room(s)) return -1;
            s->out[s->out_pos++] = (uint8_t)(e >> 16);
        } else if (e & INF_E_END) {
            return 0;
        } else {
            if (e & INF_E_BAD) return -1;
            length = (e >> 16) + inf_bits(s, (int)((e >> 8) & 0x1F));
            e = inf_decode(s, c->dist_tab, INF_DIST_ROOT, &c->dist, INF_KIND_DIST);
            if (!e || (e & INF_E_BAD)) return -1;
            distance = (e >> 16) + inf_bits(s, (int)((e >> 8) & 0x1F));
            /* After a flush out_pos is at least INF_WINDOW, as far as a match reaches. */
            if (s->error || distance > s->out_pos || length > inf_left(s)) return -1;
            while (length) {
                size_t n;
                if (inf_room(s)) return -1;
                n = s->out_cap - s->out_pos;
                if (n > length) n = length;
                length -= n;
                /* Byte by byte: the copy may overlap what it writes (runs). */
                while (n--) { s->out[s->out_pos] = s->out[s->out_pos - distance]; s->out_pos++; }
            }
        }
    }
}

/* Copies `len` input bytes to the output as they are (stored blocks and entries). */
static int inf_copy(inf_state* s, uint64_t len)
{
    while (len) {
        size_t n;
        if (inf_cancelled(s)) return -1;
        if (s->in_pos >= s->in_size && !inf_refill(s)) return -1;
        if (inf_room(s)) return -1;
        n = s->in_size - s->in_pos;
        if (n > len) n = (size_t)len;
        if (n > s->out_cap - s->out_pos) n = s->out_cap - s->out_pos;
        memcpy(s->out + s->out_pos, s->in + s->in_pos, n);
        s->in_pos += n;
        s->out_pos += n;
        len -= n;
    }
    return 0;
}

static int inf_stored(inf_state* s)
{
    uint32_t len, nlen;
    /* Skip to the byte boundary; whole bytes already in the bit buffer come first. */
    s->bitbuf >>= s->bitcnt & 7;
    s->bitcnt &= ~7;
    len = inf_bits(s, 16);
    nlen = inf_bits(s, 16);
    if (s->error || len != (~nlen & 0xFFFFu) || len > inf_left(s)) return -1;
    for (; len && s->bitcnt >= 8; len--) {
        if (inf_room(s)) return -1;
        s->out[s->out_pos++] = (uint8_t)s->bitbuf;
        s->bitbuf >>= 8;
        s->bitcnt -= 8;
    }
    if (!len) return 0;
    s->bitbuf = 0;   /* the bits above are of the bytes inf_copy takes */
    return inf_copy(s, len);
}

/* The fixed codes (RFC 1951 3.2.6), built once per process. */
static inf_codes_t inf_fixed_codes[2];   /* DEFLATE, Deflate64 */
static fa_atomic32 inf_fixed_state;

static int inf_fixed_build(void* ctx)
{
    uint8_t lengths[INF_MAX_LITLEN];
    int i, d64;
    (void)ctx;
    for (d64 = 0; d64 < 2; d64++) {
        inf_codes_t* c = &inf_fixed_codes[d64];
        for (i = 0; i < 144; i++) lengths[i] = 8;
        for (; i < 256; i++) lengths[i] = 9;
        for (; i < 280; i++) lengths[i] = 7;
        for (; i < INF_MAX_LITLEN; i++) lengths[i] = 8;
        inf_build(&c->lit, lengths, INF_MAX_LITLEN);
        for (i = 0; i < INF_MAX_DIST; i++) lengths[i] = 5;
        inf_build(&c->dist, lengths, INF_MAX_DIST);
        inf_table(c->lit_tab, INF_LIT_ROOT, &c->lit, INF_KIND_LITLEN, d64);
        inf_table(c->dist_tab, INF_DIST_ROOT, &c->dist, INF_KIND_DIST, d64);
    }
    return 1;
}

static int inf_fixed(inf_state* s)
{
    fa_once(&inf_fixed_state, inf_fixed_build, NULL);
    return inf_codes(s, &inf_fixed_codes[s->d64 ? 1 : 0]);
}

static int inf_dynamic(inf_state* s)
{
    static const uint8_t order[19] = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
    uint8_t lengths[INF_MAX_LITLEN + INF_MAX_DIST];
    uint32_t len_tab[1u << INF_LEN_ROOT];
    inf_huff lencode;
    inf_codes_t c;
    int nlen, ndist, ncode, i;
    nlen = (int)inf_bits(s, 5) + 257;
    ndist = (int)inf_bits(s, 5) + 1;
    ncode = (int)inf_bits(s, 4) + 4;
    if (s->error || nlen > 286 || ndist > (s->d64 ? INF_MAX_DIST : INF_DIST_CODES)) return -1;
    memset(lengths, 0, sizeof(lengths));
    for (i = 0; i < ncode; i++) lengths[order[i]] = (uint8_t)inf_bits(s, 3);
    if (s->error || inf_build(&lencode, lengths, 19) != 0) return -1;
    inf_table(len_tab, INF_LEN_ROOT, &lencode, INF_KIND_LEN, 0);
    i = 0;
    while (i < nlen + ndist) {
        uint32_t e = inf_decode(s, len_tab, INF_LEN_ROOT, &lencode, INF_KIND_LEN);
        int sym = (int)(e >> 16), repeat;
        uint8_t value = 0;
        if (!e) return -1;
        if (sym < 16) { lengths[i++] = (uint8_t)sym; continue; }
        if (sym == 16) {
            if (i == 0) return -1;
            value = lengths[i - 1];
            repeat = 3 + (int)inf_bits(s, 2);
        } else if (sym == 17) {
            repeat = 3 + (int)inf_bits(s, 3);
        } else {
            repeat = 11 + (int)inf_bits(s, 7);
        }
        if (s->error || i + repeat > nlen + ndist) return -1;
        while (repeat--) lengths[i++] = value;
    }
    if (lengths[256] == 0) return -1; /* no end-of-block code */
    if (inf_build(&c.lit, lengths, nlen) != 0 || inf_build(&c.dist, lengths + nlen, ndist) != 0) return -1;
    inf_table(c.lit_tab, INF_LIT_ROOT, &c.lit, INF_KIND_LITLEN, s->d64);
    inf_table(c.dist_tab, INF_DIST_ROOT, &c.dist, INF_KIND_DIST, s->d64);
    return inf_codes(s, &c);
}

/* Where the next input bit is, counted from the start of the stream the run began with. */
static uint64_t inf_in_bits(const inf_state* s)
{
    uint64_t loaded = s->src ? s->in_total - s->src_left - (s->in_size - s->in_pos) : s->in_pos;
    return loaded * 8 - (uint64_t)s->bitcnt;
}

/* At a block start: a restart point when the last one is far enough back. */
static void inf_record(inf_state* s)
{
    fa_infcps* r = s->rec;
    uint64_t off = s->flushed + s->out_pos;
    size_t w = s->out_pos < FA_INF_CP_WINDOW ? s->out_pos : FA_INF_CP_WINDOW;
    fa_infcp* c;
    uint8_t* win;
    if (off < r->next_at) return;
    if (r->n >= FA_INF_CP_MAX) {
        /* Keep every second one and space the rest twice as far apart. */
        uint32_t i, j = 0;
        for (i = 0; i < r->n; i++) {
            if (i & 1) free(r->v[i].window);
            else r->v[j++] = r->v[i];
        }
        r->n = j;
        r->every *= 2;
        r->next_at = r->v[r->n - 1].out_offset + r->every;
        if (off < r->next_at) return;
    }
    if (r->n == r->cap) {
        fa_infcp* nv;
        uint32_t cap = r->cap ? r->cap * 2 : 16;
        nv = (fa_infcp*)realloc(r->v, (size_t)cap * sizeof(*nv));
        if (!nv) { r->next_at = UINT64_MAX; return; }
        r->v = nv;
        r->cap = cap;
    }
    win = (uint8_t*)malloc(w ? w : 1);
    if (!win) { r->next_at = UINT64_MAX; return; }
    memcpy(win, s->out + s->out_pos - w, w);
    c = &r->v[r->n++];
    c->out_offset = off;
    c->in_bit = s->in_base_bits + inf_in_bits(s);
    c->window = win;
    c->window_len = (uint32_t)w;
    r->next_at = off + r->every;
}

static fa_result inf_run(inf_state* s)
{
    int last;
    do {
        int type, r;
        if (inf_cancelled(s)) break;   /* every block, also when all the data is in memory */
        if (s->rec) inf_record(s);
        last = (int)inf_bits(s, 1);
        type = (int)inf_bits(s, 2);
        if (s->error) break;
        if (type == 0) r = inf_stored(s);
        else if (type == 1) r = inf_fixed(s);
        else if (type == 2) r = inf_dynamic(s);
        else r = -1;
        if (r != 0 || s->error) { s->error = 1; break; }
    } while (!last);
    if (s->cancelled) return FA_ERROR_CANCELLED;
    if (s->io_error) return FA_ERROR_IO;
    return !s->error && (s->unbounded || inf_left(s) == 0) ? FA_OK : FA_ERROR_UNSUPPORTED_FORMAT;
}

void fa_infcps_free(fa_infcps* r)
{
    uint32_t i;
    if (!r) return;
    for (i = 0; i < r->n; i++) free(r->v[i].window);
    free(r->v);
    memset(r, 0, sizeof(*r));
}

fa_result fa_inflate_stream(fa_cin in, int d64, uint64_t usize, fa_sink_fn sink, void* ctx, const volatile int32_t* cancel,
                            const fa_infcp* resume, fa_infcps* record, fa_inflate_result* res)
{
    inf_state s;
    uint8_t* out;
    uint64_t start_off = resume ? resume->out_offset : 0, in_skip_bits = resume ? resume->in_bit : 0;
    fa_result r;
    memset(res, 0, sizeof(*res));
    out = (uint8_t*)malloc(INF_OUT_CHUNK);
    if (!out) return FA_ERROR_OUT_OF_MEMORY;
    memset(&s, 0, sizeof(s));
    s.out = out;
    s.out_cap = INF_OUT_CHUNK;
    s.unbounded = usize == UINT64_MAX;
    s.total = s.unbounded ? UINT64_MAX >> 1 : usize;
    s.sink = sink;
    s.sink_ctx = ctx;
    s.cancel = cancel;
    s.d64 = d64;
    s.rec = record;
    s.in_total = in.left;
    if (resume) {
        /* Start at a block boundary with the 32 KB before it as the window. */
        uint64_t skip_bytes = in_skip_bits >> 3;
        if (skip_bytes > in.left || resume->window_len > FA_INF_CP_WINDOW) { free(out); return FA_ERROR_INVALID_ARG; }
        memcpy(out, resume->window, resume->window_len);
        s.out_pos = s.pending_from = resume->window_len;
        s.flushed = start_off - resume->window_len;
        s.in_base_bits = skip_bytes * 8;
        s.in_total = in.left - skip_bytes;
        if (in.mem) in.mem += skip_bytes;
        else if (fa_source_seek(in.src, fa_source_tell(in.src) + (int64_t)skip_bytes) != FA_OK) { free(out); return FA_ERROR_IO; }
        in.left -= skip_bytes;
    }
    if (in.mem) {
        s.in = in.mem;
        s.in_size = (size_t)in.left;
    } else {
        s.src = in.src;
        s.src_left = in.left;
        s.in_buf = (uint8_t*)malloc(INF_IN_CHUNK);
        if (!s.in_buf) { free(out); return FA_ERROR_OUT_OF_MEMORY; }
    }
    if (resume && (in_skip_bits & 7)) (void)inf_bits(&s, (int)(in_skip_bits & 7));
    r = inf_run(&s);
    if (s.stopped) { res->stopped = 1; r = FA_OK; }
    else if (r == FA_OK) {
        size_t n = s.out_pos - s.pending_from;
        s.crc = fa_crc32_update(s.crc, s.out + s.pending_from, n);
        if (n && s.sink && s.sink(s.sink_ctx, s.out + s.pending_from, n)) res->stopped = 1;
    }
    res->produced = s.flushed + s.out_pos - start_off;
    res->crc = s.crc;
    res->consumed = s.in_base_bits / 8 + (inf_in_bits(&s) + 7) / 8;
    free(s.in_buf);
    free(out);
    return r;
}

fa_result fa_inflate(const uint8_t* in, size_t in_size, uint8_t* out, size_t out_size)
{
    inf_state s;
    memset(&s, 0, sizeof(s));
    s.in = in; s.in_size = in_size;
    s.out = out; s.out_cap = out_size;
    s.total = out_size;
    return inf_run(&s);
}

/* ---- Archives ------------------------------------------------------------------ */

int fa_zip_probe(const uint8_t* data, size_t size)
{
    return size >= 4 && fa_le32(data) == ZIP_LOCAL_SIG;
}

/* Exactly n bytes at `offset`; a short read is a damaged (cut) archive. */
static fa_result read_at(fa_source* src, int64_t offset, void* buf, size_t n)
{
    size_t done = 0;
    if (fa_source_seek(src, offset) != FA_OK) return FA_ERROR_IO;
    while (done < n) {
        int64_t got = fa_source_read(src, (uint8_t*)buf + done, n - done);
        if (got < 0) return FA_ERROR_IO;
        if (got == 0) return FA_ERROR_UNSUPPORTED_FORMAT;
        done += (size_t)got;
    }
    return FA_OK;
}

/* ZIP64 (APPNOTE 4.3.14 to 4.3.15 and 4.5.3): the end record holds 0xFFFF or
 * 0xFFFFFFFF where a value does not fit, and the real ones follow in a ZIP64 end
 * record, found through a locator right before the end record. */

/* The directory's place, from the end record at tail[end] (tail starts at `tail_at`). */
static fa_result find_directory(fa_source* src, const uint8_t* tail, size_t end, int64_t tail_at,
                                uint64_t* total, uint64_t* cd_at, uint64_t* cd_size)
{
    uint8_t z[ZIP64_END_SIZE];
    uint64_t z_at, limit = (uint64_t)tail_at + end;
    fa_result r;
    *total = fa_le16(tail + end + 10);
    *cd_size = fa_le32(tail + end + 12);
    *cd_at = fa_le32(tail + end + 16);
    if (fa_le16(tail + end + 4) != 0 || fa_le16(tail + end + 6) != 0) return FA_ERROR_UNSUPPORTED_FORMAT; /* multi-disk */
    /* Without a locator the values are real ones (an archive of exactly 65,535 entries). */
    if ((*total == 0xFFFF || *cd_size == 0xFFFFFFFFu || *cd_at == 0xFFFFFFFFu) && end >= ZIP64_LOCATOR_SIZE &&
        fa_le32(tail + end - ZIP64_LOCATOR_SIZE) == ZIP64_LOCATOR_SIG) {
        const uint8_t* loc = tail + end - ZIP64_LOCATOR_SIZE;
        if (fa_le32(loc + 4) != 0 || fa_le32(loc + 16) != 1) return FA_ERROR_UNSUPPORTED_FORMAT;
        z_at = fa_le64(loc + 8);
        if (z_at > limit - ZIP64_LOCATOR_SIZE || limit - ZIP64_LOCATOR_SIZE - z_at < ZIP64_END_SIZE)
            return FA_ERROR_UNSUPPORTED_FORMAT;
        r = read_at(src, (int64_t)z_at, z, sizeof(z));
        if (r != FA_OK) return r;
        if (fa_le32(z) != ZIP64_END_SIG || fa_le32(z + 16) != 0 || fa_le32(z + 20) != 0 || fa_le64(z + 24) != fa_le64(z + 32))
            return FA_ERROR_UNSUPPORTED_FORMAT;
        *total = fa_le64(z + 32);
        *cd_size = fa_le64(z + 40);
        *cd_at = fa_le64(z + 48);
        limit = z_at;
    }
    if (*cd_at > limit || *cd_size > limit - *cd_at) return FA_ERROR_UNSUPPORTED_FORMAT;
    return FA_OK;
}

/* Replaces the 32-bit sizes and offset that read 0xFFFFFFFF by the ZIP64 extra
 * field's (0x0001): in the order uncompressed size, compressed size, header offset,
 * each present only when its 32-bit field is 0xFFFFFFFF. */
static int apply_zip64_extra(fa_zip_entry* e, const uint8_t* extra, size_t extra_len, int need_size,
                             int need_comp, int need_offset)
{
    if (!need_size && !need_comp && !need_offset) return 0;
    while (extra_len >= 4) {
        uint32_t id = fa_le16(extra), len = fa_le16(extra + 2);
        if (len > extra_len - 4) return -1;
        if (id == 0x0001) {
            const uint8_t* f = extra + 4;
            uint32_t left = len;
            if (need_size) { if (left < 8) return -1; e->size = fa_le64(f); f += 8; left -= 8; }
            if (need_comp) { if (left < 8) return -1; e->compressed_size = fa_le64(f); f += 8; left -= 8; }
            if (need_offset) { if (left < 8) return -1; e->header_offset = fa_le64(f); }
            return 0;
        }
        extra += 4 + len;
        extra_len -= 4 + len;
    }
    return -1;
}

/* The extended timestamp extra field (0x5455: flags, then UTC mtime, atime, ctime as
 * 32-bit seconds, each only when its flag bit is set; the central copy holds mtime only). */
static void read_times(fa_zip_entry* e, const uint8_t* extra, size_t extra_len)
{
    while (extra_len >= 4) {
        uint32_t id = fa_le16(extra), len = fa_le16(extra + 2);
        if (len > extra_len - 4) return;
        if (id == 0x5455 && len >= 5 && (extra[4] & 1)) {
            e->mtime_ns = (int64_t)fa_le32(extra + 5) * 1000000000ll;
            return;
        }
        extra += 4 + len;
        extra_len -= 4 + len;
    }
}

static int by_offset(const void* a, const void* b)
{
    const fa_zip_entry* x = *(const fa_zip_entry* const*)a;
    const fa_zip_entry* y = *(const fa_zip_entry* const*)b;
    if (x->header_offset != y->header_offset) return x->header_offset < y->header_offset ? -1 : 1;
    return x < y ? -1 : x > y;   /* the directory's order */
}

/* Entries whose local header and data run into an earlier entry's are left out
 * (only a hostile archive points many entries at the same bytes, each unpacked
 * in full). The rest keep the directory's order. Returns how many are kept. */
static uint32_t drop_overlapping(fa_zip_entry* e, uint32_t n)
{
    fa_zip_entry** order;
    uint8_t* drop;
    uint64_t end = 0;
    uint32_t i, kept = 0;
    if (n < 2) return n;
    order = (fa_zip_entry**)malloc((size_t)n * sizeof(*order));
    drop = (uint8_t*)calloc(n, 1);
    if (!order || !drop) { free(order); free(drop); return n; }   /* the unpack limits still hold */
    for (i = 0; i < n; i++) order[i] = &e[i];
    qsort(order, n, sizeof(*order), by_offset);
    for (i = 0; i < n; i++) {
        fa_zip_entry* x = order[i];
        if (i > 0 && x->header_offset < end) { drop[x - e] = 1; continue; }
        end = x->header_offset > UINT64_MAX - ZIP_LOCAL_SIZE ||
                      x->compressed_size > UINT64_MAX - ZIP_LOCAL_SIZE - x->header_offset
                  ? UINT64_MAX : x->header_offset + ZIP_LOCAL_SIZE + x->compressed_size;
    }
    free(order);
    for (i = 0; i < n; i++) if (!drop[i]) e[kept++] = e[i];
    free(drop);
    return kept;
}

fa_result fa_zip_list(fa_source* src, fa_zip_entry** out_entries, uint32_t* out_count)
{
    uint8_t* tail = NULL;
    uint8_t* cd = NULL;
    size_t tail_len, end, scan_from, pos;
    int64_t len = src->length, tail_at;
    uint64_t total, i, cd_at, cd_size;
    uint32_t n = 0, cap = 0;
    fa_zip_entry* entries = NULL;
    fa_result r;
    *out_entries = NULL;
    *out_count = 0;
    if (len < 0 || !src->seekable) return FA_ERROR_NOT_SEEKABLE;
    if (len < ZIP_END_SIZE) return FA_ERROR_UNSUPPORTED_FORMAT;
    /* The end record is the last thing in the file, followed only by a comment of
     * at most 64 KB; the ZIP64 locator sits right before it: only that much of the
     * end is read. */
    tail_len = len > ZIP_END_SIZE + ZIP64_LOCATOR_SIZE + 0xFFFF ? ZIP_END_SIZE + ZIP64_LOCATOR_SIZE + 0xFFFF : (size_t)len;
    tail_at = len - (int64_t)tail_len;
    tail = (uint8_t*)malloc(tail_len);
    if (!tail) return FA_ERROR_OUT_OF_MEMORY;
    r = read_at(src, tail_at, tail, tail_len);
    if (r != FA_OK) goto done;
    r = FA_ERROR_UNSUPPORTED_FORMAT;
    end = tail_len - ZIP_END_SIZE;
    scan_from = 0;
    for (;;) {
        if (fa_le32(tail + end) == ZIP_END_SIG && end + ZIP_END_SIZE + fa_le16(tail + end + 20) == tail_len) break;
        if (end == scan_from) goto done;
        end--;
    }
    r = find_directory(src, tail, end, tail_at, &total, &cd_at, &cd_size);
    if (r != FA_OK) goto done;
    r = FA_ERROR_UNSUPPORTED_FORMAT;
    /* A real directory holds every entry it counts; karaoke ones are a few hundred bytes. */
    if (total > cd_size / ZIP_CENTRAL_SIZE || cd_size > ZIP_MAX_DIRECTORY || total > UINT32_MAX) goto done;
    cd = (uint8_t*)malloc(cd_size ? (size_t)cd_size : 1);
    if (!cd) { r = FA_ERROR_OUT_OF_MEMORY; goto done; }
    r = read_at(src, (int64_t)cd_at, cd, (size_t)cd_size);
    if (r != FA_OK) goto done;
    r = FA_ERROR_UNSUPPORTED_FORMAT;
    pos = 0;
    for (i = 0; i < total; i++) {
        const uint8_t* h = cd + pos;
        uint32_t flags, name_len, extra_len, skip, comp32, size32, off32;
        fa_zip_entry* e;
        if (cd_size - pos < ZIP_CENTRAL_SIZE || fa_le32(h) != ZIP_CENTRAL_SIG) goto done;
        flags = fa_le16(h + 8);
        name_len = fa_le16(h + 28);
        extra_len = fa_le16(h + 30);
        skip = ZIP_CENTRAL_SIZE + name_len + extra_len + fa_le16(h + 32);
        if (cd_size - pos < skip) goto done;
        /* Directories are left out; encrypted entries are listed (and refused when read). */
        if (name_len == 0 || h[ZIP_CENTRAL_SIZE + name_len - 1] == '/') { pos += skip; continue; }
        if (n == cap) {
            fa_zip_entry* grown;
            cap = cap ? cap * 2 : 16;
            if (cap > total) cap = (uint32_t)total;
            grown = (fa_zip_entry*)realloc(entries, (size_t)cap * sizeof(*entries));
            if (!grown) { r = FA_ERROR_OUT_OF_MEMORY; goto done; }
            entries = grown;
        }
        e = &entries[n];
        memset(e, 0, sizeof(*e));
        e->data_offset = -1;
        e->method = fa_le16(h + 10);
        e->flags = (uint16_t)(flags & ~FA_ZE_NO_CRC);   /* bit 14 is ours (no CRC to check): never taken from the file */
        e->dos_time = fa_le16(h + 12);
        e->dos_date = fa_le16(h + 14);
        e->mtime_ns = e->atime_ns = e->ctime_ns = INT64_MIN;
        read_times(e, h + ZIP_CENTRAL_SIZE + name_len, extra_len);
        e->crc32 = fa_le32(h + 16);
        comp32 = fa_le32(h + 20);
        size32 = fa_le32(h + 24);
        off32 = fa_le32(h + 42);
        e->compressed_size = comp32;
        e->size = size32;
        e->header_offset = off32;
        if (apply_zip64_extra(e, h + ZIP_CENTRAL_SIZE + name_len, extra_len, size32 == 0xFFFFFFFFu,
                              comp32 == 0xFFFFFFFFu, off32 == 0xFFFFFFFFu) != 0)
            goto done;
        if (name_len >= sizeof(e->name)) name_len = sizeof(e->name) - 1;
        memcpy(e->name, h + ZIP_CENTRAL_SIZE, name_len);
        e->name[name_len] = '\0';
        pos += skip;
        n++;
    }
    n = drop_overlapping(entries, n);
    *out_entries = entries;
    *out_count = n;
    entries = NULL;
    r = FA_OK;
done:
    free(entries);
    free(cd);
    free(tail);
    return r;
}

fa_result fa_zip_entry_offset(fa_source* src, const fa_zip_entry* e, int64_t* out_offset)
{
    uint8_t h[ZIP_LOCAL_SIZE];
    uint64_t data_pos, len = (uint64_t)src->length;
    fa_result r;
    *out_offset = 0;
    if (e->data_offset >= 0) {
        /* A format with no local header (tar, gzip, bzip2, xz): the directory said where it is. */
        data_pos = (uint64_t)e->data_offset;
        if (src->length < 0 || data_pos > len || len - data_pos < e->compressed_size) return FA_ERROR_UNSUPPORTED_FORMAT;
    } else {
        if (src->length < 0 || e->header_offset > len || len - e->header_offset < ZIP_LOCAL_SIZE) return FA_ERROR_UNSUPPORTED_FORMAT;
        r = read_at(src, (int64_t)e->header_offset, h, sizeof(h));
        if (r != FA_OK) return r;
        if (fa_le32(h) != ZIP_LOCAL_SIG) return FA_ERROR_UNSUPPORTED_FORMAT;
        data_pos = e->header_offset + ZIP_LOCAL_SIZE + fa_le16(h + 26) + fa_le16(h + 28);
        if (data_pos > len || len - data_pos < e->compressed_size) return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    if (e->flags & 1) return FA_ERROR_PROTECTED_CONTENT;   /* encrypted: no password support */
    if (e->method != 0 && e->method != 8 && e->method != 9 && e->method != 12 && e->method != 14 && e->method != 95)
        return FA_ERROR_UNSUPPORTED_FORMAT;
    if (e->method == 0 && e->compressed_size != e->size) return FA_ERROR_UNSUPPORTED_FORMAT;
    /* DEFLATE expands at most 1032:1 (258-byte matches in about two bits each), so a
     * larger declared size is a lie: refuse it before allocating. */
    if (e->method == 8 && e->size / 1032 > e->compressed_size + 1024) return FA_ERROR_UNSUPPORTED_FORMAT;
    /* Deflate64 matches reach 65,538 bytes: at most about 26,000:1. */
    if (e->method == 9 && e->size / 32768 > e->compressed_size + 1024) return FA_ERROR_UNSUPPORTED_FORMAT;
    *out_offset = (int64_t)data_pos;
    return FA_OK;
}

/* The other methods go through the decoders of codec.c. Sinks: the output buffer, a file
 * (CRC kept), or the first bytes only. */
typedef struct codec_sink {
    uint8_t* out;
    size_t cap, pos;
    FILE* file;
    uint32_t crc;
    int prefix, io_error;
} codec_sink;

static int codec_sink_put(void* ctx, const uint8_t* data, size_t n)
{
    codec_sink* c = (codec_sink*)ctx;
    if (c->file) {
        if (fwrite(data, 1, n, c->file) != n) { c->io_error = 1; return 1; }
        c->crc = fa_crc32_update(c->crc, data, n);
        return 0;
    }
    if (c->prefix) {
        size_t take = n < c->cap - c->pos ? n : c->cap - c->pos;
        memcpy(c->out + c->pos, data, take);
        c->pos += take;
        return c->pos >= c->cap;
    }
    if (n > c->cap - c->pos) return 1;
    memcpy(c->out + c->pos, data, n);
    c->pos += n;
    return 0;
}

static fa_result unpack_codec(fa_source* src, const fa_zip_entry* e, int64_t data_pos, uint8_t* out, size_t out_cap, FILE* sink,
                              const volatile int32_t* cancel, size_t* prefix)
{
    int codec = e->method == 12 ? FA_CODEC_BZIP2 : e->method == 14 ? FA_CODEC_LZMA_ZIP : FA_CODEC_XZ;
    fa_cin in;
    codec_sink c;
    uint64_t made = 0;
    int stopped = 0;
    fa_result r;
    memset(&in, 0, sizeof(in));
    in.left = e->compressed_size;
    if (src->kind == FA_SOURCE_MEMORY) in.mem = src->mem + data_pos;
    else {
        in.src = src;
        if (fa_source_seek(src, data_pos) != FA_OK) return FA_ERROR_IO;
    }
    if (!prefix && !sink) {
        r = fa_codec_to_buffer(codec, in, out, out_cap, cancel);
        if (r == FA_OK && !(e->flags & FA_ZE_NO_CRC) && fa_crc32(out, out_cap) != e->crc32) r = FA_ERROR_UNSUPPORTED_FORMAT;
        return r;
    }
    memset(&c, 0, sizeof(c));
    c.out = out; c.cap = out_cap; c.file = sink; c.prefix = prefix != NULL;
    r = fa_codec_to_sink(codec, in, e->size, codec_sink_put, &c, cancel, &made, &stopped);
    if (c.io_error) return FA_ERROR_IO;
    if (prefix) {
        *prefix = r == FA_OK ? c.pos : 0;
        return r;
    }
    if (r == FA_OK && sink) {
        if (fflush(sink) != 0) r = FA_ERROR_IO;
        else if (!(e->flags & FA_ZE_NO_CRC) && c.crc != e->crc32) r = FA_ERROR_UNSUPPORTED_FORMAT;
    }
    return r;
}

/* Unpacks entry `e` into `out` (out_cap bytes; the whole entry, or INF_OUT_CHUNK
 * when writing to `sink`), CRC checked. With *prefix set, only the first out_cap
 * bytes (at most the entry's size) are unpacked, unchecked, and *prefix gets how many. */
static fa_result unpack(fa_source* src, const fa_zip_entry* e, uint8_t* out, size_t out_cap, FILE* sink,
                        const volatile int32_t* cancel, size_t* prefix)
{
    inf_state s;
    int64_t data_pos;
    uint64_t want = e->size;
    fa_result r = fa_zip_entry_offset(src, e, &data_pos);
    if (r != FA_OK) return r;
    if (fa_source_seek(src, data_pos) != FA_OK) return FA_ERROR_IO;
    if (prefix && want > out_cap) want = out_cap;
    if (e->method == 12 || e->method == 14 || e->method == 95) return unpack_codec(src, e, data_pos, out, out_cap, sink, cancel, prefix);
    memset(&s, 0, sizeof(s));
    s.out = out;
    s.out_cap = out_cap;
    s.total = want;
    if (sink) { s.sink = inf_file_sink; s.sink_ctx = sink; }
    s.cancel = cancel;
    if (inf_cancelled(&s)) return FA_ERROR_CANCELLED;
    if (src->kind == FA_SOURCE_MEMORY) {
        s.in = src->mem + data_pos;   /* no copy of a block already in memory */
        s.in_size = (size_t)e->compressed_size;
    } else {
        s.src = src;
        s.src_left = e->compressed_size;
        s.in_buf = (uint8_t*)malloc(INF_IN_CHUNK);
        if (!s.in_buf) return FA_ERROR_OUT_OF_MEMORY;
    }
    if (e->method == 8 || e->method == 9) {
        s.d64 = e->method == 9;
        r = inf_run(&s);
        /* The prefix ends where the output buffer is full: the stream goes on, which
         * inf_run counts as a failure. */
        if (prefix && r == FA_ERROR_UNSUPPORTED_FORMAT && s.out_pos > 0) r = FA_OK;
    } else {
        r = inf_copy(&s, want) == 0 ? FA_OK : s.cancelled ? FA_ERROR_CANCELLED : s.io_error ? FA_ERROR_IO : FA_ERROR_UNSUPPORTED_FORMAT;
    }
    free(s.in_buf);
    if (prefix) {
        *prefix = r == FA_OK ? s.out_pos : 0;
        return r;
    }
    if (r == FA_OK && sink) {
        if (fwrite(s.out, 1, s.out_pos, sink) != s.out_pos || fflush(sink) != 0) r = FA_ERROR_IO;
    }
    if (r == FA_OK && !(e->flags & FA_ZE_NO_CRC) && fa_crc32_update(s.crc, s.out, s.out_pos) != e->crc32) r = FA_ERROR_UNSUPPORTED_FORMAT;
    return r;
}

fa_result fa_zip_extract(fa_source* src, const fa_zip_entry* e, uint64_t max_size, const volatile int32_t* cancel, uint8_t** out_data)
{
    uint8_t* out;
    fa_result r;
    *out_data = NULL;
    if (e->size > max_size || e->size > (uint64_t)(SIZE_MAX - 1)) return FA_ERROR_TOO_LARGE;
    out = (uint8_t*)malloc(e->size ? (size_t)e->size : 1);
    if (!out) return FA_ERROR_OUT_OF_MEMORY;
    r = unpack(src, e, out, (size_t)e->size, NULL, cancel, NULL);
    if (r != FA_OK) { free(out); return r; }
    *out_data = out;
    return FA_OK;
}

fa_result fa_zip_extract_file(fa_source* src, const fa_zip_entry* e, const volatile int32_t* cancel, FILE* f)
{
    uint8_t* out;
    fa_result r;
    if (e->size > FA_ZIP_TEMP_LIMIT) return FA_ERROR_TOO_LARGE;
    out = (uint8_t*)malloc(INF_OUT_CHUNK);
    if (!out) return FA_ERROR_OUT_OF_MEMORY;
    r = unpack(src, e, out, INF_OUT_CHUNK, f, cancel, NULL);
    free(out);
    return r;
}

fa_result fa_zip_extract_prefix(fa_source* src, const fa_zip_entry* e, uint8_t* buf, size_t cap, size_t* got)
{
    *got = 0;
    if (cap == 0) return FA_OK;
    return unpack(src, e, buf, cap, NULL, NULL, got);
}
