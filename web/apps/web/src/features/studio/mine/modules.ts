/** Composable mine layouts. Anchors are the footprint's centre at the entrance feet Y. */
import type { V3 } from '../canvas';
import { rotateMeta } from '../facing';
import { room, spiral, strip, type MineCells } from './mineTemplates';

export type MineModuleKind = 'tunnel' | 'branches' | 'chamber' | 'stairs' | 'spiral' | 'junction';
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
}
export interface MineModuleInstance { id: string; spec: MineModuleSpec; at: V3 }
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
export const moduleName = (kind: MineModuleKind): string => MINE_MODULES.find((m) => m.kind === kind)?.name ?? 'Mine layout';
export function defaultModule(kind: MineModuleKind): MineModuleSpec {
  return { kind, rotation: 0, length: kind === 'chamber' || kind === 'junction' ? 7 : 16,
    width: kind === 'chamber' ? 7 : 1, height: kind === 'chamber' ? 3 : 2,
    branchLength: 8, spacing: 4, depth: 6, size: 4 };
}
const integer = (n: unknown, lo: number, hi: number, fallback: number) =>
  typeof n === 'number' && Number.isFinite(n) ? Math.max(lo, Math.min(hi, Math.round(n))) : fallback;
export function normalizeModule(value: unknown): MineModuleSpec | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Partial<MineModuleSpec>;
  if (!MINE_MODULES.some((m) => m.kind === v.kind)) return null;
  const d = defaultModule(v.kind!);
  return { kind: d.kind, rotation: integer(v.rotation, 0, 3, 0), length: integer(v.length, 3, 48, d.length),
    width: integer(v.width, 1, d.kind === 'chamber' ? 24 : 2, d.width), height: integer(v.height, 2, 6, d.height),
    branchLength: integer(v.branchLength, 2, 24, d.branchLength), spacing: integer(v.spacing, 1, 8, d.spacing),
    depth: integer(v.depth, 1, 96, d.depth), size: integer(v.size, 3, 6, d.size) };
}
export const encodeModule = (spec: MineModuleSpec): string => JSON.stringify(spec);
export function decodeModule(raw: string): MineModuleSpec | null {
  if (raw.length > 2048) return null;
  try { return normalizeModule(JSON.parse(raw)); } catch { return null; }
}
export const decodeModuleDrag = decodeModule;
const rotate = ([x, y, z]: V3, q: number): V3 => {
  for (let i = 0; i < q; i++) [x, z] = [-z, x];
  return [x, y, z];
};
const plus = (a: V3, b: V3): V3 => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];

/** Local geometry uses +X for the main passage. Existing generators remain golden-compatible. */
function local(spec: MineModuleSpec): MineCells {
  const half = Math.floor((spec.length - 1) / 2);
  if (spec.kind === 'tunnel' || spec.kind === 'branches')
    return strip({ x: -half, y: 0, z: -Math.floor((spec.width - 1) / 2), length: spec.length,
      width: spec.width, height: spec.height, branchLength: spec.kind === 'branches' ? spec.branchLength : 0,
      spacing: spec.spacing, sides: spec.kind === 'branches' ? 'both' : 'none' });
  if (spec.kind === 'chamber') return room(-half, 0, -Math.floor((spec.width - 1) / 2), spec.length, spec.width, spec.height);
  if (spec.kind === 'spiral') {
    const offset = Math.floor((spec.size - 1) / 2);
    // Generate at a positive level, then translate: the shared generator clamps its absolute bottom to Y1.
    const result = spiral({ x: -offset, z: -offset, top: spec.depth + 1, bottom: 1, size: spec.size, core: spec.size - 2 });
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
export function moduleCells(input: MineModuleSpec, at: V3): MineCells {
  const spec = normalizeModule(input)!;
  const shape = local(spec);
  const point = (p: V3) => plus(rotate(p, spec.rotation), at);
  const unique = new Map<string, MineCells['cells'][number]>();
  for (const [x, y, z, id, meta] of shape.cells) {
    const p = point([x, y, z]);
    unique.set(p.join(','), [...p, id, rotateMeta(id, meta, spec.rotation, '1.12.2')]);
  }
  return { cells: [...unique.values()], entry: point(shape.entry), path: shape.path.map(point), label: moduleName(spec.kind) };
}

export function modulePorts(input: MineModuleSpec, at: V3): MineModulePort[] {
  const s = normalizeModule(input)!, shape = local(s);
  const port = (p: V3, out: V3) => ({ at: plus(rotate(p, s.rotation), at), out: rotate(out, s.rotation) });
  if (s.kind === 'stairs') return [port(shape.path[0]!, [-1, 0, 0]), port(shape.path.at(-1)!, [1, 0, 0])];
  if (s.kind === 'spiral') {
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
