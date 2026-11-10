/**
 * The components' part of a redstone design's saved metadata, and the user's quick library in this browser.
 *
 * In the envelope (./authoring.ts): `components`, the placed instances (`{ id, def, at, turns, mirror, label? }`, `def`
 * a library id such as `gate.xor` or a custom id), and `customDefs`, the user's own definitions the design uses
 * (`{ id, name, layout, version? }`). The quick library is a list of the same definitions kept per browser and per
 * account (`mineclaw.redstone.library.v1:<owner>`), with an export and import as JSON; there is no server table for it.
 * Anything read from storage or a saved text is checked entry by entry, and what does not fit is dropped.
 */
import type { ComponentLayout, Pos, Port, RedstoneVersion, Side } from '@mineclaw/redstone';
import type { Mirror } from './ports';

export interface ComponentInstance {
  id: string;
  /** A library id (`gate.not`) or a custom definition's (`u.…`). */
  def: string;
  /** The box's min corner. */
  at: Pos;
  /** Clockwise quarter turns, after the mirror. */
  turns: number;
  mirror: Mirror;
  label?: string;
}

/** A component the user made: from a selection, from another saved design, or from an imported library. */
export interface CustomDef {
  id: string;
  name: string;
  layout: ComponentLayout;
  /** The game version it was drawn for; another version may still use it, with a warning. */
  version?: RedstoneVersion;
}

export const MAX_COMPONENTS = 256;
export const MAX_CUSTOM_DEFS = 64;
export const MAX_QUICK_LIBRARY = 64;
/** The largest custom box side: a design is saved at most this big too, and a component is part of one. */
export const MAX_DEF_SIDE = 32;
const MAX_LABEL = 40;
const MAX_NAME = 60;
const MAX_ID = 64;
const SIDES: readonly Side[] = ['north', 'east', 'south', 'west'];

type Json = Record<string, unknown>;
const isObject = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v);
const isId = (v: unknown): v is string => typeof v === 'string' && v.length > 0 && v.length <= MAX_ID;
const isInt = (v: unknown, lo: number, hi: number): v is number =>
  Number.isInteger(v) && (v as number) >= lo && (v as number) <= hi;

function readPos(v: unknown, lo = -1024, hi = 1024): Pos | null {
  if (!Array.isArray(v) || v.length !== 3 || !v.every((n) => isInt(n, lo, hi))) return null;
  return [v[0] as number, v[1] as number, v[2] as number];
}

function readInstance(v: unknown): ComponentInstance | null {
  if (!isObject(v) || !isId(v.id) || !isId(v.def) || !isInt(v.turns, 0, 3)) return null;
  const at = readPos(v.at);
  const mirror =
    v.mirror === 'x' || v.mirror === 'z' ? v.mirror : v.mirror === null || v.mirror === undefined ? null : undefined;
  if (!at || mirror === undefined) return null;
  const c: ComponentInstance = { id: v.id, def: v.def, at, turns: v.turns, mirror };
  const label = typeof v.label === 'string' ? v.label.trim().slice(0, MAX_LABEL) : '';
  if (label) c.label = label;
  return c;
}

function readLayout(v: unknown): ComponentLayout | null {
  if (!isObject(v)) return null;
  const size = readPos(v.size, 1, MAX_DEF_SIDE);
  if (!size || !Array.isArray(v.cells) || !Array.isArray(v.ports) || v.ports.length > 32) return null;
  const [sx, sy, sz] = size;
  if (v.cells.length > sx * sy * sz) return null;
  const cells: Array<readonly [number, number, number, number, number]> = [];
  for (const c of v.cells) {
    if (!Array.isArray(c) || c.length !== 5) return null;
    const [x, y, z, id, meta] = c as unknown[];
    if (
      !isInt(x, 0, sx - 1) ||
      !isInt(y, 0, sy - 1) ||
      !isInt(z, 0, sz - 1) ||
      !isInt(id, 1, 255) ||
      !isInt(meta, 0, 15)
    )
      return null;
    cells.push([x, y, z, id, meta]);
  }
  const ports: Port[] = [];
  for (const p of v.ports) {
    if (!isObject(p) || typeof p.name !== 'string' || !p.name || p.name.length > 16) return null;
    const at = readPos(p.at, -1, MAX_DEF_SIDE);
    const side = SIDES.find((s) => s === p.side);
    if (!at || !side || (p.kind !== 'in' && p.kind !== 'out')) return null;
    ports.push({ name: p.name, kind: p.kind, at, side });
  }
  return { size, cells, ports };
}

function readDef(v: unknown): CustomDef | null {
  if (!isObject(v) || !isId(v.id) || typeof v.name !== 'string') return null;
  const name = v.name.trim().slice(0, MAX_NAME);
  const layout = readLayout(v.layout);
  if (!name || !layout) return null;
  const d: CustomDef = { id: v.id, name, layout };
  if (v.version === 'b1.8.1' || v.version === '1.12.2') d.version = v.version;
  return d;
}

function readList<T extends { id: string }>(raw: unknown, read: (v: unknown) => T | null, max: number): T[] {
  if (!Array.isArray(raw)) return [];
  const out: T[] = [];
  const seen = new Set<string>();
  for (const v of raw) {
    if (out.length >= max) break;
    const t = read(v);
    if (!t || seen.has(t.id)) continue;
    seen.add(t.id);
    out.push(t);
  }
  return out;
}

export const readInstances = (raw: unknown): ComponentInstance[] => readList(raw, readInstance, MAX_COMPONENTS);
export const readCustomDefs = (raw: unknown): CustomDef[] => readList(raw, readDef, MAX_CUSTOM_DEFS);

const pos = (p: Pos): Pos => [p[0], p[1], p[2]];

/** An instance in the saved form, keys in a fixed order (the same state always gives the same text). */
export function writeInstance(c: ComponentInstance): ComponentInstance {
  return {
    id: c.id,
    def: c.def,
    at: pos(c.at),
    turns: c.turns,
    mirror: c.mirror,
    ...(c.label ? { label: c.label } : {}),
  };
}

export function writeDef(d: CustomDef): CustomDef {
  return {
    id: d.id,
    name: d.name,
    layout: {
      size: [d.layout.size[0], d.layout.size[1], d.layout.size[2]],
      cells: d.layout.cells.map((c) => [c[0], c[1], c[2], c[3], c[4]] as const),
      ports: d.layout.ports.map((p) => ({ name: p.name, kind: p.kind, at: pos(p.at), side: p.side })),
    },
    ...(d.version ? { version: d.version } : {}),
  };
}

// ---------------------------------------------------------------------------------------------- the quick library

/** A browser Storage; listing and removing are optional (without them old copies are only overwritten, never pruned). */
export type StorageLike = Pick<Storage, 'getItem' | 'setItem'> &
  Partial<Pick<Storage, 'removeItem' | 'key' | 'length'>>;

export function browserStorage(): StorageLike | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

export const quickLibraryKey = (owner: string): string => `mineclaw.redstone.library.v1:${owner}`;

export function loadQuickLibrary(storage: StorageLike | null, owner: string): CustomDef[] {
  try {
    const text = storage?.getItem(quickLibraryKey(owner));
    return text ? readList(JSON.parse(text) as unknown, readDef, MAX_QUICK_LIBRARY) : [];
  } catch {
    return [];
  }
}

/** False when the browser would not keep it (no storage, or full). */
export function saveQuickLibrary(storage: StorageLike | null, owner: string, defs: readonly CustomDef[]): boolean {
  try {
    if (!storage) return false;
    storage.setItem(quickLibraryKey(owner), JSON.stringify(defs.slice(0, MAX_QUICK_LIBRARY).map(writeDef)));
    return true;
  } catch {
    return false;
  }
}

/** The library as a JSON file's text. */
export const exportLibrary = (defs: readonly CustomDef[]): string =>
  JSON.stringify({ redstoneLibrary: 1, defs: defs.map(writeDef) }, null, 1);

/** The definitions an exported text holds (a bare list is taken too); null when the text is not one. */
export function importLibrary(text: string): CustomDef[] | null {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return null;
  }
  const list = Array.isArray(raw) ? raw : isObject(raw) && raw.redstoneLibrary === 1 ? raw.defs : null;
  return Array.isArray(list) ? readList(list, readDef, MAX_QUICK_LIBRARY) : null;
}

/** A new custom definition id. */
export const newDefId = (): string =>
  `u.${Date.now().toString(36)}${Math.floor(Math.random() * 36 ** 4)
    .toString(36)
    .padStart(4, '0')}`;
