/* MPEG program streams (.mpg .mpeg .vob .dat) and transport streams (.ts .m2ts .mts), the
 * audio streams only. Written from ISO/IEC 13818-1 (ITU-T H.222.0): the pack header, the PES
 * packet and its header, the transport packet, the PAT and PMT sections and the stream type
 * table; the MPEG-1 system layer of ISO/IEC 11172-1 (Video CD `.dat`); and, for the private
 * stream 1 header of a DVD, the plain layout every DVD player reads (substream id, frame
 * count, first access unit pointer).
 *
 * Both are read the same way. The audio of a stream is carried in PES packets, each of
 * which may begin with a PTS (90 kHz, 33 bits). The payloads of the chosen stream are joined
 * into one elementary stream and cut into frames (MPEG audio, AC-3 / E-AC-3, ADTS AAC) that
 * go to the decoder one at a time. A frame has the PTS of the PES packet in which it starts;
 * the frames after it follow on by their own length, so audio from a muxer whose PTS jitter by
 * a tick still plays without a click.
 *
 * Neither format has an index. The length comes from the first PTS and the last one, found
 * in the last few megabytes (the frames from there to the end are counted for the exact
 * end). A seek searches the file for the PES packet at or before the target: it guesses
 * from the data rate so far, reads the next PES packet with a PTS at the guess, and narrows
 * down (halving when the guess does not help) until a few dozen kilobytes are left; the
 * decoder then reads on to the target. The packet it starts from is early by the decoder's
 * run-in. A stream with no PTS at all cannot be placed and is refused. PTS count in 33 bits
 * and wrap after 26.5 hours; positions are taken relative to the first one, which is right
 * as long as the file is shorter than 13 hours.
 *
 * Streams. Program stream: MPEG audio (stream ids 0xC0 to 0xDF), AC-3 and DTS in private
 * stream 1 (substreams 0x80 to 0x8F), DVD LPCM (0xA0 to 0xAF). Transport stream (188 bytes a
 * packet, 192 with the 4-byte time stamp of an M2TS file, or 204 with error correction
 * bytes): the first program's streams of MPEG audio (types 0x03 and 0x04), ADTS AAC (0x0F),
 * AC-3 (0x81, or a DVB private stream with an AC-3 descriptor), E-AC-3 (0x84 and 0x87, or
 * descriptor 0x7A). DTS, TrueHD, LPCM and AAC in LATM are listed as tracks but reported
 * unsupported when asked for. Scrambled transport streams are reported as protected.
 *
 * A source that cannot seek is read as it comes: the first audio stream of a program stream,
 * any stream of a transport stream; no length, no seeking. */
#include "demux.h"
#include "ac3.h"
#include <stdlib.h>
#include <string.h>

#define MPG_TICKS 90000
#define PTS_MASK ((int64_t)0x1FFFFFFFF)
#define MPG_MAX_STREAMS 32
#define MPG_ACC_MAX (4u << 20)       /* joined bytes that make no frame: dropped */
#define MPG_SCAN_PS (2u << 20)       /* how far the streams of a program stream are looked for */
#define MPG_SCAN_TS (4u << 20)       /* how far the PMT is looked for */
#define MPG_FIRST_MAX (32u << 20)    /* how far the first frame is looked for */
#define PM_MAX 4                     /* PTS waiting at once */
#define MPG_GRAN (32 << 10)          /* a seek stops searching this close */

enum { K_MPA = 1, K_DOLBY, K_ADTS, K_UNSUPPORTED };

typedef struct mpg_stream {
    int id;                  /* program stream: stream id; transport stream: PID */
    int sub;                 /* program stream private stream 1: substream id, else -1 */
    int kind;
    int skip;                /* private header bytes in front of the payload (substream header) */
} mpg_stream;

typedef struct mpg_psi {
    uint8_t buf[1100];
    int len, need, active;
} mpg_psi;

typedef struct mpg {
    fa_br br;
    fa_track track;
    int ts;                  /* transport stream, else program stream */
    int ts_size;             /* 188, 192 or 204 */
    int64_t ts_base;         /* where the first sync byte is */
    int has_packs;           /* program stream starts with a pack header: a landing in the middle looks for one */
    int seekable;
    int want_track;

    mpg_stream st[MPG_MAX_STREAMS];
    int nst;
    int sel;                 /* index of the chosen stream, -1 = none yet */
    int scan;                /* looking for streams (open) */
    int need_pack;           /* program stream: ignore everything up to the next pack header */
    mpg_psi pat, pmt;
    int pmt_pid, pmt_done;
    int in_pes;              /* transport stream: inside a PES packet of the chosen stream */
    int scrambled;

    uint8_t* acc;            /* the joined payloads */
    size_t acc_len, acc_off, acc_cap;
    int time_ok;             /* a PTS has been seen since the start or the last jump */
    int pm_n;                /* PTS that wait for the frame that starts at or after acc[pm_idx[k]] (oldest first): */
    size_t pm_idx[PM_MAX];   /* a frame can straddle two PES packets that both carry a PTS */
    int64_t pm_raw[PM_MAX], pm_unit[PM_MAX];
    int new_mark;            /* a PES packet with a PTS came in (the searches look at this) */
    int64_t new_raw, new_unit;

    int start_set;
    int64_t start_raw;       /* the first PTS: time 0 */
    int64_t first_off;       /* where the PES packet with it starts */
    double cur;              /* ticks after the start of the next frame without its own PTS */
    int cur_valid;
    int64_t end_rel;         /* ticks from start to the end of the audio, -1 = unknown */
    int64_t frame_ticks;     /* one frame, for the run-in of a seek */
    int at_eof;

    uint8_t* first;          /* the frame the open read, handed out first */
    size_t first_len;
    int first_valid;
    int64_t first_pts, first_dur;
    uint8_t* out;            /* the frame handed out (copied: the joined bytes move) */
    size_t out_cap;
} mpg;

typedef struct mpg_frame {
    const uint8_t* data;
    size_t len;
    int64_t pts;             /* ticks after the start */
    int64_t dur;             /* ticks */
    int64_t unit;            /* where the PES packet whose PTS this is starts, -1 = a frame without one */
    uint32_t rate, channels;
    const char* codec;
    int eac3;
    int64_t samples;
} mpg_frame;

static void mpg_free(mpg* m)
{
    fa_br_free(&m->br);
    free(m->acc);
    free(m->first);
    free(m->out);
    free(m);
}

/* ---- audio frames ----------------------------------------------------------------------- */

/* The frame at p: 1 and its fields, 0 if it is none, -1 if p holds too little to tell. */
static int mpa_frame(const uint8_t* p, size_t n, mpg_frame* f)
{
    static const uint32_t rates[3] = { 44100, 48000, 32000 };
    unsigned ver, layer, bi, sri, pad, kbps;
    uint32_t rate;
    if (n < 4) return -1;
    if (p[0] != 0xFF || (p[1] & 0xE0) != 0xE0) return 0;
    ver = (p[1] >> 3) & 3;
    layer = 4 - ((p[1] >> 1) & 3);
    bi = p[2] >> 4;
    sri = (p[2] >> 2) & 3;
    pad = (p[2] >> 1) & 1;
    if (ver == 1 || layer == 4 || sri == 3 || bi == 15) return 0;
    kbps = fa_mpa_kbps(ver == 3, (int)layer, bi);
    if (kbps == 0) return 0;
    rate = rates[sri] >> (ver == 3 ? 0 : ver == 2 ? 1 : 2);
    if (layer == 1) f->len = (size_t)(12u * kbps * 1000u / rate + pad) * 4;
    else if (layer == 2) f->len = (size_t)(144u * kbps * 1000u / rate + pad);
    else f->len = (size_t)((ver == 3 ? 144u : 72u) * kbps * 1000u / rate + pad);
    f->samples = layer == 1 ? 384 : layer == 2 ? 1152 : ver == 3 ? 1152 : 576;
    f->rate = rate;
    f->channels = (p[3] >> 6) == 3 ? 1 : 2;
    f->codec = layer == 1 ? "mp1" : layer == 2 ? "mp2" : "mp3";
    return 1;
}

static int adts_frame(const uint8_t* p, size_t n, mpg_frame* f)
{
    static const uint32_t layout[8] = { 2, 1, 2, 3, 4, 5, 6, 8 };
    fa_adts_header h;
    if (n < 7) return -1;
    if (p[0] != 0xFF || (p[1] & 0xF0) != 0xF0) return 0;
    if (!fa_adts_parse(p, n, &h) || h.len <= h.header) return 0;
    f->len = h.len;
    f->samples = 1024 * h.blocks;
    f->rate = fa_aac_rates[h.rate_index];
    f->channels = layout[h.config];
    f->codec = "aac";
    return 1;
}

static int dolby_frame(const uint8_t* p, size_t n, mpg_frame* f)
{
    fa_ac3_header h;
    if (n < 8) return -1;
    if (p[0] != 0x0B || p[1] != 0x77) return 0;
    if (!fa_ac3_parse_header(p, n, &h) || h.frame_size == 0) return 0;
    f->len = h.frame_size;
    f->rate = h.rate;
    f->channels = (uint32_t)h.channels;
    f->eac3 = h.eac3;
    f->codec = h.eac3 ? "eac3" : "ac3";
    f->samples = (int64_t)h.blocks * 256;
    return 1;
}

static int frame_at(int kind, const uint8_t* p, size_t n, mpg_frame* f)
{
    memset(f, 0, sizeof(*f));
    return kind == K_MPA ? mpa_frame(p, n, f) : kind == K_ADTS ? adts_frame(p, n, f) : dolby_frame(p, n, f);
}

/* What the first bytes of an MPEG audio stream's payload are. */
static int sniff_kind(const uint8_t* p, size_t n)
{
    size_t i;
    mpg_frame f;
    if (n > 2048) n = 2048;
    for (i = 0; i + 8 <= n; i++) {
        if (p[i] == 0x0B && dolby_frame(p + i, n - i, &f) == 1) return K_DOLBY;
        if (p[i] == 0xFF) {
            if (adts_frame(p + i, n - i, &f) == 1) return K_ADTS;
            if (mpa_frame(p + i, n - i, &f) == 1) return K_MPA;
        }
    }
    return K_MPA;
}

static void pm_pop(mpg* m)
{
    int k;
    m->pm_n--;
    for (k = 0; k < m->pm_n; k++) {
        m->pm_idx[k] = m->pm_idx[k + 1];
        m->pm_raw[k] = m->pm_raw[k + 1];
        m->pm_unit[k] = m->pm_unit[k + 1];
    }
}

/* The next frame in the joined bytes, if they hold one that is complete and followed by
 * another (or the end of the stream): the bytes before it are dropped. */
static int extract(mpg* m, int eof, mpg_frame* f)
{
    int kind = m->st[m->sel].kind;
    const uint8_t* a = m->acc + m->acc_off;
    size_t n = m->acc_len - m->acc_off, i = 0;
    int found = 0;
    while (i < n) {
        mpg_frame nx;
        int r = frame_at(kind, a + i, n - i, f);
        if (r < 0) { if (eof) i = n; break; }
        if (r == 0) { i++; continue; }
        if (kind == K_DOLBY && f->eac3) {
            /* An access unit: the independent frame and the dependent ones after it. */
            int more = 1, wait = 0;
            while (more) {
                fa_ac3_header h;
                size_t at = i + f->len;
                more = 0;
                if (at + 8 > n) { wait = !eof; break; }
                if (fa_ac3_parse_header(a + at, n - at, &h) && h.eac3 && !fa_ac3_starts_unit(&h)) {
                    f->len += h.frame_size;
                    more = 1;
                }
            }
            if (wait) break;
        }
        if (i + f->len > n) { if (eof) i = n; break; }
        if (i + f->len < n) {
            r = frame_at(kind, a + i + f->len, n - i - f->len, &nx);
            if (r == 0) { i++; continue; }
            if (r < 0 && !eof) break;
        } else if (!eof) {
            break;
        }
        found = 1;
        break;
    }
    if (found) {
        uint32_t rate = 0;
        int64_t idx = (int64_t)(m->acc_off + i);
        if (kind == K_DOLBY) {
            int64_t s = fa_ac3_packet_samples(a + i, f->len, &rate);
            if (s > 0 && rate > 0) { f->samples = s; f->rate = rate; }
        }
        f->data = a + i;
        f->dur = (int64_t)((double)f->samples * MPG_TICKS / f->rate + 0.5);
        f->unit = -1;
        /* The newest PTS at or before the frame is its own; older ones had no frame start. */
        while (m->pm_n > 1 && (int64_t)m->pm_idx[1] <= idx) pm_pop(m);
        if (m->pm_n > 0 && idx >= (int64_t)m->pm_idx[0]) {
            int64_t d = (m->pm_raw[0] - m->start_raw) & PTS_MASK;
            if (d >= ((int64_t)1 << 32)) d -= (int64_t)1 << 33;
            m->cur = (double)d;
            m->cur_valid = 1;
            f->unit = m->pm_unit[0];
            pm_pop(m);
        }
        f->pts = m->cur_valid ? (int64_t)(m->cur + 0.5) : 0;
        m->cur += (double)f->samples * MPG_TICKS / f->rate;
        m->acc_off += i + f->len;
        return 1;
    }
    m->acc_off += i;
    return 0;
}

/* ---- joining the payloads ------------------------------------------------------------------ */

static fa_result deliver(mpg* m, const uint8_t* p, size_t n, int has_pts, int64_t pts, int64_t unit)
{
    int k;
    if (has_pts) {
        m->new_mark = 1;
        m->new_raw = pts;
        m->new_unit = unit;
    }
    if (m->sel < 0) return FA_OK;
    if (has_pts && !m->start_set) { m->start_raw = pts; m->start_set = 1; }
    if (!m->time_ok && !has_pts) return FA_OK;
    if (m->acc_off > 0 && (m->acc_off >= m->acc_len || m->acc_off > (64u << 10))) {
        memmove(m->acc, m->acc + m->acc_off, m->acc_len - m->acc_off);
        m->acc_len -= m->acc_off;
        for (k = 0; k < m->pm_n; k++) m->pm_idx[k] = m->pm_idx[k] > m->acc_off ? m->pm_idx[k] - m->acc_off : 0;
        m->acc_off = 0;
    }
    if (m->acc_len - m->acc_off > MPG_ACC_MAX) {
        /* Never a frame in that much: keep the last bytes only. */
        m->acc_off = m->acc_len - 3;
        m->pm_n = 0;
    }
    if (m->acc_len + n > m->acc_cap) {
        size_t cap = m->acc_cap ? m->acc_cap : 16384;
        uint8_t* b;
        while (cap < m->acc_len + n) cap *= 2;
        b = (uint8_t*)realloc(m->acc, cap);
        if (!b) return FA_ERROR_OUT_OF_MEMORY;
        m->acc = b;
        m->acc_cap = cap;
    }
    if (has_pts) {
        if (m->pm_n == PM_MAX) pm_pop(m);
        m->pm_idx[m->pm_n] = m->acc_len;
        m->pm_raw[m->pm_n] = pts;
        m->pm_unit[m->pm_n] = unit;
        m->pm_n++;
        m->time_ok = 1;
    }
    memcpy(m->acc + m->acc_len, p, n);
    m->acc_len += n;
    return FA_OK;
}

static int64_t pts_read(const uint8_t* p)
{
    return ((int64_t)((p[0] >> 1) & 7) << 30) | ((int64_t)p[1] << 22) | ((int64_t)(p[2] >> 1) << 15) |
           ((int64_t)p[3] << 7) | (p[4] >> 1);
}

/* The PES header after the packet length (n bytes there): 1 with its length and the PTS it
 * carries. MPEG-2 and the MPEG-1 form of Video CD both. */
static int pes_header(const uint8_t* p, size_t n, int* has, int64_t* pts, size_t* len)
{
    size_t i = 0;
    *has = 0;
    if (n < 1) return 0;
    if ((p[0] & 0xC0) == 0x80) {
        size_t hl;
        if (n < 3) return 0;
        hl = p[2];
        if (3 + hl > n) return 0;
        if ((p[1] & 0x80) && hl >= 5 && (p[3] & 0xE1) == 0x21 && (p[5] & 1) && (p[7] & 1)) {
            *has = 1;
            *pts = pts_read(p + 3);
        }
        *len = 3 + hl;
        return 1;
    }
    while (i < n && i < 16 && p[i] == 0xFF) i++;
    if (i < n && (p[i] & 0xC0) == 0x40) i += 2;
    if (i >= n) return 0;
    if ((p[i] & 0xF0) == 0x20 || (p[i] & 0xF0) == 0x30) {
        size_t need = (p[i] & 0xF0) == 0x30 ? 10 : 5;
        if (i + need > n || !(p[i] & 1) || !(p[i + 2] & 1) || !(p[i + 4] & 1)) return 0;
        *has = 1;
        *pts = pts_read(p + i);
        i += need;
    } else if (p[i] == 0x0F) {
        i++;
    } else {
        return 0;
    }
    *len = i;
    return 1;
}

/* ---- program streams ------------------------------------------------------------------------ */

static int ps_audio_id(int id) { return (id >= 0xC0 && id <= 0xDF) || id == 0xBD; }

/* What a private stream 1 substream is: kind and header bytes to skip. */
static int ps_sub_kind(int sub, int* skip)
{
    *skip = 4;
    if (sub >= 0x80 && sub <= 0x87) return K_DOLBY;
    if (sub >= 0x88 && sub <= 0x8F) return K_UNSUPPORTED;     /* DTS */
    if (sub >= 0x98 && sub <= 0x9F) return K_UNSUPPORTED;     /* DTS-HD */
    if (sub >= 0xA0 && sub <= 0xAF) { *skip = 7; return K_UNSUPPORTED; }   /* LPCM */
    return 0;
}

static int add_stream(mpg* m, int id, int sub, int kind, int skip)
{
    int i;
    for (i = 0; i < m->nst; i++)
        if (m->st[i].id == id && m->st[i].sub == sub) return i;
    if (m->nst >= MPG_MAX_STREAMS) return -1;
    m->st[m->nst].id = id;
    m->st[m->nst].sub = sub;
    m->st[m->nst].kind = kind;
    m->st[m->nst].skip = skip;
    return m->nst++;
}

static fa_result ps_pull(mpg* m)
{
    uint8_t b[16];
    uint32_t w;
    int id;
    int64_t unit;
    if (fa_br_read(&m->br, b, 4) != 4) return FA_ERROR_END_OF_STREAM;
    w = ((uint32_t)b[0] << 16) | ((uint32_t)b[1] << 8) | b[2];
    id = b[3];
    unit = fa_br_tell(&m->br) - 4;
    while (!(w == 1 && (!m->need_pack || id == 0xBA))) {
        uint8_t c;
        if (fa_br_read(&m->br, &c, 1) != 1) return FA_ERROR_END_OF_STREAM;
        w = ((w << 8) | (uint32_t)id) & 0xFFFFFF;
        id = c;
        unit++;
    }
    if (id == 0xBA) {
        /* MPEG-2: 14 bytes and stuffing; MPEG-1: 12. */
        m->need_pack = 0;
        if (fa_br_read(&m->br, b, 1) != 1) return FA_ERROR_END_OF_STREAM;
        if ((b[0] & 0xC0) == 0x40) {
            if (fa_br_read(&m->br, b, 9) != 9) return FA_ERROR_END_OF_STREAM;
            return fa_br_seek(&m->br, fa_br_tell(&m->br) + (b[8] & 7)) == FA_OK ? FA_OK : FA_ERROR_END_OF_STREAM;
        }
        return fa_br_read(&m->br, b, 7) == 7 ? FA_OK : FA_ERROR_END_OF_STREAM;
    }
    if (id == 0xB9 || id < 0xBB) return FA_OK;
    {
        uint8_t lenb[2];
        size_t len, got;
        int sub = -1, kind = K_MPA, skip = 0, idx = -1, want;
        if (fa_br_read(&m->br, lenb, 2) != 2) return FA_ERROR_END_OF_STREAM;
        len = ((size_t)lenb[0] << 8) | lenb[1];
        want = ps_audio_id(id);
        if (want && !m->scan) {
            idx = m->sel;
            want = idx >= 0 && m->st[idx].id == id;
        }
        if (!want || len == 0) {
            if (len == 0) return FA_OK;
            return fa_br_seek(&m->br, fa_br_tell(&m->br) + (int64_t)len) == FA_OK ? FA_OK : FA_ERROR_END_OF_STREAM;
        }
        {
            uint8_t buf[65536];
            const uint8_t* p = buf;
            size_t hl;
            int has, cut;
            int64_t pts = 0;
            got = fa_br_read(&m->br, buf, len);
            cut = got < len;
            if (cut) len = got;
            if (!pes_header(buf, len, &has, &pts, &hl)) return cut ? FA_ERROR_END_OF_STREAM : FA_OK;
            p += hl;
            len -= hl;
            if (id == 0xBD) {
                if (len < 1) return FA_OK;
                sub = p[0];
                kind = ps_sub_kind(sub, &skip);
                if (!kind) return FA_OK;
            } else if (m->scan) {
                kind = sniff_kind(p, len);
            }
            if (m->scan) {
                idx = add_stream(m, id, sub, kind, skip);
                if (idx < 0) return FA_OK;
                if (!m->seekable && m->sel < 0 && m->want_track == 0) m->sel = idx;
            } else if (id == 0xBD && m->st[idx].sub != sub) {
                return FA_OK;
            }
            if (m->sel < 0 || idx != m->sel) return FA_OK;
            if (len < (size_t)m->st[idx].skip) return FA_OK;
            return deliver(m, p + m->st[idx].skip, len - (size_t)m->st[idx].skip, has, pts, unit);
        }
    }
}

/* ---- transport streams ---------------------------------------------------------------------- */

/* The layout of a transport stream in a window: sync bytes `size` apart. */
static int ts_layout(const uint8_t* d, size_t n, int64_t* base, int* size)
{
    static const int sizes[3] = { 188, 192, 204 };
    int s;
    for (s = 0; s < 3; s++) {
        size_t i, step = (size_t)sizes[s];
        for (i = 0; i < step && i < n; i++) {
            size_t k, hits = 0;
            if (d[i] != 0x47) continue;
            for (k = 0; i + k * step < n && k < 5; k++) {
                if (d[i + k * step] != 0x47) { hits = 0; break; }
                hits++;
            }
            if (hits >= 3) { *base = (int64_t)i; *size = sizes[s]; return 1; }
        }
    }
    return 0;
}

static int ts_stream_kind(int type, const uint8_t* desc, int dlen, int* kind)
{
    int i;
    switch (type) {
    case 0x03: case 0x04: *kind = K_MPA; return 1;
    case 0x0F: *kind = K_ADTS; return 1;
    case 0x81: case 0x84: case 0x87: case 0xA1: *kind = K_DOLBY; return 1;    /* AC-3, E-AC-3, Blu-ray secondary E-AC-3 */
    case 0x11: case 0x80: case 0x82: case 0x83: case 0x85: case 0x86: case 0xA2: *kind = K_UNSUPPORTED; return 1;
    case 0x06:
    case 0x05:
        for (i = 0; i + 2 <= dlen; i += 2 + desc[i + 1]) {
            if (i + 2 + desc[i + 1] > dlen) break;
            if (desc[i] == 0x6A || desc[i] == 0x7A) { *kind = K_DOLBY; return 1; }
            if (desc[i] == 0x7B || desc[i] == 0x7C) { *kind = K_UNSUPPORTED; return 1; }
            if (desc[i] == 0x05 && desc[i + 1] >= 4) {   /* registration descriptor */
                const uint8_t* f = desc + i + 2;
                if (memcmp(f, "AC-3", 4) == 0 || memcmp(f, "EAC3", 4) == 0) { *kind = K_DOLBY; return 1; }
                if (memcmp(f, "DTS", 3) == 0) { *kind = K_UNSUPPORTED; return 1; }
            }
        }
        return 0;
    default: return 0;
    }
}

/* Gathers a PSI section across packets. 1 when one is complete (in s->buf). */
static int psi_feed(mpg_psi* s, int pusi, const uint8_t* p, int n)
{
    if (pusi) {
        int ptr;
        if (n < 1) return 0;
        ptr = p[0];
        if (1 + ptr >= n) { s->active = 0; return 0; }
        p += 1 + ptr;
        n -= 1 + ptr;
        s->len = 0;
        s->need = -1;
        s->active = 1;
    } else if (!s->active) {
        return 0;
    }
    if (s->len + n > (int)sizeof(s->buf)) n = (int)sizeof(s->buf) - s->len;
    memcpy(s->buf + s->len, p, (size_t)n);
    s->len += n;
    if (s->need < 0 && s->len >= 3) s->need = 3 + (((s->buf[1] & 0x0F) << 8) | s->buf[2]);
    if (s->need > 0 && s->need <= (int)sizeof(s->buf) && s->len >= s->need) { s->active = 0; return 1; }
    if (s->need > (int)sizeof(s->buf)) s->active = 0;
    return 0;
}

static void ts_parse_pat(mpg* m)
{
    const uint8_t* p = m->pat.buf;
    int i, n = m->pat.need;
    if (p[0] != 0x00 || n < 12) return;
    for (i = 8; i + 4 <= n - 4; i += 4) {
        int program = (p[i] << 8) | p[i + 1];
        if (program != 0) { m->pmt_pid = ((p[i + 2] & 0x1F) << 8) | p[i + 3]; return; }
    }
}

static void ts_parse_pmt(mpg* m)
{
    const uint8_t* p = m->pmt.buf;
    int n = m->pmt.need, i, info;
    if (p[0] != 0x02 || n < 16) return;
    info = ((p[10] & 0x0F) << 8) | p[11];
    for (i = 12 + info; i + 5 <= n - 4;) {
        int type = p[i], pid = ((p[i + 1] & 0x1F) << 8) | p[i + 2];
        int es = ((p[i + 3] & 0x0F) << 8) | p[i + 4], kind;
        int dl = i + 5 + es <= n - 4 ? es : 0;
        if (ts_stream_kind(type, p + i + 5, dl, &kind)) add_stream(m, pid, type, kind, 0);
        i += 5 + es;
    }
    m->pmt_done = 1;
}

/* A bad byte where a sync byte belongs: on to the next place with two in a row. */
static fa_result ts_resync(mpg* m, int64_t at)
{
    int64_t q;
    for (q = at + 1; q < at + (1 << 20); q++) {
        uint8_t c;
        if (fa_br_seek(&m->br, q) != FA_OK || fa_br_read(&m->br, &c, 1) != 1) return FA_ERROR_END_OF_STREAM;
        if (c != 0x47) continue;
        if (fa_br_seek(&m->br, q + m->ts_size) != FA_OK || fa_br_read(&m->br, &c, 1) != 1 || c == 0x47) {
            /* Back to the packet; a forward-only source cannot go that far back and loses this packet. */
            if (fa_br_seek(&m->br, q) == FA_OK) return FA_OK;
            return fa_br_seek(&m->br, q + m->ts_size) == FA_OK ? FA_OK : FA_ERROR_END_OF_STREAM;
        }
    }
    return FA_ERROR_END_OF_STREAM;
}

static fa_result ts_pull(mpg* m)
{
    uint8_t pk[188];
    int64_t at = fa_br_tell(&m->br);
    int pid, pusi, afc, off = 4, n;
    const uint8_t* p;
    if (fa_br_read(&m->br, pk, 188) != 188) return FA_ERROR_END_OF_STREAM;
    if (pk[0] != 0x47) return ts_resync(m, at);
    if (m->ts_size > 188 && fa_br_seek(&m->br, at + m->ts_size) != FA_OK) return FA_ERROR_END_OF_STREAM;
    if (pk[1] & 0x80) return FA_OK;                               /* transport error */
    pid = ((pk[1] & 0x1F) << 8) | pk[2];
    pusi = (pk[1] & 0x40) != 0;
    afc = (pk[3] >> 4) & 3;
    if (afc == 0 || afc == 2) return FA_OK;
    if (afc == 3) off += 1 + pk[4];
    if (off >= 188) return FA_OK;
    p = pk + off;
    n = 188 - off;
    if (!m->pmt_done) {
        if (pid == 0) {
            if (psi_feed(&m->pat, pusi, p, n)) ts_parse_pat(m);
            return FA_OK;
        }
        if (m->pmt_pid >= 0 && pid == m->pmt_pid) {
            if (psi_feed(&m->pmt, pusi, p, n)) {
                ts_parse_pmt(m);
                if (!m->seekable && m->want_track < m->nst) m->sel = m->want_track;
            }
            return FA_OK;
        }
    }
    if (m->sel < 0 || pid != m->st[m->sel].id) return FA_OK;
    if (pk[3] & 0xC0) { m->scrambled++; return FA_OK; }
    if (pusi) {
        int has = 0;
        int64_t pts = 0;
        size_t hl = 0;
        m->in_pes = 0;
        if (n < 9 || p[0] != 0 || p[1] != 0 || p[2] != 1) return FA_OK;
        if (!pes_header(p + 6, (size_t)n - 6, &has, &pts, &hl)) return FA_OK;
        m->in_pes = 1;
        p += 6 + hl;
        n -= 6 + (int)hl;
        if (n <= 0) return FA_OK;
        return deliver(m, p, (size_t)n, has, pts, at);
    }
    if (!m->in_pes) return FA_OK;
    return deliver(m, p, (size_t)n, 0, 0, at);
}

/* ---- positioning ---------------------------------------------------------------------------- */

static fa_result pull(mpg* m) { return m->ts ? ts_pull(m) : ps_pull(m); }

static void reset_join(mpg* m)
{
    m->acc_len = m->acc_off = 0;
    m->pm_n = 0;
    m->new_mark = 0;
    m->time_ok = 0;
    m->in_pes = 0;
    m->at_eof = 0;
    m->cur_valid = 0;
    m->need_pack = 0;
}

/* Reads from `off`. exact: `off` is where a PES packet (a transport packet) starts; else
 * it is anywhere, and the reading looks for a pack header (a transport packet boundary). */
static fa_result reposition(mpg* m, int64_t off, int exact)
{
    reset_join(m);
    if (m->ts) {
        if (off < m->ts_base) off = m->ts_base;
        if (!exact) off = m->ts_base + (off - m->ts_base + m->ts_size - 1) / m->ts_size * m->ts_size;
    } else if (!exact && m->has_packs) {
        m->need_pack = 1;
    }
    return fa_br_seek(&m->br, off);
}

/* The first PES packet with a PTS in the chosen stream at or after `off`, reading no more
 * than `span` bytes. 1 with m->new_raw and m->new_unit. */
static int locate(mpg* m, int64_t off, int64_t span)
{
    if (reposition(m, off, 0) != FA_OK) return 0;
    for (;;) {
        fa_result r;
        if (fa_br_tell(&m->br) - off > span) return 0;
        r = pull(m);
        if (r != FA_OK) return 0;
        if (m->new_mark) { m->new_mark = 0; return 1; }
    }
}

static int64_t rel_of(const mpg* m, int64_t raw)
{
    int64_t d = (raw - m->start_raw) & PTS_MASK;
    return d >= ((int64_t)1 << 32) ? d - ((int64_t)1 << 33) : d;
}

/* The PES packet to start from so that the frames from there reach `target` (ticks after
 * the start) with the decoder's run-in before it. */
static int64_t search(mpg* m, int64_t target)
{
    int64_t lo_off = m->first_off, lo_rel = 0, hi_off = m->br.src->length;
    int64_t pair_off = hi_off, pair_rel = m->end_rel;
    int it;
    if (target <= 0 || hi_off <= lo_off) return lo_off;
    for (it = 0; hi_off - lo_off > MPG_GRAN && it < 64; it++) {
        int64_t guess;
        if ((it & 1) && pair_rel > lo_rel && pair_off > lo_off) {
            double f = (double)(target - lo_rel) / (double)(pair_rel - lo_rel);
            guess = lo_off + (int64_t)(f * (double)(pair_off - lo_off));
        } else {
            guess = lo_off + (hi_off - lo_off) / 2;
        }
        if (guess <= lo_off) guess = lo_off + 1;
        if (guess >= hi_off) guess = hi_off - 1;
        if (!locate(m, guess, hi_off - guess) || m->new_unit >= hi_off) {
            hi_off = guess;
        } else {
            int64_t rel = rel_of(m, m->new_raw);
            if (rel <= target) { lo_off = m->new_unit; lo_rel = rel; }
            else { hi_off = guess; pair_off = m->new_unit; pair_rel = rel; }
        }
    }
    return lo_off;
}

static fa_result mpg_seek(void* ctx, int64_t t, int packets_back, size_t bytes_back)
{
    mpg* m = (mpg*)ctx;
    int64_t margin, off;
    if (!m->seekable) return FA_ERROR_NOT_SEEKABLE;
    m->first_valid = 0;
    /* bytes_back (a bit reservoir) as time: 12 ticks a byte is the time of a byte at 60 kbit/s, longer than any MPEG audio bit rate that needs it. */
    margin = (int64_t)(packets_back + 2) * m->frame_ticks + (int64_t)bytes_back * 12;
    off = search(m, t - m->start_raw - margin);
    return reposition(m, off, 1);
}

/* ---- packets ---------------------------------------------------------------------------------- */

static fa_result mpg_next(void* ctx, fa_demux_packet* out)
{
    mpg* m = (mpg*)ctx;
    if (m->first_valid) {
        m->first_valid = 0;
        out->data = m->first;
        out->size = m->first_len;
        out->pts = m->start_raw + m->first_pts;
        out->duration = m->first_dur;
        return FA_OK;
    }
    for (;;) {
        mpg_frame f;
        fa_result r;
        if (extract(m, m->at_eof, &f)) {
            if (f.len > m->out_cap) {
                uint8_t* b = (uint8_t*)realloc(m->out, f.len);
                if (!b) return FA_ERROR_OUT_OF_MEMORY;
                m->out = b;
                m->out_cap = f.len;
            }
            memcpy(m->out, f.data, f.len);
            out->data = m->out;
            out->size = f.len;
            out->pts = m->start_raw + f.pts;
            out->duration = f.dur;
            return FA_OK;
        }
        if (m->at_eof) return FA_ERROR_END_OF_STREAM;
        r = pull(m);
        if (r == FA_ERROR_OUT_OF_MEMORY) return r;
        if (r != FA_OK) m->at_eof = 1;
    }
}

static const fa_demux_ops mpg_ops = { mpg_next, mpg_seek };

/* ---- open ------------------------------------------------------------------------------------- */

/* The end of the audio, ticks after the start: the last PES packet with a PTS in the last
 * stretch of the file, then its frames counted to the end. -1 if there is none. */
static int64_t find_end(mpg* m)
{
    int64_t len = m->br.src->length, win, last_off = -1;
    for (win = 4 << 20; win <= (16 << 20) && last_off < 0; win *= 4) {
        int64_t from = len > win ? len - win : 0;
        if (from < m->first_off) from = m->first_off;
        if (reposition(m, from, 0) != FA_OK) return -1;
        while (pull(m) == FA_OK) {
            /* Only the PES positions matter here: the joined bytes are not kept. */
            m->acc_len = m->acc_off = 0;
            m->pm_n = 0;
            if (m->new_mark) { m->new_mark = 0; last_off = m->new_unit; }
        }
        if (from == m->first_off) break;
    }
    if (last_off < 0) return -1;
    if (reposition(m, last_off, 1) != FA_OK) return -1;
    {
        int64_t end = -1;
        for (;;) {
            mpg_frame f;
            fa_result r;
            if (extract(m, m->at_eof, &f)) {
                if (f.pts + f.dur > end) end = f.pts + f.dur;
                continue;
            }
            if (m->at_eof) break;
            r = pull(m);
            if (r != FA_OK) m->at_eof = 1;
        }
        return end;
    }
}

static fa_result mpg_open_common(fa_decoder* dec, int ts)
{
    fa_source* src = dec->src;
    mpg* m = (mpg*)calloc(1, sizeof(mpg));
    const uint8_t* head;
    size_t hn;
    int64_t scan_limit;
    mpg_frame first;
    fa_track_params tp;
    fa_codec_params* cp;
    fa_result r;
    int got = 0;

    if (!m) return FA_ERROR_OUT_OF_MEMORY;
    m->ts = ts;
    m->sel = -1;
    m->pmt_pid = -1;
    m->end_rel = -1;
    m->seekable = src->seekable && src->length > 0;
    m->want_track = dec->audio_track < 0 ? 0 : dec->audio_track;
    hn = fa_source_peek(src, &head);
    if (ts) {
        if (!ts_layout(head, hn, &m->ts_base, &m->ts_size)) { r = FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    } else {
        if (hn < 4 || head[0] != 0 || head[1] != 0 || head[2] != 1) { r = FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
        m->has_packs = head[3] == 0xBA;
        if (!m->seekable && m->want_track > 0) { r = FA_ERROR_NOT_SEEKABLE; goto fail; }
    }
    r = fa_br_init(&m->br, src);
    if (r != FA_OK) goto fail;
    if (fa_br_seek(&m->br, ts ? m->ts_base : 0) != FA_OK) { r = FA_ERROR_IO; goto fail; }

    /* The streams. */
    m->scan = 1;
    scan_limit = ts ? MPG_SCAN_TS : MPG_SCAN_PS;
    while (fa_br_tell(&m->br) < scan_limit) {
        r = pull(m);
        if (r == FA_ERROR_OUT_OF_MEMORY) goto fail;
        if (r != FA_OK) break;
        if (ts && m->pmt_done) break;
        if (!ts && !m->seekable && m->sel >= 0) break;
    }
    m->scan = 0;
    if (m->nst == 0) { r = FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    dec->track_count = m->seekable || ts ? m->nst : 1;
    if (m->want_track >= m->nst) { r = FA_ERROR_NOT_FOUND; goto fail; }
    m->sel = m->want_track;
    if (m->st[m->sel].kind == K_UNSUPPORTED) { r = FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    if (m->seekable) {
        r = fa_br_seek(&m->br, ts ? m->ts_base : 0) == FA_OK ? FA_OK : FA_ERROR_IO;
        if (r != FA_OK) goto fail;
        reset_join(m);
        m->start_set = 0;
    }

    /* The first frame. */
    for (;;) {
        int64_t at = fa_br_tell(&m->br);
        if (extract(m, m->at_eof, &first)) { got = 1; break; }
        if (m->at_eof || at > MPG_FIRST_MAX) break;
        r = pull(m);
        if (r == FA_ERROR_OUT_OF_MEMORY) goto fail;
        if (r != FA_OK) m->at_eof = 1;
    }
    if (!got) { r = m->scrambled ? FA_ERROR_PROTECTED_CONTENT : FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    if (first.unit < 0 || first.rate == 0 || first.rate > FA_MAX_SAMPLE_RATE) { r = FA_ERROR_UNSUPPORTED_FORMAT; goto fail; }
    m->first_off = first.unit;
    m->frame_ticks = first.dur;
    if (!m->seekable) {
        /* Read as it comes: the frame is handed out from a copy. */
        m->first = (uint8_t*)malloc(first.len);
        if (!m->first) { r = FA_ERROR_OUT_OF_MEMORY; goto fail; }
        memcpy(m->first, first.data, first.len);
        m->first_len = first.len;
        m->first_pts = first.pts;
        m->first_dur = first.dur;
        m->first_valid = 1;
    }

    memset(&tp, 0, sizeof(tp));
    fa_codec_params_init(&tp.codec);
    cp = &tp.codec;
    cp->codec = first.codec;
    cp->sample_rate = first.rate;
    cp->channels = first.channels ? first.channels : 2;
    tp.time_base = 1.0 / MPG_TICKS;
    tp.start = m->start_raw;
    tp.end = FA_DEMUX_NO_PTS;
    tp.last_pts = FA_DEMUX_NO_PTS;
    tp.precision = 4;                     /* ticks: a muxer's PTS may be a few off the frame grid */

    if (m->seekable) {
        /* The end is found by reading the last stretch; then back to the first packet. */
        int64_t e = find_end(m);
        if (e >= 0) {
            m->end_rel = e;
            tp.end = m->start_raw + e;
        }
        if (reposition(m, m->first_off, 1) != FA_OK) { r = FA_ERROR_IO; goto fail; }
    }

    r = fa_track_open(&m->track, dec, &mpg_ops, m, &tp);
    if (r != FA_OK) goto fail;
    dec->impl = m;
    dec->seekable = m->seekable;
    snprintf(dec->container, sizeof(dec->container), "%s", ts ? (m->ts_size == 192 ? "m2ts" : "mpeg-ts") : "mpeg-ps");
    return FA_OK;
fail:
    mpg_free(m);
    return r;
}

static fa_result mpg_open_ps(fa_decoder* dec) { return mpg_open_common(dec, 0); }
static fa_result mpg_open_ts(fa_decoder* dec) { return mpg_open_common(dec, 1); }

static fa_result mpg_read(fa_decoder* dec, float* out, fa_frames frames, fa_frames* got)
{
    return fa_track_read(&((mpg*)dec->impl)->track, out, frames, got);
}

static fa_result mpg_seek_frames(fa_decoder* dec, fa_frames frame)
{
    if (!dec->seekable) return FA_ERROR_NOT_SEEKABLE;
    return fa_track_seek(&((mpg*)dec->impl)->track, frame);
}

static void mpg_close(fa_decoder* dec)
{
    mpg* m = (mpg*)dec->impl;
    if (!m) return;
    fa_track_close(&m->track);
    mpg_free(m);
    dec->impl = NULL;
}

/* A pack header; or sync bytes a packet apart. */
static int ps_probe(const uint8_t* d, size_t n, size_t id3)
{
    (void)id3;
    return n >= 4 && d[0] == 0 && d[1] == 0 && d[2] == 1 && d[3] == 0xBA ? 90 : 0;
}

static int ts_probe(const uint8_t* d, size_t n, size_t id3)
{
    int64_t base;
    int size;
    (void)id3;
    return ts_layout(d, n, &base, &size) ? 90 : 0;
}

const fa_decoder_vtable fa_decoder_mpg = {
    "mpeg-ps", "MPEG program streams (audio track)", "mpg;mpeg;vob;dat;m2p;mpe",
    "video/mpeg;video/mp2p;video/x-mpeg;video/x-ms-vob",
    ps_probe, mpg_open_ps, mpg_read, mpg_seek_frames, mpg_close, 1, NULL, NULL
};

const fa_decoder_vtable fa_decoder_ts = {
    "mpeg-ts", "MPEG transport streams (audio track)", "ts;m2ts;mts;m2t",
    "video/mp2t;video/vnd.dlna.mpeg-tts",
    ts_probe, mpg_open_ts, mpg_read, mpg_seek_frames, mpg_close, 1, NULL, NULL
};
