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
let lastLoops = [];       // 표시 중 매크로의 최근 반복 프레임 [{startIndex,total,remaining}]
let capTrigId = null;     // 실행 목록에서 트리거 캡처 중인 매크로 id
let capCleanup = null;    // 트리거 캡처 정리 콜백

// 매크로 목록 아이콘(라인 SVG): 복제·삭제·편집·트리거·핀
const ICON = {
  pin: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M7 3h10M9 3v6l-2.2 3.2V13h10.4v-.8L15 9V3M12 13v8"/></svg>',
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
  loadSyncConfig();
  loadWidgetConfig();
}

// ---------- 위젯 모양(색/불투명도) ----------
async function loadWidgetConfig() {
  try {
    const c = await api.widgetGetConfig();
    if ($('widget-color')) $('widget-color').value = c.color || '#1f232c';
    if ($('widget-opacity')) { $('widget-opacity').value = c.opacity ?? 72; $('widget-opacity-val').textContent = $('widget-opacity').value + '%'; }
  } catch { /* 무시 */ }
}
let widgetCfgTimer = null;
function saveWidgetConfig() {
  const color = $('widget-color').value;
  const ov = parseInt($('widget-opacity').value, 10);
  const opacity = Number.isFinite(ov) ? ov : 72; // 0도 유효(|| 72 쓰면 0%가 72%로 튀는 버그)
  clearTimeout(widgetCfgTimer);
  widgetCfgTimer = setTimeout(() => api.widgetSetConfig({ color, opacity }).catch((e) => log('error', e.message)), 140);
}

// ---------- 동기화(GitHub 비공개 저장소) ----------
let syncTokenSet = false; // 서버에 토큰이 이미 있으면 빈 입력=유지
function fmtSyncTime(iso) {
  if (!iso) return '';
  try { return new Date(iso).toLocaleString(); } catch { return ''; }
}
function renderSyncStatus(s) {
  const el = $('sync-status'); if (!el || !s) return;
  syncTokenSet = !!s.hasToken;
  const tok = $('sync-token'); if (tok) tok.placeholder = s.hasToken ? '설정됨 — 바꾸려면 새로 입력' : 'ghp_… (gist 권한)';
  const parts = [];
  parts.push(s.enabled ? '켜짐' : '꺼짐');
  if (s.hasGist) parts.push('gist 연결됨');
  if (s.syncing) parts.push('동기화 중…');
  if (s.lastResult) parts.push(s.lastResult);
  if (s.lastSync) parts.push('마지막: ' + fmtSyncTime(s.lastSync));
  el.textContent = parts.join(' · ');
  el.classList.toggle('err', !!(s.lastResult && s.lastResult.startsWith('실패')));
}
async function loadSyncConfig() {
  try {
    const s = await api.syncConfig();
    $('sync-enabled').checked = !!s.enabled;
    renderSyncStatus(s);
  } catch (e) { const el = $('sync-status'); if (el) el.textContent = '설정을 불러오지 못했습니다: ' + e.message; }
}
async function onSyncSave() {
  const token = $('sync-token').value;
  const cfg = {
    enabled: $('sync-enabled').checked,
    token: token ? token : null, // 빈칸이면 기존 토큰 유지
  };
  try {
    const s = await api.syncSave(cfg);
    $('sync-token').value = ''; // 저장 후 입력칸 비움(플레이스홀더가 '설정됨' 표시)
    renderSyncStatus(s);
    log('info', '동기화 설정을 저장했습니다.');
  } catch (e) { log('error', e.message); }
}
async function onSyncNow() {
  const btn = $('btn-sync-now'); if (btn) btn.disabled = true;
  try { const r = await api.syncNow(); renderSyncStatus(r.status); log('info', '동기화: ' + r.message); }
  catch (e) { log('error', e.message); }
  finally { if (btn) btn.disabled = false; }
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
  // 동시 재생: 재생 중이 아닌 매크로의 인디케이터는 초기화하고, 표시 중인 매크로가 재생 아니면 하이라이트 해제
  const playingSet = new Set(s.playingIds || []);
  document.querySelectorAll('.macro-prog').forEach((el) => {
    if (!playingSet.has(el.dataset.id)) { resetMacroTimeline(el); delayAnims.delete(el.dataset.id); }
  });
  if (!runShownId || !playingSet.has(runShownId)) { lastStepIndex = -1; lastLoops = []; applyRunProgress(-1, []); }
  renderMacroActive();
}

// ---------- 매크로 목록(실행 탭=재생+적용토글 / 편집 탭=복제) ----------
async function loadMacros() {
  state.macros = await api.listMacros();
  renderMacroList($('macro-list-run'), $('macro-empty-run'), 'run');
  renderMacroList($('macro-list-edit'), $('macro-empty-edit'), 'edit');
  if (runShownId && !state.macros.some((m) => m.id === runShownId)) clearRunSteps();
  renderMacroActive();
  reflectPins(); // 목록 다시 그린 뒤 핀 버튼 상태 반영
}

// ---------- 고정(핀) 위젯 — 클릭하면 별도의 보더리스 창(WebView2)으로 띄움. 상태는 서버가 관리 ----------
let pinnedIds = []; // 현재 열려 있는 위젯 창의 macroId들(서버 기준)
const isPinned = (id) => pinnedIds.includes(id);
function reflectPins() { // 목록의 핀 버튼 활성 표시를 현재 열린 창과 맞춤
  document.querySelectorAll('.macro-item.run').forEach((li) => {
    const b = li.querySelector('.act-pin'); if (b) b.classList.toggle('on', isPinned(li.dataset.id));
  });
}
function setPinned(ids) { pinnedIds = Array.isArray(ids) ? ids : []; reflectPins(); }
async function togglePin(id) {
  const open = isPinned(id);
  // 낙관적 반영(서버 'widgets' 브로드캐스트가 최종 확정)
  pinnedIds = open ? pinnedIds.filter((x) => x !== id) : [...pinnedIds, id];
  reflectPins();
  try { if (open) await api.widgetClose(id); else await api.widgetOpen(id); }
  catch (e) { log('error', '위젯 ' + (open ? '닫기' : '열기') + ' 실패: ' + e.message); loadPinned(); }
}
async function loadPinned() { try { const r = await api.widgetList(); setPinned(r.ids || []); } catch { /* 무시 */ } }

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
            <button class="mbtn act-pin${isPinned(m.id) ? ' on' : ''}" title="상단에 고정(위젯으로 띄우기)">${ICON.pin}</button>
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
        <div class="macro-prog" data-id="${m.id}" title="재생 진행(● 행위 · ━ 지연 · ⟲ 반복)"></div>
        <div class="macro-prog-bar" data-id="${m.id}" title="전체 진행도"><i class="macro-prog-bar-fill"></i></div>`;
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
      try { buildTimeline(li.querySelector('.macro-prog'), m.shape); } catch (err) { /* 인디케이터 실패가 목록 렌더를 막지 않도록 */ }
      const tg = li.querySelector('.act-toggle');
      tg.onchange = async () => {
        try { await api.setEnabled(m.id, tg.checked); await loadMacros(); } // 재정렬(활성 위로) 위해 다시 그림
        catch (e) { log('error', e.message); tg.checked = !tg.checked; }
      };
      li.querySelector('.act-edit').onclick = () => { openMacro(m.id); switchTab('edit'); };
      li.querySelector('.act-pin').onclick = (e) => { e.stopPropagation(); togglePin(m.id); };
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

// ---------- 실행 페이지: 현재 매크로 동작 순서(펼친 스텝, 읽기전용) + 재생 하이라이트 ----------
async function selectRunMacro(id) {
  runShownId = id; // 낙관적 설정 — 로딩 중 중복 요청/이벤트 누락 방지
  try { renderRunSteps(await api.getTree(id)); } // 참조를 들여쓴 트리(step의 i = 재생 stepIndex)
  catch (e) { log('error', e.message); }
}
function clearRunSteps() {
  runShownId = null;
  if ($('run-steps')) { $('run-steps')._pi = -1; $('run-steps').innerHTML = ''; }
  if ($('run-steps-empty')) $('run-steps-empty').hidden = false;
  if ($('run-steps-title')) $('run-steps-title').textContent = '현재 매크로';
}
// 참조(매크로 실행)를 들여쓴 트리로 렌더. step의 data-i = 재생 stepIndex와 1:1, ref 헤더는 from~to 범위.
function renderRunSteps(data) {
  runShownId = data.id;
  const wrap = $('run-steps'); if (!wrap) return;
  wrap._pi = -1; // 증분 진행 갱신용 인덱스 초기화(패널을 새로 그리므로)
  wrap.innerHTML = '';
  const nodes = data.nodes || [];
  $('run-steps-empty').hidden = nodes.length > 0;
  $('run-steps-title').textContent = data.name || '현재 매크로';
  renderRunNodes(wrap, nodes);
  const st = state.status;
  const playingThis = st && st.playingIds && st.playingIds.includes(data.id);
  applyRunProgress(playingThis ? lastStepIndex : -1, playingThis ? lastLoops : []);
}
function renderRunNodes(parent, nodes) {
  nodes.forEach((node) => {
    if (node.kind === 'ref') {
      const head = document.createElement('div');
      head.className = 'run-steps-row rs-ref';
      if (node.from != null) { head.dataset.from = node.from; head.dataset.to = node.to; }
      head.innerHTML =
        `<span class="rs-rail"><span class="rs-node type-macroRef">${km.TYPE_ICON.macroRef || '🧩'}</span></span>` +
        `<span class="rs-body"><span class="rs-head">` +
          `<span class="rs-label rs-ref-label">매크로 실행 — ${esc(node.name || '?')}` +
          (node.note ? ` <span class="rs-ref-note">(${esc(node.note)})</span>` : '') +
          `</span></span></span>`;
      parent.appendChild(head);
      if (node.children && node.children.length) { // 그 매크로 내용을 한 단계 들여쓰기로
        const group = document.createElement('div'); group.className = 'rs-group';
        renderRunNodes(group, node.children);
        parent.appendChild(group);
      }
      return;
    }
    const t = node.event['$type'];
    const isDelay = t === 'delay';
    const ms = isDelay ? Math.round(node.delayBeforeMs || 0) : 0;
    const row = document.createElement('div');
    row.className = 'run-steps-row'; row.dataset.i = node.i; row.dataset.t = t;
    row.innerHTML =
      `<span class="rs-rail"><span class="rs-node type-${t}">${km.TYPE_ICON[t] || '•'}</span></span>` +
      `<span class="rs-body"><span class="rs-head">` +
        `<span class="rs-num">${node.i + 1}</span>` +
        `<span class="rs-label">${esc(isDelay ? `지연 ${ms} ms` : km.summarizeEvent(node.event))}</span>` +
        (isDelay ? `<span class="rs-elapsed"></span>` : '') +
        (t === 'loopStart' ? `<span class="rs-loop-iter" data-start="${node.i}"></span>` : '') +
      `</span>` +
      (isDelay ? `<span class="rs-delaybar" data-ms="${ms}"><span class="rs-fill"></span></span>` : '') +
      `</span>`;
    parent.appendChild(row);
  });
}
// 우측 패널 진행 반영: 진행된 행 glow, 활성 행 강조, 지연 채움/경과ms(활성은 rAF), 반복 회차 k/N.
function applyRunProgress(idx, loops) {
  const wrap = $('run-steps'); if (!wrap) return;
  // 스텝 행(다수)은 바뀐 행만 갱신(전체 순회 제거). rs-ref 행은 data-i가 없어 resolve에 안 잡힘 → 아래 별도 패스.
  applyIndexDelta(wrap._pi ?? -1, idx,
    (i) => wrap.querySelector(`.run-steps-row[data-i="${i}"]`),
    (r) => { r.classList.add('done'); r.classList.remove('active'); if (r.dataset.t === 'delay') { const f = r.querySelector('.rs-fill'); if (f) { f.style.transition = 'none'; f.style.width = '100%'; } const e = r.querySelector('.rs-elapsed'); if (e) { const ms = +r.querySelector('.rs-delaybar').dataset.ms || 0; e.textContent = `${ms} / ${ms} ms`; } } },
    (r) => { r.classList.add('active'); r.classList.remove('done'); },
    (r) => { r.classList.remove('done', 'active'); if (r.dataset.t === 'delay') { const f = r.querySelector('.rs-fill'); if (f) { f.style.transition = 'none'; f.style.width = '0%'; } const e = r.querySelector('.rs-elapsed'); if (e) e.textContent = ''; } });
  wrap._pi = idx;
  // 매크로 실행(ref) 헤더 — 범위 [from,to] 기준(개수 적음), 전체 패스
  wrap.querySelectorAll('.run-steps-row.rs-ref').forEach((r) => {
    const from = r.dataset.from != null ? +r.dataset.from : -1;
    const to = r.dataset.to != null ? +r.dataset.to : -1;
    r.classList.toggle('active', from >= 0 && idx >= from && idx <= to);
    r.classList.toggle('done', to >= 0 && idx > to);
  });
  wrap.querySelectorAll('.rs-loop-iter').forEach((b) => { b.textContent = ''; });
  (loops || []).forEach((f) => {
    const b = wrap.querySelector(`.rs-loop-iter[data-start="${f.startIndex}"]`);
    if (!b) return;
    const total = f.total || 1;
    b.textContent = `${total - f.remaining + 1} / ${total}`;
  });
  if (idx >= 0) { const row = wrap.querySelector(`.run-steps-row[data-i="${idx}"]`); if (row) scrollRowIntoPanel(wrap, row); }
}
function scrollRowIntoPanel(wrap, row) {
  // run-steps 패널 '안에서만' 스크롤 — 좌측 목록/페이지 스크롤은 건드리지 않는다.
  const wr = wrap.getBoundingClientRect(), rr = row.getBoundingClientRect();
  if (rr.top < wr.top) wrap.scrollTop -= (wr.top - rr.top) + 8;
  else if (rr.bottom > wr.bottom) wrap.scrollTop += (rr.bottom - wr.bottom) + 8;
}
// ---------- 좌측 목록 인디케이터: 점(행위)·선(지연,채움)·반복 브라켓 가로 타임라인 ----------
// shape 토큰: ["a"]=행위 / ["d",ms]=지연 / ["s",n]=반복시작 / ["e"]=반복끝. data-i = 재생 stepIndex.
function buildTimeline(container, shape) {
  if (!container) return;
  container._pi = -1; // 증분 진행 갱신용 마지막 적용 stepIndex(새로 그렸으니 초기화)
  container.innerHTML = '';
  container.dataset.shape = JSON.stringify(shape || []); // 전체 진행도(반복 반영) 계산용
  if (!shape || !shape.length) return;
  const dot = (i, extra) => { const d = document.createElement('i'); d.className = 'tl-dot' + (extra || ''); d.dataset.i = i; return d; };
  let cur = container; const stack = [];
  for (let i = 0; i < shape.length; i++) {
    const t = shape[i][0];
    if (t === 'a') {
      cur.appendChild(dot(i));
    } else if (t === 'd') {
      const ln = document.createElement('i'); ln.className = 'tl-line'; ln.dataset.i = i;
      const ms = +shape[i][1] || 0;
      ln.style.width = Math.round(Math.max(12, Math.min(40, 12 + ms * 0.035))) + 'px';
      const f = document.createElement('i'); f.className = 'tl-fill'; ln.appendChild(f); cur.appendChild(ln);
    } else if (t === 's') {
      // 반복: 브라켓(track/채움) 오버레이 + 본문(시작/끝 점은 본문 안). 중첩 루프 안전(insertBefore 미사용).
      const lp = document.createElement('span'); lp.className = 'tl-loop'; lp.dataset.start = i;
      const bar = document.createElement('i'); bar.className = 'tl-loop-bar';
      const fill = document.createElement('i'); fill.className = 'tl-loop-fill';
      const inner = document.createElement('span'); inner.className = 'tl-loop-inner';
      lp.append(bar, fill, inner);
      inner.appendChild(dot(i, ' tl-loop-pt')); // 반복 시작 점
      cur.appendChild(lp); stack.push(cur); cur = inner;
    } else if (t === 'e') {
      if (stack.length) {
        cur.appendChild(dot(i, ' tl-loop-pt')); // 반복 끝 점
        const lp = cur.parentElement; if (lp && lp.classList.contains('tl-loop')) lp.dataset.end = i; // 루프 끝 인덱스(완료 판정용)
        cur = stack.pop(); // 상위로
      }
    }
  }
}
function setLoopFill(lp, frac) { lp.style.setProperty('--loop-fill', (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%'); }
function resetMacroTimeline(el) {
  if (!el) return;
  el._pi = -1; // 증분 진행 갱신용 인덱스 초기화(전체를 비우므로)
  el.querySelectorAll('.tl-dot, .tl-line').forEach((n) => n.classList.remove('done', 'active'));
  el.querySelectorAll('.tl-fill').forEach((f) => { f.style.transition = 'none'; f.style.width = '0%'; });
  el.querySelectorAll('.tl-loop').forEach((lp) => { lp.classList.remove('active', 'done'); setLoopFill(lp, 0); });
  el.scrollLeft = 0;
  const bar = el.nextElementSibling; // 전체 진행도 선 비우기
  if (bar && bar.firstElementChild) bar.firstElementChild.style.width = '0%';
}
// 전체 진행도 선 — 모든 항목에 항상 표시. 채움=반복까지 반영한 단조 진행률(뒤로 안 감).
function setProgBar(el, frac) {
  const bar = el && el.nextElementSibling;
  if (!bar || !bar.classList.contains('macro-prog-bar')) return;
  const fill = bar.firstElementChild;
  if (fill) fill.style.width = (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%';
}
// 반복까지 반영한 단조(monotone) 진행률 0..1 — 펼친(반복 풀린) 전체 스텝 수 대비 완료 스텝 수.
// idx=현재 스텝(재생 stepIndex, 항상 a/d 리프), loops=활성 반복 프레임[{startIndex,total,remaining}].
function loopAwareFraction(shape, idx, loops) {
  const n = shape.length;
  if (!n) return 0;
  const endOf = new Array(n).fill(-1), st = []; // 's'→짝 'e' 인덱스
  for (let i = 0; i < n; i++) {
    const t = shape[i][0];
    if (t === 's') st.push(i);
    else if (t === 'e') { const s = st.pop(); if (s != null) endOf[s] = i; }
  }
  const lm = new Map(); (loops || []).forEach((f) => lm.set(f.startIndex, f));
  const total = (a, b) => { // [a,b) 1회 실행의 펼친 리프 수
    let c = 0;
    for (let i = a; i < b;) {
      const t = shape[i][0];
      if (t === 's') { const e = endOf[i] < 0 ? b : endOf[i]; c += Math.max(1, +shape[i][1] || 1) * total(i + 1, e); i = e + 1; }
      else if (t === 'e') i++;
      else { c += 1; i++; }
    }
    return c;
  };
  const done = (a, b) => { // [a,b) 중 현재까지 완료한 리프 수
    let c = 0;
    for (let i = a; i < b;) {
      const t = shape[i][0];
      if (t === 's') {
        const e = endOf[i] < 0 ? b : endOf[i], N = Math.max(1, +shape[i][1] || 1), bw = total(i + 1, e);
        if (idx > e) c += N * bw;                 // 루프 완전히 통과 → 전부 완료
        else if (idx > i && idx < e) {            // 현재 이 루프 안
          const f = lm.get(i), iterDone = f ? Math.max(0, f.total - f.remaining) : 0;
          c += iterDone * bw + done(i + 1, e);    // 끝난 회차 + 이번 회차 진행분
        }
        i = e + 1;
      } else if (t === 'e') i++;
      else { if (i <= idx) c += 1; i++; }         // 현재 스텝 포함(끝에서 100% 도달)
    }
    return c;
  };
  const W = total(0, n);
  return W > 0 ? Math.max(0, Math.min(1, done(0, n) / W)) : 0;
}
function updateMacroTimeline(macroId, p) {
  // 목록 항목 + 고정(핀) 위젯 등 같은 macroId의 모든 인디케이터를 갱신(각자 _pi로 증분 추적).
  document.querySelectorAll(`.macro-prog[data-id="${macroId}"]`).forEach((el) => updateOneTimeline(el, p));
}
function updateOneTimeline(el, p) {
  const idx = p.stepIndex;
  // 점·선(스텝)은 바뀐 노드만 갱신(전체 순회 제거). 채움: done=100% / undo=0% / active(지연)는 delayAnims가 담당.
  applyIndexDelta(el._pi ?? -1, idx,
    (i) => el.querySelector(`.tl-dot[data-i="${i}"], .tl-line[data-i="${i}"]`),
    (n) => { n.classList.add('done'); n.classList.remove('active'); if (n.classList.contains('tl-line')) { const f = n.querySelector('.tl-fill'); if (f) { f.style.transition = 'none'; f.style.width = '100%'; } } },
    (n) => { n.classList.add('active'); n.classList.remove('done'); },
    (n) => { n.classList.remove('done', 'active'); if (n.classList.contains('tl-line')) { const f = n.querySelector('.tl-fill'); if (f) { f.style.transition = 'none'; f.style.width = '0%'; } } });
  el._pi = idx;
  el.querySelectorAll('.tl-loop').forEach((lp) => {
    lp.classList.remove('active');
    const end = +lp.dataset.end;
    const fin = Number.isFinite(end) && idx > end; // 루프를 완전히 빠져나옴 → 완료(색 유지)
    lp.classList.toggle('done', fin);
    setLoopFill(lp, fin ? 1 : 0); // 완료=가득 / 미진입=빈 칸 (활성은 아래에서 덮어씀)
  });
  (p.loops || []).forEach((f) => {
    const lp = el.querySelector(`.tl-loop[data-start="${f.startIndex}"]`);
    if (!lp) return;
    lp.classList.add('active'); lp.classList.remove('done'); // 되돌아온(재진입) 루프는 다시 진행 중
    const total = f.total || 1; setLoopFill(lp, (total - f.remaining + 1) / total); // 색 채움 = 현재/전체 (숫자 없음)
  });
  let shape = []; try { shape = JSON.parse(el.dataset.shape || '[]'); } catch { /* 손상 시 빈 */ }
  setProgBar(el, loopAwareFraction(shape, idx, p.loops)); // 전체 진행도 녹색선 — 반복까지 반영, 뒤로 안 감
  scrollTimelineToActive(el, idx);
}
// 인디케이터가 항목 폭을 넘으면 활성 노드가 중앙~우측(60%)에 오도록 가로 스크롤 추적
function scrollTimelineToActive(el, idx) {
  const node = el.querySelector(`.tl-dot[data-i="${idx}"], .tl-line[data-i="${idx}"]`);
  if (!node) return;
  const c = el.getBoundingClientRect(), n = node.getBoundingClientRect();
  const center = (n.left + n.width / 2) - c.left; // 보이는 영역 내 활성 노드 중심 x
  if (center < c.width * 0.25 || center > c.width * 0.78) {
    el.scrollTo({ left: Math.max(0, el.scrollLeft + center - c.width * 0.6), behavior: MREDUCE ? 'auto' : 'smooth' });
  }
}
// 활성 지연 채움 애니메이션(좌측 .tl-fill + 우측 .rs-fill/.rs-elapsed) — 공유 rAF, 동시 재생별 추적.
const delayAnims = new Map(); // macroId -> { start, dur, i }
let delayRaf = 0;
function setActiveDelay(macroId, idx, delayMs) {
  if (delayMs > 0) { delayAnims.set(macroId, { start: performance.now(), dur: delayMs, i: idx }); if (!delayRaf) delayRaf = requestAnimationFrame(tickDelays); }
  else delayAnims.delete(macroId);
}
function tickDelays() {
  delayRaf = 0;
  const now = performance.now();
  delayAnims.forEach((a, macroId) => {
    const frac = a.dur > 0 ? Math.min(1, (now - a.start) / a.dur) : 1;
    const w = (frac * 100).toFixed(1) + '%';
    document.querySelectorAll(`.macro-prog[data-id="${macroId}"] .tl-line[data-i="${a.i}"] .tl-fill`)
      .forEach((lf) => { lf.style.transition = 'none'; lf.style.width = w; });
    if (runShownId === macroId) {
      const row = document.querySelector(`#run-steps .run-steps-row[data-i="${a.i}"]`);
      if (row) {
        const rf = row.querySelector('.rs-fill'); if (rf) { rf.style.transition = 'none'; rf.style.width = w; }
        const re = row.querySelector('.rs-elapsed'); if (re) re.textContent = `${Math.round(Math.min(a.dur, now - a.start))} / ${Math.round(a.dur)} ms`;
      }
    }
    if (frac >= 1) delayAnims.delete(macroId);
  });
  if (delayAnims.size) delayRaf = requestAnimationFrame(tickDelays);
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
    try {
      if (editor.current()?.id === id) editor.abandon(); // 편집 중이면 자동 저장 취소(삭제가 되살아나지 않게)
      await api.deleteMacro(id);
      await loadMacros();
    }
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
      case 'progress': queueProgress(msg.data); break;
      case 'inputDetected': if (capTrigId) onTriggerGamepad(msg.data); else editor.onInputDetected(msg.data); break;
      case 'inputMonitor': log('monitor', `[${msg.data.source}] ${msg.data.label}`, msg.data.time); break;
      case 'macrosChanged': loadMacros(); break; // 동기화로 원격 변경 반영(목록 새로고침)
      case 'syncStatus': if (!$('settings-overlay').hidden) renderSyncStatus(msg.data); break; // 동기화 진행/결과 실시간
      case 'widgets': setPinned(msg.data.ids); break; // 열린 위젯 창 목록 → 핀 버튼 상태 동기화
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
// 들어오는 progress를 macro별 최신 1건으로 합쳐 프레임당 1회만 적용 — 고속/동시 매크로 폭주로 인한 DOM 부하 방지.
// (서버도 ~60Hz로 코얼레싱하지만, 동시 재생·백그라운드 탭 등에서 추가 안전장치.)
const pendingProgress = new Map(); // macroId -> 최신 progress
let progressRaf = 0;
function queueProgress(p) {
  if (!p || p.macroId == null) return;
  pendingProgress.set(p.macroId, p); // 같은 매크로는 최신값으로 덮어씀(밀려도 누적 안 됨)
  if (!progressRaf) progressRaf = requestAnimationFrame(flushProgress);
}
function flushProgress() {
  progressRaf = 0;
  const items = [...pendingProgress.values()];
  pendingProgress.clear();
  for (const p of items) showProgress(p);
}
// 진행 인덱스 prev→idx 변화 시 '바뀐 스텝 노드만' 갱신(전체 querySelectorAll 순회 O(스텝수) 제거).
// resolve(i)=그 인덱스 노드(없으면 null). 정방향 [prev..idx-1]=done, 역방향(루프 되감기) (idx..prev]=undo, 그 후 idx=active.
function applyIndexDelta(prev, idx, resolve, done, active, undone) {
  if (idx > prev) { for (let i = Math.max(0, prev); i < idx; i++) { const n = resolve(i); if (n) done(n); } }
  else if (idx < prev) { for (let i = prev; i > idx; i--) { const n = resolve(i); if (n) undone(n); } }
  if (idx >= 0) { const n = resolve(idx); if (n) active(n); }
}
function showProgress(p) {
  // 좌측 타임라인(해당 macroId만) + 우측 패널(표시 중일 때) + 활성 지연 채움(공유 rAF)
  updateMacroTimeline(p.macroId, p);
  if (runShownId && runShownId === p.macroId) {
    lastStepIndex = p.stepIndex; lastLoops = p.loops || [];
    applyRunProgress(p.stepIndex, p.loops);
  } else if (!runShownId) {
    selectRunMacro(p.macroId); // 아무것도 안 보고 있으면 진행 중인 매크로를 표시
  }
  setActiveDelay(p.macroId, p.stepIndex, p.delayMs);
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
  $('btn-reload').onclick = () => location.reload(); // 화면 새로고침(잠금으로 키보드 단축키가 막혀 있어 버튼 제공)
  $('btn-sync-save').onclick = onSyncSave;
  $('btn-sync-now').onclick = onSyncNow;
  $('widget-color').oninput = saveWidgetConfig;
  $('widget-opacity').oninput = () => { $('widget-opacity-val').textContent = $('widget-opacity').value + '%'; saveWidgetConfig(); };
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
      if (editor.current()?.id) editor.abandon(); // 편집 중 저장된 매크로도 초기화 대상 — 자동 저장이 되살리지 않게
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
  loadPinned(); // 열려 있는 위젯 창 목록 → 핀 버튼 상태
  editor.open(null); // 기본: 새 매크로 추가 모드로 시작
  log('info', 'Y Input UI 준비됨.');
}

init();
