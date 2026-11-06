/* Monkey's Audio (dec_ape.c over the vendored SDK) on the files in tests/data/ape
 * (make_ape_data.sh). Their audio is make_ape_wav.py's integer signal, computed
 * again here, so every decoded sample must match exactly: Monkey's Audio is
 * lossless. Also seeks across the SDK's frames, tags around the audio, damaged
 * and cut files, and sources that cannot seek. */
#include "fa_internal.h"
#ifndef FA_TEST_DATA_DIR
#  define FA_TEST_DATA_DIR "data"
#endif
#include "test_util.h"

static fa_engine* g_engine;

static void data_path(char* out, size_t cap, const char* name)
{
    snprintf(out, cap, "%s/ape/%s", FA_TEST_DATA_DIR, name);
}

static uint8_t* load_file(const char* name, size_t* size)
{
    char path[512];
    data_path(path, sizeof(path), name);
    return test_read_file(path, size);
}

static fa_result open_file(const char* name, fa_stream** s)
{
    char path[512];
    fa_open_options o;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    data_path(path, sizeof(path), name);
    *s = NULL;
    return fa_stream_open_path(g_engine, path, &o, s);
}

/* make_ape_wav.py's signal as F32, converted the way the decoder converts PCM. */
static float* expected(uint32_t bits, uint32_t channels, int64_t frames)
{
    static const int32_t amplitude[] = { 60, 8000, 2000000 };
    int32_t a = amplitude[bits / 8 - 1];
    uint32_t x = 1, bytes = bits / 8;
    size_t n = (size_t)frames * channels, k = 0;
    uint8_t* pcm = (uint8_t*)malloc(n * bytes);
    float* out = (float*)malloc(n * sizeof(float));
    const void* plane[1];
    int64_t i;
    for (i = 0; i < frames; i++) {
        uint32_t c;
        for (c = 0; c < channels; c++, k++) {
            int32_t period = 200 + 50 * (int32_t)c, phase = (int32_t)(i % period);
            int32_t half = phase < period / 2 ? phase : period - phase;
            int32_t v = (int32_t)(((int64_t)half * 4 * a) / period) - a, r;
            x = x * 1103515245u + 12345u;
            r = (int32_t)((x >> 16) & 7);
            v += (r == 0) - (r == 1);
            if (bytes == 1) pcm[k] = (uint8_t)(v + 128);
            else {
                uint32_t u = (uint32_t)v, b;
                for (b = 0; b < bytes; b++) pcm[k * bytes + b] = (uint8_t)(u >> (8 * b));
            }
        }
    }
    plane[0] = pcm;
    fa_convert_to_f32(plane, bits == 8 ? FA_SAMPLE_U8 : bits == 16 ? FA_SAMPLE_S16 : FA_SAMPLE_S24, 0, 0,
                      channels, (fa_frames)frames, out);
    free(pcm);
    return out;
}

static float* read_all(fa_stream* s, uint32_t channels, int64_t cap, int64_t* out_n)
{
    float* all = (float*)calloc((size_t)(cap + 4096) * channels, sizeof(float));
    int64_t n = 0;
    for (;;) {
        fa_frames got = 0;
        fa_frames want = 1000;   /* not a divisor of anything in the files */
        fa_result r;
        if (n + want > cap + 4096) break;
        r = fa_stream_read(s, all + (size_t)n * channels, want, &got);
        n += got;
        if (r != FA_OK || got == 0) break;
    }
    *out_n = n;
    return all;
}

static int same(const float* a, const float* b, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++)
        if (a[i] != b[i]) {
            fprintf(stderr, "  sample %zu: %.9f vs %.9f\n", i, (double)a[i], (double)b[i]);
            return 0;
        }
    return 1;
}

static void check_file(const char* name, uint32_t rate, uint32_t channels, uint32_t bits, int64_t frames)
{
    fa_stream* s;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float *full, *want;
    int64_t n;
    CHECK_EQ(open_file(name, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK(strcmp(info.codec, "ape") == 0);
    CHECK(strcmp(info.container, "ape") == 0);
    CHECK_EQ(info.format.sample_rate, rate);
    CHECK_EQ(info.format.channels, channels);
    CHECK_EQ(info.bits_per_sample, bits);
    CHECK_EQ(info.length_frames, frames);
    CHECK(info.bitrate > 0);
    full = read_all(s, channels, frames, &n);
    want = expected(bits, channels, frames);
    if (n != frames) fprintf(stderr, "  %s: %lld frames\n", name, (long long)n);
    CHECK_EQ(n, frames);
    CHECK(same(full, want, (size_t)frames * channels));
    free(full);
    free(want);
    fa_stream_close(s);
}

static void test_levels(void)
{
    check_file("s16_c1.ape", 44100, 2, 16, 8000);   /* fast */
    check_file("s16_c2.ape", 44100, 2, 16, 8000);   /* normal */
    check_file("s16_c3.ape", 44100, 2, 16, 8000);   /* high */
    check_file("s16_c4.ape", 44100, 2, 16, 8000);   /* extra high */
    check_file("s16_c5.ape", 44100, 2, 16, 8000);   /* insane */
}

static void test_sample_formats(void)
{
    check_file("u8.ape", 8000, 1, 8, 8000);
    check_file("s24.ape", 48000, 2, 24, 8000);
    check_file("s16_long.ape", 22050, 2, 16, 80000);
}

/* Seeks inside the first frame, onto and around the frame boundary at 73728, to
 * the last frame and to the end, in an order that goes back and forth. */
static void test_seeks(void)
{
    static const int64_t targets[] = { 40000, 0, 73728, 1, 73727, 79999, 12345, 80000, 73000 };
    fa_stream* s;
    float* want = expected(16, 2, 80000);
    size_t k;
    CHECK_EQ(open_file("s16_long.ape", &s), FA_OK);
    for (k = 0; k < sizeof(targets) / sizeof(targets[0]); k++) {
        int64_t at = targets[k], len = 80000 - at < 3000 ? 80000 - at : 3000, n = 0;
        float buf[3000 * 2];
        CHECK_EQ(fa_stream_seek(s, at), FA_OK);
        while (n < len) {
            fa_frames got = 0;
            fa_result r = fa_stream_read(s, buf + n * 2, len - n < 777 ? len - n : 777, &got);
            n += got;
            if (r != FA_OK || got == 0) break;
        }
        if (n != len) fprintf(stderr, "  seek to %lld: %lld of %lld frames\n", (long long)at, (long long)n, (long long)len);
        CHECK_EQ(n, len);
        CHECK(same(buf, want + at * 2, (size_t)len * 2));
        if (at == 80000) {
            fa_frames got = 1;
            CHECK_EQ(fa_stream_read(s, buf, 100, &got), FA_ERROR_END_OF_STREAM);
            CHECK_EQ(got, 0);
        }
    }
    free(want);
    fa_stream_close(s);
}

/* An ID3v2 tag in front (the SDK skips it) and an APEv2 tag at the end: same audio,
 * and the tags read like any file's. */
static void test_tags(void)
{
    char path[512];
    fa_tags* tags = NULL;
    const char* v;
    check_file("id3v2.ape", 44100, 2, 16, 8000);
    check_file("apev2.ape", 44100, 2, 16, 8000);
    data_path(path, sizeof(path), "apev2.ape");
    CHECK_EQ(fa_tags_read_path(path, 0, &tags), FA_OK);
    v = fa_tags_get(tags, FA_TAG_TITLE);
    CHECK(v && strcmp(v, "APE Test") == 0);
    v = fa_tags_get(tags, FA_TAG_ARTIST);
    CHECK(v && strcmp(v, "FatzAudio") == 0);
    fa_tags_close(tags);
    data_path(path, sizeof(path), "id3v2.ape");
    CHECK_EQ(fa_tags_read_path(path, 0, &tags), FA_OK);
    v = fa_tags_get(tags, FA_TAG_TITLE);
    CHECK(v && strcmp(v, "ID3 Test") == 0);
    fa_tags_close(tags);
}

/* Memory without a name: found by its "MAC " signature. */
static void test_memory(void)
{
    size_t size = 0;
    uint8_t* data = load_file("s16_c2.ape", &size);
    fa_open_options o;
    fa_stream* s = NULL;
    fa_stream_info info = { .struct_size = sizeof(info) };
    float *full, *want;
    int64_t n;
    CHECK(data != NULL);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    CHECK_EQ(fa_stream_open_memory(g_engine, data, size, &o, &s), FA_OK);
    CHECK_EQ(fa_stream_get_info(s, &info), FA_OK);
    CHECK(strcmp(info.codec, "ape") == 0);
    full = read_all(s, 2, 8000, &n);
    want = expected(16, 2, 8000);
    CHECK_EQ(n, 8000);
    CHECK(same(full, want, 16000));
    free(full);
    free(want);
    fa_stream_close(s);
    free(data);
}

/* Cut short and damaged copies: an error or fewer frames, never a crash or a hang
 * (the SDK gives silence for a frame it cannot decode). */
static void test_damaged(void)
{
    static const size_t cuts[] = { 4, 40, 100, 1000, 30000, 60000, 108000 };
    size_t size = 0, k;
    uint8_t* data = load_file("s16_long.ape", &size);
    fa_open_options o;
    CHECK(data != NULL);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    for (k = 0; k < sizeof(cuts) / sizeof(cuts[0]) + 3; k++) {
        uint8_t* copy = (uint8_t*)malloc(size);
        size_t len = size;
        fa_stream* s = NULL;
        memcpy(copy, data, size);
        if (k < sizeof(cuts) / sizeof(cuts[0])) {
            len = cuts[k];
        } else {
            size_t i, from = 2000 + (k - 7) * 25000;
            for (i = from; i < from + 64 && i < size; i++) copy[i] ^= 0x5A;   /* a damaged stretch */
        }
        if (fa_stream_open_memory(g_engine, copy, len, &o, &s) == FA_OK) {
            int64_t n;
            float* p = read_all(s, 2, 80000, &n);
            CHECK(n <= 80000);
            /* A damaged stretch costs that frame (silence), not the rest of the file. */
            if (len == size) CHECK_EQ(n, 80000);
            (void)fa_stream_seek(s, 50000);
            free(p);
            fa_stream_close(s);
        }
        free(copy);
    }
    free(data);
}

typedef struct forward_io { const uint8_t* p; size_t size, pos; } forward_io;

static int64_t FA_CALL forward_read(void* user, void* buf, size_t n)
{
    forward_io* f = (forward_io*)user;
    if (n > f->size - f->pos) n = f->size - f->pos;
    memcpy(buf, f->p + f->pos, n);
    f->pos += n;
    return (int64_t)n;
}

/* The SDK needs the seek table and the end of the file: a forward-only source is
 * refused with FA_ERROR_NOT_SEEKABLE. */
static void test_forward_only(void)
{
    size_t size = 0;
    uint8_t* data = load_file("s16_c2.ape", &size);
    forward_io f;
    fa_io_callbacks io;
    fa_open_options o;
    fa_stream* s = NULL;
    CHECK(data != NULL);
    f.p = data;
    f.size = size;
    f.pos = 0;
    memset(&io, 0, sizeof(io));
    io.read = forward_read;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    CHECK_EQ(fa_stream_open_io(g_engine, &io, &f, &o, &s), FA_ERROR_NOT_SEEKABLE);
    free(data);
}

int main(void)
{
    fa_engine_config cfg;
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT;
    cfg.worker_threads = 2;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) {
        fprintf(stderr, "engine create failed\n");
        return 1;
    }
    RUN(test_levels);
    RUN(test_sample_formats);
    RUN(test_seeks);
    RUN(test_tags);
    RUN(test_memory);
    RUN(test_damaged);
    RUN(test_forward_only);
    fa_engine_destroy(g_engine);
    if (g_failures) {
        fprintf(stderr, "%d failure(s)\n", g_failures);
        return 1;
    }
    printf("all ok\n");
    return 0;
}
