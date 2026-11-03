/**
 * The Design Studio canvas in the browser: the same sandbox the bot holds (contracts designer.ts), local coordinates x, z
 * 0..63 and y 0..47, cells `[x, y, z, id, meta]` (id 0 = air). Every change, the AI's and the user's, goes through `apply`,
 * which returns what it really changed and the inverse, so one history covers both (undo of an AI op is a user edit the bot
 * then hears about). Door halves stay paired.
 */
import { DESIGNER_PLOT, type DesignerCell } from '@mineclaw/contracts';
import { doorUpper, isDoor } from './facing';

export type Cell = DesignerCell;
export type V3 = [number, number, number];

export const SIZE = DESIGNER_PLOT.size;
export const HEIGHT = DESIGNER_PLOT.height;

export const inside = (x: number, y: number, z: number): boolean => x >= 0 && x < SIZE && z >= 0 && z < SIZE && y >= 0 && y < HEIGHT;
export const keyOf = (x: number, y: number, z: number): number => x + SIZE * (z + SIZE * y);
export const posOf = (k: number): V3 => [k % SIZE, Math.floor(k / (SIZE * SIZE)), Math.floor(k / SIZE) % SIZE];

/** An applied change set: the cells as they became (`forward`) and as they were (`backward`). */
export interface Change {
  forward: Cell[];
  backward: Cell[];
}

export class Canvas {
  /** key → id * 16 + meta. */
  readonly blocks = new Map<number, number>();

  get size(): number {
    return this.blocks.size;
  }

  get(x: number, y: number, z: number): [number, number] {
    const v = this.blocks.get(keyOf(x, y, z));
    return v === undefined ? [0, 0] : [v >> 4, v & 15];
  }

  id(x: number, y: number, z: number): number {
    const v = this.blocks.get(keyOf(x, y, z));
    return v === undefined ? 0 : v >> 4;
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

  load(cells: readonly Cell[]): void {
    this.blocks.clear();
    for (const c of cells) if (inside(c[0], c[1], c[2]) && c[3] !== 0) this.blocks.set(keyOf(c[0], c[1], c[2]), c[3] * 16 + (c[4] & 15));
  }

  /**
   * Applies cells in order (later wins). Out-of-plot cells are skipped. With `pairDoors`, a placed lower door half brings its
   * upper half and replacing either half of a door clears the other (what the bot's canvas does too).
   */
  apply(cells: readonly Cell[], pairDoors = true): Change {
    const before = new Map<number, number>();
    const touched: number[] = [];
    const set = (x: number, y: number, z: number, id: number, meta: number) => {
      if (!inside(x, y, z)) return;
      const k = keyOf(x, y, z);
      const old = this.blocks.get(k) ?? 0;
      const next = id === 0 ? 0 : id * 16 + (meta & 15);
      if (old === next) return;
      if (!before.has(k)) {
        before.set(k, old);
        touched.push(k);
      }
      if (pairDoors && old !== 0 && isDoor(old >> 4) && id !== old >> 4) {
        const oy = (old & 8) !== 0 ? y - 1 : y + 1;
        const ok = inside(x, oy, z) ? keyOf(x, oy, z) : -1;
        const other = ok >= 0 ? this.blocks.get(ok) : undefined;
        if (other !== undefined && other >> 4 === old >> 4) {
          if (!before.has(ok)) {
            before.set(ok, other);
            touched.push(ok);
          }
          this.blocks.delete(ok);
        }
      }
      if (next === 0) this.blocks.delete(k);
      else this.blocks.set(k, next);
    };
    for (const c of cells) {
      set(c[0], c[1], c[2], c[3], c[4]);
      if (pairDoors && c[3] !== 0 && isDoor(c[3]) && (c[4] & 8) === 0) set(c[0], c[1] + 1, c[2], c[3], doorUpper(c[4]));
    }
    const forward: Cell[] = [];
    const backward: Cell[] = [];
    for (const k of touched) {
      const [x, y, z] = posOf(k);
      const now = this.blocks.get(k) ?? 0;
      const was = before.get(k)!;
      if (now === was) continue;
      forward.push([x, y, z, now >> 4, now & 15]);
      backward.push([x, y, z, was >> 4, was & 15]);
    }
    return { forward, backward };
  }

  /** The blocks' box, or null when empty. */
  bounds(): { min: V3; max: V3 } | null {
    if (this.blocks.size === 0) return null;
    const min: V3 = [SIZE, HEIGHT, SIZE];
    const max: V3 = [-1, -1, -1];
    for (const k of this.blocks.keys()) {
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
}

/** Undo/redo over AI and user ops alike. `undo()` returns the cells to apply (the group's inverse). */
export class History {
  private past: OpGroup[] = [];
  private future: OpGroup[] = [];
  constructor(readonly limit = 200) {}

  push(g: OpGroup): void {
    if (g.forward.length === 0) return;
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
  Math.max(0, Math.min(HEIGHT - 1, p[1])),
  Math.max(0, Math.min(SIZE - 1, p[2])),
];

export function sizeOf(s: { min: V3; max: V3 }): V3 {
  return [s.max[0] - s.min[0] + 1, s.max[1] - s.min[1] + 1, s.max[2] - s.min[2] + 1];
}

export function inSel(s: Selection, x: number, y: number, z: number): boolean {
  if (x < s.min[0] || x > s.max[0] || y < s.min[1] || y > s.max[1] || z < s.min[2] || z > s.max[2]) return false;
  return !s.mask || s.mask.has(keyOf(x, y, z));
}

/** The canvas blocks inside a selection. */
export function selected(c: Canvas, s: Selection): Cell[] {
  const out: Cell[] = [];
  if (s.mask) {
    for (const k of s.mask) {
      const v = c.blocks.get(k);
      if (v === undefined) continue;
      const [x, y, z] = posOf(k);
      out.push([x, y, z, v >> 4, v & 15]);
    }
    return out;
  }
  for (let y = s.min[1]; y <= s.max[1]; y++)
    for (let z = s.min[2]; z <= s.max[2]; z++)
      for (let x = s.min[0]; x <= s.max[0]; x++) {
        const v = c.blocks.get(keyOf(x, y, z));
        if (v !== undefined) out.push([x, y, z, v >> 4, v & 15]);
      }
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
  const min: V3 = [SIZE, HEIGHT, SIZE];
  const max: V3 = [-1, -1, -1];
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
  const want = startV >> 4;
  const stack = [keyOf(...start)];
  out.add(stack[0]!);
  while (stack.length && out.size < limit) {
    const [x, y, z] = posOf(stack.pop()!);
    for (const [dx, dy, dz] of N6) {
      const nx = x + dx, ny = y + dy, nz = z + dz;
      if (!inside(nx, ny, nz)) continue;
      const k = keyOf(nx, ny, nz);
      if (out.has(k)) continue;
      const v = c.blocks.get(k);
      if (v === undefined || (same && v >> 4 !== want)) continue;
      out.add(k);
      stack.push(k);
    }
  }
  return out;
}

export const N6: ReadonlyArray<V3> = [
  [1, 0, 0],
  [-1, 0, 0],
  [0, 1, 0],
  [0, -1, 0],
  [0, 0, 1],
  [0, 0, -1],
];
