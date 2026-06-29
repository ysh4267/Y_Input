import { api } from './api.js';
import * as km from './keymap.js';
import { promptDialog } from './ui.js';

const $ = (id) => document.getElementById(id);

// 트리거 핫키 VK 매핑/표시는 keymap.js(km.eventToVk / km.vkLabel) — IME 안전(e.code 기반)
function mouseTriggerName(m) {
  return ({ Left: 'Mouse좌', Right: 'Mouse우', Middle: 'Mouse휠', X1: 'Mouse X1(엄지뒤로)', X2: 'Mouse X2(엄지앞으로)' })[m] || ('Mouse ' + m);
}
function hotkeyToString(t) {
  if (!t || (!t.virtualKey && !t.mouse && !t.gamepad)) return '없음';
  const p = []; if (t.ctrl) p.push('Ctrl'); if (t.alt) p.push('Alt');
  if (t.shift) p.push('Shift'); if (t.win) p.push('Win');
  p.push(t.gamepad ? ('Pad ' + t.gamepad) : t.mouse ? mouseTriggerName(t.mouse) : km.vkLabel(t.virtualKey));
  return p.join('+');
}

const TYPE_NAME = { keyboard: '키', mouse: '마우스', gamepad: '패드', text: '텍스트', delay: '지연', loopStart: '반복', loopEnd: '반복' };
const fmtMs = (ms) => ms >= 1000 ? (ms / 1000).toFixed(2) + ' s' : Math.round(ms) + ' ms';
const REDUCE_MOTION = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

// 세그먼트 컨트롤 헬퍼
const setSeg = (el, val) => el.querySelectorAll('.seg-btn').forEach((b) => b.classList.toggle('on', b.dataset.val === val));
const getSeg = (el) => el.querySelector('.seg-btn.on')?.dataset.val ?? null;

export function createEditor({ log, onSaved, getStatus }) {
  let editing = null;
  let selected = new Set();   // 선택된 스텝의 _uid 집합
  let lastUid = null;         // Shift 범위 선택 기준
  let dragType = null;        // 팔레트에서 끌어오는 동작 타입(HTML5 DnD)
  let uidSeq = 0;

  // 드래그(포인터)·마퀴·캡처 상태
  let drag = null;
  let marquee = null;
  let draggingUids = new Set();
  let stepCapture = null;     // 게임패드 캡처 라우팅 콜백

  // 클립보드·언두
  let clipboard = [];
  const undoStack = [], redoStack = [];

  const stepsEl = () => $('steps');
  const newUid = () => ++uidSeq;
  const ensureUids = () => editing.steps.forEach((s) => { if (s._uid == null) s._uid = newUid(); });
  const idxOfUid = (uid) => editing.steps.findIndex((s) => s._uid === uid);
  const selIdxs = () => editing.steps.map((s, i) => (selected.has(s._uid) ? i : -1)).filter((i) => i >= 0);
  const freshStep = (s) => ({ delayBeforeMs: s.delayBeforeMs || 0, event: structuredClone(s.event), _uid: newUid() });
  const tagUids = (arr) => arr.map((s) => ({ ...s, _uid: newUid() }));

  function blank() {
    return { id: '', name: '새 매크로', loopCount: 1, speedMultiplier: 1.0, randomizeDelayPercent: 0, trigger: null, steps: [] };
  }

  function open(macro) {
    editing = structuredClone(macro || blank());
    editing.steps ||= [];
    ensureUids();
    selected = new Set(); lastUid = null;
    undoStack.length = 0; redoStack.length = 0;
    $('empty-state').hidden = true;
    $('editor').hidden = false;
    $('ed-name').value = editing.name || '';

    const lc = editing.loopCount || 0;
    const mode = lc <= 0 ? 'inf' : lc === 1 ? 'once' : 'count';
    setSeg($('seg-repeat'), mode);
    $('ed-loop').value = lc > 1 ? lc : 1;
    syncLoopInput();

    $('ed-speed').value = editing.speedMultiplier || 1;
    $('ed-speed-val').textContent = (editing.speedMultiplier || 1).toFixed(1) + 'x';
    $('ed-random').value = editing.randomizeDelayPercent || 0;
    $('ed-random-val').textContent = (editing.randomizeDelayPercent || 0) + '%';
    $('ed-hotkey').value = hotkeyToString(editing.trigger);
    renderSteps();
    onStatus(getStatus());
  }

  function close() { open(null); } // 닫기=현재 비우고 새 매크로 모드
  function isOpen() { return editing !== null; }
  function current() { return editing; }

  function syncLoopInput() { $('ed-loop').hidden = getSeg($('seg-repeat')) !== 'count'; }

  function updateStats() {
    if (!editing) return;
    $('ed-stat-steps').textContent = editing.steps.length;
    const dur = editing.steps.reduce((a, s) => a + (s.event['$type'] === 'delay' ? (s.delayBeforeMs || 0) : 0), 0);
    $('ed-stat-dur').textContent = fmtMs(dur);
    $('ed-stat-trigger').textContent = hotkeyToString(editing.trigger);
  }

  // ---------- 언두/리두 ----------
  function snapshot() { return { steps: structuredClone(editing.steps), sel: [...selected] }; }
  function pushUndo() {
    undoStack.push(snapshot());
    if (undoStack.length > 50) undoStack.shift();
    redoStack.length = 0;
  }
  function restore(s) { editing.steps = s.steps; selected = new Set(s.sel); lastUid = null; renderSteps(); }
  function undo() { if (!undoStack.length) return; redoStack.push(snapshot()); restore(undoStack.pop()); }
  function redo() { if (!redoStack.length) return; undoStack.push(snapshot()); restore(redoStack.pop()); }

  // ---------- 렌더 ----------
  function renderSteps() {
    const wrap = stepsEl();
    wrap.innerHTML = '';
    if (!editing.steps.length) {
      const empty = document.createElement('div');
      empty.className = 'steps-empty';
      empty.textContent = '스텝이 없습니다 — 왼쪽 “동작”을 끌어다 놓거나 클릭해 추가하세요.';
      wrap.appendChild(empty);
    } else {
      editing.steps.forEach((step, i) => wrap.appendChild(buildRow(step, i)));
      renderPairLines();
      applyLoopStyles();
    }
    applySel();
    updateStats();
  }

  function applyLoopStyles() {
    const rows = [...stepsEl().querySelectorAll('.step')];
    let depth = 0;
    editing.steps.forEach((s, i) => {
      const t = s.event['$type'];
      const row = rows[i]; if (!row) return;
      if (t === 'loopEnd') depth = Math.max(0, depth - 1);
      const marker = (t === 'loopStart' || t === 'loopEnd');
      if (marker) row.classList.add('loop-marker');
      if (marker || depth > 0) row.classList.add('in-loop');
      if (t === 'loopStart') depth += 1;
    });
  }

  // 키 Down↔Up 같은 키끼리 스택 매칭 → 우측 트랙 한 세트 연결선
  function renderPairLines() {
    const gutters = [...stepsEl().querySelectorAll('.step .step-pair')];
    const stack = [], pairs = [];
    editing.steps.forEach((s, i) => {
      const ev = s.event;
      if (ev['$type'] !== 'keyboard') return;
      if ((ev.state & 1) === 1) {
        for (let k = stack.length - 1; k >= 0; k--) {
          if (stack[k].code === ev.code) { pairs.push({ down: stack[k].i, up: i }); stack.splice(k, 1); break; }
        }
      } else stack.push({ code: ev.code, i });
    });
    pairs.sort((a, b) => a.down - b.down);
    const laneEnds = [];
    pairs.forEach((p) => {
      let lane = laneEnds.findIndex((end) => p.down > end);
      if (lane === -1) { lane = laneEnds.length; laneEnds.push(p.up); } else laneEnds[lane] = p.up;
      p.lane = Math.min(lane, 7);
    });
    const COLORS = ['#6b8cff', '#34d399', '#c084fc', '#f0a93b', '#f472b6'];
    pairs.forEach((p, pi) => {
      const color = COLORS[pi % COLORS.length];
      const x = 10 + p.lane * 16;
      for (let i = p.down; i <= p.up; i++) {
        const g = gutters[i]; if (!g) continue;
        const seg = document.createElement('div');
        seg.className = 'pair-seg'; seg.style.left = x + 'px'; seg.style.background = color;
        if (i === p.down) { seg.style.top = '50%'; seg.style.bottom = '-8px'; }
        else if (i === p.up) { seg.style.top = '-8px'; seg.style.bottom = '50%'; }
        else { seg.style.top = '-8px'; seg.style.bottom = '-8px'; }
        g.appendChild(seg);
        if (i === p.down || i === p.up) {
          const node = document.createElement('div');
          node.className = 'pair-node'; node.style.left = (x + 1) + 'px'; node.style.background = color;
          g.appendChild(node);
        }
      }
    });
  }

  function buildRow(step, i) {
    const t = step.event['$type'];
    const row = document.createElement('div');
    row.className = 'step';
    row.dataset.uid = step._uid;
    if (draggingUids.has(step._uid)) row.classList.add('dragging');

    // 행 액션(복제/삭제) — 맨 왼쪽
    const act = document.createElement('div'); act.className = 'step-act';
    const bDup = document.createElement('button'); bDup.className = 'rowbtn'; bDup.title = '복제'; bDup.textContent = '⎘';
    bDup.onclick = (e) => { e.stopPropagation(); pushUndo(); const c = freshStep(step); editing.steps.splice(i + 1, 0, c); selectOnly(c._uid); renderSteps(); };
    const bDel = document.createElement('button'); bDel.className = 'rowbtn del'; bDel.title = '삭제'; bDel.textContent = '✕';
    bDel.onclick = (e) => { e.stopPropagation(); pushUndo(); selected.delete(step._uid); editing.steps.splice(i, 1); renderSteps(); };
    act.append(bDup, bDel); row.appendChild(act);

    const num = document.createElement('span'); num.className = 'step-num'; num.textContent = i + 1; row.appendChild(num);

    // 타입(아이콘 + 이름)
    const type = document.createElement('span'); type.className = 'step-type';
    const ico = document.createElement('span'); ico.className = 'step-ico type-' + t; ico.textContent = km.TYPE_ICON[t] || '?';
    const tn = document.createElement('span'); tn.className = 'tname'; tn.textContent = TYPE_NAME[t] || t;
    type.append(ico, tn); row.appendChild(type);

    // 동작(인라인 편집)
    const detail = document.createElement('div'); detail.className = 'step-detail';
    buildDetail(detail, step); row.appendChild(detail);

    // 한 세트(키 Down↔Up) 연결선 트랙 — 우측
    const pairCell = document.createElement('span'); pairCell.className = 'step-pair'; row.appendChild(pairCell);

    // 팔레트 드롭(HTML5) — 이 행 위치에 삽입
    row.addEventListener('dragover', (e) => { if (dragType) { e.preventDefault(); row.classList.add('dragover'); } });
    row.addEventListener('dragleave', () => row.classList.remove('dragover'));
    row.addEventListener('drop', (e) => {
      row.classList.remove('dragover');
      if (!dragType) return;
      e.preventDefault(); e.stopPropagation();
      insertTypeAt(dragType, i); dragType = null;
    });
    return row;
  }

  function buildDetail(td, step) {
    const ev = step.event;
    const t = ev['$type'];
    if (km.isUniversalInput(ev)) {
      // 유니버설 입력(키/마우스버튼/패드): 캡처 버튼 + 방향 토글
      const cap = document.createElement('button');
      cap.className = 'keycap'; cap.textContent = km.inputLabel(ev);
      cap.title = '클릭 후 키·마우스 버튼·패드 버튼을 누르면 그 입력으로 지정';
      cap.onclick = (e) => { e.stopPropagation(); captureInput(step, cap); };
      const down = km.inputDirection(ev);
      const dir = document.createElement('span');
      dir.className = 'dir-badge ' + (down ? 'dir-down' : 'dir-up');
      dir.textContent = down ? '↓ 누름' : '↑ 뗌'; dir.title = '누름/뗌 전환';
      dir.onclick = (e) => {
        e.stopPropagation(); pushUndo();
        km.setInputDirection(ev, !km.inputDirection(ev));
        renderSteps();
      };
      td.append(cap, dir);
    } else if (t === 'mouse') {
      // 레거시 이동/휠(신규 생성 불가, 기존 데이터 표시용)
      const m = km.MOUSE;
      if ((ev.buttonState & m.ScrollV) || ev.rolling) {
        td.append(labelTag('휠'), numInput(ev.rolling, (v) => ev.rolling = v, '휠량(±)'));
      } else {
        const x = numInput(ev.x, (v) => ev.x = v, 'dx');
        const y = numInput(ev.y, (v) => ev.y = v, 'dy');
        const abs = document.createElement('label'); abs.className = 'chip';
        const ac = document.createElement('input'); ac.type = 'checkbox'; ac.checked = !!(ev.flags & 1);
        ac.style.marginRight = '4px';
        ac.onchange = () => { pushUndo(); ev.flags = ac.checked ? 1 : 0; };
        abs.append(ac, document.createTextNode('절대좌표'));
        td.append(labelTag('이동'), x, y, abs);
      }
    } else if (t === 'text') {
      const txt = document.createElement('input'); txt.type = 'text'; txt.value = ev.text || '';
      txt.placeholder = '입력할 텍스트'; txt.onchange = () => { pushUndo(); ev.text = txt.value; };
      td.append(txt, numInput(ev.perKeyDelayMs || 0, (v) => ev.perKeyDelayMs = v, '키당ms'));
    } else if (t === 'delay') {
      const n = document.createElement('input');
      n.type = 'number'; n.min = '0'; n.step = '1'; n.value = Math.round(step.delayBeforeMs || 0); n.title = '대기 시간(ms)';
      n.onchange = () => { pushUndo(); step.delayBeforeMs = Math.max(0, parseFloat(n.value) || 0); n.value = Math.round(step.delayBeforeMs); updateStats(); };
      td.append(labelTag('대기'), n, labelTag('ms 만큼 멈춤'));
    } else if (t === 'loopStart') {
      const n = document.createElement('input');
      n.type = 'number'; n.min = '1'; n.value = Math.max(1, ev.count || 2); n.title = '반복 횟수';
      n.onchange = () => { pushUndo(); ev.count = Math.max(1, parseInt(n.value, 10) || 1); n.value = ev.count; };
      td.append(labelTag('반복'), n, labelTag('회 — 아래 “반복 끝”까지'));
    } else if (t === 'loopEnd') {
      const span = document.createElement('span'); span.className = 'muted';
      span.textContent = '반복 끝 — 위 “반복”부터 여기까지가 한 묶음';
      td.append(span);
    }
  }

  // 유니버설 입력 캡처(키보드=keydown, 마우스=mousedown, 패드=서버 listen→inputDetected)
  function captureInput(step, cap) {
    const down = km.inputDirection(step.event);
    pushUndo();
    cap.classList.add('capturing'); cap.textContent = '입력 대기…';
    let done = false;
    const finish = (changed) => {
      if (done) return; done = true;
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('mousedown', onMouse, true);
      document.removeEventListener('contextmenu', onCtx, true);
      stepCapture = null;
      api.listenStop().catch(() => {});
      if (changed) renderSteps();
      else { cap.classList.remove('capturing'); cap.textContent = km.inputLabel(step.event); }
    };
    const onKey = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); finish(false); return; }
      const ke = km.keyEventFromCode(e.code, down);
      if (!ke) return;
      e.preventDefault();
      step.event = ke; finish(true);
    };
    const MB = { 0: 'left', 1: 'middle', 2: 'right' };
    const onMouse = (e) => {
      const btn = MB[e.button]; if (!btn) return; // X1/X2는 무시
      e.preventDefault(); e.stopPropagation();
      step.event = km.mouseButtonEvent(btn, down); finish(true);
    };
    const onCtx = (e) => e.preventDefault();
    stepCapture = (control) => { step.event = km.gamepadEvent(control, down ? 1 : 0); finish(true); };
    setTimeout(() => {
      if (done) return;
      window.addEventListener('keydown', onKey, true);
      window.addEventListener('mousedown', onMouse, true);
      document.addEventListener('contextmenu', onCtx, true);
      api.listenStart().catch(() => {});
    }, 0);
  }

  // 헬퍼
  const labelTag = (txt) => { const s = document.createElement('span'); s.className = 'muted'; s.textContent = txt; return s; };
  const numInput = (val, set, title) => {
    const n = document.createElement('input'); n.type = 'number'; n.value = val | 0; n.title = title || ''; n.placeholder = title || '';
    n.onchange = () => { pushUndo(); set(parseInt(n.value, 10) || 0); };
    return n;
  };

  // ---------- 선택 ----------
  function onRowClick(uid, e) {
    const i = idxOfUid(uid);
    if (e.shiftKey && lastUid != null) {
      const a = idxOfUid(lastUid);
      if (a >= 0) { const [lo, hi] = [Math.min(a, i), Math.max(a, i)]; for (let k = lo; k <= hi; k++) selected.add(editing.steps[k]._uid); }
    } else if (e.ctrlKey || e.metaKey) {
      if (selected.has(uid)) selected.delete(uid); else selected.add(uid);
      lastUid = uid;
    } else {
      if (selected.size === 1 && selected.has(uid)) selected.clear();
      else selected = new Set([uid]);
      lastUid = uid;
    }
    applySel();
  }
  function selectOnly(uid) { selected = new Set([uid]); lastUid = uid; }
  function applySel() {
    stepsEl().querySelectorAll('.step').forEach((el) => el.classList.toggle('sel', selected.has(+el.dataset.uid)));
    const n = selected.size;
    $('sel-bar').hidden = n === 0;
    if (n) $('sel-count').textContent = n;
  }

  // ---------- 삽입 ----------
  function makeSteps(type) {
    switch (type) {
      case 'input-down': return [{ delayBeforeMs: 0, event: km.kbEventsFromKey('KeyA', 'down')[0] }];
      case 'input-up': return [{ delayBeforeMs: 0, event: km.kbEventsFromKey('KeyA', 'up')[0] }];
      case 'delay': return [{ delayBeforeMs: 100, event: { '$type': 'delay' } }];
      case 'text': return [{ delayBeforeMs: 0, event: { '$type': 'text', text: '', perKeyDelayMs: 0 } }];
      case 'loop': return [
        { delayBeforeMs: 0, event: km.loopStartEvent(2) },
        { delayBeforeMs: 0, event: km.loopEndEvent() }];
      default: return [];
    }
  }
  function insertTypeAt(type, at) {
    const made = tagUids(makeSteps(type));
    if (!made.length) return;
    pushUndo();
    editing.steps.splice(at, 0, ...made);
    selected = new Set(made.map((s) => s._uid)); lastUid = made[0]._uid;
    renderSteps();
  }
  function insert(type) {
    insertTypeAt(type, selected.size ? Math.max(...selIdxs()) + 1 : editing.steps.length);
  }
  // 반복: 선택이 있으면 그 범위를 시작/끝 블록으로 감싸고, 없으면 빈 반복쌍을 끝에
  function insertLoop() {
    const s = selIdxs();
    pushUndo();
    if (s.length) {
      const a = s[0], b = s[s.length - 1];
      const end = tagUids([{ delayBeforeMs: 0, event: km.loopEndEvent() }])[0];
      const start = tagUids([{ delayBeforeMs: 0, event: km.loopStartEvent(2) }])[0];
      editing.steps.splice(b + 1, 0, end);
      editing.steps.splice(a, 0, start);
      selected = new Set([start._uid]); lastUid = start._uid;
      renderSteps();
    } else {
      const made = tagUids(makeSteps('loop'));
      editing.steps.push(...made);
      selected = new Set(made.map((s2) => s2._uid)); lastUid = made[0]._uid;
      renderSteps();
    }
  }

  // ---------- 일괄/이동 ----------
  function delayTargets() {
    const idxs = selected.size ? selIdxs() : editing.steps.map((_, i) => i);
    return idxs.filter((i) => editing.steps[i].event['$type'] === 'delay');
  }
  function duplicateSel() {
    const s = selIdxs(); if (!s.length) return;
    pushUndo();
    const copies = s.map((i) => freshStep(editing.steps[i]));
    const at = Math.max(...s) + 1;
    editing.steps.splice(at, 0, ...copies);
    selected = new Set(copies.map((c) => c._uid)); lastUid = copies[0]._uid;
    renderSteps();
  }
  function deleteSel() {
    if (!selected.size) return;
    pushUndo();
    editing.steps = editing.steps.filter((s) => !selected.has(s._uid));
    selected = new Set(); lastUid = null; renderSteps();
  }
  function moveSel(dir) {
    const idxs = selIdxs(); if (!idxs.length) return;
    if (dir < 0 && idxs[0] === 0) return;
    if (dir > 0 && idxs[idxs.length - 1] === editing.steps.length - 1) return;
    pushUndo();
    const before = captureRects();
    const order = dir < 0 ? idxs : idxs.slice().reverse();
    for (const i of order) { const j = i + dir; [editing.steps[i], editing.steps[j]] = [editing.steps[j], editing.steps[i]]; }
    renderSteps();
    playFlip(before);
  }
  async function bulkSet() {
    const tgt = delayTargets();
    if (!tgt.length) { log('warn', '선택에 ‘지연’ 블록이 없습니다.'); return; }
    const v = await promptDialog('지연(ms) 값 (선택한 지연 블록에 적용):', '50', { title: '지연 설정' });
    if (v === null) return;
    const ms = parseFloat(v); if (isNaN(ms)) return;
    pushUndo();
    tgt.forEach((i) => editing.steps[i].delayBeforeMs = Math.max(0, ms)); renderSteps();
  }
  async function bulkScale() {
    const tgt = delayTargets();
    if (!tgt.length) { log('warn', '선택에 ‘지연’ 블록이 없습니다.'); return; }
    const v = await promptDialog('지연 배율(예: 0.5=절반, 2=두배):', '1', { title: '지연 배율' });
    if (v === null) return;
    const f = parseFloat(v); if (isNaN(f) || f <= 0) return;
    pushUndo();
    tgt.forEach((i) => editing.steps[i].delayBeforeMs = Math.round((editing.steps[i].delayBeforeMs || 0) * f)); renderSteps();
  }

  // ---------- 클립보드 ----------
  function copySel() {
    const s = selIdxs(); if (!s.length) return;
    clipboard = s.map((i) => ({ delayBeforeMs: editing.steps[i].delayBeforeMs || 0, event: structuredClone(editing.steps[i].event) }));
    log('info', `${clipboard.length}개 복사`);
  }
  function cutSel() {
    if (!selected.size) return;
    copySel(); deleteSel();
  }
  function pasteClipboard() {
    if (!clipboard.length) return;
    pushUndo();
    const added = tagUids(clipboard.map((s) => ({ delayBeforeMs: s.delayBeforeMs || 0, event: structuredClone(s.event) })));
    editing.steps.push(...added); // 항상 맨 아래에 순서 유지
    selected = new Set(added.map((s) => s._uid)); lastUid = added[0]._uid;
    renderSteps();
    log('info', `${added.length}개 맨 아래에 붙여넣기`);
  }

  // ---------- FLIP 애니메이션 ----------
  function captureRects() {
    const m = new Map();
    stepsEl().querySelectorAll('.step').forEach((r) => m.set(+r.dataset.uid, r.getBoundingClientRect()));
    return m;
  }
  function playFlip(before) {
    if (REDUCE_MOTION || !before) return;
    stepsEl().querySelectorAll('.step').forEach((r) => {
      const old = before.get(+r.dataset.uid); if (!old) return;
      const now = r.getBoundingClientRect();
      const dy = old.top - now.top;
      if (!dy) return;
      r.style.transition = 'none';
      r.style.transform = `translateY(${dy}px)`;
      requestAnimationFrame(() => {
        r.style.transition = 'transform .16s ease';
        r.style.transform = '';
        setTimeout(() => { r.style.transition = ''; }, 180);
      });
    });
  }

  // ---------- 포인터 드래그(행 전체) + 마퀴 선택 ----------
  function onPointerDown(e) {
    if (e.button !== 0 || !editing) return;
    if (e.target.closest('input,select,button,textarea,.keycap,.dir-badge')) return;
    const rowEl = e.target.closest('.step');
    if (rowEl) {
      const uid = +rowEl.dataset.uid;
      const uids = (selected.has(uid) && selected.size > 1) ? selIdxs().map((i) => editing.steps[i]._uid) : [uid];
      drag = { startX: e.clientX, startY: e.clientY, uids, anchorUid: uid, moving: false };
      window.addEventListener('pointermove', onPointerMove);
      window.addEventListener('pointerup', onPointerUp);
    } else {
      startMarquee(e);
    }
  }
  function onPointerMove(e) {
    if (!drag) return;
    if (!drag.moving) {
      if (Math.abs(e.clientX - drag.startX) + Math.abs(e.clientY - drag.startY) < 5) return;
      drag.moving = true;
      pushUndo();
      draggingUids = new Set(drag.uids);
      stepsEl().querySelectorAll('.step').forEach((r) => r.classList.toggle('dragging', draggingUids.has(+r.dataset.uid)));
    }
    const newSteps = computeDropOrder(e.clientY, draggingUids);
    if (orderChanged(newSteps)) {
      const before = captureRects();
      editing.steps = newSteps;
      renderSteps();
      playFlip(before);
    }
  }
  function onPointerUp(e) {
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    const d = drag; drag = null;
    if (!d) return;
    if (!d.moving) { onRowClick(d.anchorUid, e); return; }
    draggingUids = new Set();
    renderSteps();
  }
  function orderChanged(arr) {
    if (arr.length !== editing.steps.length) return true;
    for (let i = 0; i < arr.length; i++) if (arr[i]._uid !== editing.steps[i]._uid) return true;
    return false;
  }
  function computeDropOrder(pointerY, dragSet) {
    const rows = [...stepsEl().querySelectorAll('.step')];
    const others = editing.steps.filter((s) => !dragSet.has(s._uid));
    const dragged = editing.steps.filter((s) => dragSet.has(s._uid));
    let insertAt = others.length, oi = 0;
    for (const row of rows) {
      const uid = +row.dataset.uid;
      if (dragSet.has(uid)) continue;
      const r = row.getBoundingClientRect();
      if (pointerY < r.top + r.height / 2) { insertAt = oi; break; }
      oi++;
    }
    return [...others.slice(0, insertAt), ...dragged, ...others.slice(insertAt)];
  }

  function startMarquee(e) {
    const add = e.ctrlKey || e.metaKey || e.shiftKey;
    if (!add) { selected = new Set(); applySel(); }
    const box = document.createElement('div'); box.className = 'marquee';
    stepsEl().appendChild(box);
    marquee = { startX: e.clientX, startY: e.clientY, box, base: new Set(selected) };
    window.addEventListener('pointermove', onMarqueeMove);
    window.addEventListener('pointerup', onMarqueeUp);
  }
  function onMarqueeMove(e) {
    if (!marquee) return;
    const wrap = stepsEl();
    const r0 = wrap.getBoundingClientRect();
    const x1 = Math.min(marquee.startX, e.clientX), x2 = Math.max(marquee.startX, e.clientX);
    const y1 = Math.min(marquee.startY, e.clientY), y2 = Math.max(marquee.startY, e.clientY);
    const b = marquee.box;
    b.style.left = (x1 - r0.left + wrap.scrollLeft) + 'px';
    b.style.top = (y1 - r0.top + wrap.scrollTop) + 'px';
    b.style.width = (x2 - x1) + 'px';
    b.style.height = (y2 - y1) + 'px';
    selected = new Set(marquee.base);
    wrap.querySelectorAll('.step').forEach((row) => {
      const r = row.getBoundingClientRect();
      if (r.bottom >= y1 && r.top <= y2) selected.add(+row.dataset.uid);
    });
    applySel();
  }
  function onMarqueeUp() {
    window.removeEventListener('pointermove', onMarqueeMove);
    window.removeEventListener('pointerup', onMarqueeUp);
    if (marquee) { marquee.box.remove(); marquee = null; }
    lastUid = null;
  }

  // ---------- 저장/재생 ----------
  function collect() {
    editing.name = $('ed-name').value.trim() || '제목 없음';
    const mode = getSeg($('seg-repeat'));
    editing.loopCount = mode === 'inf' ? 0 : mode === 'once' ? 1 : (parseInt($('ed-loop').value, 10) || 1);
    editing.speedMultiplier = parseFloat($('ed-speed').value) || 1.0;
    editing.randomizeDelayPercent = parseInt($('ed-random').value, 10) || 0;
    // _uid는 직렬화에서 제외
    return { ...editing, steps: editing.steps.map((s) => ({ delayBeforeMs: s.delayBeforeMs || 0, event: s.event })) };
  }
  async function save() {
    try {
      const m = collect();
      const saved = m.id ? await api.updateMacro(m.id, m) : await api.createMacro(m);
      editing.id = saved.id;
      log('info', `저장됨: ${saved.name}`);
      onSaved && onSaved();
      return saved;
    } catch (e) { log('error', e.message); return null; }
  }
  async function play() {
    const st = getStatus();
    if (st && st.state === 'playing' && st.currentMacroId === editing.id) {
      try { await api.stop(); } catch (e) { log('error', e.message); }
      return;
    }
    const saved = await save();
    if (!saved) return;
    try { await api.play(saved.id); } catch (e) { log('error', e.message); }
  }

  function onStatus(st) {
    if (!editing) return;
    const playing = st && st.state === 'playing' && st.currentMacroId === editing.id;
    const playBtn = $('btn-play');
    playBtn.textContent = playing ? '■ 정지' : '▶ 재생';
    playBtn.disabled = st && st.state === 'recording';
  }

  // ---------- 와이어링 ----------
  $('ed-name').oninput = () => { if (editing) editing.name = $('ed-name').value; };
  $('seg-repeat').querySelectorAll('.seg-btn').forEach((b) => b.onclick = () => { setSeg($('seg-repeat'), b.dataset.val); syncLoopInput(); });
  $('ed-loop').onchange = () => { if (parseInt($('ed-loop').value, 10) < 1) $('ed-loop').value = 1; };
  $('ed-speed').oninput = () => { $('ed-speed-val').textContent = parseFloat($('ed-speed').value).toFixed(1) + 'x'; };
  $('ed-random').oninput = () => { $('ed-random-val').textContent = $('ed-random').value + '%'; };
  $('btn-save').onclick = save;
  $('btn-cancel').onclick = close;
  $('btn-play').onclick = play;
  // 동작 팔레트: 클릭=추가, 드래그=원하는 위치(행/끝)에 드롭(HTML5)
  document.querySelectorAll('#palette .pal-item').forEach((el) => {
    const type = el.dataset.type;
    el.onclick = () => (type === 'loop' ? insertLoop() : insert(type));
    el.addEventListener('dragstart', (e) => { dragType = type; e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', 'add:' + type); el.classList.add('dragging'); });
    el.addEventListener('dragend', () => { dragType = null; el.classList.remove('dragging'); });
  });
  // 빈 영역에 팔레트 드롭 → 맨 뒤에 추가
  stepsEl().addEventListener('dragover', (e) => { if (dragType) e.preventDefault(); });
  stepsEl().addEventListener('drop', (e) => {
    if (!dragType || e.target.closest('.step')) return;
    e.preventDefault(); insertTypeAt(dragType, editing.steps.length); dragType = null;
  });
  // 포인터 드래그/마퀴 — 행 어디를 잡아도(입력칸 제외) 이동, 빈 곳은 박스 선택
  stepsEl().addEventListener('pointerdown', onPointerDown);

  $('btn-selall').onclick = () => { selected = new Set(editing.steps.map((s) => s._uid)); applySel(); };
  $('btn-dup').onclick = duplicateSel;
  $('btn-del').onclick = deleteSel;
  $('btn-up').onclick = () => moveSel(-1);
  $('btn-down').onclick = () => moveSel(+1);
  $('btn-bulk-set').onclick = bulkSet;
  $('btn-bulk-scale').onclick = bulkScale;
  $('btn-selclear').onclick = () => { selected = new Set(); applySel(); };

  // 클립보드(Ctrl+C/X/V) + 언두/리두(Ctrl+Z / Ctrl+Shift+Z·Ctrl+Y) — 입력칸 포커스 시 네이티브 양보
  document.addEventListener('keydown', (e) => {
    if (!editing || $('editor').hidden) return;
    if (e.target.closest('input,textarea,select,[contenteditable="true"]')) return;
    const ctrl = e.ctrlKey || e.metaKey;
    if (!ctrl) return;
    const k = (e.key || '').toLowerCase();
    if (k === 'c') { e.preventDefault(); copySel(); }
    else if (k === 'x') { e.preventDefault(); cutSel(); }
    else if (k === 'v') { e.preventDefault(); pasteClipboard(); }
    else if (k === 'z' && !e.shiftKey) { e.preventDefault(); undo(); }
    else if (k === 'y' || (k === 'z' && e.shiftKey)) { e.preventDefault(); redo(); }
  });

  // 트리거 핫키 캡처
  let capHk = false;
  const hk = $('ed-hotkey');
  const MOUSE_BTN = { 0: 'Left', 1: 'Middle', 2: 'Right', 3: 'X1', 4: 'X2' };
  hk.addEventListener('focus', () => { capHk = true; hk.value = '키 또는 마우스 버튼 입력 대기…'; });
  hk.addEventListener('blur', () => { capHk = false; hk.value = hotkeyToString(editing && editing.trigger); });
  hk.addEventListener('keydown', (e) => {
    if (!capHk) return; e.preventDefault();
    const vk = km.eventToVk(e); if (vk == null) return;
    editing.trigger = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: vk, mouse: null, gamepad: null };
    hk.value = hotkeyToString(editing.trigger); updateStats(); hk.blur();
  });
  hk.addEventListener('mousedown', (e) => {
    if (!capHk) return;
    const m = MOUSE_BTN[e.button];
    if (!m) return;
    e.preventDefault();
    editing.trigger = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: 0, mouse: m, gamepad: null };
    hk.value = hotkeyToString(editing.trigger); updateStats();
    setTimeout(() => hk.blur(), 0);
  });
  hk.addEventListener('contextmenu', (e) => { if (capHk) e.preventDefault(); });
  hk.addEventListener('auxclick', (e) => { if (capHk) e.preventDefault(); });
  $('ed-hotkey-clear').onclick = () => { if (editing) editing.trigger = null; hk.value = '없음'; updateStats(); };

  // 게임패드 버튼 잡기(서버 listen) — 다음 패드 버튼을 트리거로
  let padListening = false;
  $('ed-hotkey-pad').onclick = async () => {
    try {
      if (padListening) { padListening = false; await api.listenStop(); hk.value = hotkeyToString(editing && editing.trigger); return; }
      padListening = true; hk.value = '패드 버튼 대기…'; await api.listenStart();
    } catch (e) { padListening = false; log('error', e.message); }
  };

  // 서버 입력 감지(inputDetected): 스텝 캡처 중이면 그쪽으로, 아니면 트리거 바인딩
  function onInputDetected(data) {
    if (!data) return;
    if (stepCapture && data.trigger?.gamepad) { stepCapture(data.trigger.gamepad); return; }
    if (!data.bindable) { log('info', '감지: ' + data.label); return; }
    if (!editing) return;
    const t = { ctrl: false, alt: false, shift: false, win: false, virtualKey: 0, mouse: null, gamepad: null };
    if (data.trigger?.gamepad) t.gamepad = data.trigger.gamepad;
    else if (data.trigger?.virtualKey) t.virtualKey = data.trigger.virtualKey;
    else if (data.trigger?.mouse) t.mouse = data.trigger.mouse;
    editing.trigger = t;
    padListening = false;
    hk.value = hotkeyToString(t); updateStats();
    log('info', '트리거 설정: ' + hk.value);
  }

  return { open, close, isOpen, current, onStatus, onInputDetected };
}
