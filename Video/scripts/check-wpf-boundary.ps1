$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $root 'FatzBombMedia.MediaControl'
$wpfProject = Join-Path $root 'FatzBombMedia.MediaControl.Wpf'

$patterns = @(
    'using System\.Windows',
    '\bImageSource\b',
    '\bBitmapSource\b',
    '\bBitmapImage\b',
    '\bWriteableBitmap\b',
    '\bD3DImage\b',
    '\bDispatcher\b',
    'PixelFormats',
    'FormatConvertedBitmap',
    'WritePixels',
    'CopyPixels',
    '\bWpfD3DImage\b',
    '\bWpfRenderingTier\b'
)

$files = Get-ChildItem -Path $coreProject -Recurse -Filter '*.cs' |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    }

$violations = foreach ($file in $files) {
    $relative = Resolve-Path -Relative $file.FullName
    $lines = Get-Content -Path $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($pattern in $patterns) {
            if ($lines[$i] -match $pattern) {
                [pscustomobject]@{
                    File = $relative
                    Line = $i + 1
                    Pattern = $pattern
                    Text = $lines[$i].Trim()
                }
            }
        }
    }
}

if ($violations) {
    $violations | Format-Table -AutoSize
    throw 'WPF boundary check failed: WPF-specific bitmap/UI types were found in FatzBombMedia.MediaControl.Core.'
}

if (-not (Test-Path $wpfProject)) {
    throw 'WPF boundary check failed: FatzBombMedia.MediaControl.Wpf project folder was not found.'
}

Write-Host 'WPF boundary check passed: Core contains no WPF-specific bitmap/UI types; WPF code is isolated in FatzBombMedia.MediaControl.Wpf.'
