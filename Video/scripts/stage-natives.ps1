<#
.SYNOPSIS
    Fills Video\redist\win-x64 with the Windows x64 native DLLs a FatzMedia host needs.

.DESCRIPTION
    Copies the seven FFmpeg 9.0.x DLLs and fatzaudio.dll into one stable, untracked folder
    (Video\redist\win-x64 by default) that host apps reference, for example through an MSBuild
    property such as FatzMediaNativeDir. Nothing is downloaded: the FFmpeg DLLs come from a folder
    already on disk and fatzaudio.dll from Audio's CMake build. Other FFmpeg DLLs already in the
    destination (an older set, say) are removed so hosts that copy the whole folder get one clean set;
    other files are left alone.
    See docs\NATIVE_DEPENDENCIES.md.

.PARAMETER FfmpegDir
    Folder holding the FFmpeg DLLs. Defaults to the Avalonia test player's native folder.

.PARAMETER AudioDir
    Folder holding fatzaudio.dll. Defaults to Audio's CMake Release output (Audio\build\bin\Release).

.PARAMETER Destination
    Folder to fill. Defaults to Video\redist\win-x64 of this checkout.
#>
[CmdletBinding()]
param(
    [string] $FfmpegDir,
    [string] $AudioDir,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$videoRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $videoRoot

if ([string]::IsNullOrWhiteSpace($FfmpegDir)) {
    $FfmpegDir = Join-Path $videoRoot 'FatzMedia.Video.Sample.Player.Avalonia\native'
}
if ([string]::IsNullOrWhiteSpace($AudioDir)) {
    $AudioDir = Join-Path $repoRoot 'Audio\build\bin\Release'
}
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $videoRoot 'redist\win-x64'
}

# The FFmpeg file list is the one the Windows package manifest pins (FFmpeg 9.0.x).
$manifestPath = Join-Path $videoRoot 'FatzMedia.Video.Windows\runtimes\win-x64\native\native-assets.manifest.json'
$ffmpegFiles = @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).ffmpeg.requiredFiles)
$sources = @($ffmpegFiles | ForEach-Object { Join-Path $FfmpegDir $_ }) + (Join-Path $AudioDir 'fatzaudio.dll')

$missing = @($sources | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
if ($missing.Count -gt 0) {
    $lines = ($missing | ForEach-Object { "  $_" }) -join [Environment]::NewLine
    throw ("Cannot stage the natives; these files are missing:{0}{1}{0}" +
        "Pass -FfmpegDir for another FFmpeg folder; build Audio (docs\NATIVE_DEPENDENCIES.md) or pass -AudioDir for fatzaudio.dll.") -f
        [Environment]::NewLine, $lines
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$destinationFull = (Resolve-Path -LiteralPath $Destination).Path
foreach ($dir in @($FfmpegDir, $AudioDir)) {
    if ((Resolve-Path -LiteralPath $dir).Path.TrimEnd('\') -eq $destinationFull.TrimEnd('\')) {
        throw "The source folder and the destination are the same: $destinationFull"
    }
}

foreach ($source in $sources) {
    Copy-Item -LiteralPath $source -Destination $destinationFull -Force
    $size = (Get-Item -LiteralPath $source).Length
    Write-Host ('Copied   {0,-18} {1,12:N0} bytes  from {2}' -f (Split-Path -Leaf $source), $size, (Split-Path -Parent $source))
}

# Only stale FFmpeg DLLs (another version's avcodec-62.dll, say) go; anything else in the folder stays.
$keep = @($sources | ForEach-Object { Split-Path -Leaf $_ })
Get-ChildItem -LiteralPath $destinationFull -Filter '*.dll' -File |
    Where-Object { $_.Name -match '^(av[a-z]+|sw[a-z]+|postproc)-\d+\.dll$' -and $keep -notcontains $_.Name } |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName
        Write-Host "Removed  $($_.Name) (another FFmpeg version)"
    }

Write-Host "Staged $($sources.Count) files in $destinationFull"
