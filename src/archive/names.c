/* Names inside archives: UTF-8 checks, the DOS code page 437 old ZIP tools write,
 * simple Unicode case folding, and the stem/kind rules of the pairing (archive.h). */
#include "archive/archive.h"
#include <string.h>

/* ---- UTF-8 ------------------------------------------------------------------ */

/* Decodes the code point at *p and advances. A byte that does not start a valid sequence
 * is returned as itself (so two invalid names still compare by their bytes). */
uint32_t fa_utf8_next(const char** p)
{
    const uint8_t* s = (const uint8_t*)*p;
    uint32_t c = s[0];
    if (c < 0x80) { *p += c ? 1 : 0; return c; }
    if (c >= 0xC2 && c <= 0xDF && (s[1] & 0xC0) == 0x80) { *p += 2; return ((c & 0x1F) << 6) | (s[1] & 0x3F); }
    if (c >= 0xE0 && c <= 0xEF && (s[1] & 0xC0) == 0x80 && (s[2] & 0xC0) == 0x80) {
        uint32_t cp = ((c & 0x0F) << 12) | ((s[1] & 0x3F) << 6) | (s[2] & 0x3F);
        if (cp >= 0x800 && (cp < 0xD800 || cp > 0xDFFF)) { *p += 3; return cp; }
    }
    if (c >= 0xF0 && c <= 0xF4 && (s[1] & 0xC0) == 0x80 && (s[2] & 0xC0) == 0x80 && (s[3] & 0xC0) == 0x80) {
        uint32_t cp = ((c & 0x07) << 18) | ((s[1] & 0x3F) << 12) | ((s[2] & 0x3F) << 6) | (s[3] & 0x3F);
        if (cp >= 0x10000 && cp <= 0x10FFFF) { *p += 4; return cp; }
    }
    *p += 1;
    return c | 0x80000000u;   /* an invalid byte: out of Unicode's range */
}

int fa_name_is_utf8(const char* s)
{
    while (*s) if (fa_utf8_next(&s) & 0x80000000u) return 0;
    return 1;
}

static size_t put_utf8(char* out, size_t cap, size_t at, uint32_t cp)
{
    if (cp < 0x80) { if (at + 1 < cap) out[at] = (char)cp; return 1; }
    if (cp < 0x800) {
        if (at + 2 < cap) { out[at] = (char)(0xC0 | (cp >> 6)); out[at + 1] = (char)(0x80 | (cp & 0x3F)); }
        return 2;
    }
    if (at + 3 < cap) {
        out[at] = (char)(0xE0 | (cp >> 12));
        out[at + 1] = (char)(0x80 | ((cp >> 6) & 0x3F));
        out[at + 2] = (char)(0x80 | (cp & 0x3F));
    }
    return 3;
}

/* Code page 437 for the bytes 0x80..0xFF. */
static const uint16_t cp437[128] = {
    0x00C7, 0x00FC, 0x00E9, 0x00E2, 0x00E4, 0x00E0, 0x00E5, 0x00E7, 0x00EA, 0x00EB, 0x00E8, 0x00EF, 0x00EE, 0x00EC, 0x00C4, 0x00C5,
    0x00C9, 0x00E6, 0x00C6, 0x00F4, 0x00F6, 0x00F2, 0x00FB, 0x00F9, 0x00FF, 0x00D6, 0x00DC, 0x00A2, 0x00A3, 0x00A5, 0x20A7, 0x0192,
    0x00E1, 0x00ED, 0x00F3, 0x00FA, 0x00F1, 0x00D1, 0x00AA, 0x00BA, 0x00BF, 0x2310, 0x00AC, 0x00BD, 0x00BC, 0x00A1, 0x00AB, 0x00BB,
    0x2591, 0x2592, 0x2593, 0x2502, 0x2524, 0x2561, 0x2562, 0x2556, 0x2555, 0x2563, 0x2551, 0x2557, 0x255D, 0x255C, 0x255B, 0x2510,
    0x2514, 0x2534, 0x252C, 0x251C, 0x2500, 0x253C, 0x255E, 0x255F, 0x255A, 0x2554, 0x2569, 0x2566, 0x2560, 0x2550, 0x256C, 0x2567,
    0x2568, 0x2564, 0x2565, 0x2559, 0x2558, 0x2552, 0x2553, 0x256B, 0x256A, 0x2518, 0x250C, 0x2588, 0x2584, 0x258C, 0x2590, 0x2580,
    0x03B1, 0x00DF, 0x0393, 0x03C0, 0x03A3, 0x03C3, 0x00B5, 0x03C4, 0x03A6, 0x0398, 0x03A9, 0x03B4, 0x221E, 0x03C6, 0x03B5, 0x2229,
    0x2261, 0x00B1, 0x2265, 0x2264, 0x2320, 0x2321, 0x00F7, 0x2248, 0x00B0, 0x2219, 0x00B7, 0x221A, 0x207F, 0x00B2, 0x25A0, 0x00A0 };

/* A stored name as UTF-8, '/'-separated: UTF-8 stays; anything else is read as code
 * page 437 (what ZIP tools write without the UTF-8 flag). `\` becomes `/`, a leading
 * "./" or "/" goes. Returns a malloc'ed string. */
char* fa_name_to_utf8(const char* in, int force_utf8)
{
    int utf8 = force_utf8 || fa_name_is_utf8(in);
    size_t n = strlen(in), at = 0, i;
    char* out = (char*)malloc(n * 3 + 1);
    if (!out) return NULL;
    for (i = 0; i < n; i++) {
        uint8_t c = (uint8_t)in[i];
        if (utf8 || c < 0x80) out[at++] = c == '\\' ? '/' : (char)c;
        else at += put_utf8(out, n * 3 + 1, at, cp437[c - 0x80]);
    }
    out[at] = '\0';
    {
        char* p = out;
        for (;;) {
            if (p[0] == '/') p++;
            else if (p[0] == '.' && p[1] == '/') p += 2;
            else break;
        }
        if (p != out) memmove(out, p, strlen(p) + 1);
    }
    return out;
}

/* ---- Case folding ------------------------------------------------------------- */

/* Simple case folding (Unicode CaseFolding.txt, status C and S) for the scripts that
 * appear in song file names: Latin (basic, 1, Extended A, Additional), Greek, Cyrillic,
 * Armenian-free, fullwidth Latin. Everything else folds to itself. */
uint32_t fa_fold(uint32_t c)
{
    if (c < 0x80) return c >= 'A' && c <= 'Z' ? c + 32 : c;
    if (c >= 0x80000000u) return c;
    if (c >= 0xC0 && c <= 0xDE && c != 0xD7) return c + 32;
    if (c == 0xB5) return 0x3BC;
    if (c >= 0x100 && c <= 0x17F) {
        if (c == 0x130 || c == 0x131 || c == 0x138 || c == 0x149) return c;
        if (c == 0x178) return 0xFF;
        if (c == 0x17F) return 's';
        if (c <= 0x137 || (c >= 0x14A && c <= 0x177)) return (c & 1) ? c : c + 1;
        return (c & 1) ? c + 1 : c;   /* 0x139..0x148 and 0x179..0x17E: odd are the capitals */
    }
    if (c >= 0x1E00 && c <= 0x1EFF) {
        if ((c >= 0x1E96 && c <= 0x1E9F) || c == 0x1E9E) return c == 0x1E9E ? 0xDF : c;
        return (c & 1) ? c : c + 1;
    }
    if (c >= 0x391 && c <= 0x3AB && c != 0x3A2) return c + 32;
    if (c == 0x3C2) return 0x3C3;
    if (c == 0x386) return 0x3AC;
    if (c >= 0x388 && c <= 0x38A) return c + 37;
    if (c == 0x38C) return 0x3CC;
    if (c == 0x38E || c == 0x38F) return c + 63;
    if (c >= 0x400 && c <= 0x40F) return c + 80;
    if (c >= 0x410 && c <= 0x42F) return c + 32;
    if (c >= 0x460 && c <= 0x481) return (c & 1) ? c : c + 1;
    if (c >= 0x48A && c <= 0x4BF) return (c & 1) ? c : c + 1;
    if (c >= 0xFF21 && c <= 0xFF3A) return c + 32;
    return c;
}

/* 1 when the names are the same ignoring case (simple folding) and the kind of slash. */
int fa_name_equal_fold(const char* a, const char* b)
{
    for (;;) {
        uint32_t x = fa_utf8_next(&a), y = fa_utf8_next(&b);
        if (x == '\\') x = '/';
        if (y == '\\') y = '/';
        if (fa_fold(x) != fa_fold(y)) return 0;
        if (x == 0) return 1;
    }
}

/* ---- Kinds, stems, ignored entries ----------------------------------------- */

static int ext_is(const char* ext, const char* const* list)
{
    for (; *list; list++) if (fa_name_equal_fold(ext, *list)) return 1;
    return 0;
}

/* The text after the last '.' of the file part of `name` ("" when none). */
const char* fa_name_ext(const char* name)
{
    const char* base = strrchr(name, '/');
    const char* dot;
    base = base ? base + 1 : name;
    dot = strrchr(base, '.');
    return dot && dot != base ? dot + 1 : "";
}

const char* fa_name_base(const char* name)
{
    const char* base = strrchr(name, '/');
    return base ? base + 1 : name;
}

int32_t fa_name_kind(const char* name)
{
    static const char* const audio[] = { "mp3", "mp2", "mp1", "wav", "wave", "flac", "ogg", "oga", "opus", "m4a", "m4b", "aac", "adts",
        "aif", "aiff", "aifc", "caf", "wma", "ac3", "eac3", "ec3", "mid", "midi", "kar", "rmi", "mod", "xm", "s3m", "it", "mptm",
        "mo3", "umx", "mtm", "669", "amf", "ams", "dbm", "digi", "dmf", "dsm", "far", "j2b", "mdl", "med", "okt", "psm", "ptm", "stm",
        "ult", "dts", "mka", "weba", "mp4a", "ape", "wv", "tta", "au", "snd", NULL };
    static const char* const video[] = { "mp4", "m4v", "mkv", "webm", "avi", "mov", "wmv", "mpg", "mpeg", "flv", "3gp", "ogv", "vob", "ts", "m2ts", "mts", "m2t", "m2p", "mpe", "dvr-ms", NULL };
    static const char* const image[] = { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "ico", "heic", NULL };
    static const char* const text[] = { "txt", "nfo", "lrc", "srt", "ass", "ssa", "vtt", "md", "log", "diz", "html", "htm", "xml", "json", "csv", "cue", "m3u", "m3u8", "pls", NULL };
    const char* ext = fa_name_ext(name);
    if (fa_name_equal_fold(ext, "cdg")) return FA_ENTRY_CDG;
    if (ext_is(ext, audio)) return FA_ENTRY_AUDIO;
    if (ext_is(ext, video)) return FA_ENTRY_VIDEO;
    if (ext_is(ext, image)) return FA_ENTRY_IMAGE;
    if (ext_is(ext, text)) return FA_ENTRY_TEXT;
    return FA_ENTRY_OTHER;
}

/* Entries the pairing never looks at: Mac resource forks and Windows folder files. */
int fa_name_ignored(const char* name)
{
    const char* base = fa_name_base(name);
    if (strncmp(name, "__MACOSX/", 9) == 0 || strstr(name, "/__MACOSX/")) return 1;
    if (base[0] == '.' && base[1] == '_') return 1;
    return fa_name_equal_fold(base, "Thumbs.db") || fa_name_equal_fold(base, "desktop.ini") || fa_name_equal_fold(base, ".DS_Store");
}

/* 1 when graphics `cdg_name` is the .cdg of `audio_name`: the same folder and the same
 * name up to the last dot of the audio ("song.mp3" and "song.mp3.cdg" both pair with
 * "song.cdg"... the first by its stem, the second because the audio's whole name is the
 * .cdg's stem). */
int fa_name_same_stem(const char* audio_name, const char* cdg_name)
{
    size_t cn = strlen(cdg_name), an = strlen(audio_name), stem_end;
    const char* dot;
    const char* base;
    if (cn < 4) return 0;
    cn -= 4;   /* the cdg's name without ".cdg" */
    /* audio's own stem: before the last dot of its file part */
    base = fa_name_base(audio_name);
    dot = strrchr(base, '.');
    stem_end = dot && dot != base ? (size_t)(dot - audio_name) : an;
    if (stem_end == cn) {
        char a[512], c[512];
        if (cn >= sizeof(a)) return 0;
        memcpy(a, audio_name, stem_end); a[stem_end] = 0;
        memcpy(c, cdg_name, cn); c[cn] = 0;
        if (fa_name_equal_fold(a, c)) return 1;
    }
    /* "song.mp3.cdg": the cdg's stem is the audio's whole name. */
    if (an == cn) {
        char a[512], c[512];
        if (cn >= sizeof(a)) return 0;
        memcpy(a, audio_name, an); a[an] = 0;
        memcpy(c, cdg_name, cn); c[cn] = 0;
        return fa_name_equal_fold(a, c);
    }
    return 0;
}
