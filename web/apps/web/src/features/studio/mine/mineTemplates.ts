/**
 * The Design Studio's parametric mine pieces: a port of the bot's MineTemplates.cs (the same cells for the same parameters; a
 * golden hash in both tests). A mine design is the inverse of a build: its cells are the ones to dig (id 0, forced air), plus
 * the few blocks put in once they are dug (torches).
 */
import type { MineTerrain } from './mineTerrain';

export type MineCell = [number, number, number, number, number];
export type P3 = [number, number, number];

export const TORCH = 50;
export const MIN_SIZE = 3,
  MAX_SIZE = 6;

export interface SpiralSpec {
  /** The square's west column and north row (canvas x, z). */
  x: number;
  z: number;
  /** The first step's feet level (the entry) and the lowest step's. */
  top: number;
  bottom: number;
  size?: number;
  core?: number;
  /** Seen from above, going down (default true). */
  cw?: boolean;
  torchEvery?: number;
  landings?: number[];
}

export interface StripSpec {
  x: number;
  y: number;
  z: number;
  axis?: 'x' | 'z';
  length?: number;
  width?: number;
  height?: number;
  spacing?: number;
  branchLength?: number;
  sides?: 'both' | 'left' | 'right' | 'none';
  torchEvery?: number;
}

/** The defaults every reader of a spiral step falls back on (the bot's SpiralSpec.Default). */
export const SPIRAL_DEFAULTS = { x: 30, z: 30, top: 64, bottom: 11, size: 4, core: 2, torchEvery: 0 } as const;

/**
 * The defaults every reader of a strip step falls back on (the bot's StripSpec.Default): the layout study's best
 * (docs/design/mine-blueprints.md): feet at y 11, a 1×2 corridor, branches 4 solid blocks apart.
 */
export const STRIP_DEFAULTS = { x: 0, y: 11, z: 0, length: 30, width: 1, height: 2, spacing: 4, branchLength: 12, torchEvery: 0 } as const;

export interface MineCells {
  cells: MineCell[];
  entry: P3;
  path: P3[];
  label: string;
}

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

/** The torch meta that hangs it on the block at side (dx, dz) (the bot's DesignerCanvas.MetaOn). */
export function torchOn(dx: number, dz: number): number {
  if (dx === -1 && dz === 0) return 1;
  if (dx === 1 && dz === 0) return 2;
  if (dx === 0 && dz === -1) return 3;
  return 4;
}

/** The ring edge of a walkway cell: 0 north, 1 east, 2 south, 3 west (corners go clockwise). */
export function edgeOf(lx: number, lz: number, n: number): number {
  const d = [lz, n - 1 - lx, n - 1 - lz, lx]; // distance to the north, east, south and west edge
  const m = Math.min(d[0]!, d[1]!, d[2]!, d[3]!);
  // a corner belongs to the edge after it, clockwise
  for (let e = 0; e < 4; e++) if (d[e] === m && d[(e + 1) % 4] === m) return (e + 1) % 4;
  return d.indexOf(m);
}

export function ringIndex(lx: number, lz: number, n: number): number {
  switch (edgeOf(lx, lz, n)) {
    case 0:
      return Math.min(lx, n - 2);
    case 1:
      return n - 1 + Math.min(lz, n - 2);
    case 2:
      return 2 * (n - 1) + Math.min(n - 1 - lx, n - 2);
    default:
      return 3 * (n - 1) + Math.min(n - 1 - lz, n - 2);
  }
}

export function outer(idx: number, n: number): [number, number] {
  const e = n - 1;
  switch (Math.trunc(idx / e)) {
    case 0:
      return [idx, 0];
    case 1:
      return [e, idx - e];
    case 2:
      return [e - (idx - 2 * e), e];
    default:
      return [0, e - (idx - 3 * e)];
  }
}

function outSide(lx: number, lz: number, n: number): [number, number] {
  switch (edgeOf(lx, lz, n)) {
    case 0:
      return [0, -1];
    case 1:
      return [1, 0];
    case 2:
      return [0, 1];
    default:
      return [-1, 0];
  }
}

/** The spiral shaft (see MineTemplates.Spiral): step k digs ring place k mod 4(n−1) three high, feet at top − k. */
export function spiral(s: SpiralSpec): MineCells {
  const n = clamp(s.size ?? SPIRAL_DEFAULTS.size, MIN_SIZE, MAX_SIZE),
    c = clamp(s.core ?? SPIRAL_DEFAULTS.core, 1, n - 2),
    ring = 4 * (n - 1);
  const cw = s.cw ?? true;
  const byPlace = ringPlaces(n, c);
  const place = (k: number) => (cw ? k % ring : (ring - (k % ring)) % ring);
  const cells: MineCell[] = [];
  const path: P3[] = [];
  const top = s.top,
    bottom = Math.max(1, s.bottom);
  for (let k = 0; top - k >= bottom; k++) {
    const y = top - k,
      at = place(k);
    for (const [lx, lz] of byPlace[at]!) for (let dy = 0; dy < 3; dy++) cells.push([s.x + lx, y + dy, s.z + lz, 0, 0]);
    const [ox, oz] = outer(at, n);
    path.push([s.x + ox, y, s.z + oz]);
  }
  digLandings(cells, s, n, bottom, top);
  spiralTorches(cells, s, n, path);
  const entry: P3 = path[0] ?? [s.x, top, s.z];
  return { cells, entry, path, label: spiralLabel(s, n, c, bottom, cw) };
}

/** The walkway cells (the n × n square less its c × c core) by their ring place. */
function ringPlaces(n: number, c: number): Array<Array<[number, number]>> {
  const off = Math.trunc((n - c) / 2);
  const inCore = (v: number) => v >= off && v < off + c;
  const byPlace: Array<Array<[number, number]>> = Array.from({ length: 4 * (n - 1) }, () => []);
  for (let lz = 0; lz < n; lz++)
    for (let lx = 0; lx < n; lx++) if (!(inCore(lx) && inCore(lz))) byPlace[ringIndex(lx, lz, n)]!.push([lx, lz]);
  return byPlace;
}

/** Each landing (inside the shaft's span, once, low to high) digs the whole square two high. */
function digLandings(cells: MineCell[], s: SpiralSpec, n: number, bottom: number, top: number) {
  const landings = [...new Set(s.landings ?? [])].filter((l) => l >= bottom && l <= top).sort((a, b) => a - b);
  for (const ly of landings)
    for (let dy = 0; dy < 2; dy++)
      for (let lz = 0; lz < n; lz++) for (let lx = 0; lx < n; lx++) cells.push([s.x + lx, ly + dy, s.z + lz, 0, 0]);
}

/** A torch every `torchEvery` steps, two above the step, hung on the shaft's outer wall. */
function spiralTorches(cells: MineCell[], s: SpiralSpec, n: number, path: readonly P3[]) {
  const every = s.torchEvery ?? 0;
  if (every <= 0) return;
  for (let k = every; k < path.length; k += every) {
    const p = path[k]!;
    const [dx, dz] = outSide(p[0] - s.x, p[2] - s.z, n);
    cells.push([p[0], p[1] + 2, p[2], TORCH, torchOn(dx, dz)]);
  }
}

function spiralLabel(s: SpiralSpec, n: number, c: number, bottom: number, cw: boolean): string {
  const landings = s.landings?.length ? ', landings ' + s.landings.join('/') : '';
  return `spiral shaft ${n}×${n} (core ${c}×${c}) y${s.top}→${bottom}${cw ? '' : ' ccw'}${landings}`;
}

/** The strip mine (see MineTemplates.Strip): a main corridor with 1×2 branches `spacing` solid blocks apart. */
export function strip(s: StripSpec): MineCells {
  const f = stripFrame(s);
  for (let a = 0; a < f.len; a++) for (let across = 0; across < f.w; across++) for (let dy = 0; dy < f.h; dy++) digStrip(f, a, across, s.y + dy);
  const sides = (s.sides ?? 'both').toLowerCase();
  const branches = digBranches(f, sides);
  stripTorches(f, branches, s.torchEvery ?? 0);
  const [ex, ez] = stripAt(f, 0, 0);
  const path: P3[] = Array.from({ length: f.len }, (_, a) => {
    const [x, z] = stripAt(f, a, 0);
    return [x, s.y, z];
  });
  const label = `strip mine at y${s.y} along ${f.alongX ? 'x' : 'z'}: ${f.w}×${f.h} × ${f.len}, ${branches.size} branch rows ${f.sp} apart (${f.bl} long, ${sides})`;
  return { cells: f.cells, entry: [ex, s.y, ez], path, label };
}

/** A strip mine being dug: its spec, clamped sizes, direction and the cells so far. */
interface StripFrame {
  s: StripSpec;
  len: number;
  w: number;
  h: number;
  sp: number;
  bl: number;
  alongX: boolean;
  cells: MineCell[];
}

function stripFrame(s: StripSpec): StripFrame {
  return {
    s,
    len: clamp(s.length ?? STRIP_DEFAULTS.length, 1, 64),
    w: clamp(s.width ?? STRIP_DEFAULTS.width, 1, 2),
    h: clamp(s.height ?? STRIP_DEFAULTS.height, 2, 3),
    sp: clamp(s.spacing ?? STRIP_DEFAULTS.spacing, 1, 8),
    bl: clamp(s.branchLength ?? STRIP_DEFAULTS.branchLength, 0, 64),
    alongX: (s.axis ?? 'x') !== 'z',
    cells: [],
  };
}

/** The (x, z) of a cell `a` along the corridor and `across` it. */
function stripAt(f: StripFrame, a: number, across: number): [number, number] {
  return f.alongX ? [f.s.x + a, f.s.z + across] : [f.s.x + across, f.s.z + a];
}

function digStrip(f: StripFrame, a: number, across: number, y: number) {
  const [x, z] = stripAt(f, a, across);
  f.cells.push([x, y, z, 0, 0]);
}

/** The 1×2 branches off the chosen sides, `spacing` solid blocks apart; returns the corridor places they leave from. */
function digBranches(f: StripFrame, sides: string): Set<number> {
  const left = sides === 'both' || sides === 'left';
  const right = sides === 'both' || sides === 'right';
  const branches = new Set<number>();
  for (let a = f.sp; a < f.len; a += f.sp + 1) {
    branches.add(a);
    for (let b = 1; b <= f.bl; b++)
      for (let dy = 0; dy < 2; dy++) {
        if (left) digStrip(f, a, -b, f.s.y + dy);
        if (right) digStrip(f, a, f.w - 1 + b, f.s.y + dy);
      }
  }
  return branches;
}

/** A torch every `every` blocks on the corridor's first wall, moved one on past a branch (and skipped if that is one too). */
function stripTorches(f: StripFrame, branches: ReadonlySet<number>, every: number) {
  if (every <= 0) return;
  const meta = f.alongX ? torchOn(0, -1) : torchOn(-1, 0);
  for (let a = every - 1; a < f.len; a += every) {
    const at = branches.has(a) ? a + 1 : a;
    if (at >= f.len || branches.has(at)) continue;
    const [x, z] = stripAt(f, at, 0);
    f.cells.push([x, f.s.y + 1, z, TORCH, meta]);
  }
}

/** A dug room: w × d × h cells from (x, y, z). */
export function room(x: number, y: number, z: number, w: number, d: number, h: number): MineCells {
  const cells: MineCell[] = [];
  for (let dy = 0; dy < h; dy++) for (let dz = 0; dz < d; dz++) for (let dx = 0; dx < w; dx++) cells.push([x + dx, y + dy, z + dz, 0, 0]);
  return { cells, entry: [x, y, z], path: [[x, y, z]], label: `room ${w}×${d}×${h} at y${y}` };
}

type Step = Record<string, unknown>;
const num = (o: Step, ...keys: string[]): number | undefined => {
  for (const k of keys) {
    const v = o[k];
    if (typeof v === 'number' && Number.isInteger(v)) return v;
    if (typeof v === 'string' && /^-?\d+$/.test(v)) return Number(v);
  }
  return undefined;
};
const str = (o: Step, k: string): string | undefined => {
  const v = o[k];
  return typeof v === 'string' ? v : undefined;
};

/** A whole-number field (the first of `keys` present), or `def`. */
const int = (o: Step, def: number, ...keys: string[]): number => num(o, ...keys) ?? def;
const SPIRAL_TEMPLATES = new Set(['spiral', 'spiral_shaft', 'shaft']);
const STRIP_TEMPLATES = new Set(['strip', 'strip_mine']);

/** One template step (a preset's): `{template: "spiral"|"strip", …}`; a spiral's `top: "surface"` reads the terrain. */
export function fromStep(step: Step, terrain: MineTerrain | null): MineCells {
  const t = (str(step, 'template') ?? 'spiral').toLowerCase();
  if (SPIRAL_TEMPLATES.has(t)) return spiral(spiralStep(step, terrain));
  if (STRIP_TEMPLATES.has(t)) return strip(stripStep(step));
  throw new Error(`no mine template '${t}' (spiral, strip)`);
}

function spiralStep(step: Step, terrain: MineTerrain | null): SpiralSpec {
  const d = SPIRAL_DEFAULTS;
  const x = int(step, d.x, 'x'),
    z = int(step, d.z, 'z'),
    size = int(step, d.size, 'size');
  const top = num(step, 'top') ?? terrain?.surfaceOver(x, z, x + size - 1, z + size - 1) ?? d.top;
  const dir = (str(step, 'dir') ?? 'cw').toLowerCase();
  const landings = Array.isArray(step.landings) ? (step.landings as unknown[]).filter((v): v is number => typeof v === 'number') : undefined;
  return {
    x,
    z,
    top,
    bottom: int(step, d.bottom, 'bottom'),
    size,
    core: int(step, d.core, 'core'),
    cw: !(dir === 'ccw' || dir === 'counterclockwise'),
    torchEvery: int(step, d.torchEvery, 'torchEvery', 'torch_every'),
    ...(landings ? { landings } : {}),
  };
}

function stripStep(step: Step): StripSpec {
  const d = STRIP_DEFAULTS;
  const axis = (str(step, 'axis') ?? 'x').trim().toLowerCase();
  return {
    x: int(step, d.x, 'x'),
    y: int(step, d.y, 'y'),
    z: int(step, d.z, 'z'),
    axis: axis.startsWith('z') ? 'z' : 'x',
    length: int(step, d.length, 'length'),
    width: int(step, d.width, 'width'),
    height: int(step, d.height, 'height'),
    spacing: int(step, d.spacing, 'spacing'),
    branchLength: int(step, d.branchLength, 'branchLength', 'branch_length'),
    sides: (str(step, 'sides') ?? 'both') as StripSpec['sides'],
    torchEvery: int(step, d.torchEvery, 'torchEvery', 'torch_every'),
  };
}

/** FNV-1a over the cells (x, y, z, id, meta in order): the golden value both languages' tests check. */
export function cellsHash(cells: readonly MineCell[]): number {
  let h = 2166136261;
  for (const c of cells) for (const v of c) h = Math.imul(h ^ v, 16777619) >>> 0;
  return h;
}
