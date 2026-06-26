import { api } from './api.js';

const $ = (id) => document.getElementById(id);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const setSeg = (el, val) => el.querySelectorAll('.seg-btn').forEach((b) => b.classList.toggle('on', b.dataset.val === val));
const getSeg = (el) => el.querySelector('.seg-btn.on')?.dataset.val ?? 'record';

export function createRecorder({ log, onRecorded, getStatus }) {
  let busy = false;

  function readOptions() {
    const mode = getSeg($('seg-recdelay'));
    // 실제=null(측정값), 고정=입력값, 없음=0
    const fixedDelayMs = mode === 'fixed' ? (parseFloat($('opt-fixed-ms').value) || 0) : mode === 'none' ? 0 : null;
    const on = (t) => !!document.querySelector(`#rec-targets .chip[data-t="${t}"]`)?.classList.contains('on');
    return {
      keyboard: on('keyboard'),
      mouseButtons: on('mouseButtons'),
      mouseMove: on('mouseMove'),
      mouseWheel: on('mouseWheel'),
      gamepad: on('gamepad'),
      fixedDelayMs,
    };
  }

  async function start() {
    if (busy) return;
    const st = getStatus();
    if (st && st.state !== 'idle') { log('warn', '녹화/재생 중에는 새 녹화를 시작할 수 없습니다.'); return; }
    busy = true;
    try {
      if ($('opt-countdown').classList.contains('on')) {
        for (let n = 3; n >= 1; n--) { $('rec-status').textContent = `${n}…`; await sleep(700); }
      }
      $('rec-status').textContent = '';
      await api.recordStart(readOptions());
      log('info', '녹화 시작 — 입력을 기록합니다.');
    } catch (e) {
      log('error', e.message);
    } finally {
      busy = false;
    }
  }

  async function stop() {
    try {
      const macro = await api.recordStop('');
      log('info', `녹화 완료: ${macro.steps.length} 스텝 → 편집기로 불러옵니다.`);
      onRecorded && onRecorded(macro);
    } catch (e) {
      log('error', e.message);
    }
  }

  async function toggle() {
    const st = getStatus();
    if (st && st.state === 'recording') await stop();
    else await start();
  }

  function onStatus(st) {
    const rec = st && st.state === 'recording';
    const btn = $('rec-toggle');
    btn.textContent = rec ? '■ 녹화 정지' : '● 녹화 시작';
    btn.classList.toggle('active', rec);
    btn.disabled = st && st.state === 'playing';
    if (!rec) $('rec-status').textContent = '';
  }

  function syncFixed() { $('opt-fixed-ms').hidden = getSeg($('seg-recdelay')) !== 'fixed'; }

  // 와이어링
  $('rec-toggle').onclick = toggle;
  setSeg($('seg-recdelay'), 'record'); syncFixed();
  $('seg-recdelay').querySelectorAll('.seg-btn').forEach((b) => b.onclick = () => { setSeg($('seg-recdelay'), b.dataset.val); syncFixed(); });
  // 토글 칩: 기록 대상 + 카운트다운
  document.querySelectorAll('#rec-targets .chip').forEach((c) => c.onclick = () => c.classList.toggle('on'));
  $('opt-countdown').onclick = () => $('opt-countdown').classList.toggle('on');

  return { start, stop, toggle, onStatus };
}
