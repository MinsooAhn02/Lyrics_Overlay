# 확장 <-> 앱 계약 (E 에이전트가 구현, F 에이전트가 소비)

## 엔드포인트 (앱의 BrowserBridge, 127.0.0.1 전용)
- 주소: `http://127.0.0.1:47831/v1/now-playing` (포트는 상수 하나로 관리, 충돌 시 앱이 화면/트레이에 이유 표시)
- 메서드: `POST`만 허용, 그 외 405
- 필수 헤더: `X-Bridge-Token: <Settings.BridgeToken>` (불일치/누락 401), `Content-Type: application/json` (아니면 415)
- Origin 검사: `Origin`이 `chrome-extension://<허용 확장 ID>` 이거나 Firefox 형식 `^moz-extension://<소문자 UUID>\z` 일 때만 허용 (그 외 403). 확장 ID는 상수 `AllowedExtensionId`. Firefox Origin 은 설치마다 달라 형식만 검사하므로 인증은 항상 `X-Bridge-Token` 이 담당
- 본문 최대 8KB (초과 413), 타임아웃 2초
- 응답: 200 + `{ "commands": ["..."] }` (성공, 큐에 쌓인 명령을 1회 전달 후 비움; 없으면 빈 배열. `{"ping":true}` 는 큐를 소비하지 않음) / 오류 코드 + 짧은 JSON `{ "error": "..." }`

## commands (앱 -> 확장, 응답 본문으로만 전달)
- 허용값: `toggle`, `next`, `prev`, `seek:<정수 ms>`. 큐 상한 16, 그 외 문자열은 앱이 버리고 DiagLog 한 줄만 남김
- seek 앱 측 검증: 정규식 `^seek:[0-9]{1,8}$` 이고 값이 0 이상 86400000 이하일 때만 큐에 넣음
- seek 큐 규칙: 새 seek 가 들어오면 큐에 남은 이전 seek 를 제거(마지막 것만 유지)
- seek 확장 측 실행: `^seek:(\d{1,8})$` 파싱, 광고 구간(isAd)이면 무시, `t = ms/1000` 이 유한하고 `0 <= t <= duration+1` 일 때만 `video.currentTime = t`
- seek 실행 직후 확장은 현재 상태를 즉시 한 번 더 전송. 알 수 없는 명령은 조용히 무시

## 본문 (BrowserNowPlaying, JSON, camelCase)
| 필드 | 타입 | 설명 |
|---|---|---|
| videoId | string | 유튜브 영상 ID (11자) |
| title | string | 영상 제목 (원문) |
| channel | string | 채널명 |
| durationMs | int | 영상 길이 ms |
| positionMs | int | `<video>.currentTime` ms (측정 시점 값) |
| paused | bool | 일시정지 여부 |
| isAd | bool | 광고 구간 여부 |
| isMusic | bool | 카테고리가 Music인지 (알 수 없으면 true) |
| sentAtMs | long | 확장이 측정한 시각 (epoch ms, 지연 보정용) |
| track | string? | (선택) 곡명, 최대 300자. 불확실하면 빈 값. 앱은 제어문자 제거, 초과 시 400 |
| artist | string? | (선택) 아티스트, 최대 200자. 불확실하면 빈 값 |
| album | string? | (선택) 앨범, 최대 200자 |
| metaSource | string? | (선택) `ytm`/`musicSection`/`topic`/`title`/`` 중 하나, 최대 16자. 추출 출처 (title = 제목 패턴 추정, 신뢰도 낮음) |

- track/artist/album/metaSource 는 매 전송마다 현재 영상 기준으로 다시 계산(이전 영상 값 잔존 없음). 필드가 없으면 앱은 빈 문자열로 취급
- 최악 본문 크기: 문자열 필드 합계 1200자 x 최대 6바이트(JSON 이스케이프) + 고정부 약 250B = 약 7.5KB < 8KB

- 확장은 재생 중 1초마다, 이벤트(재생/정지/시크/영상 전환) 발생 시 즉시 전송
- 앱은 마지막 수신 후 3초 넘게 새 값이 없으면 확장 소스를 무효로 보고 SMTC로 폴백

## 토큰 발급
- 앱 최초 실행 시 `Settings.BridgeToken`이 비어 있으면 암호학적 난수(32바이트, hex)로 생성해 저장
- 확장은 옵션 페이지에서 토큰을 한 번 입력(복사: 앱 설정 화면의 복사 버튼 또는 settings.json)

## 캐시 키 (F 에이전트)
- 확장 값이 있으면 `yt:<videoId>`, 없으면 기존 키(`title|artist|album|길이초`) 그대로
