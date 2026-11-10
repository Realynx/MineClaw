/** Editable layouts are saved with each design revision; executable work remains ordinary canvas edits. */
import { DEPTH, inSel, inside, keyOf, plotMode, posOf, type Cell, type Selection, type V3 } from '../canvas';
import { browserStorage, keepNewest, type Studio, type StorageLike } from '../studio';
import { defaultModule, moduleCells, moduleName, normalizeModule, ungroupParts, type MineModuleInstance, type MineModuleSpec } from './modules';

type Values = Map<number, number>;
interface State { modules: readonly MineModuleInstance[]; base: Values; overrides: Values }
/** A layout as an envelope holds it, not yet compared with the canvas. */
interface Layout extends State {
  /** Owned cells without a base entry were untouched terrain (`baseDefault: -1`); the first envelopes listed every one. */
  filled: boolean;
  /** A working copy only: the fingerprint of the bot's layout it continues from. */
  descends: string | null;
}
/** The part of a layout the canvas still holds: its owned cells, and how many components were left out. */
interface Fitted { state: State; cells: Values; dropped: number }
interface Candidate { from: 'copy' | 'saved' | 'revision'; layout: Layout | null; own?: boolean }
const value = (cell: Cell) => cell[3] < 0 ? -1 : cell[3] * 16 + cell[4];
const cell = (k: number, v: number): Cell => [...posOf(k), v < 0 ? -1 : v >> 4, v < 0 ? 0 : v & 15];
const clone = (s: State): State => ({ modules: s.modules, base: new Map(s.base), overrides: new Map(s.overrides) });
const none = (): State => ({ modules: [], base: new Map(), overrides: new Map() });
const plus = (a: V3, b: V3): V3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
const WHOLE_CELLS = 'Place layouts on whole block coordinates.';
const REACHES_BEDROCK = 'This layout reaches bedrock. Raise its entrance or shorten the descent.';
const offPlot = () => `The whole layout must fit inside the ${plotMode().size} × ${plotMode().size} plot, between Y1 and Y${plotMode().maxY}.`;
function union(modules: readonly MineModuleInstance[]): Values {
  const cells: Values = new Map();
  for (const m of modules) for (const c of moduleCells(m.spec, m.at).cells) cells.set(keyOf(c[0], c[1], c[2]), value(c));
  return cells;
}
/** A session's working copy (`v2:<owner>:<session>`, or the older `v1:<owner>:<session>:<version>:<seed>`). */
const SESSION_COPY = /^mineclaw\.mine-modules\.v[12]:([^:]*):(\d+)(?::|$)/;
/** The session this browser last left a design unsaved in (studioSave's `mineclaw.studio.unsaved:<owner>:<design>`). */
const UNSAVED_WORK = /^mineclaw\.studio\.unsaved:([^:]*):\d+$/;
/** An envelope's length and a 32-bit hash of it: enough to tell which saved layout a working copy continues from ('' = none). */
function fingerprint(text: string): string {
  if (!text) return '';
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
  return `${text.length}:${(h >>> 0).toString(36)}`;
}
/** "2 of 5 saved components no longer match the excavation and were opened as plain cells. The saved revision still has them." */
function leftOut(dropped: number, total: number, saved: boolean): string {
  const one = dropped === 1, kind = saved ? 'saved ' : '';
  const who = total === 1 ? `The ${kind}component` : `${dropped} of ${total} ${kind}components`;
  return `${who} no longer ${one ? 'matches' : 'match'} the excavation and ${one ? 'was' : 'were'} opened as plain cells.`
    + (saved ? ` The saved revision still has ${one ? 'it' : 'them'}.` : '');
}
/**
 * An envelope's layout as written: its shape, unique ids, whole positions and bounded component settings. Null when the text
 * is not an envelope for this plot. Nothing here looks at the canvas (`fit` does).
 */
function parse(text: string | null | undefined): Layout | null {
  try {
    if (!text || text.length > 20_000_000) return null;
    const parsed = JSON.parse(text) as { size?: unknown; modules?: unknown; baseDefault?: unknown; base?: unknown; overrides?: unknown; descends?: unknown };
    const size = parsed.size ?? 64; // Original component metadata used a 64-column key stride.
    if (size !== 64 && size !== plotMode().size) return null;
    if (!Array.isArray(parsed.modules) || parsed.modules.length > 100) return null;
    const modules: MineModuleInstance[] = [];
    for (const raw of parsed.modules) {
      const m = raw as Partial<MineModuleInstance>, spec = normalizeModule(m.spec);
      if (!spec || typeof m.id !== 'string' || !Array.isArray(m.at) || m.at.length !== 3 || !m.at.every(Number.isInteger)) return null;
      // The key order `add` writes, so a layout opened and saved untouched is the same text again.
      modules.push({ id: m.id, spec, at: m.at, ...(m.visible === false ? { visible: false } : {}) });
    }
    if (new Set(modules.map((m) => m.id)).size !== modules.length) return null;
    const read = (raw: unknown): Values => {
      if (!Array.isArray(raw) || raw.length > plotMode().size ** 2 * (plotMode().maxY - plotMode().minY + 1)) throw new Error('Invalid saved layout');
      const map: Values = new Map();
      for (const pair of raw) {
        if (!Array.isArray(pair) || pair.length !== 2 || !pair.every(Number.isInteger) || pair[1] < -1 || pair[1] > 65535) throw new Error('Invalid saved layout');
        const k = Number(pair[0]);
        const x = k % size, y = Math.floor(k / (size * size)) - DEPTH, z = Math.floor(k / size) % size;
        if (!inside(x, y, z)) throw new Error('Invalid saved layout coordinate');
        map.set(keyOf(x, y, z), Number(pair[1]));
      }
      return map;
    };
    return { modules, base: read(parsed.base), overrides: read(parsed.overrides), filled: parsed.baseDefault === -1,
      descends: typeof parsed.descends === 'string' ? parsed.descends : null };
  } catch { /* Stale or malformed metadata is not a layout. */ }
  return null;
}

export class MineModuleController {
  private state: State = none();
  private owned: Values = new Map();
  private listeners = new Set<() => void>();
  private detach: (() => void) | null = null;
  /** The spec `check` saw last, with its cells about the origin and their bounds. */
  private checked: { input: MineModuleSpec; cells: readonly Cell[]; min: V3; max: V3 } | null = null;
  private applying = false;
  private serial = 0;
  /** This browser's working copy of the session's layout. No version or seed in the key: the cells say whether it still fits. */
  private readonly storageKey: string;
  /** The fingerprint of the bot's layout that the working copy continues from. */
  private descends = '';
  /** The page opened with component changes this browser kept and the bot does not have. */
  recoveredLocal = false;
  /** What the reader should know about how the layout opened (unsaved work recovered, components left out), or null. */
  restoreNote: string | null = null;
  lastError: string | null = null;

  /**
   * `attach: false` builds the controller without following the canvas (`attach` does that later): React runs a memo's
   * factory twice in a development build, and the instance it throws away must not keep writing its layout.
   */
  constructor(private readonly studio: Studio, private readonly storage: StorageLike | null = browserStorage(), options: { attach?: boolean } = {}) {
    this.storageKey = `mineclaw.mine-modules.v2:${studio.session.owner}:${studio.id}`;
    // Unsaved means this browser holds a layout the bot does not: the bot's is the baseline, the one opened is compared with it.
    studio.setMineAuthoring(this.restore(), true);
    studio.setMineAuthoring(this.serialize());
    this.prune();
    if (options.attach !== false) this.attach();
  }

  /**
   * Keeps this browser's working copies bounded: the newest 20 other sessions' per owner (session ids only grow, so higher is
   * newer). This session's always stay, and so do those of sessions "Open in studio" offers to resume (studioSave's unsaved
   * work entries), whose unsaved component changes live only here. The copies kept per saved revision are not pruned: they
   * are written only on a save, and for a design saved before the bot kept layouts they are its only record of them.
   */
  private prune(): void {
    const { owner } = this.studio.session, resumable = this.resumable();
    keepNewest(this.storage, 20, (k) => {
      const m = SESSION_COPY.exec(k);
      if (!m || (m[1] === owner && Number(m[2]) === this.studio.id) || resumable.has(`${m[1]}:${Number(m[2])}`)) return null;
      return { group: m[1]!, order: Number(m[2]) };
    });
  }
  /** "owner:session" of every session an unsaved-work entry (`mineclaw.studio.unsaved:<owner>:<design>`) points at. */
  private resumable(): Set<string> {
    const out = new Set<string>(), s = this.storage;
    if (!s?.key || typeof s.length !== 'number') return out;
    try {
      for (let i = 0; i < s.length; i++) {
        const k = s.key(i), m = k === null ? null : UNSAVED_WORK.exec(k);
        if (m) out.add(`${m[1]}:${Number(s.getItem(k!))}`);
      }
    } catch { /* Storage that refuses is only left fuller. */ }
    return out;
  }

  /** Follows hand edits and the bot's corrections inside components: each becomes an override. Returns the detach. */
  attach(): () => void {
    const off = (this.detach ??= this.studio.onCells(this.follow));
    return () => {
      if (this.detach !== off) return;
      off();
      this.detach = null;
    };
  }
  private follow = (cells: Cell[]): void => {
    if (this.applying) return;
    const owned = this.owned;
    let changed = false;
    for (const c of cells) {
      const k = keyOf(c[0], c[1], c[2]);
      if (!owned.has(k)) continue;
      if (owned.get(k) === value(c)) this.state.overrides.delete(k);
      else this.state.overrides.set(k, value(c));
      changed = true;
    }
    if (changed) this.persist();
  };

  snapshot = (): readonly MineModuleInstance[] => this.state.modules;
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  add(spec: MineModuleSpec, at: V3): MineModuleInstance | null {
    if (this.state.modules.length >= 100) { this.lastError = 'A mine can contain up to 100 editable layouts.'; return null; }
    const normalized = normalizeModule(spec);
    if (!normalized) { this.lastError = 'Unknown mine layout.'; return null; }
    const instance = { id: `module-${Date.now().toString(36)}-${++this.serial}`, spec: normalized, at: [...at] as V3 };
    return this.change([...this.state.modules, instance], `Add ${moduleName(spec.kind)}`) ? instance : null;
  }
  /** Presets become a single undoable insertion of independently editable pieces. */
  addMany(pieces: readonly { spec: MineModuleSpec; at: V3 }[], label: string): readonly MineModuleInstance[] | null {
    if (this.state.modules.length + pieces.length > 100) { this.lastError = 'A mine can contain up to 100 editable layouts.'; return null; }
    const items: MineModuleInstance[] = [];
    for (const piece of pieces) {
      const spec = normalizeModule(piece.spec);
      if (!spec) { this.lastError = 'Unknown mine layout.'; return null; }
      items.push({ id: `module-${Date.now().toString(36)}-${++this.serial}`, spec, at: [...piece.at] as V3 });
    }
    return this.change([...this.state.modules, ...items], label) ? items : null;
  }
  /** Visibility only affects editor overlays and picking, never the excavation or jobs. */
  setVisible(id: string, visible: boolean): void {
    if (!this.state.modules.some((m) => m.id === id && (m.visible !== false) !== visible)) return;
    this.state = { ...this.state, modules: this.state.modules.map((m) => m.id === id ? { ...m, visible } : m) };
    this.persist();
    for (const listener of this.listeners) listener();
  }
  update(id: string, spec: MineModuleSpec): boolean {
    const normalized = normalizeModule(spec);
    if (!normalized || !this.state.modules.some((m) => m.id === id)) return false;
    return this.change(this.state.modules.map((m) => m.id === id ? { ...m, spec: normalized } : m), `Edit ${moduleName(spec.kind)}`);
  }
  move(id: string, at: V3, group?: string): boolean {
    if (!this.state.modules.some((m) => m.id === id)) return false;
    return this.change(this.state.modules.map((m) => m.id === id ? { ...m, at: [...at] as V3 } : m), 'Move mine layout', group);
  }
  replace(id: string, spec: MineModuleSpec, at: V3): boolean {
    const normalized = normalizeModule(spec);
    if (!normalized || !this.state.modules.some((m) => m.id === id)) return false;
    return this.change(this.state.modules.map((m) => m.id === id ? { ...m, spec: normalized, at: [...at] as V3 } : m), 'Move mine layout');
  }
  remove(id: string): boolean {
    if (!this.state.modules.some((m) => m.id === id)) return false;
    return this.change(this.state.modules.filter((m) => m.id !== id), 'Remove mine layout');
  }
  /** Grouping changes authoring metadata atomically; the excavation and manual edits stay exactly where they are. */
  combine(ids: readonly string[]): MineModuleInstance | null {
    const selected = new Set(ids), pieces = this.state.modules.filter((m) => selected.has(m.id));
    if (pieces.length < 2) { this.lastError = 'Select at least two components to combine.'; return null; }
    // A group turns about its anchor: the middle of its footprint, as every other component's is, at the first piece's level.
    // A group saved before this keeps the anchor it was saved with (its first piece's).
    let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
    for (const m of pieces) for (const c of moduleCells(m.spec, m.at).cells) {
      x0 = Math.min(x0, c[0]); x1 = Math.max(x1, c[0]); z0 = Math.min(z0, c[2]); z1 = Math.max(z1, c[2]);
    }
    const at: V3 = [x0 + Math.floor((x1 - x0) / 2), pieces[0]!.at[1], z0 + Math.floor((z1 - z0) / 2)];
    const spec = normalizeModule({ ...defaultModule('custom'), label: 'Custom component',
      parts: pieces.map((m) => ({ spec: m.spec, at: m.at.map((v, i) => v - at[i]!) as V3 })) });
    if (!spec) { this.lastError = 'This group is too large or too deeply nested. Combine fewer components.'; return null; }
    const instance = { id: `module-${Date.now().toString(36)}-${++this.serial}`, spec, at };
    return this.change([...this.state.modules.filter((m) => !selected.has(m.id)), instance], 'Combine mine components') ? instance : null;
  }
  /**
   * The inverse of `combine`: the group's parts take its place in the list, in their order, so the later-wins overlap order
   * and every cell stay as they are. Only the layout changes; one undo step.
   */
  ungroup(id: string): readonly MineModuleInstance[] | null {
    const index = this.state.modules.findIndex((m) => m.id === id), group = this.state.modules[index];
    if (!group || group.spec.kind !== 'custom' || !group.spec.parts?.length) { this.lastError = 'Select a combined component to ungroup it.'; return null; }
    if (this.state.modules.length - 1 + group.spec.parts.length > 100) { this.lastError = 'A mine can contain up to 100 editable layouts.'; return null; }
    const items: MineModuleInstance[] = [];
    for (const part of ungroupParts(group.spec, group.at)) {
      const spec = normalizeModule(part.spec);
      if (!spec) { this.lastError = 'Unknown mine layout.'; return null; }
      items.push({ id: `module-${Date.now().toString(36)}-${++this.serial}`, spec, at: part.at, ...(group.visible === false ? { visible: false } : {}) });
    }
    const modules = [...this.state.modules.slice(0, index), ...items, ...this.state.modules.slice(index + 1)];
    return this.change(modules, 'Ungroup mine component') ? items : null;
  }
  atCell(at: V3): MineModuleInstance | null {
    return [...this.state.modules].reverse().find((m) => moduleCells(m.spec, m.at).cells.some((c) => c[0] === at[0] && c[1] === at[1] && c[2] === at[2])) ?? null;
  }

  /** Why a component cannot stand where it is (off the plot, into bedrock), or null. */
  private problem(m: MineModuleInstance): string | null {
    if (!m.at.every(Number.isInteger)) return WHOLE_CELLS;
    return this.cellsProblem(moduleCells(m.spec, m.at).cells, [0, 0, 0]);
  }
  /** `problem` for cells shifted by `by`: off the plot first, then bedrock, which only lies at or below the version's top. */
  private cellsProblem(cells: readonly Cell[], by: V3): string | null {
    const [dx, dy, dz] = by;
    for (const [x, y, z] of cells) if (!inside(x + dx, y + dy, z + dz) || y + dy < 1) return offPlot();
    const terrain = this.studio.terrain!, top = terrain.table.bedrockTop;
    for (const [x, y, z] of cells)
      if (y + dy <= top && (terrain.state(x + dx, y + dy, z + dz) >> 4) === 7) return REACHES_BEDROCK;
    return null;
  }
  /**
   * Why `spec` could not be placed at `at` (the rule `add` and `move` apply), or null when it can. A preview or a drag asks at
   * every step with the same spec: its cells about the origin are worked out once, then only the bounds are tested, and the
   * terrain only where the shape reaches down to the bedrock layers.
   */
  check(spec: MineModuleSpec, at: V3): string | null {
    if (this.checked?.input !== spec) {
      const normalized = normalizeModule(spec);
      if (!normalized) return 'Unknown mine layout.';
      const cells = moduleCells(normalized, [0, 0, 0]).cells;
      const min: V3 = [Infinity, Infinity, Infinity], max: V3 = [-Infinity, -Infinity, -Infinity];
      for (const c of cells) for (let i = 0; i < 3; i++) { min[i] = Math.min(min[i]!, c[i]!); max[i] = Math.max(max[i]!, c[i]!); }
      this.checked = { input: spec, cells, min, max };
    }
    if (!at.every(Number.isInteger)) return WHOLE_CELLS;
    const { cells, min, max } = this.checked;
    if (!cells.length) return null;
    const lo = plus(min, at), hi = plus(max, at);
    if (!inside(...lo) || !inside(...hi) || lo[1] < 1) return offPlot();
    return lo[1] <= this.studio.terrain!.table.bedrockTop ? this.cellsProblem(cells, at) : null;
  }
  /** Whether a selection takes in any cell a component owns (raw tools would change it under the component). */
  ownsAny(s: Selection): boolean {
    if (s.mask) {
      for (const k of s.mask) if (this.owned.has(k)) return true;
      return false;
    }
    for (const k of this.owned.keys()) if (inSel(s, ...posOf(k))) return true;
    return false;
  }
  private validate(modules: readonly MineModuleInstance[]): boolean {
    for (const m of modules) {
      const problem = this.problem(m);
      if (problem) { this.lastError = problem; return false; }
    }
    return true;
  }

  private change(modules: readonly MineModuleInstance[], label: string, group?: string): boolean {
    if (this.studio.busy) { this.lastError = 'Wait for the designer to finish before changing mine layouts.'; return false; }
    if (modules.length === this.state.modules.length && modules.every((m, i) => {
      const old = this.state.modules[i]!;
      return m.id === old.id && m.at.every((v, axis) => v === old.at[axis]) && JSON.stringify(m.spec) === JSON.stringify(old.spec);
    })) { this.lastError = null; return true; }
    if (!this.studio.mine || !this.validate(modules)) return false;
    this.lastError = null;
    const before = clone(this.state), after = clone(this.state);
    const oldCells = this.owned, nextCells = union(modules);
    after.modules = modules;
    for (const k of nextCells.keys()) if (!oldCells.has(k)) {
      after.base.set(k, this.studio.canvas.authoredState(k));
      after.overrides.delete(k);
    }
    const patch: Cell[] = [];
    for (const k of new Set([...oldCells.keys(), ...nextCells.keys()])) {
      const desired = after.overrides.get(k) ?? nextCells.get(k) ?? after.base.get(k) ?? -1;
      if (this.studio.canvas.authoredState(k) !== desired) patch.push(cell(k, desired));
      if (!nextCells.has(k)) { after.base.delete(k); after.overrides.delete(k); }
    }
    this.applying = true;
    try {
      this.state = after;
      this.studio.editWithHistory(patch, 'mine-layout', label, {
        undo: () => this.setState(clone(before)), redo: () => this.setState(clone(after)),
      }, group);
    } finally { this.applying = false; }
    this.setState(after, nextCells);
    return true;
  }
  private setState(state: State, owned = union(state.modules)): void {
    this.state = state;
    this.owned = owned;
    this.persist();
    for (const listener of this.listeners) listener();
  }
  private designKey(designId: number, revision: number): string {
    return `mineclaw.mine-modules.design.v1:${this.studio.session.owner}:${this.studio.session.version}:${this.studio.session.seed}:${designId}:${revision}`;
  }
  /** Called only after a successful save; later unsaved edits never change this revision's metadata. */
  rememberDesign(designId: number, revision: number): void {
    const data = this.serialize();
    try { this.storage?.setItem(this.designKey(designId, revision), data); } catch { /* Optional local authoring metadata. */ }
    // The bot now holds the layout that was saved: this browser's working copy continues from that one.
    this.descends = fingerprint(this.studio.savedAuthoring ?? '');
    this.store(data);
    this.prune();
  }
  private serialize(state: State = this.state): string {
    // Most component cells replace untouched terrain. Store that baseline once instead of one pair per block, so large
    // layouts stay well below browser storage quotas; authored pre-existing digs/blocks still retain their exact values.
    return JSON.stringify({ size: plotMode().size, modules: state.modules, baseDefault: -1,
      base: [...state.base].filter(([, v]) => v !== -1), overrides: [...state.overrides] });
  }
  private persist(): void {
    const data = this.serialize();
    this.studio.setMineAuthoring(data);
    this.store(data);
  }
  /** The working copy is the envelope plus `descends`, which stays in this browser: the bot is never sent it. */
  private store(data: string): void {
    try {
      this.storage?.setItem(this.storageKey, `${data.slice(0, -1)},"descends":${JSON.stringify(this.descends)}}`);
    } catch { /* Storage restrictions never block canvas authoring. */ }
  }
  private read(key: string): string | null {
    try { return this.storage?.getItem(key) ?? null; } catch { return null; }
  }
  /**
   * Opens the newest layout the canvas still holds and returns the bot's own layout as this page writes one: the baseline
   * that unsaved changes are measured against. This browser's working copy wins outright when it continues from the bot's
   * layout and all of it still fits, so a combine or a move made after the last save comes back. Otherwise the candidate that
   * keeps the most cells in components wins, the earlier one on a tie: a layout saved from another browser beats a stale
   * copy of the same cells, a copy that lost one component still beats a saved layout that knows fewer, and an empty copy
   * never hides what a saved layout still has right.
   */
  private restore(): string {
    const text = this.studio.session.mineAuthoring ?? '';
    this.descends = fingerprint(text);
    const saved = parse(text);
    let best: (Fitted & Pick<Candidate, 'from'>) | null = null;
    for (const c of this.candidates(saved)) {
      if (!c.layout) continue;
      const fitted = this.fit(c.layout);
      if (!best || fitted.cells.size > best.cells.size) best = { ...fitted, from: c.from };
      if (c.own && fitted.dropped === 0) break;
    }
    if (!best) return this.serialize();
    this.state = best.state;
    this.owned = best.cells;
    // Opened from the bot's layout, whole or in part, the page starts clean; opened from this browser, it differs from the bot's.
    const local = best.from !== 'saved', baseline = local ? this.serialize(saved ?? none()) : this.serialize();
    this.recoveredLocal = local && this.serialize() !== baseline;
    const found = !this.recoveredLocal ? ''
      : best.from === 'revision' ? 'Recovered saved components from this browser. Save the design to keep them with its revision on the server.'
      : 'Recovered unsaved component changes from this browser. Save the design to keep them.';
    // On an empty canvas nothing was opened as plain cells: the layout is only out of date (the canvas was cleared).
    const lost = best.dropped > 0 && this.studio.canvas.size + this.studio.canvas.dug.size > 0
      ? leftOut(best.dropped, best.state.modules.length + best.dropped, !local) : '';
    this.restoreNote = [found, lost].filter(Boolean).join(' ') || null;
    return baseline;
  }
  /**
   * The layouts this session could open from, newest first: this browser's working copy when it continues from the bot's
   * layout (`own`), the bot's layout, a working copy of some other layout, then the copies kept per saved revision for
   * designs whose layout never reached the bot. Read one at a time, because the first often settles it.
   */
  private *candidates(saved: Layout | null): Generator<Candidate> {
    const session = this.studio.session;
    // A copy written before copies said what they continue from was also keyed by version and seed.
    const copy = parse(this.read(this.storageKey))
      ?? parse(this.read(`mineclaw.mine-modules.v1:${session.owner}:${this.studio.id}:${session.version}:${session.seed}`));
    const own = copy?.descends === this.descends;
    if (own) yield { from: 'copy', layout: copy, own };
    yield { from: 'saved', layout: saved };
    if (!own) yield { from: 'copy', layout: copy };
    if (session.mineAuthoring || !session.designId) return;
    // Old servers did not report the opened design revision. Their historical fallback stays compatible, but current
    // replies never borrow an identically-shaped component configuration from a different saved revision.
    const revisions = typeof session.designRevision === 'number' ? [session.designRevision] : session.mineAuthoring === undefined ? session.revisions.slice(0, 40).map((r) => r.rev) : [];
    for (const rev of revisions) yield { from: 'revision', layout: parse(this.read(this.designKey(session.designId, rev))) };
  }
  /**
   * The part of a layout the canvas still holds. Components off the plot or in bedrock are left out, then every component
   * with a cell that is not what the layout says, again and again until the rest agrees (leaving one out can uncover a cell
   * of another). The canvas is never changed to match: what a left-out component dug stays there as plain cells.
   */
  private fit(layout: Layout): Fitted {
    let modules = layout.modules.filter((m) => this.problem(m) === null), cells = union(modules), before: number;
    const differs = (c: Cell): boolean => {
      const k = keyOf(c[0], c[1], c[2]);
      return (!layout.filled && !layout.base.has(k)) || this.studio.canvas.authoredState(k) !== (layout.overrides.get(k) ?? cells.get(k));
    };
    do {
      before = modules.length;
      modules = modules.filter((m) => !moduleCells(m.spec, m.at).cells.some(differs));
      if (modules.length < before) cells = union(modules);
    } while (modules.length < before);
    const owns = ([k]: [number, number]) => cells.has(k);
    const base: Values = new Map([...layout.base].filter(owns)), overrides: Values = new Map([...layout.overrides].filter(owns));
    for (const k of cells.keys()) if (!base.has(k)) base.set(k, -1);
    return { state: { modules, base, overrides }, cells, dropped: layout.modules.length - modules.length };
  }
  dispose(): void { this.detach?.(); this.detach = null; this.listeners.clear(); }
}
