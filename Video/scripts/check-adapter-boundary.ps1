$ErrorActionPreference = 'Stop'

# Applies the WPF adapter rules to every other UI adapter: adapters own framework surface
# lifecycle and input conversions only. They must not reference renderer, pipeline, internal
# CDG, video-frame, or GPU implementation types, and must not reference renderer projects.
$root = Split-Path -Parent $PSScriptRoot
$adapters = @(
    'FatzBomb.MediaControl.Avalonia',
    'FatzBomb.MediaControl.WinForms'
)

$forbiddenPatterns = @(
    '\bMediaRenderer\b',
    '\bRenderPipeline\b',
    '\bVideoPipeline\b',
    '\bICdgEngine\b',
    '\bIVideoEngine\b',
    '\bIMediaEngine\b',
    '\bFrameReady\b',
    '\bBackgroundFrameReady\b',
    '\bImageFrameReady\b',
    'FatzBomb\.MediaControl\.Core\.Cdg',
    'FatzBomb\.MediaControl\.Core\.Video',
    'FatzBomb\.MediaControl\.Core\.Graphics',
    'FatzBomb\.MediaControl\.Pipeline',
    'FatzBomb\.MediaControl\.Rendering',
    '\bCdgEngine\b',
    '\bCdgRenderSnapshot\b',
    '\bVideoFrameEventArgs\b',
    '\bD3D11\b',
    '\bVortice\b',
    '\bVulkan\b',
    '\bSilk\.NET\b',
    '\bPipeline\b'
)

$violations = @()
foreach ($adapter in $adapters) {
    $adapterPath = Join-Path $root $adapter
    if (-not (Test-Path $adapterPath)) {
        throw "Adapter boundary check failed: $adapter was not found."
    }

    $files = Get-ChildItem -Path $adapterPath -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($file in $files) {
        $lines = @(Get-Content -Path $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($pattern in $forbiddenPatterns) {
                if ($lines[$i] -match $pattern) {
                    $violations += "$adapter/$($file.Name):$($i + 1): $($lines[$i].Trim())"
                }
            }
        }
    }

    [xml] $projectFile = Get-Content -Raw -Path (Join-Path $adapterPath "$adapter.csproj")
    $references = @($projectFile.Project.ItemGroup.ProjectReference | ForEach-Object Include)
    if ($references | Where-Object { $_ -match 'Rendering\.|Windows\.Direct3D11' }) {
        throw "Adapter boundary check failed: $adapter must not reference renderer projects; renderer choice belongs to the host."
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Host $_ }
    throw 'Adapter boundary check failed: renderer/pipeline/internal CDG/GPU terms were found in a UI adapter.'
}

Write-Host "Adapter boundary check passed: $($adapters -join ', ') own only surface lifecycle and input conversions."
