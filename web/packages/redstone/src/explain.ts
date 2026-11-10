/**
 * `Simulation.explain`: the chain that powers a cell, walked back from the cell to its source one link at a time. Each
 * link is read from the world as it is now, by the same power rules the parts use, so the chain tells what holds the
 * power at this tick. A part that keeps its output after its input has gone (a repeater waiting for its turn-off, a
 * locked repeater) ends the chain with that reason.
 */
import type { ExplainStep } from './api.js';
import { comparator } from './components/comparator.js';
import type { ComponentHandler } from './components/handler.js';
import { repeater } from './components/repeater.js';
import { torch } from './components/torch.js';
import type { Engine } from './engine.js';
import { INERT } from './grid.js';
import { DOWN, HORIZONTAL, UP, diodeRear, opposite, torchSupport, type Dir } from './orientation.js';
import { isSolid, powerFrom, strongFrom } from './power.js';
import { isWire } from './wire.js';

/** A chain longer than this is cut: a dust line of 15 and a few parts fit many times over. */
const MAX_LINKS = 64;

/** Where a neighbour is (`to the west`), and where something comes from (`from the west`), by direction. */
const TO = ['below', 'above', 'to the north', 'to the south', 'to the west', 'to the east'];
const FROM = ['from below', 'from above', 'from the north', 'from the south', 'from the west', 'from the east'];
const to = (d: Dir): string => TO[d]!;
const from = (d: Dir): string => FROM[d]!;

interface Link {
  why: string;
  next: number | null;
}

export function explain(engine: Engine, start: number): ExplainStep[] {
  const out: ExplainStep[] = [];
  if (start < 0 || engine.powerAt(start) === 0) return out;
  const seen = new Set<number>();
  let at: number | null = start;
  while (at !== null && out.length < MAX_LINKS && !seen.has(at)) {
    seen.add(at);
    const link = linkOf(engine, at);
    if (!link) break;
    out.push({ at: engine.pos(at), why: link.why });
    at = link.next;
  }
  return out;
}

/** What gives the cell its power, and the cell to look at next; null when the cell holds none and is no source. */
function linkOf(engine: Engine, at: number): Link | null {
  const state = engine.cells[at]!;
  const power = engine.powerAt(at);
  if (state < 0 || (state > 0 && state < INERT && !engine.handlers[state >> 4] && isSolid(engine, at)))
    return power > 0 ? solidLink(engine, at, state < 0 ? 'Ground' : 'Block', power) : null;
  if (state <= 0 || state >= INERT) return null;
  const h = engine.handlers[state >> 4];
  if (!h) return null;
  if (h.fill) {
    const fill = h.fill(engine, at, state);
    if (power === 0) return fill > 0 ? { why: `${h.name}, filled to level ${fill}: the source`, next: null } : null;
  }
  if (power === 0) return null;
  if (isWire(state)) return dustLink(engine, at, state & 15);
  if (h === torch) return { why: torchWhy(state), next: null };
  if (h === repeater) return repeaterLink(engine, at, state);
  if (h === comparator) return comparatorLink(engine, at, state, power);
  if (isSource(engine, h, state)) return { why: `${h.name}, giving ${power}: the source`, next: null };
  return strongestLink(engine, at, `${h.name}, powered to ${power}`);
}

/**
 * Parts that give power which comes from the user or from a change they saw, not from a neighbour's power. A hopper
 * takes a level too, but gives no power: its power is what reaches it.
 */
function isSource(engine: Engine, h: ComponentHandler, state: number): boolean {
  if (!h.weak) return false;
  return (
    (h.inputKind?.(state, engine.version) ?? null) !== null || h.name === 'Redstone block' || h.name === 'Observer'
  );
}

function torchWhy(state: number): string {
  const where = torchSupport(state & 15) === DOWN ? 'stands on' : 'hangs on';
  return `Redstone torch, lit: the block it ${where} is not powered`;
}

/** A full solid block: the strongest strong power a neighbour gives it. */
function solidLink(engine: Engine, at: number, name: string, power: number): Link {
  let best = -1;
  let most = 0;
  for (let d = 0; d < 6; d++) {
    const p = strongFrom(engine, at + engine.step[d]!, opposite(d));
    if (p > most) {
      most = p;
      best = d;
    }
  }
  if (best < 0) return { why: `${name}, powered to ${power}`, next: null };
  return { why: `${name}, strongly powered to ${power} ${from(best)}`, next: at + engine.step[best]! };
}

/** Dust: a source beside it that gives its level, else the dust a level higher that it reads. */
function dustLink(engine: Engine, at: number, level: number): Link {
  const step = engine.step;
  const hidden = engine.dustHidden;
  engine.dustHidden = true;
  try {
    for (let d = 0; d < 6; d++) {
      const n = at + step[d]!;
      if (isWire(engine.cells[n]!)) continue;
      const p = powerFrom(engine, n, opposite(d));
      if (p > 0 && p >= level) return { why: `Redstone dust at ${level}, fed ${from(d)}`, next: n };
    }
  } finally {
    engine.dustHidden = hidden;
  }
  for (const d of HORIZONTAL)
    for (const [dy, how] of [
      [0, ''],
      [UP, ', one up'],
      [DOWN, ', one down'],
    ] as const) {
      const n = at + step[d]! + (dy === 0 ? 0 : step[dy]!);
      const s = engine.cells[n]!;
      if (isWire(s) && (s & 15) === level + 1)
        return { why: `Redstone dust at ${level}, from the dust ${to(d)}${how}`, next: n };
    }
  return { why: `Redstone dust at ${level}`, next: null };
}

/** The level a repeater or comparator reads from behind it: a part's power, a powered block, or bare dust. */
function rearPower(engine: Engine, at: number, rear: Dir): number {
  const from = at + engine.step[rear]!;
  const behind = engine.cells[from]!;
  const p = powerFrom(engine, from, opposite(rear));
  return isWire(behind) ? Math.max(p, behind & 15) : p;
}

function repeaterLink(engine: Engine, at: number, state: number): Link {
  const rear = diodeRear(state & 15);
  if (engine.lockedAt(at))
    return { why: 'Repeater, on and locked: a powered repeater or comparator points into its side', next: null };
  if (rearPower(engine, at, rear) === 0)
    return { why: 'Repeater, on: its input has gone; it turns off when its scheduled tick runs', next: null };
  return { why: `Repeater, on: reads ${from(rear)}`, next: at + engine.step[rear]! };
}

function comparatorLink(engine: Engine, at: number, state: number, power: number): Link {
  const rear = diodeRear(state & 15);
  const mode = (state & 4) !== 0 ? 'subtract' : 'compare';
  const behind = at + engine.step[rear]!;
  const what = `Comparator (${mode}) putting out ${power}`;
  if (holdsContainer(engine, behind)) return { why: `${what}: reads the container ${to(rear)}`, next: behind };
  const further = behind + engine.step[rear]!;
  if (isSolid(engine, behind) && holdsContainer(engine, further))
    return { why: `${what}: reads the container two cells ${to(rear)}`, next: further };
  if (rearPower(engine, at, rear) === 0)
    return { why: `${what}: its input has gone; it changes when its scheduled tick runs`, next: null };
  return { why: `${what}: reads ${from(rear)}`, next: behind };
}

function holdsContainer(engine: Engine, at: number): boolean {
  const state = engine.cells[at]!;
  return state > 0 && state < INERT && engine.handlers[state >> 4]?.fill !== undefined;
}

/** Any other part: the neighbour that gives it the most power. */
function strongestLink(engine: Engine, at: number, why: string): Link {
  let best = -1;
  let most = 0;
  for (let d = 0; d < 6; d++) {
    const p = powerFrom(engine, at + engine.step[d]!, opposite(d));
    if (p > most) {
      most = p;
      best = d;
    }
  }
  return best < 0 ? { why, next: null } : { why: `${why} ${from(best)}`, next: at + engine.step[best]! };
}
