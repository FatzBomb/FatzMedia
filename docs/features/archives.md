# Archives

FatzAudio reads ZIP archives (ZIP64, stored, DEFLATE and Deflate64 entries) without an engine and without
unpacking what you do not ask for. The same code plays karaoke downloads (`song.mp3` + `song.cdg` in one `.zip`)
when you open them with `fa_stream_open_path`, so there is one set of rules for which entry plays.

Other archive types (7z, tar, gzip, bzip2, xz) are recognised by `fa_archive_detect` / `Archive.Detect` but not
read yet; opening one gives `FA_ERROR_UNSUPPORTED_FORMAT`. RAR is out of scope. For any format FatzAudio does not
read, unpack the audio and the `.cdg` yourself, open the audio with `fa_stream_open_memory` and attach the
graphics (see [CD+G](cdg.md)).

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

A stored entry is read straight from the archive. A compressed one is unpacked once, into memory up to
`memory_limit_mb` (default 256) and past that into a file in `fatzaudio-tmp` inside `temp_directory` if you gave
one, else `FA_ERROR_TOO_LARGE`. The CRC-32 is checked as it unpacks. `cancel` stops an unpack with
`FA_ERROR_CANCELLED`. Encrypted entries are listed with `FA_ENTRY_ENCRYPTED` and give `FA_ERROR_PROTECTED_CONTENT`
when read (no passwords). Multi-part archives are refused.

## Tags and fingerprints

`fa_tags_read_archive` / `fa_tags_read_archive_entry`, `fa_fingerprint_archive` and
`fa_fingerprint_archive_entry` take an entry like a file. `fa_tags_read_io` (.NET `Tags.Read(Stream)`) reads tags
from any seekable source. `fa_fingerprint_archive_entry` with a `NULL` entry name uses the pick.

## Behaviour changes from earlier versions

- A `.cdg` that does not match any song is no longer attached when the archive holds several songs.
- Named lookups ignore case and slash direction.
- An archive whose only audio is encrypted gives `FA_ERROR_PROTECTED_CONTENT`.
- Pairing probes the first 64 KB of each candidate.
