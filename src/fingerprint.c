/* Content keys for the caches (fa_scan_cache_key): a fingerprint of a file's audio,
 * not of its name and not of its tags, so a song moved, renamed or retagged keeps
 * its cached loudness and analysis.
 *
 * First the audio's byte range is found, leaving out what tag editors change:
 *   - ID3v2 at the start; ID3v1, APEv2 and Lyrics3 at the end (MP3, AAC, ...);
 *   - FLAC: the metadata blocks (Vorbis comments, pictures, padding);
 *   - WAV / RF64 / AIFF: only the "data" / "SSND" chunk and the "fmt " / "COMM" chunk
 *     that says how it sounds (LIST, id3 and other chunks are left out);
 *   - MP4 / M4A / MOV: only the first "mdat" box (tags live in "moov");
 *   - Ogg (Vorbis, Opus, FLAC): from the first page with audio (the comment header
 *     sits in the pages before it);
 *   - anything else: the whole file less the tag blocks at either end.
 * Uncompressed audio (WAV, AIFF) up to 1 GB is hashed whole: an edit that keeps its
 * length changes some sample somewhere. Other audio is hashed at 16 points spread
 * evenly over it, 4 KB each, plus its length: a coded stream changes throughout when
 * it is encoded again. */
#include "fa_internal.h"
#include <stdlib.h>
#include <string.h>

#define FP_POINTS 16
#define FP_POINT_BYTES 4096
#define FP_WHOLE_MAX ((int64_t)1 << 30)
#define FP_WHOLE_READ (1 << 20)
#define FP_OGG_MAX_PAGES 4096

#define FP_FORMAT_MAX 64        /* the "fmt " / "COMM" chunk bytes that go into the key */

/* Reads exactly n bytes at `at`; 0 when they are not all there. */
static int read_at(fa_source* src, int64_t at, uint8_t* buf, size_t n)
{
    if (at < 0 || fa_source_seek(src, at) != FA_OK) return 0;
    return fa_source_read(src, buf, n) == (int64_t)n;
}

/* 8 bytes at a time: fast enough to hash a whole WAV at disk speed. */
static uint64_t hash_bytes(uint64_t h, const uint8_t* p, size_t n)
{
    size_t i = 0;
    for (; i + 8 <= n; i += 8) {
        h = (h ^ fa_le64(p + i)) * 0x9E3779B97F4A7C15ULL;
        h ^= h >> 29;
    }
    for (; i < n; i++) h = (h ^ p[i]) * 0x100000001b3ULL;
    return h;
}

/* ---- Where the audio is ----------------------------------------------------------- */

static void skip_id3v2(fa_source* src, int64_t* start, int64_t end)
{
    uint8_t h[10];
    while (*start + 10 <= end && read_at(src, *start, h, 10) && memcmp(h, "ID3", 3) == 0 &&
           !((h[6] | h[7] | h[8] | h[9]) & 0x80)) {
        int64_t size = (int64_t)h[6] << 21 | (int64_t)h[7] << 14 | (int64_t)h[8] << 7 | h[9];
        *start += 10 + size + ((h[5] & 0x10) ? 10 : 0);
    }
}

static void trim_tail_tags(fa_source* src, int64_t start, int64_t* end)
{
    uint8_t b[32];
    int again = 1;
    while (again && *end > start) {
        again = 0;
        if (*end - start >= 128 && read_at(src, *end - 128, b, 3) && memcmp(b, "TAG", 3) == 0) {
            *end -= 128;                                   /* ID3v1 */
            again = 1;
            continue;
        }
        if (*end - start >= 32 && read_at(src, *end - 32, b, 32) && memcmp(b, "APETAGEX", 8) == 0) {
            int64_t size = fa_le32(b + 12);                   /* items + footer */
            int has_header = (fa_le32(b + 20) & 0x80000000u) != 0;
            *end -= size + (has_header ? 32 : 0);
            again = 1;
            continue;
        }
        if (*end - start >= 15 && read_at(src, *end - 15, b, 15) && memcmp(b + 6, "LYRICS200", 9) == 0) {
            int64_t size = 0;
            int i;
            for (i = 0; i < 6; i++) {
                if (b[i] < '0' || b[i] > '9') { size = -1; break; }
                size = size * 10 + (b[i] - '0');
            }
            if (size >= 0) { *end -= size + 15; again = 1; }
        }
    }
    if (*end < start) *end = start;
}

/* RIFF/RF64 "data", AIFF "SSND": the chunk's samples. 1 when found. *fmt_at and
 * *fmt_len give the "fmt " / "COMM" chunk's body (rate, channels, sample format),
 * which says how the samples sound: it goes into the key too (0 length if not seen). */
static int find_pcm(fa_source* src, int64_t len, const uint8_t* head, int64_t* start, int64_t* end, int64_t* fmt_at,
                    int64_t* fmt_len)
{
    int riff = (memcmp(head, "RIFF", 4) == 0 || memcmp(head, "RF64", 4) == 0) && memcmp(head + 8, "WAVE", 4) == 0;
    int aiff = memcmp(head, "FORM", 4) == 0 && (memcmp(head + 8, "AIFF", 4) == 0 || memcmp(head + 8, "AIFC", 4) == 0);
    int64_t pos = 12;
    uint8_t c[16];
    if (!riff && !aiff) return 0;
    while (pos + 8 <= len && read_at(src, pos, c, 8)) {
        int64_t size = riff ? fa_le32(c + 4) : fa_be32(c + 4);
        if (memcmp(c, riff ? "fmt " : "COMM", 4) == 0) {
            *fmt_at = pos + 8;
            *fmt_len = size < FP_FORMAT_MAX ? size : FP_FORMAT_MAX;
        }
        if (riff && memcmp(c, "data", 4) == 0) {
            *start = pos + 8;
            *end = size == 0xFFFFFFFFu || pos + 8 + size > len ? len : pos + 8 + size;   /* RF64: to the end */
            return 1;
        }
        if (aiff && memcmp(c, "SSND", 4) == 0 && read_at(src, pos + 8, c + 8, 8)) {
            *start = pos + 16 + fa_be32(c + 8);               /* past offset and block size */
            *end = pos + 8 + size > len ? len : pos + 8 + size;
            if (*start > *end) *start = *end;
            return 1;
        }
        pos += 8 + size + (size & 1);
    }
    return 0;
}

static int find_flac(fa_source* src, int64_t* start, int64_t end)
{
    uint8_t b[4];
    int64_t pos = *start;
    if (!read_at(src, pos, b, 4) || memcmp(b, "fLaC", 4) != 0) return 0;
    pos += 4;
    while (pos + 4 <= end && read_at(src, pos, b, 4)) {
        pos += 4 + ((int64_t)b[1] << 16 | (int64_t)b[2] << 8 | b[3]);
        if (b[0] & 0x80) break;                            /* the last metadata block */
    }
    *start = pos < end ? pos : end;
    return 1;
}

static int find_mdat(fa_source* src, int64_t len, int64_t* start, int64_t* end)
{
    int64_t pos = 0;
    uint8_t b[16];
    int boxes = 0;
    while (pos + 8 <= len && boxes++ < 64 && read_at(src, pos, b, 8)) {
        int64_t size = fa_be32(b), hdr = 8;
        if (size == 1) {
            if (!read_at(src, pos + 8, b + 8, 8)) return 0;
            size = (int64_t)fa_be64(b + 8);
            hdr = 16;
        } else if (size == 0) {
            size = len - pos;
        }
        if (size < hdr) return 0;
        if (memcmp(b + 4, "mdat", 4) == 0) {
            *start = pos + hdr;
            *end = pos + size > len ? len : pos + size;
            return 1;
        }
        pos += size;
    }
    return 0;
}

/* Past the pages before the first one with a granule position (the codec headers). */
static int find_ogg(fa_source* src, int64_t* start, int64_t end)
{
    int64_t pos = *start;
    uint8_t h[27 + 255];
    int pages;
    if (!read_at(src, pos, h, 4) || memcmp(h, "OggS", 4) != 0) return 0;
    for (pages = 0; pages < FP_OGG_MAX_PAGES && pos + 27 <= end; pages++) {
        int64_t body = 0;
        int i;
        if (!read_at(src, pos, h, 27) || memcmp(h, "OggS", 4) != 0) break;
        if (!read_at(src, pos + 27, h + 27, h[26])) break;
        for (i = 0; i < h[26]; i++) body += h[27 + i];
        if (fa_le64(h + 6) != 0 && fa_le64(h + 6) != UINT64_MAX) { *start = pos; return 1; }
        pos += 27 + h[26] + body;
    }
    return 1;   /* no audio page found: keep the whole range */
}

/* ---- The key ------------------------------------------------------------------------ */

int fa_content_key(fa_source* src, uint64_t* out_key, int64_t* out_size)
{
    int64_t len = src->length, start = 0, end, n;
    uint8_t head[16];
    uint8_t* buf;
    uint64_t h = 0xcbf29ce484222325ULL;
    int64_t fmt_at = 0, fmt_len = 0;
    int whole = 0, i;
    if (len <= 0) return 0;
    end = len;
    memset(head, 0, sizeof(head));
    read_at(src, 0, head, len < 16 ? (size_t)len : 16);
    if (find_pcm(src, len, head, &start, &end, &fmt_at, &fmt_len)) {
        whole = end - start <= FP_WHOLE_MAX;
    } else if (memcmp(head + 4, "ftyp", 4) == 0 && find_mdat(src, len, &start, &end)) {
        /* MP4 family: the samples alone */
    } else {
        start = 0;
        end = len;
        skip_id3v2(src, &start, end);
        trim_tail_tags(src, start, &end);
        if (!find_flac(src, &start, end)) find_ogg(src, &start, end);
    }
    n = end - start;
    if (n <= 0) { start = 0; n = len; }               /* nothing left: the file as it is */
    buf = (uint8_t*)malloc(FP_WHOLE_READ);
    if (!buf) return 0;
    {
        uint8_t nb[8];
        for (i = 0; i < 8; i++) nb[i] = (uint8_t)((uint64_t)n >> (i * 8));
        h = hash_bytes(h, nb, 8);
    }
    if (fmt_len > 0 && read_at(src, fmt_at, buf, (size_t)fmt_len)) h = hash_bytes(h, buf, (size_t)fmt_len);
    if (whole || n <= FP_POINTS * FP_POINT_BYTES) {
        int64_t at = 0;
        if (fa_source_seek(src, start) != FA_OK) { free(buf); return 0; }
        while (at < n) {
            size_t want = n - at < FP_WHOLE_READ ? (size_t)(n - at) : FP_WHOLE_READ;
            int64_t got = fa_source_read(src, buf, want);
            if (got <= 0) { free(buf); return 0; }   /* a read error: no key, not one of part of it */
            h = hash_bytes(h, buf, (size_t)got);
            at += got;
        }
    } else {
        for (i = 0; i < FP_POINTS; i++) {
            int64_t at = start + (n - FP_POINT_BYTES) * i / (FP_POINTS - 1);
            if (!read_at(src, at, buf, FP_POINT_BYTES)) { free(buf); return 0; }
            h = hash_bytes(h, buf, FP_POINT_BYTES);
        }
    }
    free(buf);
    *out_key = h;
    *out_size = n;
    return 1;
}
