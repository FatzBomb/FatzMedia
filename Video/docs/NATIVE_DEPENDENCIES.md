# Native Dependency Distribution Policy

Updated: 2026-07-17

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
| `linux-*`, `osx-*`, `android-*` | Not supported | Require platform packages and native policies of their own. |

The managed Windows projects retain `net10.0-windows7.0` API compatibility, but the
approved native runtime baseline is Windows 10/11 x64. A native distributor may have
stricter OS or UCRT requirements than the managed TFM.

## BASS Policy

Approved source: Un4seen Developments only:
<https://www.un4seen.com/bass.html>

- Supported ABI line: BASS 2.4.
- Verified repository fixture: `bass.dll` 2.4.17.
- Current official update candidate at policy date: BASS 2.4.18.3.
- Required: `bass.dll`, `bass_fx.dll`.
- Optional plugins: `bass_aac.dll`, `bass_ac3.dll`, `bassalac.dll`, `basscd.dll`,
  `bassflac.dll`, `bassmidi.dll`, `bassopus.dll`, and `basswma.dll`.
- Optional plugin presence controls the corresponding accepted file extensions.

BASS is free only for qualifying non-commercial use. Commercial host developers
must obtain the appropriate BASS licence. Un4seen states that BASS licences apply to
the licensee's end-user products and do not permit reselling or sublicensing BASS.
This control therefore does not redistribute BASS; a developer purchasing or using
the control remains responsible for their own BASS rights and each add-on's terms.

## FFmpeg Policy

Approved source baseline: signed FFmpeg 8.1.2 source from
<https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz>.

- Managed binding: `FFmpeg.AutoGen` 8.1.0.
- Supported native release line: FFmpeg 8.1.x with library majors 62/62/62/11/60/6/9.
- Verified repository validation fixture: FFmpeg 8.1.1.
- Required DLLs: `avcodec-62.dll`, `avdevice-62.dll`, `avfilter-11.dll`,
  `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, and `swscale-9.dll`.
- Hosts must use a shared-library build; static executables alone are insufficient.

For a proprietary host, the default approved build policy is an LGPL-compatible
shared build compiled without `--enable-gpl` and without `--enable-nonfree`, followed
by legal review of every enabled external library. FFmpeg's own compliance checklist
is at <https://ffmpeg.org/legal.html>.

FFmpeg.org links to third-party Windows builds, including Gyan and BtbN, but those
archives are not automatically approved for proprietary redistribution. Configuration
and licence terms must be reviewed for each archive. In particular, the Gyan "full
shared" fixture currently used for local Player validation is built with
`--enable-gpl --enable-version3`; it is suitable for repository validation only unless
the distributing host deliberately accepts and complies with the resulting GPL terms.

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

## Private CI Gate

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
