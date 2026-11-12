#requires -Version 7
# End to end in the real app: a playlist report shows the 셔플 button, pressing it queues "shuffle:on" for the extension,
# an "on" report turns it into "shuffle:off", and a report without a playlist hides it.
#   pwsh tools\shuffle-button-check.ps1 [-Exe <LyricsOnTheGo.exe>]   (restarts the app; leaves it running)
param([string]$Exe)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = (Resolve-Path (Join-Path $PSScriptRoot '..\app\src\LyricsOnTheGo\bin\Release\net8.0-windows10.0.19041.0\LyricsOnTheGo.exe')).Path }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$fails = 0
function Check($name, $ok, $extra = '') { Write-Host ("{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $extra); if (-not $ok) { $script:fails++ } }
function WaitFor([scriptblock]$c, $sec = 12) { $end = (Get-Date).AddSeconds($sec); while ((Get-Date) -lt $end) { if (& $c) { return $true }; Start-Sleep -Milliseconds 250 }; $false }

Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' } | Stop-Process -Force; Start-Sleep 2
Set-Content (Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\launch-ticket') ([DateTime]::UtcNow.Ticks)   # as the extension's "앱 켜기" does
$proc = Start-Process $Exe -PassThru
[void](WaitFor { Test-NetConnection 127.0.0.1 -Port 47831 -WarningAction SilentlyContinue -InformationLevel Quiet })
$tok = (Get-Content (Join-Path $env:LOCALAPPDATA 'LyricsOnTheGo\settings.json') -Raw | ConvertFrom-Json).BridgeToken
$h = @{ 'X-Bridge-Token' = $tok; Origin = 'chrome-extension://khoankekndekkeoamaopgjhpogbooldk' }
function Report($shuffle) {
    $o = @{ videoId = 'shuf0000001'; title = 'Shuffle Test'; channel = 'Ch'; durationMs = 200000; positionMs = 1000; paused = $false; isAd = $false; isMusic = $true; sentAtMs = 1 }
    if ($null -ne $shuffle) { $o.shuffle = $shuffle }
    (Invoke-RestMethod 'http://127.0.0.1:47831/v1/now-playing' -Method POST -Headers $h -ContentType 'application/json' -Body ($o | ConvertTo-Json -Compress)).commands
}
function Button {
    $win = [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    if (-not $win) { return $null }
    $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ShuffleButton')))
}
function Visible { $b = Button; $b -and -not $b.Current.IsOffscreen -and $b.Current.BoundingRectangle.Width -gt 0 }
function Press { ((Button).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }

[void](Report $null); Start-Sleep 1
Check 'old extension (no shuffle field) -> no button' (-not (Visible))
[void](Report ''); Start-Sleep 1
Check 'not a playlist -> no button' (-not (Visible))
[void](Report 'off')
Check 'playlist -> button shown' (WaitFor { Visible } 5)
Press; $c = Report 'off'
Check 'press while off -> extension gets shuffle:on' (@($c) -contains 'shuffle:on') ($c -join ',')
[void](Report 'on'); Start-Sleep 1
Press; $c = Report 'on'
Check 'press while on -> extension gets shuffle:off' (@($c) -contains 'shuffle:off') ($c -join ',')
[void](Report 'busy'); Start-Sleep 1
Press; $c = Report 'busy'
Check 'press while reading the list -> nothing sent' (@($c).Count -eq 0) ($c -join ',')
[void](Report ('x' * 41)); Start-Sleep 1
Check 'oversized shuffle value is ignored (no button)' (-not (Visible))
[void](Report ''); Start-Sleep 1
Check 'left the playlist -> button hidden again' (-not (Visible))

Write-Host ($(if ($fails -eq 0) { 'ALL PASS' } else { "$fails FAILED" }))
exit $fails
