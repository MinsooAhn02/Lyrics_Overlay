// Run: node tools\background-check.js   (exit code = number of failed checks)
// Loads extension/background.js against a fake browser API and checks that a failing captions POST never touches
// the now-playing backoff / badge, while a failing now-playing POST still does.
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(process.env.BG_FILE || path.join(__dirname, '..', 'extension', 'background.js'), 'utf8');

let fails = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name} ${extra}`); if (!ok) fails++; };

function makeEnv(fetchImpl) {
  const local = { token: 'tok' }, session = {};
  const badge = [];
  const store = (o) => ({
    get: async (k) => (typeof k === 'string' ? { [k]: o[k] } : { ...o }),
    set: async (v) => { Object.assign(o, v); },
  });
  const api = {
    runtime: { id: 'x', onMessage: { addListener() {} }, onStartup: { addListener() {} }, onInstalled: { addListener() {} } },
    storage: { local: store(local), session: store(session), onChanged: { addListener() {} } },
    action: { setBadgeText: (b) => badge.push(b.text), setBadgeBackgroundColor() {} },
    tabs: { sendMessage: async () => {} },
  };
  globalThis.chrome = api; // background.js reads globalThis.browser ?? globalThis.chrome
  const fn = new Function('fetch', 'AbortSignal', src + '\nreturn { post, URL_, CAP_URL };');
  const m = fn(fetchImpl, { timeout: () => undefined });
  return { ...m, session, badge };
}
const resp = (status, body = {}) => ({ ok: status >= 200 && status < 300, status, json: async () => body });

(async () => {
  let e = makeEnv(async () => resp(413));
  let r = await e.post({ x: 1 }, e.CAP_URL);
  check('captions 413 is dropped (ok, no resend)', r.ok === true && r.dropped === true);
  check('captions 413 leaves backoff untouched', e.session.fails === undefined && e.session.nextAt === undefined && e.badge.length === 0);

  e = makeEnv(async () => { throw new Error('network down'); });
  r = await e.post({ x: 1 }, e.CAP_URL);
  check('captions network error: not ok (will retry)', r.ok === false);
  check('captions network error leaves backoff + badge untouched', e.session.fails === undefined && e.session.nextAt === undefined && e.badge.length === 0);

  e = makeEnv(async () => resp(401));
  r = await e.post({ x: 1 }, e.CAP_URL);
  check('captions 401 is NOT dropped (token problem, retry)', r.ok === false && r.dropped !== true);

  e = makeEnv(async () => resp(500));
  r = await e.post({ x: 1 }, e.CAP_URL);
  check('captions 500 retries without backoff', r.ok === false && e.session.fails === undefined);

  e = makeEnv(async () => resp(200, {}));
  r = await e.post({ x: 1 }, e.CAP_URL);
  check('captions 200 ok, badge untouched', r.ok === true && !r.dropped && e.badge.length === 0);

  e = makeEnv(async () => resp(500));
  r = await e.post({ x: 1 });
  check('now-playing 500 still backs off', r.ok === false && e.session.fails === 1 && e.session.nextAt > Date.now() && e.badge.includes('OFF'));

  e = makeEnv(async () => resp(200, { commands: ['next'] }));
  r = await e.post({ x: 1 });
  check('now-playing 200 returns commands, clears backoff', r.ok === true && r.commands[0] === 'next' && e.session.fails === 0 && e.badge.includes('ON'));

  console.log(fails ? `${fails} FAILED` : 'ALL PASS');
  process.exit(fails);
})();
