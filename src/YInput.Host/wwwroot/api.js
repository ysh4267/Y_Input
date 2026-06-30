// REST API 래퍼 — 실패 시 서버가 준 error 메시지로 throw.
async function request(method, url, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) {
    opts.headers['Content-Type'] = 'application/json';
    opts.body = typeof body === 'string' ? body : JSON.stringify(body);
  }
  const res = await fetch(url, opts);
  const text = await res.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = text; }
  }
  if (!res.ok) {
    const msg = (data && data.error) ? data.error : `HTTP ${res.status}`;
    throw new Error(msg);
  }
  return data;
}

export const api = {
  status: () => request('GET', '/api/status'),
  installDrivers: () => request('POST', '/api/drivers/install'),

  listMacros: () => request('GET', '/api/macros'),
  getMacro: (id) => request('GET', `/api/macros/${id}`),
  getExpanded: (id) => request('GET', `/api/macros/${id}/expanded`),
  getTree: (id) => request('GET', `/api/macros/${id}/tree`),
  macroUsage: (id) => request('GET', `/api/macros/${id}/usage`),
  createMacro: (macro) => request('POST', '/api/macros', macro),
  updateMacro: (id, macro) => request('PUT', `/api/macros/${id}`, macro),
  deleteMacro: (id) => request('DELETE', `/api/macros/${id}`),
  resetMacros: () => request('POST', '/api/macros/reset'),
  importMacros: (macros) => request('POST', '/api/macros/import', macros),
  reorder: (ids) => request('POST', '/api/macros/reorder', ids),
  setEnabled: (id, enabled) => request('POST', `/api/macros/${id}/enabled`, { enabled }),
  setTrigger: (id, trigger) => request('POST', `/api/macros/${id}/trigger`, trigger ?? null),
  setPlayback: (id, loopCount) => request('POST', `/api/macros/${id}/playback`, { loopCount }),

  recordStart: (options) => request('POST', '/api/record/start', options || {}),
  recordStop: (name, persist = true) => request('POST', '/api/record/stop', { name, persist }),
  play: (id) => request('POST', `/api/play/${id}`),
  stop: () => request('POST', '/api/stop'),

  gamepadConnect: () => request('POST', '/api/gamepad/connect'),
  gamepadDisconnect: () => request('POST', '/api/gamepad/disconnect'),

  reloadHotkeys: () => request('POST', '/api/hotkeys/reload'),

  listenStart: () => request('POST', '/api/listen/start'),
  listenStop: () => request('POST', '/api/listen/stop'),
  monitorOn: () => request('POST', '/api/monitor/on'),
  monitorOff: () => request('POST', '/api/monitor/off'),

  appVersion: () => request('GET', '/api/app/version'),
  updateCheck: () => request('GET', '/api/app/update/check'),
};
