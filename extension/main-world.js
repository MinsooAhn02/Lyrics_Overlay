// Runs in the page's MAIN world: reads YouTube's player data and posts it to content.js.
(() => {
  const read = () => {
    let pr = null;
    try { pr = document.getElementById('movie_player')?.getPlayerResponse?.() ?? null; } catch (e) { /* ignore */ }
    pr = pr || window.ytInitialPlayerResponse || null;
    const vd = pr?.videoDetails;
    const len = Number(vd?.lengthSeconds) || 0;
    return {
      src: 'lotg-main',
      videoId: vd?.videoId || null,
      title: vd?.title || null,
      author: vd?.author || null,
      desc: typeof vd?.shortDescription === 'string' ? vd.shortDescription.slice(0, 3000) : null, // for 'Provided to YouTube by' parsing in content.js
      category: pr?.microformat?.playerMicroformatRenderer?.category || null,
      lengthSeconds: len,
      isLive: !!vd?.isLive || (!!vd?.isLiveContent && !len),
    };
  };
  const post = () => { window.postMessage(read(), location.origin); captions(); };

  // ---- Official (manual, non-ASR) captions: fetched once per video and handed to content.js ----
  // Since 2025 YouTube answers timedtext requests that lack a video-bound 'pot' token with an EMPTY 200. The only
  // token source in the browser is a timedtext request the player made itself, so: capture that URL
  // (PerformanceObserver), and if the player has not asked for captions yet, make it do so invisibly for a moment.
  const potUrls = new Map(); // videoId -> player-made timedtext URL carrying pot
  let capVideo = null;
  const notePot = (name) => {
    try {
      if (!name.includes('/api/timedtext')) return;
      const u = new URL(name);
      if (u.searchParams.get('pot') && u.searchParams.get('v')) potUrls.set(u.searchParams.get('v'), name);
    } catch (err) { /* ignore */ }
  };
  try {
    new PerformanceObserver((l) => { for (const e of l.getEntries()) notePot(e.name); }).observe({ type: 'resource', buffered: true });
  } catch (e) { /* unsupported: direct fetch only */ }

  const playerResponse = () => {
    let pr = null;
    try { pr = document.getElementById('movie_player')?.getPlayerResponse?.() ?? null; } catch (e) { /* ignore */ }
    return pr || window.ytInitialPlayerResponse || null;
  };
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // Reads a timedtext track. words=false: one entry per caption line (manual captions);
  // words=true: one entry per word with its own start time (auto-generated track, used to re-time lyrics).
  async function fetchLines(url, words) {
    const u = new URL(url, location.href);
    u.searchParams.set('fmt', 'json3');
    const r = await fetch(u.toString(), { credentials: 'same-origin' });
    const txt = await r.text();
    let ev = [];
    try { ev = JSON.parse(txt).events || []; } catch (e) { return { lines: [], status: r.ok ? 'empty' : 'http' + r.status }; }
    const lines = [];
    const max = words ? 3000 : 600;
    for (const e of ev) {
      if (!Array.isArray(e.segs) || !Number.isFinite(e.tStartMs)) continue;
      if (words) {
        for (const s of e.segs) {
          const t = String(s.utf8 || '').replace(/\s+/g, ' ').trim();
          if (t && lines.length < max) lines.push([Math.round(e.tStartMs + (Number(s.tOffsetMs) || 0)), t.slice(0, 40)]);
        }
      } else {
        const t = e.segs.map((s) => s.utf8 || '').join('').replace(/\s+/g, ' ').trim();
        if (t && lines.length < max) lines.push([Math.round(e.tStartMs), t.slice(0, 300)]);
      }
    }
    return { lines, status: lines.length ? 'ok' : 'empty' };
  }

  // The pot token is bound to the VIDEO, not to a track: copy pot (and c) from the player's own request onto any track URL.
  function withPot(base, vid) {
    const u = new URL(base, location.href);
    const p = potUrls.get(vid);
    if (p) { const q = new URL(p).searchParams; for (const k of ['pot', 'c']) if (q.get(k)) u.searchParams.set(k, q.get(k)); }
    return u.toString();
  }

  // Makes the player request a track (so its pot URL shows up), with the caption layer hidden; restores the prior state.
  async function probePot(vid, track) {
    const pl = document.getElementById('movie_player');
    if (!pl || !pl.loadModule || !pl.setOption) return;
    let had = null;
    try { had = pl.getOption('captions', 'track'); } catch (e) { /* ignore */ }
    if (had && had.languageCode) return; // captions already on: the player's own request is already recorded
    const st = document.createElement('style');
    st.textContent = '.ytp-caption-window-container{visibility:hidden!important}';
    document.head.appendChild(st);
    let obj = { languageCode: track.languageCode };
    try {
      pl.loadModule('captions');
      try { // prefer the player's own track object (it knows manual vs auto-generated)
        const list = pl.getOption('captions', 'tracklist') || [];
        obj = list.find((x) => x && x.languageCode === track.languageCode && ((x.kind === 'asr') === (track.kind === 'asr'))) || obj;
      } catch (e) { /* fall back to the language code */ }
      pl.setOption('captions', 'track', obj);
      for (let i = 0; i < 16 && !potUrls.has(vid); i++) await sleep(250);
    } finally {
      try { // restore only when the track is still the one we set (the user may have picked another meanwhile)
        const cur = pl.getOption('captions', 'track');
        if (!cur || !cur.languageCode || cur.languageCode === obj.languageCode) { pl.setOption('captions', 'track', {}); pl.unloadModule('captions'); }
      } catch (e) { /* ignore */ }
      st.remove();
    }
  }

  async function readTrack(vid, track, words) {
    let res = await fetchLines(withPot(track.baseUrl, vid), words);
    if (!res.lines.length && !potUrls.has(vid)) { // blocked without pot: let the player make a request, then retry
      await probePot(vid, track);
      res = await fetchLines(withPot(track.baseUrl, vid), words);
    }
    return res;
  }

  async function captions() {
    const pr = playerResponse();
    const vid = pr?.videoDetails?.videoId;
    if (!vid || vid === capVideo) return;
    capVideo = vid; // one attempt per video, whatever the outcome
    const out = { src: 'lotg-cap', videoId: vid, status: 'none', lang: '', name: '', lines: [], asr: null, desc: '' };
    // Mix / playlist / album uploads list their songs with timestamps in the description: pass it on (only then).
    const vd = pr?.videoDetails;
    const d = typeof vd?.shortDescription === 'string' ? vd.shortDescription : '';
    if (Number(vd?.lengthSeconds) >= 600 && (d.match(/\d{1,2}:\d{2}/g) || []).length >= 3) out.desc = d.slice(0, 6000);
    const cat = pr?.microformat?.playerMicroformatRenderer?.category;
    const NON_MUSIC = /^(Gaming|News & Politics|Education|Howto & Style|Science & Technology|Sports|Autos & Vehicles|Pets & Animals|Travel & Events|Nonprofits & Activism)$/;
    const sec = Number(vd?.lengthSeconds) || 0;
    const musicLike = !cat || cat === 'Music' || (!NON_MUSIC.test(cat) && ((sec > 0 && sec <= 720) || (d.match(/\d{1,2}:\d{2}/g) || []).length >= 3)); // keep in sync with content.js
    if (location.hostname !== 'music.youtube.com' && !musicLike) { // the app ignores non-music anyway: do not touch the player
      if (capVideo === vid) window.postMessage(out, location.origin);
      return;
    }
    try {
      const tl = pr?.captions?.playerCaptionsTracklistRenderer;
      const all = tl?.captionTracks || [];
      const manual = all.filter((t) => t && t.kind !== 'asr' && t.baseUrl);
      if (manual.length) {
        const di = tl?.audioTracks?.[0]?.defaultCaptionTrackIndex; // the track YouTube itself pairs with the audio language
        const track = manual.find((t) => di != null && all[di] === t) || manual[0];
        out.lang = String(track.languageCode || '').slice(0, 16);
        out.name = String(track.name?.simpleText || track.name?.runs?.[0]?.text || '').slice(0, 80);
        const res = await readTrack(vid, track, false);
        out.lines = res.lines;
        out.status = res.status;
      }
      // No (usable) manual captions: also fetch the auto-generated track. The app uses it only as a clock to re-time
      // reference lyrics (covers etc.), never as lyrics.
      const asrTrack = all.find((t) => t && t.kind === 'asr' && t.baseUrl);
      if (out.lines.length < 6 && asrTrack) {
        try {
          const res = await readTrack(vid, asrTrack, true);
          out.asr = { status: res.status, lang: String(asrTrack.languageCode || '').slice(0, 16), lines: res.lines };
        } catch (e) { out.asr = { status: 'error', lang: '', lines: [] }; }
      }
    } catch (e) { out.status = 'error'; out.lines = []; }
    if (capVideo === vid) window.postMessage(out, location.origin);
  }
  // content.js delegates seeks here: the player API keeps YouTube's own state (UI, buffering) consistent.
  window.addEventListener('message', (e) => {
    const d = e.data;
    if (e.source !== window || e.origin !== location.origin || !d || d.src !== 'lotg-cmd' || d.type !== 'seek') return;
    const sec = Number(d.sec);
    if (!Number.isFinite(sec) || sec < 0) return;
    try { document.getElementById('movie_player').seekTo(sec, true); }
    catch (err) { const v = document.querySelector('video.html5-main-video') || document.querySelector('video'); if (v) v.currentTime = sec; }
  });
  window.addEventListener('yt-navigate-finish', post);
  window.addEventListener('yt-page-data-updated', post);
  setInterval(post, 1000); // redundancy: SPA events can be missed
  post();
})();
