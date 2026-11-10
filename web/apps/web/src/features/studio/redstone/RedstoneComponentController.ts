/**
 * Placed components in a redstone design, outside React. An instance owns every cell of its box: the layout's blocks,
 * turned and mirrored, and air in the rest. Placing, moving, turning, mirroring, renaming and removing one is one undoable
 * edit (`Studio.editWithHistory` with the instances as its undo effect), which reaches the bot like any other edit. A
 * hand edit of an owned cell (a key, a brush, the Configure tool, the AI) detaches that instance: its cells stay as plain
 * cells. Boxes keep one free cell between them; snapping puts an input port on another component's output port, in that
 * gap, with one dust there. Duplicating, combining (selected instances into one of the design's own components that
 * remembers its parts) and ungrouping are one undo step each too. A drag asks `check` and `snapMoveCorner` every frame
 * and changes nothing until its one `move`.
 *
 * The instances and the custom definitions they use are saved with the design in `redstoneAuthoring` (through the
 * shared store, so the debugger's keys stay). Unsaved work is also mirrored to this browser per session, and that copy
 * comes back on the next open only when every cell it owns still matches the canvas.
 */
import {
  LIBRARY,
  type ComponentDef,
  type ComponentLayout,
  type Port,
  type Pos,
  type RedstoneVersion,
  type Side,
} from '@mineclaw/redstone';
import { EARTH, inside, keyOf, type OpGroup, posOf, type Cell, type Selection } from '../canvas';
import type { Studio } from '../studio';
import { authoringStoreOf, type RedstoneAuthoringStore } from './authoring';
import {
  browserStorage,
  MAX_COMPONENTS,
  MAX_CUSTOM_DEFS,
  MAX_DEF_SIDE,
  newDefId,
  readCustomDefs,
  readInstances,
  writeDef,
  writeInstance,
  type ComponentInstance,
  type ComponentPart,
  type CustomDef,
  type StorageLike,
} from './componentData';
import {
  besides,
  boxCornerAfter,
  composePlacement,
  layoutOf,
  MOVE_SNAP_REACH,
  placeLayout,
  quarter,
  snapCorner,
  snapPlacement,
  tooClose,
  inBox,
  type Box,
  type Mirror,
  type Outlet,
  type PickedPort,
  type Placed,
  type SnapTarget,
} from './ports';

const DUST = 55;
const NO_OUTLET = 'No output port near there to join: place it by its corner instead.';

/** What a port is joined to: another component's port on the same cell, or dust there. */
export type PortLink = { to: 'port'; id: string; port: string } | { to: 'dust' };

export interface PortState extends Port {
  link: PortLink | null;
}

/** An outliner row's facts about a placed component: its blocks (not air) and its ports as they are. */
export interface ComponentRow {
  blocks: number;
  ports: PortState[];
}

/** Port cells to mark: amber where a port is open, green where it is joined. */
export interface PortMarks {
  open: Pos[];
  joined: Pos[];
}

export interface MoveOptions {
  /** The shared port cell of a snap: it takes one dust unless it has some. */
  joint?: Pos | null;
  turns?: number;
  mirror?: Mirror;
  /** Moves with the same group are one undo step (a held arrow key). */
  group?: string;
}

interface ChangeOptions {
  /** Cells the new boxes take in become the component's own: leaving them later gives bare cells, not a copy. */
  absorb?: boolean;
  /** Cells the boxes give up keep what they hold now instead of what was under them. */
  release?: boolean;
  group?: string;
}

/** "2 of 5 saved components no longer match the circuit and were opened as plain blocks. The saved revision still has them." */
function leftOut(dropped: number, total: number): string {
  const one = dropped === 1;
  const who = total === 1 ? 'The saved component' : `${dropped} of ${total} saved components`;
  return (
    `${who} no longer ${one ? 'matches' : 'match'} the circuit and ${one ? 'was' : 'were'} opened as plain blocks.` +
    ` The saved revision still has ${one ? 'it' : 'them'}.`
  );
}

/**
 * The turns and mirror after mirroring across x (east and west swap) or z, as seen now. The saved form mirrors first and
 * then turns, so after an odd turn a mirror seen across x is one across z in the layout's own frame.
 */
export function mirrorAsSeen(turns: number, mirror: Mirror, axis: 'x' | 'z'): { turns: number; mirror: Mirror } {
  const own: 'x' | 'z' = turns % 2 === 1 ? (axis === 'x' ? 'z' : 'x') : axis;
  // Mirroring twice across one axis is no mirror; across both it is a half turn.
  if (mirror === null) return { turns, mirror: own };
  if (mirror === own) return { turns, mirror: null };
  return { turns: quarter(turns + 2), mirror: null };
}

const add = (a: Pos, b: Pos): Pos => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const samePos = (a: Pos, b: Pos): boolean => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

type Values = Map<number, number>;

interface State {
  components: readonly ComponentInstance[];
  customDefs: readonly CustomDef[];
  /** What each owned cell held before a component took it, put back when the component leaves it. */
  base: Values;
}

export interface ComponentSnapshot {
  components: readonly ComponentInstance[];
  customDefs: readonly CustomDef[];
  /** Why the last change was refused; null after one that worked. */
  error: string | null;
  /** The opened design came back from this browser's copy of unsaved work. */
  recovered: boolean;
}

/** A definition as the studio can place it in this design's version. */
export interface PlaceableDef {
  id: string;
  name: string;
  layout: ComponentLayout;
  /** A custom definition drawn for the other game version. */
  otherVersion: RedstoneVersion | null;
  builtIn: ComponentDef | null;
}

export interface PlaceOptions {
  turns?: number;
  mirror?: Mirror;
  label?: string;
  /** Join an output port near `at` instead of placing the box's corner there. */
  snap?: boolean;
}

/** The value a cell has in the studio's authored terms: id * 16 + meta, air 0, undisturbed ground -1. */
const valueOf = (c: Cell): number => (c[3] === EARTH ? -1 : c[3] * 16 + (c[4] & 15));
const cellOf = (k: number, v: number): Cell => {
  const [x, y, z] = posOf(k);
  return v < 0 ? [x, y, z, EARTH, 0] : [x, y, z, v >> 4, v & 15];
};
const cloneState = (s: State): State => ({ components: s.components, customDefs: s.customDefs, base: new Map(s.base) });

type ComponentStudio = Pick<
  Studio,
  | 'session'
  | 'canvas'
  | 'history'
  | 'onCells'
  | 'editWithHistory'
  | 'busy'
  | 'setRedstoneAuthoring'
  | 'subscribe'
  | 'snapshot'
>;

/** This browser's copies of unsaved components, one per session: `<prefix><owner>:<session>:<version>`. */
const COPY_PREFIX = 'mineclaw.redstone.components.v1:';
/** How many copies of other sessions an account keeps (the newest); older ones are dropped when a design opens. */
export const KEEP_COPIES = 8;

/** When a copy was written (0 for one that does not say). */
function writtenAt(text: string | null): number {
  try {
    const at = (JSON.parse(text ?? 'null') as { at?: unknown } | null)?.at;
    return typeof at === 'number' && Number.isFinite(at) ? at : 0;
  } catch {
    return 0;
  }
}

export class RedstoneComponentController {
  private state: State = { components: [], customDefs: [], base: new Map() };
  /** Owned cell → the value its instance wants there. */
  private owned: Values = new Map();
  /** Owned cell → the instance that owns it. */
  private owner = new Map<number, string>();
  private placed: ReadonlyMap<string, Placed> = new Map();
  /** Port cell → the placed ports on it. */
  private portCells = new Map<number, Array<{ id: string; port: Port }>>();
  /** Shapes about the origin by definition, turns and mirror: a drag works one out once, then only moves it. */
  private readonly shapes = new Map<string, Placed | null>();
  private shapesFor: readonly CustomDef[] = [];
  /** What the reader should know about how the components opened (saved ones left out), or null. */
  restoreNote: string | null = null;
  private readonly listeners = new Set<() => void>();
  private readonly unsubscribe: () => void;
  private readonly unsubscribeStudio: () => void;
  /** This session's copy is in the browser (it holds unsaved work). */
  private stored = false;
  private readonly store: RedstoneAuthoringStore;
  private readonly storageKey: string;
  private readonly version: RedstoneVersion;
  /** The undo groups of hand edits that detached instances, with the instances before and after. */
  private readonly detaches = new WeakMap<OpGroup, { before: State; after: State }>();
  private applying = false;
  private error: string | null = null;
  private recovered = false;
  private snap: ComponentSnapshot;
  /** Counts canvas changes: a port's link can change with no change to the components. */
  private cellsEpoch = 0;
  /** The outliner's rows as last worked out, and for which snapshot and canvas. */
  private rowCache: { snap: ComponentSnapshot; epoch: number; rows: Map<string, ComponentRow> } | null = null;

  constructor(
    private readonly studio: ComponentStudio,
    private readonly storage: StorageLike | null = browserStorage(),
    private readonly now: () => number = Date.now,
  ) {
    const s = studio.session;
    // The page's own rule for a session's version.
    this.version = s.version === '1.12.2' ? '1.12.2' : 'b1.8.1';
    this.storageKey = `${COPY_PREFIX}${s.owner}:${s.id}:${this.version}`;
    this.store = authoringStoreOf(studio);
    this.state = this.restore();
    this.index();
    this.snap = this.build();
    this.save();
    this.unsubscribe = studio.onCells(this.canvasChanged);
    this.unsubscribeStudio = studio.subscribe(this.studioChanged);
    this.prune(`${COPY_PREFIX}${s.owner}:`);
    this.studioChanged();
  }

  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  };

  snapshot = (): ComponentSnapshot => this.snap;

  dispose(): void {
    this.unsubscribe();
    this.unsubscribeStudio();
    this.listeners.clear();
  }

  // ------------------------------------------------------------------ reading

  /** The library's components and this design's own, as they can be placed here. */
  defs(): PlaceableDef[] {
    const out: PlaceableDef[] = [];
    for (const d of LIBRARY) {
      const layout = d.layouts[this.version];
      if (layout) out.push({ id: d.id, name: d.name, layout, otherVersion: null, builtIn: d });
    }
    for (const d of this.state.customDefs) out.push(this.custom(d));
    return out;
  }

  /** A definition by id: the library's in this version, or one of the design's own. */
  defOf(id: string): PlaceableDef | null {
    return this.defIn(id, this.state.customDefs);
  }

  private defIn(id: string, customDefs: readonly CustomDef[]): PlaceableDef | null {
    const own = customDefs.find((d) => d.id === id);
    if (own) return this.custom(own);
    const d = LIBRARY.find((x) => x.id === id);
    const layout = d?.layouts[this.version];
    return d && layout ? { id: d.id, name: d.name, layout, otherVersion: null, builtIn: d } : null;
  }

  /** Where an instance's cells and ports are now. */
  placedOf(id: string): Placed | null {
    return this.placed.get(id) ?? null;
  }

  /** The instance whose box holds the cell. */
  atCell(at: Pos): ComponentInstance | null {
    const id = this.owner.get(keyOf(at[0], at[1], at[2]));
    return id ? (this.state.components.find((c) => c.id === id) ?? null) : null;
  }

  /** Every placed output port, as a snap target. */
  outlets(except?: string): Outlet[] {
    const out: Outlet[] = [];
    for (const [id, p] of this.placed)
      if (id !== except)
        for (const port of p.ports) if (port.kind === 'out') out.push({ at: port.at, side: port.side });
    return out;
  }

  /**
   * An instance's cells and ports about its corner, turned and mirrored as asked (as it is by default). Worked out once
   * per shape and shared: a drag's outline and every `check` reuse it, so it must not be changed.
   */
  shapeOf(id: string, turns?: number, mirror?: Mirror): Placed | null {
    const c = this.find(id);
    return c ? this.shape(c.def, turns ?? c.turns, mirror === undefined ? c.mirror : mirror) : null;
  }

  /**
   * Why the instance could not stand with its corner at `at` (turned and mirrored as given), or null when it could: the
   * rule a move applies, off the plot or too close to another component. Cheap enough for every frame of a drag: it
   * reads the prepared shape and the other boxes and writes nothing.
   */
  check(id: string, at: Pos, turns?: number, mirror?: Mirror): string | null {
    const c = this.find(id);
    if (!c) return `There is no placed component "${id}".`;
    return this.fits(c, this.shape(c.def, turns ?? c.turns, mirror === undefined ? c.mirror : mirror), at, id);
  }

  /** The instance's ports where they are now, each with what it is joined to. */
  portsOf(id: string): PortState[] {
    return (this.placed.get(id)?.ports ?? []).map((p) => ({ ...p, link: this.linkAt(p.at, id) }));
  }

  /**
   * Each placed component's blocks (cells that are not air) and its ports as they are, for the outliner. Worked out once
   * per change of the components or the canvas, so a page render for anything else (the aim, while flying) costs nothing.
   */
  rows(): ReadonlyMap<string, ComponentRow> {
    const cached = this.rowCache;
    if (cached && cached.snap === this.snap && cached.epoch === this.cellsEpoch) return cached.rows;
    const rows = new Map<string, ComponentRow>();
    for (const c of this.snap.components) {
      const blocks = this.placed.get(c.id)?.cells.filter((cell) => cell[3] > 0).length ?? 0;
      rows.set(c.id, { blocks, ports: this.portsOf(c.id) });
    }
    this.rowCache = { snap: this.snap, epoch: this.cellsEpoch, rows };
    return rows;
  }

  /** Every placed port, open or joined, each cell once. */
  portMarkers(): PortMarks {
    const marks: PortMarks = { open: [], joined: [] };
    const seen = new Set<number>();
    for (const [id, p] of this.placed)
      for (const port of p.ports) {
        const k = inside(port.at[0], port.at[1], port.at[2]) ? keyOf(port.at[0], port.at[1], port.at[2]) : null;
        if (k !== null && seen.has(k)) continue;
        if (k !== null) seen.add(k);
        (this.linkAt(port.at, id) ? marks.joined : marks.open).push(port.at);
      }
    return marks;
  }

  /** The instance's ports as they would be with its corner at `at` (a drag's preview); `joint` counts as joined. */
  previewPorts(id: string, at: Pos, turns?: number, mirror?: Mirror, joint?: Pos | null): PortMarks {
    const marks: PortMarks = { open: [], joined: [] };
    for (const p of this.shapeOf(id, turns, mirror)?.ports ?? []) {
      const cell = add(p.at, at);
      (this.linkAt(cell, id) || (joint && samePos(joint, cell)) ? marks.joined : marks.open).push(cell);
    }
    return marks;
  }

  /**
   * Where a dragged instance goes when one of its ports is near another component's port (an input onto an output, or an
   * output onto an input) or near the end of a dust line: the nearest such corner that the move rule takes, and the
   * shared cell. Null when nothing is in reach, so the drag keeps its own corner.
   */
  snapMoveCorner(id: string, at: Pos, turns?: number, mirror?: Mirror): { at: Pos; joint: Pos } | null {
    const c = this.find(id);
    const s = this.shapeOf(id, turns, mirror);
    if (!c || !s) return null;
    const targets: SnapTarget[] = [];
    for (const [other, p] of this.placed)
      if (other !== id) for (const port of p.ports) targets.push({ at: port.at, kind: port.kind, side: port.side });
    const canvas = this.studio.canvas;
    const seen = new Set<number>();
    const r = MOVE_SNAP_REACH;
    for (const port of s.ports) {
      const [px, py, pz] = add(port.at, at);
      for (let y = py - r; y <= py + r; y++)
        for (let z = pz - r; z <= pz + r; z++)
          for (let x = px - r; x <= px + r; x++) {
            const k = keyOf(x, y, z);
            if (seen.has(k) || !inside(x, y, z)) continue;
            seen.add(k);
            // Dust inside a box is the component's own; dust on a port cell is that port's, which snaps as a port. The
            // dragged one's own joints are skipped too: they would pull it back to where it stands on every short drag.
            if (canvas.id(x, y, z) !== DUST || this.owner.has(k) || this.portCells.get(k)?.length) continue;
            const side = this.dustEnd([x, y, z]);
            if (side !== false) targets.push({ at: [x, y, z], kind: 'dust', side });
          }
    }
    for (const snap of snapCorner(s.ports, at, targets, r))
      if (this.fits(c, s, snap.at, id) === null) return { at: snap.at, joint: snap.joint };
    return null;
  }

  /** A definition's cells and ports about the origin, turned and mirrored (a placing ghost's outline); shared, as shapeOf. */
  placeShape(defId: string, turns = 0, mirror: Mirror = null): Placed | null {
    return this.defOf(defId) ? this.shape(defId, turns, mirror) : null;
  }

  /**
   * What `place(defId, at, o)` would do, without writing anything: the box's corner (snapped onto an output port near
   * `at` with `o.snap`), the snap's shared cell, why it would be refused (null when it would not), and its ports open or
   * joined there. Cheap enough for every frame of a placing ghost. Null for an unknown definition.
   */
  previewPlace(
    defId: string,
    at: Pos,
    o: PlaceOptions = {},
  ): { at: Pos; joint: Pos | null; reason: string | null; ports: PortMarks } | null {
    const def = this.defOf(defId);
    if (!def) return null;
    const turns = quarter(o.turns ?? 0);
    const mirror = o.mirror ?? null;
    const s = this.shape(def.id, turns, mirror);
    const target = this.placeCorner(def, at, turns, mirror, o.snap ?? false);
    const corner = target?.at ?? [at[0], at[1], at[2]];
    const joint = target?.joint ?? null;
    // The order of place's own refusals.
    const reason =
      this.state.components.length >= MAX_COMPONENTS
        ? `A design holds at most ${MAX_COMPONENTS} components.`
        : !target
          ? NO_OUTLET
          : this.fits({ id: '', def: def.id, at: corner, turns, mirror }, s, corner, null);
    const ports: PortMarks = { open: [], joined: [] };
    for (const p of s?.ports ?? []) {
      const cell = add(p.at, corner);
      (this.linkAt(cell, '') || (joint && samePos(joint, cell)) ? ports.joined : ports.open).push(cell);
    }
    return { at: corner, joint, reason, ports };
  }

  // ------------------------------------------------------------------ changing

  /** Places a component with its box's min corner at `at` (or snapped to an output port near it). Null when refused. */
  place(defId: string, at: Pos, o: PlaceOptions = {}): ComponentInstance | null {
    const def = this.defOf(defId);
    if (!def) return this.refuse(`There is no component "${defId}" for ${this.version}.`);
    if (this.state.components.length >= MAX_COMPONENTS)
      return this.refuse(`A design holds at most ${MAX_COMPONENTS} components.`);
    const turns = quarter(o.turns ?? 0);
    const mirror = o.mirror ?? null;
    const target = this.placeCorner(def, at, turns, mirror, o.snap ?? false);
    if (!target) return this.refuse(NO_OUTLET);
    const corner = target.at;
    const extra: Cell[] = [];
    if (target.joint) {
      const [jx, jy, jz] = target.joint;
      if (this.studio.canvas.id(jx, jy, jz) !== DUST) extra.push([jx, jy, jz, DUST, 0]);
    }
    const instance: ComponentInstance = { id: this.nextId(), def: def.id, at: corner, turns, mirror };
    const label = o.label?.trim().slice(0, 40);
    if (label) instance.label = label;
    return this.change([...this.state.components, instance], `place ${def.name}`, this.state.customDefs, extra)
      ? instance
      : null;
  }

  /** Moves an instance's corner to `at` (turned and mirrored as given, as it is by default); a snap's joint takes dust. */
  move(id: string, at: Pos, o: MoveOptions = {}): boolean {
    const j = o.joint;
    const extra: Cell[] = j && this.studio.canvas.id(j[0], j[1], j[2]) !== DUST ? [[j[0], j[1], j[2], DUST, 0]] : [];
    return this.update(
      id,
      (c) => ({
        ...c,
        at: [at[0], at[1], at[2]],
        ...(o.turns === undefined ? {} : { turns: quarter(o.turns) }),
        ...(o.mirror === undefined ? {} : { mirror: o.mirror }),
      }),
      'move',
      extra,
      o.group,
    );
  }

  /** Turns an instance clockwise in place (its box keeps its min corner). */
  turn(id: string, by = 1): boolean {
    return this.update(id, (c) => ({ ...c, turns: quarter(c.turns + by) }), 'turn');
  }

  /**
   * Mirrors an instance across x (east and west swap) or z, as seen now. The saved form mirrors first and then turns, so
   * after an odd turn a mirror seen across x is one across z in the layout's own frame.
   */
  mirror(id: string, axis: 'x' | 'z'): boolean {
    return this.update(id, (c) => ({ ...c, ...mirrorAsSeen(c.turns, c.mirror, axis) }), 'mirror');
  }

  rename(id: string, label: string): boolean {
    const text = label.trim().slice(0, 40);
    return this.update(
      id,
      (c) => {
        const { label: _old, ...rest } = c;
        return text ? { ...rest, label: text } : rest;
      },
      'rename',
    );
  }

  /** Takes the component and its cells away: what was there before it was placed comes back. */
  remove(id: string): boolean {
    const c = this.state.components.find((x) => x.id === id);
    if (!c) return false;
    return this.change(
      this.state.components.filter((x) => x.id !== id),
      `remove ${this.nameOf(c)}`,
      this.state.customDefs,
    );
  }

  /** Leaves the cells as plain cells: the component is no longer one. */
  detach(id: string): boolean {
    const c = this.state.components.find((x) => x.id === id);
    if (!c) return false;
    const before = cloneState(this.state);
    const after: State = { ...cloneState(this.state), components: this.state.components.filter((x) => x.id !== id) };
    for (const cell of this.placed.get(id)?.cells ?? []) after.base.delete(keyOf(cell[0], cell[1], cell[2]));
    this.commit(before, after, [], `unlink ${this.nameOf(c)}`);
    return true;
  }

  /** A copy of an instance at `at`, or beside it where there is room (east first, then clockwise, further out). */
  duplicate(id: string, at?: Pos): ComponentInstance | null {
    const c = this.find(id);
    if (!c) return null;
    if (this.state.components.length >= MAX_COMPONENTS)
      return this.refuse(`A design holds at most ${MAX_COMPONENTS} components.`);
    const s = this.shape(c.def, c.turns, c.mirror);
    const corner = at ?? (s ? this.roomBeside(c, s) : null);
    if (!corner)
      return this.refuse(`There is no free room beside ${this.nameOf(c)} for a copy: place one by its corner.`);
    const copy: ComponentInstance = { ...writeInstance(c), id: this.nextId(), at: [corner[0], corner[1], corner[2]] };
    return this.change([...this.state.components, copy], `duplicate ${this.nameOf(c)}`, this.state.customDefs)
      ? copy
      : null;
  }

  /**
   * The selected instances as one component of the design's own: its box spans theirs, it keeps the cells between them
   * (the wires that join them), and its ports are their ports outside that box (one that joins two of them is inside).
   * The definition remembers the parts, so `ungroup` gives them back. The cells stay as they are; one undo step.
   */
  combine(ids: readonly string[], name = ''): ComponentInstance | null {
    const pick = new Set(ids);
    const members = this.state.components.filter((c) => pick.has(c.id));
    if (members.length < 2) return this.refuse('Select at least two components to combine.');
    if (this.state.customDefs.length >= MAX_CUSTOM_DEFS)
      return this.refuse(`A design holds at most ${MAX_CUSTOM_DEFS} of its own components.`);
    const boxes = members.map((m) => this.placed.get(m.id)!.box);
    const low = (i: number) => Math.min(...boxes.map((b) => b.min[i]!));
    const high = (i: number) => Math.max(...boxes.map((b) => b.max[i]!));
    const min: Pos = [low(0), low(1), low(2)];
    const max: Pos = [high(0), high(1), high(2)];
    const box: Box = { min, max };
    if ([0, 1, 2].some((i) => max[i]! - min[i]! + 1 > MAX_DEF_SIDE))
      return this.refuse(`A component is at most ${MAX_DEF_SIDE} blocks on a side.`);
    for (const c of this.state.components)
      if (!pick.has(c.id) && tooClose(box, this.placed.get(c.id)!.box))
        return this.refuse(`${this.nameOf(c)} lies in or beside the combined box: select it too, or move it away.`);
    const outward: PickedPort[] = [];
    for (const m of members)
      for (const p of this.placed.get(m.id)!.ports)
        if (!inBox(box, p.at) && !outward.some((o) => samePos(o.at, p.at)))
          outward.push({ name: p.name, kind: p.kind, at: p.at });
    // Two NOTs side by side both have an A: number the names that repeat, in the members' order.
    const count = new Map<string, number>();
    for (const p of outward) count.set(p.name, (count.get(p.name) ?? 0) + 1);
    const nth = new Map<string, number>();
    for (const p of outward)
      if (count.get(p.name)! > 1) {
        const n = (nth.get(p.name) ?? 0) + 1;
        nth.set(p.name, n);
        p.name = `${p.name.slice(0, 14)}${n}`;
      }
    const blocks = this.studio.canvas.cells().filter((c) => inBox(box, [c[0], c[1], c[2]]));
    const layout = layoutOf(box, blocks, outward);
    if (!layout) return this.refuse('These components cannot be combined.');
    const parts: ComponentPart[] = members.map((m) => ({
      def: m.def,
      at: [m.at[0] - min[0], m.at[1] - min[1], m.at[2] - min[2]],
      turns: m.turns,
      mirror: m.mirror,
      ...(m.label ? { label: m.label } : {}),
    }));
    const def = writeDef({
      id: newDefId(),
      name: name.trim().slice(0, 60) || 'combined',
      layout,
      version: this.version,
      parts,
    });
    const instance: ComponentInstance = {
      id: this.nextId(),
      def: def.id,
      at: [min[0], min[1], min[2]],
      turns: 0,
      mirror: null,
    };
    // Its box owns every cell, air included: undisturbed ground between the parts would be dug out.
    const canvas = this.studio.canvas;
    for (const c of placeLayout(layout, instance, this.version).cells)
      if (canvas.authoredState(keyOf(c[0], c[1], c[2])) !== valueOf(c))
        return this.refuse('These components reach into the ground: combine components that stand above it.');
    const first = this.state.components.findIndex((c) => pick.has(c.id));
    const next = this.state.components.filter((c) => !pick.has(c.id));
    next.splice(first, 0, instance);
    return this.change(next, `combine ${def.name}`, [...this.state.customDefs, def], [], { absorb: true })
      ? instance
      : null;
  }

  /**
   * The inverse of `combine`: the parts take the combined instance's place, turned and mirrored with it, and its cells stay
   * as they are (the wires between the parts become plain cells). A component that was never combined ungroups to plain
   * blocks, as an unlink does. The combined definition goes when nothing else uses it. One undo step; the new parts.
   */
  ungroup(id: string): ComponentInstance[] | null {
    const index = this.state.components.findIndex((c) => c.id === id);
    const c = this.state.components[index];
    if (!c) return null;
    const def = this.state.customDefs.find((d) => d.id === c.def);
    const parts = def?.parts;
    if (!def || !parts?.length || parts.some((p) => !this.defOf(p.def))) return this.detach(id) ? [] : null;
    if (this.state.components.length - 1 + parts.length > MAX_COMPONENTS)
      return this.refuse(`A design holds at most ${MAX_COMPONENTS} components.`);
    let n = this.nextNumber();
    const items = parts.map((p): ComponentInstance => {
      const size = this.shape(p.def, p.turns, p.mirror)!.box.max;
      const rel = boxCornerAfter(def.layout.size, { min: p.at, max: add(p.at, size) }, c.turns, c.mirror, this.version);
      const o = composePlacement(c, p);
      return {
        id: `c${n++}`,
        def: p.def,
        at: add(c.at, rel),
        turns: o.turns,
        mirror: o.mirror,
        ...(p.label ? { label: p.label } : {}),
      };
    });
    const next = [...this.state.components.slice(0, index), ...items, ...this.state.components.slice(index + 1)];
    const used = next.some((x) => x.def === def.id) || this.partOfOther(def.id);
    const customDefs = used ? this.state.customDefs : this.state.customDefs.filter((d) => d.id !== def.id);
    return this.change(next, `ungroup ${this.nameOf(c)}`, customDefs, [], { release: true }) ? items : null;
  }

  /** Adds a definition to the design (one the user made or took from the quick library). Its id when it worked. */
  addDef(def: CustomDef): string | null {
    const known = this.state.customDefs.find((d) => d.id === def.id);
    if (known) return known.id;
    if (this.state.customDefs.length >= MAX_CUSTOM_DEFS)
      return this.refuse(`A design holds at most ${MAX_CUSTOM_DEFS} of its own components.`);
    const own = writeDef(def);
    const before = cloneState(this.state);
    const after: State = { ...cloneState(this.state), customDefs: [...this.state.customDefs, own] };
    this.commit(before, after, [], `add component ${own.name}`);
    return own.id;
  }

  /** Another of the design's own definitions names `defId` among its parts. */
  private partOfOther(defId: string): boolean {
    return this.state.customDefs.some((d) => d.id !== defId && d.parts?.some((p) => p.def === defId));
  }

  /** Drops a definition the design no longer places. */
  removeDef(id: string): boolean {
    if (this.state.components.some((c) => c.def === id))
      return this.refuse('Remove or unlink the placed copies of this component first.') !== null;
    if (!this.state.customDefs.some((d) => d.id === id)) return false;
    // A combined component of the design is made of it: without it that one could no longer give its parts back.
    if (this.partOfOther(id)) return this.refuse('Another of your components is made from this one.') !== null;
    const before = cloneState(this.state);
    const after: State = { ...cloneState(this.state), customDefs: this.state.customDefs.filter((d) => d.id !== id) };
    this.commit(before, after, [], 'drop component');
    return true;
  }

  /**
   * A component from the cells in a selection's box, with ports picked just outside it; added to the design. Null when
   * the box is too big, empty, or a port is not on one of its sides.
   */
  saveSelection(name: string, sel: Selection, ports: readonly PickedPort[]): CustomDef | null {
    const box: Box = { min: sel.min, max: sel.max };
    if ([0, 1, 2].some((i) => sel.max[i]! - sel.min[i]! + 1 > MAX_DEF_SIDE))
      return this.refuse(`A component is at most ${MAX_DEF_SIDE} blocks on a side.`);
    const blocks = this.studio.canvas.cells().filter((c) => inBox(box, [c[0], c[1], c[2]]));
    if (blocks.length === 0) return this.refuse('The selection holds no blocks.');
    const layout = layoutOf(box, blocks, ports);
    if (!layout) return this.refuse('A port must be a cell just outside the selection, beside one of its four sides.');
    return this.newDef(name, layout);
  }

  /** A component from another saved design's cells (no ports: it has none to say where its wires join). */
  insertDesign(name: string, cells: readonly Cell[], version: RedstoneVersion): CustomDef | null {
    const blocks = cells.filter((c) => c[3] > 0);
    if (blocks.length === 0) return this.refuse('That design has no blocks.');
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (const c of blocks)
      for (let i = 0; i < 3; i++) {
        min[i] = Math.min(min[i]!, c[i]!);
        max[i] = Math.max(max[i]!, c[i]!);
      }
    if ([0, 1, 2].some((i) => max[i]! - min[i]! + 1 > MAX_DEF_SIDE))
      return this.refuse(`A component is at most ${MAX_DEF_SIDE} blocks on a side.`);
    const layout = layoutOf({ min: [min[0]!, min[1]!, min[2]!], max: [max[0]!, max[1]!, max[2]!] }, blocks, []);
    return layout ? this.newDef(name, layout, version) : null;
  }

  // ------------------------------------------------------------------ internals

  private custom(d: CustomDef): PlaceableDef {
    return {
      id: d.id,
      name: d.name,
      layout: d.layout,
      otherVersion: d.version && d.version !== this.version ? d.version : null,
      builtIn: null,
    };
  }

  private newDef(name: string, layout: ComponentLayout, version: RedstoneVersion = this.version): CustomDef | null {
    const def: CustomDef = { id: newDefId(), name: name.trim().slice(0, 60) || 'my component', layout, version };
    return this.addDef(def) ? def : null;
  }

  private nameOf(c: ComponentInstance): string {
    return c.label ?? this.defOf(c.def)?.name ?? c.def;
  }

  private nextId(): string {
    return `c${this.nextNumber()}`;
  }

  private nextNumber(): number {
    return 1 + Math.max(0, ...this.state.components.map((c) => Number(/^c(\d+)$/.exec(c.id)?.[1] ?? 0)));
  }

  private find(id: string): ComponentInstance | null {
    return this.state.components.find((c) => c.id === id) ?? null;
  }

  /** Where a placement's box goes: its corner at `at`, or snapped onto an output port near it (null when none is). */
  private placeCorner(
    def: PlaceableDef,
    at: Pos,
    turns: number,
    mirror: Mirror,
    snap: boolean,
  ): { at: Pos; joint: Pos | null } | null {
    if (!snap) return { at: [at[0], at[1], at[2]], joint: null };
    return snapPlacement(def.layout, turns, mirror, this.version, at, this.outlets());
  }

  private shape(def: string, turns: number, mirror: Mirror): Placed | null {
    if (this.shapesFor !== this.state.customDefs) {
      this.shapes.clear();
      this.shapesFor = this.state.customDefs;
    }
    const key = `${def}|${quarter(turns)}|${mirror ?? ''}`;
    let s = this.shapes.get(key);
    if (s === undefined) {
      const d = this.defOf(def);
      s = d ? placeLayout(d.layout, { at: [0, 0, 0], turns, mirror }, this.version) : null;
      if (this.shapes.size >= 64) this.shapes.clear();
      this.shapes.set(key, s);
    }
    return s;
  }

  /** Why `c` in shape `s` could not have its corner at `at`: off the plot, or too close to a component but `except`. */
  private fits(c: ComponentInstance, s: Placed | null, at: Pos, except: string | null): string | null {
    if (!s) return `There is no component "${c.def}" for ${this.version}.`;
    if (!at.every(Number.isInteger)) return 'Place components on whole block coordinates.';
    const box: Box = { min: add(s.box.min, at), max: add(s.box.max, at) };
    if (!inside(box.min[0], box.min[1], box.min[2]) || !inside(box.max[0], box.max[1], box.max[2]))
      return `${this.nameOf(c)} does not fit on the plot there.`;
    for (const [otherId, other] of this.placed)
      if (otherId !== except && tooClose(box, other.box))
        return `${this.nameOf(c)} would touch ${this.nameOf(this.find(otherId)!)}: keep one free cell between components.`;
    return null;
  }

  /** Room for a copy of `c` beside it: a free cell's gap east, south, west or north, then twice that, up to four times. */
  private roomBeside(c: ComponentInstance, s: Placed): Pos | null {
    const w = s.box.max[0] + 2;
    const d = s.box.max[2] + 2;
    for (let n = 1; n <= 4; n++)
      for (const [dx, dz] of [
        [w, 0],
        [0, d],
        [-w, 0],
        [0, -d],
      ] as const) {
        const at: Pos = [c.at[0] + dx * n, c.at[1], c.at[2] + dz * n];
        if (this.fits(c, s, at, null) === null) return at;
      }
    return null;
  }

  /** What a port on `cell` is joined to: a port of another component there, else dust no component owns. */
  private linkAt(cell: Pos, self: string): PortLink | null {
    // A port off the plot (a box against its edge) joins nothing, and its key would name another cell.
    if (!inside(cell[0], cell[1], cell[2])) return null;
    const k = keyOf(cell[0], cell[1], cell[2]);
    const other = this.portCells.get(k)?.find((e) => e.id !== self);
    if (other) return { to: 'port', id: other.id, port: other.port.name };
    return !this.owner.has(k) && this.studio.canvas.id(cell[0], cell[1], cell[2]) === DUST ? { to: 'dust' } : null;
  }

  /**
   * Whether loose dust is the end of a line: the side its one loose dust neighbour on its level lies on, null for a lone
   * dust, false for a dust in the middle of a line.
   */
  private dustEnd(p: Pos): Side | null | false {
    let side: Side | null = null;
    for (const b of besides(p)) {
      const [x, y, z] = b.at;
      if (!inside(x, y, z) || this.studio.canvas.id(x, y, z) !== DUST || this.owner.has(keyOf(x, y, z))) continue;
      if (side) return false;
      side = b.side;
    }
    return side;
  }

  private refuse(text: string): null {
    this.error = text;
    this.publish();
    return null;
  }

  private update(
    id: string,
    f: (c: ComponentInstance) => ComponentInstance,
    word: string,
    extra: readonly Cell[] = [],
    group?: string,
  ): boolean {
    const c = this.state.components.find((x) => x.id === id);
    if (!c) return false;
    const changed = f(c);
    // Nothing to do is no undo step either.
    if (JSON.stringify(writeInstance(changed)) === JSON.stringify(writeInstance(c))) return true;
    const next = this.state.components.map((x) => (x.id === id ? changed : x));
    return this.change(next, `${word} ${this.nameOf(c)}`, this.state.customDefs, extra, { group });
  }

  /** Where each instance lies, or why one cannot: off the plot, too close to another, or of an unknown kind. */
  private layOut(
    components: readonly ComponentInstance[],
    customDefs: readonly CustomDef[] = this.state.customDefs,
  ): Map<string, Placed> | string {
    const out = new Map<string, Placed>();
    for (const c of components) {
      const def = this.defIn(c.def, customDefs);
      if (!def) return `There is no component "${c.def}" for ${this.version}.`;
      const placed = placeLayout(def.layout, c, this.version);
      const { min, max } = placed.box;
      if (!inside(min[0], min[1], min[2]) || !inside(max[0], max[1], max[2]))
        return `${this.nameOf(c)} does not fit on the plot there.`;
      for (const [otherId, other] of out)
        if (tooClose(placed.box, other.box)) {
          const o = components.find((x) => x.id === otherId)!;
          return `${this.nameOf(c)} would touch ${this.nameOf(o)}: keep one free cell between components.`;
        }
      out.set(c.id, placed);
    }
    return out;
  }

  /** One undoable edit: the cells the instances now want, the cells they give back, and `extra` (a snap's dust). */
  private change(
    components: readonly ComponentInstance[],
    label: string,
    customDefs: readonly CustomDef[],
    extra: readonly Cell[] = [],
    o: ChangeOptions = {},
  ): boolean {
    if (this.studio.busy) return this.refuse('Wait for the designer to finish before changing components.') !== null;
    const placed = this.layOut(components, customDefs);
    if (typeof placed === 'string') return this.refuse(placed) !== null;
    const before = cloneState(this.state);
    const after: State = { components, customDefs, base: new Map(before.base) };
    const nextOwned: Values = new Map();
    for (const p of placed.values()) for (const c of p.cells) nextOwned.set(keyOf(c[0], c[1], c[2]), valueOf(c));
    const canvas = this.studio.canvas;
    for (const k of nextOwned.keys())
      if (!this.owned.has(k)) after.base.set(k, o.absorb ? this.bare(k) : canvas.authoredState(k));
    const patch: Cell[] = [];
    for (const k of new Set([...this.owned.keys(), ...nextOwned.keys()])) {
      const want = nextOwned.get(k) ?? (o.release ? canvas.authoredState(k) : (after.base.get(k) ?? this.bare(k)));
      if (canvas.authoredState(k) !== want) patch.push(cellOf(k, want));
      if (!nextOwned.has(k)) after.base.delete(k);
    }
    // A joint's dust lies in the gap; one that would land in a box is dropped rather than break that component.
    const loose = extra.filter((c) => !nextOwned.has(keyOf(c[0], c[1], c[2])));
    this.commit(before, after, [...patch, ...loose], label, o.group);
    return true;
  }

  /** A cell no one has a memory of: air above the grass, undisturbed ground below it. */
  private bare(k: number): number {
    return posOf(k)[1] < 0 ? -1 : 0;
  }

  private commit(before: State, after: State, cells: readonly Cell[], label: string, group?: string): void {
    this.error = null;
    this.applying = true;
    try {
      this.state = after;
      this.studio.editWithHistory(
        cells,
        'redstone-component',
        label,
        {
          undo: () => this.setState(cloneState(before)),
          redo: () => this.setState(cloneState(after)),
        },
        group,
      );
    } finally {
      this.applying = false;
    }
    this.setState(after);
  }

  private setState(s: State): void {
    this.state = s;
    this.index();
    this.save();
    this.publish();
  }

  /** The cells each instance owns, from its placement. */
  private index(): void {
    const placed = this.layOut(this.state.components);
    this.placed = typeof placed === 'string' ? new Map() : placed;
    this.owned = new Map();
    this.owner = new Map();
    this.portCells = new Map();
    for (const [id, p] of this.placed) {
      for (const c of p.cells) {
        const k = keyOf(c[0], c[1], c[2]);
        this.owned.set(k, valueOf(c));
        this.owner.set(k, id);
      }
      for (const port of p.ports) {
        if (!inside(port.at[0], port.at[1], port.at[2])) continue;
        const k = keyOf(port.at[0], port.at[1], port.at[2]);
        const here = this.portCells.get(k);
        if (here) here.push({ id, port });
        else this.portCells.set(k, [{ id, port }]);
      }
    }
  }

  /**
   * The canvas changed. An instance whose cell no longer holds what it wants was edited by hand and is detached; when
   * that edit is the newest undo step, undoing it brings the instance back.
   */
  private readonly canvasChanged = (cells: Cell[], by: 'ai' | 'user'): void => {
    this.cellsEpoch++;
    if (this.applying) return;
    const hit = new Set<string>();
    const changed = new Set<number>();
    for (const c of cells) {
      const k = keyOf(c[0], c[1], c[2]);
      const id = this.owner.get(k);
      if (id && this.owned.get(k) !== valueOf(c)) {
        hit.add(id);
        changed.add(k);
      }
    }
    if (hit.size === 0) return;
    const before = cloneState(this.state);
    const after: State = { ...cloneState(this.state), components: this.state.components.filter((c) => !hit.has(c.id)) };
    for (const id of hit) for (const c of this.placed.get(id)?.cells ?? []) after.base.delete(keyOf(c[0], c[1], c[2]));
    const top = by === 'user' ? this.studio.history.peekUndo() : null;
    if (top && top.forward.some((c) => changed.has(keyOf(c[0], c[1], c[2])))) {
      const known = this.detaches.get(top);
      if (known) known.after = after;
      else if (!top.effect) {
        const e = { before, after };
        this.detaches.set(top, e);
        top.effect = {
          undo: () => this.setState(cloneState(e.before)),
          redo: () => this.setState(cloneState(e.after)),
        };
      }
    }
    this.setState(after);
  };

  // ------------------------------------------------------------------ saving

  private build(): ComponentSnapshot {
    return {
      components: this.state.components,
      customDefs: this.state.customDefs,
      error: this.error,
      recovered: this.recovered,
    };
  }

  private publish(): void {
    this.snap = this.build();
    for (const l of this.listeners) l();
  }

  private text(s: Pick<State, 'components' | 'customDefs'>): string {
    return JSON.stringify({ components: s.components.map(writeInstance), customDefs: s.customDefs.map(writeDef) });
  }

  /** The instances, each with what its box covered (`under`), and the definitions, as they are saved. */
  private written(): { components: ComponentInstance[]; customDefs: CustomDef[] } {
    const { components, customDefs, base } = this.state;
    return {
      components: components.map((c) => {
        const under: Array<[number, number, number, number]> = [];
        for (const cell of this.placed.get(c.id)?.cells ?? []) {
          const k = keyOf(cell[0], cell[1], cell[2]);
          const v = base.get(k);
          if (v !== undefined && v !== this.bare(k))
            under.push([cell[0] - c.at[0], cell[1] - c.at[1], cell[2] - c.at[2], v]);
        }
        return writeInstance(under.length ? { ...c, under } : c);
      }),
      customDefs: customDefs.map(writeDef),
    };
  }

  /** Into the design's envelope (only when it changed: opening a design is no edit) and this browser's copy. */
  private save(): void {
    const env = this.store.current;
    const saved = { components: readInstances(env.components), customDefs: readCustomDefs(env.customDefs) };
    const now = this.written();
    if (this.text(saved) !== JSON.stringify(now)) this.store.update(now);
    try {
      const copy = JSON.stringify({ at: this.now(), ...now });
      this.storage?.setItem(this.storageKey, copy);
      this.stored = !!this.storage;
    } catch {
      // The browser's copy is a convenience; a full or blocked storage never stops an edit.
    }
  }

  /** A design with nothing unsaved needs no copy in the browser: it is dropped once the studio is clean (saved). */
  private readonly studioChanged = (): void => {
    if (!this.stored || this.studio.snapshot().dirty) return;
    this.stored = false;
    try {
      this.storage?.removeItem?.(this.storageKey);
    } catch {
      // Left for the next prune.
    }
  };

  /** Keeps the account's newest copies of other sessions and drops the rest, so they never fill the browser's storage. */
  private prune(prefix: string): void {
    const s = this.storage;
    if (!s?.key || !s.removeItem || typeof s.length !== 'number') return;
    try {
      const copies: Array<{ key: string; at: number }> = [];
      for (let i = 0; i < s.length; i++) {
        const key = s.key(i);
        if (key && key.startsWith(prefix) && key !== this.storageKey)
          copies.push({ key, at: writtenAt(s.getItem(key)) });
      }
      copies.sort((a, b) => b.at - a.at);
      for (const c of copies.slice(KEEP_COPIES)) s.removeItem(c.key);
    } catch {
      // A blocked storage keeps what it has.
    }
  }

  /** Whether every cell the instances own holds what they want there now. */
  private matches(components: readonly ComponentInstance[], defs: readonly CustomDef[]): boolean {
    const keep = this.state;
    this.state = { components: [], customDefs: defs, base: new Map() };
    try {
      const placed = this.layOut(components);
      if (typeof placed === 'string') return false;
      const canvas = this.studio.canvas;
      for (const p of placed.values())
        for (const c of p.cells) if (canvas.authoredState(keyOf(c[0], c[1], c[2])) !== valueOf(c)) return false;
      return true;
    } finally {
      this.state = keep;
    }
  }

  /**
   * What the design opens with: this browser's copy of unsaved work when every cell it owns still matches the canvas,
   * else the saved envelope's instances that still match (an instance edited by hand since is plain cells now).
   */
  private restore(): State {
    const env = this.store.current;
    const customDefs = readCustomDefs(env.customDefs);
    try {
      const text = this.storage?.getItem(this.storageKey);
      if (text) {
        const local = JSON.parse(text) as { components?: unknown; customDefs?: unknown };
        const defs = readCustomDefs(local.customDefs);
        const comps = readInstances(local.components);
        if (this.matches(comps, defs)) {
          this.recovered =
            this.text({ components: comps, customDefs: defs }) !==
            this.text({ components: readInstances(env.components), customDefs });
          return this.opened(comps, defs);
        }
      }
    } catch {
      // A broken copy is dropped; the saved envelope decides.
    }
    const saved = readInstances(env.components);
    const keep: ComponentInstance[] = [];
    for (const c of saved) if (this.matches([...keep, c], customDefs)) keep.push(c);
    // On an empty canvas nothing was opened as plain blocks: the saved components are only out of date.
    const canvas = this.studio.canvas;
    if (keep.length < saved.length && canvas.size + canvas.dug.size > 0)
      this.restoreNote = leftOut(saved.length - keep.length, saved.length);
    return this.opened(keep, customDefs);
  }

  /** The state for opened instances: what their boxes covered comes from their `under`, which the state never keeps. */
  private opened(components: readonly ComponentInstance[], customDefs: readonly CustomDef[]): State {
    const base: Values = new Map();
    const placed = this.layOut(components, customDefs);
    const plain = components.map(({ under, ...c }) => {
      const box = typeof placed === 'string' ? null : placed.get(c.id)?.box;
      for (const u of under ?? []) {
        const at: Pos = [c.at[0] + u[0], c.at[1] + u[1], c.at[2] + u[2]];
        // An entry outside its own box names some other cell: it is not this component's memory.
        if (box && inBox(box, at)) base.set(keyOf(at[0], at[1], at[2]), u[3]);
      }
      return c;
    });
    return { components: plain, customDefs, base };
  }
}
