# Shared helpers for the guided smoke scripts (smoke-audit.ps1, smoke-backlog.ps1).
# Dot-source it after setting $Repo, $MediaDir and $SmokeName:
#     $SmokeName = 'smoke'; . (Join-Path $PSScriptRoot 'smoke-common.ps1')
# It creates the results folder in work\fatzvideo\test-results (FatzBomb-<SmokeName>-<date>) and defines the UI,
# player, measurement and media helpers.
# ---------------------------------------------------------------------------------------------
# Native helpers (C# 5, so Windows PowerShell 5.1 can compile them too)
# ---------------------------------------------------------------------------------------------
if (-not ('FbSmoke' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class FbSmoke
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
        public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
    }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    public static ulong BytesRead(Process p)
    {
        IO_COUNTERS c;
        return GetProcessIoCounters(p.Handle, out c) ? c.ReadTransferCount : 0;
    }

    // Pings the process's main window until the process exits and times each reply: a slow
    // reply means the UI thread was blocked. Returns the longest reply first, then one
    // "at <s> s: <ms> ms" entry per reply of thresholdMs or more.
    public static string[] WatchUiUntilExit(int pid, int thresholdMs)
    {
        Process p = Process.GetProcessById(pid);
        Stopwatch total = Stopwatch.StartNew();
        long longest = 0;
        List<string> stalls = new List<string>();
        while (!p.HasExited)
        {
            p.Refresh();
            IntPtr h = p.MainWindowHandle;
            if (h == IntPtr.Zero) { Thread.Sleep(100); continue; }
            double at = total.Elapsed.TotalSeconds;
            Stopwatch sw = Stopwatch.StartNew();
            IntPtr r;
            SendMessageTimeout(h, 0, IntPtr.Zero, IntPtr.Zero, 0, 15000, out r);
            long ms = sw.ElapsedMilliseconds;
            if (ms > longest) longest = ms;
            if (ms >= thresholdMs) stalls.Add(string.Format("at {0:F1} s: UI blocked {1} ms", at, ms));
            Thread.Sleep(50);
        }
        stalls.Insert(0, longest.ToString());
        return stalls.ToArray();
    }
}
'@
}
[void][FbSmoke]::SetProcessDPIAware()

# ---------------------------------------------------------------------------------------------
# Output folder and results file
# ---------------------------------------------------------------------------------------------
$OutDir = Join-Path (Join-Path $PSScriptRoot '..\..\..\work\fatzvideo\test-results') ('FatzBomb-' + $SmokeName + '-' + (Get-Date -Format 'yyyyMMdd-HHmm'))
$LogsOut = Join-Path $OutDir 'logs'
$ShotsOut = Join-Path $OutDir 'screenshots'
New-Item -ItemType Directory -Force -Path $OutDir, $LogsOut, $ShotsOut | Out-Null
$ResultsFile = Join-Path $OutDir 'results.txt'

$LogDir = Join-Path $env:TEMP 'FatzMedia.Video'
$LogNames = @{ Wpf = 'player-debug.log'; Ava = 'player-avalonia-debug.log'; WinForms = 'player-winforms-debug.log' }
$PlayerNames = @{ Wpf = 'WPF player'; Ava = 'Avalonia player'; WinForms = 'WinForms player' }
$Dxcap = Join-Path $env:WINDIR 'System32\dxcap.exe'

# ---------------------------------------------------------------------------------------------
# Small UI helpers
# ---------------------------------------------------------------------------------------------
function Say([string]$Text) { Write-Host $Text }
function Say-Info([string]$Text) { Write-Host $Text -ForegroundColor DarkGray }
function Say-Warn([string]$Text) { Write-Host $Text -ForegroundColor Yellow }

function Say-Do([string[]]$Lines) {
    Write-Host ''
    Write-Host 'YOUR TURN:' -ForegroundColor Green
    $n = 1
    foreach ($line in $Lines) {
        Write-Host ('  {0}. {1}' -f $n, $line) -ForegroundColor Green
        $n++
    }
}

function Wait-Enter([string]$Prompt = 'Press Enter here when done') { [void](Read-Host $Prompt) }

function Ask-YesNo([string]$Question) {
    while ($true) {
        $a = (Read-Host "$Question (y/n)").Trim().ToLowerInvariant()
        if ($a -eq 'y' -or $a -eq 'yes') { return 'yes' }
        if ($a -eq 'n' -or $a -eq 'no') { return 'no' }
    }
}

function Ask-Choice([string]$Question, [string[]]$Options) {
    Write-Host $Question -ForegroundColor Cyan
    for ($i = 0; $i -lt $Options.Count; $i++) { Write-Host ('  {0}) {1}' -f ($i + 1), $Options[$i]) }
    while ($true) {
        $a = Read-Host 'Type the number'
        $n = 0
        if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $Options.Count) { return $Options[$n - 1] }
    }
}

function Add-Result([string]$Text) { Add-Content -Path $ResultsFile -Value $Text -Encoding UTF8 }

function Start-Step([int]$Number, [string]$Title, [string]$Why, [string]$Minutes) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host (" TEST {0}: {1}   (about {2} min)" -f $Number, $Title, $Minutes) -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Say-Info "Why: $Why"
    $a = (Read-Host 'Press Enter to start this test, or type S to skip it').Trim().ToLowerInvariant()
    Add-Result ''
    Add-Result ("== TEST {0}: {1} ==" -f $Number, $Title)
    if ($a -eq 's') { Add-Result 'skipped by the user'; return $false }
    return $true
}

function Need-Media([string[]]$Keys) {
    $missing = @($Keys | Where-Object { -not $Media[$_] })
    if ($missing.Count -gt 0) {
        Say-Warn ('Skipping: no file chosen for ' + ($missing -join ', '))
        Add-Result ('skipped: no file chosen for ' + ($missing -join ', '))
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------------------------
# Player helpers
# ---------------------------------------------------------------------------------------------
function Find-Exe([string]$Project, [string]$Name) {
    $root = Join-Path $Repo "$Project\bin\Release"
    $exe = Get-ChildItem -Path $root -Recurse -Filter "$Name.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'windows' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw "Could not find $Name.exe under $root. Build first (run without -SkipBuild)." }
    return $exe.FullName
}

function Start-Player([string]$Kind, [string[]]$PlayerArgs) {
    $exe = $Exe[$Kind]
    $quoted = @($PlayerArgs | Where-Object { $_ } | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    Say-Info ("Starting the {0}: {1}" -f $PlayerNames[$Kind], ($quoted -join ' '))
    if ($quoted.Count -gt 0) {
        $proc = Start-Process -FilePath $exe -ArgumentList ($quoted -join ' ') -WorkingDirectory (Split-Path $exe) -PassThru
    } else {
        $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    }
    $null = $proc.Handle   # keeps the exit code readable after the process ends
    return $proc
}

function Wait-Window($Process, [int]$TimeoutSeconds = 60) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($Process.HasExited) { Say-Warn 'The player closed by itself.'; return $false }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 300
    }
    Say-Warn 'The player window did not appear within 60 s.'
    return $false
}

function Stop-Player($Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    Say-Info 'Closing the player...'
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(20000)) {
        Say-Warn 'The player did not close within 20 s; ending it.'
        Add-Result '  NOTE: the player did not close within 20 s and was ended by the script'
        try { $Process.Kill() } catch { }
    }
    Start-Sleep -Seconds 1
}

function Save-RunLog([string]$Kind, [string]$Tag) {
    $src = Join-Path $LogDir $LogNames[$Kind]
    if (-not (Test-Path $src)) { return $null }
    $dst = Join-Path $LogsOut "$Tag.log"
    Copy-Item -Path $src -Destination $dst -Force
    return $dst
}

function Count-Log($LogFile, [string]$Pattern) {
    if (-not $LogFile) { return 0 }
    return @(Select-String -Path $LogFile -Pattern $Pattern).Count
}

function Add-LogExcerpt($LogFile, [string]$Pattern, [int]$Max = 30) {
    if (-not $LogFile) { Add-Result '  (no log file found)'; return }
    $lines = @(Select-String -Path $LogFile -Pattern $Pattern | ForEach-Object { $_.Line })
    Add-Result ("  log lines matching [{0}]: {1}" -f $Pattern, $lines.Count)
    if ($lines.Count -le $Max) {
        $show = $lines
    } else {
        $half = [int]($Max / 2)
        $show = @($lines[0..($half - 1)]) + @("... ($($lines.Count - $Max) lines left out) ...") + @($lines[($lines.Count - $half)..($lines.Count - 1)])
    }
    foreach ($l in $show) { Add-Result ('    ' + $l) }
}

function Add-LogTail($LogFile, [int]$Count = 15) {
    if (-not $LogFile) { return }
    Add-Result "  last $Count log lines:"
    foreach ($l in @(Get-Content -Path $LogFile -Tail $Count)) { Add-Result ('    ' + $l) }
}

function Measure-Usage($Process, [int]$Seconds) {
    $Process.Refresh()
    $cpu0 = $Process.TotalProcessorTime
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $gpu = @{}
    try {
        $samples = Get-Counter -Counter ("\GPU Engine(pid_{0}_*)\Utilization Percentage" -f $Process.Id) -SampleInterval 1 -MaxSamples $Seconds -ErrorAction Stop
        $perType = @{}
        foreach ($s in $samples) {
            $sums = @{}
            foreach ($c in $s.CounterSamples) {
                if ($c.InstanceName -match 'engtype_(.+)$') {
                    $t = $Matches[1]
                    if (-not $sums.ContainsKey($t)) { $sums[$t] = 0.0 }
                    $sums[$t] += $c.CookedValue
                }
            }
            foreach ($t in $sums.Keys) {
                if (-not $perType.ContainsKey($t)) { $perType[$t] = New-Object System.Collections.Generic.List[double] }
                $perType[$t].Add($sums[$t])
            }
        }
        foreach ($t in $perType.Keys) { $gpu[$t] = [math]::Round(($perType[$t] | Measure-Object -Average).Average, 1) }
    } catch {
        $left = $Seconds - $sw.Elapsed.TotalSeconds
        if ($left -gt 0) { Start-Sleep -Seconds ([int][math]::Ceiling($left)) }
    }
    $Process.Refresh()
    $cpuMs = ($Process.TotalProcessorTime - $cpu0).TotalMilliseconds
    $wallMs = $sw.Elapsed.TotalMilliseconds
    $gpuText = 'n/a'
    if ($gpu.Count -gt 0) {
        $gpuText = (@($gpu.Keys | Where-Object { $gpu[$_] -ge 0.5 } | Sort-Object | ForEach-Object { '{0} {1}%' -f $_, $gpu[$_] }) -join ', ')
        if (-not $gpuText) { $gpuText = 'idle' }
    }
    return [pscustomobject]@{
        CpuPercent = [math]::Round($cpuMs / $wallMs / [Environment]::ProcessorCount * 100, 1)
        CoresUsed  = [math]::Round($cpuMs / $wallMs, 2)
        Gpu        = $gpuText
    }
}

function Save-WindowShot($Process, [string]$Path) {
    $Process.Refresh()
    $h = $Process.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { return }
    $r = New-Object 'FbSmoke+RECT'
    [void][FbSmoke]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    if ($w -le 0 -or $hh -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Invoke-ResizeStorm($Process) {
    $Process.Refresh()
    $h = $Process.MainWindowHandle
    [void][FbSmoke]::ShowWindow($h, 9)          # restore
    [void][FbSmoke]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
    $r = New-Object 'FbSmoke+RECT'
    [void][FbSmoke]::GetWindowRect($h, [ref]$r)
    $x = $r.Left; $y = $r.Top; $w0 = [math]::Max(800, $r.Right - $r.Left); $h0 = [math]::Max(500, $r.Bottom - $r.Top)
    for ($i = 0; $i -lt 100; $i++) {
        $f = 0.55 + 0.45 * [math]::Abs([math]::Sin($i / 7.0))
        [void][FbSmoke]::MoveWindow($h, $x, $y, [int]($w0 * $f), [int]($h0 * $f), $true)
        Start-Sleep -Milliseconds 80
    }
    [void][FbSmoke]::MoveWindow($h, $x, $y, $w0, $h0, $true)
    for ($k = 0; $k -lt 2; $k++) {
        [void][FbSmoke]::ShowWindow($h, 3); Start-Sleep -Milliseconds 1500   # maximize
        [void][FbSmoke]::ShowWindow($h, 9); Start-Sleep -Milliseconds 1500   # restore
    }
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-Dxcap {
    if (Test-Path $Dxcap) { return $true }
    Say-Warn 'The GPU reset tool (dxcap, from the Windows "Graphics Tools" feature) is not installed.'
    if ((Ask-YesNo 'Install it now? A Windows admin prompt will appear; it takes a minute or two') -eq 'no') { return $false }
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile -Command "Add-WindowsCapability -Online -Name Tools.Graphics.DirectX~~~~0.0.1.0"'
    return (Test-Path $Dxcap)
}

function Invoke-Tdr {
    if (Test-IsAdmin) { & $Dxcap -forcetdr | Out-Null }
    else { Start-Process -FilePath $Dxcap -ArgumentList '-forcetdr' -Verb RunAs -Wait -WindowStyle Hidden }
}

# ---------------------------------------------------------------------------------------------
# Media selection
# ---------------------------------------------------------------------------------------------
function Select-Media([string]$Label, [string[]]$Extensions, [string[]]$Hints, [scriptblock]$Filter, [string]$Exclude) {
    Write-Host ''
    Write-Host "Choose: $Label" -ForegroundColor Cyan
    $files = @(Get-ChildItem -Path $MediaDir -File -Recurse -Depth 2 -ErrorAction SilentlyContinue |
        Where-Object { $Extensions -contains $_.Extension.ToLowerInvariant() -and $_.FullName -ne $Exclude })
    if ($Filter) { $files = @($files | Where-Object $Filter) }
    $files = @($files | Sort-Object @{ Expression = {
                $n = $_.Name.ToLowerInvariant(); $score = 0
                foreach ($hint in $Hints) { if ($n.Contains($hint)) { $score-- } }
                $score } }, Name | Select-Object -First 12)
    for ($i = 0; $i -lt $files.Count; $i++) {
        Write-Host ('  {0,2}) {1}  ({2:N0} MB)' -f ($i + 1), $files[$i].FullName, ($files[$i].Length / 1MB))
    }
    if ($files.Count -eq 0) { Say-Warn "  No matching files found in $MediaDir." }
    while ($true) {
        $a = (Read-Host 'Type a number, paste a full path, Enter for 1, or S to skip').Trim().Trim('"')
        if ($a -eq '' -and $files.Count -gt 0) { return $files[0].FullName }
        if ($a.ToLowerInvariant() -eq 's') { return $null }
        $n = 0
        if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $files.Count) { return $files[$n - 1].FullName }
        if ($a -and (Test-Path -LiteralPath $a)) { return (Resolve-Path -LiteralPath $a).Path }
        Say-Warn '  Not understood, try again.'
    }
}

