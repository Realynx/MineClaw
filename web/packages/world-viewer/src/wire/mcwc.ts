// The supervisor's binary chunk payload, a byte-for-byte reader of src/MineClaw.World/Chunks/ColumnPayload.cs (little-endian):
//   "MCWC" u16 version(1|2) u16 flags(0) i32 dim i32 count(<=1024)
//   count × { i32 cx, i32 cz, i64 hash, i64 seen_unix_ms, u8 live(0|1), u8 by_len, by_len×utf8, i32 data_len, data
//             [v2: i32 light_len, light] }
// data_len 0 = the column is unknown (drop it); light_len 0 = none (remembered). Trailing bytes and truncation are errors.

export const MCWC_MAGIC = 0x4357434d; // "MCWC" read as a little-endian u32
export const MCWC_VERSION = 1;
export const MCWC_VERSION_LIGHT = 2;
export const MCWC_MAX_COLUMNS = 1024;
export const MCWC_MAX_COLUMN_BYTES = 256 * 1024;
const HEADER = 16;

export class McwcError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'McwcError';
  }
}

export interface McwcEntry {
  cx: number;
  cz: number;
  /** The exact signed 64-bit hash. */
  hash: bigint;
  seenMs: number;
  live: boolean;
  by: string | null;
  /** ChunkSnapshot.Encode bytes (raw DEFLATE); empty = missing. A view into the payload. */
  data: Uint8Array;
  /** LightSnapshot.Encode bytes (raw DEFLATE), or null. */
  light: Uint8Array | null;
}

export interface McwcPayload {
  version: number;
  dim: number;
  entries: McwcEntry[];
}

const utf8 = new TextDecoder('utf-8');

export function decodeMcwc(buf: ArrayBuffer | Uint8Array): McwcPayload {
  const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const n = bytes.byteLength;
  if (n < HEADER || dv.getUint32(0, true) !== MCWC_MAGIC) throw new McwcError('not a column payload (bad magic)');
  const version = dv.getUint16(4, true);
  if (version !== MCWC_VERSION && version !== MCWC_VERSION_LIGHT) throw new McwcError(`column payload version ${version} unsupported (want 1 or 2)`);
  const dim = dv.getInt32(8, true);
  const count = dv.getInt32(12, true);
  if (count < 0 || count > MCWC_MAX_COLUMNS) throw new McwcError(`column payload count ${count} out of range`);
  const need = (at: number, k: number) => {
    if (k < 0 || at + k > n) throw new McwcError('column payload truncated');
  };
  const entries: McwcEntry[] = new Array(count);
  let o = HEADER;
  for (let i = 0; i < count; i++) {
    need(o, 4 + 4 + 8 + 8 + 1 + 1);
    const cx = dv.getInt32(o, true); o += 4;
    const cz = dv.getInt32(o, true); o += 4;
    const hash = dv.getBigInt64(o, true); o += 8;
    const seenMs = Number(dv.getBigInt64(o, true)); o += 8;
    const live = bytes[o++];
    if (live > 1) throw new McwcError('column payload: bad live flag');
    const nameLen = bytes[o++];
    need(o, nameLen + 4);
    const by = nameLen === 0 ? null : utf8.decode(bytes.subarray(o, o + nameLen)); o += nameLen;
    const len = dv.getInt32(o, true); o += 4;
    if (len < 0 || len > MCWC_MAX_COLUMN_BYTES) throw new McwcError(`column payload: column size ${len} out of range`);
    need(o, len);
    const data = bytes.subarray(o, o + len); o += len;
    let light: Uint8Array | null = null;
    if (version === MCWC_VERSION_LIGHT) {
      need(o, 4);
      const ll = dv.getInt32(o, true); o += 4;
      if (ll < 0 || ll > MCWC_MAX_COLUMN_BYTES) throw new McwcError(`column payload: light size ${ll} out of range`);
      need(o, ll);
      if (ll > 0) light = bytes.subarray(o, o + ll);
      o += ll;
    }
    entries[i] = { cx, cz, hash, seenMs, live: live === 1, by, data, light };
  }
  if (o !== n) throw new McwcError('column payload: trailing bytes');
  return { version, dim, entries };
}
