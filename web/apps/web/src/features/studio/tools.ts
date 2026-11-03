/**
 * The Design Studio's manual tools as pure functions over the canvas: shapes (box fill / hollow / walls, lines), brushes
 * (sphere or cube: add, remove, paint, replace, smooth), selection edits (fill, hollow, walls, raise / lower the top surface,
 * replace material), face tools (flood a face area, extrude / push-pull it) and transforms (move, rotate 90° around Y,
 * mirror X/Z, copy / paste) with block facing turned per family (./facing). Each returns the cells to apply; the page applies
 * them through `Canvas.apply`, so everything lands in one history and one op stream to the bot.
 */
import {
  boxOf,
  type Canvas,
  type Cell,
  inSel,
  inside,
  keyOf,
  N6,
  posOf,
  selected,
  type Selection,
  type V3,
} from './canvas';
import { FACE_NORMAL, mirrorMeta, rotateMeta, type Version } from './facing';

export type BoxMode = 'fill' | 'hollow' | 'walls' | 'outline';

/** A box from a to b (inclusive): filled, a hollow shell (inside air), 4 walls (inside air, no floor or roof) or its edges. */
export function boxCells(a: V3, b: V3, id: number, meta: number, mode: BoxMode = 'fill'): Cell[] {
  const { min, max } = boxOf(a, b);
  const out: Cell[] = [];
  for (let y = min[1]; y <= max[1]; y++)
    for (let z = min[2]; z <= max[2]; z++)
      for (let x = min[0]; x <= max[0]; x++) {
        const ex = x === min[0] || x === max[0],
          ey = y === min[1] || y === max[1],
          ez = z === min[2] || z === max[2];
        const on =
          mode === 'fill'
            ? true
            : mode === 'hollow'
              ? ex || ey || ez
              : mode === 'walls'
                ? ex || ez
                : (ex ? 1 : 0) + (ey ? 1 : 0) + (ez ? 1 : 0) >= 2;
        if (on) out.push([x, y, z, id, meta]);
        else if (mode === 'hollow' || mode === 'walls') out.push([x, y, z, 0, 0]);
      }
  return out;
}

/** A straight line of blocks (3D DDA, both ends). */
export function lineCells(a: V3, b: V3, id: number, meta: number): Cell[] {
  const n = Math.max(Math.abs(b[0] - a[0]), Math.abs(b[1] - a[1]), Math.abs(b[2] - a[2]));
  const out: Cell[] = [];
  const seen = new Set<number>();
  for (let i = 0; i <= n; i++) {
    const t = n === 0 ? 0 : i / n;
    const p: V3 = [
      Math.round(a[0] + (b[0] - a[0]) * t),
      Math.round(a[1] + (b[1] - a[1]) * t),
      Math.round(a[2] + (b[2] - a[2]) * t),
    ];
    const k = keyOf(...p);
    if (seen.has(k)) continue;
    seen.add(k);
    out.push([p[0], p[1], p[2], id, meta]);
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------- brushes

export type BrushShape = 'sphere' | 'cube';
export type BrushMode = 'add' | 'remove' | 'paint' | 'replace' | 'smooth';

/** The cells a brush of size 1..8 covers around a centre (size 1 = the single block). */
export function brushCells(centre: V3, size: number, shape: BrushShape): V3[] {
  const r = Math.max(0, Math.min(8, Math.round(size)) - 1);
  const out: V3[] = [];
  for (let dy = -r; dy <= r; dy++)
    for (let dz = -r; dz <= r; dz++)
      for (let dx = -r; dx <= r; dx++) {
        if (shape === 'sphere' && dx * dx + dy * dy + dz * dz > r * r + r) continue;
        const p: V3 = [centre[0] + dx, centre[1] + dy, centre[2] + dz];
        if (inside(...p)) out.push(p);
      }
  return out;
}

/**
 * One brush stroke: add (fills air), remove (to air), paint (re-blocks what is there), replace (only `target` blocks),
 * smooth (each cell follows its 26 neighbours: 14+ filled fills it with their commonest block, under 10 empties it).
 */
export function brush(
  c: Canvas,
  centre: V3,
  size: number,
  shape: BrushShape,
  mode: BrushMode,
  id: number,
  meta: number,
  target?: number,
): Cell[] {
  const cells = brushCells(centre, size, shape);
  const out: Cell[] = [];
  for (const [x, y, z] of cells) {
    const cur = c.id(x, y, z);
    switch (mode) {
      case 'add':
        if (cur === 0) out.push([x, y, z, id, meta]);
        break;
      case 'remove':
        if (cur !== 0) out.push([x, y, z, 0, 0]);
        break;
      case 'paint':
        if (cur !== 0) out.push([x, y, z, id, meta]);
        break;
      case 'replace':
        if (cur !== 0 && cur === target) out.push([x, y, z, id, meta]);
        break;
      case 'smooth': {
        let filled = 0;
        const votes = new Map<number, number>();
        for (let dy = -1; dy <= 1; dy++)
          for (let dz = -1; dz <= 1; dz++)
            for (let dx = -1; dx <= 1; dx++) {
              if ((dx === 0 && dy === 0 && dz === 0) || !inside(x + dx, y + dy, z + dz)) continue;
              const v = c.blocks.get(keyOf(x + dx, y + dy, z + dz));
              if (v === undefined) continue;
              filled++;
              votes.set(v, (votes.get(v) ?? 0) + 1);
            }
        if (filled >= 14 && cur === 0 && votes.size) {
          const best = [...votes.entries()].sort((a, b) => b[1] - a[1])[0]![0];
          out.push([x, y, z, best >> 4, best & 15]);
        } else if (filled < 10 && cur !== 0) out.push([x, y, z, 0, 0]);
        break;
      }
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------- selection edits

/** Fill the selection's box, hollow it (keep the shell), or build its 4 walls. */
export function selectionShape(s: Selection, id: number, meta: number, mode: 'fill' | 'hollow' | 'walls'): Cell[] {
  if (mode === 'fill') return boxCells(s.min, s.max, id, meta, 'fill').filter((c) => inSel(s, c[0], c[1], c[2]));
  if (mode === 'walls') return boxCells(s.min, s.max, id, meta, 'walls').filter((c) => c[3] !== 0);
  // hollow: the inside of the box becomes air, the shell stays as it is
  const out: Cell[] = [];
  for (let y = s.min[1] + 1; y < s.max[1]; y++)
    for (let z = s.min[2] + 1; z < s.max[2]; z++)
      for (let x = s.min[0] + 1; x < s.max[0]; x++) out.push([x, y, z, 0, 0]);
  return out;
}

/** Raise (one block on top of every column's highest block, the same block) or lower (remove it) inside the selection. */
export function raiseLower(c: Canvas, s: Selection, up: boolean): Cell[] {
  const out: Cell[] = [];
  for (let z = s.min[2]; z <= s.max[2]; z++)
    for (let x = s.min[0]; x <= s.max[0]; x++) {
      for (let y = s.max[1]; y >= s.min[1]; y--) {
        const [id, meta] = c.get(x, y, z);
        if (id === 0 || !inSel(s, x, y, z)) continue;
        if (up) {
          if (inside(x, y + 1, z) && c.id(x, y + 1, z) === 0) out.push([x, y + 1, z, id, meta]);
        } else out.push([x, y, z, 0, 0]);
        break;
      }
    }
  return out;
}

/** Replace a material in the selection: `from` = a block id (any meta) or 'any'. Facing kept when both are facing blocks. */
export function replaceIn(c: Canvas, s: Selection, from: number | 'any', id: number, meta: number): Cell[] {
  return selected(c, s)
    .filter((b) => from === 'any' || b[3] === from)
    .map((b): Cell => [b[0], b[1], b[2], id, meta]);
}

// ---------------------------------------------------------------------------------------------------------- face tools

/**
 * The face area under the cursor: the blocks connected to the hit one in the face's plane whose same face is exposed (air in
 * front), like a creative editor's face select. `same` limits it to the hit block's type.
 */
export function faceArea(c: Canvas, hit: V3, face: number, same = true, limit = 4096): V3[] {
  const n = FACE_NORMAL[face];
  if (!n || c.id(...hit) === 0) return [];
  const want = c.id(...hit);
  const axis = n[0] !== 0 ? 0 : n[1] !== 0 ? 1 : 2;
  const exposed = (p: V3) => {
    const f: V3 = [p[0] + n[0], p[1] + n[1], p[2] + n[2]];
    return !inside(...f) || c.id(...f) === 0;
  };
  const out: V3[] = [];
  const seen = new Set<number>([keyOf(...hit)]);
  const stack: V3[] = [hit];
  while (stack.length && out.length < limit) {
    const p = stack.pop()!;
    out.push(p);
    for (const d of N6) {
      if (d[axis] !== 0) continue;
      const q: V3 = [p[0] + d[0], p[1] + d[1], p[2] + d[2]];
      if (!inside(...q)) continue;
      const k = keyOf(...q);
      if (seen.has(k)) continue;
      seen.add(k);
      const id = c.id(...q);
      if (id === 0 || (same && id !== want) || !exposed(q)) continue;
      stack.push(q);
    }
  }
  return out;
}

/** Flood fill of a face area: those blocks become (id, meta). */
export function floodFace(c: Canvas, hit: V3, face: number, id: number, meta: number): Cell[] {
  return faceArea(c, hit, face).map((p): Cell => [p[0], p[1], p[2], id, meta]);
}

/** Push (n > 0: copies of the face blocks stacked outward) or pull (n < 0: the face's blocks removed n layers inward). */
export function extrude(c: Canvas, hit: V3, face: number, n: number): Cell[] {
  const area = faceArea(c, hit, face);
  const d = FACE_NORMAL[face]!;
  const out: Cell[] = [];
  for (const p of area) {
    const [id, meta] = c.get(...p);
    if (n > 0) for (let i = 1; i <= n; i++) out.push([p[0] + d[0] * i, p[1] + d[1] * i, p[2] + d[2] * i, id, meta]);
    else for (let i = 0; i < -n; i++) out.push([p[0] - d[0] * i, p[1] - d[1] * i, p[2] - d[2] * i, 0, 0]);
  }
  return out.filter((x) => inside(x[0], x[1], x[2]));
}

// ---------------------------------------------------------------------------------------------------------- transforms

export interface Transformed {
  /** Cells to apply: the old places cleared, then the blocks at their new places. */
  cells: Cell[];
  /** The selection afterwards (null when nothing was selected). */
  selection: Selection | null;
}

function relocate(blocks: Cell[], s: Selection, map: (b: Cell) => Cell): Transformed {
  if (blocks.length === 0) return { cells: [], selection: s };
  const moved = blocks.map(map);
  const cells: Cell[] = blocks.map((b): Cell => [b[0], b[1], b[2], 0, 0]);
  for (const m of moved) cells.push(m);
  const corners = [s.min, s.max, [s.min[0], s.min[1], s.max[2]] as V3, [s.max[0], s.min[1], s.min[2]] as V3].map(
    (p) => {
      const c = map([p[0], p[1], p[2], 0, 0]);
      return [c[0], c[1], c[2]] as V3;
    },
  );
  const min: V3 = [
    Math.min(...corners.map((p) => p[0])),
    Math.min(...corners.map((p) => p[1])),
    Math.min(...corners.map((p) => p[2])),
  ];
  const max: V3 = [
    Math.max(...corners.map((p) => p[0])),
    Math.max(...corners.map((p) => p[1])),
    Math.max(...corners.map((p) => p[2])),
  ];
  const mask = s.mask ? new Set(moved.map((b) => keyOf(b[0], b[1], b[2]))) : null;
  return { cells, selection: { min, max, mask } };
}

/** Move the selected blocks by d (the selection follows). */
export function move(c: Canvas, s: Selection, d: V3): Transformed {
  return relocate(selected(c, s), s, (b) => [b[0] + d[0], b[1] + d[1], b[2] + d[2], b[3], b[4]]);
}

/**
 * Rotate the selected blocks 90° clockwise (seen from above) `quarters` times around the selection's middle column (a fixed
 * cell, so four turns are exactly the identity and the design stays where it stands).
 */
export function rotate(c: Canvas, s: Selection, quarters: number, version: Version): Transformed {
  const q = ((quarters % 4) + 4) % 4;
  const px = Math.floor((s.min[0] + s.max[0]) / 2),
    pz = Math.floor((s.min[2] + s.max[2]) / 2);
  return relocate(selected(c, s), s, (b) => {
    let dx = b[0] - px,
      dz = b[2] - pz;
    for (let i = 0; i < q; i++) [dx, dz] = [-dz, dx];
    return [px + dx, b[1], pz + dz, b[3], rotateMeta(b[3], b[4], q, version)];
  });
}

/** Mirror the selected blocks across x (east ↔ west) or z (north ↔ south), in place. */
export function mirrorSel(c: Canvas, s: Selection, axis: 'x' | 'z'): Transformed {
  return relocate(selected(c, s), s, (b) =>
    axis === 'x'
      ? [s.min[0] + s.max[0] - b[0], b[1], b[2], b[3], mirrorMeta(b[3], b[4], 'x')]
      : [b[0], b[1], s.min[2] + s.max[2] - b[2], b[3], mirrorMeta(b[3], b[4], 'z')],
  );
}

/** Blocks copied relative to their box's min corner. */
export interface Clip {
  size: V3;
  cells: Cell[];
}

export function copy(c: Canvas, s: Selection): Clip | null {
  const blocks = selected(c, s);
  if (!blocks.length) return null;
  const min: V3 = [
    Math.min(...blocks.map((b) => b[0])),
    Math.min(...blocks.map((b) => b[1])),
    Math.min(...blocks.map((b) => b[2])),
  ];
  const max: V3 = [
    Math.max(...blocks.map((b) => b[0])),
    Math.max(...blocks.map((b) => b[1])),
    Math.max(...blocks.map((b) => b[2])),
  ];
  return {
    size: [max[0] - min[0] + 1, max[1] - min[1] + 1, max[2] - min[2] + 1],
    cells: blocks.map((b): Cell => [b[0] - min[0], b[1] - min[1], b[2] - min[2], b[3], b[4]]),
  };
}

/** The clip's cells placed with its min corner at `at` (the paste ghost and the paste itself). */
export function pasteCells(clip: Clip, at: V3): Cell[] {
  return clip.cells
    .map((b): Cell => [b[0] + at[0], b[1] + at[1], b[2] + at[2], b[3], b[4]])
    .filter((b) => inside(b[0], b[1], b[2]));
}

/** Delete the selected blocks. */
export function erase(c: Canvas, s: Selection): Cell[] {
  return selected(c, s).map((b): Cell => [b[0], b[1], b[2], 0, 0]);
}

/** A selection of explicit cells (for tests and select-connected). */
export function keysOf(cells: Iterable<V3>): Set<number> {
  const s = new Set<number>();
  for (const p of cells) s.add(keyOf(...p));
  return s;
}

export { posOf };
