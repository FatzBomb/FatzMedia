$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $root 'FatzBomb.MediaControl'
# Keep the approved reference DAG explicit so intentional project-reference changes require review here.
$projectReferences = @{
    'FatzBomb.MediaControl\FatzBomb.MediaControl.csproj' = @()
    'FatzBomb.MediaControl.Rendering.Direct3D11\FatzBomb.MediaControl.Rendering.Direct3D11.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Windows\FatzBomb.MediaControl.Windows.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Windows.Direct3D11\FatzBomb.MediaControl.Windows.Direct3D11.csproj' = @(
        '..\FatzBomb.MediaControl.Rendering.Direct3D11\FatzBomb.MediaControl.Rendering.Direct3D11.csproj',
        '..\FatzBomb.MediaControl.Windows\FatzBomb.MediaControl.Windows.csproj'
    )
    'FatzBomb.MediaControl.Wpf\FatzBomb.MediaControl.Wpf.csproj' = @(
        '..\FatzBomb.MediaControl.Windows\FatzBomb.MediaControl.Windows.csproj'
    )
    'FatzBomb.MediaControl.Player\FatzBomb.MediaControl.Player.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj',
        '..\FatzBomb.MediaControl.Wpf\FatzBomb.MediaControl.Wpf.csproj',
        '..\FatzBomb.MediaControl.Windows\FatzBomb.MediaControl.Windows.csproj',
        '..\FatzBomb.MediaControl.Windows.Direct3D11\FatzBomb.MediaControl.Windows.Direct3D11.csproj'
    )
    'FatzBomb.MediaControl.Rendering.Vulkan\FatzBomb.MediaControl.Rendering.Vulkan.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Rendering.Vulkan.Tests\FatzBomb.MediaControl.Rendering.Vulkan.Tests.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Rendering.Vulkan\FatzBomb.MediaControl.Rendering.Vulkan.csproj',
        '..\FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj'
    )
    'FatzBomb.MediaControl.Avalonia\FatzBomb.MediaControl.Avalonia.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Avalonia.Tests\FatzBomb.MediaControl.Avalonia.Tests.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Avalonia\FatzBomb.MediaControl.Avalonia.csproj'
    )
    'FatzBomb.MediaControl.Linux\FatzBomb.MediaControl.Linux.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
    'FatzBomb.MediaControl.Linux.Tests\FatzBomb.MediaControl.Linux.Tests.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Linux\FatzBomb.MediaControl.Linux.csproj',
        '..\FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj'
    )
    'FatzBomb.MediaControl.Portable.Tests\FatzBomb.MediaControl.Portable.Tests.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj'
    )
    'FatzBomb.MediaControl.Tests\FatzBomb.MediaControl.Tests.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj',
        '..\FatzBomb.MediaControl.Rendering.Direct3D11\FatzBomb.MediaControl.Rendering.Direct3D11.csproj',
        '..\FatzBomb.MediaControl.Rendering.Software\FatzBomb.MediaControl.Rendering.Software.csproj',
        '..\FatzBomb.MediaControl.Windows\FatzBomb.MediaControl.Windows.csproj',
        '..\FatzBomb.MediaControl.Wpf\FatzBomb.MediaControl.Wpf.csproj',
        '..\FatzBomb.MediaControl.WinForms\FatzBomb.MediaControl.WinForms.csproj'
    )
    'FatzBomb.MediaControl.WinForms\FatzBomb.MediaControl.WinForms.csproj' = @(
        '..\FatzBomb.MediaControl\FatzBomb.MediaControl.csproj'
    )
}

if (-not (Test-Path $coreProject)) {
    throw 'Core boundary check failed: FatzBomb.MediaControl project folder was not found.'
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

[xml] $coreProjectFile = Get-Content -Raw -Path (Join-Path $coreProject 'FatzBomb.MediaControl.csproj')
if ($coreProjectFile.Project.PropertyGroup.TargetFramework -ne 'net10.0') {
    throw 'Core boundary check failed: Core must target net10.0.'
}

$corePackageReferences = @($coreProjectFile.Project.ItemGroup.PackageReference | ForEach-Object Include)
if ($corePackageReferences | Where-Object { $_ -like 'Vortice.*' }) {
    throw 'Core boundary check failed: Core must not reference Vortice packages.'
}

$coreFriendAssemblies = @($coreProjectFile.Project.ItemGroup.InternalsVisibleTo | ForEach-Object Include)
if ($coreFriendAssemblies | Where-Object { $_ -and $_ -notin @('FatzBomb.MediaControl.Tests', 'FatzBomb.MediaControl.Portable.Tests', 'FatzBomb.MediaControl.Linux.Tests') }) {
    throw 'Core boundary check failed: Core must not expose internals to platform or renderer projects.'
}

$forbiddenPatterns = @(
    '\[LibraryImport',
    '\[DllImport',
    '\bVortice\b',
    'using System\.Windows',
    'FatzBomb\.MediaControl\.(Windows|Rendering)',
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
