<#
.SYNOPSIS
    Captures a thread-model baseline (stage 022A) from a running FatzBombMedia host.

.DESCRIPTION
    Attaches dotnet-counters (runtime thread pool, lock contention, CPU, GC) and dotnet-trace
    (CPU sampling plus runtime contention and threading events) to one process for a fixed
    duration. Run one workflow per capture (for example "video: open, play, seek x5, pause,
    loop, stop, close") and give it a -Label. Results land in -OutputDirectory as
    <label>.counters.csv and <label>.nettrace; open the trace in PerfView or Visual Studio.

.EXAMPLE
    pwsh scripts/capture-thread-baseline.ps1 -Label video-hw -DurationSeconds 60
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'FatzBomb.MediaControl.Player',
    [int]$ProcessId = 0,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Label,
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 60,
    [string]$OutputDirectory = 'artifacts/thread-baseline',
    [switch]$SkipTrace
)

$ErrorActionPreference = 'Stop'

function Get-DotNetTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    Write-Host "Installing $Name as a global .NET tool..."
    dotnet tool install --global $Name | Out-Host
    $toolDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet/tools'
    $candidate = Get-ChildItem -Path $toolDirectory -Filter "$Name*" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $candidate) {
        throw "$Name is not available after installation; add $toolDirectory to PATH."
    }

    return $candidate.FullName
}

if ($ProcessId -le 0) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) {
        throw "No running process named '$ProcessName'. Start the host first or pass -ProcessId."
    }

    $ProcessId = $process.Id
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$countersPath = Join-Path $OutputDirectory "$Label.counters.csv"
$tracePath = Join-Path $OutputDirectory "$Label.nettrace"
$duration = [TimeSpan]::FromSeconds($DurationSeconds)
$durationText = '{0:00}:{1:00}:{2:00}:{3:00}' -f $duration.Days, $duration.Hours, $duration.Minutes, $duration.Seconds

$counters = Get-DotNetTool 'dotnet-counters'
$counterList = 'System.Runtime[cpu-usage,threadpool-thread-count,threadpool-queue-length,threadpool-completed-items-count,monitor-lock-contention-count,gen-0-gc-count,alloc-rate]'
$counterArgs = @('collect', '--process-id', $ProcessId, '--refresh-interval', '1', '--format', 'csv', '--output', $countersPath, '--duration', $durationText, '--counters', $counterList)

Write-Host "Capturing '$Label' from process $ProcessId for $DurationSeconds s. Run the workflow now."
$countersJob = Start-Process -FilePath $counters -ArgumentList $counterArgs -PassThru -NoNewWindow

if (-not $SkipTrace) {
    $trace = Get-DotNetTool 'dotnet-trace'
    # Sampled thread stacks (~100 Hz) plus runtime Contention (0x4000) and Threading (0x10000)
    # events at verbose level.
    $traceArgs = @('collect', '--process-id', $ProcessId, '--profile', 'dotnet-sampled-thread-time', '--providers', 'Microsoft-Windows-DotNETRuntime:0x14000:5', '--duration', $durationText, '--output', $tracePath)
    & $trace @traceArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        $countersJob.WaitForExit()
        throw "dotnet-trace failed with exit code $LASTEXITCODE."
    }
}

$countersJob.WaitForExit()
if ($countersJob.ExitCode -ne 0) {
    throw "dotnet-counters failed with exit code $($countersJob.ExitCode)."
}

Write-Host "Counters: $countersPath"
if (-not $SkipTrace) {
    Write-Host "Trace:    $tracePath"
}
