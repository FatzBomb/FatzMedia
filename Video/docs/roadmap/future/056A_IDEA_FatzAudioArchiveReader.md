# Stage 056A (planned) - Archives through FatzAudio's reader; rar dropped

Owner decisions 2026-11-05. After FatzAudio ships its public archive API (requested 2026-11-05).

- FatzAudio becomes the one archive reader for both (zip, 7z, tar, gz, bz2, xz; tar combinations
  `.tar.gz`/`.tgz`/`.tar.bz2`/`.tar.xz` to confirm). We hand it the archive path; it plays the song and
  its .cdg (stored entries with no copy, compressed ones unpacked once). Our pairing (which entry plays,
  which .cdg and background) runs on its listing; video entries, backgrounds and other files are read
  through its API for FFmpeg and us. Measured on rc.1: the loudness cache keys on the song's bytes and
  is shared across loose, zipped and memory opens.
- Behind Core's `IArchiveEntryInfo` (implemented in the FatzAudio package; Core does not reference
  FatzAudio until the 037A decision slice).
- **rar dropped entirely** (owner): `.rar` leaves the archive file types, SharpCompress is removed (its
  only remaining use), and a rar file gives "RAR archives are not supported; extract the files first."
  Apps that need rar extract it themselves (053A's stream source helps). Behaviour change, documented.
