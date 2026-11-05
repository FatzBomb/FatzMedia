/* Content sniffing for the decoder registry. Each probe looks only at the first
 * FA_PROBE_BYTES of the source and returns a confidence from 0 (not this format)
 * to 100 (certain). Written from the public format specifications. */
#include "fa_internal.h"
#include <string.h>

/* ID3v2: "ID3", version (2 bytes), flags, then a 28-bit "syncsafe" size that
 * excludes the 10-byte header (and the optional 10-byte footer, flag 0x10). */
size_t fa_probe_id3v2_size(const uint8_t* d, size_t n)
{
    size_t size;
    if (n < 10 || memcmp(d, "ID3", 3) != 0) return 0;
    if (d[3] == 0xFF || d[4] == 0xFF) return 0;
    if ((d[6] | d[7] | d[8] | d[9]) & 0x80) return 0;
    size = ((size_t)d[6] << 21) | ((size_t)d[7] << 14) | ((size_t)d[8] << 7) | (size_t)d[9];
    size += 10;
    if (d[5] & 0x10) size += 10;
    return size;
}

int fa_probe_wav(const uint8_t* d, size_t n, size_t id3)
{
    static const uint8_t w64_riff[16] = { 0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11,
                                          0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00 };
    (void)id3;
    if (n >= 12 && (memcmp(d, "RIFF", 4) == 0 || memcmp(d, "RIFX", 4) == 0 ||
                    memcmp(d, "RF64", 4) == 0) &&
        memcmp(d + 8, "WAVE", 4) == 0)
        return 100;
    if (n >= 16 && memcmp(d, w64_riff, 16) == 0) return 100;
    if (n >= 12 && memcmp(d, "FORM", 4) == 0 && (memcmp(d + 8, "AIFF", 4) == 0 || memcmp(d + 8, "AIFC", 4) == 0))
        return 100;
    return 0;
}

int fa_probe_flac(const uint8_t* d, size_t n, size_t id3)
{
    if (id3 < n && n - id3 >= 4 && memcmp(d + id3, "fLaC", 4) == 0) return 100;
    return 0;
}

/* ---- MPEG audio Layer III ----------------------------------------------- */

/* Bitrate in kbit/s of MPEG audio `layer` (1..3) by header index 1..14, 0 if invalid. */
unsigned fa_mpa_kbps(int mpeg1, int layer, int index)
{
    static const uint16_t tab[2][3][15] = {
        { { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },     /* MPEG-1 layer I */
          { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },        /* layer II */
          { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 } },       /* layer III */
        { { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },        /* MPEG-2/2.5 layer I */
          { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },             /* layers II and III */
          { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 } } };
    if (layer < 1 || layer > 3 || index < 0 || index > 14) return 0;
    return tab[mpeg1 ? 0 : 1][layer - 1][index];
}

static const uint32_t mp3_rates[3] = { 44100, 48000, 32000 };

/* Returns the frame length in bytes of an MPEG audio header at `h` whose layer is
 * `layer` (1..3), or 0 if invalid. Free-format frames (bitrate index 0) are
 * rejected: their length is unknowable here. */
static size_t mpa_frame_len(const uint8_t* h, int layer, uint32_t* out_rate)
{
    uint32_t version, br, sr, pad, rate, kbps;
    if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return 0;
    version = (h[1] >> 3) & 3;  /* 0 = MPEG 2.5, 1 = reserved, 2 = MPEG 2, 3 = MPEG 1 */
    if ((int)(4 - ((h[1] >> 1) & 3)) != layer) return 0;
    br = (h[2] >> 4) & 15;
    sr = (h[2] >> 2) & 3;
    pad = (h[2] >> 1) & 1;
    if (version == 1 || br == 0 || br == 15 || sr == 3) return 0;
    if ((h[3] & 3) == 2) return 0; /* reserved emphasis */
    rate = mp3_rates[sr] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
    kbps = fa_mpa_kbps(version == 3, layer, (int)br);
    /* MPEG-1 Layer II allows only some bitrate and channel-mode pairs: 32 to 56 and
     * 80 kbit/s are single channel only, 224 and up are not. */
    if (layer == 2 && version == 3) {
        int mono = (h[3] >> 6) == 3;
        if (mono ? kbps >= 224 : (kbps <= 56 || kbps == 80)) return 0;
    }
    if (out_rate) *out_rate = rate;
    if (layer == 1) return (size_t)((12u * kbps * 1000u / rate + pad) * 4u);
    if (layer == 3 && version != 3) return (size_t)(72u * kbps * 1000u / rate + pad);
    return (size_t)(144u * kbps * 1000u / rate + pad);
}

static size_t mp3_frame_len(const uint8_t* h, uint32_t* out_rate) { return mpa_frame_len(h, 3, out_rate); }

/* Content check for MPEG audio of `layer`: two chained frames of one rate. */
static int probe_mpa(const uint8_t* d, size_t n, size_t id3, int layer, int chained, int first, int later)
{
    size_t i;
    if (id3 >= n) return 0;
    for (i = id3; i + 4 <= n; i++) {
        uint32_t rate1 = 0, rate2 = 0;
        size_t len = mpa_frame_len(d + i, layer, &rate1);
        if (len == 0) continue;
        if (i + len + 4 <= n) {
            size_t len2 = mpa_frame_len(d + i + len, layer, &rate2);
            if (len2 == 0 || rate2 != rate1) continue;
            if (chained > 1) {   /* a third frame too: Layer I headers look like random bytes often */
                if (i + len + len2 + 4 <= n && mpa_frame_len(d + i + len + len2, layer, NULL) == 0) continue;
            }
            return i == id3 ? first : later;
        }
        return 0;   /* one frame in the window: not enough for Layers I and II */
    }
    return 0;
}

/* Layers I and II (MP1, MP2): .mp2, .mpa and the audio of MPEG program streams. */
int fa_probe_mp2(const uint8_t* d, size_t n, size_t id3)
{
    int s = probe_mpa(d, n, id3, 2, 1, 90, 65);
    if (s) return s;
    return probe_mpa(d, n, id3, 1, 2, 60, 35);
}

int fa_probe_mp3(const uint8_t* d, size_t n, size_t id3)
{
    size_t i;
    if (id3 >= n) return id3 > 0 ? 40 : 0; /* tag larger than the probe window; likely MP3 */
    for (i = id3; i + 4 <= n; i++) {
        uint32_t rate1 = 0, rate2 = 0;
        size_t len = mp3_frame_len(d + i, &rate1);
        if (len == 0) continue;
        if (i + len + 4 <= n) {
            size_t len2 = mp3_frame_len(d + i + len, &rate2);
            if (len2 == 0 || rate2 != rate1) continue; /* random 0xFFEx bytes, keep looking */
            return i == id3 ? 95 : 70;                 /* two chained frames */
        }
        /* Only one frame fits in the window: weak evidence unless it starts the data. */
        return i == id3 ? 50 : 20;
    }
    return id3 > 0 ? 30 : 0;
}

int fa_probe_mp3_header(const uint8_t* d, size_t n, size_t start, uint32_t* out_kbps, uint32_t* out_rate)
{
    size_t i;
    for (i = start; i + 4 <= n; i++) {
        uint32_t rate = 0, version, br;
        size_t len = mp3_frame_len(d + i, &rate);
        if (len == 0) continue;
        if (i + len + 4 <= n && mp3_frame_len(d + i + len, NULL) == 0) continue;  /* not a chain of frames */
        version = (d[i + 1] >> 3) & 3;
        br = (d[i + 2] >> 4) & 15;
        *out_kbps = fa_mpa_kbps(version == 3, 3, (int)br);
        *out_rate = rate;
        return 1;
    }
    return 0;
}

/* Standard MIDI File: an "MThd" chunk of at least 6 bytes. RIFF MIDI (.rmi): a
 * RIFF form of type "RMID" around one. */
int fa_probe_midi(const uint8_t* d, size_t n, size_t id3)
{
    (void)id3;
    if (n >= 8 && memcmp(d, "MThd", 4) == 0 && d[4] == 0 && d[5] == 0 && d[6] == 0 && d[7] >= 6) return 100;
    if (n >= 12 && memcmp(d, "RIFF", 4) == 0 && memcmp(d + 8, "RMID", 4) == 0) return 100;
    return 0;
}
