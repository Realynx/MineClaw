/**
 * Geometry for the server-laid-out RAPTOR graph (unit space, see `GraphNode` in the contracts): view transforms, hit
 * testing and graph navigation. Pure functions so they can be tested without a canvas.
 */
import type { GraphLayout, GraphNode, RaptorGraph } from '@mineclaw/contracts';

export interface View {
  /** zoom factor */
  k: number;
  tx: number;
  ty: number;
}

export interface Frame {
  layout: GraphLayout;
  /** where the unit square maps before zoom/pan */
  bx: number;
  by: number;
  bw: number;
  bh: number;
}

/** Icicle stretches to the canvas; radial and node-link keep a square aspect, centred. */
export function baseFrame(layout: GraphLayout, w: number, h: number, pad = 12): Frame {
  if (layout === 'icicle') return { layout, bx: pad, by: pad, bw: w - pad * 2, bh: h - pad * 2 };
  const s = Math.min(w, h) - pad * 2;
  return { layout, bx: (w - s) / 2, by: (h - s) / 2, bw: s, bh: s };
}

export function toScreen(f: Frame, v: View, ux: number, uy: number): [number, number] {
  return [(f.bx + ux * f.bw) * v.k + v.tx, (f.by + uy * f.bh) * v.k + v.ty];
}

export function toUnit(f: Frame, v: View, sx: number, sy: number): [number, number] {
  return [((sx - v.tx) / v.k - f.bx) / f.bw, ((sy - v.ty) / v.k - f.by) / f.bh];
}

/** Zoom by `factor` keeping the screen point (px, py) fixed. */
export function zoomAt(v: View, factor: number, px: number, py: number, min = 0.5, max = 60): View {
  const k = Math.max(min, Math.min(max, v.k * factor));
  const r = k / v.k;
  return { k, tx: px - (px - v.tx) * r, ty: py - (py - v.ty) * r };
}

const TAU = Math.PI * 2;

/** Whether a unit-space point is inside a node's shape. */
export function contains(n: GraphNode, layout: GraphLayout, ux: number, uy: number, unitPerPx = 0): boolean {
  return (CONTAINS[layout] ?? inDot)(n, ux, uy, unitPerPx);
}

type Contains = (n: GraphNode, ux: number, uy: number, unitPerPx: number) => boolean;

/** Icicle: inside the node's rectangle. */
const inRect: Contains = (n, ux, uy) => Math.abs(ux - n.x) <= (n.w ?? 0) / 2 && Math.abs(uy - n.y) <= (n.h ?? 0) / 2;

/** Radial: inside the node's ring sector (angle 0 = up, clockwise). */
const inSector: Contains = (n, ux, uy) => {
  const dx = ux - 0.5;
  const dy = uy - 0.5;
  const r = Math.hypot(dx, dy);
  if (r < (n.r0 ?? 0) || r > (n.r1 ?? 0)) return false;
  let a = Math.atan2(dx, -dy);
  if (a < 0) a += TAU;
  return a >= (n.a0 ?? 0) && a <= (n.a1 ?? 0);
};

/** Node-link: within the dot's radius (at least 4 px). */
const inDot: Contains = (n, ux, uy, unitPerPx) => Math.hypot(ux - n.x, uy - n.y) <= Math.max(n.r ?? 0.005, unitPerPx * 4);

const CONTAINS: Partial<Record<GraphLayout, Contains>> = { icicle: inRect, radial: inSector };

/** The topmost node under a unit point (leaves are drawn last, so search from the end). */
export function hitTest(nodes: readonly GraphNode[], layout: GraphLayout, ux: number, uy: number, unitPerPx = 0): GraphNode | null {
  for (let i = nodes.length - 1; i >= 0; i--) {
    const n = nodes[i]!;
    if (contains(n, layout, ux, uy, unitPerPx)) return n;
  }
  return null;
}

export interface GraphIndex {
  byId: Map<number, GraphNode>;
  children: Map<number, number[]>;
  /** all parents (primary first), from edges */
  parents: Map<number, number[]>;
  roots: number[];
}

export function indexGraph(g: Pick<RaptorGraph, 'nodes' | 'edges'>): GraphIndex {
  const byId = new Map(g.nodes.map((n) => [n.id, n]));
  const children = new Map<number, number[]>();
  const parents = new Map<number, number[]>();
  for (const n of g.nodes) {
    if (n.parent !== null && n.parent !== undefined) {
      children.set(n.parent, [...(children.get(n.parent) ?? []), n.id]);
      parents.set(n.id, [n.parent]);
    }
  }
  for (const e of g.edges) {
    if (e.primary) continue;
    const list = parents.get(e.target) ?? [];
    if (!list.includes(e.source)) parents.set(e.target, [...list, e.source]);
  }
  for (const [p, kids] of children) children.set(p, kids.sort((a, b) => (byId.get(a)?.x ?? 0) - (byId.get(b)?.x ?? 0)));
  const roots = g.nodes.filter((n) => n.parent === null || n.parent === undefined).map((n) => n.id);
  return { byId, children, parents, roots };
}

/** Keyboard walk: up = parent, down = first child, left/right = previous/next sibling. */
export function walk(idx: GraphIndex, from: number | null, dir: 'up' | 'down' | 'left' | 'right'): number | null {
  const n = from === null ? undefined : idx.byId.get(from);
  if (from === null || !n) return idx.roots[0] ?? null;
  if (dir === 'up') return n.parent ?? from;
  if (dir === 'down') return idx.children.get(from)?.[0] ?? from;
  return sibling(idx, n, from, dir === 'left' ? -1 : 1);
}

/** The previous (-1) or next (+1) sibling (among the roots for a root), or the node itself at the end. */
function sibling(idx: GraphIndex, n: GraphNode, from: number, step: number): number {
  const siblings = n.parent !== null && n.parent !== undefined ? (idx.children.get(n.parent) ?? []) : idx.roots;
  return siblings[siblings.indexOf(from) + step] ?? from;
}

/** All ancestors of a node (every parent link), nearest first, without duplicates. */
export function ancestors(parents: ReadonlyMap<number, readonly number[]>, id: number): number[] {
  const out: number[] = [];
  const seen = new Set<number>([id]);
  const queue = [...(parents.get(id) ?? [])];
  while (queue.length) {
    const p = queue.shift()!;
    if (seen.has(p)) continue;
    seen.add(p);
    out.push(p);
    queue.push(...(parents.get(p) ?? []));
  }
  return out;
}
