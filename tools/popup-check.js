// Run: node tools\popup-check.js   (exit code = number of failed checks)
// Drives extension/popup.js against a fake DOM + browser API and checks what the "앱 켜기" / "앱 끄기" buttons tell the user.
const fs = require('fs'), path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'extension', 'popup.js'), 'utf8');
let fails = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name} ${extra}`); if (!ok) fails++; };

function boot({ native, send }) {
  const els = {};
  const el = (id) => (els[id] ??= { id, textContent: '', className: '', value: '', placeholder: '', handlers: {}, classList: { toggle() {} }, addEventListener(t, f) { this.handlers[t] = f; } });
  const calls = { native: [], send: [] };
  globalThis.chrome = {
    storage: { local: { get: async () => ({ token: 'abcd1234' }), set: async () => {} }, session: { get: async () => ({}) }, onChanged: { addListener() {} } },
    runtime: {
      sendNativeMessage: async (host, msg) => { calls.native.push([host, msg]); return native(); },
      sendMessage: async (msg) => { calls.send.push(msg); return send(); },
    },
  };
  delete globalThis.browser;
  new Function('document', 'Date', src)({ getElementById: el }, Date);
  return { el, calls, click: async (id) => { await els[id].handlers.click(); await new Promise((r) => setImmediate(r)); } };
}

(async () => {
  let b = boot({ native: async () => ({ ok: true, running: true, started: true }), send: async () => ({ ok: true }) });
  await b.click('launch');
  check('launch: started', b.el('msg').textContent === '앱을 켰습니다.', b.el('msg').textContent);
  check('launch: asks the registered host with the start command', b.calls.native[0][0] === 'com.lyricsoverlay.host' && b.calls.native[0][1].cmd === 'start');

  b = boot({ native: async () => ({ ok: true, running: true, started: false }), send: async () => ({ ok: true }) });
  await b.click('launch');
  check('launch: already running', b.el('msg').textContent === '이미 실행 중입니다.', b.el('msg').textContent);

  b = boot({ native: async () => ({ ok: false, error: 'started but did not come up in time' }), send: async () => ({ ok: true }) });
  await b.click('launch');
  check('launch: host error is shown, not hidden', b.el('msg').textContent.includes('실패') && b.el('msg').textContent.includes('did not come up'), b.el('msg').textContent);

  b = boot({ native: async () => null, send: async () => ({ ok: true }) });
  await b.click('launch');
  check('launch: empty reply is a visible failure', b.el('msg').textContent.startsWith('실패'), b.el('msg').textContent);

  b = boot({ native: async () => { throw new Error('Specified native messaging host not found.'); }, send: async () => ({ ok: true }) });
  await b.click('launch');
  check('launch: host not registered -> tells how to fix it', b.el('msg').textContent.includes('한 번 직접 실행'), b.el('msg').textContent);

  b = boot({ native: async () => ({ ok: true }), send: async () => ({ ok: true }) });
  await b.click('quit');
  check('quit: asks the background worker', b.calls.send[0].type === 'quit' && b.el('msg').textContent === '앱을 껐습니다.', b.el('msg').textContent);

  b = boot({ native: async () => ({ ok: true }), send: async () => ({ ok: false, error: '앱에 연결할 수 없습니다 (앱이 꺼져 있나요?)' }) });
  await b.click('quit');
  check('quit: app already off -> shows why', b.el('msg').textContent.includes('실패') && b.el('msg').textContent.includes('꺼져'), b.el('msg').textContent);

  b = boot({ native: async () => ({ ok: true }), send: async () => { throw new Error('worker gone'); } });
  await b.click('quit');
  check('quit: extension error is visible', b.el('msg').textContent.includes('실패'), b.el('msg').textContent);

  console.log(fails ? `${fails} FAILED` : 'ALL PASS');
  process.exit(fails);
})();
