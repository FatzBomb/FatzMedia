/* Loudness scans of audio the app feeds (MediaControl request 13).
 *
 * A feed is one song's audio pushed in order on the app's own thread: packets (decoded
 * here) or decoded frames. The calling thread decodes and measures as it pushes, with
 * the same meter as a scan by path, and the result goes to the loudness cache under the
 * song's identity (the fingerprint of the original file plus the track).
 *
 * Playing packet and push streams opened with a source identity have a "slot" in the
 * engine's registry, keyed the same way. A feed tells the slots of its song how far it
 * has got and, at the end, the result; a stream whose analysis starts later finds the
 * result in the cache, or the running feed in the registry.
 *
 * Locks. The registry (engine->id_slots, engine->feeds, slot refs) is under
 * engine->scan_lock, held only for list operations. Delivering to a stream takes the
 * slot's own lock, then the stream's locks (norm_lock, api_lock, list_lock) as the scan
 * threads' deliver() does; fa_id_detach takes the slot lock the same way fa_scan_release
 * waits for a scan: from fa_stream_close, holding none of the stream's locks. */
#include "fa_internal.h"
#include "ac3.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define FA_FEED_PUBLISH_S 5        /* progress and the partial value are published this often (audio seconds) */
#define FA_FEED_PARTIAL_MIN_S 30   /* with no duration given: the partial value starts after this much */
#define FA_FEED_DIALNORM_TRIES 4   /* packets looked at for a Dolby stream's dialogue level */

typedef struct fa_idslot {
    int refs;                      /* under scan_lock: the stream's and each delivery in progress */
    fa_mutex lock;                 /* held while delivering to s */
    struct fa_stream* s;           /* NULL once the stream closed */
    uint64_t key;
    int64_t size;
    int32_t track;
    struct fa_idslot* next;        /* engine->id_slots */
} fa_idslot;

struct fa_scan_feed {
    fa_engine* engine;
    uint64_t key;
    int64_t size;
    int32_t track;
    double duration_s;
    int packets;                   /* fed packets (else frames) */
    fa_codec codec;
    int codec_ready;
    fa_atomic64 dolby_mix;
    char codec_name[32];
    int use_dialnorm;
    int dn_tries;
    uint32_t rate, channels;       /* fixed by the first audio measured (or by open_push) */
    fa_meter m;
    int meter_ready;
    float* conv;                   /* decoded frames converted to float */
    size_t conv_cap;               /* in floats */
    fa_frames fed;                 /* frames measured */
    fa_frames next_publish;
    int decided;                   /* the result is known without more audio (cache, dialnorm) */
    int finished;
    fa_result status;
    fa_loudness result;
    int registered;                /* in engine->feeds */
    struct fa_scan_feed* next;
};

/* ---- The registry ------------------------------------------------------------------ */

static int same_song(uint64_t key, int64_t size, int32_t track, uint64_t k2, int64_t s2, int32_t t2)
{
    return key == k2 && size == s2 && track == t2;
}

static fa_result read_identity(const struct fa_fingerprint* in, fa_fingerprint* out)
{
    memset(out, 0, sizeof(*out));
    out->struct_size = (uint32_t)sizeof(*out);
    return fa_read_sized(out, in, sizeof(*out), FA_SIZE_TO(fa_fingerprint, audio_bytes));
}

fa_result fa_id_attach(struct fa_stream* s, const struct fa_fingerprint* id, int32_t track)
{
    fa_engine* e = s->engine;
    fa_idslot* sl;
    fa_fingerprint fp;
    if (!id) return FA_OK;
    if (read_identity(id, &fp) != FA_OK) return FA_ERROR_INVALID_ARG;
    sl = (fa_idslot*)calloc(1, sizeof(*sl));
    if (!sl) return FA_ERROR_OUT_OF_MEMORY;
    if (fa_mutex_init(&sl->lock) != 0) { free(sl); return FA_ERROR_GENERIC; }
    sl->refs = 1;
    sl->s = s;
    sl->key = fp.key;
    sl->size = fp.audio_bytes;
    sl->track = track;
    s->idslot = sl;
    fa_mutex_lock(&e->scan_lock);
    sl->next = e->id_slots;
    e->id_slots = sl;
    fa_mutex_unlock(&e->scan_lock);
    return FA_OK;
}

static void slot_unref(fa_engine* e, fa_idslot* sl)
{
    int last;
    fa_mutex_lock(&e->scan_lock);
    last = --sl->refs == 0;
    fa_mutex_unlock(&e->scan_lock);
    if (last) { fa_mutex_destroy(&sl->lock); free(sl); }
}

void fa_id_detach(struct fa_stream* s)
{
    fa_engine* e = s->engine;
    fa_idslot* sl = s->idslot;
    fa_idslot** pp;
    if (!sl) return;
    s->idslot = NULL;
    fa_mutex_lock(&sl->lock);      /* a delivery in progress finishes first */
    sl->s = NULL;
    fa_mutex_unlock(&sl->lock);
    fa_mutex_lock(&e->scan_lock);
    for (pp = &e->id_slots; *pp; pp = &(*pp)->next)
        if (*pp == sl) { *pp = sl->next; break; }
    fa_mutex_unlock(&e->scan_lock);
    slot_unref(e, sl);
}

typedef void (*slot_fn)(struct fa_stream* s, const void* arg);

/* Calls fn on every open stream of the song. */
static void for_each_slot(fa_engine* e, uint64_t key, int64_t size, int32_t track, slot_fn fn, const void* arg)
{
    fa_idslot** list;
    fa_idslot* sl;
    int n = 0, i = 0;
    fa_mutex_lock(&e->scan_lock);
    for (sl = e->id_slots; sl; sl = sl->next) n += same_song(sl->key, sl->size, sl->track, key, size, track);
    list = n ? (fa_idslot**)malloc((size_t)n * sizeof(*list)) : NULL;
    if (list)
        for (sl = e->id_slots; sl && i < n; sl = sl->next)
            if (same_song(sl->key, sl->size, sl->track, key, size, track)) { sl->refs++; list[i++] = sl; }
    fa_mutex_unlock(&e->scan_lock);
    for (n = 0; n < i; n++) {
        fa_mutex_lock(&list[n]->lock);
        if (list[n]->s) fn(list[n]->s, arg);
        fa_mutex_unlock(&list[n]->lock);
        slot_unref(e, list[n]);
    }
    free(list);
}

static int wants_result(struct fa_stream* s)
{
    int w;
    fa_mutex_lock(&s->norm_lock);
    w = s->analysis_tried && !s->loud.is_final;
    fa_mutex_unlock(&s->norm_lock);
    return w;
}

static void fn_kick(struct fa_stream* s, const void* arg)
{
    (void)arg;
    if (wants_result(s)) fa_store32(&s->norm_pending, 1);
}

typedef struct progress_arg { int32_t permille; double lufs; } progress_arg;

static void fn_progress(struct fa_stream* s, const void* arg)
{
    const progress_arg* p = (const progress_arg*)arg;
    if (!wants_result(s)) return;
    fa_store32(&s->norm_progress, p->permille);
    if (p->lufs == p->lufs) fa_stream_loudness_partial(s, p->lufs);
}

/* The result, or (integrated_lufs NAN) the end of the scan with none. */
static void fn_final(struct fa_stream* s, const void* arg)
{
    if (wants_result(s)) fa_stream_loudness_ready(s, (const fa_loudness*)arg, NAN);
}

/* ---- Streams ----------------------------------------------------------------------- */

int fa_feed_running(fa_engine* e, uint64_t key, int64_t size, int32_t track)
{
    struct fa_scan_feed* f;
    fa_mutex_lock(&e->scan_lock);
    for (f = e->feeds; f; f = f->next) if (same_song(f->key, f->size, f->track, key, size, track)) break;
    fa_mutex_unlock(&e->scan_lock);
    return f != NULL;
}

/* Tags are not looked at: a fed song has no file for them. The scan thread that runs the job looks in
 * the cache, then for a running feed; a feed that starts later finds the stream (analysis_tried is set)
 * in its kick, and one that ends delivers to it. */
fa_scan_job* fa_id_start(struct fa_stream* s)
{
    fa_idslot* sl = s->idslot;
    return sl ? fa_scan_start_identity(s, sl->key, sl->size, sl->track) : NULL;
}

/* ---- Feeds ------------------------------------------------------------------------- */

static void unregister(struct fa_scan_feed* f)
{
    fa_engine* e = f->engine;
    struct fa_scan_feed** pp;
    if (!f->registered) return;
    fa_mutex_lock(&e->scan_lock);
    for (pp = &e->feeds; *pp; pp = &(*pp)->next)
        if (*pp == f) { *pp = f->next; break; }
    fa_mutex_unlock(&e->scan_lock);
    f->registered = 0;
}

FA_API void FA_CALL fa_scan_options_init(fa_scan_options* options)
{
    if (!options) return;
    memset(options, 0, sizeof(*options));
    options->struct_size = (uint32_t)sizeof(*options);
    options->audio_track = -1;
}

static fa_result feed_new(fa_engine* engine, const fa_scan_options* options, struct fa_scan_feed** out)
{
    fa_scan_options o;
    struct fa_scan_feed* f;
    fa_fingerprint id;
    *out = NULL;
    if (!engine || !options) return FA_ERROR_INVALID_ARG;
    fa_scan_options_init(&o);
    if (fa_read_sized(&o, options, sizeof(o), FA_V1(fa_scan_options)) != FA_OK) return FA_ERROR_INVALID_ARG;
    if (!o.identity || !(o.duration_seconds >= 0.0) || o.duration_seconds > 1e7) return FA_ERROR_INVALID_ARG;
    if (read_identity(o.identity, &id) != FA_OK) return FA_ERROR_INVALID_ARG;
    f = (struct fa_scan_feed*)calloc(1, sizeof(*f));
    if (!f) return FA_ERROR_OUT_OF_MEMORY;
    f->engine = engine;
    f->key = id.key;
    f->size = id.audio_bytes;
    f->track = o.audio_track;
    f->duration_s = o.duration_seconds;
    f->use_dialnorm = fa_load32(&engine->use_dialnorm);
    f->status = FA_ERROR_END_OF_STREAM;
    f->next_publish = 0;
    f->result.integrated_lufs = f->result.range_lu = f->result.true_peak_dbtp = NAN;
    *out = f;
    return FA_OK;
}

static void feed_free(struct fa_scan_feed* f)
{
    if (f->codec_ready) fa_codec_close(&f->codec);
    if (f->meter_ready) fa_meter_free(&f->m);
    free(f->conv);
    free(f);
}

/* Registers the feed (so streams see it) and, if the result is known already, decides. */
static void feed_start(struct fa_scan_feed* f)
{
    fa_engine* e = f->engine;
    fa_loudness cached;
    memset(&cached, 0, sizeof(cached));
    if (e->lcache && fa_lcache_get(e->lcache, f->key, f->size, f->track, &cached)) {
        f->result = cached;
        f->result.is_final = 1;
        f->result.source = FA_LOUDNESS_CACHE;
        f->status = FA_OK;
        f->decided = 1;
        for_each_slot(e, f->key, f->size, f->track, fn_final, &f->result);
        return;
    }
    fa_mutex_lock(&e->scan_lock);
    f->next = e->feeds;
    e->feeds = f;
    f->registered = 1;
    fa_mutex_unlock(&e->scan_lock);
    /* Streams that asked before the feed existed now have something to wait for. */
    for_each_slot(e, f->key, f->size, f->track, fn_kick, NULL);
}

FA_API fa_result FA_CALL fa_scan_open_packets(fa_engine* engine, const fa_codec_params* params,
                                              const fa_scan_options* options, fa_scan_feed** out_feed)
{
    struct fa_scan_feed* f;
    fa_codec_params cp;
    fa_result r;
    if (out_feed) *out_feed = NULL;
    if (!out_feed || !params) return FA_ERROR_INVALID_ARG;
    fa_codec_params_init(&cp);
    if (fa_read_sized(&cp, params, sizeof(cp), FA_V1(fa_codec_params)) != FA_OK) return FA_ERROR_INVALID_ARG;
    if (!cp.codec || !cp.codec[0] || strlen(cp.codec) >= 32) return FA_ERROR_INVALID_ARG;
    if (cp.sample_rate < 1000 || cp.sample_rate > FA_MAX_SAMPLE_RATE || cp.channels == 0 || cp.channels > 32 ||
        (cp.extradata_size > 0 && !cp.extradata))
        return FA_ERROR_INVALID_ARG;
    r = feed_new(engine, options, &f);
    if (r != FA_OK) return r;
    f->packets = 1;
    snprintf(f->codec_name, sizeof(f->codec_name), "%s", cp.codec);
    r = fa_codec_open(&f->codec, &cp);
    if (r != FA_OK) { free(f); return r; }
    f->codec_ready = 1;
    f->codec.dynamic_range = NULL;              /* full range: a scan measures the whole mix */
    f->codec.dolby_mix = &f->dolby_mix;
    feed_start(f);
    *out_feed = f;
    return FA_OK;
}

FA_API fa_result FA_CALL fa_scan_open_push(fa_engine* engine, const fa_audio_format* format,
                                           const fa_scan_options* options, fa_scan_feed** out_feed)
{
    struct fa_scan_feed* f;
    fa_result r;
    if (out_feed) *out_feed = NULL;
    if (!out_feed || !format) return FA_ERROR_INVALID_ARG;
    if (format->sample_rate < 1000 || format->sample_rate > FA_MAX_SAMPLE_RATE || format->channels == 0 ||
        format->channels > 32)
        return FA_ERROR_INVALID_ARG;
    r = feed_new(engine, options, &f);
    if (r != FA_OK) return r;
    f->rate = format->sample_rate;
    f->channels = format->channels;
    feed_start(f);
    *out_feed = f;
    return FA_OK;
}

FA_API int32_t FA_CALL fa_scan_wants_more(fa_scan_feed* feed)
{
    return feed && !feed->decided && !feed->finished;
}

/* Tells the song's streams how far the feed is and the value so far. */
static void publish(struct fa_scan_feed* f)
{
    progress_arg a;
    double per = (double)f->rate;
    int past_start;
    a.lufs = NAN;
    a.permille = 0;
    if (f->duration_s > 0.0) {
        double p = (double)f->fed / (per * f->duration_s) * 1000.0;
        a.permille = p > 1000.0 ? 1000 : (int32_t)p;
    }
    /* The value so far leans on the start of the song (a feed goes in order), so it is
     * offered only after a quarter of the song, or 30 s when its length is not known. */
    past_start = f->duration_s > 0.0 ? (double)f->fed >= 0.25 * per * f->duration_s
                                     : (double)f->fed >= per * FA_FEED_PARTIAL_MIN_S;
    if (past_start && f->m.nsubs > 0) {
        double l = fa_loudness_integrated(f->m.subs, f->m.nsubs);
        if (l > -70.0) a.lufs = l;
    }
    for_each_slot(f->engine, f->key, f->size, f->track, fn_progress, &a);
    f->next_publish = f->fed + (fa_frames)(per * FA_FEED_PUBLISH_S);
}

static fa_result measure(struct fa_scan_feed* f, const float* x, fa_frames n, uint32_t rate, uint32_t channels)
{
    fa_result r;
    if (n <= 0) return FA_OK;
    if (f->meter_ready && (rate != f->rate || channels != f->channels)) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (!f->meter_ready) {
        f->rate = rate;
        f->channels = channels;
        r = fa_meter_init(&f->m, rate, channels, 1, 0);
        if (r != FA_OK) return r;
        f->meter_ready = 1;
    }
    r = fa_meter_process(&f->m, x, n);
    if (r != FA_OK) return r;
    f->fed += n;
    if (f->fed >= f->next_publish) publish(f);
    return FA_OK;
}

/* The first units of a Dolby stream state its dialogue level; with fa_engine_set_dialnorm
 * on, that is the loudness and nothing more needs to be fed (as for a scan by path). */
static void try_dialnorm(struct fa_scan_feed* f)
{
    fa_ac3_mix m;
    if (!f->use_dialnorm || !fa_ac3_is_codec(f->codec_name) || f->dn_tries >= FA_FEED_DIALNORM_TRIES) return;
    f->dn_tries++;
    if (!fa_ac3_mix_unpack((uint64_t)fa_load64(&f->dolby_mix), &m)) return;
    f->dn_tries = FA_FEED_DIALNORM_TRIES;
    if (m.dialnorm == 0 || m.dialnorm == 31) return;
    f->result.integrated_lufs = -(double)m.dialnorm;
    f->result.range_lu = f->result.true_peak_dbtp = NAN;
    f->result.is_final = 1;
    f->result.source = FA_LOUDNESS_DIALNORM;
    f->status = FA_OK;
    f->decided = 1;
    unregister(f);
    for_each_slot(f->engine, f->key, f->size, f->track, fn_final, &f->result);
}

FA_API fa_result FA_CALL fa_scan_push_packet(fa_scan_feed* f, const fa_packet* p)
{
    fa_result r;
    if (!f || !p || !f->packets || f->finished) return FA_ERROR_INVALID_ARG;
    if (f->decided || p->size == 0) return FA_OK;
    if (!p->data || p->size > (64u << 20)) return FA_ERROR_INVALID_ARG;
    r = f->codec.vt->decode(&f->codec, (const uint8_t*)p->data, p->size);
    if (r != FA_OK) return r;
    try_dialnorm(f);
    if (f->decided) return FA_OK;
    if (f->codec.out_frames > 0 && f->codec.rate > 0 && f->codec.channels > 0)
        return measure(f, f->codec.out, f->codec.out_frames, f->codec.rate, f->codec.channels);
    return FA_OK;
}

FA_API fa_result FA_CALL fa_scan_push_frame(fa_scan_feed* f, const fa_audio_frame* frame)
{
    int planar, kind;
    size_t need;
    uint32_t c;
    if (!f || !frame || f->packets || f->finished) return FA_ERROR_INVALID_ARG;
    if (f->decided || frame->frames == 0) return FA_OK;
    kind = fa_sample_kind(frame->format.sample_format, &planar);
    if (kind < 0 || frame->frames < 0 || frame->frames > (fa_frames)1 << 24 || !frame->data || !frame->data[0])
        return FA_ERROR_INVALID_ARG;
    if (frame->format.sample_rate != f->rate || frame->format.channels != f->channels) return FA_ERROR_UNSUPPORTED_FORMAT;
    if (planar)
        for (c = 0; c < frame->format.channels; c++) if (!frame->data[c]) return FA_ERROR_INVALID_ARG;
    need = (size_t)frame->frames * f->channels;
    if (need > f->conv_cap) {
        float* p = (float*)realloc(f->conv, need * sizeof(float));
        if (!p) return FA_ERROR_OUT_OF_MEMORY;
        f->conv = p;
        f->conv_cap = need;
    }
    fa_convert_to_f32(frame->data, kind, planar, 0, f->channels, frame->frames, f->conv);
    return measure(f, f->conv, frame->frames, f->rate, f->channels);
}

FA_API fa_result FA_CALL fa_scan_finish(fa_scan_feed* f, fa_loudness* out_loudness)
{
    if (!f || !out_loudness || out_loudness->struct_size < sizeof(uint32_t)) return FA_ERROR_INVALID_ARG;
    if (!f->finished) {
        if (!f->decided) {
            f->status = FA_ERROR_END_OF_STREAM;
            /* The decoder gives back what it still holds. */
            if (f->packets && f->codec_ready && f->codec.vt->decode(&f->codec, NULL, 0) == FA_OK && f->codec.out_frames > 0 &&
                f->codec.rate > 0 && f->codec.channels > 0)
                measure(f, f->codec.out, f->codec.out_frames, f->codec.rate, f->codec.channels);
            if (f->meter_ready && f->fed > 0) {
                fa_truepeak_flush(f->m.tp);
                f->result.integrated_lufs = fa_loudness_integrated(f->m.subs, f->m.nsubs);
                f->result.range_lu = fa_loudness_range(f->m.subs, f->m.nsubs);
                f->result.true_peak_dbtp = fa_lin_to_db(f->m.tp->peak);
                f->result.is_final = 1;
                f->result.source = FA_LOUDNESS_SCAN;
                f->status = FA_OK;
                if (f->engine->lcache) fa_lcache_put(f->engine->lcache, f->key, f->size, f->track, &f->result);
            }
            unregister(f);
            if (f->status == FA_OK) {
                for_each_slot(f->engine, f->key, f->size, f->track, fn_final, &f->result);
            } else {
                fa_loudness none;
                memset(&none, 0, sizeof(none));
                none.integrated_lufs = NAN;
                for_each_slot(f->engine, f->key, f->size, f->track, fn_final, &none);   /* streams stop waiting */
            }
        }
        f->finished = 1;
    }
    if (f->status != FA_OK) return f->status;
    f->result.gain_db = fa_engine_norm_gain_db(f->engine, f->result.integrated_lufs);
    fa_copy_sized(out_loudness, &f->result, sizeof(f->result));
    return FA_OK;
}

FA_API void FA_CALL fa_scan_close(fa_scan_feed* f)
{
    fa_loudness none;
    if (!f) return;
    if (!f->finished && !f->decided) {
        unregister(f);
        memset(&none, 0, sizeof(none));
        none.integrated_lufs = NAN;
        for_each_slot(f->engine, f->key, f->size, f->track, fn_final, &none);   /* streams stop waiting */
    }
    unregister(f);
    feed_free(f);
}
