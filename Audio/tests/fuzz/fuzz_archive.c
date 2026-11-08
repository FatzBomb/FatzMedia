/* Fuzz target: the archive layer over ZIP (all methods), tar, and gzip, bzip2 and xz around a tar or one
 * file. Opens the bytes with a small memory limit, so a stream larger than 1 MB is listed once and read
 * again per entry; reads every entry it lists (up to 8), through a reader with seeks, and asks for the pick. */
#include "cdg.h"
#include "archive/archive.h"
#include <stdlib.h>

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    fa_archive* a = NULL;
    fa_archive_options o;
    uint32_t n, i, audio;
    int32_t cdg;
    (void)fa_archive_detect(data, size);
    fa_archive_options_init(&o);
    o.memory_limit_mb = 1;
    if (fa_archive_open_memory(data, size, &o, &a) != FA_OK) return 0;
    n = fa_archive_get_entry_count(a);
    (void)fa_archive_pick(a, &audio, &cdg);
    for (i = 0; i < n && i < 8; i++) {
        fa_archive_entry e = { .struct_size = sizeof(e) };
        fa_archive_reader* r = NULL;
        void* d = NULL;
        size_t got = 0;
        uint8_t buf[512];
        (void)fa_archive_get_entry(a, i, &e);
        if (fa_archive_read(a, i, &d, &got) == FA_OK) fa_free(d);
        if (fa_archive_open_reader(a, i, &r) == FA_OK) {
            (void)fa_archive_reader_read(r, buf, sizeof(buf));
            if (fa_archive_reader_seek(r, fa_archive_reader_get_size(r) / 2) == FA_OK) (void)fa_archive_reader_read(r, buf, sizeof(buf));
            fa_archive_reader_close(r);
        }
    }
    fa_archive_close(a);
    return 0;
}
