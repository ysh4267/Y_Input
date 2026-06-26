import { api } from './api.js';

const $ = (id) => document.getElementById(id);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function createRecorder({ log, onRecorded, getStatus }) {
  let busy = false;

  function readOptions() {
    const fixedOn = $('opt-fixed-enable').checked;
    return {
      keyboard: $('opt-kbd').checked,
      mouseButtons: $('opt-mbtn').checked,
      mouseMove: $('opt-mmove').checked,
      mouseWheel: $('opt-mwheel').checked,
      fixedDelayMs: fixedOn ? (parseFloat($('opt-fixed-ms').value) || 0) : null,
    };
  }

  async function start() {
    if (busy) return;
    const st = getStatus();
    if (st && st.state !== 'idle') { log('warn', '녹화/재생 중에는 새 녹화를 시작할 수 없습니다.'); return; }
    busy = true;
    try {
      if ($('opt-countdown').checked) {
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

  // 와이어링
  $('rec-toggle').onclick = toggle;
  $('opt-fixed-enable').onchange = () => { $('opt-fixed-ms').disabled = !$('opt-fixed-enable').checked; };

  return { start, stop, toggle, onStatus };
}
