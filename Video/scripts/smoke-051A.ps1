<#
.SYNOPSIS
    Guided smoke tests for stage 051A (FirstFrameReady, the composited UI-thread counter, no startup delay).

.DESCRIPTION
    Does the git and build steps for you, then runs each test: it starts the players with the right
    flags, reads their logs for the expected lines, maximizes windows, collects the composited
    UI-thread numbers (047A's before numbers) and writes PASS/FAIL per check. You only do what needs
    hands or eyes (dropping a file, watching the TriviaBomb swap); the script tells you exactly what.

    Everything is saved to a folder on your Desktop (FatzBomb-051A-<date>); at the end the results are
    copied to your clipboard so you can paste them into the chat.

    Run it from the repository folder, in PowerShell:

        cd C:\development\nuget\FatzbombMedia
        powershell -ExecutionPolicy Bypass -File scripts\smoke-051A.ps1

    Run it again on the laptop for the laptop numbers (test 3).

    Optional parameters:
        -Repo       repo folder             (default: the folder above this script)
        -MediaDir   folder with your media  (default C:\media)
        -Branch     branch to test          (default claude/zen-tesla-ux51s6)
        -Steps      which tests, e.g. "1,3" or "all" (default: asks you)
        -SkipGit    do not fetch, switch or pull
        -SkipBuild  do not rebuild first
        -Seconds    how long each measurement runs (default 30)
#>
[CmdletBinding()]
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$MediaDir = 'C:\media',
    [string]$Branch = 'claude/zen-tesla-ux51s6',
    [string]$Steps = '',
    [switch]$SkipGit,
    [switch]$SkipBuild,
    [int]$Seconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$SmokeName = '051A'
. (Join-Path $PSScriptRoot 'smoke-common.ps1')
$Totals = @{ Pass = 0; Fail = 0; Info = 0 }

# ---------------------------------------------------------------------------------------------
# Result and log helpers
# ---------------------------------------------------------------------------------------------
function Add-Check([string]$Name, [bool]$Pass, [string]$Detail = '') {
    $tag = if ($Pass) { 'PASS' } else { 'FAIL' }
    if ($Pass) { $Totals.Pass++ } else { $Totals.Fail++ }
    $text = "  [$tag] $Name" + $(if ($Detail) { " - $Detail" } else { '' })
    Add-Result $text
    if ($Pass) { Write-Host $text -ForegroundColor Green } else { Write-Host $text -ForegroundColor Red }
}

function Add-Info([string]$Text) {
    $Totals.Info++
    Add-Result "  [INFO] $Text"
    Write-Host "  $Text" -ForegroundColor Cyan
}

function Add-Answer([string]$Name, [string]$Question) {
    $a = Ask-YesNo $Question
    Add-Check $Name ($a -eq 'yes') "owner: $a"
}

# The live log of a running player (it is rewritten each time a player starts).
function Get-LiveLog([string]$Kind) { return (Join-Path $LogDir $LogNames[$Kind]) }

function Get-Lines([string]$LogFile, [string]$Text) {
    if (-not $LogFile -or -not (Test-Path $LogFile)) { return @() }
    return @(Get-Content -Path $LogFile | Where-Object { $_.Contains($Text) })
}

# Waits until the log has a line containing $Text; returns the seconds waited, or -1 on timeout.
function Wait-LogText([string]$Kind, [string]$Text, [int]$TimeoutSeconds = 30) {
    $log = Get-LiveLog $Kind
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (@(Get-Lines $log $Text).Count -gt 0) { return [Math]::Round($sw.Elapsed.TotalSeconds, 1) }
        Start-Sleep -Milliseconds 200
    }
    return -1
}

# Starts a player, removing its old log first. Returns the process, or $null when it did not start.
function Start-Fresh([string]$Kind, [string[]]$PlayerArgs) {
    Remove-Item -Path (Get-LiveLog $Kind) -ErrorAction SilentlyContinue
    $p = Start-Player $Kind $PlayerArgs
    if (-not (Wait-Window $p)) { return $null }
    return $p
}

# Averages the composited UI-thread readings of the [Stats] lines (one list per player label).
function Measure-UiThread([string[]]$StatsLines) {
    $byPlayer = @{}
    foreach ($line in $StatsLines) {
        if ($line -match '\[Stats\]\s+(\S+)\s.*?surface (\d+x\d+) via frame sink, UI thread ([\d.]+) ms/s \(([\d.]+) ms/frame\)') {
            $label = $Matches[1]
            if (-not $byPlayer.ContainsKey($label)) { $byPlayer[$label] = @() }
            $byPlayer[$label] += , @($Matches[2], [double]$Matches[3], [double]$Matches[4])
        }
    }
    $out = @()
    foreach ($label in ($byPlayer.Keys | Sort-Object)) {
        $r = $byPlayer[$label]
        $msPerSecond = ($r | ForEach-Object { $_[1] } | Measure-Object -Average).Average
        $msPerFrame = ($r | ForEach-Object { $_[2] } | Measure-Object -Average).Average
        $out += ('{0} {1}: {2:N1} ms/s ({3:N2} ms/frame), {4} readings' -f $label, $r[-1][0], $msPerSecond, $msPerFrame, $r.Count)
    }
    return $out
}

# Waits $Seconds while the player runs, then returns the [Stats] lines written meanwhile.
function Collect-Stats([string]$Kind, [int]$Wait) {
    $log = Get-LiveLog $Kind
    $before = @(Get-Lines $log '[Stats]').Count
    Say-Info "  Measuring for $Wait s, do not touch the player..."
    Start-Sleep -Seconds $Wait
    $all = @(Get-Lines $log '[Stats]')
    if ($all.Count -le $before) { return @() }
    return @($all[$before..($all.Count - 1)])
}

function Show-Explorer([string]$File) { Start-Process explorer.exe -ArgumentList "/select,`"$File`"" }

# ---------------------------------------------------------------------------------------------
# The tests
# ---------------------------------------------------------------------------------------------

# 1. The startup media shows at once and FirstFrameReady fires once (WPF, native and composited).
function Test-1 {
    if (-not (Start-Step 1 'Startup without delay, one first-frame line' 'The 500 ms startup delay is gone; FirstFrameReady fires once per load. Automatic.' '2')) { return }
    if (-not (Need-Media @('video'))) { return }
    foreach ($mode in @(@{ Name = 'native window'; Args = @() }, @{ Name = 'frame sink'; Args = @('--surface=framesink') })) {
        $p = Start-Fresh 'Wpf' ($mode.Args + @($Media.video))
        if (-not $p) { Add-Check "WPF $($mode.Name): player starts" $false; continue }
        $t = Wait-LogText 'Wpf' 'first frame ready' 30
        Start-Sleep -Seconds 3
        Stop-Player $p
        $log = Save-RunLog 'Wpf' ("test1-" + ($mode.Name -replace ' ', '-'))
        $lines = @(Get-Lines $log 'first frame ready')
        Add-Check "WPF $($mode.Name): one first-frame line" ($lines.Count -eq 1) ("found {0}; {1}" -f $lines.Count, $(if ($lines) { $lines[0] -replace '^.*\] Surface: ', '' } else { 'none' }))
        Add-Check "WPF $($mode.Name): the line names the path" (@($lines | Where-Object { $_.Contains("via $($mode.Name)") }).Count -eq 1)
        if ($t -ge 0) { Add-Info ("WPF {0}: first frame {1} s after the window appeared" -f $mode.Name, $t) }
    }
}

# 2. Once per load: pause, seek and loop do not raise it again; audio-only media never does.
function Test-2 {
    if (-not (Start-Step 2 'Once per load' 'Two video loads give two lines; pause, seek, loop and an MP3 give none. Guided: you drop files.' '3')) { return }
    if (-not (Need-Media @('video', 'song'))) { return }
    $p = Start-Fresh 'Wpf' @()
    if (-not $p) { Add-Check 'WPF player starts' $false; return }
    Show-Explorer $Media.video
    Say-Do @(
        "An Explorer window shows the video: drag $(Split-Path -Leaf $Media.video) onto the player's picture.",
        'Pause it (Space), click somewhere on the seek bar, play again (Space).',
        "Drag $(Split-Path -Leaf $Media.song) onto the picture (Explorer: $($Media.song)).",
        "Drag $(Split-Path -Leaf $Media.video) onto the picture again and let it play a few seconds.",
        'Leave the player open; the script closes it.')
    Wait-Enter
    Stop-Player $p
    $log = Save-RunLog 'Wpf' 'test2-once-per-load'
    $lines = @(Get-Lines $log 'first frame ready')
    Add-Check 'two first-frame lines (one per video load)' ($lines.Count -eq 2) "found $($lines.Count)"
    Add-Check 'no line for the MP3' (@($lines | Where-Object { $_.Contains((Split-Path -Leaf $Media.song)) }).Count -eq 0)
}

# 3. The composited UI-thread counter: 047A's before numbers.
function Test-3 {
    if (-not (Start-Step 3 'Composited UI-thread time (047A before numbers)' 'Reads the UI-thread ms/s and ms/frame of composited surfaces: one player at window size and maximized (automatic), then three players (you drop two files).' '5')) { return }
    if (-not (Need-Media @('video'))) { return }
    $power = [System.Windows.Forms.SystemInformation]::PowerStatus
    Add-Info ("machine {0}; power: {1}, battery {2:P0}" -f $env:COMPUTERNAME, $power.PowerLineStatus, $power.BatteryLifePercent)
    Add-Info "clip: $($Media.video)"

    $p = Start-Fresh 'Wpf' @('--surface=framesink', $Media.video)
    if (-not $p) { Add-Check 'WPF player starts' $false; return }
    [void](Wait-LogText 'Wpf' 'first frame ready' 30)
    Start-Sleep -Seconds 3
    $window = @(Measure-UiThread (Collect-Stats 'Wpf' $Seconds))
    [void][FbSmoke]::ShowWindow($p.MainWindowHandle, 3)   # maximize
    Start-Sleep -Seconds 3
    $maximized = @(Measure-UiThread (Collect-Stats 'Wpf' $Seconds))
    Stop-Player $p
    [void](Save-RunLog 'Wpf' 'test3-one-player')
    Add-Check 'one player: readings at window size' ($window.Count -gt 0) ($window -join '; ')
    Add-Check 'one player: readings maximized' ($maximized.Count -gt 0) ($maximized -join '; ')

    $p = Start-Fresh 'Wpf' @('--surface=framesink', '--players=3', $Media.video)
    if (-not $p) { Add-Check 'WPF player (3 players) starts' $false; return }
    [void][FbSmoke]::ShowWindow($p.MainWindowHandle, 3)
    Show-Explorer $Media.video
    Say-Do @(
        "Drag $(Split-Path -Leaf $Media.video) from Explorer onto the picture of P2, then onto P3.",
        'Wait until all three play, then press Enter here (do not touch the player after that).')
    Wait-Enter
    $three = @(Measure-UiThread (Collect-Stats 'Wpf' $Seconds))
    Stop-Player $p
    [void](Save-RunLog 'Wpf' 'test3-three-players')
    Add-Check 'three players: a reading for each' ($three.Count -ge 3) ($three -join '; ')
}

# 4. AutoHide off still raises it once.
function Test-4 {
    if (-not (Start-Step 4 'Auto-hide off' 'FirstFrameReady does not depend on auto-hide. Automatic.' '1')) { return }
    if (-not (Need-Media @('video'))) { return }
    foreach ($mode in @(@{ Name = 'native window'; Args = @() }, @{ Name = 'frame sink'; Args = @('--surface=framesink') })) {
        $p = Start-Fresh 'Wpf' (@('--no-auto-hide') + $mode.Args + @($Media.video))
        if (-not $p) { Add-Check "WPF $($mode.Name), auto-hide off: player starts" $false; continue }
        [void](Wait-LogText 'Wpf' 'first frame ready' 30)
        Start-Sleep -Seconds 3
        Stop-Player $p
        $log = Save-RunLog 'Wpf' ("test4-" + ($mode.Name -replace ' ', '-'))
        $count = @(Get-Lines $log 'first frame ready').Count
        Add-Check "WPF $($mode.Name), auto-hide off: one first-frame line" ($count -eq 1) "found $count"
    }
}

# 5. The A/B swap in TriviaBomb (or any WPF app following the WPF README's example).
function Test-5 {
    if (-not (Start-Step 5 'A/B swap in TriviaBomb' 'The same-frame swap needs eyes; the test players have no A/B mode. Guided.' '10')) { return }
    Say-Do @(
        'In TriviaBomb: two surfaces in WpfComposited mode, stacked, the waiting one Visibility.Hidden (WPF README, "First frame ready and the A/B swap").',
        'Swap clips 20 times from FirstFrameReady, showing the question panel in the same handler.',
        'Optional: film the screen with a phone in slow motion (240 fps).')
    Wait-Enter 'Press Enter when you have done the 20 swaps'
    Add-Answer 'no black frame between clips' 'Did every swap go straight from one clip to the next, with no black frame?'
    Add-Answer 'no old frame on the new surface' 'Did the new clip start with its own picture (never the previous clip''s last frame)?'
    Add-Answer 'panel and clip change together' 'Did the question panel change in the same frame as the clip?'
    Say-Do @('Change the waiting surface to Visibility.Collapsed instead of Hidden and swap a few times.')
    Wait-Enter
    Add-Answer 'Collapsed shows a wrong-size first frame (expected, why the pattern says Hidden)' 'With Collapsed, did the first frame look blurred or wrong-sized for a moment?'
    Say-Do @('Now set both surfaces to NativeHwndOverlay and swap a few times.')
    Wait-Enter
    $a = Ask-Choice 'Native mode: the question panel compared with the new clip?' @('same frame', 'panel a frame early', 'panel a frame late', 'could not tell')
    Add-Info "native mode, panel vs clip: $a (a frame early or late is possible in native mode)"
}

# 6. Avalonia and WinForms start their media at once too.
function Test-6 {
    if (-not (Start-Step 6 'Avalonia and WinForms startup' 'No startup delay in the other two players. Automatic, plus one question.' '2')) { return }
    if (-not (Need-Media @('video'))) { return }
    foreach ($kind in @('Ava', 'WinForms')) {
        $p = Start-Fresh $kind @($Media.video)
        if (-not $p) { Add-Check "$($PlayerNames[$kind]) starts" $false; continue }
        $t = Wait-LogText $kind 'Loaded:' 30
        Start-Sleep -Seconds 3
        $seen = Ask-YesNo "$($PlayerNames[$kind]): did the video appear right away (no half-second of black first)?"
        Stop-Player $p
        [void](Save-RunLog $kind "test6-$kind")
        Add-Check "$($PlayerNames[$kind]): startup media loaded" ($t -ge 0) $(if ($t -ge 0) { "$t s after the window appeared" } else { 'no Loaded: line in 30 s' })
        Add-Check "$($PlayerNames[$kind]): picture at once" ($seen -eq 'yes') "owner: $seen"
    }
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
Clear-Host
Write-Host 'FatzBomb.MediaControl smoke: stage 051A' -ForegroundColor Cyan
Write-Host "Results folder: $OutDir"
if (-not (Test-Path (Join-Path $Repo 'FatzBomb.MediaControl.sln'))) { throw "No FatzBomb.MediaControl.sln in $Repo. Pass -Repo <folder>." }

if (-not $SkipGit) {
    # Your own uncommitted changes would block the switch (or be mixed into the test): stop instead.
    $dirty = @(& git -C $Repo status --porcelain --untracked-files=no)
    if ($dirty.Count -gt 0) {
        Say-Warn 'The repo has uncommitted changes (git status lists them):'
        $dirty | ForEach-Object { Say-Warn "  $_" }
        throw 'Commit or stash them first (git stash), or run again with -SkipGit to test what you have.'
    }
    Say "Getting branch $Branch from GitHub..."
    & git -C $Repo fetch origin $Branch
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed: is the branch name right ($Branch)?" }
    # A local copy of the branch exists after the first run; the first run creates it from GitHub's.
    $null = & git -C $Repo rev-parse --verify --quiet "refs/heads/$Branch"
    if ($LASTEXITCODE -eq 0) { & git -C $Repo switch $Branch } else { & git -C $Repo switch --track "origin/$Branch" }
    if ($LASTEXITCODE -ne 0) { throw "Could not switch to $Branch." }
    & git -C $Repo pull --ff-only origin $Branch
    if ($LASTEXITCODE -ne 0) { throw 'git pull failed (your local branch has commits GitHub does not have?). Ask Claude before changing anything.' }
}

$branchNow = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git -C $Repo rev-parse --short HEAD).Trim()
Say "Repo: $Repo  (branch $branchNow, commit $commit)"
if (-not $env:FATZAUDIO_PACKAGES_TOKEN) { Say-Warn 'FATZAUDIO_PACKAGES_TOKEN is not set: the build cannot restore FatzAudio. Set it first (a classic PAT with read:packages).' }

if (-not $SkipBuild) {
    Say 'Building the solution (Release). This takes a few minutes...'
    & dotnet build (Join-Path $Repo 'FatzBomb.MediaControl.sln') -c Release -v minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The build failed: see the output above.' }
}

$Exe = @{
    Wpf      = Find-Exe 'FatzBomb.MediaControl.Player' 'FatzBomb.MediaControl.Player'
    Ava      = Find-Exe 'FatzBomb.MediaControl.Player.Avalonia' 'FatzBomb.MediaControl.Player.Avalonia'
    WinForms = Find-Exe 'FatzBomb.MediaControl.Player.WinForms' 'FatzBomb.MediaControl.Player.WinForms'
}

$all = 1..6
if (-not $Steps) {
    Write-Host ''
    Write-Host 'Tests (A = automatic, G = guided):' -ForegroundColor Cyan
    Write-Host '   1  A   Startup without delay, one first-frame line (WPF)     ~2 min'
    Write-Host '   2  G   Once per load (you drop three files)                  ~3 min'
    Write-Host '   3  AG  Composited UI-thread time, 047A before numbers        ~5 min'
    Write-Host '   4  A   Auto-hide off                                         ~1 min'
    Write-Host '   5  G   A/B swap in TriviaBomb (needs TriviaBomb)             ~10 min'
    Write-Host '   6  A   Avalonia and WinForms startup                         ~2 min'
    Write-Host ''
    Write-Host 'On the laptop, test 3 alone is enough: type 3.' -ForegroundColor DarkGray
    $Steps = Read-Host 'Which tests? Enter = all, or a list like 1,3'
}
if (-not $Steps -or $Steps.Trim().ToLowerInvariant() -eq 'all') { $selected = $all }
else { $selected = @($Steps -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ }) }

$Media = @{}
$videoExt = @('.mp4', '.mkv', '.mov', '.m4v', '.webm')
$audioExt = @('.mp3', '.flac', '.wav', '.m4a', '.ogg')
if (@($selected | Where-Object { $_ -in 1, 2, 3, 4, 6 }).Count -gt 0) {
    $Media.video = Select-Media 'a 1080p60 video (H.264 or HEVC; at least 1 minute long)' $videoExt @('1080', '60', 'h264', 'hd') $null ''
}
if ($selected -contains 2) { $Media.song = Select-Media 'any song (audio only)' $audioExt @() $null '' }

$gpuNames = (@(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { '{0} (driver {1})' -f $_.Name, $_.DriverVersion }) -join '; ')
Add-Result 'FatzBomb.MediaControl smoke 051A results'
Add-Result ('Date: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Add-Result "Repo: branch $branchNow, commit $commit"
Add-Result "Machine: $env:COMPUTERNAME; GPU: $gpuNames"
Add-Result ('Screens: {0}; Windows {1}; {2} logical cores' -f [System.Windows.Forms.Screen]::AllScreens.Count, [Environment]::OSVersion.Version, [Environment]::ProcessorCount)
foreach ($k in $Media.Keys) { Add-Result ('Media {0}: {1}' -f $k, $Media[$k]) }

foreach ($s in $selected) {
    try {
        switch ($s) {
            1 { Test-1 } 2 { Test-2 } 3 { Test-3 } 4 { Test-4 } 5 { Test-5 } 6 { Test-6 }
            default { Say-Warn "There is no test $s." }
        }
    } catch {
        Say-Warn ("Test {0} hit an error: {1}" -f $s, $_.Exception.Message)
        Add-Result ("ERROR in test {0}: {1}" -f $s, $_.Exception.Message)
    }
}

Add-Result ''
Add-Result ('TOTAL: {0} passed, {1} failed, {2} notes' -f $Totals.Pass, $Totals.Fail, $Totals.Info)
Write-Host ''
Write-Host ('=' * 78) -ForegroundColor Green
Write-Host (' All done: {0} passed, {1} failed.' -f $Totals.Pass, $Totals.Fail) -ForegroundColor Green
Write-Host ('=' * 78) -ForegroundColor Green
Set-Clipboard -Value (Get-Content -Path $ResultsFile -Raw)
Say "Results: $ResultsFile"
Say 'The results are on your clipboard: paste them into the chat with Claude.'
Invoke-Item $OutDir
