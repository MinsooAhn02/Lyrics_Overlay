#requires -Version 7
# Checks the "start / stop the app from the extension" feature end to end against a built exe.
#   pwsh tools\native-host-check.ps1 [-Exe <path to LyricsOnTheGo.exe / LyricsOverlay.exe>]
# It STOPS every running LyricsOnTheGo process first, and leaves the app running + registered at the end.
param([string]$Exe)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = (Resolve-Path (Join-Path $PSScriptRoot '..\app\src\LyricsOnTheGo\bin\Release\net8.0-windows10.0.19041.0\LyricsOnTheGo.exe')).Path }
$Origin  = 'chrome-extension://khoankekndekkeoamaopgjhpogbooldk/'
$HostKey = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.lyricsoverlay.host'
$FfKey   = 'HKCU:\Software\Mozilla\NativeMessagingHosts\com.lyricsoverlay.host'
$Dir     = Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\native-host'
$fails = 0
function Check($name, $ok, $extra = '') { Write-Host ("{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $extra); if (-not $ok) { $script:fails++ } }
function AppProcs { @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue) }
function PortOpen { Test-NetConnection 127.0.0.1 -Port 47831 -WarningAction SilentlyContinue -InformationLevel Quiet }
function WaitFor([scriptblock]$cond, $sec = 12) { $end = (Get-Date).AddSeconds($sec); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }; return $false }
function StopApp { AppProcs | Stop-Process -Force; [void](WaitFor { -not (PortOpen) } 8) }

# ---- minimal native-messaging client: spawns the exe the way a browser does and speaks the framed JSON protocol ----
function New-Host([string[]]$HostArgs = @($Origin)) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in $HostArgs) { $psi.ArgumentList.Add($a) }
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    [Diagnostics.Process]::Start($psi)
}
function Send-Frame($p, [byte[]]$payload, [Nullable[int]]$lenOverride = $null) {
    $len = if ($null -ne $lenOverride) { [int]$lenOverride } else { $payload.Length }
    $s = $p.StandardInput.BaseStream
    $s.Write([BitConverter]::GetBytes($len), 0, 4); if ($payload.Length) { $s.Write($payload, 0, $payload.Length) }; $s.Flush()
}
function Read-Frame($p, $timeoutMs = 15000) {
    $s = $p.StandardOutput.BaseStream
    $read = { param($n) $b = [byte[]]::new($n); $got = 0
        while ($got -lt $n) { $t = $s.ReadAsync($b, $got, $n - $got); if (-not $t.Wait($timeoutMs)) { return $null }; if ($t.Result -le 0) { return $null }; $got += $t.Result }; $b }
    $h = & $read 4; if (-not $h) { return $null }
    $b = & $read ([BitConverter]::ToInt32($h, 0)); if (-not $b) { return $null }
    [Text.Encoding]::UTF8.GetString($b) | ConvertFrom-Json
}
function Ask($cmdJson, [string[]]$HostArgs = @($Origin)) {
    $p = New-Host $HostArgs
    try { Send-Frame $p ([Text.Encoding]::UTF8.GetBytes($cmdJson)); $r = Read-Frame $p; $p.StandardInput.Close(); [void]$p.WaitForExit(5000); [pscustomobject]@{ Reply = $r; Exited = $p.HasExited; Code = $(if ($p.HasExited) { $p.ExitCode }) } }
    finally { if (-not $p.HasExited) { $p.Kill() } }
}
function PostQuit($headers) {
    try { [int](Invoke-WebRequest 'http://127.0.0.1:47831/v1/quit' -Method POST -Headers $headers -ContentType 'application/json' -Body '{}' -UseBasicParsing).StatusCode }
    catch { [int]$_.Exception.Response.StatusCode }
}

$Ticket = Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\launch-ticket'
# A plain launch of an already-registered exe must NOT open the overlay (it shows a notice and exits on OK).
function PlainLaunchBlocked {
    $p = Start-Process $Exe -PassThru; Start-Sleep 5
    $blocked = -not (PortOpen) -and -not $p.HasExited   # waiting on the notice, bridge never opened
    if (-not $p.HasExited) { $p.Kill() }; [void](WaitFor { (AppProcs).Count -eq 0 } 5); $blocked
}

Write-Host "exe: $Exe"
StopApp
Start-Process $Exe '--unregister-native-host'; [void](WaitFor { -not (Test-Path $HostKey) } 8)
Check 'nothing running and not registered at the start' ((AppProcs).Count -eq 0 -and -not (PortOpen) -and -not (Test-Path $HostKey))

# 1. first plain launch registers the host and opens the overlay (the only plain launch that does)
Start-Process $Exe
Check 'app starts (bridge port opens)' (WaitFor { PortOpen })
Check 'registry: Chrome key set' (Test-Path $HostKey)
Check 'registry: Firefox key set' (Test-Path $FfKey)
$chromeJson = (Get-ItemProperty $HostKey).'(default)'; $ffJson = (Get-ItemProperty $FfKey).'(default)'
$cj = Get-Content $chromeJson -Raw | ConvertFrom-Json; $fj = Get-Content $ffJson -Raw | ConvertFrom-Json
Check 'Chrome manifest: path is this exe, only our extension may call it' ($cj.path -eq $Exe -and $cj.allowed_origins.Count -eq 1 -and $cj.allowed_origins[0] -eq $Origin -and $cj.type -eq 'stdio' -and $cj.name -eq 'com.lyricsoverlay.host')
Check 'Firefox manifest: path is this exe, only our extension id' ($fj.path -eq $Exe -and $fj.allowed_extensions.Count -eq 1 -and $fj.allowed_extensions[0] -eq 'lyrics-overlay-bridge@local')
Start-Process $Exe; Start-Sleep -Seconds 3
Check 'second launch does not create a second overlay' ((AppProcs).Count -eq 1) "(processes: $((AppProcs).Count))"

# 2. host protocol while the app runs
$r = Ask '{"cmd":"status"}'
Check 'host: status -> running' ($r.Reply.ok -eq $true -and $r.Reply.running -eq $true)
# Firefox starts the host as: <exe> <path of the host manifest> <extension id>
$r = Ask '{"cmd":"status"}' @((Join-Path $Dir 'com.lyricsoverlay.host.firefox.json'), 'lyrics-overlay-bridge@local')
Check 'host: Firefox-style launch (manifest path + extension id) works too' ($r.Reply.ok -eq $true -and $r.Reply.running -eq $true -and $r.Exited -and $r.Code -eq 0)
Check 'host: exits by itself when the browser closes the pipe' ($r.Exited -eq $true -and $r.Code -eq 0) "(exit=$($r.Code))"
$r = Ask '{"cmd":"start"}'
Check 'host: start while running -> no second app' ($r.Reply.ok -eq $true -and $r.Reply.started -eq $false)
Check 'still exactly one overlay process' ((AppProcs).Count -eq 1)
$r = Ask '{"cmd":"format-c"}'
Check 'host: unknown command refused' ($r.Reply.ok -eq $false -and $r.Reply.error -eq 'unknown command')
$r = Ask 'this is not json'
Check 'host: invalid JSON refused, no crash' ($r.Reply.ok -eq $false)
$r = Ask '[1,2,3]'
Check 'host: JSON that is not an object refused' ($r.Reply.ok -eq $false)
$p = New-Host
try { Send-Frame $p ([byte[]]@()) 100000; $rep = Read-Frame $p; [void]$p.WaitForExit(5000)
      Check 'host: absurd message length refused and host exits' ($rep.ok -eq $false -and $p.HasExited) } finally { if (-not $p.HasExited) { $p.Kill() } }
$p = New-Host
try { $p.StandardInput.Close(); Check 'host: immediate EOF -> clean exit' ($p.WaitForExit(5000) -and $p.ExitCode -eq 0) } finally { if (-not $p.HasExited) { $p.Kill() } }
Check 'only the overlay is left running after all host calls' ((AppProcs).Count -eq 1)

# 3. quit endpoint: authenticated like everything else
$tok = (Get-Content (Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\settings.json') -Raw | ConvertFrom-Json).BridgeToken
$good = @{ 'X-Bridge-Token' = $tok; Origin = $Origin.TrimEnd('/') }
Check 'quit without token -> 401' ((PostQuit @{ Origin = $Origin.TrimEnd('/') }) -eq 401)
Check 'quit with wrong token -> 401' ((PostQuit @{ 'X-Bridge-Token' = 'nope'; Origin = $Origin.TrimEnd('/') }) -eq 401)
Check 'quit from a web page origin -> 403' ((PostQuit @{ 'X-Bridge-Token' = $tok; Origin = 'https://evil.example' }) -eq 403)
Check 'app still alive after the refused attempts' ((AppProcs).Count -eq 1 -and (PortOpen))
Check 'quit with token -> 200' ((PostQuit $good) -eq 200)
Check 'app exits after an authorized quit' (WaitFor { (AppProcs).Count -eq 0 } 10)
Check 'bridge port closed' (-not (PortOpen))

# 3b. plain launches are refused once registered; an old ticket does not count
Check 'plain launch (already registered) does not open the overlay' (PlainLaunchBlocked)
Set-Content $Ticket ([DateTime]::UtcNow.AddMinutes(-5).Ticks)
Check 'plain launch with a 5-minute-old ticket does not open the overlay' (PlainLaunchBlocked)
Check 'old ticket was consumed' (-not (Test-Path $Ticket))
Set-Content $Ticket 'garbage'
Check 'plain launch with a garbage ticket does not open the overlay' (PlainLaunchBlocked)

# 4. host can start the app from nothing
$r = Ask '{"cmd":"status"}'
Check 'host: status -> not running' ($r.Reply.ok -eq $true -and $r.Reply.running -eq $false)
$r = Ask '{"cmd":"start"}'
Check 'host: start -> app comes up' ($r.Reply.ok -eq $true -and $r.Reply.started -eq $true) ($r.Reply | ConvertTo-Json -Compress)
Check 'bridge port open after start' (WaitFor { PortOpen } 10)
$r = Ask '{"cmd":"start"}'
Check 'host: second start does nothing' ($r.Reply.ok -eq $true -and $r.Reply.started -eq $false)
Check 'exactly one overlay process' ((AppProcs).Count -eq 1) "(processes: $((AppProcs).Count))"
$survivor = (AppProcs)[0]
Check 'started app is not a child of the host (survives its exit)' ($null -ne $survivor -and -not $survivor.HasExited)
Check 'launch ticket was consumed by the started app' (-not (Test-Path $Ticket))

# 5. unregister removes everything we created
StopApp
Start-Process $Exe '--unregister-native-host'; [void](WaitFor { -not (Test-Path $HostKey) } 8)
Check 'unregister: registry keys gone' (-not (Test-Path $HostKey) -and -not (Test-Path $FfKey) -and -not (Test-Path 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.lyricsoverlay.host'))
Check 'unregister: manifest files gone' (-not (Test-Path $Dir))
Check 'unregister: did not start the overlay' ((AppProcs).Count -eq 0 -and -not (PortOpen))

# leave the machine in the normal state: app running and registered again (first launch after unregister opens it)
Start-Process $Exe; [void](WaitFor { PortOpen })
Check 'app registers itself again and opens on the first launch after unregister' ((Test-Path $HostKey) -and (PortOpen))

Write-Host ($(if ($fails -eq 0) { 'ALL PASS' } else { "$fails FAILED" }))
exit $fails
