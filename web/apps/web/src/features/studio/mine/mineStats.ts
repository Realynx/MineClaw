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

/**
 * Ore blocks seen (mined or in a wall) per 100 blocks mined: a layout's yield for its digging, the measure of the layout study
 * (docs/design/mine-blueprints.md; the bot's MineStats.SeenPer100). One ore, or all of them.
 */
export function seenPer100(stats: MineStats, oreId?: string): number {
  if (stats.toMine === 0) return 0;
  const seen = stats.ores.filter((o) => !oreId || o.ore.id === oreId).reduce((n, o) => n + o.mined + o.exposed, 0);
  return (100 * seen) / stats.toMine;
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
  const set = new Set<string>();
  const cells: Array<readonly [number, number, number]> = [];
  for (const p of dug) {
    set.add(`${p[0]},${p[1]},${p[2]}`);
    cells.push(p);
  }
  const tally = new DigTally(terrain, set);
  for (const [x, y, z] of cells) tally.add(x, y, z);
  return tally.result(cells.length > 0);
}

const bump = (m: Map<string, number>, k: string) => m.set(k, (m.get(k) ?? 0) + 1);

/** Counts a dig cell by cell: blocks to mine by material, ores mined, ores exposed in the walls (once each), the y range. */
class DigTally {
  private readonly materials = new Map<string, number>();
  private readonly mined = new Map<string, number>();
  private readonly exposed = new Set<string>();
  private readonly exposedBy = new Map<string, number>();
  private toMine = 0;
  private lo = Infinity;
  private hi = -Infinity;

  constructor(
    private readonly terrain: MineTerrain,
    private readonly dug: ReadonlySet<string>,
  ) {}

  add(x: number, y: number, z: number) {
    this.lo = Math.min(this.lo, y);
    this.hi = Math.max(this.hi, y);
    const version = this.terrain.version;
    const v = this.terrain.state(x, y, z);
    const id = v >> 4,
      meta = v & 15;
    if (id === 7) return;
    if (id !== 0) {
      this.toMine++;
      bump(this.materials, terrainName(version, id, meta));
      const ore = oreOf(version, id, meta);
      if (ore) bump(this.mined, ore.id);
    }
    // An authored tunnel still reveals its walls where it crosses a naturally empty seed cave.
    for (const [dx, dy, dz] of N6) this.expose(x + dx, y + dy, z + dz);
  }

  /** A wall cell next to a dug one: counted once if it is an ore and not dug itself. */
  private expose(x: number, y: number, z: number) {
    const k = `${x},${y},${z}`;
    if (this.dug.has(k) || this.exposed.has(k)) return;
    const v = this.terrain.state(x, y, z);
    const ore = v ? oreOf(this.terrain.version, v >> 4, v & 15) : null;
    if (!ore) return;
    this.exposed.add(k);
    bump(this.exposedBy, ore.id);
  }

  result(any: boolean): MineStats {
    return {
      toMine: this.toMine,
      materials: [...this.materials].map(([name, count]) => ({ name, count })).sort((a, b) => b.count - a.count),
      ores: this.terrain.table.ores
        .filter((o) => o.veins > 0)
        .map((o) => ({ ore: o, mined: this.mined.get(o.id) ?? 0, exposed: this.exposedBy.get(o.id) ?? 0 })),
      yRange: any ? { min: this.lo, max: this.hi } : null,
    };
  }
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
        const o = xrayOre(terrain, x, y, z, !window);
        if (o) pushTo(byOre, o.id, [x, y, z, oreColour(o)]);
      }
  return rarestFirst(byOre, max);
}

/** The ore at a cell the X-ray shows (coal left out without a window). */
function xrayOre(terrain: MineTerrain, x: number, y: number, z: number, skipCoal: boolean): OreBand | null {
  const v = terrain.state(x, y, z);
  if (v === 0) return null;
  const o = oreOf(terrain.version, v >> 4, v & 15);
  return !o || (skipCoal && o.id === 'coal') ? null : o;
}

function pushTo<T>(m: Map<string, T[]>, k: string, v: T) {
  let list = m.get(k);
  if (!list) m.set(k, (list = []));
  list.push(v);
}

/** Every list's cells, rarest list first, up to `max`: the cap drops coal and iron before diamonds. */
function rarestFirst<T>(byKind: Map<string, T[]>, max: number): T[] {
  const out: T[] = [];
  for (const list of [...byKind.values()].sort((a, b) => a.length - b.length)) {
    for (const c of list) {
      if (out.length >= max) return out;
      out.push(c);
    }
  }
  return out;
}
