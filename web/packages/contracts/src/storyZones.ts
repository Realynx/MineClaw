/**
 * Story Mode zones as cell sets, painted RimWorld-style (docs/web/story-mode.md "Zones"). The same rules as the bot's
 * `ZonePainter` (src/MineClaw.Hive/Story/StoryZones.cs): the mock director applies them and the map previews a paint or an erase
 * with them before it is sent.
 *
 * - A zone is a set of XZ columns (row runs `[z, x1, x2]`, normalised) plus its level range (`area` y; `area` x/z is the cells'
 *   bounding box). Zones without `rows` (older saves) are their whole `area` rectangle.
 * - Add kind K: cells touching (4-neighbour) or overlapping zones of the same type join them; several touched zones merge into the
 *   oldest (its id, name, settings). Same type = same kind, and for growing zones (`farm`) the same plant. `into` extends that zone
 *   (its type wins). Work zones (farm, chop, mine) are mutually exclusive: the painted cells leave the other work zones.
 * - Home and keep-out (`forbidden`) are overlay layers: they never overwrite and are never overwritten.
 * - Erase: the cells leave the zones of a kind (or any); an empty zone goes, a zone cut in parts becomes several (" 2", " 3").
 */
import type { StoryArea } from './story.js';

export type ZoneRow = [z: number, x1: number, x2: number];

export interface ZoneLike {
  id: string | number;
  kind: string;
  area: StoryArea;
  rows?: ZoneRow[] | null;
  plant?: string | null;
  name?: string | null;
  cells?: number | null;
}

export const ZONE_OVERLAYS: readonly string[] = ['home', 'forbidden'];
export const ZONE_MAX_CELLS = 65_536;
export const isOverlayZone = (kind: string): boolean => ZONE_OVERLAYS.includes(kind);

const OFF = 2 ** 25;
const SPAN = 2 ** 26;
/** A column's key (exact for |x|, |z| < 2^25). */
export const cellKey = (x: number, z: number): number => (x + OFF) * SPAN + (z + OFF);
export const cellX = (k: number): number => Math.floor(k / SPAN) - OFF;
export const cellZ = (k: number): number => (k % SPAN) - OFF;

export function rectCells(x1: number, z1: number, x2: number, z2: number): Set<number> {
  const s = new Set<number>();
  for (let x = Math.min(x1, x2); x <= Math.max(x1, x2); x++)
    for (let z = Math.min(z1, z2); z <= Math.max(z1, z2); z++) s.add(cellKey(x, z));
  return s;
}

const cache = new WeakMap<object, Set<number>>();
/** A zone's columns (cached per rows array). */
export function zoneCellSet(z: ZoneLike): Set<number> {
  const src = (z.rows ?? z.area) as object;
  const hit = cache.get(src);
  if (hit) return hit;
  let s: Set<number>;
  if (z.rows) {
    s = new Set();
    for (const [rz, x1, x2] of z.rows) for (let x = Math.min(x1, x2); x <= Math.max(x1, x2); x++) s.add(cellKey(x, rz));
  } else s = rectCells(z.area[0], z.area[2], z.area[3], z.area[5]);
  cache.set(src, s);
  return s;
}

/** Normalised row runs: sorted by z then x, each maximal. */
export function rowsOf(cells: Iterable<number>): ZoneRow[] {
  const pts = [...cells].map((k) => [cellX(k), cellZ(k)] as const).sort((a, b) => a[1] - b[1] || a[0] - b[0]);
  const rows: ZoneRow[] = [];
  let cur: ZoneRow | null = null;
  for (const [x, z] of pts) {
    if (cur && cur[0] === z && cur[2] === x - 1) cur[2] = x;
    else rows.push((cur = [z, x, x]));
  }
  return rows;
}

const N4 = [
  [1, 0],
  [-1, 0],
  [0, 1],
  [0, -1],
] as const;

/** Do the sets overlap or touch (4-neighbour)? */
export function cellsTouch(a: Set<number>, b: Set<number>): boolean {
  const [small, big] = a.size <= b.size ? [a, b] : [b, a];
  for (const k of small) {
    if (big.has(k)) return true;
    const x = cellX(k),
      z = cellZ(k);
    for (const [dx, dz] of N4) if (big.has(cellKey(x + dx, z + dz))) return true;
  }
  return false;
}

const overlapsSet = (a: Set<number>, b: Set<number>): boolean => {
  const [small, big] = a.size <= b.size ? [a, b] : [b, a];
  for (const k of small) if (big.has(k)) return true;
  return false;
};

/** 4-connected parts, largest first (ties: the one whose first cell sorts first). */
export function components(cells: Set<number>): Set<number>[] {
  const left = new Set(cells);
  const parts: Set<number>[] = [];
  const sorted = [...cells].sort((a, b) => cellZ(a) - cellZ(b) || cellX(a) - cellX(b));
  for (const s of sorted) {
    if (!left.delete(s)) continue;
    const part = new Set<number>();
    const stack = [s];
    while (stack.length) {
      const k = stack.pop()!;
      part.add(k);
      const x = cellX(k),
        z = cellZ(k);
      for (const [dx, dz] of N4) {
        const n = cellKey(x + dx, z + dz);
        if (left.delete(n)) stack.push(n);
      }
    }
    parts.push(part);
  }
  return parts
    .map((p, i) => [p, i] as const)
    .sort((a, b) => b[0].size - a[0].size || a[1] - b[1])
    .map((p) => p[0]);
}

export interface ZonePaintResult {
  /** The zone the painted cells are in now (add). */
  id?: string | number;
  created: Array<string | number>;
  merged: Array<{ into: string | number; from: Array<string | number> }>;
  grown: Array<string | number>;
  shrunk: Array<string | number>;
  split: Array<{ from: string | number; into: Array<string | number> }>;
  deleted: Array<string | number>;
}

export interface ZonePaintOut<Z extends ZoneLike> {
  zones: Z[];
  result: ZonePaintResult;
  /** Cells taken out of other zones (add: overwritten work zones; erase: everything erased). */
  removedCells: Set<number>;
}

const sameType = (z: ZoneLike, kind: string, plant: string | null | undefined) =>
  z.kind === kind && (kind !== 'farm' || (z.plant ?? null) === (plant ?? null));

function withCells<Z extends ZoneLike>(z: Z, cells: Set<number>, y1: number, y2: number): Z {
  let x1 = Infinity,
    z1 = Infinity,
    x2 = -Infinity,
    z2 = -Infinity;
  for (const k of cells) {
    const x = cellX(k),
      zz = cellZ(k);
    x1 = Math.min(x1, x);
    x2 = Math.max(x2, x);
    z1 = Math.min(z1, zz);
    z2 = Math.max(z2, zz);
  }
  const rows = rowsOf(cells);
  cache.set(rows, cells);
  return { ...z, area: [x1, y1, z1, x2, y2, z2] as StoryArea, rows, cells: cells.size };
}

function subtract<Z extends ZoneLike>(
  zones: Z[],
  i: number,
  cells: Set<number>,
  r: ZonePaintResult,
  removed: Set<number>,
  newId: () => string | number,
): void {
  const z = zones[i]!;
  const own = zoneCellSet(z);
  const rest = new Set<number>();
  for (const k of own) {
    if (cells.has(k)) removed.add(k);
    else rest.add(k);
  }
  if (rest.size === own.size) return;
  const [y1, y2] = [z.area[1], z.area[4]];
  if (rest.size === 0) {
    zones.splice(i, 1);
    r.deleted.push(z.id);
    return;
  }
  const parts = components(rest);
  zones[i] = withCells(z, parts[0]!, y1, y2);
  r.shrunk.push(z.id);
  if (parts.length === 1) return;
  const into: Array<string | number> = [];
  parts.slice(1).forEach((p, j) => {
    const id = newId();
    const suffix = ` ${j + 2}`;
    const name = z.name ? z.name.slice(0, 40 - suffix.length) + suffix : (z.name ?? null);
    zones.splice(i + 1 + j, 0, withCells({ ...z, id, name }, p, y1, y2));
    into.push(id);
  });
  r.split.push({ from: z.id, into });
}

const emptyResult = (): ZonePaintResult => ({ created: [], merged: [], grown: [], shrunk: [], split: [], deleted: [] });

/** Shrunk / split / deleted lists come out in age order (the loops walk the zones newest first). */
function ageOrder(r: ZonePaintResult): void {
  r.shrunk.reverse();
  r.split.reverse();
  r.deleted.reverse();
}

const badRequest = (message: string) => Object.assign(new Error(message), { status: 400 });

/** The zone `into` names (undefined: none named); throws when it names no zone. */
function intoZone<Z extends ZoneLike>(zones: readonly Z[], into: string | number | null | undefined): Z | undefined {
  if (into === undefined || into === null) return undefined;
  const z = zones.find((x) => String(x.id) === String(into));
  if (!z) throw badRequest('into: no such zone');
  return z;
}

/** The painted type: `into`'s (kind and plant), else the request's (a plant only for growing zones). */
function paintType(into: ZoneLike | undefined, req: { kind: string; plant?: string | null }) {
  const kind = into?.kind ?? req.kind;
  if (into) return { kind, plant: into.plant ?? null };
  return { kind, plant: kind === 'farm' ? (req.plant ?? null) : null };
}

/** Work zones are mutually exclusive: the painted cells leave every other non-overlay zone (newest first). */
function overwriteOthers<Z extends ZoneLike>(
  zones: Z[],
  type: { kind: string; plant: string | null },
  cells: Set<number>,
  out: { r: ZonePaintResult; removed: Set<number> },
  newId: () => string | number,
): void {
  for (let i = zones.length - 1; i >= 0; i--) {
    const z = zones[i]!;
    if (!isOverlayZone(z.kind) && !sameType(z, type.kind, type.plant) && overlapsSet(zoneCellSet(z), cells))
      subtract(zones, i, cells, out.r, out.removed, newId);
  }
}

/** The painted cells join `target`; the other touched zones of the type merge into it (their cells and level range). */
function mergeInto<Z extends ZoneLike>(
  zones: Z[],
  target: Z,
  touched: readonly Z[],
  cells: Set<number>,
  y: readonly [number, number],
  r: ZonePaintResult,
): void {
  const all = new Set(zoneCellSet(target));
  const before = all.size;
  for (const k of cells) all.add(k);
  let lo = Math.min(target.area[1], y[0]),
    hi = Math.max(target.area[4], y[1]);
  const from: Array<string | number> = [];
  for (const o of touched) {
    if (o === target) continue;
    for (const k of zoneCellSet(o)) all.add(k);
    lo = Math.min(lo, o.area[1]);
    hi = Math.max(hi, o.area[4]);
    zones.splice(zones.indexOf(o), 1);
    from.push(o.id);
  }
  if (from.length) r.merged.push({ into: target.id, from });
  else if (all.size > before) r.grown.push(target.id);
  zones[zones.indexOf(target)] = withCells(target, all, lo, hi);
  r.id = target.id;
}

/**
 * Paint `rect` ([x1, z1, x2, z2]) at level `y` as a zone of `kind` (growing: `plant`); `into` extends that zone. `make` builds a new
 * zone's settings (id, name, growing settings) when the cells touch no zone of the type. Throws past {@link ZONE_MAX_CELLS}.
 */
export function paintZones<Z extends ZoneLike>(
  zonesIn: readonly Z[],
  req: {
    kind: string;
    plant?: string | null;
    rect: readonly [number, number, number, number];
    y: readonly [number, number];
    into?: string | number | null;
    /** The cells to paint when not the whole rect (a fishing zone: only its still water). */
    cells?: ReadonlySet<number> | null;
  },
  make: (kind: string, plant: string | null) => Z,
  newId: () => string | number,
): ZonePaintOut<Z> {
  const zones = [...zonesIn];
  const cells = req.cells ? new Set(req.cells) : rectCells(...req.rect);
  if (cells.size === 0) throw badRequest('nothing to paint here');
  const into = intoZone(zones, req.into);
  const type = paintType(into, req);
  const touched = zones.filter((z) => sameType(z, type.kind, type.plant) && cellsTouch(zoneCellSet(z), cells));
  const target = into && touched.includes(into) ? into : touched[0];
  if (cells.size + touched.reduce((n, z) => n + zoneCellSet(z).size, 0) > ZONE_MAX_CELLS)
    throw badRequest(`zone too large (${ZONE_MAX_CELLS} cells max)`);
  const r = emptyResult();
  const removed = new Set<number>();
  if (!isOverlayZone(type.kind)) overwriteOthers(zones, type, cells, { r, removed }, newId);
  ageOrder(r);
  if (target) mergeInto(zones, target, touched, cells, req.y, r);
  else {
    const z = withCells(make(type.kind, type.plant), new Set(cells), req.y[0], req.y[1]);
    zones.push(z);
    r.created.push(z.id);
    r.id = z.id;
  }
  return { zones, result: r, removedCells: removed };
}

/** Erase `rect` ([x1, z1, x2, z2]) from every zone of `kind` (null / 'any' = every kind). */
export function eraseZones<Z extends ZoneLike>(
  zonesIn: readonly Z[],
  kind: string | null | undefined,
  rect: readonly [number, number, number, number],
  newId: () => string | number,
): ZonePaintOut<Z> {
  const zones = [...zonesIn];
  const cells = rectCells(...rect);
  const r = emptyResult();
  const removed = new Set<number>();
  for (let i = zones.length - 1; i >= 0; i--) {
    const z = zones[i]!;
    if ((!kind || kind === 'any' || z.kind === kind) && overlapsSet(zoneCellSet(z), cells))
      subtract(zones, i, cells, r, removed, newId);
  }
  ageOrder(r);
  return { zones, result: r, removedCells: removed };
}

const CLOCKWISE = [
  [1, 0],
  [0, 1],
  [-1, 0],
  [0, -1],
] as const;

/**
 * A quarry's stair ring (the bot's `StoryQuarry.Ring`): the zone's edge cells clockwise from its north-west-most one; layer k
 * leaves ring cell k mod n standing as a step (a 2×2 zone's ring is its 4 cells). Null = no closed ring of 4+ (a 1-wide zone).
 */
export function quarryRing(cells: ReadonlySet<number>): Array<[number, number]> | null {
  const edge = ringEdge(cells);
  if (edge.size < 4) return null;
  const start = [...edge].sort((a, b) => cellZ(a) - cellZ(b) || cellX(a) - cellX(b))[0]!;
  const ring = walkRing(edge, start);
  const last = ring[ring.length - 1]!;
  const closed = Math.abs(cellX(last) - cellX(start)) + Math.abs(cellZ(last) - cellZ(start)) === 1;
  return ring.length >= 4 && closed ? ring.map((k) => [cellX(k), cellZ(k)]) : null;
}

/** The zone's edge cells (a side open to the outside). */
function ringEdge(cells: ReadonlySet<number>): Set<number> {
  const edge = new Set<number>();
  for (const k of cells) {
    const x = cellX(k),
      z = cellZ(k);
    if (CLOCKWISE.some(([dx, dz]) => !cells.has(cellKey(x + dx, z + dz)))) edge.add(k);
  }
  return edge;
}

/** Walks the edge clockwise from `start` (left turn first, then straight, right, back) until no new edge cell is next. */
function walkRing(edge: ReadonlySet<number>, start: number): number[] {
  const ring = [start];
  const seen = new Set([start]);
  let heading = 0;
  let cur = start;
  for (;;) {
    const step = nextRingStep(edge, seen, cur, heading);
    if (!step) break;
    seen.add(step.cell);
    ring.push(step.cell);
    cur = step.cell;
    heading = step.heading;
  }
  return ring;
}

function nextRingStep(edge: ReadonlySet<number>, seen: ReadonlySet<number>, cur: number, heading: number) {
  for (const turn of [-1, 0, 1, 2]) {
    const d = (heading + turn + 4) % 4;
    const n = cellKey(cellX(cur) + CLOCKWISE[d]![0], cellZ(cur) + CLOCKWISE[d]![1]);
    if (edge.has(n) && !seen.has(n)) return { cell: n, heading: d };
  }
  return null;
}

/** Layer k's step: ring cell k mod n at y = top − k. */
export function quarryStep(
  ring: ReadonlyArray<readonly [number, number]>,
  top: number,
  k: number,
): [number, number, number] {
  const [x, z] = ring[k % ring.length]!;
  return [x, top - k, z];
}

/**
 * The still-water columns of a block scan (flat `[x, y, z, id, meta, below]` records of ids 8/9): a column counts when its top
 * water block is a source (meta 0). What a fishing zone keeps of a painted area (the bot also wants open air above).
 */
export function waterColumns(scan: readonly number[]): Set<number> {
  const top = new Map<number, { y: number; meta: number }>();
  for (let i = 0; i + 5 < scan.length + 1; i += 6) {
    const [x, y, z, id, meta] = [scan[i]!, scan[i + 1]!, scan[i + 2]!, scan[i + 3]!, scan[i + 4]!];
    if (id !== 8 && id !== 9) continue;
    const k = cellKey(x, z);
    const t = top.get(k);
    if (!t || y > t.y) top.set(k, { y, meta });
  }
  return new Set([...top].filter(([, t]) => t.meta === 0).map(([k]) => k));
}

/** "a zone" / "N zones". */
const zonesWord = (n: number) => (n === 1 ? 'a zone' : `${n} zones`);

/** What the paint cut out of other zones: the cells it takes (when counted), else the zones it shrinks. */
function cutPhrase(r: ZonePaintResult, removedCells: number): string | null {
  if (!r.shrunk.length) return null;
  if (!removedCells) return `shrinks ${zonesWord(r.shrunk.length)}`;
  return `takes ${removedCells} cell${removedCells === 1 ? '' : 's'} from ${zonesWord(r.shrunk.length + r.deleted.length)}`;
}

/** A one-line summary of what a paint or erase did ("merged 2 zones", "overwrote 6 cells of 1 zone"…), for toasts and previews. */
export function describeZonePaint(r: ZonePaintResult, removedCells = 0): string {
  const out: string[] = [];
  if (r.created.length) out.push('a new zone');
  for (const m of r.merged) out.push(`merged ${m.from.length + 1} zones`);
  if (r.grown.length) out.push('extends the zone');
  if (r.split.length) out.push(`splits ${zonesWord(r.split.length)}`);
  const cut = cutPhrase(r, removedCells);
  if (cut) out.push(cut);
  if (r.deleted.length) out.push(`removes ${zonesWord(r.deleted.length)}`);
  return out.join(', ') || 'no change';
}
