import { api } from './api.js';

const $ = (id) => document.getElementById(id);
const state = { status: null, macros: [], editing: null, selectedId: null };

// ---------- 유틸 ----------
const hex = (n) => (n ?? 0).toString(16).toUpperCase().padStart(2, '0');

function vkName(vk) {
  if (vk >= 0x70 && vk <= 0x7B) return 'F' + (vk - 0x6F);
  if (vk >= 0x30 && vk <= 0x39) return String.fromCharCode(vk);
  if (vk >= 0x41 && vk <= 0x5A) return String.fromCharCode(vk);
  if (vk === 0x20) return 'Space';
  if (vk === 0x0D) return 'Enter';
  if (vk === 0x1B) return 'Esc';
  return 'VK_0x' + hex(vk);
}

function hotkeyToString(t) {
  if (!t || !t.virtualKey) return '(없음)';
  const p = [];
  if (t.ctrl) p.push('Ctrl');
  if (t.alt) p.push('Alt');
  if (t.shift) p.push('Shift');
  if (t.win) p.push('Win');
  p.push(vkName(t.virtualKey));
  return p.join('+');
}

function eventToVk(e) {
  const k = e.key;
  if (/^F([1-9]|1[0-2])$/.test(k)) return 0x6F + parseInt(k.slice(1), 10);
  if (/^[a-zA-Z]$/.test(k)) return k.toUpperCase().charCodeAt(0);
  if (/^[0-9]$/.test(k)) return k.charCodeAt(0);
  if (k === ' ') return 0x20;
  if (k === 'Enter') return 0x0D;
  if (k === 'Escape') return 0x1B;
  return null;
}

function summarize(ev) {
  switch (ev['$type']) {
    case 'keyboard': return `Key sc=${hex(ev.code)} ${(ev.state & 1) ? 'up' : 'down'}`;
    case 'mouse': return `Mouse st=${hex(ev.buttonState)} (${ev.x},${ev.y}) roll=${ev.rolling}`;
    case 'gamepad': return `Pad ${ev.control}=${ev.value}`;
    case 'text': return `Type "${ev.text}"`;
    default: return ev['$type'] || '?';
  }
}

// ---------- 로그 ----------
function log(level, message, time) {
  const el = $('log');
  const line = document.createElement('div');
  line.className = 'line';
  line.innerHTML = `<span class="time">${time || new Date().toLocaleTimeString()}</span><span class="${level}">${message}</span>`;
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
  while (el.childElementCount > 400) el.removeChild(el.firstChild);
}

// ---------- 상태 렌더 ----------
function renderStatus(s) {
  state.status = s;
  const mark = (v) => v ? '<span class="ok">●</span>' : '<span class="no">●</span>';
  const b = s.backend, d = s.driver;

  $('d-interception').innerHTML = `${mark(d.interception)} ${d.interception ? '설치됨' : '미설치'}${b.interceptionAvailable ? ' · 사용가능' : ''}`;
  $('d-vigem').innerHTML = `${mark(d.vigem)} ${d.vigem ? '설치됨' : '미설치'}${b.gamepadConnected ? ' · 연결됨' : ''}`;
  $('d-admin').innerHTML = `${mark(d.admin)} ${d.admin ? '예' : '아니오'}`;

  let hint = '';
  if (!b.interceptionAvailable) hint = 'Interception이 준비되지 않았습니다. 드라이버 설치 후 재부팅하세요.';
  else if (!b.keyboardReady || !b.mouseReady) {
    const need = [];
    if (!b.keyboardReady) need.push('키보드를 한 번 누르기');
    if (!b.mouseReady) need.push('마우스를 한 번 움직이기');
    hint = `송출 활성화를 위해 ${need.join(' / ')} 해주세요(디바이스 인식).`;
  }
  $('device-hint').textContent = hint;

  const badge = $('state-badge');
  badge.className = 'badge ' + s.state;
  badge.textContent = { idle: '대기', recording: '녹화 중', playing: '재생 중' }[s.state] || s.state;

  // 버튼 상태
  const recBtn = $('btn-record');
  recBtn.classList.toggle('active', s.state === 'recording');
  recBtn.textContent = s.state === 'recording' ? '■ 정지' : '● 녹화';
  recBtn.disabled = s.state === 'playing';

  $('btn-pad-connect').disabled = !b.gamepadAvailable || b.gamepadConnected;
  $('btn-pad-disconnect').disabled = !b.gamepadConnected;
  $('btn-pad-test').disabled = !b.gamepadConnected;

  state.selectedId = s.currentMacroId || state.selectedId;
  renderMacroButtons();
  $('foot-url').textContent = s.url || '';
}

function renderMacroButtons() {
  const playing = state.status?.state === 'playing';
  document.querySelectorAll('.macro-item').forEach((el) => {
    const id = el.dataset.id;
    const playBtn = el.querySelector('.act-play');
    const isCurrent = playing && state.status?.currentMacroId === id;
    el.classList.toggle('active', isCurrent);
    if (playBtn) playBtn.textContent = isCurrent ? '■ 정지' : '▶ 재생';
    if (playBtn) playBtn.disabled = state.status?.state === 'recording';
  });
}

// ---------- 매크로 목록 ----------
async function loadMacros() {
  state.macros = await api.listMacros();
  const list = $('macro-list');
  list.innerHTML = '';
  $('macro-empty').hidden = state.macros.length > 0;

  for (const m of state.macros) {
    const li = document.createElement('li');
    li.className = 'macro-item';
    li.dataset.id = m.id;
    li.innerHTML = `
      <div class="macro-meta">
        <span class="name">${escapeHtml(m.name)}</span>
        <span class="sub">${m.stepCount} 스텝 · 반복 ${m.loopCount === 0 ? '∞' : m.loopCount} · ${m.speedMultiplier}x · 🔑 ${escapeHtml(m.trigger || '없음')}</span>
      </div>
      <div class="macro-actions">
        <button class="btn sm act-play">▶ 재생</button>
        <button class="btn ghost sm act-edit">편집</button>
        <button class="btn ghost sm act-del">🗑</button>
      </div>`;
    li.querySelector('.act-play').onclick = () => togglePlay(m.id);
    li.querySelector('.act-edit').onclick = () => openEditor(m.id);
    li.querySelector('.act-del').onclick = () => removeMacro(m.id, m.name);
    list.appendChild(li);
  }
  renderMacroButtons();
}

async function togglePlay(id) {
  try {
    if (state.status?.state === 'playing' && state.status?.currentMacroId === id) {
      await api.stop();
    } else {
      await api.play(id);
    }
  } catch (e) { log('error', e.message); }
}

async function removeMacro(id, name) {
  if (!confirm(`'${name}' 매크로를 휴지통으로 보낼까요?`)) return;
  try { await api.deleteMacro(id); await loadMacros(); }
  catch (e) { log('error', e.message); }
}

// ---------- 녹화 ----------
async function toggleRecord() {
  try {
    if (state.status?.state === 'recording') {
      const name = prompt('매크로 이름:', `매크로 ${new Date().toLocaleTimeString()}`);
      await api.recordStop(name || '');
      await loadMacros();
    } else {
      await api.recordStart();
    }
  } catch (e) { log('error', e.message); }
}

// ---------- 에디터 ----------
async function openEditor(id) {
  try {
    const macro = id ? await api.getMacro(id)
      : { id: '', name: '새 매크로', loopCount: 1, speedMultiplier: 1.0, trigger: null, steps: [] };
    state.editing = macro;
    $('editor-card').hidden = false;
    $('ed-name').value = macro.name;
    $('ed-loop').value = macro.loopCount;
    $('ed-speed').value = macro.speedMultiplier;
    $('ed-hotkey').value = hotkeyToString(macro.trigger);
    renderSteps();
  } catch (e) { log('error', e.message); }
}

function renderSteps() {
  const m = state.editing;
  $('ed-step-count').textContent = m.steps.length;
  const ol = $('ed-steps');
  ol.innerHTML = '';
  m.steps.forEach((step, i) => {
    const li = document.createElement('li');
    li.className = 'step';
    li.innerHTML = `
      <span class="idx">${i + 1}</span>
      <span class="summary">${escapeHtml(summarize(step.event))}</span>
      <input class="delay" type="number" min="0" step="1" value="${Math.round(step.delayBeforeMs)}" title="직전 지연(ms)" />
      <button class="rm" title="삭제">✕</button>`;
    li.querySelector('.delay').onchange = (e) => { step.delayBeforeMs = parseFloat(e.target.value) || 0; };
    li.querySelector('.rm').onclick = () => { m.steps.splice(i, 1); renderSteps(); };
    ol.appendChild(li);
  });
}

function collectEditor() {
  const m = state.editing;
  m.name = $('ed-name').value.trim() || '제목 없음';
  m.loopCount = parseInt($('ed-loop').value, 10) || 0;
  m.speedMultiplier = parseFloat($('ed-speed').value) || 1.0;
  return m;
}

async function saveEditor() {
  try {
    const m = collectEditor();
    const saved = m.id ? await api.updateMacro(m.id, m) : await api.createMacro(m);
    state.editing = saved;
    $('editor-card').hidden = true;
    await loadMacros();
    log('info', `저장됨: ${saved.name}`);
  } catch (e) { log('error', e.message); }
}

function addTextStep() {
  const text = prompt('입력할 텍스트:');
  if (text == null) return;
  // $type을 첫 속성으로 유지(System.Text.Json 판별자 요구사항)
  state.editing.steps.push({ delayBeforeMs: 0, event: { '$type': 'text', text, perKeyDelayMs: 0 } });
  renderSteps();
}

let capturingHotkey = false;
function setupHotkeyCapture() {
  const input = $('ed-hotkey');
  input.addEventListener('focus', () => { capturingHotkey = true; input.value = '키 입력 대기…'; });
  input.addEventListener('blur', () => { capturingHotkey = false; input.value = hotkeyToString(state.editing?.trigger); });
  input.addEventListener('keydown', (e) => {
    if (!capturingHotkey) return;
    e.preventDefault();
    const vk = eventToVk(e);
    if (vk == null) return; // 수정자만 누른 경우 무시
    state.editing.trigger = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: vk };
    input.value = hotkeyToString(state.editing.trigger);
    input.blur();
  });
  $('ed-hotkey-clear').onclick = () => {
    if (state.editing) state.editing.trigger = null;
    input.value = '(없음)';
  };
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ---------- WebSocket ----------
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onopen = () => $('ws-dot').className = 'ws-dot on';
  ws.onclose = () => { $('ws-dot').className = 'ws-dot off'; setTimeout(connectWs, 1500); };
  ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    switch (msg.type) {
      case 'status': renderStatus(msg.data); break;
      case 'log': log(msg.data.level, escapeHtml(msg.data.message), msg.data.time); break;
      case 'recordedStep':
        log('step', `+ ${escapeHtml(msg.data.summary)} (${Math.round(msg.data.delayBeforeMs)}ms)`);
        break;
      case 'progress': showProgress(msg.data); break;
    }
  };
}

function showProgress(p) {
  const box = $('progress');
  box.hidden = false;
  const pct = p.stepCount ? Math.round(((p.stepIndex + 1) / p.stepCount) * 100) : 0;
  $('progress-bar').style.width = pct + '%';
  $('progress-text').textContent = `루프 ${p.loop + 1} · 스텝 ${p.stepIndex + 1}/${p.stepCount}`;
  clearTimeout(showProgress._t);
  showProgress._t = setTimeout(() => { box.hidden = true; }, 1500);
}

// ---------- 초기화 ----------
function wire() {
  $('btn-record').onclick = toggleRecord;
  $('btn-new').onclick = () => openEditor('');
  $('btn-save').onclick = saveEditor;
  $('btn-cancel').onclick = () => { $('editor-card').hidden = true; };
  $('btn-add-text').onclick = addTextStep;
  $('btn-clear-log').onclick = () => { $('log').innerHTML = ''; };

  $('btn-install').onclick = async () => {
    log('info', '드라이버 설치/점검 중… (수 분 걸릴 수 있습니다)');
    try { const r = await api.installDrivers(); if (r.rebootRequired) log('warn', '재부팅이 필요합니다.'); }
    catch (e) { log('error', e.message); }
  };
  $('btn-pad-connect').onclick = async () => { try { await api.gamepadConnect(); } catch (e) { log('error', e.message); } };
  $('btn-pad-disconnect').onclick = async () => { try { await api.gamepadDisconnect(); } catch (e) { log('error', e.message); } };
  $('btn-pad-test').onclick = async () => {
    try {
      await api.gamepadSend('A', 1);
      setTimeout(() => api.gamepadSend('A', 0).catch(() => {}), 200);
      log('info', '게임패드 A 버튼 테스트 전송.');
    } catch (e) { log('error', e.message); }
  };

  setupHotkeyCapture();
}

async function init() {
  wire();
  connectWs();
  try { renderStatus(await api.status()); } catch (e) { log('error', e.message); }
  await loadMacros();
  log('info', 'Y_Input UI 준비됨.');
}

init();
