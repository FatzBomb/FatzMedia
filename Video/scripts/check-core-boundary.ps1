$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $root 'FatzMedia.Video'
# Keep the approved reference DAG explicit so intentional project-reference changes require review here.
$projectReferences = @{
    # 037A: the audio engine is FatzMedia.Audio, by project from Audio\ in this repo (its native library is
    # loaded by the binding, so Core itself still has no P/Invoke).
    'FatzMedia.Video\FatzMedia.Video.csproj' = @(
        '..\..\Audio\bindings\dotnet\src\FatzMedia.Audio\FatzMedia.Audio.csproj'
    )
    'FatzMedia.Video.Direct3D11\FatzMedia.Video.Direct3D11.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    # SkiaSharp subtitle and karaoke rasterizer for the software and Vulkan renderers (the
    # Direct3D11 renderer keeps DirectWrite).
    'FatzMedia.Video.Text\FatzMedia.Video.Text.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    'FatzMedia.Video.Software\FatzMedia.Video.Software.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Text\FatzMedia.Video.Text.csproj'
    )

    'FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    'FatzMedia.Video.Windows.Direct3D11\FatzMedia.Video.Windows.Direct3D11.csproj' = @(
        '..\FatzMedia.Video.Direct3D11\FatzMedia.Video.Direct3D11.csproj',
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj'
    )
    'FatzMedia.Video.Wpf\FatzMedia.Video.Wpf.csproj' = @(
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj'
    )
    'FatzMedia.Video.Sample.Player\FatzMedia.Video.Sample.Player.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj',
        '..\FatzMedia.Video.Vulkan\FatzMedia.Video.Vulkan.csproj',
        '..\FatzMedia.Video.Wpf\FatzMedia.Video.Wpf.csproj',
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj',
        '..\FatzMedia.Video.Windows.Direct3D11\FatzMedia.Video.Windows.Direct3D11.csproj'
    )
    'FatzMedia.Video.Vulkan\FatzMedia.Video.Vulkan.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Text\FatzMedia.Video.Text.csproj'
    )
    'FatzMedia.Video.Vulkan.Tests\FatzMedia.Video.Vulkan.Tests.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Vulkan\FatzMedia.Video.Vulkan.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj'
    )
    'FatzMedia.Video.Avalonia\FatzMedia.Video.Avalonia.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    'FatzMedia.Video.Avalonia.Tests\FatzMedia.Video.Avalonia.Tests.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Avalonia\FatzMedia.Video.Avalonia.csproj'
    )
    'FatzMedia.Video.Linux\FatzMedia.Video.Linux.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    'FatzMedia.Video.Linux.Tests\FatzMedia.Video.Linux.Tests.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Linux\FatzMedia.Video.Linux.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj'
    )
    'FatzMedia.Video.Portable.Tests\FatzMedia.Video.Portable.Tests.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj'
    )
    'FatzMedia.Video.Tests\FatzMedia.Video.Tests.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj',
        '..\FatzMedia.Video.Direct3D11\FatzMedia.Video.Direct3D11.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj',
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj',
        '..\FatzMedia.Video.Wpf\FatzMedia.Video.Wpf.csproj',
        '..\FatzMedia.Video.WinForms\FatzMedia.Video.WinForms.csproj'
    )
    'FatzMedia.Video.WinForms\FatzMedia.Video.WinForms.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    # The test players' shared view model (Stage 044A): Core only, no UI, adapter, platform or
    # renderer; each player supplies those through its Hosting/ abstractions.
    'FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj'
    )
    # Test players (hosts): public API, adapters, platform and renderer packages only.
    # The Avalonia player multi-targets and references Windows and Windows.Direct3D11
    # (net10.0-windows7.0 only; Direct3D11 feeds the shared GPU image path, Stage 027A slice 2) and
    # Linux (net10.0 only) conditionally; all appear here because the script reads every item group.
    'FatzMedia.Video.Sample.Player.Avalonia\FatzMedia.Video.Sample.Player.Avalonia.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Avalonia\FatzMedia.Video.Avalonia.csproj',
        '..\FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj',
        '..\FatzMedia.Video.Vulkan\FatzMedia.Video.Vulkan.csproj',
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj',
        '..\FatzMedia.Video.Windows.Direct3D11\FatzMedia.Video.Windows.Direct3D11.csproj',
        '..\FatzMedia.Video.Linux\FatzMedia.Video.Linux.csproj'
    )
    # Stage 046A slice 2: the software renderer, shown through the WinForms surface's frame sink.
    # Stage 046A slice 3: the Vulkan renderer (--renderer=vulkan) in the WPF and WinForms players too.
    'FatzMedia.Video.Sample.Player.WinForms\FatzMedia.Video.Sample.Player.WinForms.csproj' = @(
        '..\FatzMedia.Video\FatzMedia.Video.csproj',
        '..\FatzMedia.Video.Sample.Player.Shared\FatzMedia.Video.Sample.Player.Shared.csproj',
        '..\FatzMedia.Video.Software\FatzMedia.Video.Software.csproj',
        '..\FatzMedia.Video.Vulkan\FatzMedia.Video.Vulkan.csproj',
        '..\FatzMedia.Video.WinForms\FatzMedia.Video.WinForms.csproj',
        '..\FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj',
        '..\FatzMedia.Video.Windows.Direct3D11\FatzMedia.Video.Windows.Direct3D11.csproj'
    )
}

if (-not (Test-Path $coreProject)) {
    throw 'Core boundary check failed: FatzMedia.Video project folder was not found.'
}

function Get-ProjectIncludes {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectPath
    )

    [xml] $project = Get-Content -Raw -Path $ProjectPath
    return @($project.Project.ItemGroup.ProjectReference | ForEach-Object Include | Sort-Object)
}

foreach ($relativeProjectPath in $projectReferences.Keys) {
    $projectPath = Join-Path $root $relativeProjectPath
    if (-not (Test-Path $projectPath)) {
        throw "Core boundary check failed: $relativeProjectPath was not found."
    }

    $expected = @($projectReferences[$relativeProjectPath] | Sort-Object)
    $actual = @(Get-ProjectIncludes -ProjectPath $projectPath)
    if (Compare-Object -ReferenceObject $expected -DifferenceObject $actual) {
        throw "Core boundary check failed: $relativeProjectPath has an unexpected project-reference direction."
    }
}

[xml] $coreProjectFile = Get-Content -Raw -Path (Join-Path $coreProject 'FatzMedia.Video.csproj')
if ($coreProjectFile.Project.PropertyGroup.TargetFramework -ne 'net10.0') {
    throw 'Core boundary check failed: Core must target net10.0.'
}

$corePackageReferences = @($coreProjectFile.Project.ItemGroup.PackageReference | ForEach-Object Include)
if ($corePackageReferences | Where-Object { $_ -like 'Vortice.*' }) {
    throw 'Core boundary check failed: Core must not reference Vortice packages.'
}

# 037A: Core takes FatzMedia.Audio by project from Audio\ in this repo (pinned above), never as a package.
if ($corePackageReferences | Where-Object { $_ -in @('FatzAudio', 'FatzMedia.Audio') }) {
    throw 'Core boundary check failed: Core must reference FatzMedia.Audio by project, not as a package.'
}

$coreFriendAssemblies = @($coreProjectFile.Project.ItemGroup.InternalsVisibleTo | ForEach-Object Include)
if ($coreFriendAssemblies | Where-Object { $_ -and $_ -notin @('FatzMedia.Video.Tests', 'FatzMedia.Video.Portable.Tests', 'FatzMedia.Video.Linux.Tests', 'FatzMedia.Video.Avalonia.Tests') }) {
    throw 'Core boundary check failed: Core must not expose internals to platform or renderer projects.'
}

# The shared player view model stays UI-agnostic: portable TFM, the MVVM toolkit as its only
# package, and no UI-framework or Windows namespaces in its sources.
$sharedPlayerProject = Join-Path $root 'FatzMedia.Video.Sample.Player.Shared'
[xml] $sharedPlayerProjectFile = Get-Content -Raw -Path (Join-Path $sharedPlayerProject 'FatzMedia.Video.Sample.Player.Shared.csproj')
if ($sharedPlayerProjectFile.Project.PropertyGroup.TargetFramework -ne 'net10.0') {
    throw 'Core boundary check failed: FatzMedia.Video.Sample.Player.Shared must target net10.0.'
}

$sharedPlayerPackages = @($sharedPlayerProjectFile.Project.ItemGroup.PackageReference | ForEach-Object Include | Where-Object { $_ })
if ($sharedPlayerPackages | Where-Object { $_ -ne 'CommunityToolkit.Mvvm' }) {
    throw 'Core boundary check failed: FatzMedia.Video.Sample.Player.Shared may reference only the CommunityToolkit.Mvvm package.'
}

$sharedPlayerViolations = @(Get-ChildItem -Path $sharedPlayerProject -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Select-String -Pattern 'using (System\.Windows|Avalonia|Microsoft\.Win32)|FatzMedia\.Video\.(Wpf|Avalonia|WinForms|Windows|Linux|Direct3D11|Vulkan|Software|Text)\b')
if ($sharedPlayerViolations.Count -gt 0) {
    $sharedPlayerViolations | ForEach-Object { Write-Host "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    throw 'Core boundary check failed: FatzMedia.Video.Sample.Player.Shared contains UI-framework, adapter, platform or renderer references.'
}

$forbiddenPatterns = @(
    '\[LibraryImport',
    '\[DllImport',
    '\bVortice\b',
    'using System\.Windows',
    'FatzMedia\.Video\.(Windows|Direct3D11|Vulkan|Software|Text)\b',
    '\bRenderingMethod\b',
    '\bNativeSwapChainPresentMode\b'
)

$violations = @()
$files = Get-ChildItem -Path $coreProject -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

foreach ($file in $files) {
    $lines = @(Get-Content -Path $file.FullName)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($pattern in $forbiddenPatterns) {
            if ($lines[$i] -match $pattern) {
                $violations += "$(Resolve-Path -Relative $file.FullName):$($i + 1): $($lines[$i].Trim())"
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Host $_ }
    throw 'Core boundary check failed: Core contains platform, renderer, renderer-configuration, UI, or P/Invoke references.'
}

Write-Host 'Core boundary check passed: project references are directed, and Core has no platform, renderer, renderer-configuration, UI, P/Invoke, or Vortice dependencies.'
