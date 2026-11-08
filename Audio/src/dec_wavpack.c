/* WavPack (.wv) through libwavpack's decoder (third_party/wavpack, BSD-3-Clause):
 * lossless, hybrid (the lossy part; a .wvc correction file is not read), integer
 * of 8 to 32 bits, 32-bit float and DSD, which the library decimates 8x to 24-bit
 * PCM (DSD64 plays at 352.8 kHz). 1 to 32 channels in the file's WAVE channel
 * order. The library reads through fa_source callbacks, so files, memory, app I/O,
 * archive entries and web streams all work; seeks use the library's own block
 * search. Tags (APEv2, ID3v1) are read by tags_read.c like any other file's. */
#include "fa_internal.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wavpack.h>

#define WV_CHUNK_FRAMES 2048

typedef struct wv_io {
    fa_source* src;
    int pushed;            /* the byte libwavpack gave back, -1 = none */
} wv_io;

typedef struct wv_state {
    WavpackContext* wpc;
    wv_io io;
    uint32_t channels;
    int is_float;
    float scale;           /* integer samples: 1 / 2^(bytes * 8 - 1) */
    int32_t* buf;          /* WV_CHUNK_FRAMES * channels */
    int at_end;            /* seeked to the length: reads end until the next seek */
} wv_state;

/* ---- fa_source as a WavpackStreamReader64 ------------------------------------------ */

static int32_t io_read(void* id, void* data, int32_t bcount)
{
    wv_io* io = (wv_io*)id;
    uint8_t* p = (uint8_t*)data;
    int32_t done = 0;
    int64_t n;
    if (bcount <= 0) return 0;
    if (io->pushed >= 0) {
        *p++ = (uint8_t)io->pushed;
        io->pushed = -1;
        done = 1;
    }
    if (done < bcount) {
        n = fa_source_read(io->src, p, (size_t)(bcount - done));
        if (n > 0) done += (int32_t)n;
    }
    return done;
}

static int64_t io_get_pos(void* id)
{
    wv_io* io = (wv_io*)id;
    return fa_source_tell(io->src) - (io->pushed >= 0 ? 1 : 0);
}

static int io_set_pos_abs(void* id, int64_t pos)
{
    wv_io* io = (wv_io*)id;
    if (pos < 0 || fa_source_seek(io->src, pos) != FA_OK) return -1;
    io->pushed = -1;
    return 0;
}

static int io_set_pos_rel(void* id, int64_t delta, int mode)
{
    wv_io* io = (wv_io*)id;
    int64_t base;
    if (mode == SEEK_SET) base = 0;
    else if (mode == SEEK_CUR) base = io_get_pos(id);
    else if (mode == SEEK_END && io->src->length >= 0) base = io->src->length;
    else return -1;
    return io_set_pos_abs(id, base + delta);
}

static int io_push_back(void* id, int c)
{
    wv_io* io = (wv_io*)id;
    if (io->pushed >= 0) return -1;   /* libwavpack gives back at most one byte */
    io->pushed = c & 0xFF;
    return c;
}

static int64_t io_get_length(void* id)
{
    wv_io* io = (wv_io*)id;
    return io->src->length > 0 ? io->src->length : 0;
}

static int io_can_seek(void* id)
{
    return ((wv_io*)id)->src->seekable;
}

static WavpackStreamReader64 wv_reader = {
    io_read, NULL, io_get_pos, io_set_pos_abs, io_set_pos_rel, io_push_back,
    io_get_length, io_can_seek, NULL, NULL
};

/* ---- the decoder ----------------------------------------------------------------- */

/* A WavPack 4/5 block header: "wvpk", block size, stream version 0x402..0x410. */
static int wv_block_header(const uint8_t* d, size_t n)
{
    uint32_t size, version;
    if (n < 32 || memcmp(d, "wvpk", 4) != 0) return 0;
    size = fa_le32(d + 4);
    version = (uint32_t)d[8] | ((uint32_t)d[9] << 8);
    return size >= 24 && size <= (1u << 24) && version >= 0x402 && version <= 0x410;
}

static int wv_probe(const uint8_t* d, size_t n, size_t id3)
{
    if (id3 < n && wv_block_header(d + id3, n - id3)) return 100;
    return 0;
}

static fa_result wv_open_context(wv_state* st, char* error)
{
    /* OPEN_NORMALIZE: float files come out at +/-1.0. No OPEN_TAGS: tags_read.c reads them. */
    st->io.pushed = -1;
    st->wpc = WavpackOpenFileInputEx64(&wv_reader, &st->io, NULL, error,
                                       OPEN_NORMALIZE | OPEN_DSD_AS_PCM, 0);
    return st->wpc ? FA_OK : st->io.src->error != FA_OK ? st->io.src->error : FA_ERROR_UNSUPPORTED_FORMAT;
}

static void wv_free(wv_state* st)
{
    if (st->wpc) WavpackCloseFile(st->wpc);
    free(st->buf);
    free(st);
}

static fa_result wv_open(fa_decoder* dec)
{
    char error[80] = "";
    wv_state* st = (wv_state*)calloc(1, sizeof(*st));
    int mode, bytes, ch;
    uint32_t rate;
    int64_t total;
    fa_result r;
    if (!st) return FA_ERROR_OUT_OF_MEMORY;
    st->io.src = dec->src;
    r = wv_open_context(st, error);
    if (r != FA_OK) {
        free(st);
        return r;
    }
    mode = WavpackGetMode(st->wpc);
    bytes = WavpackGetBytesPerSample(st->wpc);
    ch = WavpackGetNumChannels(st->wpc);
    rate = WavpackGetSampleRate(st->wpc);
    if (ch < 1 || ch > 32 || rate == 0 || rate > FA_MAX_SAMPLE_RATE || bytes < 1 || bytes > 4) {
        wv_free(st);
        return FA_ERROR_UNSUPPORTED_FORMAT;
    }
    st->channels = (uint32_t)ch;
    st->is_float = (mode & MODE_FLOAT) != 0;
    st->scale = 1.0f / (float)(1u << (bytes * 8 - 1));
    st->buf = (int32_t*)malloc(sizeof(int32_t) * WV_CHUNK_FRAMES * st->channels);
    if (!st->buf) {
        wv_free(st);
        return FA_ERROR_OUT_OF_MEMORY;
    }
    total = WavpackGetNumSamples64(st->wpc);
    dec->impl = st;
    dec->format.sample_rate = rate;
    dec->format.channels = st->channels;
    dec->length = total >= 0 ? total : -1;
    /* libwavpack refuses every seek when it does not know the length. */
    dec->seekable = dec->src->seekable && total >= 0;
    /* Lossless files keep their bit depth; a hybrid file without its correction
     * file is lossy. DSD counts as 1 bit, whatever the decimated output is. */
    if (WavpackGetQualifyMode(st->wpc) & QMODE_DSD_AUDIO) dec->bits_per_sample = 1;
    else if (mode & MODE_LOSSLESS) dec->bits_per_sample = (uint32_t)WavpackGetBitsPerSample(st->wpc);
    snprintf(dec->codec, sizeof(dec->codec), "wavpack");
    snprintf(dec->container, sizeof(dec->container), "wavpack");
    return FA_OK;
}

static fa_result wv_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    wv_state* st = (wv_state*)dec->impl;
    fa_frames done = 0;
    while (!st->at_end && done < frames) {
        uint32_t want = frames - done > WV_CHUNK_FRAMES ? WV_CHUNK_FRAMES : (uint32_t)(frames - done);
        uint32_t n = st->wpc ? WavpackUnpackSamples(st->wpc, st->buf, want) : 0;
        size_t i, count = (size_t)n * st->channels;
        float* o = out + (size_t)done * st->channels;
        if (n == 0) break;
        if (st->is_float) {
            memcpy(o, st->buf, count * sizeof(float));
            fa_zero_non_finite(o, count);
        } else {
            for (i = 0; i < count; i++) o[i] = (float)st->buf[i] * st->scale;
        }
        done += n;
    }
    *got = done;
    return done > 0 ? FA_OK : FA_ERROR_END_OF_STREAM;
}

/* After a failed seek the library's context is unusable: open the file again. */
static int wv_reopen(fa_decoder* dec, wv_state* st)
{
    char error[80] = "";
    if (st->wpc) WavpackCloseFile(st->wpc);
    st->wpc = NULL;
    return fa_source_seek(dec->src, 0) == FA_OK && wv_open_context(st, error) == FA_OK;
}

static fa_result wv_seek(fa_decoder* dec, fa_frames frame)
{
    wv_state* st = (wv_state*)dec->impl;
    int64_t was;
    /* libwavpack seeks only to a sample it can decode; the end itself (allowed by
     * fa_decoder_seek) is a position with nothing left to read. */
    if (dec->length >= 0 && frame >= dec->length) {
        st->at_end = 1;
        return FA_OK;
    }
    was = st->wpc ? WavpackGetSampleIndex64(st->wpc) : 0;
    if (st->wpc && WavpackSeekSample64(st->wpc, frame)) {
        st->at_end = 0;
        return FA_OK;
    }
    /* Reopen and go back where the stream was, so a failed seek leaves playback in
     * place (or at the start, when that position cannot be reached either). */
    if (!wv_reopen(dec, st)) return FA_ERROR_IO;
    if (!st->at_end && was > 0 && !WavpackSeekSample64(st->wpc, was) && !wv_reopen(dec, st)) return FA_ERROR_IO;
    return FA_ERROR_INVALID_ARG;
}

static void wv_close(fa_decoder* dec)
{
    if (dec->impl) wv_free((wv_state*)dec->impl);
    dec->impl = NULL;
}

const fa_decoder_vtable fa_decoder_wavpack = {
    "wavpack", "WavPack (lossless, hybrid, float and DSD)", "wv",
    "audio/wavpack;audio/x-wavpack",
    wv_probe, wv_open, wv_read, wv_seek, wv_close, 1, NULL, NULL
};
