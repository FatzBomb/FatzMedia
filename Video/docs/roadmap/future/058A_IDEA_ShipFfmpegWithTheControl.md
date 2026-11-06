# Stage 058A (planned) - Ship FFmpeg with the control

Owner decision 2026-11-06: the control ships its FFmpeg libraries, as FatzAudio ships its own, so an
app always has the DLLs the control was built against and an update of the control can never leave
an app on the wrong FFmpeg. With BASS gone (037A), an app then needs no native set-up at all.
**LGPL only** (owner, 2026-11-06): no GPL build (it would make the control and every app using it
GPL) and no `--enable-nonfree` build (not redistributable). Replaces the host-owned FFmpeg part of
the 023A policy; `docs/NATIVE_DEPENDENCIES.md` is rewritten with it.

## Scope

1. **Runtime packages** `FatzBomb.MediaControl.Native.FFmpeg` for `win-x64` and `linux-x64`: the seven
   FFmpeg 9.0.x shared libraries under `runtimes/<rid>/native/`, copied to the output by the build
   (FatzAudio's model). The Windows and Linux platform packages reference them. First a pinned,
   repacked LGPL shared build with its archive URL, SHA-256 and configure line recorded (Linux: the
   BtbN LGPL build the sandbox uses; Windows: the 9.0.2 LGPL v3 build the owner's players use now);
   our own CI build later (reproducible, and can drop unused parts for size).
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

## Open for the owner before a paid release (not a blocker for this stage)

Codec patents (H.264, HEVC, AAC and others) are separate from FFmpeg's copyright licence and depend on
the country and how the product is sold: a short consult with an IP lawyer. Points to raise: hardware
decoding (D3D11VA, Vulkan Video) is the control's default; a build without some software decoders is
possible later; audio decoding moves to FatzAudio with 037A. Not legal advice.

## Queue

After the FatzAudio rc.4 work, unless moved.
