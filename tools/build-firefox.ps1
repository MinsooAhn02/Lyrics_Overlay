#requires -Version 7
# Builds the pre-signing Firefox package: extension\dist\firefox\ and extension\dist\lyrics-overlay-bridge-firefox.zip
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$ext  = (Resolve-Path (Join-Path $PSScriptRoot '..\extension')).Path
$dist = Join-Path $ext 'dist'
$out  = Join-Path $dist 'firefox'
$zip  = Join-Path $dist 'lyrics-overlay-bridge-firefox.zip'

$files = 'background.js', 'content.js', 'main-world.js', 'popup.html', 'popup.js', 'popup.css'
foreach ($f in ($files + 'manifest.firefox.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ext $f) -PathType Leaf)) { throw "Missing source file: $f" }
}
$icons = @(Get-ChildItem -LiteralPath (Join-Path $ext 'icons') -Filter *.png -File)
if ($icons.Count -eq 0) { throw 'No icons found in extension\icons' }

# Clean only inside dist\ (never outside it)
if (Test-Path -LiteralPath $dist) {
    Get-ChildItem -LiteralPath $dist -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $dist | Out-Null
}
New-Item -ItemType Directory -Path (Join-Path $out 'icons') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $ext 'manifest.firefox.json') -Destination (Join-Path $out 'manifest.json')
foreach ($f in $files) { Copy-Item -LiteralPath (Join-Path $ext $f) -Destination (Join-Path $out $f) }
foreach ($i in $icons) { Copy-Item -LiteralPath $i.FullName -Destination (Join-Path $out 'icons' $i.Name) }

# Zip with explicit '/' entry names (manifest.json at the zip root)
$za = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in Get-ChildItem -LiteralPath $out -Recurse -File) {
        $rel = $f.FullName.Substring($out.Length).TrimStart('\', '/').Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, 'Optimal')
    }
} finally { $za.Dispose() }

# ---- self check ----
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($za.Entries | ForEach-Object FullName)
    Write-Host 'Zip entries:'; $names | ForEach-Object { Write-Host "  $_" }
    if ($names | Where-Object { $_ -match '\\' }) { throw 'Zip entry contains a backslash' }
    if ($names -notcontains 'manifest.json') { throw 'manifest.json is not at the zip root' }
    foreach ($n in ($files + 'manifest.json')) { if ($names -notcontains $n) { throw "Zip is missing $n" } }
    if ($names | Where-Object { $_ -match 'id-info|README|test|manifest\.firefox' }) { throw 'Zip contains an excluded file' }
    $e = $za.GetEntry('manifest.json')
    $sr = [System.IO.StreamReader]::new($e.Open())
    try { $m = $sr.ReadToEnd() | ConvertFrom-Json } finally { $sr.Dispose() }
} finally { $za.Dispose() }

if ($m.background.PSObject.Properties.Name -contains 'service_worker') { throw 'manifest still has service_worker' }
if (@($m.background.scripts) -notcontains 'background.js') { throw 'background.scripts missing background.js' }
$g = $m.browser_specific_settings.gecko
if ($g.id -ne 'lyrics-overlay-bridge@local') { throw 'gecko.id wrong' }
if ($m.PSObject.Properties.Name -contains 'key') { throw 'manifest still has key' }
Write-Host "background.scripts      : $($m.background.scripts -join ', ')"
Write-Host "gecko.id                : $($g.id)"
Write-Host "gecko.strict_min_version: $($g.strict_min_version)"
Write-Host "data_collection_perms   : $($g.data_collection_permissions.required -join ', ')"
Write-Host "OK: $zip"
