/* The vector paths of the DSP loops (SSE on x86, NEON on ARM) against plain math.
 *
 * Each loop has a scalar fallback and one or two vector versions. Whichever one the build
 * picks must give the scalar result within float rounding, so these tests compare the
 * library's output with a reference worked out here in double precision, in plain C, and
 * check that every channel lane of the multichannel paths gets the same treatment. They
 * pass on every platform; on ARM they are what shows the NEON code is right.
 *
 * White-box (links the library's sources): the equalizer and the varispeed resampler are
 * internal. */
#include "fa_internal.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "test_util.h"

#define PI 3.14159265358979323846
#define RATE 48000

/* A cheap deterministic noise in [-0.5, 0.5). */
static float noise(uint32_t* s)
{
    *s = *s * 1664525u + 1013904223u;
    return (float)(*s >> 8) / 16777216.0f - 0.5f;
}

/* The equalizer's recursion (transposed direct form II) in double precision, from the
 * coefficients the library designed. */
static void reference_bands(const fa_eq_state* st, uint32_t first, uint32_t last, double* buf, int64_t frames)
{
    uint32_t i;
    int64_t j;
    int c;
    for (i = first; i < last; i++) {
        const fa_eq_bandstate* b = &st->band[i];
        double z0[2] = { 0, 0 }, z1[2] = { 0, 0 };
        if (b->g == 0.0f) continue;
        for (j = 0; j < frames; j++) {
            for (c = 0; c < 2; c++) {
                double x = buf[j * 2 + c], y = (double)b->b0 * x + z0[c];
                z0[c] = (double)b->b1 * x - (double)b->a1 * y + z1[c];
                z1[c] = (double)b->b2 * x - (double)b->a2 * y;
                buf[j * 2 + c] = y;
            }
        }
    }
}

static void test_eq_matches_double_precision(void)
{
    enum { FRAMES = 4096 };
    static float in[FRAMES * 2], out[FRAMES * 2];
    static double ref[FRAMES * 2];
    static fa_eq_shared sh;
    static fa_eq_state st;
    const fa_eq_band bands[3] = { { FA_EQ_PEAK, 1000.0f, 1.41f, 6.0f }, { FA_EQ_LOW_SHELF, 120.0f, 0.71f, -5.0f }, { FA_EQ_PEAK, 6000.0f, 2.0f, 4.0f } };
    uint32_t seed = 12345u;
    int64_t i;
    double worst = 0.0;
    fa_eq_shared_init(&sh);
    fa_eq_shared_set(&sh, bands, 3);
    memset(&st, 0, sizeof(st));
    CHECK(fa_eq_begin(&st, &sh, RATE, 1));
    /* Let the bands glide in on silence first: until the coefficients are designed and steady
     * the filter is all zeros and the comparison below would pass without testing anything. */
    memset(out, 0, sizeof(out));
    for (i = 0; i < 20; i++) fa_eq_process(&st, out, FRAMES);
    /* Different left and right, so a mixed-up lane shows. */
    for (i = 0; i < FRAMES; i++) {
        in[i * 2] = noise(&seed);
        in[i * 2 + 1] = 0.5f * (float)sin(2.0 * PI * 440.0 * (double)i / RATE) + 0.25f * noise(&seed);
    }
    memcpy(out, in, sizeof(in));
    fa_eq_process(&st, out, FRAMES);
    for (i = 0; i < FRAMES * 2; i++) ref[i] = (double)in[i];
    reference_bands(&st, 0, FA_EQ_MAX_BANDS, ref, FRAMES);
    for (i = 0; i < FRAMES * 2; i++) {
        double d = fabs((double)out[i] - ref[i]);
        if (d > worst) worst = d;
    }
    CHECK(worst < 2e-4);
    CHECK(fabs((double)out[FRAMES]) > 1e-3 || fabs((double)out[FRAMES + 1]) > 1e-3);   /* not silent */
}

/* One block at a time gives the same result as one call: the state a call leaves is the
 * state the next one needs (the vector code keeps it in registers and writes it back). */
static void test_eq_blocks_equal_one_call(void)
{
    enum { FRAMES = 2048 };
    static float in[FRAMES * 2], whole[FRAMES * 2], parts[FRAMES * 2];
    static fa_eq_shared sh;
    static fa_eq_state a, b;
    const fa_eq_band bands[2] = { { FA_EQ_PEAK, 800.0f, 1.0f, 8.0f }, { FA_EQ_LOW_SHELF, 200.0f, 0.71f, 6.0f } };
    uint32_t seed = 99u;
    int64_t i, at;
    double worst = 0.0;
    fa_eq_shared_init(&sh);
    fa_eq_shared_set(&sh, bands, 2);
    memset(&a, 0, sizeof(a));
    memset(&b, 0, sizeof(b));
    CHECK(fa_eq_begin(&a, &sh, RATE, 1));
    CHECK(fa_eq_begin(&b, &sh, RATE, 1));
    /* Let the bands glide in (a change glides over about 10 ms) so the coefficients are steady. */
    memset(whole, 0, sizeof(whole));
    for (at = 0; at < 20; at++) {
        fa_eq_process(&a, whole, FRAMES);
        fa_eq_process(&b, whole, FRAMES);
    }
    for (i = 0; i < FRAMES * 2; i++) in[i] = noise(&seed);
    memcpy(whole, in, sizeof(in));
    memcpy(parts, in, sizeof(in));
    fa_eq_process(&a, whole, FRAMES);
    for (at = 0; at < FRAMES; at += 64) fa_eq_process(&b, parts + at * 2, 64);
    for (i = 0; i < FRAMES * 2; i++) {
        double d = fabs((double)whole[i] - (double)parts[i]);
        if (d > worst) worst = d;
    }
    CHECK(worst < 1e-5);
}

/* The varispeed resampler on a sine at several channel counts: every channel is the same
 * sine (so all must come out equal: a wrong lane or a short load shows), and it matches the
 * ideal resampled sine to the kernel's accuracy. */
static void run_varispeed(uint32_t ch, double ratio)
{
    enum { IN = 6000 };
    static float in[IN * 8], out[IN * 8];
    const double f = 1000.0;
    fa_varispeed v;
    int64_t i, got, margin = fa_varispeed_width(ratio) + 4;
    uint32_t c;
    double worst = 0.0, worst_lane = 0.0;
    for (i = 0; i < IN; i++)
        for (c = 0; c < ch; c++) in[i * ch + c] = (float)(0.5 * sin(2.0 * PI * f * (double)i / RATE));
    CHECK_EQ(fa_varispeed_init(&v, ch), FA_OK);
    CHECK_EQ(fa_varispeed_write(&v, in, IN), FA_OK);
    got = fa_varispeed_read(&v, ratio, out, IN);
    CHECK(got > 1000);
    for (i = 0; i < got; i++) {
        /* The output frame i is at input time i * ratio (the position starts at 0). */
        double want = 0.5 * sin(2.0 * PI * f * ((double)i * ratio) / RATE);
        double d = fabs((double)out[i * ch] - want);
        if ((double)i * ratio > (double)margin && d > worst) worst = d;
        for (c = 1; c < ch; c++) {
            double l = fabs((double)out[i * ch + c] - (double)out[i * ch]);
            if (l > worst_lane) worst_lane = l;
        }
    }
    fa_varispeed_free(&v);
    CHECK(worst < 2e-3);
    CHECK(worst_lane < 1e-6);
}

static void test_varispeed_all_channel_counts(void)
{
    uint32_t ch;
    for (ch = 1; ch <= 8; ch++) {
        run_varispeed(ch, 1.0);
        run_varispeed(ch, 0.75);
        run_varispeed(ch, 1.5);
    }
}

int main(void)
{
    RUN(test_eq_matches_double_precision);
    RUN(test_eq_blocks_equal_one_call);
    RUN(test_varispeed_all_channel_counts);
    if (g_failures) { printf("%d failure(s)\n", g_failures); return 1; }
    printf("PASS\n");
    return 0;
}
