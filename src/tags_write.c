/* fa_tags_write_loudness: measure files and write ReplayGain 2 / R128 tags.
 *
 * Only runs when the app asks. Each file is measured without the loudness cache
 * (fa_scan_measure_path), then rewritten into a new file next to it: the format
 * writer describes the new tag bytes (tags_write.h), the rest of the original is
 * copied around them. The new file is flushed to the disk, checked (its loudness
 * tags read back as written, and its first and middle audio decode to the same
 * samples as the original's), and only then renamed over the original. The file
 * must not change meanwhile (its identity and size are compared before writing
 * and before the rename). */
#include "fa_internal.h"
#include "tags_write.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define TW_MAX_FILES 10000
#define TW_COPY_BLOCK (1u << 20)
#define TW_CHECK_FRAMES 8192

/* ---- shared helpers ------------------------------------------------------------------ */

static const char* const track_keys[] = { "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "R128_TRACK_GAIN" };
static const char* const album_keys[] = { "REPLAYGAIN_ALBUM_GAIN", "REPLAYGAIN_ALBUM_PEAK", "R128_ALBUM_GAIN" };

static int key_eq(const char* key, size_t n, const char* name)
{
    size_t i;
    if (strlen(name) != n) return 0;
    for (i = 0; i < n; i++) {
        char c = key[i];
        if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
        if (c != name[i]) return 0;
    }
    return 1;
}

int fa_tw_replaces(const fa_tw_set* set, const char* key, size_t n)
{
    size_t i;
    for (i = 0; i < 3; i++) {
        if (key_eq(key, n, track_keys[i])) return 1;
        if (set->album && key_eq(key, n, album_keys[i])) return 1;
    }
    return 0;
}

void fa_tw_put(fa_tw_buf* b, const void* p, size_t n)
{
    if (b->failed || n == 0) return;
    if (b->n + n > b->cap) {
        size_t cap = b->cap ? b->cap : 256;
        uint8_t* q;
        while (cap < b->n + n) cap *= 2;
        q = (uint8_t*)realloc(b->p, cap);
        if (!q) { b->failed = 1; return; }
        b->p = q;
        b->cap = cap;
    }
    memcpy(b->p + b->n, p, n);
    b->n += n;
}

void fa_tw_u8(fa_tw_buf* b, uint32_t v) { uint8_t c = (uint8_t)v; fa_tw_put(b, &c, 1); }
void fa_tw_be32(fa_tw_buf* b, uint32_t v)
{
    uint8_t q[4] = { (uint8_t)(v >> 24), (uint8_t)(v >> 16), (uint8_t)(v >> 8), (uint8_t)v };
    fa_tw_put(b, q, 4);
}
void fa_tw_le32(fa_tw_buf* b, uint32_t v)
{
    uint8_t q[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) };
    fa_tw_put(b, q, 4);
}
void fa_tw_zeros(fa_tw_buf* b, size_t n)
{
    static const uint8_t zero[4096] = { 0 };
    while (n > 0 && !b->failed) {
        size_t k = n < sizeof(zero) ? n : sizeof(zero);
        fa_tw_put(b, zero, k);
        n -= k;
    }
}
void fa_tw_free(fa_tw_buf* b) { free(b->p); memset(b, 0, sizeof(*b)); }

uint8_t* fa_tw_read(fa_source* src, int64_t pos, size_t n)
{
    uint8_t* p;
    if (pos < 0 || n > (size_t)INT32_MAX || pos + (int64_t)n > src->length) return NULL;
    p = (uint8_t*)malloc(n ? n : 1);
    if (!p) return NULL;
    if (fa_source_seek(src, pos) != FA_OK || fa_source_read(src, p, n) != (int64_t)n) { free(p); return NULL; }
    return p;
}

fa_tw_edit* fa_tw_add(fa_tw_plan* plan, int64_t from, int64_t to)
{
    fa_tw_edit* e;
    if (plan->count >= FA_TW_MAX_EDITS) return NULL;
    e = &plan->edits[plan->count++];
    memset(e, 0, sizeof(*e));
    e->from = from;
    e->to = to;
    return e;
}

void fa_tw_plan_free(fa_tw_plan* plan)
{
    int i;
    for (i = 0; i < plan->count; i++) fa_tw_free(&plan->edits[i].data);
    plan->count = 0;
}

fa_result fa_tw_vorbis_comments(const uint8_t* old, size_t n, const fa_tw_set* set, fa_tw_buf* out, size_t* tail)
{
    static const char vendor[] = "FatzAudio";
    size_t pos, vlen, i;
    uint32_t count = 0, kept = 0, k;
    int pass;
    if (!old) {
        fa_tw_le32(out, (uint32_t)(sizeof(vendor) - 1));
        fa_tw_put(out, vendor, sizeof(vendor) - 1);
        fa_tw_le32(out, (uint32_t)set->count);
        pos = 0;
    } else {
        if (n < 8) return FA_ERROR_UNSUPPORTED_FORMAT;
        vlen = fa_le32(old);
        if (vlen > n - 8) return FA_ERROR_UNSUPPORTED_FORMAT;
        count = fa_le32(old + 4 + vlen);
        /* Pass 0 checks the block and counts what stays; pass 1 writes. */
        for (pass = 0; pass < 2; pass++) {
            pos = 8 + vlen;
            if (pass == 1) {
                fa_tw_put(out, old, 4 + vlen);
                fa_tw_le32(out, kept + (uint32_t)set->count);
            }
            for (k = 0; k < count; k++) {
                uint32_t len;
                size_t eq;
                if (n - pos < 4) return FA_ERROR_UNSUPPORTED_FORMAT;
                len = fa_le32(old + pos);
                if (len > n - pos - 4) return FA_ERROR_UNSUPPORTED_FORMAT;
                for (eq = 0; eq < len && old[pos + 4 + eq] != '='; eq++) {}
                if (!fa_tw_replaces(set, (const char*)old + pos + 4, eq)) {
                    if (pass == 0) kept++;
                    else fa_tw_put(out, old + pos, 4 + (size_t)len);
                }
                pos += 4 + (size_t)len;
            }
        }
    }
    for (i = 0; i < (size_t)set->count; i++) {
        size_t kl = strlen(set->items[i].key), vl = strlen(set->items[i].value);
        fa_tw_le32(out, (uint32_t)(kl + 1 + vl));
        fa_tw_put(out, set->items[i].key, kl);
        fa_tw_put(out, "=", 1);
        fa_tw_put(out, set->items[i].value, vl);
    }
    if (tail) *tail = pos;
    return out->failed ? FA_ERROR_OUT_OF_MEMORY : FA_OK;
}

/* ---- values ---------------------------------------------------------------------------- */

/* `v` with `decimals` places, independent of the C locale: "-7.89" (+ `suffix`). */
static void fixed(char* out, size_t cap, double v, int decimals, int plus, const char* suffix)
{
    long long scale = 1, q, ip;
    int i;
    for (i = 0; i < decimals; i++) scale *= 10;
    q = (long long)llround(fabs(v) * (double)scale);
    ip = q / scale;
    snprintf(out, cap, "%s%lld.%0*lld%s", v < 0 && q ? "-" : plus ? "+" : "", ip, decimals, q % scale, suffix);
}

static void add_item(fa_tw_set* set, const char* key, const char* value)
{
    fa_tw_item* it = &set->items[set->count++];
    it->key = key;
    snprintf(it->value, sizeof(it->value), "%s", value);
}

/* ReplayGain 2: gain to -18 LUFS, peak as a linear value. */
static void rg_set(fa_tw_set* set, double lufs, double peak_db, int album, double album_lufs, double album_peak_db)
{
    char v[40];
    memset(set, 0, sizeof(*set));
    set->album = album;
    fixed(v, sizeof(v), -18.0 - lufs, 2, 1, " dB");
    add_item(set, "REPLAYGAIN_TRACK_GAIN", v);
    fixed(v, sizeof(v), pow(10.0, peak_db / 20.0), 6, 0, "");
    add_item(set, "REPLAYGAIN_TRACK_PEAK", v);
    if (album) {
        fixed(v, sizeof(v), -18.0 - album_lufs, 2, 1, " dB");
        add_item(set, "REPLAYGAIN_ALBUM_GAIN", v);
        fixed(v, sizeof(v), pow(10.0, album_peak_db / 20.0), 6, 0, "");
        add_item(set, "REPLAYGAIN_ALBUM_PEAK", v);
    }
}

/* R128 (Opus, RFC 7845): Q7.8 dB to -23 LUFS, on top of the header's output gain
 * (which the decoder applied while measuring). */
static int r128_q78(double lufs)
{
    double q = floor((-23.0 - lufs) * 256.0 + 0.5);
    return q > 32767.0 ? 32767 : q < -32768.0 ? -32768 : (int)q;
}

static void r128_set(fa_tw_set* set, double lufs, int album, double album_lufs)
{
    char v[40];
    memset(set, 0, sizeof(*set));
    set->album = album;
    snprintf(v, sizeof(v), "%d", r128_q78(lufs));
    add_item(set, "R128_TRACK_GAIN", v);
    if (album) {
        snprintf(v, sizeof(v), "%d", r128_q78(album_lufs));
        add_item(set, "R128_ALBUM_GAIN", v);
    }
}

/* ---- format ------------------------------------------------------------------------------ */

enum { KIND_NONE, KIND_ID3, KIND_FLAC, KIND_APE, KIND_MP4, KIND_VORBIS, KIND_OPUS };

static int is_mp4(const uint8_t* h, size_t n)
{
    static const char* const types[] = { "ftyp", "moov", "mdat", "free", "skip", "wide" };
    size_t i;
    if (n < 8) return 0;
    for (i = 0; i < sizeof(types) / sizeof(types[0]); i++) if (memcmp(h + 4, types[i], 4) == 0) return 1;
    return 0;
}

static int detect(fa_source* src)
{
    uint8_t h[16];
    int64_t at = 0;
    int id3 = 0;
    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, h, sizeof(h)) != (int64_t)sizeof(h)) return KIND_NONE;
    if (is_mp4(h, sizeof(h))) return KIND_MP4;
    if (memcmp(h, "OggS", 4) == 0) {
        int k = fa_tw_ogg_kind(src);
        return k == 1 ? KIND_OPUS : k == 0 ? KIND_VORBIS : KIND_NONE;
    }
    if (memcmp(h, "ID3", 3) == 0 && h[3] >= 2 && h[3] <= 4 && !((h[6] | h[7] | h[8] | h[9]) & 0x80)) {
        id3 = 1;
        at = 10 + (((int64_t)h[6] << 21) | ((int64_t)h[7] << 14) | ((int64_t)h[8] << 7) | h[9]) + ((h[5] & 0x10) ? 10 : 0);
        if (fa_source_seek(src, at) != FA_OK || fa_source_read(src, h, 4) != 4) return KIND_NONE;
    }
    if (memcmp(h, "fLaC", 4) == 0) return KIND_FLAC;
    if (memcmp(h, "MAC ", 4) == 0 || memcmp(h, "wvpk", 4) == 0 || memcmp(h, "MPCK", 4) == 0 ||
        memcmp(h, "MP+", 3) == 0 || memcmp(h, "TTA1", 4) == 0)
        return KIND_APE;
    if (id3) return KIND_ID3;                              /* whatever follows has lived with an ID3v2 tag */
    if (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0) return KIND_ID3;   /* MPEG audio or ADTS AAC */
    return KIND_NONE;
}

/* ---- the new file ------------------------------------------------------------------------- */

static fa_result copy_range(fa_source* src, int64_t from, int64_t to, FILE* out, uint8_t* buf)
{
    if (fa_source_seek(src, from) != FA_OK) return FA_ERROR_IO;
    while (from < to) {
        size_t want = to - from < (int64_t)TW_COPY_BLOCK ? (size_t)(to - from) : TW_COPY_BLOCK;
        if (fa_source_read(src, buf, want) != (int64_t)want) return FA_ERROR_IO;
        if (fwrite(buf, 1, want, out) != want) return FA_ERROR_IO;
        from += (int64_t)want;
    }
    return FA_OK;
}

static fa_result write_plan(fa_source* src, const fa_tw_plan* plan, FILE* out)
{
    uint8_t* buf = (uint8_t*)malloc(TW_COPY_BLOCK);
    int64_t at = 0;
    fa_result r = FA_OK;
    int i;
    if (!buf) return FA_ERROR_OUT_OF_MEMORY;
    for (i = 0; i < plan->count && r == FA_OK; i++) {
        const fa_tw_edit* e = &plan->edits[i];
        if (e->from < at || e->to < e->from || e->to > src->length) { r = FA_ERROR_GENERIC; break; }
        r = copy_range(src, at, e->from, out, buf);
        if (r == FA_OK && e->data.n && fwrite(e->data.p, 1, e->data.n, out) != e->data.n) r = FA_ERROR_IO;
        at = e->to;
    }
    if (r == FA_OK) r = copy_range(src, at, src->length, out, buf);
    free(buf);
    return r;
}

/* The file's identity (fa_file_identity) and size, to see it did not change. */
typedef struct file_stamp {
    uint64_t id[4];
    int have_id;
    int64_t size;
} file_stamp;

static fa_result stamp(const char* path, file_stamp* s)
{
    fa_source src;
    fa_result r = fa_source_open_path(&src, path);
    if (r != FA_OK) return r;
    memset(s, 0, sizeof(*s));
    s->have_id = src.file ? fa_file_identity(src.file, s->id) : 0;
    s->size = src.length;
    fa_source_close(&src);
    return FA_OK;
}

static int same_stamp(const file_stamp* a, const file_stamp* b)
{
    return a->size == b->size && a->have_id == b->have_id && (!a->have_id || memcmp(a->id, b->id, sizeof(a->id)) == 0);
}

/* The first and middle audio of a file: the same samples before and after? */
static fa_result read_check_audio(const char* path, const char* name, float* buf, fa_frames* got, uint32_t* channels)
{
    fa_source src;
    fa_decoder dec;
    fa_decoder_env env;
    fa_result r;
    fa_frames half = 0, n;
    memset(&env, 0, sizeof(env));
    r = fa_source_open_path(&src, path);
    if (r != FA_OK) return r;
    r = fa_decoder_open(&dec, &src, name, NULL, -1, &env);
    if (r != FA_OK) { fa_source_close(&src); return r; }
    *channels = dec.format.channels;
    if (*channels == 0 || *channels > 64) r = FA_ERROR_UNSUPPORTED_FORMAT;
    got[0] = got[1] = 0;
    if (r == FA_OK) {
        r = fa_decoder_read(&dec, buf, TW_CHECK_FRAMES / 8, &got[0]);
        if (r == FA_ERROR_END_OF_STREAM) r = FA_OK;
    }
    if (r == FA_OK && dec.seekable && dec.length > TW_CHECK_FRAMES) half = dec.length / 2;
    if (r == FA_OK && half > 0 && fa_decoder_seek(&dec, half) == FA_OK) {
        n = 0;
        r = fa_decoder_read(&dec, buf + (size_t)(TW_CHECK_FRAMES / 8) * *channels, TW_CHECK_FRAMES / 8, &n);
        if (r == FA_ERROR_END_OF_STREAM) r = FA_OK;
        got[1] = n;
    }
    fa_decoder_close(&dec);
    fa_source_close(&src);
    return r;
}

static fa_result check_new_file(const char* path, const char* tmp, const fa_tw_set* set, int opus, double lufs,
                                double album_lufs)
{
    fa_source src;
    fa_tag_loudness t;
    fa_result r;
    float* a;
    float* b;
    fa_frames ga[2], gb[2];
    uint32_t ca = 0, cb = 0;
    double tol = opus ? 1.0 / 512.0 + 1e-9 : 0.005 + 1e-9;
    r = fa_source_open_path(&src, tmp);
    if (r != FA_OK) return r;
    fa_tags_read_loudness(&src, &t);
    fa_source_close(&src);
    if (!(fabs(t.track_lufs - lufs) <= tol)) return FA_ERROR_GENERIC;
    if (set->album && !(fabs(t.album_lufs - album_lufs) <= tol)) return FA_ERROR_GENERIC;

    a = (float*)malloc((size_t)TW_CHECK_FRAMES / 4 * 64 * sizeof(float));
    b = (float*)malloc((size_t)TW_CHECK_FRAMES / 4 * 64 * sizeof(float));
    r = a && b ? FA_OK : FA_ERROR_OUT_OF_MEMORY;
    if (r == FA_OK) r = read_check_audio(path, path, a, ga, &ca);
    if (r == FA_OK) r = read_check_audio(tmp, path, b, gb, &cb);
    if (r == FA_OK && (ca != cb || ga[0] != gb[0] || ga[1] != gb[1] || ga[0] == 0 ||
                       memcmp(a, b, (size_t)ga[0] * ca * sizeof(float)) != 0 ||
                       memcmp(a + (size_t)(TW_CHECK_FRAMES / 8) * ca, b + (size_t)(TW_CHECK_FRAMES / 8) * cb,
                              (size_t)ga[1] * ca * sizeof(float)) != 0))
        r = FA_ERROR_GENERIC;
    free(a);
    free(b);
    return r;
}

/* Writes the new tags of one measured file. */
static fa_result write_one(const char* path, const file_stamp* before, double lufs, double peak_db, int album,
                           double album_lufs, double album_peak_db, int32_t* out_format, double* out_gain,
                           double* out_album_gain)
{
    char real[4096], tmp[4200];
    fa_source src;
    fa_tw_set set;
    fa_tw_plan plan;
    file_stamp now;
    FILE* out = NULL;
    fa_result r;
    int kind, i, opus;

    if (!fa_real_path(path, real, sizeof(real))) return FA_ERROR_IO;
    r = fa_source_open_path(&src, real);
    if (r != FA_OK) return r;
    memset(&plan, 0, sizeof(plan));
    kind = detect(&src);
    opus = kind == KIND_OPUS;
    if (opus) {
        r128_set(&set, lufs, album, album_lufs);
        *out_gain = r128_q78(lufs) / 256.0;
        *out_album_gain = album ? r128_q78(album_lufs) / 256.0 : NAN;
    } else {
        rg_set(&set, lufs, peak_db, album, album_lufs, album_peak_db);
        *out_gain = -18.0 - lufs;
        *out_album_gain = album ? -18.0 - album_lufs : NAN;
    }
    switch (kind) {
    case KIND_ID3: r = fa_tw_plan_id3v2(&src, &set, &plan); break;
    case KIND_FLAC: r = fa_tw_plan_flac(&src, &set, &plan); break;
    case KIND_APE: r = fa_tw_plan_ape(&src, &set, &plan, 1); break;
    case KIND_MP4: r = fa_tw_plan_mp4(&src, &set, &plan); break;
    case KIND_VORBIS: case KIND_OPUS: r = FA_OK; plan.format = FA_TAG_FORMAT_VORBIS; break;
    default: r = FA_ERROR_UNSUPPORTED_FORMAT; break;
    }
    if (r == FA_OK && (stamp(real, &now) != FA_OK || !same_stamp(before, &now))) r = FA_ERROR_BUSY;

    /* A new file next to the original (the rename must stay on one volume). */
    if (r == FA_OK) {
        for (i = 0; i < 64 && !out; i++) {
            snprintf(tmp, sizeof(tmp), "%s.fatzaudio-%d.tmp", real, i);
            out = fa_fopen_utf8(tmp, "wbx");
        }
        if (!out) r = FA_ERROR_IO;
    }
    if (r == FA_OK) {
        fa_file_copy_mode(real, out);
        r = (kind == KIND_VORBIS || kind == KIND_OPUS) ? fa_tw_write_ogg(&src, &set, opus, out) : write_plan(&src, &plan, out);
        if (r == FA_OK && fa_file_sync(out) != 0) r = FA_ERROR_IO;
    }
    fa_source_close(&src);
    if (out) {
        if (fclose(out) != 0 && r == FA_OK) r = FA_ERROR_IO;
        if (r == FA_OK) r = check_new_file(real, tmp, &set, opus, opus ? -23.0 - *out_gain : lufs,
                                           opus && album ? -23.0 - *out_album_gain : album_lufs);
        if (r == FA_OK && (stamp(real, &now) != FA_OK || !same_stamp(before, &now))) r = FA_ERROR_BUSY;
        if (r == FA_OK && fa_replace_file(tmp, real) != 0) r = FA_ERROR_IO;
        if (r != FA_OK) fa_remove_utf8(tmp);
    }
    if (r == FA_OK) *out_format = plan.format;
    fa_tw_plan_free(&plan);
    return r;
}

/* ---- the call ------------------------------------------------------------------------------- */

typedef struct tw_file {
    file_stamp stamp;
    fa_loudness l;
    double* subs;
    int64_t nsubs;
    fa_result r;
    int silent;
} tw_file;

FA_API fa_result FA_CALL fa_tags_write_loudness(fa_engine* engine, const char* const* paths, uint32_t count,
                                                uint32_t flags, fa_loudness_tag_result* out_results)
{
    tw_file* f;
    uint32_t i;
    int album = (flags & FA_TAGS_WRITE_ALBUM) != 0;
    double album_lufs = NAN, album_peak = NAN;
    fa_result first = FA_OK;
    int measured_all = 1;

    if (!engine || !paths || count == 0 || count > TW_MAX_FILES || (flags & ~(uint32_t)FA_TAGS_WRITE_ALBUM))
        return FA_ERROR_INVALID_ARG;
    for (i = 0; i < count; i++) {
        if (!paths[i]) return FA_ERROR_INVALID_ARG;
        if (out_results && out_results[i].struct_size < sizeof(uint32_t))
            return FA_ERROR_INVALID_ARG;
    }
    f = (tw_file*)calloc(count, sizeof(*f));
    if (!f) {
        /* Every file still gets its result: a zeroed one would read as written (FA_OK). */
        if (out_results) {
            fa_loudness_tag_result res;
            memset(&res, 0, sizeof(res));
            res.struct_size = sizeof(res);
            res.result = FA_ERROR_OUT_OF_MEMORY;
            res.integrated_lufs = res.true_peak_dbtp = res.track_gain_db = NAN;
            res.album_lufs = res.album_peak_dbtp = res.album_gain_db = NAN;
            for (i = 0; i < count; i++) fa_copy_sized(&out_results[i], &res, sizeof(res));
        }
        return FA_ERROR_OUT_OF_MEMORY;
    }

    for (i = 0; i < count; i++) {
        f[i].r = stamp(paths[i], &f[i].stamp);
        if (f[i].r == FA_OK) f[i].r = fa_scan_measure_path(engine, paths[i], &f[i].l, album ? &f[i].subs : NULL, &f[i].nsubs);
        if (f[i].r == FA_OK && !isfinite(f[i].l.integrated_lufs)) f[i].silent = 1;
        if (f[i].r != FA_OK) measured_all = 0;
    }

    if (album && measured_all) {
        int64_t total = 0, at = 0;
        double* all;
        for (i = 0; i < count; i++) total += f[i].nsubs;
        all = (double*)malloc((size_t)(total > 0 ? total : 1) * sizeof(double));
        if (all) {
            for (i = 0; i < count; i++) {
                if (f[i].nsubs) memcpy(all + at, f[i].subs, (size_t)f[i].nsubs * sizeof(double));
                at += f[i].nsubs;
                if (!f[i].silent && !(f[i].l.true_peak_dbtp <= album_peak)) album_peak = f[i].l.true_peak_dbtp;
            }
            album_lufs = fa_loudness_integrated(all, total);
            free(all);
        } else {
            for (i = 0; i < count; i++) if (f[i].r == FA_OK) f[i].r = FA_ERROR_OUT_OF_MEMORY;
        }
    }

    for (i = 0; i < count; i++) {
        fa_loudness_tag_result res;
        memset(&res, 0, sizeof(res));
        res.struct_size = sizeof(res);
        res.integrated_lufs = res.true_peak_dbtp = res.track_gain_db = NAN;
        res.album_lufs = res.album_peak_dbtp = res.album_gain_db = NAN;
        res.result = f[i].r;
        if (f[i].r == FA_OK) {
            res.integrated_lufs = f[i].l.integrated_lufs;
            res.true_peak_dbtp = f[i].l.true_peak_dbtp;
            if (album) {
                res.album_lufs = album_lufs;
                res.album_peak_dbtp = album_peak;
            }
        }
        if (album && !measured_all && f[i].r == FA_OK) res.result = FA_ERROR_CANCELLED;
        else if (album && f[i].r == FA_OK && !isfinite(album_lufs)) res.result = FA_ERROR_NOT_FOUND;
        else if (f[i].r == FA_OK && f[i].silent) res.result = FA_ERROR_NOT_FOUND;
        else if (f[i].r == FA_OK)
            res.result = write_one(paths[i], &f[i].stamp, f[i].l.integrated_lufs, f[i].l.true_peak_dbtp, album, album_lufs,
                                   album_peak, &res.tag_format, &res.track_gain_db, &res.album_gain_db);
        if (res.result != FA_OK) {
            res.track_gain_db = res.album_gain_db = NAN;
            res.tag_format = 0;
            if (first == FA_OK) first = res.result;
        }
        if (out_results) fa_copy_sized(&out_results[i], &res, sizeof(res));
        free(f[i].subs);
    }
    free(f);
    return first;
}
