#Requires -Version 7
# Static smoke test for the BrowserBridge HTTP server. Start the app first.
# Usage: pwsh tools\test-bridge.ps1 -Token <token from app settings>
param(
    [Parameter(Mandatory)] [string] $Token,
    [string] $ExtensionId = 'khoankekndekkeoamaopgjhpogbooldk'
)

$base = 'http://127.0.0.1:47831/v1/now-playing'
$origin = "chrome-extension://$ExtensionId"
$good = @{ 'X-Bridge-Token' = $Token; 'Origin' = $origin }
$json = 'application/json'
$valid = '{"videoId":"dQw4w9WgXcQ","title":"t","channel":"c","durationMs":200000,"positionMs":1000,"paused":false,"isAd":false,"isMusic":true,"sentAtMs":1}'
$results = @()

function Invoke-Case {
    param([string]$Name, [int[]]$Expect, [string]$Method = 'POST', [string]$Uri = $base,
          [hashtable]$Headers = $good, [string]$ContentType = $json, $Body = '{"ping":true}', [scriptblock]$Check)
    $status = -1; $content = ''
    try {
        $p = @{ Uri = $Uri; Method = $Method; Headers = $Headers; SkipHttpErrorCheck = $true; TimeoutSec = 5 }
        if ($Method -eq 'POST') { $p.Body = $Body; $p.ContentType = $ContentType }
        $r = Invoke-WebRequest @p
        $status = [int]$r.StatusCode; $content = [string]$r.Content
    } catch { $content = $_.Exception.Message }
    $ok = ($Expect -contains $status)
    if ($ok -and $Check) { $ok = [bool](& $Check $content) }
    $script:results += [pscustomobject]@{ Case = $Name; Expect = ($Expect -join '/'); Got = $status; Pass = $ok }
}

Invoke-Case 'ping ok -> 200 + commands array' 200 -Check { param($c) $c -match '^\{"commands":\[' }
Invoke-Case 'valid payload -> 200 + commands' 200 -Body $valid -Check { param($c) $c -match '^\{"commands":\[' }
Invoke-Case 'valid payload with track/artist -> 200' 200 -Body ($valid -replace '"sentAtMs":1', '"sentAtMs":1,"track":"Blueming","artist":"IU","album":"Love poem","metaSource":"ytm"') -Check { param($c) $c -match '^\{"commands":\[' }
Invoke-Case 'artist 201 chars -> 400' 400 -Body ($valid -replace '"sentAtMs":1', ('"sentAtMs":1,"artist":"' + ('a' * 201) + '"'))
Invoke-Case 'no token -> 401' 401 -Headers @{ 'Origin' = $origin }
Invoke-Case 'wrong token -> 401' 401 -Headers @{ 'X-Bridge-Token' = 'wrong'; 'Origin' = $origin }
Invoke-Case 'wrong origin -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
Invoke-Case 'web origin -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'https://www.youtube.com' }
Invoke-Case 'no origin -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token }
Invoke-Case 'wrong Host header -> 400/403' @(400, 403) -Headers ($good + @{ 'Host' = 'evil.example:47831' }) # HTTP.sys may answer 400 itself
Invoke-Case 'GET -> 405' 405 -Method GET
Invoke-Case 'wrong path -> 404' 404 -Uri 'http://127.0.0.1:47831/v1/other'
Invoke-Case 'text/plain -> 415' 415 -ContentType 'text/plain'
Invoke-Case '9KB body -> 413' 413 -Body ('{"pad":"' + ('a' * 9300) + '"}')
Invoke-Case 'broken JSON -> 400' 400 -Body '{"videoId":'
Invoke-Case 'bad videoId -> 400' 400 -Body ($valid -replace 'dQw4w9WgXcQ', 'short')
Invoke-Case 'negative position -> 400' 400 -Body ($valid -replace '"positionMs":1000', '"positionMs":-5')
Invoke-Case 'OPTIONS good origin -> 204' 204 -Method OPTIONS -Headers @{ 'Origin' = $origin }
Invoke-Case 'OPTIONS bad origin -> 403' 403 -Method OPTIONS -Headers @{ 'Origin' = 'https://evil.example' }

# Firefox extension origins: moz-extension://<lowercase uuid>, anchored. The token is still required.
$moz = 'moz-extension://12345678-1234-1234-1234-123456789abc'
Invoke-Case 'moz origin + token -> 200' 200 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = $moz } -Check { param($c) $c -match '^\{"commands":\[' }
Invoke-Case 'moz origin OPTIONS -> 204' 204 -Method OPTIONS -Headers @{ 'Origin' = $moz }
Invoke-Case 'moz origin, no token -> 401' 401 -Headers @{ 'Origin' = $moz }
Invoke-Case 'moz origin, wrong token -> 401' 401 -Headers @{ 'X-Bridge-Token' = 'wrong'; 'Origin' = $moz }
Invoke-Case 'moz UPPERCASE uuid -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'moz-extension://12345678-1234-1234-1234-123456789ABC' }
Invoke-Case 'moz short uuid -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'moz-extension://12345678-1234-1234-1234-123456789ab' }
Invoke-Case 'moz uuid + extra char -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = ($moz + 'd') }
Invoke-Case 'moz uuid + path -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = ($moz + '/x') }
Invoke-Case 'moz scheme only -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'moz-extension://' }
Invoke-Case 'moz prefix variant -> 403' 403 -Headers @{ 'X-Bridge-Token' = $Token; 'Origin' = 'xmoz-extension://12345678-1234-1234-1234-123456789abc' }
Invoke-Case 'moz OPTIONS bad uuid -> 403' 403 -Method OPTIONS -Headers @{ 'Origin' = 'moz-extension://not-a-uuid' }

$results | Format-Table -AutoSize | Out-String | Write-Host
$fail = @($results | Where-Object { -not $_.Pass }).Count
Write-Host ("SUMMARY: {0} passed, {1} failed" -f ($results.Count - $fail), $fail)
exit ([int]($fail -gt 0))
