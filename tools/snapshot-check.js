// Run: node tools\snapshot-check.js   (exit code = number of failed checks)
// Drives extension/content.js against a fake page: right after a navigation the url names the NEW video while the
// player data (main) and document.title still describe the OLD one. No report may pair the new id with the old title.
const fs = require('fs'), path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'extension', 'content.js'), 'utf8');
let fails = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name} ${extra}`); if (!ok) fails++; };

function boot() {
  const sent = [];
  let onMessage = null, tick = null;
  let now = 1_000_000;
  const realNow = Date.now; Date.now = () => now;
  const video = { duration: 200, currentTime: 12, paused: false, addEventListener() {} };
  const page = { search: '?v=NEWVIDEO001', pathname: '/watch', hostname: 'www.youtube.com', origin: 'https://www.youtube.com', href: 'https://www.youtube.com/watch?v=NEWVIDEO001' };
  const win = {
    addEventListener: (t, f) => { if (t === 'message') onMessage = f; },
    postMessage() {},
  };
  win.window = win;
  const doc = {
    title: 'Old Song - YouTube',
    querySelector: (sel) => (sel.includes('video') ? video : null),
  };
  globalThis.chrome = {
    runtime: { id: 'x', getManifest: () => ({ version: 'test' }), sendMessage: async (m) => { if (m.type === 'nowPlaying') sent.push(m.payload); return { ok: true }; }, onMessage: { addListener() {} } },
  };
  delete globalThis.browser;
  new Function('window', 'location', 'document', 'setInterval', src)(win, page, doc, (f) => { tick = f; return 1; });
  const mainMsg = (d) => onMessage({ source: win, origin: page.origin, data: { src: 'lotg-main', ...d } });
  return { sent, mainMsg, doc, page, advance: (ms) => { now += ms; }, run: async () => { tick(); await new Promise((r) => setImmediate(r)); }, restore: () => { Date.now = realNow; } };
}

(async () => {
  // A. player data still old, 1 s after the navigation -> nothing is sent
  let e = boot();
  e.mainMsg({ videoId: 'OLDVIDEO001', title: 'Old Song', author: 'OldCh', lengthSeconds: 200, category: 'Music' });
  await e.run(); e.advance(1000); await e.run();
  check('A: stale player data -> no report with the old title', e.sent.length === 0, `sent=${e.sent.length}`);
  // B. player data catches up -> reported with the NEW title
  e.mainMsg({ videoId: 'NEWVIDEO001', title: 'New Song', author: 'NewCh', lengthSeconds: 200, category: 'Music' });
  e.advance(1500); await e.run();
  check('B: after the player data catches up the new title is sent', e.sent.length >= 1 && e.sent.at(-1).videoId === 'NEWVIDEO001' && e.sent.at(-1).title === 'New Song', JSON.stringify(e.sent.at(-1) && e.sent.at(-1).title));
  check('B: no report ever paired the new id with the old title', e.sent.every((p) => !(p.videoId === 'NEWVIDEO001' && p.title.includes('Old Song'))));
  e.restore();

  // C. player data never catches up -> after 4 s we still report (fallback), so a broken main world cannot silence the extension
  e = boot();
  e.mainMsg({ videoId: 'OLDVIDEO001', title: 'Old Song', author: 'OldCh', lengthSeconds: 200, category: 'Music' });
  await e.run(); e.advance(3000); await e.run();
  const before = e.sent.length;
  e.advance(2500); await e.run();
  check('C: held back for the first seconds', before === 0, `sent before=${before}`);
  check('C: reports again once 4 s have passed (fallback)', e.sent.length === 1 && e.sent[0].videoId === 'NEWVIDEO001', `sent=${e.sent.length}`);
  e.restore();

  // D. no player data at all (main world not loaded yet) -> reported immediately like before
  e = boot();
  await e.run();
  check('D: no player data yet -> still reports (document.title fallback)', e.sent.length === 1 && e.sent[0].videoId === 'NEWVIDEO001', `sent=${e.sent.length}`);
  check('D: report says who sent it (version, url id, player id)', typeof e.sent[0].dbg === 'string' && e.sent[0].dbg.startsWith('vtest') && e.sent[0].dbg.includes('u=NEWVIDEO001') && e.sent[0].dbg.includes('m=-') && e.sent[0].dbg.includes('sv=0'), e.sent[0].dbg);
  e.restore();

  console.log(fails ? `${fails} FAILED` : 'ALL PASS');
  process.exit(fails);
})();
