$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $root 'FatzBombMedia.MediaControl'
$wpfProject = Join-Path $root 'FatzBombMedia.MediaControl.Wpf'

if (-not (Test-Path $coreProject)) {
    throw 'WPF boundary check failed: FatzBombMedia.MediaControl project folder was not found.'
}

if (-not (Test-Path $wpfProject)) {
    throw 'WPF boundary check failed: FatzBombMedia.MediaControl.Wpf project folder was not found.'
}

$coreForbiddenPatterns = @(
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

$adapterForbiddenPatterns = @(
    '\bMediaRenderer\b',
    '\bOptimizedGraphicsRenderer\b',
    '\bRenderPipeline\b',
    '\bVideoPipeline\b',
    '\bICdgEngine\b',
    '\bIVideoEngine\b',
    '\bFrameReady\b',
    '\bBackgroundFrameReady\b',
    '\bImageFrameReady\b',
    '\bBackgroundCleared\b',
    '\bDxCdg\b',
    '\bD3D11\b',
    '\bVortice\b',
    '\bRenderer\b',
    '\bPipeline\b'
)

function Find-Violations {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string[]] $Patterns
    )

    $files = Get-ChildItem -Path $Path -Recurse -Filter '*.cs' |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }

    foreach ($file in $files) {
        $relative = Resolve-Path -Relative $file.FullName
        $lines = Get-Content -Path $file.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($pattern in $Patterns) {
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
}

$coreViolations = @(Find-Violations -Path $coreProject -Patterns $coreForbiddenPatterns)
if ($coreViolations.Count -gt 0) {
    $coreViolations | Format-Table -AutoSize
    throw 'WPF boundary check failed: WPF-specific bitmap/UI types were found in FatzBombMedia.MediaControl.'
}

$adapterViolations = @(Find-Violations -Path $wpfProject -Patterns $adapterForbiddenPatterns)
if ($adapterViolations.Count -gt 0) {
    $adapterViolations | Format-Table -AutoSize
    throw 'WPF boundary check failed: renderer/pipeline/DxCdg/D3D11 terms were found in FatzBombMedia.MediaControl.Wpf.'
}

Write-Host 'WPF boundary check passed: Core has no WPF UI/bitmap types, and WPF adapter has no renderer/pipeline/DxCdg/D3D11 references.'
