# Tags, cover art and file properties

FatzAudio reads the title, artist, album and the rest of a file's tags, its embedded pictures, and its properties (duration, bitrate, channels, sample rate, bits per sample, file and audio size) in one call. No engine or device is needed, so a media library can scan thousands of files with it. It replaces `BASS_ChannelGetTags` and the tag readers of the BASS add-ons, and also covers what the Tags add-on was used for: the app gets every field ready to show and formats it however it likes.

| Tag format | Files | Items | Pictures |
|---|---|---|---|
| ID3v2.2, 2.3, 2.4 | MP3, AAC (ADTS), AC-3, E-AC-3, FLAC (leading), WAV and AIFF (`id3 ` chunk) | Every text frame, `TXXX` (keyed by its description), `COMM`, `USLT`, `SYLT` (as timed lines, see below), URL frames; frames stored with the zlib flag are unpacked | `APIC`, `PIC` |
| ID3v1 / 1.1 | End of MP3, AAC, AC-3, FLAC, WAV, AIFF | Title, artist, album, year, comment, track, genre | - |
| Lyrics3 v1.00 and v2.00 | End of MP3 (before ID3v1 or APEv2, or after the APEv2 tag) | `LYR` (lyrics), `INF`, `AUT`, `EAL`, `EAR`, `ETT`, `IND`; v1.00's text is `LYR` | - |
| APEv2 | End of MP3, AAC, FLAC, WAV, AIFF (also with a header) | Every text item; lists joined with "; " | Binary `Cover Art (Front)` and the other cover items |
| Vorbis comments | FLAC, Ogg Vorbis, Opus, Ogg FLAC | Every comment, the encoder's vendor string as `VENDOR`; a FLAC `CUESHEET` block as `CUESHEET/...` items | FLAC `PICTURE` blocks, `METADATA_BLOCK_PICTURE` |
| MP4 / iTunes | M4A, MP4, M4B, MOV | Every `ilst` atom: `©nam`, `trkn`, `disk`, `gnre`, `tmpo`, freeform `----` atoms (keyed by their name) | `covr` |
| ASF | WMA, WMV | Content Description (Title, Author, Copyright, Description, Rating) and every extended attribute (`WM/AlbumTitle`, ...) of every type | `WM/Picture` |
| RIFF INFO | WAV, RF64, BW64 | `INAM`, `IART`, `IPRD`, `ICMT`, ... | - |
| AIFF text chunks | AIFF, AIFF-C | `NAME`, `AUTH`, `(c) `, `ANNO` | - |
| Matroska tags | MKV, MKA, WebM | Global and album-level `SimpleTag`s; the segment title as a fallback; tags aimed at one track, chapter, edition or attachment as `TRACK:<uid>/NAME` and so on | Image attachments (`cover.*` is the front cover) |
| MIDI | MID, KAR, RMI | Title, artist, copyright | - |
| CAF `info` chunk | CAF | `title`, `artist`, `album`, `composer`, `genre`, `recorded date`, `year`, `track number`, `comments`, `copyright`, `encoding application`, `tempo`, and any other key as an item (`FA_TAG_FORMAT_CAF`) | - |

A ZIP (MP3+G) is read through the audio file inside it: the one named like the `.cdg`, else the first audio file. Text in any encoding the format allows (Latin-1, UTF-16 with or without BOM, UTF-8) comes out as UTF-8; a file that claims UTF-8 but isn't is read as Latin-1, so nothing is lost. "Isn't UTF-8" is strict: overlong forms, UTF-16 surrogates and code points above U+10FFFF count as not UTF-8, the same check MIDI text, stream titles and HTTP headers use.

## Common fields

`fa_tags_get(tags, field)` (.NET: `tags.Title`, `tags.Artist`, ...) gives a field whichever format it came from. It never returns NULL: a field that isn't there is `""` in C and `null` in .NET.

| Field | .NET | Notes |
|---|---|---|
| `FA_TAG_TITLE` | `Title` | |
| `FA_TAG_ARTIST` | `Artist` | |
| `FA_TAG_ALBUM` | `Album` | |
| `FA_TAG_ALBUM_ARTIST` | `AlbumArtist` | |
| `FA_TAG_COMPOSER` | `Composer` | |
| `FA_TAG_GENRE` | `Genre` | ID3 numbers (`"(17)"`, `"17"`, `"(RX)"`) and the MP4 `gnre` atom are turned into names ("Rock", "Remix") |
| `FA_TAG_DATE` | `Date`, `Year` | As written: `"2024"`, `"2024-05-17"`. `Year` is its first four digits as a number |
| `FA_TAG_TRACK`, `FA_TAG_TRACK_TOTAL` | `Track`, `TrackTotal` | A `"3/12"` in the file is split into 3 and 12. .NET gives numbers |
| `FA_TAG_DISC`, `FA_TAG_DISC_TOTAL` | `Disc`, `DiscTotal` | Same as tracks |
| `FA_TAG_COMMENT` | `Comment` | |
| `FA_TAG_LYRICS` | `Lyrics` | Unsynchronised lyrics (`USLT`, `LYRICS`, `©lyr`, `WM/Lyrics`) |
| `FA_TAG_COPYRIGHT` | `Copyright` | |
| `FA_TAG_ENCODER` | `Encoder` | The software that made the file |
| `FA_TAG_BPM` | `Bpm` | |

When a file has several tag formats, the richest wins for each field: ID3v2 before APEv2 before ID3v1; in FLAC, the Vorbis comments before a leading ID3v2; in WAV and AIFF, ID3v2 before the native chunks. A field the winning format lacks is filled from the next one. A key that appears more than once in one format (two `ARTIST` comments) is joined with "; ". A joined field stops growing at 64 KB, and a file keeps at most 65,536 items and 1,024 pictures: only damaged or hostile files reach either limit.

Everything else, ReplayGain values and MusicBrainz IDs included, is in the item list exactly as the file stores it: `fa_tags_get_item` (.NET: `tags.Items`, or the indexer `tags["REPLAYGAIN_TRACK_GAIN"]`, which ignores case). The key is the format's own name: an ID3v2 frame ID (`TIT2`; a `TXXX` uses its description, a `COMM` with a description is `COMM:desc`), a Vorbis or APE key, an MP4 atom (`©nam` in UTF-8; freeform atoms by their name), an ASF attribute, a RIFF or AIFF chunk ID, or a Matroska tag name (`ALBUM/TITLE` for album-level tags when the file has track-level ones too).

## Timed lyrics, cue sheets and the rest

These come out as ordinary items (`fa_tags_get_item`, .NET `tags.Items` or `tags["KEY"]`), with the formats' own names, so nothing in the API changes.

- **`SYLT`** (ID3v2 synchronised lyrics or text) is one item per frame, keyed `SYLT` or `SYLT:description`. The value has one entry per line, `[mm:ss.mmm]text`: `[00:01.000]Hello`. A line break inside an entry's text is dropped, and one at its start or end shows as an empty line (the usual way a SYLT file marks a new verse line). Only frames with times in milliseconds are read; times in MPEG frames cannot be turned into time without the audio and the frame is skipped. The text is not offered to the lyric surface ([lyrics](lyrics.md)); your app can parse these lines itself.
- **ID3v2 compressed frames** (the zlib flag: v2.3 bit `0x80`, v2.4 bit `0x08` with the data length indicator) are unpacked and read like any other frame. Encrypted frames are skipped: the key is the app's, not the library's.
- **Lyrics3** at the end of an MP3 (`LYRICSBEGIN` ... `LYRICSEND`, v2.00's `LYRICS200`) is read in either order with APEv2, before the ID3v1 tag. `LYR` fills `FA_TAG_LYRICS`, `ETT`, `EAR` and `EAL` the title, artist and album, `INF` the comment, all after every other tag format. `audio_bytes` and the bitrate leave the tag out.
- **FLAC cue sheets** (`CUESHEET` block) come as `CUESHEET/CATALOG` and, for each track, `CUESHEET/TRACKnn` (where it starts, `mm:ss.mmm`, at its first index point 1) and `CUESHEET/TRACKnn/ISRC`. The lead-out track is left out. Only native FLAC files are read for them, not Ogg FLAC.
- **The Vorbis vendor string** (the encoder library, such as `libvorbis` or `Lavf`) is the item `VENDOR` of every Vorbis comment block: FLAC, Ogg Vorbis, Opus, Ogg FLAC.
- **Matroska tags aimed at something** (a `TagTrackUID`, `TagChapterUID`, `TagEditionUID` or `TagAttachmentUID` in their `Targets`) come out as `TRACK:<uid>/NAME`, `CHAPTER:<uid>/NAME`, `EDITION:<uid>/NAME` and `ATTACHMENT:<uid>/NAME`, with the UID as a decimal number. They never fill the common fields: they describe one track of a video, or are muxer statistics (`BPS`, `DURATION`).

## Properties

Unless `FA_TAGS_NO_INFO` is set, the reader also opens a decoder on the file (headers only, nothing is decoded) and keeps its `fa_stream_info`: `fa_tags_get_info` (.NET: `tags.Info`, plus the shortcuts `Duration`, `Bitrate`, `Channels`, `SampleRate`, `BitsPerSample`, `Codec`, `FileSize`, `AudioSize` and `HasCdg`). The fields are the same as `fa_stream_get_info` gives for a stream of that file; [streams.md](streams.md#stream-info) describes them. `audio_bytes` is the coded audio alone, without tags, cover art, headers or other tracks, which is what a bitrate needs.

`fa_tags_get_info` returns `FA_ERROR_NOT_FOUND` when the file is not audio any reader recognises. A file whose codec nobody decodes here (DTS, TrueHD, WMA off Windows, ... in Matroska, MP4, ASF, AVI or FLV, and a raw `.dts` file) still gives its details, read from the container: codec, container, rate, channels, duration, size and bitrate, with `can_decode` (.NET `Info.CanDecode`, `tags.CanDecode`) 0. Opening a stream on such a file fails as before, so an app can show the details and grey the file out. A bitrate is the container's own figure; a raw `.dts` file's length assumes frames of constant size, as DTS core frames are. TrueHD and MLP are only read inside a container, not as raw `.thd`/`.mlp` files. A WMA file protected by DRM still returns `FA_ERROR_PROTECTED_CONTENT`. A MIDI file is read without a soundfont, whole (the length comes from its events): codec `midi`, container `smf` or `rmid`, 44.1 kHz stereo, the duration, and the title, artist and copyright as `FA_TAG_FORMAT_MIDI` items. `FA_TAGS_NO_INFO` skips that, as it skips the decoder for other files.

## C

```c
#include <fatzaudio/fatzaudio.h>

fa_tags* tags = NULL;
if (fa_tags_read_path("C:/Music/track.flac", 0, &tags) != FA_OK) { /* missing file */ }

printf("%s - %s (%s, track %s)\n",
       fa_tags_get(tags, FA_TAG_ARTIST), fa_tags_get(tags, FA_TAG_TITLE),
       fa_tags_get(tags, FA_TAG_ALBUM), fa_tags_get(tags, FA_TAG_TRACK));

fa_stream_info info = { .struct_size = sizeof(info) };
if (fa_tags_get_info(tags, &info) == FA_OK)
    printf("%s, %u Hz, %u ch, %.1f s, %u kbps, %lld bytes\n", info.codec,
           info.format.sample_rate, info.format.channels, info.duration / 1e9,
           info.bitrate / 1000, (long long)info.file_size);

fa_tag_picture cover;
if (fa_tags_get_cover(tags, &cover) == FA_OK)
    save_image(cover.data, cover.size, cover.mime);   /* still encoded: JPEG, PNG, ... */

/* Every item, as stored. */
for (uint32_t i = 0; i < fa_tags_get_item_count(tags); i++) {
    fa_tag_item item;
    fa_tags_get_item(tags, i, &item);
    printf("%s = %s\n", item.key, item.value);
}

fa_tags_close(tags);    /* every string and picture above is freed here */
```

A library scan that only needs text and a duration:

```c
fa_tags_read_path(path, FA_TAGS_NO_PICTURES, &tags);    /* pictures are skipped unread */
```

Every picture, not only the cover: `fa_tags_get_picture_count` and `fa_tags_get_picture(tags, i, &pic)` list them in file order (none with `FA_TAGS_NO_PICTURES`; .NET `Tags.Pictures`).

A file you already hold in memory (downloaded, or from a database): `fa_tags_read_memory(data, size, flags, &tags)` reads it like `fa_tags_read_path`. The bytes are used only during the call, not copied (.NET `Tags.Read` from a byte span).

A stream that is already open:

```c
fa_tags* tags = NULL;
fa_stream_get_tags(song, 0, &tags);
```

## C#

```csharp
using FatzAudio;

var tags = Tags.Read(@"C:\Music\track.flac");
Console.WriteLine($"{tags.Artist} - {tags.Title} ({tags.Album}, track {tags.Track}/{tags.TrackTotal})");
Console.WriteLine($"{tags.Codec}, {tags.SampleRate} Hz, {tags.Channels} ch, {tags.Duration}, {tags.Bitrate / 1000} kbps");
Console.WriteLine($"{tags.FileSize} bytes, {tags.AudioSize} of audio, {tags.BitsPerSample} bits");

if (tags.Cover is { } cover)
    File.WriteAllBytes("cover" + (cover.MimeType == "image/png" ? ".png" : ".jpg"), cover.Data);

string? gain = tags["REPLAYGAIN_TRACK_GAIN"];
foreach (var item in tags.Items)
    Console.WriteLine($"{item.Format} {item.Key} = {item.Value}");

// Faster scans (a library scan should always pass NoPictures: the default copies every
// embedded picture into a new array, which lands on the large object heap):
var quick = Tags.Read(path, TagReadOptions.NoPictures);
var textOnly = Tags.Read(path, TagReadOptions.NoPictures | TagReadOptions.NoInfo);

// From memory, or from an open stream:
var fromBytes = Tags.Read(File.ReadAllBytes(path));
var fromStream = song.GetTags();
```

`Tags` is a plain managed object: everything is copied out of the library when it is read, so there is nothing to dispose, and it can be kept, cached or passed between threads.

## Options

| C flag | .NET | Effect |
|---|---|---|
| `FA_TAGS_NO_PICTURES` | `TagReadOptions.NoPictures` | Picture data is skipped without being read: no memory for cover art, faster on files with large covers |
| `FA_TAGS_NO_INFO` | `TagReadOptions.NoInfo` | Only the tags are read; no decoder is opened, and `fa_tags_get_info` returns `FA_ERROR_NOT_FOUND` |

## Streams

`fa_stream_get_tags` (.NET: `AudioStream.GetTags()`) reads the tags of the file a stream plays, opened from a path or from memory; the properties are the stream's own `fa_stream_info`. A MIDI stream also gets the title, artist and copyright of `fa_stream_get_midi_info` as `FA_TAG_FORMAT_MIDI` items (karaoke `@T` lines included). Streams of the app's own I/O callbacks, CD tracks and push streams give empty tags. Web streams have no file tags: their station name and song titles come from `fa_stream_get_metadata` and the metadata event ([web-streams.md](web-streams.md)).

## Audio fingerprint

`fa_fingerprint_path` / `fa_fingerprint_memory` (.NET `AudioFingerprint.Compute(path)` / `Compute(bytes)`) give a key for a file's audio that leaves out its tags and its name: the key the [loudness cache](normalization.md#the-cache) files songs by. A file that is renamed, moved, copied or retagged keeps its fingerprint; audio that is encoded again or edited gets a new one. A library can use it to tell "same song, new tags" (keep its play count, its cached analysis) from "the audio changed", and a tag editor's writes don't make it look like a new song. No engine is needed and no audio is decoded: most files cost a few 4 KB reads.

```c
fa_fingerprint fp = { .struct_size = sizeof(fp) };
if (fa_fingerprint_path("song.mp3", &fp) == FA_OK)
    printf("%016llx-%lld\n", (unsigned long long)fp.key, (long long)fp.audio_bytes);
```

```csharp
var before = AudioFingerprint.Compute(path);          // or Compute(bytes) for an entry your app unpacked
// ... the user edits the tags ...
bool sameAudio = AudioFingerprint.Compute(path) == before;
string stored = before.ToString();                    // "00ab12...ef-4123456": key in hex, then the audio bytes
```

For one entry of an archive, without unpacking the others, use `fa_fingerprint_archive_entry(archive_path, entry_name, &fp)` (.NET `AudioFingerprint.Compute(archivePath, entryName)`). The name is the entry's path as stored, `/`-separated, matched exactly. It gives the same fingerprint as the entry's bytes, so a song is recognised whether it is loose, in memory or in an archive. ZIP for now; other archive types join as FatzAudio learns to read them.

```csharp
var inZip = AudioFingerprint.Compute("pack.zip", "Disc 1/Song.mp3");   // == AudioFingerprint.Compute(songBytes)
```

Two fingerprints are equal when `key` and `audio_bytes` both are (the .NET value type compares both). A ZIP gives the fingerprint of its audio entry (chosen as an open chooses it), the same as that entry's bytes; compressed entries are unpacked in memory for it, up to 256 MB. What is read for each format is in [the cache's table](normalization.md#the-cache). Two limits:

- **WAV and AIFF over 64 MB** are sampled at 64 points plus the file's last-write time (reading 600 MB on every lookup would be too slow). For those, any write to the file, a tag edit included, gives a new fingerprint, and the same bytes in memory give another one than the file. Smaller WAV and AIFF and every coded format are content only.
- **MIDI**: the loudness cache adds the soundfont and rate to a MIDI file's key; the public fingerprint is the file's own.

Errors: `FA_ERROR_NOT_FOUND` (missing file or archive entry), `FA_ERROR_UNSUPPORTED_FORMAT` (an empty file, a ZIP without a readable audio entry), `FA_ERROR_IO` (a read failed), `FA_ERROR_TOO_LARGE` (a compressed archive entry over 256 MB). Any other file gets a fingerprint, audio or not: the format is not checked.

## Threads

Reading is synchronous and does file I/O on the calling thread, so a media library should scan on a worker thread. A tag object is immutable after it is read: any thread may read it, and its strings and pictures stay valid until `fa_tags_close`.

## Pitfalls

- An empty result is not an error. A file without tags, or of a format that has none (raw AAC without ID3, a bare WAV), gives `FA_OK` and an empty tag object.
- Fields are text as written. A date may be a year or a full date, a track may have leading zeros, and a genre may be any text. .NET's `Year`, `Track`, `Disc` and `Bpm` give `null` when the text isn't a number.
- MP4 atom keys start with the copyright sign, which is two bytes in UTF-8 (`"\xC2\xA9nam"`). Use `fa_tags_get` for the common fields instead of matching keys.
- Encrypted ID3v2 frames are skipped. `SYLT` frames with times in MPEG frames are skipped.
- Bitrate and `audio_bytes` for MP3, AAC, AC-3 and FLAC are measured between the tags (an MP3's Xing/LAME header frame is left out). MP4, WAV, AIFF and CD are exact.
- FatzAudio reads tags; the one thing it writes is loudness (ReplayGain / R128), and only when you call `fa_tags_write_loudness` ([writing loudness tags](normalization.md#writing-loudness-tags)). Edit other tags with a tag library.
- The BASS `TAGS_Read` format strings (`%ARTI - %TITL`) have no equivalent: build the text from the fields.
