// Isolated world: samples <video> state, sends it to the service worker, executes returned commands.
(() => {
  const api = globalThis.browser ?? globalThis.chrome;
  const IS_MUSIC_HOST = location.hostname === 'music.youtube.com';
  const RECENT_MS = 15000;
  let main = null;          // last message from main-world.js
  let lastEventAt = 0;
  let inflight = false;
  let again = false;
  let lastSendAt = 0;
  let fastUntil = 0;

  let capPending = null;    // captions message not yet acknowledged by the app (resent until it is)
  let capTriedAt = 0;

  window.addEventListener('message', (e) => {
    if (e.source !== window || e.origin !== location.origin || !e.data) return;
    if (e.data.src === 'lotg-main') main = e.data;
    else if (e.data.src === 'lotg-cap' && /^[A-Za-z0-9_-]{11}$/.test(e.data.videoId)) { capPending = e.data; capTriedAt = 0; sendCaptions(); }
  });

  async function sendCaptions() {
    const c = capPending;
    if (!c || !api.runtime?.id || Date.now() - capTriedAt < 4000) return;
    capTriedAt = Date.now();
    const payload = { videoId: c.videoId, status: String(c.status).slice(0, 16), lang: String(c.lang || '').slice(0, 16), name: String(c.name || '').slice(0, 80), lines: Array.isArray(c.lines) ? c.lines : [],
      asr: c.asr && Array.isArray(c.asr.lines) ? { status: String(c.asr.status).slice(0, 16), lang: String(c.asr.lang || '').slice(0, 16), lines: c.asr.lines } : null,
      desc: typeof c.desc === 'string' ? c.desc.slice(0, 6000) : '' };
    try {
      const r = await api.runtime.sendMessage({ type: 'captions', payload });
      if (r && r.ok && capPending === c) capPending = null;
    } catch (err) { /* worker restarting: next tick retries */ }
  }

  const getVideo = () => document.querySelector('video.html5-main-video') || document.querySelector('video');


  // ---- structured music metadata: ytm > musicSection > topic > title. Wrong value is worse than empty. ----
  const clean = (s, n) => String(s || '').replace(/[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u2028\u2029\ufeff]/g, ' ').replace(/\s+/g, ' ').trim().slice(0, n);
  const txt = (el) => (el ? clean(el.textContent, 400) : '');
  const DECOR = /official|\bm\/?v\b|music video|lyrics?|audio|\bhd\b|\b4k\b|visuali[sz]er|뮤직\s*비디오|가사|공식|歌詞|公式/i;
  const SEPS = [' - ', ' – ', ' — ', ' | ', '：', ' : '];
  const ALBUM_LABELS = /^(album|앨범|アルバム)$/i, SONG_LABELS = /^(song|songs|track|곡|노래|楽曲|曲)$/i, ARTIST_LABELS = /^(artist|artists|아티스트|가수|アーティスト)$/i;

  function stripDecor(t) {
    let s = ' ' + t + ' ';
    s = s.replace(/\([^()]*\)|\[[^\[\]]*\]|【[^】]*】|「[^」]*」(?=\s*$)/g, (m) => (DECOR.test(m) ? ' ' : m));
    s = s.replace(/(\s+(Official\s+)?(Music\s+)?(M\/V|MV|Video|Audio|Lyrics?|가사|뮤직비디오))+\s*$/i, ' ');
    return clean(s, 300);
  }

  function fromTitle(title, channel) {
    const t = stripDecor(title);
    let best = -1, sep = '';
    for (const sp of SEPS) { const i = t.indexOf(sp); if (i > 0 && (best < 0 || i < best)) { best = i; sep = sp; } }
    if (best < 0) return { track: t, artist: '', album: '' }; // no separator: artist unknown
    let artist = clean(t.slice(0, best), 200), track = clean(t.slice(best + sep.length), 300);
    if (!artist || !track) return { track: '', artist: '', album: '' };
    // "Title - Artist/Series" when the channel name is the right-hand part (equal, or one contains the other): swap.
    // "Artist - Title" (left part related to the channel) stays as is.
    const nz = (x) => String(x).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
    const chRaw = clean(channel, 200).replace(/ - Topic$/, '');
    const ch = nz(chRaw);
    const GENERIC = /^(official|music|records?|entertainment|ent|channel|tv|vevo|labels?|studio|studios|media|뮤직|공식)$/;
    const chWords = chRaw.split(/\s+/).map(nz).filter((w) => w.length >= 2 && !GENERIC.test(w));
    const related = (part) => {
      const p = nz(part);
      if (ch.length < 2 || p.length < 2) return false;
      return p.includes(ch) || ch.includes(p) || chWords.some((w) => p.includes(w));
    };
    if (ch && related(track) && !related(artist)) [artist, track] = [track, artist];
    return { track, artist: clean(artist, 200), album: '' };
  }

  // The watch page swaps its "music in this video" rows AFTER the url / video-id attribute change, so right after a
  // navigation they still describe the PREVIOUS song. A song name that appears in neither this video's title nor its
  // description cannot be trusted for this video.
  const normKey = (x) => String(x || '').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
  function sectionMatchesVideo(out, m) {
    const t = normKey(out.track);
    return t.length >= 2 && !!m && (normKey(m.title).includes(t) || normKey(m.desc).includes(t));
  }

  function fromYtm(m) {
    try {
      if (!IS_MUSIC_HOST) return null;
      const bar = document.querySelector('ytmusic-player-bar');
      if (!bar) return null;
      const track = txt(bar.querySelector('.title'));
      if (!track || track !== clean(m.title, 400)) return null; // bar may still show the previous song: only trust it when it matches videoDetails
      const by = bar.querySelector('.byline');
      let artist = '', album = '';
      if (by) {
        const links = [...by.querySelectorAll('a')];
        const href = (a) => a.getAttribute('href') || '';
        const artists = links.filter((a) => /(^|\/)channel\/|browse\/UC/.test(href(a))).map(txt).filter(Boolean);
        artist = artists.join(', ');
        album = txt(links.find((a) => /browse\/MPRE/.test(href(a))));
      }
      if (!artist) artist = clean(m.author, 200).replace(/ - Topic$/, '');
      return artist ? { track, artist, album } : null;
    } catch (e) { return null; }
  }

  function fromMusicSection(videoId, m) {
    try {
      if (IS_MUSIC_HOST) return null;
      const flexy = document.querySelector('ytd-watch-flexy');
      if (!flexy || flexy.getAttribute('video-id') !== videoId) return null; // description DOM belongs to the playing video
      const out = { track: '', artist: '', album: '' };
      for (const row of flexy.querySelectorAll('ytd-info-row-renderer, ytd-video-description-music-section-renderer ytd-info-row-renderer')) {
        const label = txt(row.querySelector('#title, .title'));
        const val = txt(row.querySelector('#default-metadata-section, #content, .content, yt-formatted-string:not(#title)'));
        if (!label || !val) continue;
        if (SONG_LABELS.test(label) && !out.track) out.track = val;
        else if (ARTIST_LABELS.test(label) && !out.artist) out.artist = val;
        else if (ALBUM_LABELS.test(label) && !out.album) out.album = val;
      }
      return out.track && out.artist && sectionMatchesVideo(out, m) ? out : null;
    } catch (e) { return null; }
  }

  function fromTopic(m) {
    try {
      const a = clean(m.author, 300);
      if (!/ - Topic$/.test(a) || !m.title) return null;
      const out = { track: clean(m.title, 300), artist: clean(a.replace(/ - Topic$/, ''), 200), album: '' };
      const lines = String(m.desc || '').split(/\r?\n/).map((l) => l.trim());
      const i = lines.findIndex((l) => /^Provided to YouTube by/i.test(l));
      if (i >= 0) {
        const rest = lines.slice(i + 1).filter(Boolean);
        const j = rest.findIndex((l) => l.includes(' · '));
        if (j >= 0) { // "Title · Artist1 · Artist2": the label's own metadata beats the video title / channel name
          const parts = rest[j].split(' · ').map((p) => clean(p, 300)).filter(Boolean);
          if (parts.length >= 2) { out.track = parts[0]; out.artist = clean(parts[1], 200); }
        }
        if (j >= 0 && j + 1 < rest.length && !rest[j + 1].includes(' · ') && !/^℗|^Released on|^Auto-generated/i.test(rest[j + 1])) out.album = clean(rest[j + 1], 200);
      }
      return out.artist ? out : null;
    } catch (e) { return null; }
  }

  function musicMeta(videoId, m) {
    const empty = { track: '', artist: '', album: '', metaSource: '' };
    if (!m) return empty; // main-world data is for another video (or not ready): send nothing rather than stale values
    const tries = [['ytm', () => fromYtm(m)], ['musicSection', () => fromMusicSection(videoId, m)], ['topic', () => fromTopic(m)], ['title', () => fromTitle(m.title || '', m.author)]];
    for (const [src, f] of tries) {
      let r = null;
      try { r = f(); } catch (e) { /* defensive */ }
      if (r && (r.track || r.artist)) return { track: clean(r.track, 300), artist: clean(r.artist, 200), album: clean(r.album, 200), metaSource: src };
    }
    return empty;
  }

  // Independent uploads are often filed under People & Blogs / Entertainment, so "category != Music" alone must not skip
  // a song. Clearly non-music categories always skip; the ambiguous ones count as music up to 12 minutes.
  const NON_MUSIC = /^(Gaming|News & Politics|Education|Howto & Style|Science & Technology|Sports|Autos & Vehicles|Pets & Animals|Travel & Events|Nonprofits & Activism)$/;
  const musicLike = (cat, sec, hasList) => !!(!cat || cat === 'Music' || (!NON_MUSIC.test(cat) && ((sec > 0 && sec <= 720) || hasList))); // hasList: mix with a timestamped track list

  const EXT_VERSION = (() => { try { return api.runtime.getManifest().version; } catch (e) { return '?'; } })();
  let staleVid = '', staleSince = 0;
  const STALE_WAIT_MS = 4000;

  function snapshot() {
    const v = getVideo();
    if (!v || location.pathname.startsWith('/shorts/')) return null;
    const videoId = new URLSearchParams(location.search).get('v') || (main && main.videoId);
    if (!videoId || !/^[A-Za-z0-9_-]{11}$/.test(videoId)) return null;
    const sameVideo = !!main && main.videoId === videoId;
    // Right after a navigation the url already names the new video while the player data (and document.title) still
    // describe the previous one: reporting now would pair the new video id with the OLD title. Wait for the player data
    // (it follows within ~1 s); if it never catches up (4 s), report with what we have, as before.
    if (main && main.videoId && !sameVideo) {
      if (staleVid !== videoId) { staleVid = videoId; staleSince = Date.now(); }
      if (Date.now() - staleSince < STALE_WAIT_MS) return null;
    } else { staleVid = ''; }
    if (sameVideo && main.isLive) return null;
    let dur = v.duration;
    if (dur === Infinity) return null; // live stream
    if (!Number.isFinite(dur) || dur <= 0) dur = sameVideo ? main.lengthSeconds : 0;
    if (!(dur > 0)) return null; // metadata not loaded yet
    const category = sameVideo ? main.category : null;
    const meta = musicMeta(videoId, sameVideo ? main : null);
    return {
      videoId,
      title: ((sameVideo && main.title) || document.title.replace(/ - YouTube( Music)?$/, '')).slice(0, 300),
      channel: ((sameVideo && main.author) || '').slice(0, 200),
      ...meta,
      durationMs: Math.round(dur * 1000),
      positionMs: Math.max(0, Math.round(v.currentTime * 1000)),
      paused: v.paused,
      isAd: !!document.querySelector('.html5-video-player.ad-showing'),
      isMusic: IS_MUSIC_HOST || musicLike(category, dur, sameVideo && (String(main.desc || '').match(/\d{1,2}:\d{2}/g) || []).length >= 3),
      sentAtMs: Date.now(),
      // diagnostics only (logged by the app): extension version, url video id, player-data video id, same video?
      dbg: `v${EXT_VERSION} u=${new URLSearchParams(location.search).get('v') || '-'} m=${(main && main.videoId) || '-'} sv=${sameVideo ? 1 : 0}`,
    };
  }

  async function send(force = false) {
    if (!api.runtime?.id) return;
    if (inflight) { if (force) again = true; return; } // seek arrives while the reply is still in flight: resend right after
    const v = getVideo();
    if (!v) return;
    const payload = snapshot();
    if (!payload) return;
    inflight = true; lastSendAt = Date.now();
    try { await api.runtime.sendMessage({ type: 'nowPlaying', payload }); }
    catch (e) { /* extension reloaded or worker restarting: next tick retries */ }
    finally { inflight = false; if (again) { again = false; send(true); } }
  }

  const click = (...sels) => {
    for (const s of sels) { const el = document.querySelector(s); if (el) { el.click(); return; } }
    console.debug('[lotg] control not found:', sels.join(' | '));
  };

  // youtube.com shuffle / no queue history: the prev button is hidden or does nothing, but the browser
  // history holds the previously played video (what Alt+Left does). Fall back to that when the click changed nothing.
  function prev() {
    if (IS_MUSIC_HOST) { click('ytmusic-player-bar .previous-button', '.previous-button'); return; }
    const btn = document.querySelector('.ytp-prev-button');
    const before = location.href;
    const v = getVideo(), t0 = v ? v.currentTime : 0;
    let navigated = false;
    const onNav = () => { navigated = true; };
    window.addEventListener('yt-navigate-start', onNav, true);
    if (btn && btn.offsetParent !== null && btn.getAttribute('aria-disabled') !== 'true' && !btn.disabled) btn.click();
    setTimeout(() => {
      window.removeEventListener('yt-navigate-start', onNav, true);
      const restarted = !!v && v.currentTime + 1.5 < t0; // the button restarted this song instead of leaving it
      if (!navigated && !restarted && location.href === before && history.length > 1) history.back();
    }, 1200);
  }

  function run(cmd) {
    const v = getVideo();
    if (cmd === 'toggle') { if (v) (v.paused ? v.play() : v.pause()); }
    else if (cmd === 'next') click('.ytp-next-button', 'ytmusic-player-bar .next-button', '.next-button');
    else if (cmd === 'prev') prev();
    else if (typeof cmd === 'string') {
      const m = /^seek:(\d{1,8})$/.exec(cmd);
      if (!m || !v || document.querySelector('.html5-video-player.ad-showing')) return;
      const t = Number(m[1]) / 1000;
      if (Number.isFinite(t) && Number.isFinite(v.duration) && t >= 0 && t <= v.duration + 1) {
        window.postMessage({ src: 'lotg-cmd', type: 'seek', sec: t }, location.origin); // main world seekTo, falls back to currentTime
        setTimeout(() => { if (Math.abs(v.currentTime - t) > 2) v.currentTime = t; send(true); }, 150); // player API absent/ignored
      }
    }
  }

  api.runtime.onMessage.addListener((msg) => {
    if (msg && msg.type === 'commands' && Array.isArray(msg.commands)) {
      fastUntil = Date.now() + 5000; // lyric clicks come in bursts: poll fast for a few seconds
      for (const c of msg.commands) { try { run(c); } catch (e) { console.debug('[lotg] command failed', c); } }
    }
  });

  const onEvt = () => { lastEventAt = Date.now(); send(); };
  const hooked = new WeakSet();
  const hook = () => {
    const v = getVideo();
    if (!v || hooked.has(v)) return;
    hooked.add(v);
    for (const n of ['play', 'pause', 'seeked', 'ratechange', 'loadedmetadata']) v.addEventListener(n, onEvt);
  };

  // Commands ride on POST replies, so polling is the command latency: 0.5 s after a command,
  // 1 s while playing, 2 s while paused/idle (previously paused >15 s sent nothing, so commands never arrived).
  setInterval(() => {
    hook();
    sendCaptions();
    const now = Date.now(), v = getVideo();
    const idle = v && v.paused && now - lastEventAt > RECENT_MS;
    if (now - lastSendAt >= (now < fastUntil ? 450 : idle ? 2000 : 1000) - 50) send();
  }, 500);
})();
