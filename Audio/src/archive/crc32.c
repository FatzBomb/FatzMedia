/* CRC-32 (IEEE 802.3, reflected), the checksum of ZIP, gzip, PNG and 7z.
 * A slice-by-8 table version everywhere (about 1.7 GB/s), and where the CPU has
 * the instructions a faster one: carry-less multiplication folding on x86-64
 * (PCLMULQDQ, Intel's "Fast CRC computation using PCLMULQDQ", about 10 GB/s) and the
 * CRC32 instructions of ARMv8 when the build targets them. The x86 version is
 * checked against the table version once per process, so a CPU or compiler that
 * gets it wrong falls back to the table. */
#include "cdg.h"
#include <string.h>

#if (defined(__x86_64__) || defined(_M_X64)) && !defined(FA_NO_SIMD_CRC)
#  define FA_CRC_PCLMUL 1
#  if defined(_MSC_VER) && !defined(__clang__)
#    include <intrin.h>
#    include <nmmintrin.h>
#    include <wmmintrin.h>
#    define FA_CRC_TARGET
#  else
#    include <immintrin.h>
#    define FA_CRC_TARGET __attribute__((target("pclmul,sse4.1")))
#  endif
#endif
#if defined(__ARM_FEATURE_CRC32) && defined(__aarch64__)
#  include <arm_acle.h>
#  define FA_CRC_ARM 1
#endif

static uint32_t crc_tab[8][256];
static fa_atomic32 crc_state;
static int crc_fast;   /* 0 table only, 1 PCLMUL, 2 ARM */

static uint32_t crc_table_update(uint32_t crc, const uint8_t* data, size_t size)
{
    for (; size >= 8; data += 8, size -= 8) {
        uint32_t a = crc ^ fa_le32(data), b = fa_le32(data + 4);
        crc = crc_tab[7][a & 0xFF] ^ crc_tab[6][(a >> 8) & 0xFF] ^ crc_tab[5][(a >> 16) & 0xFF] ^ crc_tab[4][a >> 24] ^
              crc_tab[3][b & 0xFF] ^ crc_tab[2][(b >> 8) & 0xFF] ^ crc_tab[1][(b >> 16) & 0xFF] ^ crc_tab[0][b >> 24];
    }
    while (size--) crc = crc_tab[0][(crc ^ *data++) & 0xFF] ^ (crc >> 8);
    return crc;
}

#if defined(FA_CRC_PCLMUL)
/* `crc` is the running register (already inverted); len >= 64 and a multiple of 16. */
FA_CRC_TARGET
static uint32_t crc_pclmul(uint32_t crc, const uint8_t* buf, size_t len)
{
    static const uint64_t k1k2[2] = { 0x0154442bd4ull, 0x01c6e41596ull };
    static const uint64_t k3k4[2] = { 0x01751997d0ull, 0x00ccaa009eull };
    static const uint64_t k5k0[2] = { 0x0163cd6124ull, 0 };
    static const uint64_t poly[2] = { 0x01db710641ull, 0x01f7011641ull };
    __m128i x0, x1, x2, x3, x4, x5, x6, x7, x8, y5, y6, y7, y8;
    x1 = _mm_loadu_si128((const __m128i*)(buf + 0x00));
    x2 = _mm_loadu_si128((const __m128i*)(buf + 0x10));
    x3 = _mm_loadu_si128((const __m128i*)(buf + 0x20));
    x4 = _mm_loadu_si128((const __m128i*)(buf + 0x30));
    x1 = _mm_xor_si128(x1, _mm_cvtsi32_si128((int)crc));
    x0 = _mm_loadu_si128((const __m128i*)k1k2);
    buf += 64;
    len -= 64;
    while (len >= 64) {
        x5 = _mm_clmulepi64_si128(x1, x0, 0x00);
        x6 = _mm_clmulepi64_si128(x2, x0, 0x00);
        x7 = _mm_clmulepi64_si128(x3, x0, 0x00);
        x8 = _mm_clmulepi64_si128(x4, x0, 0x00);
        x1 = _mm_clmulepi64_si128(x1, x0, 0x11);
        x2 = _mm_clmulepi64_si128(x2, x0, 0x11);
        x3 = _mm_clmulepi64_si128(x3, x0, 0x11);
        x4 = _mm_clmulepi64_si128(x4, x0, 0x11);
        y5 = _mm_loadu_si128((const __m128i*)(buf + 0x00));
        y6 = _mm_loadu_si128((const __m128i*)(buf + 0x10));
        y7 = _mm_loadu_si128((const __m128i*)(buf + 0x20));
        y8 = _mm_loadu_si128((const __m128i*)(buf + 0x30));
        x1 = _mm_xor_si128(_mm_xor_si128(x1, x5), y5);
        x2 = _mm_xor_si128(_mm_xor_si128(x2, x6), y6);
        x3 = _mm_xor_si128(_mm_xor_si128(x3, x7), y7);
        x4 = _mm_xor_si128(_mm_xor_si128(x4, x8), y8);
        buf += 64;
        len -= 64;
    }
    /* Four lanes into one. */
    x0 = _mm_loadu_si128((const __m128i*)k3k4);
    x5 = _mm_clmulepi64_si128(x1, x0, 0x00);
    x1 = _mm_clmulepi64_si128(x1, x0, 0x11);
    x1 = _mm_xor_si128(_mm_xor_si128(x1, x2), x5);
    x5 = _mm_clmulepi64_si128(x1, x0, 0x00);
    x1 = _mm_clmulepi64_si128(x1, x0, 0x11);
    x1 = _mm_xor_si128(_mm_xor_si128(x1, x3), x5);
    x5 = _mm_clmulepi64_si128(x1, x0, 0x00);
    x1 = _mm_clmulepi64_si128(x1, x0, 0x11);
    x1 = _mm_xor_si128(_mm_xor_si128(x1, x4), x5);
    while (len >= 16) {
        x2 = _mm_loadu_si128((const __m128i*)buf);
        x5 = _mm_clmulepi64_si128(x1, x0, 0x00);
        x1 = _mm_clmulepi64_si128(x1, x0, 0x11);
        x1 = _mm_xor_si128(_mm_xor_si128(x1, x2), x5);
        buf += 16;
        len -= 16;
    }
    /* 128 bits to 64. */
    x2 = _mm_clmulepi64_si128(x1, x0, 0x10);
    x3 = _mm_setr_epi32(~0, 0, ~0, 0);
    x1 = _mm_srli_si128(x1, 8);
    x1 = _mm_xor_si128(x1, x2);
    x0 = _mm_loadl_epi64((const __m128i*)k5k0);
    x2 = _mm_srli_si128(x1, 4);
    x1 = _mm_and_si128(x1, x3);
    x1 = _mm_clmulepi64_si128(x1, x0, 0x00);
    x1 = _mm_xor_si128(x1, x2);
    /* Barrett reduction to 32 bits. */
    x0 = _mm_loadu_si128((const __m128i*)poly);
    x2 = _mm_and_si128(x1, x3);
    x2 = _mm_clmulepi64_si128(x2, x0, 0x10);
    x2 = _mm_and_si128(x2, x3);
    x2 = _mm_clmulepi64_si128(x2, x0, 0x00);
    x1 = _mm_xor_si128(x1, x2);
    return (uint32_t)_mm_extract_epi32(x1, 1);
}

static int cpu_has_pclmul(void)
{
#  if defined(_MSC_VER) && !defined(__clang__)
    int r[4];
    __cpuid(r, 1);
    return (r[2] & (1 << 1)) && (r[2] & (1 << 19));   /* PCLMULQDQ, SSE4.1 */
#  else
    return __builtin_cpu_supports("pclmul") && __builtin_cpu_supports("sse4.1");
#  endif
}
#endif

#if defined(FA_CRC_ARM)
static uint32_t crc_arm(uint32_t crc, const uint8_t* data, size_t size)
{
    for (; size >= 8; data += 8, size -= 8) {
        uint64_t v;
        memcpy(&v, data, 8);
        crc = __crc32d(crc, v);
    }
    while (size--) crc = __crc32b(crc, *data++);
    return crc;
}
#endif

static int crc_build(void* ctx)
{
    uint32_t n, k, c;
    (void)ctx;
    for (n = 0; n < 256; n++) {
        c = n;
        for (k = 0; k < 8; k++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        crc_tab[0][n] = c;
    }
    for (n = 0; n < 256; n++)
        for (k = 1; k < 8; k++) crc_tab[k][n] = (crc_tab[k - 1][n] >> 8) ^ crc_tab[0][crc_tab[k - 1][n] & 0xFF];
#if defined(FA_CRC_PCLMUL)
    if (cpu_has_pclmul()) {
        /* Prove it on a pattern of every length class before trusting it. */
        uint8_t probe[1000];
        uint32_t i, ok = 1;
        size_t chunk;
        for (i = 0; i < sizeof(probe); i++) probe[i] = (uint8_t)(i * 131u + (i >> 3));
        for (chunk = 64; chunk <= 992 && ok; chunk += 16)
            if (crc_pclmul(0xFFFFFFFFu, probe, chunk) != crc_table_update(0xFFFFFFFFu, probe, chunk)) ok = 0;
        if (ok) crc_fast = 1;
    }
#endif
#if defined(FA_CRC_ARM)
    crc_fast = 2;
#endif
    return 1;
}

uint32_t fa_crc32_update(uint32_t crc, const uint8_t* data, size_t size)
{
    fa_once(&crc_state, crc_build, NULL);
    crc ^= 0xFFFFFFFFu;
#if defined(FA_CRC_PCLMUL)
    if (crc_fast == 1 && size >= 64) {
        size_t chunk = size & ~(size_t)15;
        crc = crc_pclmul(crc, data, chunk);
        data += chunk;
        size -= chunk;
    }
#endif
#if defined(FA_CRC_ARM)
    if (crc_fast == 2) return crc_arm(crc, data, size) ^ 0xFFFFFFFFu;
#endif
    return crc_table_update(crc, data, size) ^ 0xFFFFFFFFu;
}

uint32_t fa_crc32(const uint8_t* data, size_t size) { return fa_crc32_update(0, data, size); }
