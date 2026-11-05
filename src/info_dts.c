/* Raw DTS core streams (.dts, .cpt): the details a tags reader can give without a
 * decoder. Written from ETSI TS 102 114 (core substream frame header: SYNC, FTYPE,
 * SHORT, CPF, NBLKS, FSIZE, AMODE, SFREQ, RATE, ... LFF). Only the 16-bit big- and
 * little-endian sync words are read; 14-bit streams are not recognised. */
#include "fa_internal.h"
#include <stdio.h>
#include <string.h>

static const uint32_t dts_rates[16] = { 0, 8000, 16000, 32000, 0, 0, 11025, 22050, 44100, 0, 0, 12000, 24000, 48000, 0, 0 };
static const uint8_t dts_amode_channels[16] = { 1, 2, 2, 2, 2, 3, 3, 4, 4, 5, 6, 6, 6, 7, 8, 8 };

/* A frame header at d[0..n): fills the rate, channels (LFE included), samples per
 * frame and bytes per frame. */
static int dts_header(const uint8_t* d, size_t n, uint32_t* rate, uint32_t* ch, uint32_t* samples, uint32_t* bytes)
{
    uint8_t h[16];
    uint32_t nblks, fsize, amode, sfreq, lff;
    size_t i;
    if (n < 16) return 0;
    if (d[0] == 0x7F && d[1] == 0xFE && d[2] == 0x80 && d[3] == 0x01) memcpy(h, d, 16);
    else if (d[0] == 0xFE && d[1] == 0x7F && d[2] == 0x01 && d[3] == 0x80)
        for (i = 0; i < 16; i += 2) { h[i] = d[i + 1]; h[i + 1] = d[i]; }   /* byte-swapped words */
    else return 0;
    nblks = ((uint32_t)(h[4] & 0x01) << 6) | (h[5] >> 2);
    fsize = ((uint32_t)(h[5] & 0x03) << 12) | ((uint32_t)h[6] << 4) | (h[7] >> 4);
    amode = ((uint32_t)(h[7] & 0x0F) << 2) | (h[8] >> 6);
    sfreq = (h[8] >> 2) & 0x0F;
    lff = (h[10] >> 1) & 0x03;                     /* bits 85..86 */
    if (nblks < 5 || fsize < 95 || amode > 15 || dts_rates[sfreq] == 0) return 0;
    *rate = dts_rates[sfreq];
    *ch = (uint32_t)dts_amode_channels[amode] + (lff == 1 || lff == 2 ? 1u : 0u);
    *samples = (nblks + 1) * 32;
    *bytes = fsize + 1;
    return 1;
}

int fa_dts_core_info(fa_source* src, fa_stream_info* out)
{
    uint8_t h[16], h2[16];
    uint32_t rate, ch, samples, bytes, r2, c2, s2, b2;
    int64_t start, end, frames, out_audio;
    uint32_t bits;
    if (!src->seekable || src->length < 32) return 0;
    if (fa_source_seek(src, 0) != FA_OK || fa_source_read(src, h, 16) != 16) return 0;
    if (!dts_header(h, 16, &rate, &ch, &samples, &bytes)) return 0;
    /* A second frame right after the first, so four lucky bytes are not taken for DTS. */
    if (src->length > (int64_t)bytes + 16) {
        if (fa_source_seek(src, bytes) != FA_OK || fa_source_read(src, h2, 16) != 16) return 0;
        if (!dts_header(h2, 16, &r2, &c2, &s2, &b2) || r2 != rate) return 0;
    }
    fa_tags_audio_span(src, &start, &end, &bits);
    out_audio = end > start ? end - start : src->length;
    frames = out_audio / bytes;
    memset(out, 0, sizeof(*out));
    out->struct_size = sizeof(*out);
    out->format.sample_format = FA_FORMAT_F32;
    out->format.sample_rate = rate;
    out->format.channels = ch;
    snprintf(out->codec, sizeof(out->codec), "dts");
    snprintf(out->container, sizeof(out->container), "dts");
    out->length_frames = frames * samples;
    out->duration = (fa_time)((double)out->length_frames * 1e9 / rate + 0.5);
    out->seekable = 1;
    out->audio_track_count = 1;
    out->file_size = src->length;
    out->audio_bytes = out_audio;
    out->bitrate = (uint32_t)((double)bytes * 8.0 * rate / samples + 0.5);
    out->can_decode = 0;
    return 1;
}
