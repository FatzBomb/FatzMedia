<#
.SYNOPSIS
    Guided smoke tests for every stage waiting on a Windows run (the HANDOFF.md smoke backlog).

.DESCRIPTION
    Runs the tests one at a time and does as much as it can itself: it builds the players, makes
    the test media it needs with FFmpeg (it can download FFmpeg for you), starts the players with the
    right flags, reads their logs for the expected lines, measures CPU, takes screenshots, serves a
    local web file, and writes PASS/FAIL per check. You watch, and answer only what needs eyes or ears.

    Everything is saved to a folder on your Desktop (FatzBomb-backlog-<date>); at the end the results
    are copied to your clipboard so you can paste them into the chat.

    Run it from the repository folder:

        powershell -ExecutionPolicy Bypass -File scripts\smoke-backlog.ps1

    Optional parameters:
        -Repo       repo folder              (default: the folder above this script)
        -MediaDir   folder with your media   (default C:\media)
        -Steps      which tests, e.g. "1,2,3" or "all" (default: asks you)
        -SkipBuild  do not rebuild first
        -Ffmpeg     path to ffmpeg.exe (default: PATH, the players' native folders, or a download)
#>
[CmdletBinding()]
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$MediaDir = 'C:\media',
    [string]$Steps = '',
    [switch]$SkipBuild,
    [string]$Ffmpeg = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$SmokeName = 'backlog'
. (Join-Path $PSScriptRoot 'smoke-common.ps1')

$GenDir = Join-Path $OutDir 'media'
New-Item -ItemType Directory -Force -Path $GenDir | Out-Null
$FallbackTemp = Join-Path $env:TEMP 'FatzBombMediaControl'
$CacheDir = Join-Path $env:LOCALAPPDATA 'FatzBomb.MediaControl\AudioCache'
$Totals = @{ Pass = 0; Fail = 0; Info = 0 }

# ---------------------------------------------------------------------------------------------
# A tiny web server for the network test (whole files, no ranges, like a basic host)
# ---------------------------------------------------------------------------------------------
if (-not ('FbHttp' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class FbHttp
{
    static TcpListener _listener;
    static string _root;

    public static int Start(string root)
    {
        _root = root;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Thread t = new Thread(Loop);
        t.IsBackground = true;
        t.Start();
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public static void Stop() { try { _listener.Stop(); } catch { } }

    static void Loop()
    {
        while (true)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
        }
    }

    static void Serve(TcpClient c)
    {
        try
        {
            using (c)
            using (NetworkStream s = c.GetStream())
            {
                StreamReader r = new StreamReader(s, Encoding.ASCII);
                string line = r.ReadLine();
                while (true) { string h = r.ReadLine(); if (string.IsNullOrEmpty(h)) break; }
                if (line == null) return;
                string[] parts = line.Split(' ');
                string name = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].TrimStart('/')) : "";
                string path = Path.Combine(_root, Path.GetFileName(name));
                bool head = parts[0] == "HEAD";
                if (!File.Exists(path))
                {
                    byte[] nf = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    s.Write(nf, 0, nf.Length);
                    return;
                }
                long len = new FileInfo(path).Length;
                byte[] hdr = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: " + len + "\r\nConnection: close\r\n\r\n");
                s.Write(hdr, 0, hdr.Length);
                if (head) return;
                using (FileStream f = File.OpenRead(path)) f.CopyTo(s);
            }
        }
        catch { }
    }
}
'@
}

# ---------------------------------------------------------------------------------------------
# Result helpers
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
    Say-Info "  $Text"
}

function Add-Answer([string]$Name, [string]$Question) {
    $a = Ask-YesNo $Question
    Add-Check $Name ($a -eq 'yes') "owner: $a"
}

function Get-LogLines($LogFile, [string]$Pattern) {
    if (-not $LogFile -or -not (Test-Path $LogFile)) { return @() }
    return @(Select-String -Path $LogFile -Pattern $Pattern -SimpleMatch | ForEach-Object { $_.Line })
}

function Get-LastStats($LogFile) {
    $l = @(Get-LogLines $LogFile '[Stats]')
    if ($l.Count -eq 0) { return '' }
    return $l[-1]
}

# Starts a player, waits for its startup load (when it has a file) and $Seconds more, takes a
# screenshot if asked, closes it (unless -KeepOpen) and saves its log. Returns the log path and
# the process.
function Invoke-Run([string]$Kind, [string[]]$PlayerArgs, [string]$Tag, [int]$Seconds = 8, [switch]$Shot, [switch]$KeepOpen, [switch]$NoLoadWait) {
    $p = Start-Player $Kind $PlayerArgs
    $log = Join-Path $LogDir $LogNames[$Kind]
    if (-not (Wait-Window $p)) { return [pscustomobject]@{ Log = (Save-RunLog $Kind $Tag); Process = $null; Started = $false } }
    $hasMedia = @($PlayerArgs | Where-Object { $_ -and (($_ -like '--url=*') -or ($_ -notlike '--*' -and (Test-Path -LiteralPath $_))) }).Count -gt 0
    if ($hasMedia -and -not $NoLoadWait) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 25) {
            if (@(Get-LogLines $log '[Status]' | Where-Object { $_ -match 'Loaded:|Could not load|Load failed' }).Count -gt 0) { break }
            if ($p.HasExited) { break }
            Start-Sleep -Milliseconds 300
        }
    }
    Start-Sleep -Seconds $Seconds
    if ($Shot) { Save-WindowShot $p (Join-Path $ShotsOut "$Tag.png") }
    if ($KeepOpen) { return [pscustomobject]@{ Log = $log; Process = $p; Started = $true } }
    Stop-Player $p
    return [pscustomobject]@{ Log = (Save-RunLog $Kind $Tag); Process = $null; Started = $true }
}

# One automatic check: every Expect text must be in the log, no Reject text may be.
function Test-Run([string]$Name, [string]$Kind, [string[]]$PlayerArgs, [string[]]$Expect = @(), [string[]]$Reject = @(), [int]$Seconds = 8, [switch]$Shot) {
    $tag = ($Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
    $r = Invoke-Run $Kind $PlayerArgs $tag -Seconds $Seconds -Shot:$Shot
    if (-not $r.Started) { Add-Check $Name $false 'the player did not start'; return $r }
    $missing = @($Expect | Where-Object { @(Get-LogLines $r.Log $_).Count -eq 0 })
    $found = @($Reject | Where-Object { @(Get-LogLines $r.Log $_).Count -gt 0 })
    $detail = @()
    if ($missing.Count) { $detail += 'missing: ' + ($missing -join ' | ') }
    if ($found.Count) { $detail += 'unexpected: ' + ($found -join ' | ') }
    $stats = Get-LastStats $r.Log
    if ($stats) { $detail += 'stats: ' + ($stats -replace '^.*\[Stats\]\s*', '') }
    Add-Check $Name ($missing.Count -eq 0 -and $found.Count -eq 0) ($detail -join '; ')
    return $r
}

# ---------------------------------------------------------------------------------------------
# FFmpeg and generated media
# ---------------------------------------------------------------------------------------------
function Find-Ffmpeg {
    if ($Ffmpeg -and (Test-Path $Ffmpeg)) { return $Ffmpeg }
    $cmd = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($k in $Exe.Keys) {
        $f = Get-ChildItem -Path (Split-Path $Exe[$k]) -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f.FullName }
    }
    $local = Join-Path $env:LOCALAPPDATA 'FatzBombSmoke\ffmpeg'
    $f = Get-ChildItem -Path $local -Recurse -Filter 'ffmpeg.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($f) { return $f.FullName }
    Say-Warn 'ffmpeg.exe was not found. The tests make their own small media files with it.'
    if ((Ask-YesNo 'Download the FFmpeg 9.0 LGPL build for Windows (about 100 MB, from github.com/BtbN) to your local app data?') -eq 'no') { return $null }
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    $zip = Join-Path $local 'ffmpeg.zip'
    $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-9.0.zip'
    Say "Downloading $url ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $local -Force
    Remove-Item $zip -Force
    $f = Get-ChildItem -Path $local -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if ($f) { return $f.FullName } else { return $null }
}

function New-Media([string]$Name, [string[]]$FfArgs) {
    $out = Join-Path $GenDir $Name
    if (Test-Path $out) { return $out }
    if (-not $script:FfmpegExe) { return $null }
    & $script:FfmpegExe -hide_banner -loglevel error -y @FfArgs $out 2>&1 | Out-Null
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) { return $out }
    Say-Warn "  Could not make $Name with this FFmpeg build."
    return $null
}

$Tone = @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=12:sample_rate=48000', '-ac', '2')
$Pic = @('-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30:duration=12')

function Test-HasEncoder([string]$Name) {
    if (-not $script:FfmpegExe) { return $false }
    return [bool](& $script:FfmpegExe -hide_banner -encoders 2>$null | Select-String -Pattern "\s$Name\s" -Quiet)
}

# ---------------------------------------------------------------------------------------------
# The tests
# ---------------------------------------------------------------------------------------------

# 1. Formats on both engines (ENG-043, 033A slice 2, FatzAudio's format answer)
function Test-1 {
    if (-not (Start-Step 1 'Audio formats on BASS and FatzAudio' 'ENG-043 (ALAC, M4R, audio-only MP4/TS), the FFmpeg fallback (033A slice 2) and what FatzAudio decodes. Fully automatic.' '12')) { return }
    if (-not $script:FfmpegExe) { Add-Info 'skipped: no FFmpeg'; return }
    Say 'Making the test files...'
    $files = [ordered]@{}
    $files['MP3'] = New-Media 'tone.mp3' ($Tone + @('-c:a', 'libmp3lame', '-b:a', '192k'))
    $files['MP2'] = New-Media 'tone.mp2' ($Tone + @('-c:a', 'mp2'))
    $files['AC-3'] = New-Media 'tone.ac3' ($Tone + @('-c:a', 'ac3'))
    $files['AAC (M4A)'] = New-Media 'tone-aac.m4a' ($Tone + @('-c:a', 'aac'))
    $files['ALAC (M4A)'] = New-Media 'tone-alac.m4a' ($Tone + @('-c:a', 'alac'))
    $files['ALAC (CAF)'] = New-Media 'tone-alac.caf' ($Tone + @('-c:a', 'alac'))
    $m4a = New-Media 'tone-ringtone.m4a' ($Tone + @('-c:a', 'aac'))
    if ($m4a) { $m4r = Join-Path $GenDir 'tone.m4r'; Copy-Item $m4a $m4r -Force; $files['M4R'] = $m4r }
    $files['FLAC'] = New-Media 'tone.flac' ($Tone + @('-c:a', 'flac'))
    $files['WAV'] = New-Media 'tone.wav' ($Tone + @('-c:a', 'pcm_s16le'))
    $files['AIFF'] = New-Media 'tone.aiff' ($Tone + @('-c:a', 'pcm_s16be'))
    $files['Ogg Vorbis'] = New-Media 'tone.ogg' ($Tone + @('-c:a', 'libvorbis'))
    $files['Opus'] = New-Media 'tone.opus' ($Tone + @('-c:a', 'libopus'))
    $files['WMA'] = New-Media 'tone.wma' ($Tone + @('-c:a', 'wmav2'))
    $files['WavPack'] = New-Media 'tone.wv' ($Tone + @('-c:a', 'wavpack'))
    $files['TTA'] = New-Media 'tone.tta' ($Tone + @('-c:a', 'tta'))
    $files['MKA (FLAC)'] = New-Media 'tone.mka' ($Tone + @('-c:a', 'flac'))
    $files['Sun AU'] = New-Media 'tone.au' ($Tone + @('-c:a', 'pcm_s16be'))
    $files['DTS'] = New-Media 'tone.dts' ($Tone + @('-c:a', 'dca', '-strict', '-2'))
    $files['MP4 audio only'] = New-Media 'audio-only.mp4' ($Tone + @('-c:a', 'aac'))
    $files['TS audio only'] = New-Media 'audio-only.ts' ($Tone + @('-c:a', 'aac', '-f', 'mpegts'))
    $cover = New-Media 'cover.png' @('-f', 'lavfi', '-i', 'testsrc2=size=300x300', '-frames:v', '1')
    if ($cover) { $files['MP4 with cover art only'] = New-Media 'cover-only.mp4' ($Tone + @('-i', $cover, '-map', '0:a', '-map', '1:v', '-c:a', 'aac', '-c:v', 'png', '-disposition:v', 'attached_pic')) }
    $files['AVI (DivX-style, MP3 audio)'] = New-Media 'divx-mp3.avi' ($Pic + $Tone + @('-c:v', 'mpeg4', '-vtag', 'XVID', '-c:a', 'libmp3lame'))
    $files['MPG (MPEG-2, MP2 audio)'] = New-Media 'mpeg2-mp2.mpg' ($Pic + $Tone + @('-c:v', 'mpeg2video', '-c:a', 'mp2', '-f', 'mpeg'))
    $files['MP4 video (MPEG-4, AAC)'] = New-Media 'video-aac.mp4' ($Pic + $Tone + @('-c:v', 'mpeg4', '-c:a', 'aac'))

    foreach ($engine in @('bass', 'fatz')) {
        Add-Result ''
        Add-Result "  -- engine: $engine --"
        foreach ($k in $files.Keys) {
            $f = $files[$k]
            if (-not $f) { Add-Info "$k ($engine): not made by this FFmpeg build"; continue }
            $r = Invoke-Run 'Wpf' @("--audio=$engine", $f) ("fmt-$engine-" + ($k -replace '[^A-Za-z0-9]+', '-')) -Seconds 4
            $loaded = @(Get-LogLines $r.Log '[Status]' | Where-Object { $_ -match 'Loaded:' }).Count -gt 0
            $stats = Get-LastStats $r.Log
            $playing = $stats -match ' Playing \|'
            $how = @()
            if (@(Get-LogLines $r.Log 'with FFmpeg in').Count) { $how += 'converted by FFmpeg' }
            if (@(Get-LogLines $r.Log 'FatzAudio cannot decode').Count) { $how += 'FatzAudio handed it to FFmpeg' }
            if (@(Get-LogLines $r.Log 'opening it as audio').Count) { $how += 'video file played as audio' }
            $errors = @(Get-LogLines $r.Log '[Diagnostic]' | Where-Object { $_ -match '\bError\b' } | Select-Object -First 2)
            $detail = ('loaded {0}, playing {1}{2}{3}' -f $loaded, $playing, $(if ($how) { ', ' + ($how -join ', ') } else { '' }), $(if ($errors) { '; ' + ($errors -join ' / ') } else { '' }))
            Add-Check "$k on $engine" ($loaded -and $playing) $detail
        }
    }

    # The long-file fallback writes a temp WAV and removes it on close (033A slice 2 step 4).
    $long = New-Media 'long-10min.wv' @('-f', 'lavfi', '-i', 'sine=frequency=330:duration=600:sample_rate=48000', '-ac', '2', '-c:a', 'wavpack')
    if ($long) {
        $r = Invoke-Run 'Wpf' @($long) 'fmt-long-wavpack' -Seconds 4
        $temp = @(Get-LogLines $r.Log 'temp file').Count -gt 0
        Start-Sleep -Seconds 2
        $left = @(Get-ChildItem -Path $FallbackTemp -Filter '*.wav' -ErrorAction SilentlyContinue)
        Add-Check '10-minute WavPack on BASS goes through a temp file' $temp ''
        Add-Check 'Its temp WAV is gone after the player closed' ($left.Count -eq 0) ("WAVs left in $FallbackTemp" + ": " + $left.Count)
    }
}

# 2. Loudness normalization on FatzAudio (037A normalization)
function Test-2 {
    if (-not (Start-Step 2 'Loudness normalization (FatzAudio)' 'The 037A normalization smoke: first play scans, later plays use the cache, levels match, BASS unchanged.' '8')) { return }
    if (-not (Need-Media @('loud', 'quiet'))) { return }
    if (Test-Path $CacheDir) { Remove-Item $CacheDir -Recurse -Force }
    Add-Info "cleared $CacheDir"
    foreach ($k in @('loud', 'quiet', 'tagged')) {
        if (-not $Media[$k]) { continue }
        $r = Invoke-Run 'Wpf' @('--audio=fatz', $Media[$k]) "norm-first-$k" -Seconds 10
        $line = @(Get-LogLines $r.Log 'Loudness ' | Where-Object { $_ -match 'LUFS' }) | Select-Object -First 1
        $ok = [bool]$line
        if ($k -eq 'tagged') { $ok = $ok -and ($line -match '\(Tags\)') }
        Add-Check "First play of the $k song reports its loudness" $ok ($line -replace '^.*\[Diagnostic\]\s*', '')
    }
    $entries = @(Get-ChildItem -Path $CacheDir -File -ErrorAction SilentlyContinue)
    Add-Check 'The cache folder holds entries' ($entries.Count -gt 0) ("$CacheDir, " + $entries.Count + ' file(s): ' + (($entries | ForEach-Object { '{0} ({1} B)' -f $_.Name, $_.Length }) -join ', '))
    foreach ($k in @('loud', 'quiet')) {
        $r = Invoke-Run 'Wpf' @('--audio=fatz', $Media[$k]) "norm-again-$k" -Seconds 6
        $line = @(Get-LogLines $r.Log 'Loudness ' | Where-Object { $_ -match 'LUFS' }) | Select-Object -First 1
        Add-Check "A new run reads the $k song from the cache" ($line -match '\(Cache\)') ($line -replace '^.*\[Diagnostic\]\s*', '')
    }
    Say 'Now the listening part. The script starts the FatzAudio player with the loud song.'
    $p = Start-Player 'Wpf' @('--audio=fatz', $Media.loud)
    [void](Wait-Window $p)
    Set-Clipboard -Value $Media.quiet
    Say-Do @('Listen to the loud song for a few seconds.',
        'Open the quiet song (its path is on your clipboard: Open, Ctrl+V, Enter) and listen.',
        'Switch back and forth a couple of times.',
        'While the quiet song plays, turn Normalize off, then on again.')
    Wait-Enter
    Stop-Player $p
    Add-Answer 'Both songs sound about equally loud' 'Did the two songs sound about equally loud?'
    Add-Answer 'Normalize off/on changes the level smoothly' 'Did Normalize off make the quiet song quieter, and on bring it back smoothly with no click?'
    Add-Answer 'No clipping or crackle' 'Was there no clipping or crackle on loud parts?'
    $r = Test-Run 'BASS keeps its peak rule' 'Wpf' @($Media.quiet) -Expect @('Deep normalization complete') -Seconds 10
}

# 3. Network sources (039A_3)
function Test-3 {
    if (-not (Start-Step 3 'Network sources' '039A follow-ups: certificate checks, live radio, http through BASS, HLS, a non-faststart MP4. Needs internet. Mostly automatic.' '8')) { return }
    foreach ($u in @('https://expired.badssl.com/stream.mp3', 'https://self-signed.badssl.com/stream.mp3', 'https://wrong.host.badssl.com/stream.mp3')) {
        $null = Test-Run "Bad certificate refused: $u" 'Wpf' @("--url=$u", '--net-retries=3') -Expect @('certificate verification failed') -Seconds 4
    }
    $null = Test-Run 'Invalid certificate allowed on request' 'Wpf' @('--url=https://self-signed.badssl.com/stream.mp3', '--net-allow-invalid-certs') -Expect @('No playable audio stream') -Seconds 4
    $r = Invoke-Run 'Wpf' @('--url=https://ice2.somafm.com/groovesalad-128-mp3') 'net-https-radio' -Seconds 25 -KeepOpen
    Say-Do @('Listen: a station (Groove Salad) should be playing.', 'Try volume, EQ and Pause/Play.')
    Wait-Enter
    Stop-Player $r.Process
    $log = Save-RunLog 'Wpf' 'net-https-radio'
    Add-Check 'https radio plays' ((Get-LastStats $log) -match ' Playing \|') (Get-LastStats $log)
    Add-Answer 'https radio sounds right and controls respond' 'Did the station play, with volume/EQ/Pause/Play responding at once?'
    $null = Test-Run 'Plain http radio plays through BASS' 'Wpf' @('--url=http://ice2.somafm.com/groovesalad-128-mp3') -Expect @('AudioEngine') -Seconds 12
    $null = Test-Run 'HLS in Background mode plays' 'Wpf' @('--mode=background', '--url=https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8') -Seconds 15 -Shot
    if ($script:FfmpegExe) {
        $slow = New-Media 'moov-at-end.mp4' ($Pic + $Tone + @('-c:v', 'mpeg4', '-c:a', 'aac'))
        $fast = Join-Path $GenDir 'faststart.mp4'
        if ($slow -and -not (Test-Path $fast)) { & $script:FfmpegExe -hide_banner -loglevel error -y -i $slow -c copy -movflags +faststart $fast }
        if ($slow -and (Test-Path $fast)) {
            $port = [FbHttp]::Start($GenDir)
            Add-Info "local web server on http://127.0.0.1:$port/"
            $null = Test-Run 'Non-faststart MP4 over http fails at once with the faststart message' 'Wpf' @("--url=http://127.0.0.1:$port/moov-at-end.mp4", '--net-retries=2') -Expect @('faststart') -Seconds 6
            $r = Invoke-Run 'Wpf' @("--url=http://127.0.0.1:$port/faststart.mp4") 'net-faststart' -Seconds 6
            Add-Check 'Faststart MP4 over http plays' ((Get-LastStats $r.Log) -match ' Playing \|') (Get-LastStats $r.Log)
            [FbHttp]::Stop()
        }
    }
    if ((Ask-YesNo 'Run the optional network-unplug check (you pull the network cable or turn Wi-Fi off for 30 s)?') -eq 'yes') {
        $p = Start-Player 'Wpf' @('--url=https://ice2.somafm.com/groovesalad-128-mp3')
        [void](Wait-Window $p); Start-Sleep -Seconds 15
        Say-Do @('Disconnect the network for 3 s, then reconnect: the sound should not stop.', 'Then disconnect for 30 s: the player should show buffering, then the stream ends.', 'Reconnect.')
        Wait-Enter
        Stop-Player $p
        $log = Save-RunLog 'Wpf' 'net-unplug'
        Add-Info ('buffering lines in the log: ' + @(Get-LogLines $log 'Buffering').Count)
        Add-Answer 'Network loss handled' 'Did a 3 s outage play through, and a 30 s one buffer and then end cleanly?'
    }
}

# 4. HDR tone mapping and HDR output (042A)
function Test-4 {
    if (-not (Start-Step 4 'HDR: HLG tone mapping and HDR output' '042A slices 1-2. Log checks are automatic; you judge the picture. HDR output needs an HDR monitor.' '10')) { return }
    $hlg = $Media.hlg; $hdr10 = $Media.hdr10
    if ((-not $hlg -or -not $hdr10) -and (Test-HasEncoder 'hevc_nvenc')) {
        Say 'Making synthetic HDR clips on the GPU (good for the log checks; real clips are better for the picture)...'
        $common = $Pic + @('-vf', 'format=p010le', '-c:v', 'hevc_nvenc', '-profile:v', 'main10', '-color_primaries', 'bt2020', '-colorspace', 'bt2020nc', '-tag:v', 'hvc1')
        if (-not $hlg) { $hlg = New-Media 'synthetic-hlg.mp4' ($common + @('-color_trc', 'arib-std-b67')) }
        if (-not $hdr10) { $hdr10 = New-Media 'synthetic-hdr10.mp4' ($common + @('-color_trc', 'smpte2084')) }
    }
    if (-not $hlg -and -not $hdr10) { Add-Info 'skipped: no HLG or HDR10 clip'; return }
    Say-Do @('Turn Windows HDR OFF for now (Settings > Display > Use HDR).')
    Wait-Enter
    if ($hlg) {
        $null = Test-Run 'HLG on Direct3D11 is tone mapped on the GPU' 'Wpf' @($hlg) -Expect @('HLG HDR, tone mapped to SDR', 'HLG video is tone mapped to SDR', 'on the GPU (Direct3D11 shader)') -Seconds 6 -Shot
        $null = Test-Run 'HLG with --hdr-tonemap-cpu' 'Wpf' @('--hdr-tonemap-cpu', $hlg) -Expect @('HLG video is tone mapped to SDR') -Seconds 6 -Shot
        $null = Test-Run 'HLG with --no-hdr-tonemap' 'Wpf' @('--no-hdr-tonemap', $hlg) -Expect @('without tone mapping') -Seconds 6 -Shot
        $null = Test-Run 'HLG on the Software renderer' 'Wpf' @('--renderer=software', '--surface=framesink', $hlg) -Expect @('HLG video is tone mapped to SDR') -Seconds 6 -Shot
        $null = Test-Run 'HLG on Vulkan (Avalonia)' 'Ava' @('--renderer=vulkan', $hlg) -Expect @('HLG video is tone mapped to SDR') -Seconds 6 -Shot
    }
    if ($hdr10) {
        $null = Test-Run 'HDR10 on Direct3D11 is tone mapped' 'Wpf' @($hdr10) -Expect @('HDR10 video is tone mapped to SDR') -Seconds 6 -Shot
        $null = Test-Run 'HDR10 with --hdr-output on an SDR desktop stays SDR' 'Wpf' @('--hdr-output', $hdr10) -Expect @('D3D11.HdrOutput', 'not in HDR mode') -Seconds 6
    }
    Say "Screenshots are in $ShotsOut."
    Add-Answer 'Tone-mapped HDR looks right (not washed out, highlights roll off)' 'Looking at the screenshots or what you saw: did the tone-mapped HDR pictures look right, and --no-hdr-tonemap brighter and flatter?'
    if ((Ask-YesNo 'Do you have an HDR monitor to test real HDR output now?') -eq 'yes') {
        Say-Do @('Turn Windows HDR ON for that monitor.', 'Move this PowerShell window aside; the player will open on your main monitor - drag it to the HDR monitor.')
        Wait-Enter
        foreach ($clip in @($hdr10, $hlg)) {
            if (-not $clip) { continue }
            $r = Invoke-Run 'Wpf' @('--hdr-output', $clip) ('hdrout-' + [IO.Path]::GetFileNameWithoutExtension($clip)) -Seconds 4 -KeepOpen
            Say-Do @('Drag the player to the HDR monitor if it is not there.', 'Watch: highlights brighter than the white of the UI, mid-tones unchanged, colours saturated.', 'Toggle Windows HDR off and on once during playback.')
            Wait-Enter
            Stop-Player $r.Process
            $log = Save-RunLog 'Wpf' ('hdrout-' + [IO.Path]::GetFileNameWithoutExtension($clip))
            Add-Check ("HDR output switched to scRGB: " + (Split-Path $clip -Leaf)) (@(Get-LogLines $log 'scRGB').Count -gt 0) ''
            Add-Answer ("HDR output looks right: " + (Split-Path $clip -Leaf)) 'Did it look right in HDR, and recover after the HDR toggle with no lasting black?'
        }
    }
}

# 5. Player modes (043A)
function Test-5 {
    if (-not (Start-Step 5 'Player modes: Full, Background, Audio' '043A slices 1-2. Startup checks are automatic; mode switching is guided.' '8')) { return }
    if (-not $script:FfmpegExe) { Add-Info 'skipped: no FFmpeg'; return }
    $mv = New-Media 'music-video.mp4' ($Pic + $Tone + @('-c:v', 'mpeg4', '-c:a', 'aac'))
    $silent = New-Media 'silent-video.mp4' ($Pic + @('-c:v', 'mpeg4'))
    $mp3 = New-Media 'song.mp3' ($Tone + @('-c:a', 'libmp3lame'))
    $jpg = New-Media 'picture.jpg' @('-f', 'lavfi', '-i', 'testsrc2=size=1280x720', '-frames:v', '1')
    $null = Test-Run 'Background mode: a music video loops silently, no audio set-up' 'Wpf' @('--mode=background', $mv) -Reject @('Global BASS initialized') -Seconds 6
    $null = Test-Run 'Background mode: an MP3 is refused' 'Wpf' @('--mode=background', $mp3) -Expect @('Background mode does not accept') -Seconds 3
    $null = Test-Run 'Background mode: a picture shows' 'Wpf' @('--mode=background', $jpg) -Seconds 3 -Shot
    $null = Test-Run 'Audio mode: a music video plays its sound, no surface' 'Wpf' @('--mode=audio', $mv) -Expect @('Audio mode has no presentation') -Seconds 6
    $null = Test-Run 'Audio mode: a picture is refused' 'Wpf' @('--mode=audio', $jpg) -Expect @('Audio mode does not accept') -Seconds 3
    $null = Test-Run 'Audio mode: a silent video is refused' 'Wpf' @('--mode=audio', $silent) -Expect @('no playable audio stream') -Seconds 3
    $null = Test-Run 'Background mode on Avalonia' 'Ava' @('--mode=background', $mv) -Reject @('Global BASS initialized') -Seconds 6
    Say 'Now mode switching on the fly. The script starts the WPF player with the music video.'
    $p = Start-Player 'Wpf' @($mv)
    [void](Wait-Window $p)
    Say-Do @('Seek a little, change volume, pitch and tempo.',
        'Mode box: Audio. Sound should continue from the same place with the same settings.',
        'Resize the window, then Mode: Full. Video returns at the current position, at the new size.',
        'Pause, switch to Audio and back: it stays paused. Stop, switch: it stays stopped.',
        'Mode: Background (Loop off before): it loops silently and Loop shows ticked. Back to Full: sound returns.',
        'Switch modes quickly a few times, pressing Play/Pause in between, and once close the window during a switch.')
    Wait-Enter
    Stop-Player $p
    $log = Save-RunLog 'Wpf' 'modes-switching'
    Add-Info ('mode switches logged: ' + @(Get-LogLines $log 'mode').Count)
    Add-Answer 'Mode switching keeps position and state, no hang' 'Did every switch keep position and play state, with no black or stale picture and no hang?'
    if ($Media.cdg) {
        $p = Start-Player 'Wpf' @($Media.cdg)
        [void](Wait-Window $p)
        Say-Do @('With the MP3+G playing: Mode Audio, then Full.')
        Wait-Enter
        Stop-Player $p
        Add-Answer 'MP3+G graphics return in sync after Audio and back' 'Did the graphics return in sync?'
    }
}

# 6. Avalonia shared GPU image and second screen (027A slices 2 and 5)
function Test-6 {
    if (-not (Start-Step 6 'Avalonia: shared GPU image and second screen' '027A slices 2 and 5. Path checks and CPU are automatic.' '8')) { return }
    if (-not (Need-Media @('v1080'))) { return }
    $r = Test-Run 'Avalonia Direct3D11 uses the shared GPU image' 'Ava' @('--renderer=direct3d11', $Media.v1080) -Expect @('via shared GPU image') -Seconds 12 -Shot
    $r = Test-Run 'Avalonia --surface=framesink uses the frame sink' 'Ava' @('--renderer=direct3d11', '--surface=framesink', $Media.v1080) -Expect @('via frame sink') -Seconds 12
    $null = Test-Run 'Avalonia Vulkan falls back to the frame sink with a reason' 'Ava' @('--renderer=vulkan', $Media.v1080) -Expect @('via frame sink') -Seconds 8
    $null = Test-Run 'Avalonia Software falls back to the frame sink with a reason' 'Ava' @('--renderer=software', $Media.v1080) -Expect @('via frame sink') -Seconds 8
    foreach ($mode in @(@{ N = 'shared GPU image'; A = @('--renderer=direct3d11') }, @{ N = 'frame sink'; A = @('--renderer=direct3d11', '--surface=framesink') })) {
        $p = Start-Player 'Ava' ($mode.A + @($Media.v1080))
        if (Wait-Window $p) {
            Start-Sleep -Seconds 8
            $u = Measure-Usage $p 20
            Add-Info ('Avalonia 1080p via {0}: CPU {1}% of all cores ({2} cores), GPU {3}' -f $mode.N, $u.CpuPercent, $u.CoresUsed, $u.Gpu)
        }
        Stop-Player $p
    }
    $null = Test-Run 'Avalonia with 4 players all on the shared GPU image' 'Ava' @('--renderer=direct3d11', '--players=4', $Media.v1080) -Expect @('via shared GPU image') -Seconds 10
    if ([System.Windows.Forms.Screen]::AllScreens.Count -ge 2) {
        $src = if ($Media.cdg) { $Media.cdg } else { $Media.v1080 }
        $r = Invoke-Run 'Ava' @('--renderer=direct3d11', '--second-screen=2', $src) 'ava-second-screen' -Seconds 6 -KeepOpen
        Say-Do @('Check: the second screen is full screen on monitor 2 and in sync with the main window.',
            'On the second screen press Escape (it becomes a window), resize it, then F11.',
            'Alt+F4 the second screen: the main player keeps playing. Re-tick the second screen box: it reopens.',
            'Mode: Audio - both go black, sound continues. Mode: Full - video on both.')
        Wait-Enter
        Stop-Player $r.Process
        $log = Save-RunLog 'Ava' 'ava-second-screen'
        Add-Check 'Second screen on the shared GPU image' (@(Get-LogLines $log "second-screen-1': shared GPU image path").Count -gt 0) ''
        Add-Answer 'Second screen behaves (sync, close/reopen, Audio mode)' 'Did the second screen do all of that correctly?'
    } else { Add-Info 'second-screen checks skipped: one monitor' }
    $null = Test-Run 'WPF composited (frame sink) second output works' 'Wpf' @('--surface=framesink', $Media.v1080) -Seconds 6 -Shot
}

# 7. Vulkan on the real GPU (026A)
function Test-7 {
    if (-not (Start-Step 7 'Vulkan renderer tests on your GPU' '026A slices 1-2: the Vulkan tests ran only on a software Vulkan so far. Fully automatic.' '3')) { return }
    $out = Join-Path $LogsOut 'vulkan-tests.txt'
    & dotnet test (Join-Path $Repo 'FatzBomb.MediaControl.Rendering.Vulkan.Tests') -c Release --logger 'console;verbosity=normal' 2>&1 | Tee-Object -FilePath $out | Out-Null
    $summary = @(Select-String -Path $out -Pattern 'Passed!|Failed!' | ForEach-Object { $_.Line.Trim() })
    $device = @(Select-String -Path $out -Pattern 'llvmpipe|lavapipe|NVIDIA|AMD|Intel|Radeon|GeForce' | Select-Object -First 1 | ForEach-Object { $_.Line.Trim() })
    Add-Check 'Vulkan tests pass on this machine' (($summary -join ' ') -match 'Passed!' -and ($summary -join ' ') -notmatch 'Failed!') ($summary -join ' ')
    Add-Check 'They ran on the real GPU, not a software Vulkan' (($device -join ' ') -notmatch 'llvmpipe|lavapipe' -and $device.Count -gt 0) ($device -join ' ')
}

# 8. Text on the Software and Vulkan renderers (ENG-040), software renderer smoothness (ENG-039 4c)
function Test-8 {
    if (-not (Start-Step 8 'Subtitles and lyrics on Software and Vulkan' 'ENG-040: text drawn with SkiaSharp off Direct3D11; ENG-039 batch 4c: the Software renderer. Screenshots automatic, you judge.' '5')) { return }
    if (-not $script:FfmpegExe) { Add-Info 'skipped: no FFmpeg'; return }
    $v = New-Media 'subtitled.mp4' ($Pic + $Tone + @('-c:v', 'mpeg4', '-c:a', 'aac'))
    $srt = [IO.Path]::ChangeExtension($v, '.srt')
    Set-Content -Path $srt -Encoding UTF8 -Value "1`r`n00:00:00,500 --> 00:00:05,000`r`nSubtitle line one`r`n`r`n2`r`n00:00:05,500 --> 00:00:11,000`r`nSecond line, with umlauts: äöü`r`n"
    $null = Test-Run 'Subtitles on the WPF Software renderer' 'Wpf' @('--renderer=software', '--surface=framesink', $v) -Reject @('SkiaSharp', 'DllNotFound') -Seconds 3 -Shot
    $null = Test-Run 'Subtitles on Vulkan (Avalonia)' 'Ava' @('--renderer=vulkan', $v) -Reject @('SkiaSharp', 'DllNotFound') -Seconds 3 -Shot
    if ($Media.kar) {
        $null = Test-Run 'KAR lyrics on the Software renderer' 'Wpf' @('--renderer=software', '--surface=framesink', $Media.kar) -Reject @('DllNotFound') -Seconds 8 -Shot
        $null = Test-Run 'KAR lyrics on Vulkan (Avalonia)' 'Ava' @('--renderer=vulkan', $Media.kar) -Reject @('DllNotFound') -Seconds 8 -Shot
    }
    Say "Screenshots: $ShotsOut"
    Add-Answer 'Text is drawn on Software and Vulkan (Segoe UI, readable)' 'In the screenshots, are the subtitles (and lyrics) drawn correctly?'
    if ($Media.v1080) {
        $p = Start-Player 'Wpf' @('--renderer=software', '--surface=framesink', $Media.v1080)
        if (Wait-Window $p) { Start-Sleep -Seconds 4; Invoke-ResizeStorm $p; Start-Sleep -Seconds 2 }
        Stop-Player $p
        Add-Answer 'Software renderer smooth through resize, maximize and restore' 'Did the Software renderer stay smooth while the script resized it, with no old-frame flash at the end?'
    }
}

# 9. Without BASS, and without the BASS FLAC plug-in (ENG-039 batches 1 and 5)
function Test-9 {
    if (-not (Start-Step 9 'Native libraries missing' 'ENG-039: players start without BASS (FFmpeg only); a FLAC plays through FFmpeg when bassflac.dll is missing. Fully automatic.' '3')) { return }
    $nativeSrc = Get-ChildItem -Path (Split-Path $Exe.Wpf) -Recurse -Filter 'avcodec*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nativeSrc) { Add-Info 'skipped: no native folder with FFmpeg found next to the WPF player'; return }
    $src = $nativeSrc.DirectoryName
    $noBass = Join-Path $OutDir 'native-no-bass'
    $noFlac = Join-Path $OutDir 'native-no-bassflac'
    New-Item -ItemType Directory -Force -Path $noBass, $noFlac | Out-Null
    Get-ChildItem $src -File | Where-Object { $_.Name -notmatch '^bass' } | Copy-Item -Destination $noBass -Force
    Get-ChildItem $src -File | Where-Object { $_.Name -ne 'bassflac.dll' } | Copy-Item -Destination $noFlac -Force
    $null = Test-Run 'Two players start without BASS, each with a warning' 'Wpf' @("--native=$noBass", '--players=2') -Expect @('Native libraries loaded without BASS') -Seconds 4 -Shot
    if ($script:FfmpegExe) {
        $flac = New-Media 'long-flac-4min.flac' @('-f', 'lavfi', '-i', 'sine=frequency=500:duration=240:sample_rate=48000', '-ac', '2', '-c:a', 'flac')
        $r = Test-Run 'FLAC without bassflac.dll plays through FFmpeg' 'Wpf' @("--native=$noFlac", $flac) -Expect @('with FFmpeg in') -Seconds 4
    }
}

# 10. The player set: counts, renderers, full screen (ENG-039 batch 4a) - guided
function Test-10 {
    if (-not (Start-Step 10 'Player set, full screen and closing' 'ENG-039 batch 4a, in all three players. Guided: you click, the script checks the log.' '10')) { return }
    if (-not (Need-Media @('v1080'))) { return }
    foreach ($k in @('Wpf', 'Ava', 'WinForms')) {
        $args1 = @('--players=2', $Media.v1080)
        if ([System.Windows.Forms.Screen]::AllScreens.Count -ge 2) { $args1 += '--second-screen=2' }
        $p = Start-Player $k $args1
        [void](Wait-Window $p)
        Say-Do @("$($PlayerNames[$k]): player 1 plays (and opens its second screen if you have two monitors); player 2 is empty.",
            'Set Players to 4, then change Renderer: all players are recreated empty, no second screen reopens.',
            'Change Players quickly several times: you end on the last count.',
            'Double-click a picture: only that player goes full screen; double-click restores. Click into player 2, F11, F11.',
            'Full screen with a flyout open (not WinForms): Escape closes the flyout, a second Escape leaves full screen.',
            'Finally, go full screen and close the window (Alt+F4).')
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p.WaitForExit(600000) | Out-Null
        $log = Save-RunLog $k "playerset-$k"
        Add-Info ("{0}: player dispose lines in the log: {1}; the window took {2:F0} s" -f $PlayerNames[$k], @(Get-LogLines $log 'dispos').Count, $sw.Elapsed.TotalSeconds)
        Add-Check "$($PlayerNames[$k]) closed cleanly" $p.HasExited ''
        Add-Answer "$($PlayerNames[$k]) player set, full screen and Escape rules" 'Did every step behave as described?'
    }
}

# 11. The audit tests that already have a script (GPU reset, resize, refresh rate, loop point,
#     Avalonia close during rebuild, karaoke memory, MKV subtitles)
function Test-11 {
    if (-not (Start-Step 11 'Audit tests from smoke-audit.ps1' 'ENG-039 batches 1, 2, 4b, 4c, 5: GPU reset recovery, resize, refresh rate, loop point, Avalonia rebuild, karaoke memory, MKV subtitles. Runs the existing script; it asks for its own media and writes its own results folder.' '25')) { return }
    Add-Info 'running scripts\smoke-audit.ps1 -Steps 2,3,4,6,8,9,10 -SkipBuild (its results are in its own Desktop folder; paste them too)'
    & (Join-Path $PSScriptRoot 'smoke-audit.ps1') -Repo $Repo -MediaDir $MediaDir -Steps '2,3,4,6,8,9,10' -SkipBuild
}

# 12. Remaining guided checks (ENG-039 batches 1, 4b, 4c, 5; 026B step 6)
function Test-12 {
    if (-not (Start-Step 12 'Remaining guided checks' 'The rest of ENG-039 batches 1, 4b, 4c and 5, and 026B step 6. The script starts each player; you look and answer.' '15')) { return }
    if ($Media.v1080) {
        $null = Test-Run 'Direct3D11 keeps hardware decoding (no fallback warning)' 'Wpf' @($Media.v1080) -Reject @('in software until it is opened again') -Seconds 6
        $r = Test-Run 'Avalonia WARP: first frame, then software decoding with one warning' 'Ava' @('--renderer=warp', $Media.v1080) -Expect @('in software until it is opened again') -Reject @('Hardware video decode restored') -Seconds 8 -Shot
        $p = Start-Player 'Wpf' @($Media.v1080)
        [void](Wait-Window $p)
        Say-Do @('Turn Loop on and let it wrap twice: gapless, sound in sync.', 'Seek ten times while playing: picture and sound stay aligned.')
        Wait-Enter; Stop-Player $p
        $log = Save-RunLog 'Wpf' 'guided-loop-seek'
        Add-Check 'No "Composition present failed" during loop and seeks' (@(Get-LogLines $log 'Composition present failed').Count -eq 0) ''
        Add-Answer 'Loop wraps gaplessly and seeks stay in sync' 'Was every loop wrap gapless and every seek in sync?'
    }
    if ($Media.song -and $script:FfmpegExe) {
        $bg = [IO.Path]::ChangeExtension((Join-Path $GenDir (Split-Path $Media.song -Leaf)), '.mp4')
        $songCopy = [IO.Path]::ChangeExtension($bg, [IO.Path]::GetExtension($Media.song))
        Copy-Item $Media.song $songCopy -Force
        $null = New-Media (Split-Path $bg -Leaf) ($Pic + @('-c:v', 'mpeg4'))
        $png = [IO.Path]::ChangeExtension($songCopy, '.PNG')
        $p = Start-Player 'Wpf' @($songCopy)
        [void](Wait-Window $p)
        Say-Do @('A song with a same-name background video beside it: play 20 s, Stop, Play: the background restarts from its first frame.', 'Turn Loop on: the background restarts at each loop point.')
        Wait-Enter; Stop-Player $p
        Add-Answer 'Background video restarts with the song' 'Did the background video restart from frame 1 on Stop/Play and at each loop?'
        Remove-Item $bg -Force -ErrorAction SilentlyContinue
        $null = & $script:FfmpegExe -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=1280x720' -frames:v 1 $png
        $r = Invoke-Run 'Wpf' @($songCopy) 'guided-png-sidecar' -Seconds 4 -Shot
        Add-Answer 'A song.PNG sidecar background attaches (026B step 6)' "In the screenshot $ShotsOut\guided-png-sidecar.png, is the test-pattern picture shown as the background?"
    }
    $p = Start-Player 'Wpf' @('--lightweight-surfaces')
    [void](Wait-Window $p)
    Say-Do @('In the lightweight-surfaces window: browse to a CD+G, press Karaoke - it plays.', 'Clear the path box and press Karaoke again: the song stops.')
    Wait-Enter; Stop-Player $p
    Add-Answer 'Karaoke surface with an empty source stops the song' 'Did the song stop when the path was cleared?'
    if ($Media.mkv) {
        $p = Start-Player 'Wpf' @($Media.mkv)
        [void](Wait-Window $p)
        Say-Do @('The MKV with embedded subtitles: it opens at once; the subtitle picker fills with "Embedded ..." and the first is shown, also when paused.', 'Choose Off before the scan ends once (reopen to try): it sticks.')
        Wait-Enter; Stop-Player $p
        Add-Answer 'Embedded subtitles scanned after the open' 'Did it open at once and the subtitle choices behave?'
    }
    $p = Start-Player 'WinForms' @('--surface=framesink', $(if ($Media.v1080) { $Media.v1080 } else { '' }))
    [void](Wait-Window $p)
    Say-Do @('WinForms frame sink: no flicker while playing and resizing.')
    Wait-Enter; Stop-Player $p
    Add-Answer 'WinForms frame sink has no flicker' 'Was it free of flicker?'
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
Clear-Host
Write-Host 'FatzBomb.MediaControl smoke backlog' -ForegroundColor Cyan
Write-Host "Results folder: $OutDir"
if (-not (Test-Path (Join-Path $Repo 'FatzBomb.MediaControl.sln'))) { throw "No FatzBomb.MediaControl.sln in $Repo. Pass -Repo <folder>." }

$branch = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git -C $Repo rev-parse --short HEAD).Trim()
Say "Repo: $Repo  (branch $branch, commit $commit)"
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
foreach ($k in $Exe.Keys) {
    $dir = Split-Path $Exe[$k]
    $hasBass = [bool](Get-ChildItem -Path $dir -Recurse -Filter 'bass.dll' -ErrorAction SilentlyContinue | Select-Object -First 1)
    $hasFfmpeg = [bool](Get-ChildItem -Path $dir -Recurse -Filter 'avcodec*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($hasBass -and $hasFfmpeg)) {
        Say-Warn ("{0}: BASS found = {1}, FFmpeg found = {2}. Put the DLLs in its native\ folder and rebuild if a test fails." -f $PlayerNames[$k], $hasBass, $hasFfmpeg)
    }
}

$all = 1..12
if (-not $Steps) {
    Write-Host ''
    Write-Host 'Tests (A = automatic, G = guided):' -ForegroundColor Cyan
    Write-Host '   1  A  Audio formats on BASS and FatzAudio (ENG-043, 033A)            ~12 min'
    Write-Host '   2  AG Loudness normalization on FatzAudio (037A)                     ~8 min'
    Write-Host '   3  A  Network sources (039A_3; needs internet)                       ~8 min'
    Write-Host '   4  AG HDR tone mapping and HDR output (042A)                         ~10 min'
    Write-Host '   5  AG Player modes (043A)                                            ~8 min'
    Write-Host '   6  A  Avalonia shared GPU image and second screen (027A)             ~8 min'
    Write-Host '   7  A  Vulkan tests on your GPU (026A)                                ~3 min'
    Write-Host '   8  AG Text on Software and Vulkan (ENG-040, ENG-039 4c)              ~5 min'
    Write-Host '   9  A  Native libraries missing (ENG-039)                             ~3 min'
    Write-Host '  10  G  Player set, full screen, closing (ENG-039 4a)                  ~10 min'
    Write-Host '  11  AG Audit tests: GPU reset, resize, ... (runs smoke-audit.ps1)     ~25 min'
    Write-Host '  12  G  Remaining guided checks (ENG-039 1, 4b, 4c, 5; 026B)           ~15 min'
    $Steps = Read-Host 'Which tests? Enter or "all" = 1-12, or a list like 1,2,7'
}
if (-not $Steps -or $Steps.Trim().ToLowerInvariant() -eq 'all') { $selected = $all }
else {
    $selected = @($Steps -split '[,\s]+' | Where-Object { $_ } | ForEach-Object {
            if ($_ -match '^(\d+)-(\d+)$') { [int]$Matches[1]..[int]$Matches[2] } else { [int]$_ } })
}

# Media, asked once up front.
$need = @{}
$uses = @{ 2 = 'loud', 'quiet', 'tagged'; 4 = 'hlg', 'hdr10'; 5 = 'cdg'; 6 = 'v1080', 'cdg'; 8 = 'kar', 'v1080'; 10 = 'v1080'; 12 = 'v1080', 'song', 'mkv' }
foreach ($s in $selected) { if ($uses.ContainsKey($s)) { foreach ($m in $uses[$s]) { $need[$m] = $true } } }
$Media = @{}
$videoExt = @('.mp4', '.mkv', '.mov', '.m4v', '.webm', '.avi', '.ts')
$audioExt = @('.mp3', '.flac', '.wav', '.m4a', '.ogg', '.opus')
if ($need.Count -gt 0) { Write-Host ''; Write-Host "Pick your media files (from $MediaDir; S skips one):" -ForegroundColor Cyan }
if ($need['loud']) { $Media.loud = Select-Media 'a LOUD modern song (a recent pop or rock master)' $audioExt @() $null '' }
if ($need['quiet']) { $Media.quiet = Select-Media 'a QUIET song (older recording, classical, acoustic)' $audioExt @() $null $Media.loud }
if ($need['tagged']) { $Media.tagged = Select-Media 'a song with ReplayGain tags (optional)' $audioExt @('rg', 'replaygain') $null '' }
if ($need['song']) { $Media.song = Select-Media 'any short song (copied, never changed)' $audioExt @() $null '' }
if ($need['v1080']) { $Media.v1080 = Select-Media 'a 1080p video (H.264 or HEVC)' $videoExt @('1080', 'h264', 'hd') $null '' }
if ($need['hlg']) { $Media.hlg = Select-Media 'an HLG HDR clip (optional; one is made on NVIDIA GPUs)' $videoExt @('hlg') $null '' }
if ($need['hdr10']) { $Media.hdr10 = Select-Media 'an HDR10 clip (optional; one is made on NVIDIA GPUs)' $videoExt @('hdr', 'hdr10', 'pq') $null '' }
if ($need['cdg']) { $Media.cdg = Select-Media 'a CD+G (.cdg with its .mp3 beside it, optional)' @('.cdg') @() { Test-Path ([IO.Path]::ChangeExtension($_.FullName, '.mp3')) } '' }
if ($need['kar']) { $Media.kar = Select-Media 'a MIDI/KAR with lyrics (optional)' @('.kar', '.mid', '.midi') @('kar') $null '' }
if ($need['mkv']) { $Media.mkv = Select-Media 'an MKV with embedded subtitle tracks (optional)' @('.mkv') @('sub', 'srt', 'ass') $null '' }

$needsFfmpeg = @($selected | Where-Object { $_ -in 1, 3, 4, 5, 8, 9, 12 }).Count -gt 0
$script:FfmpegExe = if ($needsFfmpeg) { Find-Ffmpeg } else { $null }
if ($script:FfmpegExe) { Say-Info "FFmpeg: $($script:FfmpegExe)" }

$gpuNames = (@(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | ForEach-Object { '{0} (driver {1})' -f $_.Name, $_.DriverVersion }) -join '; ')
Add-Result 'FatzBomb.MediaControl smoke backlog results'
Add-Result ('Date: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'))
Add-Result "Repo: branch $branch, commit $commit"
Add-Result "GPU: $gpuNames"
Add-Result ('Screens: {0}; Windows {1}; PowerShell {2}; {3} logical cores' -f [System.Windows.Forms.Screen]::AllScreens.Count, [Environment]::OSVersion.Version, $PSVersionTable.PSVersion, [Environment]::ProcessorCount)
Add-Result ("FFmpeg for test media: " + $(if ($script:FfmpegExe) { $script:FfmpegExe } else { 'none' }))
foreach ($k in $Media.Keys) { Add-Result ('Media {0}: {1}' -f $k, $Media[$k]) }

foreach ($s in $selected) {
    try {
        switch ($s) {
            1 { Test-1 } 2 { Test-2 } 3 { Test-3 } 4 { Test-4 } 5 { Test-5 } 6 { Test-6 }
            7 { Test-7 } 8 { Test-8 } 9 { Test-9 } 10 { Test-10 } 11 { Test-11 } 12 { Test-12 }
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
Say 'The results are on your clipboard: paste them into the chat with Claude (and attach screenshots for any FAIL).'
Invoke-Item $OutDir
