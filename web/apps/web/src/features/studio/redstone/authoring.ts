/**
 * A redstone design's own metadata, saved with each revision as one JSON text (`redstoneAuthoring`): the placed library
 * components, the user's component definitions, and what the debugger keeps (probes, breakpoints, the start levels of
 * containers and analog sources, the simulator's speed and redraw rate). The circuit itself is the design's cells.
 *
 * ```json
 * { "redstone": 1, "components": [], "customDefs": [], "probes": [], "breakpoints": [], "levels": [],
 *   "sim": { "speed": 10, "displayHz": 10 }, "origin": [30, 0, 30] }
 * ```
 *
 * Every cell it names is a canvas cell; `origin` (the min corner of the canvas's blocks, written while it names any cell)
 * lets a reopened design, which the bot centres on the plot again, move them to where the blocks are now.
 *
 * The text goes through `Studio.setRedstoneAuthoring`, so a change of a probe alone is unsaved work. Keys this code does
 * not know (a newer studio's, the components' own) are kept as they were, so no part of the studio drops another's data.
 */
import type { Breakpoint, BreakOn, Pos } from '@mineclaw/redstone';
import { posOf, type Canvas } from '../canvas';
import type { Studio } from '../studio';
import {
  DEFAULT_DISPLAY_HZ,
  DEFAULT_SIM_SPEED,
  DISPLAY_RATES,
  type DisplayHz,
  type Probe,
  type SimControllerApi,
  type SimSnapshot,
  type SimSpeed,
  type StartLevel,
} from './simController.types';

export const AUTHORING_VERSION = 1;

/** The most of each list a design keeps; more is dropped when it is read. */
export const MAX_PROBES = 256;
export const MAX_BREAKPOINTS = 256;
export const MAX_LEVELS = 4096;
const MAX_LABEL = 40;
const MAX_ID = 64;

/** The saved envelope. Anything else in it belongs to someone else and is carried along untouched. */
export interface RedstoneAuthoring {
  redstone: typeof AUTHORING_VERSION;
  probes: Probe[];
  breakpoints: Breakpoint[];
  levels: StartLevel[];
  sim: { speed: SimSpeed; displayHz: DisplayHz };
  [other: string]: unknown;
}

/** The debugger's part of the envelope. */
export interface DebuggerAuthoring {
  probes: readonly Probe[];
  breakpoints: readonly Breakpoint[];
  levels: readonly StartLevel[];
  speed: SimSpeed;
  displayHz: DisplayHz;
}

const BREAK_ONS: readonly BreakOn[] = ['change', 'rising', 'falling', 'atLeast', 'state'];

type Json = Record<string, unknown>;
const isObject = (v: unknown): v is Json => typeof v === 'object' && v !== null && !Array.isArray(v);
const isId = (v: unknown): v is string => typeof v === 'string' && v.length > 0 && v.length <= MAX_ID;
const posKey = (p: Pos) => `${p[0]},${p[1]},${p[2]}`;

function readPos(v: unknown): Pos | null {
  if (!Array.isArray(v) || v.length !== 3 || !v.every((n) => Number.isSafeInteger(n))) return null;
  return [v[0] as number, v[1] as number, v[2] as number];
}

function readProbe(v: unknown): Probe | null {
  if (!isObject(v) || !isId(v.id)) return null;
  const at = readPos(v.at);
  const label = typeof v.label === 'string' ? v.label.trim().slice(0, MAX_LABEL) : '';
  const color = v.color;
  if (!at || !label || !Number.isInteger(color) || (color as number) < 0 || (color as number) > 0xffffff) return null;
  return { id: v.id, at, label, color: color as number };
}

function readBreakpoint(v: unknown): Breakpoint | null {
  if (!isObject(v) || !isId(v.id) || typeof v.enabled !== 'boolean') return null;
  const at = readPos(v.at);
  const on = BREAK_ONS.find((o) => o === v.on);
  if (!at || !on) return null;
  const b: Breakpoint = { id: v.id, at, on, enabled: v.enabled };
  if (Number.isSafeInteger(v.value)) b.value = v.value as number;
  return b;
}

function readLevel(v: unknown): StartLevel | null {
  if (!isObject(v)) return null;
  const at = readPos(v.at);
  if (!at || typeof v.level !== 'number' || !Number.isFinite(v.level)) return null;
  const level = Math.max(0, Math.min(15, Math.round(v.level)));
  return level > 0 ? { at, level } : null;
}

/** The valid entries of a list, the first of each id (or cell) only, at most `max`. */
function readList<T>(raw: unknown, read: (v: unknown) => T | null, keyOf: (t: T) => string, max: number): T[] {
  if (!Array.isArray(raw)) return [];
  const out: T[] = [];
  const seen = new Set<string>();
  for (const v of raw) {
    if (out.length >= max) break;
    const t = read(v);
    if (!t || seen.has(keyOf(t))) continue;
    seen.add(keyOf(t));
    out.push(t);
  }
  return out;
}

function readSpeed(v: unknown): SimSpeed {
  if (v === 'max') return 'max';
  return typeof v === 'number' && Number.isFinite(v) && v > 0 ? v : DEFAULT_SIM_SPEED;
}

function readDisplayHz(v: unknown): DisplayHz {
  return DISPLAY_RATES.find((hz) => hz === v) ?? DEFAULT_DISPLAY_HZ;
}

/**
 * The envelope a saved text holds, its debugger part checked entry by entry (what does not fit is dropped). Text that is
 * missing, not JSON or not marked `"redstone": 1` gives an empty envelope.
 */
export function parseAuthoring(text: string | null | undefined): RedstoneAuthoring {
  let raw: unknown = null;
  if (text) {
    try {
      raw = JSON.parse(text);
    } catch {
      raw = null;
    }
  }
  const json: Json = isObject(raw) && raw.redstone === AUTHORING_VERSION ? raw : {};
  const sim = isObject(json.sim) ? json.sim : {};
  const part: DebuggerAuthoring = {
    probes: readList(json.probes, readProbe, (p) => p.id, MAX_PROBES),
    breakpoints: readList(json.breakpoints, readBreakpoint, (b) => b.id, MAX_BREAKPOINTS),
    levels: readList(json.levels, readLevel, (l) => posKey(l.at), MAX_LEVELS),
    speed: readSpeed(sim.speed),
    displayHz: readDisplayHz(sim.displayHz),
  };
  // In the form `withDebugger` writes, so reading a text and writing the same state back gives the same text.
  const base = { ...json, redstone: AUTHORING_VERSION, sim } as unknown as RedstoneAuthoring;
  return withDebugger(base, part);
}

/** The debugger's part of an envelope. */
export function debuggerPart(a: RedstoneAuthoring): DebuggerAuthoring {
  return {
    probes: a.probes,
    breakpoints: a.breakpoints,
    levels: a.levels,
    speed: a.sim.speed,
    displayHz: a.sim.displayHz,
  };
}

/**
 * The envelope with its debugger part replaced; every other key stays where and as it was. Entries are rebuilt with a
 * fixed key order, so the same state always gives the same text.
 */
export function withDebugger(a: RedstoneAuthoring, d: DebuggerAuthoring): RedstoneAuthoring {
  const pos = (p: Pos): Pos => [p[0], p[1], p[2]];
  return {
    ...a,
    redstone: AUTHORING_VERSION,
    probes: d.probes.map((p) => ({ id: p.id, at: pos(p.at), label: p.label, color: p.color })),
    breakpoints: d.breakpoints.map((b) => ({
      id: b.id,
      at: pos(b.at),
      on: b.on,
      ...(b.value !== undefined ? { value: b.value } : {}),
      enabled: b.enabled,
    })),
    levels: d.levels.map((l) => ({ at: pos(l.at), level: l.level })),
    sim: { ...a.sim, speed: d.speed, displayHz: d.displayHz },
  };
}

export const serializeAuthoring = (a: RedstoneAuthoring): string => JSON.stringify(a);

/** The min corner of the canvas's blocks, or null when it has none. */
export function blocksMin(canvas: Pick<Canvas, 'blocks'>): Pos | null {
  let min: [number, number, number] | null = null;
  for (const k of canvas.blocks.keys()) {
    const p = posOf(k);
    if (!min) min = [p[0], p[1], p[2]];
    else for (let i = 0; i < 3; i++) if (p[i]! < min[i]!) min[i] = p[i]!;
  }
  return min;
}

/** Whether the envelope names any cell of the canvas (a probe, a breakpoint, a start level or a placed component). */
const hasCells = (a: RedstoneAuthoring): boolean =>
  a.probes.length > 0 ||
  a.breakpoints.length > 0 ||
  a.levels.length > 0 ||
  (Array.isArray(a.components) && a.components.length > 0);

/** Every cell the envelope names moved by `d`; what it does not know stays as it was. */
export function shiftAuthoring(a: RedstoneAuthoring, d: Pos): RedstoneAuthoring {
  const move = (p: Pos): Pos => [p[0] + d[0], p[1] + d[1], p[2] + d[2]];
  const moved: RedstoneAuthoring = {
    ...a,
    probes: a.probes.map((p) => ({ ...p, at: move(p.at) })),
    breakpoints: a.breakpoints.map((b) => ({ ...b, at: move(b.at) })),
    levels: a.levels.map((l) => ({ ...l, at: move(l.at) })),
  };
  if (Array.isArray(a.components))
    moved.components = a.components.map((c: unknown) => {
      const at = isObject(c) ? readPos(c.at) : null;
      return at && isObject(c) ? { ...c, at: move(at) } : c;
    });
  return moved;
}

type AuthoringStudio = Pick<Studio, 'session' | 'setRedstoneAuthoring'> & Partial<Pick<Studio, 'canvas' | 'onCells'>>;

/**
 * The one copy of a studio's redstone authoring: every part of the studio that keeps something in it (the debugger here,
 * the components later) reads and writes through this, so none overwrites another's keys.
 */
export class RedstoneAuthoringStore {
  private envelope: RedstoneAuthoring;
  private text: string;

  constructor(private readonly studio: AuthoringStudio) {
    // The opened revision's text, in the form this code writes it: opening a design is never an unsaved change.
    this.envelope = this.rebased(parseAuthoring(studio.session.redstoneAuthoring));
    this.text = serializeAuthoring(this.envelope);
    studio.setRedstoneAuthoring(this.text, true);
    // The origin follows the blocks' corner, so it always names the canvas the cells were saved on.
    studio.onCells?.(() => {
      if (hasCells(this.envelope)) this.replace(this.envelope);
    });
  }

  /**
   * The cells the envelope names are canvas cells, and a saved design reopens centred on the plot (every block moved
   * alike). `origin` is the blocks' min corner on the canvas the envelope was written for: on open, every cell moves by
   * how far that corner moved.
   */
  private rebased(a: RedstoneAuthoring): RedstoneAuthoring {
    const now = this.studio.canvas ? blocksMin(this.studio.canvas) : null;
    const was = readPos(a.origin);
    if (!now) return a;
    if (was && (was[0] !== now[0] || was[1] !== now[1] || was[2] !== now[2]))
      return { ...shiftAuthoring(a, [now[0] - was[0], now[1] - was[1], now[2] - was[2]]), origin: now };
    return this.stamped(a);
  }

  /** The envelope with the canvas's current origin when it names a cell, and without one when it names none. */
  private stamped(a: RedstoneAuthoring): RedstoneAuthoring {
    if (!this.studio.canvas) return a;
    const { origin: _old, ...rest } = a;
    const now = hasCells(a) ? blocksMin(this.studio.canvas) : null;
    if (!now) return rest;
    return { ...a, origin: now };
  }

  get current(): RedstoneAuthoring {
    return this.envelope;
  }

  get debugger(): DebuggerAuthoring {
    return debuggerPart(this.envelope);
  }

  setDebugger(d: DebuggerAuthoring): void {
    this.replace(withDebugger(this.envelope, d));
  }

  /** Sets other keys of the envelope (the debugger's own go through `setDebugger`). */
  update(patch: Readonly<Record<string, unknown>>): void {
    this.replace({ ...this.envelope, ...patch, redstone: AUTHORING_VERSION });
  }

  private replace(given: RedstoneAuthoring): void {
    const next = this.stamped(given);
    const text = serializeAuthoring(next);
    this.envelope = next;
    if (text === this.text) return;
    this.text = text;
    this.studio.setRedstoneAuthoring(text);
  }
}

const stores = new WeakMap<AuthoringStudio, RedstoneAuthoringStore>();

/** The studio's authoring store, made the first time it is asked for. */
export function authoringStoreOf(studio: AuthoringStudio): RedstoneAuthoringStore {
  let store = stores.get(studio);
  if (!store) {
    store = new RedstoneAuthoringStore(studio);
    stores.set(studio, store);
  }
  return store;
}

/** A controller the saved state can be put back into. */
export interface RestorableSim extends SimControllerApi {
  restore(state: DebuggerAuthoring): void;
}

const sameSaved = (a: SimSnapshot, b: SimSnapshot) =>
  a.probes === b.probes &&
  a.breakpoints === b.breakpoints &&
  a.levels === b.levels &&
  a.speed === b.speed &&
  a.displayHz === b.displayHz;

/**
 * Puts the saved debugger state into the controller, then writes every change of it back to the store. Returns the
 * function that stops following the controller.
 */
export function bindRedstoneAuthoring(store: RedstoneAuthoringStore, sim: RestorableSim): () => void {
  sim.restore(store.debugger);
  let last = sim.snapshot();
  return sim.subscribe(() => {
    const snap = sim.snapshot();
    if (sameSaved(last, snap)) return;
    last = snap;
    store.setDebugger({
      probes: snap.probes,
      breakpoints: snap.breakpoints,
      levels: snap.levels,
      speed: snap.speed,
      displayHz: snap.displayHz,
    });
  });
}
