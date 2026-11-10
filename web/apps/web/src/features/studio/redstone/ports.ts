/**
 * Where a component sits on the canvas: its layout turned, mirrored and moved to a corner, the box it fills, its ports
 * (the cells just outside the box where wires join), the gap that keeps two components from talking to each other, and
 * the snap that joins one component's input to another's output. Plain geometry: no canvas, no studio.
 *
 * A component owns every cell of its box, the empty ones as air: a stray block inside a gate breaks it (layout rule L6).
 * Boxes keep one free cell between them, and only port cells hold dust there, so two components side by side never
 * cross-talk. A snapped input port is the very cell of the output port it joins, which lies in that gap.
 */
import {
  transformLayout,
  type ComponentLayout,
  type Pos,
  type Port,
  type RedstoneVersion,
  type Side,
} from '@mineclaw/redstone';
import type { Cell } from '../canvas';

export type Mirror = 'x' | 'z' | null;

/** A box by its corners, both inside it. */
export interface Box {
  min: Pos;
  max: Pos;
}

/** A layout placed on the canvas: absolute cells and ports. */
export interface Placed {
  box: Box;
  /** Every cell of the box in y, z, x order: the layout's blocks, the rest air. */
  cells: Cell[];
  ports: Port[];
}

/** Where and how a component lies: its box's min corner, then mirrored, then turned clockwise. */
export interface Placement {
  at: Pos;
  turns: number;
  mirror: Mirror;
}

const SIDE_STEP: Readonly<Record<Side, readonly [number, number]>> = {
  north: [0, -1],
  east: [1, 0],
  south: [0, 1],
  west: [-1, 0],
};

const OPPOSITE: Readonly<Record<Side, Side>> = { north: 'south', south: 'north', east: 'west', west: 'east' };

export const opposite = (s: Side): Side => OPPOSITE[s];
export const quarter = (turns: number): number => ((Math.trunc(turns) % 4) + 4) % 4;
const add = (a: Pos, b: Pos): Pos => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

/** The layout turned and mirrored as `p` says, its cells and ports moved to `p.at`. */
export function placeLayout(layout: ComponentLayout, p: Placement, v: RedstoneVersion): Placed {
  const t = transformLayout(layout, quarter(p.turns), p.mirror, v);
  const [sx, sy, sz] = t.size;
  const [ax, ay, az] = p.at;
  const blocks = new Map<string, (typeof t.cells)[number]>();
  for (const c of t.cells) if (c[3] > 0) blocks.set(`${c[0]},${c[1]},${c[2]}`, c);
  const cells: Cell[] = [];
  for (let y = 0; y < sy; y++)
    for (let z = 0; z < sz; z++)
      for (let x = 0; x < sx; x++) {
        const c = blocks.get(`${x},${y},${z}`);
        cells.push([ax + x, ay + y, az + z, c ? c[3] : 0, c ? c[4] : 0]);
      }
  return {
    box: { min: [ax, ay, az], max: [ax + sx - 1, ay + sy - 1, az + sz - 1] },
    cells,
    ports: t.ports.map((port) => ({ ...port, at: add(port.at, p.at) })),
  };
}

/** Whether `p` lies in the box. */
export const inBox = (b: Box, p: Pos): boolean =>
  p[0] >= b.min[0] && p[0] <= b.max[0] && p[1] >= b.min[1] && p[1] <= b.max[1] && p[2] >= b.min[2] && p[2] <= b.max[2];

/** Two boxes closer than one free cell: they touch, overlap, or meet at an edge or corner. */
export function tooClose(a: Box, b: Box): boolean {
  for (let i = 0; i < 3; i++) if (a.min[i]! - 1 > b.max[i]! || b.min[i]! > a.max[i]! + 1) return false;
  return true;
}

/**
 * The face a cell just outside the box lies on, at a y inside it and across from the box (never at a corner); null
 * for any other cell. Ports are such cells.
 */
export function portSideOf(b: Box, at: Pos): Side | null {
  const [x, y, z] = at;
  if (y < b.min[1] || y > b.max[1]) return null;
  const inX = x >= b.min[0] && x <= b.max[0];
  const inZ = z >= b.min[2] && z <= b.max[2];
  if (inZ && x === b.min[0] - 1) return 'west';
  if (inZ && x === b.max[0] + 1) return 'east';
  if (inX && z === b.min[2] - 1) return 'north';
  if (inX && z === b.max[2] + 1) return 'south';
  return null;
}

/** An output port of a placed component, as a snap target. */
export interface Outlet {
  at: Pos;
  side: Side;
}

/** How far from the aimed cell a snap looks for an output port. */
export const SNAP_REACH = 3;

/**
 * The placement that puts one of the layout's input ports on an output port near `target`, the two facing each other
 * across the gap; the nearest such pair wins. `at` is where the box goes, `joint` the shared port cell (it takes one
 * dust). Null when no output port is in reach.
 */
export function snapPlacement(
  layout: ComponentLayout,
  turns: number,
  mirror: Mirror,
  v: RedstoneVersion,
  target: Pos,
  outlets: readonly Outlet[],
  reach = SNAP_REACH,
): { at: Pos; joint: Pos } | null {
  const t = transformLayout(layout, quarter(turns), mirror, v);
  let best: { at: Pos; joint: Pos; d: number } | null = null;
  for (const o of outlets) {
    const d = Math.max(Math.abs(o.at[0] - target[0]), Math.abs(o.at[1] - target[1]), Math.abs(o.at[2] - target[2]));
    if (d > reach || (best && d >= best.d)) continue;
    const input = t.ports.find((p) => p.kind === 'in' && p.side === opposite(o.side));
    if (!input) continue;
    best = { at: [o.at[0] - input.at[0], o.at[1] - input.at[1], o.at[2] - input.at[2]], joint: o.at, d };
  }
  return best ? { at: best.at, joint: best.joint } : null;
}

/**
 * What a moved component's port can land on: another component's port (`in` or `out`, with its side), or the end of a
 * dust line (`dust`, with the way the line runs on from it, or null for a lone dust).
 */
export interface SnapTarget {
  at: Pos;
  kind: 'in' | 'out' | 'dust';
  side: Side | null;
}

/** A corner that puts one port on a target: `joint` is the shared cell, `d` how far the port had to go. */
export interface CornerSnap {
  at: Pos;
  joint: Pos;
  d: number;
  to: 'port' | 'dust';
}

/** How far a dragged component's port reaches for a target: a little less than placing, so a drag does not jump. */
export const MOVE_SNAP_REACH = 2;

/**
 * Every corner within reach of `corner` that lays one of `ports` (relative to the corner, already turned) on a target,
 * nearest first and a port before a dust end at the same distance. An input takes an output and an output an input,
 * facing each other; a dust end takes any port whose side points along the line, so the wire runs straight on.
 */
export function snapCorner(
  ports: readonly Port[],
  corner: Pos,
  targets: readonly SnapTarget[],
  reach = MOVE_SNAP_REACH,
): CornerSnap[] {
  const out: CornerSnap[] = [];
  for (const t of targets)
    for (const p of ports) {
      if (t.kind === 'dust') {
        if (t.side !== null && p.side !== t.side) continue;
      } else if (p.kind === t.kind || t.side === null || p.side !== opposite(t.side)) continue;
      const at = add(p.at, corner);
      const d = Math.max(Math.abs(at[0] - t.at[0]), Math.abs(at[1] - t.at[1]), Math.abs(at[2] - t.at[2]));
      if (d > reach) continue;
      out.push({
        at: [t.at[0] - p.at[0], t.at[1] - p.at[1], t.at[2] - p.at[2]],
        joint: t.at,
        d,
        to: t.kind === 'dust' ? 'dust' : 'port',
      });
    }
  return out.sort((a, b) => a.d - b.d || (a.to === b.to ? 0 : a.to === 'port' ? -1 : 1));
}

/** The four cells beside one on its level, with the side each lies on. */
export const besides = (p: Pos): Array<{ at: Pos; side: Side }> =>
  (Object.keys(SIDE_STEP) as Side[]).map((side) => ({
    at: [p[0] + SIDE_STEP[side][0], p[1], p[2] + SIDE_STEP[side][1]],
    side,
  }));

/**
 * How a part lies once the component around it is mirrored and turned: `outer` applied after `inner`. A mirror reverses
 * the way a later turn goes, and two mirrors across different axes make a half turn.
 */
export function composePlacement(
  outer: { turns: number; mirror: Mirror },
  inner: { turns: number; mirror: Mirror },
): { turns: number; mirror: Mirror } {
  if (outer.mirror === null) return { turns: quarter(outer.turns + inner.turns), mirror: inner.mirror };
  if (inner.mirror === null) return { turns: quarter(outer.turns - inner.turns), mirror: outer.mirror };
  if (inner.mirror === outer.mirror) return { turns: quarter(outer.turns - inner.turns), mirror: null };
  return { turns: quarter(outer.turns - inner.turns + 2), mirror: null };
}

/**
 * Where a box inside a layout of `size` has its min corner once the layout is mirrored and turned. The box's corners go
 * through the package's own transform (as ports, which it moves like cells), so the two can never disagree.
 */
export function boxCornerAfter(
  size: readonly [number, number, number],
  box: Box,
  turns: number,
  mirror: Mirror,
  v: RedstoneVersion,
): Pos {
  const corner = (name: string, at: Pos): Port => ({ name, kind: 'in', at, side: 'north' });
  const t = transformLayout(
    { size, cells: [], ports: [corner('min', box.min), corner('max', box.max)] },
    turns,
    mirror,
    v,
  );
  const [a, b] = [t.ports[0]!.at, t.ports[1]!.at];
  return [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.min(a[2], b[2])];
}

/** The cell one step outside a port, away from its box (where a feeder's next cell goes). */
export const beyond = (port: Pick<Port, 'at' | 'side'>): Pos => {
  const [dx, dz] = SIDE_STEP[port.side];
  return [port.at[0] + dx, port.at[1], port.at[2] + dz];
};

/** A port picked on the canvas: the cell and what it carries. */
export interface PickedPort {
  name: string;
  kind: 'in' | 'out';
  at: Pos;
}

/**
 * A layout from the blocks in a box (absolute cells; air is left out) and the ports picked around it, all made relative
 * to the box's min corner. Null when a port is not just outside the box on one of its four sides.
 */
export function layoutOf(box: Box, blocks: readonly Cell[], ports: readonly PickedPort[]): ComponentLayout | null {
  const [mx, my, mz] = box.min;
  const out: Port[] = [];
  for (const p of ports) {
    const side = portSideOf(box, p.at);
    if (!side) return null;
    out.push({ name: p.name, kind: p.kind, at: [p.at[0] - mx, p.at[1] - my, p.at[2] - mz], side });
  }
  const cells = blocks
    .filter((c) => c[3] > 0 && inBox(box, [c[0], c[1], c[2]]))
    .map((c): Cell => [c[0] - mx, c[1] - my, c[2] - mz, c[3], c[4]])
    .sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);
  return {
    size: [box.max[0] - mx + 1, box.max[1] - my + 1, box.max[2] - mz + 1],
    cells,
    ports: out,
  };
}
