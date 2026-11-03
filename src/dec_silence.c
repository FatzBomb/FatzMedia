/* CD+G graphics with no audio (a lone .cdg, or a ZIP holding only one): a decoder
 * of silence exactly as long as the graphics, so the stream plays, pauses, seeks,
 * loops and changes tempo like any file and its clock drives the CD+G surface.
 * The graphics themselves are the same bytes, attached by the stream (stream.c). */
#include "fa_internal.h"
#include "cdg.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define SILENCE_RATE 44100
#define SILENCE_CHANNELS 2
/* 44,100 / 300: one CD+G pack is exactly 147 frames. */
#define FRAMES_PER_PACK (SILENCE_RATE / FA_CDG_PACKS_PER_SECOND)

typedef struct {
    fa_frames pos;
} silence_state;

/* CD+G: every pack's command (low 6 bits of byte 0) is 0 (nothing), 0x09 (TV
 * graphics) or 0x0A (extended graphics), and at least one is a TV-graphics
 * instruction the decoder knows. Random data passes the first test for one pack in
 * 21, so a window of packs is never mistaken; the lowest score lets every real
 * format win. A window of empty packs proves nothing: only the name or MIME type
 * claims those (fa_decoder_open). */
static int silence_probe(const uint8_t* data, size_t size, size_t id3_size)
{
    size_t i, packs = size / FA_CDG_PACK_BYTES;
    int drawn = 0;
    if (id3_size || packs == 0) return 0;
    for (i = 0; i < packs; i++) {
        const uint8_t* p = data + i * FA_CDG_PACK_BYTES;
        uint8_t cmd = p[0] & 0x3F, ins = p[1] & 0x3F;
        if (cmd != 0x00 && cmd != 0x09 && cmd != 0x0A) return 0;
        if (cmd == 0x09 && (ins == 1 || ins == 2 || ins == 6 || ins == 20 || ins == 24 || ins == 28 ||
                            ins == 30 || ins == 31 || ins == 38))
            drawn = 1;
    }
    return drawn;
}

static fa_result silence_open(fa_decoder* dec)
{
    fa_source* src = dec->src;
    int64_t bytes = src->length;
    silence_state* st;
    if (bytes < 0) {
        /* App callbacks with no size: count the bytes. */
        uint8_t buf[16384];
        int64_t got;
        bytes = 0;
        while ((got = fa_source_read(src, buf, sizeof(buf))) > 0) bytes += got;
        if (got < 0) return (fa_result)got;
        if (fa_source_seek(src, 0) != FA_OK) return FA_ERROR_NOT_SEEKABLE;
    }
    if (bytes < FA_CDG_PACK_BYTES) return FA_ERROR_UNSUPPORTED_FORMAT;
    st = (silence_state*)calloc(1, sizeof(*st));
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    dec->impl = st;
    dec->format.sample_rate = SILENCE_RATE;
    dec->format.channels = SILENCE_CHANNELS;
    dec->length = (fa_frames)(bytes / FA_CDG_PACK_BYTES) * FRAMES_PER_PACK;
    dec->seekable = 1;
    dec->audio_bytes = 0;
    snprintf(dec->codec, sizeof(dec->codec), "none");
    snprintf(dec->container, sizeof(dec->container), "cdg");
    return FA_OK;
}

static fa_result silence_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    silence_state* st = (silence_state*)dec->impl;
    fa_frames n = dec->length - st->pos;
    if (n <= 0) { *got = 0; return FA_ERROR_END_OF_STREAM; }
    if (n > frames) n = frames;
    memset(out, 0, (size_t)n * SILENCE_CHANNELS * sizeof(float));
    st->pos += n;
    *got = n;
    return FA_OK;
}

static fa_result silence_seek(fa_decoder* dec, fa_frames frame)
{
    ((silence_state*)dec->impl)->pos = frame;
    return FA_OK;
}

static void silence_close(fa_decoder* dec)
{
    free(dec->impl);
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_cdg = {
    "cdg", "CD+G graphics with no audio (plays silence for the graphics' length)", "cdg",
    "application/x-cdg;video/x-cdg",
    silence_probe, silence_open, silence_read, silence_seek, silence_close, 1, NULL, NULL
};

int fa_decoder_is_cdg(const fa_decoder* dec) { return dec->vt == &fa_decoder_cdg; }
