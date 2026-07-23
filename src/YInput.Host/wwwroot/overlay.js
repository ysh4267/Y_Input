// 인게임 오버레이 페이지. 현재 활성화(트리거 켜짐)된 매크로를 전부 세로로 쌓아 2중 원 + 이름/루프로 보여준다.
// 재생 중이 아니어도 표시하고(원 비어 있음/대기), 재생되면 그 매크로 원이 채워진다.
// 창(네이티브)은 클릭 통과·투명·게임 추적(왼쪽 중앙 고정)을 담당하고, 이 페이지는 "표시 의도
// (overlay:show/hide)"와 "콘텐츠 크기(size:WxH)"만 네이티브에 알린다.
const $ = (id) => document.getElementById(id);
const clamp01 = (v) => Math.max(0, Math.min(1, v));
const toNative = (m) => { try { window.chrome.webview.postMessage(m); } catch { /* WebView2 밖 */ } };

const rowsEl = $('rows');

let settings = { enabled: true };
let macroList = [];                   // /api/macros 전체
let enabledIds = [];                  // 활성화된 매크로 id(order 정렬)
const macroMeta = new Map();          // id -> { name, loopCount }
let playing = new Set();              // 재생 중 macroId
const progressById = new Map();       // id -> 최신 progress
const delayAnim = new Map();          // id -> { start, dur, stepIndex }
const rows = new Map();               // id -> { el, outer, inner, nameEl, loopEl }
let lastShow = null, lastW = 0, lastH = 0;

// ---------- 2중 원 ----------
const R_OUT = 15.5, R_IN = 10;
const C_OUT = 2 * Math.PI * R_OUT, C_IN = 2 * Math.PI * R_IN;
function ringSvg() {
  return `<svg class="ring" viewBox="0 0 40 40" aria-hidden="true">
    <circle class="track" cx="20" cy="20" r="${R_OUT}" stroke-width="3.4"></circle>
    <circle class="track" cx="20" cy="20" r="${R_IN}" stroke-width="2.8"></circle>
    <circle class="arc outer" cx="20" cy="20" r="${R_OUT}" stroke-width="3.4" stroke-dasharray="${C_OUT}" stroke-dashoffset="${C_OUT}"></circle>
    <circle class="arc inner" cx="20" cy="20" r="${R_IN}" stroke-width="2.8" stroke-dasharray="${C_IN}" stroke-dashoffset="${C_IN}"></circle>
  </svg>`;
}
const setArc = (circle, C, frac) => { circle.style.strokeDashoffset = (C * (1 - clamp01(frac))).toFixed(2); };

// ---------- 렌더(행 구성: 활성화된 매크로 전부) ----------
function computeEnabled() {
  enabledIds = macroList.filter((m) => m.enabled).sort((a, b) => (a.order || 0) - (b.order || 0)).map((m) => m.id);
}
function renderRows() {
  const want = new Set(enabledIds);
  for (const [id, r] of rows) if (!want.has(id)) { r.el.remove(); rows.delete(id); }

  for (const id of enabledIds) {
    let r = rows.get(id);
    if (!r) {
      const el = document.createElement('div');
      el.className = 'row';
      el.innerHTML = ringSvg() + '<div class="meta"><span class="name"></span><span class="loop"></span></div>';
      const [outer, inner] = el.querySelectorAll('.arc');
      r = { el, outer, inner, nameEl: el.querySelector('.name'), loopEl: el.querySelector('.loop') };
      rows.set(id, r);
    }
    rowsEl.appendChild(r.el); // order 순서대로 재배치
    paintRow(id, r);
  }
  reportSize();
}
function metaOf(id) { return macroMeta.get(id) || { name: '매크로', loopCount: 1 }; }
function loopText(loopCount, loop, isPlaying) {
  if (!isPlaying) return (loopCount == null || loopCount <= 0) ? '↻' : `×${loopCount}`; // 대기 상태: 총 반복만
  const cur = (loop || 0) + 1;
  return (loopCount == null || loopCount <= 0) ? `${cur} ↻` : `${cur}/${loopCount}`;
}
function paintRow(id, r) {
  const m = metaOf(id);
  const isPlaying = playing.has(id);
  r.el.classList.toggle('idle', !isPlaying);
  r.nameEl.textContent = m.name;
  r.nameEl.title = m.name;
  const p = isPlaying ? progressById.get(id) : null;
  const outFrac = p && p.stepCount > 0 ? p.stepIndex / p.stepCount : 0;
  setArc(r.outer, C_OUT, outFrac);
  setArc(r.inner, C_IN, isPlaying ? innerFrac(id) : 0);
  r.loopEl.textContent = loopText(m.loopCount, p ? p.loop : 0, isPlaying);
}

// ---------- 딜레이 채움 애니메이션 ----------
function innerFrac(id) {
  const a = delayAnim.get(id);
  if (!a || a.dur <= 0) return 0;
  return clamp01((performance.now() - a.start) / a.dur);
}
let raf = 0;
function tick() {
  raf = 0;
  let alive = false;
  for (const [id, a] of delayAnim) {
    const r = rows.get(id);
    const f = innerFrac(id);
    if (r) setArc(r.inner, C_IN, f);
    if (f < 1) alive = true; else delayAnim.delete(id);
  }
  if (alive) raf = requestAnimationFrame(tick);
}
const kick = () => { if (!raf) raf = requestAnimationFrame(tick); };

// ---------- 데이터 수신 ----------
function onProgress(p) {
  progressById.set(p.macroId, p);
  const r = rows.get(p.macroId);
  if (!r || !playing.has(p.macroId)) return;
  if (p.delayMs > 0) { delayAnim.set(p.macroId, { start: performance.now(), dur: p.delayMs, stepIndex: p.stepIndex }); kick(); }
  else delayAnim.delete(p.macroId);
  paintRow(p.macroId, r);
}
function onStatus(s) {
  const next = new Set(Array.isArray(s.playingIds) ? s.playingIds : []);
  playing = next;
  for (const id of [...progressById.keys()]) if (!next.has(id)) progressById.delete(id);
  for (const id of [...delayAnim.keys()]) if (!next.has(id)) delayAnim.delete(id);
  for (const [id, r] of rows) paintRow(id, r); // 재생/대기 상태 반영
  updateShowIntent();
}
function updateShowIntent() {
  const show = !!settings.enabled && enabledIds.length > 0;
  if (show !== lastShow) { lastShow = show; toNative(show ? 'overlay:show' : 'overlay:hide'); }
}

// ---------- 콘텐츠 크기 → 네이티브 ----------
function reportSize() {
  requestAnimationFrame(() => {
    const rct = $('panel').getBoundingClientRect();
    const w = Math.ceil(rct.width), h = Math.ceil(rct.height);
    if (w && h && (w !== lastW || h !== lastH)) { lastW = w; lastH = h; toNative('size:' + w + 'x' + h); }
  });
}

// ---------- 설정/매크로 로드 ----------
async function loadSettings() {
  try { settings = await fetch('/api/overlay').then((r) => r.json()); } catch { /* 서버 준비 전 */ }
  updateShowIntent();
}
async function loadMacros() {
  try {
    macroList = await fetch('/api/macros').then((r) => r.json());
    macroMeta.clear();
    for (const m of macroList) macroMeta.set(m.id, { name: m.name, loopCount: m.loopCount });
    computeEnabled();
    renderRows();
    updateShowIntent();
  } catch { /* 무시 */ }
}

// ---------- WebSocket ----------
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws?widget=1`); // 위젯 취급 → 메인 UI 수 제외
  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === 'progress') onProgress(msg.data);
    else if (msg.type === 'status') onStatus(msg.data);
    else if (msg.type === 'macrosChanged') loadMacros(); // 활성화/이름/반복 변경 반영
    else if (msg.type === 'overlaySettings') { settings = msg.data; updateShowIntent(); }
  };
  ws.onclose = () => setTimeout(connectWs, 1200);
  ws.onerror = () => { try { ws.close(); } catch { /* */ } };
}

loadSettings();
loadMacros();
connectWs();
