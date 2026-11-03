// A synthetic world encoded exactly like the supervisor's payloads (MCWC v2 with ChunkSnapshot blocks + LightSnapshot light),
// plus a fake WorldSource around it: an island in a sea with a tree, a glass house, a double chest, a fence, stairs and slabs,
// a tunnel through a hill, a torch-lit room, bots walking about, mobs, a drop, overlays and a build plan that progresses (so
// column hashes change and the index-then-fetch path is exercised). No Mojang assets.

import type { AtlasResource, BlockChange, BlockDeltas, BotDto, EntityDelta, EntityDto, Overlays, PlanBlock, WorldIndex, WorldSource } from '../types';
import { cellIndex } from '../wire/chunk';
import { combineHash } from '../wire/hash';
import { encodeColumn, encodeMcwc, lightSections, type EncodeEntry } from '../wire/encode';
import type { GameVersion } from '../world/blocks';

export interface FixtureOptions {
  version?: GameVersion;
  /** Columns from -radius..radius on both axes. */
  radius?: number;
  /** Milliseconds clock (tests pass a fake). */
  now?: () => number;
  /** World ticks per real second (20 = vanilla; the demo speeds it up). */
  ticksPerSecond?: number;
  startTime?: number;
  rain?: number;
  thunder?: number;
  /** Send light (MCWC v2). */
  light?: boolean;
  /** Build plan blocks placed per second (0 = frozen). */
  buildRate?: number;
  atlas?: () => Promise<AtlasResource | null>;
  /** Wandering mobs (default 5). */
  mobs?: number;
  /** A denser world for benchmarks: a lava lake, a water channel and stone pillars. */
  dense?: boolean;
  /** [entity models] A parade of every mob type of the version, drops of several kinds, objects and an XP orb. */
  zoo?: boolean;
}

// [entity models] Mob types per version for the zoo (b1.8.1 has 50–60 and 90–95), with Mob16 variants worth showing.
const ZOO_BETA: Array<[number, number?]> = [[50], [51], [52], [53], [54], [55, 2], [56], [57], [58], [59], [60], [90, 1], [91, 14], [91, 16], [92], [93], [94], [95], [95, 4]];
const ZOO_1122: Array<[number, number?]> = [
  ...ZOO_BETA.filter(([t]) => t !== 53 && t !== 56),
  [4], [5], [6], [23], [27], [28], [29], [31], [32], [34], [35], [36], [37], [61], [62, 2], [64], [65], [66], [67], [68], [69], [96], [97], [98], [98, 4], [99], [100], [101], [102], [103], [105], [120],
];
/** [entity models] Objects (not sent by the supervisor today: the fixture exercises their render paths). */
const ZOO_OBJECTS: Array<Partial<EntityDto>> = [
  { kind: 'object', type: 1 },
  { kind: 'object', type: 10 },
  { kind: 'object', type: 60 },
  { kind: 'object', type: 70, item: 12 },
  { kind: 'object', type: 50 },
  { kind: 'xp_orb', count: 20 },
];

const SEA = 62;

export function surface(x: number, z: number): number {
  const d = Math.hypot(x, z);
  const island = 70 - d * 0.18 + 4 * Math.sin(x / 11) + 3 * Math.cos(z / 13);
  const hill = 14 * Math.exp(-((x - 30) ** 2 + (z - 2) ** 2) / 260);
  return Math.floor(Math.max(48, island + hill));
}

export const PLAN: PlanBlock[] = (() => {
  const out: PlanBlock[] = [];
  const base = surface(-14, -14) + 1;
  for (let i = 0; i < 7; i++) for (let y = 0; y < 3; y++) out.push([-18 + i, base + y, -16, 98, 0, 0]);
  return out;
})();

export const TORCH_ROOM = { x: 30, z: 2, y: 0 };
/** The block gallery's plinth (top surface at y). */
export const GALLERY = { x0: 8, x1: 30, z0: -40, z1: -26, y: 69 };

/** One of each specially shaped block, on the gallery plinth: [x, y, z, id, meta]. */
export function galleryBlocks(modern: boolean): Array<[number, number, number, number, number]> {
  const y = GALLERY.y + 1, out: Array<[number, number, number, number, number]> = [];
  const put = (x: number, dy: number, z: number, id: number, m = 0) => out.push([x, y + dy, z, id, m]);
  let z = GALLERY.z0 + 1;
  // Row 1: stairs (straight, outer and inner corners), slabs, fences with a gate, panes, iron bars.
  put(9, 0, z, 53, 0); put(10, 0, z, 53, 3); // outer corner
  put(13, 0, z, 53, 3); put(14, 0, z, 53, 0); // inner corner (the back stair faces across)
  put(16, 0, z, 67, 2); put(17, 0, z, 44, 0); put(18, 0, z, 44, modern ? 8 : 0);
  for (let x = 20; x <= 23; x++) put(x, 0, z, x === 22 ? 107 : 85, 0);
  for (let x = 25; x <= 27; x++) put(x, 0, z, 102);
  put(28, 0, z, 101); put(29, 0, z, 101); put(29, 0, z + 1, 101);
  // Row 2: redstone (a line, a corner, a torch, a repeater, a lever), rails (straight, curve, slope), a piston.
  z += 3;
  for (let x = 9; x <= 12; x++) put(x, 0, z, 55, x === 12 ? 15 : 15 - (12 - x) * 4);
  put(12, 0, z + 1, 55, 13); put(13, 0, z, 76, 5); put(9, 0, z + 1, 93, 1); put(10, 0, z + 2, 69, 5 | 8);
  for (let x = 15; x <= 17; x++) put(x, 0, z, 66, 1);
  put(18, 0, z, 66, 7); put(18, 0, z + 1, 66, 0); put(19, 0, z, 66, 2); put(20, 0, z, 1); put(20, 1, z, 66, 1);
  put(22, 0, z, 33, 1); put(23, 0, z, 29, 3); put(24, 0, z, 96, 0); put(25, 0, z, 92, 2); put(26, 0, z, 78, 3);
  // Row 3: doors, beds, chests (a pair and a single), signs and torches on a wall, a ladder, vines.
  z += 4;
  put(9, 0, z, 64, 0); put(9, 1, z, 64, 8); put(11, 0, z, 71, 4 | 1); put(11, 1, z, 71, 8 | 1);
  put(13, 0, z, 26, 0); put(13, 0, z + 1, 26, 8); put(15, 0, z, 26, 3); put(16, 0, z, 26, 8 | 3);
  put(17, 0, z, 54, 3); put(18, 0, z, 54, 3); put(20, 0, z, 54, 3);
  for (let x = 22; x <= 27; x++) for (let dy = 0; dy < 3; dy++) put(x, dy, z + 1, 5);
  put(22, 0, z, 63, 8); put(23, 1, z, 68, 2); put(24, 1, z, 50, 4); put(25, 0, z, 65, 2); put(25, 1, z, 65, 2); put(26, 1, z, 106, 4); put(26, 0, z, 106, 4);
  put(27, 0, z, 77, 4);
  // Row 4: fluids (a source, a flowing run, a fall) for water and lava, fire, a portal, plants.
  z += 3;
  for (let i = 0; i < 5; i++) put(9 + i, 0, z, i === 0 ? 9 : 8, i);
  put(9, 1, z, 1); put(9, 2, z, 9, 0); put(10, 1, z, 8, 8);
  for (let i = 0; i < 4; i++) put(9 + i, 0, z + 2, i === 0 ? 11 : 10, i * 2);
  put(16, -1, z, 87); put(16, 0, z, 51, 0);
  for (let dy = 0; dy < 5; dy++) for (let dx = 0; dx < 4; dx++) put(18 + dx, dy, z, dx === 0 || dx === 3 || dy === 0 || dy === 4 ? 49 : 90);
  put(23, -1, z, 12); put(23, 0, z, 81); put(23, 1, z, 81); put(24, -1, z, 60, 7); put(24, 0, z, 59, 7); put(25, -1, z, 60, 7); put(25, 0, z, 104, 7); put(26, 0, z, 86, 1);
  put(27, -1, z, 12); put(27, 0, z, 83); put(27, 1, z, 83); put(28, 0, z, 31, 1); put(29, 0, z, 37);
  if (modern) {
    // Row 5 (1.12.2): banners, a skull, shulker boxes, trapped/ender chests, a wall, stained glass, glazed terracotta.
    z += 3;
    put(9, 0, z, 176, 0); put(11, 0, z, 144, 1); put(12, 0, z, 219, 1); put(13, 0, z, 229, 1); put(14, 0, z, 146, 3); put(15, 0, z, 146, 3); put(17, 0, z, 130, 3);
    for (let x = 19; x <= 21; x++) put(x, 0, z, 139);
    put(23, 0, z, 95, 14); put(24, 0, z, 95, 3); put(25, 0, z, 160, 5); put(26, 0, z, 160, 5); put(27, 0, z, 235 + 4, 0); put(28, 0, z, 198, 1); put(29, 0, z, 251, 11);
  }
  return out;
}
const TUNNEL = { x0: 18, x1: 44, z: 2 };

export function tunnelY(): number {
  return surface(30, 2) - 9;
}

export class FixtureWorld {
  readonly version: GameVersion;
  readonly height: number;
  readonly radius: number;
  private placed = 0;
  dense = false;
  constructor(version: GameVersion = 'b1.8.1', radius = 6) {
    this.version = version;
    this.height = version === '1.12.2' ? 256 : 128;
    this.radius = radius;
  }

  setPlaced(n: number): void {
    this.placed = Math.max(0, Math.min(PLAN.length, n));
  }

  get placedCount(): number {
    return this.placed;
  }

  has(cx: number, cz: number): boolean {
    return Math.abs(cx) <= this.radius && Math.abs(cz) <= this.radius;
  }

  /** Blocks of a column in ChunkColumn.Index order. */
  column(cx: number, cz: number): { ids: Uint8Array; meta: Uint8Array } {
    const v = 256 * this.height;
    const ids = new Uint8Array(v), meta = new Uint8Array(v);
    const set = (lx: number, y: number, lz: number, id: number, m = 0) => {
      if (y < 0 || y >= this.height) return;
      const i = cellIndex(lx, y, lz);
      ids[i] = id;
      meta[i] = m;
    };
    const modern = this.version === '1.12.2';
    for (let lx = 0; lx < 16; lx++)
      for (let lz = 0; lz < 16; lz++) {
        const x = cx * 16 + lx, z = cz * 16 + lz;
        const h = surface(x, z);
        set(lx, 0, lz, 7);
        for (let y = 1; y <= h; y++) set(lx, y, lz, y < h - 3 ? 1 : y < h ? 3 : h <= SEA + 1 ? 12 : 2);
        for (let y = h + 1; y <= SEA; y++) set(lx, y, lz, 9);
        // The tunnel through the hill, with a torch-lit room in the middle.
        const ty = tunnelY();
        if (x >= TUNNEL.x0 && x <= TUNNEL.x1 && Math.abs(z - TUNNEL.z) <= 1) {
          for (let y = ty; y < ty + 3; y++) if (y < h) set(lx, y, lz, 0);
          set(lx, ty - 1, lz, 4);
        }
        if (Math.abs(x - TORCH_ROOM.x) <= 3 && Math.abs(z - TORCH_ROOM.z) <= 3) {
          for (let y = ty; y < ty + 4; y++) set(lx, y, lz, 0);
          if (x === TORCH_ROOM.x && z === TORCH_ROOM.z) set(lx, ty, lz, 50, 5);
        }
        // A lava pool (a danger overlay marks it).
        if (Math.hypot(x + 20, z - 20) < 2.5) set(lx, h, lz, 11);
        if (this.dense) {
          // Benchmark extras: a lava lake, a flowing water channel, stone pillars with glass tops.
          if (Math.hypot(x - 20, z + 30) < 7) for (let y = h - 2; y <= h; y++) set(lx, y, lz, y === h && (x + z) % 5 === 0 ? 10 : 11, 0);
          if (Math.abs(z - 18) <= 1 && x > -40 && x < 40) { set(lx, h, lz, 8, Math.abs(x) % 8); set(lx, h - 1, lz, 4); }
          if ((x * 7 + z * 13) % 23 === 0 && h > SEA) for (let y = h + 1; y < h + 6; y++) set(lx, y, lz, y === h + 5 ? 20 : 1);
        }
      }
    const local = (x: number, y: number, z: number, id: number, m = 0) => {
      if (x >> 4 !== cx || z >> 4 !== cz) return;
      set(x & 15, y, z & 15, id, m);
    };
    // A tree.
    const tb = surface(6, 6);
    for (let y = 1; y <= 5; y++) local(6, tb + y, 6, 17, modern ? 0 : 0);
    for (let dx = -2; dx <= 2; dx++)
      for (let dz = -2; dz <= 2; dz++)
        for (let dy = 3; dy <= 6; dy++) {
          if (Math.abs(dx) + Math.abs(dz) + Math.max(0, dy - 4) * 2 > 3 || (dx === 0 && dz === 0 && dy <= 5)) continue;
          local(6 + dx, tb + dy, 6 + dz, 18);
        }
    // A glass house with a planks floor and a torch.
    const hb = surface(-8, 10);
    for (let x = -11; x <= -5; x++)
      for (let z = 7; z <= 13; z++) {
        local(x, hb, z, 5);
        const wall = x === -11 || x === -5 || z === 7 || z === 13;
        for (let y = 1; y <= 3; y++) local(x, hb + y, z, wall ? (y === 2 ? 20 : 4) : 0);
        local(x, hb + 4, z, 44, 0);
      }
    local(-8, hb + 1, 10, 50, 5);
    local(-5, hb + 1, 10, 0);
    local(-5, hb + 2, 10, 0);
    // Chests: a pair and a single.
    const cb = surface(2, -4);
    local(2, cb + 1, -4, 54, 3);
    local(3, cb + 1, -4, 54, 3);
    local(6, cb + 1, -4, 54, 3);
    // A fence, stairs, a slab.
    for (let x = -4; x <= 0; x++) local(x, surface(x, -8) + 1, -8, 85);
    local(1, surface(1, 2) + 1, 2, 53, 0);
    local(2, surface(2, 2) + 1, 2, 44, 0);
    local(0, surface(0, 4) + 1, 4, 38);
    local(1, surface(1, 4) + 1, 4, 31, 1);
    // The block gallery: a raised stone plinth with one of everything the mesher shapes specially.
    if (cx * 16 + 15 >= GALLERY.x0 && cx * 16 <= GALLERY.x1 && cz * 16 + 15 >= GALLERY.z0 && cz * 16 <= GALLERY.z1) {
      for (let x = GALLERY.x0; x <= GALLERY.x1; x++)
        for (let z = GALLERY.z0; z <= GALLERY.z1; z++) {
          for (let y = surface(x, z) + 1; y <= GALLERY.y; y++) local(x, y, z, y === GALLERY.y ? (modern ? 1 : 1) : 4);
          for (let y = GALLERY.y + 1; y <= GALLERY.y + 8; y++) local(x, y, z, 0);
        }
      for (const [x, y, z, id, m] of galleryBlocks(modern)) local(x, y, z, id, m);
    }
    // The build plan's placed blocks.
    for (let i = 0; i < this.placed; i++) {
      const b = PLAN[i];
      local(b[0], b[1], b[2], b[3], b[4]);
    }
    return { ids, meta };
  }

  /** Sky light: 15 down to the first opaque block, then dark; block light from the torches (no occlusion). */
  light(cx: number, cz: number, ids: Uint8Array) {
    const tops = new Int16Array(256);
    const clear = new Set([0, 6, 8, 9, 10, 11, 18, 20, 26, 27, 28, 29, 31, 32, 33, 37, 38, 44, 50, 51, 53, 54, 55, 59, 63, 64, 65, 66, 67, 68, 69, 71, 75, 76, 77, 78, 81, 83, 85, 86, 90, 92, 93, 94, 95, 96, 101, 102, 104, 106, 107, 130, 139, 144, 146, 160, 176, 198, 219, 229]);
    for (let lx = 0; lx < 16; lx++)
      for (let lz = 0; lz < 16; lz++) {
        let y = this.height - 1;
        while (y > 0 && clear.has(ids[cellIndex(lx, y, lz)])) y--;
        tops[lx * 16 + lz] = y;
      }
    const ty = tunnelY();
    const torches = [
      [TORCH_ROOM.x, ty, TORCH_ROOM.z],
      [-8, surface(-8, 10) + 1, 10],
    ];
    const sky = (x: number, y: number, z: number) => (y > tops[x * 16 + z] ? 15 : 0);
    const block = (x: number, y: number, z: number) => {
      let best = 0;
      for (const [tx, tyy, tz] of torches) best = Math.max(best, 14 - (Math.abs(cx * 16 + x - tx) + Math.abs(y - tyy) + Math.abs(cz * 16 + z - tz)));
      return best;
    };
    return lightSections(this.height, sky, block);
  }

  /** One encoded column with its delta-key hash (block hash, or Combine(block, light)). */
  encode(cx: number, cz: number, withLight: boolean): { data: Uint8Array; light: Uint8Array | null; hash: bigint } {
    const { ids, meta } = this.column(cx, cz);
    const biomes = this.version === '1.12.2' ? new Uint8Array(256).fill(Math.abs(cx + cz) % 3 === 0 ? 4 : 1) : null;
    const enc = encodeColumn(ids, meta, withLight ? this.light(cx, cz, ids) : null, biomes);
    return { data: enc.data, light: enc.light, hash: enc.lightHash != null ? combineHash(enc.blockHash, enc.lightHash) : enc.blockHash };
  }
}

/** A WorldSource backed by `FixtureWorld`, with moving bots and mobs. */
export function createFixtureSource(opts: FixtureOptions = {}): WorldSource & { world: FixtureWorld; calls: { index: number; chunks: number; columns: number } } {
  const world = new FixtureWorld(opts.version ?? 'b1.8.1', opts.radius ?? 6);
  world.dense = !!opts.dense;
  const now = opts.now ?? (() => Date.now());
  const t0 = now();
  const tps = opts.ticksPerSecond ?? 20;
  const withLight = opts.light ?? true;
  const cache = new Map<string, { data: Uint8Array; light: Uint8Array | null; hash: bigint; placed: number }>();
  const calls = { index: 0, chunks: 0, columns: 0 };
  let rev = 0;

  // Block delta log: every change of the plan's placed count becomes per-block changes with their own revision.
  const log: Array<{ rev: number; b: BlockChange }> = [];
  let blockRev = 1;
  let lastPlaced = world.placedCount;
  const record = () => {
    const p = world.placedCount;
    if (p === lastPlaced) return;
    if (p > lastPlaced) for (let i = lastPlaced; i < p; i++) log.push({ rev: ++blockRev, b: [PLAN[i][0], PLAN[i][1], PLAN[i][2], PLAN[i][3], PLAN[i][4]] });
    else for (let i = p; i < lastPlaced; i++) log.push({ rev: ++blockRev, b: [PLAN[i][0], PLAN[i][1], PLAN[i][2], 0, 0] });
    lastPlaced = p;
    if (log.length > 2000) log.splice(0, log.length - 2000);
  };
  const syncPlan = () => {
    if (opts.buildRate) world.setPlaced(Math.floor(((now() - t0) / 1000) * opts.buildRate) % (PLAN.length + 1));
    record();
  };
  const planCols = new Set(PLAN.map((b) => `${b[0] >> 4},${b[2] >> 4}`));
  const encoded = (cx: number, cz: number) => {
    const k = `${cx},${cz}`;
    const c = cache.get(k);
    const placed = planCols.has(k) ? world.placedCount : 0;
    if (c && c.placed === placed) return c;
    const e = { ...world.encode(cx, cz, withLight), placed };
    cache.set(k, e);
    return e;
  };

  const bots = (): BotDto[] => {
    const s = (now() - t0) / 1000;
    const a = s / 8;
    const ax = Math.cos(a) * 10, az = Math.sin(a) * 10;
    const ty = tunnelY();
    const bx = 22 + ((Math.sin(s / 5) + 1) / 2) * 18;
    return [
      { name: 'Alice', short: 'main', main: true, dim: 0, x: ax, y: surface(Math.floor(ax), Math.floor(az)) + 1, z: az, yaw: ((a * 180) / Math.PI + 180) % 360, hp: 20, food: 18, job: 'patrolling the island', held: 'stone pickaxe', claims: ['island'], live: true },
      { name: 'Bob', short: '67', main: false, dim: 0, x: bx, y: ty, z: TUNNEL.z + 0.5, yaw: Math.cos(s / 5) > 0 ? 270 : 90, hp: 6, food: 12, job: 'mining the tunnel', held: 'torch', claims: [], live: true },
    ];
  };

  const entities = (): EntityDto[] => {
    const s = (now() - t0) / 1000;
    const out: EntityDto[] = [];
    for (let i = 0; i < (opts.mobs ?? 5); i++) {
      const a = s / (6 + i) + i * 1.3;
      const ring = i * 2.399, rr = 6 + (i % 5) * 4; // spread around the island centre
      const x = Math.cos(ring) * rr + Math.cos(a) * 3, z = Math.sin(ring) * rr - 4 + Math.sin(a) * 3;
      out.push({ kind: 'mob', name: null, dim: 0, x, y: surface(Math.floor(x), Math.floor(z)) + 1, z, id: 100 + i, type: i % 2 ? 92 : 54, yaw: (a * 57) % 360 });
    }
    out.push({ kind: 'item', name: 'cobblestone', dim: 0, x: 3.5, y: surface(3, 0) + 1, z: 0.5, id: 200, item: 4, count: 12, age: s });
    if (opts.zoo) {
      // [entity models] Mobs in rows south of the island centre (+z), every other one walking a small circle.
      const types = world.version === '1.12.2' ? ZOO_1122 : ZOO_BETA;
      types.forEach(([type, variant], i) => {
        const walk = i % 2 === 0, a = s / 3 + i;
        const x = -14 + (i % 10) * 3 + (walk ? Math.cos(a) * 0.8 : 0), z = 18 + Math.floor(i / 10) * 5 + (walk ? Math.sin(a) * 0.8 : 0);
        const yaw = walk ? ((((a + Math.PI / 2) * 180) / Math.PI) + 90) % 360 : (i * 40) % 360;
        const fly = type === 56 || type === 65 || type === 63 ? 4 : 0;
        out.push({ kind: 'mob', name: null, dim: 0, x, y: surface(Math.floor(x), Math.floor(z)) + 1 + fly, z, id: 1000 + i, type, variant: variant ?? 0, yaw, age: s, flags: type === 57 && i % 3 === 0 ? 1 : 0 });
      });
      // Drops: a block item, a tool, a flat item, a flower; and objects.
      [[1, 64], [278, 1], [264, 5], [38, 1], [5, 30], [50, 3]].forEach(([item, count], i) =>
        out.push({ kind: 'item', name: null, dim: 0, x: -6 + i * 1.5, y: surface(-6 + i * 1.5, -12) + 1, z: -12, id: 2000 + i, item, count, age: s + i * 3 }),
      );
      ZOO_OBJECTS.forEach((o, i) =>
        out.push({ name: null, dim: 0, x: 6 + i * 2.5, y: surface(6 + i * 2.5, -12) + 1, z: -12, id: 3000 + i, yaw: 30 * i, age: s, ...o, kind: o.kind! }),
      );
    }
    out.push({ kind: 'player', name: 'poofimafox', dim: 0, x: -2, y: surface(-2, 12) + 1, z: 12, id: 300, yaw: 180 });
    return out;
  };

  return {
    world,
    calls,
    async index(dim, x, z, r): Promise<WorldIndex> {
      calls.index++;
      syncPlan();
      const cx0 = (Math.floor(x) - r) >> 4, cx1 = (Math.floor(x) + r) >> 4, cz0 = (Math.floor(z) - r) >> 4, cz1 = (Math.floor(z) + r) >> 4;
      const columns: WorldIndex['columns'] = [];
      if (dim === 0)
        for (let cx = cx0; cx <= cx1; cx++)
          for (let cz = cz0; cz <= cz1; cz++) {
            if (!world.has(cx, cz)) continue;
            const e = encoded(cx, cz);
            columns.push({ cx, cz, hash: e.hash.toString(), seen: now(), by: 'Alice', live: true });
          }
      const time = (opts.startTime ?? 1000) + ((now() - t0) / 1000) * tps;
      return { dim, at: now(), columns, version: world.version, time, rain: opts.rain ?? 0, thunder: opts.thunder ?? 0 };
    },
    async chunks(dim, cols): Promise<ArrayBuffer> {
      calls.chunks++;
      calls.columns += cols.length;
      const entries: EncodeEntry[] = cols.map(([cx, cz]) => {
        if (dim !== 0 || !world.has(cx, cz)) return { cx, cz, hash: 0n, seenMs: now(), by: null, live: false, data: new Uint8Array(0), light: null };
        const e = encoded(cx, cz);
        return { cx, cz, hash: e.hash, seenMs: now(), by: 'Alice', live: true, data: e.data, light: e.light };
      });
      return encodeMcwc(dim, entries);
    },
    async entities(since): Promise<EntityDelta> {
      rev++;
      return { at: now(), rev, full: since == null, bots: bots(), entities: entities(), removed: [] };
    },
    async blocks(since): Promise<BlockDeltas> {
      syncPlan();
      if (since == null) return { rev: blockRev, full: true, blocks: {}, columns: [] };
      if (log.length && since < log[0].rev - 1) return { rev: blockRev, full: true, blocks: {}, columns: [] };
      const out = log.filter((e) => e.rev > since);
      const page = out.slice(0, 256);
      return { at: now(), rev: page.length ? page[page.length - 1].rev : blockRev, full: false, more: out.length > page.length, blocks: { 0: page.map((e) => e.b) }, columns: [] };
    },
    async overlays(dim): Promise<Overlays> {
      const hb = surface(-8, 10);
      const cb = surface(2, -4);
      const ty = tunnelY();
      return {
        at: now(),
        dim,
        claims: [{ kind: 'claim', text: 'Alice’s house', min: [-12, hb - 1, 6], max: [-4, hb + 5, 14], owner: 'Alice' }],
        places: [{ kind: 'structure:house', text: 'glass house', min: [-11, hb, 7], max: [-5, hb + 4, 13] }, { kind: 'waypoint', text: 'tunnel mouth', min: [TUNNEL.x0, ty, TUNNEL.z], max: [TUNNEL.x0, ty, TUNNEL.z] }],
        points: [{ kind: 'chest', text: 'double chest', min: [2, cb + 1, -4], max: [3, cb + 1, -4] }],
        plans: [{ id: 1, name: 'stone wall', status: 'building', part: 0, blocks: PLAN.map((b, i) => [b[0], b[1], b[2], b[3], b[4], i < world.placedCount ? 1 : 0] as PlanBlock) }],
        dangers: [{ kind: 'hazard', text: 'lava pool', min: [-23, surface(-20, 20) - 1, 17], max: [-18, surface(-20, 20) + 1, 22] }, { kind: 'vital', text: '67 hp 6', min: [30, ty, 2], max: [30, ty, 2], owner: 'Bob' }],
        notes: ['fixture world (no bot connected)'],
      };
    },
    atlas: opts.atlas ?? (async () => null),
  };
}
