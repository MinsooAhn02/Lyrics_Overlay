// Run: node tools\shuffle-check.js [--live]   (exit code = number of failed checks)
// Drives extension/content.js (shuffle order / moves / failures) against a fake page. With --live it also runs
// main-world.js's playlist reader against YouTube for a public playlist (network).
const fs = require('fs'), path = require('path');
const ext = (f) => fs.readFileSync(path.join(__dirname, '..', 'extension', f), 'utf8');
let fails = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name} ${extra}`); if (!ok) fails++; };
const tickAsync = () => new Promise((r) => setImmediate(r));

// ids: what the page's playlist reader answers ({ids} or {error}); null = never answers
function boot({ url = '/watch?v=AAAAAAAAA00&list=PLtestlist001', host = 'www.youtube.com', reply = null, storage = true } = {}) {
  const sent = [], posted = [], assigned = [];
  let onMessage = null, onCmd = null;
  const listeners = {};
  const video = { duration: 200, currentTime: 50, paused: false, addEventListener: (t, f) => { (listeners[t] ??= []).push(f); } };
  const loc = {
    hostname: host, origin: 'https://' + host, pathname: '/watch',
    get search() { return url.slice(url.indexOf('?')); },
    get href() { return loc.origin + url; },
    assign: (u) => { assigned.push(u); url = u; },
  };
  const store = new Map();
  const ss = storage ? { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => store.set(k, String(v)), removeItem: (k) => store.delete(k) }
                     : { getItem: () => null, setItem: () => { throw new Error('denied'); }, removeItem: () => {} };
  const win = {
    addEventListener: (t, f) => { if (t === 'message') onMessage = f; },
    postMessage: (d) => { posted.push(d); if (d.type === 'playlist' && reply) setImmediate(() => onMessage({ source: win, origin: loc.origin, data: { src: 'lotg-list', req: d.req, list: d.list, ...reply } })); },
  };
  win.window = win;
  const doc = { title: 'Song - YouTube', querySelector: (s) => (s.includes('video') && !s.includes('ad-showing') ? video : null) };
  globalThis.chrome = {
    runtime: { id: 'x', getManifest: () => ({ version: 'test' }), sendMessage: async (m) => { if (m.type === 'nowPlaying') sent.push(m.payload); return { ok: true }; }, onMessage: { addListener: (f) => { onCmd = f; } } },
  };
  delete globalThis.browser;
  let tick = null;
  new Function('window', 'location', 'document', 'setInterval', 'sessionStorage', ext('content.js'))(win, loc, doc, (f) => { tick = f; return 1; }, ss);
  onMessage({ source: win, origin: loc.origin, data: { src: 'lotg-main', videoId: new URLSearchParams(loc.search).get('v'), title: 'Song', author: 'Ch', lengthSeconds: 200, category: 'Music' } });
  return {
    sent, posted, assigned, store, video,
    cmd: async (c) => { onCmd({ type: 'commands', commands: [c] }); for (let i = 0; i < 5; i++) await tickAsync(); },
    tick: async () => { tick(); await tickAsync(); },
    ended: async () => { for (const f of listeners.ended || []) f(); for (let i = 0; i < 3; i++) await tickAsync(); },
    state: () => (sent.length ? sent.at(-1).shuffle : undefined),
    // a "new page": same tab storage, the url we moved to
    reload: () => url,
  };
}
const ids = (n) => Array.from({ length: n }, (_, i) => 'AAAAAAAAA' + String(i).padStart(2, '0'));

(async () => {
  // 1. not a playlist / a mix / YouTube Music -> no button
  let e = boot({ url: '/watch?v=AAAAAAAAAA0' }); await e.tick();
  check('no list -> no shuffle button', e.state() === '', JSON.stringify(e.state()));
  e = boot({ url: '/watch?v=AAAAAAAAAA0&list=RDAAAAAAAAAA0' }); await e.tick();
  check('mix (RD...) -> no shuffle button', e.state() === '');
  e = boot({ host: 'music.youtube.com' }); await e.tick();
  check('YouTube Music -> no shuffle button (not supported yet)', e.state() === '');

  // 2. turn on: reads the list, current song first, every song exactly once
  const list = ids(30);
  e = boot({ reply: { ids: list } }); await e.tick();
  check('playlist -> button off', e.state() === 'off');
  await e.cmd('shuffle:on');
  const s = JSON.parse(e.store.get('lotg-shuffle'));
  check('on: playlist was requested from the page', e.posted.some((p) => p.type === 'playlist' && p.list === 'PLtestlist001'));
  check('on: state reported as on', e.state() === 'on', JSON.stringify(e.state()));
  check('on: every song exactly once', s.order.length === 30 && new Set(s.order).size === 30 && list.every((x) => s.order.includes(x)));
  check('on: current song first', s.order[0] === 'AAAAAAAAA00');
  check('on: does not move by itself', e.assigned.length === 0);

  // 3. next / ended / prev follow the shuffled order
  await e.cmd('next');
  check('next -> 2nd song of the order, same list', e.assigned.at(-1) === `/watch?v=${s.order[1]}&list=PLtestlist001`, e.assigned.at(-1));
  await e.ended();
  check('song ended -> 3rd song', e.assigned.at(-1) === `/watch?v=${s.order[2]}&list=PLtestlist001`, e.assigned.at(-1));
  await e.cmd('prev');
  check('prev -> back to the 2nd song', e.assigned.at(-1) === `/watch?v=${s.order[1]}&list=PLtestlist001`);
  const before = e.assigned.length;
  await e.cmd('prev'); await e.cmd('prev');
  check('prev at the start restarts the song instead of leaving', e.assigned.length === before + 1 && e.video.currentTime === 0, `moves=${e.assigned.length - before}`);

  // 4. a full round: after the last song a NEW order starts (list read again)
  e = boot({ reply: { ids: ids(3) } }); await e.cmd('shuffle:on');
  await e.cmd('next'); await e.cmd('next');
  const reads = e.posted.filter((p) => p.type === 'playlist').length;
  await e.cmd('next');
  check('after the last song the list is read again and a new round starts', e.posted.filter((p) => p.type === 'playlist').length === reads + 1 && e.assigned.length === 3, `moves=${e.assigned.length}`);

  // 5. off / leaving the playlist -> normal buttons again
  e = boot({ reply: { ids: ids(5) } }); await e.cmd('shuffle:on'); await e.cmd('shuffle:off');
  check('off: state off, storage cleared', e.state() === 'off' && !e.store.has('lotg-shuffle'));
  await e.cmd('next');
  check('off: next does not use the shuffle order', e.assigned.length === 0);
  e = boot({ reply: { ids: ids(5) } }); await e.cmd('shuffle:on');
  e.store.set('lotg-shuffle', JSON.stringify({ ...JSON.parse(e.store.get('lotg-shuffle')), list: 'PLotherlist01' }));
  await e.cmd('next');
  check('another playlist in the url -> shuffle not applied', e.assigned.length === 0);

  // 6. failures are visible, never "on"
  e = boot({ reply: { error: 'http 403' } }); await e.cmd('shuffle:on');
  check('read error -> err:<reason>', e.state() === 'err:http 403', JSON.stringify(e.state()));
  e = boot({ reply: { ids: ['AAAAAAAAA00'] } }); await e.cmd('shuffle:on');
  check('1 playable song -> err', String(e.state()).startsWith('err:'), JSON.stringify(e.state()));
  e = boot({ reply: { ids: ['bad', 42, '<script>xx', 'AAAAAAAAA01', 'AAAAAAAAA01'] } }); await e.cmd('shuffle:on');
  check('invalid / duplicate ids dropped (1 left) -> err', String(e.state()).startsWith('err:'));
  e = boot({ reply: { ids: ids(5) }, storage: false }); await e.cmd('shuffle:on');
  check('storage blocked -> err, not on', e.state() === 'err:storage blocked', JSON.stringify(e.state()));
  e = boot({ reply: null }); onTimeout: { await e.cmd('shuffle:on'); }
  check('reader not answering -> busy (until the 60 s timeout)', e.state() === 'busy');

  // 7. live: main-world.js reads a real public playlist through the page API
  if (process.argv.includes('--live')) {
    const listId = 'PLa60DXNuGTy7VjJSsmeBo1sPAOBKnCzYc';
    const html = await (await fetch(`https://www.youtube.com/playlist?list=${listId}`, { headers: { 'Accept-Language': 'en' } })).text();
    const cfg = { INNERTUBE_API_KEY: html.match(/"INNERTUBE_API_KEY":"([^"]+)"/)[1], INNERTUBE_CONTEXT: JSON.parse(html.match(/"INNERTUBE_CONTEXT":(\{.*?\}),"INNERTUBE_CONTEXT_CLIENT_NAME"/)[1]) };
    const handlers = [];
    const win = { ytcfg: { get: (k) => cfg[k] }, addEventListener: (t, f) => { if (t === 'message') handlers.push(f); }, postMessage: (d) => { if (d.src === 'lotg-list') win.result = d; } };
    win.window = win;
    const pageFetch = (u, o) => fetch('https://www.youtube.com' + u, o);
    new Function('window', 'document', 'location', 'fetch', 'setInterval', 'PerformanceObserver', ext('main-world.js'))(
      win, { getElementById: () => null, querySelector: () => null }, { origin: 'https://www.youtube.com' }, pageFetch, () => 1, undefined);
    const t0 = Date.now();
    for (const h of handlers) h({ source: win, origin: 'https://www.youtube.com', data: { src: 'lotg-cmd', type: 'playlist', list: listId, req: 'r1' } });
    while (!win.result && Date.now() - t0 < 60000) await new Promise((r) => setTimeout(r, 100));
    const r = win.result || {};
    check('live: playlist read through the page API', Array.isArray(r.ids) && r.ids.length > 100, `songs=${r.ids && r.ids.length} error=${r.error} ms=${Date.now() - t0}`);
    check('live: no duplicates, all valid ids', Array.isArray(r.ids) && new Set(r.ids).size === r.ids.length && r.ids.every((x) => /^[A-Za-z0-9_-]{11}$/.test(x)));
    win.result = null;
    for (const h of handlers) h({ source: win, origin: 'https://www.youtube.com', data: { src: 'lotg-cmd', type: 'playlist', list: 'PLdoesnotexist0000000000000000000', req: 'r2' } });
    const t1 = Date.now(); while (!win.result && Date.now() - t1 < 30000) await new Promise((res) => setTimeout(res, 100));
    check('live: unknown playlist -> error or empty, no crash', !!win.result && (win.result.error || (win.result.ids || []).length === 0), JSON.stringify(win.result && { e: win.result.error, n: (win.result.ids || []).length }));
  }

  console.log(fails ? `${fails} FAILED` : 'ALL PASS');
  process.exit(fails);
})();
