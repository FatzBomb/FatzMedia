#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
/* Tests of the archive layer (src/archive) and its public calls: the CRC-32 behind
 * every check, Deflate64, entry lists and names, the pairing rules, reading entries,
 * and playing, tagging and fingerprinting entries of path, memory and callback
 * archives. Every archive is built here byte by byte. */
#include "cdg.h"
#include "archive/archive.h"
#include "archive_fixtures.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); g_failures++; return; } } while (0)
#define CHECK_EQ(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); if (va_ != vb_) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (%lld vs %lld)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_STR(a, b) do { const char* va_ = (a); const char* vb_ = (b); if (strcmp(va_, vb_) != 0) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (\"%s\" vs \"%s\")\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define RUN(t) do { int before_ = g_failures; printf("%s\n", #t); t(); if (g_failures == before_) printf("  ok\n"); } while (0)

static fa_engine* g_engine;

/* ---- byte builders -------------------------------------------------------------- */

typedef struct buf { uint8_t* p; size_t n, cap; } buf;
static void put(buf* b, const void* d, size_t n)
{
    if (b->n + n > b->cap) { b->cap = (b->n + n) * 2 + 64; b->p = (uint8_t*)realloc(b->p, b->cap); }
    if (n) memcpy(b->p + b->n, d, n);
    b->n += n;
}
static void put8(buf* b, uint32_t v) { uint8_t c = (uint8_t)v; put(b, &c, 1); }
static void le16(buf* b, uint32_t v) { put8(b, v); put8(b, v >> 8); }
static void le32(buf* b, uint32_t v) { le16(b, v); le16(b, v >> 16); }

static buf make_wav(uint32_t rate, uint32_t frames)
{
    buf b = { 0 };
    uint32_t i, data = frames * 2;
    put(&b, "RIFF", 4); le32(&b, 36 + data); put(&b, "WAVE", 4);
    put(&b, "fmt ", 4); le32(&b, 16); le16(&b, 1); le16(&b, 1);
    le32(&b, rate); le32(&b, rate * 2); le16(&b, 2); le16(&b, 16);
    put(&b, "data", 4); le32(&b, data);
    for (i = 0; i < frames; i++) le16(&b, (uint16_t)(int16_t)(sin(i * 0.05) * 8000.0));
    return b;
}

/* One second of empty CD+G packs. */
static buf make_cdg(void)
{
    buf c = { 0 };
    uint8_t pack[24];
    int i;
    memset(pack, 0, sizeof(pack));
    for (i = 0; i < 300; i++) put(&c, pack, sizeof(pack));
    return c;
}

static buf make_text(const char* s) { buf b = { 0 }; put(&b, s, strlen(s)); return b; }

/* Bits written least significant first, as DEFLATE reads them. */
typedef struct bits { buf out; uint64_t acc; int n; } bits;
static void bits_put(bits* w, uint32_t value, int count)
{
    w->acc |= (uint64_t)value << w->n;
    w->n += count;
    while (w->n >= 8) { put8(&w->out, (uint32_t)w->acc); w->acc >>= 8; w->n -= 8; }
}
/* A Huffman code goes in most significant bit first. */
static void bits_code(bits* w, uint32_t code, int len)
{
    uint32_t r = 0;
    int i;
    for (i = 0; i < len; i++) r |= ((code >> i) & 1u) << (len - 1 - i);
    bits_put(w, r, len);
}
static void bits_literal(bits* w, uint32_t b)
{
    if (b < 144) bits_code(w, 0x30 + b, 8); else bits_code(w, 0x190 + (b - 144), 9);
}
static buf bits_finish(bits* w)
{
    if (w->n > 0) put8(&w->out, (uint32_t)w->acc);
    return w->out;
}

/* Deflate64 (method 9): one fixed-code block of literals; the data is any bytes. */
static buf deflate64_literals(const buf* d)
{
    bits w;
    size_t i;
    memset(&w, 0, sizeof(w));
    bits_put(&w, 1, 1);   /* last block */
    bits_put(&w, 1, 2);   /* fixed codes */
    for (i = 0; i < d->n; i++) bits_literal(&w, d->p[i]);
    bits_code(&w, 0, 7);  /* end of block */
    return bits_finish(&w);
}

/* "Deflate" with stored blocks: a valid method-8 entry without a compressor. */
static buf stored_deflate(const buf* d)
{
    buf z = { 0 };
    size_t pos = 0;
    do {
        size_t n = d->n - pos > 65535 ? 65535 : d->n - pos;
        put8(&z, pos + n == d->n ? 1 : 0);
        le16(&z, (uint32_t)n);
        le16(&z, (uint32_t)~n & 0xFFFF);
        put(&z, d->p + pos, n);
        pos += n;
    } while (pos < d->n);
    return z;
}

typedef struct zent {
    const char* name;
    const buf* data;            /* the entry's bytes */
    int method;                 /* 0 stored, 8 stored-block deflate, 9 deflate64 literals, -1 packed given */
    const buf* packed;          /* method -1 / 9: the compressed bytes (9 builds them when NULL) */
    uint32_t flags;             /* general purpose bits */
    uint32_t crc_xor;
    uint32_t dos_date, dos_time;
    uint32_t ext_mtime;         /* the extended timestamp field when non-zero */
} zent;

static buf make_zip(const zent* e, int count)
{
    buf z = { 0 }, cd = { 0 };
    int i;
    for (i = 0; i < count; i++) {
        buf body;
        int own = 0;
        int method = e[i].method < 0 ? 9 : e[i].method;
        uint32_t crc = fa_crc32(e[i].data->p, e[i].data->n) ^ e[i].crc_xor;
        uint32_t name_len = (uint32_t)strlen(e[i].name), offset = (uint32_t)z.n;
        if (e[i].method == 8) { body = stored_deflate(e[i].data); own = 1; }
        else if (e[i].method == 9 && !e[i].packed) { body = deflate64_literals(e[i].data); own = 1; }
        else if (e[i].packed) body = *e[i].packed;
        else body = *e[i].data;
        le32(&z, 0x04034b50); le16(&z, 20); le16(&z, e[i].flags); le16(&z, method);
        le16(&z, e[i].dos_time); le16(&z, e[i].dos_date); le32(&z, crc); le32(&z, (uint32_t)body.n);
        le32(&z, (uint32_t)e[i].data->n); le16(&z, name_len); le16(&z, 0);
        put(&z, e[i].name, name_len);
        put(&z, body.p, body.n);
        le32(&cd, 0x02014b50); le16(&cd, 20); le16(&cd, 20); le16(&cd, e[i].flags); le16(&cd, method);
        le16(&cd, e[i].dos_time); le16(&cd, e[i].dos_date); le32(&cd, crc); le32(&cd, (uint32_t)body.n);
        le32(&cd, (uint32_t)e[i].data->n); le16(&cd, name_len); le16(&cd, e[i].ext_mtime ? 9 : 0);
        le16(&cd, 0); le16(&cd, 0); le16(&cd, 0); le32(&cd, 0); le32(&cd, offset);
        put(&cd, e[i].name, name_len);
        if (e[i].ext_mtime) { le16(&cd, 0x5455); le16(&cd, 5); put8(&cd, 1); le32(&cd, e[i].ext_mtime); }
        if (own) free(body.p);
    }
    {
        uint32_t cd_offset = (uint32_t)z.n;
        put(&z, cd.p, cd.n);
        le32(&z, 0x06054b50); le16(&z, 0); le16(&z, 0); le16(&z, (uint32_t)count); le16(&z, (uint32_t)count);
        le32(&z, (uint32_t)cd.n); le32(&z, cd_offset); le16(&z, 0);
    }
    free(cd.p);
    return z;
}

static void write_file(const char* path, const buf* b)
{
    FILE* f = fopen(path, "wb");
    if (!f) return;
    fwrite(b->p, 1, b->n, f);
    fclose(f);
}

typedef struct mem_io { const buf* b; int64_t pos; int closes; } mem_io;
static int64_t FA_CALL mio_read(void* u, void* dst, size_t n)
{
    mem_io* m = (mem_io*)u;
    size_t left = (size_t)((int64_t)m->b->n - m->pos);
    if (n > left) n = left;
    memcpy(dst, m->b->p + m->pos, n);
    m->pos += (int64_t)n;
    return (int64_t)n;
}
static fa_result FA_CALL mio_seek(void* u, int64_t off)
{
    mem_io* m = (mem_io*)u;
    if (off < 0 || off > (int64_t)m->b->n) return FA_ERROR_IO;
    m->pos = off;
    return FA_OK;
}
static int64_t FA_CALL mio_length(void* u) { return (int64_t)((mem_io*)u)->b->n; }
static void FA_CALL mio_close(void* u) { ((mem_io*)u)->closes++; }

static int64_t decode_all(fa_stream* s)
{
    float pcm[4096];
    int64_t total = 0;
    for (;;) {
        fa_frames got = 0;
        fa_result r = fa_stream_read(s, pcm, 2048, &got);
        if (r == FA_ERROR_END_OF_STREAM || (r == FA_OK && got == 0)) return total;
        if (r != FA_OK) return -1;
        total += got;
    }
}

/* ---- CRC-32 ----------------------------------------------------------------------- */

static uint32_t crc_reference(const uint8_t* d, size_t n)
{
    uint32_t c = 0xFFFFFFFFu;
    size_t i;
    int k;
    for (i = 0; i < n; i++) {
        c ^= d[i];
        for (k = 0; k < 8; k++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
    }
    return ~c;
}

static void test_crc32(void)
{
    uint8_t* d = (uint8_t*)malloc(70000 + 16);
    size_t n, off;
    uint32_t i, state = 12345;
    CHECK(d != NULL);
    for (i = 0; i < 70000 + 16; i++) { state = state * 1664525u + 1013904223u; d[i] = (uint8_t)(state >> 24); }
    CHECK_EQ(fa_crc32((const uint8_t*)"123456789", 9), 0xCBF43926u);
    CHECK_EQ(fa_crc32(d, 0), 0);
    /* Every length around the vector widths and a few big ones, at every alignment. */
    for (off = 0; off < 16; off += 3)
        for (n = 0; n < 300; n += (n < 140 ? 1 : 17))
            CHECK_EQ(fa_crc32(d + off, n), crc_reference(d + off, n));
    CHECK_EQ(fa_crc32(d + 5, 70000), crc_reference(d + 5, 70000));
    /* Running: pieces of any size give the whole's CRC. */
    {
        uint32_t c = 0;
        c = fa_crc32_update(c, d, 1);
        c = fa_crc32_update(c, d + 1, 99);
        c = fa_crc32_update(c, d + 100, 10001);
        CHECK_EQ(c, crc_reference(d, 10101));
    }
    free(d);
}

/* ---- Deflate64 -------------------------------------------------------------------- */

/* The 64 KB window and the 3 + 16-bit length: a block with matches 40,000 and 50,000
 * bytes back (distance codes 30 and 31) and one of 65,538 bytes (length code 285). */
static void test_deflate64_matches(void)
{
    bits w;
    buf expect = { 0 }, z, packed;
    zent e;
    fa_archive* a = NULL;
    void* out = NULL;
    size_t size = 0, i, from;
    int k;
    struct { uint32_t len, dist; } m[3] = { { 1003, 40000 }, { 65538, 50000 }, { 300, 33000 } };
    memset(&w, 0, sizeof(w));
    bits_put(&w, 1, 1);
    bits_put(&w, 1, 2);
    for (i = 0; i < 50000; i++) {
        uint8_t b = (uint8_t)(i * 7 + i / 13);
        bits_literal(&w, b);
        put8(&expect, b);
    }
    for (k = 0; k < 3; k++) {
        bits_code(&w, 0xC0 + 5, 8);                    /* length symbol 285: 3 + 16 extra bits */
        bits_put(&w, m[k].len - 3, 16);
        if (m[k].dist >= 49153) { bits_code(&w, 31, 5); bits_put(&w, m[k].dist - 49153, 14); }
        else { bits_code(&w, 30, 5); bits_put(&w, m[k].dist - 32769, 14); }
        from = expect.n - m[k].dist;
        for (i = 0; i < m[k].len; i++) put8(&expect, expect.p[from + i]);
    }
    bits_code(&w, 0, 7);
    packed = bits_finish(&w);
    e.name = "d64.bin"; e.data = &expect; e.method = -1; e.packed = &packed; e.flags = 0; e.crc_xor = 0;
    e.dos_date = e.dos_time = e.ext_mtime = 0;
    z = make_zip(&e, 1);
    CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
    {
        fa_archive_entry ent = { .struct_size = sizeof(ent) };
        CHECK_EQ(fa_archive_get_entry(a, 0, &ent), FA_OK);
        CHECK_EQ(ent.method, FA_METHOD_DEFLATE64);
        CHECK(ent.flags & FA_ENTRY_READABLE);
        CHECK_EQ(ent.size, (long long)expect.n);
    }
    CHECK_EQ(fa_archive_read(a, 0, &out, &size), FA_OK);
    CHECK_EQ(size, expect.n);
    CHECK(memcmp(out, expect.p, size) == 0);
    fa_free(out);
    fa_archive_close(a);
    /* The same stream as plain DEFLATE fails: 285 is a 258-byte match and the distance codes stop at 29. */
    z.p[8] = 8;                                       /* the local header's method, then the directory's */
    {
        size_t at = z.n - 22;                         /* the end record: the directory is just before it */
        uint32_t cd_size = (uint32_t)(z.p[at + 12] | (z.p[at + 13] << 8) | (z.p[at + 14] << 16));
        z.p[at - cd_size + 10] = 8;
    }
    CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
    CHECK_EQ(fa_archive_read(a, 0, &out, &size), FA_ERROR_UNSUPPORTED_FORMAT);
    fa_archive_close(a);
    free(z.p); free(packed.p); free(expect.p);
}

/* ---- Entries and names ---------------------------------------------------------- */

static void test_entries_and_names(void)
{
    buf song = make_wav(8000, 800), cdg = make_cdg(), txt = make_text("hello"), empty = { 0 }, z;
    zent e[7];
    fa_archive* a = NULL;
    fa_archive_entry ent = { .struct_size = sizeof(ent) };
    uint32_t idx = 99;
    memset(e, 0, sizeof(e));
    /* A UTF-8 name with the flag, a code page 437 name without it, a backslash path, a folder. */
    e[0].name = "Sch\xc3\xb6n.mp3"; e[0].data = &song; e[0].method = 0; e[0].flags = 0x800;
    e[0].dos_date = ((2024 - 1980) << 9) | (5 << 5) | 17; e[0].dos_time = (13 << 11) | (45 << 5) | 15;
    e[1].name = "Caf\x82.cdg"; e[1].data = &cdg; e[1].method = 8; e[1].ext_mtime = 1700000000u;
    e[2].name = "disc 1\\track.txt"; e[2].data = &txt; e[2].method = 0;
    e[3].name = "folder/"; e[3].data = &empty; e[3].method = 0;
    e[4].name = "./lead.txt"; e[4].data = &txt; e[4].method = 0; e[4].flags = 1;   /* encrypted */
    e[5].name = "wide.bin"; e[5].data = &txt; e[5].method = 0; e[5].crc_xor = 0;
    e[6].name = "other.bin"; e[6].data = &txt; e[6].method = -1; e[6].packed = &txt;   /* Deflate64 */
    z = make_zip(e, 7);
    CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
    CHECK_EQ(fa_archive_get_entry_count(a), 6);   /* the folder is not an entry */
    CHECK_EQ(fa_archive_get_entry(a, 0, &ent), FA_OK);
    CHECK_STR(ent.name, "Sch\xc3\xb6n.mp3");
    CHECK_EQ(ent.kind, FA_ENTRY_AUDIO);
    CHECK_EQ(ent.method, FA_METHOD_STORED);
    CHECK_EQ(ent.size, (long long)song.n);
    CHECK((ent.flags & FA_ENTRY_HAS_CRC32) && (ent.flags & FA_ENTRY_READABLE) && !(ent.flags & FA_ENTRY_ENCRYPTED));
    CHECK_EQ(ent.crc32, fa_crc32(song.p, song.n));
    /* A DOS time is local; read as UTC: 2024-05-17 13:45:30. */
    CHECK_EQ(ent.modified_ns, 1715953530LL * 1000000000LL);
    CHECK_EQ(ent.created_ns, INT64_MIN);
    CHECK_EQ(fa_archive_get_entry(a, 1, &ent), FA_OK);
    CHECK_STR(ent.name, "Caf\xc3\xa9.cdg");           /* code page 437 0x82 is e-acute */
    CHECK_EQ(ent.kind, FA_ENTRY_CDG);
    CHECK_EQ(ent.method, FA_METHOD_DEFLATE);
    CHECK_EQ(ent.modified_ns, 1700000000LL * 1000000000LL);   /* the extended timestamp wins */
    CHECK_EQ(fa_archive_get_entry(a, 2, &ent), FA_OK);
    CHECK_STR(ent.name, "disc 1/track.txt");
    CHECK_EQ(ent.kind, FA_ENTRY_TEXT);
    CHECK_EQ(fa_archive_get_entry(a, 3, &ent), FA_OK);
    CHECK_STR(ent.name, "lead.txt");                  /* "./" goes */
    CHECK(ent.flags & FA_ENTRY_ENCRYPTED);
    CHECK(!(ent.flags & FA_ENTRY_READABLE));
    CHECK_EQ(fa_archive_get_entry(a, 5, &ent), FA_OK);
    CHECK_EQ(ent.method, FA_METHOD_DEFLATE64);
    CHECK_EQ(fa_archive_get_entry(a, 6, &ent), FA_ERROR_NOT_FOUND);
    /* An older struct gets its first fields only. */
    {
        struct { uint32_t struct_size; uint32_t index; const char* name; } small = { sizeof(small), 99, NULL };
        CHECK_EQ(fa_archive_get_entry(a, 2, (fa_archive_entry*)&small), FA_OK);
        CHECK_EQ(small.index, 2);
        CHECK_STR(small.name, "disc 1/track.txt");
    }
    /* Names: exact, then ignoring case and slash kind; two that differ by case are ambiguous. */
    CHECK_EQ(fa_archive_find(a, "disc 1/track.txt", &idx), FA_OK);
    CHECK_EQ(idx, 2);
    CHECK_EQ(fa_archive_find(a, "DISC 1\\TRACK.TXT", &idx), FA_OK);
    CHECK_EQ(idx, 2);
    CHECK_EQ(fa_archive_find(a, "SCH\xc3\x96N.MP3", &idx), FA_OK);   /* O-umlaut folds to o-umlaut */
    CHECK_EQ(idx, 0);
    CHECK_EQ(fa_archive_find(a, "nothing.mp3", &idx), FA_ERROR_NOT_FOUND);
    CHECK_EQ(fa_archive_find(a, NULL, &idx), FA_ERROR_INVALID_ARG);
    fa_archive_close(a);
    free(z.p);
    memset(e, 0, sizeof(e));
    e[0].name = "A.mp3"; e[0].data = &txt; e[0].method = 0;
    e[1].name = "a.MP3"; e[1].data = &txt; e[1].method = 0;
    e[2].name = "b.mp3"; e[2].data = &txt; e[2].method = 0;
    z = make_zip(e, 3);
    CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
    CHECK_EQ(fa_archive_find(a, "A.mp3", &idx), FA_OK);   /* exact wins */
    CHECK_EQ(idx, 0);
    CHECK_EQ(fa_archive_find(a, "a.mp3", &idx), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_archive_find(a, "B.MP3", &idx), FA_OK);
    CHECK_EQ(idx, 2);
    fa_archive_close(a);
    free(z.p); free(song.p); free(cdg.p); free(txt.p);
}

/* ---- detect ------------------------------------------------------------------------ */

static void test_detect(void)
{
    uint8_t tar[512];
    CHECK_EQ(fa_archive_detect("PK\x03\x04____", 8), FA_ARCHIVE_ZIP);
    CHECK_EQ(fa_archive_detect("Rar!\x1A\x07\x00__", 8), FA_ARCHIVE_RAR);
    CHECK_EQ(fa_archive_detect("Rar!\x1A\x07\x01\x00", 8), FA_ARCHIVE_RAR);
    CHECK_EQ(fa_archive_detect("7z\xBC\xAF\x27\x1C\x00\x04", 8), FA_ARCHIVE_7Z);
    CHECK_EQ(fa_archive_detect("\xFD" "7zXZ\0__", 8), FA_ARCHIVE_XZ);
    CHECK_EQ(fa_archive_detect("BZh9____", 8), FA_ARCHIVE_BZIP2);
    CHECK_EQ(fa_archive_detect("\x1F\x8B\x08\x00____", 8), FA_ARCHIVE_GZIP);
    memset(tar, 0, sizeof(tar));
    memcpy(tar + 257, "ustar", 5);
    CHECK_EQ(fa_archive_detect(tar, sizeof(tar)), FA_ARCHIVE_TAR);
    CHECK_EQ(fa_archive_detect("RIFF____", 8), FA_ARCHIVE_NONE);
    CHECK_EQ(fa_archive_detect(NULL, 8), FA_ARCHIVE_NONE);
    CHECK_EQ(fa_archive_detect("PK", 2), FA_ARCHIVE_NONE);
    /* A RAR or a 7z opens as no archive this build reads. */
    {
        fa_archive* a = NULL;
        uint8_t rar[64] = { 'R', 'a', 'r', '!', 0x1A, 7, 0 };
        CHECK_EQ(fa_archive_open_memory(rar, sizeof(rar), NULL, &a), FA_ERROR_UNSUPPORTED_FORMAT);
        CHECK(a == NULL);
    }
}

/* ---- Pairing rules ------------------------------------------------------------------ */

typedef struct pick_case {
    const char* names[8];       /* "x.wav" audio, "x.cdg" graphics, anything else text */
    int audio;                  /* expected index of the song (or the graphics alone), -1 = none */
    int cdg;                    /* expected graphics index, -1 = none */
} pick_case;

static buf zip_of_names(const char* const* names, buf* wav, buf* cdg, buf* txt)
{
    zent e[8];
    int n = 0;
    memset(e, 0, sizeof(e));
    while (n < 8 && names[n]) {
        size_t len = strlen(names[n]);
        e[n].name = names[n];
        e[n].method = n % 2 ? 8 : 0;   /* both kinds of entry */
        if (len > 4 && !strcmp(names[n] + len - 4, ".cdg")) e[n].data = cdg;
        else if (len > 4 && (!strcmp(names[n] + len - 4, ".wav") || !strcmp(names[n] + len - 4, ".WAV"))) e[n].data = wav;
        else e[n].data = txt;
        n++;
    }
    return make_zip(e, n);
}

static void test_pick_rules(void)
{
    static const pick_case cases[] = {
        /* 1. songs with a same-name .cdg come first, in archive order */
        { { "b.wav", "a.wav", "a.cdg", NULL }, 1, 2 },
        { { "a.cdg", "a.wav", "b.wav", "b.cdg", NULL }, 1, 0 },
        /* stems compare ignoring case, in the same folder only */
        { { "Song.WAV", "song.cdg", NULL }, 0, 1 },
        { { "x/song.wav", "y/song.cdg", NULL }, 0, 1 },                      /* one song, one .cdg: the pair */
        { { "x/song.wav", "x/other.wav", "y/song.cdg", NULL }, 0, -1 },      /* two songs: not the same folder */
        /* song.mp3.cdg pairs with song.mp3 */
        { { "other.wav", "song.mp3.cdg", "song.mp3.wav", NULL }, 2, 1 },
        { { "x.wav", "song.wav", "song.wav.cdg", NULL }, 1, 2 },
        /* 2. no match: the first song; the only .cdg goes with the only song */
        { { "Track01.wav", "SC8125-01.cdg", NULL }, 0, 1 },
        { { "a.wav", "b.wav", "z.cdg", NULL }, 0, -1 },                      /* several songs: the .cdg is not attached */
        { { "a.wav", "z.cdg", "y.cdg", NULL }, 0, -1 },                      /* several .cdg */
        { { "readme.txt", "a.wav", NULL }, 1, -1 },
        /* ignored entries */
        { { "__MACOSX/._a.wav", "a.wav", "a.cdg", NULL }, 1, 2 },
        { { "._a.wav", "Thumbs.db", "desktop.ini", "b.wav", NULL }, 3, -1 },
        /* 3. only graphics */
        { { "readme.txt", "lone.cdg", NULL }, 1, -1 },
        { { "a.cdg", "b.cdg", NULL }, 0, -1 },
    };
    size_t i;
    buf wav = make_wav(8000, 800), cdg = make_cdg(), txt = make_text("text");
    for (i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
        buf z = zip_of_names(cases[i].names, &wav, &cdg, &txt);
        fa_archive* a = NULL;
        uint32_t audio = 99;
        int32_t gfx = 99;
        if (fa_archive_open_memory(z.p, z.n, NULL, &a) != FA_OK) { fprintf(stderr, "  case %zu: open\n", i); g_failures++; return; }
        if (fa_archive_pick(a, &audio, &gfx) != FA_OK || (int)audio != cases[i].audio || gfx != cases[i].cdg) {
            fprintf(stderr, "  FAIL case %zu: pick %d,%d expected %d,%d\n", i, (int)audio, gfx, cases[i].audio, cases[i].cdg);
            g_failures++;
        }
        fa_archive_close(a);
        free(z.p);
    }
    {
        /* Nothing to play: no song and no graphics; encrypted songs say so. */
        static const char* const none[] = { "readme.txt", "pic.jpg", NULL };
        buf z = zip_of_names(none, &wav, &cdg, &txt);
        fa_archive* a = NULL;
        uint32_t audio;
        int32_t gfx;
        CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
        CHECK_EQ(fa_archive_pick(a, &audio, &gfx), FA_ERROR_NOT_FOUND);
        fa_archive_close(a);
        free(z.p);
    }
    free(wav.p); free(cdg.p); free(txt.p);
}

/* ---- Reading entries ---------------------------------------------------------------- */

static void test_read_and_reader(void)
{
    buf wav = make_wav(8000, 30000), txt = make_text("secret"), z;
    zent e[4];
    fa_archive* a = NULL;
    fa_archive_reader* rd = NULL;
    void* out = NULL;
    size_t size = 0;
    uint8_t chunk[100];
    int method;
    memset(e, 0, sizeof(e));
    e[0].name = "stored.wav"; e[0].data = &wav; e[0].method = 0;
    e[1].name = "deflate.wav"; e[1].data = &wav; e[1].method = 8;
    e[2].name = "bad.wav"; e[2].data = &wav; e[2].method = 0; e[2].crc_xor = 1;
    e[3].name = "locked.wav"; e[3].data = &txt; e[3].method = 0; e[3].flags = 1;
    z = make_zip(e, 4);
    CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
    for (method = 0; method < 2; method++) {
        CHECK_EQ(fa_archive_read(a, (uint32_t)method, &out, &size), FA_OK);
        CHECK_EQ(size, wav.n);
        CHECK(memcmp(out, wav.p, size) == 0);
        fa_free(out);
        /* On demand, seeking both ways. */
        CHECK_EQ(fa_archive_open_reader(a, (uint32_t)method, &rd), FA_OK);
        CHECK_EQ(fa_archive_reader_get_size(rd), (long long)wav.n);
        CHECK_EQ(fa_archive_reader_seek(rd, 5000), FA_OK);
        CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), 100);
        CHECK(memcmp(chunk, wav.p + 5000, 100) == 0);
        CHECK_EQ(fa_archive_reader_seek(rd, 7), FA_OK);
        CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), 100);
        CHECK(memcmp(chunk, wav.p + 7, 100) == 0);
        CHECK_EQ(fa_archive_reader_seek(rd, (int64_t)wav.n - 10), FA_OK);
        CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), 10);
        CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), 0);
        fa_archive_reader_close(rd);
    }
    CHECK_EQ(fa_archive_read(a, 2, &out, &size), FA_ERROR_UNSUPPORTED_FORMAT);   /* the CRC does not match */
    CHECK(out == NULL);
    CHECK_EQ(fa_archive_read(a, 3, &out, &size), FA_ERROR_PROTECTED_CONTENT);
    CHECK_EQ(fa_archive_open_reader(a, 3, &rd), FA_ERROR_PROTECTED_CONTENT);
    CHECK_EQ(fa_archive_read(a, 9, &out, &size), FA_ERROR_NOT_FOUND);
    fa_archive_close(a);
    /* The memory limit: a compressed entry past it is too large without a temp folder, read through one with. */
    {
        fa_archive_options o;
        fa_archive_options_init(&o);
        o.memory_limit_mb = 1;
        {
            buf big = make_wav(8000, 700000);   /* 1.4 MB */
            zent be = { "big.wav", &big, 8, NULL, 0, 0, 0, 0, 0 };
            buf bz = make_zip(&be, 1);
            CHECK_EQ(fa_archive_open_memory(bz.p, bz.n, &o, &a), FA_OK);
            CHECK_EQ(fa_archive_read(a, 0, &out, &size), FA_ERROR_TOO_LARGE);
            CHECK_EQ(fa_archive_open_reader(a, 0, &rd), FA_OK);
            CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), FA_ERROR_TOO_LARGE);
            fa_archive_reader_close(rd);
            fa_archive_close(a);
            o.temp_directory = "archive-test-tmp";
            CHECK_EQ(fa_archive_open_memory(bz.p, bz.n, &o, &a), FA_OK);
            CHECK_EQ(fa_archive_open_reader(a, 0, &rd), FA_OK);
            CHECK_EQ(fa_archive_reader_seek(rd, 1000000), FA_OK);
            CHECK_EQ(fa_archive_reader_read(rd, chunk, sizeof(chunk)), 100);
            CHECK(memcmp(chunk, big.p + 1000000, 100) == 0);
            fa_archive_reader_close(rd);
            fa_archive_close(a);
            free(bz.p); free(big.p);
        }
        /* A cancel flag stops an unpack. */
        {
            volatile int32_t flag = 1;
            o.temp_directory = NULL;
            o.memory_limit_mb = 0;
            o.cancel = &flag;
            CHECK_EQ(fa_archive_open_memory(z.p, z.n, &o, &a), FA_OK);
            CHECK_EQ(fa_archive_read(a, 1, &out, &size), FA_ERROR_CANCELLED);
            fa_archive_close(a);
        }
    }
    free(z.p); free(wav.p); free(txt.p);
}

/* ---- Playing, tags and fingerprints of entries --------------------------------------- */

static void test_open_archive_streams(void)
{
    buf song = make_wav(8000, 8000), other = make_wav(8000, 4000), cdg = make_cdg(), z;
    zent e[4];
    fa_archive* a = NULL;
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    fa_open_options o;
    fa_io_callbacks io;
    mem_io m;
    uint32_t idx;
    int method;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    for (method = 0; method < 3; method++) {
        int m_ = method == 0 ? 0 : method == 1 ? 8 : 9;   /* stored, deflate, deflate64 */
        memset(e, 0, sizeof(e));
        e[0].name = "one.wav"; e[0].data = &other; e[0].method = m_;
        e[1].name = "Two.WAV"; e[1].data = &song; e[1].method = m_;
        e[2].name = "two.cdg"; e[2].data = &cdg; e[2].method = m_;
        z = make_zip(e, 3);
        write_file("archive_test.zip", &z);
        /* The pick: a song with its same-name .cdg comes first, whatever the archive order. */
        CHECK_EQ(fa_stream_open_path(g_engine, "archive_test.zip", &o, &s), FA_OK);
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 1);
        CHECK_EQ(decode_all(s), 8000);
        fa_stream_close(s); s = NULL;
        /* Named, ignoring case; its own graphics only. */
        o.archive_entry = "ONE.wav";
        CHECK_EQ(fa_stream_open_path(g_engine, "archive_test.zip", &o, &s), FA_OK);
        CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 0);                  /* two songs: the .cdg is not one.wav's */
        CHECK_EQ(decode_all(s), 4000);
        fa_stream_close(s); s = NULL;
        o.archive_entry = "two.cdg";                /* a .cdg named: its song comes with it */
        CHECK_EQ(fa_stream_open_memory(g_engine, z.p, z.n, &o, &s), FA_OK);
        CHECK_EQ(decode_all(s), 8000);
        fa_stream_close(s); s = NULL;
        o.archive_entry = "missing.wav";
        CHECK_EQ(fa_stream_open_memory(g_engine, z.p, z.n, &o, &s), FA_ERROR_NOT_FOUND);
        o.archive_entry = NULL;
        /* Through callbacks: the entry plays and the app's close is called once. */
        memset(&io, 0, sizeof(io));
        io.read = mio_read; io.seek = mio_seek; io.length = mio_length; io.close = mio_close;
        memset(&m, 0, sizeof(m));
        m.b = &z;
        o.archive_entry = "one.wav";
        CHECK_EQ(fa_stream_open_io(g_engine, &io, &m, &o, &s), FA_OK);
        CHECK_EQ(decode_all(s), 4000);
        fa_stream_close(s); s = NULL;
        CHECK_EQ(m.closes, 1);
        o.archive_entry = NULL;
        /* An open archive: play by index, by pick; the stream keeps the archive alive. */
        CHECK_EQ(fa_archive_open_path("archive_test.zip", NULL, &a), FA_OK);
        CHECK_EQ(fa_archive_find(a, "one.wav", &idx), FA_OK);
        CHECK_EQ(fa_stream_open_archive(g_engine, a, (int32_t)idx, &o, &s), FA_OK);
        fa_archive_close(a);
        CHECK_EQ(decode_all(s), 4000);
        CHECK_EQ(fa_stream_seek(s, 100), FA_OK);
        CHECK_EQ(decode_all(s), 3900);
        fa_stream_close(s); s = NULL;
        CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
        CHECK_EQ(fa_stream_open_archive(g_engine, a, -1, &o, &s), FA_OK);
        fa_archive_close(a);
        CHECK_EQ(decode_all(s), 8000);
        fa_stream_close(s); s = NULL;
        CHECK_EQ(fa_stream_open_archive(g_engine, NULL, -1, &o, &s), FA_ERROR_INVALID_ARG);
        remove("archive_test.zip");
        free(z.p);
    }
    free(song.p); free(other.p); free(cdg.p);
}

static void test_tags_and_fingerprint_of_entries(void)
{
    buf song = make_wav(8000, 8000), other = make_wav(8000, 4000), cdg = make_cdg(), z;
    zent e[3];
    fa_tags* t = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    fa_fingerprint fa_, fb_, fc_;
    fa_archive* a = NULL;
    fa_io_callbacks io;
    mem_io m;
    int method;
    for (method = 0; method < 3; method++) {
        int m_ = method == 0 ? 0 : method == 1 ? 8 : 9;
        memset(e, 0, sizeof(e));
        e[0].name = "one.wav"; e[0].data = &other; e[0].method = m_;
        e[1].name = "two.wav"; e[1].data = &song; e[1].method = m_;
        e[2].name = "two.cdg"; e[2].data = &cdg; e[2].method = m_;
        z = make_zip(e, 3);
        write_file("archive_test.zip", &z);
        CHECK_EQ(fa_tags_read_archive_entry("archive_test.zip", "one.wav", 0, &t), FA_OK);
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 0);
        CHECK(fabs(((double)info.duration / 1e9) - 0.5) < 0.01);
        fa_tags_close(t); t = NULL;
        CHECK_EQ(fa_tags_read_archive_entry("archive_test.zip", NULL, 0, &t), FA_OK);   /* the pick */
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 1);
        CHECK(fabs(((double)info.duration / 1e9) - 1.0) < 0.01);
        fa_tags_close(t); t = NULL;
        CHECK_EQ(fa_tags_read_archive_entry("archive_test.zip", "nope.wav", 0, &t), FA_ERROR_NOT_FOUND);
        CHECK(t == NULL);
        CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
        CHECK_EQ(fa_tags_read_archive(a, 0, 0, &t), FA_OK);
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK(fabs(((double)info.duration / 1e9) - 0.5) < 0.01);
        fa_tags_close(t); t = NULL;
        CHECK_EQ(fa_tags_read_archive(a, 17, 0, &t), FA_ERROR_NOT_FOUND);
        fa_archive_close(a);
        /* Tags through callbacks: a plain file and a ZIP, and the app's close is not called. */
        memset(&io, 0, sizeof(io));
        io.read = mio_read; io.seek = mio_seek; io.length = mio_length; io.close = mio_close;
        memset(&m, 0, sizeof(m));
        m.b = &z;
        CHECK_EQ(fa_tags_read_io(&io, &m, 0, &t), FA_OK);
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 1);
        fa_tags_close(t); t = NULL;
        CHECK_EQ(m.closes, 0);
        m.b = &song; m.pos = 0;
        CHECK_EQ(fa_tags_read_io(&io, &m, 0, &t), FA_OK);
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK(fabs(((double)info.duration / 1e9) - 1.0) < 0.01);
        fa_tags_close(t); t = NULL;
        /* One song, one key: loose, in this archive, named, the pick. */
        memset(&fa_, 0, sizeof(fa_)); fa_.struct_size = sizeof(fa_);
        memset(&fb_, 0, sizeof(fb_)); fb_.struct_size = sizeof(fb_);
        memset(&fc_, 0, sizeof(fc_)); fc_.struct_size = sizeof(fc_);
        CHECK_EQ(fa_fingerprint_memory(song.p, song.n, &fa_), FA_OK);
        CHECK_EQ(fa_fingerprint_archive_entry("archive_test.zip", "two.wav", &fb_), FA_OK);
        CHECK_EQ(fa_fingerprint_archive_entry("archive_test.zip", NULL, &fc_), FA_OK);
        CHECK(fa_.key == fb_.key && fa_.audio_bytes == fb_.audio_bytes);
        CHECK(fa_.key == fc_.key && fa_.audio_bytes == fc_.audio_bytes);
        CHECK_EQ(fa_fingerprint_memory(z.p, z.n, &fc_), FA_OK);
        CHECK(fa_.key == fc_.key);
        {
            uint32_t two;
            CHECK_EQ(fa_archive_open_memory(z.p, z.n, NULL, &a), FA_OK);
            CHECK_EQ(fa_archive_find(a, "two.wav", &two), FA_OK);
            CHECK_EQ(fa_fingerprint_archive(a, (int32_t)two, &fb_), FA_OK);
            CHECK_EQ(fa_fingerprint_archive(a, -1, &fc_), FA_OK);
            CHECK_EQ(fa_fingerprint_archive(a, 99, &fc_), FA_ERROR_NOT_FOUND);
            CHECK(fa_.key == fb_.key && fa_.key == fc_.key);
            fa_archive_close(a);
        }
        remove("archive_test.zip");
        free(z.p);
    }
    free(song.p); free(other.p); free(cdg.p);
}

/* A stored entry plays in place; encrypted and unreadable ones report why. */
static void test_unplayable_entries(void)
{
    buf song = make_wav(8000, 800), z;
    zent e[2];
    fa_stream* s = NULL;
    fa_open_options o;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    memset(e, 0, sizeof(e));
    e[0].name = "locked.wav"; e[0].data = &song; e[0].method = 0; e[0].flags = 1;
    z = make_zip(e, 1);
    CHECK_EQ(fa_stream_open_memory(g_engine, z.p, z.n, &o, &s), FA_ERROR_PROTECTED_CONTENT);
    free(z.p);
    e[0].flags = 0;
    e[0].method = -1;                         /* method 9, but not a valid stream */
    {
        buf junk = make_text("this is not deflate data at all");
        e[0].packed = &junk;
        z = make_zip(e, 1);
        CHECK_EQ(fa_stream_open_memory(g_engine, z.p, z.n, &o, &s), FA_ERROR_UNSUPPORTED_FORMAT);
        free(z.p); free(junk.p);
    }
    free(song.p);
}

/* ---- tar, gzip, bzip2 and xz ---------------------------------------------------------- */

static uint64_t fnv64(const uint8_t* p, size_t n)
{
    uint64_t h = 0xcbf29ce484222325ull;
    size_t i;
    for (i = 0; i < n; i++) h = (h ^ p[i]) * 0x100000001b3ull;
    return h;
}

typedef struct fixture { const char* what; const unsigned char* p; size_t n; int32_t format, filter; int32_t method; } fixture;

static const char* const k_small_names[4] = { "songs/a/song.wav", "songs/a/song.cdg", "songs/other.wav", "notes.txt" };
static const uint32_t k_small_sizes[4] = { FX_SMALL_SIZE_0, FX_SMALL_SIZE_1, FX_SMALL_SIZE_2, FX_SMALL_SIZE_3 };
static const uint64_t k_small_fnv[4] = { FX_SMALL_FNV_0, FX_SMALL_FNV_1, FX_SMALL_FNV_2, FX_SMALL_FNV_3 };

static void check_small_archive(fa_archive* a, int32_t format, int32_t filter, int32_t method, int reverse)
{
    int32_t f = -1, fl = -1;
    uint32_t k, audio = 99;
    int32_t cdg = -9;
    CHECK_EQ(fa_archive_get_format(a, &f), FA_OK);
    CHECK_EQ(fa_archive_get_filter(a, &fl), FA_OK);
    CHECK_EQ(f, format);
    CHECK_EQ(fl, filter);
    CHECK_EQ(fa_archive_get_entry_count(a), 4);
    for (k = 0; k < 4; k++) {
        uint32_t i = reverse ? 3 - k : k;
        fa_archive_entry e = { .struct_size = sizeof(e) };
        void* d = NULL;
        size_t n = 0;
        CHECK_EQ(fa_archive_get_entry(a, i, &e), FA_OK);
        CHECK_STR(e.name, k_small_names[i]);
        CHECK_EQ(e.size, k_small_sizes[i]);
        CHECK_EQ(e.method, method);
        CHECK(e.flags & FA_ENTRY_READABLE);
        CHECK_EQ(fa_archive_read(a, i, &d, &n), FA_OK);
        CHECK_EQ(n, k_small_sizes[i]);
        CHECK(fnv64((const uint8_t*)d, n) == k_small_fnv[i]);
        fa_free(d);
    }
    CHECK_EQ(fa_archive_pick(a, &audio, &cdg), FA_OK);
    CHECK_EQ(audio, 0);
    CHECK_EQ(cdg, 1);
}

static void test_tar_and_compressed_tar(void)
{
    static const fixture fx[4] = {
        { "tar", fx_small_tar, sizeof(fx_small_tar), FA_ARCHIVE_TAR, FA_ARCHIVE_FILTER_NONE, FA_METHOD_STORED },
        { "tar.gz", fx_small_tar_gz, sizeof(fx_small_tar_gz), FA_ARCHIVE_TAR, FA_ARCHIVE_FILTER_GZIP, FA_METHOD_DEFLATE },
        { "tar.bz2", fx_small_tar_bz2, sizeof(fx_small_tar_bz2), FA_ARCHIVE_TAR, FA_ARCHIVE_FILTER_BZIP2, FA_METHOD_BZIP2 },
        { "tar.xz", fx_small_tar_xz, sizeof(fx_small_tar_xz), FA_ARCHIVE_TAR, FA_ARCHIVE_FILTER_XZ, FA_METHOD_LZMA2 },
    };
    fa_fingerprint loose = { .struct_size = sizeof(loose) };
    uint32_t i;
    CHECK_EQ(fa_fingerprint_memory(fx_small_tar + 512, FX_SMALL_SIZE_0, &loose), FA_OK);   /* the first entry's data */
    for (i = 0; i < 4; i++) {
        fa_archive* a = NULL;
        fa_tags* t = NULL;
        fa_stream_info info = { .struct_size = sizeof(info) };
        fa_fingerprint fp = { .struct_size = sizeof(fp) };
        buf file;
        int pass;
        printf("  %s\n", fx[i].what);
        CHECK_EQ(fa_archive_detect(fx[i].p, fx[i].n), fx[i].filter == FA_ARCHIVE_FILTER_NONE ? FA_ARCHIVE_TAR
                                                      : fx[i].filter == FA_ARCHIVE_FILTER_GZIP ? FA_ARCHIVE_GZIP
                                                      : fx[i].filter == FA_ARCHIVE_FILTER_BZIP2 ? FA_ARCHIVE_BZIP2 : FA_ARCHIVE_XZ);
        file.p = (uint8_t*)fx[i].p; file.n = fx[i].n; file.cap = fx[i].n;
        write_file("archive_test.bin", &file);
        for (pass = 0; pass < 4; pass++) {
            fa_archive_options o;
            fa_archive_options_init(&o);
            if (pass == 3) o.memory_limit_mb = 1;   /* still holds the little archive */
            if (pass & 1) CHECK_EQ(fa_archive_open_path("archive_test.bin", &o, &a), FA_OK);
            else CHECK_EQ(fa_archive_open_memory(fx[i].p, fx[i].n, &o, &a), FA_OK);
            check_small_archive(a, FA_ARCHIVE_TAR, fx[i].filter, fx[i].method, pass >= 2);
            fa_archive_close(a);
        }
        /* One song, one key: the same in every container, named or picked, by path or by memory. */
        CHECK_EQ(fa_fingerprint_archive_entry("archive_test.bin", "songs/a/song.wav", &fp), FA_OK);
        CHECK(fp.key == loose.key && fp.audio_bytes == loose.audio_bytes);
        memset(&fp, 0, sizeof(fp)); fp.struct_size = sizeof(fp);
        CHECK_EQ(fa_fingerprint_archive_entry("archive_test.bin", NULL, &fp), FA_OK);
        CHECK(fp.key == loose.key);
        memset(&fp, 0, sizeof(fp)); fp.struct_size = sizeof(fp);
        CHECK_EQ(fa_fingerprint_memory(fx[i].p, fx[i].n, &fp), FA_OK);
        CHECK(fp.key == loose.key);
        CHECK_EQ(fa_tags_read_archive_entry("archive_test.bin", NULL, 0, &t), FA_OK);
        CHECK_EQ(fa_tags_get_info(t, &info), FA_OK);
        CHECK_EQ(info.has_cdg, 1);
        CHECK(fabs(((double)info.duration / 1e9) - 0.25) < 0.01);
        fa_tags_close(t);
        remove("archive_test.bin");
    }
}

static void test_single_file_compressed(void)
{
    static const fixture fx[3] = {
        { "gz", fx_one_gz, sizeof(fx_one_gz), FA_ARCHIVE_GZIP, FA_ARCHIVE_FILTER_GZIP, FA_METHOD_DEFLATE },
        { "bz2", fx_one_bz2, sizeof(fx_one_bz2), FA_ARCHIVE_BZIP2, FA_ARCHIVE_FILTER_BZIP2, FA_METHOD_BZIP2 },
        { "xz", fx_one_xz, sizeof(fx_one_xz), FA_ARCHIVE_XZ, FA_ARCHIVE_FILTER_XZ, FA_METHOD_LZMA2 },
    };
    fa_fingerprint loose = { .struct_size = sizeof(loose) };
    uint32_t i;
    CHECK_EQ(fa_fingerprint_memory(fx_small_tar + 512, FX_ONE_SIZE, &loose), FA_OK);
    for (i = 0; i < 3; i++) {
        fa_archive* a = NULL;
        fa_archive_entry e = { .struct_size = sizeof(e) };
        fa_fingerprint fp = { .struct_size = sizeof(fp) };
        fa_stream* s = NULL;
        fa_open_options oo;
        void* d = NULL;
        size_t n = 0;
        int32_t f = -1, fl = -1;
        buf file;
        printf("  %s\n", fx[i].what);
        file.p = (uint8_t*)fx[i].p; file.n = fx[i].n; file.cap = fx[i].n;
        write_file("song.wav.tmp", &file);
        CHECK_EQ(fa_archive_open_path("song.wav.tmp", NULL, &a), FA_OK);
        CHECK_EQ(fa_archive_get_format(a, &f), FA_OK);
        CHECK_EQ(fa_archive_get_filter(a, &fl), FA_OK);
        CHECK_EQ(f, fx[i].format);
        CHECK_EQ(fl, fx[i].filter);
        CHECK_EQ(fa_archive_get_entry_count(a), 1);
        CHECK_EQ(fa_archive_get_entry(a, 0, &e), FA_OK);
        CHECK_EQ(e.size, FX_ONE_SIZE);
        CHECK_EQ(e.method, fx[i].method);
        CHECK_EQ(fa_archive_read(a, 0, &d, &n), FA_OK);
        CHECK_EQ(n, FX_ONE_SIZE);
        CHECK(fnv64((const uint8_t*)d, n) == FX_ONE_FNV);
        fa_free(d);
        fa_archive_close(a);
        /* It plays and fingerprints like the loose file. */
        fa_open_options_init(&oo);
        oo.flags = FA_OPEN_DECODE_ONLY;
        CHECK_EQ(fa_stream_open_memory(g_engine, fx[i].p, fx[i].n, &oo, &s), FA_OK);
        CHECK_EQ(decode_all(s), 2000);
        fa_stream_close(s);
        CHECK_EQ(fa_fingerprint_archive_entry("song.wav.tmp", NULL, &fp), FA_OK);
        CHECK(fp.key == loose.key);
        remove("song.wav.tmp");
    }
}

/* A stream larger than the memory limit is not held decoded: each entry is decoded again from the
 * block, checkpoint or stream start nearest to it. */
static void test_stream_mode(void)
{
    static const fixture fx[3] = {
        { "big tar.gz", fx_big_tar_gz, sizeof(fx_big_tar_gz), 0, FA_ARCHIVE_FILTER_GZIP, FA_METHOD_DEFLATE },
        { "big tar.bz2", fx_big_tar_bz2, sizeof(fx_big_tar_bz2), 0, FA_ARCHIVE_FILTER_BZIP2, FA_METHOD_BZIP2 },
        { "big tar.xz", fx_big_tar_xz, sizeof(fx_big_tar_xz), 0, FA_ARCHIVE_FILTER_XZ, FA_METHOD_LZMA2 },
    };
    static const uint32_t sizes[3] = { FX_BIG_SIZE_0, FX_BIG_SIZE_1, FX_BIG_SIZE_2 };
    static const uint64_t hashes[3] = { FX_BIG_FNV_0, FX_BIG_FNV_1, FX_BIG_FNV_2 };
    uint32_t i;
    int pass;
    for (i = 0; i < 3; i++) {
        buf file;
        printf("  %s\n", fx[i].what);
        file.p = (uint8_t*)fx[i].p; file.n = fx[i].n; file.cap = fx[i].n;
        write_file("archive_big.bin", &file);
        for (pass = 0; pass < 4; pass++) {
            fa_archive_options o;
            fa_archive* a = NULL;
            uint32_t k;
            fa_archive_options_init(&o);
            o.memory_limit_mb = 1;
            o.temp_directory = "archive-test-tmp";   /* an entry past the limit is unpacked to a file there */
            if (pass & 1) CHECK_EQ(fa_archive_open_path("archive_big.bin", &o, &a), FA_OK);
            else CHECK_EQ(fa_archive_open_memory(fx[i].p, fx[i].n, &o, &a), FA_OK);
            CHECK_EQ(fa_archive_get_entry_count(a), 3);
            for (k = 0; k < 3; k++) {
                uint32_t idx = pass >= 2 ? 2 - k : k;   /* forward, then backward: each is a fresh decode */
                fa_archive_entry e = { .struct_size = sizeof(e) };
                fa_archive_reader* r = NULL;
                uint8_t* all;
                int64_t got, total = 0;
                CHECK_EQ(fa_archive_get_entry(a, idx, &e), FA_OK);
                CHECK_EQ(e.size, sizes[idx]);
                CHECK(e.flags & FA_ENTRY_SOLID);
                if (idx == 0) {
                    void* d = NULL;
                    size_t n = 0;
                    CHECK_EQ(fa_archive_read(a, idx, &d, &n), FA_ERROR_TOO_LARGE);   /* more than the 1 MB limit: a reader will do */
                    CHECK(d == NULL);
                }
                CHECK_EQ(fa_archive_open_reader(a, idx, &r), FA_OK);
                CHECK_EQ(fa_archive_reader_get_size(r), sizes[idx]);
                all = (uint8_t*)malloc(sizes[idx]);
                CHECK(all != NULL);
                while (total < (int64_t)sizes[idx] && (got = fa_archive_reader_read(r, all + total, 65536)) > 0) total += got;
                CHECK_EQ(total, sizes[idx]);
                CHECK(fnv64(all, (size_t)total) == hashes[idx]);
                if (idx == 0) {   /* a seek back and into the middle: the data there is the same */
                    uint8_t again[1000];
                    CHECK_EQ(fa_archive_reader_seek(r, 5000000), FA_OK);
                    CHECK_EQ(fa_archive_reader_read(r, again, sizeof(again)), 1000);
                    CHECK(memcmp(again, all + 5000000, 1000) == 0);
                    CHECK_EQ(fa_archive_reader_seek(r, 100), FA_OK);
                    CHECK_EQ(fa_archive_reader_read(r, again, sizeof(again)), 1000);
                    CHECK(memcmp(again, all + 100, 1000) == 0);
                }
                free(all);
                fa_archive_reader_close(r);
            }
            fa_archive_close(a);
        }
        remove("archive_big.bin");
    }
}

/* Damaged streams end in an error, never in wrong bytes passed as right. */
static void test_damaged_streams(void)
{
    static const fixture fx[3] = {
        { "tar.gz", fx_small_tar_gz, sizeof(fx_small_tar_gz), 0, 0, 0 },
        { "tar.bz2", fx_small_tar_bz2, sizeof(fx_small_tar_bz2), 0, 0, 0 },
        { "tar.xz", fx_small_tar_xz, sizeof(fx_small_tar_xz), 0, 0, 0 },
    };
    uint32_t i;
    for (i = 0; i < 3; i++) {
        int cut;
        for (cut = 0; cut < 2; cut++) {
            uint8_t* copy = (uint8_t*)malloc(fx[i].n);
            fa_archive* a = NULL;
            fa_result r;
            uint32_t k;
            int bad = 0;
            CHECK(copy != NULL);
            memcpy(copy, fx[i].p, fx[i].n);
            if (!cut) copy[fx[i].n / 2] ^= 0x55;   /* a flipped byte, or (cut) the last third missing */
            r = fa_archive_open_memory(copy, cut ? fx[i].n - fx[i].n / 3 : fx[i].n, NULL, &a);
            if (r != FA_OK) bad = 1;
            else {
                for (k = 0; k < fa_archive_get_entry_count(a); k++) {
                    void* d = NULL;
                    size_t n = 0;
                    if (fa_archive_read(a, k, &d, &n) != FA_OK) bad = 1;
                    else {
                        const uint8_t* x = (const uint8_t*)d;
                        CHECK(fnv64(x, n) == k_small_fnv[k] && n == k_small_sizes[k]);   /* right, or an error */
                        fa_free(d);
                    }
                }
                fa_archive_close(a);
            }
            CHECK(bad);
            free(copy);
        }
    }
}

int main(void)
{
    fa_engine_config cfg;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine\n"); return 1; }
    RUN(test_crc32);
    RUN(test_deflate64_matches);
    RUN(test_detect);
    RUN(test_entries_and_names);
    RUN(test_pick_rules);
    RUN(test_read_and_reader);
    RUN(test_open_archive_streams);
    RUN(test_tags_and_fingerprint_of_entries);
    RUN(test_unplayable_entries);
    RUN(test_tar_and_compressed_tar);
    RUN(test_single_file_compressed);
    RUN(test_stream_mode);
    RUN(test_damaged_streams);
    fa_engine_destroy(g_engine);
    remove("archive-test-tmp/fatzaudio-tmp");
    remove("archive-test-tmp");
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all ok\n");
    return 0;
}
