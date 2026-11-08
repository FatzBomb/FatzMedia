/* Sun/NeXT ".au" / ".snd" files, written from the public format description:
 * ".snd", then big-endian 32-bit data offset, data size (0xFFFFFFFF = unknown),
 * encoding, sample rate and channel count; the audio follows at the data offset.
 * Encodings: 1 µ-law, 2/3/4/5 linear PCM of 8/16/24/32 bits, 6/7 float of 32/64
 * bits, 27 A-law. The ADPCM and G.72x encodings are refused. */
#include "fa_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define AU_HEADER 24
#define AU_CHUNK_BYTES 16384

typedef struct au_state {
    int encoding;
    uint32_t channels;
    uint32_t frame_bytes;
    int64_t data_off;
    int64_t data_len;      /* bytes, -1 = to the end of the source */
    int64_t pos;           /* frames read */
} au_state;

static int au_kind(int enc, int* bytes)
{
    switch (enc) {
    case 2: *bytes = 1; return FA_SAMPLE_S8;
    case 3: *bytes = 2; return FA_SAMPLE_S16;
    case 4: *bytes = 3; return FA_SAMPLE_S24;
    case 5: *bytes = 4; return FA_SAMPLE_S32;
    case 6: *bytes = 4; return FA_SAMPLE_F32;
    case 7: *bytes = 8; return FA_SAMPLE_F64;
    default: return -1;
    }
}

static int au_header(const uint8_t* d, size_t n, uint32_t* off, uint32_t* size, uint32_t* enc, uint32_t* rate, uint32_t* ch)
{
    int bytes;
    if (n < AU_HEADER || memcmp(d, ".snd", 4) != 0) return 0;
    *off = fa_be32(d + 4);
    *size = fa_be32(d + 8);
    *enc = fa_be32(d + 12);
    *rate = fa_be32(d + 16);
    *ch = fa_be32(d + 20);
    if (*off < AU_HEADER || *rate == 0 || *rate > FA_MAX_SAMPLE_RATE || *ch == 0 || *ch > 32) return 0;
    return *enc == 1 || *enc == 27 || au_kind((int)*enc, &bytes) >= 0;
}

static int au_probe(const uint8_t* d, size_t n, size_t id3)
{
    uint32_t off, size, enc, rate, ch;
    (void)id3;
    return au_header(d, n, &off, &size, &enc, &rate, &ch) ? 100 : 0;
}

static fa_result au_open(fa_decoder* dec)
{
    const uint8_t* head;
    size_t n = fa_source_peek(dec->src, &head);
    uint32_t off, size, enc, rate, ch;
    int bytes = 1;
    au_state* st;
    int64_t avail;
    fa_result r;
    if (!au_header(head, n, &off, &size, &enc, &rate, &ch)) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (enc != 1 && enc != 27) au_kind((int)enc, &bytes);
    st = (au_state*)calloc(1, sizeof(*st));
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    st->encoding = (int)enc;
    st->channels = ch;
    st->frame_bytes = (uint32_t)bytes * ch;
    st->data_off = off;
    avail = dec->src->length >= 0 ? dec->src->length - (int64_t)off : -1;
    if (avail < 0 && dec->src->length >= 0) avail = 0;
    st->data_len = size == 0xFFFFFFFFu ? avail : (avail >= 0 && (int64_t)size > avail ? avail : (int64_t)size);
    if (st->data_len >= 0) st->data_len -= st->data_len % st->frame_bytes;   /* a cut file keeps whole frames */
    dec->impl = st;
    dec->format.sample_rate = rate;
    dec->format.channels = ch;
    dec->seekable = dec->src->seekable;
    if (st->data_len >= 0) dec->length = st->data_len / st->frame_bytes;
    dec->audio_bytes = st->data_len;
    dec->bits_per_sample = (uint32_t)bytes * 8;
    snprintf(dec->codec, sizeof(dec->codec), "%s", enc == 1 ? "pcm_mulaw" : enc == 27 ? "pcm_alaw" : enc >= 6 ? "pcm_float" : "pcm");
    snprintf(dec->container, sizeof(dec->container), "au");
    /* Skip the header and any annotation. */
    r = fa_source_seek(dec->src, st->data_off);
    if (r != FA_OK) {
        free(st);
        dec->impl = NULL;
        return r == FA_ERROR_INVALID_ARG ? FA_ERROR_UNSUPPORTED_FORMAT : r;   /* data offset past the end of the source */
    }
    return FA_OK;
}

static fa_result au_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    au_state* st = (au_state*)dec->impl;
    uint8_t buf[AU_CHUNK_BYTES];
    fa_frames done = 0;
    int bytes = 1, kind = (st->encoding == 1 || st->encoding == 27) ? -1 : au_kind(st->encoding, &bytes);
    while (done < frames) {
        int64_t want = (int64_t)(frames - done), room = AU_CHUNK_BYTES / st->frame_bytes, n;
        const void* plane[1];
        if (room < 1) break;
        if (want > room) want = room;
        if (st->data_len >= 0) {
            int64_t left = st->data_len / st->frame_bytes - st->pos;
            if (left <= 0) break;
            if (want > left) want = left;
        }
        n = fa_source_read(dec->src, buf, (size_t)want * st->frame_bytes);
        if (n <= 0) break;
        n -= n % st->frame_bytes;
        if (n == 0) break;
        n /= st->frame_bytes;
        if (kind < 0) {
            fa_g711_to_f32(buf, st->encoding == 27, (size_t)n * st->channels, out + (size_t)done * st->channels);
        } else {
            plane[0] = buf;
            fa_convert_to_f32(plane, kind, 0, 1, st->channels, n, out + (size_t)done * st->channels);
        }
        done += n;
        st->pos += n;
    }
    *got = done;
    return done > 0 ? FA_OK : FA_ERROR_END_OF_STREAM;
}

static fa_result au_seek(fa_decoder* dec, fa_frames frame)
{
    au_state* st = (au_state*)dec->impl;
    fa_result r;
    if (!dec->seekable) return FA_ERROR_NOT_SEEKABLE;
    if (frame < 0) return FA_ERROR_INVALID_ARG;
    if (st->data_len >= 0 && frame > st->data_len / st->frame_bytes) frame = st->data_len / st->frame_bytes;
    r = fa_source_seek(dec->src, st->data_off + (int64_t)frame * st->frame_bytes);
    if (r == FA_OK) st->pos = frame;
    return r;
}

static void au_close(fa_decoder* dec)
{
    free(dec->impl);
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_au = {
    "au", "Sun/NeXT AU and SND (PCM, float, µ-law, A-law)", "au;snd",
    "audio/basic;audio/x-au;audio/x-basic",
    au_probe, au_open, au_read, au_seek, au_close, 1, NULL, NULL
};
