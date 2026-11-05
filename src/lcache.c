/* The loudness cache: one file, "loudness.fac", in fa_engine_config.cache_directory.
 *
 * A 16-byte header, then 48-byte records appended one at a time:
 *   0 "FALR"            4 last used (minutes since 1970)   8 content key
 *  16 key size          24 audio track                     28 loudness, range, peak (float)
 *  40 reserved (0)      44 FNV-1a of bytes 0..43
 * The newest record of a song wins. The file is read once, on the first lookup (a
 * scan thread, never the audio thread), into a table in memory; lookups after that
 * never touch the disk.
 *
 * Writes append a whole record in one write that other processes' appends cannot
 * split (fa_append_utf8), so two apps may share the folder. A record cut short by a
 * crash fails its checksum and the reader finds the next one by its marker. When the
 * file grows past its limit (default 5 MB, about 109,000 songs), or holds much that is
 * stale, it is rewritten: the records other processes appended meanwhile are merged
 * in, the songs used least recently are dropped down to 3/4 of the limit, and the new
 * file replaces the old one by rename, so a crash leaves either file whole.
 *
 * "Used" is the last lookup that found the song or the scan that measured it. A hit
 * moves the time in memory at once; on disk it is appended at most once a day per song
 * (and written in full whenever the file is rewritten), so a song played every day is
 * never the one dropped. */
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define LC_NAME "loudness.fac"
#define LC_MAGIC "FatzAudio-LC"         /* + version, 16 bytes */
#define LC_VERSION 3u                    /* bumped whenever the measurement or key changes */
#define LC_HDR 16
#define LC_REC 48
#define LC_DEFAULT_LIMIT (5u * 1024u * 1024u)
#define LC_MIN_LIMIT (16u * 1024u)
#define LC_TOUCH_MINUTES (24u * 60u)

typedef struct lc_entry {
    uint64_t key;
    int64_t size;
    int32_t track;
    float lufs, range, peak;
    uint32_t used;        /* minutes since 1970, in memory */
    uint32_t used_disk;   /* as the file has it */
    uint64_t seq;         /* order of use within the minute: later = more recent */
} lc_entry;

struct fa_lcache {
    fa_mutex lock;
    char* dir;
    char path[1100];
    uint64_t limit;
    int loaded;
    lc_entry* e;
    uint32_t n, cap;
    uint32_t* slots;      /* index + 1, 0 = empty; power-of-two size */
    uint32_t nslots;
    int64_t file_bytes;   /* the file's size as far as we know */
    int64_t stale_bytes;  /* records in it that a newer one replaced, or garbage */
    double load_ms;
    uint64_t seq;         /* the last lc_entry.seq handed out */
};

static uint32_t now_minutes(void)
{
    time_t t = time(NULL);
    return t > 0 ? (uint32_t)((uint64_t)t / 60u) : 0;
}

static void put_u32(uint8_t* p, uint32_t v) { int i; for (i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (i * 8)); }
static void put_u64(uint8_t* p, uint64_t v) { int i; for (i = 0; i < 8; i++) p[i] = (uint8_t)(v >> (i * 8)); }
static void put_f32(uint8_t* p, float f) { uint32_t v; memcpy(&v, &f, 4); put_u32(p, v); }
static float get_f32(const uint8_t* p) { uint32_t v = fa_le32(p); float f; memcpy(&f, &v, 4); return f; }

static void header(uint8_t* h)
{
    memset(h, 0, LC_HDR);
    memcpy(h, LC_MAGIC, 12);
    put_u32(h + 12, LC_VERSION);
}

static void encode(const lc_entry* x, uint32_t used, uint8_t* r)
{
    memcpy(r, "FALR", 4);
    put_u32(r + 4, used);
    put_u64(r + 8, x->key);
    put_u64(r + 16, (uint64_t)x->size);
    put_u32(r + 24, (uint32_t)x->track);
    put_f32(r + 28, x->lufs);
    put_f32(r + 32, x->range);
    put_f32(r + 36, x->peak);
    put_u32(r + 40, 0);
    put_u32(r + 44, (uint32_t)fa_fnv1a(0xcbf29ce484222325ULL, r, 44));
}

static int decode(const uint8_t* r, lc_entry* x)
{
    if (memcmp(r, "FALR", 4) != 0 || fa_le32(r + 44) != (uint32_t)fa_fnv1a(0xcbf29ce484222325ULL, r, 44)) return 0;
    x->used = x->used_disk = fa_le32(r + 4);
    x->key = fa_le64(r + 8);
    x->size = (int64_t)fa_le64(r + 16);
    x->track = (int32_t)fa_le32(r + 24);
    x->lufs = get_f32(r + 28);
    x->range = get_f32(r + 32);
    x->peak = get_f32(r + 36);
    return 1;
}

/* ---- The table ------------------------------------------------------------------ */

static uint32_t hash_of(uint64_t key, int64_t size, int32_t track)
{
    uint64_t h = key ^ ((uint64_t)size * 0x9E3779B97F4A7C15ULL) ^ ((uint64_t)(uint32_t)track << 17);
    h ^= h >> 29;
    h *= 0xBF58476D1CE4E5B9ULL;
    return (uint32_t)(h ^ (h >> 32));
}

static uint32_t* slot_of(fa_lcache* c, uint64_t key, int64_t size, int32_t track)
{
    uint32_t i = hash_of(key, size, track) & (c->nslots - 1);
    for (;;) {
        uint32_t s = c->slots[i];
        if (!s) return &c->slots[i];
        if (c->e[s - 1].key == key && c->e[s - 1].size == size && c->e[s - 1].track == track) return &c->slots[i];
        i = (i + 1) & (c->nslots - 1);
    }
}

static int rehash(fa_lcache* c, uint32_t want)
{
    uint32_t n = 1024, i;
    uint32_t* s;
    while (n < want * 2) n *= 2;
    s = (uint32_t*)calloc(n, sizeof(uint32_t));
    if (!s) return 0;
    free(c->slots);
    c->slots = s;
    c->nslots = n;
    for (i = 0; i < c->n; i++) *slot_of(c, c->e[i].key, c->e[i].size, c->e[i].track) = i + 1;
    return 1;
}

static void table_reset(fa_lcache* c)
{
    free(c->e);
    free(c->slots);
    c->e = NULL;
    c->slots = NULL;
    c->n = c->cap = c->nslots = 0;
}

/* Adds or replaces. A record from the file (keep_newer 1) replaces one used no more
 * recently, so the later of two records wins; merged in later (keep_newer 2) only one
 * used less recently, so what this process saw since stays. Returns the entry, NULL
 * when out of memory. */
static lc_entry* upsert(fa_lcache* c, const lc_entry* x, int keep_newer)
{
    uint32_t* s;
    if (c->nslots == 0 || (c->n + 1) * 2 > c->nslots) {
        if (!rehash(c, c->n + 1)) return NULL;
    }
    s = slot_of(c, x->key, x->size, x->track);
    if (*s) {
        lc_entry* old = &c->e[*s - 1];
        if ((keep_newer == 1 && old->used > x->used) || (keep_newer == 2 && old->used >= x->used)) {
            if (x->used_disk > old->used_disk) old->used_disk = x->used_disk;
            return old;
        }
        *old = *x;
        return old;
    }
    if (c->n == c->cap) {
        uint32_t ncap = c->cap ? c->cap * 2 : 1024;
        lc_entry* ne = (lc_entry*)realloc(c->e, (size_t)ncap * sizeof(lc_entry));
        if (!ne) return NULL;
        c->e = ne;
        c->cap = ncap;
    }
    c->e[c->n] = *x;
    *s = ++c->n;
    return &c->e[c->n - 1];
}

/* ---- The file ------------------------------------------------------------------- */

/* Reads the file's records into the table (merged by last use). Returns 1 when the
 * file needs rewriting: another version, a damaged record, or much that is stale. */
static int read_file(fa_lcache* c, int merge)
{
    FILE* f = fa_fopen_utf8(c->path, "rb");
    uint8_t* buf;
    long len;
    int64_t pos;
    uint8_t h[LC_HDR];
    int damaged = 0;
    uint32_t before = c->n;
    if (!f) { c->file_bytes = 0; return 0; }
    if (fseek(f, 0, SEEK_END) != 0 || (len = ftell(f)) < 0 || fseek(f, 0, SEEK_SET) != 0) { fclose(f); return 0; }
    c->file_bytes = len;
    buf = (uint8_t*)malloc(len > 0 ? (size_t)len : 1);
    if (!buf) { fclose(f); return 0; }
    if (fread(buf, 1, (size_t)len, f) != (size_t)len) len = 0;
    fclose(f);
    header(h);
    if (len < LC_HDR || memcmp(buf, h, LC_HDR) != 0) {
        free(buf);
        c->stale_bytes = c->file_bytes;
        return len > 0;   /* another version: start over */
    }
    for (pos = LC_HDR; pos + LC_REC <= len;) {
        lc_entry x;
        if (decode(buf + pos, &x)) {
            x.seq = ++c->seq;     /* appended later = used later */
            upsert(c, &x, merge ? 2 : 1);
            pos += LC_REC;
        } else {
            damaged = 1;          /* a record cut short: find the next by its marker */
            pos++;
        }
    }
    if (pos < len) damaged = 1;
    free(buf);
    c->stale_bytes = c->file_bytes - LC_HDR - (int64_t)(c->n - before) * LC_REC;
    if (c->stale_bytes < 0) c->stale_bytes = 0;
    return damaged || c->stale_bytes > c->file_bytes / 4;
}

static int by_use_desc(const void* a, const void* b)
{
    const lc_entry *x = (const lc_entry*)a, *y = (const lc_entry*)b;
    if (x->used != y->used) return x->used < y->used ? 1 : -1;
    return x->seq < y->seq ? 1 : x->seq > y->seq ? -1 : 0;
}

/* Rewrites the file from the table, after merging what other processes appended
 * (unless the table was just read from it), keeping the songs used most recently
 * within 3/4 of the limit. */
static void rewrite_from(fa_lcache* c, int merge)
{
    char tmp[1200];
    FILE* f;
    uint8_t rec[LC_REC], h[LC_HDR];
    uint32_t keep, i;
    int ok;
    if (merge) read_file(c, 1);                    /* merge others' appends */
    keep = (uint32_t)((c->limit * 3 / 4 - LC_HDR) / LC_REC);
    if (c->n > keep) {
        qsort(c->e, c->n, sizeof(lc_entry), by_use_desc);
        c->n = keep;
        if (!rehash(c, c->n)) table_reset(c);      /* the old slots point at moved entries */
    }
    fa_mkdir_utf8(c->dir);
    snprintf(tmp, sizeof(tmp), "%s.%llx.tmp", c->path, (unsigned long long)fa_thread_current_id());
    f = fa_fopen_utf8(tmp, "wb");
    if (!f) return;
    header(h);
    ok = fwrite(h, 1, LC_HDR, f) == LC_HDR;
    for (i = 0; ok && i < c->n; i++) {
        encode(&c->e[i], c->e[i].used, rec);
        ok = fwrite(rec, 1, LC_REC, f) == LC_REC;
        c->e[i].used_disk = c->e[i].used;
    }
    if (fclose(f) != 0 || !ok || fa_replace_file(tmp, c->path) != 0) {
        fa_remove_utf8(tmp);
        return;
    }
    c->file_bytes = LC_HDR + (int64_t)c->n * LC_REC;
    c->stale_bytes = 0;
}

static void rewrite(fa_lcache* c) { rewrite_from(c, 1); }

static void append(fa_lcache* c, const lc_entry* x)
{
    uint8_t rec[LC_REC];
    if (c->file_bytes < LC_HDR) {
        /* No file yet (or one of another version, rewritten instead). */
        rewrite(c);
        return;
    }
    encode(x, x->used, rec);
    if (fa_append_utf8(c->path, rec, LC_REC) == 0) c->file_bytes += LC_REC;
    else rewrite(c);   /* gone (another app cleared the cache): start it again, header first */
}

static void ensure_loaded(fa_lcache* c)
{
    int64_t t0;
    if (c->loaded) return;
    c->loaded = 1;
    t0 = fa_monotonic_ns();
    if (read_file(c, 0) || c->file_bytes > (int64_t)c->limit) rewrite_from(c, 0);   /* just read */
    c->load_ms = (double)(fa_monotonic_ns() - t0) / 1e6;
}

/* ---- Internal API ----------------------------------------------------------------- */

fa_lcache* fa_lcache_new(const char* dir)
{
    fa_lcache* c;
    size_t n;
    if (!dir || !dir[0]) return NULL;
    c = (fa_lcache*)calloc(1, sizeof(*c));
    if (!c) return NULL;
    if (fa_mutex_init(&c->lock) != 0) { free(c); return NULL; }
    c->dir = fa_strdup(dir);
    if (!c->dir) { fa_mutex_destroy(&c->lock); free(c); return NULL; }
    n = strlen(dir);
    snprintf(c->path, sizeof(c->path), "%s%s%s", dir, (dir[n - 1] == '/' || dir[n - 1] == '\\') ? "" : "/", LC_NAME);
    c->limit = LC_DEFAULT_LIMIT;
    return c;
}

void fa_lcache_free(fa_lcache* c)
{
    if (!c) return;
    table_reset(c);
    free(c->dir);
    fa_mutex_destroy(&c->lock);
    free(c);
}

int fa_lcache_get(fa_lcache* c, uint64_t key, int64_t size, int32_t track, fa_loudness* out)
{
    uint32_t* s;
    int found = 0;
    if (!c) return 0;
    fa_mutex_lock(&c->lock);
    ensure_loaded(c);
    if (c->nslots && *(s = slot_of(c, key, size, track))) {
        lc_entry* x = &c->e[*s - 1];
        uint32_t now = now_minutes();
        out->integrated_lufs = x->lufs;
        out->range_lu = x->range;
        out->true_peak_dbtp = x->peak;
        found = 1;
        if (now > x->used) x->used = now;
        x->seq = ++c->seq;
        if (x->used > x->used_disk && x->used - x->used_disk >= LC_TOUCH_MINUTES) {   /* used: tell the file, once a day at most */
            x->used_disk = x->used;
            c->stale_bytes += LC_REC;
            append(c, x);         /* last: a rewrite in it may move the entries */
        }
    }
    fa_mutex_unlock(&c->lock);
    return found;
}

void fa_lcache_put(fa_lcache* c, uint64_t key, int64_t size, int32_t track, const fa_loudness* l)
{
    lc_entry x, *in;
    if (!c) return;
    memset(&x, 0, sizeof(x));
    x.key = key;
    x.size = size;
    x.track = track;
    x.lufs = (float)l->integrated_lufs;
    x.range = (float)l->range_lu;
    x.peak = (float)l->true_peak_dbtp;
    x.used = x.used_disk = now_minutes();
    fa_mutex_lock(&c->lock);
    x.seq = ++c->seq;
    ensure_loaded(c);
    in = upsert(c, &x, 0);
    if (in) {
        append(c, in);
        if (c->file_bytes > (int64_t)c->limit || c->stale_bytes > c->file_bytes / 2) rewrite(c);
    }
    fa_mutex_unlock(&c->lock);
}

void fa_lcache_set_limit(fa_lcache* c, uint64_t bytes)
{
    if (!c) return;
    fa_mutex_lock(&c->lock);
    c->limit = bytes ? bytes : LC_DEFAULT_LIMIT;
    if (c->loaded && c->file_bytes > (int64_t)c->limit) rewrite(c);
    fa_mutex_unlock(&c->lock);
}

/* The per-song files of earlier versions ("<16 hex>-<size>-<track>.loudness") and
 * the analysis results (".analysis"): FatzAudio's own names only. */
static void clear_one(const char* folder, const char* name, void* user)
{
    size_t n = strlen(name), i;
    char path[1200];
    (void)user;
    if (strcmp(name, LC_NAME) != 0) {
        int ours = n > 17 && name[16] == '-';
        for (i = 0; ours && i < 16; i++)
            ours = (name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f');
        if (!ours) return;
        if (!(n > 9 && strcmp(name + n - 9, ".loudness") == 0) && !(n > 9 && strcmp(name + n - 9, ".analysis") == 0)) return;
    }
    snprintf(path, sizeof(path), "%s/%s", folder, name);
    fa_remove_utf8(path);
}

void fa_lcache_clear(fa_lcache* c)
{
    if (!c) return;
    fa_mutex_lock(&c->lock);
    table_reset(c);
    c->loaded = 1;
    c->file_bytes = c->stale_bytes = 0;
    fa_folder_each_file(c->dir, clear_one, NULL);
    fa_mutex_unlock(&c->lock);
}

double fa_lcache_load_ms(fa_lcache* c)
{
    double ms;
    if (!c) return -1.0;
    fa_mutex_lock(&c->lock);
    ms = c->loaded ? c->load_ms : -1.0;
    fa_mutex_unlock(&c->lock);
    return ms;
}

/* ---- Public API ------------------------------------------------------------------- */

FA_API fa_result FA_CALL fa_engine_set_cache_size(fa_engine* engine, uint64_t max_bytes)
{
    if (!engine || (max_bytes != 0 && max_bytes < LC_MIN_LIMIT)) return FA_ERROR_INVALID_ARG;
    fa_lcache_set_limit(engine->lcache, max_bytes);
    return FA_OK;
}

FA_API fa_result FA_CALL fa_engine_clear_cache(fa_engine* engine)
{
    if (!engine) return FA_ERROR_INVALID_ARG;
    fa_lcache_clear(engine->lcache);
    return FA_OK;
}
