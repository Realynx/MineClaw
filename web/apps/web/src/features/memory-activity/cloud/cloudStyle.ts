/**
 * Memory cloud styling and geometry (architecture.md, "Memory activity tab" → Cloud). Pure: the canvas and the tests call these.
 * - Colour = the bot's own RAPTOR cluster. Eight fixed categorical slots in cluster order (largest first, the bridge's order), one
 *   set stepped for each theme (both validated with the dataviz palette checks); a ninth cluster on folds into "other".
 * - Brightness = recency of use, the Memory activity heat model (`0.5^(age/halfLife)`) with a faint linear tail across the chosen
 *   window, so anything used in the window still reads apart from never-used memories once its heat has decayed.
 */
import { decayHeat, type MemoryCloudResponse } from '@mineclaw/contracts';
import type { LastAccess } from '../heat';

export const CLUSTER_SLOTS = 8;
export const CLUSTER_PALETTE = {
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'],
  dark: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'],
} as const;
export const OTHER_COLOR = { light: '#9aa1ad', dark: '#7d8590' } as const;

export type Theme = 'light' | 'dark';

/** Colour of cluster `c` (an index into `clusters`; −1 = not under any cluster). */
export function clusterColor(c: number, theme: Theme): string {
  return c >= 0 && c < CLUSTER_SLOTS ? CLUSTER_PALETTE[theme][c]! : OTHER_COLOR[theme];
}

export function hexRgb(hex: string): [number, number, number] {
  const h = hex.replace('#', '');
  return [parseInt(h.slice(0, 2), 16) / 255, parseInt(h.slice(2, 4), 16) / 255, parseInt(h.slice(4, 6), 16) / 255];
}

/** Share of the window the linear tail spans, and its peak. */
const TAIL = 0.32;

/** 0..1: bright = used just now, dim = stale; 0 = not used in the window (or never). */
export function cloudBrightness(la: LastAccess | undefined, t: number, halfLifeS: number, windowMs: number): number {
  if (!la || la.at > t) return 0;
  const age = t - la.at;
  if (age > windowMs) return 0;
  const heat = decayHeat(la.at, t, halfLifeS);
  return Math.max(heat, TAIL * (1 - age / Math.max(1, windowMs)));
}

export interface PointLook {
  alpha: number;
  /** CSS px diameter. */
  size: number;
  /** 0..1 halo strength (0 = no halo drawn). */
  halo: number;
}

/**
 * One point's look from its brightness at zoom `k` (1 = fitted). Unused memories stay visible (the shape of the memory is the
 * point of the view) but recede; points shrink when zoomed out so tens of thousands don't merge into a blob.
 */
export function pointLook(
  b: number,
  k: number,
  opts: { dimmed?: boolean; highlighted?: boolean; selected?: boolean } = {},
): PointLook {
  const base = Math.min(7, Math.max(2.2, 2.6 * Math.sqrt(Math.max(0.25, k))));
  let alpha = 0.3 + 0.7 * b;
  let size = base * (1 + 0.7 * b);
  let halo = b > 0.08 ? b : 0;
  if (opts.dimmed) {
    alpha *= 0.18;
    halo = 0;
  }
  if (opts.highlighted) {
    alpha = Math.max(alpha, 0.95);
    size = Math.max(size, base * 1.6);
  }
  if (opts.selected) {
    alpha = 1;
    size = Math.max(size, base * 2);
  }
  return { alpha, size, halo };
}

/** Case-insensitive match of every word of `q` in a title (or `#id`). */
export function matchesQuery(title: string, id: number, q: string): boolean {
  const s = q.trim().toLowerCase();
  if (!s) return false;
  if (/^#\d+$/.test(s)) return Number(s.slice(1)) === id;
  const t = title.toLowerCase();
  return s.split(/\s+/).every((w) => t.includes(w));
}

/** Ids of leaves whose cluster is hidden by the legend toggles. */
export function visibleMask(cloud: Pick<MemoryCloudResponse, 'points'>, hidden: ReadonlySet<number>): Uint8Array {
  const n = cloud.points.id.length;
  const out = new Uint8Array(n);
  for (let i = 0; i < n; i++) out[i] = hidden.has(legendKey(cloud.points.cluster[i]!)) ? 0 : 1;
  return out;
}

/** The legend entry a cluster folds into: its own slot, or −1 for "other" (the ninth cluster on, and leaves under none). */
export function legendKey(c: number): number {
  return c >= 0 && c < CLUSTER_SLOTS ? c : -1;
}

/** Toggle one legend entry; Alt/solo shows that one entry only (again = everything). */
export function toggleLegend(
  hidden: ReadonlySet<number>,
  key: number,
  all: readonly number[],
  solo = false,
): Set<number> {
  if (solo) {
    const onlyThis = all.every((k) => (k === key ? !hidden.has(k) : hidden.has(k)));
    return onlyThis ? new Set() : new Set(all.filter((k) => k !== key));
  }
  const out = new Set(hidden);
  if (out.has(key)) out.delete(key);
  else out.add(key);
  return out;
}

/** Legend rows: the coloured slots, then "other" when anything folds into it. */
export function legendEntries(cloud: Pick<MemoryCloudResponse, 'clusters' | 'points'>, theme: Theme) {
  const rows: Array<{ key: number; label: string; size: number; color: string; node: number | null }> = cloud.clusters
    .slice(0, CLUSTER_SLOTS)
    .map((c) => ({ key: c.id, label: c.label, size: c.size, color: clusterColor(c.id, theme), node: c.node }));
  const rest = cloud.points.cluster.filter((c) => legendKey(c) === -1).length;
  if (rest)
    rows.push({
      key: -1,
      label:
        cloud.clusters.length > CLUSTER_SLOTS
          ? `${cloud.clusters.length - CLUSTER_SLOTS} more clusters`
          : 'unclustered',
      size: rest,
      color: OTHER_COLOR[theme],
      node: null,
    });
  return rows;
}

// ---------- geometry ----------

/** A uniform grid over the unit square for hover / click / lasso. */
export interface CloudGrid {
  n: number;
  cells: Int32Array[];
}

export function buildGrid(xy: readonly number[], n = 64): CloudGrid {
  const buckets: number[][] = Array.from({ length: n * n }, () => []);
  for (let i = 0; i < xy.length / 2; i++) {
    const cx = Math.min(n - 1, Math.max(0, Math.floor(xy[2 * i]! * n)));
    const cy = Math.min(n - 1, Math.max(0, Math.floor(xy[2 * i + 1]! * n)));
    buckets[cy * n + cx]!.push(i);
  }
  return { n, cells: buckets.map((b) => Int32Array.from(b)) };
}

/** Nearest point index to (x, y) within `r` (unit-square distance), skipping masked-out points; −1 when none. */
export function nearest(
  grid: CloudGrid,
  xy: readonly number[],
  x: number,
  y: number,
  r: number,
  mask?: Uint8Array,
): number {
  const { n } = grid;
  const x0 = Math.max(0, Math.floor((x - r) * n)),
    x1 = Math.min(n - 1, Math.floor((x + r) * n));
  const y0 = Math.max(0, Math.floor((y - r) * n)),
    y1 = Math.min(n - 1, Math.floor((y + r) * n));
  let best = -1;
  let bestD = r * r;
  for (let cy = y0; cy <= y1; cy++)
    for (let cx = x0; cx <= x1; cx++)
      for (const i of grid.cells[cy * n + cx]!) {
        if (mask && !mask[i]) continue;
        const dx = xy[2 * i]! - x,
          dy = xy[2 * i + 1]! - y;
        const d = dx * dx + dy * dy;
        if (d <= bestD) {
          bestD = d;
          best = i;
        }
      }
  return best;
}

/** Even-odd point in polygon. */
export function inPolygon(x: number, y: number, poly: ReadonlyArray<readonly [number, number]>): boolean {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i]!;
    const [xj, yj] = poly[j]!;
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi || 1e-12) + xi) inside = !inside;
  }
  return inside;
}

/** Point indexes inside a lasso (unit-square polygon), visible ones only. */
export function lassoSelect(
  grid: CloudGrid,
  xy: readonly number[],
  poly: ReadonlyArray<readonly [number, number]>,
  mask?: Uint8Array,
): number[] {
  if (poly.length < 3) return [];
  let minX = 1,
    minY = 1,
    maxX = 0,
    maxY = 0;
  for (const [x, y] of poly) {
    minX = Math.min(minX, x);
    maxX = Math.max(maxX, x);
    minY = Math.min(minY, y);
    maxY = Math.max(maxY, y);
  }
  const { n } = grid;
  const out: number[] = [];
  for (let cy = Math.max(0, Math.floor(minY * n)); cy <= Math.min(n - 1, Math.floor(maxY * n)); cy++)
    for (let cx = Math.max(0, Math.floor(minX * n)); cx <= Math.min(n - 1, Math.floor(maxX * n)); cx++)
      for (const i of grid.cells[cy * n + cx]!) {
        if (mask && !mask[i]) continue;
        if (inPolygon(xy[2 * i]!, xy[2 * i + 1]!, poly)) out.push(i);
      }
  return out.sort((a, b) => a - b);
}

/** child → parents for every leaf and summary in the cloud (for heat propagation and the link overlay). */
export function cloudParents(
  cloud: Pick<MemoryCloudResponse, 'points' | 'summaries'> | undefined,
): Map<number, number[]> {
  const out = new Map<number, number[]>();
  if (!cloud) return out;
  cloud.points.id.forEach((id, i) => out.set(id, cloud.points.parents[i] ?? []));
  cloud.summaries.id.forEach((id, i) => out.set(id, cloud.summaries.parents[i] ?? []));
  return out;
}
