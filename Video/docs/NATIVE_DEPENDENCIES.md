# Native Dependency Distribution Policy

Updated: 2026-11-10

The repository tracks no native DLLs or executables. The Gyan GPL-enabled `ffmpeg.exe`, `ffplay.exe`,
and `ffprobe.exe` fixtures were removed on 2026-10-17 (owner decision): the app never runs
them, and it probes and decodes media in-process through the FFmpeg DLLs.

BASS was removed in 037A (2026-11): audio is FatzAudio, built from `Audio\` in this repo, so neither
hosts nor end users need BASS DLLs or a BASS licence.

## Responsibility

`FatzMedia.Video` packages do not contain FFmpeg native binaries. The host developer is responsible for
obtaining, licensing, validating, deploying, and updating the FFmpeg DLLs used by their application.
This is the same model used by `FatzMedia.Video.Sample.Player`. The audio engine's native library
(`fatzaudio.dll`, `libfatzaudio.so`) is ours: it comes from Audio's build and is copied to the output by
the `FatzMedia.Audio` project (see "Audio engine (FatzAudio)").

The host may either:

1. Put the FFmpeg DLLs in an existing predefined directory beneath the application base:
   - `native`
   - `runtimes/win-x64/native`
2. Pass one or more custom directories, in priority order, to the WPF `Player` or
   Core `PlayerCore` constructor.

Caller-supplied directories are searched first. Existing predefined directories are
appended as fallbacks. Searches are recursive, so FFmpeg may sit in a subdirectory beneath a
supplied root. Paths passed through `PlayerCore` must exist; a missing directory produces
`DirectoryNotFoundException` before native initialization.

```csharp
var platform = new WindowsDirect3D11PlatformFactory();
IReadOnlyList<string> nativePaths =
[
    @"C:\Program Files\MyHost\media-native\ffmpeg"
];

var player = await FatzMedia.Video.Wpf.Player.CreateAsync(
    configuration,
    nativePaths,
    platform);
```

To use only existing predefined paths:

```csharp
IReadOnlyList<string> nativePaths = WindowsNativeLibraryPaths.GetDefaultSearchPaths();
```

Passing an empty list also allows the Windows loader to probe the predefined paths.

## Supported Native Target

| RID | Status | Notes |
|---|---|---|
| `win-x64` | Supported | Current Windows, Direct3D11, WPF, Player, and native smoke-test target. |
| `win-arm64` | Not supported | Requires dedicated FFmpeg and FatzAudio validation and package targeting. |
| `linux-x64` | Preview (Stage 026B) | `FatzMedia.Video.Linux`; FFmpeg load, software decode and FatzAudio validated in CI, player smoke pending. See Linux Native Target. |
| `osx-*`, `android-*`, other `linux-*` | Not supported | Require platform packages and native policies of their own. |

The managed Windows projects retain `net10.0-windows7.0` API compatibility, but the
approved native runtime baseline is Windows 10/11 x64. A native distributor may have
stricter OS or UCRT requirements than the managed TFM.

## Linux Native Target (Stage 026B, pending manual smoke)

`FatzMedia.Video.Linux` follows the same host-owned policy: no binaries ship in
the package. The host supplies the FFmpeg shared libraries in a host path (absolute or relative to
the app), `native/`, or `runtimes/linux-<arch>/native/`. System library directories are
not searched implicitly.

| Kind | Required | Optional |
|---|---|---|
| FFmpeg (avcodec 63 ABI, FFmpeg 9.0.x) | `libavutil.so.61`, `libswresample.so.7`, `libswscale.so.10`, `libavcodec.so.63`, `libavformat.so.63` | `libavfilter.so.12`, `libavdevice.so.63` |

FFmpeg libraries must sit in one directory; they are preloaded in dependency order
because typical builds reference each other by soname without an `$ORIGIN` run path.
The same FFmpeg licensing cautions as Windows apply (LGPL vs GPL build choice).

## Audio engine (FatzAudio)

The player's audio engine is FatzAudio: audio files and archive entries, MIDI/KAR with lyrics, trackers,
CD+G, video soundtracks and network audio (which opens through FFmpeg). It is the default
(`MediaPlayerConfiguration.AudioEngineFactory` null); a host may pass its own `IAudioEngineFactory`.

- **Project:** `FatzMedia.Audio`, the .NET binding in `Audio\bindings\dotnet` of this repo, referenced
  by project from `FatzMedia.Video` (no package, feed or token). Its native library (`fatzaudio.dll`,
  `libfatzaudio.so`) comes from Audio's CMake build and is copied to the output by the build; nothing
  goes in `native/`. The binding refuses a native library of another major.minor version
  (`InvalidOperationException` at the first call); the player then starts without audio (below).
- **Formats:** what FatzAudio opens (`FatzMedia.Audio.AudioEngine.SupportedFormats`) is registered as
  audio when the first engine starts; any other audio FFmpeg decodes still plays through the FFmpeg
  conversion fallback (Stage 033A).
- **MIDI:** FatzAudio loads SF2, SF3 and DLS directly. Without `MidiSoundfontPath` it uses the system
  bank (Windows `gm.dls`, an installed `.sf2` on Linux); with no bank at all a MIDI file does not open.
- **Licence:** FatzMedia's own code is proprietary (all rights reserved, the repo's `LICENSE`); the
  audio engine's third-party components keep their licences (`Audio\THIRD_PARTY_NOTICES.md`).

### Running without the audio engine (ENG-033)

FFmpeg is the only native set a host supplies. When the audio engine cannot start (the FatzAudio
library is missing or of another version, or no output device opens):

- The player constructs in every mode. A Warning diagnostic says "Audio engine FatzAudio could not
  start (...). Audio engine unavailable: video plays without sound; audio media will not open."
- Background mode works fully (it never uses audio).
- Full mode plays video pictures on the playback clock without sound, as Background mode
  does; images and CD+G graphics without audio open normally. This includes network video
  (Stage 039A).
- Media that needs audio (audio files, MP3+G, network audio, and video soundtracks in Audio
  mode) fails to load: `Load` returns false, with an Error diagnostic and a `MediaError`
  (`NativeLibraryMissing`): "Audio engine unavailable; <file> was not loaded."

### Building the native library

Build Audio once, and again after a change under `Audio\src` or `Audio\include` (`Audio\docs\building.md`
has the details; on Windows use the Visual Studio 18 2026 generator, never the VC98 `cl.exe`):

```powershell
cd Audio
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

```bash
cd Audio
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel
```

The build looks for the library in `Audio\build\bin\Release` (Windows) or wherever
`FatzAudioNativeDir` points (`-p:FatzAudioNativeDir=...` or an environment variable of that name; on
Linux CI it is `Audio/build/bin`). Without it everything still builds, but players have no audio and
the FatzAudio native tests return at once.

**CI:** `video-ci.yml` builds the native library on Linux for `FatzMedia.Video.Linux.Tests`. Windows CI
does not build it, so Windows test runs cover the service graph without audio.

## FFmpeg Policy

Approved source baseline: signed FFmpeg 9.0.2 source from
<https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz> (updated 2026-10-17 with the binding;
the owner confirms the signed tarball).

- Managed binding: `FFmpeg.AutoGen` 9.0.1.1 (upgraded from 8.1.0 on 2026-10-17).
- Supported native release line: FFmpeg 9.0.x with library majors 63/63/63/12/61/7/10
  (avcodec/avdevice/avformat/avfilter/avutil/swresample/swscale). FFmpeg 8 DLLs no
  longer load.
- Verified: FFmpeg 9.0.2 LGPL shared libraries on Linux (library load, decoder scan,
  BGRA decode, decode benchmark on par with 8.1). Every FFmpeg function the engine calls
  is exported by 9.0.2. Windows runtime smoke with 9.0.x DLLs is pending.
- Required DLLs: `avcodec-63.dll`, `avdevice-63.dll`, `avfilter-12.dll`,
  `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll`, and `swscale-10.dll`.
- A convenient LGPL source for local testing is the NuGet package
  `TqkLibrary.FFmpeg.Lgpl3.Native.win.x64` 9.0.2 (rename the `.nupkg` to `.zip`; the
  DLLs are in `runtimes/win-x64/native`). It includes D3D11VA hardware decoding for
  H.264/HEVC/VP9/AV1. As with any third-party build, review its configuration before
  redistributing it.
- Hosts must use a shared-library build; static executables alone are insufficient.
- GPU decoding with the Vulkan renderer (Stage 045A slices 2-3, Vulkan Video on the renderer's
  device) needs a build configured with `--enable-vulkan` (check with `ffmpeg -hide_banner
  -hwaccels`, which then lists `vulkan`). The Linux BtbN 9.0 LGPL build has it. Without it the
  player logs `this FFmpeg build has no Vulkan hardware context` and the Vulkan renderer decodes
  in software. Whether the TqkLibrary package above includes Vulkan is not yet checked; BtbN and
  Gyan Windows shared builds do.
- Network video (Stage 039A) uses FFmpeg's own protocols: the build needs the `http`, `tcp`
  and `hls` components (on by default) and a TLS backend for `https` (OpenSSL, GnuTLS,
  mbedTLS or Schannel). Certificate checks are described under HTTPS Certificate Verification.

### HTTPS Certificate Verification (ENG-034)

Network video verifies https certificates by default: the engine sets `tls_verify=1` on the
open and on every HLS/DASH playlist, segment and key it opens (FFmpeg passes only a fixed option
subset to those, so the engine adds the TLS options through `AVFormatContext.io_open`). Hosts
change this with `MediaPlayerConfiguration.NetworkTls` (`NetworkTlsPolicy`):
`CertificateAuthorityFile` (a PEM bundle, FFmpeg's `ca_file`) for a private CA, or
`AllowInvalidCertificates` (off by default) for a server the host controls. A rejected
certificate fails the load with `certificate verification failed for <host>: <FFmpeg text>` and
is not retried. Plain http is unaffected.

Where the trusted roots come from depends on the build's TLS backend (read at run time from
`avformat_configuration`; FFmpeg's configure lets Schannel win only when no other TLS library is
enabled):

| Backend | Default roots | `CertificateAuthorityFile` | Engine behaviour without a CA file |
| --- | --- | --- | --- |
| Schannel (Windows) | Windows certificate store; host name and revocation checked | Ignored (install the CA in the store) | Verifies; Warning if a CA file is set |
| OpenSSL (Linux) | Build-time `OPENSSLDIR`; BtbN builds use `/etc/ssl` (`certs/`, `cert.pem`), `SSL_CERT_FILE`/`SSL_CERT_DIR` override | Used | Verifies |
| OpenSSL (Windows) | Build-time path such as `/opt/ffbuild/ssl`, which does not exist on Windows | Used | Warning before connecting; every https server is rejected until a CA file is set or a Schannel build is used |
| GnuTLS | System trust (also the Windows store) | Used | Verifies |
| mbedTLS | None | Used | Warning before connecting; rejected until a CA file is set |

Checked builds:

- Linux, BtbN FFmpeg 9.0 LGPL (`/opt/fatzbomb-native/ffmpeg`, `scripts/setup-linux-native.sh`):
  OpenSSL 3.6 linked statically (`--enable-openssl --disable-schannel`, `OPENSSLDIR: "/etc/ssl"`).
  Without `ca_file` it loads the default paths (`/etc/ssl/certs` on Debian/Ubuntu); a self-signed
  server is rejected (`error:0A000086:SSL routines::certificate verify failed`), accepted with
  `ca_file`, `SSL_CERT_FILE` or `SSL_CERT_DIR`, and a certificate for another host name is
  rejected. Covered by the opt-in tests in `LinuxNetworkVideoNativeTests` (`Https_*`).
- Windows, `TqkLibrary.FFmpeg.Lgpl3.Native.win.x64` 9.0.2 (configure line read from the DLLs):
  `--enable-schannel`, no `--enable-openssl`; the bundled OpenSSL 3.6.3 serves libsrt, librist
  and libssh, not FFmpeg's https. So https uses the Windows store. Not yet run on Windows.

Windows checks for the owner (see the 039A StageUpdate smoke steps): with the shipped DLLs, an
https URL with a public certificate plays and the log has no TLS warning; a self-signed local
server is rejected with `certificate verification failed for ...` (Schannel reports
`Creating security context failed (0x80090325)`), plays with `--net-allow-invalid-certs`, and
plays after its certificate is imported into the current user's Trusted Root store. Before
shipping a different FFmpeg build, read its backend: `ffmpeg -hide_banner -buildconf` (or
search `avformat-63.dll` for `--enable-openssl` / `--enable-schannel`).

Network audio opens through FFmpeg's network path like network video (ENG-034, 039A.3), so this
policy applies to https audio too; FatzAudio only plays the decoded sound.

For a proprietary host, the default approved build policy is an LGPL-compatible
shared build compiled without `--enable-gpl` and without `--enable-nonfree`, followed
by legal review of every enabled external library. FFmpeg's own compliance checklist
is at <https://ffmpeg.org/legal.html>.

FFmpeg.org links to third-party Windows builds, including Gyan and BtbN, but those
archives are not automatically approved for proprietary redistribution. Configuration
and licence terms must be reviewed for each archive. In particular, Gyan "full" builds
are compiled with `--enable-gpl --enable-version3`. They are suitable for local
validation only, unless the distributing host deliberately accepts and complies with
the resulting GPL terms.

### Shipping FFmpeg With Your App

An LGPL build of the FFmpeg DLLs may be shipped inside a host's installer and updates,
including a commercial, closed-source host. This is the host's checklist; it follows
FFmpeg's own at <https://ffmpeg.org/legal.html> and is not legal advice.

1. Use a shared (DLL) build compiled without `--enable-gpl` and without
   `--enable-nonfree`. Check the build's configuration line (shipped with the archive, or
   printed by `ffmpeg -version` from the same build). Gyan "full" builds do not qualify.
2. Ship the seven DLLs unmodified, loaded dynamically as this control does, so users can
   replace them with their own build.
3. Include the LGPL licence text (2.1 or 3, matching the build) with the app, and credit
   FFmpeg in the About box or documentation ("This software uses libraries from the
   FFmpeg project under the LGPL").
4. Make the exact matching source available: host the source tarball and build
   configuration for the shipped version, or link to it from the same place users get
   the app. The approved baseline above names the tarball.
5. Do not forbid, in the EULA, the reverse engineering needed to debug modifications to
   the FFmpeg libraries (both LGPL 2.1 and 3 require this).
6. Ship the DLLs that match the control: FFmpeg 9.0.x for this binding. When an update
   moves the control to a new FFmpeg major, the same app update must carry the new DLLs.
   `PlayerCore.NativeDependencies` reports any missing or wrong-version file at
   startup, so a broken install is visible immediately.
7. Codec patents (H.264, HEVC, AAC, and others) are a separate question from FFmpeg's
   copyright licence and depend on the country; review them before a commercial release.

## Startup Dependency Report

`PlayerCore.NativeDependencies` (and `IPlatformFactory.NativeDependencies`)
returns a `NativeDependencyReport`: one `NativeLibraryCheck` per FFmpeg library
with its exact file name, whether it is required, and `Loaded`, `Missing`, `NotLoaded`
(found, but its set is incomplete), or `FailedToLoad`, plus the search paths. When a
versioned file is missing and another version of it is present, the check carries a hint
(for example "Found avcodec-62.dll, a different version; this control needs
avcodec-63.dll."). `Describe()` gives one readable line per problem for a host to show.
The Windows and Linux platforms fill it in; other platforms return
`NativeDependencyReport.Empty`. When a required (FFmpeg) library fails, the player
constructor throws (its message includes the hints), so read the report from the platform
factory you passed in. The audio engine is not in the report: when it cannot start, the player
warns and plays without audio (ENG-033).

## Binary-Free Package Layout

The Windows package reserves this layout without supplying DLLs:

```text
runtimes/
  win-x64/
    native/
      README.md
      native-assets.manifest.json
      # Host adds approved DLLs in its application output.
```

The manifest is descriptive and contains no checksum approvals because no binary is
shipped. A distributing host should maintain its own lock file containing archive
URL, exact version, SHA-256, build configuration, licence classification, acquisition
date, and approver for every deployed binary set.

## Update Process

1. Open a dedicated native-update change; never replace binaries silently.
2. Obtain the exact signed FFmpeg source release or review a third-party shared build.
   Record source tag/commit, configure line, external libraries, licence class, and hash.
3. Confirm DLL names and FFmpeg library majors still match
   `native-assets.manifest.json` and `FFmpeg.AutoGen` bindings.
4. Run `scripts/test-windows-native-publish.ps1` on a clean Windows x64 machine using
   the candidate directory.
5. Run `docs/VALIDATION.md`, including audio, MIDI, software/hardware video, subtitles,
   CD+G, seek, device-loss fallback, and secondary output.
6. Update the manifest's verified versions and this document only after validation.
7. Retain source, notices, configure output, hashes, and licence evidence with the
   host application's release records.

This policy is engineering guidance, not legal advice.

## CI Gates

The normal Windows CI gate `.github/workflows/ci.yml` runs on `push` and `pull_request`
and covers restore, Release build, tests, the architecture boundary scripts, and a
`dotnet format --verify-no-changes` check. It never stages or loads native binaries.

### Private CI Gate

`.github/workflows/windows-native-smoke.yml` runs only on a private self-hosted
Windows x64 runner and is invoked manually. The
runner must expose a host-owned native directory through repository variable or
runner-service environment variable `FATZBOMB_MEDIA_NATIVE_PATH`.

Required runner labels:

```text
self-hosted, Windows, X64, fatzbomb-native
```

The workflow checks out only managed source, packs the package graph, installs it into
an isolated generated host, and executes both native-path modes. It does not download,
cache, or upload FFmpeg binaries. Generated package/publish output is directed to
the runner's temporary directory rather than the checkout. See
`docs/PRIVATE_NATIVE_RUNNER.md` for secure provisioning and environment configuration.
