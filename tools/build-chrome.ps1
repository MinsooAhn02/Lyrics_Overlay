#requires -Version 7
# Builds the Chrome package: dist-release\lyrics-overlay-bridge-chrome.zip (unzip, then chrome://extensions -> Load unpacked).
# The manifest keeps its "key", so the extension id stays khoankekndekkeoamaopgjhpogbooldk (the app only accepts that id).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ext  = Join-Path $root 'extension'
$out  = Join-Path $root 'dist-release'
$zip  = Join-Path $out 'lyrics-overlay-bridge-chrome.zip'

$files = 'manifest.json', 'background.js', 'content.js', 'main-world.js', 'popup.html', 'popup.js', 'popup.css'
foreach ($f in $files) { if (-not (Test-Path -LiteralPath (Join-Path $ext $f) -PathType Leaf)) { throw "Missing source file: $f" } }
$icons = @(Get-ChildItem -LiteralPath (Join-Path $ext 'icons') -Filter *.png -File)
if ($icons.Count -eq 0) { throw 'No icons found in extension\icons' }

New-Item -ItemType Directory -Path $out -Force | Out-Null
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

$za = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in $files) { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, (Join-Path $ext $f), $f, 'Optimal') }
    foreach ($i in $icons) { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $i.FullName, "icons/$($i.Name)", 'Optimal') }
} finally { $za.Dispose() }

# ---- self check ----
$za = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($za.Entries | ForEach-Object FullName)
    if ($names | Where-Object { $_ -match '\\' }) { throw 'Zip entry contains a backslash' }
    foreach ($n in $files) { if ($names -notcontains $n) { throw "Zip is missing $n" } }
    if ($names | Where-Object { $_ -match 'id-info|README|manifest\.firefox|dist' }) { throw 'Zip contains an excluded file' }
    $sr = [System.IO.StreamReader]::new($za.GetEntry('manifest.json').Open())
    try { $m = $sr.ReadToEnd() | ConvertFrom-Json } finally { $sr.Dispose() }
} finally { $za.Dispose() }

if (-not $m.key) { throw 'manifest has no "key": the extension id would change' }
if ($m.background.service_worker -ne 'background.js') { throw 'manifest.background.service_worker missing' }
if ($m.PSObject.Properties.Name -contains 'browser_specific_settings') { throw 'manifest still has Firefox settings' }
Write-Host "entries: $($names.Count)  version: $($m.version)  min chrome: $($m.minimum_chrome_version)"
Write-Host "OK: $zip"
