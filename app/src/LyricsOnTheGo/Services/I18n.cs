using System;
using System.Collections.Generic;

namespace LyricsOnTheGo.Services;

/// <summary>
/// App-wide string table. English is the default; a single switch toggles EN / KO.
/// UI surfaces subscribe to <see cref="Changed"/> and re-pull every string via <see cref="T"/>
/// when the language changes, so localization is live (no restart).
/// </summary>
public static class I18n
{
    // key → (English, 한국어). An empty/missing translation falls back to English in T().
    private static readonly Dictionary<string, (string En, string Ko)> Table = new()
    {
        ["settingsTitle"]   = ("Settings", "설정"),
        ["pin"]             = ("Pin on top", "항상 위에 고정"),
        ["karaoke"]         = ("Karaoke (fullscreen)", "노래방 (전체 화면)"),
        ["resync"]          = ("Resync", "다시 동기화"),
        ["close"]           = ("Close (to tray)", "닫기 (트레이로)"),
        ["grpAppearance"]   = ("Appearance", "화면"),
        ["textColor"]       = ("Text color", "글자 색상"),
        ["bgColor"]         = ("Background color", "배경 색상"),
        ["bgOpacity"]       = ("Background transparency", "배경 투명도"),
        ["grpText"]         = ("Text", "글자"),
        ["textSize"]        = ("Text size", "글자 크기"),
        ["titleSize"]       = ("Title size", "제목 크기"),
        ["dimInactive"]     = ("Dim inactive lines", "비활성 줄 흐리게"),
        ["alignment"]       = ("Alignment", "정렬"),
        ["alignLeft"]       = ("Left", "왼쪽"),
        ["alignCenter"]     = ("Center", "가운데"),
        ["grpSync"]         = ("Sync", "싱크"),
        ["offset"]          = ("Lyrics offset", "가사 싱크 보정"),
        ["offsetHint"]      = ("Delays (+) or advances (−) the active line up to ±6 s. Useful if the lyrics are out of sync.",
                               "현재 줄을 최대 ±6초까지 늦춤(+) · 앞당김(−). 가사가 어긋날 때 조정"),
        ["grpVisibility"]   = ("Visibility", "표시"),
        ["autohide"]        = ("Auto-hide header", "헤더 자동 숨김"),
        ["autohideHint"]    = ("Hides the top bar. Move the mouse or click the overlay to show it for 2 s and open settings.",
                               "상단 바 숨김. 마우스를 움직이거나 오버레이를 클릭하면 2초간 표시되어 설정 열기 가능"),
        ["showProgress"]    = ("Show progress bar", "진행 막대 표시"),
        ["showTimes"]       = ("Show times (current / total)", "시간 표시 (현재 / 전체)"),
        ["grpBehavior"]     = ("Behavior", "동작"),
        ["clickthrough"]    = ("Click-through (ignore clicks)", "클릭 통과 (클릭 무시)"),
        ["clickthroughHint"]= ("This lets the overlay pass clicks through. To disable it, use the tray icon menu.",
                               "오버레이가 클릭을 통과시킴. 해제는 트레이 아이콘 메뉴에서"),
        ["plainFallback"]   = ("Show plain text if no sync", "싱크 없으면 일반 텍스트 표시"),
        ["hideJapanese"]    = ("Hide Japanese lyrics", "일본어 가사 숨김"),
        ["hideJapaneseHint"] = ("Do not show lyrics that are mostly Japanese", "대부분 일본어인 가사는 표시하지 않음"),
        ["grpCache"]        = ("Lyrics cache", "가사 캐시"),
        ["cacheHint"]       = ("Lyrics are saved to disk so they aren't downloaded again. Clear the cache to force a fresh download.",
                               "가사를 디스크에 저장해 재다운로드 방지. 새로 받으려면 캐시 삭제"),
        ["clearCache"]      = ("Clear lyrics cache", "가사 캐시 지우기"),
        ["clearOffsets"]    = ("Clear saved offsets", "저장된 보정값 지우기"),
        ["grpLocalDb"]      = ("Local database", "로컬 데이터베이스"),
        ["localDbHint"]     = ("Link the offline LRCLIB database for instant, offline lookups.",
                               "오프라인 LRCLIB 데이터베이스를 연결해 즉시 검색"),
        ["localDbDownload"] = ("Download the database ↗", "데이터베이스 다운로드 ↗"),
        ["localDbWarn"]     = ("The uncompressed database is around 117 GB.",
                               "압축 해제 시 데이터베이스 크기 약 117GB"),
        ["selectDb"]        = ("Select database file…", "데이터베이스 파일 선택…"),
        ["unlinkDb"]        = ("Unlink database", "데이터베이스 연결 해제"),
        ["localDbNone"]     = ("(no database linked)", "(연결된 데이터베이스 없음)"),
        ["localDbLinked"]   = ("Linked ✓ — used first; the API is the fallback.",
                               "연결됨 ✓ — 먼저 사용, API는 대체 수단"),
        ["localDbMissing"]  = ("The linked file is missing — using the API.",
                               "연결된 파일 없음 — API 사용"),
        ["localDbInvalid"]  = ("That file isn't a valid LRCLIB database.",
                               "올바른 LRCLIB 데이터베이스 파일 아님"),
        ["offsetsCleared"]  = ("Offsets cleared ({n})", "보정값 삭제됨 ({n})"),
        ["reset"]           = ("Reset to defaults", "기본값으로 되돌리기"),
        ["openSpotify"]     = ("Play something…", "음악을 재생하세요…"),
        ["searching"]       = ("Searching for lyrics…", "가사 검색 중…"),
        ["notfound"]        = ("Lyrics not found", "가사를 찾을 수 없음"),
        ["onlyUnsynced"]    = ("Only unsynced lyrics available (disabled in settings)",
                               "싱크 없는 가사만 있음 (설정에서 꺼짐)"),
        ["instrumental"]    = ("♪ Instrumental ♪", "♪ 연주곡 ♪"),
        ["cacheCleared"]    = ("Cache cleared ({n})", "캐시 삭제됨 ({n})"),
        ["clearFailed"]     = ("Clear failed", "삭제 실패"),
        // Tray menu.
        ["trayShow"]          = ("Show", "보이기"),
        ["trayHide"]          = ("Hide", "숨기기"),
        ["trayClickThrough"]  = ("Click-through", "클릭 통과"),
        ["trayDiagnostics"]   = ("Diagnostics…", "진단…"),
        ["trayQuit"]          = ("Quit", "종료"),
        // Diagnostics window.
        ["diagTitle"]         = ("LyricsOnTheGo — Diagnostics", "LyricsOnTheGo — 진단"),
        ["diagProviders"]     = ("Lyrics providers", "가사 제공처"),
        ["diagProvidersHint"] = ("Turn a source on/off live. Disabling a bad source stops it from being used immediately.",
                                 "제공처를 실시간으로 켜고 끔. 문제 있는 제공처를 끄면 즉시 사용 중단"),
        ["diagClear"]         = ("Clear log", "로그 지우기"),
        ["diagAutoscroll"]    = ("Auto-scroll", "자동 스크롤"),
        ["diagColTime"]       = ("Time", "시간"),
        ["diagColTrack"]      = ("Track", "곡"),
        ["diagColProvider"]   = ("Provider", "제공처"),
        ["diagColResult"]     = ("Result", "결과"),
        ["diagColDetail"]     = ("Match / detail", "일치 / 상세"),
        ["diagColMs"]         = ("ms", "ms"),
        ["diagWaiting"]       = ("Waiting for the next song change…", "다음 곡 변경 대기 중…"),
        ["diagSource"]        = ("Now playing from", "재생 출처"),
        ["diagSourceNone"]    = ("no active session", "활성 세션 없음"),
        ["diagSourceBrowser"] = ("browser (title-only search)", "브라우저 (제목만으로 검색)"),
        ["diagTrackDuration"] = ("track duration", "곡 길이"),
        // Added by the fork: visual toggles + Chrome extension bridge.
        ["textOutline"]         = ("Text outline", "글자 외곽선"),
        ["grpBridge"]           = ("Chrome extension link", "Chrome 확장 연결"),
        ["bridgeEnabled"]       = ("Enable link", "연결 사용"),
        ["bridgeToken"]         = ("Pairing token", "연결 토큰"),
        ["bridgeTokenEmpty"]    = ("Generated the next time you restart the app.",
                                   "앱을 다시 실행하면 생성됨"),
        ["bridgeTokenHint"]     = ("Paste this token into the extension. It is hidden here; Copy puts it on the clipboard.",
                                   "이 토큰을 확장 프로그램에 붙여넣기. 여기서는 숨김, 복사 버튼으로 클립보드에 복사"),
        ["bridgeCopy"]          = ("Copy", "복사"),
        ["bridgeCopied"]        = ("Copied", "복사됨"),
        ["bridgeCopyFailed"]    = ("Copy failed", "복사 실패"),
        ["fontFamily"]          = ("Font", "폰트"),
        ["fontDefault"]         = ("Default", "기본"),
        ["fontRecommended"]     = ("Recommended", "추천"),
        ["fontBundledSuffix"]   = (" (bundled)", " (내장)"),
        ["fontAll"]             = ("All fonts", "전체 폰트"),
        ["fontHint"]            = ("Pretendard, SUIT and Noto Sans KR work well for mixed Korean/English lyrics. Install a font to see it here.",
                                   "한영 혼용 가사에는 Pretendard·SUIT·Noto Sans KR이 잘 맞음. 설치하면 목록에 나타남"),
    };

    /// <summary>Current language: "en" (default) or "ko".</summary>
    public static string Lang { get; private set; } = "en";

    /// <summary>Raised after the language changes so UI can re-localize.</summary>
    public static event Action? Changed;

    /// <summary>Only "ko" is recognised; anything else (including a stale saved "es") becomes "en".</summary>
    public static void SetLanguage(string? lang)
    {
        string normalized = lang == "ko" ? "ko" : "en";
        if (normalized == Lang)
            return;
        Lang = normalized;
        Changed?.Invoke();
    }

    /// <summary>Look up a string in the current language. A missing/empty translation falls back to
    /// English; unknown keys return the key itself.</summary>
    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var t))
            return key;
        string s = Lang == "ko" ? t.Ko : t.En;
        return string.IsNullOrEmpty(s) ? t.En : s;
    }
}
