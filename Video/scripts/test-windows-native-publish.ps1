[CmdletBinding()]
param(
    [string] $NativeLibraryPath = $env:FATZBOMB_MEDIA_NATIVE_PATH,
    [string] $Configuration = 'Release',
    [string] $PackageVersion,
    [string] $ArtifactsDirectory,
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    [xml] $buildProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $PackageVersion = @($buildProps.Project.PropertyGroup) |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
        throw 'PackageVersion was not supplied and could not be derived from Directory.Build.props <Version>.'
    }
}

if ([string]::IsNullOrWhiteSpace($NativeLibraryPath)) {
    throw 'Supply -NativeLibraryPath or set FATZBOMB_MEDIA_NATIVE_PATH to one host-owned directory containing the Windows x64 FFmpeg DLL set.'
}

$resolvedNativePath = (Resolve-Path -LiteralPath $NativeLibraryPath).Path
if (-not (Test-Path -LiteralPath $resolvedNativePath -PathType Container)) {
    throw "Native smoke test path is not a directory: $resolvedNativePath"
}

$manifestPath = Join-Path $root 'FatzMedia.Video.Windows\runtimes\win-x64\native\native-assets.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$requiredFiles = @($manifest.ffmpeg.requiredFiles)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedNativePath $requiredFile) -PathType Leaf)) {
        throw "Native smoke test input is missing required file: $requiredFile"
    }
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$artifactBase = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path $root 'artifacts\native-smoke'
}
else {
    [System.IO.Path]::GetFullPath($ArtifactsDirectory)
}
$artifactRoot = Join-Path $artifactBase $runId
$feedPath = Join-Path $artifactRoot 'feed'
$packagesPath = Join-Path $artifactRoot 'packages'
$hostPath = Join-Path $artifactRoot 'host'
$publishPath = Join-Path $artifactRoot 'publish'
$runtimeNativePath = Join-Path $publishPath 'runtimes\win-x64\native'

$null = New-Item -ItemType Directory -Path $feedPath -Force
$null = New-Item -ItemType Directory -Path $hostPath -Force

$packProjects = @(
    'FatzMedia.Video\FatzMedia.Video.csproj',
    'FatzMedia.Video.Direct3D11\FatzMedia.Video.Direct3D11.csproj',
    'FatzMedia.Video.Windows\FatzMedia.Video.Windows.csproj',
    'FatzMedia.Video.Windows.Direct3D11\FatzMedia.Video.Windows.Direct3D11.csproj'
)

foreach ($relativeProject in $packProjects) {
    & dotnet pack (Join-Path $root $relativeProject) -c $Configuration -o $feedPath -p:PackageVersion=$PackageVersion --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Package creation failed: $relativeProject"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$windowsPackagePath = Join-Path $feedPath "FatzMedia.Video.Windows.$PackageVersion.nupkg"
if (-not (Test-Path -LiteralPath $windowsPackagePath -PathType Leaf)) {
    throw "Windows package was not created: $windowsPackagePath"
}

$windowsPackage = [System.IO.Compression.ZipFile]::OpenRead($windowsPackagePath)
try {
    $packageEntries = @($windowsPackage.Entries | ForEach-Object FullName)
    if ($packageEntries -notcontains 'runtimes/win-x64/native/native-assets.manifest.json') {
        throw 'Windows package is missing the binary-free win-x64 native manifest.'
    }

    if ($packageEntries | Where-Object { $_ -match '^runtimes/win-x64/native/.+\.dll$' }) {
        throw 'Windows package unexpectedly contains native DLLs.'
    }
}
finally {
    $windowsPackage.Dispose()
}

$templatePath = Join-Path $PSScriptRoot 'native-smoke\NativePublishSmoke.csproj.template'
$programTemplatePath = Join-Path $PSScriptRoot 'native-smoke\Program.cs'
$smokeProjectPath = Join-Path $hostPath 'NativePublishSmoke.csproj'
$smokeProgramPath = Join-Path $hostPath 'Program.cs'
$nugetConfigPath = Join-Path $hostPath 'NuGet.config'

$projectText = (Get-Content -LiteralPath $templatePath -Raw).Replace('__PACKAGE_VERSION__', $PackageVersion)
Set-Content -LiteralPath $smokeProjectPath -Value $projectText -Encoding utf8
Copy-Item -LiteralPath $programTemplatePath -Destination $smokeProgramPath

$feedUri = [System.Security.SecurityElement]::Escape($feedPath)
$packagesUri = [System.Security.SecurityElement]::Escape($packagesPath)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$packagesUri" />
  </config>
  <packageSources>
    <clear />
    <add key="native-smoke-feed" value="$feedUri" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
Set-Content -LiteralPath $nugetConfigPath -Value $nugetConfig -Encoding utf8

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& dotnet restore $smokeProjectPath --configfile $nugetConfigPath -r win-x64 -p:SelfContained=$selfContained --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Generated native smoke host restore failed.'
}

& dotnet publish $smokeProjectPath -c $Configuration -r win-x64 --self-contained $selfContained --no-restore -o $publishPath --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Generated native smoke host publish failed.'
}

$expectedManagedAssemblies = @(
    'NativePublishSmoke.dll',
    'FatzMedia.Video.dll',
    'FatzMedia.Video.Windows.dll',
    'FatzMedia.Video.Direct3D11.dll',
    'FatzMedia.Video.Windows.Direct3D11.dll'
)
foreach ($assembly in $expectedManagedAssemblies) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $assembly) -PathType Leaf)) {
        throw "Published host is missing managed assembly: $assembly"
    }
}

function Invoke-PublishedSmoke {
    param([string[]] $Arguments)

    if ($FrameworkDependent) {
        & dotnet (Join-Path $publishPath 'NativePublishSmoke.dll') @Arguments
    }
    else {
        & (Join-Path $publishPath 'NativePublishSmoke.exe') @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Published native smoke host failed with exit code $LASTEXITCODE. Artifacts: $artifactRoot"
    }
}

# First prove caller-supplied paths work while the published output contains no native DLLs.
Invoke-PublishedSmoke -Arguments @($resolvedNativePath)

$null = New-Item -ItemType Directory -Path $runtimeNativePath -Force
Get-ChildItem -LiteralPath $resolvedNativePath -File -Filter '*.dll' | Copy-Item -Destination $runtimeNativePath

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeNativePath $requiredFile) -PathType Leaf)) {
        throw "Published runtime layout is missing staged native file: $requiredFile"
    }
}

# Then prove the predefined NuGet-style runtime layout works without caller paths.
Invoke-PublishedSmoke -Arguments @()

Write-Host "Windows native publish smoke test passed for custom and predefined paths. Artifacts: $artifactRoot"
