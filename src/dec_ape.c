/* Monkey's Audio (.ape), decoded by the vendored Monkey's Audio SDK through
 * codec_ape.h. Every version the SDK reads (3.80 and later, so files from 2000
 * on), 8/16/24/32-bit integer and 32-bit float, 1 to 32 channels. The SDK needs
 * the seek table at the start and the tags at the end, so the source must seek
 * and know its length. Tags (APEv2, ID3v1) are read by tags.c like any file's. */
#include "fa_internal.h"
#include "codec_ape.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define APE_CHUNK_BYTES 32768

typedef struct ape_state {
    fa_ape* ape;
    fa_ape_info info;
    int kind;            /* FA_SAMPLE_* of the SDK's PCM */
    int64_t pos;         /* frames read */
    uint8_t buf[APE_CHUNK_BYTES];
} ape_state;

static int64_t ape_io_read(void* user, void* buf, size_t n)
{
    return fa_source_read((fa_source*)user, buf, n);
}

static int ape_io_seek(void* user, int64_t offset)
{
    return fa_source_seek((fa_source*)user, offset) == FA_OK ? 0 : -1;
}

static int64_t ape_io_tell(void* user)
{
    return fa_source_tell((const fa_source*)user);
}

/* "MAC " (integer) or "MACF" (float) and a version, at the start or after an ID3v2 tag. */
static int ape_probe(const uint8_t* d, size_t n, size_t id3)
{
    uint32_t version;
    if (id3 + 6 > n) return 0;
    d += id3;
    if (memcmp(d, "MAC", 3) != 0 || (d[3] != ' ' && d[3] != 'F')) return 0;
    version = (uint32_t)d[4] | ((uint32_t)d[5] << 8);
    return version >= 3800 && version <= 5000 ? 100 : 0;
}

static fa_result ape_open(fa_decoder* dec)
{
    fa_ape_io io;
    ape_state* st;
    int error = 0;
    if (!dec->src->seekable || dec->src->length <= 0) return FA_ERROR_NOT_SEEKABLE;
    st = (ape_state*)calloc(1, sizeof(*st));
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    io.read = ape_io_read;
    io.seek = ape_io_seek;
    io.tell = ape_io_tell;
    io.size = dec->src->length;
    io.user = dec->src;
    st->ape = fa_ape_open(&io, &st->info, &error);
    if (!st->ape) {
        free(st);
        if (error == 3) return FA_ERROR_OUT_OF_MEMORY;
        if (dec->src->error != FA_OK) return FA_ERROR_IO;
        return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    switch (st->info.bits) {
    case 8: st->kind = FA_SAMPLE_U8; break;
    case 16: st->kind = FA_SAMPLE_S16; break;
    case 24: st->kind = FA_SAMPLE_S24; break;
    default: st->kind = st->info.is_float ? FA_SAMPLE_F32 : FA_SAMPLE_S32; break;
    }
    dec->impl = st;
    dec->format.sample_rate = st->info.sample_rate;
    dec->format.channels = st->info.channels;
    dec->seekable = 1;
    dec->length = st->info.total_blocks;
    dec->audio_bytes = st->info.audio_bytes;
    dec->bitrate = st->info.bitrate;
    dec->bits_per_sample = st->info.bits;
    snprintf(dec->codec, sizeof(dec->codec), "ape");
    snprintf(dec->container, sizeof(dec->container), "ape");
    return FA_OK;
}

static fa_result ape_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    ape_state* st = (ape_state*)dec->impl;
    uint32_t align = st->info.block_align;
    fa_frames done = 0;
    while (done < frames && st->pos < st->info.total_blocks) {
        int64_t want = (int64_t)(frames - done), n = 0;
        const void* plane[1];
        if (want > (int64_t)(APE_CHUNK_BYTES / align)) want = APE_CHUNK_BYTES / align;
        /* A damaged frame comes back as silence with an error: play on. */
        fa_ape_read(st->ape, st->buf, want, &n);
        if (n <= 0) break;
        plane[0] = st->buf;
        fa_convert_to_f32(plane, st->kind, 0, 0, st->info.channels, (fa_frames)n, out + (size_t)done * st->info.channels);
        done += (fa_frames)n;
        st->pos += n;
    }
    *got = done;
    if (done > 0) return FA_OK;
    return dec->src->error != FA_OK ? FA_ERROR_IO : FA_ERROR_END_OF_STREAM;
}

static fa_result ape_seek(fa_decoder* dec, fa_frames frame)
{
    ape_state* st = (ape_state*)dec->impl;
    if (frame < 0) return FA_ERROR_INVALID_ARG;
    if (frame >= st->info.total_blocks) {
        st->pos = st->info.total_blocks;   /* the SDK would stop one frame short of the end */
        return FA_OK;
    }
    if (fa_ape_seek(st->ape, frame) != 0) return dec->src->error != FA_OK ? FA_ERROR_IO : FA_ERROR_GENERIC;
    st->pos = frame;
    return FA_OK;
}

static void ape_close(fa_decoder* dec)
{
    ape_state* st = (ape_state*)dec->impl;
    if (st) {
        fa_ape_close(st->ape);
        free(st);
    }
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_ape = {
    "ape", "Monkey's Audio (APE)", "ape;mac",
    "audio/ape;audio/x-ape;audio/x-monkeys-audio",
    ape_probe, ape_open, ape_read, ape_seek, ape_close, 1, NULL, NULL
};
