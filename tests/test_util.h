/* Shared by the C tests: result counting, the CHECK macros, the RUN filter (FA_TEST=name runs
 * one test), a sleep shim and loaders for the fixture files under FA_TEST_DATA_DIR. Include it
 * once per test program, after the project headers. */
#ifndef FA_TEST_UTIL_H
#define FA_TEST_UTIL_H
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <direct.h>
#else
#include <unistd.h>
#endif

/* Tests that scan loudness give their engine a cache folder of their own (cfg.cache_directory) and
 * clear it before and after, so a run never sees an earlier run's results and never touches the
 * real per-user cache. */
static inline void test_clear_cache_dir(const char* dir)
{
    char path[512];
    snprintf(path, sizeof(path), "%s/loudness.fac", dir);
    remove(path);
    snprintf(path, sizeof(path), "%s/loudness.fac.lock", dir);
    remove(path);
#ifdef _WIN32
    _rmdir(dir);
#else
    rmdir(dir);
#endif
}

static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); g_failures++; return; } } while (0)
#define CHECK_EQ(a, b) do { long long va_ = (long long)(a), vb_ = (long long)(b); if (va_ != vb_) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (%lld vs %lld)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_NEAR(a, b, tol) do { double va_ = (a), vb_ = (b); if (!(fabs(va_ - vb_) <= (tol))) { \
    fprintf(stderr, "  FAIL %s:%d: %s ~ %s (%.6f vs %.6f)\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define CHECK_STR(a, b) do { const char* va_ = (a); const char* vb_ = (b); if (strcmp(va_, vb_) != 0) { \
    fprintf(stderr, "  FAIL %s:%d: %s == %s (\"%s\" vs \"%s\")\n", __FILE__, __LINE__, #a, #b, va_, vb_); g_failures++; return; } } while (0)
#define RUN(t) do { const char* only_ = getenv("FA_TEST"); int before_ = g_failures; \
    if (only_ && strcmp(only_, #t) != 0) break; \
    printf("%s\n", #t); fflush(stdout); t(); if (g_failures == before_) printf("  ok\n"); } while (0)

/* Reads a whole file into a malloc'd buffer (one spare byte); NULL if it cannot be read. */
static inline uint8_t* test_read_file(const char* path, size_t* size)
{
    FILE* f = fopen(path, "rb");
    uint8_t* p;
    long n;
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    n = ftell(f);
    fseek(f, 0, SEEK_SET);
    p = (uint8_t*)malloc((size_t)n + 1);
    if (p && fread(p, 1, (size_t)n, f) != (size_t)n) { free(p); p = NULL; }
    fclose(f);
    *size = (size_t)n;
    return p;
}

#endif
