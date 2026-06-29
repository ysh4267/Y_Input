import { api } from './api.js';
import { createEditor } from './editor.js';
import { confirmDialog, installLockdown } from './ui.js';
import * as km from './keymap.js';

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const state = { status: null, macros: [] };
let runShownId = null;    // 실행 페이지에 스텝 표시 중인 매크로 id
let lastStepIndex = -1;   // 재생 진행 중 현재 스텝(하이라이트용)

// ---------- 탭 ----------
function switchTab(name) {
  document.querySelectorAll('.tab-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.page').forEach((p) => p.classList.toggle('active', p.id === 'page-' + name));
}

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

// ---------- 편집기(녹화는 편집기 안 '녹화하기' 카드로 통합) ----------
const editor = createEditor({ log, onSaved: loadMacros, getStatus: () => state.status });

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
  $('btn-monitor').classList.toggle('active', !!s.monitoring);
  $('btn-monitor').textContent = s.monitoring ? '■ 모니터 끄기' : '입력 모니터';

  $('foot-url').textContent = s.url || '';
  editor.onStatus(s);
  if (s.state !== 'playing') { lastStepIndex = -1; highlightRunStep(-1); }
  renderMacroActive();
}

// ---------- 매크로 목록(실행 탭=재생+적용토글 / 편집 탭=복제) ----------
async function loadMacros() {
  state.macros = await api.listMacros();
  renderMacroList($('macro-list-run'), $('macro-empty-run'), 'run');
  renderMacroList($('macro-list-edit'), $('macro-empty-edit'), 'edit');
  if (runShownId && !state.macros.some((m) => m.id === runShownId)) clearRunSteps();
  renderMacroActive();
}

function renderMacroList(listEl, emptyEl, mode) {
  if (!listEl) return;
  listEl.innerHTML = '';
  if (emptyEl) emptyEl.hidden = state.macros.length > 0;
  for (const m of state.macros) {
    const li = document.createElement('li');
    li.className = 'macro-item';
    li.dataset.id = m.id;
    const sub = `${m.stepCount}스텝 · ${m.loopCount === 0 ? '∞' : m.loopCount}회 · ${m.speedMultiplier}x · 🔑${esc(m.trigger || '없음')}`;
    const actions = mode === 'run'
      ? `<label class="toggle" title="적용(트리거 활성)"><input type="checkbox" class="act-toggle" ${m.enabled ? 'checked' : ''}><span class="track"></span><span class="knob"></span></label>
         <button class="btn sm act-play" title="재생/정지">▶</button>
         <button class="btn ghost sm act-del" title="삭제">🗑</button>`
      : `<button class="btn ghost sm act-dup" title="복제">⎘</button>
         <button class="btn ghost sm act-del" title="삭제">🗑</button>`;
    li.innerHTML = `
      <div class="macro-top">
        <span class="name">${esc(m.name)}</span>
        <div class="macro-actions">${actions}</div>
      </div>
      <div class="macro-sub">${sub}</div>`;
    li.onclick = (e) => {
      if (e.target.closest('button, label, input')) return;
      if (mode === 'run') selectRunMacro(m.id); else openMacro(m.id);
    };
    li.querySelector('.act-del').onclick = () => removeMacro(m.id, m.name);
    if (mode === 'run') {
      li.querySelector('.act-play').onclick = () => togglePlay(m.id);
      const tg = li.querySelector('.act-toggle');
      tg.onchange = async () => {
        try { await api.setEnabled(m.id, tg.checked); }
        catch (e) { log('error', e.message); tg.checked = !tg.checked; }
      };
    } else {
      li.querySelector('.act-dup').onclick = () => duplicateMacro(m.id);
    }
    listEl.appendChild(li);
  }
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

// ---------- 실행 페이지: 현재 매크로 동작 순서(읽기전용) + 재생 하이라이트 ----------
async function selectRunMacro(id) {
  try { renderRunSteps(await api.getMacro(id)); }
  catch (e) { log('error', e.message); }
}
function clearRunSteps() {
  runShownId = null;
  if ($('run-steps')) $('run-steps').innerHTML = '';
  if ($('run-steps-empty')) $('run-steps-empty').hidden = false;
  if ($('run-steps-title')) $('run-steps-title').textContent = '현재 매크로';
}
function renderRunSteps(macro) {
  runShownId = macro.id;
  const wrap = $('run-steps'); if (!wrap) return;
  wrap.innerHTML = '';
  const steps = macro.steps || [];
  $('run-steps-empty').hidden = steps.length > 0;
  $('run-steps-title').textContent = macro.name || '현재 매크로';
  steps.forEach((s, i) => {
    const t = s.event['$type'];
    const row = document.createElement('div'); row.className = 'run-steps-row'; row.dataset.i = i;
    const num = document.createElement('span'); num.className = 'rs-num'; num.textContent = i + 1;
    const ico = document.createElement('span'); ico.className = 'rs-ico type-' + t; ico.textContent = km.TYPE_ICON[t] || '•';
    const label = document.createElement('span'); label.className = 'rs-label';
    label.textContent = t === 'delay' ? `지연 ${Math.round(s.delayBeforeMs || 0)} ms` : km.summarizeEvent(s.event);
    row.append(num, ico, label);
    wrap.appendChild(row);
  });
  const st = state.status;
  const playingThis = st && st.state === 'playing' && st.currentMacroId === macro.id;
  highlightRunStep(playingThis ? lastStepIndex : -1);
}
function highlightRunStep(idx) {
  const wrap = $('run-steps'); if (!wrap) return;
  wrap.querySelectorAll('.run-steps-row.playing').forEach((r) => r.classList.remove('playing'));
  if (idx < 0) return;
  const row = wrap.querySelector(`.run-steps-row[data-i="${idx}"]`);
  if (row) { row.classList.add('playing'); row.scrollIntoView({ block: 'nearest' }); }
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
async function duplicateMacro(id) {
  try {
    const m = await api.getMacro(id);
    await api.createMacro({ ...m, id: '', name: (m.name || '매크로') + ' 복사' });
    await loadMacros();
    log('info', `복제됨: ${m.name} 복사`);
  } catch (e) { log('error', e.message); }
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
      case 'recordedStep': editor.onRecordedStep(msg.data); break;
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
  // 실행 페이지: 현재 재생 중인 매크로 스텝을 자동 표시 + 현재 스텝 하이라이트
  lastStepIndex = p.stepIndex;
  const cur = state.status && state.status.currentMacroId;
  if (cur && runShownId !== cur) selectRunMacro(cur);  // 최초 1회 로드(이후 progress는 바로 하이라이트)
  else if (cur && runShownId === cur) highlightRunStep(p.stepIndex);
}

// ---------- 업데이트(Git 동기화) ----------
async function onUpdateCheck() {
  $('update-status').textContent = '확인 중…';
  $('btn-update-apply').disabled = true;
  try {
    const r = await api.updateCheck();
    if (!r.ok) { $('update-status').textContent = '확인 실패: ' + (r.message || ''); return; }
    if (r.behind > 0) { $('update-status').textContent = `${r.behind}개 업데이트 가능 (현재 ${r.current})`; $('btn-update-apply').disabled = false; }
    else $('update-status').textContent = `최신 상태 (현재 ${r.current})`;
  } catch (e) { $('update-status').textContent = '확인 실패: ' + e.message; }
}
async function onUpdateApply() {
  const ok = await confirmDialog('최신 코드로 업데이트하고 앱을 재시작할까요?\n(git pull → 빌드 → 재실행)', { title: '업데이트 적용', ok: '업데이트', cancel: '취소' });
  if (!ok) return;
  $('update-status').textContent = '업데이트 중… 빌드 후 자동 재시작됩니다.';
  $('btn-update-apply').disabled = true; $('btn-update-check').disabled = true;
  try { await api.updateApply(); } catch (e) { /* 곧 종료되어 응답이 안 올 수 있음 */ }
}

// ---------- 와이어링 ----------
function wire() {
  document.querySelectorAll('.tab-btn').forEach((b) => b.onclick = () => switchTab(b.dataset.tab));
  $('btn-update-check').onclick = onUpdateCheck;
  $('btn-update-apply').onclick = onUpdateApply;
  $('btn-new').onclick = () => { editor.open(null); switchTab('edit'); };
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
  switchTab('edit'); // 기본: 매크로 편집 탭
  connectWs();
  try { renderStatus(await api.status()); } catch (e) { log('error', e.message); }
  await loadMacros();
  editor.open(null); // 기본: 새 매크로 추가 모드로 시작
  log('info', 'Y_Input UI 준비됨.');
}

init();
