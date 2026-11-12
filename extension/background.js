// MV3 service worker (Chrome) / event page (Firefox). No state in globals: it may be killed at any time.
// Listeners below are registered synchronously at top level, as event pages require.
const api = globalThis.browser ?? globalThis.chrome; // promise-based in both browsers
const URL_ = 'http://127.0.0.1:47831/v1/now-playing';
const CAP_URL = 'http://127.0.0.1:47831/v1/captions';
const QUIT_URL = 'http://127.0.0.1:47831/v1/quit';
const MAX_BACKOFF_MS = 30000;
const NO_TOKEN = '토큰이 없습니다';

const badge = (text, color) => {
  api.action.setBadgeText({ text });
  api.action.setBadgeBackgroundColor({ color });
};

async function record(ok, error) {
  await api.storage.session.set({ status: { ok, error: error || null, at: Date.now() } });
  if (ok) badge('ON', '#2e9e4f'); else badge('OFF', '#d93025');
}

// POSTs `body`. Returns {ok, error, commands}. Never throws; never logs the token.
async function post(body, url = URL_) {
  const { token } = await api.storage.local.get('token');
  if (!token) {
    badge('?', '#f29900');
    await api.storage.session.set({ status: { ok: false, error: NO_TOKEN, at: Date.now() } });
    return { ok: false, error: NO_TOKEN, commands: [] };
  }
  const caps = url !== URL_; // captions POSTs never touch the now-playing backoff / badge
  const { fails = 0 } = await api.storage.session.get('fails');
  try {
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Bridge-Token': token },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(url === URL_ ? 3000 : 8000),
    });
    if (!r.ok) {
      // the app refused this payload for good (400/413...): drop it instead of resending forever
      if (caps && r.status >= 400 && r.status < 500 && r.status !== 401 && r.status !== 403) return { ok: true, dropped: true, commands: [] };
      const why = r.status === 401 ? '토큰이 앱과 다릅니다'
        : r.status === 403 ? '앱이 이 확장을 거부했습니다'
        : `앱 응답 오류 (${r.status})`;
      throw Object.assign(new Error(why), { friendly: true });
    }
    const j = await r.json();
    if (!caps) {
      await api.storage.session.set({ fails: 0, nextAt: 0 });
      await record(true);
    }
    return { ok: true, commands: Array.isArray(j.commands) ? j.commands : [] };
  } catch (e) {
    const why = e && e.friendly ? e.message : '앱에 연결할 수 없습니다 (앱이 꺼져 있나요?)';
    if (!caps) {
      const n = fails + 1;
      await api.storage.session.set({ fails: n, nextAt: Date.now() + Math.min(MAX_BACKOFF_MS, 1000 * 2 ** n) });
      await record(false, why);
    }
    return { ok: false, error: why, commands: [] };
  }
}

async function onNowPlaying(payload, tabId) {
  const { nextAt = 0 } = await api.storage.session.get('nextAt');
  if (Date.now() < nextAt) return; // backing off after failures
  if (payload && typeof payload.dbg === 'string') payload = { ...payload, dbg: `${payload.dbg} tab=${tabId}`.slice(0, 120) };
  const res = await post(payload);
  if (res.ok && res.commands.length)
    await api.tabs.sendMessage(tabId, { type: 'commands', commands: res.commands }).catch(() => {});
}

api.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (sender.id !== api.runtime.id || !msg) return;
  if (msg.type === 'nowPlaying' && sender.tab) {
    onNowPlaying(msg.payload, sender.tab.id).catch(() => {}).finally(() => sendResponse({}));
    return true;
  }
  if (msg.type === 'captions' && sender.tab) { // official captions: one POST per video, content.js resends until ok
    post(msg.payload, CAP_URL).then(({ ok }) => sendResponse({ ok })).catch(() => sendResponse({ ok: false }));
    return true;
  }
  if (msg.type === 'quit') { // popup: ask the app to exit (token-authenticated, same as every other request)
    post({}, QUIT_URL).then(({ ok, error }) => sendResponse({ ok, error })).catch(() => sendResponse({ ok: false, error: '알 수 없는 오류' }));
    return true;
  }
  if (msg.type === 'test') { // popup: authenticated ping, ignores backoff, carries no playback data
    post({ ping: true })
      .then(({ ok, error }) => sendResponse({ ok, error }))
      .catch(() => sendResponse({ ok: false, error: '알 수 없는 오류' }));
    return true;
  }
});

async function refreshBadge() {
  const { token } = await api.storage.local.get('token');
  if (!token) return badge('?', '#f29900');
  const { status } = await api.storage.session.get('status');
  if (status && status.ok) badge('ON', '#2e9e4f'); else badge('OFF', '#d93025');
}
api.runtime.onStartup.addListener(refreshBadge);
api.runtime.onInstalled.addListener(refreshBadge);
api.storage.onChanged.addListener((ch, area) => {
  if (area === 'local' && ch.token) api.storage.session.set({ fails: 0, nextAt: 0 }).then(refreshBadge);
});
