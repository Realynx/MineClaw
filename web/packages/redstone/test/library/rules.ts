/**
 * The static layout rules of report 4.5 (L1, L3, L5 and L6); L2, L4 and L7 need the engine and are checked through
 * `verifyComponent` and a bare simulation. Each returns what breaks the rule, empty when the layout keeps it.
 */
import type { Cell, Pos, RedstoneVersion, Side } from '../../src/api.js';
import { harnessCells } from '../../src/library/verify.js';
import type { ComponentLayout, Feeder } from '../../src/library/types.js';
import {
  DOWN,
  EAST,
  NORTH,
  SOUTH,
  UP,
  WEST,
  diodeRear,
  opposite,
  torchSupport,
  type Dir,
} from '../../src/orientation.js';

const STONE = 1;
const DUST = 55;
const TORCH = 76;
const REPEATER = 93;
const COMPARATOR = 149;

/** The blocks the gates of WP4 are drawn from: they exist in both versions. */
export const WP4_PARTS = new Set([STONE, DUST, TORCH, REPEATER]);

/** What a layout may use in each version: 1.12.2 adds the comparator (WP5). */
export const PARTS: Readonly<Record<RedstoneVersion, ReadonlySet<number>>> = {
  'b1.8.1': WP4_PARTS,
  '1.12.2': new Set([...WP4_PARTS, COMPARATOR]),
};

/** The studio saves a design of at most this many cells a side (owner decision 1). */
export const MAX_SIDE = 32;

const STEP: Readonly<Record<Dir, Pos>> = {
  [DOWN]: [0, -1, 0],
  [UP]: [0, 1, 0],
  [NORTH]: [0, 0, -1],
  [SOUTH]: [0, 0, 1],
  [WEST]: [-1, 0, 0],
  [EAST]: [1, 0, 0],
};
const add = (p: Pos, d: Dir): Pos => {
  const s = STEP[d]!;
  return [p[0] + s[0], p[1] + s[1], p[2] + s[2]];
};
const key = (p: Pos | Cell): string => `${p[0]},${p[1]},${p[2]}`;
const show = (p: Pos | Cell): string => `(${p[0]},${p[1]},${p[2]})`;

function inside(l: ComponentLayout, p: Pos | Cell): boolean {
  return p[0] >= 0 && p[1] >= 0 && p[2] >= 0 && p[0] < l.size[0] && p[1] < l.size[1] && p[2] < l.size[2];
}

/** The cell just outside the box on `side` is where a port on that side sits. */
function onSide(l: ComponentLayout, p: Pos, side: Side): boolean {
  const [sx, sy, sz] = l.size;
  const y = p[1] >= 0 && p[1] < sy;
  const along = (v: number, n: number): boolean => v >= 0 && v < n;
  switch (side) {
    case 'west':
      return y && p[0] === -1 && along(p[2], sz);
    case 'east':
      return y && p[0] === sx && along(p[2], sz);
    case 'north':
      return y && p[2] === -1 && along(p[0], sx);
    case 'south':
      return y && p[2] === sz && along(p[0], sx);
  }
}

/** L1: every cell inside the box; every port cell just outside it on its side, at a y inside it. */
export function l1(l: ComponentLayout): string[] {
  const out: string[] = [];
  for (const c of l.cells) if (!inside(l, c)) out.push(`cell ${show(c)} is outside the ${l.size.join('x')} box`);
  for (const p of l.ports)
    if (!onSide(l, p.at, p.side)) out.push(`port ${p.name} at ${show(p.at)} is not just outside the ${p.side} face`);
  return out;
}

/** With its port cells the layout fits the studio's limit. */
export function fits(l: ComponentLayout): string[] {
  const out: string[] = [];
  for (const axis of [0, 1, 2]) {
    let lo = 0;
    let hi = l.size[axis]! - 1;
    for (const p of l.ports) {
      lo = Math.min(lo, p.at[axis]!);
      hi = Math.max(hi, p.at[axis]!);
    }
    if (hi - lo + 1 > MAX_SIDE) out.push(`${'xyz'[axis]} spans ${hi - lo + 1} cells with its ports`);
  }
  return out;
}

/**
 * L3: ports of different signals are at least two cells apart, and with any feeder the cells the harness lays for one
 * port never touch those of another (a spur beside one port may not reach the next one's wire).
 */
export function l3(l: ComponentLayout, feeders: readonly Feeder[]): string[] {
  const out: string[] = [];
  const ports = l.ports;
  for (let i = 0; i < ports.length; i++)
    for (let j = i + 1; j < ports.length; j++) {
      const a = ports[i]!;
      const b = ports[j]!;
      const apart = Math.max(...[0, 1, 2].map((k) => Math.abs(a.at[k]! - b.at[k]!)));
      if (apart < 2) out.push(`ports ${a.name} and ${b.name} touch`);
      for (const f of feeders) {
        const near = harnessCells(a, f).some((ca) =>
          harnessCells(b, f).some(
            (cb) => Math.abs(ca[0] - cb[0]) + Math.abs(ca[2] - cb[2]) <= 1 && Math.abs(ca[1] - cb[1]) <= 1,
          ),
        );
        if (near) out.push(`with the ${f} feeder the wires of ${a.name} and ${b.name} touch`);
      }
    }
  return out;
}

/** L5: only the parts this library may use. */
export function l5(l: ComponentLayout, parts: ReadonlySet<number>): string[] {
  return l.cells
    .filter((c) => !parts.has(c[3]))
    .map((c) => `block ${c[3]} at ${show(c)} is not one of the allowed parts`);
}

/**
 * The cells a torch, a repeater or a comparator powers, together with the blocks it powers strongly (whose neighbours
 * then read it): a torch powers every side but its block and strongly powers the block above; a repeater or a
 * comparator powers and strongly powers the cell in front.
 */
function reach(c: Cell, solid: (p: Pos) => boolean): Pos[] {
  const at: Pos = [c[0], c[1], c[2]];
  const out: Pos[] = [];
  const through = (p: Pos): void => {
    out.push(p);
    if (solid(p)) for (const d of [DOWN, UP, NORTH, SOUTH, WEST, EAST]) out.push(add(p, d));
  };
  if (c[3] === TORCH) {
    const support = torchSupport(c[4]);
    for (const d of [NORTH, SOUTH, WEST, EAST]) if (d !== support) out.push(add(at, d));
    through(add(at, UP));
  } else if (c[3] === REPEATER || c[3] === COMPARATOR) through(add(at, opposite(diodeRear(c[4]))));
  return out;
}

/**
 * L6: no torch, repeater or comparator output touches a side face of the box except at an output port, so two components placed
 * with a one-cell gap cannot cross-talk. The ground below and the open sky above are not faces another component sits
 * against.
 */
export function l6(l: ComponentLayout): string[] {
  const solid = new Set(l.cells.filter((c) => c[3] === STONE).map(key));
  const outputs = new Set(l.ports.filter((p) => p.kind === 'out').map((p) => key(p.at)));
  const out: string[] = [];
  for (const c of l.cells)
    for (const p of reach(c, (q) => solid.has(key(q))))
      if (!inside(l, p) && p[1] >= 0 && p[1] < l.size[1] && !outputs.has(key(p)))
        out.push(`the ${c[3] === TORCH ? 'torch' : 'diode'} at ${show(c)} reaches ${show(p)} outside the box`);
  return out;
}
