#requires -Version 7
# Builds everything that goes into a GitHub release, into dist-release\ (git-ignored):
#   LyricsOverlay-win-x64.zip                      app: one exe + licenses + 사용법.txt
#   lyrics-overlay-bridge-chrome.zip               Chrome extension (load unpacked)
#   lyrics-overlay-bridge-firefox-unsigned.zip     what to upload to addons.mozilla.org (unlisted) to get the signed .xpi
# It never touches running processes: stop the overlay first (the build replaces files it may have open).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out  = Join-Path $root 'dist-release'
$pkg  = Join-Path $out 'LyricsOverlay'
$running = @(Get-Process | Where-Object { $_.Name -match '^Lyrics(OnTheGo|Overlay)$' })
if ($running.Count) { throw "Stop the running overlay first: $($running.Name -join ', ') (pid $($running.Id -join ', '))" }

New-Item -ItemType Directory $out -Force | Out-Null
foreach ($p in $pkg, (Join-Path $out 'single'), (Join-Path $out 'LyricsOverlay-win-x64.zip')) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }

# ---- app: self-contained single file (fonts are embedded; nothing else is needed next to the exe) ----
dotnet publish (Join-Path $root 'app\src\LyricsOnTheGo\LyricsOnTheGo.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $out 'single') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$exe = Join-Path $out 'single\LyricsOnTheGo.exe'
if (-not (Test-Path $exe)) { throw 'published exe missing' }

New-Item -ItemType Directory $pkg | Out-Null
Copy-Item $exe (Join-Path $pkg 'LyricsOverlay.exe')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $pkg 'LICENSE.txt')
Copy-Item (Join-Path $root 'app\LICENSE') (Join-Path $pkg 'LICENSE-LyricsOnTheGo.txt')
Copy-Item (Join-Path $root 'app\src\LyricsOnTheGo\Fonts\LICENSE-Pretendard.txt') (Join-Path $pkg 'LICENSE-Pretendard.txt')
@'
Lyrics Overlay 사용법
=====================

1) LyricsOverlay.exe 를 더블클릭해서 실행합니다. (설치 필요 없음, .NET 포함)
   - 처음 실행할 때 Windows 가 "PC 보호" 경고를 띄우면 [추가 정보] -> [실행] 을 누르세요.
     (서명되지 않은 프로그램이라 나오는 경고입니다.)
   - 처음에는 5초쯤 걸릴 수 있습니다.
   - 가사 창의 X 는 종료가 아니라 "트레이로 숨기기" 입니다. 완전히 끄려면 확장 팝업의 [앱 끄기] 를 누르거나
     작업표시줄 트레이 아이콘을 우클릭해서 [종료] 를 누르세요.

2) 브라우저 확장을 설치합니다.
   - Chrome : lyrics-overlay-bridge-chrome.zip 을 풀고 chrome://extensions 에서
              [개발자 모드] 켜기 -> [압축해제된 확장 프로그램을 로드합니다] -> 푼 폴더 선택
   - Firefox: .xpi 파일을 Firefox 창에 끌어다 놓고 [추가]

3) 앱 설정(톱니바퀴)에서 토큰을 복사해 확장 아이콘 팝업에 붙여넣고 [저장] -> [연결 테스트].
   배지가 ON 이면 유튜브에서 노래를 재생하세요.

4) 앱을 끈 뒤에는 확장 팝업의 [앱 켜기] 로 다시 켤 수 있습니다.
   (앱을 한 번이라도 직접 실행했어야 합니다. 그때 브라우저가 앱을 켤 수 있도록 현재 사용자 계정에 등록됩니다.)
   등록을 지우려면:  LyricsOverlay.exe --unregister-native-host

문제가 있으면: https://github.com/MinsooAhn02/Lyrics_Overlay
이 프로그램은 재생 정보를 내 PC 안(127.0.0.1)에서만 주고받고, 가사 검색을 위해 곡명/아티스트를 LRCLIB 로 보냅니다.
'@ | Set-Content (Join-Path $pkg '사용법.txt') -Encoding UTF8
Compress-Archive -Path (Join-Path $pkg '*') -DestinationPath (Join-Path $out 'LyricsOverlay-win-x64.zip') -Force

# ---- extensions ----
& (Join-Path $PSScriptRoot 'build-firefox.ps1') | Out-Null
Copy-Item (Join-Path $root 'extension\dist\lyrics-overlay-bridge-firefox.zip') (Join-Path $out 'lyrics-overlay-bridge-firefox-unsigned.zip') -Force
& (Join-Path $PSScriptRoot 'build-chrome.ps1') | Out-Null

Write-Host 'Built:'
Get-ChildItem $out -File | ForEach-Object { Write-Host ('  {0,-48} {1,8:N2} MB' -f $_.Name, ($_.Length / 1MB)) }
