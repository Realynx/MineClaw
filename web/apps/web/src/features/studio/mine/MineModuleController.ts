/** Editable layouts are saved with each design revision; executable work remains ordinary canvas edits. */
import { DEPTH, inside, keyOf, plotMode, posOf, type Cell, type V3 } from '../canvas';
import type { Studio } from '../studio';
import { defaultModule, moduleCells, moduleName, normalizeModule, type MineModuleInstance, type MineModuleSpec } from './modules';

type Values = Map<number, number>;
interface State { modules: readonly MineModuleInstance[]; base: Values; overrides: Values }
type StorageLike = Pick<Storage, 'getItem' | 'setItem'>;
const value = (cell: Cell) => cell[3] < 0 ? -1 : cell[3] * 16 + cell[4];
const cell = (k: number, v: number): Cell => [...posOf(k), v < 0 ? -1 : v >> 4, v < 0 ? 0 : v & 15];
const clone = (s: State): State => ({ modules: s.modules, base: new Map(s.base), overrides: new Map(s.overrides) });
function union(modules: readonly MineModuleInstance[]): Values {
  const cells: Values = new Map();
  for (const m of modules) for (const c of moduleCells(m.spec, m.at).cells) cells.set(keyOf(c[0], c[1], c[2]), value(c));
  return cells;
}
function browserStorage(): StorageLike | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage; } catch { return null; }
}

export class MineModuleController {
  private state: State = { modules: [], base: new Map(), overrides: new Map() };
  private owned: Values = new Map();
  private listeners = new Set<() => void>();
  private unsubscribe: () => void;
  private applying = false;
  private serial = 0;
  private readonly storageKey: string;
  recoveredLocal = false;
  lastError: string | null = null;

  constructor(private readonly studio: Studio, private readonly storage: StorageLike | null = browserStorage()) {
    this.storageKey = `mineclaw.mine-modules.v1:${studio.session.owner}:${studio.id}:${studio.session.version}:${studio.session.seed}`;
    this.restore();
    this.owned = union(this.state.modules);
    studio.setMineAuthoring(this.serialize(), !this.recoveredLocal);
    this.unsubscribe = studio.onCells((cells) => {
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
    });
  }

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
    const at = [...pieces[0]!.at] as V3;
    const spec = normalizeModule({ ...defaultModule('custom'), label: 'Custom component',
      parts: pieces.map((m) => ({ spec: m.spec, at: m.at.map((v, i) => v - at[i]!) as V3 })) });
    if (!spec) { this.lastError = 'This group is too large or too deeply nested. Combine fewer components.'; return null; }
    const instance = { id: `module-${Date.now().toString(36)}-${++this.serial}`, spec, at };
    return this.change([...this.state.modules.filter((m) => !selected.has(m.id)), instance], 'Combine mine components') ? instance : null;
  }
  atCell(at: V3): MineModuleInstance | null {
    return [...this.state.modules].reverse().find((m) => moduleCells(m.spec, m.at).cells.some((c) => c[0] === at[0] && c[1] === at[1] && c[2] === at[2])) ?? null;
  }

  private validate(modules: readonly MineModuleInstance[]): boolean {
    for (const m of modules) {
      if (!m.at.every(Number.isInteger)) { this.lastError = 'Place layouts on whole block coordinates.'; return false; }
      for (const [x, y, z] of moduleCells(m.spec, m.at).cells) {
        if (!inside(x, y, z) || y < 1) {
          this.lastError = `The whole layout must fit inside the ${plotMode().size} × ${plotMode().size} plot, between Y1 and Y${plotMode().maxY}.`; return false;
        }
        if ((this.studio.terrain!.state(x, y, z) >> 4) === 7) {
          this.lastError = 'This layout reaches bedrock. Raise its entrance or shorten the descent.'; return false;
        }
      }
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
    try { this.storage?.setItem(this.designKey(designId, revision), this.serialize()); } catch { /* Optional local authoring metadata. */ }
  }
  private serialize(): string {
    // Most component cells replace untouched terrain. Store that baseline once instead of one pair per block, so large
    // layouts stay well below browser storage quotas; authored pre-existing digs/blocks still retain their exact values.
    return JSON.stringify({ size: plotMode().size, modules: this.state.modules, baseDefault: -1,
      base: [...this.state.base].filter(([, v]) => v !== -1), overrides: [...this.state.overrides] });
  }
  private persist(): void {
    const data = this.serialize();
    this.studio.setMineAuthoring(data);
    try {
      this.storage?.setItem(this.storageKey, data);
    } catch { /* Storage restrictions never block canvas authoring. */ }
  }
  private restore(): void {
    const session = this.studio.session;
    // Saved metadata is authoritative. Legacy browser-only layouts may be recovered after exact cell validation.
    if (session.mineAuthoring) {
      this.restoreText(session.mineAuthoring);
      this.lastError = null;
      return;
    }
    // Old servers did not report the opened design revision. Their historical fallback stays compatible, but current
    // replies never borrow an identically-shaped component configuration from a different saved revision.
    const revisions = typeof session.designRevision === 'number' ? [session.designRevision] : session.mineAuthoring === undefined ? session.revisions.slice(0, 40).map((r) => r.rev) : [];
    const keys = [this.storageKey, ...(session.designId ? revisions.map((rev) => this.designKey(session.designId!, rev)) : [])];
    for (const key of keys) if (this.restoreFrom(key)) { this.recoveredLocal = true; break; }
    this.lastError = null;
  }
  private restoreFrom(key: string): boolean {
    try { return this.restoreText(this.storage?.getItem(key)); } catch { return false; }
  }
  private restoreText(text: string | null | undefined): boolean {
    try {
      if (!text || text.length > 20_000_000) return false;
      const parsed = JSON.parse(text) as { size?: unknown; modules?: unknown; baseDefault?: unknown; base?: unknown; overrides?: unknown };
      const size = parsed.size ?? 64; // Original component metadata used a 64-column key stride.
      if (size !== 64 && size !== plotMode().size) return false;
      if (!Array.isArray(parsed.modules) || parsed.modules.length > 100) return false;
      const modules: MineModuleInstance[] = [];
      for (const raw of parsed.modules) {
        const m = raw as Partial<MineModuleInstance>, spec = normalizeModule(m.spec);
        if (!spec || typeof m.id !== 'string' || !Array.isArray(m.at) || m.at.length !== 3 || !m.at.every(Number.isInteger)) return false;
        modules.push({ id: m.id, at: m.at, spec, ...(m.visible === false ? { visible: false } : {}) });
      }
      if (new Set(modules.map((m) => m.id)).size !== modules.length || !this.validate(modules)) return false;
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
      const candidate: State = { modules, base: read(parsed.base), overrides: read(parsed.overrides) };
      for (const [k, v] of union(modules)) {
        if (!candidate.base.has(k) && parsed.baseDefault === -1) candidate.base.set(k, -1);
        if (!candidate.base.has(k) || this.studio.canvas.authoredState(k) !== (candidate.overrides.get(k) ?? v)) return false;
      }
      this.state = candidate;
      return true;
    } catch { /* Stale or malformed local metadata is discarded; never change the canvas to match it. */ }
    return false;
  }
  dispose(): void { this.unsubscribe(); this.listeners.clear(); }
}
