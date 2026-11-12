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

1) 이 폴더를 계속 둘 곳(다운로드 폴더 말고)에 두고, LyricsOverlay.exe 를 한 번 더블클릭합니다.
   (설치 필요 없음, .NET 포함) 이때 브라우저에 등록되고 앱이 켜집니다.
   - Windows 가 "PC 보호" 경고를 띄우면 [추가 정보] -> [실행] 을 누르세요. (서명되지 않은 프로그램이라 나오는 경고)
   - 처음에는 5초쯤 걸릴 수 있습니다.
   - 그 뒤로는 확장 팝업의 [앱 켜기] 로만 켭니다. exe 를 다시 더블클릭하면 안내만 뜹니다.
   - exe 를 옮겼다면 새 위치에서 한 번 더블클릭하면 다시 등록됩니다.
   - 가사 창의 X 는 종료가 아니라 "트레이로 숨기기" 입니다. 완전히 끄려면 확장 팝업의 [앱 끄기] 를 누르거나
     작업표시줄 트레이 아이콘을 우클릭해서 [종료] 를 누르세요.

2) 브라우저 확장을 설치합니다.
   - Chrome : lyrics-overlay-bridge-chrome.zip 을 풀고 chrome://extensions 에서
              [개발자 모드] 켜기 -> [압축해제된 확장 프로그램을 로드합니다] -> 푼 폴더 선택
   - Firefox: .xpi 파일을 Firefox 창에 끌어다 놓고 [추가] (서명본은 준비되는 대로 Releases 에 올라옵니다)

3) 앱 설정(톱니바퀴)에서 토큰을 복사해 확장 아이콘 팝업에 붙여넣고 [저장] -> [연결 테스트].
   배지가 ON 이면 유튜브에서 노래를 재생하세요.

4) 앱 켜기/끄기는 확장 팝업의 [앱 켜기] / [앱 끄기] 로 합니다. 자동 실행은 없습니다.
   등록을 지우려면:  LyricsOverlay.exe --unregister-native-host

5) 유튜브 재생목록에서는 가사 창 아래 셔플 아이콘으로 재생목록 전체를 한 번씩 섞어 들을 수 있습니다.

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
