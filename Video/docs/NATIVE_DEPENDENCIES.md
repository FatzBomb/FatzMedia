# Native Dependency Distribution Policy

Updated: 2026-10-24

The repository tracks no native DLLs or executables. The Gyan GPL-enabled `ffmpeg.exe`, `ffplay.exe`,
and `ffprobe.exe` fixtures were removed on 2026-10-17 (owner decision): the app never runs
them, and it probes and decodes media in-process through the FFmpeg DLLs.

## Responsibility

`FatzBomb.MediaControl` packages do not contain BASS or FFmpeg native binaries.
The host developer is responsible for obtaining, licensing, validating, deploying,
and updating the native DLLs used by their application. This is the same model used
by `FatzBomb.MediaControl.Player`.

The host may either:

1. Put DLLs in an existing predefined directory beneath the application base:
   - `native`
   - `runtimes/win-x64/native`
2. Pass one or more custom directories, in priority order, to the WPF `Player` or
   Core `MediaControlPlayer` constructor.

Caller-supplied directories are searched first. Existing predefined directories are
appended as fallbacks. Searches are recursive, so BASS and FFmpeg may use separate
subdirectories beneath a supplied root. Paths passed through `MediaControlPlayer`
must exist; a missing directory produces `DirectoryNotFoundException` before native
initialization.

```csharp
var platform = new WindowsDirect3D11MediaControlPlatformFactory();
IReadOnlyList<string> nativePaths =
[
    @"C:\Program Files\MyHost\media-native\bass",
    @"C:\Program Files\MyHost\media-native\ffmpeg"
];

var player = await FatzBomb.MediaControl.Wpf.Player.CreateAsync(
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
| `win-arm64` | Not supported | Requires dedicated BASS/FFmpeg validation and package targeting. |
| `linux-x64` | Preview (Stage 026B) | `FatzBomb.MediaControl.Linux`; FFmpeg load and software decode validated, BASS/player smoke pending. See Linux Native Target. |
| `osx-*`, `android-*`, other `linux-*` | Not supported | Require platform packages and native policies of their own. |

The managed Windows projects retain `net10.0-windows7.0` API compatibility, but the
approved native runtime baseline is Windows 10/11 x64. A native distributor may have
stricter OS or UCRT requirements than the managed TFM.

## Linux Native Target (Stage 026B, pending manual smoke)

`FatzBomb.MediaControl.Linux` follows the same host-owned policy: no binaries ship in
the package. The host supplies shared libraries in a host path (absolute or relative to
the app), `native/`, or `runtimes/linux-<arch>/native/`. System library directories are
not searched implicitly.

| Kind | Core / required | Optional |
|---|---|---|
| BASS (optional, ENG-033: core needed for audio) | `libbass.so`, `libbass_fx.so` | `libbass_aac.so`, `libbass_ac3.so`, `libbassflac.so`, `libbassmidi.so`, `libbassopus.so`, `libbasswv.so` |
| FFmpeg (avcodec 63 ABI, FFmpeg 9.0.x) | `libavutil.so.61`, `libswresample.so.7`, `libswscale.so.10`, `libavcodec.so.63`, `libavformat.so.63` | `libavfilter.so.12`, `libavdevice.so.63` |

FFmpeg libraries must sit in one directory; they are preloaded in dependency order
because typical builds reference each other by soname without an `$ORIGIN` run path.
BASS core libraries are routed to ManagedBass through a `DllImport` resolver; add-ons
use `BASS_PluginLoad`. The same licensing cautions as Windows apply (BASS commercial
licence; FFmpeg LGPL vs GPL build choice).

## BASS Policy

Approved source: Un4seen Developments only:
<https://www.un4seen.com/bass.html>

- Supported ABI line: BASS 2.4.
- Verified repository fixture: `bass.dll` 2.4.17.
- Current official update candidate at policy date: BASS 2.4.18.3.
- Core: `bass.dll`, `bass_fx.dll`. BASS as a whole is optional (ENG-033): without the
  core DLLs the player still starts, with no audio (see "Running without BASS").
- Optional plugins: `bass_aac.dll`, `bass_ac3.dll`, `bassalac.dll`, `basscd.dll`,
  `bassflac.dll`, `bassmidi.dll`, `bassopus.dll`, and `basswma.dll`.
- Optional plugin presence controls the corresponding accepted file extensions. When
  FFmpeg is loaded, formats a missing plugin would have played (AAC/M4A, ALAC, FLAC,
  Opus, AC-3, WMA, and other audio FFmpeg decodes) still open through the FFmpeg
  fallback (Stage 033A); MIDI and CD audio still need `bassmidi` and `basscd`.

### Running without BASS (ENG-033)

FFmpeg is the only required native set. When the BASS core (`bass.dll` + `bass_fx.dll`,
`libbass.so` + `libbass_fx.so`) is missing or fails to load:

- The player constructs in every mode. Native loading reports a Warning, not a failure, and
  no BASS function is called: no plugins, no audio device monitor, no MIDI soundfont, no
  output watchdog, and `AudioOutputDevices.Enumerate` returns an empty list.
- `NativeDependencyReport.IsAudioAvailable` is false; the BASS checks are listed with
  `IsRequired` false, and `Describe()` adds "Audio is unavailable: ...".
- Background mode works fully (it never uses audio).
- Full mode plays video pictures on the playback clock without sound, as Background mode
  does; images and CD+G graphics without audio open normally. This includes network video
  (Stage 039A).
- Media that needs audio (audio files, MP3+G, network audio, and video soundtracks in Audio
  mode) fails to load: `Load` returns false, with an Error diagnostic and a `MediaError`
  (`NativeLibraryMissing`): "Audio engine unavailable: BASS libraries not found; <file> was
  not loaded."
- A later player creation retries BASS with its own paths; the loaded FFmpeg is kept.

BASS is free only for qualifying non-commercial use. Commercial host developers
must obtain the appropriate BASS licence. Un4seen states that BASS licences apply to
the licensee's end-user products and do not permit reselling or sublicensing BASS.
This control therefore does not redistribute BASS; a developer purchasing or using
the control remains responsible for their own BASS rights and each add-on's terms.

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
- Network video (Stage 039A) uses FFmpeg's own protocols: the build needs the `http`, `tcp`
  and `hls` components (on by default) and a TLS backend for `https` (OpenSSL, GnuTLS,
  mbedTLS or Schannel). FFmpeg's TLS layer verifies certificates only if its `tls_verify`
  option is on, which the engine does not set yet (ENG-034).

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
   `MediaControlPlayer.NativeDependencies` reports any missing or wrong-version file at
   startup, so a broken install is visible immediately.
7. Codec patents (H.264, HEVC, AAC, and others) are a separate question from FFmpeg's
   copyright licence and depend on the country; review them before a commercial release.

## Startup Dependency Report

`MediaControlPlayer.NativeDependencies` (and `IMediaControlPlatformFactory.NativeDependencies`)
returns a `NativeDependencyReport`: one `NativeLibraryCheck` per BASS and FFmpeg library
with its exact file name, whether it is required, and `Loaded`, `Missing`, `NotLoaded`
(found, but its set is incomplete), or `FailedToLoad`, plus the search paths and
`IsAudioAvailable` (false when the BASS core did not load). Only FFmpeg libraries are
required; BASS libraries are always reported as not required. When a
versioned file is missing and another version of it is present, the check carries a hint
(for example "Found avcodec-62.dll, a different version; this control needs
avcodec-63.dll."). `Describe()` gives one readable line per problem for a host to show.
The Windows and Linux platforms fill it in; other platforms return
`NativeDependencyReport.Empty`. When a required (FFmpeg) library fails, the player
constructor throws (its message includes the hints), so read the report from the platform
factory you passed in. A missing BASS does not throw (ENG-033).

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
2. Download BASS only from the approved Un4seen source. Verify archive provenance and
   record exact core/add-on versions and hashes in the host's lock file.
3. Obtain the exact signed FFmpeg source release or review a third-party shared build.
   Record source tag/commit, configure line, external libraries, licence class, and hash.
4. Confirm DLL names and FFmpeg library majors still match
   `native-assets.manifest.json` and `FFmpeg.AutoGen` bindings.
5. Run `scripts/test-windows-native-publish.ps1` on a clean Windows x64 machine using
   the candidate directory.
6. Run `docs/VALIDATION.md`, including audio, MIDI, software/hardware video, subtitles,
   CD+G, seek, device-loss fallback, and secondary output.
7. Update the manifest's verified versions and this document only after validation.
8. Retain source, notices, configure output, hashes, and licence evidence with the
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
cache, or upload BASS/FFmpeg binaries. Generated package/publish output is directed to
the runner's temporary directory rather than the checkout. See
`docs/PRIVATE_NATIVE_RUNNER.md` for secure provisioning and environment configuration.
