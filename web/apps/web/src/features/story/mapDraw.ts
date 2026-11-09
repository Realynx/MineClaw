/**
 * The Story map overlay's drawing (StoryMap.tsx draws one frame by calling these layers in order): the primitives (a cell's
 * quad, a mark sprite, a ghost cube, a label pill) on one long-lived {@link MapPainter} that each frame re-aims at its
 * canvas and camera, and the colony's static layers (home, beds, build ghosts, failed build cells, designation marks, chests
 * and workbenches, bodies). Kept allocation-light for the ≥ 40 fps software-WebGL budget: the painter and its projector are
 * made once per map, not per frame.
 */
import { designationCells, targetPos, type ContainerSummary, type StoryColonyState } from '@mineclaw/contracts';
import { getViewerQuality } from '@mineclaw/world-viewer/settings';
import type { BodyLook } from './bodyLook';
import { ChestBadgeCache, drawChestBadge } from './chestBadges';
import { clusterFailed, type FailedCluster, type FailedMark } from './failedLabels';
import { project, type Pt, type V3, type ViewMats } from './math';
import { failedCells, type FailedCell } from './tools';
import { COLORS, glyph, pawnColor } from './visuals';
import { WORK_GLYPHS } from './workAppearance';
import { pawnNumber } from './pawnNumber';

type Pawn = StoryColonyState['pawns'][number];
type AiBody = NonNullable<StoryColonyState['ai']>[number];
type Bed = NonNullable<StoryColonyState['beds']>[number];
type Storage = NonNullable<StoryColonyState['storages']>[number];
type GhostBlocks = NonNullable<StoryColonyState['ghosts']>['blocks'];

/** One selectable body with its screen point (StoryMap `bodies`). */
export interface MapBody {
  key: string;
  at: Pt | null;
  kind: 'pawn' | 'ai';
  pos: V3;
  look: BodyLook;
  r: number;
}

/** Where a failed designation's ring was drawn (clicks select it and its cluster). */
export interface FailedRing {
  id: string | number;
  cluster: Array<string | number>;
  x: number;
  y: number;
}

export const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

/** The job glyph drawn beside a pawn doing it. */
export const GLYPH_OF = WORK_GLYPHS;

const STORAGE_PRIORITIES = ['low', 'normal', 'preferred', 'important', 'critical'];
const BRACKET_CORNERS = [
  [-1, -1],
  [1, -1],
  [1, 1],
  [-1, 1],
] as const;

// ------------------------------------------------------------------ mark sprites

/** A map mark (its glyph on a dark disc) drawn once into a small canvas and stamped with drawImage after that: a few hundred
 * marks a frame cost a blit each instead of a path fill with save/restore (the Story perf profile's top item). */
const sprites = new Map<string, HTMLCanvasElement | null>();

/** Draws a mark's disc and glyph at the context's origin (`size` px across). */
function paintMark(x: CanvasRenderingContext2D, id: string, color: string, size: number) {
  const g = glyph(id);
  x.fillStyle = 'rgba(12,14,18,0.72)';
  x.beginPath();
  x.arc(size / 2, size / 2, size / 2 + 2, 0, Math.PI * 2);
  x.fill();
  if (!g) return;
  x.scale(size / 16, size / 16);
  x.fillStyle = color;
  x.strokeStyle = color;
  x.lineWidth = 1.4;
  x.fill(g, 'evenodd');
  if (id === 'hunt') x.stroke(g);
}

function renderSprite(id: string, color: string, size: number): HTMLCanvasElement | null {
  if (typeof document === 'undefined') return null;
  const dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
  const full = size + 6;
  const c = document.createElement('canvas');
  c.width = Math.ceil(full * dpr);
  c.height = Math.ceil(full * dpr);
  const x = c.getContext('2d');
  if (!x) return null;
  x.scale(dpr, dpr);
  x.translate(3, 3);
  paintMark(x, id, color, size);
  return c;
}

export function markSprite(id: string, color: string, size: number): HTMLCanvasElement | null {
  const key = `${id}|${color}|${size}`;
  const hit = sprites.get(key);
  if (hit !== undefined) return hit;
  const c = renderSprite(id, color, size);
  if (sprites.size > 256) sprites.clear();
  sprites.set(key, c);
  return c;
}

// ------------------------------------------------------------------ per-state models (worked out once per list)

/** One designation as the map draws it: its mark's cell (a tree's top log), colour, ring, owner. */
interface DesMark {
  d: StoryColonyState['designations'][number];
  cell: V3;
  col: string;
  ring: string | null;
  failed: boolean;
}

/** The designations' marks and the failed ones' clusters, worked out once per state (not per frame). */
export interface DesModel {
  marks: DesMark[];
  failed: FailedMark[];
  clusters: FailedCluster[];
  clusterOf: Map<string, Array<string | number>>;
}

const desModels = new WeakMap<object, DesModel>();

/** The failed build cells of a ghost list (blocked, or with a failure's reason), worked out once per list; by column for hovers. */
const failedModels = new WeakMap<object, { cells: FailedCell[]; byColumn: Map<string, FailedCell> }>();
export function failedModel(blocks: GhostBlocks) {
  const hit = failedModels.get(blocks);
  if (hit) return hit;
  const cells = failedCells(blocks);
  const byColumn = new Map<string, FailedCell>();
  for (const f of cells) if (!byColumn.has(`${f.cell[0]},${f.cell[2]}`)) byColumn.set(`${f.cell[0]},${f.cell[2]}`, f);
  const m = { cells, byColumn };
  failedModels.set(blocks, m);
  return m;
}

/** The cells a designation's mark goes on: a tree's top log, else its first cell. */
function markCells(d: StoryColonyState['designations'][number]): V3[] {
  const all = designationCells(d);
  return d.kind === 'chop' && all.length > 1 ? [all.reduce((a, b) => (b[1] > a[1] ? b : a))] : all.slice(0, 1);
}

function ringOf(d: StoryColonyState['designations'][number]): string | null {
  if (d.status === 'failed') return COLORS.danger;
  return (d.fails ?? 0) > 0 ? '#ffb35c' : null;
}

/** A designation's failed (or waiting) mark for the labels, if it has one. */
function failedMarkOf(d: StoryColonyState['designations'][number], cell: V3): FailedMark | null {
  if (d.status === 'failed') return { id: d.id, kind: d.kind, reason: d.reason ?? '', cell };
  if (d.wait) return { id: d.id, kind: d.kind, reason: d.wait, cell, tone: 'wait' };
  return null;
}

export function designationModel(list: StoryColonyState['designations']): DesModel {
  const hit = desModels.get(list);
  if (hit) return hit;
  const marks: DesMark[] = [];
  const failed: FailedMark[] = [];
  for (const d of list) {
    const isFailed = d.status === 'failed';
    const ring = ringOf(d);
    for (const b of markCells(d)) {
      const cell: V3 = [b[0], b[1], b[2]];
      marks.push({ d, cell, col: COLORS.designation[d.kind] ?? '#ffffff', ring, failed: isFailed });
      const f = failedMarkOf(d, cell);
      if (f) failed.push(f);
    }
  }
  const clusters = clusterFailed(failed);
  const clusterOf = new Map<string, Array<string | number>>();
  for (const cl of clusters) for (const id of cl.ids) clusterOf.set(String(id), cl.ids);
  const m = { marks, failed, clusters, clusterOf };
  desModels.set(list, m);
  return m;
}

// ------------------------------------------------------------------ small pure helpers

/** A pawn low on health or food: its name always shows, in red. */
export function inDanger(pw: Pawn | undefined): boolean {
  return pw ? (pw.hp ?? 20) <= 8 || (pw.food ?? 20) <= 4 : false;
}

/** A known pawn's number on its bed tag; unnumbered owners retain their initials. */
function bedInitials(owner: string, pawns: readonly Pawn[] | undefined): string {
  const pw = pawns?.find((x) => x.name === owner);
  return (pw && pawnNumber(pw.name)) || (pw?.display ?? owner).slice(0, 2).toUpperCase();
}

/** A pawn's job target and queued order targets, as cells. */
function orderTargets(pw: Pawn | undefined): Array<[number, number, number]> {
  return [pw?.job?.target, ...(pw?.queue ?? []).map((o) => o.target)]
    .map((t) => targetPos(t))
    .filter((t): t is [number, number, number] => !!t);
}

/** A pawn's number takes precedence over old nicknames; AI bodies and other identities keep their existing labels. */
export function bodyLabel(key: string, pw: Pawn | undefined, ai: AiBody | undefined): string {
  if (pw) return pawnNumber(pw.name) || (pw.display ?? key);
  return ai ? (ai.kind === 'main' ? 'main' : ai.name.replace(/^maid_chan_/, '')) : key;
}

const isOffScreen = (at: Pt, vw: number, vh: number) => at.x < -20 || at.y < -20 || at.x > vw + 20 || at.y > vh + 20;

/** The chest layer's selection: the selected chests and workbench. */
export interface ChestSelection {
  chests: readonly V3[];
  bench: V3 | null;
}

// ------------------------------------------------------------------ the painter

/**
 * The overlay's drawing state for one frame (the context, the camera matrices, the zoom in px per block, the level cut) with
 * the primitives and layers that use it. One painter lives as long as the map; {@link begin} re-aims it each frame.
 */
export class MapPainter {
  ctx!: CanvasRenderingContext2D;
  m!: ViewMats;
  /** Px per block. */
  cell = 0;
  cutY: number | null = null;
  angled = false;
  /** The camera's world position (the angled cube faces toward it). */
  cam: { x: number; y: number; z: number } = { x: 0, y: 0, z: 0 };
  vw = 0;
  vh = 0;
  /** World → canvas px with this frame's matrices (one closure for the painter's life). */
  readonly at = (x: number, y: number, z: number) => project(this.m, x, y, z);

  begin(
    ctx: CanvasRenderingContext2D,
    m: ViewMats,
    cell: number,
    cutY: number | null,
    angled: boolean,
    cam: { x: number; y: number; z: number },
    vw: number,
    vh: number,
  ): void {
    this.ctx = ctx;
    this.m = m;
    this.cell = cell;
    this.cutY = cutY;
    this.angled = angled;
    this.cam = cam;
    this.vw = vw;
    this.vh = vh;
  }

  /** A mark's y under the level cut (a cell at or above it is drawn at the cut). */
  private capY(y: number): number {
    return Math.min(y, this.cutY ?? 999);
  }

  private hiddenByCut(y: number): boolean {
    return this.cutY !== null && y >= this.cutY;
  }

  // ---------------------------------------------------------------- primitives

  quad(x1: number, z1: number, x2: number, z2: number, y: number): Pt[] | null {
    const a = this.at(x1, y, z1),
      b = this.at(x2, y, z1),
      cc = this.at(x2, y, z2),
      d = this.at(x1, y, z2);
    return a && b && cc && d ? [a, b, cc, d] : null;
  }

  poly(pts: Pt[]): void {
    const ctx = this.ctx;
    ctx.beginPath();
    ctx.moveTo(pts[0]!.x, pts[0]!.y);
    for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i]!.x, pts[i]!.y);
    ctx.closePath();
  }

  mark(id: string, at: Pt, color: string, size = 14): void {
    const ctx = this.ctx;
    const sp = markSprite(id, color, size);
    if (sp) {
      ctx.drawImage(sp, at.x - size / 2 - 3, at.y - size / 2 - 3, size + 6, size + 6);
      return;
    }
    ctx.save();
    ctx.translate(at.x - size / 2, at.y - size / 2);
    paintMark(ctx, id, color, size);
    ctx.restore();
  }

  /** A block cube: its top quad top-down; the top and the two faces toward the camera in the angled view. */
  cube(x: number, y: number, z: number, color: string, alpha: number, faint: boolean): void {
    if (this.angled) this.cubeAngled(x, y, z, color, alpha, faint);
    else this.cubeFlat(x, y, z, color, alpha, faint);
  }

  private cubeFlat(x: number, y: number, z: number, color: string, alpha: number, faint: boolean): void {
    const ctx = this.ctx;
    const q = this.quad(x, z, x + 1, z + 1, y + 1);
    if (!q) return;
    this.poly(q);
    if (faint) {
      ctx.strokeStyle = `${color}66`;
      ctx.lineWidth = 1;
      ctx.stroke();
      return;
    }
    ctx.globalAlpha = alpha;
    ctx.fillStyle = color;
    ctx.fill();
    ctx.globalAlpha = 1;
    ctx.strokeStyle = color;
    ctx.lineWidth = this.cell > 10 ? 1.2 : 0.6;
    ctx.stroke();
  }

  private cubeAngled(x: number, y: number, z: number, color: string, alpha: number, faint: boolean): void {
    const ctx = this.ctx;
    for (const f of cubeFaces(x, y, z, this.cam)) {
      const pts = f.map(([a, b, cc]) => this.at(a, b, cc));
      if (pts.some((q) => !q)) continue;
      this.poly(pts as Pt[]);
      if (!faint) {
        ctx.globalAlpha = alpha;
        ctx.fillStyle = color;
        ctx.fill();
        ctx.globalAlpha = 1;
      }
      ctx.strokeStyle = faint ? `${color}66` : color;
      ctx.lineWidth = 0.8;
      ctx.stroke();
    }
  }

  /** A text pill beside a point (white when ok, red when not). */
  label(at: Pt, text: string, ok: boolean): void {
    const ctx = this.ctx;
    ctx.font = '600 12px system-ui, sans-serif';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(text).width + 12;
    ctx.fillStyle = 'rgba(10,12,16,0.85)';
    ctx.fillRect(at.x + 14, at.y + 10, w, 20);
    ctx.fillStyle = ok ? '#f4f6f8' : '#ff8a8a';
    ctx.fillText(text, at.x + 20, at.y + 20);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
  }

  /** The selection square around a mark. */
  private selectBox(at: Pt): void {
    const ctx = this.ctx;
    ctx.strokeStyle = COLORS.select;
    ctx.lineWidth = 2;
    ctx.strokeRect(at.x - 11, at.y - 11, 22, 22);
  }

  private ring(at: Pt, r: number, color: string): void {
    const ctx = this.ctx;
    ctx.strokeStyle = color;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(at.x, at.y, r, 0, Math.PI * 2);
    ctx.stroke();
  }

  /** An X of half-size `s` around a point (stroked in the current style). */
  cross(at: Pt, s: number): void {
    const ctx = this.ctx;
    ctx.beginPath();
    ctx.moveTo(at.x - s, at.y - s);
    ctx.lineTo(at.x + s, at.y + s);
    ctx.moveTo(at.x + s, at.y - s);
    ctx.lineTo(at.x - s, at.y + s);
    ctx.stroke();
  }

  /** A glyph (no disc) at a corner of a body, scaled. */
  private smallGlyph(id: string, x: number, y: number, scale: number, color: string): void {
    const g = glyph(id);
    if (!g) return;
    const ctx = this.ctx;
    ctx.save();
    ctx.translate(x, y);
    ctx.scale(scale, scale);
    ctx.fillStyle = color;
    ctx.fill(g);
    ctx.restore();
  }

  // ---------------------------------------------------------------- the colony's layers

  /** The colony's home: a house mark in a ring. */
  drawHome(home: V3 | null): void {
    if (!home) return;
    const at = this.at(home[0] + 0.5, home[1] + 1, home[2] + 0.5);
    if (!at) return;
    this.mark('home', at, '#7fb8ff', 15);
    const ctx = this.ctx;
    ctx.strokeStyle = '#7fb8ff';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(at.x, at.y, 12, 0, Math.PI * 2);
    ctx.stroke();
  }

  /** Beds: a bed mark, the owner's colour ring and pawn number (or unnumbered owner's initials). */
  drawBeds(beds: readonly Bed[], pawns: readonly Pawn[] | undefined, selected: V3 | null | undefined): void {
    for (const bd of beds) {
      if ((bd.dim ?? 0) !== 0 || this.hiddenByCut(bd.pos[1])) continue;
      const at = this.at(bd.pos[0] + 0.5, bd.pos[1] + 0.6, bd.pos[2] + 0.5);
      if (!at) continue;
      const col = bd.pawn ? pawnColor(bd.pawn) : '#c9ccd2';
      this.mark('bed', at, col, 13);
      if (bd.pawn) this.bedTag(at, col, bedInitials(bd.pawn, pawns));
      if (selected && k3(selected) === k3(bd.pos)) this.selectBox(at);
    }
  }

  private bedTag(at: Pt, col: string, ini: string): void {
    const ctx = this.ctx;
    ctx.font = '700 9px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(ini).width + 6;
    ctx.fillStyle = col;
    ctx.fillRect(at.x + 7, at.y - 14, w, 11);
    ctx.fillStyle = '#0c1018';
    ctx.fillText(ini, at.x + 7 + w / 2, at.y - 8.5);
  }

  /** Build ghosts: translucent blocks by state; anything above the level cut only as a faint outline (x-rayed). */
  drawGhosts(ghosts: GhostBlocks): void {
    const sorted = this.angled ? [...ghosts].sort((a, b) => a[1] - b[1]) : ghosts;
    let drawn = 0;
    for (const g of sorted) {
      if (drawn++ > 3000) break;
      const state = g[6];
      if (state === 'done') continue;
      const col = COLORS.ghost[state] ?? COLORS.ghost.todo!;
      this.cube(g[0], g[1], g[2], col, state === 'leased' ? 0.5 : 0.32, this.hiddenByCut(g[1]));
    }
  }

  /** Failed build cells: a dashed red ring with a cross on each. */
  drawFailedBuildCells(blocks: GhostBlocks): void {
    const ctx = this.ctx;
    for (const f of failedModel(blocks).cells.slice(0, 2000)) {
      const at = this.at(f.cell[0] + 0.5, this.capY(f.cell[1] + 1), f.cell[2] + 0.5);
      if (!at || isOffScreen(at, this.vw, this.vh)) continue;
      ctx.strokeStyle = COLORS.danger;
      ctx.lineWidth = 2;
      ctx.setLineDash([3, 2]);
      ctx.beginPath();
      ctx.arc(at.x, at.y, 9, 0, Math.PI * 2);
      ctx.stroke();
      ctx.setLineDash([]);
      this.cross(at, 4);
    }
  }

  /** Designations: a mark on every target, rings on failing ones and on leased ones; returns where the failed rings went. */
  drawDesignations(model: DesModel): FailedRing[] {
    const failedOn: FailedRing[] = [];
    const markSize = this.cell > 18 ? 14 : 11;
    for (const mk of model.marks) {
      const b = mk.cell;
      const at = this.at(b[0] + 0.5, this.capY(b[1] + 1), b[2] + 0.5);
      if (!at || isOffScreen(at, this.vw, this.vh)) continue;
      this.mark(mk.d.kind, at, mk.col, markSize);
      if (mk.ring) {
        this.ring(at, 11, mk.ring);
        if (mk.failed)
          failedOn.push({ id: mk.d.id, cluster: model.clusterOf.get(String(mk.d.id)) ?? [mk.d.id], x: at.x, y: at.y });
      }
      if (mk.d.pawn) this.ring(at, 10, pawnColor(mk.d.pawn));
    }
    return failedOn;
  }

  /** Chests and workbenches: registered storage gets its mark and a priority pip; unregistered chests show faintly. */
  /** Badges (the hive's container summaries by block) show when zoomed in past `labelZoom`. */
  drawChests(
    cells: ReadonlyArray<{ pos: V3; id: number }>,
    st: StoryColonyState | undefined,
    selection: ChestSelection,
    badges: ReadonlyMap<string, ContainerSummary>,
    labelZoom: number,
  ): void {
    this.indexStorages(st, selection.chests);
    for (const { pos, id } of cells) {
      if (this.hiddenByCut(pos[1])) continue;
      const at = this.at(pos[0] + 0.5, pos[1] + 1, pos[2] + 0.5);
      if (!at || at.x < -100 || at.y < -40 || at.x > this.vw + 100 || at.y > this.vh + 40) continue;
      if (id === 54 || id === 146) this.drawChestCell(at, pos, st, this.cell > labelZoom ? badges : null);
      else this.drawBench(at, pos, st, selection.bench);
    }
  }

  /** Registered storages by each of their chest blocks (a double chest's both halves), the selected chests; this frame's. */
  private readonly storages = new Map<string, Storage>();
  private storageList: StoryColonyState['storages'] | undefined;
  private selectedChests: readonly V3[] | undefined;
  private readonly badgeCache = new ChestBadgeCache();
  private readonly selChests = new Set<string>();
  /** Storages already drawn this frame (a double chest draws once). */
  private readonly seenChests = new Set<string>();

  private indexStorages(st: StoryColonyState | undefined, selected: readonly V3[]): void {
    if (this.storageList !== st?.storages) {
      this.storageList = st?.storages;
      this.storages.clear();
      for (const s of st?.storages ?? []) {
        this.storages.set(k3(s.chest), s);
        if (s.doubleWith) this.storages.set(k3(s.doubleWith), s);
      }
    }
    if (this.selectedChests !== selected) {
      this.selectedChests = selected;
      this.selChests.clear();
      for (const c of selected) this.selChests.add(k3(c));
    }
    this.seenChests.clear();
  }

  private drawChestCell(
    at: Pt,
    pos: V3,
    st: StoryColonyState | undefined,
    badges: ReadonlyMap<string, ContainerSummary> | null,
  ): void {
    const s = this.storages.get(k3(pos));
    if (s && this.seenChests.has(k3(s.chest))) return;
    if (s) this.seenChests.add(k3(s.chest));
    this.drawChest(at, s);
    if (badges) this.chestBadgeAt(at, pos, s, badges.get(k3(s ? s.chest : pos)), st?.categories);
    if (this.isSelectedChest(pos, s)) this.selectBox(at);
  }

  private isSelectedChest(pos: V3, s: Storage | undefined): boolean {
    return this.selChests.has(k3(pos)) || (!!s && this.selChests.has(k3(s.chest)));
  }

  private drawChest(at: Pt, s: Storage | undefined): void {
    const ctx = this.ctx;
    ctx.globalAlpha = s ? 1 : 0.45;
    this.mark('chest', at, COLORS.chest, 13);
    ctx.globalAlpha = 1;
    if (s) this.priorityPips(at, s.priority);
  }

  private priorityPips(at: Pt, priority: string): void {
    const ctx = this.ctx;
    const pri = STORAGE_PRIORITIES.indexOf(priority);
    for (let i = 0; i <= Math.max(0, pri); i++) {
      ctx.fillStyle = pri >= 3 ? '#ff9f43' : '#ffd166';
      ctx.fillRect(at.x + 9, at.y + 5 - i * 4, 3, 3);
    }
  }

  /** One badge per container (a double chest's first half). */
  private chestBadgeAt(
    at: Pt,
    pos: V3,
    s: Storage | undefined,
    known: ContainerSummary | undefined,
    categories: StoryColonyState['categories'],
  ): void {
    if (!(s || !known || k3(known.pos) === k3(pos))) return;
    const badge = this.badgeCache.get(k3(pos), s, known, categories);
    if (badge) drawChestBadge(this.ctx, at, badge);
  }

  private drawBench(at: Pt, pos: V3, st: StoryColonyState | undefined, selected: V3 | null): void {
    const ctx = this.ctx;
    const bench = (st?.workbenches ?? []).find((w) => k3(w.pos) === k3(pos));
    this.mark('bench', at, COLORS.bench, 13);
    const n = bench?.bills.length ?? 0;
    if (n) {
      ctx.fillStyle = '#9ec7ff';
      ctx.beginPath();
      ctx.arc(at.x + 10, at.y - 9, 6, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = '#0c1220';
      ctx.font = '600 9px system-ui, sans-serif';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(n), at.x + 10, at.y - 9);
    }
    if (selected && k3(selected) === k3(pos)) this.selectBox(at);
  }

  /** The selected pawns' order lines: dashed from the body through its job and queued targets (six at most). */
  drawOrderLines(list: readonly MapBody[], sel: ReadonlySet<string>, pawns: readonly Pawn[] | undefined): void {
    const ctx = this.ctx;
    for (const b of list) {
      if (!sel.has(b.key) || !b.at) continue;
      const targets = orderTargets(pawns?.find((x) => x.name === b.key));
      let from = b.at;
      ctx.strokeStyle = COLORS.order;
      ctx.lineWidth = 1.5;
      ctx.setLineDash([5, 4]);
      for (const t of targets.slice(0, 6)) {
        const at = this.at(t[0] + 0.5, t[1] + 0.2, t[2] + 0.5);
        if (!at) continue;
        ctx.beginPath();
        ctx.moveTo(from.x, from.y);
        ctx.lineTo(at.x, at.y);
        ctx.stroke();
        ctx.strokeRect(at.x - 4, at.y - 4, 8, 8);
        from = at;
      }
      ctx.setLineDash([]);
    }
  }

  /** The bodies' tokens (or the marks around a body drawn as its model), job marks and name tags. */
  drawBodies(list: readonly MapBody[], sel: ReadonlySet<string>, st: StoryColonyState | undefined): void {
    const tokenR = Math.max(7, Math.min(13, this.cell * 0.55));
    const names = getViewerQuality().labels;
    const ctx = this.ctx;
    ctx.font = '600 11px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (const b of list) {
      if (!b.at) continue;
      const pw = b.kind === 'pawn' ? st?.pawns.find((x) => x.name === b.key) : undefined;
      const ai = b.kind === 'ai' ? st?.ai?.find((x) => x.name === b.key) : undefined;
      this.drawBody(b, b.at, pw, ai, sel.has(b.key), tokenR, names);
    }
  }

  private drawBody(
    b: MapBody,
    at: Pt,
    pw: Pawn | undefined,
    ai: AiBody | undefined,
    selected: boolean,
    tokenR: number,
    names: boolean,
  ): void {
    const ctx = this.ctx;
    const color = b.kind === 'ai' ? COLORS.ai : pawnColor(b.key);
    const { x, y } = at;
    // A body drawn as its model gets marks around it (no token over it); a storey above the cut shows a faint token.
    const model = b.look === 'model';
    const r = model ? Math.max(tokenR, b.r - 4) : tokenR;
    ctx.globalAlpha = b.look === 'cut' ? 0.35 : 1;
    if (selected) this.brackets(x, y, r + 5);
    if (model) this.feetRing(b.pos, color);
    else this.token(x, y, r, color);
    if (pw) this.pawnMarks(pw, x, y, r);
    const danger = inDanger(pw);
    // Name tags (the viewer settings): off leaves the names of the selected bodies and those in danger.
    if (names || selected || danger) this.nameTag(bodyLabel(b.key, pw, ai), x, y + r, danger, b.kind);
    ctx.globalAlpha = 1;
  }

  /** A pawn's corner marks: drafted (top right), its job (top left). */
  private pawnMarks(pw: Pawn, x: number, y: number, r: number): void {
    if (pw.drafted) this.smallGlyph('drafted', x + r - 4, y - r - 6, 0.75, '#ff9f43');
    const job = pw.job?.type;
    if (job && GLYPH_OF[job])
      this.smallGlyph(GLYPH_OF[job], x - r - 8, y - r - 6, 0.7, COLORS.designation[job] ?? '#ffffff');
  }

  /** Corner brackets (the genre's selection mark, our own drawing). */
  private brackets(x: number, y: number, s: number): void {
    const ctx = this.ctx;
    ctx.strokeStyle = COLORS.select;
    ctx.lineWidth = 2;
    ctx.beginPath();
    for (const [sx, sy] of BRACKET_CORNERS) {
      ctx.moveTo(x + sx * s, y + sy * (s - 5));
      ctx.lineTo(x + sx * s, y + sy * s);
      ctx.lineTo(x + sx * (s - 5), y + sy * s);
    }
    ctx.stroke();
  }

  /** The body's colour as a ring on the ground at its feet. */
  private feetRing(pos: V3, color: string): void {
    const f = this.at(pos[0], pos[1] + 0.02, pos[2]),
      fx = this.at(pos[0] + 0.5, pos[1] + 0.02, pos[2]),
      fz = this.at(pos[0], pos[1] + 0.02, pos[2] + 0.5);
    if (!f || !fx || !fz) return;
    const ctx = this.ctx;
    ctx.strokeStyle = color;
    ctx.lineWidth = 2.5;
    ctx.beginPath();
    ctx.ellipse(
      f.x,
      f.y,
      Math.max(4, Math.hypot(fx.x - f.x, fx.y - f.y)),
      Math.max(2, Math.hypot(fz.x - f.x, fz.y - f.y)),
      0,
      0,
      Math.PI * 2,
    );
    ctx.stroke();
  }

  private token(x: number, y: number, r: number, color: string): void {
    const ctx = this.ctx;
    ctx.fillStyle = 'rgba(10,12,16,0.78)';
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = color;
    ctx.lineWidth = 3;
    ctx.stroke();
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, r * 0.42, 0, Math.PI * 2);
    ctx.fill();
  }

  private nameTag(label: string, x: number, y: number, danger: boolean, kind: MapBody['kind']): void {
    const ctx = this.ctx;
    const tw = ctx.measureText(label).width;
    ctx.fillStyle = 'rgba(10,12,16,0.7)';
    ctx.fillRect(x - tw / 2 - 4, y + 3, tw + 8, 15);
    ctx.fillStyle = danger ? COLORS.danger : kind === 'ai' ? '#d9ccff' : '#f4f6f8';
    ctx.fillText(label, x, y + 4);
  }

  /** The white outline of the hovered cell's top. */
  drawCellOutline(hc: V3): void {
    const q = this.quad(hc[0], hc[2], hc[0] + 1, hc[2] + 1, hc[1] + 1.02);
    if (!q) return;
    this.poly(q);
    this.ctx.strokeStyle = '#ffffffaa';
    this.ctx.lineWidth = 1;
    this.ctx.stroke();
  }
}

/** A cube's faces toward the camera: the top always, then the x side and the z side it looks at (if any). */
function cubeFaces(
  x: number,
  y: number,
  z: number,
  cam: { x: number; z: number },
): Array<Array<[number, number, number]>> {
  const faces: Array<Array<[number, number, number]>> = [
    [
      [x, y + 1, z],
      [x + 1, y + 1, z],
      [x + 1, y + 1, z + 1],
      [x, y + 1, z + 1],
    ],
  ];
  const xFace = cam.x > x + 1 ? x + 1 : cam.x < x ? x : null;
  if (xFace !== null)
    faces.push([
      [xFace, y, z],
      [xFace, y + 1, z],
      [xFace, y + 1, z + 1],
      [xFace, y, z + 1],
    ]);
  const zFace = cam.z > z + 1 ? z + 1 : cam.z < z ? z : null;
  if (zFace !== null)
    faces.push([
      [x, y, zFace],
      [x + 1, y, zFace],
      [x + 1, y + 1, zFace],
      [x, y + 1, zFace],
    ]);
  return faces;
}
