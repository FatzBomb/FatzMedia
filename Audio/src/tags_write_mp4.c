/* Loudness tags in MP4 / M4A files (tags_write.h): iTunes freeform items,
 * ----:com.apple.iTunes:replaygain_track_gain and so on, in moov/udta/meta/ilst.
 *
 * moov is rebuilt in memory with the new ilst (or a new udta / meta / ilst where
 * there was none) and its parents' sizes adjusted. When moov changes size, a free
 * box right after it takes up the difference if it can; otherwise every chunk
 * offset (stco, co64) that points past moov moves with it. Fragmented files
 * (moof), whose offsets live elsewhere, are refused unless the free box absorbs it. */
#include "tags_write.h"
#include <stdlib.h>
#include <string.h>

#define MP4_MAX_MOOV (64u << 20)
#define FOURCC(a, b, c, d) (((uint32_t)(a) << 24) | ((uint32_t)(b) << 16) | ((uint32_t)(c) << 8) | (uint32_t)(d))

typedef struct box {
    uint32_t type;
    size_t pos, data, end;            /* in the buffer */
    int hl;                           /* header length: 8, or 16 with a 64-bit size */
} box;

/* The box at `pos` of p[0..end). */
static int box_at(const uint8_t* p, size_t pos, size_t end, box* b)
{
    uint64_t size;
    if (pos + 8 > end) return 0;
    size = fa_be32(p + pos);
    b->hl = 8;
    if (size == 1) {
        if (pos + 16 > end) return 0;
        size = ((uint64_t)fa_be32(p + pos + 8) << 32) | fa_be32(p + pos + 12);
        b->hl = 16;
    } else if (size == 0) {
        size = end - pos;
    }
    if (size < (uint64_t)b->hl || size > (uint64_t)(end - pos)) return 0;
    b->type = fa_be32(p + pos + 4);
    b->pos = pos;
    b->data = pos + (size_t)b->hl;
    b->end = pos + (size_t)size;
    return 1;
}

static int find_child(const uint8_t* p, size_t from, size_t end, uint32_t type, box* out)
{
    box b;
    while (box_at(p, from, end, &b)) {
        if (b.type == type) { *out = b; return 1; }
        from = b.end;
    }
    return 0;
}

/* Adds `delta` to the size of the box at `b` (its header still at b->pos). */
static int grow_box(uint8_t* p, const box* b, int64_t delta)
{
    if (b->hl == 16) {
        uint64_t s = (((uint64_t)fa_be32(p + b->pos + 8) << 32) | fa_be32(p + b->pos + 12)) + (uint64_t)delta;
        int i;
        for (i = 0; i < 8; i++) p[b->pos + 8 + i] = (uint8_t)(s >> (56 - 8 * i));
        return 1;
    } else {
        int64_t s = (int64_t)fa_be32(p + b->pos) + delta;
        int i;
        if (fa_be32(p + b->pos) == 0) return 1;         /* "to the end": stays so */
        if (s < 8 || s > (int64_t)UINT32_MAX) return 0;
        for (i = 0; i < 4; i++) p[b->pos + i] = (uint8_t)((uint64_t)s >> (24 - 8 * i));
        return 1;
    }
}

static void put_box_header(fa_tw_buf* b, uint32_t size, uint32_t type)
{
    fa_tw_be32(b, size);
    fa_tw_be32(b, type);
}

/* A freeform item's name ("replaygain_track_gain") from its 'name' child; "" if none. */
static void item_name(const uint8_t* p, const box* it, char* out, size_t cap)
{
    box nm;
    out[0] = '\0';
    if (find_child(p, it->data, it->end, FOURCC('n', 'a', 'm', 'e'), &nm) && nm.end - nm.data >= 4) {
        size_t n = nm.end - nm.data - 4;
        if (n >= cap) n = cap - 1;
        memcpy(out, p + nm.data + 4, n);
        out[n] = '\0';
    }
}

static void put_freeform(fa_tw_buf* b, const char* key, const char* value)
{
    static const char mean[] = "com.apple.iTunes";
    char name[40];
    size_t kl = strlen(key) < sizeof(name) ? strlen(key) : sizeof(name) - 1, vl = strlen(value), i;
    uint32_t mean_box = 12 + (uint32_t)(sizeof(mean) - 1), name_box = 12 + (uint32_t)kl, data_box = 16 + (uint32_t)vl;
    for (i = 0; i < kl && i + 1 < sizeof(name); i++) name[i] = (char)(key[i] >= 'A' && key[i] <= 'Z' ? key[i] - 'A' + 'a' : key[i]);
    put_box_header(b, 8 + mean_box + name_box + data_box, FOURCC('-', '-', '-', '-'));
    put_box_header(b, mean_box, FOURCC('m', 'e', 'a', 'n'));
    fa_tw_be32(b, 0);
    fa_tw_put(b, mean, sizeof(mean) - 1);
    put_box_header(b, name_box, FOURCC('n', 'a', 'm', 'e'));
    fa_tw_be32(b, 0);
    fa_tw_put(b, name, kl);
    put_box_header(b, data_box, FOURCC('d', 'a', 't', 'a'));
    fa_tw_be32(b, 1);                                    /* UTF-8 */
    fa_tw_be32(b, 0);                                    /* locale */
    fa_tw_put(b, value, vl);
}

/* The new ilst box: the old items without the replaced loudness ones, then ours. */
static fa_result new_ilst(const uint8_t* p, const box* ilst, const fa_tw_set* set, fa_tw_buf* out)
{
    fa_tw_buf body;
    size_t pos;
    box it;
    int i;
    memset(&body, 0, sizeof(body));
    if (ilst) {
        for (pos = ilst->data; pos < ilst->end; pos = it.end) {
            char name[260];
            if (!box_at(p, pos, ilst->end, &it)) { fa_tw_free(&body); return FA_ERROR_UNSUPPORTED_FORMAT; }
            if (it.type == FOURCC('-', '-', '-', '-')) {
                item_name(p, &it, name, sizeof(name));
                if (fa_tw_replaces(set, name, strlen(name))) continue;
            }
            fa_tw_put(&body, p + it.pos, it.end - it.pos);
        }
    }
    for (i = 0; i < set->count; i++) put_freeform(&body, set->items[i].key, set->items[i].value);
    put_box_header(out, (uint32_t)(8 + body.n), FOURCC('i', 'l', 's', 't'));
    fa_tw_put(out, body.p, body.n);
    if (body.failed || body.n > MP4_MAX_MOOV) out->failed = 1;
    fa_tw_free(&body);
    return out->failed ? FA_ERROR_OUT_OF_MEMORY : FA_OK;
}

/* meta (a full box) with an iTunes handler around `ilst`. */
static void wrap_meta(const fa_tw_buf* ilst, fa_tw_buf* out)
{
    static const uint8_t hdlr[33] = { 0, 0, 0, 33, 'h', 'd', 'l', 'r', 0, 0, 0, 0, 0, 0, 0, 0,
                                      'm', 'd', 'i', 'r', 'a', 'p', 'p', 'l', 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    put_box_header(out, (uint32_t)(12 + sizeof(hdlr) + ilst->n), FOURCC('m', 'e', 't', 'a'));
    fa_tw_be32(out, 0);
    fa_tw_put(out, hdlr, sizeof(hdlr));
    fa_tw_put(out, ilst->p, ilst->n);
}

/* Moves every chunk offset at or past `from` by `delta`: moov/trak/mdia/minf/stbl/stco|co64. */
static int shift_offsets(uint8_t* p, const box* moov, int64_t from, int64_t delta)
{
    size_t pos;
    box trak, mdia, minf, stbl, co;
    for (pos = moov->data; box_at(p, pos, moov->end, &trak); pos = trak.end) {
        size_t c;
        if (trak.type != FOURCC('t', 'r', 'a', 'k')) continue;
        if (!find_child(p, trak.data, trak.end, FOURCC('m', 'd', 'i', 'a'), &mdia) ||
            !find_child(p, mdia.data, mdia.end, FOURCC('m', 'i', 'n', 'f'), &minf) ||
            !find_child(p, minf.data, minf.end, FOURCC('s', 't', 'b', 'l'), &stbl))
            continue;
        for (c = stbl.data; box_at(p, c, stbl.end, &co); c = co.end) {
            int wide = co.type == FOURCC('c', 'o', '6', '4');
            uint32_t n, i;
            if (co.type != FOURCC('s', 't', 'c', 'o') && !wide) continue;
            if (co.end - co.data < 8) return 0;
            n = fa_be32(p + co.data + 4);
            if ((uint64_t)n * (wide ? 8u : 4u) > co.end - co.data - 8) return 0;
            for (i = 0; i < n; i++) {
                uint8_t* e = p + co.data + 8 + (size_t)i * (wide ? 8 : 4);
                int64_t v = wide ? (int64_t)(((uint64_t)fa_be32(e) << 32) | fa_be32(e + 4)) : (int64_t)fa_be32(e);
                int k;
                if (v < from) continue;
                v += delta;
                if (v < 0 || (!wide && v > (int64_t)UINT32_MAX)) return 0;
                for (k = 0; k < (wide ? 8 : 4); k++) e[k] = (uint8_t)((uint64_t)v >> ((wide ? 56 : 24) - 8 * k));
            }
        }
    }
    return 1;
}

fa_result fa_tw_plan_mp4(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan)
{
    uint8_t h[16];
    int64_t pos = 0, moov_pos = -1, moov_end = 0, next_end = 0;
    int fragmented = 0, free_after = 0;
    uint32_t next_size = 0;
    uint8_t* m;
    size_t mlen, splice_from, splice_to;
    box moov, udta, meta, ilst, chain[3];
    int nchain = 0, have_udta, have_meta = 0, have_ilst = 0, i;
    fa_tw_buf piece, inner, out;
    fa_tw_edit* e;
    int64_t delta;

    /* The top level: where moov is, what follows it, and whether there are fragments. */
    while (pos + 8 <= src->length) {
        uint64_t size;
        int hl = 8;
        if (fa_source_seek(src, pos) != FA_OK || fa_source_read(src, h, 8) != 8) return FA_ERROR_IO;
        size = fa_be32(h);
        if (size == 1) {
            if (fa_source_read(src, h + 8, 8) != 8) return FA_ERROR_IO;
            size = ((uint64_t)fa_be32(h + 8) << 32) | fa_be32(h + 12);
            hl = 16;
        } else if (size == 0) {
            size = (uint64_t)(src->length - pos);
        }
        if (size < (uint64_t)hl || size > (uint64_t)(src->length - pos)) return FA_ERROR_UNSUPPORTED_FORMAT;
        if (fa_be32(h + 4) == FOURCC('m', 'o', 'o', 'v')) {
            if (moov_pos >= 0) return FA_ERROR_UNSUPPORTED_FORMAT;
            moov_pos = pos;
            moov_end = pos + (int64_t)size;
        } else if (moov_pos >= 0 && pos == moov_end && hl == 8 &&
                   (fa_be32(h + 4) == FOURCC('f', 'r', 'e', 'e') || fa_be32(h + 4) == FOURCC('s', 'k', 'i', 'p'))) {
            free_after = 1;
            next_size = (uint32_t)size;
            next_end = pos + (int64_t)size;
        }
        if (fa_be32(h + 4) == FOURCC('m', 'o', 'o', 'f')) fragmented = 1;
        pos += (int64_t)size;
    }
    if (moov_pos < 0 || moov_end - moov_pos > (int64_t)MP4_MAX_MOOV) return FA_ERROR_UNSUPPORTED_FORMAT;
    mlen = (size_t)(moov_end - moov_pos);
    m = fa_tw_read(src, moov_pos, mlen);
    if (!m) return FA_ERROR_IO;
    if (!box_at(m, 0, mlen, &moov)) { free(m); return FA_ERROR_UNSUPPORTED_FORMAT; }

    /* udta/meta/ilst, or meta/ilst straight in moov. */
    chain[nchain++] = moov;
    have_udta = find_child(m, moov.data, moov.end, FOURCC('u', 'd', 't', 'a'), &udta);
    if (have_udta) {
        chain[nchain++] = udta;
        have_meta = find_child(m, udta.data, udta.end, FOURCC('m', 'e', 't', 'a'), &meta);
    }
    if (!have_meta && find_child(m, moov.data, moov.end, FOURCC('m', 'e', 't', 'a'), &meta)) {
        have_meta = 1;
        nchain = 1;
    }
    if (have_meta) {
        /* ISO meta is a full box (4 bytes of version and flags); QuickTime's is not. */
        size_t first = meta.data;
        if (meta.end - meta.data >= 8 && fa_be32(m + meta.data + 4) != FOURCC('h', 'd', 'l', 'r')) first += 4;
        chain[nchain++] = meta;
        have_ilst = find_child(m, first, meta.end, FOURCC('i', 'l', 's', 't'), &ilst);
    }

    memset(&piece, 0, sizeof(piece));
    memset(&inner, 0, sizeof(inner));
    memset(&out, 0, sizeof(out));
    if (new_ilst(m, have_ilst ? &ilst : NULL, set, have_ilst || have_meta ? &piece : &inner) != FA_OK) {
        fa_tw_free(&piece); fa_tw_free(&inner); free(m);
        return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    if (have_ilst) {
        splice_from = ilst.pos;
        splice_to = ilst.end;
    } else if (have_meta) {
        splice_from = splice_to = meta.end;
    } else if (have_udta) {
        wrap_meta(&inner, &piece);
        splice_from = splice_to = udta.end;
    } else {
        fa_tw_buf meta_box;
        memset(&meta_box, 0, sizeof(meta_box));
        wrap_meta(&inner, &meta_box);
        put_box_header(&piece, (uint32_t)(8 + meta_box.n), FOURCC('u', 'd', 't', 'a'));
        fa_tw_put(&piece, meta_box.p, meta_box.n);
        if (meta_box.failed) piece.failed = 1;
        fa_tw_free(&meta_box);
        splice_from = splice_to = moov.end;
    }
    fa_tw_free(&inner);
    delta = (int64_t)piece.n - (int64_t)(splice_to - splice_from);

    fa_tw_put(&out, m, splice_from);
    fa_tw_put(&out, piece.p, piece.n);
    fa_tw_put(&out, m + splice_to, mlen - splice_to);
    fa_tw_free(&piece);
    free(m);
    if (out.failed) { fa_tw_free(&out); return FA_ERROR_OUT_OF_MEMORY; }
    for (i = 0; i < nchain; i++) {
        if (!grow_box(out.p, &chain[i], delta)) { fa_tw_free(&out); return FA_ERROR_UNSUPPORTED_FORMAT; }
    }
    moov.end = (size_t)((int64_t)moov.end + delta);

    e = fa_tw_add(plan, moov_pos, moov_end);
    if (delta != 0 && free_after && (int64_t)next_size - delta >= 8 && (int64_t)next_size - delta <= (int64_t)MP4_MAX_MOOV) {
        /* The free box after moov takes up the difference: nothing else moves. */
        uint32_t fs = (uint32_t)((int64_t)next_size - delta);
        put_box_header(&out, fs, FOURCC('f', 'r', 'e', 'e'));
        fa_tw_zeros(&out, fs - 8);
        e->to = next_end;
    } else if (delta != 0) {
        if (fragmented || !shift_offsets(out.p, &moov, moov_end, delta)) {
            fa_tw_free(&out);
            return FA_ERROR_UNSUPPORTED_FORMAT;
        }
    }
    if (out.failed) { fa_tw_free(&out); return FA_ERROR_OUT_OF_MEMORY; }
    e->data = out;
    plan->format = FA_TAG_FORMAT_MP4;
    return FA_OK;
}
