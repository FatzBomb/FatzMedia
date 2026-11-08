/* tar archives: ustar, GNU and pax headers (POSIX.1-1988/2001, GNU long names). Only regular
 * files are listed. The same header code serves a list read by seeking and a list read from a
 * stream of decoded bytes (tar.gz, tar.bz2, tar.xz). */
#include "archive/tar.h"
#include "archive/archive.h"
#include <stdlib.h>
#include <string.h>

#define TAR_BLOCK 512
#define TAR_META_MAX ((uint64_t)1 << 20)

/* Octal text, or the base-256 form GNU tar writes for big numbers. */
static uint64_t tar_num(const uint8_t* p, size_t n, int* ok)
{
    uint64_t v = 0;
    size_t i = 0;
    if (p[0] & 0x80) {
        if (p[0] & 0x40) { *ok = 0; return 0; }   /* negative */
        v = p[0] & 0x3F;
        for (i = 1; i < n; i++) {
            if (v >> 55) { *ok = 0; return 0; }
            v = (v << 8) | p[i];
        }
        return v;
    }
    while (i < n && p[i] == ' ') i++;
    for (; i < n && p[i] >= '0' && p[i] <= '7'; i++) {
        if (v >> 60) { *ok = 0; return 0; }
        v = (v << 3) | (uint64_t)(p[i] - '0');
    }
    for (; i < n; i++)
        if (p[i] != ' ' && p[i] != 0) { *ok = 0; return 0; }
    return v;
}

int fa_tar_valid_header(const uint8_t* h)
{
    uint32_t sum = 0, ssum = 0, want;
    int ok = 1, i;
    for (i = 0; i < TAR_BLOCK; i++) {
        uint8_t c = (i >= 148 && i < 156) ? ' ' : h[i];
        sum += c;
        ssum += (uint32_t)(int32_t)(int8_t)c;
    }
    want = (uint32_t)tar_num(h + 148, 8, &ok);
    if (!ok || (want != sum && want != ssum)) return 0;
    return h[0] != 0;
}

void fa_tarlist_free(fa_tarlist* t)
{
    uint32_t i;
    for (i = 0; i < t->n; i++) free(t->names[i]);
    free(t->names);
    free(t->e);
    free(t->long_name);
    free(t->pax_path);
    memset(t, 0, sizeof(*t));
}

static char* dup_n(const char* s, size_t n)
{
    char* d = (char*)malloc(n + 1);
    if (!d) return NULL;
    memcpy(d, s, n);
    d[n] = 0;
    return d;
}

static size_t strnlen_(const uint8_t* p, size_t n)
{
    size_t i = 0;
    while (i < n && p[i]) i++;
    return i;
}

static void tarlist_drop_last(fa_tarlist* t)
{
    if (t->n == 0) return;
    t->n--;
    free(t->names[t->n]);
    t->names[t->n] = NULL;
}

static void clear_pending(fa_tarlist* t)
{
    free(t->long_name); t->long_name = NULL;
    free(t->pax_path); t->pax_path = NULL;
    t->has_pax_size = 0;
    t->pax_mtime_ns = INT64_MIN;
}

void fa_tar_meta(fa_tarlist* t, int kind, const uint8_t* data, size_t n)
{
    if (kind == 'L') {
        size_t len = strnlen_(data, n);
        free(t->long_name);
        t->long_name = dup_n((const char*)data, len);
        if (!t->long_name) t->oom = 1;
        return;
    }
    /* pax: "<length> <key>=<value>\n" records. */
    while (n) {
        size_t len = 0, i = 0;
        const uint8_t* rec;
        const uint8_t* eq;
        while (i < n && data[i] >= '0' && data[i] <= '9' && len < n) { len = len * 10 + (size_t)(data[i] - '0'); i++; }
        if (i == 0 || i >= n || data[i] != ' ' || len > n || len <= i + 1) return;
        rec = data + i + 1;
        eq = (const uint8_t*)memchr(rec, '=', len - i - 1);
        if (eq && data[len - 1] == '\n') {
            size_t klen = (size_t)(eq - rec), vlen = len - 1 - (size_t)(eq + 1 - data);
            const char* v = (const char*)eq + 1;
            if (klen == 4 && memcmp(rec, "path", 4) == 0) {
                free(t->pax_path);
                t->pax_path = dup_n(v, vlen);
                if (!t->pax_path) t->oom = 1;
            } else if (klen == 4 && memcmp(rec, "size", 4) == 0) {
                uint64_t x = 0;
                size_t k;
                for (k = 0; k < vlen && v[k] >= '0' && v[k] <= '9' && x < UINT64_MAX / 10 - 10; k++) x = x * 10 + (uint64_t)(v[k] - '0');
                if (k == vlen && k > 0) { t->pax_size = x; t->has_pax_size = 1; }
            } else if (klen == 5 && memcmp(rec, "mtime", 5) == 0) {
                int64_t secs = 0, frac = 0, scale = 1000000000;
                size_t k = 0;
                for (; k < vlen && v[k] >= '0' && v[k] <= '9' && secs < INT64_MAX / 20; k++) secs = secs * 10 + (v[k] - '0');
                if (k < vlen && v[k] == '.') {
                    for (k++; k < vlen && v[k] >= '0' && v[k] <= '9' && scale > 1; k++) { scale /= 10; frac += (v[k] - '0') * scale; }
                }
                if (secs < INT64_MAX / 1000000000 / 2) t->pax_mtime_ns = secs * 1000000000 + frac;
            }
        }
        data += len;
        n -= len;
    }
}

static int add_entry(fa_tarlist* t, const uint8_t* h, uint64_t data_pos, uint64_t size)
{
    fa_zip_entry* e;
    char raw[100 + 155 + 2];
    const char* name;
    size_t nlen;
    int ok = 1;
    uint64_t mtime;
    if (t->n >= FA_TAR_MAX_ENTRIES) { t->too_many = 1; return -1; }
    if (t->n == t->cap) {
        uint32_t cap = t->cap ? t->cap * 2 : 32;
        fa_zip_entry* ne = (fa_zip_entry*)realloc(t->e, (size_t)cap * sizeof(*ne));
        char** nn;
        if (!ne) { t->oom = 1; return -1; }
        t->e = ne;
        nn = (char**)realloc(t->names, (size_t)cap * sizeof(*nn));
        if (!nn) { t->oom = 1; return -1; }
        t->names = nn;
        t->cap = cap;
    }
    if (t->long_name) name = t->long_name;
    else if (t->pax_path) name = t->pax_path;
    else {
        size_t pl = 0, nl = strnlen_(h, 100);
        /* ustar's prefix field (POSIX magic only; GNU's old format uses those bytes for other things). */
        if (memcmp(h + 257, "ustar\0", 6) == 0) pl = strnlen_(h + 345, 155);
        if (pl) { memcpy(raw, h + 345, pl); raw[pl++] = '/'; }
        memcpy(raw + pl, h, nl);
        raw[pl + nl] = 0;
        name = raw;
    }
    nlen = strlen(name);
    if (nlen == 0 || name[nlen - 1] == '/') return 0;
    while (name[0] == '.' && name[1] == '/') name += 2;
    if (!*name) return 0;
    e = &t->e[t->n];
    memset(e, 0, sizeof(*e));
    e->data_offset = (int64_t)data_pos;
    e->header_offset = data_pos - TAR_BLOCK;
    e->method = 0;
    e->size = e->compressed_size = size;
    e->flags = FA_ZE_NO_CRC;
    e->atime_ns = e->ctime_ns = INT64_MIN;
    mtime = tar_num(h + 136, 12, &ok);
    e->mtime_ns = t->pax_mtime_ns != INT64_MIN ? t->pax_mtime_ns : (ok && mtime < (uint64_t)INT64_MAX / 2000000000 ? (int64_t)mtime * 1000000000 : INT64_MIN);
    t->names[t->n] = fa_name_to_utf8(name, 0);
    if (!t->names[t->n]) { t->oom = 1; return -1; }
    t->n++;
    return 1;
}

int fa_tar_header(fa_tarlist* t, const uint8_t* h, uint64_t data_pos, uint64_t* data_len, int* meta_kind)
{
    uint64_t size;
    int ok = 1, type;
    static const uint8_t zero[TAR_BLOCK];
    *data_len = 0;
    if (memcmp(h, zero, TAR_BLOCK) == 0) return FA_TAR_END;
    if (!fa_tar_valid_header(h)) return FA_TAR_BAD;
    type = h[156];
    size = tar_num(h + 124, 12, &ok);
    if (!ok) return FA_TAR_BAD;
    if (type == 'L' || type == 'x') {
        if (size > TAR_META_MAX) return FA_TAR_BAD;
        *data_len = size;
        *meta_kind = type;
        return FA_TAR_META;
    }
    if (type == 'g' || type == 'K') { *data_len = size; return FA_TAR_SKIP; }
    if (type == '0' || type == 0 || type == '7') {
        int a;
        if (t->has_pax_size) size = t->pax_size;
        a = add_entry(t, h, data_pos, size);
        clear_pending(t);
        if (a < 0) return FA_TAR_BAD;
        *data_len = size;
        return a ? FA_TAR_ENTRY : FA_TAR_SKIP;
    }
    /* Directories, links, devices: no data (a link's size field is 0; others may carry some). */
    clear_pending(t);
    *data_len = (type == '1' || type == '2' || type == '5') ? 0 : size;
    return FA_TAR_SKIP;
}

fa_result fa_tar_list_source(fa_source* src, uint64_t size, fa_tarlist* t, const volatile int32_t* cancel)
{
    uint64_t pos = 0;
    uint8_t h[TAR_BLOCK];
    memset(t, 0, sizeof(*t));
    t->pax_mtime_ns = INT64_MIN;
    while (pos + TAR_BLOCK <= size) {
        uint64_t data_len;
        int kind = 0, r;
        int64_t got;
        if (cancel && *cancel) { fa_tarlist_free(t); return FA_ERROR_CANCELLED; }
        if (fa_source_seek(src, (int64_t)pos) != FA_OK) { fa_tarlist_free(t); return FA_ERROR_IO; }
        got = fa_source_read(src, h, TAR_BLOCK);
        if (got != TAR_BLOCK) { fa_tarlist_free(t); return FA_ERROR_IO; }
        pos += TAR_BLOCK;
        r = fa_tar_header(t, h, pos, &data_len, &kind);
        if (r == FA_TAR_END) break;
        if (r == FA_TAR_BAD) {
            if (t->too_many) { fa_tarlist_free(t); return FA_ERROR_TOO_LARGE; }
            if (t->oom) { fa_tarlist_free(t); return FA_ERROR_OUT_OF_MEMORY; }
            if (pos == TAR_BLOCK) { fa_tarlist_free(t); return FA_ERROR_UNSUPPORTED_FORMAT; }
            break;   /* damage or junk after the last entry: what was read stays */
        }
        if (data_len > size - pos) {
            if (r == FA_TAR_ENTRY) tarlist_drop_last(t);
            break;   /* cut short */
        }
        if (r == FA_TAR_META) {
            uint8_t* m = (uint8_t*)malloc(data_len ? (size_t)data_len : 1);
            if (!m) { fa_tarlist_free(t); return FA_ERROR_OUT_OF_MEMORY; }
            if (data_len && (fa_source_seek(src, (int64_t)pos) != FA_OK || fa_source_read(src, m, (size_t)data_len) != (int64_t)data_len)) {
                free(m);
                fa_tarlist_free(t);
                return FA_ERROR_IO;
            }
            fa_tar_meta(t, kind, m, (size_t)data_len);
            free(m);
            if (t->oom) { fa_tarlist_free(t); return FA_ERROR_OUT_OF_MEMORY; }
        }
        pos += (data_len + TAR_BLOCK - 1) / TAR_BLOCK * TAR_BLOCK;
    }
    free(t->long_name); t->long_name = NULL;
    free(t->pax_path); t->pax_path = NULL;
    return FA_OK;
}

/* ---- As a stream --------------------------------------------------------------------------- */

enum { TS_HEADER, TS_META, TS_DATA, TS_SKIP, TS_PAD, TS_DONE };

void fa_tarstream_init(fa_tarstream* s)
{
    memset(s, 0, sizeof(*s));
    s->list.pax_mtime_ns = INT64_MIN;
    s->cur = -1;
}

void fa_tarstream_free(fa_tarstream* s)
{
    uint32_t i;
    for (i = 0; i < s->prefix_cap; i++) free(s->prefix ? s->prefix[i] : NULL);
    free(s->prefix);
    free(s->prefix_len);
    free(s->meta);
    s->prefix = NULL;
    s->prefix_len = NULL;
    s->meta = NULL;
}

/* A last entry whose data did not all arrive is dropped. Call when the stream is over. */
void fa_tarstream_finish(fa_tarstream* s)
{
    if ((s->state == TS_DATA && s->left > 0) && s->cur >= 0 && (uint32_t)s->cur + 1 == s->list.n) {
        free(s->prefix[s->cur]);
        s->prefix[s->cur] = NULL;
        s->prefix_len[s->cur] = 0;
        tarlist_drop_last(&s->list);
    }
}

static int ts_grow_prefix(fa_tarstream* s)
{
    uint32_t cap;
    uint8_t** np;
    uint32_t* nl;
    if (s->list.n <= s->prefix_cap) return 1;
    cap = s->list.cap > s->list.n ? s->list.cap : s->list.n;
    np = (uint8_t**)realloc(s->prefix, (size_t)cap * sizeof(*np));
    if (!np) return 0;
    s->prefix = np;
    nl = (uint32_t*)realloc(s->prefix_len, (size_t)cap * sizeof(*nl));
    if (!nl) return 0;
    s->prefix_len = nl;
    memset(s->prefix + s->prefix_cap, 0, (size_t)(cap - s->prefix_cap) * sizeof(*np));
    memset(s->prefix_len + s->prefix_cap, 0, (size_t)(cap - s->prefix_cap) * sizeof(*nl));
    s->prefix_cap = cap;
    return 1;
}

int fa_tarstream_feed(void* ctx, const uint8_t* data, size_t n)
{
    fa_tarstream* s = (fa_tarstream*)ctx;
    while (n && s->state != TS_DONE) {
        size_t take;
        switch (s->state) {
        case TS_HEADER: {
            uint64_t data_len;
            int kind = 0, r;
            take = TAR_BLOCK - s->have < n ? TAR_BLOCK - s->have : n;
            memcpy(s->block + s->have, data, take);
            s->have += take; data += take; n -= take; s->pos += take;
            if (s->have < TAR_BLOCK) break;
            s->have = 0;
            r = fa_tar_header(&s->list, s->block, s->pos, &data_len, &kind);
            if (r == FA_TAR_END) { s->ended = 1; s->state = TS_DONE; break; }
            if (r == FA_TAR_BAD) { s->bad = s->pos == TAR_BLOCK; s->ended = 1; s->state = TS_DONE; break; }
            s->left = data_len;
            s->pad = (TAR_BLOCK - data_len % TAR_BLOCK) % TAR_BLOCK;
            if (r == FA_TAR_META) {
                free(s->meta);
                s->meta = (uint8_t*)malloc(data_len ? (size_t)data_len : 1);
                if (!s->meta) { s->list.oom = 1; s->ended = 1; s->state = TS_DONE; break; }
                s->meta_n = 0;
                s->meta_kind = kind;
                s->state = data_len ? TS_META : TS_PAD;
                if (!data_len) fa_tar_meta(&s->list, kind, s->meta, 0);
            } else if (r == FA_TAR_ENTRY) {
                s->cur = (int)s->list.n - 1;
                if (!ts_grow_prefix(s)) { s->list.oom = 1; s->ended = 1; s->state = TS_DONE; break; }
                if (data_len && s->prefix_used < FA_TAR_PREFIX_BUDGET) {
                    size_t want = data_len < FA_TAR_PREFIX_BYTES ? (size_t)data_len : FA_TAR_PREFIX_BYTES;
                    s->prefix[s->cur] = (uint8_t*)malloc(want);
                    if (s->prefix[s->cur]) s->prefix_used += want;
                }
                s->state = data_len ? TS_DATA : TS_PAD;
            } else {
                s->state = data_len ? TS_SKIP : TS_PAD;
            }
            if (s->state == TS_PAD && s->pad == 0) s->state = TS_HEADER;
            break;
        }
        case TS_META:
            take = s->left < n ? (size_t)s->left : n;
            memcpy(s->meta + s->meta_n, data, take);
            s->meta_n += take; s->left -= take; data += take; n -= take; s->pos += take;
            if (s->left == 0) {
                fa_tar_meta(&s->list, s->meta_kind, s->meta, s->meta_n);
                if (s->list.oom) { s->ended = 1; s->state = TS_DONE; break; }
                s->state = s->pad ? TS_PAD : TS_HEADER;
            }
            break;
        case TS_DATA: {
            uint64_t seen = (uint64_t)s->list.e[s->cur].size - s->left;
            take = s->left < n ? (size_t)s->left : n;
            if (s->prefix[s->cur] && seen < FA_TAR_PREFIX_BYTES) {
                size_t c = take;
                if (seen + c > FA_TAR_PREFIX_BYTES) c = (size_t)(FA_TAR_PREFIX_BYTES - seen);
                memcpy(s->prefix[s->cur] + seen, data, c);
                s->prefix_len[s->cur] = (uint32_t)(seen + c);
            }
            s->left -= take; data += take; n -= take; s->pos += take;
            if (s->left == 0) s->state = s->pad ? TS_PAD : TS_HEADER;
            break;
        }
        case TS_SKIP:
            take = s->left < n ? (size_t)s->left : n;
            s->left -= take; data += take; n -= take; s->pos += take;
            if (s->left == 0) s->state = s->pad ? TS_PAD : TS_HEADER;
            break;
        case TS_PAD:
            take = s->pad < n ? (size_t)s->pad : n;
            s->pad -= take; data += take; n -= take; s->pos += take;
            if (s->pad == 0) s->state = TS_HEADER;
            break;
        default:
            break;
        }
    }
    return s->state == TS_DONE;
}
