/**
 * Placed components in a redstone design, outside React. An instance owns every cell of its box: the layout's blocks,
 * turned and mirrored, and air in the rest. Placing, moving, turning, mirroring, renaming and removing one is one undoable
 * edit (`Studio.editWithHistory` with the instances as its undo effect), which reaches the bot like any other edit. A
 * hand edit of an owned cell (a key, a brush, the Configure tool, the AI) detaches that instance: its cells stay as plain
 * cells. Boxes keep one free cell between them; snapping puts an input port on another component's output port, in that
 * gap, with one dust there.
 *
 * The instances and the custom definitions they use are saved with the design in `redstoneAuthoring` (through the
 * shared store, so the debugger's keys stay). Unsaved work is also mirrored to this browser per session, and that copy
 * comes back on the next open only when every cell it owns still matches the canvas.
 */
import { LIBRARY, type ComponentDef, type ComponentLayout, type Pos, type RedstoneVersion } from '@mineclaw/redstone';
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
  type CustomDef,
  type StorageLike,
} from './componentData';
import {
  layoutOf,
  placeLayout,
  quarter,
  snapPlacement,
  tooClose,
  inBox,
  type Box,
  type Mirror,
  type Outlet,
  type PickedPort,
  type Placed,
} from './ports';

const DUST = 55;

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
const sameList = <T>(a: readonly T[], b: readonly T[], w: (t: T) => unknown) =>
  a.length === b.length && JSON.stringify(a.map(w)) === JSON.stringify(b.map(w));

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
    const own = this.state.customDefs.find((d) => d.id === id);
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

  // ------------------------------------------------------------------ changing

  /** Places a component with its box's min corner at `at` (or snapped to an output port near it). Null when refused. */
  place(defId: string, at: Pos, o: PlaceOptions = {}): ComponentInstance | null {
    const def = this.defOf(defId);
    if (!def) return this.refuse(`There is no component "${defId}" for ${this.version}.`);
    if (this.state.components.length >= MAX_COMPONENTS)
      return this.refuse(`A design holds at most ${MAX_COMPONENTS} components.`);
    const turns = quarter(o.turns ?? 0);
    const mirror = o.mirror ?? null;
    let corner: Pos = [at[0], at[1], at[2]];
    const extra: Cell[] = [];
    if (o.snap) {
      const s = snapPlacement(def.layout, turns, mirror, this.version, at, this.outlets());
      if (!s) return this.refuse('No output port near there to join: place it by its corner instead.');
      corner = s.at;
      const [jx, jy, jz] = s.joint;
      if (this.studio.canvas.id(jx, jy, jz) !== DUST) extra.push([jx, jy, jz, DUST, 0]);
    }
    const instance: ComponentInstance = { id: this.nextId(), def: def.id, at: corner, turns, mirror };
    const label = o.label?.trim().slice(0, 40);
    if (label) instance.label = label;
    return this.change([...this.state.components, instance], `place ${def.name}`, this.state.customDefs, extra)
      ? instance
      : null;
  }

  move(id: string, at: Pos): boolean {
    return this.update(id, (c) => ({ ...c, at: [at[0], at[1], at[2]] }), 'move');
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
    return this.update(
      id,
      (c) => {
        const own: 'x' | 'z' = c.turns % 2 === 1 ? (axis === 'x' ? 'z' : 'x') : axis;
        // Mirroring twice across one axis is no mirror; across both it is a half turn.
        if (c.mirror === null) return { ...c, mirror: own };
        if (c.mirror === own) return { ...c, mirror: null };
        return { ...c, mirror: null, turns: quarter(c.turns + 2) };
      },
      'mirror',
    );
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

  /** Drops a definition the design no longer places. */
  removeDef(id: string): boolean {
    if (this.state.components.some((c) => c.def === id))
      return this.refuse('Remove or unlink the placed copies of this component first.') !== null;
    if (!this.state.customDefs.some((d) => d.id === id)) return false;
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
    const n = 1 + Math.max(0, ...this.state.components.map((c) => Number(/^c(\d+)$/.exec(c.id)?.[1] ?? 0)));
    return `c${n}`;
  }

  private refuse(text: string): null {
    this.error = text;
    this.publish();
    return null;
  }

  private update(id: string, f: (c: ComponentInstance) => ComponentInstance, word: string): boolean {
    const c = this.state.components.find((x) => x.id === id);
    if (!c) return false;
    const changed = f(c);
    // Nothing to do is no undo step either.
    if (JSON.stringify(writeInstance(changed)) === JSON.stringify(writeInstance(c))) return true;
    const next = this.state.components.map((x) => (x.id === id ? changed : x));
    return this.change(next, `${word} ${this.nameOf(c)}`, this.state.customDefs);
  }

  /** Where each instance lies, or why one cannot: off the plot, too close to another, or of an unknown kind. */
  private layOut(components: readonly ComponentInstance[]): Map<string, Placed> | string {
    const out = new Map<string, Placed>();
    for (const c of components) {
      const def = this.defOf(c.def);
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
  ): boolean {
    if (this.studio.busy) return this.refuse('Wait for the designer to finish before changing components.') !== null;
    const placed = this.layOut(components);
    if (typeof placed === 'string') return this.refuse(placed) !== null;
    const before = cloneState(this.state);
    const after: State = { components, customDefs, base: new Map(before.base) };
    const nextOwned: Values = new Map();
    for (const p of placed.values()) for (const c of p.cells) nextOwned.set(keyOf(c[0], c[1], c[2]), valueOf(c));
    const canvas = this.studio.canvas;
    for (const k of nextOwned.keys()) if (!this.owned.has(k)) after.base.set(k, canvas.authoredState(k));
    const patch: Cell[] = [];
    for (const k of new Set([...this.owned.keys(), ...nextOwned.keys()])) {
      const want = nextOwned.get(k) ?? after.base.get(k) ?? this.bare(k);
      if (canvas.authoredState(k) !== want) patch.push(cellOf(k, want));
      if (!nextOwned.has(k)) after.base.delete(k);
    }
    this.commit(before, after, [...patch, ...extra], label);
    return true;
  }

  /** A cell no one has a memory of: air above the grass, undisturbed ground below it. */
  private bare(k: number): number {
    return posOf(k)[1] < 0 ? -1 : 0;
  }

  private commit(before: State, after: State, cells: readonly Cell[], label: string): void {
    this.error = null;
    this.applying = true;
    try {
      this.state = after;
      this.studio.editWithHistory(cells, 'redstone-component', label, {
        undo: () => this.setState(cloneState(before)),
        redo: () => this.setState(cloneState(after)),
      });
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
    for (const [id, p] of this.placed)
      for (const c of p.cells) {
        const k = keyOf(c[0], c[1], c[2]);
        this.owned.set(k, valueOf(c));
        this.owner.set(k, id);
      }
  }

  /**
   * The canvas changed. An instance whose cell no longer holds what it wants was edited by hand and is detached; when
   * that edit is the newest undo step, undoing it brings the instance back.
   */
  private readonly canvasChanged = (cells: Cell[], by: 'ai' | 'user'): void => {
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

  /** Into the design's envelope (only when it changed: opening a design is no edit) and this browser's copy. */
  private save(): void {
    const env = this.store.current;
    const saved = { components: readInstances(env.components), customDefs: readCustomDefs(env.customDefs) };
    const now = this.state;
    if (
      !sameList(saved.components, now.components, writeInstance) ||
      !sameList(saved.customDefs, now.customDefs, writeDef)
    )
      this.store.update({ components: now.components.map(writeInstance), customDefs: now.customDefs.map(writeDef) });
    try {
      const copy = JSON.stringify({
        at: this.now(),
        components: now.components.map(writeInstance),
        customDefs: now.customDefs.map(writeDef),
      });
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
          return { components: comps, customDefs: defs, base: new Map() };
        }
      }
    } catch {
      // A broken copy is dropped; the saved envelope decides.
    }
    const keep: ComponentInstance[] = [];
    for (const c of readInstances(env.components)) if (this.matches([...keep, c], customDefs)) keep.push(c);
    return { components: keep, customDefs, base: new Map() };
  }
}
