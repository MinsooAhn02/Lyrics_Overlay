# Lyrics Overlay Bridge (Chrome 확장)
1. 크롬에서 `chrome://extensions` 를 열고 우측 상단 **개발자 모드**를 켠다.
2. **압축해제된 확장 프로그램을 로드합니다**를 눌러 이 `extension` 폴더를 선택한다 (ID 는 항상 `khoankekndekkeoamaopgjhpogbooldk`).
3. 확장 아이콘(퍼즐 메뉴에서 고정)을 눌러 팝업을 연다.
4. 앱 설정 화면의 **Chrome 확장 연결**에서 토큰을 복사해 팝업에 붙여넣고 **저장**을 누른다.
5. **연결 테스트**가 성공하면 유튜브/유튜브 뮤직에서 재생한다 (배지 ON = 연결됨).

# Firefox 설치 (Firefox 128 이상)
먼저 PowerShell 7 에서 `tools\build-firefox.ps1` 을 실행하면 `extension\dist\firefox\` 폴더와 `extension\dist\lyrics-overlay-bridge-firefox.zip` 이 만들어진다. (크롬은 위 절차 그대로, `manifest.json` 사용)

## (a) 임시 로드 (테스트용, Firefox 를 끄면 사라짐)
1. 주소창에 `about:debugging#/runtime/this-firefox` 를 연다.
2. **임시 부가 기능 로드...** 를 누르고 `extension\dist\firefox\manifest.json` 을 선택한다.

## (b) 영구 설치 (AMO 비공개 서명)
Firefox 정식판은 서명 안 된 확장을 영구 설치할 수 없다. 아래 업로드는 외부 서비스(Mozilla) 작업이므로 **사용자가 직접** 한다.
1. `addons.mozilla.org/developers` 에 로그인한다.
2. **새 부가 기능 제출** > 배포 방식에서 **자체 배포(On your own, unlisted)** 를 선택한다.
3. `extension\dist\lyrics-overlay-bridge-firefox.zip` 을 업로드한다. (버전을 올려 다시 올릴 때는 `manifest.firefox.json` 의 `version` 을 먼저 올려야 한다.)
4. 서명이 끝나면 **.xpi** 를 내려받아 Firefox 창에 끌어다 놓고 **추가**를 누른다.

## (c) 권한 허용
Firefox MV3 는 호스트 권한(유튜브, `127.0.0.1:47831`)이 설치 때 자동 허용되지 않을 수 있다. `about:addons` > Lyrics Overlay Bridge > **권한** 탭에서 "모든 사이트/요청한 사이트 접근" 항목이 꺼져 있으면 켠다. 켜지 않으면 확장이 앱에 연결하지 못하거나(오프) 유튜브에서 동작하지 않는다.

## (d) 토큰
크롬과 같다. 앱 설정의 토큰을 복사해 확장 팝업에 붙여넣고 **저장** > **연결 테스트**. Firefox 확장의 Origin 은 설치마다 달라지는 `moz-extension://<UUID>` 이며 앱이 형식만 확인하므로 토큰이 반드시 맞아야 한다.
