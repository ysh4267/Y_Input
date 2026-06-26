import { api } from './api.js';
import { createEditor } from './editor.js';
import { createRecorder } from './recorder.js';
import { confirmDialog, installLockdown } from './ui.js';

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const state = { status: null, macros: [] };

// ---------- 로그 ----------
function log(level, message, time) {
  const el = $('log');
  const line = document.createElement('div');
  line.className = 'line';
  line.innerHTML = `<span class="time">${time || new Date().toLocaleTimeString()}</span><span class="${level}">${esc(message)}</span>`;
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
  while (el.childElementCount > 400) el.removeChild(el.firstChild);
}

// ---------- 편집기 / 녹화기 ----------
const editor = createEditor({ log, onSaved: loadMacros, getStatus: () => state.status });
const recorder = createRecorder({ log, onRecorded: (m) => editor.open(m), getStatus: () => state.status });

// ---------- 상태 ----------
function renderStatus(s) {
  state.status = s;
  const mark = (v) => v ? '<span class="ok">●</span>' : '<span class="no">●</span>';
  const b = s.backend, d = s.driver;
  $('d-interception').innerHTML = `${mark(d.interception)} ${d.interception ? '설치' : '미설치'}${b.interceptionAvailable ? '·가능' : ''}`;
  $('d-vigem').innerHTML = `${mark(d.vigem)} ${d.vigem ? '설치' : '미설치'}${b.gamepadConnected ? '·연결' : ''}`;
  $('d-admin').innerHTML = `${mark(d.admin)} ${d.admin ? '예' : '아니오'}`;

  let hint = '';
  if (!b.interceptionAvailable) hint = 'Interception 미준비 — 설치 후 재부팅하세요.';
  else if (!b.keyboardReady || !b.mouseReady) {
    const need = [];
    if (!b.keyboardReady) need.push('키 한 번');
    if (!b.mouseReady) need.push('마우스 한 번');
    hint = `송출 활성화: ${need.join(' / ')} 입력(디바이스 인식).`;
  }
  $('device-hint').textContent = hint;

  const badge = $('state-badge');
  badge.className = 'badge ' + s.state;
  badge.textContent = { idle: '대기', recording: '녹화 중', playing: '재생 중' }[s.state] || s.state;

  $('btn-pad-connect').disabled = !b.gamepadAvailable || b.gamepadConnected;
  $('btn-pad-disconnect').disabled = !b.gamepadConnected;
  $('btn-pad-test').disabled = !b.gamepadConnected;
  $('btn-record').classList.toggle('active', s.state === 'recording');
  $('btn-record').textContent = s.state === 'recording' ? '■ 정지' : '● 녹화';
  $('btn-monitor').classList.toggle('active', !!s.monitoring);
  $('btn-monitor').textContent = s.monitoring ? '■ 모니터 끄기' : '입력 모니터';

  $('foot-url').textContent = s.url || '';
  editor.onStatus(s);
  recorder.onStatus(s);
  renderMacroActive();
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
        <span class="name">${esc(m.name)}</span>
        <span class="sub">${m.stepCount}스텝 · ${m.loopCount === 0 ? '∞' : m.loopCount}회 · ${m.speedMultiplier}x · 🔑${esc(m.trigger || '없음')}</span>
      </div>
      <div class="macro-actions">
        <button class="btn sm act-play">▶</button>
        <button class="btn ghost sm act-del">🗑</button>
      </div>`;
    li.onclick = (e) => { if (!e.target.closest('button')) openMacro(m.id); };
    li.querySelector('.act-play').onclick = () => togglePlay(m.id);
    li.querySelector('.act-del').onclick = () => removeMacro(m.id, m.name);
    list.appendChild(li);
  }
  renderMacroActive();
}

function renderMacroActive() {
  const st = state.status;
  document.querySelectorAll('.macro-item').forEach((el) => {
    const playing = st && st.state === 'playing' && st.currentMacroId === el.dataset.id;
    el.classList.toggle('active', playing);
    const pb = el.querySelector('.act-play');
    if (pb) pb.textContent = playing ? '■' : '▶';
  });
}

async function openMacro(id) {
  try { editor.open(await api.getMacro(id)); }
  catch (e) { log('error', e.message); }
}
async function togglePlay(id) {
  try {
    const st = state.status;
    if (st && st.state === 'playing' && st.currentMacroId === id) await api.stop();
    else await api.play(id);
  } catch (e) { log('error', e.message); }
}
async function removeMacro(id, name) {
  const ok = await confirmDialog(`'${name}' 매크로를 휴지통으로 보낼까요?`, { title: '매크로 삭제', ok: '휴지통으로', cancel: '취소' });
  if (!ok) return;
  try { await api.deleteMacro(id); await loadMacros(); if (editor.current()?.id === id) editor.close(); }
  catch (e) { log('error', e.message); }
}

// ---------- WebSocket ----------
let shuttingDown = false;
function connectWs() {
  if (shuttingDown) return;
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onopen = () => $('ws-dot').className = 'ws-dot on';
  ws.onclose = () => { $('ws-dot').className = 'ws-dot off'; if (!shuttingDown) setTimeout(connectWs, 1500); };
  ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    switch (msg.type) {
      case 'status': renderStatus(msg.data); break;
      case 'log': log(msg.data.level, msg.data.message, msg.data.time); break;
      case 'recordedStep': log('step', `+ ${msg.data.summary} (${Math.round(msg.data.delayBeforeMs)}ms)`); break;
      case 'progress': showProgress(msg.data); break;
      case 'inputDetected': editor.onInputDetected(msg.data); break;
      case 'inputMonitor': log('monitor', `[${msg.data.source}] ${msg.data.label}`, msg.data.time); break;
      case 'shutdown': handleShutdown(); break;
    }
  };
}

// 트레이 종료 신호 → 페이지 닫기(앱 모드면 OK, 일반 탭이면 안내 화면)
function handleShutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  document.title = 'Y_Input — 종료됨';
  try { window.close(); } catch (e) { /* 일반 탭은 막힐 수 있음 */ }
  setTimeout(() => {
    const o = document.createElement('div');
    o.className = 'closed-screen';
    o.innerHTML = '<div><h1>Y_Input 종료됨</h1><p>프로그램이 종료되었습니다. 이 창을 닫아 주세요.</p></div>';
    document.body.appendChild(o);
  }, 150);
}
function showProgress(p) {
  const box = $('progress'); box.hidden = false;
  const pct = p.stepCount ? Math.round(((p.stepIndex + 1) / p.stepCount) * 100) : 0;
  $('progress-bar').style.width = pct + '%';
  $('progress-text').textContent = `루프 ${p.loop + 1} · 스텝 ${p.stepIndex + 1}/${p.stepCount}`;
  clearTimeout(showProgress._t);
  showProgress._t = setTimeout(() => { box.hidden = true; }, 1500);
}

// ---------- 사이드바 와이어링 ----------
function wire() {
  $('btn-new').onclick = () => editor.open(null);
  $('btn-record').onclick = () => {
    if (state.status?.state === 'recording') { recorder.stop(); return; }
    editor.open(null);
    recorder.start();
  };
  $('btn-clear-log').onclick = () => { $('log').innerHTML = ''; };
  $('btn-monitor').onclick = async () => {
    try { if (state.status?.monitoring) await api.monitorOff(); else await api.monitorOn(); }
    catch (e) { log('error', e.message); }
  };
  $('btn-install').onclick = async () => {
    log('info', '드라이버 설치/점검 중… (수 분 걸릴 수 있습니다)');
    try { const r = await api.installDrivers(); if (r.rebootRequired) log('warn', '재부팅이 필요합니다.'); }
    catch (e) { log('error', e.message); }
  };
  $('btn-pad-connect').onclick = async () => { try { await api.gamepadConnect(); } catch (e) { log('error', e.message); } };
  $('btn-pad-disconnect').onclick = async () => { try { await api.gamepadDisconnect(); } catch (e) { log('error', e.message); } };
  $('btn-pad-test').onclick = async () => {
    try { await api.gamepadSend('A', 1); setTimeout(() => api.gamepadSend('A', 0).catch(() => {}), 200); log('info', '게임패드 A 테스트.'); }
    catch (e) { log('error', e.message); }
  };
}

async function init() {
  installLockdown(); // 텍스트 드래그·브라우저 단축키·우클릭 차단
  wire();
  connectWs();
  try { renderStatus(await api.status()); } catch (e) { log('error', e.message); }
  await loadMacros();
  editor.open(null); // 기본: 새 매크로 추가 모드로 시작
  log('info', 'Y_Input UI 준비됨.');
}

init();
