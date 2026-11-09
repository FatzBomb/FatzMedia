# Stage 058A (planned) - Ship FFmpeg with the control

Owner decision 2026-11-06: the control ships its FFmpeg libraries, as FatzAudio ships its own, so an
app always has the DLLs the control was built against and an update of the control can never leave
an app on the wrong FFmpeg. With BASS gone (037A), an app then needs no native set-up at all.
**LGPL only** (owner, 2026-11-06): no GPL build (it would make the control and every app using it
GPL) and no `--enable-nonfree` build (not redistributable). Replaces the host-owned FFmpeg part of
the 023A policy; `docs/NATIVE_DEPENDENCIES.md` is rewritten with it.

## Scope

1. **Runtime packages** `FatzMedia.Video.Native.FFmpeg`, first for `win-x64` and `linux-x64`: the seven
   FFmpeg 9.0.x shared libraries under `runtimes/<rid>/native/`, copied to the output by the build
   (FatzAudio's model). The Windows and Linux platform packages reference them. First a pinned,
   repacked LGPL shared build with its archive URL, SHA-256 and configure line recorded (Linux: the
   BtbN LGPL build the sandbox uses; Windows: the 9.0.2 LGPL v3 build the owner's players use now);
   our own CI build later (reproducible, and can drop unused parts for size). Planned for every target
   FatzAudio ships (2026-11-06: win-arm64, linux-arm64, `osx` universal arm64+x64, android, iOS as an
   xcframework), each added as that platform becomes real (ARM: 059A; macOS: 026A/026B), so audio and
   video arrive together per platform.
2. **Licence compliance in the package:** the LGPL text matching the build, a third-party notices file
   copied into the app's output (FFmpeg credit, and the libraries the build links), and the exact
   source tarball plus build configuration for each pinned version published or linked from the
   release. The control's own licence must not forbid the reverse engineering the LGPL allows for
   debugging changes to the FFmpeg libraries.
3. **Build check** (CI and a boundary script): the configure line of every packaged build has no
   `--enable-gpl` and no `--enable-nonfree`, and the version matches the binding (9.0.x).
4. **Search order:** the packaged libraries load by default; native search paths become an override
   for an app that ships its own LGPL build (ENG-055, the missing-folder exception, is folded in here:
   with paths rarely passed it matters little; decide then).
5. **Docs and test players:** the native-dependency pages (repo and site) say no FFmpeg set-up is
   needed; the test players stop needing FFmpeg in `native\`; `scripts/setup-linux-native.sh` keeps
   only test media.

## The builds in use today (checked 2026-11-06)

| | Windows (owner's players) | Linux (sandbox, `scripts/setup-linux-native.sh`) |
|---|---|---|
| Source | NuGet `TqkLibrary.FFmpeg.Lgpl3.Native.Win.x64` 9.0.2 (tqk2811/FFmpegBuild, BtbN-style ffbuild) | BtbN FFmpeg-Builds `latest` (not pinned) |
| FFmpeg inside | `n9.0-2-g03d9533176`, built 2026-08-09: 9.0 plus two commits, **not** the 9.0.2 release the package number suggests | `n9.0.2-45-g1e65c6cd5d`, built 2026-11-03: 9.0.2 plus 45 release-branch commits |
| Licence | LGPL v3 (`--enable-version3`; no `--enable-gpl`, no `--enable-nonfree`) | the same |
| TLS | Schannel (Windows store) | OpenSSL |
| Size | 7 DLLs, 127 MB (57 MB package) | 7 shared libraries |

Both pass the licence rule. Points for the stage:
- **Pin one exact FFmpeg version for every platform**: today Windows and Linux run different 9.0 snapshots,
  and the Linux script downloads whatever `latest` is.
- **Trim the build for playback**: both builds carry encoders and libraries a player never uses (x264/x265
  are off, but SVT-AV1, VVenC, rav1e, kvazaar, libaom's encoder, OpenH264, LAME, TwoLAME, VMAF, ZeroMQ,
  chromaprint, OpenCL, ...). A decode-only LGPL build is much smaller and has fewer third-party licences to
  list. **OpenH264 compiled from source is not covered by Cisco's patent licence** (that covers only
  Cisco's own binaries), so it should be left out; H.264 decoding uses FFmpeg's own decoder or the GPU.
- Our own CI build (step 1's "later") is the way to get both.

## Owner decisions (2026-11-06)

- **The control is sold under a proprietary licence** (closed source). The LGPL FFmpeg build loaded as
  separate DLLs allows that; the obligations are scope item 2. BASS needs a commercial licence for as long
  as it is in the control (until 037A removes it). The managed packages (FFmpeg.AutoGen, ManagedBass,
  Silk.NET, SkiaSharp, Vortice, Avalonia, SharpCompress) are MIT: notices only.
- **A trimmed, decode-only LGPL build:** keep every decoder, demuxer, parser, protocol, TLS, libxml2 (DASH),
  dav1d, the hardware decoders (D3D11VA, D3D12VA, Vulkan, VAAPI) and libraries that only add reading
  (tracker and game music, teletext and ARIB subtitles, JPEG XL, SVG). Drop encoders, muxers, encode-only
  libraries (incl. OpenH264), NVENC/AMF/QSV encode and VMAF, ZeroMQ, chromaprint. The control never
  encodes or muxes with FFmpeg (checked 2026-11-06; the fallback's WAV is written by our code) and uses
  no FFmpeg filters. Acceptance: the Linux format matrix, network, subtitle and HDR tests and the owner's
  backlog format test pass on the trimmed build; anything that stops playing goes back in.
- **One pinned FFmpeg version for every platform.**

## Open for the owner before a paid release (not a blocker for this stage)

Codec patents (H.264, HEVC, AAC and others) are separate from FFmpeg's copyright licence and depend on
the country and how the product is sold: a short consult with an IP lawyer. Points to raise: hardware
decoding (D3D11VA, Vulkan Video) is the control's default; a build without some software decoders is
possible later; audio decoding moves to FatzAudio with 037A. Not legal advice.

## Queue

After the FatzAudio rc.4 work, unless moved.
