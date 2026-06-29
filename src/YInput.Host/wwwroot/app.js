import { api } from './api.js';
import { createEditor } from './editor.js';
import { confirmDialog, installLockdown } from './ui.js';
import * as km from './keymap.js';

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtMs = (ms) => ms >= 1000 ? (ms / 1000).toFixed(2) + ' s' : Math.round(ms) + ' ms'; // 편집기와 동일 표기

const state = { status: null, macros: [] };
let runShownId = null;    // 실행 페이지에 스텝 표시 중인 매크로 id
let lastStepIndex = -1;   // 재생 진행 중 현재 스텝(하이라이트용)
let capTrigId = null;     // 실행 목록에서 트리거 캡처 중인 매크로 id
let capCleanup = null;    // 트리거 캡처 정리 콜백

// 매크로 목록 아이콘(라인 SVG): 복제·삭제·편집·트리거
const ICON = {
  dup: '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><rect x="5.6" y="5.6" width="8.1" height="8.1" rx="1.6"/><path d="M10.4 5.4V3.1A1.6 1.6 0 0 0 8.8 1.5H3.1A1.6 1.6 0 0 0 1.5 3.1V8.8A1.6 1.6 0 0 0 3.1 10.4H5.4"/></svg>',
  del: '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.6 4.3H13.4"/><path d="M6.4 4.3V3.2A1.1 1.1 0 0 1 7.5 2.1H8.5A1.1 1.1 0 0 1 9.6 3.2V4.3"/><path d="M3.9 4.3 4.6 13A1.3 1.3 0 0 0 5.9 14.2H10.1A1.3 1.3 0 0 0 11.4 13L12.1 4.3"/><path d="M6.6 6.9V11.3M9.4 6.9V11.3"/></svg>',
  edit: '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M11.4 2.3 13.7 4.6"/><path d="M10.6 3.1 3.4 10.3 2.4 13.6 5.7 12.6 12.9 5.4Z"/></svg>',
  trigger: '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><rect x="1.5" y="4" width="13" height="8" rx="1.6"/><path d="M4 6.6h0M6.5 6.6h0M9 6.6h0M11.5 6.6h0M4.7 9.3h6.6"/></svg>',
};

// ---------- 탭 ----------
function switchTab(name) {
  document.querySelectorAll('.tab-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.page').forEach((p) => p.classList.toggle('active', p.id === 'page-' + name));
}

// ---------- 설정 드로어(드라이버·업데이트) ----------
function openSettings() {
  const ov = $('settings-overlay');
  ov.hidden = false;
  requestAnimationFrame(() => ov.classList.add('open'));
}
function closeSettings() {
  const ov = $('settings-overlay');
  ov.classList.remove('open');
  setTimeout(() => { ov.hidden = true; }, 200);
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
const editor = createEditor({ log, onSaved: loadMacros, getStatus: () => state.status, getMacros: () => state.macros });

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
  // 실행 탭: 활성(적용 ON) 매크로를 위로(그 안에서는 기존 이름순 유지 — JS sort는 안정 정렬)
  const ordered = mode === 'run'
    ? [...state.macros].sort((a, b) => (b.enabled ? 1 : 0) - (a.enabled ? 1 : 0))
    : state.macros;
  for (const m of ordered) {
    const li = document.createElement('li');
    li.className = 'macro-item' + (mode === 'run' ? ' run' : '');
    li.dataset.id = m.id;
    if (mode === 'run' && m.enabled) li.classList.add('enabled'); // 활성(적용 ON) 항목 전체 하이라이트
    if (mode === 'run') {
      // 트리거는 버튼 안, 반복/속도는 아랫줄 컨트롤 → 상세줄은 스텝 수만
      const repMode = m.loopCount <= 0 ? 'inf' : m.loopCount === 1 ? 'once' : 'count';
      const repCount = m.loopCount > 1 ? m.loopCount : 2;
      li.innerHTML = `
        <div class="mi-main">
          <label class="toggle" title="적용(트리거 활성)"><input type="checkbox" class="act-toggle" ${m.enabled ? 'checked' : ''}><span class="track"></span><span class="knob"></span></label>
          <div class="macro-meta"><span class="name">${esc(m.name)}</span><span class="macro-sub">${m.stepCount}스텝 · 총 ${fmtMs(m.durationMs || 0)}</span></div>
          <div class="macro-actions">
            <button class="mbtn act-edit" title="편집">${ICON.edit}</button>
            <button class="mbtn act-del" title="삭제">${ICON.del}</button>
          </div>
        </div>
        <div class="mi-play">
          <span class="pl-label">반복</span>
          <div class="seg pl-rep">
            <button class="seg-btn ${repMode === 'once' ? 'on' : ''}" data-val="once">한번</button>
            <button class="seg-btn ${repMode === 'count' ? 'on' : ''}" data-val="count">반복</button>
            <button class="seg-btn ${repMode === 'inf' ? 'on' : ''}" data-val="inf">무한∞</button>
          </div>
          <input type="number" class="mini pl-count" min="1" value="${repCount}" ${repMode === 'count' ? '' : 'hidden'} title="반복 횟수">
          <button class="mbtn-trig act-trigger" title="트리거 설정(클릭 후 키/마우스/패드 입력)">${ICON.trigger}<span class="trig-val">${esc(m.trigger || '없음')}</span></button>
        </div>`;
    } else {
      const sub = `${m.stepCount}스텝 · 총 ${fmtMs(m.durationMs || 0)}`;
      li.innerHTML = `
        <div class="macro-meta"><span class="name">${esc(m.name)}</span><span class="macro-sub">${sub}</span></div>
        <div class="macro-actions">
          <button class="mbtn act-dup" title="복제">${ICON.dup}</button>
          <button class="mbtn act-del" title="삭제">${ICON.del}</button>
        </div>`;
    }
    li.onclick = (e) => {
      if (e.target.closest('button, label, input')) return;
      if (mode === 'run') selectRunMacro(m.id); else openMacro(m.id);
    };
    li.querySelector('.act-del').onclick = () => confirmDeleteInline(li, m.id);
    const dupBtn = li.querySelector('.act-dup'); if (dupBtn) dupBtn.onclick = () => duplicateMacro(m.id);
    if (mode === 'run') {
      const tg = li.querySelector('.act-toggle');
      tg.onchange = async () => {
        try { await api.setEnabled(m.id, tg.checked); await loadMacros(); } // 재정렬(활성 위로) 위해 다시 그림
        catch (e) { log('error', e.message); tg.checked = !tg.checked; }
      };
      li.querySelector('.act-edit').onclick = () => { openMacro(m.id); switchTab('edit'); };
      li.querySelector('.act-trigger').onclick = (e) => beginTriggerCapture(m.id, m.name, e.currentTarget);
      // 반복(속도 기능 제거)
      const seg = li.querySelector('.pl-rep');
      const countEl = li.querySelector('.pl-count');
      seg.querySelectorAll('.seg-btn').forEach((b) => b.onclick = (e) => {
        e.stopPropagation();
        seg.querySelectorAll('.seg-btn').forEach((x) => x.classList.toggle('on', x === b));
        countEl.hidden = b.dataset.val !== 'count';
        saveRunPlayback(m.id, seg, countEl);
      });
      countEl.onchange = () => saveRunPlayback(m.id, seg, countEl);
    }
    listEl.appendChild(li);
  }
}

function renderMacroActive() {
  const st = state.status;
  document.querySelectorAll('.macro-item').forEach((el) => {
    const playing = st && st.state === 'playing' && st.currentMacroId === el.dataset.id;
    el.classList.toggle('active', playing);
  });
}

// 실행 항목 반복 저장(속도 기능 제거)
function saveRunPlayback(id, seg, countEl) {
  const mode = seg.querySelector('.seg-btn.on')?.dataset.val || 'once';
  const loopCount = mode === 'inf' ? 0 : mode === 'once' ? 1 : Math.max(1, parseInt(countEl.value, 10) || 1);
  api.setPlayback(id, loopCount).catch((e) => log('error', e.message));
}

// ---------- 실행 목록: 트리거 핫키 직접 설정(키/마우스/패드 캡처) ----------
const MOUSE_TRIG = { 0: 'Left', 1: 'Middle', 2: 'Right', 3: 'X1', 4: 'X2' };
function beginTriggerCapture(id, name, btn) {
  endTriggerCapture();
  capTrigId = id;
  btn.classList.add('capturing');
  const valEl = btn.querySelector('.trig-val');
  const prevVal = valEl ? valEl.textContent : '';
  if (valEl) valEl.textContent = '대기…';

  // 키보드 조합(chord): 여러 키를 함께 누른 채로 모두 떼면 확정. 1개만 누르면 단일 키.
  const held = new Set();      // 현재 물리적으로 눌려있는 키(vk)
  const captured = new Map();  // vk -> 라벨 (이번 캡처에서 함께 눌린 최대 집합)
  let mods = { ctrl: false, alt: false, shift: false, win: false };
  const liveLabel = () => {
    const mp = [];
    if (mods.ctrl) mp.push('Ctrl'); if (mods.alt) mp.push('Alt');
    if (mods.shift) mp.push('Shift'); if (mods.win) mp.push('Win');
    const all = [...mp, ...captured.values()];
    return all.length ? all.join('+') : '대기…';
  };
  const onKey = (e) => {
    if (e.key === 'Escape') { e.preventDefault(); endTriggerCapture(); return; }
    mods = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey };
    const vk = km.eventToVk(e);
    if (vk == null) { if (valEl) valEl.textContent = liveLabel(); return; } // 순수 모디파이어: 플래그만
    e.preventDefault();
    held.add(vk); captured.set(vk, km.vkLabel(vk));
    if (valEl) valEl.textContent = liveLabel();
  };
  const onKeyUp = (e) => {
    const vk = km.eventToVk(e);
    if (vk != null) held.delete(vk);
    if (held.size === 0 && captured.size > 0) {  // 모든 키를 뗀 순간 확정
      const keys = [...captured.keys()];
      if (keys.length === 1)
        finishTrigger({ ...mods, virtualKey: keys[0], keys: [], mouse: null, gamepad: null });
      else
        finishTrigger({ ...mods, virtualKey: 0, keys, mouse: null, gamepad: null });
    }
  };
  const onMouse = (e) => {
    const mb = MOUSE_TRIG[e.button]; if (!mb) return;
    e.preventDefault(); e.stopPropagation();
    finishTrigger({ ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: 0, keys: [], mouse: mb, gamepad: null });
  };
  const onCtx = (e) => e.preventDefault();
  log('info', `'${name}' 트리거 입력 대기… 키(여러 개 동시 가능)·마우스·패드를 누르고 떼면 확정 (Esc 취소)`);
  setTimeout(() => {
    if (capTrigId !== id) return;
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('keyup', onKeyUp, true);
    window.addEventListener('mousedown', onMouse, true);
    document.addEventListener('contextmenu', onCtx, true);
    api.listenStart().catch(() => {});
  }, 0);
  capCleanup = () => {
    window.removeEventListener('keydown', onKey, true);
    window.removeEventListener('keyup', onKeyUp, true);
    window.removeEventListener('mousedown', onMouse, true);
    document.removeEventListener('contextmenu', onCtx, true);
    api.listenStop().catch(() => {});
    btn.classList.remove('capturing');
    if (valEl) valEl.textContent = prevVal;
  };
}
function onTriggerGamepad(data) {
  if (!capTrigId || !data || !data.trigger || !data.trigger.gamepad) return;
  finishTrigger({ ctrl: false, alt: false, shift: false, win: false, virtualKey: 0, keys: [], mouse: null, gamepad: data.trigger.gamepad });
}
async function finishTrigger(trigger) {
  const id = capTrigId; endTriggerCapture();
  if (!id) return;
  try { await api.setTrigger(id, trigger); await loadMacros(); log('info', '트리거 설정됨.'); }
  catch (e) { log('error', e.message); }
}
function endTriggerCapture() {
  if (capCleanup) { capCleanup(); capCleanup = null; }
  capTrigId = null;
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
// 삭제: 항목 위에 빨간 오버레이로 예/아니오 인라인 확인
function confirmDeleteInline(li, id) {
  if (li.querySelector('.macro-del-overlay')) return;
  li.classList.add('confirming');
  const ov = document.createElement('div');
  ov.className = 'macro-del-overlay';
  ov.innerHTML = '<span class="q">삭제할까요?</span><button class="btn rec sm ov-yes">예</button><button class="btn ghost sm ov-no">아니오</button>';
  ov.onclick = (e) => e.stopPropagation();
  const close = () => { li.classList.remove('confirming'); ov.remove(); };
  ov.querySelector('.ov-yes').onclick = async (e) => {
    e.stopPropagation();
    try { await api.deleteMacro(id); if (editor.current()?.id === id) editor.close(); await loadMacros(); }
    catch (err) { log('error', err.message); close(); }
  };
  ov.querySelector('.ov-no').onclick = (e) => { e.stopPropagation(); close(); };
  li.appendChild(ov);
  // 다른 매크로가 '매크로 실행' 블록으로 이 매크로를 참조하면 경고로 알림(삭제는 가능 — 그 블록은 무효 처리)
  api.macroUsage(id).then((u) => {
    const names = (u && u.usedBy) || [];
    if (names.length && li.contains(ov)) {
      const q = ov.querySelector('.q');
      const label = names.length <= 2 ? `'${names.join("', '")}'` : `${names.length}개 매크로`;
      if (q) q.textContent = `${label}에서 사용 중! 삭제하면 그 블록이 무효가 됩니다. 삭제할까요?`;
    }
  }).catch(() => {});
}
async function duplicateMacro(id) {
  try {
    const m = await api.getMacro(id);
    await api.createMacro({ ...m, id: '', name: (m.name || '매크로') + ' 복사' });
    await loadMacros();
    log('info', `복제됨: ${m.name} 복사`);
  } catch (e) { log('error', e.message); }
}

// ---------- JSON 임포트/익스포트(좌측 메뉴) ----------
const safeName = (name) => ((name || 'macro').replace(/[\\/:*?"<>|]/g, '_').trim()) || 'macro';
function downloadJson(text, filename) {
  const blob = new Blob([text], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
// 내보내기 팝업 — 매크로 여러 개 선택 후 한 번에 내보내기
function openExportModal() {
  const macros = state.macros || [];
  if (!macros.length) { log('info', '내보낼 매크로가 없습니다.'); return; }
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay';
  overlay.innerHTML = `
    <div class="modal" role="dialog" aria-modal="true">
      <div class="modal-title">매크로 내보내기</div>
      <div class="export-tools">
        <label class="export-all"><input type="checkbox" id="exp-all"> 전체 선택</label>
        <span class="export-count" id="exp-count">0개 선택</span>
      </div>
      <div class="export-list" id="exp-list"></div>
      <div class="modal-actions">
        <button class="btn ghost" id="exp-cancel" type="button">취소</button>
        <button class="btn primary" id="exp-ok" type="button" disabled>내보내기</button>
      </div>
    </div>`;
  document.body.appendChild(overlay);
  const list = overlay.querySelector('#exp-list');
  for (const m of macros) {
    const row = document.createElement('label');
    row.className = 'export-row';
    row.innerHTML = `<input type="checkbox" value="${esc(m.id)}"><span class="ex-name"></span><span class="ex-sub"></span>`;
    row.querySelector('.ex-name').textContent = m.name;
    row.querySelector('.ex-sub').textContent = `${m.stepCount}스텝`;
    list.appendChild(row);
  }
  const boxes = () => [...list.querySelectorAll('input[type=checkbox]')];
  const upd = () => {
    const all = boxes(); const n = all.filter((b) => b.checked).length;
    overlay.querySelector('#exp-count').textContent = `${n}개 선택`;
    overlay.querySelector('#exp-ok').disabled = n === 0;
    const allBox = overlay.querySelector('#exp-all');
    allBox.checked = n > 0 && n === all.length;
    allBox.indeterminate = n > 0 && n < all.length;
  };
  list.addEventListener('change', upd);
  overlay.querySelector('#exp-all').onchange = (e) => { boxes().forEach((b) => b.checked = e.target.checked); upd(); };
  const close = () => overlay.remove();
  overlay.querySelector('#exp-cancel').onclick = close;
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) close(); });
  overlay.querySelector('#exp-ok').onclick = async () => {
    const ids = boxes().filter((b) => b.checked).map((b) => b.value);
    close();
    await exportMacros(ids);
  };
  upd();
}
async function exportMacros(ids) {
  if (!ids.length) return;
  try {
    const macros = [];
    for (const id of ids) macros.push(await api.getMacro(id));
    if (macros.length === 1) downloadJson(JSON.stringify(macros[0], null, 2), safeName(macros[0].name) + '.json');
    else downloadJson(JSON.stringify(macros, null, 2), `Y-Input-매크로-${macros.length}개.json`);
    log('info', `${macros.length}개 매크로를 내보냈습니다.`);
  } catch (e) { log('error', e.message); }
}
// 가져오기 — 파일 여러 개 + 각 파일이 단일 매크로 또는 배열(번들) 모두 지원
async function importMacros(fileList) {
  const files = [...(fileList || [])];
  if (!files.length) return;
  let ok = 0;
  for (const f of files) {
    try {
      const parsed = JSON.parse(await f.text());
      const arr = Array.isArray(parsed) ? parsed : [parsed];
      for (const m of arr) {
        if (!m || !Array.isArray(m.steps)) { log('error', `가져오기 건너뜀(${f.name}): 매크로 형식 아님`); continue; }
        const name = (m.name && String(m.name).trim()) || f.name.replace(/\.json$/i, '');
        await api.createMacro({ ...m, id: '', name });
        ok++;
      }
    } catch (e) { log('error', `가져오기 실패(${f.name}): ${e.message}`); }
  }
  if (ok) { await loadMacros(); log('info', `${ok}개 매크로를 가져왔습니다.`); }
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
      case 'inputDetected': if (capTrigId) onTriggerGamepad(msg.data); else editor.onInputDetected(msg.data); break;
      case 'inputMonitor': log('monitor', `[${msg.data.source}] ${msg.data.label}`, msg.data.time); break;
      case 'shutdown': handleShutdown(); break;
    }
  };
}

// 트레이 종료 신호 → 페이지 닫기(앱 모드면 OK, 일반 탭이면 안내 화면)
function handleShutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  document.title = 'Y Input — 종료됨';
  try { window.close(); } catch (e) { /* 일반 탭은 막힐 수 있음 */ }
  setTimeout(() => {
    const o = document.createElement('div');
    o.className = 'closed-screen';
    o.innerHTML = '<div><h1>Y Input 종료됨</h1><p>프로그램이 종료되었습니다. 이 창을 닫아 주세요.</p></div>';
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
  $('btn-settings').onclick = openSettings;
  $('settings-close').onclick = closeSettings;
  $('settings-overlay').onclick = (e) => { if (e.target === $('settings-overlay')) closeSettings(); };
  $('btn-update-check').onclick = onUpdateCheck;
  $('btn-update-apply').onclick = onUpdateApply;
  $('btn-new').onclick = () => { editor.open(null); switchTab('edit'); };
  $('btn-new-run').onclick = () => { editor.open(null); switchTab('edit'); };
  $('btn-import').onclick = () => $('file-import').click();
  $('file-import').onchange = (e) => { importMacros(e.target.files); e.target.value = ''; };
  $('btn-export').onclick = openExportModal;
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
  log('info', 'Y Input UI 준비됨.');
}

init();
