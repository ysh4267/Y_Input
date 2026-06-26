import { api } from './api.js';
import * as km from './keymap.js';

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

const TYPE_NAME = { keyboard: '키', mouse: '마우스', gamepad: '패드', text: '텍스트', delay: '지연' };
const fmtMs = (ms) => ms >= 1000 ? (ms / 1000).toFixed(2) + ' s' : Math.round(ms) + ' ms';

// 세그먼트 컨트롤 헬퍼
const setSeg = (el, val) => el.querySelectorAll('.seg-btn').forEach((b) => b.classList.toggle('on', b.dataset.val === val));
const getSeg = (el) => el.querySelector('.seg-btn.on')?.dataset.val ?? null;

export function createEditor({ log, onSaved, getStatus }) {
  let editing = null;
  let selected = new Set();
  let lastIdx = -1;
  let dragSrc = -1;   // 기존 스텝 재정렬 중인 인덱스(-1=없음)
  let dragType = null; // 팔레트에서 새로 끌어오는 동작 타입

  function blank() {
    return { id: '', name: '새 매크로', loopCount: 1, speedMultiplier: 1.0, randomizeDelayPercent: 0, trigger: null, steps: [] };
  }

  function open(macro) {
    editing = structuredClone(macro || blank());
    editing.steps ||= [];
    selected = new Set(); lastIdx = -1;
    $('empty-state').hidden = true;
    $('editor').hidden = false;
    $('ed-name').value = editing.name || '';

    // 반복 모드: 0=무한, 1=한 번, >1=반복
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

  function close() { open(null); } // 닫기=현재 매크로 비우고 새 매크로 모드로
  function isOpen() { return editing !== null; }
  function current() { return editing; }

  function syncLoopInput() {
    $('ed-loop').hidden = getSeg($('seg-repeat')) !== 'count';
  }

  function updateStats() {
    if (!editing) return;
    $('ed-stat-steps').textContent = editing.steps.length;
    const dur = editing.steps.reduce((a, s) => a + (s.delayBeforeMs || 0), 0);
    $('ed-stat-dur').textContent = fmtMs(dur);
    $('ed-stat-trigger').textContent = hotkeyToString(editing.trigger);
  }

  // ---------- 스텝 리스트 ----------
  function renderSteps() {
    const wrap = $('steps');
    wrap.innerHTML = '';
    if (!editing.steps.length) {
      const empty = document.createElement('div');
      empty.className = 'steps-empty';
      empty.textContent = '스텝이 없습니다 — 왼쪽 “동작”을 끌어다 놓거나 클릭해 추가하세요.';
      wrap.appendChild(empty);
    } else {
      editing.steps.forEach((step, i) => wrap.appendChild(buildRow(step, i)));
      renderPairLines();
    }
    applySel();
    updateStats();
  }

  // 키 Down↔Up 같은 키끼리 스택 매칭 → 좌측 거터에 한 세트 연결선
  function renderPairLines() {
    const gutters = [...$('steps').querySelectorAll('.step .step-pair')];
    const stack = [], pairs = [];
    editing.steps.forEach((s, i) => {
      const ev = s.event;
      if (ev['$type'] !== 'keyboard') return;
      if ((ev.state & 1) === 1) { // Up → 가장 가까운 같은 키 Down과 짝
        for (let k = stack.length - 1; k >= 0; k--) {
          if (stack[k].code === ev.code) { pairs.push({ down: stack[k].i, up: i }); stack.splice(k, 1); break; }
        }
      } else stack.push({ code: ev.code, i });
    });
    // 겹치는 짝은 레인 분리(최대 3레인)
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
      const x = 10 + p.lane * 16; // 우측 트랙: 넓은 레인 간격(git 그래프 느낌)
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
    row.dataset.i = i;

    // 드래그 핸들
    const drag = document.createElement('span');
    drag.className = 'step-drag'; drag.textContent = '⠿'; drag.draggable = true; drag.title = '드래그로 이동';
    drag.addEventListener('dragstart', (e) => { dragSrc = i; dragType = null; row.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', 'move:' + i); });
    drag.addEventListener('dragend', () => { dragSrc = -1; row.classList.remove('dragging'); });
    row.appendChild(drag);

    const num = document.createElement('span'); num.className = 'step-num'; num.textContent = i + 1; row.appendChild(num);

    // 타입(아이콘 + 이름)
    const type = document.createElement('span'); type.className = 'step-type';
    const ico = document.createElement('span'); ico.className = 'step-ico type-' + t; ico.textContent = km.TYPE_ICON[t] || '?';
    const tn = document.createElement('span'); tn.className = 'tname'; tn.textContent = TYPE_NAME[t] || t;
    type.append(ico, tn); row.appendChild(type);

    // 동작(인라인 편집)
    const detail = document.createElement('div'); detail.className = 'step-detail';
    buildDetail(detail, step); row.appendChild(detail);

    // 지연
    const dly = document.createElement('div'); dly.className = 'step-delay';
    const din = document.createElement('input'); din.type = 'number'; din.min = '0'; din.step = '1';
    din.value = Math.round(step.delayBeforeMs || 0);
    din.onchange = () => { step.delayBeforeMs = parseFloat(din.value) || 0; updateStats(); };
    const unit = document.createElement('span'); unit.className = 'unit'; unit.textContent = 'ms';
    dly.append(din, unit); row.appendChild(dly);

    // 행 액션
    const act = document.createElement('div'); act.className = 'step-act';
    const bDup = document.createElement('button'); bDup.className = 'rowbtn'; bDup.title = '복제'; bDup.textContent = '⎘';
    bDup.onclick = (e) => { e.stopPropagation(); editing.steps.splice(i + 1, 0, structuredClone(step)); selectOnly(i + 1); renderSteps(); };
    const bDel = document.createElement('button'); bDel.className = 'rowbtn del'; bDel.title = '삭제'; bDel.textContent = '✕';
    bDel.onclick = (e) => { e.stopPropagation(); editing.steps.splice(i, 1); selected.delete(i); renderSteps(); };
    act.append(bDup, bDel); row.appendChild(act);

    // 한 세트(키 Down↔Up) 연결선 트랙 — 우측. renderPairLines가 채움
    const pairCell = document.createElement('span'); pairCell.className = 'step-pair'; row.appendChild(pairCell);

    // 선택(행 클릭) — 입력/버튼/드래그핸들 클릭은 제외
    row.onclick = (e) => {
      if (e.target.closest('input,select,button,textarea,.keycap,.dir-badge,.step-drag')) return;
      onRowClick(i, e);
    };

    // 드롭 대상
    row.addEventListener('dragover', (e) => { e.preventDefault(); row.classList.add('dragover'); });
    row.addEventListener('dragleave', () => row.classList.remove('dragover'));
    row.addEventListener('drop', (e) => {
      e.preventDefault(); e.stopPropagation(); row.classList.remove('dragover');
      if (dragType) { insertTypeAt(dragType, i); dragType = null; return; } // 팔레트 → 이 행 위치에 삽입
      if (dragSrc < 0 || dragSrc === i) return;
      const [moved] = editing.steps.splice(dragSrc, 1);
      const dst = dragSrc < i ? i - 1 : i;
      editing.steps.splice(dst, 0, moved);
      selectOnly(dst); renderSteps();
    });
    return row;
  }

  function buildDetail(td, step) {
    const ev = step.event;
    const t = ev['$type'];
    if (t === 'keyboard') {
      const cap = document.createElement('button');
      cap.className = 'keycap'; cap.textContent = km.keyName(ev);
      cap.onclick = (e) => { e.stopPropagation(); captureKey(cap, ev); };
      const isUp = (ev.state & 1) === 1;
      const dir = document.createElement('span');
      dir.className = 'dir-badge ' + (isUp ? 'dir-up' : 'dir-down');
      dir.textContent = isUp ? '↑ 뗌' : '↓ 누름'; dir.title = '누름/뗌 전환';
      dir.onclick = (e) => {
        e.stopPropagation();
        const nowUp = (ev.state & 1) === 1;
        ev.state = (ev.state & 0x02) | (nowUp ? 0 : 1);
        dir.className = 'dir-badge ' + (nowUp ? 'dir-down' : 'dir-up');
        dir.textContent = nowUp ? '↓ 누름' : '↑ 뗌';
      };
      td.append(cap, dir);
    } else if (t === 'mouse') {
      const m = km.MOUSE;
      if ((ev.buttonState & m.ScrollV) || ev.rolling) {
        td.append(labelTag('휠'), numInput(ev.rolling, (v) => ev.rolling = v, '휠량(±)'));
      } else if (ev.buttonState !== 0) {
        const btn = mkSelect([['left', '좌'], ['right', '우'], ['middle', '중']], curMouseBtn(ev));
        const dir = mkSelect([['down', '누름'], ['up', '뗌']], curMouseDir(ev));
        const apply = () => { Object.assign(ev, km.mouseButtonEvent(btn.value, dir.value === 'down')); };
        btn.onchange = apply; dir.onchange = apply;
        td.append(labelTag('클릭'), btn, dir);
      } else {
        const x = numInput(ev.x, (v) => ev.x = v, 'dx');
        const y = numInput(ev.y, (v) => ev.y = v, 'dy');
        const abs = document.createElement('label'); abs.className = 'chip';
        const ac = document.createElement('input'); ac.type = 'checkbox'; ac.checked = !!(ev.flags & 1);
        ac.style.marginRight = '4px';
        ac.onchange = () => ev.flags = ac.checked ? 1 : 0;
        abs.append(ac, document.createTextNode('절대좌표'));
        td.append(labelTag('이동'), x, y, abs);
      }
    } else if (t === 'gamepad') {
      const sel = mkSelect(km.GAMEPAD_CONTROLS.map((c) => [c, c]), ev.control);
      sel.onchange = () => ev.control = sel.value;
      td.append(sel, numInput(ev.value, (v) => ev.value = v, '값'));
    } else if (t === 'text') {
      const txt = document.createElement('input'); txt.type = 'text'; txt.value = ev.text || '';
      txt.placeholder = '입력할 텍스트'; txt.onchange = () => ev.text = txt.value;
      td.append(txt, numInput(ev.perKeyDelayMs || 0, (v) => ev.perKeyDelayMs = v, '키당ms'));
    } else if (t === 'delay') {
      const span = document.createElement('span'); span.className = 'muted';
      span.textContent = '대기 — 오른쪽 지연(ms) 값만큼 멈춤';
      td.append(span);
    }
  }

  function captureKey(cap, ev) {
    cap.classList.add('capturing'); cap.textContent = '키…';
    const handler = (e) => {
      e.preventDefault();
      const info = km.scanInfo(e.code);
      if (info) { ev.code = info.scan; ev.state = (info.e0 ? 0x02 : 0) | (ev.state & 0x01); }
      cap.textContent = km.keyName(ev); cap.classList.remove('capturing');
      window.removeEventListener('keydown', handler, true);
    };
    window.addEventListener('keydown', handler, true);
  }

  // 헬퍼
  const labelTag = (txt) => { const s = document.createElement('span'); s.className = 'muted'; s.textContent = txt; return s; };
  const mkSelect = (opts, val) => {
    const s = document.createElement('select'); s.className = 'sel';
    for (const [v, label] of opts) { const o = document.createElement('option'); o.value = v; o.textContent = label; s.appendChild(o); }
    s.value = val; return s;
  };
  const numInput = (val, set, title) => {
    const n = document.createElement('input'); n.type = 'number'; n.value = val | 0; n.title = title || ''; n.placeholder = title || '';
    n.onchange = () => set(parseInt(n.value, 10) || 0); return n;
  };
  const curMouseBtn = (ev) => {
    const m = km.MOUSE;
    if (ev.buttonState & (m.RightDown | m.RightUp)) return 'right';
    if (ev.buttonState & (m.MidDown | m.MidUp)) return 'middle';
    return 'left';
  };
  const curMouseDir = (ev) => {
    const m = km.MOUSE;
    return (ev.buttonState & (m.LeftDown | m.RightDown | m.MidDown)) ? 'down' : 'up';
  };

  // ---------- 선택 ----------
  function onRowClick(i, e) {
    if (e.shiftKey && lastIdx >= 0) {
      const [a, b] = [Math.min(lastIdx, i), Math.max(lastIdx, i)];
      for (let k = a; k <= b; k++) selected.add(k);
    } else if (e.ctrlKey || e.metaKey) {
      if (selected.has(i)) selected.delete(i); else selected.add(i);
      lastIdx = i;
    } else {
      if (selected.size === 1 && selected.has(i)) selected.clear();
      else { selected = new Set([i]); }
      lastIdx = i;
    }
    applySel();
  }
  function selectOnly(i) { selected = new Set([i]); lastIdx = i; }
  function applySel() {
    document.querySelectorAll('#steps .step').forEach((el) => {
      el.classList.toggle('sel', selected.has(+el.dataset.i));
    });
    const n = selected.size;
    $('sel-bar').hidden = n === 0;
    if (n) $('sel-count').textContent = n;
  }
  const selIdxs = () => [...selected].sort((a, b) => a - b);

  // ---------- 삽입 ----------
  function makeSteps(type) {
    switch (type) {
      case 'key-press': return km.kbEventsFromKey('KeyA', 'press').map((e, i) => ({ delayBeforeMs: i ? 30 : 0, event: e }));
      case 'key-down': return [{ delayBeforeMs: 0, event: km.kbEventsFromKey('KeyA', 'down')[0] }];
      case 'key-up': return [{ delayBeforeMs: 0, event: km.kbEventsFromKey('KeyA', 'up')[0] }];
      case 'mouse-click': return [
        { delayBeforeMs: 0, event: km.mouseButtonEvent('left', true) },
        { delayBeforeMs: 30, event: km.mouseButtonEvent('left', false) }];
      case 'mouse-move': return [{ delayBeforeMs: 0, event: km.mouseMoveEvent(0, 0, false) }];
      case 'mouse-wheel': return [{ delayBeforeMs: 0, event: km.mouseWheelEvent(120) }];
      case 'delay': return [{ delayBeforeMs: 100, event: { '$type': 'delay' } }];
      case 'gamepad': return [{ delayBeforeMs: 0, event: km.gamepadEvent('A', 1) }];
      case 'text': return [{ delayBeforeMs: 0, event: { '$type': 'text', text: '', perKeyDelayMs: 0 } }];
      default: return [];
    }
  }
  function insertTypeAt(type, at) {
    const made = makeSteps(type);
    if (!made.length) return;
    editing.steps.splice(at, 0, ...made);
    selected = new Set(made.map((_, k) => at + k)); lastIdx = at;
    renderSteps();
  }
  function insert(type) {
    insertTypeAt(type, selected.size ? Math.max(...selIdxs()) + 1 : editing.steps.length);
  }

  // ---------- 일괄 작업 ----------
  function targets() { return selected.size ? selIdxs() : editing.steps.map((_, i) => i); }
  function duplicateSel() {
    const s = selIdxs(); if (!s.length) return;
    const copies = s.map((i) => structuredClone(editing.steps[i]));
    const at = Math.max(...s) + 1;
    editing.steps.splice(at, 0, ...copies);
    selected = new Set(copies.map((_, k) => at + k)); lastIdx = at;
    renderSteps();
  }
  function deleteSel() {
    const s = new Set(selected); if (!s.size) return;
    editing.steps = editing.steps.filter((_, i) => !s.has(i));
    selected = new Set(); lastIdx = -1; renderSteps();
  }
  function moveSel(dir) {
    const idxs = selIdxs(); if (!idxs.length) return;
    if (dir < 0 && idxs[0] === 0) return;
    if (dir > 0 && idxs[idxs.length - 1] === editing.steps.length - 1) return;
    const order = dir < 0 ? idxs : idxs.slice().reverse();
    for (const i of order) { const j = i + dir; [editing.steps[i], editing.steps[j]] = [editing.steps[j], editing.steps[i]]; }
    selected = new Set(idxs.map((i) => i + dir)); lastIdx = -1;
    renderSteps();
  }
  function bulkSet() {
    const ms = parseFloat(prompt('선택(없으면 전체) 스텝의 지연(ms):', '50'));
    if (isNaN(ms)) return;
    targets().forEach((i) => editing.steps[i].delayBeforeMs = ms); renderSteps();
  }
  function bulkScale() {
    const f = parseFloat(prompt('지연 배율(예: 0.5=절반, 2=두배):', '1'));
    if (isNaN(f) || f <= 0) return;
    targets().forEach((i) => editing.steps[i].delayBeforeMs = Math.round((editing.steps[i].delayBeforeMs || 0) * f));
    renderSteps();
  }

  // ---------- 저장/재생 ----------
  function collect() {
    editing.name = $('ed-name').value.trim() || '제목 없음';
    const mode = getSeg($('seg-repeat'));
    editing.loopCount = mode === 'inf' ? 0 : mode === 'once' ? 1 : (parseInt($('ed-loop').value, 10) || 1);
    editing.speedMultiplier = parseFloat($('ed-speed').value) || 1.0;
    editing.randomizeDelayPercent = parseInt($('ed-random').value, 10) || 0;
    return editing;
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
  // 동작 팔레트: 클릭=추가, 드래그=원하는 위치(행/끝)에 드롭
  document.querySelectorAll('#palette .pal-item').forEach((el) => {
    const type = el.dataset.type;
    el.onclick = () => insert(type);
    el.addEventListener('dragstart', (e) => { dragType = type; dragSrc = -1; e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', 'add:' + type); el.classList.add('dragging'); });
    el.addEventListener('dragend', () => { dragType = null; el.classList.remove('dragging'); });
  });
  // 행이 아닌 빈 영역에 드롭 → 맨 뒤에 추가/이동
  const stepsEl = $('steps');
  stepsEl.addEventListener('dragover', (e) => { if (dragType || dragSrc >= 0) e.preventDefault(); });
  stepsEl.addEventListener('drop', (e) => {
    if (e.target.closest('.step')) return; // 행에서 처리됨
    e.preventDefault();
    if (dragType) { insertTypeAt(dragType, editing.steps.length); dragType = null; }
    else if (dragSrc >= 0) { const [m] = editing.steps.splice(dragSrc, 1); editing.steps.push(m); dragSrc = -1; selectOnly(editing.steps.length - 1); renderSteps(); }
  });
  $('btn-selall').onclick = () => { selected = new Set(editing.steps.map((_, i) => i)); applySel(); };
  $('btn-dup').onclick = duplicateSel;
  $('btn-del').onclick = deleteSel;
  $('btn-up').onclick = () => moveSel(-1);
  $('btn-down').onclick = () => moveSel(+1);
  $('btn-bulk-set').onclick = bulkSet;
  $('btn-bulk-scale').onclick = bulkScale;
  $('btn-selclear').onclick = () => { selected = new Set(); applySel(); };

  // 트리거 핫키 캡처
  let capHk = false;
  const hk = $('ed-hotkey');
  const MOUSE_BTN = { 0: 'Left', 1: 'Middle', 2: 'Right', 3: 'X1', 4: 'X2' };
  hk.addEventListener('focus', () => { capHk = true; hk.value = '키 또는 마우스 버튼 입력 대기…'; });
  hk.addEventListener('blur', () => { capHk = false; hk.value = hotkeyToString(editing && editing.trigger); });
  hk.addEventListener('keydown', (e) => {
    if (!capHk) return; e.preventDefault();
    const vk = km.eventToVk(e); if (vk == null) return; // 모디파이어 단독/미지원 키는 대기 유지
    editing.trigger = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: vk, mouse: null, gamepad: null };
    hk.value = hotkeyToString(editing.trigger); updateStats(); hk.blur();
  });
  // 마우스 버튼 트리거(엄지 X1/X2 포함). 포커스용 첫 클릭은 capHk=false라 무시됨.
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

  // 서버에서 입력 감지(inputDetected) 수신 시 트리거 바인딩
  function onInputDetected(data) {
    if (!data) return;
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
