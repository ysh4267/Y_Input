import { api } from './api.js';
import { createEditor } from './editor.js';
import { confirmDialog, alertDialog, installLockdown } from './ui.js';
import * as km from './keymap.js';
import { unzip } from './zip.js';

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
  // 재생 중 편집 탭을 열면 모든 매크로 즉시 정지(안전).
  if (name === 'edit' && state.status && state.status.playingIds && state.status.playingIds.length) {
    api.stop().catch(() => {});
    log('info', '편집 탭 진입 — 실행 중인 매크로를 모두 정지했습니다.');
  }
  document.querySelectorAll('.tab-btn').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.page').forEach((p) => p.classList.toggle('active', p.id === 'page-' + name));
}

// ---------- 설정 드로어(드라이버·업데이트) ----------
function openSettings() {
  const ov = $('settings-overlay');
  ov.hidden = false;
  requestAnimationFrame(() => ov.classList.add('open'));
  loadVersion();
}

// 설정 패널 하단 버전 표시: 현재 빌드·GitHub 최신 릴리즈 태그·릴리즈 날짜
async function loadVersion() {
  try {
    const v = await api.appVersion();
    $('ver-current').textContent = v.current ? `${v.current}${v.currentDate ? ` (${v.currentDate})` : ''}` : '개발 빌드';
    $('ver-release').textContent = v.release || '—';
    $('ver-date').textContent = v.releaseDate || '—';
  } catch {
    $('ver-current').textContent = $('ver-release').textContent = $('ver-date').textContent = '—';
  }
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
  $('btn-monitor').classList.toggle('active', !!s.monitoring);
  $('btn-monitor').textContent = s.monitoring ? '■ 모니터 끄기' : '입력 모니터';

  $('foot-url').textContent = s.url || '';
  editor.onStatus(s);
  // 동시 재생: 재생 중이 아닌 매크로의 진행 점은 비우고, 표시 중인 매크로가 재생 아니면 하이라이트 해제
  const playingSet = new Set(s.playingIds || []);
  document.querySelectorAll('.macro-prog').forEach((el) => {
    if (!playingSet.has(el.dataset.id)) el.querySelectorAll('.pdot.on').forEach((d) => d.classList.remove('on'));
  });
  if (!runShownId || !playingSet.has(runShownId)) { lastStepIndex = -1; highlightRunStep(-1); }
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
  // 사용자 지정 순서(서버 Order)대로 표시 — 항목 빈 곳을 잡고 드래그하면 순서 변경·저장됨.
  for (let idx = 0; idx < state.macros.length; idx++) {
    const m = state.macros[idx];
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
          <span class="macro-num" title="순서 — 항목을 잡고 드래그하면 변경">${idx + 1}</span>
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
        </div>
        <div class="macro-prog" data-id="${m.id}" title="재생 진행">${'<i class="pdot"></i>'.repeat(Math.min(m.stepCount || 0, 60))}</div>`;
    } else {
      const sub = `${m.stepCount}스텝 · 총 ${fmtMs(m.durationMs || 0)}`;
      li.innerHTML = `
        <span class="macro-num" title="순서 — 항목을 잡고 드래그하면 변경">${idx + 1}</span>
        <div class="macro-meta"><span class="name">${esc(m.name)}</span><span class="macro-sub">${sub}</span></div>
        <div class="macro-actions">
          <button class="mbtn act-dup" title="복제">${ICON.dup}</button>
          <button class="mbtn act-del" title="삭제">${ICON.del}</button>
        </div>`;
    }
    li.querySelector('.act-del').onclick = () => confirmDeleteInline(li, m.id);
    const dupBtn = li.querySelector('.act-dup'); if (dupBtn) dupBtn.onclick = () => duplicateMacro(m.id);
    wireMacroDrag(li, m.id, mode);
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

// ---------- 매크로 목록 포인터 드래그(편집기 스텝처럼: 고스트가 손에 딸려 나오고, 슬라이드 + 자동 스크롤) ----------
const MREDUCE = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
let mdrag = null;
function scrollParent(el) {
  let n = el && el.parentElement;
  while (n) {
    const oy = getComputedStyle(n).overflowY;
    if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight + 1) return n;
    n = n.parentElement;
  }
  return document.scrollingElement || document.documentElement;
}
function nextVisibleMacro(el) {
  let n = el.nextElementSibling;
  while (n && (!n.classList || !n.classList.contains('macro-item') || n.style.display === 'none')) n = n.nextElementSibling;
  return n;
}
function captureMacroRects(els) { const m = new Map(); els.forEach((el) => m.set(el, el.getBoundingClientRect())); return m; }
function playMacroFlip(before) {
  if (MREDUCE) return;
  before.forEach((old, el) => {
    if (!el.isConnected) return;
    const now = el.getBoundingClientRect();
    const dy = old.top - now.top;
    if (!dy) return;
    el.style.transition = 'none'; el.style.transform = `translateY(${dy}px)`;
    requestAnimationFrame(() => { el.style.transition = 'transform .16s ease'; el.style.transform = ''; setTimeout(() => { el.style.transition = ''; el.style.transform = ''; }, 180); });
  });
}
function wireMacroDrag(li, id, mode) {
  // 항목의 빈 곳(번호·이름·여백) 아무 데나 눌러 드래그. 버튼·토글·입력 등 상호작용 요소는 제외.
  li.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || mdrag) return;
    if (e.target.closest('button, input, label, select, textarea')) return;
    const r = li.getBoundingClientRect();
    mdrag = { id, mode, li, listEl: li.closest('.macro-list'), scroller: scrollParent(li.closest('.macro-list')),
      startX: e.clientX, startY: e.clientY, offX: e.clientX - r.left, offY: e.clientY - r.top, moving: false, lastY: e.clientY, autoTimer: 0 };
    window.addEventListener('pointermove', macroPointerMove);
    window.addEventListener('pointerup', macroPointerUp, { once: true });
  });
}
function macroPointerMove(e) {
  if (!mdrag) return;
  if (!mdrag.moving) {
    if (Math.abs(e.clientX - mdrag.startX) + Math.abs(e.clientY - mdrag.startY) < 5) return;
    macroBeginDrag();
  }
  mdrag.lastY = e.clientY;
  mdrag.ghost.style.left = (e.clientX - mdrag.offX) + 'px';
  mdrag.ghost.style.top = (e.clientY - mdrag.offY) + 'px';
  macroUpdatePlaceholder();
  macroAutoScroll(e.clientY);
}
function macroBeginDrag() {
  mdrag.moving = true;
  const li = mdrag.li;
  const ph = document.createElement('li');
  ph.className = 'macro-placeholder';
  ph.style.height = li.offsetHeight + 'px';
  li.parentNode.insertBefore(ph, li);
  mdrag.placeholder = ph;
  const gr = li.getBoundingClientRect();
  const ghost = li.cloneNode(true);
  ghost.classList.add('macro-ghost');
  ghost.style.width = gr.width + 'px';
  ghost.style.left = gr.left + 'px';
  ghost.style.top = gr.top + 'px';
  document.body.appendChild(ghost);
  mdrag.ghost = ghost;
  li.style.display = 'none';
}
function macroUpdatePlaceholder() {
  if (!mdrag || !mdrag.moving) return;
  const listEl = mdrag.listEl;
  const items = [...listEl.querySelectorAll('.macro-item')].filter((el) => el.style.display !== 'none');
  const g = mdrag.ghost.getBoundingClientRect();
  const mid = g.top + g.height / 2;
  let target = null;
  for (const c of items) { const r = c.getBoundingClientRect(); if (mid < r.top + r.height / 2) { target = c; break; } }
  if (nextVisibleMacro(mdrag.placeholder) === target) return;
  const before = captureMacroRects(items.concat(mdrag.placeholder));
  if (target) listEl.insertBefore(mdrag.placeholder, target); else listEl.appendChild(mdrag.placeholder);
  playMacroFlip(before);
}
function macroAutoScroll(clientY) {
  if (mdrag.autoTimer) { cancelAnimationFrame(mdrag.autoTimer); mdrag.autoTimer = 0; }
  const sc = mdrag.scroller; if (!sc) return;
  const r = sc.getBoundingClientRect();
  const EDGE = 44;
  let dy = 0;
  if (clientY < r.top + EDGE) dy = -Math.ceil((r.top + EDGE - clientY) / 3);
  else if (clientY > r.bottom - EDGE) dy = Math.ceil((clientY - (r.bottom - EDGE)) / 3);
  if (!dy) return;
  const step = () => {
    if (!mdrag || !mdrag.moving) return;
    const top0 = sc.scrollTop;
    sc.scrollTop = Math.max(0, Math.min(sc.scrollHeight - sc.clientHeight, sc.scrollTop + dy));
    if (sc.scrollTop !== top0) { mdrag.ghost.style.top = (mdrag.lastY - mdrag.offY) + 'px'; macroUpdatePlaceholder(); }
    mdrag.autoTimer = requestAnimationFrame(step);
  };
  mdrag.autoTimer = requestAnimationFrame(step);
}
function macroPointerUp() {
  window.removeEventListener('pointermove', macroPointerMove);
  const d = mdrag; mdrag = null;
  if (!d) return;
  if (d.autoTimer) cancelAnimationFrame(d.autoTimer);
  if (!d.moving) { // 이동 없이 뗌 = 클릭: 선택/열기
    if (d.mode === 'run') selectRunMacro(d.id); else openMacro(d.id);
    return;
  }
  d.ghost.remove();
  const listEl = d.listEl;
  const ids = [];
  for (const child of [...listEl.children]) {
    if (child === d.placeholder) { ids.push(d.id); continue; }
    if (child.classList && child.classList.contains('macro-item') && child.style.display !== 'none' && child.dataset.id !== d.id) ids.push(child.dataset.id);
  }
  d.placeholder.remove(); d.li.style.display = '';
  state.macros.forEach((m) => { if (!ids.includes(m.id)) ids.push(m.id); });
  state.macros = ids.map((id) => state.macros.find((m) => m.id === id)).filter(Boolean);
  renderMacroList($('macro-list-run'), $('macro-empty-run'), 'run');
  renderMacroList($('macro-list-edit'), $('macro-empty-edit'), 'edit');
  api.reorder(state.macros.map((m) => m.id)).catch((e) => { log('error', e.message); loadMacros(); });
}

function renderMacroActive() {
  const playing = new Set((state.status && state.status.playingIds) || []);
  document.querySelectorAll('.macro-item').forEach((el) => {
    el.classList.toggle('active', playing.has(el.dataset.id));
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
  const playingThis = st && st.playingIds && st.playingIds.includes(macro.id);
  highlightRunStep(playingThis ? lastStepIndex : -1);
}
function highlightRunStep(idx) {
  const wrap = $('run-steps'); if (!wrap) return;
  wrap.querySelectorAll('.run-steps-row.playing').forEach((r) => r.classList.remove('playing'));
  if (idx < 0) return;
  const row = wrap.querySelector(`.run-steps-row[data-i="${idx}"]`);
  if (!row) return;
  row.classList.add('playing');
  // run-steps 패널 '안에서만' 스크롤 — 좌측 매크로 목록/페이지 스크롤은 건드리지 않는다.
  const wr = wrap.getBoundingClientRect(), rr = row.getBoundingClientRect();
  if (rr.top < wr.top) wrap.scrollTop -= (wr.top - rr.top) + 8;
  else if (rr.bottom > wr.bottom) wrap.scrollTop += (rr.bottom - wr.bottom) + 8;
}
// 실행 목록의 매크로별 진행 점(인디케이터): 진행 비율만큼 왼쪽부터 채움
function updateMacroProg(macroId, frac) {
  const el = document.querySelector(`.macro-prog[data-id="${macroId}"]`);
  if (!el) return;
  const dots = el.querySelectorAll('.pdot');
  const filled = Math.max(0, Math.min(dots.length, Math.round((frac || 0) * dots.length)));
  dots.forEach((d, i) => d.classList.toggle('on', i < filled));
}
function clearMacroProg() {
  document.querySelectorAll('.macro-prog .pdot.on').forEach((d) => d.classList.remove('on'));
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
function downloadBlob(blob, filename) {
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
// 내보내기 — 선택한 매크로 + 그것들이 참조(매크로 실행)하는 모든 매크로를 재귀로 모아
// 단일 JSON 배열 파일로 저장(자기완결적 번들 → 가져올 때 참조가 끊기지 않음).
async function exportMacros(ids) {
  if (!ids.length) return;
  try {
    const wanted = new Map(); // id -> 전체 매크로(참조 대상까지 포함)
    const addWithDeps = async (id) => {
      if (!id || wanted.has(id)) return;
      let m;
      try { m = await api.getMacro(id); } catch { return; }
      if (!m) return;
      wanted.set(id, m);
      for (const s of (m.steps || [])) {
        const ev = s && s.event;
        if (ev && ev.$type === 'macroRef' && ev.macroId) await addWithDeps(ev.macroId); // 매크로의 매크로의…까지 재귀
      }
    };
    for (const id of ids) await addWithDeps(id);
    const macros = [...wanted.values()];
    const filename = (ids.length === 1 ? safeName(wanted.get(ids[0]) && wanted.get(ids[0]).name) : 'Y_Input Macros') + '.json';
    downloadBlob(new Blob([JSON.stringify(macros, null, 2)], { type: 'application/json' }), filename);
    const extra = macros.length - ids.length;
    log('info', `${ids.length}개 매크로${extra > 0 ? ` + 참조 ${extra}개` : ''}를 ${filename} 으로 내보냈습니다.`);
  } catch (e) { log('error', e.message); }
}
// 가져오기 — .zip(여러 매크로 묶음) + .json(단일/배열 번들) 파일 여러 개 모두 지원.
// 모든 매크로를 한 묶음으로 모아 서버에 한 번에 보내면, 서버가 내부 매크로 참조(macroRef)를
// 새 Id로 자동 재연결한다(임포트로 참조가 끊기던 문제 해결).
async function importMacros(fileList) {
  const files = [...(fileList || [])];
  if (!files.length) return;
  const incoming = [];
  for (const f of files) {
    try {
      if (/\.zip$/i.test(f.name) || f.type === 'application/zip') {
        const items = (await unzip(await f.arrayBuffer())).filter((it) => /\.json$/i.test(it.name));
        if (!items.length) { log('error', `가져오기 건너뜀(${f.name}): zip 안에 .json 매크로 없음`); continue; }
        for (const it of items) collectMacros(incoming, it.text, it.name, `${f.name}>${it.name}`);
      } else {
        collectMacros(incoming, await f.text(), f.name, f.name);
      }
    } catch (e) { log('error', `가져오기 실패(${f.name}): ${e.message}`); }
  }
  if (!incoming.length) return;
  try {
    const r = await api.importMacros(incoming);
    await loadMacros();
    const added = r?.added ?? incoming.length;
    log('info', added > 0 ? `${added}개 매크로를 가져왔습니다(참조 자동 연결).` : '이미 동일한 매크로가 있어 새로 추가된 항목은 없습니다.');
  } catch (e) { log('error', `가져오기 실패: ${e.message}`); }
}
// JSON 텍스트(단일 매크로 또는 배열 번들)에서 매크로 객체를 모은다. 옛 id/이름은 그대로 두어
// 서버가 참조 재연결에 사용하게 한다.
function collectMacros(out, text, fileName, label) {
  let parsed;
  try { parsed = JSON.parse(text); } catch { log('error', `가져오기 건너뜀(${label}): JSON 파싱 실패`); return; }
  const arr = Array.isArray(parsed) ? parsed : [parsed];
  for (const m of arr) {
    if (!m || !Array.isArray(m.steps)) { log('error', `가져오기 건너뜀(${label}): 매크로 형식 아님`); continue; }
    const name = (m.name && String(m.name).trim()) || fileName.replace(/\.json$/i, '');
    out.push({ ...m, name });
  }
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
  const frac = p.stepCount ? (p.stepIndex + 1) / p.stepCount : 0;
  // 동시 재생: 진행 이벤트의 macroId 매크로 점만 채운다.
  updateMacroProg(p.macroId, frac);
  // run-steps 패널: 표시 중인 매크로의 진행만 하이라이트. 아무것도 안 보고 있으면 진행 중인 걸 표시.
  if (runShownId && runShownId === p.macroId) { lastStepIndex = p.stepIndex; highlightRunStep(p.stepIndex); }
  else if (!runShownId) selectRunMacro(p.macroId);
}

// ---------- 업데이트(GitHub Releases) ----------
async function onUpdateCheck() {
  $('update-status').textContent = '확인 중…';
  try {
    const r = await api.updateCheck();
    if (!r.ok) { $('update-status').textContent = '확인 실패: ' + (r.message || ''); return; }
    const cur = r.current || '개발 빌드';
    if (r.updateAvailable) $('update-status').textContent = `새 버전 ${r.latest} 사용 가능 (현재 ${cur}) — 다운로드를 누르세요.`;
    else $('update-status').textContent = `최신 상태 (현재 ${cur})`;
  } catch (e) { $('update-status').textContent = '확인 실패: ' + e.message; }
}
// 다운로드 — 교체/재시작 대신, 최신 릴리즈 빌드의 다운로드 링크를 열어 브라우저 다운로드 폴더에 받는다.
async function onUpdateDownload() {
  $('update-status').textContent = '다운로드 링크 확인 중…';
  try {
    const r = await api.updateCheck();
    const url = (r && r.downloadUrl) || (r && r.pageUrl);
    if (!url) { $('update-status').textContent = '다운로드 링크를 찾을 수 없습니다: ' + ((r && r.message) || ''); return; }
    window.open(url, '_blank', 'noopener'); // 브라우저가 다운로드 폴더에 저장
    $('update-status').textContent = r.downloadUrl
      ? `${r.latest || '최신 버전'} 다운로드를 시작했습니다 — 브라우저 다운로드 폴더를 확인하세요.`
      : '릴리즈 페이지를 열었습니다.';
  } catch (e) { $('update-status').textContent = '다운로드 실패: ' + e.message; }
}

// ---------- 와이어링 ----------
function wire() {
  document.querySelectorAll('.tab-btn').forEach((b) => b.onclick = () => switchTab(b.dataset.tab));
  $('btn-settings').onclick = openSettings;
  $('settings-close').onclick = closeSettings;
  $('settings-overlay').onclick = (e) => { if (e.target === $('settings-overlay')) closeSettings(); };
  $('btn-update-check').onclick = onUpdateCheck;
  $('btn-update-apply').onclick = onUpdateDownload;
  $('btn-new').onclick = () => { editor.open(null); switchTab('edit'); };
  $('btn-new-run').onclick = () => { editor.open(null); switchTab('edit'); };
  $('btn-import').onclick = () => $('file-import').click();
  $('file-import').onchange = (e) => { importMacros(e.target.files); e.target.value = ''; };
  $('btn-export').onclick = openExportModal;
  $('btn-reset-macros').onclick = async () => {
    const n = state.macros.length;
    if (!n) { await alertDialog('삭제할 매크로가 없습니다.', { title: '매크로 모두 초기화' }); return; }
    const ok = await confirmDialog(`저장된 매크로 ${n}개를 모두 삭제할까요? 휴지통으로 이동되며, 되돌리려면 휴지통에서 복원해야 합니다.`, { title: '매크로 모두 초기화', ok: '모두 삭제', cancel: '취소' });
    if (!ok) return;
    try {
      const r = await api.resetMacros();
      await loadMacros();
      log('warn', `매크로 ${r?.deleted ?? n}개를 모두 초기화했습니다(휴지통 이동).`);
    } catch (e) { log('error', e.message); }
  };
  $('log-fab').onclick = () => { $('log-float').hidden = !$('log-float').hidden; };
  $('log-float-close').onclick = () => { $('log-float').hidden = true; };
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
}

async function init() {
  installLockdown(); // 텍스트 드래그·브라우저 단축키·우클릭 차단
  wire();
  switchTab('run'); // 기본: 매크로 실행 탭
  connectWs();
  try { renderStatus(await api.status()); } catch (e) { log('error', e.message); }
  await loadMacros();
  editor.open(null); // 기본: 새 매크로 추가 모드로 시작
  log('info', 'Y Input UI 준비됨.');
}

init();
