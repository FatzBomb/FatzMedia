/* Small helpers shared across the library, with no dependencies of their own (the
 * surface code uses them without the rest of fa_internal.h). */
#ifndef FA_UTIL_H
#define FA_UTIL_H

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* Byte-order readers. */
static inline uint16_t fa_be16(const uint8_t* p) { return (uint16_t)((p[0] << 8) | p[1]); }
static inline uint32_t fa_be24(const uint8_t* p) { return ((uint32_t)p[0] << 16) | ((uint32_t)p[1] << 8) | p[2]; }
static inline uint32_t fa_be32(const uint8_t* p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}
static inline uint64_t fa_be64(const uint8_t* p) { return ((uint64_t)fa_be32(p) << 32) | fa_be32(p + 4); }
static inline uint16_t fa_le16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static inline uint32_t fa_le32(const uint8_t* p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static inline uint64_t fa_le64(const uint8_t* p) { return (uint64_t)fa_le32(p) | ((uint64_t)fa_le32(p + 4) << 32); }
/* Byte-order writers (the counterparts of the readers above). */
static inline void fa_put_le16(uint8_t* p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static inline void fa_put_le32(uint8_t* p, uint32_t v)
{
    p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24);
}
static inline void fa_put_le64(uint8_t* p, uint64_t v)
{
    fa_put_le32(p, (uint32_t)v);
    fa_put_le32(p + 4, (uint32_t)(v >> 32));
}
static inline void fa_put_be32(uint8_t* p, uint32_t v)
{
    p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16); p[2] = (uint8_t)(v >> 8); p[3] = (uint8_t)v;
}
#define FA_PI 3.14159265358979323846
#define FA_FOURCC(a, b, c, d) (((uint32_t)(a) << 24) | ((uint32_t)(b) << 16) | ((uint32_t)(c) << 8) | (uint32_t)(d))

/* ASCII case-insensitive comparison (C locale rules whatever the process locale): like strncmp, then the
 * difference of the first unequal lower-cased bytes. */
static inline int fa_strncasecmp_ascii(const char* a, const char* b, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++) {
        unsigned char x = (unsigned char)a[i], y = (unsigned char)b[i];
        if (x >= 'A' && x <= 'Z') x = (unsigned char)(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = (unsigned char)(y - 'A' + 'a');
        if (x != y) return (int)x - (int)y;
        if (!x) return 0;
    }
    return 0;
}
/* 1 if the two strings are equal ignoring ASCII case. */
static inline int fa_streq_ci(const char* a, const char* b)
{
    size_t n = strlen(a);
    return n == strlen(b) && fa_strncasecmp_ascii(a, b, n) == 0;
}
/* 1 if the first `len` bytes of `s` end with `suffix` (ASCII case-insensitive). */
static inline int fa_ends_with_ci_n(const char* s, size_t len, const char* suffix)
{
    size_t m = strlen(suffix);
    return len >= m && fa_strncasecmp_ascii(s + len - m, suffix, m) == 0;
}
static inline int fa_ends_with_ci(const char* s, const char* suffix) { return fa_ends_with_ci_n(s, strlen(s), suffix); }

/* malloc'd copy of the first `n` bytes of `s`, plus a terminator. */
static inline char* fa_strndup(const char* s, size_t n)
{
    char* d = (char*)malloc(n + 1);
    if (d) { memcpy(d, s, n); d[n] = '\0'; }
    return d;
}
/* malloc'd copy of all of `s`; NULL for NULL. */
static inline char* fa_strdup(const char* s) { return s ? fa_strndup(s, strlen(s)) : NULL; }

/* 1 if the `n` bytes at `s` are well-formed UTF-8 (RFC 3629): no overlong forms,
 * UTF-16 surrogates, code points above U+10FFFF or cut-off characters. Text that
 * fails is read in a legacy code page by the caller. */
static inline int fa_utf8_valid(const void* s, size_t n)
{
    const uint8_t* p = (const uint8_t*)s;
    size_t i = 0, k, len;
    while (i < n) {
        uint8_t c = p[i];
        if (c < 0x80) { i++; continue; }
        if (c >= 0xC2 && c <= 0xDF) len = 2;
        else if (c >= 0xE0 && c <= 0xEF) len = 3;
        else if (c >= 0xF0 && c <= 0xF4) len = 4;
        else return 0;
        if (n - i < len) return 0;
        for (k = 1; k < len; k++) if ((p[i + k] & 0xC0) != 0x80) return 0;
        if ((c == 0xE0 && p[i + 1] < 0xA0) || (c == 0xED && p[i + 1] > 0x9F) ||
            (c == 0xF0 && p[i + 1] < 0x90) || (c == 0xF4 && p[i + 1] > 0x8F)) return 0;
        i += len;
    }
    return 1;
}

/* An ID3v2 synchsafe integer: 4 bytes of 7 bits each (sizes in tag headers and ID3v2.4 frames). */
static inline uint32_t fa_synchsafe(const uint8_t* p)
{
    return ((uint32_t)(p[0] & 0x7f) << 21) | ((uint32_t)(p[1] & 0x7f) << 14) | ((uint32_t)(p[2] & 0x7f) << 7) | (p[3] & 0x7f);
}

/* Grows the array *p (elements of `elem` bytes, *cap of them allocated) to hold at least `need`:
 * the capacity doubles from `first` (the first allocation's size), with the overflow checked.
 * 1 on success; 0 when memory is short or the size does not fit, with *p and *cap unchanged. */
static inline int fa_grow(void** p, size_t* cap, size_t need, size_t elem, size_t first)
{
    size_t c = *cap ? *cap : first;
    void* q;
    if (need <= *cap) return 1;
    if (elem == 0) return 0;
    while (c < need) {
        if (c > SIZE_MAX / 2) return 0;
        c *= 2;
    }
    if (c > SIZE_MAX / elem) return 0;
    q = realloc(*p, c * elem);
    if (!q) return 0;
    *p = q;
    *cap = c;
    return 1;
}

/* Writes code point `u` (up to U+10FFFF) as UTF-8; returns the bytes written (1-4). */
static inline size_t fa_put_utf8(char* out, uint32_t u)
{
    if (u < 0x80) { out[0] = (char)u; return 1; }
    if (u < 0x800) { out[0] = (char)(0xC0 | (u >> 6)); out[1] = (char)(0x80 | (u & 63)); return 2; }
    if (u < 0x10000) {
        out[0] = (char)(0xE0 | (u >> 12)); out[1] = (char)(0x80 | ((u >> 6) & 63)); out[2] = (char)(0x80 | (u & 63));
        return 3;
    }
    out[0] = (char)(0xF0 | (u >> 18)); out[1] = (char)(0x80 | ((u >> 12) & 63));
    out[2] = (char)(0x80 | ((u >> 6) & 63)); out[3] = (char)(0x80 | (u & 63));
    return 4;
}

#endif
