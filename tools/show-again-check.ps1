#requires -Version 7
# "앱 켜기" while the overlay is hidden in the tray must bring it back.
#   pwsh tools\show-again-check.ps1 [-Exe <LyricsOverlay.exe>]
# Restarts the app, closes the overlay to the tray (X button), sends the extension's "start" through the real native
# host, and checks the window is visible again. Turns the app OFF at the end.
param([string]$Exe)
$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = (Resolve-Path (Join-Path $PSScriptRoot '..\dist-release\LyricsOverlay\LyricsOverlay.exe')).Path }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace SA -Name U -MemberDefinition '[DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool P(IntPtr h, IntPtr l); [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
[DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
public static bool OverlayVisible(uint pid) { bool v = false; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
  if (p == pid) { var sb = new System.Text.StringBuilder(64); GetWindowText(h, sb, 64); if (sb.ToString() == "LyricsOnTheGo" && IsWindowVisible(h)) v = true; } return true; }, IntPtr.Zero); return v; }' -ErrorAction SilentlyContinue

function Host-Start {   # one native-messaging round trip, exactly like the browser does it
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe, 'chrome-extension://khoankekndekkeoamaopgjhpogbooldk/')
    $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $h = [Diagnostics.Process]::Start($psi)
    $msg = [Text.Encoding]::UTF8.GetBytes('{"cmd":"start"}')
    $h.StandardInput.BaseStream.Write([BitConverter]::GetBytes($msg.Length)); $h.StandardInput.BaseStream.Write($msg); $h.StandardInput.BaseStream.Flush()
    $len = [byte[]]::new(4); [void]$h.StandardOutput.BaseStream.ReadExactly($len)
    $buf = [byte[]]::new([BitConverter]::ToInt32($len, 0)); [void]$h.StandardOutput.BaseStream.ReadExactly($buf)
    $h.StandardInput.Close(); [void]$h.WaitForExit(5000)
    [Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json
}
function WaitFor([scriptblock]$cond, [int]$sec = 5) { $end = (Get-Date).AddSeconds($sec); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 200 }; $false }

$fails = 0
function Check($name, $ok, $detail) { if (-not $ok) { $script:fails++ }; Write-Host ($(if ($ok) { 'PASS ' } else { 'FAIL ' }) + $name + $(if ($detail) { "  ->  $detail" })) }

Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' } | Stop-Process -Force; Start-Sleep 2
$r = Host-Start
Check 'start when off -> started' ($r.ok -and $r.started) ($r | ConvertTo-Json -Compress)
$p = Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' } | Select-Object -First 1
Check 'overlay visible after start' (WaitFor { [SA.U]::OverlayVisible($p.Id) })

$win = [Windows.Automation.AutomationElement]::RootElement.FindFirst('Children', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
$close = $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'CloseButton')))
$close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
Check 'X hides the overlay to the tray (app keeps running)' ((WaitFor { -not [SA.U]::OverlayVisible($p.Id) }) -and -not $p.HasExited)

$r = Host-Start
Check 'start while hidden -> not started again, shown' ($r.ok -and -not $r.started -and $r.shown) ($r | ConvertTo-Json -Compress)
Check 'overlay visible again' (WaitFor { [SA.U]::OverlayVisible($p.Id) })
Check 'still one app process' (@(Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' }).Count -eq 1)

$r = Host-Start
Check 'start while already visible -> stays visible' ($r.ok -and $r.shown -and (WaitFor { [SA.U]::OverlayVisible($p.Id) })) ($r | ConvertTo-Json -Compress)

$p | Stop-Process -Force
Write-Host 'app turned off'
Write-Host $(if ($fails) { "$fails FAILED" } else { 'ALL PASS' })
exit $fails
