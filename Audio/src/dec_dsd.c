/* DSD audio files, written from the public format descriptions, decoded to PCM by dsd.c:
 *
 *   DSF     Sony's "DSF File Format Specification" 1.01: a "DSD " chunk (file size and
 *           where an ID3v2 tag is), "fmt " (channel type and count, rate, bit order,
 *           samples per channel, block size), then "data": blocks of 4096 bytes of one
 *           channel in turn, the oldest bit of a byte its lowest.
 *   DSDIFF  Philips' "DSDIFF 1.5": "FRM8" form "DSD ", with "FVER", "PROP" ("SND ": "FS  ",
 *           "CHNL", "CMPR") and a "DSD " chunk, one byte of each channel in turn, the oldest
 *           bit highest; big-endian 64-bit chunk sizes, chunks padded to even sizes.
 *           DST-compressed files ("DST " chunk) are recognised but not decoded.
 *
 * The output rate is FA_DSD_DEFAULT_RATE x the family rate (88.2 or 96 kHz) unless the
 * stream asks for another (fa_open_options.dsd_rate). */
#include "fa_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DSD_DFF_CHUNK 32768    /* DSDIFF bytes read at a time (all channels) */

typedef struct dsd_state {
    fa_dsd_conv* conv;
    int dff;
    int lsb_first;
    uint32_t channels;
    uint32_t block;            /* DSF: bytes of one channel per block */
    uint32_t frame_bytes;      /* bytes of one channel per output frame */
    int64_t data_off;
    int64_t frames;            /* output frames in the file */
    int64_t pos;               /* output frame next read */
    int64_t buf_block;         /* DSF: the block group in buf, -1 = none */
    uint8_t* buf;
    size_t buf_cap;
    uint8_t order[32];         /* output channel c is the file's channel order[c] */
} dsd_state;

static int probe_dsf(const uint8_t* d, size_t n)
{
    return n >= 32 && memcmp(d, "DSD ", 4) == 0 && fa_le64(d + 4) == 28 && memcmp(d + 28, "fmt ", 4) == 0;
}

static int probe_dff(const uint8_t* d, size_t n)
{
    return n >= 16 && memcmp(d, "FRM8", 4) == 0 && memcmp(d + 12, "DSD ", 4) == 0;
}

static int dsd_probe(const uint8_t* d, size_t n, size_t id3)
{
    (void)id3;
    return probe_dsf(d, n) || probe_dff(d, n) ? 100 : 0;
}

static int read_at(fa_source* src, int64_t pos, void* buf, size_t n)
{
    return fa_source_seek(src, pos) == FA_OK && fa_source_read(src, buf, n) == (int64_t)n;
}

/* WAVE speaker bits: front left, right, centre, LFE, back left, right. */
#define SPK_FL 0x1u
#define SPK_FR 0x2u
#define SPK_FC 0x4u
#define SPK_LFE 0x8u
#define SPK_BL 0x10u
#define SPK_BR 0x20u

static uint32_t dsf_mask(uint32_t type, uint32_t channels)
{
    static const uint32_t masks[8] = { 0, SPK_FC, SPK_FL | SPK_FR, SPK_FL | SPK_FR | SPK_FC,
        SPK_FL | SPK_FR | SPK_BL | SPK_BR, SPK_FL | SPK_FR | SPK_FC | SPK_LFE,
        SPK_FL | SPK_FR | SPK_FC | SPK_BL | SPK_BR, SPK_FL | SPK_FR | SPK_FC | SPK_LFE | SPK_BL | SPK_BR };
    uint32_t m = type < 8 ? masks[type] : 0, n = 0, b;
    for (b = 0; b < 32; b++) n += (m >> b) & 1;
    return n == channels && channels > 2 ? m : 0;
}

static fa_result open_dsf(fa_decoder* dec, dsd_state* st, uint32_t* rate)
{
    uint8_t h[80];
    uint64_t fmt_size, data_size, samples, payload, groups;
    uint32_t type, bits;
    if (!read_at(dec->src, 0, h, 28) || !read_at(dec->src, 28, h, 52)) return FA_ERROR_UNSUPPORTED_FORMAT;
    fmt_size = fa_le64(h + 4);
    if (memcmp(h, "fmt ", 4) != 0 || fmt_size < 52 || fmt_size > 4096 || fa_le32(h + 12) != 1 || fa_le32(h + 16) != 0)
        return FA_ERROR_UNSUPPORTED_FORMAT;
    type = fa_le32(h + 20);
    st->channels = fa_le32(h + 24);
    *rate = fa_le32(h + 28);
    bits = fa_le32(h + 32);
    samples = fa_le64(h + 36);
    st->block = fa_le32(h + 44);
    if (st->channels == 0 || st->channels > 32 || (bits != 1 && bits != 8) || st->block == 0 || st->block > 65536)
        return FA_ERROR_UNSUPPORTED_FORMAT;
    st->lsb_first = bits == 1;
    if (!read_at(dec->src, 28 + (int64_t)fmt_size, h, 12) || memcmp(h, "data", 4) != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    data_size = fa_le64(h + 4);
    st->data_off = 28 + (int64_t)fmt_size + 12;
    payload = data_size >= 12 ? data_size - 12 : 0;
    if (dec->src->length >= 0) {
        uint64_t avail = dec->src->length > st->data_off ? (uint64_t)(dec->src->length - st->data_off) : 0;
        if (payload > avail) payload = avail;
    }
    /* Whole block groups only; the last one is padded, so the sample count says where the audio ends. */
    groups = payload / ((uint64_t)st->block * st->channels);
    if (samples / 8 > groups * st->block) samples = groups * st->block * 8;
    st->frames = (int64_t)(samples / 8);   /* bytes per channel, made frames by the caller */
    dec->format.channel_mask = dsf_mask(type, st->channels);
    for (bits = 0; bits < st->channels; bits++) st->order[bits] = (uint8_t)bits;
    snprintf(dec->container, sizeof(dec->container), "dsf");
    return FA_OK;
}

static uint32_t dff_speaker(const uint8_t* id)
{
    if (!memcmp(id, "SLFT", 4) || !memcmp(id, "MLFT", 4)) return SPK_FL;
    if (!memcmp(id, "SRGT", 4) || !memcmp(id, "MRGT", 4)) return SPK_FR;
    if (!memcmp(id, "C   ", 4)) return SPK_FC;
    if (!memcmp(id, "LFE ", 4)) return SPK_LFE;
    if (!memcmp(id, "LS  ", 4)) return SPK_BL;
    if (!memcmp(id, "RS  ", 4)) return SPK_BR;
    return 0;
}

/* PROP/SND: the rate, the channels (put in WAVE order when every one is named) and the
 * compression. */
static fa_result dff_prop(fa_decoder* dec, dsd_state* st, int64_t pos, int64_t end, uint32_t* rate, int* dst)
{
    uint8_t h[12], b[4 + 2 + 32 * 4];
    uint32_t spk[32], mask = 0, i, j;
    int named = 0;
    if (!read_at(dec->src, pos, h, 4) || memcmp(h, "SND ", 4) != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    for (pos += 4; pos + 12 <= end;) {
        uint64_t size;
        if (!read_at(dec->src, pos, h, 12)) return FA_ERROR_UNSUPPORTED_FORMAT;
        size = fa_be64(h + 4);
        if (size > (uint64_t)(end - pos - 12)) return FA_ERROR_UNSUPPORTED_FORMAT;
        if (!memcmp(h, "FS  ", 4) && size >= 4) {
            if (fa_source_read(dec->src, b, 4) != 4) return FA_ERROR_UNSUPPORTED_FORMAT;
            *rate = fa_be32(b);
        } else if (!memcmp(h, "CHNL", 4) && size >= 2) {
            size_t n = size < sizeof(b) ? (size_t)size : sizeof(b);
            if (fa_source_read(dec->src, b, n) != (int64_t)n) return FA_ERROR_UNSUPPORTED_FORMAT;
            st->channels = fa_be16(b);
            if (st->channels == 0 || st->channels > 32 || 2 + (size_t)st->channels * 4 > n) return FA_ERROR_UNSUPPORTED_FORMAT;
            named = 1;
            for (i = 0; i < st->channels; i++) {
                spk[i] = dff_speaker(b + 2 + i * 4);
                if (!spk[i] || (mask & spk[i])) named = 0;
                mask |= spk[i];
            }
        } else if (!memcmp(h, "CMPR", 4) && size >= 4) {
            if (fa_source_read(dec->src, b, 4) != 4) return FA_ERROR_UNSUPPORTED_FORMAT;
            if (!memcmp(b, "DST ", 4)) *dst = 1;
            else if (memcmp(b, "DSD ", 4) != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
        }
        pos += 12 + (int64_t)size + (int64_t)(size & 1);
    }
    if (!st->channels) return FA_ERROR_UNSUPPORTED_FORMAT;
    for (i = 0; i < st->channels; i++) st->order[i] = (uint8_t)i;
    if (named && st->channels > 1) {
        /* Lowest speaker bit first, as WAVE files order their channels. */
        uint32_t k = 0;
        for (j = 0; j < 32; j++)
            for (i = 0; i < st->channels; i++) if (spk[i] == (1u << j)) st->order[k++] = (uint8_t)i;
        if (st->channels > 2) dec->format.channel_mask = mask;
    }
    return FA_OK;
}

static fa_result open_dff(fa_decoder* dec, dsd_state* st, uint32_t* rate, int64_t* dst_frames)
{
    uint8_t h[16];
    int64_t pos = 16, end;
    int prop = 0, dst = 0;
    fa_result r;
    if (!read_at(dec->src, 0, h, 16)) return FA_ERROR_UNSUPPORTED_FORMAT;
    end = 12 + (int64_t)(fa_be64(h + 4) > (uint64_t)INT64_MAX / 2 ? (uint64_t)INT64_MAX / 2 : fa_be64(h + 4));
    if (dec->src->length >= 0 && end > dec->src->length) end = dec->src->length;
    while (pos + 12 <= end) {
        uint64_t size;
        if (!read_at(dec->src, pos, h, 12)) break;
        size = fa_be64(h + 4);
        if (!memcmp(h, "PROP", 4)) {
            if (size > (uint64_t)(end - pos - 12)) return FA_ERROR_UNSUPPORTED_FORMAT;
            r = dff_prop(dec, st, pos + 12, pos + 12 + (int64_t)size, rate, &dst);
            if (r != FA_OK) return r;
            prop = 1;
        } else if (!memcmp(h, "DSD ", 4) && prop && !dst) {
            int64_t left = end - pos - 12;
            st->data_off = pos + 12;
            /* A cut file keeps what it has; whole bytes of every channel. */
            st->frames = (int64_t)(size < (uint64_t)left ? size : (uint64_t)left) / st->channels;
            snprintf(dec->container, sizeof(dec->container), "dff");
            return FA_OK;
        } else if (!memcmp(h, "DST ", 4) && prop && dst) {
            uint8_t f[18];
            *dst_frames = 0;
            if (read_at(dec->src, pos + 12, f, sizeof(f)) && !memcmp(f, "FRTE", 4) && fa_be16(f + 16) > 0)
                *dst_frames = (int64_t)fa_be32(f + 12) * (*rate / 8) / fa_be16(f + 16);   /* bytes per channel */
            snprintf(dec->container, sizeof(dec->container), "dff");
            return FA_ERROR_NOT_IMPLEMENTED;
        }
        if (size > (uint64_t)(end - pos - 12)) break;
        pos += 12 + (int64_t)size + (int64_t)(size & 1);
    }
    return FA_ERROR_UNSUPPORTED_FORMAT;
}

static fa_result dsd_open(fa_decoder* dec)
{
    const uint8_t* head;
    size_t n = fa_source_peek(dec->src, &head);
    dsd_state* st;
    uint32_t rate = 0;
    int64_t dst_frames = -1;
    fa_result r;
    st = (dsd_state*)calloc(1, sizeof(*st));
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    st->dff = probe_dff(head, n);
    if (st->dff) r = open_dff(dec, st, &rate, &dst_frames);
    else if (probe_dsf(head, n)) r = open_dsf(dec, st, &rate);
    else r = FA_ERROR_UNSUPPORTED_FORMAT;
    if (r == FA_ERROR_NOT_IMPLEMENTED) {
        /* DST: what the file says, for the tags reader; nothing to play. */
        uint32_t out = fa_dsd_out_rate(rate, dec->dsd_rate);
        r = FA_ERROR_UNSUPPORTED_FORMAT;
        if (dec->info_only && out) {
            dec->format.sample_rate = out;
            dec->format.channels = st->channels;
            dec->length = dst_frames >= 0 ? dst_frames / (rate / out / 8) : -1;
            dec->bits_per_sample = 1;
            snprintf(dec->codec, sizeof(dec->codec), "dst");
            dec->undecodable = 1;
            dec->impl = st;
            return FA_OK;
        }
    }
    if (r == FA_OK) r = fa_dsd_conv_create(rate, st->channels, dec->dsd_rate, &st->conv);
    if (r != FA_OK) {
        free(st);
        return r;
    }
    st->frame_bytes = fa_dsd_conv_frame_bytes(st->conv);
    if (!st->dff && st->block % st->frame_bytes) {
        fa_dsd_conv_free(st->conv);
        free(st);
        return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    st->frames /= st->frame_bytes;
    /* The whole frames' bytes, so the bitrate is the DSD rate times the channels. */
    dec->audio_bytes = st->frames * st->frame_bytes * st->channels;
    st->buf_cap = st->dff ? DSD_DFF_CHUNK : (size_t)st->block * st->channels;
    st->buf = (uint8_t*)malloc(st->buf_cap);
    if (!st->buf) {
        fa_dsd_conv_free(st->conv);
        free(st);
        return FA_ERROR_OUT_OF_MEMORY;
    }
    st->buf_block = -1;
    dec->impl = st;
    dec->format.sample_rate = fa_dsd_conv_rate(st->conv);
    dec->format.channels = st->channels;
    dec->length = st->frames;
    dec->seekable = dec->src->seekable;
    dec->bits_per_sample = 1;
    snprintf(dec->codec, sizeof(dec->codec), "dsd");
    return fa_source_seek(dec->src, st->data_off) == FA_OK ? FA_OK : FA_ERROR_UNSUPPORTED_FORMAT;
}

/* Up to `frames` frames from where st->pos is. Returns the frames made, 0 at the end, <0 on an error. */
static int64_t convert(fa_decoder* dec, dsd_state* st, float* out, int64_t frames)
{
    const uint8_t* planes[32];
    int64_t left = st->frames - st->pos, nf;
    uint32_t c;
    if (frames > left) frames = left;
    if (frames <= 0) return 0;
    if (st->dff) {
        size_t step = (size_t)st->frame_bytes * st->channels;
        nf = (int64_t)(st->buf_cap / step);
        if (nf > frames) nf = frames;
        if (!read_at(dec->src, st->data_off + st->pos * (int64_t)step, st->buf, (size_t)nf * step)) return FA_ERROR_IO;
        for (c = 0; c < st->channels; c++) planes[c] = st->buf + st->order[c];
        fa_dsd_conv_run(st->conv, planes, st->channels, 0, (size_t)nf, out);
    } else {
        int64_t byte = st->pos * st->frame_bytes, group = byte / st->block;
        uint32_t in_block = (uint32_t)(byte % st->block);
        if (group != st->buf_block) {
            int64_t at = st->data_off + group * (int64_t)st->buf_cap, got;
            st->buf_block = -1;   /* a failed read leaves buf part new, part old */
            if (fa_source_seek(dec->src, at) != FA_OK) return FA_ERROR_IO;
            got = fa_source_read(dec->src, st->buf, st->buf_cap);
            if (got < (int64_t)st->buf_cap) return got < 0 ? got : FA_ERROR_IO;
            st->buf_block = group;
        }
        nf = (st->block - in_block) / st->frame_bytes;
        if (nf > frames) nf = frames;
        for (c = 0; c < st->channels; c++) planes[c] = st->buf + (size_t)c * st->block + in_block;
        fa_dsd_conv_run(st->conv, planes, 1, st->lsb_first, (size_t)nf, out);
    }
    st->pos += nf;
    return nf;
}

static fa_result dsd_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    dsd_state* st = (dsd_state*)dec->impl;
    fa_frames done = 0;
    while (done < frames) {
        int64_t n = convert(dec, st, out + (size_t)done * st->channels, frames - done);
        if (n < 0) {
            if (done) break;
            *got = 0;
            return (fa_result)n;
        }
        if (n == 0) break;
        done += n;
    }
    *got = done;
    return done > 0 ? FA_OK : FA_ERROR_END_OF_STREAM;
}

static fa_result dsd_seek(fa_decoder* dec, fa_frames frame)
{
    dsd_state* st = (dsd_state*)dec->impl;
    int64_t from;
    float tmp[256];
    if (!dec->seekable) return FA_ERROR_NOT_SEEKABLE;
    if (frame < 0) return FA_ERROR_INVALID_ARG;
    if (frame > st->frames) frame = st->frames;
    /* The filters start over from silence a little earlier, so frame `frame` comes out
     * exactly as it does when the file plays through. */
    from = frame - (int64_t)fa_dsd_conv_preroll(st->conv);
    if (from < 0) from = 0;
    fa_dsd_conv_reset(st->conv);
    st->pos = from;
    while (st->pos < frame) {
        int64_t want = frame - st->pos, n;
        if (want > (int64_t)(256 / st->channels)) want = 256 / st->channels;
        n = convert(dec, st, tmp, want);
        if (n <= 0) return n < 0 ? (fa_result)n : FA_ERROR_IO;
    }
    return FA_OK;
}

static void dsd_close(fa_decoder* dec)
{
    dsd_state* st = (dsd_state*)dec->impl;
    if (st) {
        fa_dsd_conv_free(st->conv);
        free(st->buf);
        free(st);
    }
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_dsd = {
    "dsd", "DSD audio: DSF and DSDIFF (DFF), converted to PCM", "dsf;dff",
    "audio/dsf;audio/x-dsf;audio/dff;audio/x-dff",
    dsd_probe, dsd_open, dsd_read, dsd_seek, dsd_close, 1, NULL, NULL
};
