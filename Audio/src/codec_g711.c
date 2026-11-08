/* Small sample codecs written from their public definitions: G.711 µ-law and A-law
 * (ITU-T G.711, one byte per sample) and Apple's IMA 4:1 ADPCM ("ima4": the IMA
 * Digital Audio Focus Group's algorithm in 34-byte blocks of 64 samples, one block
 * per channel, as QuickTime and Core Audio files hold it). */
#include "fa_internal.h"
#include <stdlib.h>
#include <string.h>

/* ---- G.711 ------------------------------------------------------------------ */

static int16_t mulaw_sample(uint8_t b)
{
    int t, u = ~b & 0xFF;
    t = (((u & 0x0F) << 3) + 0x84) << ((u & 0x70) >> 4);
    return (int16_t)((u & 0x80) ? (0x84 - t) : (t - 0x84));
}

static int16_t alaw_sample(uint8_t b)
{
    int a = b ^ 0x55, t = (a & 0x0F) << 4, seg = (a & 0x70) >> 4;
    if (seg == 0) t += 8;
    else t = (t + 0x108) << (seg - 1);
    return (int16_t)((a & 0x80) ? t : -t);
}

void fa_g711_to_f32(const uint8_t* in, int alaw, size_t n, float* out)
{
    size_t i;
    if (alaw)
        for (i = 0; i < n; i++) out[i] = alaw_sample(in[i]) / 32768.0f;
    else
        for (i = 0; i < n; i++) out[i] = mulaw_sample(in[i]) / 32768.0f;
}

typedef struct g711_state {
    int alaw;
    uint8_t carry[32];      /* the samples of a frame split across two packets */
    size_t carry_len;
} g711_state;

static fa_result g711_decode(fa_codec* c, const uint8_t* data, size_t size)
{
    g711_state* st = (g711_state*)c->impl;
    size_t ch = c->channels, total, frames, from_data, i;
    fa_result r;
    c->out_frames = 0;
    if (!data || size == 0) return FA_OK;
    total = st->carry_len + size;
    frames = total / ch;
    if (frames > 0) {
        float* out;
        r = fa_codec_reserve(c, (fa_frames)frames, c->channels);
        if (r != FA_OK) return r;
        out = c->out;
        /* The carried samples first, then whole frames from the packet. */
        fa_g711_to_f32(st->carry, st->alaw, st->carry_len, out);
        from_data = frames * ch - st->carry_len;
        fa_g711_to_f32(data, st->alaw, from_data, out + st->carry_len);
        c->out_frames = (fa_frames)frames;
        data += from_data;
        size -= from_data;
        st->carry_len = 0;
    }
    for (i = 0; i < size && st->carry_len < sizeof(st->carry); i++) st->carry[st->carry_len++] = data[i];
    return FA_OK;
}

static void g711_flush(fa_codec* c) { ((g711_state*)c->impl)->carry_len = 0; }
static void free_impl(fa_codec* c) { free(c->impl); c->impl = NULL; }
static const fa_codec_vtable g711_vtable = { "g711", g711_decode, g711_flush, free_impl };

fa_result fa_g711_codec_open(fa_codec* c, const fa_codec_params* p)
{
    g711_state* st;
    int alaw = strcmp(p->codec, "pcm_alaw") == 0;
    if (!alaw && strcmp(p->codec, "pcm_mulaw") != 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (p->sample_rate == 0 || p->channels == 0 || p->channels > 32) return FA_ERROR_UNSUPPORTED_FORMAT;
    st = (g711_state*)calloc(1, sizeof(*st));
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    st->alaw = alaw;
    c->impl = st;
    c->vt = &g711_vtable;
    c->rate = p->sample_rate;
    c->channels = p->channels;
    return FA_OK;
}

/* ---- IMA 4:1 ADPCM ---------------------------------------------------------- */

#define IMA4_BLOCK 34
#define IMA4_SAMPLES 64

static const int16_t ima_steps[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
    118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
    1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
    6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
    32767 };
static const int8_t ima_index[8] = { -1, -1, -1, -1, 2, 4, 6, 8 };

typedef struct ima4_chan { int pred, idx, valid; } ima4_chan;

/* One block: 2 header bytes (9 bits of predictor, 7 of step index), then 32 bytes
 * of 4-bit codes, low nibble first. Writes 64 samples with stride `stride`.
 * Apple's decoder keeps its own running predictor when the header agrees with it
 * (same step index, predictor within 127): the header holds only 9 bits of it, so
 * the low bits are not thrown away at every block. A decoder that starts fresh (a
 * seek) takes the header. */
static void ima4_block(ima4_chan* ch, const uint8_t* b, float* out, size_t stride)
{
    int hdr = (int16_t)(((b[0] << 8) | b[1]) & 0xFF80), hidx = b[1] & 0x7F, pred, idx, i;
    if (hidx > 88) hidx = 88;
    if (ch->valid && ch->idx == hidx && abs(hdr - ch->pred) <= 0x7F) {
        pred = ch->pred;
        idx = ch->idx;
    } else {
        pred = hdr;
        idx = hidx;
    }
    for (i = 0; i < IMA4_SAMPLES; i++) {
        int code = (i & 1) ? (b[2 + i / 2] >> 4) : (b[2 + i / 2] & 15), step = ima_steps[idx];
        int diff = step >> 3;
        if (code & 4) diff += step;
        if (code & 2) diff += step >> 1;
        if (code & 1) diff += step >> 2;
        pred += (code & 8) ? -diff : diff;
        if (pred > 32767) pred = 32767;
        else if (pred < -32768) pred = -32768;
        idx += ima_index[code & 7];
        if (idx < 0) idx = 0;
        else if (idx > 88) idx = 88;
        out[(size_t)i * stride] = (float)pred / 32768.0f;
    }
    ch->pred = pred;
    ch->idx = idx;
    ch->valid = 1;
}

static fa_result ima4_decode(fa_codec* c, const uint8_t* data, size_t size)
{
    ima4_chan* chans = (ima4_chan*)c->impl;
    size_t ch = c->channels, packet = (size_t)IMA4_BLOCK * ch, n, k, j;
    fa_result r;
    c->out_frames = 0;
    if (!data || size < packet) return FA_OK;
    n = size / packet;     /* a cut-off last packet is dropped */
    r = fa_codec_reserve(c, (fa_frames)(n * IMA4_SAMPLES), c->channels);
    if (r != FA_OK) return r;
    for (k = 0; k < n; k++)
        for (j = 0; j < ch; j++)
            ima4_block(&chans[j], data + k * packet + j * IMA4_BLOCK, c->out + k * IMA4_SAMPLES * ch + j, ch);
    c->out_frames = (fa_frames)(n * IMA4_SAMPLES);
    return FA_OK;
}

static void ima4_flush(fa_codec* c)
{
    ima4_chan* chans = (ima4_chan*)c->impl;
    uint32_t j;
    for (j = 0; j < c->channels; j++) chans[j].valid = 0;
}

static const fa_codec_vtable ima4_vtable = { "ima4", ima4_decode, ima4_flush, free_impl };

fa_result fa_ima4_codec_open(fa_codec* c, const fa_codec_params* p)
{
    if (p->sample_rate == 0 || p->channels == 0 || p->channels > 32) return FA_ERROR_UNSUPPORTED_FORMAT;
    c->impl = calloc(p->channels, sizeof(ima4_chan));
    if (!c->impl) return FA_ERROR_OUT_OF_MEMORY;
    c->vt = &ima4_vtable;
    c->rate = p->sample_rate;
    c->channels = p->channels;
    return FA_OK;
}
