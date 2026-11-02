/* Silence detection: where the audio stays below a threshold for at least a minimum
 * length, and the first and last audible frame.
 *
 * One detector (fa_silence_det) serves three callers: the per-stream edge scan here,
 * the FA_ANALYZE_SILENCE pass in analyze.c and the public fa_silence_detector that
 * apps feed with their own PCM. A frame is audible when any channel's |sample| is
 * at or above the threshold; a silent run that lasts at least min_length frames is
 * a region.
 *
 * The edge scan (fa_stream_get_silence) decodes a second copy of the stream's file:
 * from the start up to the first audible frame, then the end, in windows that grow
 * backwards (30 s, then 4x each time) until one holds audio. Every frame is decoded
 * at most once, and when the windows would meet the start, or the format cannot
 * seek cheaply, the rest is decoded in one go and every region is known. */
#include "fa_internal.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define FA_SILENCE_READ 4096
#define FA_SILENCE_TAIL_SECONDS 30
#define FA_SILENCE_MIN_DB -120.0f
#define FA_SILENCE_MAX_MS 3600000u

/* ---- Options ----------------------------------------------------------------------- */

FA_API void FA_CALL fa_silence_options_init(fa_silence_options* options)
{
    if (!options) return;
    memset(options, 0, sizeof(*options));
    options->struct_size = sizeof(*options);
    options->threshold_db = -60.0f;
    options->min_length_ms = 1000;
}

int fa_silence_options_valid(float threshold_db, uint32_t min_length_ms)
{
    return threshold_db >= FA_SILENCE_MIN_DB && threshold_db <= 0.0f && min_length_ms >= 1 &&
           min_length_ms <= FA_SILENCE_MAX_MS;
}

int fa_silence_options_read(const fa_silence_options* in, fa_silence_options* out)
{
    fa_silence_options_init(out);
    if (in && fa_read_sized(out, in, sizeof(*out), FA_V1(fa_silence_options)) != FA_OK) return 0;
    return fa_silence_options_valid(out->threshold_db, out->min_length_ms);
}

/* ---- The detector ------------------------------------------------------------------- */

void fa_silence_det_init(fa_silence_det* d, float threshold_db, uint32_t min_length_ms, uint32_t rate, uint32_t channels)
{
    memset(d, 0, sizeof(*d));
    d->threshold_db = threshold_db;
    d->threshold = (float)pow(10.0, threshold_db / 20.0);
    d->min_length_ms = min_length_ms;
    d->min_length = (fa_frames)ceil((double)min_length_ms * rate / 1000.0);
    if (d->min_length < 1) d->min_length = 1;
    d->rate = rate;
    d->channels = channels;
    d->run_start = 0;   /* the start counts as a silent run until audio comes */
    d->first = d->last = -1;
}

static void add_region(fa_silence_det* d, fa_frames start, fa_frames end)
{
    if (d->failed) return;
    if (d->nregs == d->cap) {
        uint32_t cap = d->cap ? d->cap * 2 : 16;
        fa_silence_region* grown = (fa_silence_region*)realloc(d->regs, (size_t)cap * sizeof(*grown));
        if (!grown) { d->failed = 1; return; }
        d->regs = grown;
        d->cap = cap;
    }
    d->regs[d->nregs].start = start;
    d->regs[d->nregs].end = end;
    d->nregs++;
}

void fa_silence_det_process(fa_silence_det* d, const float* x, fa_frames n)
{
    const float thr = d->threshold;
    const uint32_t ch = d->channels;
    fa_frames i;
    for (i = 0; i < n; i++) {
        const float* f = x + (size_t)i * ch;
        uint32_t c;
        int loud = 0;
        for (c = 0; c < ch; c++)
            if (fabsf(f[c]) >= thr) { loud = 1; break; }
        if (loud) {
            fa_frames at = d->pos + i;
            if (d->run_start >= 0) {
                if (at - d->run_start >= d->min_length) add_region(d, d->run_start, at);
                d->run_start = -1;
            }
            if (d->first < 0) d->first = at;
            d->last = at + 1;
        } else if (d->run_start < 0) {
            d->run_start = d->pos + i;
        }
    }
    d->pos += n;
}

void fa_silence_det_skip_silent(fa_silence_det* d, fa_frames n)
{
    if (n <= 0) return;
    if (d->run_start < 0) d->run_start = d->pos;
    d->pos += n;
}

void fa_silence_det_free(fa_silence_det* d)
{
    free(d->regs);
    d->regs = NULL;
    d->nregs = d->cap = 0;
}

fa_result fa_silence_det_result(const fa_silence_det* d, uint32_t flags, fa_silence_data* out)
{
    uint32_t n = d->nregs;
    int open_run = d->run_start >= 0 && d->pos - d->run_start >= d->min_length;
    memset(out, 0, sizeof(*out));
    if (d->failed) return FA_ERROR_OUT_OF_MEMORY;
    if (open_run) n++;
    if (n) {
        out->regs = (fa_silence_region*)malloc((size_t)n * sizeof(*out->regs));
        if (!out->regs) return FA_ERROR_OUT_OF_MEMORY;
        if (d->nregs) memcpy(out->regs, d->regs, (size_t)d->nregs * sizeof(*out->regs));
        if (open_run) {
            out->regs[n - 1].start = d->run_start;
            out->regs[n - 1].end = d->pos;
        }
    }
    out->nregs = n;
    out->threshold_db = d->threshold_db;
    out->min_length_ms = d->min_length_ms;
    out->info.struct_size = sizeof(out->info);
    out->info.flags = flags;
    out->info.sample_rate = d->rate;
    out->info.first_audible = d->first >= 0 ? d->first : FA_FRAMES_UNSET;
    out->info.last_audible = d->last >= 0 ? d->last : FA_FRAMES_UNSET;
    out->info.length = d->pos;
    return FA_OK;
}

void fa_silence_data_free(fa_silence_data* d)
{
    free(d->regs);
    d->regs = NULL;
    d->nregs = 0;
}

fa_result fa_silence_data_get(const fa_silence_data* d, fa_silence* out, fa_silence_region* regions,
                              uint32_t capacity, uint32_t* out_count)
{
    uint32_t n = d->nregs < capacity ? d->nregs : capacity;
    if (out) fa_copy_sized(out, &d->info, sizeof(d->info));
    if (n) memcpy(regions, d->regs, (size_t)n * sizeof(*regions));
    if (out_count) *out_count = d->nregs;
    return FA_OK;
}

int fa_silence_regions_valid(const fa_silence_region* r, uint32_t n, fa_frames length)
{
    fa_frames prev = 0;
    uint32_t i;
    for (i = 0; i < n; i++) {
        if (r[i].start < prev || r[i].end <= r[i].start || r[i].end > length) return 0;
        prev = r[i].end;
    }
    return 1;
}

/* ---- The edge scan ------------------------------------------------------------------- */

typedef struct scan {
    fa_decoder* dec;
    float* buf;
    uint32_t ch;
} scan;

/* Decodes from where the decoder is into `d` until `stop` (-1 = the end) or, with
 * until_audible, the read that holds the first audible frame. *at_end is set when
 * the decoder ran out. */
static fa_result feed(scan* sc, fa_silence_det* d, fa_frames stop, int until_audible, int* at_end)
{
    *at_end = 0;
    while (stop < 0 || d->pos < stop) {
        fa_frames want = FA_SILENCE_READ, got = 0;
        fa_result r;
        if (stop >= 0 && stop - d->pos < want) want = stop - d->pos;
        r = fa_decoder_read(sc->dec, sc->buf, want, &got);
        if (got <= 0) {
            if (r != FA_OK && r != FA_ERROR_END_OF_STREAM) return r;
            *at_end = 1;
            return FA_OK;
        }
        fa_silence_det_process(d, sc->buf, got);
        if (d->failed) return FA_ERROR_OUT_OF_MEMORY;
        if (until_audible && d->first >= 0) return FA_OK;
    }
    return FA_OK;
}

/* Seeking clears what was still sounding (a MIDI file's release and reverb, a
 * module's sample decay), so a window that ends just after the last note would cut
 * its tail: a window before the last one reads on past its end until the audio has
 * been silent this long. */
#define FA_SILENCE_SETTLE_SECONDS 10

/* The leading part, then the tail windows, of one decoder. */
static fa_result edge_scan(fa_decoder* dec, float threshold_db, uint32_t min_length_ms, fa_silence_data* out)
{
    fa_silence_det lead, tail;
    scan sc;
    fa_frames length = dec->length, window, hi = -1, end = -1, settle;
    uint32_t rate = dec->format.sample_rate;
    int at_end = 0, complete = 0, whole = 0, cheap_seek;
    fa_result r;

    sc.dec = dec;
    sc.ch = dec->format.channels;
    if (rate == 0 || sc.ch == 0) return FA_ERROR_UNSUPPORTED_FORMAT;
    sc.buf = (float*)malloc((size_t)FA_SILENCE_READ * sc.ch * sizeof(float));
    if (!sc.buf) return FA_ERROR_OUT_OF_MEMORY;
    fa_silence_det_init(&lead, threshold_db, min_length_ms, rate, sc.ch);
    memset(&tail, 0, sizeof(tail));
    window = (fa_frames)FA_SILENCE_TAIL_SECONDS * rate;
    settle = (fa_frames)FA_SILENCE_SETTLE_SECONDS * rate;
    if (settle < lead.min_length) settle = lead.min_length;
    /* MIDI and modules are not fast_seek for the loudness scanner (their seek plays
     * the events up to the target), but that costs little next to rendering. */
    cheap_seek = dec->vt->fast_seek || fa_midi_of(dec) || fa_tracker_of(dec);

    r = feed(&sc, &lead, -1, 1, &at_end);
    /* Read on to the end in one go when nothing was audible (already at the end),
     * the format cannot seek cheaply or its length is unknown, or the rest is short. */
    if (r == FA_OK && !at_end && (!dec->seekable || !cheap_seek || length < 0 || length - lead.pos <= 2 * window))
        r = feed(&sc, &lead, -1, 0, &at_end);
    if (r == FA_OK && at_end) complete = 1;
    /* Windows [lo, hi) from the end backwards until one holds audio. The first reads
     * past `length` to the real end (a length can be an estimate). */
    while (r == FA_OK && !complete) {
        fa_frames lo = (hi < 0 ? length : hi) - window;
        if (lo <= lead.pos) {
            /* The windows would meet the leading scan: decode everything before them
             * in one go (from the start: a seek would lose what still sounds) and
             * count the windows as the silence they were. */
            fa_silence_det_free(&lead);
            fa_silence_det_init(&lead, threshold_db, min_length_ms, rate, sc.ch);
            r = fa_decoder_seek(dec, 0);
            if (r == FA_OK) r = feed(&sc, &lead, hi, 0, &at_end);
            if (r == FA_OK && hi >= 0) {
                if (lead.pos != hi) { whole = 1; break; }   /* the decoder came up short */
                /* What still sounds at hi (a tail the seek before a window cut off)
                 * is read on until it has died away, as for a tail window. */
                while (r == FA_OK && lead.pos < end && !(lead.run_start >= 0 && lead.pos - lead.run_start >= settle)) {
                    fa_frames stop = lead.pos + FA_SILENCE_READ < end ? lead.pos + FA_SILENCE_READ : end;
                    r = feed(&sc, &lead, stop, 0, &at_end);
                    if (at_end) break;
                }
                if (r == FA_OK && lead.pos < end) fa_silence_det_skip_silent(&lead, end - lead.pos);
            }
            complete = 1;
            break;
        }
        if (fa_decoder_seek(dec, lo) != FA_OK) { whole = 1; break; }
        fa_silence_det_free(&tail);
        fa_silence_det_init(&tail, threshold_db, min_length_ms, rate, sc.ch);
        tail.pos = tail.run_start = lo;
        r = feed(&sc, &tail, hi, 0, &at_end);
        if (r != FA_OK) break;
        if (hi < 0) end = tail.pos;
        else if (tail.pos != hi) { whole = 1; break; }
        if (tail.last >= 0) {
            /* Read on past hi (a later window) until the tail has died away. */
            while (r == FA_OK && hi >= 0 && tail.pos < end && !(tail.run_start >= 0 && tail.pos - tail.run_start >= settle)) {
                fa_frames stop = tail.pos + FA_SILENCE_READ < end ? tail.pos + FA_SILENCE_READ : end;
                r = feed(&sc, &tail, stop, 0, &at_end);
                if (at_end) break;
            }
            if (r != FA_OK) break;
            /* The leading regions as found, then the silence after the last audible
             * frame (the regions in between are not known). */
            lead.last = tail.last;
            lead.run_start = tail.last;
            lead.pos = end > tail.pos ? end : tail.pos;
            break;
        }
        hi = lo;
        window *= 4;
    }
    if (r == FA_OK && whole) {
        /* A seek that failed or landed elsewhere: decode it all from the start. */
        fa_silence_det_free(&lead);
        fa_silence_det_init(&lead, threshold_db, min_length_ms, rate, sc.ch);
        r = fa_decoder_seek(dec, 0);
        if (r == FA_OK) r = feed(&sc, &lead, -1, 0, &at_end);
        complete = 1;
    }
    if (r == FA_OK) r = fa_silence_det_result(&lead, complete ? FA_SILENCE_COMPLETE : 0, out);
    fa_silence_det_free(&tail);
    fa_silence_det_free(&lead);
    free(sc.buf);
    return r;
}

FA_API fa_result FA_CALL fa_stream_get_silence(fa_stream* stream, const fa_silence_options* options,
                                               fa_silence* out_silence, fa_silence_region* out_regions,
                                               uint32_t capacity, uint32_t* out_count)
{
    fa_silence_options opt;
    fa_silence_data data;
    fa_stream_reader* rd;
    fa_result r;
    if (out_count) *out_count = 0;
    if (!stream || (capacity && !out_regions) || (out_silence && out_silence->struct_size < sizeof(uint32_t)))
        return FA_ERROR_INVALID_ARG;
    if (!fa_silence_options_read(options, &opt)) return FA_ERROR_INVALID_ARG;
    if (fa_analysis_cached_silence(stream, opt.threshold_db, opt.min_length_ms, &data) == FA_OK) {
        r = fa_silence_data_get(&data, out_silence, out_regions, capacity, out_count);
        fa_silence_data_free(&data);
        return r;
    }
    rd = (fa_stream_reader*)calloc(1, sizeof(*rd));
    if (!rd) return FA_ERROR_OUT_OF_MEMORY;
    r = fa_stream_reader_open(stream, rd);
    if (r == FA_OK) {
        r = edge_scan(&rd->dec, opt.threshold_db, opt.min_length_ms, &data);
        fa_stream_reader_close(rd);
        if (r == FA_OK) {
            r = fa_silence_data_get(&data, out_silence, out_regions, capacity, out_count);
            fa_silence_data_free(&data);
        }
    }
    free(rd);
    return r;
}

/* ---- The public detector ----------------------------------------------------------- */

struct fa_silence_detector {
    fa_silence_det det;
    int kind, planar, bytes;
    float* buf;
};

FA_API fa_result FA_CALL fa_silence_detector_create(const fa_audio_format* format, const fa_silence_options* options,
                                                    fa_silence_detector** out_detector)
{
    fa_silence_options opt;
    fa_silence_detector* d;
    int planar, kind;
    if (out_detector) *out_detector = NULL;
    if (!format || !out_detector) return FA_ERROR_INVALID_ARG;
    kind = fa_sample_kind(format->sample_format, &planar);
    if (kind < 0 || format->channels == 0 || format->channels > 32 || format->sample_rate < 1000 ||
        format->sample_rate > FA_MAX_SAMPLE_RATE)
        return FA_ERROR_INVALID_ARG;
    if (!fa_silence_options_read(options, &opt)) return FA_ERROR_INVALID_ARG;
    d = (fa_silence_detector*)calloc(1, sizeof(*d));
    if (!d) return FA_ERROR_OUT_OF_MEMORY;
    d->buf = (float*)malloc((size_t)FA_SILENCE_READ * format->channels * sizeof(float));
    if (!d->buf) { free(d); return FA_ERROR_OUT_OF_MEMORY; }
    d->kind = kind;
    d->planar = planar;
    d->bytes = fa_sample_bytes(kind);
    fa_silence_det_init(&d->det, opt.threshold_db, opt.min_length_ms, format->sample_rate, format->channels);
    *out_detector = d;
    return FA_OK;
}

FA_API fa_result FA_CALL fa_silence_detector_feed(fa_silence_detector* detector, const void* const* planes,
                                                  fa_frames frames)
{
    const uint8_t* p[32];
    uint32_t ch, c;
    fa_frames done = 0;
    if (!detector || frames < 0) return FA_ERROR_INVALID_ARG;
    if (frames == 0) return FA_OK;
    if (!planes || !planes[0]) return FA_ERROR_INVALID_ARG;
    ch = detector->det.channels;
    if (detector->planar) {
        for (c = 0; c < ch; c++) {
            if (!planes[c]) return FA_ERROR_INVALID_ARG;
            p[c] = (const uint8_t*)planes[c];
        }
    } else {
        p[0] = (const uint8_t*)planes[0];
    }
    while (done < frames) {
        fa_frames n = frames - done < FA_SILENCE_READ ? frames - done : FA_SILENCE_READ;
        const void* at[32];
        if (detector->planar) {
            for (c = 0; c < ch; c++) at[c] = p[c] + (size_t)done * detector->bytes;
        } else {
            at[0] = p[0] + (size_t)done * ch * detector->bytes;
        }
        fa_convert_to_f32(at, detector->kind, detector->planar, 0, ch, n, detector->buf);
        fa_silence_det_process(&detector->det, detector->buf, n);
        done += n;
    }
    return detector->det.failed ? FA_ERROR_OUT_OF_MEMORY : FA_OK;
}

FA_API fa_result FA_CALL fa_silence_detector_get(fa_silence_detector* detector, fa_silence* out_silence,
                                                 fa_silence_region* out_regions, uint32_t capacity,
                                                 uint32_t* out_count)
{
    fa_silence_data data;
    fa_result r;
    if (out_count) *out_count = 0;
    if (!detector || (capacity && !out_regions) || (out_silence && out_silence->struct_size < sizeof(uint32_t)))
        return FA_ERROR_INVALID_ARG;
    r = fa_silence_det_result(&detector->det, 0, &data);
    if (r != FA_OK) return r;
    r = fa_silence_data_get(&data, out_silence, out_regions, capacity, out_count);
    fa_silence_data_free(&data);
    return r;
}

FA_API void FA_CALL fa_silence_detector_free(fa_silence_detector* detector)
{
    if (!detector) return;
    fa_silence_det_free(&detector->det);
    free(detector->buf);
    free(detector);
}
