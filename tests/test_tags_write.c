/* Tests of fa_tags_write_loudness (tags_write*.c): every writer on copies of the
 * test files (never anything else), checked by reading the tags back, by decoding
 * the whole file before and after (the audio must not change at all), and for Ogg
 * by walking every page (checksums and page numbers). */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef FA_TEST_DATA_DIR
#  define FA_TEST_DATA_DIR "data"
#endif

static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); g_failures++; return; } } while (0)
#define CHECK_EQ(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); if (va_ != vb_) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (%lld vs %lld)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_NEAR(a, b, tol) do { double va_ = (a), vb_ = (b); if (!(fabs(va_ - vb_) <= (tol))) { \
    fprintf(stderr, "  FAIL %s:%d: %s ~ %s (%.6f vs %.6f)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_STR(a, b) do { const char* va_ = (a); const char* vb_ = (b); if (strcmp(va_, vb_) != 0) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (\"%s\" vs \"%s\")\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define RUN(t) do { int before_ = g_failures; printf("%s\n", #t); t(); if (g_failures == before_) printf("  ok\n"); } while (0)

static fa_engine* g_engine;

/* ---- files ------------------------------------------------------------------------ */

static uint8_t* load(const char* path, size_t* n)
{
    FILE* f = fopen(path, "rb");
    uint8_t* p;
    long len;
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    len = ftell(f);
    fseek(f, 0, SEEK_SET);
    p = (uint8_t*)malloc((size_t)len + 1);
    if (p && fread(p, 1, (size_t)len, f) != (size_t)len) { free(p); p = NULL; }
    fclose(f);
    *n = (size_t)len;
    return p;
}

static int save(const char* path, const void* p, size_t n)
{
    FILE* f = fopen(path, "wb");
    int ok = f && fwrite(p, 1, n, f) == n;
    if (f) fclose(f);
    return ok;
}

/* A copy of a test file in the working directory (the build's tests folder). */
static int copy_data(const char* rel, const char* out)
{
    char path[512];
    size_t n;
    uint8_t* p;
    int ok;
    snprintf(path, sizeof(path), "%s/%s", FA_TEST_DATA_DIR, rel);
    p = load(path, &n);
    if (!p) return 0;
    ok = save(out, p, n);
    free(p);
    return ok;
}

/* Every sample of a file, decoded. */
static float* decode_all(const char* path, int64_t* out_frames, uint32_t* out_ch)
{
    fa_source src;
    fa_decoder dec;
    fa_decoder_env env;
    float* all = NULL;
    int64_t n = 0, cap = 0;
    memset(&env, 0, sizeof(env));
    *out_frames = 0;
    if (fa_source_open_path(&src, path) != FA_OK) return NULL;
    if (fa_decoder_open(&dec, &src, path, NULL, -1, &env) != FA_OK) { fa_source_close(&src); return NULL; }
    *out_ch = dec.format.channels;
    for (;;) {
        fa_frames got = 0;
        if (n + 4096 > cap) {
            cap = (n + 4096) * 2;
            all = (float*)realloc(all, (size_t)cap * dec.format.channels * sizeof(float));
        }
        fa_decoder_read(&dec, all + (size_t)n * dec.format.channels, 4096, &got);
        if (got <= 0) break;
        n += got;
    }
    fa_decoder_close(&dec);
    fa_source_close(&src);
    *out_frames = n;
    return all;
}

static int same_audio(const float* a, int64_t na, uint32_t ca, const char* path)
{
    int64_t nb;
    uint32_t cb = 0;
    float* b = decode_all(path, &nb, &cb);
    int same = b && na == nb && ca == cb && na > 0 && memcmp(a, b, (size_t)na * ca * sizeof(float)) == 0;
    free(b);
    return same;
}

/* The value of the first tag item named `key` (any case), and how many there are. */
static int item(const char* path, const char* key, char* out, size_t cap)
{
    fa_tags* t;
    fa_tag_item it;
    uint32_t i;
    int count = 0;
    out[0] = '\0';
    if (fa_tags_read_path(path, FA_TAGS_NO_INFO, &t) != FA_OK) return -1;
    for (i = 0; fa_tags_get_item(t, i, &it) == FA_OK; i++) {
        if (fa_tags_key_is(it.key, key)) {
            if (!count) snprintf(out, cap, "%s", it.value);
            count++;
        }
    }
    fa_tags_close(t);
    return count;
}

static void text(const char* path, fa_tag_field field, char* out, size_t cap)
{
    fa_tags* t;
    out[0] = '\0';
    if (fa_tags_read_path(path, FA_TAGS_NO_INFO, &t) != FA_OK) return;
    snprintf(out, cap, "%s", fa_tags_get(t, field) ? fa_tags_get(t, field) : "");
    fa_tags_close(t);
}

static fa_result write1(const char* path, fa_loudness_tag_result* res)
{
    memset(res, 0, sizeof(*res));
    res->struct_size = sizeof(*res);
    return fa_tags_write_loudness(g_engine, &path, 1, 0, res);
}

/* Writes the tags of a copy of `rel` and checks the ReplayGain values read back,
 * the audio, and that a second write replaces the first (one item each). */
static void check_rg_file(const char* rel, const char* out, int32_t format)
{
    fa_loudness_tag_result res;
    fa_loudness l;
    float* before;
    int64_t frames;
    uint32_t ch = 0;
    char v[64], want[64];
    CHECK(copy_data(rel, out));
    before = decode_all(out, &frames, &ch);
    CHECK(before != NULL);
    memset(&l, 0, sizeof(l));
    l.struct_size = sizeof(l);
    CHECK_EQ(fa_scan_loudness_path(g_engine, out, &l), FA_OK);
    CHECK_EQ(write1(out, &res), FA_OK);
    CHECK_EQ(res.result, FA_OK);
    CHECK_EQ(res.tag_format, format);
    CHECK_NEAR(res.integrated_lufs, l.integrated_lufs, 1e-9);
    CHECK_NEAR(res.track_gain_db, -18.0 - l.integrated_lufs, 1e-9);
    CHECK(isnan(res.album_gain_db));
    CHECK_EQ(item(out, "REPLAYGAIN_TRACK_GAIN", v, sizeof(v)), 1);
    snprintf(want, sizeof(want), "%+.2f dB", -18.0 - l.integrated_lufs);
    CHECK_STR(v, want);
    CHECK_EQ(item(out, "REPLAYGAIN_TRACK_PEAK", v, sizeof(v)), 1);
    CHECK_NEAR(atof(v), pow(10.0, l.true_peak_dbtp / 20.0), 1e-6);
    CHECK_EQ(item(out, "R128_TRACK_GAIN", v, sizeof(v)), 0);
    CHECK(same_audio(before, frames, ch, out));
    /* Again: replaced, not added. */
    CHECK_EQ(write1(out, &res), FA_OK);
    CHECK_EQ(item(out, "REPLAYGAIN_TRACK_GAIN", v, sizeof(v)), 1);
    CHECK_STR(v, want);
    CHECK(same_audio(before, frames, ch, out));
    {
        /* FatzAudio's own normalization reads them. */
        fa_stream* s;
        fa_loudness got;
        int i;
        memset(&got, 0, sizeof(got));
        got.struct_size = sizeof(got);
        CHECK_EQ(fa_stream_open_path(g_engine, out, NULL, &s), FA_OK);
        for (i = 0; i < 500 && !got.is_final; i++) {
            fa_stream_get_loudness(s, &got);
            if (!got.is_final) fa_sleep_ms(10);
        }
        fa_stream_close(s);
        CHECK_EQ(got.source, FA_LOUDNESS_TAGS);
        CHECK_NEAR(got.integrated_lufs, l.integrated_lufs, 0.006);
    }
    free(before);
}

static void test_mp3_new_tag(void) { check_rg_file("mp3/cbr.mp3", "tw_cbr.mp3", FA_TAG_FORMAT_ID3V2); }
static void test_mp3_id3v23(void)
{
    char v[256];
    check_rg_file("tags/id3v23.mp3", "tw_id3v23.mp3", FA_TAG_FORMAT_ID3V2);
    text("tw_id3v23.mp3", FA_TAG_TITLE, v, sizeof(v));
    CHECK_STR(v, "S\xc3\xb3ng T\xc3\xaftle");
    text("tw_id3v23.mp3", FA_TAG_ALBUM, v, sizeof(v));
    CHECK_STR(v, "Great Album");
}
static void test_mp3_id3v24(void)
{
    char v[256];
    check_rg_file("tags/id3v24.mp3", "tw_id3v24.mp3", FA_TAG_FORMAT_ID3V2);
    text("tw_id3v24.mp3", FA_TAG_ARTIST, v, sizeof(v));
    CHECK_STR(v, "The Artist");
}
static void test_flac(void)
{
    char v[256];
    check_rg_file("tags/tags.flac", "tw_tags.flac", FA_TAG_FORMAT_VORBIS);
    text("tw_tags.flac", FA_TAG_TITLE, v, sizeof(v));
    CHECK_STR(v, "Song Title");
}
static void test_flac_plain(void) { check_rg_file("flac/tone.flac", "tw_tone.flac", FA_TAG_FORMAT_VORBIS); }
static void test_vorbis(void) { check_rg_file("ogg/tone.ogg", "tw_tone.ogg", FA_TAG_FORMAT_VORBIS); }
/* 0.2 s: shorter than one 400 ms block, so no loudness: not written. */
static void test_too_short(void)
{
    fa_loudness_tag_result res;
    char v[64];
    CHECK(copy_data("ogg/rg.ogg", "tw_rg.ogg"));
    CHECK_EQ(write1("tw_rg.ogg", &res), FA_ERROR_NOT_FOUND);
    CHECK_EQ(item("tw_rg.ogg", "REPLAYGAIN_TRACK_GAIN", v, sizeof(v)), 1);
    CHECK_STR(v, "-6.50 dB");
}
static void test_m4a(void)
{
    char v[256];
    check_rg_file("tags/tags.m4a", "tw_tags.m4a", FA_TAG_FORMAT_MP4);
    text("tw_tags.m4a", FA_TAG_TITLE, v, sizeof(v));
    CHECK_STR(v, "Song Title");
}
/* moov before mdat: the chunk offsets move with it. */
static void test_m4a_faststart(void) { check_rg_file("tags/faststart.m4a", "tw_faststart.m4a", FA_TAG_FORMAT_MP4); }
static void test_m4a_no_ilst(void) { check_rg_file("aac.m4a", "tw_aac.m4a", FA_TAG_FORMAT_MP4); }

/* ---- Ogg pages --------------------------------------------------------------------- */

/* Every page valid, numbered without gaps per stream (chained streams restart). */
static int ogg_pages_ok(const char* path, int* out_pages)
{
    size_t n, pos = 0;
    uint8_t* d = load(path, &n);
    uint32_t serial = 0, next = 0;
    int pages = 0;
    if (!d) return 0;
    while (pos < n) {
        size_t blen = 0, size, i;
        uint8_t save[4];
        if (n - pos < 27 || memcmp(d + pos, "OggS", 4) != 0) { free(d); return 0; }
        for (i = 0; i < d[pos + 26]; i++) blen += d[pos + 27 + i];
        size = 27 + d[pos + 26] + blen;
        memcpy(save, d + pos + 22, 4);
        memset(d + pos + 22, 0, 4);
        if (fa_ogg_crc(d + pos, size) != fa_le32(save)) { free(d); return 0; }
        if (d[pos + 5] & 0x02) { serial = fa_le32(d + pos + 14); next = 0; }
        if (fa_le32(d + pos + 14) == serial) {
            if (fa_le32(d + pos + 18) != next) { free(d); return 0; }
            next++;
        }
        pos += size;
        pages++;
    }
    free(d);
    *out_pages = pages;
    return 1;
}

static void test_opus(void)
{
    fa_loudness_tag_result res;
    fa_loudness l;
    float* before;
    int64_t frames;
    uint32_t ch = 0;
    int pages;
    char v[64], want[64];
    CHECK(copy_data("tags/tags.opus", "tw_tags.opus"));
    before = decode_all("tw_tags.opus", &frames, &ch);
    memset(&l, 0, sizeof(l));
    l.struct_size = sizeof(l);
    CHECK_EQ(fa_scan_loudness_path(g_engine, "tw_tags.opus", &l), FA_OK);
    CHECK_EQ(write1("tw_tags.opus", &res), FA_OK);
    CHECK_EQ(res.tag_format, FA_TAG_FORMAT_VORBIS);
    CHECK_EQ(item("tw_tags.opus", "R128_TRACK_GAIN", v, sizeof(v)), 1);
    snprintf(want, sizeof(want), "%d", (int)floor((-23.0 - l.integrated_lufs) * 256.0 + 0.5));
    CHECK_STR(v, want);
    CHECK_NEAR(res.track_gain_db, atoi(want) / 256.0, 1e-9);
    CHECK_EQ(item("tw_tags.opus", "REPLAYGAIN_TRACK_GAIN", v, sizeof(v)), 0);
    CHECK(ogg_pages_ok("tw_tags.opus", &pages));
    CHECK(same_audio(before, frames, ch, "tw_tags.opus"));
    CHECK_EQ(write1("tw_tags.opus", &res), FA_OK);
    CHECK_EQ(item("tw_tags.opus", "R128_TRACK_GAIN", v, sizeof(v)), 1);
    CHECK_STR(v, want);
    CHECK(same_audio(before, frames, ch, "tw_tags.opus"));
    free(before);
}

static void test_ogg_chained(void)
{
    int pages_before, pages_after;
    fa_loudness_tag_result res;
    CHECK(copy_data("ogg/chained.ogg", "tw_chained.ogg"));
    CHECK(ogg_pages_ok("tw_chained.ogg", &pages_before));
    CHECK_EQ(write1("tw_chained.ogg", &res), FA_OK);
    CHECK(ogg_pages_ok("tw_chained.ogg", &pages_after));
    CHECK_EQ(pages_after, pages_before);
}

/* A Vorbis file whose comment header spans three pages because of a huge stale
 * gain tag: the new header needs fewer pages, so every later page is renumbered. */
static void ogg_page(uint8_t* out, size_t* pos, int flags, uint32_t serial, uint32_t seq, int64_t granule,
                     const uint8_t* lace, int nseg, const uint8_t* body, size_t blen)
{
    uint8_t* d = out + *pos;
    uint32_t crc;
    int i;
    memcpy(d, "OggS", 4);
    d[4] = 0;
    d[5] = (uint8_t)flags;
    for (i = 0; i < 8; i++) d[6 + i] = (uint8_t)((uint64_t)granule >> (8 * i));
    for (i = 0; i < 4; i++) d[14 + i] = (uint8_t)(serial >> (8 * i));
    for (i = 0; i < 4; i++) d[18 + i] = (uint8_t)(seq >> (8 * i));
    memset(d + 22, 0, 4);
    d[26] = (uint8_t)nseg;
    memcpy(d + 27, lace, (size_t)nseg);
    memcpy(d + 27 + nseg, body, blen);
    crc = fa_ogg_crc(d, 27 + (size_t)nseg + blen);
    for (i = 0; i < 4; i++) d[22 + i] = (uint8_t)(crc >> (8 * i));
    *pos += 27 + (size_t)nseg + blen;
}

static void test_ogg_renumber(void)
{
    size_t n, pos = 0, out_pos = 0, i;
    uint8_t* d;
    uint8_t* out;
    uint8_t* comment;
    size_t clen = 0, setup_at, setup_len;
    uint32_t serial;
    int pages_before, pages_after, k;
    float* before;
    int64_t frames;
    uint32_t ch = 0;
    fa_loudness_tag_result res;
    char v[64];
    static const char big_key[] = "REPLAYGAIN_TRACK_GAIN=+1.00 dB";
    size_t big = 140000;

    CHECK(copy_data("ogg/tone.ogg", "tw_big_src.ogg"));
    d = load("tw_big_src.ogg", &n);
    CHECK(d != NULL);
    /* tone.ogg: page 0 = identification, page 1 = comment + setup, then audio. */
    serial = fa_le32(d + 14);
    pos = 27 + d[26] + d[27];
    {
        size_t p1 = pos, body = p1 + 27 + d[p1 + 26], at = body;
        size_t first_len = 0;
        for (i = 0; i < d[p1 + 26]; i++) { first_len += d[p1 + 27 + i]; if (d[p1 + 27 + i] < 255) { i++; break; } }
        setup_at = body + first_len;
        setup_len = 0;
        for (; i < d[p1 + 26]; i++) setup_len += d[p1 + 27 + i];
        (void)at;
        /* The comment: vendor "x", one item: the stale gain padded with spaces. */
        comment = (uint8_t*)malloc(big + 64);
        memcpy(comment, "\x03vorbis", 7);
        clen = 7;
        comment[clen++] = 1; comment[clen++] = 0; comment[clen++] = 0; comment[clen++] = 0; comment[clen++] = 'x';
        comment[clen++] = 1; comment[clen++] = 0; comment[clen++] = 0; comment[clen++] = 0;
        {
            uint32_t ilen = (uint32_t)big;
            for (k = 0; k < 4; k++) comment[clen++] = (uint8_t)(ilen >> (8 * k));
            memcpy(comment + clen, big_key, sizeof(big_key) - 1);
            memset(comment + clen + sizeof(big_key) - 1, ' ', big - (sizeof(big_key) - 1));
            clen += big;
        }
        comment[clen++] = 1;
        out = (uint8_t*)malloc(n + big + 4096);
        memcpy(out, d, pos);                        /* page 0 */
        out_pos = pos;
        {
            /* comment over pages of 255 segments, then the setup on its own page */
            size_t off = 0;
            uint32_t seq = 1;
            int cont = 0;
            while (off <= clen) {
                uint8_t lace[255];
                int nseg = 0;
                size_t start = off;
                int ended = 0;
                while (nseg < 255) {
                    size_t left = clen - off, l = left >= 255 ? 255 : left;
                    lace[nseg++] = (uint8_t)l;
                    off += l;
                    if (l < 255) { ended = 1; off = clen + 1; break; }
                }
                ogg_page(out, &out_pos, cont ? 1 : 0, serial, seq++, ended ? 0 : -1, lace, nseg, comment + start,
                         (ended ? clen : off) - start);
                cont = !ended;
            }
            {
                uint8_t lace[255];
                int nseg = 0;
                size_t left = setup_len;
                while (left >= 255) { lace[nseg++] = 255; left -= 255; }
                lace[nseg++] = (uint8_t)left;
                ogg_page(out, &out_pos, 0, serial, seq, 0, lace, nseg, d + setup_at, setup_len);
                /* The audio pages, renumbered after ours. */
                pos = p1 + 27 + d[p1 + 26];
                for (i = 0; i < d[p1 + 26]; i++) pos += d[p1 + 27 + i];
                while (pos < n) {
                    size_t blen = 0, j;
                    for (j = 0; j < d[pos + 26]; j++) blen += d[pos + 27 + j];
                    ogg_page(out, &out_pos, d[pos + 5], serial, ++seq, (int64_t)(fa_le32(d + pos + 6) | ((uint64_t)fa_le32(d + pos + 10) << 32)),
                             d + pos + 27, d[pos + 26], d + pos + 27 + d[pos + 26], blen);
                    pos += 27 + d[pos + 26] + blen;
                }
            }
        }
    }
    CHECK(save("tw_big.ogg", out, out_pos));
    free(out);
    free(comment);
    free(d);
    CHECK(ogg_pages_ok("tw_big.ogg", &pages_before));
    before = decode_all("tw_big.ogg", &frames, &ch);
    CHECK(before != NULL);
    CHECK_EQ(write1("tw_big.ogg", &res), FA_OK);
    CHECK(ogg_pages_ok("tw_big.ogg", &pages_after));
    CHECK(pages_after < pages_before);
    CHECK_EQ(item("tw_big.ogg", "REPLAYGAIN_TRACK_GAIN", v, sizeof(v)), 1);
    CHECK(strlen(v) < 16);
    CHECK(same_audio(before, frames, ch, "tw_big.ogg"));
    free(before);
}

/* ---- other cases ------------------------------------------------------------------- */

/* An MP3 with an APEv2 tag (and ID3v1 after it): both tags get the new values. */
static void test_mp3_ape(void)
{
    size_t n;
    uint8_t* d;
    uint8_t ape[256];
    size_t a = 0;
    char v[64], want[64];
    fa_loudness_tag_result res;
    static const char key[] = "REPLAYGAIN_TRACK_GAIN", val[] = "+9.99 dB", title_key[] = "Title", title[] = "Ape Title";
    uint8_t v1[128];
    FILE* f;
    CHECK(copy_data("mp3/cbr.mp3", "tw_ape.mp3"));
    d = load("tw_ape.mp3", &n);
    CHECK(d != NULL);
#define LE32(x) do { uint32_t x_ = (uint32_t)(x); ape[a++] = (uint8_t)x_; ape[a++] = (uint8_t)(x_ >> 8); ape[a++] = (uint8_t)(x_ >> 16); ape[a++] = (uint8_t)(x_ >> 24); } while (0)
    {
        size_t items = (8 + sizeof(key) + sizeof(val) - 1) + (8 + sizeof(title_key) + sizeof(title) - 1);
        LE32(sizeof(val) - 1); LE32(0); memcpy(ape + a, key, sizeof(key)); a += sizeof(key); memcpy(ape + a, val, sizeof(val) - 1); a += sizeof(val) - 1;
        LE32(sizeof(title) - 1); LE32(0); memcpy(ape + a, title_key, sizeof(title_key)); a += sizeof(title_key); memcpy(ape + a, title, sizeof(title) - 1); a += sizeof(title) - 1;
        memcpy(ape + a, "APETAGEX", 8); a += 8;
        LE32(2000); LE32(items + 32); LE32(2); LE32(0); memset(ape + a, 0, 8); a += 8;
    }
#undef LE32
    memset(v1, 0, sizeof(v1));
    memcpy(v1, "TAGV1 title", 11);
    f = fopen("tw_ape.mp3", "ab");
    CHECK(f != NULL);
    fwrite(ape, 1, a, f);
    fwrite(v1, 1, sizeof(v1), f);
    fclose(f);
    free(d);
    CHECK_EQ(write1("tw_ape.mp3", &res), FA_OK);
    snprintf(want, sizeof(want), "%+.2f dB", res.track_gain_db);
    {
        fa_tags* t;
        fa_tag_item it;
        uint32_t i;
        int id3 = 0, apen = 0, title_ok = 0;
        CHECK_EQ(fa_tags_read_path("tw_ape.mp3", FA_TAGS_NO_INFO, &t), FA_OK);
        for (i = 0; fa_tags_get_item(t, i, &it) == FA_OK; i++) {
            if (fa_tags_key_is(it.key, key)) {
                if (strcmp(it.value, want) != 0) { fprintf(stderr, "  value %s\n", it.value); g_failures++; }
                if (it.format == FA_TAG_FORMAT_ID3V2) id3++;
                if (it.format == FA_TAG_FORMAT_APE) apen++;
            }
            if (it.format == FA_TAG_FORMAT_APE && strcmp(it.key, "Title") == 0 && strcmp(it.value, title) == 0) title_ok = 1;
        }
        fa_tags_close(t);
        CHECK_EQ(id3, 1);
        CHECK_EQ(apen, 1);
        CHECK(title_ok);
    }
    d = load("tw_ape.mp3", &n);
    CHECK(d != NULL);
    CHECK(memcmp(d + n - 128, "TAGV1 title", 11) == 0);
    free(d);
    (void)v;
}

/* Formats it does not write are left exactly as they were. */
static void test_unsupported(void)
{
    fa_loudness_tag_result res;
    size_t n1, n2;
    uint8_t* a;
    uint8_t* b;
    CHECK(copy_data("tags/tags.aiff", "tw_tags.aiff"));
    a = load("tw_tags.aiff", &n1);
    CHECK_EQ(write1("tw_tags.aiff", &res), FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(res.result, FA_ERROR_UNSUPPORTED_FORMAT);
    CHECK_EQ(res.tag_format, 0);
    b = load("tw_tags.aiff", &n2);
    CHECK(a && b && n1 == n2 && memcmp(a, b, n1) == 0);
    free(a);
    free(b);
}

static void test_album(void)
{
    const char* paths[2] = { "tw_alb1.flac", "tw_alb2.mp3" };
    fa_loudness_tag_result res[2];
    char v[64], want[64];
    int i;
    CHECK(copy_data("flac/tone.flac", paths[0]));
    CHECK(copy_data("mp3/cbr.mp3", paths[1]));
    memset(res, 0, sizeof(res));
    res[0].struct_size = res[1].struct_size = sizeof(res[0]);
    CHECK_EQ(fa_tags_write_loudness(g_engine, paths, 2, FA_TAGS_WRITE_ALBUM, res), FA_OK);
    CHECK_NEAR(res[0].album_lufs, res[1].album_lufs, 0);
    CHECK(res[0].album_lufs >= fmin(res[0].integrated_lufs, res[1].integrated_lufs) - 0.5);
    CHECK(res[0].album_lufs <= fmax(res[0].integrated_lufs, res[1].integrated_lufs) + 0.5);
    CHECK_NEAR(res[0].album_peak_dbtp, fmax(res[0].true_peak_dbtp, res[1].true_peak_dbtp), 0);
    for (i = 0; i < 2; i++) {
        CHECK_EQ(res[i].result, FA_OK);
        CHECK_EQ(item(paths[i], "REPLAYGAIN_ALBUM_GAIN", v, sizeof(v)), 1);
        snprintf(want, sizeof(want), "%+.2f dB", -18.0 - res[i].album_lufs);
        CHECK_STR(v, want);
        CHECK_EQ(item(paths[i], "REPLAYGAIN_ALBUM_PEAK", v, sizeof(v)), 1);
    }
    /* Track-only afterwards keeps the album values. */
    CHECK_EQ(write1(paths[0], &res[0]), FA_OK);
    CHECK_EQ(item(paths[0], "REPLAYGAIN_ALBUM_GAIN", v, sizeof(v)), 1);
}

/* One file of an album missing: nothing is written. */
static void test_album_incomplete(void)
{
    const char* paths[2] = { "tw_inc1.flac", "tw_missing_file.flac" };
    fa_loudness_tag_result res[2];
    size_t n1, n2;
    uint8_t* a;
    uint8_t* b;
    CHECK(copy_data("flac/tone.flac", paths[0]));
    a = load(paths[0], &n1);
    memset(res, 0, sizeof(res));
    res[0].struct_size = res[1].struct_size = sizeof(res[0]);
    CHECK(fa_tags_write_loudness(g_engine, paths, 2, FA_TAGS_WRITE_ALBUM, res) != FA_OK);
    CHECK_EQ(res[0].result, FA_ERROR_CANCELLED);
    CHECK_EQ(res[1].result, FA_ERROR_NOT_FOUND);
    b = load(paths[0], &n2);
    CHECK(a && b && n1 == n2 && memcmp(a, b, n1) == 0);
    free(a);
    free(b);
}

static void test_args(void)
{
    const char* p = "tw_cbr.mp3";
    const char* none = NULL;
    fa_loudness_tag_result res;
    memset(&res, 0, sizeof(res));
    CHECK_EQ(fa_tags_write_loudness(NULL, &p, 1, 0, NULL), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_tags_write_loudness(g_engine, &p, 0, 0, NULL), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_tags_write_loudness(g_engine, &none, 1, 0, NULL), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_tags_write_loudness(g_engine, &p, 1, 0x100, NULL), FA_ERROR_INVALID_ARG);
    CHECK_EQ(fa_tags_write_loudness(g_engine, &p, 1, 0, &res), FA_ERROR_INVALID_ARG);   /* struct_size 0 */
}

/* The loudness cache is neither read nor written. */
static void test_no_cache(void)
{
    fa_engine_config cfg;
    fa_engine* e;
    fa_loudness_tag_result res;
    const char* p = "tw_cache.flac";
    FILE* f;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.cache_directory = "tw_cache_dir";
    fa_mkdir_utf8("tw_cache_dir");
    remove("tw_cache_dir/loudness.fac");
    CHECK(copy_data("flac/tone.flac", p));
    CHECK_EQ(fa_engine_create(&cfg, &e), FA_OK);
    memset(&res, 0, sizeof(res));
    res.struct_size = sizeof(res);
    CHECK_EQ(fa_tags_write_loudness(e, &p, 1, 0, &res), FA_OK);
    fa_engine_destroy(e);
    f = fopen("tw_cache_dir/loudness.fac", "rb");
    if (f) fclose(f);
    CHECK(f == NULL);
}

int main(void)
{
    fa_engine_config cfg;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT | FA_ENGINE_NO_CACHE;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }

    RUN(test_mp3_new_tag);
    RUN(test_mp3_id3v23);
    RUN(test_mp3_id3v24);
    RUN(test_mp3_ape);
    RUN(test_flac);
    RUN(test_flac_plain);
    RUN(test_vorbis);
    RUN(test_too_short);
    RUN(test_opus);
    RUN(test_ogg_chained);
    RUN(test_ogg_renumber);
    RUN(test_m4a);
    RUN(test_m4a_faststart);
    RUN(test_m4a_no_ilst);
    RUN(test_unsupported);
    RUN(test_album);
    RUN(test_album_incomplete);
    RUN(test_args);
    RUN(test_no_cache);

    fa_engine_destroy(g_engine);
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all tag writing tests passed\n");
    return 0;
}
