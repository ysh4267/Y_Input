// 보더리스 위젯 창(WebView2)의 스크립트 — 한 매크로의 이름 + 인디케이터. 서버 WS로 진행 애니메이션.
// 인디케이터 로직은 app.js와 동일(단일 매크로용으로 복제). CSS는 app.css 재사용.
const $ = (id) => document.getElementById(id);
const MREDUCE = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const MID = new URLSearchParams(location.search).get('id') || '';
const toNative = (m) => { try { window.chrome.webview.postMessage(m); } catch { /* WebView2 밖에서 열림 */ } };

// ---------- 모양: 상태별(대기/켜짐/재생) 배경색 + 알파 — 고정값(설정 제거). 검정 계열 + 높은 알파(블러 위로 색이 진하게). ----------
// 참고: 블러가 켜져 있어도 색은 적용됨(DWM 틴트). 다만 알파가 낮으면 뒤 블러가 비쳐 밝아 보임 → 알파를 높여 더 검게.
const STATE_STYLE = {
  idle: { color: '#0c0e13', alpha: 94, border: 'rgba(255,255,255,.24)' }, // 대기: 거의 검정
  on: { color: '#0b0f1c', alpha: 94, border: 'rgba(79,140,255,.9)' },     // 켜짐: 거의 검정(아주 옅은 파랑) — 상태는 테두리로
  play: { color: '#0a130e', alpha: 94, border: 'rgba(52,211,153,.95)' },  // 재생: 거의 검정(아주 옅은 녹색) — 상태는 테두리로
};
let enabled = false, playing = false;
let curShape = [], durationMs = 0, speedMul = 1, loopCountN = 1, lastP = null; // 남은/최대 시간 계산용
function hex2rgb(h) {
  const m = /^#?([0-9a-f]{6})$/i.exec(h || ''); if (!m) return [12, 14, 19];
  const n = parseInt(m[1], 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function applyAppearance() {
  const s = playing ? STATE_STYLE.play : enabled ? STATE_STYLE.on : STATE_STYLE.idle;
  const [r, g, b] = hex2rgb(s.color);
  const av = Math.round(Math.max(0, Math.min(100, s.alpha)) / 100 * 255);
  const abgr = (((av << 24) | (b << 16) | (g << 8) | r) >>> 0).toString(16).padStart(8, '0');
  toNative('tint:' + abgr); // 색+알파를 네이티브(DWM)가 창 전체에 균일 적용 → 폭 늘려도 안 갈라짐(페이지 배경은 안 씀)
  document.body.style.borderColor = s.border;
}

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
  if (lastP && playing) showTimeReadout(lastP); // 긴 지연 동안에도 남은 시간 매끄럽게 감소
  if (delayAnim) delayRaf = requestAnimationFrame(tickDelay);
}

// ---------- 남은/최대 시간(초) ----------
const fmtS = (ms) => (Math.max(0, ms) / 1000).toFixed(1) + 's';
// 한 패스의 base ms(내부 반복 반영). shape가 잘렸으면 부정확 → 표시는 durationMs로 스케일해 보정.
function shapePassMs(shape) {
  const n = shape.length, endOf = new Array(n).fill(-1), st = [];
  for (let i = 0; i < n; i++) { const t = shape[i][0]; if (t === 's') st.push(i); else if (t === 'e') { const s = st.pop(); if (s != null) endOf[s] = i; } }
  const tot = (a, b) => { let ms = 0; for (let i = a; i < b;) { const t = shape[i][0]; if (t === 's') { const e = endOf[i] < 0 ? b : endOf[i]; ms += Math.max(1, +shape[i][1] || 1) * tot(i + 1, e); i = e + 1; } else if (t === 'e') i++; else { if (t === 'd') ms += (+shape[i][1] || 0); i++; } } return ms; };
  return tot(0, n);
}
// 현재 위치(idx)까지 소비한 base ms(내부 반복 반영) — 진행 중 지연은 curFrac(0..1)만큼 부분 반영.
function shapeElapsedMs(shape, idx, loops, curFrac) {
  const n = shape.length, endOf = new Array(n).fill(-1), st = [];
  for (let i = 0; i < n; i++) { const t = shape[i][0]; if (t === 's') st.push(i); else if (t === 'e') { const s = st.pop(); if (s != null) endOf[s] = i; } }
  const lm = new Map(); (loops || []).forEach((f) => lm.set(f.startIndex, f));
  const tot = (a, b) => { let ms = 0; for (let i = a; i < b;) { const t = shape[i][0]; if (t === 's') { const e = endOf[i] < 0 ? b : endOf[i]; ms += Math.max(1, +shape[i][1] || 1) * tot(i + 1, e); i = e + 1; } else if (t === 'e') i++; else { if (t === 'd') ms += (+shape[i][1] || 0); i++; } } return ms; };
  const done = (a, b) => {
    let ms = 0;
    for (let i = a; i < b;) {
      const t = shape[i][0];
      if (t === 's') {
        const e = endOf[i] < 0 ? b : endOf[i], N = Math.max(1, +shape[i][1] || 1), bw = tot(i + 1, e);
        if (idx > e) ms += N * bw;
        else if (idx > i && idx < e) { const f = lm.get(i), it = f ? Math.max(0, f.total - f.remaining) : 0; ms += it * bw + done(i + 1, e); }
        i = e + 1;
      } else if (t === 'e') i++;
      else { if (t === 'd') { const dm = +shape[i][1] || 0; if (i < idx) ms += dm; else if (i === idx) ms += dm * Math.max(0, Math.min(1, curFrac || 0)); } i++; }
    }
    return ms;
  };
  return done(0, n);
}
function curDelayFrac(idx) {
  if (delayAnim && delayAnim.i === idx && delayAnim.dur > 0) return Math.min(1, (performance.now() - delayAnim.start) / delayAnim.dur);
  return 0;
}
// 재생 중 남은/최대(ms). 지연 없는 매크로(durationMs=0)면 null.
function computeTiming(p) {
  if (!durationMs || durationMs <= 0) return null;
  const passReal = durationMs / (speedMul || 1);                    // 한 패스 실제 ms(속도 반영, 지터 제외 추정)
  const passShape = shapePassMs(curShape) || durationMs;
  const frac = passShape > 0 ? Math.max(0, Math.min(1, shapeElapsedMs(curShape, p.stepIndex, p.loops, curDelayFrac(p.stepIndex)) / passShape)) : 0;
  const elapsedInPass = frac * passReal;
  if (!(loopCountN >= 1)) return { remaining: Math.max(0, passReal - elapsedInPass), total: passReal, infinite: true }; // 무한 반복 → 한 바퀴 기준
  const total = passReal * loopCountN;
  const elapsed = passReal * (p.loop || 0) + elapsedInPass;
  return { remaining: Math.max(0, total - elapsed), total, infinite: false };
}
function showTimeReadout(p) {
  const el = $('w-time'); if (!el) return;
  const t = p && computeTiming(p);
  el.textContent = t ? (fmtS(t.remaining) + ' / ' + fmtS(t.total) + (t.infinite ? ' ↻' : '')) : '';
}
function idleTimeReadout() {
  const el = $('w-time'); if (!el) return;
  if (!durationMs || durationMs <= 0) { el.textContent = ''; return; }
  const passReal = durationMs / (speedMul || 1);
  el.textContent = (loopCountN >= 1) ? '최대 ' + fmtS(passReal * loopCountN) : fmtS(passReal) + ' ↻';
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
    curShape = m.shape || [];
    durationMs = +m.durationMs || 0;
    speedMul = +m.speedMultiplier || 1;
    loopCountN = (m.loopCount == null) ? 1 : +m.loopCount; // <=0 = 무한 반복
    buildTimeline($('w-prog'), curShape);
    if (!playing) idleTimeReadout(); // 대기 시 최대 시간 표시
    applyAppearance(); // 켜짐 상태 반영(파랑)
  } catch { /* 서버 준비 전 — WS 재연결 때 다시 시도 */ }
}
function showProgress(p) { if (p.macroId === MID) { lastP = p; updateTimeline($('w-prog'), p); setActiveDelay(p.stepIndex, p.delayMs); showTimeReadout(p); } }
function onStatus(s) {
  playing = new Set(s.playingIds || []).has(MID);
  if (!playing) { resetTimeline($('w-prog')); delayAnim = null; lastP = null; idleTimeReadout(); }
  applyAppearance(); // 재생=녹색 / 정지 시 원래색
}
function connectWs() {
  const ws = new WebSocket(`ws://${location.host}/ws?widget=1`); // 위젯 표시 → 서버가 '메인 UI' 수에서 제외
  ws.onmessage = (ev) => {
    let msg; try { msg = JSON.parse(ev.data); } catch { return; }
    if (msg.type === 'progress') showProgress(msg.data);
    else if (msg.type === 'status') onStatus(msg.data);
    else if (msg.type === 'macrosChanged') loadMacro(); // 이름/켜짐/모양 갱신(삭제 시 창 닫힘)
    else if (msg.type === 'shutdown') toNative('close');
  };
  ws.onclose = () => setTimeout(connectWs, 1200);
  ws.onerror = () => { try { ws.close(); } catch { /* */ } };
}

// ---------- 창 조작(드래그/닫기/더블클릭 편집) ----------
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
// 더블클릭(닫기/그립 제외) → 메인 편집 페이지 열기(네이티브가 기본 브라우저로 /?edit=id 를 엶)
document.body.addEventListener('dblclick', (e) => {
  if (e.target.closest && (e.target.closest('.w-close') || e.target.closest('.w-grip'))) return;
  toNative('edit');
});
// 폭 바뀌면 테두리 다시 적용(배경 색/알파는 네이티브 DWM이 창 전체에 균일 적용하므로 갈라짐 없음)
window.addEventListener('resize', applyAppearance);

applyAppearance(); // 초기 대기 상태 모양(데이터 로드 전에도 테두리/틴트 표시)
loadMacro();
connectWs();
