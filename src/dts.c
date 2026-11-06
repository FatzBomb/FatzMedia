/* DTS (ETSI TS 102 114) through the system's decoders.
 *
 * FatzAudio has no DTS decoder of its own: the specification leaves out the two
 * code books every real stream needs (D.10.1 ADPCM coefficients, D.10.2 high
 * frequency vectors: "Due to its extensive size, this table is not included
 * here", in every edition), and the only copies are in other decoders' source.
 * What is here is written from the headers the specification does give:
 *
 * - the core frame header (5.4.2: SYNC, FTYPE, SHORT, CPF, NBLKS, FSIZE, AMODE,
 *   SFREQ, ... LFF) and the extension substream header (7.5.2: SYNCEXTSSH ...
 *   nuExtSSFsize), which say how long a frame is, its rate, channels and samples;
 * - the packet codec "dts": whatever bytes a container hands over are cut into
 *   whole frames (a core frame and the extension substreams right after it, the
 *   DTS-HD parts) and given to the system's decoder (fa_system_codec_open: Media
 *   Foundation on Windows, MediaCodec on Android) where the machine has one. One
 *   that takes DTS-HD is asked for first ("dtshd"), else one that takes the core
 *   ("dts"), which gets the core frames alone. Without either, unsupported;
 * - the raw file reader (.dts, .cpt): core frames in 16-bit words, either byte
 *   order, with or without extension substreams.
 * 14-bit packed streams (DTS audio CDs) and substream-only streams (DTS Express)
 * are not recognised. */
#include "fa_internal.h"
#include "demux.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DTS_RESYNC 65536            /* junk bytes skipped looking for a frame */
#define DTS_MAX_UNIT (1u << 20)     /* bytes of a core frame and its substreams, at most */
#define DTS_MAX_INDEX (1 << 24)     /* index limit: about 50 hours at 10.7 ms per frame */
#define DTS_CBR_CHECKS 32

static const uint32_t dts_rates[16] = { 0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 0, 0 };
/* Channels of AMODE 0..15 (Table 5-4); 16 and up are user defined. */
static const uint8_t dts_amode_channels[16] = { 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 6, 7, 8, 8 };

static int is_core_sync(const uint8_t* d)
{
    return (d[0] == 0x7F && d[1] == 0xFE && d[2] == 0x80 && d[3] == 0x01) ||
           (d[0] == 0xFE && d[1] == 0x7F && d[2] == 0x01 && d[3] == 0x80);
}

static int is_substream_sync(const uint8_t* d)
{
    return d[0] == 0x64 && d[1] == 0x58 && d[2] == 0x20 && d[3] == 0x25;
}

int fa_dts_parse_core(const uint8_t* d, size_t n, fa_dts_header* out)
{
    uint8_t h[FA_DTS_HEADER_BYTES];
    uint32_t nblks, fsize, amode, sfreq, lff;
    size_t i;
    if (n < FA_DTS_HEADER_BYTES) return 0;
    memset(out, 0, sizeof(*out));
    if (d[0] == 0x7F && d[1] == 0xFE && d[2] == 0x80 && d[3] == 0x01) memcpy(h, d, sizeof(h));
    else if (d[0] == 0xFE && d[1] == 0x7F && d[2] == 0x01 && d[3] == 0x80) {
        for (i = 0; i < sizeof(h); i += 2) { h[i] = d[i + 1]; h[i + 1] = d[i]; }   /* byte-swapped words */
        out->swapped = 1;
    } else {
        return 0;
    }
    nblks = ((uint32_t)(h[4] & 0x01) << 6) | (h[5] >> 2);
    fsize = ((uint32_t)(h[5] & 0x03) << 12) | ((uint32_t)h[6] << 4) | (h[7] >> 4);
    amode = ((uint32_t)(h[7] & 0x0F) << 2) | (h[8] >> 6);
    sfreq = (h[8] >> 2) & 0x0F;
    lff = (h[10] >> 1) & 0x03;
    /* NBLKS 5..127 and FSIZE 95..16383 are the valid ranges; LFF 3 is invalid. */
    if (nblks < 5 || fsize < 95 || amode > 15 || dts_rates[sfreq] == 0 || lff == 3) return 0;
    out->rate = dts_rates[sfreq];
    out->channels = (uint32_t)dts_amode_channels[amode] + (lff != 0 ? 1u : 0u);
    out->samples = (nblks + 1) * 32;
    out->size = fsize + 1;
    return 1;
}

/* An extension substream header at d (n >= 10): its frame size in bytes, 0 if none. */
static size_t substream_size(const uint8_t* d, size_t n)
{
    uint64_t bits = 0;
    unsigned hdr_bits, size_bits, used, k;
    size_t hdr, size;
    if (n < 10 || !is_substream_sync(d)) return 0;
    /* After the sync word and UserDefinedBits: nExtSSIndex (2), bHeaderSizeType (1),
     * nuExtSSHeaderSize (8 or 12) and nuExtSSFsize (16 or 20), all minus one. */
    for (k = 5; k < 10; k++) bits = (bits << 8) | d[k];
    used = 40;
    hdr_bits = (bits >> (used - 3)) & 1 ? 12 : 8;
    size_bits = hdr_bits == 12 ? 20 : 16;
    used -= 3;
    hdr = (size_t)((bits >> (used - hdr_bits)) & ((1u << hdr_bits) - 1)) + 1;
    used -= hdr_bits;
    size = (size_t)((bits >> (used - size_bits)) & ((1u << size_bits) - 1)) + 1;
    if (hdr < 10 || size < hdr) return 0;
    return size;
}

int fa_dts_unit(const uint8_t* d, size_t n, int end, fa_dts_header* h, size_t* len)
{
    size_t at;
    if (n < 4) { *len = 4; return end ? 0 : -1; }
    if (!is_core_sync(d)) return 0;
    if (n < FA_DTS_HEADER_BYTES) { *len = FA_DTS_HEADER_BYTES; return end ? 0 : -1; }
    if (!fa_dts_parse_core(d, n, h)) return 0;
    if (n < h->size) { *len = h->size; return -1; }
    at = h->size;
    /* The extension substreams that follow (DTS-HD); byte-swapped streams have none. */
    while (!h->swapped) {
        size_t ss;
        if (n < at + 4) { if (end) break; *len = at + 4; return -1; }
        if (!is_substream_sync(d + at)) break;
        if (n < at + 10) { if (end) break; *len = at + 10; return -1; }
        ss = substream_size(d + at, n - at);
        if (ss == 0 || at + ss > DTS_MAX_UNIT) break;
        if (n < at + ss) { if (end) break; *len = at + ss; return -1; }
        at += ss;
    }
    *len = at;
    return 1;
}

int64_t fa_dts_packet_samples(const uint8_t* d, size_t n, uint32_t* out_rate)
{
    size_t pos = 0;
    int64_t samples = 0;
    while (pos + FA_DTS_HEADER_BYTES <= n) {
        fa_dts_header h;
        if (!fa_dts_parse_core(d + pos, n - pos, &h)) break;
        samples += h.samples;
        *out_rate = h.rate;
        pos += h.size;
        while (pos + 10 <= n) {
            size_t ss = substream_size(d + pos, n - pos);
            if (ss == 0) break;
            pos += ss;
        }
    }
    return samples > 0 ? samples : -1;
}

/* ---- the packet codec ------------------------------------------------------------- */

typedef struct dts_codec {
    fa_codec inner;          /* the system's decoder */
    int core_only;           /* it takes core frames only: the substreams are cut off */
    uint8_t* buf;            /* bytes not yet handed over */
    size_t len, cap;
    uint8_t* swap;           /* a byte-swapped frame put in big-endian order */
    size_t swap_cap;
} dts_codec;

static fa_result grow(uint8_t** p, size_t* cap, size_t need)
{
    size_t c;
    uint8_t* q;
    if (need <= *cap) return FA_OK;
    c = *cap ? *cap : 16384;
    while (c < need) c *= 2;
    q = (uint8_t*)realloc(*p, c);
    if (!q) return FA_ERROR_OUT_OF_MEMORY;
    *p = q;
    *cap = c;
    return FA_OK;
}

/* Appends what the system's decoder put out to c->out. A change of format drops the
 * audio of the same call before it (the track decoder takes one format per call). */
static fa_result take_output(fa_codec* c, dts_codec* st)
{
    fa_codec* in = &st->inner;
    fa_result r;
    if (in->out_frames == 0) return FA_OK;
    if (c->out_frames > 0 && (in->channels != c->channels || in->rate != c->rate)) c->out_frames = 0;
    c->rate = in->rate;
    c->channels = in->channels;
    r = fa_codec_reserve(c, in->out_frames, in->channels);
    if (r != FA_OK) return r;
    memcpy(c->out + (size_t)c->out_frames * c->channels, in->out, (size_t)in->out_frames * in->channels * sizeof(float));
    c->out_frames += in->out_frames;
    return FA_OK;
}

/* Hands every whole frame in st->buf to the system's decoder. `end`: no bytes follow. */
static fa_result feed_units(fa_codec* c, dts_codec* st, int end)
{
    size_t pos = 0;
    fa_result r = FA_OK;
    while (pos < st->len) {
        fa_dts_header h;
        size_t whole = 0, give;
        const uint8_t* u = st->buf + pos;
        int k = fa_dts_unit(u, st->len - pos, end, &h, &whole);
        if (k < 0 && !end) {
            /* The packet ends right after a whole core frame or substream: the frame is
             * complete (containers carry a frame and its substreams in one packet), so it
             * goes now, not after the next packet. A packet cut inside one waits. */
            fa_dts_header h2;
            size_t l2 = 0;
            if (fa_dts_unit(u, st->len - pos, 1, &h2, &l2) == 1 && l2 == st->len - pos) {
                k = 1;
                h = h2;
                whole = l2;
            }
        }
        if (k < 0) break;
        if (k == 0) { pos++; continue; }
        give = st->core_only ? h.size : whole;
        if (h.swapped) {
            /* 16-bit words in little-endian order: the decoder gets them big-endian.
             * FSIZE+1 is even in such streams; an odd last byte goes in as it is. */
            size_t i;
            r = grow(&st->swap, &st->swap_cap, h.size);
            if (r != FA_OK) break;
            for (i = 0; i + 1 < h.size; i += 2) { st->swap[i] = u[i + 1]; st->swap[i + 1] = u[i]; }
            if (h.size & 1) st->swap[h.size - 1] = u[h.size - 1];
            u = st->swap;
            give = h.size;
        }
        r = st->inner.vt->decode(&st->inner, u, give);
        if (r == FA_OK) r = take_output(c, st);
        pos += whole;
        if (r != FA_OK) break;
    }
    if (pos > 0) {
        memmove(st->buf, st->buf + pos, st->len - pos);
        st->len -= pos;
    }
    if (st->len > DTS_MAX_UNIT + DTS_RESYNC) st->len = 0;   /* junk that never makes a frame */
    return r;
}

static fa_result dtsc_decode(fa_codec* c, const uint8_t* data, size_t size)
{
    dts_codec* st = (dts_codec*)c->impl;
    fa_result r;
    c->out_frames = 0;
    if (!data && size == 0) {
        /* The end: the frame still held, then what the system's decoder holds. */
        r = feed_units(c, st, 1);
        st->len = 0;
        if (r != FA_OK) return r;
        r = st->inner.vt->decode(&st->inner, NULL, 0);
        if (r != FA_OK) return r;
        return take_output(c, st);
    }
    if (size == 0) return FA_OK;
    r = grow(&st->buf, &st->cap, st->len + size);
    if (r != FA_OK) return r;
    memcpy(st->buf + st->len, data, size);
    st->len += size;
    return feed_units(c, st, 0);
}

static void dtsc_flush(fa_codec* c)
{
    dts_codec* st = (dts_codec*)c->impl;
    st->len = 0;
    if (st->inner.vt->flush) st->inner.vt->flush(&st->inner);
}

static void dtsc_close(fa_codec* c)
{
    dts_codec* st = (dts_codec*)c->impl;
    if (!st) return;
    fa_codec_close(&st->inner);
    free(st->buf);
    free(st->swap);
    free(st);
    c->impl = NULL;
}

static const fa_codec_vtable dtsc_vtable = { "dts", dtsc_decode, dtsc_flush, dtsc_close };

fa_result fa_dts_codec_open(fa_codec* c, const fa_codec_params* p)
{
    dts_codec* st = (dts_codec*)calloc(1, sizeof(*st));
    fa_codec_params q = *p;
    fa_result r;
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    q.codec = "dtshd";
    r = fa_system_codec_open(&st->inner, &q);
    if (r == FA_ERROR_UNSUPPORTED_FORMAT) {
        memset(&st->inner, 0, sizeof(st->inner));
        q.codec = "dts";
        r = fa_system_codec_open(&st->inner, &q);
        st->core_only = 1;
    }
    if (r != FA_OK) {
        free(st);
        return r;
    }
    c->impl = st;
    c->vt = &dtsc_vtable;
    c->trails = st->inner.trails;
    c->rate = st->inner.rate ? st->inner.rate : p->sample_rate;
    c->channels = st->inner.channels ? st->inner.channels : p->channels;
    return FA_OK;
}

/* ---- the raw file reader ---------------------------------------------------------- */

typedef struct dts_unit_pos {
    int64_t off;
    int64_t pts;
} dts_unit_pos;

typedef struct dts_raw {
    fa_br br;
    fa_track track;
    uint8_t* data;           /* bytes read ahead: the next unit starts here */
    size_t data_len, data_cap;
    uint8_t* pkt;            /* the unit handed out */
    size_t pkt_cap;
    int64_t data_start;
    int64_t pts;             /* of the next unit, in samples at `rate` */
    uint32_t rate;
    int in_flow;             /* the next byte is where the last unit ended */
    dts_unit_pos* index;
    size_t nindex;
    int64_t cbr;             /* every unit this size, `cbr_samples` long (no index needed) */
    int64_t cbr_units;
    uint32_t cbr_samples;
    double bytes_per_sample;
    int64_t est_samples;
} dts_raw;

/* Units chained from d[i]: `want` of them, each where the previous ends (a unit
 * reaching the end of the window counts). */
static int chain(const uint8_t* d, size_t n, size_t i, int want)
{
    int k;
    for (k = 0; k < want; k++) {
        fa_dts_header h;
        size_t len = 0;
        int r;
        if (i + 4 > n) return k > 0;
        r = fa_dts_unit(d + i, n - i, 0, &h, &len);
        if (r < 0) return k > 0;
        if (r == 0) return 0;
        i += len;
    }
    return 1;
}

static int dts_probe(const uint8_t* d, size_t n, size_t id3)
{
    size_t i;
    if (id3 >= n) return 0;
    for (i = id3; i + FA_DTS_HEADER_BYTES <= n && i < id3 + 4096; i++) {
        fa_dts_header h;
        if (!is_core_sync(d + i) || !fa_dts_parse_core(d + i, n - i, &h)) continue;
        if (i + h.size + FA_DTS_HEADER_BYTES <= n) {
            if (!chain(d, n, i, 3)) continue;
            return i == id3 ? 95 : 70;
        }
        if (i == id3) return 40;   /* one frame fills the probe: the open checks the next */
    }
    return 0;
}

/* Makes a->data hold `n` bytes ahead (fewer at the end of the source). */
static fa_result fill(dts_raw* a, size_t n)
{
    fa_result r;
    if (a->data_len >= n) return FA_OK;
    r = grow(&a->data, &a->data_cap, n);
    if (r != FA_OK) return r;
    a->data_len += fa_br_read(&a->br, a->data + a->data_len, n - a->data_len);
    return FA_OK;
}

/* Finds the next unit: it starts a->data, `len` bytes long. Right after a unit any
 * header is taken; after junk, a seek or at the start it must be followed by another
 * unit or by the end of the source. */
static fa_result read_unit(dts_raw* a, fa_dts_header* h, size_t* len)
{
    size_t skipped = 0, want = 16;
    fa_result r;
    for (;;) {
        int k, end;
        r = fill(a, want);
        if (r != FA_OK) return r;
        end = a->data_len < want;
        if (a->data_len < 4) return FA_ERROR_END_OF_STREAM;
        k = fa_dts_unit(a->data, a->data_len, end, h, len);
        if (k < 0) {
            if (end) return FA_ERROR_END_OF_STREAM;   /* cut short */
            want = *len;
            continue;
        }
        if (k == 1 && (!a->in_flow || skipped > 0)) {
            fa_dts_header h2;
            r = fill(a, *len + FA_DTS_HEADER_BYTES);
            if (r != FA_OK) return r;
            if (a->data_len >= *len + FA_DTS_HEADER_BYTES && !fa_dts_parse_core(a->data + *len, a->data_len - *len, &h2))
                k = 0;
        }
        if (k == 1) {
            a->in_flow = 1;
            return FA_OK;
        }
        if (++skipped > DTS_RESYNC) return FA_ERROR_END_OF_STREAM;
        a->in_flow = 0;
        memmove(a->data, a->data + 1, a->data_len - 1);
        a->data_len--;
        want = 16;
    }
}

static fa_result raw_next(void* ctx, fa_demux_packet* out)
{
    dts_raw* a = (dts_raw*)ctx;
    fa_dts_header h;
    size_t len = 0;
    fa_result r = read_unit(a, &h, &len);
    if (r != FA_OK) return r;
    r = grow(&a->pkt, &a->pkt_cap, len);
    if (r != FA_OK) return r;
    memcpy(a->pkt, a->data, len);
    memmove(a->data, a->data + len, a->data_len - len);
    a->data_len -= len;
    out->data = a->pkt;
    out->size = len;
    out->pts = a->pts;
    out->duration = h.rate == a->rate ? (int64_t)h.samples : (int64_t)h.samples * a->rate / h.rate;
    a->pts += out->duration;
    return FA_OK;
}

static fa_result raw_seek_packets(void* ctx, int64_t t, int packets_back, size_t bytes_back)
{
    dts_raw* a = (dts_raw*)ctx;
    int64_t off, pts;
    fa_result r;
    (void)bytes_back;
    if (a->index) {
        size_t lo = 0, hi = a->nindex;
        while (hi - lo > 1) {
            size_t mid = lo + (hi - lo) / 2;
            if (a->index[mid].pts <= t) lo = mid;
            else hi = mid;
        }
        lo = lo >= (size_t)packets_back ? lo - (size_t)packets_back : 0;
        off = a->index[lo].off;
        pts = a->index[lo].pts;
    } else if (a->cbr) {
        int64_t u = t > 0 ? t / a->cbr_samples : 0;
        if (u >= a->cbr_units) u = a->cbr_units - 1;
        u = u >= packets_back ? u - packets_back : 0;
        off = a->data_start + u * a->cbr;
        pts = u * a->cbr_samples;
    } else {
        if (!a->br.src->seekable || a->bytes_per_sample <= 0) return FA_ERROR_NOT_SEEKABLE;
        pts = t - (int64_t)packets_back * 4096;
        if (pts < 0) pts = 0;
        off = a->data_start + (int64_t)((double)pts * a->bytes_per_sample);
        if (a->br.src->length > 0 && off >= a->br.src->length) off = a->br.src->length - 1;
    }
    r = fa_br_seek(&a->br, off);
    if (r != FA_OK) return r;
    a->data_len = 0;
    a->pts = pts;
    a->in_flow = a->index != NULL || a->cbr;
    return FA_OK;
}

static const fa_demux_ops raw_ops = { raw_next, raw_seek_packets };

/* The unit header at `pos` of a local file: 1 and its whole size and header. */
static int unit_at(dts_raw* a, int64_t pos, fa_dts_header* h, size_t* len)
{
    uint8_t hb[FA_DTS_HEADER_BYTES], sb[10];
    size_t at;
    if (fa_br_seek(&a->br, pos) != FA_OK || fa_br_read(&a->br, hb, sizeof(hb)) != sizeof(hb)) return 0;
    if (!fa_dts_parse_core(hb, sizeof(hb), h)) return 0;
    at = h->size;
    while (!h->swapped) {
        size_t ss;
        if (fa_br_seek(&a->br, pos + (int64_t)at) != FA_OK || fa_br_read(&a->br, sb, sizeof(sb)) != sizeof(sb)) break;
        ss = substream_size(sb, sizeof(sb));
        if (ss == 0 || at + ss > DTS_MAX_UNIT) break;
        at += ss;
    }
    *len = at;
    return 1;
}

/* Local files where every unit has the first one's size and length (nearly every
 * DTS core file): unit k is at data_start + k * size, no index to build. The second,
 * the last and 32 units spread in between are checked. */
static int check_cbr(dts_raw* a, const fa_dts_header* first, size_t first_len, int64_t* out_end)
{
    int64_t len = a->br.src->length, size = (int64_t)first_len, n;
    int i;
    if (len <= a->data_start || size <= 0 || (len - a->data_start) % size != 0) return 0;
    n = (len - a->data_start) / size;
    for (i = 0; i <= DTS_CBR_CHECKS + 1; i++) {
        fa_dts_header h;
        size_t ul;
        int64_t at = i == 0 ? 1 : i == DTS_CBR_CHECKS + 1 ? n - 1 : n * i / (DTS_CBR_CHECKS + 1);
        if (at >= n) at = n - 1;
        if (!unit_at(a, a->data_start + at * size, &h, &ul) || (int64_t)ul != size || h.rate != first->rate ||
            h.samples != first->samples)
            return 0;
    }
    a->cbr = size;
    a->cbr_units = n;
    a->cbr_samples = first->samples;
    a->bytes_per_sample = (double)size / (double)a->cbr_samples;
    *out_end = n * a->cbr_samples;
    return 1;
}

/* The next unit after junk at `pos` (within DTS_RESYNC bytes): one followed by
 * another unit or by the end of the file. -1 if there is none (tags at the end). */
static int64_t resync_at(dts_raw* a, int64_t pos)
{
    int64_t len = a->br.src->length;
    size_t n, i;
    uint8_t* w = (uint8_t*)malloc(DTS_RESYNC + FA_DTS_HEADER_BYTES);
    int64_t found = -1;
    if (!w) return -1;
    if (fa_br_seek(&a->br, pos) != FA_OK) { free(w); return -1; }
    n = fa_br_read(&a->br, w, DTS_RESYNC + FA_DTS_HEADER_BYTES);
    for (i = 1; i + FA_DTS_HEADER_BYTES <= n && found < 0; i++) {
        fa_dts_header h, h2;
        size_t ul;
        int64_t at = pos + (int64_t)i;
        if (!is_core_sync(w + i) || !fa_dts_parse_core(w + i, n - i, &h)) continue;
        if (!unit_at(a, at, &h, &ul)) continue;
        if ((len > 0 && at + (int64_t)ul == len) || unit_at(a, at + (int64_t)ul, &h2, &ul)) found = at;
    }
    free(w);
    return found;
}

/* Other local files: every unit's offset and time, hopping from header to header
 * (junk between units is slid over; tags or junk at the end stop it). */
static fa_result build_index(dts_raw* a, int64_t* out_end)
{
    size_t cap = 0;
    int64_t pos = a->data_start, pts = 0, len = a->br.src->length;
    for (;;) {
        fa_dts_header h;
        size_t ul;
        if (!unit_at(a, pos, &h, &ul)) {
            pos = resync_at(a, pos);
            if (pos < 0 || !unit_at(a, pos, &h, &ul)) break;
        }
        if (len > 0 && pos + (int64_t)ul > len) break;   /* cut short */
        if (a->nindex == cap) {
            dts_unit_pos* u;
            if (cap >= DTS_MAX_INDEX) {
                a->bytes_per_sample = (double)(pos - a->data_start) / (double)pts;
                free(a->index);
                a->index = NULL;
                a->nindex = 0;
                *out_end = FA_DEMUX_NO_PTS;
                return FA_OK;
            }
            cap = cap ? cap * 2 : 1024;
            u = (dts_unit_pos*)realloc(a->index, cap * sizeof(*u));
            if (!u) { free(a->index); a->index = NULL; a->nindex = 0; return FA_ERROR_OUT_OF_MEMORY; }
            a->index = u;
        }
        a->index[a->nindex].off = pos;
        a->index[a->nindex].pts = pts;
        a->nindex++;
        pts += h.rate == a->rate ? (int64_t)h.samples : (int64_t)h.samples * a->rate / h.rate;
        pos += (int64_t)ul;
    }
    *out_end = pts;
    if (a->nindex == 0) {
        free(a->index);
        a->index = NULL;
        return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    if (pts > 0) a->bytes_per_sample = (double)(pos - a->data_start) / (double)pts;
    return FA_OK;
}

static void raw_free(dts_raw* a)
{
    fa_br_free(&a->br);
    free(a->data);
    free(a->pkt);
    free(a->index);
    free(a);
}

static fa_result dts_open(fa_decoder* dec)
{
    fa_source* src = dec->src;
    dts_raw* a = (dts_raw*)calloc(1, sizeof(*a));
    fa_track_params tp;
    fa_dts_header h;
    size_t first_len = 0;
    int64_t end = FA_DEMUX_NO_PTS;
    fa_result r;
    if (!a) return FA_ERROR_OUT_OF_MEMORY;
    r = fa_br_init(&a->br, src);
    if (r != FA_OK) { free(a); return r; }
    /* The first unit, followed by another (or the end), sets the format. */
    r = read_unit(a, &h, &first_len);
    if (r != FA_OK) { r = r == FA_ERROR_OUT_OF_MEMORY ? r : FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    a->data_start = fa_br_tell(&a->br) - (int64_t)a->data_len;
    a->rate = h.rate;
    if (src->seekable && src->kind != FA_SOURCE_IO && !src->remote) {
        if (!check_cbr(a, &h, first_len, &end)) r = build_index(a, &end);
        if (r != FA_OK) goto fail;
    } else {
        a->bytes_per_sample = (double)first_len / (double)h.samples;   /* DTS is nearly always constant */
        if (src->length > a->data_start) a->est_samples = (int64_t)((double)(src->length - a->data_start) / a->bytes_per_sample);
    }
    if (src->seekable) {
        r = fa_br_seek(&a->br, a->data_start);
        if (r != FA_OK) goto fail;
        a->data_len = 0;
        a->in_flow = 1;
    }

    memset(&tp, 0, sizeof(tp));
    fa_codec_params_init(&tp.codec);
    tp.codec.codec = "dts";
    tp.codec.sample_rate = h.rate;
    tp.codec.channels = h.channels;
    tp.codec.bit_rate = (int64_t)((double)first_len * 8.0 * h.rate / h.samples + 0.5);
    tp.time_base = 1.0 / h.rate;
    tp.start = 0;
    tp.end = end != FA_DEMUX_NO_PTS ? end : a->est_samples > 0 ? a->est_samples : FA_DEMUX_NO_PTS;
    tp.clip_to_end = a->index != NULL || a->cbr;
    tp.exact_durations = 1;
    tp.last_pts = FA_DEMUX_NO_PTS;

    r = fa_track_open(&a->track, dec, &raw_ops, a, &tp);
    if (r != FA_OK) goto fail;
    dec->impl = a;
    dec->seekable = a->index != NULL || a->cbr || (src->seekable && a->bytes_per_sample > 0);
    if (!dec->bitrate && tp.codec.bit_rate > 0 && tp.codec.bit_rate < 0xFFFFFFFF) dec->bitrate = (uint32_t)tp.codec.bit_rate;
    snprintf(dec->container, sizeof(dec->container), "dts");
    return FA_OK;
fail:
    raw_free(a);
    return r;
}

static fa_result dts_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    return fa_track_read(&((dts_raw*)dec->impl)->track, out, frames, got);
}

static fa_result dts_seek(fa_decoder* dec, fa_frames frame)
{
    dts_raw* a = (dts_raw*)dec->impl;
    if (!dec->seekable) return FA_ERROR_NOT_SEEKABLE;
    return fa_track_seek(&a->track, frame);
}

static void dts_close(fa_decoder* dec)
{
    dts_raw* a = (dts_raw*)dec->impl;
    if (!a) return;
    fa_track_close(&a->track);
    raw_free(a);
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_dts = {
    "dts", "DTS (through the system's decoder)", "dts;cpt;dtshd",
    "audio/vnd.dts;audio/vnd.dts.hd;audio/x-dts",
    dts_probe, dts_open, dts_read, dts_seek, dts_close, 1, NULL, NULL
};
