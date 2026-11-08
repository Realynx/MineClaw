/**
 * The Story map overlay's tool previews (StoryMap.tsx): a tool drag's rectangle and grid with its live result (a build's
 * blocks, a zone paint, a designate drag's targets or height-mode columns) and the placement under the cursor before a drag
 * starts. Drawn with the frame's {@link MapPainter}.
 */
import { designationCells, type StoryColonyState } from '@mineclaw/contracts';
import { growPlantNow } from './GrowingZones';
import type { MapPainter } from './mapDraw';
import { cellRect, type Pt, type V3 } from './math';
import type { DragResult, MapTool, StoryMapProps } from './StoryMap';
import {
  buildItem,
  DESIGNATE_TOOLS,
  heightLabel,
  placeDesign,
  preview as buildPreview,
  zoneColor,
  type BuildOpts,
  type BuildPreview,
  type BuildToolId,
  type DesignateId,
} from './tools';
import { COLORS } from './visuals';
import { drawZonePreview } from './zones/zoneDraw';
import { previewLabel, zoneInto, zonePreview } from './zones/zonePaint';
import { drawQuarryPreview, quarryModeNow } from './zones/quarry';

/** One column of a height-mode preview: x, z, its highest and lowest designated y, how many cells. */
export type ColumnSpan = [number, number, number, number, number];

/** Groups a height drag's cells by column (once per scan, not per frame). */
export function columnSpans(cells: readonly V3[]): ColumnSpan[] {
  const by = new Map<string, ColumnSpan>();
  for (const [x, y, z] of cells) {
    const k = `${x},${z}`;
    const c = by.get(k);
    if (!c) by.set(k, [x, z, y, y, 1]);
    else {
      c[2] = Math.max(c[2], y);
      c[3] = Math.min(c[3], y);
      c[4]++;
    }
  }
  return [...by.values()];
}

/** A designate drag's scanned preview (StoryMap's scan ref). */
export type ScanResult = (DragResult & { cols?: ColumnSpan[] }) | null;

/** What a tool drag's preview reads of the drag in progress. */
export interface ToolDrag {
  now: Pt;
  shift: boolean;
  alt: boolean;
  startCell: V3 | null;
  endCell: V3 | null;
}

/** A fishing-zone drag's still water (StoryMap's water ref): the rectangle it was read for and its columns. */
export interface WaterScan {
  key: string;
  cells: Set<number> | null;
}

type ZoneMapTool = Extract<MapTool, { kind: 'zone' }>;

/** The colour of a tool's drag rectangle. */
export function dragColor(tool: MapTool): string {
  if (tool.kind === 'designate') return COLORS.designation[tool.id] ?? '#ffffff';
  if (tool.kind === 'zone') return tool.op === 'remove' ? '#ff6b6b' : zoneColor(tool.id);
  return COLORS.chest;
}

/** A build preview's label: its blocks, the material it costs, and why it can't be queued. */
export function buildDragText(pv: BuildPreview): string {
  const n = pv.blocks.length;
  const material = typeof pv.item.material === 'string' ? pv.item.material.replace(/_/g, ' ') : 'blocks';
  const cost = pv.item.kind !== 'remove' ? ` · ${pv.cost} ${material}` : '';
  return `${n} block${n === 1 ? '' : 's'}${cost}${pv.reason ? ` · ${pv.reason}` : ''}`;
}

/** A tool's drag (left button, past the threshold): its rectangle and what it would do. */
export function drawToolDrag(
  pt: MapPainter,
  props: StoryMapProps,
  d: ToolDrag,
  scan: ScanResult,
  water: WaterScan,
): void {
  const tool = props.tool;
  const a = d.startCell!,
    b = d.endCell!;
  if (tool.kind === 'build') {
    drawBuildDrag(pt, tool.id, a, b, { ...props.build, line: d.alt }, d.now);
    return;
  }
  const rect = cellRect(a, b);
  const col = dragColor(tool);
  drawDragRect(pt, rect, col, props.level + 1.02);
  if (tool.kind === 'zone') drawZoneDrag(pt, tool, props, d, rect, col, water);
  else if (tool.kind === 'designate') drawDesignateDrag(pt, tool.id, props, d.now, rect, col, scan);
  else pt.label(d.now, `${rect[2] - rect[0] + 1} × ${rect[3] - rect[1] + 1}`, true);
}

function drawBuildDrag(pt: MapPainter, id: BuildToolId, a: V3, b: V3, o: BuildOpts, now: Pt): void {
  const pv = buildPreview(buildItem(id, a, b, o));
  const col = pv.valid ? (pv.item.kind === 'remove' ? '#ff6b6b' : '#7fd3ff') : '#ff6b6b';
  for (const g of pv.blocks.slice(0, 1500)) pt.cube(g[0], g[1], g[2], col, 0.42, false);
  pt.label(now, buildDragText(pv), pv.valid);
}

/** The drag's rectangle on the working level, with the cell grid when zoomed in and not too large. */
function drawDragRect(pt: MapPainter, rect: readonly number[], col: string, y: number): void {
  const q = pt.quad(rect[0]!, rect[1]!, rect[2]! + 1, rect[3]! + 1, y);
  if (!q) return;
  const ctx = pt.ctx;
  pt.poly(q);
  ctx.fillStyle = `${col}22`;
  ctx.fill();
  ctx.strokeStyle = col;
  ctx.lineWidth = 1.5;
  ctx.stroke();
  if (pt.cell >= 9 && (rect[2]! - rect[0]!) * (rect[3]! - rect[1]!) < 2500) drawGrid(pt, rect, col, y);
}

function drawGrid(pt: MapPainter, rect: readonly number[], col: string, y: number): void {
  const ctx = pt.ctx;
  const [x0, z0, x1, z1] = rect as [number, number, number, number];
  ctx.strokeStyle = `${col}33`;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let x = x0 + 1; x <= x1; x++) gridLine(ctx, pt.at(x, y, z0), pt.at(x, y, z1 + 1));
  for (let z = z0 + 1; z <= z1; z++) gridLine(ctx, pt.at(x0, y, z), pt.at(x1 + 1, y, z));
  ctx.stroke();
}

function gridLine(ctx: CanvasRenderingContext2D, s1: Pt | null, s2: Pt | null): void {
  if (!s1 || !s2) return;
  ctx.moveTo(s1.x, s1.y);
  ctx.lineTo(s2.x, s2.y);
}

/** A zone paint: the resulting cells as the director will paint or erase them (zones/zonePaint.ts), a quarry's stair. */
function drawZoneDrag(
  pt: MapPainter,
  tool: ZoneMapTool,
  props: StoryMapProps,
  d: ToolDrag,
  rect: [number, number, number, number],
  col: string,
  water: WaterScan,
): void {
  const zs = props.state?.zones ?? [];
  const into = zoneInto(tool, zs, d.startCell, d.shift);
  const wk = `${rect.join(',')}@${props.level}`;
  const wet = water.key === wk ? water.cells : null;
  const out = zonePreview(zs, tool, rect, props.level, growPlantNow(), into, wet);
  if (out) drawZonePreview(pt.ctx, out.zones, out.result.id, out.removedCells, col, props.level + 1.03, pt.at);
  // A quarry: its spiral stair (step 1 at the top) and how deep it goes.
  const stair =
    out && tool.id === 'mine' && tool.op === 'add' && into === undefined && quarryModeNow() === 'quarry'
      ? drawQuarryPreview(
          pt.ctx,
          out.zones.find((z) => String(z.id) === String(out.result.id)),
          props.level,
          pt.at,
        )
      : '';
  pt.label(d.now, previewLabel(tool, out, (rect[2] - rect[0] + 1) * (rect[3] - rect[1] + 1)) + stair, out !== null);
}

/** A designate drag: its height-mode columns, else the scanned targets (and what Cancel takes), with the count. */
function drawDesignateDrag(
  pt: MapPainter,
  id: DesignateId,
  props: StoryMapProps,
  now: Pt,
  rect: readonly number[],
  col: string,
  res: ScanResult,
): void {
  const t = DESIGNATE_TOOLS.find((x) => x.id === id)!;
  if (res?.height && res.cols) {
    drawHeightColumns(pt, res.cols, col, res.height.layers > 1, !!res.height.tooMany, props.view === 'angled');
    pt.label(
      now,
      heightLabel(t.label, props.heightMode, res.height, props.layers),
      !res.height.tooMany && res.count > 0,
    );
    return;
  }
  if (!res) {
    pt.label(now, `${t.label}: …`, true);
    return;
  }
  drawScanTargets(pt, res, id, col);
  if (id === 'cancel') drawCancelled(pt, props.state?.designations ?? [], rect);
  pt.label(now, `${t.label}: ${res.count} ${res.count === 1 ? t.noun[0] : t.noun[1]}`, true);
}

/** A column's top-face fill: fuller the deeper it goes. */
const columnAlpha = (n: number) => (n > 1 ? (n > 3 ? '66' : '44') : '22');

/** Exactly what will be designated: each column's top face outlined (fuller the deeper it goes), its bottom dashed. */
function drawHeightColumns(
  pt: MapPainter,
  cols: readonly ColumnSpan[],
  col: string,
  deep: boolean,
  tooMany: boolean,
  angled: boolean,
): void {
  const ctx = pt.ctx;
  ctx.lineWidth = 1.2;
  for (const [x, z, top, bottom, n] of cols.slice(0, 2500)) {
    const qt = pt.quad(x + 0.08, z + 0.08, x + 0.92, z + 0.92, top + 1.01);
    if (!qt) continue;
    pt.poly(qt);
    ctx.fillStyle = `${col}${columnAlpha(n)}`;
    ctx.fill();
    ctx.strokeStyle = tooMany ? '#ff6b6b' : col;
    ctx.stroke();
    if (deep && bottom < top && angled) dashedBottom(pt, x, z, bottom);
  }
}

function dashedBottom(pt: MapPainter, x: number, z: number, bottom: number): void {
  const qb = pt.quad(x + 0.08, z + 0.08, x + 0.92, z + 0.92, bottom + 0.01);
  if (!qb) return;
  pt.poly(qb);
  pt.ctx.setLineDash([3, 3]);
  pt.ctx.stroke();
  pt.ctx.setLineDash([]);
}

/** A designate scan's matching blocks (dots) and entities (marks). */
function drawScanTargets(pt: MapPainter, res: DragResult, id: DesignateId, col: string): void {
  const ctx = pt.ctx;
  for (const c of res.cells.slice(0, 1200)) {
    const s = pt.at(c[0] + 0.5, c[1] + 1, c[2] + 0.5);
    if (s) {
      ctx.fillStyle = `${col}bb`;
      ctx.fillRect(s.x - 3, s.y - 3, 6, 6);
    }
  }
  for (const en of res.entities) {
    const s = pt.at(en.x, en.y + 0.3, en.z);
    if (s) pt.mark(id, s, col, 12);
  }
}

/** Is a designation's first cell inside the drag's rectangle (x0, z0, x1, z1)? */
export function designationInRect(d: StoryColonyState['designations'][number], rect: readonly number[]): boolean {
  const bb = designationCells(d)[0];
  return !!bb && bb[0] >= rect[0]! && bb[0] <= rect[2]! && bb[2] >= rect[1]! && bb[2] <= rect[3]!;
}

/** The Cancel tool: a red X over every designation it would cancel. */
function drawCancelled(pt: MapPainter, list: StoryColonyState['designations'], rect: readonly number[]): void {
  for (const dd of list) {
    if (!designationInRect(dd, rect)) continue;
    const bb = designationCells(dd)[0]!;
    const s = pt.at(bb[0] + 0.5, bb[1] + 1, bb[2] + 0.5);
    if (!s) continue;
    pt.ctx.strokeStyle = '#ff6b6b';
    pt.ctx.lineWidth = 2;
    pt.cross(s, 6);
  }
}

/** A design under the cursor (Q/E turns it): its blocks (unless the viewer draws it as holograms) and what a click queues. */
export function drawDesignHover(
  pt: MapPainter,
  tool: Extract<MapTool, { kind: 'design' }>,
  a: V3,
  at: Pt,
  props: StoryMapProps,
  holoOn: boolean,
): void {
  if (!holoOn)
    for (const b of placeDesign(tool.blocks, a, props.build.facing, !!tool.mine, props.state?.version).slice(0, 2500))
      pt.cube(b[0], b[1], b[2], b[3] === 0 ? '#ff6b6b' : '#7fd3ff', 0.38, false);
  pt.label(
    at,
    tool.mine
      ? `${tool.name}: y ${tool.mine.min}..${tool.mine.max} (absolute) · click to queue the mine`
      : `${tool.name}: ${tool.blocks.length} blocks · click to queue`,
    true,
  );
}

/** The placement ghost under the cursor before a drag starts. */
export function drawBuildHover(pt: MapPainter, id: BuildToolId, a: V3, build: BuildOpts): void {
  const pv = buildPreview(buildItem(id, a, a, build));
  for (const b of pv.blocks.slice(0, 600)) pt.cube(b[0], b[1], b[2], b[3] === 0 ? '#ff6b6b' : '#7fd3ff', 0.4, false);
}
