#!/usr/bin/env python3
"""Writes THIRD_PARTY_LICENSES.txt: the full license text of every third-party
component built into FatzAudio, copied from its folder under third_party/.
BSD, MIT and zlib licenses ask for their notices to go with binary releases;
the NuGet package carries this file. Run it again after adding or updating a
component (CI does not check it)."""
import os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# (component, file under third_party/, marker): marker None = the whole file; otherwise
# the license comment inside a source file, from the marker to the end of the comment.
COMPONENTS = [
    ("miniaudio (with dr_wav, dr_flac, dr_mp3)", "miniaudio/LICENSE", None),
    ("libopus", "opus/COPYING", None),
    ("libvorbis", "vorbis/COPYING", None),
    ("libogg", "ogg/COPYING", None),
    ("TinySoundFont (tsf.h)", "tinysoundfont/LICENSE", None),
    ("TinyMidiLoader (tml.h)", "tinysoundfont/tml.h", "LICENSE (ZLIB)"),
    ("Signalsmith Stretch", "signalsmith/signalsmith-stretch/LICENSE.txt", None),
    ("Signalsmith Linear", "signalsmith/signalsmith-linear/LICENSE.txt", None),
    ("libopenmpt", "libopenmpt/LICENSE", None),
    ("libopenmpt (src/mpt, Boost Software License 1.0)", "libopenmpt/src/mpt/LICENSE.BSL-1.0.txt", None),
    ("Monkey's Audio SDK", "monkeysaudio/License.txt", None),
    ("libwavpack", "wavpack/COPYING", None),
    ("bzip2 (libbzip2)", "bzip2/LICENSE", None),
]

def text(path, marker):
    with open(os.path.join(ROOT, "third_party", path), encoding="utf-8", errors="replace") as f:
        data = f.read()
    if marker:  # a license comment inside a header: from the marker to the end of the comment
        start = data.index(marker)
        data = data[start:data.index("*/", start)]
        data = "\n".join(line[3:] if line.startswith("   ") else line for line in data.splitlines())
    return data.strip("\n") + "\n"

out = ["FatzAudio: licenses of the third-party components built into it",
       "",
       "FatzAudio's own code is proprietary (see LICENSE). The components below are",
       "statically linked into the FatzAudio library and keep their own licenses.",
       "Versions and what each is used for: THIRD_PARTY_NOTICES.md in the repository.",
       "The LZMA SDK (xz, LZMA) is also built in; it is in the public domain.",
       ""]
for name, path, marker in COMPONENTS:
    out += ["=" * 78, name, "=" * 78, "", text(path, marker)]
with open(os.path.join(ROOT, "THIRD_PARTY_LICENSES.txt"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(out))
