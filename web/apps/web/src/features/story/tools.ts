/**
 * Story Mode tools: the designate palette (drag a grid rectangle, every matching target lights up with a live count,
 * release designates), zones, the Architect's structure and furniture tools (placement preview, wall height, roof style,
 * facing, levels, face snapping in the angled view), the right-click order menu, and the keyboard map. Pure, unit-tested.
 */
import {
  ANIMAL_TYPES,
  CHEST_IDS,
  countTrees,
  expandBuildItem,
  LOG_IDS,
  materialKey,
  matchesTool,
  type StoryArea,
  type StoryBuildItem,
  type StoryDesignationKind,
  type StoryDirectiveSpec,
  type StoryFacing,
  type StoryMaterial,
  type StoryMineFilter,
  type StoryOrderSpec,
  type StoryRoofStyle,
  type StoryZoneKind,
} from '@mineclaw/contracts';

type V3 = [number, number, number];

// ------------------------------------------------------------------ designate tools

export type DesignateId = 'chop' | 'cut' | 'harvest' | 'mine' | 'haul' | 'hunt' | 'deconstruct' | 'cancel' | 'forbid' | 'allow';

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
  let colKey = '',
    colTop: number | null = null,
    colCount = 0,
    layers = 0,
    columns = 0,
    top: number | null = null,
    bottom: number | null = null;
  const skip = new Set(NOT_GROUND_IDS);
  const flush = () => {
    if (colCount > 0) columns++;
    layers = Math.max(layers, colCount);
  };
  for (let i = 0; i + 5 < stacks.length + 1; i += 6) {
    const x = stacks[i]!, y = stacks[i + 1]!, z = stacks[i + 2]!, id = stacks[i + 3]!, meta = stacks[i + 4]!, below = stacks[i + 5]!;
    const key = `${x},${z}`;
    if (key !== colKey) {
      flush();
      colKey = key;
      colTop = null;
      colCount = 0;
    }
    if (id === 0) continue;
    let take: boolean;
    if (colTop === null && skip.has(id)) {
      // Over the column's top: a plant (or snow) standing on it goes with it; trees stay (Chop takes them).
      take = tool === 'mine' && ON_TOP_IDS.has(id) && filter === 'all';
    } else {
      if (colTop === null) colTop = y;
      take = matchesTool(tool, id, meta, below, filter);
    }
    if (take && mode === 'level') take = y === level;
    else if (take && mode === 'down') take = y >= level;
    else if (take && mode === 'flatten') take = y > level;
    if (!take) continue;
    cells.push([x, y, z]);
    colCount++;
    top = top === null ? y : Math.max(top, y);
    bottom = bottom === null ? y : Math.min(bottom, y);
  }
  flush();
  return { cells, columns, top, bottom, layers, tooMany: cells.length > MAX_DRAG_CELLS };
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

/** Block ids a tool's preview scans for (empty = every non-air block, filtered by `matchesTool`). */
export function scanIds(tool: DesignateId): number[] {
  switch (tool) {
    case 'chop':
      return [...LOG_IDS];
    case 'cut':
      return [6, 31, 32, 37, 38, 175];
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
  if (tool === 'cancel' || tool === 'haul' || tool === 'hunt') return { cells: [], count: 0 };
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

/** The designations request a released drag sends. */
export function designationBody(tool: DesignateId, area: StoryArea, filter?: StoryMineFilter): Record<string, unknown> {
  if (tool === 'cancel') return { op: 'remove', area };
  return { op: 'add', kind: tool, area, ...(tool === 'mine' && filter && filter !== 'all' ? { filter } : {}) };
}

/** The same task as a directive for AI bodies. */
export function designationDirective(tool: DesignateId, area: StoryArea, targets: readonly V3[], filter?: StoryMineFilter): StoryDirectiveSpec | null {
  if (tool === 'cancel' || tool === 'forbid' || tool === 'allow') return null;
  return { kind: tool, area, ...(targets.length ? { targets: targets.slice(0, 512) } : {}), ...(tool === 'mine' && filter ? { filter } : {}) };
}

export function countLabel(tool: DesignateTool, n: number): string {
  return `${tool.label}: ${n} ${n === 1 ? tool.noun[0] : tool.noun[1]}`;
}

// ------------------------------------------------------------------ zones

export const ZONE_TOOLS: ReadonlyArray<{ id: StoryZoneKind; label: string; hint: string; color: string }> = [
  { id: 'home', label: 'Home area', hint: 'Where pawns rest and keep things tidy', color: '#4f9cf0' },
  { id: 'farm', label: 'Growing zone', hint: 'Pawns plant, tend and harvest here', color: '#7cc35b' },
  { id: 'chop', label: 'Woodlot', hint: 'Trees here are cut and replanted', color: '#b98a4e' },
  { id: 'mine', label: 'Quarry', hint: 'Pawns mine here when idle', color: '#9aa3ad' },
  { id: 'fish', label: 'Fishing zone', hint: 'Pawns fish here with rods (still water only)', color: '#3fb8d9' },
  { id: 'forbidden', label: 'Keep out', hint: 'Pawns never walk here', color: '#e0525a' },
];

export function zoneColor(kind: string): string {
  return ZONE_TOOLS.find((z) => z.id === kind)?.color ?? '#c9a3ff';
}

// ------------------------------------------------------------------ the Architect

export type StructureId = 'wall' | 'floor' | 'roof' | 'door' | 'window' | 'stairs' | 'fence' | 'block' | 'remove';
export type FurnitureId = 'crafting_table' | 'furnace' | 'chest' | 'bed' | 'torch';
export type BuildToolId = StructureId | `furniture:${FurnitureId}`;

export const STRUCTURE_TOOLS: ReadonlyArray<{ id: StructureId; label: string; hint: string; drag: 'rect' | 'line' | 'fill' | 'point' | 'box' }> = [
  { id: 'wall', label: 'Wall', hint: 'Drag a rectangle (Alt: a straight line)', drag: 'rect' },
  { id: 'floor', label: 'Floor', hint: 'Drag to fill', drag: 'fill' },
  { id: 'roof', label: 'Roof', hint: 'Drag over the room; sits on top of the walls', drag: 'fill' },
  { id: 'door', label: 'Door', hint: 'Click a wall cell', drag: 'point' },
  { id: 'window', label: 'Window', hint: 'Click wall cells (drag for a row)', drag: 'line' },
  { id: 'stairs', label: 'Stairs', hint: 'Drag a run; Q/E turns', drag: 'line' },
  { id: 'fence', label: 'Fence', hint: 'Drag a rectangle', drag: 'rect' },
  { id: 'block', label: 'Single block', hint: 'Click, or drag a box', drag: 'box' },
  { id: 'remove', label: 'Deconstruct', hint: 'Drag a box to clear', drag: 'box' },
];

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
}

export const FACINGS: readonly StoryFacing[] = ['n', 'e', 's', 'w'];

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

/**
 * The build item a placement makes. `a` / `b` are the drag's start and end cells (the first block layer: the level's floor
 * + 1 in top-down, the snapped face cell in the angled view). Walls rise `height`; a roof sits on top of walls of that
 * height (`a.y + height`); a floor goes one below the first layer (on the level's floor).
 */
export function buildItem(tool: BuildToolId, a: V3, b: V3, o: BuildOpts): StoryBuildItem {
  if (tool.startsWith('furniture:')) return { kind: tool.slice('furniture:'.length) as FurnitureId, blocks: [a], facing: o.facing };
  const t = tool as StructureId;
  const spec = STRUCTURE_TOOLS.find((s) => s.id === t)!;
  const end = spec.drag === 'line' || (t === 'wall' && o.line) ? lineEnd(a, b) : b;
  const y = a[1];
  const flat: StoryArea = [a[0], y, a[2], end[0], y, end[2]];
  switch (t) {
    case 'wall':
      return { kind: 'wall', area: flat, material: o.material, height: clampHeight(o.height) };
    case 'floor':
      return { kind: 'floor', area: [a[0], y - 1, a[2], end[0], y - 1, end[2]], material: o.material };
    case 'roof':
      return {
        kind: 'roof',
        area: [a[0], y + clampHeight(o.height), a[2], end[0], y + clampHeight(o.height), end[2]],
        material: o.material,
        roof: { style: o.roof, ...(o.roofMaterial ? { material: o.roofMaterial } : {}), ...(o.overhang ? { overhang: 1 as const } : {}) },
      };
    case 'door':
      return { kind: 'door', blocks: [a], material: o.material, facing: o.facing };
    case 'window':
      return { kind: 'window', area: [a[0], y + 1, a[2], end[0], y + 1, end[2]], material: 'glass' };
    case 'stairs':
      return { kind: 'stairs', area: flat, material: o.material, facing: o.facing };
    case 'fence':
      return { kind: 'fence', area: flat, material: o.material };
    case 'block':
      return a[0] === b[0] && a[1] === b[1] && a[2] === b[2] ? { kind: 'block', blocks: [a], material: o.material } : { kind: 'block', area: [a[0], a[1], a[2], b[0], b[1], b[2]], material: o.material };
    case 'remove':
      return { kind: 'remove', area: [a[0], a[1], a[2], b[0], b[1], b[2]], material: 'stone' };
  }
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

/**
 * A design's blocks from whatever JSON the bot gives for it: the first list of `[x, y, z, id, meta?]` tuples or of objects with
 * numeric `x, y, z` and `id` (or `block`), moved so its lowest corner is (0, 0, 0). Empty when there is none.
 */
export function designBlocks(json: unknown, depth = 0): DesignBlock[] {
  if (depth > 6 || json === null || typeof json !== 'object') return [];
  if (Array.isArray(json) && json.length) {
    const tuples = json.every((e) => Array.isArray(e) && e.length >= 4 && e.slice(0, 4).every((n) => typeof n === 'number'));
    const objects = json.every((e) => e && typeof e === 'object' && !Array.isArray(e) && ['x', 'y', 'z'].every((k) => typeof (e as Record<string, unknown>)[k] === 'number') && typeof ((e as Record<string, unknown>).id ?? (e as Record<string, unknown>).block) === 'number');
    let out: DesignBlock[] = [];
    if (tuples) out = (json as number[][]).map((e) => [e[0]!, e[1]!, e[2]!, e[3]!, e[4] ?? 0]);
    else if (objects)
      out = (json as Array<Record<string, number>>).map((e) => [e.x!, e.y!, e.z!, e.id ?? e.block!, e.meta ?? e.data ?? 0]);
    if (out.length) {
      const mx = Math.min(...out.map((b) => b[0])), my = Math.min(...out.map((b) => b[1])), mz = Math.min(...out.map((b) => b[2]));
      return out.filter((b) => b[3] !== 0).map((b) => [b[0] - mx, b[1] - my, b[2] - mz, b[3], b[4]]);
    }
  }
  for (const v of Array.isArray(json) ? json : Object.values(json as Record<string, unknown>)) {
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
export function designCells(session: { blocks?: readonly number[][] | null; dug?: readonly number[][] | null } | null): DesignBlock[] {
  if (!session) return [];
  const out: DesignBlock[] = [
    ...(session.blocks ?? []).filter((b) => (b[3] ?? 0) > 0).map((b): DesignBlock => [b[0]!, b[1]!, b[2]!, b[3]!, b[4] ?? 0]),
    ...(session.dug ?? []).map((p): DesignBlock => [p[0]!, p[1]!, p[2]!, 0, 0]),
  ];
  if (!out.length) return [];
  const mx = Math.min(...out.map((b) => b[0])),
    my = Math.min(...out.map((b) => b[1])),
    mz = Math.min(...out.map((b) => b[2]));
  const dy = my < 0 ? 0 : my;
  return out.map((b) => [b[0] - mx, b[1] - dy, b[2] - mz, b[3], b[4]]);
}

/** A design placed with its origin at `at`, turned by `facing` (n = as drawn, then clockwise). */
export function placeDesign(blocks: readonly DesignBlock[], at: V3, facing: StoryFacing): DesignBlock[] {
  const turns = FACINGS.indexOf(facing);
  return blocks.map(([x, y, z, id, meta]) => {
    let dx = x, dz = z;
    for (let i = 0; i < turns; i++) [dx, dz] = [-dz, dx];
    return [at[0] + dx, at[1] + y, at[2] + dz, id, meta];
  });
}

/**
 * The build request for a placed design: one `block` item per material, explicit blocks (at most 64 × 4096). A design that digs
 * (id 0 cells: its basement) queues a `remove` item first and ties its items into one `group`, so its blocks wait for the dig.
 */
export function designItems(placed: readonly DesignBlock[], name?: string, group?: string): StoryBuildItem[] {
  const by = new Map<string, Array<[number, number, number]>>();
  const dig: Array<[number, number, number]> = [];
  for (const [x, y, z, id, meta] of placed) {
    if (id === 0) {
      dig.push([x, y, z]);
      continue;
    }
    const k = `${id}:${meta}`;
    by.set(k, [...(by.get(k) ?? []), [x, y, z]]);
  }
  const items: StoryBuildItem[] = [];
  const tag = dig.length ? { group: (group ?? `d${Date.now().toString(36)}`).slice(0, 40) } : {};
  // Top-down is the colony's own order for a dig; the list order is the queue order (the dig first).
  for (let i = 0; i < dig.length; i += 4096)
    items.push({ kind: 'remove', blocks: dig.slice(i, i + 4096), ...(name ? { name: name.slice(0, 40) } : {}), ...tag });
  for (const [k, cells] of by) {
    const [id, meta] = k.split(':').map(Number) as [number, number];
    for (let i = 0; i < cells.length; i += 4096) items.push({ kind: 'block', blocks: cells.slice(i, i + 4096), material: [id, meta], ...(name ? { name: name.slice(0, 40) } : {}), ...tag });
  }
  return items.slice(0, 64);
}

// ------------------------------------------------------------------ the right-click menu

export type Target =
  | { kind: 'block'; x: number; y: number; z: number; id: number; name: string }
  | { kind: 'mob'; entity: number; name: string; animal: boolean; x: number; y: number; z: number }
  | { kind: 'item'; entity: number; name: string; x: number; y: number; z: number }
  | { kind: 'ghost'; build: string | number; label: string; x: number; y: number; z: number }
  | { kind: 'ground'; x: number; y: number; z: number };

export interface MenuItem {
  id: string;
  label: string;
  hint?: string;
  order?: StoryOrderSpec;
  /** A non-order action (ghost cancel/prioritise, open storage…). */
  action?: 'cancel-build' | 'first-build' | 'storage' | 'bills';
  directive?: StoryDirectiveSpec;
}

/** The genre's context menu: what the selected pawns can do to the thing under the cursor. */
export function contextMenu(t: Target, hasPawns: boolean, hasAi: boolean): MenuItem[] {
  const at = { x: Math.floor(t.x), y: Math.floor(t.y), z: Math.floor(t.z) };
  const p: [number, number, number] = [at.x, at.y, at.z];
  const out: MenuItem[] = [];
  const order = (id: string, label: string, spec: StoryOrderSpec, d: StoryDirectiveSpec) => {
    if (hasPawns || hasAi) out.push({ id, label, order: spec, directive: d });
  };
  switch (t.kind) {
    case 'ghost':
      out.push({ id: 'first-build', label: `Build "${t.label}" first`, action: 'first-build' });
      out.push({ id: 'cancel-build', label: `Cancel "${t.label}"`, action: 'cancel-build' });
      order('build', 'Build this now', { kind: 'build', target: at, design: t.build }, { kind: 'build', targets: [p] });
      break;
    case 'mob':
      order('attack', t.animal ? `Hunt ${t.name}` : `Attack ${t.name}`, { kind: 'attack', target: { entity: t.entity } }, { kind: t.animal ? 'hunt' : 'attack', entities: [t.entity] });
      order('move', 'Go here', { kind: 'move', target: at }, { kind: 'move', targets: [p] });
      break;
    case 'item':
      order('haul', `Haul ${t.name}`, { kind: 'haul', target: { entity: t.entity } }, { kind: 'haul', entities: [t.entity] });
      order('move', 'Go here', { kind: 'move', target: at }, { kind: 'move', targets: [p] });
      break;
    case 'block': {
      const tree = LOG_IDS.has(t.id) || t.id === 18 || t.id === 161;
      if (CHEST_IDS.has(t.id)) out.push({ id: 'storage', label: 'Storage settings', action: 'storage' });
      if (t.id === 58 || t.id === 61 || t.id === 62) out.push({ id: 'bills', label: 'Bills', action: 'bills' });
      if (tree) order('chop', 'Chop this tree', { kind: 'chop', target: at }, { kind: 'chop', targets: [p] });
      else order('mine', `Mine ${t.name}`, { kind: 'mine', target: at }, { kind: 'mine', targets: [p] });
      order('move', 'Go here', { kind: 'move', target: { x: at.x, y: at.y + 1, z: at.z } }, { kind: 'move', targets: [[at.x, at.y + 1, at.z]] });
      break;
    }
    case 'ground':
      order('move', 'Go here', { kind: 'move', target: at }, { kind: 'move', targets: [p] });
      break;
  }
  if (hasPawns || hasAi) {
    out.push({ id: 'hold', label: 'Hold position', order: { kind: 'hold' }, directive: { kind: 'guard' } });
    out.push({ id: 'stop', label: 'Stop', order: { kind: 'stop' }, directive: { kind: 'text', text: 'stop what you are doing' } });
  }
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
  | `height:${HeightMode}`
  | `tool:${DesignateId}`
  | `tab:${string}`;

/** Bottom tab hotkeys. */
export const TAB_KEYS: Record<string, string> = { KeyB: 'architect', KeyP: 'work', KeyY: 'schedule', KeyU: 'queue', KeyI: 'colony', KeyO: 'directives' };

/**
 * A key on the map → its action, or null (WASD/arrows pan in the frame loop; 1–9 stay free, except 1–5 pick the height mode
 * while Mine or Deconstruct is the tool, whose + / − then set the Layers count). Typing in a field never reaches here (the
 * caller checks `isTyping` first).
 */
export function keyAction(
  e: { code: string; key: string; shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean },
  buildTool: boolean,
  heightTool = false,
): KeyAction | null {
  if (e.ctrlKey || e.metaKey) return null;
  if (heightTool) {
    const mode = HEIGHT_MODES.find((h) => h.key === e.code || h.key.replace('Digit', 'Numpad') === e.code);
    if (mode && !e.shiftKey && !e.altKey) return `height:${mode.id}`;
    if (e.code === 'Equal' || e.code === 'NumpadAdd') return 'layersUp';
    if (e.code === 'Minus' || e.code === 'NumpadSubtract') return 'layersDown';
  }
  switch (e.code) {
    case 'KeyR':
      return 'draft';
    case 'Space':
      return 'pause';
    case 'Escape':
      return 'escape';
    case 'KeyT':
    case 'Enter':
      return 'chat';
    case 'KeyV':
      return 'view';
    case 'KeyX':
      return 'xray';
    case 'PageUp':
      return 'levelUp';
    case 'PageDown':
      return 'levelDown';
    case 'KeyQ':
      return 'rotateCcw';
    case 'KeyE':
      return 'rotateCw';
    case 'Equal':
    case 'NumpadAdd':
      return buildTool ? 'heightUp' : null;
    case 'Minus':
    case 'NumpadSubtract':
      return buildTool ? 'heightDown' : null;
    case 'Slash':
      return e.shiftKey ? 'help' : null;
    case 'Home':
      return 'focus';
    case 'KeyF':
      // Shift+F: full screen (F alone is the Forbid tool; the browser's F11 shows the whole page, not the colony).
      if (e.shiftKey) return 'fullscreen';
      break;
  }
  const tool = DESIGNATE_TOOLS.find((t) => t.key === e.code);
  if (tool && !e.shiftKey) return `tool:${tool.id}`;
  const tab = TAB_KEYS[e.code];
  if (tab) return `tab:${tab}`;
  return null;
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
