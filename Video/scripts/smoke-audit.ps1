<#
.SYNOPSIS
    Guided smoke tests for the October 2026 code audit of FatzMedia.Video.

.DESCRIPTION
    Walks you through each test one at a time. The script builds the players, starts them,
    measures CPU/GPU, forces GPU resets, resizes windows, takes screenshots and reads the logs
    itself. You only follow the short on-screen instructions and answer a few questions.

    Everything is saved to a folder in work\fatzvideo\test-results (FatzBomb-smoke-<date>), and at the end the
    results are copied to your clipboard so you can paste them into the chat.

    Run it from the repository folder in any PowerShell window (Windows PowerShell 5.1 or 7):

        powershell -ExecutionPolicy Bypass -File scripts\smoke-audit.ps1

    Optional parameters:
        -Repo       repo folder              (default: the folder above this script)
        -MediaDir   folder with your media   (default C:\media)
        -Steps      which tests, e.g. "1,2,3" or "all" (default: asks you)
        -SkipBuild  do not rebuild first

    Tip: run PowerShell "as administrator" to avoid a Windows prompt at each GPU reset in test 3.
#>
[CmdletBinding()]
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$MediaDir = 'C:\media',
    [string]$Steps = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$SmokeName = 'smoke'
. (Join-Path $PSScriptRoot 'smoke-common.ps1')

# ---------------------------------------------------------------------------------------------
# The tests
# ---------------------------------------------------------------------------------------------
function Measure-PlayerRun([string]$Name, [string[]]$PlayerArgs, [int]$Players) {
    Write-Host ''
    Write-Host "--- Run: $Name ---" -ForegroundColor Cyan
    $p = Start-Player 'Wpf' $PlayerArgs
    if (-not (Wait-Window $p)) { Add-Result "$Name : the player did not start"; return }
    if ($Players -gt 1) {
        Set-Clipboard -Value $PlayerArgs[-1]
        Say-Do @('In player 2 (the right-hand one): click its Open button (folder icon).',
            'Press Ctrl+V (the clip path is on your clipboard), then Enter. Press Play if it does not start.',
            'Come back to this window.')
        Wait-Enter 'Press Enter here once both players are playing'
    }
    Say 'Letting it settle for 10 s...'
    Start-Sleep -Seconds 10
    Say-Warn 'Measuring for 30 s. Please do not touch anything...'
    $u = Measure-Usage $p 30
    Say-Do @('Look at the stats line under the video (it reads "... fps | CPU ...").')
    if ($Players -gt 1) { $fps = Read-Host 'Type the fps of player 1 and player 2 (for example 59.9 / 59.8)' }
    else { $fps = Read-Host 'Type the fps shown (for example 59.9)' }
    $stutter = Ask-YesNo 'Did you see stutter or dropped frames during the run?'
    Stop-Player $p
    Add-Result ('{0}: CPU {1}% of all cores ({2} cores busy), GPU {3}, fps {4}, stutter {5}' -f $Name, $u.CpuPercent, $u.CoresUsed, $u.Gpu, $fps, $stutter)
}

function Test-1 {
    if (-not (Start-Step 1 '047A: cost of the WPF CPU copy' 'Decides whether the 047A spike (a GPU surface for WPF composited mode) is worth doing.' '8')) { return }
    if (-not (Need-Media @('v4k', 'v1080'))) { return }
    Say 'The script plays each clip in the WPF player four times and measures CPU and GPU itself.'
    Say 'You only type the fps at the end of each run.'
    Measure-PlayerRun '4K, native window (default)' @($Media.v4k) 1
    Measure-PlayerRun '4K, frame sink (CPU copy)' @('--surface=framesink', $Media.v4k) 1
    Measure-PlayerRun '1080p, native window' @($Media.v1080) 1
    Measure-PlayerRun '1080p, frame sink (CPU copy)' @('--surface=framesink', $Media.v1080) 1
    if ((Ask-YesNo 'Also measure two 4K players side by side? (you open the clip in player 2 yourself)') -eq 'yes') {
        Measure-PlayerRun '4K x2 players, native window' @('--players=2', $Media.v4k) 2
        Measure-PlayerRun '4K x2 players, frame sink' @('--players=2', '--surface=framesink', $Media.v4k) 2
    }
}

function Test-2 {
    if (-not (Start-Step 2 'Monitor refresh-rate query' 'Checks whether the refresh-rate query fails and silently falls back to 60 Hz (likely cause of ENG-018).' '1')) { return }
    $fields = 'public uint Size; public R RefreshRate; public ulong RefreshPeriodQpc; public R ComposeRate; public ulong VBlankQpc; public ulong RefreshCount; public uint DxRefreshCount; public ulong ComposeQpc; public ulong FrameCount; public uint DxPresentCount; public ulong FrameRefreshCount; public ulong SubmittedFrameCount; public uint SubmittedDxPresentCount; public ulong ConfirmedFrameCount; public uint ConfirmedDxPresentCount; public ulong ConfirmedRefreshCount; public uint ConfirmedDxRefreshCount; public ulong LateFrameCount; public uint OutstandingFrameCount; public ulong DisplayedFrameCount; public ulong DisplayedFrameQpc; public ulong DisplayedFrameRefreshCount; public ulong CompletedFrameCount; public ulong CompletedFrameQpc; public ulong PendingFrameCount; public ulong PendingFrameQpc; public ulong DisplayedFramesCount; public ulong CompletedFramesCount; public ulong PendingFramesCount; public ulong AvailableFramesCount; public ulong DroppedFrameCount; public ulong MissedFrameCount; public ulong NextDisplayedRefreshCount; public ulong NextPresentedRefreshCount; public ulong DisplayedRefreshesCount; public ulong PresentedRefreshesCount; public ulong StartedRefreshCount; public ulong ReceivedPixelsCount; public ulong DrawnPixelsCount; public ulong EmptyBuffersCount;'
    if (-not ('FbDwmProbe' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class FbDwmProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct R { public uint N; public uint D; }
    [StructLayout(LayoutKind.Sequential)] public struct AsShipped { $fields }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Packed { $fields }
    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr h, ref AsShipped t);
    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr h, ref Packed t);
    public static string Run()
    {
        AsShipped a = new AsShipped(); a.Size = (uint)Marshal.SizeOf(typeof(AsShipped));
        int ha = DwmGetCompositionTimingInfo(IntPtr.Zero, ref a);
        Packed b = new Packed(); b.Size = (uint)Marshal.SizeOf(typeof(Packed));
        int hb = DwmGetCompositionTimingInfo(IntPtr.Zero, ref b);
        return string.Format("As shipped: size {0}, HRESULT 0x{1:X8}, refresh {2}/{3}\r\nPack=1:     size {4}, HRESULT 0x{5:X8}, refresh {6}/{7}",
            Marshal.SizeOf(typeof(AsShipped)), ha, a.RefreshRate.N, a.RefreshRate.D,
            Marshal.SizeOf(typeof(Packed)), hb, b.RefreshRate.N, b.RefreshRate.D);
    }
}
"@
    }
    $text = [FbDwmProbe]::Run()
    Say $text
    foreach ($l in ($text -split "`r`n")) { Add-Result $l }
    foreach ($vc in @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)) {
        Add-Result ('Windows reports: {0}, current refresh {1} Hz' -f $vc.Name, $vc.CurrentRefreshRate)
    }
    Say 'Done: nothing for you to do here.'
}

function Invoke-TdrRun([string]$Kind, [string]$Tag, [string]$Label, [string[]]$PlayerArgs, [string]$What) {
    Write-Host ''
    Write-Host "--- $Tag : $Label ---" -ForegroundColor Cyan
    $p = Start-Player $Kind $PlayerArgs
    if (-not (Wait-Window $p)) { Add-Result "$Tag $Label : the player did not start"; return }
    Say "Playing for 10 s (press Play in the player if it has not started)..."
    Start-Sleep -Seconds 10
    Say-Warn 'Forcing a GPU reset now: your screens go black for a moment. Approve the Windows prompt if one appears.'
    Invoke-Tdr
    Say 'Waiting 20 s to see whether the picture comes back...'
    Start-Sleep -Seconds 20
    $pic = Ask-Choice "What does the $What area show now?" @('Playing normally', 'Black', 'Frozen picture (not moving)', 'Something else')
    $sound = Ask-YesNo 'Is the sound still playing?'
    Stop-Player $p
    $log = Save-RunLog $Kind $Tag
    Add-Result ('{0} {1}: picture = {2}, sound = {3}' -f $Tag, $Label, $pic, $sound)
    Add-Result ("  counts: 'GPU device recovered' {0}, 'Hardware background copy failed' {1}, 'DeviceLost' {2}, 'Vulkan device:' {3}, 'exported' {4}" -f `
        (Count-Log $log 'GPU device recovered'), (Count-Log $log 'Hardware background copy failed'), (Count-Log $log 'DeviceLost'), (Count-Log $log 'Vulkan device:'), (Count-Log $log 'exported'))
    Add-LogExcerpt $log 'DeviceLost|recover|restored|background copy failed|cannot be drawn|Vulkan device:|exported|Decoder\.' 30
    Start-Sleep -Seconds 5
}

function Test-3 {
    if (-not (Start-Step 3 'GPU reset during video' 'Checks whether playback recovers after a GPU reset (Direct3D11 and Vulkan).' '6')) { return }
    if (-not (Need-Media @('v1080'))) { return }
    if (-not (Ensure-Dxcap)) { Add-Result 'skipped: dxcap not installed'; return }
    Say-Warn 'This test resets the GPU four times: every screen flickers black for 1-2 s each time.'
    Say-Warn 'Save your work in other apps first.'
    Wait-Enter 'Press Enter when ready'
    Invoke-TdrRun 'Wpf' '3a' 'WPF, Direct3D11 native window, hardware decoding' @($Media.v1080) 'video'
    Invoke-TdrRun 'Ava' '3b' 'Avalonia, Direct3D11 shared texture' @($Media.v1080) 'video'
    if ($Media.cdg) { Invoke-TdrRun 'Wpf' '3c' 'WPF, CD+G only (comparison)' @($Media.cdg) 'CD+G graphics' }
    Invoke-TdrRun 'Ava' '3d' 'Avalonia, Vulkan' @('--renderer=vulkan', $Media.v1080) 'video'
}

function Test-4 {
    if (-not (Start-Step 4 'Resize while video plays' 'Checks for black flashes while resizing and counts the noisy export log lines.' '3')) { return }
    if (-not (Need-Media @('v1080'))) { return }
    $runs = @(
        @{ Kind = 'Ava'; Tag = '4a'; Label = 'Avalonia, Direct3D11 shared texture'; Args = @($Media.v1080) },
        @{ Kind = 'Wpf'; Tag = '4b'; Label = 'WPF, frame sink'; Args = @('--surface=framesink', $Media.v1080) })
    foreach ($run in $runs) {
        Write-Host ''
        Write-Host ("--- {0} : {1} ---" -f $run.Tag, $run.Label) -ForegroundColor Cyan
        $p = Start-Player $run.Kind $run.Args
        if (-not (Wait-Window $p)) { Add-Result ('{0}: the player did not start' -f $run.Tag); continue }
        Start-Sleep -Seconds 8
        Say-Do @('Watch the video. The script will now resize the window for about 15 s.', 'Look for black flashes in the picture.')
        Wait-Enter 'Press Enter to start the resizing'
        Invoke-ResizeStorm $p
        $flash = Ask-Choice 'Did the picture flash black while it resized?' @('No flashes', 'A few flashes', 'Many flashes (almost every step)')
        Stop-Player $p
        $log = Save-RunLog $run.Kind $run.Tag
        Add-Result ('{0} {1}: black flashes = {2}; export log lines = {3}; frame-dropped lines = {4}' -f $run.Tag, $run.Label, $flash,
            (Count-Log $log 'Shared textures exported|Shared GPU images exported'), (Count-Log $log 'frame dropped'))
    }
}

function Test-5 {
    if (-not (Start-Step 5 'CD+G look per renderer' 'Direct3D11, Vulkan and Software draw the start of a CD+G differently; you decide which is right.' '3')) { return }
    if (-not (Need-Media @('cdg'))) { return }
    $runs = @(
        @{ Tag = 'direct3d11'; Args = @('--renderer=direct3d11', $Media.cdg) },
        @{ Tag = 'vulkan'; Args = @('--renderer=vulkan', $Media.cdg) },
        @{ Tag = 'software'; Args = @('--software', $Media.cdg) })
    Say 'The script opens the same CD+G three times and takes screenshots during the first seconds.'
    Say 'Just watch the start of each one.'
    foreach ($run in $runs) {
        Write-Host ''
        Write-Host ("--- {0} ---" -f $run.Tag) -ForegroundColor Cyan
        $p = Start-Player 'Ava' $run.Args
        if (-not (Wait-Window $p)) { Add-Result ('{0}: the player did not start' -f $run.Tag); continue }
        foreach ($s in 1..6) {
            Start-Sleep -Seconds 1
            try { Save-WindowShot $p (Join-Path $ShotsOut ('cdg-{0}-{1}s.png' -f $run.Tag, $s)) } catch { Say-Warn ('Screenshot failed: ' + $_.Exception.Message) }
        }
        Start-Sleep -Seconds 4
        Stop-Player $p
        [void](Save-RunLog 'Ava' ('5-' + $run.Tag))
    }
    Say 'Opening the screenshots folder so you can compare them.'
    Invoke-Item $ShotsOut
    $same = Ask-Choice 'Did the three renderers look the same during the first seconds?' @('Same', 'Different (one showed a coloured or black area the others did not)', 'Not sure')
    $choice = Ask-Choice 'Before the CD+G sets its first background colour, what should show?' @(
        'Transparent: show what is behind it (the background or black) - what Core intends',
        'Opaque: draw the CD+G colour - what Direct3D11 and Software do today',
        'No preference / not sure')
    Add-Result "Renderers looked: $same"
    Add-Result "Decision: $choice"
    Add-Result "Screenshots: $ShotsOut (please attach a few to the chat if they differ)"
}

function Test-6 {
    if (-not (Start-Step 6 'Silence trim + background video at the loop point' 'Checks whether looping a song with a background video stalls audio for every player on the device.' '6')) { return }
    if (-not (Need-Media @('song', 'song2', 'v1080'))) { return }
    $t = Join-Path $env:TEMP 'fb-smoke-bg'
    New-Item -ItemType Directory -Force -Path $t | Out-Null
    $loopSong = Join-Path $t ('loop' + [IO.Path]::GetExtension($Media.song))
    Copy-Item -LiteralPath $Media.song -Destination $loopSong -Force
    Copy-Item -LiteralPath $Media.v1080 -Destination (Join-Path $t ('loop' + [IO.Path]::GetExtension($Media.v1080))) -Force
    foreach ($trim in @($true, $false)) {
        $label = 'without --trim-silence (comparison)'
        $playerArgs = @('--players=2', $loopSong)
        if ($trim) { $label = 'with --trim-silence'; $playerArgs = @('--players=2', '--trim-silence', $loopSong) }
        Write-Host ''
        Write-Host "--- $label ---" -ForegroundColor Cyan
        $p = Start-Player 'Wpf' $playerArgs
        if (-not (Wait-Window $p)) { Add-Result "$label : the player did not start"; continue }
        Set-Clipboard -Value $Media.song2
        Say-Do @(
            'Player 1 (left) plays the song with the video behind it. Click its Loop button (two circling arrows).',
            'Player 2 (right): click Open (folder icon), press Ctrl+V, Enter. Press Play if needed.',
            "Drag player 1's seek bar to about 10 s before the end, and listen to PLAYER 2 as player 1 loops back to the start.",
            'Do that 3 times.')
        Wait-Enter 'Press Enter here when done'
        $glitch = Ask-YesNo "Did player 2's sound drop out, stutter or click when player 1 looped?"
        $hitch = Ask-YesNo "Did player 1's video freeze, or start over, when the song looped? (Expected: it keeps playing and loops at its own end, ENG-051.)"
        Stop-Player $p
        $log = Save-RunLog 'Wpf' ('6-' + $(if ($trim) { 'trim' } else { 'notrim' }))
        Add-Result ("{0}: player 2 glitch = {1}, player 1 video hitch = {2}" -f $label, $glitch, $hitch)
        Add-LogExcerpt $log 'Loop|loop|trim|Trim|Background' 12
    }
}

function Test-7 {
    if (-not (Start-Step 7 'Background/karaoke controls freezing the UI' 'Times how long the UI freezes when a background or karaoke control lets go of its player.' '6')) { return }
    if (-not (Need-Media @('v1080', 'cdg'))) { return }
    foreach ($kind in @('Wpf', 'Ava', 'WinForms')) {
        Write-Host ''
        Write-Host ("--- {0} ---" -f $PlayerNames[$kind]) -ForegroundColor Cyan
        $p = Start-Player $kind @('--lightweight-surfaces')
        if (-not (Wait-Window $p)) { Add-Result ('{0}: the player did not start' -f $PlayerNames[$kind]); continue }
        Start-Sleep -Seconds 3
        Set-Clipboard -Value $Media.v1080
        Say-Do @('A second window opens: "Background and karaoke surfaces".',
            'In it, click Browse..., press Ctrl+V, Enter. Then click Background.')
        Wait-Enter 'Press Enter here when the background video plays'
        Set-Clipboard -Value $Media.cdg
        Say-Do @('Click Browse... again, press Ctrl+V, Enter. Then click Karaoke.')
        Wait-Enter 'Press Enter here when the karaoke plays'
        Say-Do @('The script now times the UI. Do these three things in order, waiting about 5 s between each:',
            '   a) click Colour',
            '   b) close the "Background and karaoke surfaces" window',
            '   c) close the player window')
        $res = [FbSmoke]::WatchUiUntilExit($p.Id, 250)
        $log = Save-RunLog $kind ('7-' + $kind)
        Add-Result ('{0}: longest UI freeze {1} ms' -f $PlayerNames[$kind], $res[0])
        if ($res.Count -gt 1) { foreach ($s in $res[1..($res.Count - 1)]) { Add-Result ('    ' + $s) } }
        else { Add-Result '    no freeze of 250 ms or more' }
        Say ('Longest freeze: {0} ms' -f $res[0])
    }
}

function Test-8 {
    if (-not (Start-Step 8 'Avalonia player: closing during a rebuild, and the second screen' 'Checks whether the Avalonia player exits with players still running, and whether --second-screen reopens on toolbar changes.' '6')) { return }
    if (-not (Need-Media @('v4k', 'v1080'))) { return }
    Write-Host ''
    Write-Host '--- 8a: close during a rebuild ---' -ForegroundColor Cyan
    $start = Get-Date
    $p = Start-Player 'Ava' @('--players=4')
    if (Wait-Window $p) {
        Set-Clipboard -Value $Media.v4k
        Say-Do @('In EACH of the 4 players: click Open (folder icon), press Ctrl+V, Enter. Press Play if needed.')
        Wait-Enter 'Press Enter here when all 4 are playing'
        Say-Do @('Switch to the player window.',
            'On the toolbar at the top, change "Players" to 1, and IMMEDIATELY press Alt+F4.')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $exited = $p.WaitForExit(90000)
        $code = 'still running'
        if ($exited) { $code = $p.ExitCode }
        Start-Sleep -Seconds 3
        $left = @(Get-Process -Name 'FatzMedia.Video.Sample.Player.Avalonia' -ErrorAction SilentlyContinue)
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start } -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -in @('Application Error', '.NET Runtime', 'Windows Error Reporting') -and $_.Message -match 'FatzMedia|FatzBomb' })
        $log = Save-RunLog 'Ava' '8a'
        Add-Result ('8a: exited = {0} after {1:N1} s, exit code = {2}, processes left over = {3}, crash events = {4}' -f $exited, $sw.Elapsed.TotalSeconds, $code, $left.Count, $events.Count)
        foreach ($e in $events) { Add-Result ('    event: ' + (($e.Message -split "`n")[0..3] -join ' | ')) }
        Add-LogTail $log 15
        foreach ($l in $left) { try { $l.Kill() } catch { } }
    } else { Add-Result '8a: the player did not start' }

    if ([System.Windows.Forms.Screen]::AllScreens.Count -lt 2) { Add-Result '8b: skipped (only one screen)'; return }
    foreach ($kind in @('Ava', 'Wpf')) {
        Write-Host ''
        Write-Host ("--- 8b: second screen, {0} ---" -f $PlayerNames[$kind]) -ForegroundColor Cyan
        $p = Start-Player $kind @('--second-screen=2', $Media.v1080)
        if (-not (Wait-Window $p)) { Add-Result ('8b {0}: the player did not start' -f $PlayerNames[$kind]); continue }
        Start-Sleep -Seconds 6
        $opened = Ask-YesNo 'Is the video showing full screen on your second monitor?'
        Say-Do @('If the second screen is open, close it (Escape on that screen, or untick "Second screen").',
            'On the toolbar at the top, change "Renderer" to a different one. Wait 5 s.')
        Wait-Enter
        $afterRenderer = Ask-YesNo 'Did the second screen open again by itself?'
        Say-Do @('Close the second screen again if it opened.',
            'On the toolbar, change "Players" to 2. Wait 5 s.')
        Wait-Enter
        $afterCount = Ask-YesNo 'Did the second screen open again by itself?'
        Stop-Player $p
        Add-Result ('8b {0}: opened at start = {1}; reopened after Renderer change = {2}; after Players change = {3}' -f $PlayerNames[$kind], $opened, $afterRenderer, $afterCount)
    }
}

function Test-9 {
    if (-not (Start-Step 9 'Karaoke lyrics memory churn (optional)' 'Measures garbage collections while MIDI/KAR lyrics wipe, against a plain MP3.' '3')) { return }
    if (-not (Need-Media @('kar', 'song'))) { return }
    $dc = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-counters.exe'
    if (-not (Test-Path $dc)) {
        Say 'Installing dotnet-counters (a Microsoft .NET tool)...'
        & dotnet tool install --global dotnet-counters | Out-Host
    }
    if (-not (Test-Path $dc)) { Add-Result 'skipped: dotnet-counters could not be installed'; return }
    foreach ($run in @(@{ Tag = 'kar'; File = $Media.kar }, @{ Tag = 'mp3'; File = $Media.song })) {
        Write-Host ''
        Write-Host ("--- {0} ---" -f $run.Tag) -ForegroundColor Cyan
        $p = Start-Player 'Wpf' @($run.File)
        if (-not (Wait-Window $p)) { Add-Result ('9 {0}: the player did not start' -f $run.Tag); continue }
        Start-Sleep -Seconds 10
        if ($run.Tag -eq 'kar') {
            $lyrics = Ask-YesNo 'Are lyrics showing with a colour wipe?'
            Add-Result "9 kar: lyrics with wipe = $lyrics"
        }
        Say-Warn 'Measuring for 30 s...'
        $csv = Join-Path $OutDir ("counters-{0}.csv" -f $run.Tag)
        & $dc collect -p $p.Id --counters System.Runtime --format csv -o $csv --duration 00:00:00:30 | Out-Null
        Stop-Player $p
        if (Test-Path $csv) {
            $lines = @(Get-Content $csv | Where-Object { $_ -match 'gen2|gen-2|gc\.collections|allocat|gc-heap|heap\.size|time_in_gc|time-in-gc' })
            Add-Result ('9 {0}: {1} counter rows (full file: {2})' -f $run.Tag, $lines.Count, $csv)
            foreach ($l in @($lines | Select-Object -Last 8)) { Add-Result ('    ' + $l) }
        } else { Add-Result ('9 {0}: no counter file was written' -f $run.Tag) }
    }
}

function Test-10 {
    if (-not (Start-Step 10 'Opening an MKV with text subtitles (optional)' 'Checks whether the whole MKV is read from disk while it opens.' '2')) { return }
    if (-not (Need-Media @('mkv'))) { return }
    $size = (Get-Item -LiteralPath $Media.mkv).Length
    $p = Start-Player 'Wpf' @($Media.mkv)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $readings = @()
    foreach ($at in @(2, 5, 10, 20)) {
        while ($sw.Elapsed.TotalSeconds -lt $at) { Start-Sleep -Milliseconds 200 }
        if ($p.HasExited) { break }
        $readings += ('{0} s: {1:N0} MB read' -f $at, ([FbSmoke]::BytesRead($p) / 1MB))
    }
    $seconds = Read-Host 'About how many seconds after the window appeared did the picture start? (type a number)'
    $subs = Ask-YesNo 'Does this file have subtitle tracks (SRT/ASS)?'
    Stop-Player $p
    [void](Save-RunLog 'Wpf' '10')
    Add-Result ('10: file size {0:N0} MB, has text subtitles = {1}, picture after about {2} s' -f ($size / 1MB), $subs, $seconds)
    foreach ($r in $readings) { Add-Result ('    ' + $r) }
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
Clear-Host
Write-Host 'FatzMedia.Video audit smoke tests' -ForegroundColor Cyan
Write-Host "Results folder: $OutDir"
if (-not (Test-Path (Join-Path $Repo 'FatzMedia.Video.sln'))) { throw "No FatzMedia.Video.sln in $Repo. Pass -Repo <folder>." }

# Git: show where we are; offer to move to the latest main.
$branch = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git -C $Repo rev-parse --short HEAD).Trim()
Say "Repo: $Repo  (branch $branch, commit $commit)"
if ((Ask-YesNo 'Switch to the latest main first? (recommended)') -eq 'yes') {
    if ((& git -C $Repo status --porcelain)) {
        Say-Warn 'You have uncommitted changes, so the script stays on the current branch.'
    } else {
        & git -C $Repo fetch origin | Out-Host
        & git -C $Repo checkout main | Out-Host
        & git -C $Repo pull --ff-only | Out-Host
        $branch = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
        $commit = (& git -C $Repo rev-parse --short HEAD).Trim()
    }
}

if (-not $SkipBuild) {
    Say 'Building the players (Release). This takes a minute...'
    & dotnet build (Join-Path $Repo 'FatzMedia.Video.sln') -c Release -v minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The build failed: see the output above.' }
}

$Exe = @{
    Wpf      = Find-Exe 'FatzMedia.Video.Sample.Player' 'FatzMedia.Video.Sample.Player'
    Ava      = Find-Exe 'FatzMedia.Video.Sample.Player.Avalonia' 'FatzMedia.Video.Sample.Player.Avalonia'
    WinForms = Find-Exe 'FatzMedia.Video.Sample.Player.WinForms' 'FatzMedia.Video.Sample.Player.WinForms'
}
foreach ($k in $Exe.Keys) {
    $dir = Split-Path $Exe[$k]
    $hasBass = [bool](Get-ChildItem -Path $dir -Recurse -Filter 'bass.dll' -ErrorAction SilentlyContinue | Select-Object -First 1)
    $hasFfmpeg = [bool](Get-ChildItem -Path $dir -Recurse -Filter 'avcodec*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($hasBass -and $hasFfmpeg)) {
        Say-Warn ("{0}: BASS found = {1}, FFmpeg found = {2}. Put the DLLs in its native\ folder and rebuild if a test fails." -f $PlayerNames[$k], $hasBass, $hasFfmpeg)
    }
}

# Which tests
$all = 1..10
if (-not $Steps) {
    Write-Host ''
    Write-Host 'Tests:' -ForegroundColor Cyan
    Write-Host '   1  047A: WPF CPU copy cost at 4K (the 047A decision)          ~8 min'
    Write-Host '   2  Monitor refresh-rate query (automatic)                      ~1 min'
    Write-Host '   3  GPU reset during video (screens flicker 4 times)            ~6 min'
    Write-Host '   4  Resize while video plays                                    ~3 min'
    Write-Host '   5  CD+G look per renderer (you decide transparent/opaque)      ~3 min'
    Write-Host '   6  Silence trim + background video at the loop point           ~6 min'
    Write-Host '   7  Background/karaoke controls freezing the UI                 ~6 min'
    Write-Host '   8  Avalonia closing during rebuild; second screen              ~6 min'
    Write-Host '   9  Karaoke lyrics memory churn (optional)                      ~3 min'
    Write-Host '  10  MKV with subtitles: opening cost (optional)                 ~2 min'
    $Steps = Read-Host 'Which tests? Enter = 1-8, "all" = 1-10, or a list like 1,2,3'
}
if (-not $Steps) { $selected = 1..8 }
elseif ($Steps.Trim().ToLowerInvariant() -eq 'all') { $selected = $all }
else {
    $selected = @($Steps -split '[,\s]+' | Where-Object { $_ } | ForEach-Object {
            if ($_ -match '^(\d+)-(\d+)$') { [int]$Matches[1]..[int]$Matches[2] } else { [int]$_ } })
}

# Media, asked once up front so the tests run without interruptions.
$need = @{}
$uses = @{ 1 = 'v4k', 'v1080'; 3 = 'v1080', 'cdg'; 4 = 'v1080'; 5 = 'cdg'; 6 = 'song', 'song2', 'v1080'; 7 = 'v1080', 'cdg'; 8 = 'v4k', 'v1080'; 9 = 'kar', 'song'; 10 = 'mkv' }
foreach ($s in $selected) { if ($uses.ContainsKey($s)) { foreach ($m in $uses[$s]) { $need[$m] = $true } } }
$Media = @{}
$videoExt = @('.mp4', '.mkv', '.mov', '.m4v', '.webm', '.avi', '.ts')
$audioExt = @('.mp3', '.flac', '.wav', '.m4a', '.ogg')
if ($need.Count -gt 0) { Write-Host ''; Write-Host "Pick the media files (from $MediaDir):" -ForegroundColor Cyan }
if ($need['v4k']) { $Media.v4k = Select-Media 'a 4K video (HEVC is best)' $videoExt @('4k', '2160', 'uhd', 'hevc') $null '' }
if ($need['v1080']) { $Media.v1080 = Select-Media 'a 1080p video' $videoExt @('1080', 'video', 'h264', 'hd') $null $Media.v4k }
if ($need['cdg']) { $Media.cdg = Select-Media 'a CD+G (.cdg with its .mp3 beside it)' @('.cdg') @() { Test-Path ([IO.Path]::ChangeExtension($_.FullName, '.mp3')) } '' }
if ($need['song']) { $Media.song = Select-Media 'a song, ideally short and ending in a second or more of silence' $audioExt @() $null '' }
if ($need['song2']) { $Media.song2 = Select-Media 'a second, different song' $audioExt @() $null $Media.song }
if ($need['kar']) { $Media.kar = Select-Media 'a MIDI/KAR with lyrics' @('.kar', '.mid', '.midi') @('kar') $null '' }
if ($need['mkv']) { $Media.mkv = Select-Media 'a large MKV with SRT/ASS subtitle tracks (on a slow disk if possible)' @('.mkv') @('sub', 'srt', 'ass') $null '' }

# Results header
$gpuNames = (@(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { '{0} (driver {1})' -f $_.Name, $_.DriverVersion }) -join '; ')
Add-Result 'FatzMedia.Video audit smoke results'
Add-Result ('Date: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Add-Result "Repo: branch $branch, commit $commit"
Add-Result "GPU: $gpuNames"
Add-Result ('Screens: {0}; Windows {1}; PowerShell {2}; {3} logical cores' -f [System.Windows.Forms.Screen]::AllScreens.Count, [Environment]::OSVersion.Version, $PSVersionTable.PSVersion, [Environment]::ProcessorCount)
foreach ($k in $Media.Keys) { Add-Result ('Media {0}: {1}' -f $k, $Media[$k]) }

foreach ($s in $selected) {
    try {
        switch ($s) {
            1 { Test-1 } 2 { Test-2 } 3 { Test-3 } 4 { Test-4 } 5 { Test-5 }
            6 { Test-6 } 7 { Test-7 } 8 { Test-8 } 9 { Test-9 } 10 { Test-10 }
            default { Say-Warn "There is no test $s." }
        }
    } catch {
        Say-Warn ("Test {0} hit an error: {1}" -f $s, $_.Exception.Message)
        Add-Result ("ERROR in test {0}: {1}" -f $s, $_.Exception.Message)
    }
}

Write-Host ''
Write-Host ('=' * 78) -ForegroundColor Green
Write-Host ' All done.' -ForegroundColor Green
Write-Host ('=' * 78) -ForegroundColor Green
Set-Clipboard -Value (Get-Content -Path $ResultsFile -Raw)
Say "Results: $ResultsFile"
Say 'The results are on your clipboard: paste them into the chat with Claude.'
Say 'If test 5 showed differences, also attach a few screenshots from the screenshots folder.'
Invoke-Item $OutDir
