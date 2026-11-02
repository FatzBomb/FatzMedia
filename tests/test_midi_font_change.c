/* Soundfonts (final audit step 8): changing a MIDI stream's soundfont while it plays
 * (fa_stream_set_soundfont, fa_stream_get_soundfont) for files and the real-time synth,
 * and SF2 2.04 24-bit samples (the sm24 chunk). Public API, with the sine soundfont of
 * midi_synth_test.h and 24-bit banks built here. */
#if !defined(_WIN32)
#  define _POSIX_C_SOURCE 200809L
#endif
#include "midi_synth_test.h"
#if defined(_WIN32)
#  include <windows.h>
static void sleep_ms(unsigned ms) { Sleep(ms); }
#else
#  include <time.h>
static void sleep_ms(unsigned ms)
{
    struct timespec t;
    t.tv_sec = ms / 1000;
    t.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&t, NULL);
}
#endif

static fa_soundfont* g_octave;   /* the same sine, an octave up (880 Hz for key 69) */

static float* read_frames(fa_stream* s, float* x, fa_frames from, fa_frames to)
{
    fa_frames done = from, got;
    while (done < to) {
        fa_frames want = to - done < 1000 ? to - done : 1000;
        if (fa_stream_read(s, x + done * 2, want, &got) != FA_OK || !got) break;
        done += got;
    }
    return x;
}

static double peak(const float* x, double from_s, double to_s)
{
    fa_frames i, a = (fa_frames)(from_s * RATE), b = (fa_frames)(to_s * RATE);
    double p = 0;
    for (i = a; i < b; i++) if (fabs(x[i * 2]) > p) p = fabs(x[i * 2]);
    return p;
}

/* The largest step between two samples in [from_s, to_s): a cut between two sounds
 * shows as a step far above what a sine at its peak level makes. */
static double max_step(const float* x, double from_s, double to_s)
{
    fa_frames i, a = (fa_frames)(from_s * RATE), b = (fa_frames)(to_s * RATE);
    double m = 0;
    for (i = a + 1; i < b; i++) if (fabs(x[i * 2] - x[(i - 1) * 2]) > m) m = fabs(x[i * 2] - x[(i - 1) * 2]);
    return m;
}

/* A file: one note held from 0 to 1.8 s. The font changes 0.5 s in; the note goes on an
 * octave up (the new soundfont's instrument), without a click. */
static void test_file_change(void)
{
    song sg = { 0 };
    buf f;
    fa_open_options o;
    fa_stream* s = NULL;
    fa_soundfont* got = NULL;
    float* x = (float*)calloc((size_t)SECONDS * RATE * 2, sizeof(float));
    track before, after;
    double level;
    note(&sg, 0, 1800, 69);
    f = finish(&sg);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = g_font;
    CHECK(fa_stream_open_memory(g_engine, f.d, f.n, &o, &s) == FA_OK);
    fa_stream_set_midi_effects(s, 0);
    CHECK(fa_stream_get_soundfont(s, &got) == FA_OK && got == g_font);
    fa_soundfont_close(got);
    read_frames(s, x, 0, RATE / 2);
    CHECK(fa_stream_set_soundfont(s, g_octave) == FA_OK);
    CHECK(fa_stream_get_soundfont(s, &got) == FA_OK && got == g_octave);   /* the one set, before it is heard */
    fa_soundfont_close(got);
    read_frames(s, x, RATE / 2, (fa_frames)SECONDS * RATE);
    fa_stream_close(s);
    before = pitch_track(x, 0.1, 0.49, 440.0);
    after = pitch_track(x, 0.53, 1.7, 440.0);
    CHECK_NEAR(before.lo, 0.0, 5.0);
    CHECK_NEAR(before.hi, 0.0, 5.0);
    CHECK_NEAR(after.lo, 1200.0, 5.0);
    CHECK_NEAR(after.hi, 1200.0, 5.0);
    level = peak(x, 0.1, 0.49);
    CHECK(level > 0.1);
    CHECK_NEAR(peak(x, 0.6, 1.7), level, level * 0.05);   /* the held note goes on at its level */
    /* An 880 Hz sine steps at most 2 pi 880 / 44100 = 0.125 of its peak; a cut can step 2x it. */
    CHECK(max_step(x, 0.45, 0.6) < level * 0.2);
    free(x);
    free(f.d);
}

/* Seeking after a change keeps the new soundfont, and a change on a stream not read yet
 * plays from the start with it. */
static void test_file_change_then_seek(void)
{
    song sg = { 0 };
    buf f;
    fa_open_options o;
    fa_stream* s = NULL;
    float* x = (float*)calloc((size_t)SECONDS * RATE * 2, sizeof(float));
    track t;
    note(&sg, 0, 1800, 69);
    f = finish(&sg);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = g_font;
    CHECK(fa_stream_open_memory(g_engine, f.d, f.n, &o, &s) == FA_OK);
    fa_stream_set_midi_effects(s, 0);
    CHECK(fa_stream_set_soundfont(s, g_octave) == FA_OK);
    read_frames(s, x, 0, RATE / 2);
    t = pitch_track(x, 0.05, 0.49, 440.0);
    CHECK_NEAR(t.lo, 1200.0, 5.0);
    CHECK_NEAR(t.hi, 1200.0, 5.0);
    CHECK(fa_stream_set_soundfont(s, g_font) == FA_OK);
    CHECK(fa_stream_seek(s, RATE) == FA_OK);   /* taken by the seek */
    memset(x, 0, (size_t)SECONDS * RATE * 2 * sizeof(float));
    read_frames(s, x, 0, RATE / 2);
    t = pitch_track(x, 0.05, 0.49, 440.0);
    CHECK_NEAR(t.lo, 0.0, 5.0);
    CHECK_NEAR(t.hi, 0.0, 5.0);
    fa_stream_close(s);
    free(x);
    free(f.d);
}

static fa_midi_event ev(int type, int channel, int param, int value)
{
    fa_midi_event e;
    memset(&e, 0, sizeof(e));
    e.frame = FA_MIDI_NOW;
    e.type = type;
    e.channel = channel;
    e.param = param;
    e.value = value;
    return e;
}

/* The real-time synth keeps its controllers (a pitch bend of +2 semitones) and plays the
 * next notes with the new soundfont; the note it held ends. */
static void test_realtime_change(void)
{
    fa_open_options o;
    fa_midi_stream_options mo;
    fa_stream* s = NULL;
    fa_midi_event e[2];
    float* x = (float*)calloc((size_t)SECONDS * RATE * 2, sizeof(float));
    track t;
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = g_font;
    fa_midi_stream_options_init(&mo);
    mo.sample_rate = RATE;
    CHECK(fa_stream_open_midi(g_engine, &mo, &o, &s) == FA_OK);
    fa_stream_set_midi_effects(s, 0);
    e[0] = ev(FA_MIDI_EVENT_PITCH_BEND, 0, 0, 16383);
    e[1] = ev(FA_MIDI_EVENT_NOTE_ON, 0, 69, 127);
    CHECK(fa_stream_send_midi_events(s, e, 2) == FA_OK);
    read_frames(s, x, 0, RATE / 2);
    CHECK(fa_stream_set_soundfont(s, g_octave) == FA_OK);
    read_frames(s, x, RATE / 2, RATE);
    e[0] = ev(FA_MIDI_EVENT_NOTE_ON, 0, 69, 127);
    CHECK(fa_stream_send_midi_events(s, e, 1) == FA_OK);
    read_frames(s, x, RATE, 2 * RATE);
    fa_stream_close(s);
    t = pitch_track(x, 0.1, 0.49, 440.0);
    CHECK_NEAR(t.lo, 200.0, 5.0);
    CHECK_NEAR(t.hi, 200.0, 5.0);
    CHECK(peak(x, 0.6, 0.99) < 1e-6);   /* the held note ended with the old synth */
    t = pitch_track(x, 1.1, 1.9, 440.0);
    CHECK_NEAR(t.lo, 1400.0, 5.0);
    CHECK_NEAR(t.hi, 1400.0, 5.0);
    free(x);
}

/* NULL: the engine's soundfont. Not a MIDI stream, or nothing to fall back on: errors. */
static void test_set_errors(void)
{
    song sg = { 0 };
    buf f;
    fa_open_options o;
    fa_stream* s = NULL;
    fa_soundfont* got = NULL;
    note(&sg, 0, 500, 69);
    f = finish(&sg);
    fa_open_options_init(&o);
    o.flags = FA_OPEN_DECODE_ONLY;
    o.soundfont = g_octave;
    CHECK(fa_stream_open_memory(g_engine, f.d, f.n, &o, &s) == FA_OK);
    CHECK(fa_stream_set_soundfont(NULL, g_font) == FA_ERROR_INVALID_ARG);
    CHECK(fa_stream_get_soundfont(s, NULL) == FA_ERROR_INVALID_ARG);
    CHECK(fa_engine_set_soundfont(g_engine, NULL) == FA_OK);
    CHECK(fa_stream_set_soundfont(s, NULL) == FA_ERROR_NO_SOUNDFONT);   /* the engine has NO_SYSTEM_SOUNDFONT */
    CHECK(fa_engine_set_soundfont(g_engine, g_font) == FA_OK);
    CHECK(fa_stream_set_soundfont(s, NULL) == FA_OK);
    CHECK(fa_engine_set_soundfont(g_engine, NULL) == FA_OK);
    CHECK(fa_stream_get_soundfont(s, &got) == FA_OK && got == g_font);   /* the stream keeps its own reference */
    fa_soundfont_close(got);
    fa_stream_close(s);
    free(f.d);
    {
        /* A WAV file: not MIDI. */
        buf w = { 0 }, c = { 0 }, riff = { 0 };
        int i;
        put(&riff, "WAVE", 4);
        put16le(&c, 1); put16le(&c, 1); put32le(&c, RATE); put32le(&c, RATE * 2); put16le(&c, 2); put16le(&c, 16);
        chunk(&riff, "fmt ", &c);
        c.n = 0;
        for (i = 0; i < 1000; i++) put16le(&c, 0);
        chunk(&riff, "data", &c);
        chunk(&w, "RIFF", &riff);
        CHECK(fa_stream_open_memory(g_engine, w.d, w.n, &o, &s) == FA_OK);
        CHECK(fa_stream_set_soundfont(s, g_font) == FA_ERROR_INVALID_ARG);
        CHECK(fa_stream_get_soundfont(s, &got) == FA_ERROR_INVALID_ARG && !got);
        fa_stream_close(s);
        free(w.d); free(c.d); free(riff.d);
    }
}

/* Changes while the stream plays on the device, from another thread than the decoder's,
 * then closing with a change not taken yet: no crash, no leak (the sanitizers check). */
static void test_change_while_playing(void)
{
    song sg = { 0 };
    buf f;
    fa_open_options o;
    fa_stream* s = NULL;
    int i;
    fa_device* dev = NULL;
    fa_frames pos = 0;
    ev3(&sg, 0, 0x90, 69, 127);
    ev3(&sg, 100, 0x90, 76, 127);
    ev3(&sg, 3000, 0x80, 69, 0);
    ev3(&sg, 3000, 0x80, 76, 0);
    f = finish(&sg);
    fa_open_options_init(&o);
    o.soundfont = g_font;
    CHECK(fa_device_open(g_engine, NULL, &dev) == FA_OK);
    CHECK(fa_stream_open_memory(g_engine, f.d, f.n, &o, &s) == FA_OK);
    CHECK(fa_stream_attach(s, dev, NULL) == FA_OK);
    CHECK(fa_device_start(dev) == FA_OK);
    CHECK(fa_stream_play(s) == FA_OK);
    /* Wait for the device to pull audio (a busy CI runner can take a while), so the changes
     * below happen while the stream plays. */
    for (i = 0; i < 1000 && (fa_stream_get_position(s, &pos) != FA_OK || pos <= 0); i++) sleep_ms(5);
    CHECK(pos > 0);
    for (i = 0; i < 40; i++) {
        CHECK(fa_stream_set_soundfont(s, (i & 1) ? g_font : g_octave) == FA_OK);
        sleep_ms(i % 3 == 0 ? 0 : 7);
    }
    CHECK(fa_stream_set_soundfont(s, g_octave) == FA_OK);
    fa_stream_close(s);
    fa_device_close(dev);
    free(f.d);
}

/* ---- 24-bit samples (sm24) ---------------------------------------------------------- */

enum { SM24_NONE, SM24_OK, SM24_WRONG_SIZE };

/* The sine instrument of midi_synth_test.h (preset 0, root key 69, looped), with the
 * samples given as 24-bit values (SINE_LEN of them, then 46 zeros), written as smpl (their
 * top 16 bits) and, by `mode`, an sm24 chunk (the low 8). `minor`: ifil 2.minor. */
static buf make_sf2_24(const int32_t* v, int minor, int mode)
{
    buf sf = { 0 }, riff = { 0 }, info = { 0 }, sdta = { 0 }, pdta = { 0 }, c = { 0 };
    int i, n = SINE_LEN + 46;
    put(&riff, "sfbk", 4);
    put(&info, "INFO", 4);
    c.n = 0; put16le(&c, 2); put16le(&c, (unsigned)minor); chunk(&info, "ifil", &c);
    c.n = 0; putstr(&c, "Sine 24", 8); chunk(&info, "INAM", &c);
    chunk(&riff, "LIST", &info);
    put(&sdta, "sdta", 4);
    c.n = 0;
    for (i = 0; i < n; i++) put16le(&c, (unsigned)(uint16_t)(int16_t)((i < SINE_LEN ? v[i] : 0) >> 8));
    chunk(&sdta, "smpl", &c);
    if (mode != SM24_NONE) {
        c.n = 0;
        for (i = 0; i < n; i++) put8(&c, (unsigned)((i < SINE_LEN ? v[i] : 0) & 255));
        if (mode == SM24_WRONG_SIZE) put16le(&c, 0);
        chunk(&sdta, "sm24", &c);
    }
    chunk(&riff, "LIST", &sdta);
    put(&pdta, "pdta", 4);
    c.n = 0;
    putstr(&c, "Sine", 20); put16le(&c, 0); put16le(&c, 0); put16le(&c, 0); put32le(&c, 0); put32le(&c, 0); put32le(&c, 0);
    putstr(&c, "EOP", 20); put16le(&c, 0); put16le(&c, 0); put16le(&c, 1); put32le(&c, 0); put32le(&c, 0); put32le(&c, 0);
    chunk(&pdta, "phdr", &c);
    c.n = 0; put16le(&c, 0); put16le(&c, 0); put16le(&c, 1); put16le(&c, 0);
    chunk(&pdta, "pbag", &c);
    c.n = 0; mods(&c, NULL, 0);
    chunk(&pdta, "pmod", &c);
    c.n = 0; gen(&c, 41, 0); gen(&c, 0, 0);
    chunk(&pdta, "pgen", &c);
    c.n = 0; putstr(&c, "SineInst", 20); put16le(&c, 0); putstr(&c, "EOI", 20); put16le(&c, 1);
    chunk(&pdta, "inst", &c);
    c.n = 0; put16le(&c, 0); put16le(&c, 0); put16le(&c, 3); put16le(&c, 0);
    chunk(&pdta, "ibag", &c);
    c.n = 0; mods(&c, NULL, 0);
    chunk(&pdta, "imod", &c);
    c.n = 0; gen(&c, 54, 1); gen(&c, 38, -3600); gen(&c, 53, 0); gen(&c, 0, 0);
    chunk(&pdta, "igen", &c);
    c.n = 0;
    putstr(&c, "Sine", 20); put32le(&c, 0); put32le(&c, SINE_LEN); put32le(&c, 0); put32le(&c, SINE_LEN);
    put32le(&c, RATE); put8(&c, 69); put8(&c, 0); put16le(&c, 0); put16le(&c, 1);
    putstr(&c, "EOS", 20); for (i = 0; i < 26; i++) put8(&c, 0);
    chunk(&pdta, "shdr", &c);
    chunk(&riff, "LIST", &pdta);
    chunk(&sf, "RIFF", &riff);
    free(riff.d); free(info.d); free(sdta.d); free(pdta.d); free(c.d);
    return sf;
}

/* One note of the font, 1 s, dry; NULL if the font does not load. */
static float* render_font(const int32_t* v, int minor, int mode, uint64_t* sample_memory)
{
    song sg = { 0 };
    buf f, sf = make_sf2_24(v, minor, mode);
    fa_soundfont* font = NULL;
    fa_soundfont_info info = { .struct_size = sizeof(info) };
    float* x = NULL;
    if (fa_soundfont_open_memory(sf.d, sf.n, &font) == FA_OK) {
        if (sample_memory && fa_soundfont_get_info(font, &info) == FA_OK) *sample_memory = info.sample_memory;
        note(&sg, 0, 1000, 69);
        f = finish(&sg);
        x = render_from(&f, font, 0);
        fa_soundfont_close(font);
        free(f.d);
    }
    free(sf.d);
    return x;
}

/* The low byte is heard: a sine that lives in the low 8 bits alone (the top 16 are 0)
 * plays with sm24 and is silence without it, with a version before 2.04 or an sm24 of the
 * wrong size (the spec says to ignore it then). Low bytes of 0 sound exactly as 16 bits. */
static void test_sm24(void)
{
    static int32_t low[SINE_LEN], full[SINE_LEN];
    uint64_t mem16 = 0, mem24 = 0, mem_old = 0;
    float *a, *b, *c, *d;
    int i;
    for (i = 0; i < SINE_LEN; i++) {
        double y = sin(2.0 * PI * 440.0 * i / RATE);
        low[i] = 127 + (int32_t)lrint(y * 120.0);                  /* 7 .. 247: the top 16 bits are 0 */
        full[i] = (int32_t)lrint(y * 32000.0) * 256;              /* 16-bit values, low bytes 0 */
    }
    a = render_font(low, 4, SM24_OK, &mem24);
    b = render_font(low, 4, SM24_NONE, &mem16);
    c = render_font(low, 1, SM24_OK, &mem_old);
    d = render_font(low, 4, SM24_WRONG_SIZE, NULL);
    CHECK(a && b && c && d);
    CHECK(rms(a, 0.1, 0.9) > 1e-6);
    CHECK(peak(b, 0.0, 1.5) == 0.0);
    CHECK(peak(c, 0.0, 1.5) == 0.0);
    CHECK(peak(d, 0.0, 1.5) == 0.0);
    CHECK(mem16 == (uint64_t)(SINE_LEN + 46) * 2);
    CHECK(mem24 == (uint64_t)(SINE_LEN + 46) * 3);
    CHECK(mem_old == mem16);
    {
        /* The sine of the low bytes, at the scale of the top 16 bits: 120/256 of a 16-bit step. */
        float* ref = render_font(full, 4, SM24_NONE, NULL);
        double r16;
        CHECK(ref);
        r16 = rms(ref, 0.1, 0.9);
        CHECK_NEAR(rms(a, 0.1, 0.9) / r16 / (sqrt(120.0 * 120.0 + 2 * 127.0 * 127.0) / (32000.0 * 256.0)), 1.0, 0.01);
        free(a); a = render_font(full, 4, SM24_OK, NULL);
        CHECK(a && memcmp(a, ref, (size_t)SECONDS * RATE * 2 * sizeof(float)) == 0);
        free(ref);
    }
    free(a); free(b); free(c); free(d);
}

int main(void)
{
    static const sfpreset up = { 0, 0, 12 };
    fa_engine_config cfg;
    buf sf = make_sf2(NULL, 0, NULL, 0), sf2 = make_sf2_presets(&up, 1, NULL, 0, NULL, 0);
    fa_engine_config_init(&cfg);
    cfg.flags = FA_ENGINE_NULL_OUTPUT | FA_ENGINE_NO_SYSTEM_SOUNDFONT;
    cfg.worker_threads = 1;
    if (fa_engine_create(&cfg, &g_engine) != FA_OK) { fprintf(stderr, "engine create failed\n"); return 1; }
    if (fa_soundfont_open_memory(sf.d, sf.n, &g_font) != FA_OK || fa_soundfont_open_memory(sf2.d, sf2.n, &g_octave) != FA_OK) {
        fprintf(stderr, "test soundfont rejected\n");
        return 1;
    }
    free(sf.d);
    free(sf2.d);
    RUN(test_file_change);
    RUN(test_file_change_then_seek);
    RUN(test_realtime_change);
    RUN(test_set_errors);
    RUN(test_change_while_playing);
    RUN(test_sm24);
    fa_soundfont_close(g_font);
    fa_soundfont_close(g_octave);
    fa_engine_destroy(g_engine);
    if (g_failures) { fprintf(stderr, "%d failure(s)\n", g_failures); return 1; }
    printf("all passed\n");
    return 0;
}
