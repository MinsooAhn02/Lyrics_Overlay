// Runs extension/main-world.js against a fake YouTube page and checks the caption token (pot) handling:
//   node tools/caption-pot-check.js
// Fake timedtext answers EMPTY without a pot (like YouTube) and returns lines with one.
const fs = require('fs'), path = require('path'), vm = require('vm');
const src = fs.readFileSync(path.join(__dirname, '..', 'extension', 'main-world.js'), 'utf8');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function page({ captionsOn, playerRequestsAt, probeWorks }) {
  const posted = [], timers = [];
  let observer = null, fetches = 0, track = captionsOn ? { languageCode: 'ko' } : {};
  const VID = 'abcdefghijk';
  const playerRequest = () => observer && observer({ getEntries: () => [{ name: `https://www.youtube.com/api/timedtext?v=${VID}&lang=ko&pot=TOKEN&c=WEB` }] });
  if (playerRequestsAt != null) timers.push(setTimeout(playerRequest, playerRequestsAt));
  const pr = { videoDetails: { videoId: VID, lengthSeconds: '200', shortDescription: '' }, microformat: { playerMicroformatRenderer: { category: 'Music' } },
    captions: { playerCaptionsTracklistRenderer: { captionTracks: [{ languageCode: 'ko', kind: '', baseUrl: `https://www.youtube.com/api/timedtext?v=${VID}&lang=ko` }] } } };
  const player = { getPlayerResponse: () => pr, loadModule() {}, unloadModule() {}, getOption: (m, k) => (k === 'track' ? track : []),
    setOption: (m, k, v) => { track = v; if (probeWorks && v && v.languageCode) setTimeout(playerRequest, 300); }, seekTo() {} };
  const win = { location: { origin: 'https://www.youtube.com', href: 'https://www.youtube.com/watch?v=' + VID, hostname: 'www.youtube.com' },
    postMessage: (m) => posted.push(m), addEventListener() {}, ytInitialPlayerResponse: pr };
  const ctx = {
    window: win, location: win.location, URL, Map, Set, Promise, JSON, Number, String, Array, Error, CustomEvent: class {},
    setTimeout: (f, ms) => { const t = setTimeout(f, ms); timers.push(t); return t; }, setInterval: (f, ms) => { const t = setInterval(f, ms); timers.push(t); return t; },
    PerformanceObserver: class { constructor(cb) { observer = (l) => cb(l); } observe() {} },
    document: { getElementById: () => player, createElement: () => ({ remove() {} }), head: { appendChild() {} }, querySelector: () => null },
    fetch: async (u) => { fetches++; const ok = new URL(u).searchParams.get('pot');
      return { ok: true, status: 200, text: async () => (ok ? JSON.stringify({ events: [{ tStartMs: 1000, segs: [{ utf8: 'line one' }] }, { tStartMs: 3000, segs: [{ utf8: 'line two' }] }] }) : '') }; },
  };
  vm.runInNewContext(src, ctx);
  return { caps: () => posted.filter((m) => m.src === 'lotg-cap'), stop: () => timers.forEach((t) => { clearTimeout(t); clearInterval(t); }) };
}

let fails = 0;
const check = (name, ok, detail) => { if (!ok) fails++; console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? '  ->  ' + detail : '')); };
const statuses = (p) => p.caps().map((c) => c.status + ':' + c.lines.length).join(' ');

(async () => {
  // 1. Captions already on, the player asks for them 1.5 s after load (the reload case): must wait, not give up.
  let p = page({ captionsOn: true, playerRequestsAt: 1500 });
  await sleep(6000); check('captions on, player request comes late -> ok', p.caps().some((c) => c.status === 'ok' && c.lines.length === 2), statuses(p)); p.stop();

  // 2. Captions off, probe makes the player request -> ok on the first report.
  p = page({ captionsOn: false, probeWorks: true });
  await sleep(3000); check('captions off, probe works -> ok', p.caps()[0]?.status === 'ok', statuses(p)); p.stop();

  // 3. No token for 5 s (probe fails), it shows up at 8 s: first report 'nopot', then one retry -> ok.
  p = page({ captionsOn: false, probeWorks: false, playerRequestsAt: 8000 });
  await sleep(6000); check('no token yet -> status nopot (not empty)', p.caps()[0]?.status === 'nopot', statuses(p));
  await sleep(5000); check('token shows up later -> retried once -> ok', p.caps().length === 2 && p.caps()[1].status === 'ok', statuses(p)); p.stop();

  // 4. Token never comes: exactly one report, no retry loop.
  p = page({ captionsOn: false, probeWorks: false });
  await sleep(9000); check('token never comes -> one nopot report, no loop', p.caps().length === 1 && p.caps()[0].status === 'nopot', statuses(p)); p.stop();

  console.log(fails ? fails + ' FAILED' : 'ALL PASS');
  process.exit(fails);
})();
