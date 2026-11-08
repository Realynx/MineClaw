/** Layout ownership is local authoring metadata; the server still receives ordinary, undoable canvas edits. */
import { keyOf, posOf, type Cell, type V3 } from '../canvas';
import type { Studio } from '../studio';
import { moduleCells, moduleName, normalizeModule, type MineModuleInstance, type MineModuleSpec } from './modules';

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
  lastError: string | null = null;

  constructor(private readonly studio: Studio, private readonly storage: StorageLike | null = browserStorage()) {
    this.storageKey = `mineclaw.mine-modules.v1:${studio.session.owner}:${studio.id}:${studio.session.version}:${studio.session.seed}`;
    this.restore();
    this.owned = union(this.state.modules);
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
  atCell(at: V3): MineModuleInstance | null {
    return [...this.state.modules].reverse().find((m) => moduleCells(m.spec, m.at).cells.some((c) => c[0] === at[0] && c[1] === at[1] && c[2] === at[2])) ?? null;
  }

  private validate(modules: readonly MineModuleInstance[]): boolean {
    for (const m of modules) {
      if (!m.at.every(Number.isInteger)) { this.lastError = 'Place layouts on whole block coordinates.'; return false; }
      for (const [x, y, z] of moduleCells(m.spec, m.at).cells) {
        if (x < 0 || x >= 64 || z < 0 || z >= 64 || y < 1 || y > 127) {
          this.lastError = 'The whole layout must fit inside the 64 × 64 plot, between Y1 and Y127.'; return false;
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
    return JSON.stringify({ modules: this.state.modules, base: [...this.state.base], overrides: [...this.state.overrides] });
  }
  private persist(): void {
    try {
      this.storage?.setItem(this.storageKey, this.serialize());
    } catch { /* Storage restrictions never block canvas authoring. */ }
  }
  private restore(): void {
    const session = this.studio.session;
    const keys = [this.storageKey, ...(session.designId ? session.revisions.slice(0, 40).map((r) => this.designKey(session.designId!, r.rev)) : [])];
    for (const key of keys) if (this.restoreFrom(key)) break;
    this.lastError = null;
  }
  private restoreFrom(key: string): boolean {
    try {
      const text = this.storage?.getItem(key);
      if (!text || text.length > 4_000_000) return false;
      const parsed = JSON.parse(text) as { modules?: unknown; base?: unknown; overrides?: unknown };
      if (!Array.isArray(parsed.modules) || parsed.modules.length > 100) return false;
      const modules: MineModuleInstance[] = [];
      for (const raw of parsed.modules) {
        const m = raw as Partial<MineModuleInstance>, spec = normalizeModule(m.spec);
        if (!spec || typeof m.id !== 'string' || !Array.isArray(m.at) || m.at.length !== 3 || !m.at.every(Number.isInteger)) return false;
        modules.push({ id: m.id, at: m.at, spec });
      }
      if (new Set(modules.map((m) => m.id)).size !== modules.length || !this.validate(modules)) return false;
      const read = (raw: unknown): Values => {
        if (!Array.isArray(raw) || raw.length > 524288) throw new Error('Invalid saved layout');
        const map: Values = new Map();
        for (const pair of raw) {
          if (!Array.isArray(pair) || pair.length !== 2 || !pair.every(Number.isInteger) || pair[1] < -1 || pair[1] > 65535) throw new Error('Invalid saved layout');
          map.set(Number(pair[0]), Number(pair[1]));
        }
        return map;
      };
      const candidate: State = { modules, base: read(parsed.base), overrides: read(parsed.overrides) };
      for (const [k, v] of union(modules)) {
        if (!candidate.base.has(k) || this.studio.canvas.authoredState(k) !== (candidate.overrides.get(k) ?? v)) return false;
      }
      this.state = candidate;
      return true;
    } catch { /* Stale or malformed local metadata is discarded; never change the canvas to match it. */ }
    return false;
  }
  dispose(): void { this.unsubscribe(); this.listeners.clear(); }
}
