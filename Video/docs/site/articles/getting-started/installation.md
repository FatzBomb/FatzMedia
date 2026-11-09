# Installation

This page tells you which packages to reference for each kind of host, which target framework to use, and how to set up the project so the native libraries end up next to your application.

## Requirements

- .NET 10 SDK.
- x64. The native BASS and FFmpeg libraries are x64 only. The Windows packages include an MSBuild guard that fails the build if you set a `RuntimeIdentifier` that is not x64. Leaving `RuntimeIdentifier` empty is fine.
- Windows 10 or 11 for the Windows packages. For Linux, see [Running on Linux](linux.md).
- The BASS and FFmpeg native libraries, which you obtain and license yourself: [Native dependencies](native-dependencies.md).

## Target frameworks

| Host | Target framework | Notes |
| --- | --- | --- |
| WPF | `net10.0-windows7.0` | `UseWPF` is true, `PlatformTarget` is `x64`. |
| WinForms | `net10.0-windows7.0` | `UseWindowsForms` is true, `PlatformTarget` is `x64`. |
| Avalonia on Windows only | `net10.0-windows7.0` | Needed to reference the Windows packages. |
| Avalonia on Linux only | `net10.0` | References the Linux package. |
| Avalonia on both | `net10.0;net10.0-windows7.0` | Multi-target. A `net10.0` project cannot reference a Windows-specific package, so each target framework references its own platform package and the code chooses the platform factory at compile time. |

## Packages per host

The packages are built in lock step, so use the same version for all of them. The examples use the current version, `0.1.0-alpha`.

### WPF

```xml
<ItemGroup>
  <PackageReference Include="FatzMedia.Video.Wpf" Version="0.1.0-alpha" />
  <PackageReference Include="FatzMedia.Video.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>
```

`FatzMedia.Video.Wpf` brings in the Windows package and Core. It does not choose a renderer. `FatzMedia.Video.Windows.Direct3D11` adds the Direct3D11 renderer and the factory that combines it with the Windows services.

### WinForms

```xml
<ItemGroup>
  <PackageReference Include="FatzMedia.Video.WinForms" Version="0.1.0-alpha" />
  <PackageReference Include="FatzMedia.Video.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>
```

`FatzMedia.Video.WinForms` depends on Core only, so the Windows platform package comes from the second reference.

### Avalonia

Avalonia hosts pick the platform package and the renderer themselves:

```xml
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.4" />
  <PackageReference Include="FatzMedia.Video.Avalonia" Version="0.1.0-alpha" />
  <PackageReference Include="FatzMedia.Video.Software" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' == 'windows'">
  <PackageReference Include="FatzMedia.Video.Windows.Direct3D11" Version="0.1.0-alpha" />
</ItemGroup>

<ItemGroup Condition="'$([MSBuild]::GetTargetPlatformIdentifier($(TargetFramework)))' != 'windows'">
  <PackageReference Include="FatzMedia.Video.Linux" Version="0.1.0-alpha" />
</ItemGroup>
```

Add `FatzMedia.Video.Vulkan` if you want the Vulkan renderer. The Software and Vulkan renderers bring `FatzMedia.Video.Text` (their subtitle and lyric text rasterizer) and SkiaSharp 3.119.4, the version Avalonia 12.1.4 uses, with its native library; you do not reference them yourself. The Windows bundle depends on `FatzMedia.Video.Windows`, so you do not list it separately. For a Windows build that uses only the Software or Vulkan renderer, reference `FatzMedia.Video.Windows` instead and skip the Direct3D11 renderer.

> [!NOTE]
> The Avalonia adapter targets `net10.0` and references Avalonia 12. Your application must use a compatible Avalonia version.

### Core only

A host without a UI (an audio-only service, a test) references `FatzMedia.Video` and one platform package (`FatzMedia.Video.Windows` or `FatzMedia.Video.Linux`) and creates <xref:FatzMedia.Video.PlayerCore> directly. A player in <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> `Audio` needs no renderer at all, but the platform factory constructors still take one, so pass the Software renderer's factory.

## Project references instead of packages

If you build from source, reference the projects with the same combinations. Each project is listed in [Overview](overview.md#packages).

## Put the native libraries next to your app

The packages contain no BASS or FFmpeg binaries. The simplest layout is a `native` folder in your project that is copied to the output directory:

```xml
<ItemGroup>
  <None Update="native\**\*.dll" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  <None Update="native\**\*.sf2" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
</ItemGroup>
```

On Linux use `native\**\*.so*` for the shared libraries. SkiaSharp's native library (`libSkiaSharp`, used by the Software and Vulkan renderers for text) is not in this folder: it comes from its NuGet package and is copied by the build like any runtime asset. The `.sf2` line is for a MIDI soundfont, if you ship one; see [Native dependencies](native-dependencies.md) for file names and search rules.

## Check that it works

After the first player is created, read <xref:FatzMedia.Video.PlayerCore.NativeDependencies> to see which libraries loaded. A missing FFmpeg library makes player creation throw; a missing BASS library only disables audio. See [Native dependencies](native-dependencies.md#startup-report).

Then follow a quickstart: [WPF](quickstart-wpf.md), [Avalonia](quickstart-avalonia.md) or [WinForms](quickstart-winforms.md).
