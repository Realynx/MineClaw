// Inflated column contents: ChunkSnapshot (ids + meta nibbles) and LightSnapshot (sky/block light + biomes),
// readers of src/MineClaw.World/Chunks/ChunkSnapshot.cs and LightSnapshot.cs.

import { McwcError } from './mcwc';

/** Cells of a 128-high column (b1.8.1); a 1.12.2 column holds twice as many. */
export const HALF_VOLUME = 16 * 128 * 16;
export const SMALL_PACKED = HALF_VOLUME + HALF_VOLUME / 2; // 49 152
export const LARGE_PACKED = 2 * SMALL_PACKED; // 98 304
export const SECTION_CELLS = 4096;

/** ChunkColumn.Index: two stacked 128-high halves, x-major then z, y fastest. */
export function cellIndex(lx: number, y: number, lz: number): number {
  return ((y >> 7) << 15) | ((lx * 16 + lz) * 128 + (y & 127));
}

export interface ColumnBlocks {
  ids: Uint8Array;
  meta: Uint8Array;
  /** 128 or 256. */
  height: number;
}

/** Unpacks ChunkSnapshot.Encode's inflated bytes: V ids, then V/2 bytes of meta nibbles (low nibble = even cell). */
export function unpackSnapshot(packed: Uint8Array): ColumnBlocks {
  if (packed.length !== SMALL_PACKED && packed.length !== LARGE_PACKED) {
    throw new McwcError(packed.length > LARGE_PACKED ? 'chunk snapshot too long' : 'truncated chunk snapshot');
  }
  const v = packed.length === SMALL_PACKED ? HALF_VOLUME : 2 * HALF_VOLUME;
  const ids = packed.slice(0, v);
  const meta = new Uint8Array(v);
  for (let i = 0; i < v; i += 2) {
    const b = packed[v + (i >> 1)];
    meta[i] = b & 0x0f;
    meta[i + 1] = b >> 4;
  }
  return { ids, meta, height: v / 256 };
}

/** A live column's light (LightSnapshot): per 16³ section a uniform packed value or 4096 bytes `sky<<4 | block`. */
export interface ColumnLight {
  sections: number;
  uniform: Uint8Array;
  full: Array<Uint8Array | null>;
  /** 256 biome ids, index lz*16+lx (1.12.2), or null. */
  biomes: Uint8Array | null;
}

/** Packed light `sky<<4 | block` of a local cell; above the covered height open sky (0xF0). */
export function lightPacked(l: ColumnLight, lx: number, y: number, lz: number): number {
  const s = y >> 4;
  if (s < 0) return 0;
  if (s >= l.sections) return 0xf0;
  const f = l.full[s];
  return f ? f[((lx & 15) << 8) | ((lz & 15) << 4) | (y & 15)] : l.uniform[s];
}

/** Parses LightSnapshot.Encode's inflated bytes. */
export function parseLight(b: Uint8Array): ColumnLight {
  let o = 0;
  const byte = () => {
    if (o >= b.length) throw new McwcError('truncated light snapshot');
    return b[o++];
  };
  if (byte() !== 1) throw new McwcError('light snapshot version unsupported');
  const sections = byte();
  const flags = byte();
  if (sections !== 8 && sections !== 16) throw new McwcError(`light snapshot: ${sections} sections`);
  const uniform = new Uint8Array(sections);
  const full: Array<Uint8Array | null> = new Array(sections).fill(null);
  const block = (len: number) => {
    if (o + len > b.length) throw new McwcError('truncated light snapshot');
    o += len;
    return b.slice(o - len, o);
  };
  for (let s = 0; s < sections; s++) {
    const kind = byte();
    if (kind === 0) uniform[s] = byte();
    else if (kind === 1) full[s] = block(SECTION_CELLS);
    else throw new McwcError('light snapshot: bad section kind');
  }
  const biomes = flags & 1 ? block(256) : null;
  if (o !== b.length) throw new McwcError('light snapshot too long');
  return { sections, uniform, full, biomes };
}
