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
  const dN = lz,
    dE = n - 1 - lx,
    dS = n - 1 - lz,
    dW = lx;
  const m = Math.min(dN, dE, dS, dW);
  if (dN === m && dE === m) return 1;
  if (dE === m && dS === m) return 2;
  if (dS === m && dW === m) return 3;
  if (dW === m && dN === m) return 0;
  return dN === m ? 0 : dE === m ? 1 : dS === m ? 2 : 3;
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
  const n = clamp(s.size ?? 4, MIN_SIZE, MAX_SIZE),
    c = clamp(s.core ?? 2, 1, n - 2),
    off = Math.trunc((n - c) / 2),
    ring = 4 * (n - 1);
  const cw = s.cw ?? true;
  const core = (lx: number, lz: number) => lx >= off && lx < off + c && lz >= off && lz < off + c;
  const byPlace: Array<Array<[number, number]>> = Array.from({ length: ring }, () => []);
  for (let lz = 0; lz < n; lz++) for (let lx = 0; lx < n; lx++) if (!core(lx, lz)) byPlace[ringIndex(lx, lz, n)]!.push([lx, lz]);
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
  const landings = [...new Set(s.landings ?? [])].filter((l) => l >= bottom && l <= top).sort((a, b) => a - b);
  for (const ly of landings)
    for (let dy = 0; dy < 2; dy++)
      for (let lz = 0; lz < n; lz++) for (let lx = 0; lx < n; lx++) cells.push([s.x + lx, ly + dy, s.z + lz, 0, 0]);
  const every = s.torchEvery ?? 0;
  if (every > 0)
    for (let k = every; k < path.length; k += every) {
      const p = path[k]!;
      const [dx, dz] = outSide(p[0] - s.x, p[2] - s.z, n);
      cells.push([p[0], p[1] + 2, p[2], TORCH, torchOn(dx, dz)]);
    }
  const entry: P3 = path[0] ?? [s.x, top, s.z];
  const label = `spiral shaft ${n}×${n} (core ${c}×${c}) y${top}→${bottom}${cw ? '' : ' ccw'}${s.landings?.length ? ', landings ' + s.landings.join('/') : ''}`;
  return { cells, entry, path, label };
}

/** The strip mine (see MineTemplates.Strip): a main corridor with 1×2 branches `spacing` solid blocks apart. */
export function strip(s: StripSpec): MineCells {
  const len = clamp(s.length ?? 30, 1, 64),
    w = clamp(s.width ?? 1, 1, 2),
    h = clamp(s.height ?? 2, 2, 3),
    sp = clamp(s.spacing ?? 3, 1, 8),
    bl = clamp(s.branchLength ?? 12, 0, 64);
  const alongX = (s.axis ?? 'x') !== 'z';
  const P = (a: number, across: number): [number, number] => (alongX ? [s.x + a, s.z + across] : [s.x + across, s.z + a]);
  const cells: MineCell[] = [];
  const dig = (a: number, across: number, y: number) => {
    const [x, z] = P(a, across);
    cells.push([x, y, z, 0, 0]);
  };
  for (let a = 0; a < len; a++) for (let across = 0; across < w; across++) for (let dy = 0; dy < h; dy++) dig(a, across, s.y + dy);
  const sides = (s.sides ?? 'both').toLowerCase();
  const branches = new Set<number>();
  for (let a = sp; a < len; a += sp + 1) {
    branches.add(a);
    for (let b = 1; b <= bl; b++)
      for (let dy = 0; dy < 2; dy++) {
        if (sides === 'both' || sides === 'left') dig(a, -b, s.y + dy);
        if (sides === 'both' || sides === 'right') dig(a, w - 1 + b, s.y + dy);
      }
  }
  const every = s.torchEvery ?? 0;
  if (every > 0)
    for (let a = every - 1; a < len; a += every) {
      const at = branches.has(a) ? a + 1 : a;
      if (at >= len || branches.has(at)) continue;
      const [x, z] = P(at, 0);
      cells.push([x, s.y + 1, z, TORCH, alongX ? torchOn(0, -1) : torchOn(-1, 0)]);
    }
  const [ex, ez] = P(0, 0);
  const path: P3[] = Array.from({ length: len }, (_, a) => {
    const [x, z] = P(a, 0);
    return [x, s.y, z];
  });
  const label = `strip mine at y${s.y} along ${alongX ? 'x' : 'z'}: ${w}×${h} × ${len}, ${branches.size} branch rows ${sp} apart (${bl} long, ${sides})`;
  return { cells, entry: [ex, s.y, ez], path, label };
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

/** One template step (a preset's): `{template: "spiral"|"strip", …}`; a spiral's `top: "surface"` reads the terrain. */
export function fromStep(step: Step, terrain: MineTerrain | null): MineCells {
  const t = (str(step, 'template') ?? 'spiral').toLowerCase();
  if (t === 'spiral' || t === 'spiral_shaft' || t === 'shaft') {
    const x = num(step, 'x') ?? 30,
      z = num(step, 'z') ?? 30,
      size = num(step, 'size') ?? 4;
    const top = num(step, 'top') ?? terrain?.surfaceOver(x, z, x + size - 1, z + size - 1) ?? 64;
    const dir = (str(step, 'dir') ?? 'cw').toLowerCase();
    const landings = Array.isArray(step.landings) ? (step.landings as unknown[]).filter((v): v is number => typeof v === 'number') : undefined;
    return spiral({
      x,
      z,
      top,
      bottom: num(step, 'bottom') ?? 11,
      size,
      core: num(step, 'core') ?? 2,
      cw: !(dir === 'ccw' || dir === 'counterclockwise'),
      torchEvery: num(step, 'torchEvery', 'torch_every') ?? 0,
      ...(landings ? { landings } : {}),
    });
  }
  if (t === 'strip' || t === 'strip_mine') {
    const axis = (str(step, 'axis') ?? 'x').trim().toLowerCase();
    return strip({
      x: num(step, 'x') ?? 0,
      y: num(step, 'y') ?? 11,
      z: num(step, 'z') ?? 0,
      axis: axis.startsWith('z') ? 'z' : 'x',
      length: num(step, 'length') ?? 30,
      width: num(step, 'width') ?? 1,
      height: num(step, 'height') ?? 2,
      spacing: num(step, 'spacing') ?? 3,
      branchLength: num(step, 'branchLength', 'branch_length') ?? 12,
      sides: (str(step, 'sides') ?? 'both') as StripSpec['sides'],
      torchEvery: num(step, 'torchEvery', 'torch_every') ?? 0,
    });
  }
  throw new Error(`no mine template '${t}' (spiral, strip)`);
}

/** FNV-1a over the cells (x, y, z, id, meta in order): the golden value both languages' tests check. */
export function cellsHash(cells: readonly MineCell[]): number {
  let h = 2166136261;
  for (const c of cells) for (const v of c) h = Math.imul(h ^ v, 16777619) >>> 0;
  return h;
}
