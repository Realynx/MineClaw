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
  const c = new Cursor(bytes, dv, HEADER);
  const entries: McwcEntry[] = new Array(count);
  for (let i = 0; i < count; i++) entries[i] = readEntry(c, version === MCWC_VERSION_LIGHT);
  if (c.o !== n) throw new McwcError('column payload: trailing bytes');
  return { version, dim, entries };
}

/** A read position in a payload; every read past its end throws "truncated". */
class Cursor {
  constructor(
    readonly bytes: Uint8Array,
    readonly dv: DataView,
    public o: number,
  ) {}
  need(k: number): void {
    if (k < 0 || this.o + k > this.bytes.byteLength) throw new McwcError('column payload truncated');
  }
  i32(): number {
    const v = this.dv.getInt32(this.o, true);
    this.o += 4;
    return v;
  }
  i64(): bigint {
    const v = this.dv.getBigInt64(this.o, true);
    this.o += 8;
    return v;
  }
  u8(): number {
    return this.bytes[this.o++];
  }
  take(len: number): Uint8Array {
    const v = this.bytes.subarray(this.o, this.o + len);
    this.o += len;
    return v;
  }
}

function readEntry(c: Cursor, withLight: boolean): McwcEntry {
  c.need(4 + 4 + 8 + 8 + 1 + 1);
  const cx = c.i32();
  const cz = c.i32();
  const hash = c.i64();
  const seenMs = Number(c.i64());
  const live = c.u8();
  if (live > 1) throw new McwcError('column payload: bad live flag');
  const nameLen = c.u8();
  c.need(nameLen + 4);
  const name = c.take(nameLen);
  const by = nameLen === 0 ? null : utf8.decode(name);
  const len = c.i32();
  if (len < 0 || len > MCWC_MAX_COLUMN_BYTES) throw new McwcError(`column payload: column size ${len} out of range`);
  c.need(len);
  const data = c.take(len);
  const light = withLight ? readLight(c) : null;
  return { cx, cz, hash, seenMs, live: live === 1, by, data, light };
}

/** A version-2 column's light bytes (null when empty). */
function readLight(c: Cursor): Uint8Array | null {
  c.need(4);
  const ll = c.i32();
  if (ll < 0 || ll > MCWC_MAX_COLUMN_BYTES) throw new McwcError(`column payload: light size ${ll} out of range`);
  c.need(ll);
  const light = c.take(ll);
  return ll > 0 ? light : null;
}
