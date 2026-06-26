import { api } from './api.js';
import * as km from './keymap.js';

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// 트리거 핫키(Win32 VK) — 스캔코드와 별개
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
function vkName(vk) {
  if (vk >= 0x70 && vk <= 0x7B) return 'F' + (vk - 0x6F);
  if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return String.fromCharCode(vk);
  if (vk === 0x20) return 'Space'; if (vk === 0x0D) return 'Enter'; if (vk === 0x1B) return 'Esc';
  return 'VK_0x' + vk.toString(16);
}
function hotkeyToString(t) {
  if (!t || !t.virtualKey) return '(없음)';
  const p = []; if (t.ctrl) p.push('Ctrl'); if (t.alt) p.push('Alt');
  if (t.shift) p.push('Shift'); if (t.win) p.push('Win'); p.push(vkName(t.virtualKey));
  return p.join('+');
}

export function createEditor({ log, onSaved, getStatus }) {
  let editing = null;
  let dragSrc = -1;
  let lastChecked = -1;

  function blank() {
    return { id: '', name: '새 매크로', loopCount: 1, speedMultiplier: 1.0, randomizeDelayPercent: 0, trigger: null, steps: [] };
  }

  function open(macro) {
    editing = structuredClone(macro || blank());
    editing.steps ||= [];
    $('empty-state').hidden = true;
    $('editor').hidden = false;
    $('ed-name').value = editing.name || '';
    const inf = (editing.loopCount || 0) <= 0;
    $('ed-loop-inf').checked = inf;
    $('ed-loop').value = inf ? 1 : editing.loopCount;
    $('ed-loop').disabled = inf;
    $('ed-speed').value = editing.speedMultiplier || 1;
    $('ed-speed-val').textContent = (editing.speedMultiplier || 1).toFixed(1) + 'x';
    $('ed-random').value = editing.randomizeDelayPercent || 0;
    $('ed-random-val').textContent = (editing.randomizeDelayPercent || 0) + '%';
    $('ed-hotkey').value = hotkeyToString(editing.trigger);
    renderGrid();
    onStatus(getStatus());
  }

  function close() { editing = null; $('editor').hidden = true; $('empty-state').hidden = false; }
  function isOpen() { return editing !== null; }
  function current() { return editing; }

  // ---------- 그리드 ----------
  function renderGrid() {
    const body = $('grid-body');
    body.innerHTML = '';
    $('ed-step-count').textContent = editing.steps.length;
    $('grid-empty').hidden = editing.steps.length > 0;
    editing.steps.forEach((step, i) => body.appendChild(buildRow(step, i)));
    $('chk-all').checked = false;
  }

  function buildRow(step, i) {
    const tr = document.createElement('tr');
    tr.draggable = true;
    tr.dataset.i = i;

    // 선택 체크박스
    const tdChk = document.createElement('td');
    const chk = document.createElement('input'); chk.type = 'checkbox'; chk.className = 'rowchk';
    chk.onclick = (e) => {
      if (e.shiftKey && lastChecked >= 0) {
        const [a, b] = [Math.min(lastChecked, i), Math.max(lastChecked, i)];
        body_rowchecks().forEach((c, idx) => { if (idx >= a && idx <= b) c.checked = chk.checked; });
      }
      lastChecked = i;
      syncRowSel();
    };
    tdChk.appendChild(chk); tr.appendChild(tdChk);

    const tdIdx = document.createElement('td'); tdIdx.className = 'c-idx'; tdIdx.textContent = i + 1; tr.appendChild(tdIdx);

    const tdType = document.createElement('td');
    tdType.className = 'type-cell'; tdType.title = '드래그로 이동';
    tdType.textContent = km.TYPE_ICON[step.event['$type']] || '?';
    tr.appendChild(tdType);

    const tdDetail = document.createElement('td');
    tdDetail.className = 'detail-cell';
    buildDetail(tdDetail, step);
    tr.appendChild(tdDetail);

    const tdDelay = document.createElement('td');
    const delay = document.createElement('input');
    delay.type = 'number'; delay.min = '0'; delay.step = '1'; delay.className = 'delay-inp';
    delay.value = Math.round(step.delayBeforeMs || 0);
    delay.onchange = () => { step.delayBeforeMs = parseFloat(delay.value) || 0; };
    tdDelay.appendChild(delay); tr.appendChild(tdDelay);

    const tdAct = document.createElement('td');
    tdAct.innerHTML = `<button class="rowbtn up" title="위로">↑</button>
      <button class="rowbtn down" title="아래로">↓</button>
      <button class="rowbtn dup" title="복제">⎘</button>
      <button class="rowbtn del" title="삭제">✕</button>`;
    tdAct.querySelector('.up').onclick = () => move(i, -1);
    tdAct.querySelector('.down').onclick = () => move(i, +1);
    tdAct.querySelector('.dup').onclick = () => { editing.steps.splice(i + 1, 0, structuredClone(step)); renderGrid(); };
    tdAct.querySelector('.del').onclick = () => { editing.steps.splice(i, 1); renderGrid(); };
    tr.appendChild(tdAct);

    // 드래그 재정렬
    tr.addEventListener('dragstart', () => { dragSrc = i; });
    tr.addEventListener('dragover', (e) => { e.preventDefault(); tr.classList.add('dragover'); });
    tr.addEventListener('dragleave', () => tr.classList.remove('dragover'));
    tr.addEventListener('drop', (e) => {
      e.preventDefault(); tr.classList.remove('dragover');
      if (dragSrc < 0 || dragSrc === i) return;
      const [moved] = editing.steps.splice(dragSrc, 1);
      editing.steps.splice(i, 0, moved);
      dragSrc = -1; renderGrid();
    });
    return tr;
  }

  function buildDetail(td, step) {
    const ev = step.event;
    const t = ev['$type'];
    if (t === 'keyboard') {
      const cap = document.createElement('button');
      cap.className = 'keycap'; cap.textContent = km.keyName(ev);
      cap.onclick = () => captureKey(cap, ev);
      const dir = mkSelect([['0', '누름 ↓'], ['1', '뗌 ↑']], (ev.state & 1).toString());
      dir.onchange = () => { ev.state = (ev.state & 0x02) | (dir.value === '1' ? 1 : 0); };
      td.append(cap, dir);
    } else if (t === 'mouse') {
      const kind = km.MOUSE; // bits
      if ((ev.buttonState & kind.ScrollV) || ev.rolling) {
        td.append(numInput(ev.rolling, (v) => ev.rolling = v, '휠량'));
      } else if (ev.buttonState !== 0) {
        const btn = mkSelect([['left', '좌'], ['right', '우'], ['middle', '중']], curMouseBtn(ev));
        const dir = mkSelect([['down', '누름'], ['up', '뗌']], curMouseDir(ev));
        const apply = () => { Object.assign(ev, km.mouseButtonEvent(btn.value, dir.value === 'down')); };
        btn.onchange = apply; dir.onchange = apply;
        td.append(btn, dir);
      } else {
        const x = numInput(ev.x, (v) => ev.x = v, 'dx');
        const y = numInput(ev.y, (v) => ev.y = v, 'dy');
        const abs = document.createElement('label'); abs.className = 'chk';
        const ac = document.createElement('input'); ac.type = 'checkbox'; ac.checked = !!(ev.flags & 1);
        ac.onchange = () => ev.flags = ac.checked ? 1 : 0;
        abs.append(ac, document.createTextNode(' 절대'));
        td.append(x, y, abs);
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
      td.append(document.createTextNode('대기 — 지연(ms) 열 사용'));
    }
  }

  function captureKey(cap, ev) {
    cap.classList.add('capturing'); cap.textContent = '키 입력…';
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
  const mkSelect = (opts, val) => {
    const s = document.createElement('select'); s.className = 'sel';
    for (const [v, label] of opts) { const o = document.createElement('option'); o.value = v; o.textContent = label; s.appendChild(o); }
    s.value = val; return s;
  };
  const numInput = (val, set, title) => {
    const n = document.createElement('input'); n.type = 'number'; n.value = val | 0; n.title = title || '';
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
  const body_rowchecks = () => Array.from(document.querySelectorAll('#grid-body .rowchk'));
  const getSelected = () => body_rowchecks().map((c, i) => c.checked ? i : -1).filter((i) => i >= 0);
  function syncRowSel() {
    body_rowchecks().forEach((c) => c.closest('tr').classList.toggle('sel', c.checked));
  }

  function move(i, dir) {
    const j = i + dir;
    if (j < 0 || j >= editing.steps.length) return;
    [editing.steps[i], editing.steps[j]] = [editing.steps[j], editing.steps[i]];
    renderGrid();
  }

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
  function insert(type) {
    const sel = getSelected();
    const at = sel.length ? Math.max(...sel) + 1 : editing.steps.length;
    editing.steps.splice(at, 0, ...makeSteps(type));
    renderGrid();
  }

  // ---------- 일괄 작업 ----------
  function targets() { const s = getSelected(); return s.length ? s : editing.steps.map((_, i) => i); }
  function duplicateSel() {
    const s = getSelected(); if (!s.length) return;
    const copies = s.map((i) => structuredClone(editing.steps[i]));
    editing.steps.splice(Math.max(...s) + 1, 0, ...copies); renderGrid();
  }
  function deleteSel() {
    const s = new Set(getSelected()); if (!s.size) return;
    editing.steps = editing.steps.filter((_, i) => !s.has(i)); renderGrid();
  }
  function moveSel(dir) {
    const s = getSelected(); if (!s.length) return;
    const order = dir < 0 ? s : s.slice().reverse();
    for (const i of order) move(i, dir);
  }
  function bulkSet() {
    const ms = parseFloat(prompt('선택(없으면 전체) 스텝의 지연(ms):', '50'));
    if (isNaN(ms)) return;
    targets().forEach((i) => editing.steps[i].delayBeforeMs = ms); renderGrid();
  }
  function bulkScale() {
    const f = parseFloat(prompt('지연 배율(예: 0.5=절반, 2=두배):', '1'));
    if (isNaN(f) || f <= 0) return;
    targets().forEach((i) => editing.steps[i].delayBeforeMs = Math.round((editing.steps[i].delayBeforeMs || 0) * f));
    renderGrid();
  }

  // ---------- 저장/재생 ----------
  function collect() {
    editing.name = $('ed-name').value.trim() || '제목 없음';
    editing.loopCount = $('ed-loop-inf').checked ? 0 : (parseInt($('ed-loop').value, 10) || 1);
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
  $('ed-loop-inf').onchange = () => { $('ed-loop').disabled = $('ed-loop-inf').checked; };
  $('ed-speed').oninput = () => { $('ed-speed-val').textContent = parseFloat($('ed-speed').value).toFixed(1) + 'x'; };
  $('ed-random').oninput = () => { $('ed-random-val').textContent = $('ed-random').value + '%'; };
  $('btn-save').onclick = save;
  $('btn-cancel').onclick = close;
  $('btn-play').onclick = play;
  $('btn-insert').onclick = () => insert($('insert-type').value);
  $('btn-dup').onclick = duplicateSel;
  $('btn-del').onclick = deleteSel;
  $('btn-up').onclick = () => moveSel(-1);
  $('btn-down').onclick = () => moveSel(+1);
  $('btn-selall').onclick = () => { const all = body_rowchecks(); const on = !all.every((c) => c.checked); all.forEach((c) => c.checked = on); syncRowSel(); };
  $('btn-bulk-set').onclick = bulkSet;
  $('btn-bulk-scale').onclick = bulkScale;
  $('chk-all').onchange = () => { body_rowchecks().forEach((c) => c.checked = $('chk-all').checked); syncRowSel(); };

  // 트리거 핫키 캡처
  let capHk = false;
  const hk = $('ed-hotkey');
  hk.addEventListener('focus', () => { capHk = true; hk.value = '키 입력 대기…'; });
  hk.addEventListener('blur', () => { capHk = false; hk.value = hotkeyToString(editing && editing.trigger); });
  hk.addEventListener('keydown', (e) => {
    if (!capHk) return; e.preventDefault();
    const vk = eventToVk(e); if (vk == null) return;
    editing.trigger = { ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, win: e.metaKey, virtualKey: vk };
    hk.value = hotkeyToString(editing.trigger); hk.blur();
  });
  $('ed-hotkey-clear').onclick = () => { if (editing) editing.trigger = null; hk.value = '(없음)'; };

  return { open, close, isOpen, current, onStatus };
}
