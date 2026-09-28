const token = location.hash.slice(1) || sessionStorage.getItem('kakaoRelayToken') || ''; if (token) sessionStorage.setItem('kakaoRelayToken', token); history.replaceState(null, '', '/');
const $ = s => document.querySelector(s); let settings, controller;
function status(text) { $('#status').textContent = text; }
async function api(path, options = {}) {
  const response = await fetch('/api/' + path, { ...options, headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', ...options.headers } });
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `요청 실패 (${response.status})`); }
  return response.status === 204 || response.headers.get('content-length') === '0' ? null : response.json();
}
function collect() {
  document.querySelectorAll('[data-persona]').forEach(el => settings.persona[el.dataset.persona] = el.value);
  settings.provider = $('#provider').value; settings.contextMessages = Number($('#limit').value); settings.timeoutSeconds = Number($('#timeout').value);
  document.querySelectorAll('[data-provider]').forEach(card => { const p = settings.providers.find(p => p.id === card.dataset.provider); card.querySelectorAll('[data-field]').forEach(el => p[el.dataset.field] = el.type === 'checkbox' ? el.checked : el.value); });
}
async function save() { collect(); await api('settings', { method: 'PUT', body: JSON.stringify(settings) }); status('설정을 저장했습니다.'); }
async function detect() { const providers = await api('providers'); $('#cliStatus').textContent = providers.map(p => `${p.provider}: ${p.detail}`).join('\n'); }
function busy(value) { document.querySelectorAll('button:not(#cancel),input,select,[data-persona],#question').forEach(el => el.disabled = value); $('#cancel').disabled = !value; }
async function showMessages() {
  if (!$('#rooms').value) return;
  const context = await api('messages?roomId=' + encodeURIComponent($('#rooms').value) + '&limit=' + settings.contextMessages);
  $('#transcript').textContent = context.messages.map(m => `${new Date(m.time).toLocaleString('ko-KR', { timeZone: 'Asia/Seoul' })} · ${m.author}\n${m.deleted ? '[삭제된 메시지]' : m.text}`).join('\n\n');
}
document.querySelectorAll('[data-tab]').forEach(button => button.onclick = () => { document.querySelectorAll('.tab').forEach(t => t.classList.toggle('hidden', t.id !== button.dataset.tab)); document.querySelectorAll('[data-tab]').forEach(b => b.classList.toggle('active', b === button)); });
document.querySelectorAll('.save').forEach(button => button.onclick = () => save().catch(e => status(e.message)));
$('#detect').onclick = () => detect().catch(e => status(e.message));
$('#rooms').onchange = () => showMessages().catch(e => status(e.message));
$('#file').onchange = async event => {
  const file = event.target.files[0]; if (!file) return;
  busy(true);
  try { if (file.size > 10_000_000) throw new Error('10MB 이하 파일을 선택하세요.'); const room = await api('import', { method: 'POST', body: JSON.stringify({ name: file.name, content: await file.text() }) }); const rooms = await api('rooms'); $('#rooms').replaceChildren(...rooms.map(r => { const o = new Option(r.title, r.id); return o; })); $('#rooms').value = room.id; await showMessages(); status('대화를 가져왔습니다. AI 생성 전에는 외부 서비스에 전달하지 않습니다.'); } catch(e) { status(e.message); } finally { busy(false); }
};
document.querySelectorAll('[data-mode]').forEach(button => button.onclick = async () => {
  if (!$('#rooms').value) { status('대화를 먼저 가져오세요.'); return; }
  controller = new AbortController(); busy(true);
  try { await save(); status('AI 응답 생성 중…'); const result = await api('generate', { method: 'POST', signal: controller.signal, body: JSON.stringify({ profile: 'import', roomId: $('#rooms').value, mode: button.dataset.mode, instruction: $('#question').value }) }); $('#answer').value = result.text; status(`${result.provider} · ${result.model} · effort ${result.effort} · ${result.messageCount}개 문맥 · ` + result.attempts.map(a => `${a.provider} ${a.status}`).join(' → ')); } catch(e) { status(e.name === 'AbortError' ? '취소했습니다.' : e.message); } finally { controller = null; busy(false); }
});
$('#cancel').onclick = () => controller?.abort();
$('#copy').onclick = () => navigator.clipboard.writeText($('#answer').value).then(() => status('응답을 복사했습니다.'), () => status('복사할 텍스트를 직접 선택해주세요.'));
(async () => {
  settings = await api('settings'); document.querySelectorAll('[data-persona]').forEach(el => el.value = settings.persona[el.dataset.persona]); $('#provider').value = settings.provider; $('#limit').value = settings.contextMessages; $('#timeout').value = settings.timeoutSeconds;
  const modelChoices = await api('models');
  for (const provider of settings.providers) {
    const card = document.createElement('div'); card.className = 'provider-card'; card.dataset.provider = provider.id;
    const heading = document.createElement('h3'); heading.textContent = provider.id.toUpperCase(); card.append(heading);
    for (const [field, caption] of [['enabled', '사용'], ['executable', '실행 파일 경로 · 비우면 자동 검색'], ['model', '모델 · 비우면 CLI 기본값'], ['effort', 'effort · default / low / medium / high …']]) { const label = document.createElement('label'); label.textContent = caption; const input = document.createElement('input'); input.dataset.field = field; input.type = field === 'enabled' ? 'checkbox' : 'text'; if (field === 'enabled') input.checked = provider[field]; else input.value = provider[field]; label.append(input); card.append(label); } $('#providerForms').append(card);
  }
  document.querySelectorAll('[data-field="model"]').forEach(input => {
    const id = input.closest('[data-provider]').dataset.provider;
    const choices = document.createElement('datalist'); choices.id = 'models-' + id;
    choices.replaceChildren(...modelChoices[id].filter(Boolean).map(m => new Option(m, m)));
    input.setAttribute('list', choices.id); input.after(choices);
  });
  await detect(); status('준비됨 · 대화 파일을 가져오세요.');
})().catch(e => status(e.message));
