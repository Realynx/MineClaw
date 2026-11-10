/**
 * Story Mode tools: the designate palette (drag a grid rectangle, every matching target lights up with a live count,
 * release designates), zones, the Architect's structure and furniture tools (placement preview, wall height, roof style,
 * facing, levels, face snapping in the angled view), the right-click order menu, and the keyboard map. Pure, unit-tested.
 */
import {
  ANIMAL_TYPES,
  CHEST_IDS,
  countTrees,
  CUT_IDS,
  expandBuildItem,
  isWaterTool,
  LOG_IDS,
  materialKey,
  matchesTool,
  type StoryArea,
  type StoryBuildItem,
  type StorySpeed,
  type StoryDesignationKind,
  type StoryDirectiveSpec,
  type StoryGearOp,
  type StoryFacing,
  type StoryMaterial,
  type StoryMineFilter,
  type StoryOrderSpec,
  type StoryRoofStyle,
  type StoryZoneKind,
  waterItems,
  waterToolCells,
  type WaterToolId,
} from '@mineclaw/contracts';
import { hasFacing, rotateMeta, withFacing, type Dir } from '../studio/facing';

type V3 = [number, number, number];

// ------------------------------------------------------------------ designate tools

export type DesignateId =
  | 'chop'
  | 'cut'
  | 'harvest'
  | 'mine'
  | 'haul'
  | 'hunt'
  | 'deconstruct'
  | 'cancel'
  | 'retry'
  | 'forbid'
  | 'allow';

export interface DesignateTool {
  id: DesignateId;
  label: string;
  /** Hotkey (KeyboardEvent.code). */
  key: string;
  hint: string;
  /** Matches entities (drops, animals) instead of blocks. */
  entities?: 'item' | 'animal';
  /** What the count says ("14 trees"). */
  noun: [string, string];
}

export const DESIGNATE_TOOLS: readonly DesignateTool[] = [
  { id: 'chop', label: 'Chop wood', key: 'KeyC', hint: 'Fell every tree in the area (whole trees)', noun: ['tree', 'trees'] },
  { id: 'cut', label: 'Cut plants', key: 'KeyG', hint: 'Clear tall grass, flowers, ferns, saplings', noun: ['plant', 'plants'] },
  { id: 'harvest', label: 'Harvest', key: 'KeyH', hint: 'Ripe crops, melons, pumpkins, cane (replanted)', noun: ['crop', 'crops'] },
  { id: 'mine', label: 'Mine', key: 'KeyM', hint: 'Blocks at this level (filter on the options bar)', noun: ['block', 'blocks'] },
  { id: 'haul', label: 'Haul', key: 'KeyL', hint: 'Carry every dropped item to storage', entities: 'item', noun: ['item', 'items'] },
  { id: 'hunt', label: 'Hunt', key: 'KeyN', hint: 'Hunt the animals in the area', entities: 'animal', noun: ['animal', 'animals'] },
  { id: 'deconstruct', label: 'Deconstruct', key: 'KeyK', hint: 'Take apart placed blocks', noun: ['block', 'blocks'] },
  { id: 'cancel', label: 'Cancel', key: 'KeyZ', hint: 'Remove designations in the area', noun: ['designation', 'designations'] },
  { id: 'retry', label: 'Retry failed', key: 'KeyO', hint: 'Try failed or resting designations and failed build cells in the area again now', noun: ['designation', 'designations'] },
  { id: 'forbid', label: 'Forbid', key: 'KeyF', hint: 'Pawns leave these items and doors alone', entities: 'item', noun: ['thing', 'things'] },
  { id: 'allow', label: 'Allow', key: 'KeyJ', hint: 'Undo a forbid', entities: 'item', noun: ['thing', 'things'] },
];

export const MINE_FILTERS: ReadonlyArray<{ id: StoryMineFilter; label: string }> = [
  { id: 'all', label: 'Everything' },
  { id: 'ores', label: 'Ores only' },
  { id: 'stone', label: 'Stone only' },
  { id: 'soil', label: 'Dirt, gravel, sand' },
];

/** The y span a tool scans at a level: surface tools take everything visible, mine and deconstruct one storey. */
export function toolYRange(tool: DesignateId, level: number, height: number, cutY: number | null): [number, number] {
  const top = cutY !== null ? cutY - 1 : level + 40;
  if (tool === 'mine') return [level - 1, Math.min(top, level + height)];
  if (tool === 'deconstruct') return [level, Math.min(top, level + height + 1)];
  return [level - 8, top];
}

// ------------------------------------------------------------------ height modes (mine, deconstruct)

/**
 * How deep a Mine or Deconstruct drag goes, the colony-sim way: the vertical extent is explicit, and the drag designates exactly
 * the cells its preview shows (sent as `blocks`, never an area the director could expand deeper).
 * - surface: the top block of each column (trees, plants and snow looked through) and the plants on it: one layer.
 * - level: only blocks at the working level (PgUp / PgDn).
 * - down: from the surface down to and including the working level (terrace down to a height).
 * - flatten: everything above the working level, plants on it included: a flat floor at the level.
 * - layers: N blocks deep from the surface (1–8).
 */
export type HeightMode = 'surface' | 'level' | 'down' | 'flatten' | 'layers';

export const HEIGHT_MODES: ReadonlyArray<{ id: HeightMode; label: string; key: string; hint: string }> = [
  { id: 'surface', label: 'Surface', key: 'Digit1', hint: 'The top block of each column and the plants on it: one layer' },
  { id: 'level', label: 'Level', key: 'Digit2', hint: 'Only blocks at the working level (PgUp / PgDn)' },
  { id: 'down', label: 'Down to level', key: 'Digit3', hint: 'From the surface down to and including the working level' },
  { id: 'flatten', label: 'Flatten here', key: 'Digit4', hint: 'Everything above the working level: flat ground at the level' },
  { id: 'layers', label: 'Layers', key: 'Digit5', hint: 'N blocks deep from the surface' },
];

/** The default mode per tool: Mine takes the surface (one layer, no surprises); Deconstruct the building down to its floor. */
export const DEFAULT_HEIGHT: Record<'mine' | 'deconstruct', HeightMode> = { mine: 'surface', deconstruct: 'down' };

/** Tools whose drag has a height mode. */
export function hasHeight(tool: DesignateId): tool is 'mine' | 'deconstruct' {
  return tool === 'mine' || tool === 'deconstruct';
}

/** At most this many cells per drag (the director's per-request limit). */
export const MAX_DRAG_CELLS = 4096;
/** How far below the working level a Surface or Layers drag looks for the top of a column (a pit, a ravine). */
export const SURFACE_REACH = 32;
/** With no level cut, the top of a column is searched this far above the working level. */
export const TOP_REACH = 64;

export function clampLayers(n: number): number {
  return Math.max(1, Math.min(8, Math.round(n)));
}

/** What a column's top looks through (not ground): logs, leaves, plants, snow, crops, cane, vines, torches' neighbours. */
export const NOT_GROUND_IDS: readonly number[] = [
  ...LOG_IDS, 18, 161, 6, 31, 32, 37, 38, 39, 40, 175, 78, 59, 83, 106, 111, 141, 142, 207, 81, 115,
];
/** On a column's top, these go with it (cut or dug): plants, flowers, saplings, mushrooms, snow. */
const ON_TOP_IDS: ReadonlySet<number> = new Set([6, 31, 32, 37, 38, 39, 40, 175, 78]);

export interface HeightQuery {
  /** The y span the column tops are searched in (the bottom is never gone below). */
  min: number;
  max: number;
  /** Cells taken from the top down. */
  depth: number;
  /** Cells over the top to look at (plants on it). */
  above: number;
}

/** The `stacks` query a height mode needs at a level (`cutY` = the x-ray cut: Surface and Layers see nothing at or over it;
 * Down to level and Flatten here take the whole column above the level either way). */
export function heightQuery(mode: HeightMode, level: number, layers: number, cutY: number | null): HeightQuery {
  const top = cutY !== null ? cutY - 1 : level + TOP_REACH;
  switch (mode) {
    case 'surface':
      return { min: level - SURFACE_REACH, max: top, depth: 1, above: 2 };
    case 'layers':
      return { min: level - SURFACE_REACH, max: top, depth: clampLayers(layers), above: 2 };
    case 'level':
      return { min: level, max: level, depth: 1, above: 0 };
    case 'down':
    case 'flatten':
      // Everything above the level, x-rayed or not: the ground comes out flat at the level, not at the cut.
      return { min: level, max: level + TOP_REACH, depth: TOP_REACH + 1, above: 2 };
  }
}

export interface HeightTargets {
  cells: V3[];
  /** Columns with at least one cell. */
  columns: number;
  /** The highest and lowest designated y (null when nothing). */
  top: number | null;
  bottom: number | null;
  /** The most cells in one column (the layers a drag takes). */
  layers: number;
  /** Over {@link MAX_DRAG_CELLS}: the drag designates nothing. */
  tooMany: boolean;
}

/** The running scan of a `stacks` answer: the column being read and the totals so far. */
interface ColumnScan {
  colKey: string;
  colTop: number | null;
  colCount: number;
  layers: number;
  columns: number;
  top: number | null;
  bottom: number | null;
}

/** Closes the column being read (a column counts when it gave a cell). */
function flushColumn(s: ColumnScan): void {
  if (s.colCount > 0) s.columns++;
  s.layers = Math.max(s.layers, s.colCount);
}

/** Takes a cell: into the list, the column's count and the y range. */
function addCell(s: ColumnScan, cells: V3[], x: number, y: number, z: number): void {
  cells.push([x, y, z]);
  s.colCount++;
  s.top = s.top === null ? y : Math.max(s.top, y);
  s.bottom = s.bottom === null ? y : Math.min(s.bottom, y);
}

/** Whether a tool takes a block: over the column's top only a plant (or snow) standing on it, with Mine and no filter. */
function takesBlock(
  tool: 'mine' | 'deconstruct',
  filter: StoryMineFilter,
  overTop: boolean,
  id: number,
  meta: number,
  below: number,
): boolean {
  // Over the column's top: a plant (or snow) standing on it goes with it; trees stay (Chop takes them).
  if (overTop) return tool === 'mine' && ON_TOP_IDS.has(id) && filter === 'all';
  return matchesTool(tool, id, meta, below, filter);
}

/** The height mode's cut at the working level (Surface and Layers take the depth the query asked for). */
function inModeRange(mode: HeightMode, y: number, level: number): boolean {
  if (mode === 'level') return y === level;
  if (mode === 'down') return y >= level;
  return mode === 'flatten' ? y > level : true;
}

/**
 * The cells a height-mode drag designates, from a `stacks` answer ([x, y, z, id, meta, below, …], each column from the top
 * down). Never air, never under the mode's bottom, plants only where they stand on the column's top.
 */
export function heightTargets(
  tool: 'mine' | 'deconstruct',
  mode: HeightMode,
  stacks: readonly number[],
  level: number,
  filter: StoryMineFilter = 'all',
): HeightTargets {
  const cells: V3[] = [];
  const s: ColumnScan = { colKey: '', colTop: null, colCount: 0, layers: 0, columns: 0, top: null, bottom: null };
  const skip = new Set(NOT_GROUND_IDS);
  for (let i = 0; i + 5 < stacks.length + 1; i += 6) {
    const x = stacks[i]!, y = stacks[i + 1]!, z = stacks[i + 2]!, id = stacks[i + 3]!;
    const key = `${x},${z}`;
    if (key !== s.colKey) {
      flushColumn(s);
      s.colKey = key;
      s.colTop = null;
      s.colCount = 0;
    }
    if (id === 0) continue;
    const overTop = s.colTop === null && skip.has(id);
    if (!overTop && s.colTop === null) s.colTop = y;
    if (!takesBlock(tool, filter, overTop, id, stacks[i + 4]!, stacks[i + 5]!) || !inModeRange(mode, y, level)) continue;
    addCell(s, cells, x, y, z);
  }
  flushColumn(s);
  return { cells, columns: s.columns, top: s.top, bottom: s.bottom, layers: s.layers, tooMany: cells.length > MAX_DRAG_CELLS };
}

/** The HUD line while dragging: "Mine (Surface): y 64 → 62 · 3 layers · 48 blocks". */
export function heightLabel(toolLabel: string, mode: HeightMode, t: HeightTargets, layersN?: number): string {
  const m = HEIGHT_MODES.find((h) => h.id === mode)!;
  const name = mode === 'layers' ? `${m.label}: ${clampLayers(layersN ?? 1)}` : m.label;
  const head = `${toolLabel} (${name})`;
  if (!t.cells.length || t.top === null || t.bottom === null) return `${head}: nothing here`;
  const span = t.top === t.bottom ? `y ${t.top}` : `y ${t.top} → ${t.bottom}`;
  const n = t.cells.length;
  const tail = t.tooMany ? ` · too many (max ${MAX_DRAG_CELLS}): make the box smaller` : '';
  return `${head}: ${span} · ${t.layers} layer${t.layers === 1 ? '' : 's'} · ${n} block${n === 1 ? '' : 's'}${tail}`;
}

/** The designations request for exactly these cells (a height-mode drag). */
export function heightBody(tool: 'mine' | 'deconstruct', cells: readonly V3[], filter?: StoryMineFilter): Record<string, unknown> {
  return {
    op: 'add',
    kind: tool,
    blocks: cells.slice(0, MAX_DRAG_CELLS).map((c) => [c[0], c[1], c[2]]),
    ...(tool === 'mine' && filter && filter !== 'all' ? { filter } : {}),
  };
}

/** The bounding box of some cells (a directive's area). */
export function cellsArea(cells: readonly V3[], fallback: StoryArea): StoryArea {
  if (!cells.length) return fallback;
  let x1 = Infinity, y1 = Infinity, z1 = Infinity, x2 = -Infinity, y2 = -Infinity, z2 = -Infinity;
  for (const [x, y, z] of cells) {
    x1 = Math.min(x1, x); y1 = Math.min(y1, y); z1 = Math.min(z1, z);
    x2 = Math.max(x2, x); y2 = Math.max(y2, y); z2 = Math.max(z2, z);
  }
  return [x1, y1, z1, x2, y2, z2];
}

// ------------------------------------------------------------------ cancelling a drag

/**
 * One map drag's life, the genre's way: Esc, or a press of the other button (right during a left drag, left during a right
 * drag such as a formation line), cancels it; the release after a cancel does nothing, and neither does the click that
 * cancelled it. A middle drag (panning) is never cancelled. Pure, so the map and the tests share it.
 */
export class DragGate {
  /** The button that started the drag (null = none). */
  button: number | null = null;
  cancelled = false;

  start(button: number): void {
    this.button = button;
    this.cancelled = false;
  }

  get active(): boolean {
    return this.button !== null && !this.cancelled;
  }

  /** Another button went down during the drag (pointer events report it as a chorded move). True = it cancels the drag. */
  chord(button: number): boolean {
    if (!this.active || button === this.button || button === 1 || this.button === 1) return false;
    this.cancelled = true;
    return true;
  }

  /** Esc during the drag. True = it cancelled one (the key goes no further). */
  escape(): boolean {
    if (!this.active || this.button === 1) return false;
    this.cancelled = true;
    return true;
  }

  /** A button came up with `buttons` still held: 'act' = the drag's release does its thing, 'ignore' = it was cancelled
   * (or there was none), 'wait' = a cancelled drag still has a button down. */
  release(buttons: number): 'act' | 'ignore' | 'wait' {
    if (this.button === null) return 'ignore';
    if (this.cancelled) {
      if (buttons !== 0) return 'wait';
      this.button = null;
      this.cancelled = false;
      return 'ignore';
    }
    this.button = null;
    return 'act';
  }
}

/**
 * Releases still waiting for the worker (a click's scan, a pick in the angled view) before they act. A new press does not
 * cancel them, so quick clicks each send their command; Esc, a press of the other button (never the middle one), leaving
 * full screen, and a tool, view, level or build change do. Each waiting release keeps what it drew (its rectangle) until
 * it has acted or was cancelled. Pure, so the map and the tests share it.
 */
export class ReleaseGuard<T = unknown> {
  private seq = 0;
  private readonly held = new Map<number, { button: number; shown: T | null }>();

  /** A button came up: the release starts waiting. `shown` is what it keeps drawn meanwhile. */
  begin(button: number, shown: T | null = null): number {
    const ticket = ++this.seq;
    this.held.set(ticket, { button, shown });
    return ticket;
  }

  /** The release may still act. */
  current(ticket: number): boolean {
    return this.held.has(ticket);
  }

  /** The release acted (or ended without acting): it no longer waits or draws. */
  finish(ticket: number): void {
    this.held.delete(ticket);
  }

  get waiting(): boolean {
    return this.held.size > 0;
  }

  /** Every waiting release ends without acting. True when one was waiting. */
  cancel(): boolean {
    if (!this.held.size) return false;
    this.held.clear();
    return true;
  }

  /** A new press: it cancels the waiting releases only when it is the other button. True when it cancelled them. */
  press(button: number): boolean {
    if (button === 1) return false;
    for (const h of this.held.values()) if (h.button !== button && h.button !== 1) return this.cancel();
    return false;
  }

  /** What the waiting releases draw, oldest first. */
  shown(): T[] {
    const out: T[] = [];
    for (const h of this.held.values()) if (h.shown !== null) out.push(h.shown);
    return out;
  }
}

/**
 * Whether a released drag does nothing: a zone or storage box needs a real drag (a click, under the drag threshold, paints
 * nothing), and a designate drag with nothing under it posts nothing.
 */
export function releaseIsNoop(kind: 'designate' | 'zone' | 'storage' | 'other', moved: boolean, count: number): boolean {
  if (kind === 'zone' || kind === 'storage') return !moved;
  if (kind === 'designate') return count === 0;
  return false;
}

/** The area a drag designates (grid rect + the tool's y span). */
export function toolArea(rect: readonly [number, number, number, number], y: readonly [number, number]): StoryArea {
  return [rect[0], y[0], rect[1], rect[2], y[1], rect[3]];
}

/** The most hits a designate preview scans for; a scan that reaches it may have missed targets. */
export const PREVIEW_HITS = 6000;

/** Whether a preview scan ([x, y, z, id, meta, below, …]) stopped at {@link PREVIEW_HITS}. */
export const scanTruncated = (scan: readonly number[]): boolean => scan.length >= PREVIEW_HITS * 6;

/** Block ids a tool's preview scans for (empty = every non-air block, filtered by `matchesTool`). */
export function scanIds(tool: DesignateId): number[] {
  switch (tool) {
    case 'chop':
      return [...LOG_IDS];
    case 'cut':
      return [...CUT_IDS];
    case 'harvest':
      return [59, 141, 142, 207, 115, 103, 86, 83, 81, 127];
    case 'forbid':
    case 'allow':
      return [64, 71, 96, 107];
    default:
      return [];
  }
}

export interface PreviewHit {
  x: number;
  y: number;
  z: number;
}

/**
 * Turns a scan ([x, y, z, id, meta, below, …]) into the preview: highlighted cells and the count the tool reports (trees for
 * chop, blocks otherwise).
 */
export function previewTargets(tool: DesignateId, scan: readonly number[], filter: StoryMineFilter = 'all'): { cells: V3[]; count: number } {
  if (tool === 'cancel' || tool === 'retry' || tool === 'haul' || tool === 'hunt') return { cells: [], count: 0 };
  const cells: V3[] = [];
  const kind = tool as StoryDesignationKind;
  for (let i = 0; i + 5 < scan.length + 1; i += 6) {
    const [x, y, z, id, meta, below] = [scan[i]!, scan[i + 1]!, scan[i + 2]!, scan[i + 3]!, scan[i + 4]!, scan[i + 5]!];
    if (matchesTool(kind, id, meta, below, filter)) cells.push([x, y, z]);
  }
  return { cells, count: tool === 'chop' ? countTrees(cells) : cells.length };
}

export interface EntityLite {
  id: number;
  kind: string;
  type?: number;
  x: number;
  y: number;
  z: number;
}

/** Drops (haul, forbid) or animals (hunt) inside a grid rect. */
export function entitiesIn(tool: DesignateTool, rect: readonly [number, number, number, number], ents: readonly EntityLite[]): EntityLite[] {
  if (!tool.entities) return [];
  return ents.filter((e) => {
    const ok = tool.entities === 'item' ? e.kind === 'item' : e.kind === 'mob' && ANIMAL_TYPES.has(e.type ?? -1);
    const x = Math.floor(e.x), z = Math.floor(e.z);
    return ok && x >= rect[0] && x <= rect[2] && z >= rect[1] && z <= rect[3];
  });
}

/** What a designate drag's preview found: its cells, and whether its scan stopped at the hit cap. */
export interface DragPreview {
  cells: readonly V3[];
  truncated?: boolean;
}

/** Tools that send exactly the cells their preview showed. Chop keeps its area (whole trees, found once each by the
 * colony's area scan); Haul, Hunt and Forbid act on entities. */
const BY_CELLS: ReadonlySet<DesignateId> = new Set(['cut', 'harvest']);

/** The designations request a released drag sends: Cut and Harvest by the previewed cells when the preview holds all of
 * them (1 to {@link MAX_DRAG_CELLS}, scan not cut short), any other tool by the area. */
export function designationBody(
  tool: DesignateId,
  area: StoryArea,
  filter?: StoryMineFilter,
  preview?: DragPreview,
): Record<string, unknown> {
  if (tool === 'cancel') return { op: 'remove', area };
  const n = preview?.cells.length ?? 0;
  const exact = BY_CELLS.has(tool) && !preview?.truncated && n >= 1 && n <= MAX_DRAG_CELLS;
  return {
    op: 'add',
    kind: tool,
    ...(exact ? { blocks: preview!.cells.map((c) => [c[0], c[1], c[2]]) } : { area }),
    ...(tool === 'mine' && filter && filter !== 'all' ? { filter } : {}),
  };
}

/** The same task as a directive for AI bodies. */
export function designationDirective(tool: DesignateId, area: StoryArea, targets: readonly V3[], filter?: StoryMineFilter): StoryDirectiveSpec | null {
  if (tool === 'cancel' || tool === 'retry' || tool === 'forbid' || tool === 'allow') return null;
  return { kind: tool, area, ...(targets.length ? { targets: targets.slice(0, 512) } : {}), ...(tool === 'mine' && filter ? { filter } : {}) };
}

export function countLabel(tool: DesignateTool, n: number): string {
  return `${tool.label}: ${n} ${n === 1 ? tool.noun[0] : tool.noun[1]}`;
}

// ------------------------------------------------------------------ zones

export const ZONE_TOOLS: ReadonlyArray<{ id: StoryZoneKind; label: string; hint: string; color: string }> = [
  { id: 'home', label: 'Home area', hint: 'Where pawns rest and keep things tidy', color: '#4f9cf0' },
  { id: 'guard',
    label: 'Guard',
    hint: 'Available pawns with Guard enabled attack visible mobs here, including animals; players are excluded',
    color: '#f0ad4f',
  },
  { id: 'farm', label: 'Growing zone', hint: 'Pawns plant, tend and harvest here', color: '#7cc35b' },
  { id: 'chop', label: 'Woodlot', hint: 'Trees here are cut and replanted', color: '#b98a4e' },
  { id: 'mine', label: 'Quarry', hint: 'Pawns dig everything down to bedrock (a spiral stair), or only the ores', color: '#9aa3ad' },
  { id: 'fish', label: 'Fishing zone', hint: 'Pawns fish here with rods (still water only)', color: '#3fb8d9' },
  { id: 'forbidden', label: 'Keep out', hint: 'Pawns never walk here', color: '#e0525a' },
  { id: 'dump', label: 'Drop spot', hint: 'Loot goes here while the colony has no storage', color: '#c8a46a' },
  {
    id: 'preserve',
    label: 'Preserve',
    hint: 'Pawns keep these blocks as they are and repair any change',
    color: '#e879c9',
  },
];

/** The colour of a preserved region's outline on the map (a Preserve zone's, or any zone with Preserve on). */
export const PRESERVE_COLOR = '#e879c9';

export function zoneColor(kind: string): string {
  return ZONE_TOOLS.find((z) => z.id === kind)?.color ?? '#c9a3ff';
}

// ------------------------------------------------------------------ the Architect

export type StructureId = 'wall' | 'floor' | 'roof' | 'door' | 'window' | 'stairs' | 'fence' | 'block' | 'remove';
export type FurnitureId = 'crafting_table' | 'furnace' | 'chest' | 'bed' | 'torch';
export type BuildToolId = StructureId | `furniture:${FurnitureId}` | WaterToolId;

export const STRUCTURE_TOOLS: ReadonlyArray<{ id: StructureId; label: string; hint: string; drag: 'rect' | 'line' | 'fill' | 'point' | 'box' }> = [
  { id: 'wall', label: 'Wall', hint: 'Drag a rectangle (Alt: a straight line)', drag: 'rect' },
  { id: 'floor', label: 'Floor', hint: 'Drag to fill', drag: 'fill' },
  { id: 'roof', label: 'Roof', hint: 'Drag over the room; sits on top of the walls', drag: 'fill' },
  { id: 'door', label: 'Door', hint: 'Click a wall cell', drag: 'point' },
  { id: 'window', label: 'Window', hint: 'Click wall cells (drag for a row)', drag: 'line' },
  { id: 'stairs', label: 'Stairs', hint: 'Drag a run; Q/E turns', drag: 'line' },
  { id: 'fence', label: 'Fence', hint: 'Drag a rectangle', drag: 'rect' },
  { id: 'block', label: 'Single block', hint: 'Click, or drag a box; Q/E turns a block that faces', drag: 'box' },
  { id: 'remove', label: 'Deconstruct', hint: 'Drag a box to clear', drag: 'box' },
];

/** The Architect's Water tab (docs/web/story-mode.md "Water"): pawns pour sources from buckets. Lava only while the colony allows it. */
export const WATER_TOOLS: ReadonlyArray<{ id: WaterToolId; label: string; hint: string; drag: 'point' | 'fill' }> = [
  { id: 'water', label: 'Water source', hint: 'Click a cell: one still water source (indoors too)', drag: 'point' },
  { id: 'water_channel', label: 'Water channel', hint: 'Drag a line or an area: every cell a source (a canal, a hydroponic tray)', drag: 'fill' },
  { id: 'infinite_water', label: 'Infinite source', hint: 'Click a corner: a 2×2 pool that refills (pawns fill their buckets there)', drag: 'point' },
  { id: 'lava', label: 'Lava source', hint: 'Click a cell: one lava source (never at home or beside wood)', drag: 'point' },
];

/** Whether a water tool digs its cells into the ground first (a trench, a sunken pool): the option, else on for the channel and
 * the infinite source, off for a single source. */
export function waterDig(tool: WaterToolId, o: Pick<BuildOpts, 'dig'>): boolean {
  return o.dig ?? (tool === 'water_channel' || tool === 'infinite_water');
}

export const FURNITURE_TOOLS: ReadonlyArray<{ id: FurnitureId; label: string }> = [
  { id: 'crafting_table', label: 'Crafting table' },
  { id: 'furnace', label: 'Furnace' },
  { id: 'chest', label: 'Chest' },
  { id: 'bed', label: 'Bed' },
  { id: 'torch', label: 'Torch' },
];

export interface BuildOpts {
  height: number;
  roof: StoryRoofStyle;
  overhang: boolean;
  facing: StoryFacing;
  material: StoryMaterial;
  roofMaterial?: StoryMaterial;
  /** Alt held: a wall becomes a straight line along the longer axis. */
  line?: boolean;
  /** Water tools: dig the cells into the ground first (one block lower, a remove item of the same group); unset = the tool's default. */
  dig?: boolean;
}

export const FACINGS: readonly StoryFacing[] = ['n', 'e', 's', 'w'];

const FACING_DIR: Record<StoryFacing, Dir> = { n: 'north', e: 'east', s: 'south', w: 'west' };

/** Whether a single block of this material turns with Q / E (it has a facing: a furnace, stairs, a pumpkin, a trapdoor…). */
export function materialTurns(m: StoryMaterial | null | undefined): boolean {
  return m !== null && m !== undefined && hasFacing(materialKey(m)[0]);
}

/** The material as the block faces `facing` ([id, meta]); one with no facing unchanged. */
export function turnedMaterial(m: StoryMaterial, facing: StoryFacing): StoryMaterial {
  const [id, meta] = materialKey(m);
  return hasFacing(id) ? [id, withFacing(id, meta, FACING_DIR[facing])] : m;
}

/** Q / E: turn the facing (counter)clockwise. */
export function rotateFacing(f: StoryFacing, dir: 1 | -1): StoryFacing {
  return FACINGS[(FACINGS.indexOf(f) + dir + 4) % 4]!;
}

export function clampHeight(h: number): number {
  return Math.max(1, Math.min(12, Math.round(h)));
}

/** Snaps the drag end to a straight line from the start (the longer axis wins). */
export function lineEnd(a: V3, b: V3): V3 {
  return Math.abs(b[0] - a[0]) >= Math.abs(b[2] - a[2]) ? [b[0], a[1], a[2]] : [a[0], a[1], b[2]];
}

/** A structure placement: the drag's cells (`end` snapped to a line for line tools), the first layer's y, its flat area. */
type Placement = { a: V3; b: V3; end: V3; y: number; flat: StoryArea; o: BuildOpts };

function roofItem({ a, end, y, o }: Placement): StoryBuildItem {
  const ry = y + clampHeight(o.height);
  return {
    kind: 'roof',
    area: [a[0], ry, a[2], end[0], ry, end[2]],
    material: o.material,
    roof: {
      style: o.roof,
      ...(o.roofMaterial ? { material: o.roofMaterial } : {}),
      ...(o.overhang ? { overhang: 1 as const } : {}),
    },
  };
}

/** A block that faces somewhere (a furnace, stairs, a pumpkin…) takes the Q/E turn as its meta: the hologram shows it and the
 * pawn places it that way. One cell is a block; a drag is a box of them. */
function blockItem({ a, b, o }: Placement): StoryBuildItem {
  const material = turnedMaterial(o.material, o.facing);
  return a[0] === b[0] && a[1] === b[1] && a[2] === b[2]
    ? { kind: 'block', blocks: [a], material }
    : { kind: 'block', area: [a[0], a[1], a[2], b[0], b[1], b[2]], material };
}

/** Each structure tool's item. */
const STRUCTURE_ITEMS: Record<StructureId, (p: Placement) => StoryBuildItem> = {
  wall: ({ flat, o }) => ({ kind: 'wall', area: flat, material: o.material, height: clampHeight(o.height) }),
  floor: ({ a, end, y, o }) => ({ kind: 'floor', area: [a[0], y - 1, a[2], end[0], y - 1, end[2]], material: o.material }),
  roof: roofItem,
  door: ({ a, o }) => ({ kind: 'door', blocks: [a], material: o.material, facing: o.facing }),
  window: ({ a, end, y }) => ({ kind: 'window', area: [a[0], y + 1, a[2], end[0], y + 1, end[2]], material: 'glass' }),
  stairs: ({ flat, o }) => ({ kind: 'stairs', area: flat, material: o.material, facing: o.facing }),
  fence: ({ flat, o }) => ({ kind: 'fence', area: flat, material: o.material }),
  block: blockItem,
  remove: ({ a, b }) => ({ kind: 'remove', area: [a[0], a[1], a[2], b[0], b[1], b[2]], material: 'stone' }),
};

/**
 * The build item a placement makes. `a` / `b` are the drag's start and end cells (the first block layer: the level's floor
 * + 1 in top-down, the snapped face cell in the angled view). Walls rise `height`; a roof sits on top of walls of that
 * height (`a.y + height`); a floor goes one below the first layer (on the level's floor).
 */
export function buildItem(tool: BuildToolId, a: V3, b: V3, o: BuildOpts): StoryBuildItem {
  // Water: the water item alone (the preview); buildItems adds the dig that goes first.
  if (isWaterTool(tool)) return waterItems(tool, waterToolCells(tool, a, b, waterDig(tool, o)), { dig: false })[0]!;
  if (tool.startsWith('furniture:')) return { kind: tool.slice('furniture:'.length) as FurnitureId, blocks: [a], facing: o.facing };
  const t = tool as StructureId;
  const spec = STRUCTURE_TOOLS.find((s) => s.id === t)!;
  const end = spec.drag === 'line' || (t === 'wall' && o.line) ? lineEnd(a, b) : b;
  const y = a[1];
  return STRUCTURE_ITEMS[t]({ a, b, end, y, flat: [a[0], y, a[2], end[0], y, end[2]], o });
}

/** Every item a placement queues: a water tool's dig item (same group) before its water; one item otherwise. */
export function buildItems(tool: BuildToolId, a: V3, b: V3, o: BuildOpts): StoryBuildItem[] {
  if (!isWaterTool(tool)) return [buildItem(tool, a, b, o)];
  const dig = waterDig(tool, o);
  return waterItems(tool, waterToolCells(tool, a, b, dig), { dig });
}

export interface BuildPreview {
  item: StoryBuildItem;
  blocks: Array<[number, number, number, number, number]>;
  /** Blocks of material it needs (air for remove). */
  cost: number;
  valid: boolean;
  reason?: string;
}

/** The ghost under the cursor: blocks, cost, and whether it can be queued. `have` = the stockpiled count of the material. */
export function preview(item: StoryBuildItem, have?: number): BuildPreview {
  const blocks = expandBuildItem(item);
  const cost = blocks.filter((b) => b[3] !== 0).length;
  if (blocks.length === 0) return { item, blocks, cost, valid: false, reason: 'nothing to place' };
  if (blocks.length > 4096) return { item, blocks, cost, valid: false, reason: 'too big (max 4096 blocks)' };
  if (have !== undefined && cost > have && item.kind !== 'remove') return { item, blocks, cost, valid: true, reason: `short of ${cost - have}` };
  return { item, blocks, cost, valid: true };
}

/** The level cut: with x-ray on, everything from the roof line up (level + wall height + 1) is cut away. */
export function levelCutY(level: number, height: number, xray: boolean): number | null {
  return xray ? level + clampHeight(height) + 1 : null;
}

/** PgUp / PgDn move a whole storey (walls + the floor above); Shift moves one block. */
export function nextLevel(level: number, height: number, dir: 1 | -1, fine: boolean): number {
  return level + dir * (fine ? 1 : clampHeight(height) + 1);
}

/** Storey number of a level relative to the colony's ground (1 = ground floor). */
export function storeyOf(level: number, ground: number, height: number): number {
  const s = Math.floor((level - ground) / (clampHeight(height) + 1));
  return s >= 0 ? s + 1 : s;
}

/** The cell a block placed against a hovered face goes in (Minecraft face order: down, up, north, south, west, east). */
export function faceCell(hit: { x: number; y: number; z: number; face: number }): V3 {
  const d: Record<number, V3> = { 0: [0, -1, 0], 1: [0, 1, 0], 2: [0, 0, -1], 3: [0, 0, 1], 4: [-1, 0, 0], 5: [1, 0, 0] };
  const o = d[hit.face] ?? [0, 1, 0];
  return [hit.x + o[0], hit.y + o[1], hit.z + o[2]];
}

export function materialLabel(m: StoryMaterial): string {
  const [id, meta] = materialKey(m);
  return typeof m === 'string' ? m.replace(/_/g, ' ') : `${id}:${meta}`;
}

// ------------------------------------------------------------------ designs (`/story?design=N`)

export type DesignBlock = [number, number, number, number, number];

const isTuple = (e: unknown): boolean =>
  Array.isArray(e) && e.length >= 4 && e.slice(0, 4).every((n) => typeof n === 'number');

function isBlockObject(e: unknown): boolean {
  if (!e || typeof e !== 'object' || Array.isArray(e)) return false;
  const o = e as Record<string, unknown>;
  return ['x', 'y', 'z'].every((k) => typeof o[k] === 'number') && typeof (o.id ?? o.block) === 'number';
}

/** A list's blocks when it is one: every entry a `[x, y, z, id, meta?]` tuple, or every entry a block object. */
function listBlocks(json: unknown[]): DesignBlock[] {
  if (json.every(isTuple)) return (json as number[][]).map((e) => [e[0]!, e[1]!, e[2]!, e[3]!, e[4] ?? 0]);
  if (json.every(isBlockObject))
    return (json as Array<Record<string, number>>).map((e) => [e.x!, e.y!, e.z!, e.id ?? e.block!, e.meta ?? e.data ?? 0]);
  return [];
}

/** A JSON list's entries or an object's values (where a design's blocks may be nested). */
const childrenOf = (json: object): unknown[] =>
  Array.isArray(json) ? json : Object.values(json as Record<string, unknown>);

/** The blocks moved so the lowest corner is (0, 0, 0), air left out. */
function fromCorner(out: DesignBlock[]): DesignBlock[] {
  const mx = Math.min(...out.map((b) => b[0])), my = Math.min(...out.map((b) => b[1])), mz = Math.min(...out.map((b) => b[2]));
  return out.filter((b) => b[3] !== 0).map((b) => [b[0] - mx, b[1] - my, b[2] - mz, b[3], b[4]]);
}

/**
 * A design's blocks from whatever JSON the bot gives for it: the first list of `[x, y, z, id, meta?]` tuples or of objects with
 * numeric `x, y, z` and `id` (or `block`), moved so its lowest corner is (0, 0, 0). Empty when there is none.
 */
export function designBlocks(json: unknown, depth = 0): DesignBlock[] {
  if (depth > 6 || json === null || typeof json !== 'object') return [];
  if (Array.isArray(json) && json.length) {
    const out = listBlocks(json);
    if (out.length) return fromCorner(out);
  }
  for (const v of childrenOf(json)) {
    const found = designBlocks(v, depth + 1);
    if (found.length) return found;
  }
  return [];
}

/**
 * A Design Studio session's cells as a placeable design: its blocks plus its dug cells (id 0: dug out of the ground), x/z moved
 * to the box's corner. y stays on the studio's ground (0 = the first layer above the grass, below = dug into it) when the design
 * digs, so the clicked level is its ground floor and the basement goes under it; else its lowest layer is 0 (as `designBlocks`).
 */
export function designCells(
  session: { blocks?: readonly number[][] | null; dug?: readonly number[][] | null; kind?: string | null } | null,
): DesignBlock[] {
  if (!session) return [];
  const out: DesignBlock[] = [
    ...(session.blocks ?? []).filter((b) => (b[3] ?? 0) > 0).map((b): DesignBlock => [b[0]!, b[1]!, b[2]!, b[3]!, b[4] ?? 0]),
    ...(session.dug ?? []).map((p): DesignBlock => [p[0]!, p[1]!, p[2]!, 0, 0]),
  ];
  if (!out.length) return [];
  // A loop, not Math.min(...cells): engines refuse argument lists of about 100 000 and more, and a big mine has that many.
  let mx = Infinity,
    my = Infinity,
    mz = Infinity;
  for (const b of out) {
    if (b[0] < mx) mx = b[0];
    if (b[1] < my) my = b[1];
    if (b[2] < mz) mz = b[2];
  }
  // A mine keeps its absolute y (ore heights matter); a build stands on the clicked level.
  const dy = session.kind === 'mine' || my < 0 ? 0 : my;
  return out.map((b) => [b[0] - mx, b[1] - dy, b[2] - mz, b[3], b[4]]);
}

/** A mine design's y range and why it can't be placed (null = it can): it never digs below y 1 (the bedrock floor). */
export function mineCheck(blocks: readonly DesignBlock[]): { min: number; max: number; refusal: string | null } {
  let min = Infinity,
    max = -Infinity,
    lowDig = Infinity;
  for (const b of blocks) {
    min = Math.min(min, b[1]);
    max = Math.max(max, b[1]);
    if (b[3] === 0) lowDig = Math.min(lowDig, b[1]);
  }
  return {
    min,
    max,
    refusal: lowDig < 1 ? `it digs down to y ${lowDig}: a mine stops at y 1 (bedrock below)` : null,
  };
}

/**
 * A design placed with its origin at `at`, turned by `facing` (n = as drawn, then clockwise). An `absolute` design (a mine)
 * keeps its own y: only x and z follow the click.
 */
export function placeDesign(
  blocks: readonly DesignBlock[],
  at: V3,
  facing: StoryFacing,
  absolute = false,
  version: string | number | null = '1.12.2',
): DesignBlock[] {
  const turns = FACINGS.indexOf(facing);
  const y0 = absolute ? 0 : at[1];
  const gameVersion = version === 'b1.8.1' ? 'b1.8.1' : '1.12.2';
  return blocks.map(([x, y, z, id, meta]) => {
    let dx = x, dz = z;
    for (let i = 0; i < turns; i++) [dx, dz] = [-dz, dx];
    return [at[0] + dx, y0 + y, at[2] + dz, id, rotateMeta(id, meta, turns, gameVersion)];
  });
}

/** A design's group tag (one id ties its items together) and its name, both cut to 40 characters. */
function designTag(prefix: string, name?: string, group?: string): { group: string; name?: string } {
  return { group: (group ?? `${prefix}${Date.now().toString(36)}`).slice(0, 40), ...(name ? { name: name.slice(0, 40) } : {}) };
}

/** A mine item holds at most this many cells (`blocks` plus `place`: the contract's cap). */
export const MINE_ITEM_CELLS = 4096;
/**
 * …and at most this many bytes of cell JSON, so one item sent alone stays under the supervisor's 64 KiB Story body cap
 * whatever the coordinates (a cell is 8 to 40 bytes: five-digit negative coordinates are what fill an item early).
 */
export const MINE_ITEM_BYTES = 56_000;
/** A mine is queued one item per request; past this many it is refused rather than queued for hours. */
export const MAX_MINE_PARTS = 64;

const cellBytes = (cell: readonly number[]) => JSON.stringify(cell).length + 1;

/**
 * The dig cells in items of their own, then the blocks: they ride in the last dig item while it has room, the rest in items
 * of their own. An item closes at `MINE_ITEM_CELLS` cells or `MINE_ITEM_BYTES` bytes, whichever comes first.
 */
function packMine(
  dig: readonly DesignBlock[],
  put: readonly DesignBlock[],
  tag: { group: string; name?: string },
): StoryBuildItem[] {
  const items: StoryBuildItem[] = [];
  let open: StoryBuildItem | null = null;
  let cells = 0,
    bytes = 0;
  const room = (size: number): StoryBuildItem => {
    if (!open || cells >= MINE_ITEM_CELLS || bytes + size > MINE_ITEM_BYTES) {
      open = { kind: 'mine', blocks: [], ...tag };
      items.push(open);
      cells = 0;
      bytes = 0;
    }
    cells++;
    bytes += size;
    return open;
  };
  for (const b of dig) {
    const cell: [number, number, number] = [b[0], b[1], b[2]];
    room(cellBytes(cell)).blocks!.push(cell);
  }
  for (const b of put) {
    const cell: [number, number, number, number, number] = [b[0], b[1], b[2], b[3], b[4]];
    (room(cellBytes(cell)).place ??= []).push(cell);
  }
  return items;
}

/**
 * The build items of a placed mine design: `mine` items of one group, its dig cells top-down (the colony digs a cell once
 * it is exposed) then the blocks put in once their cells are dug (`place`: torches, ladders). Each item fits one request on
 * its own (`packMine`); a mine that needs more than `MAX_MINE_PARTS` items is refused, never cut short.
 */
export function mineItems(placed: readonly DesignBlock[], name?: string, group?: string): StoryBuildItem[] {
  const dig = placed.filter((b) => b[3] === 0).sort((a, b) => b[1] - a[1] || a[0] - b[0] || a[2] - b[2]);
  const put = placed.filter((b) => b[3] !== 0);
  const items = packMine(dig, put, designTag('m', name, group));
  if (items.length > MAX_MINE_PARTS)
    throw new Error(
      `This mine needs ${items.length} parts to queue (at most ${MAX_MINE_PARTS}). Place a smaller mine, or split it into several designs.`,
    );
  return items;
}

/** A design's cells to dig (id 0) and its blocks by material (`id:meta`), in the design's order. */
function splitDesign(placed: readonly DesignBlock[]): { dig: V3[]; by: Map<string, V3[]> } {
  const by = new Map<string, V3[]>();
  const dig: V3[] = [];
  for (const [x, y, z, id, meta] of placed) {
    if (id === 0) {
      dig.push([x, y, z]);
      continue;
    }
    const k = `${id}:${meta}`;
    const list = by.get(k);
    if (list) list.push([x, y, z]);
    else by.set(k, [[x, y, z]]);
  }
  return { dig, by };
}

/**
 * One group per palette placement, with its full bounds as replacement volume. The server clears unlisted interior cells
 * and retires old overlapping ownership before building. Explicit basement digs still precede the material items.
 */
export function designItems(placed: readonly DesignBlock[], name?: string, group?: string): StoryBuildItem[] {
  if (!placed.length) return [];
  const bounds: [number, number, number, number, number, number] = [
    Infinity,
    Infinity,
    Infinity,
    -Infinity,
    -Infinity,
    -Infinity,
  ];
  for (const b of placed)
    for (let axis = 0; axis < 3; axis++) {
      bounds[axis] = Math.min(bounds[axis]!, b[axis]!);
      bounds[axis + 3] = Math.max(bounds[axis + 3]!, b[axis]!);
    }
  const volume = (bounds[3] - bounds[0] + 1) * (bounds[4] - bounds[1] + 1) * (bounds[5] - bounds[2] + 1);
  if (volume > 262144) throw new Error('This design covers more than 262144 cells. Place a smaller design.');
  const { dig, by } = splitDesign(placed);
  const items: StoryBuildItem[] = [];
  const tag = { group: designTag('d', undefined, group).group };
  const named = name ? { name: name.slice(0, 40) } : {};
  // Top-down is the colony's own order for a dig; the list order is the queue order (the dig first).
  for (let i = 0; i < dig.length; i += 4096) items.push({ kind: 'remove', blocks: dig.slice(i, i + 4096), ...named, ...tag });
  for (const [k, cells] of by) {
    const [id, meta] = k.split(':').map(Number) as [number, number];
    for (let i = 0; i < cells.length; i += 4096)
      items.push({ kind: 'block', blocks: cells.slice(i, i + 4096), material: [id, meta], ...named, ...tag });
  }
  if (items.length > 64) throw new Error('This design needs more than 64 build items. Reduce its materials or size.');
  items[0]!.replaceArea = bounds;
  return items;
}

// ------------------------------------------------------------------ the right-click menu

export type Target =
  | { kind: 'block'; x: number; y: number; z: number; id: number; name: string }
  | { kind: 'mob'; entity: number; name: string; animal: boolean; x: number; y: number; z: number }
  | { kind: 'item'; entity: number; name: string; x: number; y: number; z: number }
  /** A player (an entity) or an AI bot (no entity id in the viewer: attacked by name). Never the colony's own pawns. */
  | { kind: 'player'; entity: number | null; name: string; bot: boolean; x: number; y: number; z: number }
  /** A build's ghost; `repair`: a preserved zone's repair (only its selected cell can be released by hand). */
  | { kind: 'ghost'; build: string | number; label: string; x: number; y: number; z: number; repair?: boolean }
  | { kind: 'ground'; x: number; y: number; z: number };

export interface MenuItem {
  id: string;
  label: string;
  hint?: string;
  order?: StoryOrderSpec;
  /** A non-order action (ghost cancel/prioritise, open storage…). */
  action?: 'cancel-build' | 'first-build' | 'storage' | 'bills' | 'gear' | 'unprotect';
  directive?: StoryDirectiveSpec;
  /** action 'gear': the bulk gear op for the selected pawns. */
  gear?: StoryGearOp;
  /** An attack on a player or bot: confirmed once per target per session before it is sent. */
  confirm?: { name: string; kind: 'player' | 'bot'; entity: number | null };
}

/** The bulk gear items of the right-click menu (selected pawns only). */
export const GEAR_MENU: ReadonlyArray<{ op: StoryGearOp; label: string }> = [
  { op: 'arm', label: 'Arm (best weapon)' },
  { op: 'armor', label: 'Equip armour' },
  { op: 'pickup', label: 'Pick up…' },
  { op: 'return', label: 'Return gear' },
  { op: 'tools', label: 'Equip tools for their work' },
];

type Cell = { x: number; y: number; z: number };
type MenuOrder = (id: string, label: string, spec: StoryOrderSpec, d: StoryDirectiveSpec) => void;
/** What a target's menu items are built with: the list, the order adder (only with pawns or AI selected), the cell. */
type MenuCtx = { out: MenuItem[]; order: MenuOrder; at: Cell; p: V3; acting: boolean };

const goHere = ({ order, at, p }: MenuCtx) =>
  order('move', 'Go here', { kind: 'move', target: at }, { kind: 'move', targets: [p] });

/** Attack a player (by entity) or an AI bot (by name): confirmed once per target. */
function playerAttack(t: Extract<Target, { kind: 'player' }>): MenuItem {
  const byEntity = t.entity !== null;
  return {
    id: 'attack',
    label: `Attack ${t.name}`,
    hint: 'asks to confirm once',
    order: { kind: 'attack', target: byEntity ? { entity: t.entity! } : { player: t.name } },
    directive: { kind: 'attack', ...(byEntity ? { entities: [t.entity!] } : { text: `attack ${t.name}` }) },
    confirm: { name: t.name, kind: t.bot ? 'bot' : 'player', entity: t.entity },
  };
}

/** A block: its storage or bills, then chop (a tree) or mine it, then go on top of it. */
function blockItems(t: Extract<Target, { kind: 'block' }>, c: MenuCtx): void {
  const { out, order, at, p } = c;
  const tree = LOG_IDS.has(t.id) || t.id === 18 || t.id === 161;
  if (CHEST_IDS.has(t.id)) out.push({ id: 'storage', label: 'Storage settings', action: 'storage' });
  if (t.id === 58 || t.id === 61 || t.id === 62) out.push({ id: 'bills', label: 'Bills', action: 'bills' });
  if (tree) order('chop', 'Chop this tree', { kind: 'chop', target: at }, { kind: 'chop', targets: [p] });
  else order('mine', `Mine ${t.name}`, { kind: 'mine', target: at }, { kind: 'mine', targets: [p] });
  const above: Cell = { x: at.x, y: at.y + 1, z: at.z };
  order('move', 'Go here', { kind: 'move', target: above }, { kind: 'move', targets: [[at.x, at.y + 1, at.z]] });
}

/** Each target kind's own items (before Hold, Stop and the gear). */
const TARGET_ITEMS: { [K in Target['kind']]: (t: Extract<Target, { kind: K }>, c: MenuCtx) => void } = {
  ghost: (t, c) => {
    c.out.push({ id: 'first-build', label: `Build "${t.label}" first`, action: 'first-build' });
    if (!t.repair) c.out.push({ id: 'cancel-build', label: `Cancel "${t.label}"`, action: 'cancel-build' });
    c.order('build', 'Build this now', { kind: 'build', target: c.at, design: t.build }, { kind: 'build', targets: [c.p] });
  },
  mob: (t, c) => {
    c.order(
      'attack',
      t.animal ? `Hunt ${t.name}` : `Attack ${t.name}`,
      { kind: 'attack', target: { entity: t.entity } },
      { kind: t.animal ? 'hunt' : 'attack', entities: [t.entity] },
    );
    goHere(c);
  },
  player: (t, c) => {
    if (c.acting) c.out.push(playerAttack(t));
    goHere(c);
  },
  item: (t, c) => {
    c.order('haul', `Haul ${t.name}`, { kind: 'haul', target: { entity: t.entity } }, { kind: 'haul', entities: [t.entity] });
    goHere(c);
  },
  block: blockItems,
  ground: (_t, c) => goHere(c),
};

/** The genre's context menu: what the selected pawns can do to the thing under the cursor. */
export function contextMenu(t: Target, hasPawns: boolean, hasAi: boolean): MenuItem[] {
  const at = { x: Math.floor(t.x), y: Math.floor(t.y), z: Math.floor(t.z) };
  const p: [number, number, number] = [at.x, at.y, at.z];
  const out: MenuItem[] = [];
  const acting = hasPawns || hasAi;
  const order: MenuOrder = (id, label, spec, d) => {
    if (acting) out.push({ id, label, order: spec, directive: d });
  };
  (TARGET_ITEMS[t.kind] as (t: Target, c: MenuCtx) => void)(t, { out, order, at, p, acting });
  if (t.kind === 'block' || t.kind === 'ghost') out.push({
    id: 'unprotect', label: 'Stop rebuilding here', action: 'unprotect',
    hint: 'Release this block from builds and Preserve; leave other cells unchanged',
  });
  if (acting) {
    out.push({ id: 'hold', label: 'Hold position', order: { kind: 'hold' }, directive: { kind: 'guard' } });
    out.push({ id: 'stop', label: 'Stop', order: { kind: 'stop' }, directive: { kind: 'text', text: 'stop what you are doing' } });
  }
  if (hasPawns) for (const g of GEAR_MENU) out.push({ id: `gear-${g.op}`, label: g.label, action: 'gear', gear: g.op });
  return out;
}

/** The orders request for a menu pick (Shift queues). */
export function orderBody(pawns: readonly string[], item: MenuItem, queue: boolean): Record<string, unknown> | null {
  if (!item.order || pawns.length === 0) return null;
  return { pawns: [...pawns], order: item.order, ...(queue ? { queue: true } : {}) };
}

// ------------------------------------------------------------------ keys

export type KeyAction =
  | 'draft'
  | 'pause'
  | 'escape'
  | 'chat'
  | 'view'
  | 'fly'
  | 'xray'
  | 'levelUp'
  | 'levelDown'
  | 'rotateCcw'
  | 'rotateCw'
  | 'heightUp'
  | 'heightDown'
  | 'help'
  | 'focus'
  | 'fullscreen'
  | 'layersUp'
  | 'layersDown'
  | 'materialNext'
  | 'materialPrev'
  | `recent:${number}`
  | `speed:${StorySpeed}`
  | `height:${HeightMode}`
  | `tool:${DesignateId}`
  | `tab:${string}`;

/** Bottom tab hotkeys. */
export const TAB_KEYS: Record<string, string> = { KeyB: 'architect', KeyP: 'work', KeyY: 'schedule', KeyU: 'queue', KeyI: 'colony', KeyO: 'directives' };

/** The speed keys (RimWorld's): 1 normal, 2 fast, 3 faster — only with no tool in hand (a tool's own 1–5 come first). */
export const SPEED_KEYS: Record<string, StorySpeed> = {
  Digit1: 'normal',
  Digit2: 'fast',
  Digit3: 'faster',
  Numpad1: 'normal',
  Numpad2: 'fast',
  Numpad3: 'faster',
};

/** The key event fields `keyAction` reads. */
type KeyInput = { code: string; key: string; shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean };

/** Keys that always mean the same action (with or without Shift / Alt). */
const FIXED_KEYS: Record<string, KeyAction> = {
  KeyR: 'draft',
  Space: 'pause',
  Escape: 'escape',
  KeyT: 'chat',
  Enter: 'chat',
  KeyV: 'view',
  KeyX: 'xray',
  PageUp: 'levelUp',
  PageDown: 'levelDown',
  KeyQ: 'rotateCcw',
  KeyE: 'rotateCw',
  Home: 'focus',
};

/**
 * Keys that act only with Shift: ? is help, Shift+F full screen (F alone is the Forbid tool; the browser's F11 shows the whole
 * page, not the colony).
 */
const SHIFT_KEYS: Record<string, KeyAction> = { Slash: 'help', KeyF: 'fullscreen' };

/** + / − raise or lower a structure tool's height. */
const BUILD_HEIGHT_KEYS: Record<string, KeyAction> = {
  Equal: 'heightUp',
  NumpadAdd: 'heightUp',
  Minus: 'heightDown',
  NumpadSubtract: 'heightDown',
};

/** + / − change the Layers count while a height tool is in hand. */
const LAYER_KEYS: Record<string, KeyAction> = {
  Equal: 'layersUp',
  NumpadAdd: 'layersUp',
  Minus: 'layersDown',
  NumpadSubtract: 'layersDown',
};

/** ] / [ cycle a structure tool's material (Tab / Shift+Tab too). */
const MATERIAL_CYCLE_KEYS: Record<string, KeyAction> = { BracketRight: 'materialNext', BracketLeft: 'materialPrev' };

const RECENT_KEY = /^(?:Digit|Numpad)([1-5])$/;

const unmodified = (e: KeyInput) => !e.shiftKey && !e.altKey;

/** 1 / 2 / 3 → the pawns' speed (no Shift / Alt). */
function speedAction(e: KeyInput): KeyAction | null {
  const speed = SPEED_KEYS[e.code];
  return speed && unmodified(e) ? `speed:${speed}` : null;
}

/** A material tool's keys (never with Alt): Tab / ] / [ cycle the material, 1–5 pick a recent one. */
function materialAction(e: KeyInput): KeyAction | null {
  if (e.altKey) return null;
  if (e.code === 'Tab') return e.shiftKey ? 'materialPrev' : 'materialNext';
  const cycle = MATERIAL_CYCLE_KEYS[e.code];
  if (cycle) return cycle;
  const n = RECENT_KEY.exec(e.code);
  return n && !e.shiftKey ? `recent:${Number(n[1])}` : null;
}

/** A height tool's keys: 1–5 pick the height mode, + / − set the Layers count. */
function heightAction(e: KeyInput): KeyAction | null {
  const mode = HEIGHT_MODES.find((h) => h.key === e.code || h.key.replace('Digit', 'Numpad') === e.code);
  if (mode && unmodified(e)) return `height:${mode.id}`;
  return LAYER_KEYS[e.code] ?? null;
}

/** The keys a tool in hand doesn't claim: fixed actions, Shift combos, a structure's height, the tools and the tabs. */
function commonAction(e: KeyInput, buildTool: boolean): KeyAction | null {
  if (e.code === 'KeyV' && e.shiftKey) return 'fly';
  const fixed = FIXED_KEYS[e.code];
  if (fixed) return fixed;
  if (e.shiftKey && SHIFT_KEYS[e.code]) return SHIFT_KEYS[e.code]!;
  if (buildTool && BUILD_HEIGHT_KEYS[e.code]) return BUILD_HEIGHT_KEYS[e.code]!;
  const tool = DESIGNATE_TOOLS.find((t) => t.key === e.code);
  if (tool && !e.shiftKey) return `tool:${tool.id}`;
  const tab = TAB_KEYS[e.code];
  return tab ? `tab:${tab}` : null;
}

/**
 * A key on the map → its action, or null (WASD/arrows pan in the frame loop). 1–3 set the pawns' speed while no tool is in hand
 * (`speedKeys`); with a tool they stay the tool's: 1–5 pick the height mode while Mine or Deconstruct is the tool (whose + / −
 * then set the Layers count), or a recent material for a structure tool. Typing in a field never reaches here (the caller checks
 * `isTyping` first).
 */
export function keyAction(
  e: KeyInput,
  buildTool: boolean,
  heightTool = false,
  /** A structure tool that takes a material: Tab / Shift+Tab and ] / [ cycle it, 1–5 pick a recent one. */
  materialTool = false,
  /** No tool in hand (the select tool): 1 / 2 / 3 set the speed. */
  speedKeys = false,
): KeyAction | null {
  if (e.ctrlKey || e.metaKey) return null;
  return toolKeyAction(e, heightTool, materialTool, speedKeys) ?? commonAction(e, buildTool);
}

/** The keys the tool in hand claims first: a material tool's, then a height tool's; the speed keys with no tool. */
function toolKeyAction(e: KeyInput, heightTool: boolean, materialTool: boolean, speedKeys: boolean): KeyAction | null {
  const material = materialTool ? materialAction(e) : null;
  if (material) return material;
  if (heightTool) return heightAction(e);
  return speedKeys && !materialTool ? speedAction(e) : null;
}

/** Keys the frame loop pans with. */
export const PAN_KEYS: Record<string, [number, number]> = {
  KeyW: [0, -1],
  ArrowUp: [0, -1],
  KeyS: [0, 1],
  ArrowDown: [0, 1],
  KeyA: [-1, 0],
  ArrowLeft: [-1, 0],
  KeyD: [1, 0],
  ArrowRight: [1, 0],
};

/** A designation that failed or is resting (its target waits out a back-off): what Retry picks up. */
/** A failed build cell (blocked after its failures, or with a failure's reason): the map's marker and the Retry tool's catch. */
export interface FailedCell {
  build: string | number;
  cell: V3;
  reason: string;
}

type GhostLike = readonly [number, number, number, number, number, string | number, string, (string | null)?, (string | null)?];

/** Whether a ghost lies outside a grid rect [x1, z1, x2, z2] (either corner first). */
function outsideGridRect(g: GhostLike, rect: readonly [number, number, number, number]): boolean {
  const [x1, z1, x2, z2] = rect;
  return g[0] < Math.min(x1, x2) || g[0] > Math.max(x1, x2) || g[2] < Math.min(z1, z2) || g[2] > Math.max(z1, z2);
}

/** A ghost's failure: blocked, or a failure's reason (null when it hasn't failed, or is done or unsupported). */
function ghostFailure(g: GhostLike): string | null {
  const state = g[6];
  if (state === 'done' || state === 'unsupported') return null;
  const reason = g[8] ?? null;
  if (state !== 'blocked' && !reason) return null;
  return reason ?? 'blocked after repeated failures';
}

/** The failed cells among the state's ghosts (never done or unsupported ones), in a grid rect [x1, z1, x2, z2] when given. */
export function failedCells(ghosts: readonly GhostLike[], rect: readonly [number, number, number, number] | null = null): FailedCell[] {
  const out: FailedCell[] = [];
  for (const g of ghosts) {
    const reason = ghostFailure(g);
    if (reason === null || (rect && outsideGridRect(g, rect))) continue;
    out.push({ build: g[5], cell: [g[0], g[1], g[2]], reason });
  }
  return out;
}

/** Failed cells by build: one retry request per build (`{op:"retry", id, cells}`). */
export function failedByBuild(cells: readonly FailedCell[]): Map<string, { id: string | number; cells: V3[] }> {
  const m = new Map<string, { id: string | number; cells: V3[] }>();
  for (const f of cells) {
    const k = String(f.build);
    const e = m.get(k) ?? { id: f.build, cells: [] };
    e.cells.push(f.cell);
    m.set(k, e);
  }
  return m;
}

export function isStalled(d: { status?: string | null; retryAt?: unknown; fails?: number | null }): boolean {
  return (
    (d.status !== null && d.status !== undefined && d.status !== 'open') || (d.retryAt !== null && d.retryAt !== undefined) || (d.fails ?? 0) > 0
  );
}

/** A designation's block cell (its target, else its first block), or null for entity designations without one. */
export function designationCell(d: {
  target?: unknown;
  block?: readonly number[] | null;
  blocks?: ReadonlyArray<readonly number[]> | null;
  targets?: ReadonlyArray<readonly number[]> | null;
}): V3 | null {
  const t = d.target;
  if (Array.isArray(t) && t.length >= 3) {
    const [x, y, z] = t as [number, number, number];
    return [Math.floor(x), Math.floor(y), Math.floor(z)];
  }
  const b = d.block ?? d.blocks?.[0] ?? d.targets?.[0];
  return b && b.length >= 3 ? [Math.floor(b[0]!), Math.floor(b[1]!), Math.floor(b[2]!)] : null;
}

/** The stalled designations whose cell lies in the dragged XZ rectangle [x1, z1, x2, z2] (any height). */
export function stalledIn<T extends Parameters<typeof isStalled>[0] & Parameters<typeof designationCell>[0] & { id: string | number },
>(
  all: readonly T[],
  rect: readonly [number, number, number, number] | null): Array<T['id']> {
  return all
    .filter((d) => {
      if (!isStalled(d)) return false;
      if (!rect) return true;
      const c = designationCell(d);
      if (!c) return false;
      const [x1, z1, x2, z2] = rect;
      return (
        c[0] >= Math.min(x1, x2) && c[0] <= Math.max(x1, x2) && c[2] >= Math.min(z1, z2) && c[2] <= Math.max(z1, z2)
      );
    })
    .map((d) => d.id);
}
