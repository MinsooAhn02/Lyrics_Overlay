# Lyrics Overlay

유튜브·유튜브 뮤직에서 재생 중인 노래의 가사를 브라우저 밖에 떠 있는 **항상 위 투명 창**으로 보여 주는 Windows 앱입니다. 브라우저 확장이 재생 정보를 내 PC의 앱으로 보내면, 앱이 가사를 찾아 재생 위치에 맞춰 한 줄씩 따라갑니다.

> 개인 프로젝트이고 테스트 단계입니다. 문제가 있으면 [Issues](https://github.com/MinsooAhn02/Lyrics_Overlay/issues)에 알려 주세요.

## 설치

앱과 브라우저 확장이 **둘 다** 필요합니다. 확장은 재생 정보를 앱으로 보내기만 하고, 가사 창은 앱이 띄웁니다.

1. **앱**: [Releases](https://github.com/MinsooAhn02/Lyrics_Overlay/releases/latest)에서 `LyricsOverlay-win-x64.zip`을 받아, 계속 둘 폴더에 풉니다(다운로드 폴더 말고). `LyricsOverlay.exe`를 **한 번** 더블클릭하면 브라우저에 등록되고 앱이 켜집니다.
   - 설치나 .NET이 따로 필요 없습니다(약 70MB).
   - 서명되지 않은 프로그램이라 SmartScreen 경고가 뜰 수 있습니다. **추가 정보 → 실행**을 누르세요.
2. **확장**
   - **Chrome**: `lyrics-overlay-bridge-chrome.zip`을 풀고 `chrome://extensions` → **개발자 모드** → **압축해제된 확장 프로그램을 로드합니다** → 푼 폴더.
   - **Firefox**: 서명된 `.xpi`는 준비되는 대로 Releases에 올립니다. 그전에는 아래 [직접 빌드](#직접-빌드)의 임시 로드를 쓰세요.
3. **연결**: 앱 설정(톱니바퀴)의 **토큰**을 복사해 확장 팝업에 붙여넣고 **저장** → **연결 테스트**. 배지가 `ON`이면 유튜브에서 노래를 재생하세요.

## 켜고 끄기

- 앱은 확장 팝업의 **앱 켜기 / 앱 끄기**로만 켜고 끕니다. 자동 실행은 없습니다.
- exe를 다시 더블클릭하면 켜지지 않고 안내만 뜹니다. exe를 다른 폴더로 옮겼다면 새 위치에서 한 번 더블클릭하세요(다시 등록되며 켜집니다).
- 가사 창의 **X는 트레이로 숨기기**입니다. 완전히 끄려면 **앱 끄기** 또는 트레이 아이콘 우클릭 → **종료**.
- 등록은 현재 Windows 사용자 계정에만 합니다(관리자 권한 불필요). 지우려면 `LyricsOverlay.exe --unregister-native-host`.
  - 레지스트리: `HKCU\Software\{Google\Chrome, Microsoft\Edge, Mozilla}\NativeMessagingHosts\com.lyricsoverlay.host`
  - 파일: `%LOCALAPPDATA%\LyricsOnTheGo\native-host\`
  - 이 도우미는 "앱이 켜져 있는지 확인"과 "앱 켜기"만 할 수 있고, 이 확장만 호출할 수 있습니다.

## 가사 창 사용법

마우스를 올리면 아래쪽에 버튼이 나타나고, 3초 가만히 있으면 숨습니다.

| 버튼 | 동작 |
|---|---|
| `−5` / `+5` | 5초 뒤로 / 앞으로 |
| 이전 · 재생 · 다음 | 유튜브 재생 조작. 유튜브의 이전 버튼이 안 먹을 때(셔플 등)는 브라우저 뒤로 가기로 대신합니다 |
| `CC` / `보정` / `MIX` | 유튜브 자막을 가사로 쓰는 중 / 자막으로 시간 보정 중 / 설명란 곡 목록으로 이어 붙이는 중. 눌러서 이 영상만 끄고 켭니다 |
| `♪` | 음악이 아니라고 잘못 판단해 건너뛴 영상에서, 눌러서 가사를 찾게 합니다 |
| 셔플 아이콘 | 유튜브 재생목록에서만 보입니다. 아래 [셔플](#셔플) 참고 |

- 가사 줄을 더블클릭하면 그 시점으로 이동합니다.
- 싱크 미세 조정은 설정의 가사 오프셋(±6초)으로 하며, 곡마다 저장됩니다.

### 셔플

유튜브 셔플은 불러온 일부만 섞습니다. 셔플 아이콘을 켜면 재생목록 **전체**(재생할 수 있는 곡만)를 읽어 모든 곡이 한 번씩 나오도록 섞고, 곡이 끝나거나 가사 창의 `다음`/`이전`을 누르면 그 순서로 이동합니다. 다 돌면 새로 섞습니다.

- 지원하지 않음: 믹스(`RD…`), 비공개 재생목록, 유튜브 뮤직. 실패하면 아이콘에 마우스를 올려 이유를 볼 수 있습니다.
- 키보드 미디어 키나 유튜브 화면의 다음 버튼은 유튜브 자체 순서를 따릅니다.

## 가사를 찾는 순서

1. **유튜브 공식 자막**: 사람이 올린 자막만 씁니다. 효과음 태그(`[Music]`, ♪)뿐이거나, 6줄 미만이거나, 앞부분만 있는 자막은 쓰지 않습니다.
2. **저장된 가사**: 한 번 받은 가사는 `%LOCALAPPDATA%\LyricsOnTheGo\lyrics-cache`에 저장해 먼저 보여 줍니다. 저장한 지 일주일이 지난 가사만 LRCLIB에 다시 확인해, 바뀌었으면 새 결과로 바꿉니다(싱크 가사를 일반 가사로 바꾸지는 않음).
3. **[LRCLIB](https://lrclib.net)**: 곡명·아티스트·길이로 찾고, 영상 길이와 차이가 큰 후보는 거릅니다.

- **시간 보정**: 싱크 가사의 타이밍이 영상과 다르면(커버, 긴 인트로 등) 영상의 자동 생성 자막을 기준으로 줄 시각을 다시 맞춥니다. 오디오는 쓰지 않고, PC 안에서 글자 비교로만 계산합니다.
- **플레이리스트 영상**: 설명란에 `00:00 곡명` 같은 곡 목록이 있으면 곡별 가사를 찾아 이어 붙입니다.
- 곡명·아티스트는 유튜브 뮤직 플레이어 → 설명란 음악 정보 → Topic 채널 → 영상 제목 순서로 읽고, 표시할 때는 `[Official MV]`, 이모지, 해시태그를 뺍니다.

## 개인정보와 보안

- 확장은 유튜브 탭의 재생 상태와 자막만 읽어 **내 PC의 앱(`127.0.0.1:47831`)으로만** 보냅니다.
- 가사 검색을 위해 곡명·아티스트·길이를 LRCLIB로 보냅니다.
- 앱은 토큰이 맞고, 허용된 확장에서 온 요청만 받습니다. 명령(다음 곡 등)은 화면에 표시 중인 탭에만 전달합니다.

## 직접 빌드

필요한 것: Windows 10 이상, .NET SDK 8, Chrome 111+ 또는 Firefox 128+.

```powershell
dotnet build app\LyricsOnTheGo.sln -c Release      # 앱
pwsh tools\build-firefox.ps1                        # Firefox 패키지 (extension\dist\)
pwsh tools\build-release.ps1                        # 배포용 zip 3개 (dist-release\, 앱이 꺼져 있어야 함)
```

- Chrome: `chrome://extensions` → 개발자 모드 → `extension` 폴더 로드. 확장 ID가 `khoankekndekkeoamaopgjhpogbooldk`여야 합니다.
- Firefox 임시 로드: `about:debugging#/runtime/this-firefox` → **임시 부가 기능 로드...** → `extension\dist\firefox\manifest.json`. Firefox를 끄면 사라집니다.
- Firefox 영구 설치: [addons.mozilla.org/developers](https://addons.mozilla.org/developers)에 **직접 배포(unlisted)**로 zip을 올려 서명된 `.xpi`를 받습니다. 다시 올릴 때는 `extension/manifest.firefox.json`의 `version`을 올립니다.

### 검사 스크립트 (`tools\`)

| 스크립트 | 확인 내용 |
|---|---|
| `test-bridge.ps1 -Token <토큰>` | 앱 연결 인증·출처·형식·크기 제한 (30개) |
| `native-host-check.ps1` | 앱 켜기/끄기, 직접 실행 막기, 등록/해제 (40개) |
| `show-again-check.ps1` | 트레이로 숨긴 오버레이를 '앱 켜기'로 다시 띄우기 |
| `shuffle-check.js [--live]` | 셔플 순서·이동·실패 처리 (`--live`: 실제 재생목록 읽기) |
| `shuffle-button-check.ps1` | 실제 앱의 셔플 버튼과 명령 전달 |
| `caption-pot-check.js` | 유튜브 자막 토큰 잡기 (늦게 오는 토큰, 재시도 1번) |
| `*-check.js`, `*-check\` | 제목 정리, 음악 판별, 시간 보정, 플레이리스트 가사 등 |
| `leak-check.ps1` | 장시간 사용 시 메모리 증가 여부 |

`native-host-check`, `show-again-check`, `shuffle-button-check`, `leak-check`는 실행 중인 앱을 껐다 켭니다. 브라우저에서 음악이 재생 중이면 결과가 섞이니 멈춘 상태에서 돌리세요.

## 알려진 한계

- 유튜브가 자막 요청에 영상별 인증 토큰을 요구해서, 자막을 못 받는 영상이 있을 수 있습니다.
- 셔플은 Firefox에서 확인했습니다. Chrome에서는 아직 확인하지 않았습니다.
- 브라우저 탭이 뒤에 있으면 브라우저가 확장의 보고를 늦춥니다. 같은 곡이면 앱이 그대로 유지하지만, 버튼 반응이 늦을 수 있습니다.

## 라이선스

- 이 저장소: MIT (`LICENSE`)
- `app/`은 [LyricsOnTheGo](https://github.com/The-MuffinDev/LyricsOnTheGo)(MIT, Luis Anchondo)를 고친 것이라 원 라이선스(`app/LICENSE`)를 유지합니다.
- 내장 글꼴 Pretendard: SIL OFL 1.1 (`app/src/LyricsOnTheGo/Fonts/LICENSE-Pretendard.txt`)
- 가사는 각 제공처의 약관과 저작권을 따릅니다.
