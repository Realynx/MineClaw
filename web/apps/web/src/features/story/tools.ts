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
  if (tool.startsWith('furniture:')) return { kind: 'furniture', blocks: [a], material: tool.slice('furniture:'.length), facing: o.facing };
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
  | `tool:${DesignateId}`
  | `tab:${string}`;

/** Bottom tab hotkeys. */
export const TAB_KEYS: Record<string, string> = { KeyB: 'architect', KeyP: 'work', KeyY: 'schedule', KeyU: 'queue', KeyI: 'colony', KeyO: 'directives' };

/**
 * A key on the map → its action, or null (WASD/arrows pan in the frame loop; 1–9 stay free). Typing in a field never reaches
 * here (the caller checks `isTyping` first).
 */
export function keyAction(e: { code: string; key: string; shiftKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; altKey?: boolean }, buildTool: boolean): KeyAction | null {
  if (e.ctrlKey || e.metaKey) return null;
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
