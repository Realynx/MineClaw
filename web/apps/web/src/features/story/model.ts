/**
 * Story Mode's client model: the colony state merged from REST + `story` deltas, the selection (colony pawns, AI bodies,
 * chests, a workbench), the Work and Schedule grids' edits, and the mixed-selection dispatch (pawns → director requests,
 * AI bodies → one directive with the same payload). Pure functions, unit-tested.
 */
import {
  STORY_WORK_TYPES,
  type StoryAiBody,
  type StoryColonyState,
  type StoryDirectiveRequest,
  type StoryDirectiveSpec,
  type StoryPawn,
  type StoryScheduleBlock,
  type StorySpeed,
} from '@mineclaw/contracts';

type Key = string | number;
type V3 = [number, number, number];
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

// ------------------------------------------------------------------ colony state

function mergeBy<T>(
  prev: readonly T[] | null | undefined,
  next: readonly T[] | null | undefined,
  key: (t: T) => string,
  removed: ReadonlyArray<unknown> | null | undefined,
): T[] {
  const map = new Map<string, T>();
  for (const t of prev ?? []) map.set(key(t), t);
  for (const t of next ?? []) map.set(key(t), t);
  for (const r of removed ?? []) map.delete(Array.isArray(r) ? k3(r as number[]) : String(r));
  return [...map.values()];
}

type Ghosts = NonNullable<StoryColonyState['ghosts']>;
type Ghost = Ghosts['blocks'][number];

const keySet = (keys: ReadonlyArray<unknown> | null | undefined) => new Set((keys ?? []).map(String));

/**
 * Where each held ghost sits in its ghosts' `blocks`: build → cell → position. It belongs to the one ghosts object it was made
 * for and moves on to the merged one, so a delta's few cells are merged without keying every held ghost again (a strip mine
 * holds tens of thousands). A ghosts object without one (a whole state, or one already merged once) is indexed on first use.
 */
type GhostIndex = Map<string, Map<number | string, number>>;
const ghostIndexes = new WeakMap<Ghosts, GhostIndex>();

const CELL_SPAN = 2097152; // 2^21: |x| and |z| below it pack into one safe integer with y
/** A cell's key inside its build: one number while the coordinates fit (no string per ghost), else "x,y,z". */
function cellKey(x: number, y: number, z: number): number | string {
  const fits =
    Number.isInteger(x) &&
    Number.isInteger(y) &&
    Number.isInteger(z) &&
    Math.abs(x) < CELL_SPAN &&
    Math.abs(z) < CELL_SPAN &&
    y >= -128 &&
    y < 384;
  return fits ? ((x + CELL_SPAN) * 2 * CELL_SPAN + (z + CELL_SPAN)) * 512 + (y + 128) : `${x},${y},${z}`;
}

function cellsOf(index: GhostIndex, build: string): Map<number | string, number> {
  let cells = index.get(build);
  if (!cells) index.set(build, (cells = new Map<number | string, number>()));
  return cells;
}

/** Adds a ghost: over the held one of its build and cell (in its place), else at the end. */
function putGhost(index: GhostIndex, blocks: Ghost[], g: Ghost): void {
  const cells = cellsOf(index, String(g[5]));
  const k = cellKey(g[0], g[1], g[2]);
  const at = cells.get(k);
  if (at === undefined) {
    cells.set(k, blocks.length);
    blocks.push(g);
  } else blocks[at] = g;
}

function indexGhosts(list: readonly Ghost[]): { index: GhostIndex; blocks: Ghost[] } {
  const index: GhostIndex = new Map();
  const blocks: Ghost[] = [];
  for (const g of list) putGhost(index, blocks, g);
  return { index, blocks };
}

/** The held position of a removed ghost named "<build>|x,y,z" (the delta's `removed.ghosts`), if it is held. */
function removedAt(index: GhostIndex, name: string): number | undefined {
  const bar = name.lastIndexOf('|');
  const xyz = name.slice(bar + 1).split(',');
  if (bar < 0 || xyz.length !== 3) return undefined;
  const [x, y, z] = xyz.map(Number) as [number, number, number];
  // Only the exact spelling a held ghost has names it (as "<build>|x,y,z" did before the index).
  if (`${x},${y},${z}` !== name.slice(bar + 1)) return undefined;
  return index.get(name.slice(0, bar))?.get(cellKey(x, y, z));
}

/**
 * The build queue's ghosts after a delta. The bot sends only the ghosts that changed after `since` (a full reply: all of
 * them), so a delta's ghosts are merged by build and cell (a changed state replaces the old one in place), a removed
 * build's ghosts go with it, and `removedGhosts` ("<build>|x,y,z": a preserved zone's repaired cell) go one by one. A ghost
 * sent in the same delta wins over its removal (it came back). Nothing changed → the same object (the map's holograms rebuild
 * only when it changes).
 */
export function mergeGhosts(
  prev: StoryColonyState['ghosts'],
  next: StoryColonyState['ghosts'],
  removedBuilds?: ReadonlyArray<unknown> | null,
  removedGhosts?: ReadonlyArray<string> | null,
): StoryColonyState['ghosts'] {
  if (!prev) return next;
  const fresh = next?.blocks ?? [];
  if (!fresh.length && !removedBuilds?.length && !removedGhosts?.length) return prev;
  const held = ghostIndexes.get(prev);
  let { index, blocks } = held ? { index: held, blocks: prev.blocks.slice() } : indexGhosts(prev.blocks);
  const gone = keySet(removedBuilds);
  const drop = new Set<number>();
  for (const b of gone) for (const at of index.get(b)?.values() ?? []) drop.add(at);
  for (const name of removedGhosts ?? []) {
    const at = removedAt(index, String(name));
    if (at !== undefined) drop.add(at);
  }
  if (!fresh.length && !drop.size) return prev;
  // The index moves to the merged ghosts (it is changed below): `prev` indexes itself again if it is ever merged again.
  ghostIndexes.delete(prev);
  if (drop.size) ({ index, blocks } = indexGhosts(blocks.filter((_, i) => !drop.has(i))));
  for (const g of fresh) if (!gone.has(String(g[5]))) putGhost(index, blocks, g);
  const merged = { rev: Math.max(prev.rev, next?.rev ?? 0), blocks };
  ghostIndexes.set(merged, index);
  return merged;
}

/** Applies a delta (or a whole state) to what the client holds. Older revisions are ignored. */
export function mergeColony(prev: StoryColonyState | undefined, d: StoryColonyState): StoryColonyState {
  if (!prev || d.full || d.rev < (prev.rev ?? 0) - 1000) return d;
  return d.rev <= prev.rev ? withTime(prev, d) : mergeDelta(prev, d);
}

/**
 * Nothing new in the colony, but the server's clock moved on: the time fields are always current (a quiet or paused colony keeps
 * its revision while the world time runs). The same object when the time didn't move either.
 */
function withTime(prev: StoryColonyState, d: StoryColonyState): StoryColonyState {
  const time = d.timeOfDay ?? null;
  if (time === null || (time === prev.timeOfDay && (d.timeFrozen ?? null) === (prev.timeFrozen ?? null))) return prev;
  return {
    ...prev,
    timeOfDay: time,
    hour: d.hour ?? prev.hour,
    timeFrozen: d.timeFrozen ?? null,
    at: d.at ?? prev.at,
  };
}

/** A newer delta: its fields over the held ones, its lists merged by id (removed ids dropped). */
function mergeDelta(prev: StoryColonyState, d: StoryColonyState): StoryColonyState {
  const rm = d.removed ?? {};
  const id = (x: { id: Key }) => String(x.id);
  const alerts = mergeBy(prev.alerts, d.alerts, (a) => String(a.id ?? `${a.at}|${a.text}`), rm.alerts).slice(-40);
  return {
    ...prev,
    ...Object.fromEntries(Object.entries(d).filter(([, v]) => v !== undefined && v !== null)),
    full: false,
    pawns: mergeBy(prev.pawns, d.pawns, (p) => p.name, rm.pawns),
    zones: mergeBy(prev.zones, d.zones, id, rm.zones),
    designations: mergeBy(prev.designations, d.designations, id, rm.designations),
    orders: mergeBy(prev.orders, d.orders, (o) => String(o.id ?? ''), rm.orders),
    alerts,
    builds: mergeBy(prev.builds, d.builds, id, rm.builds),
    storages: mergeBy(prev.storages, d.storages, (s) => k3(s.chest), rm.storages),
    workbenches: mergeBy(prev.workbenches, d.workbenches, (w) => k3(w.pos), rm.workbenches),
    directives: mergeBy(prev.directives, d.directives, id, rm.directives).slice(-60),
    ai: d.ai ?? prev.ai,
    ghosts: mergeGhosts(prev.ghosts, d.ghosts, rm.builds, rm.ghosts),
    categories: d.categories ?? prev.categories,
    removed: null,
  };
}

// ------------------------------------------------------------------ selection

export interface Selection {
  /** Colony pawn names and AI body names, in click order. */
  bodies: string[];
  /** Selected chests (storage editing). */
  chests: V3[];
  /** A selected workbench (bills). */
  bench: V3 | null;
  /** A selected bed (its foot block, as the colony lists it). */
  bed?: V3 | null;
  /** A selected (failed) designation, by id; `cluster` = the ids its map label stands for. */
  designation?: { id: string | number; cluster: Array<string | number> } | null;
}

export const EMPTY_SELECTION: Selection = { bodies: [], chests: [], bench: null };

/** A click on a body: plain = only it; Shift = add; Ctrl = toggle. */
export function clickBody(sel: Selection, name: string, mods: { shift?: boolean; ctrl?: boolean } = {}): Selection {
  if (mods.ctrl)
    return {
      ...EMPTY_SELECTION,
      bodies: sel.bodies.includes(name) ? sel.bodies.filter((n) => n !== name) : [...sel.bodies, name],
    };
  if (mods.shift) return { ...EMPTY_SELECTION, bodies: sel.bodies.includes(name) ? sel.bodies : [...sel.bodies, name] };
  return { ...EMPTY_SELECTION, bodies: [name] };
}

/** A drag box over bodies: replaces the selection (Shift adds). An empty box with no Shift clears. */
export function boxBodies(sel: Selection, names: readonly string[], add: boolean): Selection {
  if (add) return { ...EMPTY_SELECTION, bodies: [...new Set([...sel.bodies, ...names])] };
  return { ...EMPTY_SELECTION, bodies: [...names] };
}

export function clickChest(sel: Selection, chest: V3, add: boolean): Selection {
  const has = sel.chests.some((c) => k3(c) === k3(chest));
  if (add)
    return { ...EMPTY_SELECTION, chests: has ? sel.chests.filter((c) => k3(c) !== k3(chest)) : [...sel.chests, chest] };
  return { ...EMPTY_SELECTION, chests: [chest] };
}

/** Splits a selection into colony pawns and AI bodies. */
export function splitBodies(
  state: StoryColonyState | undefined,
  names: readonly string[],
): { pawns: string[]; ai: string[] } {
  const pawns = new Set((state?.pawns ?? []).map((p) => p.name));
  const ai = new Set((state?.ai ?? []).map((a) => a.name));
  return { pawns: names.filter((n) => pawns.has(n)), ai: names.filter((n) => ai.has(n) && !pawns.has(n)) };
}

// ------------------------------------------------------------------ the top bar

/** AI bodies in bar order: main, then each hive followed by its subhive bodies. */
export function orderAi(ai: readonly StoryAiBody[]): StoryAiBody[] {
  const main = ai.filter((a) => a.kind === 'main');
  const hives = ai.filter((a) => a.kind === 'hive');
  const subs = ai.filter((a) => a.kind !== 'main' && a.kind !== 'hive');
  const out: StoryAiBody[] = [...main];
  for (const h of hives) out.push(h, ...subs.filter((s) => s.owner === h.name));
  out.push(...subs.filter((s) => !out.includes(s)));
  return out;
}

/** Pawn bar order: the user's saved order first (drag to reorder), new pawns after. */
export function orderPawns(pawns: readonly StoryPawn[], saved: readonly string[]): StoryPawn[] {
  const rank = new Map(saved.map((n, i) => [n, i]));
  return [...pawns].sort(
    (a, b) => (rank.get(a.name) ?? 1e6 + pawns.indexOf(a)) - (rank.get(b.name) ?? 1e6 + pawns.indexOf(b)),
  );
}

/** Moves `name` to sit before `before` (null = the end). */
export function reorderNames(order: readonly string[], name: string, before: string | null): string[] {
  const rest = order.filter((n) => n !== name);
  const i = before === null ? rest.length : rest.indexOf(before);
  rest.splice(i < 0 ? rest.length : i, 0, name);
  return rest;
}

export type PawnMood = 'danger' | 'idle' | 'busy' | 'drafted' | 'offline';

/** Hurt (8 health or less), starving (4 food or less) or fleeing. */
const inDanger = (p: Pick<StoryPawn, 'hp' | 'food' | 'state'>) =>
  (p.hp ?? 20) <= 8 || (p.food ?? 20) <= 4 || p.state === 'fleeing';

/** The indicator a portrait shows. */
export function pawnMood(p: Pick<StoryPawn, 'hp' | 'food' | 'state' | 'drafted' | 'online'>): PawnMood {
  if (p.online === false || p.state === 'offline') return 'offline';
  if (inDanger(p)) return 'danger';
  if (p.drafted) return 'drafted';
  if (!p.state || p.state === 'idle') return 'idle';
  return 'busy';
}

// ------------------------------------------------------------------ work grid

/** One click on a Work cell: off → 1 → 2 → 3 → 4 → off (right click / Shift goes the other way). */
export function cyclePriority(v: number, back = false): number {
  const order = [0, 1, 2, 3, 4];
  const i = Math.max(0, order.indexOf(v));
  return order[(i + (back ? order.length - 1 : 1)) % order.length]!;
}

/** Skill → cell tint strength 0..1. */
export function skillTint(level: number | null | undefined): number {
  return Math.max(0, Math.min(1, (level ?? 0) / 20));
}

export type SortKey = { col: string; dir: 1 | -1 } | null;

/** Sorts Work rows by a work column's skill (or by name). */
export function sortPawns(pawns: readonly StoryPawn[], key: SortKey): StoryPawn[] {
  if (!key) return [...pawns];
  return [...pawns].sort((a, b) => {
    if (key.col === 'name') return a.display.localeCompare(b.display) * key.dir;
    const d = (a.skills[key.col]?.level ?? 0) - (b.skills[key.col]?.level ?? 0);
    return (d || a.display.localeCompare(b.display)) * -key.dir;
  });
}

/** The next sort when a header is clicked: descending, ascending, off. */
export function nextSort(cur: SortKey, col: string): SortKey {
  if (!cur || cur.col !== col) return { col, dir: 1 };
  return cur.dir === 1 ? { col, dir: -1 } : null;
}

export const WORK_LABELS: Record<string, string> = {
  guard: 'Guard',
  doctor: 'Doctor',
  cook: 'Cook',
  hunt: 'Hunt',
  fish: 'Fish',
  build: 'Build',
  farm: 'Farm',
  mine: 'Mine',
  chop: 'Chop',
  craft: 'Craft',
  haul: 'Haul',
};
export const WORK_TYPES = STORY_WORK_TYPES;

// ------------------------------------------------------------------ schedule grid

export const SCHEDULE_BRUSHES: ReadonlyArray<{ id: StoryScheduleBlock; label: string; key: string }> = [
  { id: 'work', label: 'Work', key: 'work' },
  { id: 'sleep', label: 'Sleep', key: 'sleep' },
  { id: 'anything', label: 'Anything', key: 'anything' },
  { id: 'free', label: 'Free time', key: 'free' },
];

/** Cells painted in one drag: pawn → hours. Dragging across rows paints the rectangle the drag spans. */
export function paintSpan(
  rows: readonly string[],
  from: { row: number; hour: number },
  to: { row: number; hour: number },
): Map<string, number[]> {
  const out = new Map<string, number[]>();
  const r1 = Math.min(from.row, to.row),
    r2 = Math.max(from.row, to.row);
  const h1 = Math.min(from.hour, to.hour),
    h2 = Math.max(from.hour, to.hour);
  for (let r = r1; r <= r2; r++) {
    const name = rows[r];
    if (!name) continue;
    out.set(
      name,
      Array.from({ length: h2 - h1 + 1 }, (_, i) => h1 + i),
    );
  }
  return out;
}

/** The schedule requests a paint produces: one per distinct pattern (pawns with the same hours share a request). */
export function paintRequests(
  cells: Map<string, number[]>,
  brush: StoryScheduleBlock,
): Array<{ pawns: string[]; hours: Record<string, StoryScheduleBlock> }> {
  const groups = new Map<string, string[]>();
  for (const [pawn, hours] of cells) {
    const k = hours.join(',');
    groups.set(k, [...(groups.get(k) ?? []), pawn]);
  }
  return [...groups].map(([k, pawns]) => ({ pawns, hours: Object.fromEntries(k.split(',').map((h) => [h, brush])) }));
}

/** A copied schedule pasted onto pawns: one request with all 24 hours. */
export function pasteRequest(
  schedule: readonly string[],
  pawns: readonly string[],
): { pawns: string[]; hours: Record<string, StoryScheduleBlock> } {
  return {
    pawns: [...pawns],
    hours: Object.fromEntries(
      schedule.slice(0, 24).map((b, h) => [String(h), (b as StoryScheduleBlock) ?? 'anything']),
    ),
  };
}

/** A local preview of a paint (so the grid updates under the brush before the server answers). */
export function applyPaint(schedule: readonly string[], hours: readonly number[], brush: StoryScheduleBlock): string[] {
  const out = [...schedule];
  while (out.length < 24) out.push('anything');
  for (const h of hours) out[h] = brush;
  return out;
}

// ------------------------------------------------------------------ mixed-selection dispatch

export type DirectorCall =
  | { route: 'story.designations'; body: Record<string, unknown> }
  | { route: 'story.orders'; body: Record<string, unknown> }
  | { route: 'story.builds'; body: Record<string, unknown> }
  | { route: 'story.bills'; body: Record<string, unknown> };

export interface Dispatch {
  director: DirectorCall | null;
  directive: StoryDirectiveRequest | null;
  /** "3 pawns ordered · sent to maid_chan (AI)". */
  summary: string;
}

const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? '' : 's'}`;

/**
 * What one action does with the selection: the colony's director gets its request (designations are colony-wide, so they go
 * to the director even with no pawn selected; orders need pawns), AI bodies in the selection get one directive with the same
 * payload. `verb` words the toast ("ordered", "designated").
 */
export function dispatch(
  sel: { pawns: readonly string[]; ai: readonly string[] },
  director: DirectorCall | null,
  directive: StoryDirectiveSpec | null,
  requestId: string,
  verb = 'ordered',
): Dispatch {
  const needsPawns = director?.route === 'story.orders';
  const d = directorFor(sel, director);
  const dir = directive && sel.ai.length ? { bots: [...sel.ai], directive, requestId } : null;
  const parts: string[] = [];
  if (d) parts.push(needsPawns ? `${plural(sel.pawns.length, 'pawn')} ${verb}` : `colony: ${verb}`);
  if (dir) parts.push(`sent to ${sel.ai.join(', ')} (AI)`);
  return { director: d, directive: dir, summary: parts.join(' · ') || 'nothing selected to do that' };
}

/** The director's call when it applies: designations always, orders only with pawns selected. */
function directorFor(sel: { pawns: readonly string[] }, director: DirectorCall | null): DirectorCall | null {
  if (!director) return null;
  return director.route !== 'story.orders' || sel.pawns.length > 0 ? director : null;
}

export function directiveStatusTone(status: string): 'info' | 'ok' | 'warn' | 'danger' | 'muted' {
  switch (status) {
    case 'sent':
      return 'info';
    case 'acknowledged':
      return 'warn';
    case 'done':
      return 'ok';
    case 'failed':
      return 'danger';
    default:
      return 'muted';
  }
}

const SPEED_ORDER: readonly StorySpeed[] = ['normal', 'fast', 'faster'];

/**
 * The slowest speed an online pawn actually runs at (a body the server kept pushing back steps itself down), or null when every
 * online pawn runs at what was asked (docs/web/story-mode.md "Speed").
 */
export function slowestSpeed(pawns: readonly StoryPawn[]): StorySpeed | null {
  let slowest: StorySpeed | null = null;
  for (const p of pawns) {
    const s = p.speed;
    if (!p.online || !s || s.multiplier >= s.requested - 0.001) continue;
    if (slowest === null || SPEED_ORDER.indexOf(s.level) < SPEED_ORDER.indexOf(slowest)) slowest = s.level;
  }
  return slowest;
}
