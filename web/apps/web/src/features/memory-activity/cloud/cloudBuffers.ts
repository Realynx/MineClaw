/**
 * What the memory cloud uploads to the GPU (CloudCanvas.tsx binds these to its buffers): the faded link segments, the per-point
 * style shared by every pane, and each pane's halos. Built when the data changes, never per frame.
 */
import type { MemoryCloudResponse } from '@mineclaw/contracts';
import { clusterColor, hexRgb, legendKey, pointLook, type Theme } from './cloudStyle';

/** A halo: point index, colour, alpha and size. */
export type Halo = [number, number, number, number, number, number];

/** Where a pane's points and summaries are now, and where they were before its last switch (the same arrays when settled). */
export interface PaneXy {
  xy: readonly number[];
  sxy: readonly number[];
  pxy: readonly number[];
  psxy: readonly number[];
}

/** A pane's positions now and (mid-switch) before; settled, both are the same arrays. */
export function paneXy(
  now: { xy: readonly number[]; sxy: readonly number[] },
  before: { xy: readonly number[]; sxy: readonly number[] } | null | undefined,
): PaneXy {
  return { xy: now.xy, sxy: now.sxy, pxy: before ? before.xy : now.xy, psxy: before ? before.sxy : now.sxy };
}

/**
 * The link overlay's segments (x, y, prevX, prevY per vertex): leaf → each parent summary, summary → each parent; a hidden
 * cluster's leaves and summaries are left out.
 */
export function linkSegments(
  cloud: MemoryCloudResponse,
  p: PaneXy,
  mask: Uint8Array,
  hidden: ReadonlySet<number>,
  summaryIndexOf: ReadonlyMap<number, number>,
): number[] {
  const seg: number[] = [];
  const push = (xy: readonly number[], pxy: readonly number[], i: number) => seg.push(xy[2 * i]!, xy[2 * i + 1]!, pxy[2 * i]!, pxy[2 * i + 1]!);
  const links = (fromXy: readonly number[], fromPxy: readonly number[], i: number, parents: readonly number[] | undefined) => {
    for (const parent of parents ?? []) {
      const q = summaryIndexOf.get(parent);
      if (q === undefined) continue;
      push(fromXy, fromPxy, i);
      push(p.sxy, p.psxy, q);
    }
  };
  const n = cloud.points.id.length;
  for (let i = 0; i < n; i++) {
    if (mask[i]) links(p.xy, p.pxy, i, cloud.points.parents[i]);
  }
  cloud.summaries.id.forEach((_, si) => {
    const c = cloud.summaries.cluster[si]!;
    if (c >= 0 && hidden.has(legendKey(c))) return;
    links(p.sxy, p.psxy, si, cloud.summaries.parents[si]);
  });
  return seg;
}

export interface StyleInput {
  cloud: MemoryCloudResponse;
  brightness: Float32Array;
  theme: Theme;
  highlight: Uint8Array | null;
  selectedId: number | null;
  mask: Uint8Array;
}

/** Per point: r, g, b, alpha, size (size 0 = not drawn), and the halos of the highlighted / selected points. */
export function pointStyles(s: StyleInput, n: number): { data: Float32Array; halos: Halo[] } {
  const data = new Float32Array(n * 5);
  const halos: Halo[] = [];
  const rgb = new Map<number, [number, number, number]>();
  const hl = s.highlight;
  for (let i = 0; i < n; i++) {
    if (!s.mask[i]) continue; // size 0: not drawn
    const cl = s.cloud.points.cluster[i]!;
    let col = rgb.get(cl);
    if (!col) rgb.set(cl, (col = hexRgb(clusterColor(cl, s.theme))));
    const look = pointLook(s.brightness[i] ?? 0, 1, {
      dimmed: hl ? !hl[i] : false,
      highlighted: hl ? !!hl[i] : false,
      selected: s.cloud.points.id[i] === s.selectedId,
    });
    data.set([col[0], col[1], col[2], look.alpha, look.size], i * 5);
    if (look.halo > 0) halos.push([i, col[0], col[1], col[2], 0.55 * look.halo, look.size * 4.5]);
  }
  return { data, halos };
}

/** One pane's halo vertices (x, y, prevX, prevY, r, g, b, alpha, size). */
export function haloVertices(halos: readonly Halo[], xy: readonly number[], pxy: readonly number[]): Float32Array {
  const buf = new Float32Array(halos.length * 9);
  halos.forEach(([i, r, gg, bb, al, sz], k) => buf.set([xy[2 * i]!, xy[2 * i + 1]!, pxy[2 * i]!, pxy[2 * i + 1]!, r, gg, bb, al, sz], k * 9));
  return buf;
}
