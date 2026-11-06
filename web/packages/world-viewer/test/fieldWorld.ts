// A seeded wheat farm for the crop-light tests (and the headless screenshots): a 32×32 field of farmland with wheat of
// mixed growth stages, a water cell in the middle of every 9×9 plot, inside a ring of grass; the bots' harvest / replant /
// growth / trampling as a seeded list of single-block deltas; and an independent reference light (vanilla's rules written
// out plainly: sky straight down until the first block that takes light away, then a relaxation to the fixpoint, each step
// losing max(1, opacity); block light likewise from the emitters) standing in for the server's own light.

import { cellIndex, SECTION_CELLS } from '../src/wire/chunk';
import { encodeColumn, encodeMcwc, type EncodeEntry, type LightSection } from '../src/wire/encode';
import { combineHash } from '../src/wire/hash';
import { ColumnStore } from '../src/mesh/columns';
import type { GameVersion } from '../src/world/blocks';
import { rng } from './digest';

export const HEIGHT = 128;
/** Columns −1..2 on both axes: the field (0..31) and a ring of plain ground around it. */
export const CX0 = -1, CX1 = 2;
const X0 = CX0 * 16, SIZE = (CX1 - CX0 + 1) * 16;
export const GROUND = 63;

/** Vanilla light opacity (Block.lightOpacity) of the ids the farm uses, b1.8.1 and 1.12.2 alike: farmland, slabs and stairs
 * are 255 although they are not full cubes (BlockSoil / BlockFarmland, BlockStep / BlockSlab, BlockStairs: f(255) /
 * setLightOpacity(255)); water 3; crops, torches and air 0. */
const VANILLA_OPACITY: Record<number, number> = { 0: 0, 1: 255, 2: 255, 3: 255, 7: 255, 9: 3, 44: 255, 50: 0, 53: 255, 59: 0, 60: 255 };
const EMIT: Record<number, number> = { 50: 14 };

/** The farm's blocks, dense over the four-by-four columns. */
export class FieldWorld {
  readonly ids = new Uint8Array(SIZE * SIZE * HEIGHT);
  readonly meta = new Uint8Array(SIZE * SIZE * HEIGHT);

  constructor(seed = 11) {
    const r = rng(seed);
    for (let x = X0; x < X0 + SIZE; x++)
      for (let z = X0; z < X0 + SIZE; z++) {
        for (let y = 0; y < GROUND; y++) this.set(x, y, z, y < 58 ? 1 : 3);
        this.set(x, 0, z, 7);
        this.surface(x, z, r);
      }
    // A slab path and stairs along one edge, a torch on a post: other partial blocks that take all light.
    for (let x = 0; x < 32; x++) this.set(x, GROUND + 1, -1, 44);
    this.set(-1, GROUND + 1, 8, 53, 0);
    this.set(-2, GROUND + 1, 16, 1);
    this.set(-2, GROUND + 2, 16, 50, 5);
  }

  /** Grass outside the field; in it, farmland with wheat of a random stage (nine in ten cells) and a water cell per plot. */
  private surface(x: number, z: number, r: () => number): void {
    if (!inField(x, z)) this.set(x, GROUND, z, 2);
    else if (x % 9 === 4 && z % 9 === 4) this.set(x, GROUND, z, 9);
    else {
      this.set(x, GROUND, z, 60, 7);
      if (r() < 0.9) this.set(x, GROUND + 1, z, 59, Math.floor(r() * 8));
    }
  }

  index(x: number, y: number, z: number): number {
    return (y * SIZE + (z - X0)) * SIZE + (x - X0);
  }

  id(x: number, y: number, z: number): number {
    return this.ids[this.index(x, y, z)];
  }

  set(x: number, y: number, z: number, id: number, meta = 0): void {
    this.ids[this.index(x, y, z)] = id;
    this.meta[this.index(x, y, z)] = id ? meta : 0;
  }

  /** Vanilla's light of every cell ([sky, block] packed sky << 4 | block), by the plain reference rules. */
  light(): Uint8Array {
    const n = this.ids.length;
    const op = new Uint8Array(n);
    for (let i = 0; i < n; i++) op[i] = Math.min(15, VANILLA_OPACITY[this.ids[i]] ?? 255);
    const sky = new Uint8Array(n), blk = new Uint8Array(n);
    for (let x = X0; x < X0 + SIZE; x++)
      for (let z = X0; z < X0 + SIZE; z++) {
        let level = 15;
        for (let y = HEIGHT - 1; y >= 0 && level > 0; y--) {
          level = Math.max(0, level - op[this.index(x, y, z)]);
          sky[this.index(x, y, z)] = level;
        }
      }
    for (let i = 0; i < n; i++) blk[i] = EMIT[this.ids[i]] ?? 0;
    this.relax(sky, op);
    this.relax(blk, op);
    const out = new Uint8Array(n);
    for (let i = 0; i < n; i++) out[i] = (sky[i] << 4) | blk[i];
    return out;
  }

  /** Repeats "a cell is at least its brightest neighbour minus max(1, its opacity)" until nothing changes. */
  private relax(a: Uint8Array, op: Uint8Array): void {
    const steps = [1, -1, SIZE, -SIZE, SIZE * SIZE, -SIZE * SIZE];
    for (let changed = true; changed; ) {
      changed = false;
      for (let y = 1; y < HEIGHT - 1; y++)
        for (let z = 1; z < SIZE - 1; z++)
          for (let x = 1; x < SIZE - 1; x++) {
            const i = (y * SIZE + z) * SIZE + x;
            if (op[i] >= 15) continue;
            let best = 0;
            for (const s of steps) best = Math.max(best, a[i + s]);
            const v = best - Math.max(1, op[i]);
            if (v > a[i]) {
              a[i] = v;
              changed = true;
            }
          }
    }
  }

  /** A store holding the farm through a real MCWC payload, lit with `light` (default: the reference light). */
  async store(version: GameVersion, light = this.light()): Promise<ColumnStore> {
    const store = new ColumnStore();
    store.version = version;
    await store.applyPayload(encodeMcwc(0, this.entries(light)));
    return store;
  }

  /** The farm's columns encoded like the supervisor's payload, lit with `light`. */
  entries(light: Uint8Array): EncodeEntry[] {
    const entries: EncodeEntry[] = [];
    for (let cx = CX0; cx <= CX1; cx++) for (let cz = CX0; cz <= CX1; cz++) entries.push(this.entry(cx, cz, light));
    return entries;
  }

  private entry(cx: number, cz: number, light: Uint8Array): EncodeEntry {
    const ids = new Uint8Array(256 * HEIGHT), meta = new Uint8Array(256 * HEIGHT);
    const sections: LightSection[] = [];
    for (let s = 0; s < HEIGHT >> 4; s++) sections.push({ full: new Uint8Array(SECTION_CELLS) });
    for (let lx = 0; lx < 16; lx++)
      for (let lz = 0; lz < 16; lz++)
        for (let y = 0; y < HEIGHT; y++) {
          const g = this.index(cx * 16 + lx, y, cz * 16 + lz);
          ids[cellIndex(lx, y, lz)] = this.ids[g];
          meta[cellIndex(lx, y, lz)] = this.meta[g];
          (sections[y >> 4] as { full: Uint8Array }).full[(lx << 8) | (lz << 4) | (y & 15)] = light[g];
        }
    const e = encodeColumn(ids, meta, sections);
    return { cx, cz, hash: combineHash(e.blockHash, e.lightHash!), seenMs: 1, by: 't', live: true, data: e.data, light: e.light };
  }

  /** The store's light of every cell, in this world's order. */
  lightOf(store: ColumnStore): Uint8Array {
    const out = new Uint8Array(this.ids.length);
    for (let x = X0; x < X0 + SIZE; x++)
      for (let z = X0; z < X0 + SIZE; z++) {
        const c = store.get(0, x >> 4, z >> 4)!;
        for (let y = 0; y < HEIGHT; y++) {
          const s = c.light!.full[y >> 4];
          out[this.index(x, y, z)] = s ? s[((x & 15) << 8) | ((z & 15) << 4) | (y & 15)] : c.light!.uniform[y >> 4];
        }
      }
    return out;
  }

  /** Where two light arrays differ: [x, y, z, id, a, b] for each cell. */
  diff(a: Uint8Array, b: Uint8Array): Array<[number, number, number, number, number, number]> {
    const out: Array<[number, number, number, number, number, number]> = [];
    for (let i = 0; i < a.length; i++) {
      if (a[i] === b[i]) continue;
      const x = (i % SIZE) + X0, rest = Math.floor(i / SIZE), z = (rest % SIZE) + X0, y = Math.floor(rest / SIZE);
      out.push([x, y, z, this.ids[i], a[i], b[i]]);
    }
    return out;
  }
}

export const inField = (x: number, z: number) => x >= 0 && x < 32 && z >= 0 && z < 32;

/** The bots' work on the farm over time: `n` single-block deltas (harvest ripe wheat, replant bare farmland, growth, a
 * trampled cell turned to dirt and tilled again), applied to `w` as they are generated. */
export function farmDeltas(w: FieldWorld, n: number, seed = 5): Delta[] {
  const r = rng(seed);
  const out: Delta[] = [];
  while (out.length < n) {
    const d = farmWork(w, Math.floor(r() * 32), Math.floor(r() * 32), r);
    if (!d) continue;
    w.set(d[0], d[1], d[2], d[3], d[4]);
    out.push(d);
  }
  return out;
}

type Delta = [number, number, number, number, number];

/** What a bot does at field cell (x, z): till trampled dirt; replant (or trample) bare farmland; harvest or regrow wheat. */
function farmWork(w: FieldWorld, x: number, z: number, r: () => number): Delta | null {
  const y = GROUND + 1;
  const ground = w.id(x, GROUND, z);
  if (ground === 3) return [x, GROUND, z, 60, 7];
  if (ground !== 60) return null;
  if (w.id(x, y, z) === 0) return r() < 0.08 ? [x, GROUND, z, 3, 0] : [x, y, z, 59, 0];
  return r() < 0.5 ? [x, y, z, 0, 0] : [x, y, z, 59, Math.min(7, Math.floor(r() * 8))];
}
