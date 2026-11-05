#!/usr/bin/env python3
"""Builds the test archives of docs/features/archives.md ("Speed") and times them through fa_archive_bench.

Needs ffmpeg with libmp3lame (only to make the test MP3), gzip, bzip2, xz, tar, zip, and py7zr for the 7z files.
Usage: run.py path/to/fa_archive_bench [--work DIR] [--reps N] [--json out.json]
The song is 4 minutes of a sine mix with noise at 192 kbps (about 5.8 MB) plus a 1.7 MB .cdg with 12% non-empty packs;
the 10-song archives hold ten different songs of that size."""
import argparse, json, os, random, shutil, subprocess, sys, tempfile

def make_mp3(path, seed):
    f = 200 + 37 * seed
    expr = "aevalsrc=0.3*sin(2*PI*%d*t)+0.2*sin(2*PI*%d*t*(1+0.1*sin(2*PI*0.3*t)))+0.1*sin(2*PI*%d*t):s=44100:d=240" % (f, f * 2 + 3, f * 3 + 5)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", expr, "-filter_complex",
                    "[0]aeval=val(0)+0.05*(random(0)-0.5)|val(0)-0.05*(random(1)-0.5)", "-c:a", "libmp3lame", "-b:a", "192k", path], check=True)

def make_cdg(path, seed):
    rnd = random.Random(seed)
    packs = 1730000 // 24
    out = bytearray()
    for _ in range(packs):
        if rnd.random() < 0.12:
            out += bytes([0x09, 6] + [rnd.randrange(64) for _ in range(22)])
        else:
            out += bytes(24)
    open(path, "wb").write(out)

def sh(cmd, **kw):
    subprocess.run(cmd, check=True, **kw)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bench")
    ap.add_argument("--work")
    ap.add_argument("--reps", type=int, default=5)
    ap.add_argument("--json")
    a = ap.parse_args()
    import py7zr
    work = a.work or tempfile.mkdtemp(prefix="archive-bench-")
    os.makedirs(work, exist_ok=True)
    songs = os.path.join(work, "songs")
    os.makedirs(songs, exist_ok=True)
    for i in range(10):
        m, c = os.path.join(songs, "song%02d.mp3" % i), os.path.join(songs, "song%02d.cdg" % i)
        if not os.path.exists(m): make_mp3(m, i)
        if not os.path.exists(c): make_cdg(c, i)
    one = ["song00.mp3", "song00.cdg"]
    ten = sorted(os.listdir(songs))
    def path(n): return os.path.join(work, n)
    def z(name, extra, files):
        if os.path.exists(path(name)): os.remove(path(name))
        sh(["zip", "-q", "-j"] + extra + [path(name)] + [os.path.join(songs, f) for f in files])
    z("one-stored.zip", ["-0"], one); z("one-deflate.zip", ["-6"], one)
    for tag, files in (("one", one), ("ten", ten)):
        t = path(tag + ".tar")
        sh(["tar", "-cf", t, "-C", songs] + files)
        for ext, cmd in (("gz", ["gzip", "-6", "-k", "-f"]), ("bz2", ["bzip2", "-9", "-k", "-f"]), ("xz", ["xz", "-6", "-k", "-f"]),
                         ("xz-mt", ["xz", "-6", "-T2", "--block-size=2MiB", "-k", "-f"])):
            if ext == "xz-mt":
                sh(["xz", "-6", "-T2", "--block-size=2MiB", "-c", t], stdout=open(path(tag + ".mt.tar.xz"), "wb"))
            else:
                sh(cmd + [t])
    def sevenz(name, files):
        p = path(name)
        if os.path.exists(p): os.remove(p)
        filters = [{"id": py7zr.FILTER_LZMA2, "preset": 5}]
        with py7zr.SevenZipFile(p, "w", filters=filters) as z7:
            for f in files: z7.write(os.path.join(songs, f), f)
    sevenz("one-solid.7z", one); sevenz("ten-solid.7z", ten)
    results = []
    def run(label, arch, extra=()):
        out = subprocess.run([a.bench, path(arch), "--reps", str(a.reps)] + list(extra), check=True, capture_output=True, text=True).stdout
        r = json.loads(out); r["label"] = label; r["size_mb"] = round(os.path.getsize(path(arch)) / 1e6, 2)
        results.append(r)
        print("%-44s %8.2f ms  reopen %7.2f ms  +%.1f MB   (%s MB)" % (label, r["cold_ms"], r["reopen_ms"], r["added_kb"] / 1024, r["size_mb"]), flush=True)
    run("stored zip", "one-stored.zip"); run("DEFLATE zip", "one-deflate.zip")
    run("tar", "one.tar"); run("tar.gz", "one.tar.gz"); run("tar.bz2", "one.tar.bz2"); run("tar.xz (1 block)", "one.tar.xz")
    run("7z (LZMA2, one song)", "one-solid.7z")
    run("tar.gz, 10 songs, 1st", "ten.tar.gz", ["--entry", ten[0]]); run("tar.gz, 10 songs, 10th", "ten.tar.gz", ["--entry", ten[-1]])
    run("tar.bz2, 10 songs, 10th", "ten.tar.bz2", ["--entry", ten[-1]])
    run("tar.xz, 1 block, 10 songs, 10th", "ten.tar.xz", ["--entry", ten[-1]])
    run("tar.xz, blocks, 10 songs, 10th", "ten.mt.tar.xz", ["--entry", ten[-1]])
    run("7z solid, 10 songs, 1st", "ten-solid.7z", ["--entry", ten[0]])
    run("7z solid, 10 songs, 10th (cold)", "ten-solid.7z", ["--entry", ten[-1]])
    seq = subprocess.run([a.bench, path("ten-solid.7z"), "--sequence", ",".join(f for f in ten if f.endswith(".mp3"))],
                         check=True, capture_output=True, text=True).stdout
    print("7z solid, 10 songs in order (cache on):", json.loads(seq)["sequence_ms"])
    results.append({"label": "7z solid, 10 songs in order", **json.loads(seq)})
    if a.json: json.dump(results, open(a.json, "w"), indent=1)

main()
