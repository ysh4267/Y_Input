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

const TYPE_NAME = { keyboard: '키', mouse: '마우스', gamepad: '패드', text: '텍스트', delay: '지연', loopStart: '반복', loopEnd: '반복' };
const fmtMs = (ms) => ms >= 1000 ? (ms / 1000).toFixed(2) + ' s' : Math.round(ms) + ' ms';
const REDUCE_MOTION = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

// 카드 액션 아이콘(복제=겹친 사각형 / 삭제=휴지통) — 또렷한 라인 아이콘
const ICON = {
  dup: '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><rect x="5.6" y="5.6" width="8.1" height="8.1" rx="1.6"/><path d="M10.4 5.4V3.1A1.6 1.6 0 0 0 8.8 1.5H3.1A1.6 1.6 0 0 0 1.5 3.1V8.8A1.6 1.6 0 0 0 3.1 10.4H5.4"/></svg>',
  del: '<svg viewBox="0 0 16 16" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.6 4.3H13.4"/><path d="M6.4 4.3V3.2A1.1 1.1 0 0 1 7.5 2.1H8.5A1.1 1.1 0 0 1 9.6 3.2V4.3"/><path d="M3.9 4.3 4.6 13A1.3 1.3 0 0 0 5.9 14.2H10.1A1.3 1.3 0 0 0 11.4 13L12.1 4.3"/><path d="M6.6 6.9V11.3M9.4 6.9V11.3"/></svg>',
};

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
  let dropLine = null;        // 팔레트 드롭 위치 표시선

  // 녹화 카드 상태(실시간 녹화)
  let recordingUid = null;    // 현재 녹화 중인 '녹화하기' 카드의 _uid
  let recBusy = false;        // 시작 처리 중 중복 방지
  let liveRec = [];           // 녹화 중 기록된 스텝(녹화 블록 '내부' 미리보기 — editing.steps 미오염)
  let liveEl = null;          // 녹화 블록 내부 스크롤 컨테이너(.rec-live)
  let tickingUid = null;      // 진행 중(측정 중) 지연 행의 _uid
  let tickStart = 0;          // 현재 지연 측정 시작(performance.now)
  let pendingServerGap = null;// 다음 입력 직전 지연(서버 측정값) — 있으면 우선 사용
  let recTickTimer = null;    // 진행 중 지연 행 실시간 갱신 타이머

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
    const real = editing.steps.filter((s) => s.event['$type'] !== 'record'); // 녹화 카드는 스텝 아님
    $('ed-stat-steps').textContent = real.length;
    const dur = real.reduce((a, s) => a + (s.event['$type'] === 'delay' ? (s.delayBeforeMs || 0) : 0), 0);
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
      empty.textContent = '왼쪽 “동작”을 여기로 끌어다 놓거나 클릭해 추가하세요';
      wrap.appendChild(empty);
    } else {
      editing.steps.forEach((step, i) => wrap.appendChild(buildRow(step, i)));
      renderPairLines();
      applyLoopStyles();
    }
    applySel();
    updateStats();
    syncRecordButtons(getStatus());
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

  // 키 Down↔Up 짝 + 반복 시작↔끝 짝 → 모두 카드 밖 '우측' ']'(뒤집어진 ㄷ) 브래킷(레인 분리)
  function renderPairLines() {
    if (draggingUids.size) return; // 드래그 중엔 생략(드롭 후 재렌더로 다시 그림)
    const wrap = stepsEl();
    const rows = [...wrap.querySelectorAll('.step')];
    const pairs = [];

    // 키 Down↔Up(같은 키 스택 매칭) — 팔레트 색 순환
    const COLORS = ['#6b8cff', '#34d399', '#c084fc', '#f0a93b', '#f472b6'];
    const kstack = [], kpairs = [];
    editing.steps.forEach((s, i) => {
      const ev = s.event;
      if (ev['$type'] !== 'keyboard') return;
      if ((ev.state & 1) === 1) {
        for (let k = kstack.length - 1; k >= 0; k--) {
          if (kstack[k].code === ev.code) { kpairs.push({ down: kstack[k].i, up: i }); kstack.splice(k, 1); break; }
        }
      } else kstack.push({ code: ev.code, i });
    });
    kpairs.sort((a, b) => a.down - b.down);
    kpairs.forEach((p, pi) => { p.color = COLORS[pi % COLORS.length]; pairs.push(p); });

    // 반복(루프) 시작↔끝 — 주황색, 같은 우측 영역
    const lstack = [];
    editing.steps.forEach((s, i) => {
      const t = s.event['$type'];
      if (t === 'loopStart') lstack.push(i);
      else if (t === 'loopEnd' && lstack.length) pairs.push({ down: lstack.pop(), up: i, color: '#fb923c' });
    });

    // 키·루프 짝을 한데 모아 우측 레인 할당(겹치면 다른 레인 → 충돌 방지)
    const all = pairs.slice().sort((a, b) => a.down - b.down);
    const laneEnds = [];
    all.forEach((p) => {
      let lane = laneEnds.findIndex((end) => p.down > end);
      if (lane === -1) { lane = laneEnds.length; laneEnds.push(p.up); } else laneEnds[lane] = p.up;
      p.lane = Math.min(lane, 5);
    });

    const hline = (x, y, w, color) => { const d = document.createElement('div'); d.className = 'pair-h'; d.style.left = x + 'px'; d.style.top = (y - 1) + 'px'; d.style.width = w + 'px'; d.style.background = color; wrap.appendChild(d); };
    const vline = (x, y, h, color) => { const d = document.createElement('div'); d.className = 'pair-v'; d.style.left = x + 'px'; d.style.top = y + 'px'; d.style.height = h + 'px'; d.style.background = color; wrap.appendChild(d); };
    const node = (x, y, color) => { const d = document.createElement('div'); d.className = 'pair-node'; d.style.left = x + 'px'; d.style.top = y + 'px'; d.style.background = color; wrap.appendChild(d); };
    all.forEach((p) => {
      const cd = rows[p.down], cu = rows[p.up];
      if (!cd || !cu) return;
      const cardRight = cd.offsetLeft + cd.offsetWidth;
      const bx = cardRight + 8 + p.lane * 14;        // 브래킷 세로선 x
      const dY = cd.offsetTop + cd.offsetHeight / 2;
      const uY = cu.offsetTop + cu.offsetHeight / 2;
      vline(bx, Math.min(dY, uY), Math.abs(uY - dY), p.color);  // 세로
      hline(cardRight, dY, bx - cardRight + 2, p.color);        // 시작(위) 가로 스텁
      hline(cardRight, uY, bx - cardRight + 2, p.color);        // 끝(아래) 가로 스텁
      node(cardRight, dY, p.color); node(cardRight, uY, p.color);
    });
  }

  function buildRow(step, i) {
    const t = step.event['$type'];
    const row = document.createElement('div');
    row.className = 'step';
    row.dataset.uid = step._uid;
    if (draggingUids.has(step._uid)) row.classList.add('dragging');

    if (t === 'record') { buildRecordCard(row, step); return row; } // 녹화하기 카드(3줄 패널 + 내부 실시간 기록)

    // 행 액션(복제/삭제) — 카드 우측 끝(아래 detail 뒤에 append)
    const act = document.createElement('div'); act.className = 'step-act';
    const bDup = document.createElement('button'); bDup.className = 'rowbtn'; bDup.title = '복제'; bDup.innerHTML = ICON.dup;
    bDup.onclick = (e) => { e.stopPropagation(); pushUndo(); const c = freshStep(step); editing.steps.splice(i + 1, 0, c); selectOnly(c._uid); renderSteps(); };
    const bDel = document.createElement('button'); bDel.className = 'rowbtn del'; bDel.title = '삭제'; bDel.innerHTML = ICON.del;
    bDel.onclick = (e) => { e.stopPropagation(); pushUndo(); selected.delete(step._uid); editing.steps.splice(i, 1); renderSteps(); };
    act.append(bDup, bDel);

    const num = document.createElement('span'); num.className = 'step-num'; num.textContent = i + 1; row.appendChild(num);

    // 타입(아이콘 + 이름)
    const type = document.createElement('span'); type.className = 'step-type';
    const ico = document.createElement('span'); ico.className = 'step-ico type-' + t; ico.textContent = km.TYPE_ICON[t] || '?';
    const tn = document.createElement('span'); tn.className = 'tname'; tn.textContent = TYPE_NAME[t] || t;
    type.append(ico, tn); row.appendChild(type);

    // 동작(인라인 편집)
    const detail = document.createElement('div'); detail.className = 'step-detail';
    buildDetail(detail, step); row.appendChild(detail);

    row.appendChild(act); // 복제/삭제 — 카드 우측 끝

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
      // 개별 휴머나이즈(±%) — 이 지연만 무작위로 흔듦
      const hz = document.createElement('input'); hz.type = 'range'; hz.min = '0'; hz.max = '90'; hz.step = '5';
      hz.className = 'rec-humanize'; hz.value = ev.randomizePercent || 0; hz.title = '휴머나이즈(±%) — 재생 시 이 지연을 무작위로 흔듦';
      const hzv = document.createElement('span'); hzv.className = 'hz-val'; hzv.textContent = (ev.randomizePercent || 0) + '%';
      hz.oninput = () => { hzv.textContent = hz.value + '%'; };
      hz.onchange = () => { pushUndo(); ev.randomizePercent = parseInt(hz.value, 10) || 0; hzv.textContent = ev.randomizePercent + '%'; };
      const hzLabel = labelTag('휴머나이즈'); hzLabel.classList.add('hz-label');
      td.append(labelTag('대기'), n, labelTag('ms'), hzLabel, hz, hzv);
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

  // ---------- 녹화하기 카드(3줄 패널: 녹화/지연/대상) ----------
  function buildRecordCard(row, step) {
    row.classList.add('record-card');
    const ev = step.event;
    ev.targets ||= { keyboard: true, mouseButtons: true, mouseMove: false, mouseWheel: true, gamepad: false };

    // 1줄: 녹화 시작/정지 + 상태 + 지속 시간(수동) + 카드 제거
    const r1 = document.createElement('div'); r1.className = 'rec-row';
    const go = document.createElement('button'); go.className = 'btn rec rec-go'; go.textContent = '● 녹화 시작';
    const status = document.createElement('span'); status.className = 'rec-status rec-go-status';
    go.onclick = (e) => {
      e.stopPropagation();
      if (recordingUid === step._uid) stopRecord();
      else startRecord(step);
    };
    const hint = document.createElement('span'); hint.className = 'muted'; hint.textContent = '이 블록 안에 실시간으로 기록 — 종료 버튼으로 끝내기';
    const spacer = document.createElement('span'); spacer.className = 'rec-spacer';
    const del = document.createElement('button'); del.className = 'rowbtn del'; del.title = '녹화 카드 제거'; del.innerHTML = ICON.del;
    del.onclick = (e) => {
      e.stopPropagation();
      if (recordingUid === step._uid) { log('warn', '녹화 중에는 카드를 제거할 수 없습니다.'); return; }
      pushUndo(); selected.delete(step._uid); editing.steps.splice(idxOfUid(step._uid), 1); renderSteps();
    };
    r1.append(go, status, hint, spacer, del);

    // 2줄: 지연(실제/고정/없음) + 고정 ms
    const r2 = document.createElement('div'); r2.className = 'rec-row';
    const fixed = document.createElement('input'); fixed.type = 'number'; fixed.min = '0'; fixed.step = '10'; fixed.className = 'mini rec-fixed';
    fixed.value = Math.max(0, parseInt(ev.fixedMs, 10) || 50); fixed.title = '고정 지연(ms)';
    const fixedUnit = labelTag('ms');
    const setFixedVis = () => { const on = (ev.delayMode || 'record') === 'fixed'; fixed.hidden = !on; fixedUnit.hidden = !on; };
    fixed.onchange = () => { ev.fixedMs = Math.max(0, parseInt(fixed.value, 10) || 0); fixed.value = ev.fixedMs; };
    const seg = document.createElement('div'); seg.className = 'seg rec-delay';
    [['record', '실제'], ['fixed', '고정'], ['none', '없음']].forEach(([val, txt]) => {
      const b = document.createElement('button'); b.className = 'seg-btn' + ((ev.delayMode || 'record') === val ? ' on' : ''); b.textContent = txt; b.dataset.val = val;
      b.onclick = (e) => { e.stopPropagation(); ev.delayMode = val; seg.querySelectorAll('.seg-btn').forEach((x) => x.classList.toggle('on', x.dataset.val === val)); setFixedVis(); };
      seg.appendChild(b);
    });
    setFixedVis();
    r2.append(labelTag('지연'), seg, fixed, fixedUnit);

    // 3줄: 기록 대상 + 카운트다운
    const r3 = document.createElement('div'); r3.className = 'rec-row';
    const chips = document.createElement('div'); chips.className = 'chips rec-targets';
    [['keyboard', '⌨ 키보드'], ['mouseButtons', '🖱 클릭'], ['mouseMove', '↔ 이동'], ['mouseWheel', '⊙ 휠'], ['gamepad', '🎮 패드']].forEach(([key, txt]) => {
      const c = document.createElement('button'); c.className = 'chip' + (ev.targets[key] ? ' on' : ''); c.textContent = txt;
      c.onclick = (e) => { e.stopPropagation(); ev.targets[key] = !ev.targets[key]; c.classList.toggle('on', ev.targets[key]); };
      chips.appendChild(c);
    });
    r3.append(labelTag('기록 대상'), chips);

    row.append(r1, r2, r3);

    // 4줄: 녹화 중에만 — 블록 내부 실시간 기록(스크롤, 마지막 ~5줄)
    if (recordingUid === step._uid) {
      const live = document.createElement('div'); live.className = 'rec-live';
      liveRec.forEach((s) => live.appendChild(liveRowEl(s)));
      row.append(live);
      liveEl = live;
      requestAnimationFrame(() => { live.scrollTop = live.scrollHeight; });
    }
  }

  // 블록 내부 실시간 기록 한 줄(아이콘 + 요약)
  function liveSummary(step) {
    const ev = step.event;
    if (ev['$type'] === 'delay') return (step._uid === tickingUid ? '지연 측정 중 ' : '지연 ') + fmtMs(step.delayBeforeMs || 0);
    return km.summarizeEvent(ev);
  }
  function liveRowEl(step) {
    const t = step.event['$type'];
    const r = document.createElement('div'); r.className = 'rec-live-row'; r.dataset.uid = step._uid;
    if (step._uid === tickingUid) r.classList.add('rec-live-ticking');
    const ico = document.createElement('span'); ico.className = 'rec-live-ico type-' + t; ico.textContent = km.TYPE_ICON[t] || '•';
    const label = document.createElement('span'); label.className = 'rec-live-label'; label.textContent = liveSummary(step);
    r.append(ico, label);
    return r;
  }

  function recOptionsOf(ev) {
    const fixedDelayMs = ev.delayMode === 'fixed' ? (ev.fixedMs || 0) : ev.delayMode === 'none' ? 0 : null;
    const t = ev.targets || {};
    return { keyboard: !!t.keyboard, mouseButtons: !!t.mouseButtons, mouseMove: !!t.mouseMove, mouseWheel: !!t.mouseWheel, gamepad: !!t.gamepad, fixedDelayMs };
  }
  // 새 '측정 중' 지연 행을 블록 내부에 추가하고 실시간 타이머로 흐른 시간 표시
  function startTickingDelay() {
    const d = tagUids([{ delayBeforeMs: 0, event: { '$type': 'delay', randomizePercent: 0 } }])[0];
    liveRec.push(d); tickingUid = d._uid; tickStart = performance.now();
    if (liveEl) { liveEl.appendChild(liveRowEl(d)); liveEl.scrollTop = liveEl.scrollHeight; }
  }
  // 100ms마다 진행 중 지연 행의 흐른 시간 갱신(텍스트만)
  function tickDelay() {
    if (tickingUid == null) return;
    const s = liveRec.find((x) => x._uid === tickingUid); if (!s) return;
    s.delayBeforeMs = Math.max(0, performance.now() - tickStart);
    if (liveEl) { const lbl = liveEl.querySelector('.rec-live-ticking .rec-live-label'); if (lbl) lbl.textContent = liveSummary(s); }
  }
  async function startRecord(step) {
    if (recBusy) return;
    const st = getStatus();
    if (st && st.state !== 'idle') { log('warn', '녹화/재생 중에는 새 녹화를 시작할 수 없습니다.'); return; }
    recBusy = true;
    try {
      await api.recordStart(recOptionsOf(step.event));
      recordingUid = step._uid;
      liveRec = []; pendingServerGap = null; tickingUid = null;
      renderSteps(); // 녹화 카드에 빈 '실시간 기록' 컨테이너(liveEl) 생성
      log('info', '녹화 시작 — 블록 안에 실시간으로 기록합니다. 종료 버튼으로 끝내세요.');
      startTickingDelay();
      if (!recTickTimer) recTickTimer = setInterval(tickDelay, 100);
      syncRecordButtons(getStatus());
    } catch (e) { log('error', e.message); recordingUid = null; }
    finally { recBusy = false; }
  }
  // 서버 실시간 스텝 수신: 지연 스텝은 다음 입력 '직전 지연' 값으로 보관, 입력 스텝은 블록 내부에 추가
  function onRecordedStep(data) {
    if (recordingUid == null || !data || !data.event) return;
    if (data.event['$type'] === 'delay') { pendingServerGap = data.delayBeforeMs || 0; return; }
    // 진행 중 지연 행 확정(서버 측정값 우선, 없으면 클라 측정 — 첫 입력 직전 지연)
    if (tickingUid != null) {
      const s = liveRec.find((x) => x._uid === tickingUid);
      const gap = pendingServerGap != null ? pendingServerGap : Math.max(0, performance.now() - tickStart);
      if (s) s.delayBeforeMs = gap;
      const doneUid = tickingUid; tickingUid = null;
      if (liveEl && s) {
        const r = liveEl.querySelector(`.rec-live-row[data-uid="${doneUid}"]`);
        if (r) { r.classList.remove('rec-live-ticking'); const lbl = r.querySelector('.rec-live-label'); if (lbl) lbl.textContent = liveSummary(s); }
      }
    }
    pendingServerGap = null;
    const inp = tagUids([{ delayBeforeMs: 0, event: data.event }])[0];
    liveRec.push(inp);
    if (liveEl) liveEl.appendChild(liveRowEl(inp));
    startTickingDelay(); // 다음 측정용 지연 행
  }
  // 녹화 종료 버튼 클릭(=마지막 왼쪽 클릭)과 그 직전 지연은 결과에서 제외
  function stripTrailingStopClick(region) {
    const LEFT = km.MOUSE.LeftDown | km.MOUSE.LeftUp;
    const isLeft = (s) => s.event['$type'] === 'mouse' && ((s.event.buttonState || 0) & LEFT) !== 0;
    const isDelay = (s) => s.event['$type'] === 'delay';
    const arr = region.slice();
    let btn = 0;
    while (arr.length && btn < 2) {
      const last = arr[arr.length - 1];
      if (isDelay(last)) { arr.pop(); continue; }
      if (isLeft(last)) { arr.pop(); btn++; continue; }
      break;
    }
    while (arr.length && isDelay(arr[arr.length - 1])) arr.pop();
    return arr;
  }
  async function stopRecord() {
    if (recTickTimer) { clearInterval(recTickTimer); recTickTimer = null; }
    const cardUid = recordingUid; recordingUid = null; // 이후 도착하는 종료 클릭 이벤트는 무시
    const tick = tickingUid; tickingUid = null;
    try { await api.recordStop('', false); } catch (e) { log('error', e.message); }
    // 진행 중(측정 중) 지연 행 제외 + 종료 클릭/직전 지연 정리
    const region = liveRec.filter((s) => s._uid !== tick);
    liveRec = []; liveEl = null; pendingServerGap = null;
    const stripped = stripTrailingStopClick(region);
    const ci = cardUid != null ? idxOfUid(cardUid) : -1;
    if (ci < 0) { renderSteps(); return; }
    pushUndo();
    editing.steps.splice(ci, 1, ...stripped); // 녹화 카드 자리에 결과 삽입(카드 대체)
    selected = new Set(stripped.map((s) => s._uid)); lastUid = stripped.length ? stripped[0]._uid : null;
    renderSteps();
    log('info', `녹화 완료: ${stripped.length} 스텝`);
  }
  function syncRecordButtons(st) {
    const playing = !!(st && st.state === 'playing');
    stepsEl().querySelectorAll('.step.record-card').forEach((row) => {
      const uid = +row.dataset.uid;
      const go = row.querySelector('.rec-go'); if (!go) return;
      const isMe = recordingUid === uid; // 우리 상태 기준(상태 브로드캐스트 지연과 무관)
      go.textContent = isMe ? '■ 녹화 종료' : '● 녹화 시작';
      go.classList.toggle('active', isMe);
      go.disabled = playing || (recordingUid != null && !isMe);
      const status = row.querySelector('.rec-go-status');
      if (status) status.textContent = isMe ? '기록 중…' : '';
    });
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
      case 'delay': return [{ delayBeforeMs: 100, event: { '$type': 'delay', randomizePercent: 0 } }];
      case 'text': return [{ delayBeforeMs: 0, event: { '$type': 'text', text: '', perKeyDelayMs: 0 } }];
      case 'loop': return [
        { delayBeforeMs: 0, event: km.loopStartEvent(2) },
        { delayBeforeMs: 0, event: km.loopEndEvent() }];
      case 'record': return [{ delayBeforeMs: 0, event: { '$type': 'record', delayMode: 'record', fixedMs: 50, targets: { keyboard: true, mouseButtons: true, mouseMove: false, mouseWheel: true, gamepad: false } } }];
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
  // 좌측 ‘지연’ 팔레트와 동일한 지연 블록을 선택 위(before)/아래(after)에 추가
  function addDelay(after) {
    const idxs = selIdxs(); if (!idxs.length) return;
    pushUndo();
    const at = after ? Math.max(...idxs) + 1 : Math.min(...idxs);
    const d = tagUids(makeSteps('delay'))[0];
    editing.steps.splice(at, 0, d);
    selectOnly(d._uid);
    renderSteps();
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
  // 요소(엘리먼트)별 FLIP — 드래그 placeholder 슬라이드용
  function captureRectsOf(els) { const m = new Map(); els.forEach((el) => m.set(el, el.getBoundingClientRect())); return m; }
  function playFlipOf(before) {
    if (REDUCE_MOTION) return;
    before.forEach((old, el) => {
      if (!el.isConnected) return;
      const now = el.getBoundingClientRect();
      const dy = old.top - now.top;
      if (!dy) return;
      el.style.transition = 'none'; el.style.transform = `translateY(${dy}px)`;
      requestAnimationFrame(() => { el.style.transition = 'transform .16s ease'; el.style.transform = ''; setTimeout(() => { el.style.transition = ''; el.style.transform = ''; }, 180); });
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
      const r = rowEl.getBoundingClientRect();
      drag = { startX: e.clientX, startY: e.clientY, uids, anchorUid: uid, anchorEl: rowEl, offX: e.clientX - r.left, offY: e.clientY - r.top, moving: false };
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
      beginDrag();
    }
    drag.ghost.style.left = (e.clientX - drag.offX) + 'px';   // 드래그 블록이 포인터에 붙어 따라옴
    drag.ghost.style.top = (e.clientY - drag.offY) + 'px';
    updatePlaceholder();
  }
  function onPointerUp(e) {
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    const d = drag; drag = null;
    if (!d) return;
    if (!d.moving) { onRowClick(d.anchorUid, e); return; }
    finishDrag(d);
  }
  function clearBrackets() { stepsEl().querySelectorAll('.pair-h,.pair-v,.pair-node').forEach((el) => el.remove()); }
  function beginDrag() {
    drag.moving = true;
    pushUndo();
    draggingUids = new Set(drag.uids);
    clearBrackets();
    const wrap = stepsEl();
    const els = drag.uids.map((u) => wrap.querySelector(`.step[data-uid="${u}"]`)).filter(Boolean);
    drag.draggedEls = els;
    const ph = document.createElement('div'); ph.className = 'step-placeholder';
    ph.style.height = (els.reduce((a, el) => a + el.offsetHeight, 0) + (els.length - 1) * 8) + 'px';
    ph.style.width = els[0].offsetWidth + 'px';
    wrap.insertBefore(ph, els[0]); drag.placeholder = ph;
    const ghost = drag.anchorEl.cloneNode(true);
    ghost.classList.add('step-ghost'); ghost.classList.remove('sel');
    ghost.style.width = drag.anchorEl.offsetWidth + 'px';
    if (drag.uids.length > 1) {
      ghost.classList.add('multi'); // 뒤로 2장 더 겹쳐 사선 스택
      const b = document.createElement('div'); b.className = 'ghost-count'; b.textContent = drag.uids.length + '개'; ghost.appendChild(b);
    }
    document.body.appendChild(ghost); drag.ghost = ghost;
    els.forEach((el) => { el.style.display = 'none'; });
  }
  const visibleCards = () => [...stepsEl().querySelectorAll('.step')].filter((el) => el.style.display !== 'none');
  function nextVisibleCard(el) {
    let n = el.nextElementSibling;
    while (n && (!n.classList || !n.classList.contains('step') || n.style.display === 'none')) n = n.nextElementSibling;
    return n;
  }
  // 드래그 중인 카드(고스트)의 '중심'이 이웃 카드 중심을 지나는 순간 자리 변경(위·아래 대칭)
  function updatePlaceholder() {
    const wrap = stepsEl();
    const cards = visibleCards();
    const g = drag.ghost.getBoundingClientRect();
    const mid = g.top + g.height / 2;
    let target = null;
    for (const c of cards) {
      const r = c.getBoundingClientRect();
      if (mid < r.top + r.height / 2) { target = c; break; }
    }
    if (nextVisibleCard(drag.placeholder) === target) return; // 이미 같은 위치면 스킵(스래싱 방지)
    const before = captureRectsOf(cards.concat(drag.placeholder));
    if (target) wrap.insertBefore(drag.placeholder, target); else wrap.appendChild(drag.placeholder);
    playFlipOf(before);
  }
  function finishDrag(d) {
    d.ghost.remove();
    const wrap = stepsEl();
    const dragged = editing.steps.filter((s) => draggingUids.has(s._uid)); // 원래 상대순서
    const order = [];
    for (const child of [...wrap.children]) {
      if (child === d.placeholder) { order.push(...dragged); continue; }
      if (child.classList && child.classList.contains('step') && child.style.display !== 'none') {
        const st = editing.steps.find((s) => s._uid === +child.dataset.uid);
        if (st) order.push(st);
      }
    }
    editing.steps = order;
    draggingUids = new Set();
    d.placeholder.remove();
    renderSteps();
  }

  // 팔레트 드롭: 우측 영역 어디든 커서 높이로 삽입 위치 계산 + 표시선
  function paletteIndexAt(clientY) {
    const cards = [...stepsEl().querySelectorAll('.step')];
    for (let i = 0; i < cards.length; i++) {
      const r = cards[i].getBoundingClientRect();
      if (clientY < r.top + r.height * 0.8) return i;
    }
    return cards.length;
  }
  function showDropLine(idx) {
    const wrap = stepsEl();
    if (!dropLine) { dropLine = document.createElement('div'); dropLine.className = 'drop-line'; }
    if (dropLine.parentElement !== wrap) wrap.appendChild(dropLine);
    const cards = [...wrap.querySelectorAll('.step')];
    let y;
    if (!cards.length) y = 18;
    else if (idx >= cards.length) { const last = cards[cards.length - 1]; y = last.offsetTop + last.offsetHeight + 4; }
    else y = cards[idx].offsetTop - 5;
    dropLine.style.top = y + 'px';
  }
  function removeDropLine() { if (dropLine) { dropLine.remove(); dropLine = null; } }

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
    // 휴머나이즈는 각 '지연' 블록(event.randomizePercent)에 개별 저장 — 전역 값 없음
    // _uid는 직렬화에서 제외, 녹화하기 카드(record)는 저장 대상 아님
    return { ...editing, steps: editing.steps.filter((s) => s.event['$type'] !== 'record').map((s) => ({ delayBeforeMs: s.delayBeforeMs || 0, event: s.event })) };
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
    syncRecordButtons(st);
  }

  // ---------- 와이어링 ----------
  $('ed-name').oninput = () => { if (editing) editing.name = $('ed-name').value; };
  $('seg-repeat').querySelectorAll('.seg-btn').forEach((b) => b.onclick = () => { setSeg($('seg-repeat'), b.dataset.val); syncLoopInput(); });
  $('ed-loop').onchange = () => { if (parseInt($('ed-loop').value, 10) < 1) $('ed-loop').value = 1; };
  $('ed-speed').oninput = () => { $('ed-speed-val').textContent = parseFloat($('ed-speed').value).toFixed(1) + 'x'; };
  $('btn-save').onclick = save;
  $('btn-cancel').onclick = close;
  $('btn-play').onclick = play;
  // 동작 팔레트: 클릭=추가, 드래그=원하는 위치(행/끝)에 드롭(HTML5)
  document.querySelectorAll('#palette .pal-item').forEach((el) => {
    const type = el.dataset.type;
    el.onclick = () => (type === 'loop' ? insertLoop() : insert(type));
    el.addEventListener('dragstart', (e) => { dragType = type; e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', 'add:' + type); el.classList.add('dragging'); });
    el.addEventListener('dragend', () => { dragType = null; el.classList.remove('dragging'); removeDropLine(); });
  });
  // 팔레트 드롭: 리스트 우측 영역 전체에서 커서 높이 위치에 삽입(드롭선 표시)
  stepsEl().addEventListener('dragover', (e) => { if (!dragType) return; e.preventDefault(); showDropLine(paletteIndexAt(e.clientY)); });
  stepsEl().addEventListener('drop', (e) => {
    if (!dragType) return;
    e.preventDefault();
    const idx = paletteIndexAt(e.clientY); removeDropLine();
    insertTypeAt(dragType, idx); dragType = null;
  });
  stepsEl().addEventListener('dragleave', (e) => { if (e.target === stepsEl()) removeDropLine(); });
  // 포인터 드래그/마퀴 — 행 어디를 잡아도(입력칸 제외) 이동, 빈 곳은 박스 선택
  stepsEl().addEventListener('pointerdown', onPointerDown);

  $('btn-selall').onclick = () => { selected = new Set(editing.steps.map((s) => s._uid)); applySel(); };
  $('btn-dup').onclick = duplicateSel;
  $('btn-del').onclick = deleteSel;
  $('btn-up').onclick = () => moveSel(-1);
  $('btn-down').onclick = () => moveSel(+1);
  $('btn-delay-before').onclick = () => addDelay(false);
  $('btn-delay-after').onclick = () => addDelay(true);
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

  return { open, close, isOpen, current, onStatus, onInputDetected, onRecordedStep };
}
