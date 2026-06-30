// 의존성 없는 ZIP 읽기/쓰기 (WebView2/모던 브라우저).
// 쓰기: 저장(stored, 무압축) 방식 — 단순·안정적. 매크로 JSON은 작아서 압축 이득이 적다.
// 읽기: stored(0) + deflate(8) 모두 지원(외부 압축 프로그램이 만든 zip 호환). deflate는 DecompressionStream 사용.

const enc = new TextEncoder();
const dec = new TextDecoder();

// CRC32
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c >>> 0;
  }
  return t;
})();
function crc32(bytes) {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8);
  return (c ^ 0xFFFFFFFF) >>> 0;
}

// entries: [{ name, text }]  →  Blob(application/zip)
export function zipBlob(entries) {
  const files = entries.map((e) => {
    const nameBytes = enc.encode(e.name);
    const data = enc.encode(e.text);
    return { nameBytes, data, crc: crc32(data) };
  });
  const chunks = [];
  const central = [];
  let offset = 0;
  const u16 = (n) => { const b = new Uint8Array(2); new DataView(b.buffer).setUint16(0, n, true); return b; };
  const u32 = (n) => { const b = new Uint8Array(4); new DataView(b.buffer).setUint32(0, n >>> 0, true); return b; };

  for (const f of files) {
    const size = f.data.length;
    // 로컬 파일 헤더
    const local = concat([
      u32(0x04034b50), u16(20), u16(0x0800), u16(0), u16(0), u16(0), // 0x0800: UTF-8 파일명
      u32(f.crc), u32(size), u32(size), u16(f.nameBytes.length), u16(0),
      f.nameBytes, f.data,
    ]);
    chunks.push(local);
    // 중앙 디렉터리 항목
    central.push(concat([
      u32(0x02014b50), u16(20), u16(20), u16(0x0800), u16(0), u16(0), u16(0),
      u32(f.crc), u32(size), u32(size), u16(f.nameBytes.length), u16(0), u16(0), u16(0), u16(0),
      u32(0), u32(offset), f.nameBytes,
    ]));
    offset += local.length;
  }
  const centralBytes = concat(central);
  const centralOffset = offset;
  const eocd = concat([
    u32(0x06054b50), u16(0), u16(0), u16(files.length), u16(files.length),
    u32(centralBytes.length), u32(centralOffset), u16(0),
  ]);
  return new Blob([...chunks, centralBytes, eocd], { type: 'application/zip' });
}

function concat(parts) {
  let len = 0;
  for (const p of parts) len += p.length;
  const out = new Uint8Array(len);
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

async function inflateRaw(bytes) {
  const ds = new DecompressionStream('deflate-raw');
  const stream = new Blob([bytes]).stream().pipeThrough(ds);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

// ArrayBuffer/Uint8Array(zip)  →  [{ name, text }]
export async function unzip(buffer) {
  const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  // EOCD(0x06054b50) 뒤에서부터 탐색
  let eocd = -1;
  for (let i = bytes.length - 22; i >= 0; i--) {
    if (dv.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('zip 형식이 아닙니다.');
  const count = dv.getUint16(eocd + 10, true);
  let p = dv.getUint32(eocd + 16, true); // 중앙 디렉터리 시작 오프셋
  const out = [];
  for (let n = 0; n < count; n++) {
    if (dv.getUint32(p, true) !== 0x02014b50) break;
    const method = dv.getUint16(p + 10, true);
    const compSize = dv.getUint32(p + 20, true);
    const nameLen = dv.getUint16(p + 28, true);
    const extraLen = dv.getUint16(p + 30, true);
    const commentLen = dv.getUint16(p + 32, true);
    const localOff = dv.getUint32(p + 42, true);
    const name = dec.decode(bytes.subarray(p + 46, p + 46 + nameLen));
    // 로컬 헤더에서 실제 데이터 시작 위치 계산
    const lNameLen = dv.getUint16(localOff + 26, true);
    const lExtraLen = dv.getUint16(localOff + 28, true);
    const dataStart = localOff + 30 + lNameLen + lExtraLen;
    const comp = bytes.subarray(dataStart, dataStart + compSize);
    if (!name.endsWith('/')) { // 디렉터리 항목 제외
      let data;
      if (method === 0) data = comp;
      else if (method === 8) data = await inflateRaw(comp);
      else throw new Error(`지원하지 않는 압축 방식(${method}): ${name}`);
      out.push({ name, text: dec.decode(data) });
    }
    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}
