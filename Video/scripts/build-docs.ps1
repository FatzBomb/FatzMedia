<#
    Builds the user guide and API reference (docs/site) into docs/site/_site.

    Usage:
        powershell -ExecutionPolicy Bypass -File scripts\build-docs.ps1          # build
        powershell -ExecutionPolicy Bypass -File scripts\build-docs.ps1 -Serve   # build, then browse at http://localhost:8080

    The output is a static site: upload the _site folder to any web host. Browsers block the
    site's navigation and diagrams when pages are opened straight from disk (file://), so browse
    it offline through -Serve (or any local static web server).
#>
param([switch]$Serve)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
    $env:EnableWindowsTargeting = 'true'
    $docfxArgs = @('docfx', 'docs/site/docfx.json', '--warningsAsErrors')
    if ($Serve) { $docfxArgs += '--serve' }
    dotnet @docfxArgs
    if ($LASTEXITCODE -ne 0) { throw 'docfx failed.' }
}
finally {
    Pop-Location
}
