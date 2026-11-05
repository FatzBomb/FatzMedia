# Archives

FatzAudio reads these archives without an engine and without unpacking what you do not ask for:

| File | Format (`fa_archive_get_format`) | Filter (`fa_archive_get_filter`) |
|---|---|---|
| `.zip` (ZIP64; stored, DEFLATE, Deflate64, bzip2, LZMA and xz entries) | `FA_ARCHIVE_ZIP` | `FA_ARCHIVE_FILTER_NONE` |
| `.tar` (ustar, GNU and pax headers, long names) | `FA_ARCHIVE_TAR` | `NONE` |
| `.tar.gz` / `.tgz`, `.tar.bz2` / `.tbz2`, `.tar.xz` / `.txz` | `FA_ARCHIVE_TAR` | `GZIP`, `BZIP2`, `XZ` |
| `.7z` (plain or LZMA-packed header; Copy, LZMA and LZMA2 folders) | `FA_ARCHIVE_7Z` | `NONE` |
| one file in a `.gz`, `.bz2` or `.xz` (`song.mp3.gz`, a `.cdg.xz`) | `FA_ARCHIVE_GZIP`, `BZIP2`, `XZ` | the same value |

The same code plays karaoke downloads (`song.mp3` + `song.cdg` in one archive) when you open them with
`fa_stream_open_path`, so there is one set of rules for which entry plays. The file's kind comes from its first
bytes, never from its name. RAR is recognised by `fa_archive_detect` / `Archive.Detect` but not read:
opening one gives `FA_ERROR_UNSUPPORTED_FORMAT`; unpack the audio and the `.cdg` yourself, open the audio with
`fa_stream_open_memory` and attach the graphics (see [CD+G](cdg.md)).

## How each kind is read

- **ZIP and plain tar** list from the directory or by hopping from header to header; a stored entry plays straight
  from the file. A compressed ZIP entry is unpacked on its own.
- **`.tar.gz`, `.tar.bz2`, `.tar.xz`** are one compressed stream, so the listing decodes it once from the start
  (about the speed of `gzip -d`, `bzip2 -d` or `xz -d` to nowhere), keeping the first 64 KB of each entry for the
  song/graphics pairing. If the decoded stream fits `memory_limit_mb` (default 256) it is kept, every entry is a
  slice of it, and entries play in place with nothing decoded twice. Past that limit the stream is **not** held:
  each entry you read is decoded again, starting at the nearest point that can be entered. That point is every 4 MB
  of a `.gz` (a saved DEFLATE state), the start of the nearest block of a `.bz2` (900 KB of input per block at most)
  and of an `.xz` made with several blocks (`xz -T0`, pixz), or the start for a one-block `.xz`.
  `fa_archive_entry.solid_offset` says how many bytes must be decoded before the entry starts (0 at one of those points).
  Reading entries from last to first costs no more than first to last.
- **`.7z`** lists from the header (read whole, and unpacked first when 7-Zip packed it, which it does by default), so
  no song is decoded to list it. A folder (7z's name for one solid run of bytes) of Copy, LZMA or LZMA2 is read; a
  stored (Copy) file plays straight from the archive. Entries of a folder with one file are decoded into the buffer
  you get, nothing kept. Entries of a folder with several files are decoded in order: the decoder stays where the last
  read ended, so opening the songs of a solid archive one after the other decodes the folder once in total, and a
  read of an entry behind the decoder (a replay, the song's `.cdg` just before it) restarts at the nearest LZMA2
  dictionary reset before it, or at the folder's start. A folder that fits `memory_limit_mb` decodes into one growing
  buffer that is also the dictionary, so nothing is copied twice; a bigger one decodes through a window of the
  dictionary's size. The first 64 KB of each entry is kept for pairing. `fa_archive_entry.solid_offset` is the bytes
  that must be decoded before the entry starts (the offset in the folder, less what a LZMA2 reset before it saves;
  7-Zip with several threads or `-ms=off` writes resets; a plain solid LZMA2 folder has none).
  Other methods (PPMd, BCJ and the other filters, BCJ2, Deflate, BZip2) are listed without `FA_ENTRY_READABLE`
  (the method is reported and `fa_archive_read` gives `FA_ERROR_UNSUPPORTED_FORMAT`); AES is listed
  encrypted (`FA_ERROR_PROTECTED_CONTENT`, an encrypted header is refused the same way). The checksum of each file
  is verified when it is read whole. Hostile sizes (more than 262,144 entries, a header over 256 MB, a folder asking for
  a dictionary over 1 GiB) are refused with `FA_ERROR_TOO_LARGE`. Names are UTF-8 with `/`; folders, links and
  "anti" items are skipped.
- **A single file in a `.gz`, `.bz2`, `.xz`** is one entry. Its name is the name stored in the gzip header, else the
  archive's file name without `.gz`/`.bz2`/`.xz`, else `data`. A `.gz` and an `.xz` give the size from the trailer or
  the index, so the open reads only the end of the file; a `.bz2` has no size, so it is decoded once (like a tar).
- **Faster where the file allows it.** A `.bz2` with several blocks and an `.xz` with several blocks are decoded on
  several cores (up to the number of cores), in order. A `.gz` cannot be, by its design. bzip2
  files from `pbzip2`/`lbzip2` (several streams in a row) are read whole.
- **Safety.** A damaged stream, a wrong checksum (gzip CRC-32 and size, bzip2 block and stream CRCs, the xz check the
  file uses), or a stream cut short end in `FA_ERROR_UNSUPPORTED_FORMAT`. A stream that would
  decode to more than 64 GB, an xz block (any, not only the first) asking for a dictionary over 1 GiB, or a tar with more than 262,144 entries is refused
  (`FA_ERROR_TOO_LARGE` / `FA_ERROR_OUT_OF_MEMORY` / `FA_ERROR_UNSUPPORTED_FORMAT`); nothing is allocated from a size before it is checked.
- Only regular files are listed; links, folders, devices and tar "global" records are skipped. Names are UTF-8 (a name that is not valid UTF-8 is read as CP437, as in ZIP), with `/`, and a leading `./`
  removed. Times are the tar header's (a pax `mtime` wins).
  Encrypted tar/gz/bz2/xz does not exist, so there is no `FA_ERROR_PROTECTED_CONTENT` for them.

## Opening the same archive again (the recently-unpacked cache)

An engine keeps, for 30 seconds after its last use (`fa_engine_config.archive_cache_seconds`, .NET
`AudioEngineOptions.ArchiveCacheSeconds`), two things from archives opened by `fa_stream_open_path` and the other
stream opens (not from `fa_archive_open_*`, which you hold yourself):

- the **archive** with its decoder where the last read ended (a solid 7z, a `.tar.gz`), so the next song of a
  playlist costs only its own decode and the listing is not read again;
- the last **compressed song** that opened as a stream, as its unpacked bytes, so replaying or previewing it is a copy.

Each is at most half of `memory_limit_mb`, so together they stay within it. The cache is keyed by the archive's file
identity (device, file id, last write time) plus its path, so a changed or replaced archive is never served from it;
archives opened from memory or callbacks are not cached. `FA_ENGINE_NO_ARCHIVE_CACHE` / `NoArchiveCache` turns it off.

A song and its `.cdg` that both need unpacking are unpacked at the same time on two threads.

## Which entry plays

Used by `fa_archive_pick`, and by every open, tags read and fingerprint of an archive without a named entry.

- Names compare with `/` and `\` as the same and ignore case (simple folding: Latin, Greek, Cyrillic, fullwidth).
- Ignored: folders, empty entries, `__MACOSX/`, `._*` files, `Thumbs.db`, `desktop.ini`.
- A name ending `.cdg` is graphics. Any other entry whose first 64 KB a decoder recognises is audio.
- A song's graphics are the `.cdg` in the same folder with the same name before the last dot (`song.cdg` for
  `song.mp3` and for `song.mp3.cdg`).

Without a named entry:

1. Audio that has a same-name `.cdg` comes first, in directory order. The first one plays with its graphics.
2. Otherwise the first audio entry plays. It gets the archive's `.cdg` only when the archive holds exactly one
   song and one `.cdg` (`Track01.mp3` + `SC8125-01.cdg`).
3. Only graphics: the first `.cdg` plays as silence for its length.

With a named entry (`fa_open_options.archive_entry`, `StreamOptions.ArchiveEntry`, `fa_archive_find`): the exact
name first, then ignoring case (two entries that differ only by case give `FA_ERROR_INVALID_ARG`, no match
`FA_ERROR_NOT_FOUND`). Its same-name `.cdg` is attached, else rule 2's pair. Naming a `.cdg` opens its same-name
audio with it, or the graphics alone.

## Reading

```c
fa_archive_options o; fa_archive_options_init(&o);
fa_archive* ar;
fa_archive_open_path("Songs.zip", &o, &ar);

uint32_t n = fa_archive_get_entry_count(ar);
for (uint32_t i = 0; i < n; i++) {
    fa_archive_entry e = { sizeof e };
    fa_archive_get_entry(ar, i, &e);        /* name, size, method, flags, times, CRC-32 */
}

uint32_t audio; int32_t cdg;
fa_archive_pick(ar, &audio, &cdg);           /* cdg is -1 when there are no graphics */

fa_stream* s;
fa_stream_open_archive(engine, ar, audio, NULL, &s);   /* plays the entry, with its graphics */
fa_archive_close(ar);                        /* the stream keeps the archive alive */
```

```csharp
using var archive = Archive.Open("Songs.zip");
foreach (var e in archive.Entries) Console.WriteLine($"{e.Name} {e.Size}");
var (audio, cdg) = archive.Pick();
using var song = engine.OpenArchiveEntry(archive, audio);
byte[] bytes = archive.ReadAllBytes(audio);
using Stream s = archive.OpenRead(audio);    // seekable
var tags = Tags.Read(archive, audio);
var print = AudioFingerprint.Compute(archive, audio);
```

Open from a path, memory (`fa_archive_open_memory`, or `_memory_ref` for no copy) or I/O callbacks (needs `seek`
and `length`, because the directory is at the end). An `fa_archive` may be used from several threads; calls wait
for each other.

## Memory, temp files and cancelling

A stored entry (a ZIP or plain-tar entry, or any entry of a `.tar.gz`/`.bz2`/`.xz` that was kept decoded in memory)
is read straight from where it sits. A compressed one is unpacked once, into memory up to
`memory_limit_mb` (default 256) and past that into a file in `fatzaudio-tmp` inside `temp_directory` if you gave
one, else `FA_ERROR_TOO_LARGE`. The CRC-32 is
checked as it unpacks (tar has none of its own: the stream's checksums cover it, checked when the whole stream has
been decoded once, which the listing does). `cancel` stops an unpack with
`FA_ERROR_CANCELLED`. Encrypted entries are listed with `FA_ENTRY_ENCRYPTED` and give `FA_ERROR_PROTECTED_CONTENT`
when read (no passwords). Multi-part archives are refused.

## One song, one key

The same song gives the same fingerprint, and so the same loudness-cache entry, whether it is loose, in a ZIP, in a tar,
alone in a `.gz`/`.bz2`/`.xz` or in a `.tar.*`: the key is made from the decoded audio, never from the container
(`fa_fingerprint_archive_entry` with a `NULL` entry name uses the pick). Tags are read from the entry the same way.

## Tags and fingerprints

`fa_tags_read_archive` / `fa_tags_read_archive_entry`, `fa_fingerprint_archive` and
`fa_fingerprint_archive_entry` take an entry like a file. `fa_tags_read_io` (.NET `Tags.Read(Stream)`) reads tags
from any seekable source. `fa_fingerprint_archive_entry` with a `NULL` entry name uses the pick.

## Behaviour changes from earlier versions

- A `.cdg` that does not match any song is no longer attached when the archive holds several songs.
- Named lookups ignore case and slash direction.
- An archive whose only audio is encrypted gives `FA_ERROR_PROTECTED_CONTENT`.
- Pairing probes the first 64 KB of each candidate.
- `fa_archive_get_format` returns `FA_ARCHIVE_TAR`/`GZIP`/`BZIP2`/`XZ` for files that gave `FA_ERROR_UNSUPPORTED_FORMAT`
  before; `fa_archive_get_filter` is new. ZIP entries made with bzip2 (method 12), LZMA (14) or xz (95) now open
  (`FA_METHOD_BZIP2`, `FA_METHOD_LZMA`, `FA_METHOD_LZMA2`).
