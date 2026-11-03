// 64-bit hashes of the world payload, exactly as the C# side computes them (fixtures and tests; the viewer itself only
// compares hashes the server sent). FNV-1a 64 runs on two 32-bit halves so no BigInt is needed in the hot loop.

import type { WireHash } from '../types';

const OFFSET_LO = 0x84222325;
const OFFSET_HI = 0xcbf29ce4;

class Fnv {
  lo = OFFSET_LO;
  hi = OFFSET_HI;

  /** h *= 1099511628211 (2^40 + 0x1B3), mod 2^64. */
  mul(): void {
    const lo = this.lo;
    const a = lo * 0x1b3;
    const newLo = a >>> 0;
    const carry = Math.floor(a / 4294967296);
    this.hi = (this.hi * 0x1b3 + carry + ((lo << 8) >>> 0)) >>> 0;
    this.lo = newLo;
  }

  byte(b: number): void {
    this.lo = (this.lo ^ b) >>> 0;
    this.mul();
  }

  /** h = (h ^ w) * prime for the little-endian u64 at bytes[i..i+8). */
  word(bytes: Uint8Array, i: number): void {
    this.lo = (this.lo ^ (bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24))) >>> 0;
    this.hi = (this.hi ^ (bytes[i + 4] | (bytes[i + 5] << 8) | (bytes[i + 6] << 16) | (bytes[i + 7] << 24))) >>> 0;
    this.mul();
  }

  xorU64(lo: number, hi: number): void {
    this.lo = (this.lo ^ lo) >>> 0;
    this.hi = (this.hi ^ hi) >>> 0;
  }

  big(): bigint {
    return BigInt.asIntN(64, (BigInt(this.hi) << 32n) | BigInt(this.lo));
  }
}

/** ChunkSnapshot.ComputeHash: FNV-1a 64 over the ids, then over `meta & 0x0F`. Signed like C#'s `long`. */
export function snapshotHash(ids: Uint8Array, meta: Uint8Array): bigint {
  const h = new Fnv();
  for (let i = 0; i < ids.length; i++) h.byte(ids[i]);
  for (let i = 0; i < meta.length; i++) h.byte(meta[i] & 0x0f);
  return h.big();
}

/** LightSnapshot.ComputeHash over sections (uniform value or full bytes as u64 words) and biomes, then h ^ h>>>29. */
export function lightHash(sections: Array<{ uniform: number } | { full: Uint8Array }>, biomes: Uint8Array | null): bigint {
  const h = new Fnv();
  h.xorU64(sections.length, 0);
  for (const s of sections) {
    if ('full' in s) for (let i = 0; i < s.full.length; i += 8) h.word(s.full, i);
    else {
      h.xorU64(0x100 | s.uniform, 0);
      h.mul();
    }
  }
  if (biomes) for (let i = 0; i < biomes.length; i += 8) h.word(biomes, i);
  const lo = ((h.lo >>> 29) | (h.hi << 3)) >>> 0;
  const hi = h.hi >>> 29;
  h.xorU64(lo, hi);
  return h.big();
}

/** LightSnapshot.Combine: the delta key of a live column with light. */
export function combineHash(blockHash: bigint, lightHashValue: bigint): bigint {
  return BigInt.asIntN(64, BigInt.asIntN(64, blockHash * -7046029254386353131n) ^ lightHashValue);
}

/**
 * The comparable form of a column hash. JSON numbers above 2^53 arrive rounded to the nearest double, so every hash (binary
 * i64 from MCWC, decimal string, JSON number) is reduced to that same double: equal i64s always compare equal, and two
 * different hashes collide only if they round together (about 1 in 2^11 for neighbouring values, never in practice for
 * unrelated FNV outputs).
 */
export function hashKey(h: WireHash): number {
  if (typeof h === 'number') return h;
  if (typeof h === 'bigint') return Number(h);
  const t = h.trim();
  return /^-?\d+$/.test(t) ? Number(BigInt(t)) : Number(t);
}
