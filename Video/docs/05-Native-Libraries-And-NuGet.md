# Native Libraries And NuGet

## Strategy

Use a host-owned native deployment model:

| Layer | Delivery |
|---|---|
| `FatzBombMedia.MediaControl` managed code | Project reference during development; local/private NuGet later. |
| Managed wrappers such as `ManagedBass`, `FFmpeg.AutoGen`, `Vortice` | NuGet package references. |
| Native BASS DLLs | Provided by host app/test shell through configured path. |
| Native FFmpeg DLLs | Provided by host app/test shell through configured path. |

The host owns native deployment because BASS licensing, FFmpeg licensing/build choices, and exact native versions belong to the host app.

## Development Setup

Use a project reference while the API is still changing:

```xml
<ProjectReference Include="..\FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj" />
```

## Later Local NuGet Setup

```powershell
dotnet pack .\FatzBombMedia.MediaControl\FatzBombMedia.MediaControl.csproj -c Release
mkdir C:\LocalNuget
copy .\FatzBombMedia.MediaControl\bin\Release\*.nupkg C:\LocalNuget
dotnet nuget add source C:\LocalNuget -n LocalFatzBombMedia
```

Then a host can use:

```xml
<PackageReference Include="FatzBombMedia.MediaControl" Version="0.1.0-alpha" />
```

## Expected Host Native Layout

```text
ConfiguredNativePath/
  bass.dll
  bass_fx.dll
  bassflac.dll
  bassopus.dll
  avcodec-<major>.dll
  avdevice-<major>.dll
  avfilter-<major>.dll
  avformat-<major>.dll
  avutil-<major>.dll
  swresample-<major>.dll
  swscale-<major>.dll
```

For the test player, the design-time host drop folder is:

```text
FatzBombMedia.Player/native/
```

`FatzBombMedia.Player.csproj` copies `native/**/*.dll` to the runtime `native/` folder, and `PlayerNativeLibraries` resolves that runtime path in code.

## Source Rule

Never hard-code a developer path such as:

```text
C:\Programming\...
```

Resolve host-owned native folder paths in the app, set `DLLPath.Bass` and `DLLPath.FFmpeg`, then create `new MediaPlayer()`.

## Native Loading Behavior

The control supports:

1. Explicit host-provided native path.
2. `AppContext.BaseDirectory/native` as compatibility fallback when no explicit path is supplied.
3. Clear errors when a required native DLL is missing.

Do not require FFmpeg for audio/CDG-only playback unless the loaded content truly needs it.

| File type | Required native layer |
|---|---|
| Audio | BASS |
| Audio+CDG | BASS |
| Standalone CDG | CDG engine |
| Video | FFmpeg + BASS |
| Image | WPF/image decode path |

