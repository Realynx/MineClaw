/**
 * The Design Studio's mine terrain: a line-for-line port of the bot's MineClaw's own generator
 * (src/MineClaw.Bot/Blueprints/Mine/MineTerrain.cs). Integer math only (fixed-point value noise, a 32-bit hash, the
 * mulberry32 stream), so the browser and the bot generate the same world for a seed and a version, bit for bit; a golden hash
 * in each side's tests holds them together. Per column: rolling hills (grass at y 58..81), 3-4 dirt layers, stone, a bedrock
 * floor at y 0 patchy up to y 4. Per 16×16 chunk, from the version's ore table: stone pockets, then ore veins at their
 * heights, then now and then one light cave; every feature stays inside its own chunk.
 */
import { oreTable, type OreBand, type OreVersion } from './oreTable';

export const TERRAIN_HEIGHT = 128;
export const STONE = 1,
  GRASS = 2,
  DIRT = 3,
  BEDROCK = 7;

const SALT_OCTAVE1 = 0x5eed0001,
  SALT_OCTAVE2 = 0x5eed0002,
  SALT_OCTAVE3 = 0x5eed0003,
  SALT_DIRT = 0xd1a7,
  SALT_BEDROCK = 0xbedc0c,
  SALT_FEATURE = 0x0be5eed,
  SALT_CAVE = 0xca7e;

const INT64_MIN = -(2n ** 63n),
  INT64_MAX = 2n ** 63n - 1n;

/** The seed as stored (trimmed, at most 32 chars; empty = "0"). */
export function normalizeSeed(seed: string | null | undefined): string {
  let t = (seed ?? '').trim();
  if (t.length > 32) t = t.slice(0, 32);
  return t.length === 0 ? '0' : t;
}

/** A random numeric seed. */
export function randomSeed(): string {
  const hi = BigInt(Math.floor(Math.random() * 0x7fffffff));
  const lo = BigInt(Math.floor(Math.random() * 0xffffffff));
  const n = (hi << 32n) | lo;
  return (n === 0n ? 1n : n).toString();
}

/** The seed's 64 bits as [low, high] words: a whole number as it is, other text by Java's string hash, sign-extended. */
export function seedBits(seed: string): [number, number] {
  const t = (seed ?? '').trim();
  if (/^[+-]?\d+$/.test(t)) {
    const n = BigInt(t);
    if (n >= INT64_MIN && n <= INT64_MAX) return [Number(BigInt.asUintN(32, n)), Number(BigInt.asUintN(32, n >> 32n))];
  }
  let h = 0;
  for (let i = 0; i < t.length; i++) h = (Math.imul(31, h) + t.charCodeAt(i)) | 0;
  return [h >>> 0, h < 0 ? 0xffffffff : 0];
}

export function mix(h: number): number {
  h ^= h >>> 16;
  h = Math.imul(h, 0x7feb352d);
  h ^= h >>> 15;
  h = Math.imul(h, 0x846ca68b);
  h ^= h >>> 16;
  return h >>> 0;
}

/** A 32-bit random stream (mulberry32), as the bot's. */
export class Rng {
  private a: number;
  constructor(state: number) {
    this.a = state | 0;
  }
  next(): number {
    this.a = (this.a + 0x6d2b79f5) | 0;
    let t = Math.imul(this.a ^ (this.a >>> 15), 1 | this.a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return (t ^ (t >>> 14)) >>> 0;
  }
  below(n: number): number {
    return n <= 1 ? 0 : this.next() % n;
  }
}

const floorDiv = (a: number, b: number): number => Math.floor(a / b);
const index = (lx: number, y: number, lz: number): number => (y * 16 + lz) * 16 + lx;

export class MineTerrain {
  readonly table: OreVersion;
  private readonly lo: number;
  private readonly hi: number;
  private readonly chunks = new Map<string, Uint16Array>();

  constructor(
    readonly seed: string,
    readonly version: string,
  ) {
    this.table = oreTable(version);
    [this.lo, this.hi] = seedBits(seed);
  }

  private static cache = new Map<string, MineTerrain>();

  /** The terrain of a seed and a version (a few kept). */
  static for(seed: string | null | undefined, version: string): MineTerrain {
    const s = normalizeSeed(seed);
    const k = `${s}|${version}`;
    let t = MineTerrain.cache.get(k);
    if (!t) {
      if (MineTerrain.cache.size > 4) MineTerrain.cache.clear();
      t = new MineTerrain(s, version);
      MineTerrain.cache.set(k, t);
    }
    return t;
  }

  h(a: number, b: number, c: number, salt: number): number {
    let h = mix((this.lo ^ salt) >>> 0);
    h = mix(h ^ this.hi);
    h = mix(h ^ a);
    h = mix(h ^ b);
    return mix(h ^ c);
  }

  private noise(x: number, z: number, s: number, salt: number): number {
    const i = floorDiv(x, s),
      j = floorDiv(z, s);
    const tx = x - i * s,
      tz = z - j * s,
      s3 = s * s * s;
    const ux = Math.floor(((3 * s - 2 * tx) * tx * tx * 65536) / s3),
      uz = Math.floor(((3 * s - 2 * tz) * tz * tz * 65536) / s3);
    const v00 = this.h(i, j, 0, salt) >>> 16,
      v10 = this.h(i + 1, j, 0, salt) >>> 16;
    const v01 = this.h(i, j + 1, 0, salt) >>> 16,
      v11 = this.h(i + 1, j + 1, 0, salt) >>> 16;
    const a = v00 + floorDiv((v10 - v00) * ux, 65536),
      b = v01 + floorDiv((v11 - v01) * ux, 65536);
    return a + floorDiv((b - a) * uz, 65536);
  }

  /** The y of the grass block of a column (58..81). */
  surface(x: number, z: number): number {
    const n =
      55 * this.noise(x, z, 48, SALT_OCTAVE1) + 30 * this.noise(x, z, 24, SALT_OCTAVE2) + 15 * this.noise(x, z, 12, SALT_OCTAVE3);
    return 58 + Math.floor((n * 24) / 6553600);
  }

  /** The generated state at a world cell as [id, meta]; above the column air, below y 0 bedrock. */
  block(x: number, y: number, z: number): [number, number] {
    if (y >= TERRAIN_HEIGHT) return [0, 0];
    if (y < 0) return [BEDROCK, 0];
    const v = this.chunk(x >> 4, z >> 4)[index(x & 15, y, z & 15)]!;
    return [v >> 4, v & 15];
  }

  /** The state as one number (id << 4 | meta). */
  state(x: number, y: number, z: number): number {
    if (y >= TERRAIN_HEIGHT) return 0;
    if (y < 0) return BEDROCK << 4;
    return this.chunk(x >> 4, z >> 4)[index(x & 15, y, z & 15)]!;
  }

  /** Solid generated ground (anything but air). */
  solid(x: number, y: number, z: number): boolean {
    return this.state(x, y, z) >> 4 !== 0;
  }

  /** A chunk's states (id << 4 | meta), indexed (y·16 + lz)·16 + lx. */
  chunk(cx: number, cz: number): Uint16Array {
    const k = `${cx},${cz}`;
    let c = this.chunks.get(k);
    if (!c) {
      c = this.generate(cx, cz);
      this.chunks.set(k, c);
    }
    return c;
  }

  private generate(cx: number, cz: number): Uint16Array {
    const cells = new Uint16Array(16 * 16 * TERRAIN_HEIGHT);
    const tops = new Int32Array(256);
    const t = this.table;
    for (let lz = 0; lz < 16; lz++)
      for (let lx = 0; lx < 16; lx++) {
        const x = cx * 16 + lx,
          z = cz * 16 + lz;
        const top = this.surface(x, z);
        tops[lz * 16 + lx] = top;
        const dirt = 3 + (this.h(x, z, 0, SALT_DIRT) % 2);
        for (let y = 0; y <= top && y < TERRAIN_HEIGHT; y++) {
          let id = y === top ? GRASS : y >= top - dirt ? DIRT : STONE;
          if (y <= t.bedrockTop && (y === t.bedrockFloor || this.h(x, y, z, SALT_BEDROCK) % 5 >= y)) id = BEDROCK;
          cells[index(lx, y, lz)] = id << 4;
        }
      }
    const features: OreBand[] = [...t.pockets, ...t.ores];
    for (let k = 0; k < features.length; k++) {
      const f = features[k]!;
      if (f.veins <= 0 || f.size <= 0) continue;
      const pocket = k < t.pockets.length;
      const state = (f.block << 4) | f.meta;
      const rng = new Rng(this.h(cx, cz, k, SALT_FEATURE));
      for (let v = 0; v < f.veins; v++) {
        let x = rng.below(16),
          z = rng.below(16);
        let y: number;
        if (f.shape === 'triangle') {
          const a = rng.below(f.spread);
          const b = rng.below(f.spread);
          y = f.center - f.spread + a + b;
        } else y = f.min + rng.below(f.max - f.min + 1);
        for (let s = 0; s < f.size; s++) {
          put(cells, x, y, z, state);
          if (pocket) {
            put(cells, x + 1, y, z, state);
            put(cells, x - 1, y, z, state);
            put(cells, x, y + 1, z, state);
            put(cells, x, y - 1, z, state);
            put(cells, x, y, z + 1, state);
            put(cells, x, y, z - 1, state);
          }
          switch (rng.below(6)) {
            case 0:
              x++;
              break;
            case 1:
              x--;
              break;
            case 2:
              y++;
              break;
            case 3:
              y--;
              break;
            case 4:
              z++;
              break;
            default:
              z--;
          }
        }
      }
    }
    const cr = new Rng(this.h(cx, cz, 0, SALT_CAVE));
    if (cr.below(3) === 0) {
      let x = 3 + cr.below(10);
      let z = 3 + cr.below(10);
      let y = 16 + cr.below(40);
      let dir = cr.below(4);
      for (let s = 0; s < 40; s++) {
        for (let dz = -1; dz <= 1; dz++)
          for (let dx = -1; dx <= 1; dx++)
            for (let dy = 0; dy <= 1; dy++) {
              const lx = x + dx,
                lz = z + dz,
                yy = y + dy;
              if (lx < 0 || lx > 15 || lz < 0 || lz > 15 || yy < 1 || yy >= tops[lz * 16 + lx]! - 6) continue;
              const i = index(lx, yy, lz);
              if (cells[i]! >> 4 !== BEDROCK) cells[i] = 0;
            }
        if (cr.below(4) === 0) dir = cr.below(4);
        if (cr.below(5) === 0) y += cr.below(3) - 1;
        switch (dir) {
          case 0:
            x++;
            break;
          case 1:
            x--;
            break;
          case 2:
            z++;
            break;
          default:
            z--;
        }
        x = Math.max(1, Math.min(14, x));
        z = Math.max(1, Math.min(14, z));
        y = Math.max(8, Math.min(70, y));
      }
    }
    return cells;
  }

  /** FNV-1a over the plot (y, z, x order): the golden value both languages' tests check. */
  plotHash(size = 64): number {
    let h = 2166136261;
    for (let y = 0; y < TERRAIN_HEIGHT; y++)
      for (let z = 0; z < size; z++)
        for (let x = 0; x < size; x++) h = Math.imul(h ^ this.state(x, y, z), 16777619) >>> 0;
    return h;
  }

  /** The lowest and highest surface over the plot. */
  surfaceRange(size = 64): { min: number; max: number } {
    let lo = Infinity,
      hi = -Infinity;
    for (let z = 0; z < size; z++)
      for (let x = 0; x < size; x++) {
        const s = this.surface(x, z);
        if (s < lo) lo = s;
        if (s > hi) hi = s;
      }
    return { min: lo, max: hi };
  }

  /** The highest surface over a box's columns (a shaft's top: "surface"). */
  surfaceOver(x0: number, z0: number, x1: number, z1: number): number {
    let hi = -Infinity;
    for (let z = Math.min(z0, z1); z <= Math.max(z0, z1); z++)
      for (let x = Math.min(x0, x1); x <= Math.max(x0, x1); x++) hi = Math.max(hi, this.surface(x, z));
    return hi;
  }
}

function put(cells: Uint16Array, lx: number, y: number, lz: number, state: number): void {
  if (lx < 0 || lx > 15 || lz < 0 || lz > 15 || y < 0 || y >= TERRAIN_HEIGHT) return;
  const i = index(lx, y, lz);
  if (cells[i] === STONE << 4) cells[i] = state;
}
