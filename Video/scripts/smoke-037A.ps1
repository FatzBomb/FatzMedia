<#
.SYNOPSIS
    The 037A hand tests (the FatzAudio engine), as much of them automated as can be.

.DESCRIPTION
    Part 1 runs by itself (about 3 minutes): it plays test tones through the video-audio path
    (FFmpeg decode into FatzAudio) and measures what a person would listen for:
      - sync drift of the clock the picture follows, at tempo 0.75, 1.0, 1.25 and 1.5
      - seeks (landing on the target, no click), loop wraps (gapless, no click),
        pause/resume (the clock holds, no click), one steady minute (no click or dropout)
    It records the output and searches it for clicks, so you do not have to listen for them.

    Part 2 needs your eyes and ears. For each check the script starts the player with the right
    flags and file, tells you what to do and look for, then asks Pass, Fail or Skip. On a Fail you
    can type a comment (or just press Enter). The player is closed for you after each check.

    Everything is saved in a folder in work\fatzmedia\Video\test-results (FatzBomb-037A-<date>):
    results.txt (readable), results.json (for Claude) and the player logs. At the end the summary is
    copied to your clipboard, so you can paste it into the chat.

    Run it from the FatzMedia folder, in PowerShell:

        cd D:\development\FatzBomb\FatzMedia\Video
        powershell -ExecutionPolicy Bypass -File scripts\smoke-037A.ps1

    Optional parameters:
        -MediaDir      folders with your media (default the shared sample-files, C:\media and D:\- Testing Files,
                       two levels deep)
        -ChooseMedia   always ask for each file (otherwise the shared sample files below are used when present)
        -SkipBuild     do not rebuild first
        -SilentPart1   part 1 plays on FatzAudio's null device: nothing is heard (default: your
                       default output device, so it measures your real hardware; quiet test tones)
        -Part          1, 2 or both (default both)
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string[]]$MediaDir = @(),
    [switch]$ChooseMedia,
    [switch]$SkipBuild,
    [switch]$SilentPart1,
    [ValidateSet('1', '2', 'both')][string]$Part = 'both'
)

$ErrorActionPreference = 'Stop'
if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }   # in the body: Windows PowerShell has no $PSScriptRoot in parameter defaults
$SmokeName = '037A'
. (Join-Path $PSScriptRoot 'smoke-common.ps1')
if ($MediaDir.Count -eq 0) { $MediaDir = @($SampleFiles, 'C:\media', 'D:\- Testing Files') }

$Results = New-Object System.Collections.Generic.List[object]
$AudioRoot = Join-Path (Split-Path -Parent $Repo) 'Audio'

function Add-Outcome([string]$Id, [string]$Name, [string]$Kind, [string]$Outcome, [string]$Detail, [string]$Comment) {
    $Results.Add([pscustomobject]@{ id = $Id; name = $Name; kind = $Kind; result = $Outcome; detail = $Detail; comment = $Comment })
    $line = "  [$Outcome] $Id $Name"
    if ($Detail) { $line += " - $Detail" }
    if ($Comment) { $line += "`n      comment: $Comment" }
    Add-Result $line
    $color = switch ($Outcome) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'DarkGray' } }
    Write-Host $line -ForegroundColor $color
}

function Ask-Outcome([string]$Id, [string]$Name, [string]$Detail = '') {
    while ($true) {
        $a = (Read-Host "Result for $Id - P = pass, F = fail, S = skip").Trim().ToLowerInvariant()
        if ($a -in 'p', 'pass') { Add-Outcome $Id $Name 'manual' 'PASS' $Detail ''; return }
        if ($a -in 's', 'skip') { Add-Outcome $Id $Name 'manual' 'SKIP' $Detail ''; return }
        if ($a -in 'f', 'fail') {
            $comment = Read-Host 'What went wrong? (optional: type a comment, or just press Enter)'
            Add-Outcome $Id $Name 'manual' 'FAIL' $Detail $comment.Trim()
            return
        }
    }
}

# ---------------------------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------------------------
Add-Result "037A hand tests, $(Get-Date -Format 'yyyy-MM-dd HH:mm'), $env:COMPUTERNAME"
if (-not $SkipBuild) {
    Say 'Building (a minute or two)...'
    if (Test-Path (Join-Path $AudioRoot 'build\CMakeCache.txt')) {
        & cmake --build (Join-Path $AudioRoot 'build') --config Release --target fatzaudio | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Building the FatzAudio native library failed.' }
    } else {
        Say-Warn "No FatzAudio native build in $AudioRoot\build: build it once as in Audio\docs\building.md."
    }
    & dotnet build (Join-Path $Repo 'FatzMedia.Video.sln') -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Building FatzMedia.Video failed: run "dotnet build FatzMedia.Video.sln -c Release" to see why.' }
}

# ---------------------------------------------------------------------------------------------
# Part 1: measured
# ---------------------------------------------------------------------------------------------
if ($Part -in '1', 'both') {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host ' PART 1: measured checks (about 3 minutes, nothing to do)' -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
    if (-not $SilentPart1) { Say-Info 'You will hear quiet test tones from your default output device.' }
    Add-Result ''
    Add-Result '== PART 1: measured =='
    $jsonl = Join-Path $OutDir 'automated.jsonl'
    $env:FATZMEDIA_HANDTESTS = '1'
    $env:FATZMEDIA_HANDTESTS_OUT = $jsonl
    $env:FATZMEDIA_HANDTESTS_DEVICE = if ($SilentPart1) { 'null' } else { 'real' }
    if (-not $env:FATZBOMB_FFMPEG_DIR) { $env:FATZBOMB_FFMPEG_DIR = Join-Path $Repo 'FatzMedia.Video.Sample.Player\native' }
    try {
        & dotnet test (Join-Path $Repo 'FatzMedia.Video.Linux.Tests') -c Release --no-build --filter 'Category=HandTests' -v q --nologo |
            Out-File (Join-Path $LogsOut 'automated-tests.txt')
    } finally {
        Remove-Item env:FATZMEDIA_HANDTESTS, env:FATZMEDIA_HANDTESTS_OUT, env:FATZMEDIA_HANDTESTS_DEVICE -ErrorAction SilentlyContinue
    }

    if (Test-Path $jsonl) {
        foreach ($r in @(Get-Content $jsonl | ForEach-Object { $_ | ConvertFrom-Json } | Sort-Object name)) {
            $id = ($r.name -split ' ')[0]   # A1 .. A5, as the measured tests name themselves
            Add-Outcome $id $r.name.Substring($id.Length).Trim() 'measured' $(if ($r.pass) { 'PASS' } else { 'FAIL' }) "$($r.detail) [$($r.device)]" ''
        }
    } else {
        Add-Outcome 'A0' 'measured checks' 'measured' 'FAIL' 'no results: see logs\automated-tests.txt (FFmpeg or the FatzAudio library not found?)' ''
    }
}

# ---------------------------------------------------------------------------------------------
# Part 2: eyes and ears
# ---------------------------------------------------------------------------------------------
if ($Part -in '2', 'both') {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host ' PART 2: your eyes and ears (about 25 minutes)' -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Say 'First pick the files. Press Enter to take the first suggestion, or S to skip a kind (its checks are skipped).'
    Add-Result ''
    Add-Result '== PART 2: by eye and ear =='

    $videoExt = @('.mp4', '.mkv', '.avi', '.mov', '.webm', '.mpg', '.ts', '.wmv')
    $Media = @{
        Video   = Select-Media 'a music video with clear lip sync or drum hits' $videoExt @('music', 'clip', 'video') -Preferred 'smoke-clips\avsync [1080p H.264 60fps, mono AAC, 1m00s].mp4'
        Short   = Select-Media 'a SHORT video (under a minute) to loop' $videoExt @('short', 'loop', 'clip') -Preferred 'smoke-clips\video [1080p H.264 30fps, mono AAC, 30s].mp4'
        Hd      = Select-Media 'a 1080p video with sound' $videoExt @('1080', 'hd', 'fhd') -Preferred 'edge-cases\LunchLady Land [1080p H.264 23.976fps, stereo AAC, 4m51s].mp4'
        Quiet   = Select-Media 'a QUIET video or song (older or classical)' ($videoExt + @('.mp3', '.flac', '.m4a')) @('quiet', 'classical', 'old')
        Cdg     = Select-Media 'a karaoke song: MP3+G (pick the .mp3) or a ZIP' @('.mp3', '.zip') @('karaoke', 'cdg', 'sc') -Preferred 'karaoke\karaoke.mp3'
        Kar     = Select-Media 'a MIDI karaoke file' @('.kar', '.mid') @('kar') -Preferred 'karaoke\abba_-_dancing_queen2.kar'
        Song    = Select-Media 'any MP3 song (for the plain song check)' @('.mp3') @('song') -Preferred 'edge-cases\audio-only [MP3 stereo, 7m06s].mp3'
    }
    $Exe = @{ Wpf = Find-Exe 'FatzMedia.Video.Sample.Player' 'FatzMedia.Video.Sample.Player' }

    function Run-Check([string]$Id, [string]$Name, [string]$MediaKey, [string[]]$PlayerArgs, [string[]]$Steps, [string]$LogPattern) {
        Write-Host ''
        Write-Host ("--- $Id $Name ---") -ForegroundColor Cyan
        if ($MediaKey -and -not $Media[$MediaKey]) { Add-Outcome $Id $Name 'manual' 'SKIP' 'no file chosen' ''; return }
        $a = (Read-Host 'Press Enter to start, or S to skip').Trim().ToLowerInvariant()
        if ($a -eq 's') { Add-Outcome $Id $Name 'manual' 'SKIP' 'skipped' ''; return }
        $args2 = @() + $PlayerArgs
        if ($MediaKey) { $args2 += $Media[$MediaKey] }
        $proc = Start-Player 'Wpf' $args2
        [void](Wait-Window $proc)
        Say-Do $Steps
        Ask-Outcome $Id $Name
        Stop-Player $proc
        $log = Save-RunLog 'Wpf' $Id
        if ($LogPattern -and $log) { Add-LogExcerpt $log $LogPattern 12 }
    }

    Run-Check 'M1' 'sync at tempo 0.75 to 1.5' 'Video' @() @(
        'Play the video. Watch lips or drum hits against the sound for 20 seconds.',
        'Set tempo to 75%, then 125%, then 150% (pitch stays): 20 seconds each.',
        'Pass if the picture stays on the sound at every tempo (no drift, no slow slide).') 'Audio engine|tempo'
    Run-Check 'M2' 'seeks' 'Video' @() @(
        'While playing, click and drag on the position bar, forward and back, about ten times.',
        'Then pause, seek, and press play.',
        'Pass if picture and sound restart together each time, with no burst of old sound and no click.') ''
    Run-Check 'M3' 'loops' 'Short' @() @(
        'Turn loop on and let it wrap three times.',
        'Then turn loop off: it should end once.',
        'Pass if there is no gap or click at the wrap and the picture wraps with the sound.') 'loop'
    Run-Check 'M4' 'pause and resume' 'Video' @() @(
        'Pause and resume about ten times at random moments.',
        'Pass if there is no click and sync holds after each resume.') ''
    Run-Check 'M5' 'two screens, 1080p, first minute' 'Hd' @('--second-screen=2') @(
        'The video shows on this screen and your second screen (if --second-screen=2 is the wrong screen, note it).',
        'Listen to the whole first minute.',
        'Pass if there are no clicks, pops or dropouts.') 'underrun|stall|Stalled'
    Run-Check 'M6' 'CD+G karaoke' 'Cdg' @() @(
        'Play it: the graphics wipe in time with the singing.',
        'Seek, pause and resume, and loop: the picture is right after each.',
        'A song you know scrolls smoothly should glide line by line.') 'Loaded CDG'
    Run-Check 'M6b' 'scaled CD+G (smoothed, outlined)' 'Cdg' @('--cdg-scale=4', '--cdg-effects') @(
        'The same song, now drawn by FatzAudio in full colour, smoothed, with an outline and shadow.',
        'It fills the height of the window, sharp at any window size; seek and pause still show the right picture.',
        'Pass if it looks right and stays in time (it is optional: the classic picture stays the default).') 'Loaded CDG|Scaled CD\+G'
    Run-Check 'M7' 'Audio-mode soundtrack' 'Video' @('--mode=audio') @(
        'The video plays as sound only: the position bar moves, seeks work.') ''
    Run-Check 'M8' 'MIDI karaoke' 'Kar' @() @(
        'It plays with lyrics on screen: white words wiping to yellow in time with the singing, the next line dimmer.',
        'Seek and pause: the lyrics are right after each.') 'Lyrics drawn by FatzAudio|draw these lyrics|could not draw the lyrics|soundfont'
    Run-Check 'M9' 'loudness' 'Quiet' @() @(
        'Play this quiet file for a few seconds, then open the music video from M1 (Open button) and play it.',
        'Pass if both are about equally loud.') 'Loudness|loudness'

    Run-Check 'M9b' 'soundtrack decoded by FatzAudio (optional switch)' 'Video' @('--video-audio=fatz') @(
        'The same music video as M1, but FatzAudio decodes its sound (the Log says "FatzAudio decodes the soundtrack").',
        'Watch lips or drum hits for 20 seconds, seek a few times, set tempo to 125%.',
        'Pass if sync is as good as in M1 (this switch stays off by default until you say it is good).') 'decodes the soundtrack'
    # M10: network radio needs a URL.
    Write-Host ''
    Write-Host '--- M10 network radio ---' -ForegroundColor Cyan
    $url = (Read-Host 'Paste an http:// or https:// radio stream URL (or press Enter to skip)').Trim()
    if ($url) {
        $proc = Start-Player 'Wpf' @("--url=$url")
        [void](Wait-Window $proc)
        Say-Do @('It plays and the song title appears.')
        Ask-Outcome 'M10' 'network radio'
        Stop-Player $proc
    } else { Add-Outcome 'M10' 'network radio' 'manual' 'SKIP' 'no URL' '' }

    # M11: output device and device events.
    Write-Host ''
    Write-Host '--- M11 output device and plug/unplug ---' -ForegroundColor Cyan
    Say 'Your Windows sound devices (playback devices are usually "Speakers ...", "Headphones ...", or a TV name):'
    Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue | Sort-Object FriendlyName | ForEach-Object { Say-Info ('  ' + $_.FriendlyName) }
    Say-Info '  (a name FatzAudio does not know plays on the default, and the Log lists the names it knows)'
    $device = (Read-Host 'Type the name of a device that is NOT your default (a USB headset, HDMI), or Enter to skip').Trim()
    if ($device -and $Media['Song']) {
        $proc = Start-Player 'Wpf' @("--audio-device=$device", $Media['Song'])
        [void](Wait-Window $proc)
        Say-Do @(
            'The sound comes out of that device (the Log says "ready on the <name> output device").',
            'Unplug that device while it plays (if you can): playback moves to the default and goes on.',
            'Plug something in and out again: the Log shows one "Audio device change detected" line each.')
        Ask-Outcome 'M11' 'output device and plug/unplug'
        Stop-Player $proc
        $log = Save-RunLog 'Wpf' 'M11'
        if ($log) { Add-LogExcerpt $log 'ready on|Audio device change|not found' 12 }
    } else { Add-Outcome 'M11' 'output device and plug/unplug' 'manual' 'SKIP' 'no device or song chosen' '' }

    # M12: no BASS DLLs anywhere in the player folder, and a song still plays.
    Write-Host ''
    Write-Host '--- M12 no BASS DLLs, song plays ---' -ForegroundColor Cyan
    if ($Media['Song']) {
        $bassFiles = @(Get-ChildItem (Split-Path $Exe['Wpf']) -Recurse -Filter 'bass*.dll' -ErrorAction SilentlyContinue)
        if ($bassFiles.Count -gt 0) { Say-Info ('Found BASS DLLs in the player folder: ' + (($bassFiles | ForEach-Object Name) -join ', ')) }
        else { Say-Info 'The player folder contains no bass*.dll.' }
        $proc = Start-Player 'Wpf' @($Media['Song'])
        [void](Wait-Window $proc)
        Say-Do @('The song plays with sound. Also open the music video from M1: it plays with sound.')
        Ask-Outcome 'M12' 'no BASS DLLs, song plays'
        Stop-Player $proc
    } else { Add-Outcome 'M12' 'no BASS DLLs, song plays' 'manual' 'SKIP' 'no song chosen' '' }
}

# ---------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------
$pass = @($Results | Where-Object result -eq 'PASS').Count
$fail = @($Results | Where-Object result -eq 'FAIL').Count
$skip = @($Results | Where-Object result -eq 'SKIP').Count
Add-Result ''
Add-Result "TOTAL: $pass passed, $fail failed, $skip skipped"
$Results | ConvertTo-Json -Depth 4 | Out-File (Join-Path $OutDir 'results.json') -Encoding utf8
Write-Host ''
Write-Host "TOTAL: $pass passed, $fail failed, $skip skipped" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
Write-Host ("Saved in " + [IO.Path]::GetFullPath($OutDir))
try { Set-Clipboard -Value (Get-Content -Path $ResultsFile -Raw); Say 'The results are on your clipboard: paste them into the chat.' } catch { }
