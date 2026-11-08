<#
.SYNOPSIS
    Measures a running program's CPU, memory and GPU use once a second, for comparing apps
    (for example a LibVLC app against a MediaControl player playing the same video).

.DESCRIPTION
    Start the program and get it into the state to measure (video playing, screens open), then run
    this script with the process name or id. It samples once a second for -Seconds and writes a CSV
    and a summary (average and maximum) to work\fatzvideo\test-results. Nothing is changed on the program.

    Columns: CPU as % of all cores and as cores used, working set and private memory (MB), threads,
    handles, GPU utilization (all engines, %) and GPU dedicated and shared memory (MB). The GPU columns
    use Windows' "GPU Engine" and "GPU Process Memory" counters; they read empty when the counters are
    not available.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\measure-process.ps1 -Name TriviaBomb -Seconds 60 -Label trivia-1080p

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\measure-process.ps1 -Name FatzBomb.MediaControl.Player -Seconds 60 -Label player-1080p
#>
param(
    [string]$Name,
    [int]$Id = 0,
    [int]$Seconds = 60,
    [string]$Label = 'measure'
)

$ErrorActionPreference = 'Stop'

if ($Id -gt 0) {
    $proc = Get-Process -Id $Id
} elseif ($Name) {
    $found = @(Get-Process -Name ($Name -replace '\.exe$', '') -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { throw "No running process named '$Name'. Start it first, or pass -Id." }
    if ($found.Count -gt 1) {
        $found | Format-Table Id, ProcessName, @{ n = 'WorkingSetMB'; e = { [math]::Round($_.WorkingSet64 / 1MB) } }, MainWindowTitle | Out-Host
        throw "More than one '$Name' is running: pass -Id with one of the ids above."
    }
    $proc = $found[0]
} else {
    throw 'Pass -Name (process name, without .exe) or -Id.'
}

$pidText = $proc.Id
$cores = [Environment]::ProcessorCount
$desktop = Join-Path $PSScriptRoot '..\..\work\fatzvideo\test-results'
New-Item -ItemType Directory -Force -Path $desktop | Out-Null
if (-not $desktop) { $desktop = [IO.Path]::GetTempPath() }
$outDir = Join-Path $desktop ("FatzBomb-measure-{0}-{1}" -f $Label, (Get-Date -Format 'yyyyMMdd-HHmm'))
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$csv = Join-Path $outDir "$Label.csv"

Write-Host ("Measuring {0} (id {1}) for {2} s on {3} logical cores; keep it in the state you want measured." -f $proc.ProcessName, $pidText, $Seconds, $cores) -ForegroundColor Cyan

$gpuPaths = @(
    "\GPU Engine(pid_$($pidText)_*)\Utilization Percentage",
    "\GPU Process Memory(pid_$($pidText)_*)\Dedicated Usage",
    "\GPU Process Memory(pid_$($pidText)_*)\Shared Usage"
)
$gpuAvailable = $true

$rows = New-Object System.Collections.Generic.List[object]
$proc.Refresh()
$lastCpu = $proc.TotalProcessorTime
$lastTime = [Diagnostics.Stopwatch]::StartNew()

for ($i = 1; $i -le $Seconds; $i++) {
    $gpuUtil = $null; $gpuDedicated = $null; $gpuShared = $null
    if ($gpuAvailable) {
        try {
            # Get-Counter takes about a second for rate counters, which also paces the loop.
            $samples = (Get-Counter -Counter $gpuPaths -ErrorAction Stop).CounterSamples
            $gpuUtil = [math]::Round((($samples | Where-Object Path -like '*utilization percentage' | Measure-Object CookedValue -Sum).Sum), 1)
            $gpuDedicated = [math]::Round((($samples | Where-Object Path -like '*dedicated usage' | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
            $gpuShared = [math]::Round((($samples | Where-Object Path -like '*shared usage' | Measure-Object CookedValue -Sum).Sum) / 1MB, 1)
        } catch {
            $gpuAvailable = $false
            Write-Host "GPU counters not available ($($_.Exception.Message)); measuring CPU and memory only." -ForegroundColor Yellow
        }
    }
    if (-not $gpuAvailable) { Start-Sleep -Milliseconds 1000 }

    if ($proc.HasExited) { Write-Host 'The program exited; stopping.' -ForegroundColor Yellow; break }
    $proc.Refresh()
    $cpu = $proc.TotalProcessorTime
    $elapsed = $lastTime.Elapsed.TotalSeconds
    $lastTime.Restart()
    $coresUsed = if ($elapsed -gt 0) { ($cpu - $lastCpu).TotalSeconds / $elapsed } else { 0 }
    $lastCpu = $cpu

    $row = [pscustomobject]@{
        Second        = $i
        CpuPercent    = [math]::Round(100 * $coresUsed / $cores, 2)
        CoresUsed     = [math]::Round($coresUsed, 3)
        WorkingSetMB  = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        PrivateMB     = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        Threads       = $proc.Threads.Count
        Handles       = $proc.HandleCount
        GpuPercent    = $gpuUtil
        GpuDedicatedMB = $gpuDedicated
        GpuSharedMB   = $gpuShared
    }
    $rows.Add($row)
    if ($i % 10 -eq 0) { Write-Host ("  {0} s: CPU {1}% ({2} cores), working set {3} MB, GPU {4}%" -f $i, $row.CpuPercent, $row.CoresUsed, $row.WorkingSetMB, $row.GpuPercent) }
}

$rows | Export-Csv -Path $csv -NoTypeInformation

# The first sample includes start-up of the measurement; leave it out of the summary when there are enough.
$use = if ($rows.Count -gt 5) { $rows | Select-Object -Skip 1 } else { $rows }
$summary = foreach ($column in 'CpuPercent', 'CoresUsed', 'WorkingSetMB', 'PrivateMB', 'Threads', 'Handles', 'GpuPercent', 'GpuDedicatedMB', 'GpuSharedMB') {
    $values = @($use | ForEach-Object { $_.$column } | Where-Object { $null -ne $_ })
    if ($values.Count -eq 0) { continue }
    $m = $values | Measure-Object -Average -Maximum -Minimum
    [pscustomobject]@{ Measure = $column; Average = [math]::Round($m.Average, 2); Min = $m.Minimum; Max = $m.Maximum }
}

$header = "{0} (id {1}), {2} samples, {3} logical cores, {4}" -f $proc.ProcessName, $pidText, $use.Count, $cores, (Get-Date -Format 'yyyy-MM-dd HH:mm')
$summaryFile = Join-Path $outDir "$Label-summary.txt"
$lines = @($header, '', ('{0,-16} {1,10} {2,10} {3,10}' -f 'Measure', 'Average', 'Min', 'Max'))
foreach ($s in $summary) { $lines += ('{0,-16} {1,10} {2,10} {3,10}' -f $s.Measure, $s.Average, $s.Min, $s.Max) }
$lines | Set-Content -Path $summaryFile
Write-Host ''
Get-Content $summaryFile | Out-Host
Write-Host "Saved: $summaryFile and $csv" -ForegroundColor Green
