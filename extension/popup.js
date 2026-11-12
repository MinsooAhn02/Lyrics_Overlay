const api = globalThis.browser ?? globalThis.chrome;
const $ = (id) => document.getElementById(id);
const fmt = (t) => new Date(t).toLocaleTimeString('ko-KR');

async function render() {
  const { token } = await api.storage.local.get('token');
  const { status } = await api.storage.session.get('status');
  const el = $('status');
  $('token').placeholder = token ? `저장됨 (••••${token.slice(-4)})` : '토큰 붙여넣기';
  if (!token) { el.className = 'status warn'; el.textContent = '토큰이 없습니다. 아래에 입력하세요.'; }
  else if (!status) { el.className = 'status'; el.textContent = '아직 전송 기록이 없습니다.'; }
  else if (status.ok) { el.className = 'status ok'; el.textContent = `연결됨 - 마지막 성공 ${fmt(status.at)}`; }
  else { el.className = 'status bad'; el.textContent = `연결 안 됨 - ${status.error} (${fmt(status.at)})`; }
  $('dl').classList.toggle('attn', !!(status && !status.ok)); // not connected: make the app download link stand out
}

$('save').addEventListener('click', async () => {
  const v = $('token').value.trim();
  if (!/^[\x21-\x7e]{8,128}$/.test(v)) { $('msg').textContent = '토큰 형식이 올바르지 않습니다.'; return; }
  await api.storage.local.set({ token: v });
  $('token').value = '';
  $('msg').textContent = '저장했습니다.';
  render();
});

$('test').addEventListener('click', async () => {
  $('msg').textContent = '테스트 중...';
  try {
    const r = await api.runtime.sendMessage({ type: 'test' });
    $('msg').textContent = r && r.ok ? '연결 성공' : `실패: ${(r && r.error) || '응답 없음'}`;
  } catch (e) { $('msg').textContent = '실패: 확장 내부 오류'; }
  render();
});

const HOST = 'com.lyricsoverlay.host';

$('launch').addEventListener('click', async () => {
  $('msg').textContent = '앱을 켜는 중...';
  try {
    const r = await api.runtime.sendNativeMessage(HOST, { cmd: 'start' });
    $('msg').textContent = r && r.ok ? (r.started ? '앱을 켰습니다.' : '이미 실행 중입니다.') : `실패: ${(r && r.error) || '응답 없음'}`;
  } catch (e) {
    $('msg').textContent = '앱을 켤 수 없습니다. 앱(LyricsOverlay.exe)을 한 번 직접 실행하면 등록됩니다.';
  }
});

$('quit').addEventListener('click', async () => {
  $('msg').textContent = '앱을 끄는 중...';
  try {
    const r = await api.runtime.sendMessage({ type: 'quit' });
    $('msg').textContent = r && r.ok ? '앱을 껐습니다.' : `실패: ${(r && r.error) || '앱이 이미 꺼져 있나요?'}`;
  } catch (e) { $('msg').textContent = '실패: 확장 내부 오류'; }
});

api.storage.onChanged.addListener(render);
render();
