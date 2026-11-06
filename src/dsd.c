/* DSD (1-bit, 64 to 1024 times 44.1 or 48 kHz) to PCM, written from the textbook
 * method: two linear-phase low-pass FIR filters (Kaiser-windowed sinc) and decimation.
 *
 *   stage 1  the bits at the DSD rate fs, down to r1 = 8 x the family rate (352.8 or
 *            384 kHz). The bits are +1 or -1, so the filter is a sum of table look-ups:
 *            one 256-entry table per 8 taps, indexed by a byte of the stream.
 *   stage 2  r1 down to the output rate (r1 / 8, / 4, / 2 or / 1) in float.
 *
 * Both stop bands are 120 dB down, so the large ultrasonic noise of a DSD stream is
 * not folded into the audio. Stage 1 keeps everything that would alias into stage 2's
 * pass band out; stage 2 is strict (nothing aliases below half the output rate) at
 * 88.2 kHz and up, and at 44.1/48 kHz only keeps aliases above 20 kHz. The pass band
 * is flat (0.0001 dB) to 20 kHz at 44.1 kHz, to 30 kHz at 88.2 kHz, to 60 kHz at
 * 176.4 kHz and 120 kHz at 352.8 kHz. The output is delayed by half of each filter
 * (0.3 ms at 88.2 kHz), the same for every channel.
 *
 * Level: a bit is +-1, so 50 % modulation, the SACD reference level ("0 dB SACD"), comes
 * out at 0.5; the +6 dB gain most players use (FA_DSD_GAIN) makes that full scale. */
#include "fa_internal.h"
#include "mdct.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define DSD_ATTEN_DB 120.0
#define DSD_HIST_CHUNK 4096    /* bytes appended to a channel's history before it is moved down */
#define DSD_SILENCE 0x69       /* the DSD idle pattern (as many ones as zeros) */

struct fa_dsd_conv {
    uint32_t channels;
    uint32_t out_rate;
    uint32_t r1;               /* stage 1 output rate */
    uint32_t b1;               /* bytes per stage 1 output */
    uint32_t m2;               /* stage 1 outputs per output frame */
    uint32_t g1;               /* stage 1 taps / 8 */
    uint32_t n2;               /* stage 2 taps (0 = no stage 2, m2 == 1) */
    float* t1;                 /* g1 x 256 */
    float* h1;                 /* g1 * 8 taps, for the tests */
    float* h2;                 /* n2 taps */
    uint8_t* hist;             /* channels x (g1 + DSD_HIST_CHUNK) */
    uint32_t* hist_len;
    float* y1;                 /* channels x 2 n2: stage 1 outputs, mirrored ring */
    uint32_t y1_at;            /* write index into each ring */
};

/* Byte with its bits in the opposite order (DSF stores the oldest bit lowest). */
#define R2(n) n, n + 2 * 64, n + 1 * 64, n + 3 * 64
#define R4(n) R2(n), R2(n + 2 * 16), R2(n + 1 * 16), R2(n + 3 * 16)
#define R6(n) R4(n), R4(n + 2 * 4), R4(n + 1 * 4), R4(n + 3 * 4)
static const uint8_t g_reverse[256] = { R6(0), R6(2), R6(1), R6(3) };
#undef R2
#undef R4
#undef R6

/* Kaiser-windowed sinc, `n` taps, cut-off `fc` in cycles per sample, summing to `gain`. */
static void design(double* h, uint32_t n, double fc, double beta, double gain)
{
    double i0b = fa_bessel_i0(beta), mid = (n - 1) * 0.5, sum = 0.0;
    uint32_t i;
    for (i = 0; i < n; i++) {
        double t = i - mid, r = mid > 0 ? t / mid : 0.0;
        double x = 2.0 * FA_PI * fc * t;
        double s = t == 0.0 ? 2.0 * fc : sin(x) / (FA_PI * t);
        double w = fa_bessel_i0(beta * sqrt(fmax(0.0, 1.0 - r * r))) / i0b;
        h[i] = s * w;
        sum += h[i];
    }
    for (i = 0; i < n; i++) h[i] *= gain / sum;
}

/* Kaiser's estimate of the taps for `atten` dB over a transition `df` (cycles per sample) wide. */
static uint32_t taps_for(double atten, double df)
{
    return (uint32_t)ceil((atten - 7.95) / (14.36 * df)) + 1;
}

int fa_dsd_rate_family(uint32_t dsd_rate, uint32_t* out_base)
{
    static const uint32_t bases[2] = { 44100, 48000 };
    int b;
    for (b = 0; b < 2; b++) {
        uint32_t m;
        if (dsd_rate % bases[b]) continue;
        m = dsd_rate / bases[b];
        if (m == 64 || m == 128 || m == 256 || m == 512 || m == 1024) {
            if (out_base) *out_base = bases[b];
            return (int)m;
        }
    }
    return 0;
}

uint32_t fa_dsd_out_rate(uint32_t dsd_rate, uint32_t want)
{
    uint32_t base, j, best = 1;
    double err = 1e30;
    if (!fa_dsd_rate_family(dsd_rate, &base)) return 0;
    if (want == 0) return base * 2;
    for (j = 0; j < 4; j++) {
        double e = fabs(log((double)(base << j) / (double)want));
        if (e < err - 1e-9) { err = e; best = j; }
    }
    return base << best;
}

fa_result fa_dsd_conv_create(uint32_t dsd_rate, uint32_t channels, uint32_t want_rate, fa_dsd_conv** out)
{
    fa_dsd_conv* c;
    uint32_t base, m, out_rate, n1, i, g, b;
    double fp, stop1, stop2 = 0.0, beta = 0.1102 * (DSD_ATTEN_DB - 8.7), *h = NULL;
    *out = NULL;
    m = (uint32_t)fa_dsd_rate_family(dsd_rate, &base);
    if (!m || channels == 0 || channels > 32) return FA_ERROR_UNSUPPORTED_FORMAT;
    out_rate = fa_dsd_out_rate(dsd_rate, want_rate);
    c = (fa_dsd_conv*)calloc(1, sizeof(*c));
    if (!c) return FA_ERROR_OUT_OF_MEMORY;
    c->channels = channels;
    c->out_rate = out_rate;
    c->r1 = base * 8;
    c->b1 = m / 64;
    c->m2 = c->r1 / out_rate;
    fp = out_rate <= 48000 ? 0.4535 * out_rate : 0.34 * out_rate;
    if (c->m2 > 1) {
        stop2 = out_rate <= 48000 ? out_rate - fp : out_rate * 0.5;
        stop1 = c->r1 - stop2;
        c->n2 = taps_for(DSD_ATTEN_DB, (stop2 - fp) / c->r1);
    } else {
        stop1 = c->r1 * 0.5;
    }
    n1 = taps_for(DSD_ATTEN_DB, (stop1 - fp) / dsd_rate);
    n1 = (n1 + 7) & ~7u;
    c->g1 = n1 / 8;
    h = (double*)malloc(sizeof(double) * (n1 > c->n2 ? n1 : c->n2));
    c->t1 = (float*)malloc(sizeof(float) * 256 * c->g1);
    c->h1 = (float*)malloc(sizeof(float) * n1);
    c->h2 = (float*)malloc(sizeof(float) * (c->n2 ? c->n2 : 1));
    c->hist = (uint8_t*)malloc((size_t)channels * (c->g1 + DSD_HIST_CHUNK));
    c->hist_len = (uint32_t*)malloc(sizeof(uint32_t) * channels);
    c->y1 = (float*)malloc(sizeof(float) * channels * 2 * (c->n2 ? c->n2 : 1));
    if (!h || !c->t1 || !c->h1 || !c->h2 || !c->hist || !c->hist_len || !c->y1) {
        free(h);
        fa_dsd_conv_free(c);
        return FA_ERROR_OUT_OF_MEMORY;
    }
    /* Stage 1, with the level: tap 8g + i weighs the bit i places before the newest of
     * byte g back, and the newest bit of a byte is its lowest (bytes are MSB first). */
    design(h, n1, (fp + stop1) * 0.5 / dsd_rate, beta, FA_DSD_GAIN);
    for (i = 0; i < n1; i++) c->h1[i] = (float)h[i];
    for (g = 0; g < c->g1; g++)
        for (b = 0; b < 256; b++) {
            double s = 0.0;
            for (i = 0; i < 8; i++) s += (b >> i) & 1 ? h[8 * g + i] : -h[8 * g + i];
            c->t1[g * 256 + b] = (float)s;
        }
    if (c->n2) {
        design(h, c->n2, (fp + stop2) * 0.5 / c->r1, beta, 1.0);
        for (i = 0; i < c->n2; i++) c->h2[i] = (float)h[i];
    }
    free(h);
    fa_dsd_conv_reset(c);
    *out = c;
    return FA_OK;
}

void fa_dsd_conv_free(fa_dsd_conv* c)
{
    if (!c) return;
    free(c->t1);
    free(c->h1);
    free(c->h2);
    free(c->hist);
    free(c->hist_len);
    free(c->y1);
    free(c);
}

void fa_dsd_conv_reset(fa_dsd_conv* c)
{
    uint32_t ch;
    for (ch = 0; ch < c->channels; ch++) {
        memset(c->hist + (size_t)ch * (c->g1 + DSD_HIST_CHUNK), DSD_SILENCE, c->g1);
        c->hist_len[ch] = c->g1;
    }
    if (c->n2) memset(c->y1, 0, sizeof(float) * c->channels * 2 * c->n2);
    c->y1_at = 0;
}

uint32_t fa_dsd_conv_rate(const fa_dsd_conv* c) { return c->out_rate; }
uint32_t fa_dsd_conv_frame_bytes(const fa_dsd_conv* c) { return c->b1 * c->m2; }

uint32_t fa_dsd_conv_preroll(const fa_dsd_conv* c)
{
    /* Output frames after a reset whose filters still hold the silence it put there. */
    uint32_t y1s = c->n2 + (c->g1 + c->b1 - 1) / c->b1;
    return (y1s + c->m2 - 1) / c->m2 + 1;
}

void fa_dsd_conv_filters(const fa_dsd_conv* c, const float** h1, uint32_t* n1, const float** h2, uint32_t* n2, uint32_t* r1)
{
    *h1 = c->h1;
    *n1 = c->g1 * 8;
    *h2 = c->h2;
    *n2 = c->n2;
    *r1 = c->r1;
}

void fa_dsd_conv_run(fa_dsd_conv* c, const uint8_t* const* planes, size_t stride, int lsb_first, size_t frames, float* out)
{
    const uint32_t g1 = c->g1, b1 = c->b1, m2 = c->m2, n2 = c->n2, nch = c->channels;
    const uint8_t* rev = lsb_first ? g_reverse : NULL;
    uint32_t at0 = c->y1_at, ch, at = at0;
    for (ch = 0; ch < nch; ch++) {
        uint8_t* hist = c->hist + (size_t)ch * (g1 + DSD_HIST_CHUNK);
        uint32_t len = c->hist_len[ch];
        float* ring = c->y1 + (size_t)ch * 2 * (n2 ? n2 : 1);
        const uint8_t* src = planes[ch];
        size_t f;
        at = at0;
        for (f = 0; f < frames; f++) {
            uint32_t k;
            float y = 0.0f;
            for (k = 0; k < m2; k++) {
                const uint8_t* last;
                const float* t = c->t1;
                uint32_t j, g;
                if (len + b1 > g1 + DSD_HIST_CHUNK) {
                    memmove(hist, hist + len - g1, g1);
                    len = g1;
                }
                for (j = 0; j < b1; j++, src += stride) hist[len++] = rev ? rev[*src] : *src;
                last = hist + len - 1;
                y = 0.0f;
                for (g = 0; g < g1; g++, t += 256) y += t[last[-(ptrdiff_t)g]];
                if (n2) {
                    ring[at] = ring[at + n2] = y;
                    if (++at == n2) at = 0;
                }
            }
            if (n2) {
                /* ring[at .. at + n2) runs oldest to newest; the filter is symmetric
                 * (h[i] == h[n2 - 1 - i]), so the two ends share a multiply. */
                const float* x = ring + at;
                const float* xe = x + n2 - 1;
                const float* h = c->h2;
                const uint32_t half = n2 / 2;
                float s0 = 0.0f, s1 = 0.0f, s2 = 0.0f, s3 = 0.0f;
                uint32_t i = 0;
                for (; i + 4 <= half; i += 4) {
                    s0 += h[i] * (x[i] + xe[-(ptrdiff_t)i]);
                    s1 += h[i + 1] * (x[i + 1] + xe[-(ptrdiff_t)i - 1]);
                    s2 += h[i + 2] * (x[i + 2] + xe[-(ptrdiff_t)i - 2]);
                    s3 += h[i + 3] * (x[i + 3] + xe[-(ptrdiff_t)i - 3]);
                }
                for (; i < half; i++) s0 += h[i] * (x[i] + xe[-(ptrdiff_t)i]);
                if (n2 & 1) s0 += h[half] * x[half];
                y = (s0 + s1) + (s2 + s3);
            }
            out[f * nch + ch] = y;
        }
        c->hist_len[ch] = len;
    }
    c->y1_at = at;
}
