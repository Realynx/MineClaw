/** Composable mine layouts. Anchors are the footprint's centre at the entrance feet Y. */
import type { V3 } from '../canvas';
import { rotateMeta } from '../facing';
import { room, spiral, strip, type MineCells } from './mineTemplates';

export type MineModuleKind = 'tunnel' | 'branches' | 'chamber' | 'stairs' | 'spiral' | 'junction' | 'custom';
export const MINE_MODULE_MAX_LENGTH = 192;
export const MINE_MODULE_MAX_BRANCH = 96;
export const MINE_MODULE_MAX_ROOM = 96;
export const MINE_MODULE_MAX_CELLS = 400_000;
export interface MineModuleSpec {
  kind: MineModuleKind;
  rotation: number;
  length: number;
  width: number;
  height: number;
  branchLength: number;
  spacing: number;
  depth: number;
  size: number;
  clockwise?: boolean;
  core?: number;
  torchEvery?: number;
  landingDepths?: number[];
  sides?: 'both' | 'left' | 'right' | 'none';
  label?: string;
  parts?: Array<{ spec: MineModuleSpec; at: V3 }>;
}
export interface MineModuleInstance { id: string; spec: MineModuleSpec; at: V3; visible?: boolean }
export interface MineModulePort { at: V3; out: V3 }
export const MINE_MODULE_MIME = 'application/x-mineclaw-mine-module';
export const MINE_MODULES: readonly { kind: MineModuleKind; name: string; detail: string; hint: string }[] = [
  { kind: 'tunnel', name: 'Tunnel', detail: '1 × 2 exploration', hint: 'A long, narrow tunnel exposes fresh rock with little excavation.' },
  { kind: 'branches', name: 'Branch mine', detail: 'Spaced side tunnels', hint: 'Branches leave four solid blocks between rows by default; connect a shaft or hub to the main tunnel.' },
  { kind: 'chamber', name: 'Chamber', detail: 'A working hub', hint: 'Room for storage, crafting and several connected corridors.' },
  { kind: 'stairs', name: 'Stair descent', detail: 'One level per step', hint: 'Three blocks of clearance keep the descent walkable without digging straight down.' },
  { kind: 'spiral', name: 'Spiral shaft', detail: 'Compact access', hint: 'A descending walkway around a solid core saves horizontal space.' },
  { kind: 'junction', name: 'Crossroads', detail: 'Four connections', hint: 'A compact junction joins tunnels into a larger network.' },
];
export const moduleName = (kind: MineModuleKind): string => kind === 'custom' ? 'Custom component' : MINE_MODULES.find((m) => m.kind === kind)?.name ?? 'Mine layout';
export function defaultModule(kind: MineModuleKind): MineModuleSpec {
  return { kind, rotation: 0, length: kind === 'chamber' || kind === 'junction' ? 7 : 16,
    width: kind === 'chamber' ? 7 : 1, height: kind === 'chamber' ? 3 : 2,
    branchLength: 8, spacing: 4, depth: 6, size: 4 };
}
const integer = (n: unknown, lo: number, hi: number, fallback: number) =>
  typeof n === 'number' && Number.isFinite(n) ? Math.max(lo, Math.min(hi, Math.round(n))) : fallback;
export function normalizeModule(value: unknown, depth = 0, budget = { parts: 0, cells: 0 }): MineModuleSpec | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Partial<MineModuleSpec>;
  if (depth > 8 || ++budget.parts > 256) return null;
  if (v.kind === 'custom') {
    if (!Array.isArray(v.parts) || !v.parts.length || v.parts.length > 100) return null;
    const parts: NonNullable<MineModuleSpec['parts']> = [];
    for (const part of v.parts) {
      if (!part || !Array.isArray(part.at) || part.at.length !== 3 || !part.at.every((n) => Number.isInteger(n) && Math.abs(n) <= 512)) return null;
      const spec = normalizeModule(part.spec, depth + 1, budget);
      if (!spec) return null;
      parts.push({ spec, at: [...part.at] as V3 });
    }
    return { ...defaultModule('custom'), rotation: integer(v.rotation, 0, 3, 0), label: typeof v.label === 'string' ? v.label.trim().slice(0, 48) || 'Custom component' : 'Custom component', parts };
  }
  if (!MINE_MODULES.some((m) => m.kind === v.kind)) return null;
  const d = defaultModule(v.kind!);
  const spec: MineModuleSpec = { kind: d.kind, rotation: integer(v.rotation, 0, 3, 0), length: integer(v.length, 3, MINE_MODULE_MAX_LENGTH, d.length),
    width: integer(v.width, 1, d.kind === 'chamber' ? MINE_MODULE_MAX_ROOM : 2, d.width), height: integer(v.height, 2, 6, d.height),
    branchLength: integer(v.branchLength, 2, MINE_MODULE_MAX_BRANCH, d.branchLength), spacing: integer(v.spacing, 1, 8, d.spacing),
    depth: integer(v.depth, 1, 126, d.depth), size: integer(v.size, 3, 6, d.size) };
  if (typeof v.clockwise === 'boolean') spec.clockwise = v.clockwise;
  if (v.core !== undefined) spec.core = integer(v.core, 1, spec.size - 2, spec.size - 2);
  if (v.torchEvery !== undefined) spec.torchEvery = integer(v.torchEvery, 0, 96, 0);
  if (Array.isArray(v.landingDepths)) spec.landingDepths = [...new Set(v.landingDepths.filter((n) => Number.isInteger(n) && n >= 0 && n <= 126))].slice(0, 127);
  if (v.sides && ['both', 'left', 'right', 'none'].includes(v.sides)) spec.sides = v.sides;
  if (typeof v.label === 'string' && v.label.trim()) spec.label = v.label.trim().slice(0, 48);
  const volume = spec.kind === 'branches' ? (spec.length + 2 * Math.ceil(spec.length / (spec.spacing + 1)) * spec.branchLength) * spec.width * spec.height
    : spec.kind === 'spiral' ? (spec.depth + 1) * spec.size * 3
    : spec.kind === 'stairs' ? (spec.depth + 1) * spec.width * 3
    : spec.length * spec.width * spec.height * (spec.kind === 'junction' ? 2 : 1);
  budget.cells += volume;
  return budget.cells <= MINE_MODULE_MAX_CELLS ? spec : null;
}
export const encodeModule = (spec: MineModuleSpec): string => JSON.stringify(spec);
export function decodeModule(raw: string): MineModuleSpec | null {
  if (raw.length > 100_000) return null;
  try { return normalizeModule(JSON.parse(raw)); } catch { return null; }
}
export const decodeModuleDrag = decodeModule;
const rotate = ([x, y, z]: V3, q: number): V3 => {
  for (let i = 0; i < q; i++) [x, z] = [-z, x];
  return [x, y, z];
};
const plus = (a: V3, b: V3): V3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

/**
 * A group's parts as components standing where the group puts them, in its order. A group's cell is
 * rotate(rotate(c, r) + part.at, q) + at, which is the part turned by r + q and placed at rotate(part.at, q) + at: the same cells.
 */
export function ungroupParts(group: MineModuleSpec, at: V3): Array<{ spec: MineModuleSpec; at: V3 }> {
  const q = group.rotation;
  return (group.parts ?? []).map((part) => ({ spec: { ...part.spec, rotation: (part.spec.rotation + q) % 4 }, at: plus(rotate(part.at, q), at) }));
}

/** Local geometry uses +X for the main passage. Existing generators remain golden-compatible. */
function local(spec: MineModuleSpec): MineCells {
  if (spec.kind === 'custom') {
    const shapes = (spec.parts ?? []).map((part) => moduleCells(part.spec, part.at));
    return { cells: shapes.flatMap((shape) => shape.cells), path: shapes.flatMap((shape) => shape.path), entry: shapes[0]?.entry ?? [0, 0, 0], label: spec.label ?? 'Custom component' };
  }
  const half = Math.floor((spec.length - 1) / 2);
  if (spec.kind === 'tunnel' || spec.kind === 'branches')
    return strip({ x: -half, y: 0, z: -Math.floor((spec.width - 1) / 2), length: spec.length,
      width: spec.width, height: spec.height, branchLength: spec.kind === 'branches' ? spec.branchLength : 0,
      spacing: spec.spacing, sides: spec.kind === 'branches' ? spec.sides ?? 'both' : 'none', torchEvery: spec.torchEvery });
  if (spec.kind === 'chamber') return room(-half, 0, -Math.floor((spec.width - 1) / 2), spec.length, spec.width, spec.height);
  if (spec.kind === 'spiral') {
    const offset = Math.floor((spec.size - 1) / 2);
    // Generate at a positive level, then translate: the shared generator clamps its absolute bottom to Y1.
    const result = spiral({ x: -offset, z: -offset, top: spec.depth + 1, bottom: 1, size: spec.size, core: spec.core ?? spec.size - 2,
      cw: spec.clockwise, torchEvery: spec.torchEvery, landings: spec.landingDepths?.filter((d) => d <= spec.depth).map((d) => spec.depth + 1 - d) });
    result.cells = result.cells.map(([x, y, z, id, meta]) => [x, y - spec.depth - 1, z, id, meta]);
    result.path = result.path.map(([x, y, z]) => [x, y - spec.depth - 1, z]);
    result.entry = result.path[0]!;
    return result;
  }
  if (spec.kind === 'stairs') {
    const cells: MineCells['cells'] = [], path: V3[] = [];
    for (let step = 0; step <= spec.depth; step++) {
      const x = step - Math.floor(spec.depth / 2);
      path.push([x, -step, 0]);
      for (let z = 0; z < spec.width; z++) for (let y = 0; y < 3; y++) cells.push([x, y - step, z, 0, 0]);
    }
    return { cells, path, entry: path[0]!, label: 'stair descent' };
  }
  const a = strip({ x: -half, y: 0, z: 0, length: spec.length, width: spec.width, height: spec.height, branchLength: 0 });
  const b = strip({ x: 0, y: 0, z: -half, axis: 'z', length: spec.length, width: spec.width, height: spec.height, branchLength: 0 });
  return { ...a, cells: [...a.cells, ...b.cells] };
}
// A bounded LRU: history retains old specs, so a WeakMap alone would also retain all their large geometry arrays.
const shapeCache = new Map<MineModuleSpec, { key: string; shape: MineCells }>();
let cachedCells = 0;
const MAX_CACHED_CELLS = 800_000;
export function moduleCells(input: MineModuleSpec, at: V3): MineCells {
  const key = at.join(','), cached = shapeCache.get(input);
  if (cached?.key === key) {
    shapeCache.delete(input); shapeCache.set(input, cached);
    return cached.shape;
  }
  const spec = normalizeModule(input);
  if (!spec) return { cells: [], path: [], entry: at, label: 'Invalid component' };
  const shape = local(spec);
  const point = (p: V3) => plus(rotate(p, spec.rotation), at);
  const unique = new Map<string, MineCells['cells'][number]>();
  for (const [x, y, z, id, meta] of shape.cells) {
    const p = point([x, y, z]);
    unique.set(p.join(','), [...p, id, rotateMeta(id, meta, spec.rotation, '1.12.2')]);
  }
  const result = { cells: [...unique.values()], entry: point(shape.entry), path: shape.path.map(point), label: spec.label ?? moduleName(spec.kind) };
  cachedCells -= shapeCache.get(input)?.shape.cells.length ?? 0;
  shapeCache.delete(input);
  shapeCache.set(input, { key, shape: result }); cachedCells += result.cells.length;
  while (cachedCells > MAX_CACHED_CELLS || shapeCache.size > 256) {
    const first = shapeCache.keys().next().value!;
    cachedCells -= shapeCache.get(first)!.shape.cells.length; shapeCache.delete(first);
  }
  return result;
}

const portCache = new WeakMap<MineModuleSpec, readonly MineModulePort[]>();

/** Specs are immutable authoring snapshots. Cache local ports so pointer snapping never rebuilds group occupancy. */
export function moduleLocalPorts(input: MineModuleSpec): readonly MineModulePort[] {
  let ports = portCache.get(input);
  if (!ports) { ports = buildModulePorts(input, [0, 0, 0]); portCache.set(input, ports); }
  return ports;
}

export function modulePorts(input: MineModuleSpec, at: V3): MineModulePort[] {
  return moduleLocalPorts(input).map((p) => ({ at: plus(p.at, at), out: p.out }));
}

function buildModulePorts(input: MineModuleSpec, at: V3): MineModulePort[] {
  const s = normalizeModule(input);
  if (!s) return [];
  if (s.kind === 'custom') {
    const occupied = new Set(moduleCells(s, at).cells.map((cell) => cell.slice(0, 3).join(',')));
    const ports = (s.parts ?? []).flatMap((part) => modulePorts(part.spec, part.at))
      .map((p) => ({ at: plus(rotate(p.at, s.rotation), at), out: rotate(p.out, s.rotation) }));
    return ports.filter((p) => !occupied.has(plus(p.at, p.out).join(',')));
  }
  const port = (p: V3, out: V3) => ({ at: plus(rotate(p, s.rotation), at), out: rotate(out, s.rotation) });
  if (s.kind === 'stairs') {
    const half = Math.floor(s.depth / 2);
    return [port([-half, 0, 0], [-1, 0, 0]), port([s.depth - half, -s.depth, 0], [1, 0, 0])];
  }
  if (s.kind === 'spiral') {
    const shape = local(s);
    const edge = (p: V3): V3 => {
      const offset = Math.floor((s.size - 1) / 2);
      if (p[2] === -offset) return [0, 0, -1];
      if (p[0] === s.size - 1 - offset) return [1, 0, 0];
      if (p[2] === s.size - 1 - offset) return [0, 0, 1];
      return [-1, 0, 0];
    };
    return [port(shape.path[0]!, edge(shape.path[0]!)), port(shape.path.at(-1)!, edge(shape.path.at(-1)!))];
  }
  const lo = -Math.floor((s.length - 1) / 2), hi = lo + s.length - 1;
  const ports = [port([lo, 0, 0], [-1, 0, 0]), port([hi, 0, 0], [1, 0, 0])];
  if (s.kind === 'junction') ports.push(port([0, 0, lo], [0, 0, -1]), port([0, 0, hi], [0, 0, 1]));
  if (s.kind === 'chamber') {
    const z = -Math.floor((s.width - 1) / 2);
    ports.push(port([0, 0, z], [0, 0, -1]), port([0, 0, z + s.width - 1], [0, 0, 1]));
  }
  if (s.kind === 'branches') {
    for (let a = s.spacing; a < s.length; a += s.spacing + 1) {
      ports.push(port([lo + a, 0, -s.branchLength], [0, 0, -1]), port([lo + a, 0, s.width - 1 + s.branchLength], [0, 0, 1]));
    }
  }
  return ports;
}

/** The cell just outside each entrance of `instances` (except `excludeId`): where another piece's entrance can join it. */
export function openPortCells(instances: readonly MineModuleInstance[], excludeId?: string): V3[] {
  return instances.filter((m) => m.id !== excludeId).flatMap((m) => modulePorts(m.spec, m.at).map((p) => plus(p.at, p.out)));
}

/** Of those cells, the ones `spec` at `at` joins: one of its entrances stands there, facing back (as snapModuleAt joins them). */
export function joinedPortCells(spec: MineModuleSpec, at: V3, instances: readonly MineModuleInstance[], excludeId?: string): V3[] {
  const ours = modulePorts(spec, at);
  const joined: V3[] = [];
  for (const instance of instances) {
    if (instance.id === excludeId) continue;
    for (const theirs of modulePorts(instance.spec, instance.at)) {
      const target = plus(theirs.at, theirs.out);
      if (ours.some((own) => own.at.every((n, i) => n === target[i]) && own.out.every((n, i) => n === -theirs.out[i]!))) joined.push(target);
    }
  }
  return joined;
}

/** Connect opposing entrances on the same feet level, leaving adjacent (not overlapping) entrance cells. */
export function snapModuleAt(spec: MineModuleSpec, at: V3, instances: readonly MineModuleInstance[], excludeId?: string, radius = 2): V3 {
  let best = radius * radius + 0.01, result = at;
  const ours = modulePorts(spec, at);
  for (const instance of instances) {
    if (instance.id === excludeId) continue;
    for (const theirs of modulePorts(instance.spec, instance.at)) for (const own of ours) {
      if (own.at[1] !== theirs.at[1] || own.out.some((n, i) => n !== -theirs.out[i]!)) continue;
      const target = plus(theirs.at, theirs.out);
      const dx = target[0] - own.at[0], dz = target[2] - own.at[2], distance = dx * dx + dz * dz;
      if (distance < best) { best = distance; result = [at[0] + dx, at[1], at[2] + dz]; }
    }
  }
  return result;
}
