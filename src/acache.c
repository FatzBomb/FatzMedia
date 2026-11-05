/* The recently-unpacked cache: see acache.h. */
#include "acache.h"
#include "archive/archive.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define ACACHE_DEFAULT_SECONDS 30

struct fa_acache {
    fa_mutex lock;
    fa_atomic32 held;           /* anything in either slot (read without the lock) */
    int64_t ttl_ns;
    uint64_t half_limit;
    struct {
        char* key;
        fa_archive* a;
        int64_t expires;
    } arch;
    struct {
        char* key;
        char* name;
        fa_blob* blob;
        int64_t expires;
    } ent;
};

fa_blob* fa_blob_new(uint8_t* data, size_t size)
{
    fa_blob* b = (fa_blob*)malloc(sizeof(*b));
    if (!b) { free(data); return NULL; }
    b->refs.v = 1;
    b->data = data;
    b->size = size;
    return b;
}

fa_blob* fa_blob_retain(fa_blob* b)
{
    if (b) fa_fetch_add32(&b->refs, 1);
    return b;
}

void fa_blob_release(fa_blob* b)
{
    if (b && fa_fetch_add32(&b->refs, -1) == 1) {
        free(b->data);
        free(b);
    }
}

fa_acache* fa_acache_new(uint32_t seconds, uint64_t memory_limit)
{
    fa_acache* c = (fa_acache*)calloc(1, sizeof(*c));
    if (!c) return NULL;
    if (fa_mutex_init(&c->lock) != 0) { free(c); return NULL; }
    c->ttl_ns = (int64_t)(seconds ? seconds : ACACHE_DEFAULT_SECONDS) * 1000000000ll;
    c->half_limit = memory_limit / 2;
    return c;
}

static void drop_arch(fa_acache* c)
{
    free(c->arch.key);
    fa_archive_release(c->arch.a);
    memset(&c->arch, 0, sizeof(c->arch));
}

static void drop_ent(fa_acache* c)
{
    free(c->ent.key);
    free(c->ent.name);
    fa_blob_release(c->ent.blob);
    memset(&c->ent, 0, sizeof(c->ent));
}

int fa_acache_is_empty(const fa_acache* c) { return !c || !fa_load32(&((fa_acache*)c)->held); }
static void update_held(fa_acache* c) { fa_store32(&c->held, c->arch.a != NULL || c->ent.blob != NULL); }

void fa_acache_free(fa_acache* c)
{
    if (!c) return;
    drop_arch(c);
    drop_ent(c);
    fa_mutex_destroy(&c->lock);
    free(c);
}

void fa_acache_expire(fa_acache* c)
{
    int64_t now;
    if (!c || !fa_load32(&c->held)) return;
    now = fa_monotonic_ns();
    fa_mutex_lock(&c->lock);
    if (c->arch.a && now >= c->arch.expires) drop_arch(c);
    if (c->ent.blob && now >= c->ent.expires) drop_ent(c);
    update_held(c);
    fa_mutex_unlock(&c->lock);
}

char* fa_acache_key(const fa_source* src, const char* path)
{
    uint64_t id[4];
    size_t n;
    char* key;
    if (!path || !src || src->kind != FA_SOURCE_FILE || !src->file || src->shared) return NULL;
    if (!fa_file_identity(src->file, id)) return NULL;
    n = strlen(path) + 128;
    key = (char*)malloc(n);
    if (!key) return NULL;
    snprintf(key, n, "%llx.%llx.%llx.%llx.%lld|%s", (unsigned long long)id[0], (unsigned long long)id[1], (unsigned long long)id[2],
             (unsigned long long)id[3], (long long)(src->whole_length ? src->whole_length : src->length), path);
    return key;
}

fa_archive* fa_acache_get_archive(fa_acache* c, const char* key)
{
    fa_archive* a = NULL;
    if (!c || !key || !fa_load32(&c->held)) return NULL;
    fa_mutex_lock(&c->lock);
    if (c->arch.a && strcmp(c->arch.key, key) == 0) a = fa_archive_retain(c->arch.a);
    fa_mutex_unlock(&c->lock);
    return a;
}

void fa_acache_put_archive(fa_acache* c, const char* key, fa_archive* a)
{
    char* copy;
    if (!c || !key || !a) return;
    if (fa_archive_held_bytes(a) > c->half_limit) {
        /* Too much to keep: and a cursor already in the cache for it (earlier, when it held less) goes too. */
        fa_mutex_lock(&c->lock);
        if (c->arch.a == a) { drop_arch(c); update_held(c); }
        fa_mutex_unlock(&c->lock);
        return;
    }
    fa_mutex_lock(&c->lock);
    if (c->arch.a == a) {
        c->arch.expires = fa_monotonic_ns() + c->ttl_ns;
    } else if ((copy = fa_strdup(key)) != NULL) {
        drop_arch(c);
        c->arch.key = copy;
        c->arch.a = fa_archive_retain(a);
        c->arch.expires = fa_monotonic_ns() + c->ttl_ns;
    }
    update_held(c);
    fa_mutex_unlock(&c->lock);
}

fa_blob* fa_acache_get_entry(fa_acache* c, const char* key, const char* entry_name)
{
    fa_blob* b = NULL;
    if (!c || !key || !entry_name || !fa_load32(&c->held)) return NULL;
    fa_mutex_lock(&c->lock);
    if (c->ent.blob && strcmp(c->ent.key, key) == 0 && strcmp(c->ent.name, entry_name) == 0) {
        b = fa_blob_retain(c->ent.blob);
        c->ent.expires = fa_monotonic_ns() + c->ttl_ns;
    }
    fa_mutex_unlock(&c->lock);
    return b;
}

int fa_acache_entry_fits(const fa_acache* c, size_t size) { return c && (uint64_t)size <= c->half_limit; }

void fa_acache_put_entry(fa_acache* c, const char* key, const char* entry_name, fa_blob* b)
{
    char* k;
    char* n;
    if (!c || !key || !entry_name || !b || !fa_acache_entry_fits(c, b->size)) return;
    k = fa_strdup(key);
    n = fa_strdup(entry_name);
    if (!k || !n) { free(k); free(n); return; }
    fa_mutex_lock(&c->lock);
    drop_ent(c);
    c->ent.key = k;
    c->ent.name = n;
    c->ent.blob = fa_blob_retain(b);
    c->ent.expires = fa_monotonic_ns() + c->ttl_ns;
    update_held(c);
    fa_mutex_unlock(&c->lock);
}
