/**
 * Story Mode water (docs/web/story-mode.md "Water"): the Architect's water tools, their build items, where poured liquid runs, the
 * order pawns pour a channel in, and the irrigation planner for growing zones. The director (bot) owns the real rules
 * (StoryColony.Water.cs); these are their reference copies for the web's tools and the mock director. Pure.
 */
import type { StoryBuildItem } from './story.js';
import { cellX, cellZ, zoneCellSet, type ZoneLike } from './storyZones.js';

type V3 = [number, number, number];

/** The Architect's water tools: a source (one click), a channel (a drawn area: a canal, a hydroponic tray), an infinite source
 * (a 2×2 pool that refills), a lava source (only while the colony allows lava). */
export const WATER_TOOL_IDS = ['water', 'water_channel', 'infinite_water', 'lava'] as const;
export type WaterToolId = (typeof WATER_TOOL_IDS)[number];

export const isWaterTool = (id: string): id is WaterToolId => (WATER_TOOL_IDS as readonly string[]).includes(id);

/** At most this many cells per water item (the director's per-item limit). */
export const MAX_WATER_CELLS = 4096;

/**
 * The cells a water tool fills from a drag a→b (top-down: the first block layer above the level's floor). With `dig` they go one
 * lower, into the ground (dug first): a trench, a sunken pool. Source and lava take the clicked cell, the infinite source the 2×2
 * from it, the channel the dragged rectangle.
 */
export function waterToolCells(tool: WaterToolId, a: V3, b: V3, dig: boolean): V3[] {
  const y = a[1] - (dig ? 1 : 0);
  if (tool === 'water' || tool === 'lava') return [[a[0], y, a[2]]];
  if (tool === 'infinite_water')
    return [
      [a[0], y, a[2]],
      [a[0] + 1, y, a[2]],
      [a[0], y, a[2] + 1],
      [a[0] + 1, y, a[2] + 1],
    ];
  const out: V3[] = [];
  const [x1, x2] = a[0] <= b[0] ? [a[0], b[0]] : [b[0], a[0]];
  const [z1, z2] = a[2] <= b[2] ? [a[2], b[2]] : [b[2], a[2]];
  for (let z = z1; z <= z2; z++) for (let x = x1; x <= x2; x++) if (out.length < MAX_WATER_CELLS) out.push([x, y, z]);
  return out;
}

/** A group name for a water placement's dig and pour items (its blocks wait for the dig). */
export function waterGroup(now = Date.now()): string {
  return `water-${now.toString(36)}`;
}

/**
 * The build items a water placement sends: the water item (`indoor` for the Water source's explicit click, `infinite` for the
 * pool, `material: "lava"` for lava), after a `remove` item of the same group for the cells that are dug first (`dig`).
 */
export function waterItems(tool: WaterToolId, cells: readonly V3[], opts: { dig: boolean; group?: string; name?: string }): StoryBuildItem[] {
  const group = opts.group ?? waterGroup();
  const water: StoryBuildItem = {
    kind: 'water',
    blocks: cells.map((c) => [...c] as V3),
    ...(tool === 'lava' ? { material: 'lava' } : {}),
    ...(tool === 'water' ? { indoor: true } : {}),
    ...(tool === 'infinite_water' ? { infinite: true } : {}),
    ...(opts.name ? { name: opts.name.slice(0, 40) } : {}),
  };
  if (!opts.dig) return [water];
  return [
    { kind: 'remove', blocks: cells.map((c) => [...c] as V3), group },
    { ...water, group },
  ];
}

/** Cells water takes (air, grass, flowers, snow, vines): what a poured source washes away. */
const TAKES = new Set([0, 31, 32, 37, 38, 78, 106, 175]);
const isWater = (id: number) => id === 8 || id === 9;
const isLava = (id: number) => id === 10 || id === 11;

export interface LiquidFlowResult {
  /** Cells the liquid reaches beyond the drawn ones. */
  spread: V3[];
  /** It meets the other liquid (obsidian, cobblestone or stone forms). */
  touches: string | null;
}

/**
 * Where poured sources run (the director's preview, after BlockFlowing): from each cell straight down through open cells (no
 * sideways spread while it can fall), else sideways up to 7 (lava 3) over open cells; a fall lands and spreads again (12 at
 * most). `dug`: cells dug before the pour (open).
 */
export function liquidFlow(
  blockAt: (x: number, y: number, z: number) => readonly [number, number],
  cells: readonly V3[],
  lava = false,
  dug: readonly V3[] = [],
): LiquidFlowResult {
  const reach = lava ? 3 : 7,
    maxFall = 12,
    max = 512;
  const key = (c: V3) => `${c[0]},${c[1]},${c[2]}`;
  const own = new Set(cells.map(key));
  const dugSet = new Set(dug.map(key));
  const open = (c: V3) => {
    if (own.has(key(c))) return false;
    if (dugSet.has(key(c))) return true;
    if (c[1] < 1) return false;
    const [id, meta] = blockAt(c[0], c[1], c[2]);
    if (lava ? isLava(id) : isWater(id)) return meta !== 0;
    return TAKES.has(id);
  };
  const seen = new Set<string>(own);
  const spread: V3[] = [];
  let touches: string | null = null;
  const reachCell = (c: V3) => {
    spread.push(c);
    if (touches) return;
    for (const d of [[1, 0, 0], [-1, 0, 0], [0, 0, 1], [0, 0, -1], [0, -1, 0], [0, 1, 0]] as const) {
      const n: V3 = [c[0] + d[0], c[1] + d[1], c[2] + d[2]];
      const [nid, nm] = blockAt(n[0], n[1], n[2]);
      if (lava ? isWater(nid) : isLava(nid))
        touches = lava
          ? `the lava would reach water at ${key(n)} (stone or cobblestone forms)`
          : `the water would reach lava at ${key(n)} (${nm === 0 ? 'obsidian' : 'cobblestone'} forms)`;
    }
  };
  const q: Array<[V3, number]> = [...cells].sort((p, r) => p[1] - r[1] || p[0] - r[0] || p[2] - r[2]).map((c) => [c, 0]);
  /** Falls straight down from `below` (12 at most); where it lands it spreads again. */
  const fall = (below: V3) => {
    let f = below,
      fallen = 1;
    const mark = (x: V3) => {
      if (seen.has(key(x))) return;
      seen.add(key(x));
      reachCell(x);
    };
    mark(f);
    while (fallen < maxFall && open([f[0], f[1] - 1, f[2]])) {
      f = [f[0], f[1] - 1, f[2]];
      mark(f);
      fallen++;
    }
    if (fallen < maxFall) q.push([f, 0]);
  };
  /** Runs one step sideways into each open neighbour. */
  const sideways = (c: V3, d: number) => {
    for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]] as const) {
      const n: V3 = [c[0] + dx, c[1], c[2] + dz];
      if (open(n) && !seen.has(key(n))) {
        seen.add(key(n));
        reachCell(n);
        q.push([n, d + 1]);
      }
    }
  };
  while (q.length && spread.length < max) {
    const [c, d] = q.shift()!;
    const below: V3 = [c[0], c[1] - 1, c[2]];
    if (open(below)) fall(below);
    else if (d < reach) sideways(c, d);
  }
  return { spread, touches };
}

/** A channel's cells in pouring order: every other cell first (no two side by side), then the ones between them, which fill by
 * themselves (two sources beside them, over ground). A 2×2 pool: two opposite corners. */
export function pourOrder<T extends V3>(cells: readonly T[]): T[] {
  const taken = new Set<string>();
  const first: T[] = [];
  for (const c of cells) {
    const near = [[1, 0], [-1, 0], [0, 1], [0, -1]].some(([dx, dz]) => taken.has(`${c[0] + dx!},${c[1]},${c[2] + dz!}`));
    if (near) continue;
    first.push(c);
    taken.add(`${c[0]},${c[1]},${c[2]}`);
  }
  return [...first, ...cells.filter((c) => !taken.has(`${c[0]},${c[1]},${c[2]}`))];
}

export interface IrrigationPlan {
  cells: V3[];
  dig: V3[];
  axis: 'x' | 'z';
  lines: number;
  note: string;
}

/** The zone's columns (sorted by z then x) and how its channels run: along its longer side, `lo..hi` across it. */
function channelFrame(zone: ZoneLike) {
  const cols = [...zoneCellSet(zone)].map((k) => [cellX(k), cellZ(k)] as const).sort((p, q) => p[1] - q[1] || p[0] - q[0]);
  const xs = cols.map((c) => c[0]),
    zs = cols.map((c) => c[1]);
  const x1 = Math.min(...xs),
    x2 = Math.max(...xs),
    z1 = Math.min(...zs),
    z2 = Math.max(...zs);
  const alongX = x2 - x1 >= z2 - z1;
  return { cols, alongX, lo: alongX ? z1 : x1, hi: alongX ? z2 : x2 };
}

/** The channel lines across `lo..hi`: a centre line when it fits one spacing, else every `spacing` (+ the far edge if dry). */
function channelLines(lo: number, hi: number, spacing: number, cover: number): number[] {
  const across = hi - lo + 1;
  if (across <= spacing) return [lo + Math.floor(across / 2)];
  const lines: number[] = [];
  for (let k = lo + cover; k <= hi; k += spacing) lines.push(k);
  if (lines[lines.length - 1]! + cover < hi) lines.push(hi);
  return lines;
}

interface Soil {
  soilAt: (x: number, z: number) => number | null;
  watered: (x: number, y: number, z: number) => boolean;
}

/** A line's soil cells, and whether any soil in its band (`cover` either side) is still dry. */
function lineBand(
  frame: ReturnType<typeof channelFrame>,
  line: number,
  cover: number,
  soil: Soil,
): { row: V3[]; dry: boolean } {
  const row: V3[] = [];
  let dry = false;
  for (const [x, z] of frame.cols) {
    const a = frame.alongX ? z : x;
    if (Math.abs(a - line) > cover) continue;
    const y = soil.soilAt(x, z);
    if (y === null) continue;
    if (!soil.watered(x, y, z)) dry = true;
    if (a === line) row.push([x, y, z]);
  }
  return { row, dry };
}

/** Crops: farmland is watered within 4 blocks, so a channel every 9 rows; sugar cane needs water beside it, every 3. */
const CROP_CHANNELS = { spacing: 9, cover: 4 };
const CANE_CHANNELS = { spacing: 3, cover: 1 };

/** Where the channels run, for the note. */
function channelWhat(cane: boolean, narrow: boolean): string {
  if (cane) return 'beside the sugar cane (every 3rd row)';
  return narrow ? 'down the middle' : 'every 9th row';
}

/** A kept line's cells: those not water already become channel cells, dug out first unless water can take their block. */
function channelRow(row: readonly V3[], holds: (x: number, y: number, z: number) => number, cells: V3[], dig: V3[]) {
  for (const c of row) {
    const id = holds(c[0], c[1], c[2]);
    if (isWater(id)) continue;
    cells.push(c);
    if (!TAKES.has(id)) dig.push(c);
  }
}

/** The plan's line for the owner. */
function irrigationNote(p: { cells: number; dig: number; kept: number; axis: string; what: string }): string {
  if (!p.cells) return 'the zone is watered already: every cell has water close enough';
  const channels = `${p.kept} channel${p.kept === 1 ? '' : 's'}`;
  const cells = `${p.cells} water cell${p.cells === 1 ? '' : 's'}`;
  return `${channels} along ${p.axis} ${p.what}: ${cells}` + (p.dig ? `, ${p.dig} dug out first` : '');
}

/**
 * The water channels a growing zone wants (the director's planner): farmland needs water within 4 blocks, so for a crop a
 * channel at soil level along the zone's longer side every 9 rows (a centre line when it is 9 or narrower, one more by the far
 * edge when that would stay dry); sugar cane needs water right beside it, so one every 3 rows. A line whose band is all watered
 * already is left out. `soilAt(x, z)`: the soil block's y in that column (null: none); `watered(x, y, z)`: water close enough to
 * that soil already; `holds(x, y, z)`: the block there now (soil is dug first).
 */
export function irrigationPlan(
  zone: ZoneLike,
  cane: boolean,
  soilAt: (x: number, z: number) => number | null,
  watered: (x: number, y: number, z: number) => boolean,
  holds: (x: number, y: number, z: number) => number,
): IrrigationPlan {
  const frame = channelFrame(zone);
  const { spacing, cover } = cane ? CANE_CHANNELS : CROP_CHANNELS;
  const cells: V3[] = [],
    dig: V3[] = [];
  let kept = 0;
  for (const line of channelLines(frame.lo, frame.hi, spacing, cover)) {
    const { row, dry } = lineBand(frame, line, cover, { soilAt, watered });
    if (!dry || !row.length) continue;
    kept++;
    channelRow(row, holds, cells, dig);
  }
  const axis = frame.alongX ? 'x' : 'z';
  const what = channelWhat(cane, frame.hi - frame.lo + 1 <= spacing);
  const note = irrigationNote({ cells: cells.length, dig: dig.length, kept, axis, what });
  return { cells, dig, axis, lines: kept, note };
}
