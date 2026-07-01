// 보더리스 위젯 창(WebView2)의 스크립트 — 한 매크로의 이름 + 인디케이터. 서버 WS로 진행 애니메이션.
// 인디케이터 로직은 app.js와 동일(단일 매크로용으로 복제). CSS는 app.css 재사용.
const $ = (id) => document.getElementById(id);
const MREDUCE = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const MID = new URLSearchParams(location.search).get('id') || '';
const toNative = (m) => { try { window.chrome.webview.postMessage(m); } catch { /* WebView2 밖에서 열림 */ } };

// ---------- 모양(배경 반투명 색 + 테두리 하이라이트): 대기=사용자색 / 켜짐=파랑 / 재생=녹색 ----------
let cfg = { color: '#1b2230', opacity: 72 };
let enabled = false, playing = false;
const GREEN = [52, 211, 153], BLUE = [59, 130, 246];
function hex2rgb(h) {
  const m = /^#?([0-9a-f]{6})$/i.exec(h || ''); if (!m) return [27, 34, 48];
  const n = parseInt(m[1], 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function applyAppearance() {
  const [r, g, b] = playing ? GREEN : enabled ? BLUE : hex2rgb(cfg.color);
  const a = Math.max(0, Math.min(100, cfg.opacity)) / 100;
  document.body.style.background = `rgba(${r},${g},${b},${a})`; // 반투명 색(뒤 데스크톱 블러는 네이티브가 제공)
  document.body.style.borderColor = playing ? 'rgba(52,211,153,.9)' : enabled ? 'rgba(59,130,246,.9)' : 'rgba(255,255,255,.16)';
}
async function loadConfig() { try { cfg = (await fetch('/api/widget/config').then((r) => r.json())) || cfg; } catch { /* 기본값 */ } applyAppearance(); }

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
    else if (msg.type === 'widgetConfig') { cfg = msg.data || cfg; applyAppearance(); } // 설정 패널에서 색/불투명도 변경
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
  toNative('resize'); // 네이티브가 우하단 크기조절 시작(Min/Max 존중)
});

loadConfig();
loadMacro();
connectWs();
