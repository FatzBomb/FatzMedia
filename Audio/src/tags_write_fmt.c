/* Loudness tag writers for ID3v2 (MP3, ADTS AAC), APEv2 and FLAC (tags_write.h).
 * Each rebuilds only its tag, keeping every other frame, item or block byte for
 * byte, and refuses (FA_ERROR_UNSUPPORTED_FORMAT) anything it cannot parse in
 * full, so nothing it does not understand is lost. */
#include "tags_write.h"
#include <stdlib.h>
#include <string.h>

#define ID3_PADDING 1024
#define FLAC_PADDING 1024
#define FLAC_MAX_BLOCKS 4096
#define APE_MAX_TAG (16u << 20)

static void put_synchsafe(fa_tw_buf* b, uint32_t v)
{
    uint8_t q[4] = { (uint8_t)((v >> 21) & 0x7f), (uint8_t)((v >> 14) & 0x7f), (uint8_t)((v >> 7) & 0x7f), (uint8_t)(v & 0x7f) };
    fa_tw_put(b, q, 4);
}

/* ---- ID3v2 ----------------------------------------------------------------------------- */

static size_t unsync_undo(uint8_t* p, size_t n)
{
    size_t i, k = 0;
    for (i = 0; i < n; i++) {
        p[k++] = p[i];
        if (p[i] == 0xFF && i + 1 < n && p[i + 1] == 0x00) i++;
    }
    return k;
}

static int frame_id_ok(const uint8_t* p)
{
    int i;
    for (i = 0; i < 4; i++) if (!((p[i] >= 'A' && p[i] <= 'Z') || (p[i] >= '0' && p[i] <= '9'))) return 0;
    return 1;
}

/* A frame's size, as the reader takes it: ID3v2.4 sizes are synchsafe, but some
 * writers store them plain; the plain one is taken when only it leads to a frame. */
static int64_t frame_size(const uint8_t* b, size_t pos, size_t n, int ver)
{
    const uint8_t* fh = b + pos;
    int64_t fsize, plain;
    if (ver == 3) return fa_be32(fh + 4);
    plain = fa_be32(fh + 4);
    fsize = !((fh[4] | fh[5] | fh[6] | fh[7]) & 0x80) ? fa_synchsafe(fh + 4) : plain;
    if (fsize >= 0x80 && pos + 10 + (size_t)fsize + 10 <= n) {
        const uint8_t* nx = b + pos + 10 + fsize;
        int synch_ok = (nx[0] >= 'A' && nx[0] <= 'Z') || (nx[0] >= '0' && nx[0] <= '9') || nx[0] == 0;
        if (!synch_ok && pos + 10 + (size_t)plain + 4 <= n) {
            const uint8_t* pn = b + pos + 10 + plain;
            if ((pn[0] >= 'A' && pn[0] <= 'Z') || pn[0] == 0) fsize = plain;
        }
    }
    return fsize;
}

/* The description of a TXXX frame body in ASCII (empty when it is not ASCII). */
static void txxx_description(const uint8_t* p, size_t n, char* out, size_t cap)
{
    size_t i = 1, k = 0;
    int enc, big = 1;
    out[0] = '\0';
    if (n < 2) return;
    enc = p[0];
    if (enc == 0 || enc == 3) {
        for (; i < n && p[i] && k + 1 < cap; i++) {
            if (p[i] > 0x7F) { out[0] = '\0'; return; }
            out[k++] = (char)p[i];
        }
    } else if (enc == 1 || enc == 2) {
        if (enc == 1 && i + 1 < n && p[i] == 0xFF && p[i + 1] == 0xFE) { big = 0; i += 2; }
        else if (enc == 1 && i + 1 < n && p[i] == 0xFE && p[i + 1] == 0xFF) i += 2;
        for (; i + 1 < n && k + 1 < cap; i += 2) {
            unsigned u = big ? ((unsigned)p[i] << 8) | p[i + 1] : ((unsigned)p[i + 1] << 8) | p[i];
            if (!u) break;
            if (u > 0x7F) { out[0] = '\0'; return; }
            out[k++] = (char)u;
        }
    }
    out[k] = '\0';
}

/* Is this frame one of the loudness TXXX frames the set replaces? */
static int frame_replaced(const fa_tw_set* set, const uint8_t* fh, size_t fsize, int ver)
{
    const uint8_t* p = fh + 10;
    size_t n = fsize;
    int fl = fh[9];
    char desc[64];
    if (memcmp(fh, "TXXX", 4) != 0) return 0;
    if (ver == 3) {
        if (fl & 0xC0) return 0;                         /* compressed or encrypted: not read, kept */
        if (fl & 0x20) { if (!n) return 0; p++; n--; }  /* group */
    } else {
        if (fl & 0x0C) return 0;
        if (fl & 0x40) { if (!n) return 0; p++; n--; }
        if (fl & 0x01) { if (n < 4) return 0; p += 4; n -= 4; }
    }
    txxx_description(p, n, desc, sizeof(desc));
    return desc[0] && fa_tw_replaces(set, desc, strlen(desc));
}

static void put_txxx(fa_tw_buf* b, int ver, const char* key, const char* value)
{
    size_t kl = strlen(key), vl = strlen(value);
    uint32_t size = (uint32_t)(1 + kl + 1 + vl);
    fa_tw_put(b, "TXXX", 4);
    if (ver == 4) put_synchsafe(b, size);
    else fa_tw_be32(b, size);
    fa_tw_u8(b, 0);
    fa_tw_u8(b, 0);
    fa_tw_u8(b, 0);                                      /* ISO-8859-1 */
    fa_tw_put(b, key, kl);
    fa_tw_u8(b, 0);
    fa_tw_put(b, value, vl);
}

fa_result fa_tw_plan_id3v2(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan)
{
    uint8_t h[10];
    uint8_t* body = NULL;
    size_t n = 0, pos = 0;
    int64_t old_total = 0;
    uint32_t old_size = 0, size, padding;
    int ver = 3, flags = 0, i;
    fa_tw_buf frames;
    fa_tw_edit* e;
    fa_result r = FA_OK;

    memset(&frames, 0, sizeof(frames));
    if (fa_source_seek(src, 0) == FA_OK && fa_source_read(src, h, 10) == 10 && memcmp(h, "ID3", 3) == 0 &&
        !((h[6] | h[7] | h[8] | h[9]) & 0x80)) {
        ver = h[3];
        flags = h[5];
        if (ver < 3 || ver > 4) return FA_ERROR_UNSUPPORTED_FORMAT;   /* ID3v2.2: too old to rewrite */
        old_size = fa_synchsafe(h + 6);
        old_total = 10 + (int64_t)old_size + ((flags & 0x10) ? 10 : 0);
        body = fa_tw_read(src, 10, old_size);
        if (!body) return FA_ERROR_UNSUPPORTED_FORMAT;
        n = old_size;
        if (ver == 3 && (flags & 0x80)) {                /* the whole tag unsynchronised: kept plain */
            n = unsync_undo(body, n);
            flags &= ~0x80;
        }
        if (flags & 0x40) {                              /* the extended header (its CRC would be stale): dropped */
            int64_t ext = n >= 4 ? (ver == 4 ? (int64_t)fa_synchsafe(body) : (int64_t)fa_be32(body) + 4) : -1;
            if (ext < 4 || ext > (int64_t)n) r = FA_ERROR_UNSUPPORTED_FORMAT;
            else pos = (size_t)ext;
            flags &= ~0x40;
        }
        while (r == FA_OK && pos + 10 <= n && body[pos] != 0) {
            int64_t fsize;
            if (!frame_id_ok(body + pos)) { r = FA_ERROR_UNSUPPORTED_FORMAT; break; }
            fsize = frame_size(body, pos, n, ver);
            if (fsize < 0 || pos + 10 + (size_t)fsize > n) { r = FA_ERROR_UNSUPPORTED_FORMAT; break; }
            if (!frame_replaced(set, body + pos, (size_t)fsize, ver)) fa_tw_put(&frames, body + pos, 10 + (size_t)fsize);
            pos += 10 + (size_t)fsize;
        }
        /* What follows must be padding: anything else is not understood, so not dropped. */
        for (; r == FA_OK && pos < n; pos++) if (body[pos] != 0) r = FA_ERROR_UNSUPPORTED_FORMAT;
        free(body);
        if (r != FA_OK) { fa_tw_free(&frames); return r; }
    }
    for (i = 0; i < set->count; i++) put_txxx(&frames, ver, set->items[i].key, set->items[i].value);
    if (frames.failed) { fa_tw_free(&frames); return FA_ERROR_OUT_OF_MEMORY; }

    /* Keep the tag's size when the frames fit (padding), else leave room for later edits. */
    if (flags & 0x10) padding = 0;                      /* no padding with a footer (ID3v2.4) */
    else if (old_total && frames.n <= old_size) padding = old_size - (uint32_t)frames.n;
    else padding = ID3_PADDING;
    size = (uint32_t)frames.n + padding;
    if (frames.n > 0x0FFFFFFFu - ID3_PADDING) { fa_tw_free(&frames); return FA_ERROR_UNSUPPORTED_FORMAT; }

    e = fa_tw_add(plan, 0, old_total);
    fa_tw_put(&e->data, "ID3", 3);
    fa_tw_u8(&e->data, (uint32_t)ver);
    fa_tw_u8(&e->data, 0);
    fa_tw_u8(&e->data, (uint32_t)flags);
    put_synchsafe(&e->data, size);
    fa_tw_put(&e->data, frames.p, frames.n);
    fa_tw_free(&frames);
    fa_tw_zeros(&e->data, padding);
    if (flags & 0x10) {
        fa_tw_put(&e->data, "3DI", 3);
        fa_tw_u8(&e->data, (uint32_t)ver);
        fa_tw_u8(&e->data, 0);
        fa_tw_u8(&e->data, (uint32_t)flags);
        put_synchsafe(&e->data, size);
    }
    if (e->data.failed) return FA_ERROR_OUT_OF_MEMORY;
    plan->format = FA_TAG_FORMAT_ID3V2;
    /* An APEv2 tag at the end (foobar2000 writes one to MP3s) gets the same values. */
    return fa_tw_plan_ape(src, set, plan, 0);
}

/* ---- APEv2 ----------------------------------------------------------------------------- */

static void ape_header(fa_tw_buf* b, uint32_t size, uint32_t count, uint32_t flags)
{
    static const uint8_t zero[8] = { 0 };
    fa_tw_put(b, "APETAGEX", 8);
    fa_tw_le32(b, 2000);
    fa_tw_le32(b, size);
    fa_tw_le32(b, count);
    fa_tw_le32(b, flags);
    fa_tw_put(b, zero, 8);
}

fa_result fa_tw_plan_ape(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan, int create)
{
    int64_t v1, ape, l3, l3_end, len = src->length, end, start;
    fa_tw_buf items;
    fa_tw_edit* e;
    uint32_t count = 0, i;
    fa_tags_tail_sizes(src, &v1, &ape, &l3, &l3_end);
    end = len - v1 - (l3 && l3_end == len - v1 ? l3 : 0);   /* where the reader looks for the APE footer */
    start = end - ape;
    if (!ape && !create) return FA_OK;
    if (start < 0 || (plan->count && start < plan->edits[plan->count - 1].to)) return FA_ERROR_UNSUPPORTED_FORMAT;
    memset(&items, 0, sizeof(items));
    if (ape) {
        uint8_t f[32];
        uint32_t size, n, pos = 0, total;
        uint8_t* p;
        if (fa_source_seek(src, end - 32) != FA_OK || fa_source_read(src, f, 32) != 32) return FA_ERROR_IO;
        size = fa_le32(f + 12);
        total = fa_le32(f + 16);
        if (size < 32 || size > APE_MAX_TAG) return FA_ERROR_UNSUPPORTED_FORMAT;
        n = size - 32;
        p = fa_tw_read(src, end - size, n);
        if (!p) return FA_ERROR_UNSUPPORTED_FORMAT;
        for (i = 0; i < total; i++) {
            uint32_t vlen, k = 0;
            if (n - pos < 9) break;
            vlen = fa_le32(p + pos);
            while (pos + 8 + k < n && p[pos + 8 + k]) k++;
            if (pos + 8 + k >= n || vlen > n - (pos + 8 + k + 1)) break;
            if (!fa_tw_replaces(set, (const char*)p + pos + 8, k)) {
                fa_tw_put(&items, p + pos, 8 + k + 1 + vlen);
                count++;
            }
            pos += 8 + k + 1 + vlen;
        }
        free(p);
        if (i < total) { fa_tw_free(&items); return FA_ERROR_UNSUPPORTED_FORMAT; }
    }
    for (i = 0; i < (uint32_t)set->count; i++) {
        size_t kl = strlen(set->items[i].key), vl = strlen(set->items[i].value);
        fa_tw_le32(&items, (uint32_t)vl);
        fa_tw_le32(&items, 0);                           /* UTF-8 text */
        fa_tw_put(&items, set->items[i].key, kl + 1);
        fa_tw_put(&items, set->items[i].value, vl);
        count++;
    }
    e = fa_tw_add(plan, start, end);
    if (!e) { fa_tw_free(&items); return FA_ERROR_GENERIC; }
    ape_header(&e->data, (uint32_t)items.n + 32, count, 0xA0000000u);   /* has a header; this is it */
    fa_tw_put(&e->data, items.p, items.n);
    ape_header(&e->data, (uint32_t)items.n + 32, count, 0x80000000u);
    if (items.failed) e->data.failed = 1;
    fa_tw_free(&items);
    if (e->data.failed) return FA_ERROR_OUT_OF_MEMORY;
    if (create) plan->format = FA_TAG_FORMAT_APE;
    return FA_OK;
}

/* ---- FLAC ------------------------------------------------------------------------------ */

typedef struct flac_block {
    int type;
    uint32_t len;
    int64_t pos;                                         /* the body */
} flac_block;

static void flac_block_header(fa_tw_buf* b, int last, int type, uint32_t len)
{
    fa_tw_u8(b, (uint32_t)((last ? 0x80 : 0) | type));
    fa_tw_u8(b, len >> 16);
    fa_tw_u8(b, len >> 8);
    fa_tw_u8(b, len);
}

fa_result fa_tw_plan_flac(fa_source* src, const fa_tw_set* set, fa_tw_plan* plan)
{
    uint8_t h[10];
    int64_t start = 0, pos, old_total;
    flac_block* blocks;
    int nb = 0, k, have_pad = 0, vc_done = 0;
    fa_tw_buf vc, out;
    fa_tw_edit* e;
    fa_result r = FA_OK;
    int64_t new_total = 0, pad_len;

    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, h, 10) != 10) return FA_ERROR_IO;
    if (memcmp(h, "ID3", 3) == 0) start = 10 + (int64_t)fa_synchsafe(h + 6) + ((h[5] & 0x10) ? 10 : 0);
    if (fa_source_seek(src, start) != FA_OK || fa_source_read(src, h, 4) != 4 || memcmp(h, "fLaC", 4) != 0)
        return FA_ERROR_UNSUPPORTED_FORMAT;
    blocks = (flac_block*)malloc(FLAC_MAX_BLOCKS * sizeof(*blocks));
    if (!blocks) return FA_ERROR_OUT_OF_MEMORY;
    pos = start + 4;
    for (;;) {
        int last;
        if (nb == FLAC_MAX_BLOCKS || fa_source_seek(src, pos) != FA_OK || fa_source_read(src, h, 4) != 4) {
            r = FA_ERROR_UNSUPPORTED_FORMAT;
            break;
        }
        last = h[0] & 0x80;
        blocks[nb].type = h[0] & 0x7f;
        blocks[nb].len = fa_be24(h + 1);
        blocks[nb].pos = pos + 4;
        pos += 4 + (int64_t)blocks[nb].len;
        if (blocks[nb].type == 127 || pos > src->length || (nb == 0 && blocks[nb].type != 0)) {
            r = FA_ERROR_UNSUPPORTED_FORMAT;
            break;
        }
        nb++;
        if (last) break;
    }
    old_total = pos - (start + 4);
    memset(&vc, 0, sizeof(vc));
    memset(&out, 0, sizeof(out));
    for (k = 0; k < nb && r == FA_OK; k++) {
        /* A second comment block (not allowed, but seen): its items would be lost, so refused. */
        if (blocks[k].type == 4 && vc.n) { r = FA_ERROR_UNSUPPORTED_FORMAT; break; }
        if (blocks[k].type == 4) {
            uint8_t* p = fa_tw_read(src, blocks[k].pos, blocks[k].len);
            if (!p) r = FA_ERROR_IO;
            else r = fa_tw_vorbis_comments(p, blocks[k].len, set, &vc, NULL);
            free(p);
        }
        if (blocks[k].type == 1) have_pad = 1;
    }
    if (r == FA_OK && !vc.n) r = fa_tw_vorbis_comments(NULL, 0, set, &vc, NULL);
    if (r == FA_OK && vc.n > 0xFFFFFF) r = FA_ERROR_UNSUPPORTED_FORMAT;

    /* The blocks in their order, the comments in place of the old ones (or after
     * STREAMINFO), padding last: resized so the audio stays where it was if it can. */
    for (k = 0; k < nb && r == FA_OK; k++) {
        if (blocks[k].type == 1) continue;
        if (blocks[k].type == 4) {
            flac_block_header(&out, 0, 4, (uint32_t)vc.n);
            fa_tw_put(&out, vc.p, vc.n);
            vc_done = 1;
        } else {
            uint8_t* p = fa_tw_read(src, blocks[k].pos, blocks[k].len);
            if (!p) { r = FA_ERROR_IO; break; }
            flac_block_header(&out, 0, blocks[k].type, blocks[k].len);
            fa_tw_put(&out, p, blocks[k].len);
            free(p);
        }
        if (k == 0 && !vc_done) {
            int any = 0, m;
            for (m = 1; m < nb; m++) if (blocks[m].type == 4) any = 1;
            if (!any) {
                flac_block_header(&out, 0, 4, (uint32_t)vc.n);
                fa_tw_put(&out, vc.p, vc.n);
                vc_done = 1;
            }
        }
    }
    fa_tw_free(&vc);
    free(blocks);
    if (r == FA_OK && out.failed) r = FA_ERROR_OUT_OF_MEMORY;
    if (r != FA_OK) { fa_tw_free(&out); return r; }

    new_total = (int64_t)out.n;
    pad_len = old_total - new_total - 4;
    if (pad_len < 0 || pad_len > 0xFFFFFF) pad_len = have_pad ? FLAC_PADDING : -1;
    if (pad_len >= 0) {
        flac_block_header(&out, 1, 1, (uint32_t)pad_len);
        fa_tw_zeros(&out, (size_t)pad_len);
    } else {
        /* No padding: the last block written gets the flag. Find its header. */
        size_t at = 0, last_at = 0;
        while (at + 4 <= out.n) { last_at = at; at += 4 + fa_be24(out.p + at + 1); }
        out.p[last_at] |= 0x80;
    }
    if (out.failed) { fa_tw_free(&out); return FA_ERROR_OUT_OF_MEMORY; }
    e = fa_tw_add(plan, start + 4, start + 4 + old_total);
    e->data = out;
    plan->format = FA_TAG_FORMAT_VORBIS;
    return FA_OK;
}
