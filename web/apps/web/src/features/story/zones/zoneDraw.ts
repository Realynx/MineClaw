/**
 * Zones on the map, drawn by their cells (docs/web/story-mode.md "Zones"): each zone's row runs filled with its kind's tint and one
 * merged outline around the whole zone (edges between a cell in it and one not). Work zones: a light fill and a dashed edge
 * (growing zones also say what they sow). Overlay layers are drawn apart: home areas a stronger fill, a solid edge and the house
 * name; keep-out red stripes and a solid red edge. The live preview of a paint or erase draws the resulting cells and the cells
 * that would be taken from other zones.
 */
import { cellX, cellZ, zoneCellSet, type StoryZone } from '@mineclaw/contracts';
import { zoneColor } from '../tools';

type Pt = { x: number; y: number };
export type Proj = (x: number, y: number, z: number) => Pt | null;

/** Outline segments [x1, z1, x2, z2, …] (unit edges merged along rows and columns), cached per cell set. */
const outlines = new WeakMap<Set<number>, number[]>();
export function outlineOf(cells: Set<number>): number[] {
  const hit = outlines.get(cells);
  if (hit) return hit;
  const has = (x: number, z: number) => cells.has((x + 2 ** 25) * 2 ** 26 + (z + 2 ** 25));
  // Horizontal edges keyed by z line, vertical by x line: collect unit edges, then merge runs.
  const hz = new Map<number, number[]>();
  const vx = new Map<number, number[]>();
  for (const k of cells) {
    const x = cellX(k),
      z = cellZ(k);
    if (!has(x, z - 1)) addEdge(hz, z, x);
    if (!has(x, z + 1)) addEdge(hz, z + 1, x);
    if (!has(x - 1, z)) addEdge(vx, x, z);
    if (!has(x + 1, z)) addEdge(vx, x + 1, z);
  }
  const seg: number[] = [];
  mergeRuns(hz, true, seg);
  mergeRuns(vx, false, seg);
  outlines.set(cells, seg);
  return seg;
}

/** A unit edge on a grid line. */
function addEdge(m: Map<number, number[]>, line: number, at: number): void {
  (m.get(line) ?? m.set(line, []).get(line)!).push(at);
}

/** Each line's unit edges merged into runs, pushed as segments [x1, z1, x2, z2]. */
function mergeRuns(m: Map<number, number[]>, horiz: boolean, seg: number[]): void {
  for (const [line, list] of m) {
    list.sort((a, b) => a - b);
    let s = list[0]!,
      e = s;
    for (let i = 1; i <= list.length; i++) {
      const v = list[i];
      if (v === e + 1) {
        e = v;
        continue;
      }
      if (horiz) seg.push(s, line, e + 1, line);
      else seg.push(line, s, line, e + 1);
      if (v !== undefined) s = e = v;
    }
  }
}

/** Fills the cells' row runs (one path). Rows: [z, x1, x2]. */
function fillRows(
  ctx: CanvasRenderingContext2D,
  rows: ReadonlyArray<readonly number[]>,
  y: number,
  proj: Proj,
): boolean {
  ctx.beginPath();
  let any = false;
  for (const [z, x1, x2] of rows) {
    const a = proj(x1!, y, z!),
      b = proj(x2! + 1, y, z!),
      c = proj(x2! + 1, y, z! + 1),
      d = proj(x1!, y, z! + 1);
    if (!a || !b || !c || !d) continue;
    ctx.moveTo(a.x, a.y);
    ctx.lineTo(b.x, b.y);
    ctx.lineTo(c.x, c.y);
    ctx.lineTo(d.x, d.y);
    ctx.closePath();
    any = true;
  }
  return any;
}

function strokeOutline(ctx: CanvasRenderingContext2D, seg: number[], y: number, proj: Proj) {
  ctx.beginPath();
  for (let i = 0; i < seg.length; i += 4) {
    const a = proj(seg[i]!, y, seg[i + 1]!),
      b = proj(seg[i + 2]!, y, seg[i + 3]!);
    if (!a || !b) continue;
    ctx.moveTo(a.x, a.y);
    ctx.lineTo(b.x, b.y);
  }
  ctx.stroke();
}

let stripes: CanvasPattern | null | undefined;
function keepOutPattern(ctx: CanvasRenderingContext2D): CanvasPattern | null {
  if (stripes !== undefined) return stripes;
  if (typeof document === 'undefined') return (stripes = null);
  const c = document.createElement('canvas');
  c.width = c.height = 10;
  const g = c.getContext('2d');
  if (!g) return (stripes = null);
  g.strokeStyle = 'rgba(224,82,90,0.55)';
  g.lineWidth = 2;
  g.beginPath();
  g.moveTo(0, 10);
  g.lineTo(10, 0);
  g.moveTo(-5, 5);
  g.lineTo(5, -5);
  g.moveTo(5, 15);
  g.lineTo(15, 5);
  g.stroke();
  return (stripes = ctx.createPattern(c, 'repeat'));
}

const rowsOfZone = (z: StoryZone): ReadonlyArray<readonly number[]> =>
  z.rows ?? [...Array(z.area[5] - z.area[2] + 1).keys()].map((i) => [z.area[2] + i, z.area[0], z.area[3]]);

/** Every zone: work zones first, then the overlay layers on top. `label` names a zone (houses, growing zones' plant). */
export function drawZones(
  ctx: CanvasRenderingContext2D,
  zones: readonly StoryZone[],
  proj: Proj,
  px: number,
  label: (z: StoryZone, houseNo: number) => string | null,
) {
  const order = [...zones].sort((a, b) => layer(a.kind) - layer(b.kind));
  let houseNo = 0;
  for (const z of order) {
    if (z.kind === 'home') houseNo++;
    const y = z.area[1] + 1.02;
    const col = zoneColor(z.kind);
    if (fillRows(ctx, rowsOfZone(z), y, proj)) {
      ctx.fillStyle = zoneFill(ctx, z.kind, col);
      ctx.fill();
    }
    strokeZone(ctx, z, y, proj, col);
    const name = px > 3 ? label(z, houseNo) : null;
    if (name) drawZoneName(ctx, z, y, proj, name);
  }
}

/** A zone's fill: keep-out stripes, a stronger tint for home areas, a light one for work zones. */
function zoneFill(ctx: CanvasRenderingContext2D, kind: string, col: string): string | CanvasPattern {
  if (kind === 'forbidden') return keepOutPattern(ctx) ?? `${col}33`;
  return kind === 'home' ? `${col}38` : `${col}2e`;
}

/** A zone's outline: solid for the overlay layers (home thicker), dashed for work zones. */
function strokeZone(ctx: CanvasRenderingContext2D, z: StoryZone, y: number, proj: Proj, col: string): void {
  const home = z.kind === 'home';
  const overlay = home || z.kind === 'forbidden';
  ctx.strokeStyle = overlay ? col : `${col}cc`;
  ctx.lineWidth = home ? 2 : 1.5;
  if (!overlay) ctx.setLineDash([6, 4]);
  strokeOutline(ctx, outlineOf(zoneCellSet(z)), y, proj);
  ctx.setLineDash([]);
}

/** The zone's name tag at its first cell (its top-left run). */
function drawZoneName(ctx: CanvasRenderingContext2D, z: StoryZone, y: number, proj: Proj, name: string): void {
  const r0 = rowsOfZone(z)[0];
  const at = r0 ? proj(r0[1]!, y, r0[0]!) : null;
  if (!at) return;
  ctx.font = '600 11px system-ui, sans-serif';
  ctx.textAlign = 'left';
  ctx.textBaseline = 'top';
  const w = ctx.measureText(name).width;
  ctx.fillStyle = 'rgba(10,12,16,0.7)';
  ctx.fillRect(at.x + 3, at.y + 3, w + 8, 15);
  ctx.fillStyle = z.kind === 'home' ? '#cfe3ff' : '#e8f5dc';
  ctx.fillText(name, at.x + 7, at.y + 4.5);
}

/** A zone's label on the map: a house's name; a growing zone's active plant (and name); other zones their name. */
export function zoneLabel(
  z: StoryZone,
  houseNo: number,
  plants: ReadonlyArray<{ id: string; label: string }> | null | undefined,
): string | null {
  if (z.kind === 'home') return z.name || z.label || `House ${houseNo}`;
  if (z.kind === 'farm' && (z.plant || z.bills)) return farmLabel(z, plants);
  return z.name || null;
}

/** A growing zone's label: what it sows now (its name first when it has one). */
function farmLabel(z: StoryZone, plants: ReadonlyArray<{ id: string; label: string }> | null | undefined): string {
  const p = plants?.find((x) => x.id === (z.activePlant ?? null));
  const what = p ? p.label : z.activePlant ? z.activePlant : 'idle: bills met';
  return z.name ? `${z.name} · ${what}` : what;
}

const layer = (kind: string) => (kind === 'home' ? 2 : kind === 'forbidden' ? 3 : 1);

/**
 * A paint's or an erase's preview: the zone the painted cells end up in (all of it: a merge shows the merged zone) in the tool's
 * colour, and the cells taken from other zones in red.
 */
export function drawZonePreview(
  ctx: CanvasRenderingContext2D,
  zones: readonly StoryZone[],
  resultId: string | number | undefined,
  removed: Set<number>,
  color: string,
  y: number,
  proj: Proj,
) {
  const z = resultId !== undefined ? zones.find((x) => String(x.id) === String(resultId)) : undefined;
  if (z) {
    if (fillRows(ctx, rowsOfZone(z), y, proj)) {
      ctx.fillStyle = `${color}55`;
      ctx.fill();
    }
    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    strokeOutline(ctx, outlineOf(zoneCellSet(z)), y, proj);
  }
  if (removed.size) {
    const rows: number[][] = [...removed].map((k) => [cellZ(k), cellX(k), cellX(k)]);
    if (fillRows(ctx, rows, y, proj)) {
      ctx.fillStyle = 'rgba(255,107,107,0.45)';
      ctx.fill();
    }
    ctx.strokeStyle = '#ff6b6b';
    ctx.lineWidth = 1.5;
    strokeOutline(ctx, outlineOf(removed), y, proj);
  }
}
