// CSS 팝업(모달) — 시스템 alert/confirm/prompt 대체 + 입력 잠금(드래그/브라우저 단축키)

let overlay = null, inputEl = null, resolver = null, escHandler = null;

function ensureDom() {
  if (overlay) return;
  overlay = document.createElement('div');
  overlay.className = 'modal-overlay'; overlay.hidden = true;
  overlay.innerHTML = `
    <div class="modal" role="dialog" aria-modal="true">
      <div class="modal-title"></div>
      <div class="modal-msg"></div>
      <input class="modal-input" type="text" hidden />
      <div class="modal-actions">
        <button class="btn ghost modal-cancel" type="button"></button>
        <button class="btn primary modal-ok" type="button"></button>
      </div>
    </div>`;
  document.body.appendChild(overlay);
  inputEl = overlay.querySelector('.modal-input');
  overlay.querySelector('.modal-ok').onclick = () => done(inputEl.hidden ? true : inputEl.value);
  overlay.querySelector('.modal-cancel').onclick = () => done(inputEl.hidden ? false : null);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) done(inputEl.hidden ? false : null); });
}

function open({ title, message, ok, cancel, input }) {
  ensureDom();
  const tt = overlay.querySelector('.modal-title');
  tt.textContent = title || ''; tt.hidden = !title;
  overlay.querySelector('.modal-msg').textContent = message || '';
  overlay.querySelector('.modal-ok').textContent = ok || '확인';
  const cb = overlay.querySelector('.modal-cancel');
  cb.textContent = cancel || '취소'; cb.hidden = cancel === false;
  const hasInput = input !== undefined && input !== null && input !== false;
  inputEl.hidden = !hasInput; inputEl.value = hasInput ? input : '';
  overlay.hidden = false;

  return new Promise((res) => {
    resolver = res;
    escHandler = (e) => {
      if (e.key === 'Escape') { e.preventDefault(); done(inputEl.hidden ? false : null); }
      else if (e.key === 'Enter') { e.preventDefault(); done(inputEl.hidden ? true : inputEl.value); }
    };
    window.addEventListener('keydown', escHandler, true);
    setTimeout(() => {
      if (inputEl.hidden) overlay.querySelector('.modal-ok').focus();
      else { inputEl.focus(); inputEl.select(); }
    }, 0);
  });
}

function done(val) {
  overlay.hidden = true;
  if (escHandler) window.removeEventListener('keydown', escHandler, true);
  escHandler = null;
  const r = resolver; resolver = null;
  if (r) r(val);
}

export function confirmDialog(message, opts = {}) {
  return open({ title: opts.title || '확인', message, ok: opts.ok || '확인', cancel: opts.cancel || '취소' });
}
export function promptDialog(message, defaultValue = '', opts = {}) {
  return open({ title: opts.title || '입력', message, ok: opts.ok || '확인', cancel: opts.cancel || '취소', input: defaultValue ?? '' });
}
export function alertDialog(message, opts = {}) {
  return open({ title: opts.title || '알림', message, ok: opts.ok || '확인', cancel: false });
}

// ---------- 입력 잠금: 텍스트 드래그·브라우저 단축키·우클릭 메뉴 차단 ----------
export function installLockdown() {
  const SELECTABLE = 'input, textarea, .log, code, .modal-msg, .modal-input';

  // 텍스트 선택(드래그 하이라이트) 차단 — 입력칸·로그·모달만 허용. CSS user-select와 이중 방어.
  document.addEventListener('selectstart', (e) => {
    if (e.target.closest && e.target.closest(SELECTABLE)) return;
    e.preventDefault();
  });

  // 텍스트/요소 드래그 차단 — 지정된 드래그 핸들(팔레트·행 핸들)만 허용
  document.addEventListener('dragstart', (e) => {
    if (e.target.closest && e.target.closest('.pal-item, .step-drag')) return;
    e.preventDefault();
  }, true);

  // 브라우저 단축키 차단(새로고침·인쇄·찾기·소스·줌·뒤로/앞으로 등).
  // preventDefault만 호출 — stopPropagation은 안 함(핫키 캡처 등과 공존).
  window.addEventListener('keydown', (e) => {
    const k = (e.key || '').toLowerCase();
    const ctrl = e.ctrlKey || e.metaKey;
    const block =
      k === 'f5' || k === 'f1' || k === 'f3' || k === 'f7' ||
      (ctrl && ['r', 'p', 's', 'f', 'g', 'u', 'j', 'h', 'o', '+', '-', '=', '_', '0'].includes(k)) ||
      (ctrl && e.shiftKey && ['r', 'i', 'j', 'c'].includes(k)) ||
      (e.altKey && (k === 'arrowleft' || k === 'arrowright'));
    if (block) e.preventDefault();
  }, true);

  // 우클릭 컨텍스트 메뉴 차단 — 편집 가능한 입력칸은 허용(붙여넣기 등)
  window.addEventListener('contextmenu', (e) => {
    if (e.target.closest && e.target.closest('input:not([readonly]), textarea')) return;
    e.preventDefault();
  });
}
