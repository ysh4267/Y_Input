// 키보드 스캔코드(set 1) · 마우스 비트 · 게임패드 컨트롤 매핑.
// Interception KeyState 비트: Down=0x00, Up=0x01, E0(확장)=0x02.

// KeyboardEvent.code → [scancode, extended(E0)?]
export const KEYS = {
  Escape: [0x01], Digit1: [0x02], Digit2: [0x03], Digit3: [0x04], Digit4: [0x05],
  Digit5: [0x06], Digit6: [0x07], Digit7: [0x08], Digit8: [0x09], Digit9: [0x0A],
  Digit0: [0x0B], Minus: [0x0C], Equal: [0x0D], Backspace: [0x0E], Tab: [0x0F],
  KeyQ: [0x10], KeyW: [0x11], KeyE: [0x12], KeyR: [0x13], KeyT: [0x14], KeyY: [0x15],
  KeyU: [0x16], KeyI: [0x17], KeyO: [0x18], KeyP: [0x19], BracketLeft: [0x1A],
  BracketRight: [0x1B], Enter: [0x1C], ControlLeft: [0x1D], KeyA: [0x1E], KeyS: [0x1F],
  KeyD: [0x20], KeyF: [0x21], KeyG: [0x22], KeyH: [0x23], KeyJ: [0x24], KeyK: [0x25],
  KeyL: [0x26], Semicolon: [0x27], Quote: [0x28], Backquote: [0x29], ShiftLeft: [0x2A],
  Backslash: [0x2B], KeyZ: [0x2C], KeyX: [0x2D], KeyC: [0x2E], KeyV: [0x2F], KeyB: [0x30],
  KeyN: [0x31], KeyM: [0x32], Comma: [0x33], Period: [0x34], Slash: [0x35],
  ShiftRight: [0x36], NumpadMultiply: [0x37], AltLeft: [0x38], Space: [0x39],
  CapsLock: [0x3A], F1: [0x3B], F2: [0x3C], F3: [0x3D], F4: [0x3E], F5: [0x3F],
  F6: [0x40], F7: [0x41], F8: [0x42], F9: [0x43], F10: [0x44], NumLock: [0x45],
  ScrollLock: [0x46], Numpad7: [0x47], Numpad8: [0x48], Numpad9: [0x49],
  NumpadSubtract: [0x4A], Numpad4: [0x4B], Numpad5: [0x4C], Numpad6: [0x4D],
  NumpadAdd: [0x4E], Numpad1: [0x4F], Numpad2: [0x50], Numpad3: [0x51], Numpad0: [0x52],
  NumpadDecimal: [0x53], F11: [0x57], F12: [0x58],

  // 확장(E0) 키
  NumpadEnter: [0x1C, true], ControlRight: [0x1D, true], NumpadDivide: [0x35, true],
  AltRight: [0x38, true], Home: [0x47, true], ArrowUp: [0x48, true], PageUp: [0x49, true],
  ArrowLeft: [0x4B, true], ArrowRight: [0x4D, true], End: [0x4F, true],
  ArrowDown: [0x50, true], PageDown: [0x51, true], Insert: [0x52, true], Delete: [0x53, true],
  MetaLeft: [0x5B, true], MetaRight: [0x5C, true], ContextMenu: [0x5D, true],
};

const LABELS = {
  Escape: 'Esc', Minus: '-', Equal: '=', Backspace: 'Backspace', Tab: 'Tab',
  BracketLeft: '[', BracketRight: ']', Enter: 'Enter', ControlLeft: 'LCtrl',
  Semicolon: ';', Quote: "'", Backquote: '`', ShiftLeft: 'LShift', Backslash: '\\',
  Comma: ',', Period: '.', Slash: '/', ShiftRight: 'RShift', NumpadMultiply: 'Num*',
  AltLeft: 'LAlt', Space: 'Space', CapsLock: 'Caps', NumLock: 'NumLk', ScrollLock: 'ScrLk',
  NumpadSubtract: 'Num-', NumpadAdd: 'Num+', NumpadDecimal: 'Num.', NumpadEnter: 'NumEnter',
  NumpadDivide: 'Num/', ControlRight: 'RCtrl', AltRight: 'RAlt',
  Home: 'Home', End: 'End', PageUp: 'PgUp', PageDown: 'PgDn', Insert: 'Ins', Delete: 'Del',
  ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→',
  MetaLeft: 'LWin', MetaRight: 'RWin', ContextMenu: 'Menu',
};

export function keyLabel(code) {
  if (LABELS[code]) return LABELS[code];
  let m;
  if ((m = /^Key([A-Z])$/.exec(code))) return m[1];
  if ((m = /^Digit(\d)$/.exec(code))) return m[1];
  if ((m = /^Numpad(\d)$/.exec(code))) return 'Num' + m[1];
  if (/^F\d{1,2}$/.test(code)) return code;
  return code;
}

// 역방향: (scancode, e0) → 표시 이름
const REVERSE = {};
for (const [code, [scan, e0]] of Object.entries(KEYS)) {
  REVERSE[`${scan}:${e0 ? 1 : 0}`] = keyLabel(code);
}

/** code → {scan, e0} (없으면 null) */
export function scanInfo(code) {
  const v = KEYS[code];
  return v ? { scan: v[0], e0: !!v[1] } : null;
}

/** KeyboardEvent {code, state} → 키 이름만(방향 제외) */
export function keyName(ev) {
  const e0 = (ev.state & 0x02) ? 1 : 0;
  return REVERSE[`${ev.code}:${e0}`] || `sc${ev.code.toString(16)}`;
}

/** KeyboardEvent {code, state} → 표시 이름(방향 포함) */
export function labelFromKbEvent(ev) {
  const dir = (ev.state & 0x01) ? '↑떼기' : '↓누름';
  return `${keyName(ev)} ${dir}`;
}

/** 브라우저 keydown → 키보드 이벤트 객체 만들기. press=true면 [down,up] 두 개 반환 */
export function kbEventsFromKey(code, mode /* 'down'|'up'|'press' */) {
  const info = scanInfo(code);
  if (!info) return [];
  const base = info.e0 ? 0x02 : 0x00;
  const down = { '$type': 'keyboard', code: info.scan, state: base };
  const up = { '$type': 'keyboard', code: info.scan, state: base | 0x01 };
  if (mode === 'down') return [down];
  if (mode === 'up') return [up];
  return [down, up];
}

// ---------- 트리거 핫키: KeyboardEvent.code → Win32 Virtual-Key ----------
// 한글 IME가 켜져 있으면 e.key가 'Process'(keyCode 229)로 와서 글자/숫자 키를 못 잡는다.
// 물리 키 식별자인 e.code는 IME·키보드 레이아웃과 무관하므로 이걸로 매핑한다.
export const CODE_TO_VK = {
  // 글자 A–Z (VK 0x41–0x5A)
  KeyA: 0x41, KeyB: 0x42, KeyC: 0x43, KeyD: 0x44, KeyE: 0x45, KeyF: 0x46, KeyG: 0x47,
  KeyH: 0x48, KeyI: 0x49, KeyJ: 0x4A, KeyK: 0x4B, KeyL: 0x4C, KeyM: 0x4D, KeyN: 0x4E,
  KeyO: 0x4F, KeyP: 0x50, KeyQ: 0x51, KeyR: 0x52, KeyS: 0x53, KeyT: 0x54, KeyU: 0x55,
  KeyV: 0x56, KeyW: 0x57, KeyX: 0x58, KeyY: 0x59, KeyZ: 0x5A,
  // 숫자 행 0–9 (VK 0x30–0x39)
  Digit0: 0x30, Digit1: 0x31, Digit2: 0x32, Digit3: 0x33, Digit4: 0x34,
  Digit5: 0x35, Digit6: 0x36, Digit7: 0x37, Digit8: 0x38, Digit9: 0x39,
  // 기능키 F1–F12 (VK 0x70–0x7B)
  F1: 0x70, F2: 0x71, F3: 0x72, F4: 0x73, F5: 0x74, F6: 0x75,
  F7: 0x76, F8: 0x77, F9: 0x78, F10: 0x79, F11: 0x7A, F12: 0x7B,
  // 편집/이동
  Space: 0x20, Enter: 0x0D, Escape: 0x1B, Tab: 0x09, Backspace: 0x08,
  ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28,
  Home: 0x24, End: 0x23, PageUp: 0x21, PageDown: 0x22, Insert: 0x2D, Delete: 0x2E,
  // 넘패드
  Numpad0: 0x60, Numpad1: 0x61, Numpad2: 0x62, Numpad3: 0x63, Numpad4: 0x64,
  Numpad5: 0x65, Numpad6: 0x66, Numpad7: 0x67, Numpad8: 0x68, Numpad9: 0x69,
  NumpadMultiply: 0x6A, NumpadAdd: 0x6B, NumpadSubtract: 0x6D,
  NumpadDecimal: 0x6E, NumpadDivide: 0x6F, NumpadEnter: 0x0D,
  // OEM(기호)
  Semicolon: 0xBA, Equal: 0xBB, Comma: 0xBC, Minus: 0xBD, Period: 0xBE, Slash: 0xBF,
  Backquote: 0xC0, BracketLeft: 0xDB, Backslash: 0xDC, BracketRight: 0xDD, Quote: 0xDE,
  // 토글/기타
  CapsLock: 0x14, NumLock: 0x90, ScrollLock: 0x91, Pause: 0x13, PrintScreen: 0x2C,
};

// 단독으로는 트리거가 될 수 없는 순수 모디파이어(누르면 무시 → 조합키의 베이스로만 사용)
const MODIFIER_CODES = new Set([
  'ControlLeft', 'ControlRight', 'ShiftLeft', 'ShiftRight',
  'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight',
]);

const VK_LABELS = {
  0x20: 'Space', 0x0D: 'Enter', 0x1B: 'Esc', 0x09: 'Tab', 0x08: 'Backspace',
  0x25: '←', 0x26: '↑', 0x27: '→', 0x28: '↓',
  0x24: 'Home', 0x23: 'End', 0x21: 'PgUp', 0x22: 'PgDn', 0x2D: 'Ins', 0x2E: 'Del',
  0x6A: 'Num*', 0x6B: 'Num+', 0x6D: 'Num-', 0x6E: 'Num.', 0x6F: 'Num/',
  0xBA: ';', 0xBB: '=', 0xBC: ',', 0xBD: '-', 0xBE: '.', 0xBF: '/',
  0xC0: '`', 0xDB: '[', 0xDC: '\\', 0xDD: ']', 0xDE: "'",
  0x14: 'Caps', 0x90: 'NumLk', 0x91: 'ScrLk', 0x13: 'Pause', 0x2C: 'PrtSc',
};

/** KeyboardEvent → Win32 VK. IME 무관(e.code 우선). 모디파이어 단독/미지원 키는 null. */
export function eventToVk(e) {
  if (MODIFIER_CODES.has(e.code)) return null;
  const vk = CODE_TO_VK[e.code];
  if (vk) return vk;
  // 폴백: e.code가 비어있는 구형 환경 — IME가 아닐 때만 의미 있음
  const k = e.key;
  if (!k || k === 'Process' || k === 'Unidentified') return null;
  if (/^F([1-9]|1[0-2])$/.test(k)) return 0x6F + parseInt(k.slice(1), 10);
  if (/^[a-zA-Z]$/.test(k)) return k.toUpperCase().charCodeAt(0);
  if (/^[0-9]$/.test(k)) return k.charCodeAt(0);
  return null;
}

/** Win32 VK → 표시 이름 */
export function vkLabel(vk) {
  if (VK_LABELS[vk]) return VK_LABELS[vk];
  if (vk >= 0x70 && vk <= 0x7B) return 'F' + (vk - 0x6F);
  if (vk >= 0x60 && vk <= 0x69) return 'Num' + (vk - 0x60);
  if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return String.fromCharCode(vk);
  return 'VK_0x' + vk.toString(16).toUpperCase();
}

// ---------- 마우스 ----------
export const MOUSE = {
  LeftDown: 0x001, LeftUp: 0x002, RightDown: 0x004, RightUp: 0x008,
  MidDown: 0x010, MidUp: 0x020, ScrollV: 0x400,
  MoveRelative: 0x00, MoveAbsolute: 0x01,
};

export function mouseButtonEvent(button /* 'left'|'right'|'middle' */, down) {
  const map = {
    left: down ? MOUSE.LeftDown : MOUSE.LeftUp,
    right: down ? MOUSE.RightDown : MOUSE.RightUp,
    middle: down ? MOUSE.MidDown : MOUSE.MidUp,
  };
  return { '$type': 'mouse', buttonState: map[button] || 0, flags: 0, rolling: 0, x: 0, y: 0 };
}

export function mouseMoveEvent(dx, dy, absolute) {
  return {
    '$type': 'mouse', buttonState: 0, flags: absolute ? MOUSE.MoveAbsolute : MOUSE.MoveRelative,
    rolling: 0, x: dx | 0, y: dy | 0,
  };
}

export function mouseWheelEvent(amount) {
  return { '$type': 'mouse', buttonState: MOUSE.ScrollV, flags: 0, rolling: amount | 0, x: 0, y: 0 };
}

export function mouseLabel(ev) {
  if ((ev.buttonState & MOUSE.ScrollV) || ev.rolling) return `휠 ${ev.rolling}`;
  const btns = [
    [MOUSE.LeftDown, 'L↓'], [MOUSE.LeftUp, 'L↑'], [MOUSE.RightDown, 'R↓'],
    [MOUSE.RightUp, 'R↑'], [MOUSE.MidDown, 'M↓'], [MOUSE.MidUp, 'M↑'],
  ];
  for (const [bit, name] of btns) if (ev.buttonState & bit) return `클릭 ${name}`;
  const abs = (ev.flags & MOUSE.MoveAbsolute) ? '절대' : '상대';
  return `이동 ${abs}(${ev.x},${ev.y})`;
}

// ---------- 게임패드 (Core GamepadControl 과 일치) ----------
export const GAMEPAD_BUTTONS = ['A', 'B', 'X', 'Y', 'LeftShoulder', 'RightShoulder',
  'Back', 'Start', 'Guide', 'LeftThumb', 'RightThumb', 'DpadUp', 'DpadDown', 'DpadLeft', 'DpadRight'];
export const GAMEPAD_AXES = ['LeftStickX', 'LeftStickY', 'RightStickX', 'RightStickY'];
export const GAMEPAD_TRIGGERS = ['LeftTrigger', 'RightTrigger'];
export const GAMEPAD_CONTROLS = [...GAMEPAD_BUTTONS, ...GAMEPAD_AXES, ...GAMEPAD_TRIGGERS];

export function gamepadKind(control) {
  if (GAMEPAD_AXES.includes(control)) return 'axis';
  if (GAMEPAD_TRIGGERS.includes(control)) return 'trigger';
  return 'button';
}

export function gamepadEvent(control, value) {
  return { '$type': 'gamepad', control, value: value | 0 };
}

// ---------- 반복(루프) 블록 ----------
export function loopStartEvent(count) {
  return { '$type': 'loopStart', count: Math.max(1, count | 0 || 2) };
}
export function loopEndEvent() {
  return { '$type': 'loopEnd' };
}

// ---------- 유니버설 입력(키보드·마우스 버튼·게임패드) 공통 ----------
const MOUSE_BTN_NAME = { left: '마우스 좌', right: '마우스 우', middle: '마우스 중' };
export function mouseButtonOf(ev) {
  if (ev.buttonState & (MOUSE.RightDown | MOUSE.RightUp)) return 'right';
  if (ev.buttonState & (MOUSE.MidDown | MOUSE.MidUp)) return 'middle';
  return 'left';
}
export function isMouseButton(ev) {
  if (ev['$type'] !== 'mouse') return false;
  const bits = MOUSE.LeftDown | MOUSE.LeftUp | MOUSE.RightDown | MOUSE.RightUp | MOUSE.MidDown | MOUSE.MidUp;
  return (ev.buttonState & bits) !== 0;
}
/** 입력 블록이 유니버설(키/마우스버튼/패드) 캡처 대상인가 */
export function isUniversalInput(ev) {
  const t = ev['$type'];
  return t === 'keyboard' || t === 'gamepad' || isMouseButton(ev);
}
/** 표시 라벨(키캡/버튼명/패드명) */
export function inputLabel(ev) {
  const t = ev['$type'];
  if (t === 'keyboard') return keyName(ev);
  if (t === 'mouse') return MOUSE_BTN_NAME[mouseButtonOf(ev)] || '마우스';
  if (t === 'gamepad') return 'Pad ' + ev.control;
  return '?';
}
/** 누름(true)/뗌(false) */
export function inputDirection(ev) {
  const t = ev['$type'];
  if (t === 'keyboard') return (ev.state & 1) === 0;
  if (t === 'mouse') return (ev.buttonState & (MOUSE.LeftDown | MOUSE.RightDown | MOUSE.MidDown)) !== 0;
  if (t === 'gamepad') return (ev.value | 0) !== 0;
  return true;
}
/** 방향을 in-place로 설정(키 state 비트 / 마우스 Down↔Up / 패드 value 1↔0) */
export function setInputDirection(ev, down) {
  const t = ev['$type'];
  if (t === 'keyboard') ev.state = (ev.state & 0x02) | (down ? 0 : 1);
  else if (t === 'mouse') Object.assign(ev, mouseButtonEvent(mouseButtonOf(ev), down));
  else if (t === 'gamepad') ev.value = down ? 1 : 0;
}
/** 브라우저 keydown(code) → 키보드 입력 이벤트(방향 지정) */
export function keyEventFromCode(code, down) {
  const info = scanInfo(code);
  if (!info) return null;
  return { '$type': 'keyboard', code: info.scan, state: (info.e0 ? 0x02 : 0) | (down ? 0 : 1) };
}

// ---------- 공통 요약 ----------
export function summarizeEvent(ev) {
  switch (ev['$type']) {
    case 'keyboard': return labelFromKbEvent(ev);
    case 'mouse': return mouseLabel(ev);
    case 'gamepad': return `패드 ${ev.control}=${ev.value}`;
    case 'text': return `텍스트 "${ev.text}"`;
    case 'delay': return '대기(Wait)';
    case 'loopStart': return `반복 시작 ×${ev.count}`;
    case 'loopEnd': return '반복 끝';
    case 'macroRef': return `매크로 실행 "${ev.name || ev.macroId || '?'}"`;
    default: return ev['$type'] || '?';
  }
}

export const TYPE_ICON = {
  keyboard: '⌨', mouse: '🖱', gamepad: '🎮', text: '🔤', delay: '⏱',
  loopStart: '🔁', loopEnd: '🏁', macroRef: '🧩',
};
