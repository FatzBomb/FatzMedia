/* Loudness tags in Ogg Vorbis and Ogg Opus files (tags_write.h).
 *
 * The comment header is the second packet of the stream. It is rebuilt with the
 * new values and laid out on new pages together with the packets that shared its
 * pages (Vorbis: the setup header); the first page and the audio pages are kept
 * as they are, except that the stream's later page numbers move by the number of
 * pages the headers gained or lost (with a new checksum each). Anything outside
 * that shape (a multiplexed stream, audio on a header page) is left alone with
 * FA_ERROR_UNSUPPORTED_FORMAT. */
#include "tags_write.h"
#include <stdlib.h>
#include <string.h>

#define OGG_HDR 27
#define OGG_MAX_PAGE (OGG_HDR + 255 + 255 * 255)

typedef struct ogg_page {
    uint8_t* d;                       /* header, segment table and body: OGG_MAX_PAGE bytes */
    size_t size;
    int nseg;
    size_t body;                      /* where the body starts in d */
} ogg_page;

/* The page at `pos`: 1 = a page, 0 = none (the data ends or is not a page). */
static int read_page(fa_source* src, int64_t pos, ogg_page* pg)
{
    size_t blen = 0;
    int i;
    if (fa_source_seek(src, pos) != FA_OK || fa_source_read(src, pg->d, OGG_HDR) != OGG_HDR) return 0;
    if (memcmp(pg->d, "OggS", 4) != 0 || pg->d[4] != 0) return 0;
    pg->nseg = pg->d[26];
    if (fa_source_read(src, pg->d + OGG_HDR, (size_t)pg->nseg) != pg->nseg) return 0;
    for (i = 0; i < pg->nseg; i++) blen += pg->d[OGG_HDR + i];
    pg->body = OGG_HDR + (size_t)pg->nseg;
    if (fa_source_read(src, pg->d + pg->body, blen) != (int64_t)blen) return 0;
    pg->size = pg->body + blen;
    return 1;
}

static uint32_t page_serial(const ogg_page* pg) { return fa_le32(pg->d + 14); }
static uint32_t page_seq(const ogg_page* pg) { return fa_le32(pg->d + 18); }

static void page_set_seq(ogg_page* pg, uint32_t seq)
{
    uint32_t crc;
    int i;
    for (i = 0; i < 4; i++) pg->d[18 + i] = (uint8_t)(seq >> (8 * i));
    memset(pg->d + 22, 0, 4);
    crc = fa_ogg_crc(pg->d, pg->size);
    for (i = 0; i < 4; i++) pg->d[22 + i] = (uint8_t)(crc >> (8 * i));
}

int fa_tw_ogg_kind(fa_source* src)
{
    uint8_t h[OGG_HDR + 1 + 8];
    int64_t at;
    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, h, OGG_HDR) != OGG_HDR) return -1;
    if (memcmp(h, "OggS", 4) != 0 || !(h[5] & 0x02) || h[26] == 0) return -1;
    at = OGG_HDR + h[26];
    if (fa_source_seek(src, at) != FA_OK || fa_source_read(src, h, 8) != 8) return -1;
    if (memcmp(h, "OpusHead", 8) == 0) return 1;
    if (memcmp(h, "\x01vorbis", 7) == 0) return 0;
    return -1;
}

/* The header packets after the first page: `want` of them, from the pages that
 * follow it. *out_end is where the last header page ends, *out_pages how many there
 * were. Each packet is malloc'ed. */
static fa_result read_headers(fa_source* src, int64_t pos, uint32_t serial, int want, fa_tw_buf* packets,
                              int64_t* out_end, int* out_pages, ogg_page* pg)
{
    int have = 0, pages = 0;
    while (have < want) {
        int i;
        size_t at;
        if (!read_page(src, pos, pg)) return FA_ERROR_UNSUPPORTED_FORMAT;
        if (page_serial(pg) != serial || (pg->d[5] & 0x02)) return FA_ERROR_UNSUPPORTED_FORMAT;   /* multiplexed */
        if (pages == 0 && (pg->d[5] & 0x01)) return FA_ERROR_UNSUPPORTED_FORMAT;
        pages++;
        at = pg->body;
        for (i = 0; i < pg->nseg; i++) {
            uint8_t lace = pg->d[OGG_HDR + i];
            if (have == want) return FA_ERROR_UNSUPPORTED_FORMAT;   /* audio on a header page */
            fa_tw_put(&packets[have], pg->d + at, lace);
            at += lace;
            if (lace < 255) have++;
        }
        pos += (int64_t)pg->size;
        if (pages > 1000) return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    for (have = 0; have < want; have++) if (packets[have].failed) return FA_ERROR_OUT_OF_MEMORY;
    *out_end = pos;
    *out_pages = pages;
    return FA_OK;
}

/* The new comment packet: the old one's prefix, the new comments, and whatever
 * followed the comments (Vorbis' framing bit, Opus' binary data). */
static fa_result new_comments(const fa_tw_buf* old, int opus, const fa_tw_set* set, fa_tw_buf* out)
{
    size_t lead = opus ? 8 : 7, tail = 0;
    fa_result r;
    if (old->n < lead || memcmp(old->p, opus ? "OpusTags" : "\x03vorbis", lead) != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    fa_tw_put(out, old->p, lead);
    r = fa_tw_vorbis_comments(old->p + lead, old->n - lead, set, out, &tail);
    if (r != FA_OK) return r;
    if (lead + tail < old->n) fa_tw_put(out, old->p + lead + tail, old->n - lead - tail);
    else if (!opus) fa_tw_u8(out, 1);
    return out->failed ? FA_ERROR_OUT_OF_MEMORY : FA_OK;
}

/* Lays packets out on pages from sequence number `seq` and writes them. */
static fa_result write_header_pages(const fa_tw_buf* packets, int count, uint32_t serial, uint32_t seq, FILE* out,
                                    int* out_pages, ogg_page* pg)
{
    int k = 0, pages = 0;
    size_t off = 0;                               /* into packets[k] */
    int in_packet = 0;                            /* the next page continues a packet */
    while (k < count) {
        int nseg = 0, ends = 0, i;
        size_t blen = 0;
        uint8_t* d = pg->d;
        int64_t granule;
        int cont = in_packet;
        /* Segments until the page is full or the packets end. */
        while (k < count && nseg < 255) {
            size_t left = packets[k].n - off;
            size_t lace = left >= 255 ? 255 : left;
            memcpy(d + OGG_HDR + 255 + blen, packets[k].p + off, lace);   /* body staged after a full table */
            d[OGG_HDR + nseg++] = (uint8_t)lace;
            blen += lace;
            off += lace;
            if (lace < 255) { k++; off = 0; ends = 1; in_packet = 0; }
            else in_packet = 1;
        }
        memmove(d + OGG_HDR + nseg, d + OGG_HDR + 255, blen);
        memcpy(d, "OggS", 4);
        d[4] = 0;
        d[5] = (uint8_t)(cont ? 0x01 : 0x00);
        granule = ends ? 0 : -1;
        for (i = 0; i < 8; i++) d[6 + i] = (uint8_t)((uint64_t)granule >> (8 * i));
        for (i = 0; i < 4; i++) d[14 + i] = (uint8_t)(serial >> (8 * i));
        d[26] = (uint8_t)nseg;
        pg->size = OGG_HDR + (size_t)nseg + blen;
        page_set_seq(pg, seq + (uint32_t)pages);
        if (fwrite(d, 1, pg->size, out) != pg->size) return FA_ERROR_IO;
        pages++;
    }
    *out_pages = pages;
    return FA_OK;
}

fa_result fa_tw_write_ogg(fa_source* src, const fa_tw_set* set, int opus, FILE* out)
{
    ogg_page pg;
    fa_tw_buf packets[2], fresh[2];
    int want = opus ? 1 : 2, old_pages = 0, new_pages = 0, i, renumber;
    uint32_t serial;
    int64_t pos, end = 0;
    fa_result r;

    memset(packets, 0, sizeof(packets));
    memset(fresh, 0, sizeof(fresh));
    pg.d = (uint8_t*)malloc(OGG_MAX_PAGE + 255);
    if (!pg.d) return FA_ERROR_OUT_OF_MEMORY;

    /* The first page: the identification header alone. */
    r = read_page(src, 0, &pg) && pg.nseg > 0 && pg.d[OGG_HDR + pg.nseg - 1] < 255 ? FA_OK : FA_ERROR_UNSUPPORTED_FORMAT;
    for (i = 0; r == FA_OK && i + 1 < pg.nseg; i++) if (pg.d[OGG_HDR + i] < 255) r = FA_ERROR_UNSUPPORTED_FORMAT;
    serial = page_serial(&pg);
    pos = (int64_t)pg.size;
    if (r == FA_OK && fwrite(pg.d, 1, pg.size, out) != pg.size) r = FA_ERROR_IO;
    if (r == FA_OK) r = read_headers(src, pos, serial, want, packets, &end, &old_pages, &pg);
    if (r == FA_OK) r = new_comments(&packets[0], opus, set, &fresh[0]);
    if (r == FA_OK && !opus) fa_tw_put(&fresh[1], packets[1].p, packets[1].n);
    if (r == FA_OK && fresh[1].failed) r = FA_ERROR_OUT_OF_MEMORY;
    if (r == FA_OK) r = write_header_pages(fresh, want, serial, 1, out, &new_pages, &pg);

    /* The rest of the stream, its page numbers moved; a new stream (chained) and
     * everything after it are copied as they are. */
    renumber = new_pages != old_pages;
    while (r == FA_OK && pos < src->length) {
        if (pos < end) { pos = end; continue; }
        if (!read_page(src, pos, &pg)) {
            /* Not a page: bytes after the stream. Kept as they are when nothing moves. */
            if (renumber) { r = FA_ERROR_UNSUPPORTED_FORMAT; break; }
            break;
        }
        pos += (int64_t)pg.size;
        if (pg.d[5] & 0x02) renumber = 0;
        if (renumber && page_serial(&pg) == serial) page_set_seq(&pg, page_seq(&pg) + (uint32_t)(new_pages - old_pages));
        if (fwrite(pg.d, 1, pg.size, out) != pg.size) r = FA_ERROR_IO;
    }
    if (r == FA_OK && pos < src->length) {
        /* Trailing bytes that are not pages, copied as they are. */
        while (r == FA_OK && pos < src->length) {
            size_t want_n = src->length - pos < OGG_MAX_PAGE ? (size_t)(src->length - pos) : OGG_MAX_PAGE;
            if (fa_source_seek(src, pos) != FA_OK || fa_source_read(src, pg.d, want_n) != (int64_t)want_n) r = FA_ERROR_IO;
            else if (fwrite(pg.d, 1, want_n, out) != want_n) r = FA_ERROR_IO;
            pos += (int64_t)want_n;
        }
    }
    for (i = 0; i < 2; i++) { fa_tw_free(&packets[i]); fa_tw_free(&fresh[i]); }
    free(pg.d);
    return r;
}
