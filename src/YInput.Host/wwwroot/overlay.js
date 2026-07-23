// 인게임 오버레이 페이지. 서버 WS로 재생 상태/진행을 받아 매크로마다 2중 원 + 이름/루프를 세로로 쌓는다.
// 창(네이티브)은 클릭 통과·반투명·게임 추적을 담당하고, 이 페이지는 "표시 의도(overlay:show/hide)"와
// "콘텐츠 크기(size:WxH)"만 네이티브에 알린다. 핸들 모드(handle:on/off)면 드래그 핸들을 노출한다.
const $ = (id) => document.getElementById(id);
const clamp01 = (v) => Math.max(0, Math.min(1, v));
const toNative = (m) => { try { window.chrome.webview.postMessage(m); } catch { /* WebView2 밖 */ } };

const rowsEl = $('rows');

let settings = { enabled: true, handleOn: false, x: 0.03, y: 0.35 };
let macroMeta = new Map();            // id -> { name, loopCount }
let playing = [];                     // 재생 중 macroId 배열(순서 유지)
const progressById = new Map();       // id -> 최신 progress
const delayAnim = new Map();          // id -> { start, dur, stepIndex }
const rows = new Map();               // id -> { el, outer, inner, nameEl, loopEl }
let handleMode = false;
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

// ---------- 렌더(행 구성) ----------
function idsToShow() {
  if (playing.length) return playing;
  if (handleMode) return ['__sample__']; // 재생 없어도 위치 조정용 샘플 1행
  return [];
}
function metaOf(id) {
  if (id === '__sample__') return { name: '위치 조정', loopCount: 1 };
  return macroMeta.get(id) || { name: '매크로', loopCount: 1 };
}
function renderRows() {
  const ids = idsToShow();
  const want = new Set(ids);
  for (const [id, r] of rows) if (!want.has(id)) { r.el.remove(); rows.delete(id); }

  for (const id of ids) {
    let r = rows.get(id);
    if (!r) {
      const el = document.createElement('div');
      el.className = 'row';
      el.innerHTML = ringSvg() + '<div class="meta"><span class="name"></span><span class="loop"></span></div>';
      const [outer, inner] = el.querySelectorAll('.arc');
      r = { el, outer, inner, nameEl: el.querySelector('.name'), loopEl: el.querySelector('.loop') };
      rows.set(id, r);
    }
    rowsEl.appendChild(r.el); // 재생 순서대로 재배치
    paintRow(id, r);
  }
  reportSize();
}
function loopText(loopCount, loop) {
  const cur = (loop || 0) + 1;
  return (loopCount == null || loopCount <= 0) ? `${cur} ↻` : `${cur}/${loopCount}`;
}
function paintRow(id, r) {
  const m = metaOf(id);
  r.nameEl.textContent = m.name;
  r.nameEl.title = m.name;
  const p = progressById.get(id);
  if (id === '__sample__') { setArc(r.outer, C_OUT, 0.62); setArc(r.inner, C_IN, 0.4); r.loopEl.textContent = '1/1'; return; }
  const outFrac = p && p.stepCount > 0 ? p.stepIndex / p.stepCount : 0;
  setArc(r.outer, C_OUT, outFrac);
  setArc(r.inner, C_IN, innerFrac(id));
  r.loopEl.textContent = loopText(m.loopCount, p ? p.loop : 0);
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
  if (!r) return; // 아직 status로 행이 안 생김 → 다음 렌더에서
  if (p.delayMs > 0) { delayAnim.set(p.macroId, { start: performance.now(), dur: p.delayMs, stepIndex: p.stepIndex }); kick(); }
  else delayAnim.delete(p.macroId);
  paintRow(p.macroId, r);
}
function onStatus(s) {
  const next = Array.isArray(s.playingIds) ? s.playingIds : [];
  const changed = next.length !== playing.length || next.some((v, i) => v !== playing[i]);
  playing = next;
  // 종료된 매크로의 잔여 상태 정리
  const live = new Set(playing);
  for (const id of [...progressById.keys()]) if (!live.has(id)) progressById.delete(id);
  for (const id of [...delayAnim.keys()]) if (!live.has(id)) delayAnim.delete(id);
  if (changed) renderRows();
  updateShowIntent();
}
function updateShowIntent() {
  const show = !!settings.enabled && playing.length > 0;
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

// ---------- 설정/메타 로드 ----------
async function loadSettings() {
  try { settings = await fetch('/api/overlay').then((r) => r.json()); } catch { /* 서버 준비 전 */ }
  applyHandle(!!settings.handleOn);
  updateShowIntent();
}
async function loadMacros() {
  try {
    const list = await fetch('/api/macros').then((r) => r.json());
    macroMeta = new Map(list.map((m) => [m.id, { name: m.name, loopCount: m.loopCount }]));
    for (const [id, r] of rows) paintRow(id, r);
  } catch { /* 무시 */ }
}
function applyHandle(on) {
  handleMode = on;
  document.body.classList.toggle('handle', on);
  renderRows(); // 샘플 행 표시/제거
}

// ---------- 네이티브 → 웹 메시지(handle:on/off) ----------
try {
  window.chrome.webview.addEventListener('message', (e) => {
    const m = typeof e.data === 'string' ? e.data : '';
    if (m === 'handle:on') applyHandle(true);
    else if (m === 'handle:off') applyHandle(false);
  });
} catch { /* WebView2 밖 */ }

// 드래그 핸들 → 네이티브가 창 이동 시작
$('handle').addEventListener('pointerdown', (e) => { if (e.button === 0) { e.preventDefault(); toNative('drag'); } });

// ---------- WebSocket ----------
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws?widget=1`); // 위젯 취급 → 메인 UI 수 제외
  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === 'progress') onProgress(msg.data);
    else if (msg.type === 'status') onStatus(msg.data);
    else if (msg.type === 'macrosChanged') loadMacros();
    else if (msg.type === 'overlaySettings') { settings = msg.data; applyHandle(!!settings.handleOn); updateShowIntent(); }
    else if (msg.type === 'shutdown') { /* 창은 네이티브가 정리 */ }
  };
  ws.onclose = () => setTimeout(connectWs, 1200);
  ws.onerror = () => { try { ws.close(); } catch { /* */ } };
}

loadSettings();
loadMacros();
connectWs();
