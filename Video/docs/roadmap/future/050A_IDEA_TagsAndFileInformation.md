# Stage 050A (planned) - Tags and file information

Owner decisions 2026-11-05. Starts after FatzAudio rc.4 (and its archive API, 056A).

- **Player:** `player.Tags` (`MediaTags`, null before a load; set at load, carried by `MediaLoaded`):
  Title, Artist, Album, AlbumArtist, Composer, Genre, Year, Track/TrackCount, Disc/DiscCount,
  Comment, Lyrics, Bpm, every raw tag. Internet-radio titles also update Title/Artist (and keep
  raising `StreamMetadataChanged`). MIDI/KAR karaoke metadata maps into the same object.
- **Read without playing:** `MediaInfo.ReadAsync(path, options, ct)`: file (size, dates, path),
  tags, audio details, video details, karaoke (CD+G, lyrics), loudness only when already known
  (tags or cache; never starts a scan), archive entries. An archive with one playable item reports
  that item's tags and details as the main result (size, dates and hash stay the archive's).
- **Sources:** audio from FatzAudio `Tags.Read(path|bytes)` (`NoPictures | NoInfo` at load, stream
  details from the already open stream); video from FFmpeg only; file-system details from .NET.
- **Hashes:** on request, one pass, any of XxHash3 (recommended with size), CRC-32, MD5, SHA-256;
  `System.IO.Hashing` approved (owner 2026-11-05). FatzAudio's audio-only fingerprint (with its
  version) once public.
- **Cover art:** a thumbnail (default 256 px long side, settable, 0 = none) made by us with FFmpeg on
  a background thread after load (`CoverArtReady`); `GetFullCoverArt()` lazy, kept until the media
  changes; `ReadFullCoverArtAsync(path)` uncached. Front cover first; picture types exposed.
