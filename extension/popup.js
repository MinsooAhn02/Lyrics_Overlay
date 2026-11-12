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

api.storage.onChanged.addListener(render);
render();
