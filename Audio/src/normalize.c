/* Loudness normalization: engine settings, the per-stream gain and the
 * loudness API. The measuring is in loudness.c, the background scans in scan.c,
 * and the gain itself is applied (with its glide) in the mixer, stream.c.
 *
 * Lock order: stream api_lock -> engine list_lock -> stream norm_lock -> engine
 * scan_lock. Nothing that holds norm_lock posts events (that takes list_lock). */
#include "fa_internal.h"
#include <math.h>
#include <string.h>

#define FA_NORM_MAX_BOOST_DB 12.0f
#define FA_NORM_MAX_CUT_DB -30.0f

float fa_engine_norm_gain_db(fa_engine* e, double lufs)
{
    float g;
    if (fa_load32(&e->norm_mode) == FA_NORMALIZE_OFF) return 0.0f;
    if (!(lufs > -70.0)) return 0.0f;   /* unknown (NAN) or silence: leave it alone */
    g = (float)((double)fa_load_float(&e->norm_target) - lufs);
    if (g > FA_NORM_MAX_BOOST_DB) g = FA_NORM_MAX_BOOST_DB;
    if (g < FA_NORM_MAX_CUT_DB) g = FA_NORM_MAX_CUT_DB;
    return g;
}

/* A value that is not final yet: the scan so far, then the running estimate. */
static float provisional_lufs(struct fa_stream* s, fa_loudness_source* out_source)
{
    float lufs = fa_load_float(&s->norm_partial);
    if (lufs == lufs) { *out_source = FA_LOUDNESS_PARTIAL; return lufs; }
    lufs = fa_load_float(&s->norm_est);
    *out_source = lufs == lufs ? FA_LOUDNESS_ESTIMATE : FA_LOUDNESS_NONE;
    return lufs;
}

/* The loudness the gain is made from: final (album, track) first, then a provisional
 * value. *out_final says which; *out_source is set for a provisional one. */
static float stream_lufs(struct fa_stream* s, int mode, int* out_final, fa_loudness_source* out_source)
{
    float lufs = NAN;
    if (mode == FA_NORMALIZE_ALBUM) lufs = fa_load_float(&s->norm_album);
    if (lufs != lufs) lufs = fa_load_float(&s->norm_track);
    *out_final = lufs == lufs;
    *out_source = FA_LOUDNESS_NONE;
    return *out_final ? lufs : provisional_lufs(s, out_source);
}

int fa_stream_norm_applies(struct fa_stream* s)
{
    return fa_load32(&s->engine->norm_mode) != FA_NORMALIZE_OFF && !(s->flags & (FA_OPEN_NO_NORMALIZE | FA_OPEN_DECODE_ONLY));
}

int fa_stream_norm_provisional(struct fa_stream* s)
{
    int final;
    fa_loudness_source src;
    stream_lufs(s, fa_load32(&s->engine->norm_mode), &final, &src);
    return final ? -1 : (int)src;
}

float fa_stream_norm_target_db(struct fa_stream* s)
{
    int final;
    fa_loudness_source src;
    float lufs, g;
    if (!fa_stream_norm_applies(s)) return 0.0f;
    lufs = stream_lufs(s, fa_load32(&s->engine->norm_mode), &final, &src);
    g = fa_engine_norm_gain_db(s->engine, lufs);
    if (!final) {
        float cap = fa_load_float(&s->engine->norm_prov_boost);   /* a guess boosts at most this */
        if (g > cap) g = cap;
    }
    /* The loudness is of the source's channels; a Dolby downmix scaled down so it
     * cannot clip gets that back (the limiter catches the peaks). */
    return g + fa_load_float(&s->dolby_makeup);
}

/* Starts tags/cache/scan for the stream once. */
void fa_stream_start_analysis(struct fa_stream* s)
{
    fa_store32(&s->est_wanted, 1);
    fa_mutex_lock(&s->norm_lock);
    if (!s->scan_job && !s->loud.is_final && !s->analysis_tried) {
        s->analysis_tried = 1;
        fa_store32(&s->norm_pending, 1);   /* before the job can deliver */
        /* A fed song (packet and push streams with a source identity) looks in the cache and
         * for a running feed (scan_feed.c); everything else is read from its file. */
        s->scan_job = s->idslot ? fa_id_start(s) : fa_scan_start_stream(s, 1);
        if (!s->scan_job) fa_store32(&s->norm_pending, 0);
    }
    fa_mutex_unlock(&s->norm_lock);
}

void fa_stream_loudness_ready(struct fa_stream* s, const fa_loudness* l, double album_lufs)
{
    int post = 0;
    fa_mutex_lock(&s->norm_lock);
    if (s->loud.source != FA_LOUDNESS_APP) {
        if (l->integrated_lufs == l->integrated_lufs) {
            s->loud = *l;
            s->loud.gain_db = 0.0;
            fa_store_float(&s->norm_track, (float)l->integrated_lufs);
            post = 1;
        }
        if (album_lufs == album_lufs) fa_store_float(&s->norm_album, (float)album_lufs);
    }
    fa_store32(&s->norm_pending, 0);      /* after the value: a held first play starts on it */
    fa_mutex_unlock(&s->norm_lock);
    if (post) {
        fa_event ev;
        ev.type = FA_EVENT_LOUDNESS_FINAL;
        ev.stream = s;
        fa_mutex_lock(&s->api_lock);      /* the position reads attach state */
        ev.frame = fa_stream_position_now(s);
        fa_mutex_unlock(&s->api_lock);
        ev.value = 0;
        fa_engine_post_event(s->engine, &ev);
    }
}

void fa_stream_loudness_partial(struct fa_stream* s, double lufs)
{
    fa_store_float(&s->norm_partial, (float)lufs);
}

/* ---- Public API ------------------------------------------------------------------ */

FA_API fa_result FA_CALL fa_engine_set_normalization(fa_engine* engine, fa_normalize_mode mode, double target_lufs)
{
    struct fa_stream* s;
    if (!engine || (int)mode < FA_NORMALIZE_OFF || mode > FA_NORMALIZE_ALBUM) return FA_ERROR_INVALID_ARG;
    if (mode != FA_NORMALIZE_OFF) {
        if (!(target_lufs >= -60.0 && target_lufs <= 0.0)) return FA_ERROR_INVALID_ARG;
        fa_store_float(&engine->norm_target, (float)target_lufs);
    }
    fa_store32(&engine->norm_mode, (int32_t)mode);
    if (mode != FA_NORMALIZE_OFF) {
        fa_mutex_lock(&engine->list_lock);   /* attached streams cannot be closed meanwhile */
        for (s = engine->streams; s; s = s->next)
            if (!(s->flags & FA_OPEN_NO_NORMALIZE)) fa_stream_start_analysis(s);
        fa_mutex_unlock(&engine->list_lock);
    }
    return FA_OK;
}

FA_API fa_result FA_CALL fa_engine_set_normalization_start_wait(fa_engine* engine, uint32_t wait_ms,
                                                                double max_provisional_boost_db)
{
    if (!engine || wait_ms > 10000 || !(max_provisional_boost_db >= 0.0 && max_provisional_boost_db <= FA_NORM_MAX_BOOST_DB))
        return FA_ERROR_INVALID_ARG;
    fa_store_float(&engine->norm_prov_boost, (float)max_provisional_boost_db);
    fa_store32(&engine->norm_start_wait_ms, (int32_t)wait_ms);
    return FA_OK;
}

FA_API fa_result FA_CALL fa_engine_set_dialnorm(fa_engine* engine, int32_t enabled)
{
    if (!engine) return FA_ERROR_INVALID_ARG;
    fa_store32(&engine->use_dialnorm, enabled != 0);
    return FA_OK;
}

FA_API fa_result FA_CALL fa_engine_set_limiter(fa_engine* engine, fa_limiter_mode mode, double ceiling_dbtp)
{
    if (!engine || (int)mode < FA_LIMITER_AUTO || mode > FA_LIMITER_OFF) return FA_ERROR_INVALID_ARG;
    if (!(ceiling_dbtp >= -20.0 && ceiling_dbtp <= 0.0)) return FA_ERROR_INVALID_ARG;
    fa_store_float(&engine->limiter_ceiling, (float)ceiling_dbtp);
    fa_store32(&engine->limiter_mode, (int32_t)mode);
    return FA_OK;
}

FA_API fa_result FA_CALL fa_stream_get_loudness(fa_stream* stream, fa_loudness* out_loudness)
{
    fa_loudness l;
    if (!stream || !out_loudness || out_loudness->struct_size < sizeof(uint32_t)) return FA_ERROR_INVALID_ARG;
    fa_stream_start_analysis(stream);
    fa_mutex_lock(&stream->norm_lock);
    if (stream->loud.is_final) {
        l = stream->loud;
    } else {
        fa_loudness_source src;
        memset(&l, 0, sizeof(l));
        l.integrated_lufs = provisional_lufs(stream, &src);
        l.range_lu = NAN;
        l.true_peak_dbtp = NAN;
        l.source = src;
    }
    fa_mutex_unlock(&stream->norm_lock);
    l.gain_db = fa_stream_norm_target_db(stream);
    fa_copy_sized(out_loudness, &l, sizeof(l));
    return FA_OK;
}

FA_API fa_result FA_CALL fa_stream_set_loudness(fa_stream* stream, const fa_loudness* loudness)
{
    fa_scan_job* job;
    fa_loudness l;
    if (!stream || !loudness) return FA_ERROR_INVALID_ARG;
    memset(&l, 0, sizeof(l));
    l.range_lu = l.true_peak_dbtp = NAN;
    if (fa_read_sized(&l, loudness, sizeof(l), FA_V1(fa_loudness)) != FA_OK || l.integrated_lufs != l.integrated_lufs)
        return FA_ERROR_INVALID_ARG;
    l.is_final = 1;
    l.source = FA_LOUDNESS_APP;
    l.gain_db = 0.0;
    fa_mutex_lock(&stream->norm_lock);
    stream->loud = l;
    fa_store_float(&stream->norm_track, (float)l.integrated_lufs);
    fa_store_float(&stream->norm_album, NAN);   /* the app's value wins in album mode too */
    job = stream->scan_job;
    stream->scan_job = NULL;
    fa_store32(&stream->norm_pending, 0);
    fa_mutex_unlock(&stream->norm_lock);
    fa_scan_release(job);
    return FA_OK;
}
