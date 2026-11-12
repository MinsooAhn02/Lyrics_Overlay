#requires -Version 7
# Memory growth test: drives the real app like a long listening session (a report every second, a new song every
# -SongSec seconds, captions for every video) and samples its memory once a minute.
#   pwsh tools\leak-check.ps1 [-Minutes 20] [-SongSec 15] [-Exe <LyricsOnTheGo.exe>]
# Restarts the app and turns it OFF at the end. Lyrics lookups go to LRCLIB for the first round only (then cache).
# Verdict: after a warm-up (first third), private memory must not keep rising by more than -MaxGrowthMBPerHour.
param([int]$Minutes = 20, [int]$SongSec = 15, [double]$MaxGrowthMBPerHour = 20, [string]$Exe)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = (Resolve-Path (Join-Path $PSScriptRoot '..\app\src\LyricsOnTheGo\bin\Release\net8.0-windows10.0.19041.0\LyricsOnTheGo.exe')).Path }
$songs = @(
    @('Hype Boy', 'NewJeans'), @('Dynamite', 'BTS'), @('Blueming', 'IU'), @('Love Dive', 'IVE'), @('Next Level', 'aespa'),
    @('Spring Day', 'BTS'), @('Ditto', 'NewJeans'), @('Antifragile', 'LE SSERAFIM'), @('Celebrity', 'IU'), @('Supernova', 'aespa'),
    @('Lemon', 'Kenshi Yonezu'), @('Shape of You', 'Ed Sheeran'), @('Blinding Lights', 'The Weeknd'), @('Bad Guy', 'Billie Eilish'), @('Levitating', 'Dua Lipa'),
    @('Fedexx Girl', 'BIBI'), @('Attention', 'NewJeans'), @('Eleven', 'IVE'), @('Butter', 'BTS'), @('Palette', 'IU'))

Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' } | Stop-Process -Force; Start-Sleep 2
Set-Content (Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\launch-ticket') ([DateTime]::UtcNow.Ticks)
$env:LOTG_MEMLOG = '1'; $p = Start-Process $Exe -PassThru; Remove-Item Env:LOTG_MEMLOG   # app logs its retained heap (after a full GC) every 30 s
$Log = Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\captions.log'; $heap = [ordered]@{}
$end = (Get-Date).AddSeconds(15); while ((Get-Date) -lt $end -and -not (Test-NetConnection 127.0.0.1 -Port 47831 -WarningAction SilentlyContinue -InformationLevel Quiet)) { Start-Sleep 0.5 }
$tok = (Get-Content (Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\settings.json') -Raw | ConvertFrom-Json).BridgeToken
$h = @{ 'X-Bridge-Token' = $tok; Origin = 'chrome-extension://khoankekndekkeoamaopgjhpogbooldk' }
$capLines = 1..60 | ForEach-Object { , @(($_ * 3000), "lyric line number $_ with some words in it") }
$asr = 1..400 | ForEach-Object { , @(($_ * 450), "word$_") }

$samples = [Collections.Generic.List[object]]::new()
$t0 = Get-Date; $nextSample = $t0; $song = -1; $songStart = $t0; $errors = 0
while ((Get-Date) -lt $t0.AddMinutes($Minutes)) {
    $now = Get-Date
    if ($song -lt 0 -or ($now - $songStart).TotalSeconds -ge $SongSec) {
        $song++; $songStart = $now
        $vid = ('leak' + ($song % 40).ToString('0000000'))   # 40 distinct videos, so caches see repeats AND new keys
        $s = $songs[$song % $songs.Count]
        $cap = @{ videoId = $vid; status = 'ok'; lang = 'en'; name = 'English'; lines = $capLines; asr = @{ status = 'ok'; lang = 'en'; lines = $asr }; desc = '' }
        try { [void](Invoke-RestMethod 'http://127.0.0.1:47831/v1/captions' -Method POST -Headers $h -ContentType 'application/json' -Body ($cap | ConvertTo-Json -Depth 5 -Compress)) } catch { $errors++ }
    }
    $pos = [int](($now - $songStart).TotalMilliseconds) + 1000
    $o = @{ videoId = $vid; title = "$($s[1]) - $($s[0]) (Official MV)"; channel = $s[1]; track = $s[0]; artist = $s[1]; metaSource = 'ytm'
            durationMs = 200000; positionMs = $pos; paused = $false; isAd = $false; isMusic = $true; sentAtMs = 1; shuffle = 'off' }
    try { [void](Invoke-RestMethod 'http://127.0.0.1:47831/v1/now-playing' -Method POST -Headers $h -ContentType 'application/json' -Body ($o | ConvertTo-Json -Compress)) } catch { $errors++ }
    if ($now -ge $nextSample) {
        $p.Refresh()
        if ($p.HasExited) { Write-Host 'FAIL app exited during the test'; exit 1 }
        $m = [pscustomobject]@{ Min = [math]::Round(($now - $t0).TotalMinutes, 1); PrivateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1); WorkingMB = [math]::Round($p.WorkingSet64 / 1MB, 1); Threads = $p.Threads.Count; Handles = $p.HandleCount; Songs = $song + 1 }
        $samples.Add($m); Write-Host ("{0,5} min  private {1,6} MB  working {2,6} MB  threads {3,3}  handles {4,5}  songs {5}" -f $m.Min, $m.PrivateMB, $m.WorkingMB, $m.Threads, $m.Handles, $m.Songs)
        foreach ($l in @(Get-Content $Log -ErrorAction SilentlyContinue | Select-String 'memlog heapMB=([\d.]+)')) { if ($l.Line.Substring(0, 8) -ge $t0.ToString('HH:mm:ss')) { $heap[$l.Line.Substring(0, 8)] = [double]$l.Matches[0].Groups[1].Value } }   # only this run; the log is reset at 200 KB: collect as we go
        $nextSample = $nextSample.AddMinutes(1)
    }
    Start-Sleep -Milliseconds 1000
}
$p | Stop-Process -Force

# slope (least squares) over the samples after the warm-up third
$after = @($samples | Where-Object { $_.Min -ge $Minutes / 3 })
function Slope($ys) { $n = $after.Count; $xs = $after.Min; $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
    $num = 0; $den = 0; for ($i = 0; $i -lt $n; $i++) { $num += ($xs[$i] - $mx) * ($ys[$i] - $my); $den += ($xs[$i] - $mx) * ($xs[$i] - $mx) }; if ($den -eq 0) { 0 } else { $num / $den * 60 } }
$memSlope = Slope $after.PrivateMB; $hSlope = Slope $after.Handles; $tSlope = Slope $after.Threads
Write-Host ("after warm-up: private {0:+0.0;-0.0} MB/h, handles {1:+0;-0}/h, threads {2:+0.0;-0.0}/h, request errors {3}" -f $memSlope, $hSlope, $tSlope, $errors)
$fails = 0
# Retained managed heap after a full GC: the exact leak signal (no sawtooth). Compare the start and end of the run after the warm-up.
$hv = @($heap.Values); Write-Host ("retained heap (after full GC, every 30 s): " + ($hv | ForEach-Object { '{0:0.0}' -f $_ }) -join ' ')
if ($hv.Count -ge 8) {
    $w = [math]::Floor($hv.Count / 3); $early = ($hv[$w..(2 * $w - 1)] | Measure-Object -Average).Average; $late = ($hv[($hv.Count - $w)..($hv.Count - 1)] | Measure-Object -Average).Average
    $rate = ($late - $early) / ($Minutes / 2) * 60
    Write-Host ('retained heap: early {0:0.0} MB, late {1:0.0} MB ({2:+0.0;-0.0} MB/h)' -f $early, $late, $rate)
    if ($rate -gt $MaxGrowthMBPerHour) { Write-Host 'FAIL retained managed heap keeps growing'; $fails++ } else { Write-Host 'PASS retained managed heap flat' }
} else { Write-Host 'FAIL too few heap samples (memlog rows missing)'; $fails++ }
# Memory is a sawtooth (garbage piles up, then a collection drops it): a straight line through it follows the last tooth.
# A leak shows in the LOW points: compare the lowest sample of the first and second half after the warm-up.
$half = [math]::Floor($after.Count / 2)
if ($after.Count -lt 4) { Write-Host 'FAIL too few samples after warm-up (run longer)'; $fails++ }
else {
    $low1 = ($after[0..($half - 1)].PrivateMB | Measure-Object -Minimum).Minimum
    $low2 = ($after[$half..($after.Count - 1)].PrivateMB | Measure-Object -Minimum).Minimum
    $span = $after[$after.Count - 1].Min - $after[0].Min
    $troughRate = ($low2 - $low1) / [math]::Max($span / 2, 1) * 60
    Write-Host ("low points: first half {0} MB, second half {1} MB ({2:+0.0;-0.0} MB/h)" -f $low1, $low2, $troughRate)
    if ($troughRate -gt $MaxGrowthMBPerHour) { Write-Host "FAIL low points keep rising ($([math]::Round($troughRate,1)) MB/h > $MaxGrowthMBPerHour)"; $fails++ }
    else { Write-Host 'PASS low points flat after warm-up (no retained growth)' }
}
if ($hSlope -gt 500) { Write-Host 'FAIL handle count keeps growing'; $fails++ } else { Write-Host 'PASS handles flat' }
if ($tSlope -gt 10) { Write-Host 'FAIL thread count keeps growing'; $fails++ } else { Write-Host 'PASS threads flat' }
if ($errors -gt 5) { Write-Host "FAIL $errors bridge requests failed"; $fails++ }
Write-Host 'app turned off'
exit $fails
