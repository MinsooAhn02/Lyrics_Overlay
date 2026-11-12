<p align="center">
  <img src="extension/icons/icon128.png" width="96" alt="">
</p>

<h1 align="center">Lyrics Overlay</h1>

<p align="center">유튜브에서 듣는 노래의 싱크 가사를 화면 위에 띄워 주는 Windows 앱</p>

<p align="center">
  <a href="https://github.com/MinsooAhn02/Lyrics_Overlay/releases/latest"><img src="https://img.shields.io/github/v/release/MinsooAhn02/Lyrics_Overlay" alt="release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/MinsooAhn02/Lyrics_Overlay" alt="license"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-blue" alt="platform">
</p>

브라우저 확장이 유튜브 탭의 재생 상태를 같은 PC의 앱으로 보내고, 앱이 가사를 찾아 재생 위치에 맞춰 보여 줍니다. 가사 창은 브라우저와 별개라서 다른 창을 쓰는 동안에도 계속 위에 떠 있습니다.

개인 프로젝트이고 아직 테스트 단계입니다.

## 기능

- 항상 위, 투명 배경 가사 창
- 가사 출처: 유튜브 공식 자막 → 로컬 캐시 → [LRCLIB](https://lrclib.net)
- 시간 정보가 없거나 어긋난 가사는 영상의 자동 생성 자막에 맞춰 싱크 보정
- 설명란에 타임스탬프가 있는 플레이리스트 영상은 곡별 가사를 이어 붙임
- 재생목록 전체 셔플 (유튜브 셔플은 불러온 일부만 섞음)
- 가사 창에서 재생 조작, 줄 더블클릭으로 이동

## 설치

필요한 것: Windows 10 (2004) 이상, Chrome 111+ 또는 Firefox 128+

앱과 확장이 둘 다 있어야 동작합니다. [Releases](https://github.com/MinsooAhn02/Lyrics_Overlay/releases/latest)에서 받으세요.

**앱**

1. `LyricsOverlay-win-x64.zip`을 계속 둘 폴더에 풉니다. .NET 설치는 필요 없습니다.
2. `LyricsOverlay.exe`를 한 번 실행하면 브라우저에 등록되고 가사 창이 뜹니다.

서명되지 않은 exe라서 SmartScreen 경고가 나오면 *추가 정보 → 실행*을 누르세요.

**확장**

- Firefox: `lyrics-overlay-bridge-firefox-<버전>.xpi`를 Firefox 창에 끌어다 놓고 추가
- Chrome: `lyrics-overlay-bridge-chrome.zip`을 풀고 `chrome://extensions` → 개발자 모드 → *압축해제된 확장 프로그램 로드*

**연결**

앱 설정(톱니바퀴)의 토큰을 복사해 확장 팝업에 붙여 넣고 *저장* → *연결 테스트*를 누릅니다. 배지가 `ON`이 되면 유튜브에서 노래를 틀면 됩니다.

## 사용법

앱은 확장 팝업의 *앱 켜기* / *앱 끄기*로 켜고 끕니다. 처음 등록한 뒤에는 exe를 직접 실행해도 켜지지 않고 안내만 나옵니다. 가사 창의 X는 트레이로 숨기기이고, 숨긴 창은 *앱 켜기*로 다시 띄울 수 있습니다.

가사 창에 마우스를 올리면 아래에 버튼이 나옵니다.

| 버튼 | 동작 |
|---|---|
| `−5` `+5` | 5초 뒤로 / 앞으로 |
| 이전 · 재생 · 다음 | 유튜브 재생 조작 |
| `CC` `보정` `MIX` | 지금 쓰는 가사 출처(공식 자막 / 자막으로 보정 / 설명란 곡 목록). 누르면 이 영상에서만 끄고 켬 |
| `♪` | 음악이 아니라고 잘못 판단한 영상에서 가사 찾기 |
| 셔플 | 재생목록에서만 보임 |

싱크가 조금 어긋나면 설정의 가사 오프셋(±6초)으로 맞춥니다. 오프셋은 곡마다 저장됩니다.

**셔플**은 재생목록 전체를 읽어서 모든 곡이 한 번씩 나오게 섞고, 다 돌면 다시 섞습니다. 믹스(`RD…`), 비공개 재생목록, 유튜브 뮤직은 지원하지 않습니다.

## 동작 방식

```
유튜브 탭 ── 확장 ──▶ 127.0.0.1:47831 ──▶ Lyrics Overlay ──▶ LRCLIB
            (재생 상태, 자막)              (가사 창)          (곡명, 아티스트, 길이)
```

가사는 다음 순서로 찾습니다.

1. 유튜브 공식 자막. 사람이 올린 자막만 쓰고, `[Music]` 같은 태그뿐이거나 너무 짧은 자막은 건너뜁니다.
2. 로컬 캐시(`%LOCALAPPDATA%\LyricsOnTheGo\lyrics-cache`). 일주일이 지난 항목만 LRCLIB에서 다시 확인합니다.
3. LRCLIB. 곡명·아티스트·길이로 찾고, 영상 길이와 많이 다른 후보는 버립니다. 아티스트가 여러 명이라 못 찾으면 첫 번째 아티스트로 다시 찾습니다.

곡명과 아티스트는 유튜브 뮤직 정보, 설명란의 음악 정보, Topic 채널, 영상 제목 순서로 가져옵니다.

## 개인정보

- 확장은 유튜브 탭의 재생 상태와 자막을 같은 PC의 앱(`127.0.0.1`)에만 보냅니다.
- 앱은 가사 검색을 위해 곡명, 아티스트, 길이를 LRCLIB에 보냅니다. 그 외 외부 전송은 없습니다.
- 앱은 토큰이 맞고 허용된 확장에서 온 요청만 받습니다.
- 브라우저 등록은 현재 사용자 계정(`HKCU`)에만 하고 관리자 권한을 쓰지 않습니다. 등록을 지우려면 `LyricsOverlay.exe --unregister-native-host`를 실행하세요.

## 개발

```powershell
dotnet build app\LyricsOnTheGo.sln -c Release   # 앱
pwsh tools\build-firefox.ps1                     # Firefox 패키지 (extension\dist\)
pwsh tools\build-release.ps1                     # 배포 파일 (dist-release\, 앱을 끈 상태에서)
```

개발 중에는 Chrome에서 `extension` 폴더를 바로 로드하면 됩니다. Firefox는 `about:debugging`에서 `extension\dist\firefox\manifest.json`을 임시로 로드합니다.

```
app/         WPF 앱 (.NET 8)
extension/   Chrome / Firefox 확장 (Manifest V3)
tools/       빌드 스크립트와 검사 스크립트
```

`tools\`의 검사 스크립트:

- `node tools\<이름>-check.js`: 제목 정리, 음악 판별, 셔플, 자막 토큰, 팝업 등 확장 로직
- `dotnet run --project tools\display-check` 등: 앱 로직 (제목 표시, 시간 보정, 플레이리스트 가사)
- `pwsh tools\test-bridge.ps1 -Token <토큰>`: 앱 연결의 인증과 입력 검사
- `native-host-check.ps1`, `show-again-check.ps1`, `shuffle-button-check.ps1`, `leak-check.ps1`: 실제 앱을 켜고 끄며 검사합니다. 브라우저에서 음악을 멈춘 상태에서 돌리세요.

## 알려진 문제

- 유튜브가 자막 요청에 영상별 토큰을 요구해서 자막을 못 받는 영상이 있습니다. 이때는 LRCLIB 가사를 씁니다.
- 브라우저 탭이 뒤에 있으면 브라우저가 확장의 보고를 늦춰서 버튼 반응이 느릴 수 있습니다.
- 셔플은 Firefox에서만 충분히 확인했습니다.

## 기여

버그는 [Issues](https://github.com/MinsooAhn02/Lyrics_Overlay/issues)에 영상 링크와 증상을 적어 주세요. `%LOCALAPPDATA%\LyricsOnTheGo\captions.log`에서 해당 곡 부분을 같이 붙여 주면 원인을 찾기 쉽습니다. PR도 환영합니다.

## 라이선스

[MIT](LICENSE)

- `app/`은 [LyricsOnTheGo](https://github.com/The-MuffinDev/LyricsOnTheGo)(MIT, Luis Anchondo)를 바탕으로 했고 원 라이선스(`app/LICENSE`)를 유지합니다.
- 내장 글꼴 [Pretendard](https://github.com/orioncactus/pretendard): SIL OFL 1.1
- 가사의 저작권은 각 권리자에게 있습니다. 가사 데이터는 [LRCLIB](https://lrclib.net)에서 가져옵니다.
