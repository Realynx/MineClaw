/**
 * Memory cloud styling and geometry (architecture.md, "Memory activity tab" → Cloud). Pure: the canvas and the tests call these.
 * - Colour = the bot's own RAPTOR cluster. Eight fixed categorical slots in cluster order (largest first, the bridge's order), one
 *   set stepped for each theme (both validated with the dataviz palette checks); a ninth cluster on folds into "other".
 * - Brightness = recency of use, the Memory activity heat model (`0.5^(age/halfLife)`) with a faint linear tail across the chosen
 *   window, so anything used in the window still reads apart from never-used memories once its heat has decayed.
 */
import { decayHeat, toNumber, type CloudArrangement, type CloudFidelity, type MemoryCloudResponse } from '@mineclaw/contracts';
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
  const base = pointBase(k);
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

/** A point's base diameter at zoom k: points shrink when zoomed out so tens of thousands don't merge into a blob. */
export function pointBase(k: number): number {
  return Math.min(7, Math.max(2.2, 2.6 * Math.sqrt(Math.max(0.25, k))));
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

// ---------- arrangements, fidelity, scale ----------

/** The bridge's arrangements, picker order; `metric` = drawn distance is meant to be read as embedding distance. */
export const ARRANGEMENTS: ReadonlyArray<{ id: CloudArrangement; short: string; label: string; metric: boolean; hint: string }> = [
  {
    id: 'mds',
    short: 'Distances',
    label: 'Distances (MDS)',
    metric: true,
    hint: 'Metric MDS on cosine distance: how far apart two memories are drawn is how different their embeddings are.',
  },
  {
    id: 'umap',
    short: 'Neighbourhoods',
    label: 'Neighbourhoods (UMAP)',
    metric: false,
    hint: 'UMAP keeps each memory next to its nearest neighbours; the gaps between islands mean nothing.',
  },
  {
    id: 'pca',
    short: 'PCA',
    label: 'PCA',
    metric: true,
    hint: 'The two directions along which the memories differ most (linear, global axes).',
  },
  {
    id: 'tree',
    short: 'Tree',
    label: 'Tree (force)',
    metric: false,
    hint: "The bot's RAPTOR tree laid out by springs along its parent links, started from the distance picture.",
  },
];
export const DEFAULT_ARRANGEMENT: CloudArrangement = 'mds';

export function arrangementMeta(id: CloudArrangement) {
  return ARRANGEMENTS.find((a) => a.id === id) ?? ARRANGEMENTS[0]!;
}

export function isArrangement(v: string | null | undefined): v is CloudArrangement {
  return !!v && ARRANGEMENTS.some((a) => a.id === v);
}

/** `?cmp=mds,umap`: 2–4 distinct known arrangements in the given order, else null (not comparing). */
export function parseCompare(v: string | null | undefined): CloudArrangement[] | null {
  if (!v) return null;
  const out: CloudArrangement[] = [];
  for (const s of v.split(',')) if (isArrangement(s) && !out.includes(s)) out.push(s);
  return out.length >= 2 ? out.slice(0, 4) : null;
}

export interface FidelityBadge {
  /** "Distances: true to embeddings" */
  text: string;
  /** "r = 0.86, stress 0.12" */
  detail: string;
  tone: 'ok' | 'info' | 'warn';
  tooltip: string;
}

/**
 * The fidelity badge: how far drawn distances can be read as embedding distances, from the bridge's numbers. "True to embeddings"
 * needs a strong correlation and low stress; the tooltip always says why it can never be perfect.
 */
export function fidelityBadge(arrangement: CloudArrangement, f: CloudFidelity | null | undefined): FidelityBadge | null {
  if (!f) return null;
  const r = toNumber(f.pearson);
  const stress = toNumber(f.stress);
  if (!Number.isFinite(r) || !Number.isFinite(stress)) return null;
  const detail = `r = ${r.toFixed(2)}, stress ${stress.toFixed(2)}`;
  const grade = fidelityGrade(r, stress);
  return { text: grade.text, detail, tone: grade.tone, tooltip: fidelityTooltip(arrangement, f) };
}

/** True to embeddings (strong correlation, low stress), roughly to scale (fair correlation), or not to scale. */
function fidelityGrade(r: number, stress: number): { text: string; tone: FidelityBadge['tone'] } {
  if (r >= 0.8 && stress <= 0.3) return { text: 'Distances: true to embeddings', tone: 'ok' };
  return r >= 0.6 ? { text: 'Distances: roughly to scale', tone: 'info' } : { text: 'Distances: not to scale', tone: 'warn' };
}

/** Why the drawn distances can never be perfect, what r and stress mean, the axes' variance, and (non-metric) how to read it. */
function fidelityTooltip(arrangement: CloudArrangement, f: CloudFidelity): string {
  const meta = arrangementMeta(arrangement);
  const ve = f.varianceExplained?.map((v) => `${Math.round(toNumber(v) * 100)} %`);
  return [
    `${meta.label}. ${meta.hint}`,
    `Each memory is a 1024-number embedding; no flat picture can keep every distance between them, so some are stretched or squeezed.`,
    `r = correlation between drawn and embedding distance on ${f.pairs.toLocaleString()} memory pairs (1 = perfect); stress = how much the drawn distances are off overall (0 = perfect, below 0.3 is good).`,
    ve ? `The two axes explain ${ve.join(' and ')} of the variance.` : '',
    meta.metric ? '' : 'This arrangement is not built to keep distances; read neighbours, not gaps.',
  ]
    .filter(Boolean)
    .join(' ');
}

/**
 * A scale bar near `targetPx` long: the round cosine distance (1, 2 or 5 × 10ⁿ) whose drawn length is closest to it. `unitDistance` is
 * the cosine distance one unit of the unit square stands for; `pxPerUnit` how many screen px that unit is at the current zoom.
 */
export function niceScale(unitDistance: number, pxPerUnit: number, targetPx = 110): { px: number; value: number; label: string } | null {
  if (!(unitDistance > 0) || !(pxPerUnit > 0)) return null;
  const perPx = unitDistance / pxPerUnit; // cosine distance per px
  const raw = targetPx * perPx;
  const p = Math.pow(10, Math.floor(Math.log10(raw)));
  let best = p;
  for (const m of [1, 2, 5, 10]) if (Math.abs(m * p - raw) < Math.abs(best - raw)) best = m * p;
  const value = Number(best.toPrecision(2));
  const digits = Math.max(0, -Math.floor(Math.log10(value)));
  return { px: value / perPx, value, label: `${value.toFixed(digits)} cosine distance` };
}

/** Drawn distance of two points in cosine-distance units (the arrangement's best-fit scale). */
export function drawnDistance(xy: ArrayLike<number>, i: number, j: number, unitDistance: number): number {
  return Math.hypot(xy[2 * i]! - xy[2 * j]!, xy[2 * i + 1]! - xy[2 * j + 1]!) * unitDistance;
}

/**
 * Link overlay alpha: faded at the fitted view, fading further when zoomed out (the blobs carry the picture there) and with more
 * links on screen, a little stronger when zoomed in on a few.
 */
export function linkAlpha(k: number, segments: number, theme: Theme): number {
  const base = theme === 'dark' ? 0.075 : 0.085;
  const density = Math.min(1, Math.sqrt(4000 / Math.max(1, segments)));
  const zoom = k < 1 ? k * k : Math.min(1.8, 1 + 0.4 * Math.log2(k));
  return Math.min(0.16, Math.max(0.008, base * Math.max(0.35, density) * zoom));
}

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Pane rectangles for one canvas: the room right of the legend card (when the canvas is wide enough), split side by side for two
 * panes (stacked when the room is taller than wide), a 2×2 grid for three or four.
 */
export function paneRects(w: number, h: number, count: number, legendW: number, legendRoomMinW: number, gap = 8): Rect[] {
  const left = w >= legendRoomMinW ? legendW : 0;
  const room: Rect = { x: left, y: 0, w: Math.max(0, w - left), h };
  if (count <= 1) return [room];
  if (count === 2) {
    if (room.w >= room.h) {
      const pw = (room.w - gap) / 2;
      return [
        { x: room.x, y: 0, w: pw, h },
        { x: room.x + pw + gap, y: 0, w: pw, h },
      ];
    }
    const ph = (room.h - gap) / 2;
    return [
      { x: room.x, y: 0, w: room.w, h: ph },
      { x: room.x, y: ph + gap, w: room.w, h: ph },
    ];
  }
  const pw = (room.w - gap) / 2;
  const ph = (room.h - gap) / 2;
  return Array.from({ length: Math.min(4, count) }, (_, i) => ({
    x: room.x + (i % 2) * (pw + gap),
    y: Math.floor(i / 2) * (ph + gap),
    w: pw,
    h: ph,
  }));
}

/** The fitted square inside a pane: the unit square in 94 % of the pane's short side, centred. */
export function paneFrame(r: Rect): { base: number; ox: number; oy: number } {
  const base = Math.max(1, Math.min(r.w, r.h) * 0.94);
  return { base, ox: r.x + (r.w - base) / 2, oy: r.y + (r.h - base) / 2 };
}

/**
 * Another arrangement's positions in the primary's point order (the bridge keeps one order for a tree state; this guards a pane
 * that answered for a different state). Missing points go far off the unit square.
 */
export function alignXy(primaryIds: readonly number[], ids: readonly number[], xy: readonly number[]): readonly number[] {
  if (ids.length === primaryIds.length && ids.every((id, i) => id === primaryIds[i])) return xy;
  const at = new Map(ids.map((id, i) => [id, i]));
  const out = new Array<number>(primaryIds.length * 2);
  primaryIds.forEach((id, i) => {
    const j = at.get(id);
    out[2 * i] = j === undefined ? -9 : xy[2 * j]!;
    out[2 * i + 1] = j === undefined ? -9 : xy[2 * j + 1]!;
  });
  return out;
}

/** Calm ease for the arrangement switch (cubic in-out over 0..1). */
export function easeInOut(t: number): number {
  const x = Math.min(1, Math.max(0, t));
  return x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2;
}

/** Point size factor at zoom k relative to the fitted view (pointLook sizes are all proportional to its base). */
export function zoomSizeFactor(k: number): number {
  return pointBase(k) / pointBase(1);
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
