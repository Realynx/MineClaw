// Writers mirroring the C# encoders (ColumnPayload.Encode, ChunkSnapshot.Encode, LightSnapshot.Encode) for fixtures, tests and
// the demo. The compressed bytes differ from .NET's DeflateStream, the inflated bytes and the container are identical.

import { deflateRaw } from 'pako';
import { HALF_VOLUME, SECTION_CELLS } from './chunk';
import { MCWC_MAGIC, MCWC_MAX_COLUMN_BYTES, MCWC_MAX_COLUMNS, MCWC_VERSION, MCWC_VERSION_LIGHT } from './mcwc';
import { lightHash, snapshotHash } from './hash';

/** ChunkSnapshot.Encode: raw DEFLATE of ids followed by meta packed two nibbles per byte. */
export function encodeSnapshot(ids: Uint8Array, meta: Uint8Array): Uint8Array {
  const n = ids.length;
  if ((n !== HALF_VOLUME && n !== 2 * HALF_VOLUME) || meta.length !== n) throw new Error('snapshot arrays must hold one column');
  const packed = new Uint8Array(n + n / 2);
  packed.set(ids, 0);
  for (let i = 0; i < n; i += 2) packed[n + i / 2] = (meta[i] & 0x0f) | ((meta[i + 1] & 0x0f) << 4);
  return deflateRaw(packed);
}

export type LightSection = { uniform: number } | { full: Uint8Array };

/** Builds LightSnapshot sections from per-cell levels (LightSnapshot.From): uniform where every cell agrees. */
export function lightSections(height: number, sky: (x: number, y: number, z: number) => number, block: (x: number, y: number, z: number) => number): LightSection[] {
  const out: LightSection[] = [];
  for (let s = 0; s < height >> 4; s++) {
    const f = new Uint8Array(SECTION_CELLS);
    let uniform = true;
    for (let x = 0; x < 16; x++)
      for (let z = 0; z < 16; z++)
        for (let y = 0; y < 16; y++) {
          const v = ((sky(x, s * 16 + y, z) & 15) << 4) | (block(x, s * 16 + y, z) & 15);
          f[(x << 8) | (z << 4) | y] = v;
        }
    for (let i = 1; i < f.length; i++) if (f[i] !== f[0]) { uniform = false; break; }
    out.push(uniform ? { uniform: f[0] } : { full: f });
  }
  return out;
}

/** LightSnapshot.Encode: raw DEFLATE of `u8 1, u8 sections, u8 flags`, the sections, then the biomes. */
export function encodeLight(sections: LightSection[], biomes: Uint8Array | null = null): Uint8Array {
  let size = 3;
  for (const s of sections) size += 'full' in s ? 1 + SECTION_CELLS : 2;
  if (biomes) size += 256;
  const b = new Uint8Array(size);
  let o = 0;
  b[o++] = 1;
  b[o++] = sections.length;
  b[o++] = biomes ? 1 : 0;
  for (const s of sections) {
    if ('full' in s) {
      b[o++] = 1;
      b.set(s.full, o);
      o += SECTION_CELLS;
    } else {
      b[o++] = 0;
      b[o++] = s.uniform;
    }
  }
  if (biomes) b.set(biomes.subarray(0, 256), o);
  return deflateRaw(b);
}

export interface EncodeEntry {
  cx: number;
  cz: number;
  hash: bigint;
  seenMs: number;
  by?: string | null;
  live: boolean;
  data: Uint8Array;
  light?: Uint8Array | null;
}

/** ColumnPayload.Encode: version 2 when any entry carries light (then every entry gets a light_len). */
export function encodeMcwc(dim: number, columns: EncodeEntry[]): ArrayBuffer {
  if (columns.length > MCWC_MAX_COLUMNS) throw new Error(`at most ${MCWC_MAX_COLUMNS} columns per payload`);
  const light = columns.some((c) => c.light != null);
  const enc = new TextEncoder();
  const names = columns.map((c) => {
    const by = c.by ?? '';
    const b = enc.encode(by.length > 64 ? by.slice(0, 64) : by);
    return b.length > 255 ? b.subarray(0, 255) : b;
  });
  let size = 16;
  columns.forEach((c, i) => {
    if (c.data.length > MCWC_MAX_COLUMN_BYTES) throw new Error('column data too large');
    size += 4 + 4 + 8 + 8 + 1 + 1 + names[i].length + 4 + c.data.length;
    if (light) size += 4 + (c.light?.length ?? 0);
  });
  const buf = new ArrayBuffer(size);
  const dv = new DataView(buf);
  const u8 = new Uint8Array(buf);
  dv.setUint32(0, MCWC_MAGIC, true);
  dv.setUint16(4, light ? MCWC_VERSION_LIGHT : MCWC_VERSION, true);
  dv.setUint16(6, 0, true);
  dv.setInt32(8, dim, true);
  dv.setInt32(12, columns.length, true);
  let o = 16;
  columns.forEach((c, i) => {
    dv.setInt32(o, c.cx, true); o += 4;
    dv.setInt32(o, c.cz, true); o += 4;
    dv.setBigInt64(o, c.hash, true); o += 8;
    dv.setBigInt64(o, BigInt(Math.trunc(c.seenMs)), true); o += 8;
    u8[o++] = c.live ? 1 : 0;
    u8[o++] = names[i].length;
    u8.set(names[i], o); o += names[i].length;
    dv.setInt32(o, c.data.length, true); o += 4;
    u8.set(c.data, o); o += c.data.length;
    if (light) {
      const l = c.light ?? new Uint8Array(0);
      dv.setInt32(o, l.length, true); o += 4;
      u8.set(l, o); o += l.length;
    }
  });
  return buf;
}

/** One fixture column: ids/meta in ChunkColumn.Index order plus optional light, encoded with its hashes. */
export function encodeColumn(ids: Uint8Array, meta: Uint8Array, light: LightSection[] | null, biomes: Uint8Array | null = null): {
  data: Uint8Array;
  light: Uint8Array | null;
  blockHash: bigint;
  lightHash: bigint | null;
} {
  return {
    data: encodeSnapshot(ids, meta),
    light: light ? encodeLight(light, biomes) : null,
    blockHash: snapshotHash(ids, meta),
    lightHash: light ? lightHash(light, biomes) : null,
  };
}
