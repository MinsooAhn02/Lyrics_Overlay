# Lyrics Overlay

유튜브·유튜브 뮤직에서 재생 중인 노래의 가사를, 브라우저 밖에 떠 있는 **항상 위 투명 오버레이**로 보여 주는 Windows 앱입니다. 브라우저 확장이 재생 정보를 앱에 보내고, 앱이 가사를 찾아 현재 재생 위치에 맞춰 줄 단위로 따라가며 보여 줍니다.

> 테스트 단계의 프로젝트입니다. 아래 "검증 상태"를 먼저 읽어 주세요.

앱은 [LyricsOnTheGo](https://github.com/The-MuffinDev/LyricsOnTheGo)(MIT, Luis Anchondo)를 바탕으로 수정했습니다. 원 라이선스는 `app/LICENSE`에 있습니다.

## 구성

| 폴더 | 내용 |
|---|---|
| `app/` | Windows 앱 (C# / WPF, .NET 8) |
| `extension/` | Chrome / Firefox 확장 (재생 정보와 유튜브 자막을 앱에 전달) |
| `tools/` | Firefox 패키지 빌드, 연결 보안 테스트 스크립트 |
| `tasks/` | 작업 메모와 확장 ↔ 앱 통신 규약 (`contract.md`) |

## 가사를 찾는 순서

1. **유튜브 공식 자막** — 사람이 올린 자막만 씁니다(자동 생성 자막 제외). 효과음 태그(`[Music]`, ♪)가 대부분이거나 6줄 미만이거나 영상 앞부분만 덮는 자막은 가사로 쓰지 않습니다.
2. **저장된 가사** — 한 번 받은 가사는 `%LOCALAPPDATA%\LyricsOnTheGo\lyrics-cache`에 저장합니다. 저장본을 먼저 보여 주고, 뒤에서 다시 조회해 내용이 다르면 최신 결과로 바꿉니다(더 낮은 등급, 예: 싱크 가사 → 일반 텍스트로는 바꾸지 않음).
3. **[LRCLIB](https://lrclib.net)** — 곡명·아티스트가 있으면 정확 조회 + 구조화 검색 + 자유 검색을 동시에 요청하고, 영상 길이와 가사 길이 차이로 후보를 거릅니다.

3-1. **시간 보정** — LRCLIB 가사가 싱크 가사인데 그 영상의 실제 타이밍이 다른 경우(커버, 인트로가 긴 영상 등), 영상의 **자동 생성 자막**을 시계로 삼아 가사 줄의 시각을 다시 맞춥니다. 오디오를 쓰지 않고 이 PC에서 글자 유사도 정렬(머신러닝 아님)로 계산합니다. 맞는 줄이 너무 적거나 평균 이동이 1.5초 미만이면 원래 타이밍을 그대로 씁니다. 자동 생성 자막은 시계로만 쓰고 가사로는 쓰지 않습니다.

곡명·아티스트는 확장이 유튜브 뮤직 플레이어 바 → 설명란 음악 섹션 → Topic 채널 설명(`곡명 · 아티스트`) → 영상 제목 순으로 읽습니다.

하단 바의 `CC` 버튼은 유튜브 자막이 가사로 쓰이는 영상에만 나타납니다. 눌러서 그 영상만 LRCLIB 가사로 바꾸거나 다시 자막을 쓸 수 있고, 선택은 영상별로 저장됩니다.

## 빠른 시작 (빌드 없이 쓰기)

앱과 브라우저 확장이 **둘 다** 필요합니다. 확장은 재생 정보를 내 PC의 앱으로 보내는 역할만 하고, 가사 창은 앱이 띄웁니다.

1. [Releases](https://github.com/MinsooAhn02/Lyrics_Overlay/releases/latest)에서 `LyricsOverlay-win-x64.zip`을 받아 풉니다. 안의 `LyricsOverlay.exe`를 더블클릭하면 실행됩니다. 설치도, .NET 설치도 필요 없습니다(약 70MB).
   - 서명되지 않은 프로그램이라 Windows SmartScreen 경고가 뜰 수 있습니다. **추가 정보 → 실행**을 누르세요.
   - 처음 실행은 5초쯤 걸립니다.
2. 같은 Releases에서 브라우저용 확장을 받습니다.
   - **Chrome**: `lyrics-overlay-bridge-chrome.zip`을 풀고 `chrome://extensions` → **개발자 모드** → **압축해제된 확장 프로그램을 로드합니다** → 푼 폴더 선택. (Chrome은 개발자 모드 확장을 켤 때마다 경고를 보여 줍니다.)
   - **Firefox**: `.xpi` 파일을 Firefox 창에 끌어다 놓고 **추가**. (서명본은 준비되는 대로 Releases에 추가됩니다. 그전에는 아래 "Firefox" 절차의 임시 로드를 쓰세요.)
3. 앱 설정의 **토큰**을 복사해 확장 팝업에 붙여넣고 **저장** → **연결 테스트**. 배지가 `ON`이면 유튜브에서 노래를 재생하세요.

확장 팝업의 "앱 다운로드" 링크는 위 Releases 페이지로 연결됩니다.

### 앱 켜고 끄기

- 가사 창의 **X는 종료가 아니라 트레이로 숨기기**입니다. 앱은 계속 실행 중입니다.
- 확장 팝업의 **앱 끄기**로 종료할 수 있습니다(트레이 아이콘 우클릭 → 종료도 같습니다).
- 확장 팝업의 **앱 켜기**로 다시 켤 수 있습니다. 앱을 **한 번은 직접 실행**해야 합니다. 그때 앱이 브라우저의 "네이티브 메시징" 도우미로 자신을 등록하기 때문입니다.
  - 등록은 현재 Windows 사용자 계정만 건드립니다(관리자 권한 불필요): `HKCU\Software\Google\Chrome`, `HKCU\Software\Microsoft\Edge`, `HKCU\Software\Mozilla` 아래 `NativeMessagingHosts\com.lyricsoverlay.host` 값과, `%LOCALAPPDATA%\LyricsOnTheGo\native-host\`의 파일 2개.
  - 이 도우미는 "앱이 실행 중인지 확인"과 "앱 시작"만 할 수 있고, 우리 확장 외에는 호출할 수 없습니다.
  - 등록을 지우려면 `LyricsOverlay.exe --unregister-native-host`를 실행하세요. (이후 앱을 다시 실행하면 다시 등록됩니다.)
- 앱은 한 번에 하나만 실행됩니다. 이미 켜져 있을 때 다시 실행해도 두 번째 창이 생기지 않습니다.

## 요구 사항

- Windows 10 이상
- 빌드하려면 .NET SDK 8 (`app/global.json` 참고)
- Chrome(111 이상) 또는 Firefox(128 이상)

## 앱 빌드와 실행

```powershell
cd app
dotnet build LyricsOnTheGo.sln -c Release
.\src\LyricsOnTheGo\bin\Release\net8.0-windows10.0.19041.0\LyricsOnTheGo.exe
```

앱은 `127.0.0.1:47831`에서 확장의 요청만 받습니다. 처음 실행하면 연결용 **토큰**이 자동으로 만들어지고, 앱 설정 화면에서 복사할 수 있습니다.

## 확장 설치

### Chrome

1. `chrome://extensions`를 열고 **개발자 모드**를 켭니다.
2. **압축해제된 확장 프로그램을 로드합니다**를 눌러 `extension` 폴더를 선택합니다.
3. 확장 ID가 `khoankekndekkeoamaopgjhpogbooldk`인지 확인합니다(앱이 이 ID만 허용합니다).

### Firefox

1. PowerShell 7에서 `tools\build-firefox.ps1`을 실행하면 `extension\dist\firefox\`와 zip이 만들어집니다.
2. 테스트용 임시 로드: `about:debugging#/runtime/this-firefox` → **임시 부가 기능 로드...** → `extension\dist\firefox\manifest.json`. Firefox를 끄면 사라지고 토큰도 지워집니다.
3. 영구 설치: Firefox 정식판은 서명이 필요합니다. [addons.mozilla.org/developers](https://addons.mozilla.org/developers)에서 **직접 배포(unlisted)**로 zip을 올려 서명된 `.xpi`를 받습니다. 버전을 올려 다시 올릴 때는 `extension/manifest.firefox.json`의 `version`을 먼저 올립니다.
4. `about:addons` → 확장 → **권한**에서 유튜브와 `127.0.0.1` 접근이 꺼져 있으면 켭니다.

### 연결

앱 설정의 토큰을 복사 → 확장 아이콘 팝업에 붙여넣고 **저장** → **연결 테스트**. 성공하면 배지가 `ON`으로 바뀝니다.

## 사용 팁

- 오버레이 위에 마우스를 올리면 상단 버튼과 하단 바(`−5` 이전곡 `재생` 다음곡 `+5` `CC`)가 나타나고, 3초 가만히 있으면 숨습니다.
- `−5` / `+5`는 5초 뒤로 / 앞으로 이동입니다. 가사 싱크 미세조정은 설정 창의 가사 오프셋(±6초)에서 합니다.
- 가사 줄을 더블클릭하면 그 시점으로 이동합니다.
- 이전 곡 버튼은 유튜브의 이전 버튼이 동작하지 않을 때(셔플 등) 브라우저 뒤로 가기(Alt+←)와 같은 동작을 합니다.
- 앱 진단 창에서 어느 출처에서 가사를 가져왔는지(`youtube-captions` / `cache` / `lrclib`)와 사유를 볼 수 있습니다.

## 검증 상태

확인한 것
- 빌드 경고·오류 0
- 자막 필터 단위 테스트(효과음 태그, 줄 수 부족, 앞부분만 있는 자막 등) 12개 통과
- 확장 → 앱 통신의 인증·출처·형식·크기 검사 (`tools\test-bridge.ps1`, 자막 수신 주소 직접 호출 11개 통과)

실제 환경에서 더 확인이 필요한 것
- 유튜브의 자막 요청 인증 토큰(`pot`) 때문에 자막을 못 받는 영상이 있을 수 있습니다. 재생기가 만든 요청 주소를 재사용하는 방식으로 우회하지만 Firefox 이외 환경과 다양한 영상에서의 동작은 검증 중입니다.
- Chrome에서의 자막 수신, CC 버튼, 재생 중 가사 교체

문제가 있으면 앱 진단 창의 `youtube-captions` 줄(사유 포함)과 영상 링크를 함께 알려 주세요.

## 개인정보와 보안

- 확장은 유튜브 탭의 재생 상태와 자막만 읽어 **내 PC의 앱(127.0.0.1)** 으로만 보냅니다. 외부 서버로 보내지 않습니다.
- 가사 검색은 LRCLIB로 곡명·아티스트·길이를 보냅니다.
- 앱은 토큰이 맞고, 허용된 확장 출처에서 온 요청만 받습니다.

## 라이선스

- 이 저장소: MIT (`LICENSE`)
- `app/`은 원작 [LyricsOnTheGo](https://github.com/The-MuffinDev/LyricsOnTheGo)(MIT, Luis Anchondo)를 바탕으로 수정한 것이라 원 라이선스(`app/LICENSE`)를 그대로 유지합니다.
- 내장 글꼴 Pretendard: SIL OFL 1.1 (`app/src/LyricsOnTheGo/Fonts/LICENSE-Pretendard.txt`)
- 가사 데이터는 각 제공처의 약관과 저작권을 따릅니다.
