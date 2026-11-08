$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$coreProject = Join-Path $root 'FatzBomb.MediaControl'
$wpfProject = Join-Path $root 'FatzBomb.MediaControl.Wpf'

if (-not (Test-Path $coreProject)) {
    throw 'WPF boundary check failed: FatzBomb.MediaControl project folder was not found.'
}

if (-not (Test-Path $wpfProject)) {
    throw 'WPF boundary check failed: FatzBomb.MediaControl.Wpf project folder was not found.'
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
    '\bWpfD3DImage\b',
    '\bWpfRenderingTier\b'
)

# The WPF adapter may expose safe public CDG-facing concepts, such as HasCdg or CdgVisuals.
# It must not reference Core pipeline, renderer, internal CDG engine/snapshot/state types,
# D3D11, Vortice, or DirectComposition implementation details.
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
    'FatzBomb\.MediaControl\.Core\.Cdg',
    '\bCdgEngine\b',
    '\bCdgStateMachine\b',
    '\bCdgRenderSnapshot\b',
    '\bCdgDirtyRect\b',
    '\bCdgPacket\b',
    '\bCdgConstants\b',
    '\bICdgSnapshotSource\b',
    '\bD3D11\b',
    '\bDirectComposition\b',
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
        $lines = @(Get-Content -Path $file.FullName)
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
    throw 'WPF boundary check failed: WPF-specific bitmap/UI types were found in FatzBomb.MediaControl.'
}

$adapterViolations = @(Find-Violations -Path $wpfProject -Patterns $adapterForbiddenPatterns)
if ($adapterViolations.Count -gt 0) {
    $adapterViolations | Format-Table -AutoSize
    throw 'WPF boundary check failed: renderer/pipeline/internal CDG/D3D11 terms were found in FatzBomb.MediaControl.Wpf.'
}

[xml] $wpfProjectFile = Get-Content -Raw -Path (Join-Path $wpfProject 'FatzBomb.MediaControl.Wpf.csproj')
$wpfProjectReferences = @($wpfProjectFile.Project.ItemGroup.ProjectReference | ForEach-Object Include)
if ($wpfProjectReferences | Where-Object { $_ -match 'Rendering\.Direct3D11' }) {
    throw 'WPF boundary check failed: WPF adapter must not reference the Direct3D11 renderer project.'
}

Write-Host 'WPF boundary check passed: Core has no WPF UI/bitmap types, and WPF adapter has no renderer/pipeline/internal CDG/D3D11 references.'
