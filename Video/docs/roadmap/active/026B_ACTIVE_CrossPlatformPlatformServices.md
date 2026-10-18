# Active Stage 026B - Cross-Platform Platform Services

Prepared: 2026-10-16
Status: Linux baseline slice implemented and natively validated in a Linux sandbox
(FFmpeg load + Core software decode); BASS/audio and a full player smoke are pending on
real Linux hardware with BASS. macOS and Linux hardware decode are later slices.

## Platform-Service Capability Matrix (pinned)

| Registration / concern | Windows | Linux (this slice) | Classification |
|---|---|---|---|
| Native library loading | `Bootstrap` (LoadLibraryW, `*.dll`) | `LinuxNativeLibraryBootstrapper` (dlopen, `lib*.so`, DllImport resolver, FFmpeg dependency-order preload) | OS-specific |
| `IMidiLyricProvider` | Core `BassMidiLyricProvider` (registered by `MediaControlServiceProvider`) | same | shared Core default; no per-platform copy |
| `IPrecisionWaiterFactory` | Win32 high-resolution waitable timer | kernel wait + 1 ms spin | OS-specific |
| `IVideoPipelinePlatformServices` | DWM refresh interval | host option (`LinuxPlatformOptions.DisplayRefreshRateHz`, default 60) | OS-specific |
| `IVideoHardwareAccelerationServices` | D3D11VA/DXVA | `SoftwareOnlyVideoHardwareAccelerationServices` | portable fallback; VA-API later |
| `IAudioDeviceMonitor` | BASS + Core Audio default endpoint | BASS state + default-flagged BASS device | OS-specific snapshot, portable detector |
| Decode capabilities | FFmpeg scan + DXVA profiles | FFmpeg scan, software only | portable scan shape |
| Renderer | host-selected | host-selected (software renderer today) | independent |

## Pinned Decisions

1. `FatzBomb.MediaControl.Linux` targets `net10.0`, references Core only, and has no
   runtime OS branches; it shares no base class with the Windows package.
2. Hosts own binaries. Search: host paths, then app folder, `native/`,
   `runtimes/linux-<arch>/native/`. No implicit system-directory search.
3. Required: `libbass.so`, `libbass_fx.so`, and five FFmpeg libraries in one directory.
   Optional add-ons degrade with a message.
4. FFmpeg libraries are preloaded in dependency order (avutil -> swresample -> swscale
   -> avcodec -> avformat -> avfilter -> avdevice).
5. A successful load is kept for the process; a failed load can be retried with other
   paths.
6. Display refresh is a host option (no compositor-neutral query).
7. Core fix: sidecar extension matching is now case-insensitive on case-sensitive file
   systems (directory enumeration instead of exact-name probes); base-name case follows
   the file system.

## Deferred

- macOS package (same contracts, CoreAudio/`dylib` loading) - not started.
- Linux hardware decode (VA-API/VDPAU) - independent follow-up slice.
- Linux renderer pairing beyond the software renderer belongs to 026A.

## Explicit Non-Goals

As in the original idea: no bundled binaries, no renderer/UI/playback policy in
platform packages, no hardware-decode requirement, no single package with OS branches.
