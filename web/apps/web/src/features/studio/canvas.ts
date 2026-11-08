/**
 * The Design Studio canvas in the browser: the same sandbox the bot holds (contracts designer.ts), local coordinates x, z
 * 0..63 and y -16..47, cells `[x, y, z, id, meta]` (id 0 = air). y 0 is the first layer above the grass; under it (y < 0)
 * is the ground: earth until dug. There id 0 digs the cell out and `EARTH` (-1) fills it back. Every change, the AI's and the
 * user's, goes through `apply`, which returns what it really changed and the inverse, so one history covers both (undo of an
 * AI op is a user edit the bot then hears about; undo of a dig fills it back). Two-cell blocks stay whole (a door's or a
 * double plant's upper half, a bed's head: `partnerOf`), and a chest placed beside a single chest takes its facing (they pair),
 * the same rules as the bot's canvas.
 */
import { DESIGNER_EARTH, DESIGNER_MINE_PLOT, DESIGNER_PLOT, type DesignerCell } from '@mineclaw/contracts';
import { facingOf, firstHalfOf, partnerOf, twoCell } from './facing';

export type Cell = DesignerCell;
export type V3 = [number, number, number];

export let SIZE: number = DESIGNER_PLOT.size;
export const HEIGHT = DESIGNER_PLOT.height;
/** Layers of ground under the grass (y -DEPTH..-1). */
export const DEPTH = DESIGNER_PLOT.depth;
/** Restores the generated terrain / removes mine intent; on build plots, undisturbed ground below y 0. */
export const EARTH = DESIGNER_EARTH;

/** What a mine canvas's ground is made of: the generated terrain (./mine/mineTerrain). */
export interface PlotTerrain {
  /** The state at a cell (id << 4 | meta; 0 = air). */
  state(x: number, y: number, z: number): number;
}

/**
 * The plot's mode (one studio per page, so module-wide): a build stands on flat grass (canvas y -16..47, the ground is y < 0,
 * canvas y 0 = world y 64); a mine is dug into generated terrain at absolute y (canvas y 0..127 = the world's), its ground
 * every solid generated cell, bedrock never dug.
 */
export interface PlotMode {
  kind: 'build' | 'mine';
  size: number;
  minY: number;
  maxY: number;
  /** World y of canvas y 0. */
  baseY: number;
  terrain: PlotTerrain | null;
}

const BUILD_MODE: PlotMode = { kind: 'build', size: DESIGNER_PLOT.size, minY: -DEPTH, maxY: HEIGHT - 1, baseY: 64, terrain: null };
let plot: PlotMode = BUILD_MODE;

/** Sets the plot's mode: a build (the default) or a mine over a terrain. */
export function setPlotMode(m: { kind: 'build' } | { kind: 'mine'; terrain: PlotTerrain }): void {
  plot =
    m.kind === 'mine'
      ? {
          kind: 'mine',
          size: DESIGNER_MINE_PLOT.size,
          minY: 0 - DESIGNER_MINE_PLOT.depth,
          maxY: DESIGNER_MINE_PLOT.height - 1,
          baseY: 0,
          terrain: m.terrain,
        }
      : BUILD_MODE;
  SIZE = plot.size;
}

/** The plot's mode now. */
export const plotMode = (): PlotMode => plot;

export const inside = (x: number, y: number, z: number): boolean =>
  x >= 0 && x < SIZE && z >= 0 && z < SIZE && y >= plot.minY && y <= plot.maxY;
export const keyOf = (x: number, y: number, z: number): number => x + SIZE * (z + SIZE * (y + DEPTH));
export const posOf = (k: number): V3 => [k % SIZE, Math.floor(k / (SIZE * SIZE)) - DEPTH, Math.floor(k / SIZE) % SIZE];
/** The ground (undisturbed unless dug): below the grass's top on a build plot, any solid generated cell on a mine. */
export const below = (x: number, y: number, z: number): boolean =>
  plot.terrain ? plot.terrain.state(x, y, z) >> 4 !== 0 : y < 0;
/** Ground that never digs (a mine's bedrock). */
export const bedrock = (x: number, y: number, z: number): boolean =>
  !!plot.terrain && plot.terrain.state(x, y, z) >> 4 === 7;

/** An applied change set: the cells as they became (`forward`) and as they were (`backward`). */
export interface Change {
  forward: Cell[];
  backward: Cell[];
}

/** An `apply` in progress: each touched cell's state before it (first touch) in touch order. */
interface ApplyRun {
  before: Map<number, number>;
  touched: number[];
  pairDoors: boolean;
}

function remember(run: ApplyRun, k: number, was: number) {
  if (run.before.has(k)) return;
  run.before.set(k, was);
  run.touched.push(k);
}

/**
 * On a mine, air always records planned excavation and earth removes that intent, restoring the generated terrain (which
 * may already be air). A mine's bedrock never digs. On builds, air only records digs below ground.
 */
function nextState(x: number, y: number, z: number, id: number, meta: number): number {
  if (id > 0) return id * 16 + (meta & 15);
  return (id === EARTH && plot.kind === 'mine') || ((id === EARTH || bedrock(x, y, z)) && below(x, y, z)) ? -1 : 0;
}

const stateCell = (x: number, y: number, z: number, v: number): Cell =>
  v < 0 ? [x, y, z, EARTH, 0] : [x, y, z, v >> 4, v & 15];

/** A neighbour chest facing `f` on side dx (else dz) faces across the pair (so the new chest takes its facing). */
const acrossPair = (dx: number, f: string): boolean =>
  dx !== 0 ? f === 'north' || f === 'south' : f === 'east' || f === 'west';

/** The four sides a chest pairs across. */
const CHEST_SIDES: ReadonlyArray<readonly [number, number]> = [
  [-1, 0],
  [1, 0],
  [0, -1],
  [0, 1],
];

export class Canvas {
  /** key → id * 16 + meta. */
  readonly blocks = new Map<number, number>();
  /** Authored air cells (all mine excavation intent, even over natural caves; build digs below ground). */
  readonly dug = new Set<number>();

  get size(): number {
    return this.blocks.size;
  }

  /** The state at a cell: a block, air ([0, 0]) or, below ground and not dug, earth ([EARTH, 0]). */
  get(x: number, y: number, z: number): [number, number] {
    const k = keyOf(x, y, z);
    const v = this.blocks.get(k);
    if (v !== undefined) return [v >> 4, v & 15];
    return below(x, y, z) && !this.dug.has(k) ? [EARTH, 0] : [0, 0];
  }

  /** The block id at a cell: 0 = air, `EARTH` = undisturbed ground (anything not 0 is solid to the tools). */
  id(x: number, y: number, z: number): number {
    return this.get(x, y, z)[0];
  }

  /** Undisturbed ground: below the grass, no block, not dug. */
  earth(x: number, y: number, z: number): boolean {
    return below(x, y, z) && !this.blocks.has(keyOf(x, y, z)) && !this.dug.has(keyOf(x, y, z));
  }

  /** Every block as cells. */
  cells(): Cell[] {
    const out: Cell[] = [];
    for (const [k, v] of this.blocks) {
      const [x, y, z] = posOf(k);
      out.push([x, y, z, v >> 4, v & 15]);
    }
    return out;
  }

  /** The dug cells as [x, y, z]. */
  holes(): V3[] {
    return [...this.dug].map(posOf);
  }

  load(cells: readonly Cell[], dug: readonly V3[] = []): void {
    this.blocks.clear();
    this.dug.clear();
    for (const c of cells)
      if (inside(c[0], c[1], c[2]) && c[3] > 0) this.blocks.set(keyOf(c[0], c[1], c[2]), c[3] * 16 + (c[4] & 15));
    for (const p of dug)
      // A mine keeps every dug cell it loads (a design keeps its digs whatever the terrain); a build only those below ground.
      if (
        inside(p[0], p[1], p[2]) &&
        (plot.kind === 'mine' || below(p[0], p[1], p[2])) &&
        !this.blocks.has(keyOf(p[0], p[1], p[2]))
      )
        this.dug.add(keyOf(p[0], p[1], p[2]));
  }

  /** Authored state for history/saving: block state, 0 = planned air, -1 = untouched terrain on mines. */
  authoredState(k: number): number {
    const v = this.blocks.get(k);
    if (v !== undefined) return v;
    const [x, y, z] = posOf(k);
    return (plot.kind === 'mine' || below(x, y, z)) && !this.dug.has(k) ? -1 : 0;
  }

  /**
   * A chest placed beside a single chest of its kind takes that chest's facing when it is across the pair (the bot's
   * `DesignerCanvas.Pair`); else the meta as given.
   */
  private chestMeta(x: number, y: number, z: number, id: number, meta: number): number {
    if (id !== 54 && id !== 146) return meta;
    for (const [dx, dz] of CHEST_SIDES) {
      const [oid, ometa] = this.get(x + dx, y, z + dz);
      const f = oid === id ? facingOf(oid, ometa) : null;
      if (!f || this.pairedAway(x + dx, y, z + dz, dx, dz, id)) continue;
      return acrossPair(dx, f) ? ometa : meta;
    }
    return meta;
  }

  /** The chest at (x, y, z) already has a partner of kind `id` on a side other than the one back toward (−dx, −dz). */
  private pairedAway(x: number, y: number, z: number, dx: number, dz: number, id: number): boolean {
    return CHEST_SIDES.some(([sx, sz]) => !(sx === -dx && sz === -dz) && this.get(x + sx, y, z + sz)[0] === id);
  }

  /**
   * Applies cells in order (later wins). Out-of-plot cells are skipped. With `pairDoors`, a placed first half of a two-cell
   * block brings its second (a door's upper half, a bed's head), replacing either half clears the other, and chests pair
   * (what the bot's canvas does too).
   */
  apply(cells: readonly Cell[], pairDoors = true): Change {
    const run: ApplyRun = { before: new Map(), touched: [], pairDoors };
    for (const c of cells) {
      const meta = pairDoors && c[3] > 0 ? this.chestMeta(c[0], c[1], c[2], c[3], c[4]) : c[4];
      this.setCell(run, c[0], c[1], c[2], c[3], meta);
      const p = pairDoors && c[3] > 0 ? partnerOf(c[3], meta) : null;
      if (p) this.setCell(run, c[0] + p[0], c[1] + p[1], c[2] + p[2], c[3], p[3]);
    }
    return this.changeOf(run);
  }

  /** Sets one cell (out of the plot: skipped), first clearing the other half of a two-cell block it replaces. */
  private setCell(run: ApplyRun, x: number, y: number, z: number, id: number, meta: number) {
    if (!inside(x, y, z)) return;
    const k = keyOf(x, y, z);
    const old = this.authoredState(k);
    const next = nextState(x, y, z, id, meta);
    if (old === next) return;
    remember(run, k, old);
    if (run.pairDoors && old > 0 && twoCell(old >> 4) && id !== old >> 4) this.clearPartner(run, x, y, z, old);
    this.put(k, next);
  }

  /** Clears the other half of the two-cell block `old` at (x, y, z), when it is still there. */
  private clearPartner(run: ApplyRun, x: number, y: number, z: number, old: number) {
    const rel = firstHalfOf(old >> 4, old & 15) ?? partnerOf(old >> 4, old & 15);
    if (!rel) return;
    const [ox, oy, oz] = [x + rel[0], y + rel[1], z + rel[2]];
    const ok = inside(ox, oy, oz) ? keyOf(ox, oy, oz) : -1;
    const other = ok >= 0 ? this.blocks.get(ok) : undefined;
    if (other === undefined || other >> 4 !== old >> 4) return;
    remember(run, ok, other);
    this.put(ok, bedrock(ox, oy, oz) ? -1 : 0);
  }

  /** Writes a state: a block, air (dug when below ground) or earth (-1: neither a block nor dug). */
  private put(k: number, next: number) {
    const [x, y, z] = posOf(k);
    this.blocks.delete(k);
    this.dug.delete(k);
    if (next > 0) this.blocks.set(k, next);
    else if (next === 0 && (plot.kind === 'mine' || below(x, y, z))) this.dug.add(k);
  }

  /** What the run really changed (cells as they became) and its inverse (as they were). */
  private changeOf(run: ApplyRun): Change {
    const forward: Cell[] = [];
    const backward: Cell[] = [];
    for (const k of run.touched) {
      const [x, y, z] = posOf(k);
      const now = this.authoredState(k);
      const was = run.before.get(k)!;
      if (now === was) continue;
      forward.push(stateCell(x, y, z, now));
      backward.push(stateCell(x, y, z, was));
    }
    return { forward, backward };
  }

  /** The box of the blocks and the dug cells, or null when empty. */
  bounds(): { min: V3; max: V3 } | null {
    if (this.blocks.size === 0 && this.dug.size === 0) return null;
    const min: V3 = [SIZE, plot.maxY + 1, SIZE];
    const max: V3 = [-1, plot.minY - 1, -1];
    for (const k of [...this.blocks.keys(), ...this.dug]) {
      const p = posOf(k);
      for (let i = 0; i < 3; i++) {
        if (p[i]! < min[i]!) min[i] = p[i]!;
        if (p[i]! > max[i]!) max[i] = p[i]!;
      }
    }
    return { min, max };
  }
}

// ---------------------------------------------------------------------------------------------------------- history

export interface OpGroup {
  label: string;
  by: 'ai' | 'user';
  forward: Cell[];
  backward: Cell[];
  /** Metadata changed with the cells (for example, the position of a mine module). */
  effect?: { undo(): void; redo(): void };
}

/** Undo/redo over AI and user ops alike. `undo()` returns the cells to apply (the group's inverse). */
export class History {
  private past: OpGroup[] = [];
  private future: OpGroup[] = [];
  constructor(readonly limit = 200) {}

  push(g: OpGroup): void {
    if (g.forward.length === 0 && !g.effect) return;
    this.past.push(g);
    if (this.past.length > this.limit) this.past.shift();
    this.future = [];
  }

  /** Folds `g` into the newest group (one brush stroke or one gizmo drag is one undo step). */
  merge(g: OpGroup): void {
    const top = this.past[this.past.length - 1];
    if (!top) return this.push(g);
    const k = (c: Cell) => keyOf(c[0], c[1], c[2]);
    const fwd = new Map(top.forward.map((c) => [k(c), c] as const));
    for (const c of g.forward) fwd.set(k(c), c);
    const back = new Map(top.backward.map((c) => [k(c), c] as const));
    for (const c of g.backward) if (!back.has(k(c))) back.set(k(c), c);
    top.forward = [...fwd.values()];
    top.backward = [...back.values()];
    this.future = [];
  }

  get canUndo(): boolean {
    return this.past.length > 0;
  }
  get canRedo(): boolean {
    return this.future.length > 0;
  }
  peekUndo(): OpGroup | null {
    return this.past[this.past.length - 1] ?? null;
  }
  peekRedo(): OpGroup | null {
    return this.future[this.future.length - 1] ?? null;
  }

  undo(): OpGroup | null {
    const g = this.past.pop();
    if (!g) return null;
    this.future.push(g);
    return g;
  }

  redo(): OpGroup | null {
    const g = this.future.pop();
    if (!g) return null;
    this.past.push(g);
    return g;
  }

  clear(): void {
    this.past = [];
    this.future = [];
  }
}

// ---------------------------------------------------------------------------------------------------------- selection

/** A box selection, optionally narrowed to some cells (select connected, invert). */
export interface Selection {
  min: V3;
  max: V3;
  /** Cell keys; absent = the whole box (air included). */
  mask?: Set<number> | null;
}

export function boxOf(a: V3, b: V3): { min: V3; max: V3 } {
  return {
    min: [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.min(a[2], b[2])],
    max: [Math.max(a[0], b[0]), Math.max(a[1], b[1]), Math.max(a[2], b[2])],
  };
}

export const clampV = (p: V3): V3 => [
  Math.max(0, Math.min(SIZE - 1, p[0])),
  Math.max(plot.minY, Math.min(plot.maxY, p[1])),
  Math.max(0, Math.min(SIZE - 1, p[2])),
];

export function sizeOf(s: { min: V3; max: V3 }): V3 {
  return [s.max[0] - s.min[0] + 1, s.max[1] - s.min[1] + 1, s.max[2] - s.min[2] + 1];
}

export function inSel(s: Selection, x: number, y: number, z: number): boolean {
  if (x < s.min[0] || x > s.max[0] || y < s.min[1] || y > s.max[1] || z < s.min[2] || z > s.max[2]) return false;
  return !s.mask || s.mask.has(keyOf(x, y, z));
}

/** The canvas blocks inside a selection; with `holes`, its dug cells too (as air cells: they move and copy with it). */
export function selected(c: Canvas, s: Selection, holes = false): Cell[] {
  const out: Cell[] = [];
  const at = (k: number) => {
    const v = c.blocks.get(k);
    if (v !== undefined) {
      const [x, y, z] = posOf(k);
      out.push([x, y, z, v >> 4, v & 15]);
    } else if (holes && c.dug.has(k)) {
      const [x, y, z] = posOf(k);
      out.push([x, y, z, 0, 0]);
    }
  };
  if (s.mask) {
    for (const k of s.mask) at(k);
    return out;
  }
  for (let y = s.min[1]; y <= s.max[1]; y++)
    for (let z = s.min[2]; z <= s.max[2]; z++) for (let x = s.min[0]; x <= s.max[0]; x++) at(keyOf(x, y, z));
  return out;
}

/** Union of a selection and a box (Shift extends). */
export function extend(s: Selection | null, a: V3, b: V3): Selection {
  const box = boxOf(a, b);
  if (!s) return box;
  return { min: boxOf(s.min, box.min).min, max: boxOf(s.max, box.max).max };
}

/** Every block of the canvas (select all). */
export function selectAll(c: Canvas): Selection | null {
  const b = c.bounds();
  return b ? { ...b } : null;
}

/** The blocks NOT in the selection (invert), as a masked selection over the canvas box. */
export function invert(c: Canvas, s: Selection | null): Selection | null {
  const b = c.bounds();
  if (!b) return null;
  const mask = new Set<number>();
  for (const k of c.blocks.keys()) {
    const [x, y, z] = posOf(k);
    if (!s || !inSel(s, x, y, z)) mask.add(k);
  }
  if (mask.size === 0) return null;
  return maskSelection(mask);
}

export function maskSelection(mask: Set<number>): Selection {
  const min: V3 = [SIZE, plot.maxY + 1, SIZE];
  const max: V3 = [-1, plot.minY - 1, -1];
  for (const k of mask) {
    const p = posOf(k);
    for (let i = 0; i < 3; i++) {
      if (p[i]! < min[i]!) min[i] = p[i]!;
      if (p[i]! > max[i]!) max[i] = p[i]!;
    }
  }
  return { min, max, mask };
}

/** Flood from a block over its 6-neighbours: the same block type (`same`), or any block (the whole structure). */
export function connected(c: Canvas, start: V3, same: boolean, limit = 50_000): Set<number> {
  const out = new Set<number>();
  const startV = c.blocks.get(keyOf(...start));
  if (startV === undefined) return out;
  const want = same ? startV >> 4 : null;
  const stack = [keyOf(...start)];
  out.add(stack[0]!);
  while (stack.length && out.size < limit) {
    const [x, y, z] = posOf(stack.pop()!);
    for (const [dx, dy, dz] of N6) {
      const k = joinedKey(c, x + dx, y + dy, z + dz, want);
      if (k < 0 || out.has(k)) continue;
      out.add(k);
      stack.push(k);
    }
  }
  return out;
}

/** The key of a block at (x, y, z) inside the plot (of type `want` when given), or -1. */
function joinedKey(c: Canvas, x: number, y: number, z: number, want: number | null): number {
  if (!inside(x, y, z)) return -1;
  const k = keyOf(x, y, z);
  const v = c.blocks.get(k);
  return v === undefined || (want !== null && v >> 4 !== want) ? -1 : k;
}

export const N6: ReadonlyArray<V3> = [
  [1, 0, 0],
  [-1, 0, 0],
  [0, 1, 0],
  [0, -1, 0],
  [0, 0, 1],
  [0, 0, -1],
];
