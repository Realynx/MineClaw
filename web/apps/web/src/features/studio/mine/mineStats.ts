/**
 * What the studio tells about a mine design, from its generated terrain (estimates: the real world will differ): the ores at a
 * level (in which bands it lies, how many generated ores the plot's slice holds), the blocks to mine by material, the ores
 * mined and the ores exposed in the tunnels' walls per band, and the X-ray layer's ore cells.
 */
import type { MineTerrain } from './mineTerrain';
import { bandWeight, oreOf, oreTable, type OreBand } from './oreTable';

const SIZE = 64;

/** A block state's name for the stats (the terrain's own blocks). */
export function terrainName(version: string, id: number, meta: number): string {
  const ore = oreOf(version, id, meta);
  if (ore) return ore.name;
  const pocket = oreTable(version).pockets.find((p) => p.block === id && p.meta === meta);
  if (pocket) return pocket.name;
  switch (id) {
    case 1:
      return 'Stone';
    case 2:
      return 'Grass';
    case 3:
      return 'Dirt';
    case 7:
      return 'Bedrock';
    case 13:
      return 'Gravel';
    default:
      return `#${id}:${meta}`;
  }
}

export interface LevelOre {
  ore: OreBand;
  /** In the ore's generated band at this y. */
  inBand: boolean;
  /** 0..1: the band's relative weight here (a triangle band peaks at its centre). */
  weight: number;
  /** Generated ores of it in the plot's slice at this y. */
  count: number;
  /** This is the ore's best strip-mining level. */
  best: boolean;
}

/** The ores at a level: band membership and the plot slice's counts (ores with no veins left out). */
export function levelOres(terrain: MineTerrain, y: number): LevelOre[] {
  const ores = terrain.table.ores.filter((o) => o.veins > 0);
  const counts = new Map<string, number>(ores.map((o) => [o.id, 0]));
  for (let z = 0; z < SIZE; z++)
    for (let x = 0; x < SIZE; x++) {
      const v = terrain.state(x, y, z);
      if (v === 0) continue;
      const o = oreOf(terrain.version, v >> 4, v & 15);
      if (o && counts.has(o.id)) counts.set(o.id, counts.get(o.id)! + 1);
    }
  return ores.map((o) => ({
    ore: o,
    inBand: y >= o.min && y <= o.max,
    weight: bandWeight(o, y),
    count: counts.get(o.id) ?? 0,
    best: o.best === y,
  }));
}

export interface MineStats {
  /** Dug cells over solid generated ground (bedrock never). */
  toMine: number;
  /** Blocks to mine by material, most first. */
  materials: Array<{ name: string; count: number }>;
  /** Per ore: mined (in a dug cell) and exposed (touching one, not dug). */
  ores: Array<{ ore: OreBand; mined: number; exposed: number }>;
  /** The dug cells' y range (null when none). */
  yRange: { min: number; max: number } | null;
}

const N6: ReadonlyArray<[number, number, number]> = [
  [1, 0, 0],
  [-1, 0, 0],
  [0, 1, 0],
  [0, -1, 0],
  [0, 0, 1],
  [0, 0, -1],
];

/** The design's dig estimate over the terrain (dug cells as [x, y, z]). */
export function mineStats(terrain: MineTerrain, dug: Iterable<readonly [number, number, number]>): MineStats {
  const version = terrain.version;
  const set = new Set<string>();
  const cells: Array<readonly [number, number, number]> = [];
  for (const p of dug) {
    set.add(`${p[0]},${p[1]},${p[2]}`);
    cells.push(p);
  }
  const materials = new Map<string, number>();
  const mined = new Map<string, number>();
  const exposed = new Set<string>();
  const exposedBy = new Map<string, number>();
  let toMine = 0,
    lo = Infinity,
    hi = -Infinity;
  for (const [x, y, z] of cells) {
    lo = Math.min(lo, y);
    hi = Math.max(hi, y);
    const v = terrain.state(x, y, z);
    const id = v >> 4,
      meta = v & 15;
    if (id === 0 || id === 7) continue;
    toMine++;
    const name = terrainName(version, id, meta);
    materials.set(name, (materials.get(name) ?? 0) + 1);
    const ore = oreOf(version, id, meta);
    if (ore) mined.set(ore.id, (mined.get(ore.id) ?? 0) + 1);
    for (const [dx, dy, dz] of N6) {
      const nx = x + dx,
        ny = y + dy,
        nz = z + dz;
      const k = `${nx},${ny},${nz}`;
      if (set.has(k) || exposed.has(k)) continue;
      const nv = terrain.state(nx, ny, nz);
      const no = nv ? oreOf(version, nv >> 4, nv & 15) : null;
      if (!no) continue;
      exposed.add(k);
      exposedBy.set(no.id, (exposedBy.get(no.id) ?? 0) + 1);
    }
  }
  return {
    toMine,
    materials: [...materials].map(([name, count]) => ({ name, count })).sort((a, b) => b.count - a.count),
    ores: terrain.table.ores
      .filter((o) => o.veins > 0)
      .map((o) => ({ ore: o, mined: mined.get(o.id) ?? 0, exposed: exposedBy.get(o.id) ?? 0 })),
    yRange: cells.length ? { min: lo, max: hi } : null,
  };
}

/** The colour of an ore as a number (the table's "#rrggbb"; grey when none). */
export const oreColour = (o: OreBand): number => (o.color ? parseInt(o.color.slice(1), 16) : 0x888888);

/**
 * The X-ray layer's cells [x, y, z, colour] over the plot: with a window [lo, hi] every ore in it; without one every ore
 * but coal (the commonest, it would hide the rest). Capped at `max`, rarest ores first.
 */
export function xrayCells(
  terrain: MineTerrain,
  window: { lo: number; hi: number } | null,
  max = 6000,
): Array<[number, number, number, number]> {
  const lo = Math.max(0, window?.lo ?? 0),
    hi = Math.min(127, window?.hi ?? 127);
  const byOre = new Map<string, Array<[number, number, number, number]>>();
  for (let y = lo; y <= hi; y++)
    for (let z = 0; z < SIZE; z++)
      for (let x = 0; x < SIZE; x++) {
        const v = terrain.state(x, y, z);
        if (v === 0) continue;
        const o = oreOf(terrain.version, v >> 4, v & 15);
        if (!o || (!window && o.id === 'coal')) continue;
        let list = byOre.get(o.id);
        if (!list) byOre.set(o.id, (list = []));
        list.push([x, y, z, oreColour(o)]);
      }
  const out: Array<[number, number, number, number]> = [];
  // Rarest first: the cap drops coal and iron before diamonds.
  for (const list of [...byOre.values()].sort((a, b) => a.length - b.length)) {
    for (const c of list) {
      if (out.length >= max) return out;
      out.push(c);
    }
  }
  return out;
}
