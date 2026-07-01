// 보더리스 위젯 창(WebView2)의 스크립트 — 한 매크로의 이름 + 인디케이터. 서버 WS로 진행 애니메이션.
// 인디케이터 로직은 app.js와 동일(단일 매크로용으로 복제). CSS는 app.css 재사용.
const $ = (id) => document.getElementById(id);
const MREDUCE = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const MID = new URLSearchParams(location.search).get('id') || '';
const toNative = (m) => { try { window.chrome.webview.postMessage(m); } catch { /* WebView2 밖에서 열림 */ } };

// ---------- 모양: 상태별(대기/켜짐/재생) 배경색 + 알파(각각 지정). 상태 테두리색은 고정(회색/파랑/녹색). ----------
let cfg = { idleColor: '#1f232c', idleAlpha: 72, onColor: '#243650', onAlpha: 72, playColor: '#1f3d34', playAlpha: 72, blur: 1 };
let enabled = false, playing = false;
function applyBlur() { toNative('blur:' + (cfg.blur === 2 ? 2 : 1)); } // 1=약함 / 2=강함(아크릴) — 네이티브가 적용
function hex2rgb(h) {
  const m = /^#?([0-9a-f]{6})$/i.exec(h || ''); if (!m) return [31, 35, 44];
  const n = parseInt(m[1], 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function applyAppearance() {
  let color, alpha, border;
  if (playing) { color = cfg.playColor; alpha = cfg.playAlpha; border = 'rgba(52,211,153,.85)'; }
  else if (enabled) { color = cfg.onColor; alpha = cfg.onAlpha; border = 'rgba(79,140,255,.75)'; }
  else { color = cfg.idleColor; alpha = cfg.idleAlpha; border = 'rgba(255,255,255,.16)'; }
  const [r, g, b] = hex2rgb(color);
  const a = Math.max(0, Math.min(100, alpha ?? 72)) / 100; // 0%=완전 투명(블러만)
  $('w-bg').style.background = `rgba(${r},${g},${b},${a})`;
  document.body.style.borderColor = border;
}
// WebView2가 폭을 늘렸을 때 새 영역을 다시 안 그리는 문제 → 배경 레이어를 잠깐 껐다 켜 강제 리페인트.
function repaintBg() { const bg = $('w-bg'); bg.style.display = 'none'; void bg.offsetHeight; bg.style.display = ''; }
async function loadConfig() { try { cfg = Object.assign(cfg, (await fetch('/api/widget/config').then((r) => r.json())) || {}); } catch { /* 기본값 */ } applyAppearance(); applyBlur(); }

// ---------- 인디케이터 (app.js buildTimeline/updateOneTimeline 등과 동일) ----------
function buildTimeline(container, shape) {
  if (!container) return;
  container._pi = -1;
  container.innerHTML = '';
  container.dataset.shape = JSON.stringify(shape || []);
  if (!shape || !shape.length) return;
  const dot = (i, extra) => { const d = document.createElement('i'); d.className = 'tl-dot' + (extra || ''); d.dataset.i = i; return d; };
  let cur = container; const stack = [];
  for (let i = 0; i < shape.length; i++) {
    const t = shape[i][0];
    if (t === 'a') cur.appendChild(dot(i));
    else if (t === 'd') {
      const ln = document.createElement('i'); ln.className = 'tl-line'; ln.dataset.i = i;
      const ms = +shape[i][1] || 0;
      ln.style.width = Math.round(Math.max(12, Math.min(40, 12 + ms * 0.035))) + 'px';
      const f = document.createElement('i'); f.className = 'tl-fill'; ln.appendChild(f); cur.appendChild(ln);
    } else if (t === 's') {
      const lp = document.createElement('span'); lp.className = 'tl-loop'; lp.dataset.start = i;
      const bar = document.createElement('i'); bar.className = 'tl-loop-bar';
      const fill = document.createElement('i'); fill.className = 'tl-loop-fill';
      const inner = document.createElement('span'); inner.className = 'tl-loop-inner';
      lp.append(bar, fill, inner);
      inner.appendChild(dot(i, ' tl-loop-pt'));
      cur.appendChild(lp); stack.push(cur); cur = inner;
    } else if (t === 'e') {
      if (stack.length) {
        cur.appendChild(dot(i, ' tl-loop-pt'));
        const lp = cur.parentElement; if (lp && lp.classList.contains('tl-loop')) lp.dataset.end = i;
        cur = stack.pop();
      }
    }
  }
}
const setLoopFill = (lp, frac) => lp.style.setProperty('--loop-fill', (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%');
function resetTimeline(el) {
  if (!el) return;
  el._pi = -1;
  el.querySelectorAll('.tl-dot, .tl-line').forEach((n) => n.classList.remove('done', 'active'));
  el.querySelectorAll('.tl-fill').forEach((f) => { f.style.transition = 'none'; f.style.width = '0%'; });
  el.querySelectorAll('.tl-loop').forEach((lp) => { lp.classList.remove('active', 'done'); setLoopFill(lp, 0); });
  el.scrollLeft = 0;
  const bar = el.nextElementSibling; if (bar && bar.firstElementChild) bar.firstElementChild.style.width = '0%';
}
function setProgBar(el, frac) {
  const bar = el && el.nextElementSibling; if (!bar || !bar.classList.contains('macro-prog-bar')) return;
  const fill = bar.firstElementChild; if (fill) fill.style.width = (Math.max(0, Math.min(1, frac)) * 100).toFixed(1) + '%';
}
function loopAwareFraction(shape, idx, loops) {
  const n = shape.length; if (!n) return 0;
  const endOf = new Array(n).fill(-1), st = [];
  for (let i = 0; i < n; i++) { const t = shape[i][0]; if (t === 's') st.push(i); else if (t === 'e') { const s = st.pop(); if (s != null) endOf[s] = i; } }
  const lm = new Map(); (loops || []).forEach((f) => lm.set(f.startIndex, f));
  const total = (a, b) => {
    let c = 0;
    for (let i = a; i < b;) {
      const t = shape[i][0];
      if (t === 's') { const e = endOf[i] < 0 ? b : endOf[i]; c += Math.max(1, +shape[i][1] || 1) * total(i + 1, e); i = e + 1; }
      else if (t === 'e') i++; else { c += 1; i++; }
    }
    return c;
  };
  const done = (a, b) => {
    let c = 0;
    for (let i = a; i < b;) {
      const t = shape[i][0];
      if (t === 's') {
        const e = endOf[i] < 0 ? b : endOf[i], N = Math.max(1, +shape[i][1] || 1), bw = total(i + 1, e);
        if (idx > e) c += N * bw;
        else if (idx > i && idx < e) { const f = lm.get(i), iterDone = f ? Math.max(0, f.total - f.remaining) : 0; c += iterDone * bw + done(i + 1, e); }
        i = e + 1;
      } else if (t === 'e') i++; else { if (i <= idx) c += 1; i++; }
    }
    return c;
  };
  const W = total(0, n);
  return W > 0 ? Math.max(0, Math.min(1, done(0, n) / W)) : 0;
}
function applyIndexDelta(prev, idx, resolve, done, active, undone) {
  if (idx > prev) { for (let i = Math.max(0, prev); i < idx; i++) { const n = resolve(i); if (n) done(n); } }
  else if (idx < prev) { for (let i = prev; i > idx; i--) { const n = resolve(i); if (n) undone(n); } }
  if (idx >= 0) { const n = resolve(idx); if (n) active(n); }
}
function scrollTimelineToActive(el, idx) {
  const node = el.querySelector(`.tl-dot[data-i="${idx}"], .tl-line[data-i="${idx}"]`);
  if (!node) return;
  const c = el.getBoundingClientRect(), n = node.getBoundingClientRect();
  const center = (n.left + n.width / 2) - c.left;
  if (center < c.width * 0.25 || center > c.width * 0.78)
    el.scrollTo({ left: Math.max(0, el.scrollLeft + center - c.width * 0.6), behavior: MREDUCE ? 'auto' : 'smooth' });
}
function updateTimeline(el, p) {
  const idx = p.stepIndex;
  applyIndexDelta(el._pi ?? -1, idx,
    (i) => el.querySelector(`.tl-dot[data-i="${i}"], .tl-line[data-i="${i}"]`),
    (n) => { n.classList.add('done'); n.classList.remove('active'); if (n.classList.contains('tl-line')) { const f = n.querySelector('.tl-fill'); if (f) { f.style.transition = 'none'; f.style.width = '100%'; } } },
    (n) => { n.classList.add('active'); n.classList.remove('done'); },
    (n) => { n.classList.remove('done', 'active'); if (n.classList.contains('tl-line')) { const f = n.querySelector('.tl-fill'); if (f) { f.style.transition = 'none'; f.style.width = '0%'; } } });
  el._pi = idx;
  el.querySelectorAll('.tl-loop').forEach((lp) => {
    lp.classList.remove('active');
    const end = +lp.dataset.end;
    const fin = Number.isFinite(end) && idx > end;
    lp.classList.toggle('done', fin);
    setLoopFill(lp, fin ? 1 : 0);
  });
  (p.loops || []).forEach((f) => {
    const lp = el.querySelector(`.tl-loop[data-start="${f.startIndex}"]`);
    if (!lp) return;
    lp.classList.add('active'); lp.classList.remove('done');
    const total = f.total || 1; setLoopFill(lp, (total - f.remaining + 1) / total);
  });
  let shape = []; try { shape = JSON.parse(el.dataset.shape || '[]'); } catch { /* 손상 시 빈 */ }
  setProgBar(el, loopAwareFraction(shape, idx, p.loops));
  scrollTimelineToActive(el, idx);
}
// 지연 채움 애니메이션(단일 매크로)
let delayAnim = null, delayRaf = 0;
function setActiveDelay(idx, delayMs) {
  if (delayMs > 0) { delayAnim = { start: performance.now(), dur: delayMs, i: idx }; if (!delayRaf) delayRaf = requestAnimationFrame(tickDelay); }
  else delayAnim = null;
}
function tickDelay() {
  delayRaf = 0;
  if (delayAnim) {
    const now = performance.now();
    const frac = delayAnim.dur > 0 ? Math.min(1, (now - delayAnim.start) / delayAnim.dur) : 1;
    const w = (frac * 100).toFixed(1) + '%';
    document.querySelectorAll(`#w-prog .tl-line[data-i="${delayAnim.i}"] .tl-fill`).forEach((lf) => { lf.style.transition = 'none'; lf.style.width = w; });
    if (frac >= 1) delayAnim = null;
  }
  if (delayAnim) delayRaf = requestAnimationFrame(tickDelay);
}

// ---------- 데이터 + WS ----------
async function loadMacro() {
  try {
    const list = await fetch('/api/macros').then((r) => r.json());
    const m = list.find((x) => x.id === MID);
    if (!m) { toNative('close'); return; } // 매크로가 삭제됨 → 창 닫기
    $('w-name').textContent = m.name;
    document.title = m.name;
    enabled = !!m.enabled;
    buildTimeline($('w-prog'), m.shape || []);
    applyAppearance(); // 켜짐 상태 반영(파랑)
  } catch { /* 서버 준비 전 — WS 재연결 때 다시 시도 */ }
}
function showProgress(p) { if (p.macroId === MID) { updateTimeline($('w-prog'), p); setActiveDelay(p.stepIndex, p.delayMs); } }
function onStatus(s) {
  playing = new Set(s.playingIds || []).has(MID);
  if (!playing) { resetTimeline($('w-prog')); delayAnim = null; }
  applyAppearance(); // 재생=녹색 / 정지 시 원래색
}
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === 'progress') showProgress(msg.data);
    else if (msg.type === 'status') onStatus(msg.data);
    else if (msg.type === 'macrosChanged') loadMacro(); // 이름/켜짐/모양 갱신(삭제 시 창 닫힘)
    else if (msg.type === 'widgetConfig') { cfg = Object.assign(cfg, msg.data || {}); applyAppearance(); applyBlur(); } // 설정 패널에서 색/불투명도/블러 변경
    else if (msg.type === 'shutdown') toNative('close');
  };
  ws.onclose = () => setTimeout(connectWs, 1200);
  ws.onerror = () => { try { ws.close(); } catch { /* */ } };
}

// ---------- 창 조작(드래그/닫기) ----------
$('w-close').onclick = () => toNative('close');
$('w-head').addEventListener('pointerdown', (e) => {
  if (e.button !== 0 || (e.target.closest && e.target.closest('.w-close'))) return;
  toNative('drag'); // 네이티브가 WM_NCLBUTTONDOWN으로 창 이동 시작
});
$('w-grip').addEventListener('pointerdown', (e) => {
  if (e.button !== 0) return;
  e.preventDefault();
  toNative('resize'); // 네이티브가 우측 폭 조절 시작(Min/Max 존중)
});
// 폭 바뀔 때 배경을 새 크기에 다시 채우고 강제 리페인트(새 영역 배경색 갱신 안 되는 문제 방지)
function onResized() { applyAppearance(); repaintBg(); }
window.addEventListener('resize', onResized);
try { new ResizeObserver(onResized).observe(document.documentElement); } catch { /* 미지원 무시 */ }

loadConfig();
loadMacro();
connectWs();
