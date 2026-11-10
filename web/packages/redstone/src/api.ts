/**
 * The one contract between the redstone engine, the gate library and the Design Studio. Everything here is plain data, so
 * the same calls work on the page, in a worker and in tests.
 *
 * Time: one game tick is 1/20 s and one redstone tick is two game ticks. Every delay and every tick number is in game
 * ticks. The engine is exact at game-tick resolution; circuits that depend on the order of updates inside one game tick
 * are reported in `diagnostics()`, not emulated.
 *
 * Order: the lists of cells the engine returns (`changed`, `displayCells`, `lockedCells`) are sorted by y, then z, then x,
 * so the same run always reports the same lists.
 */

export type RedstoneVersion = 'b1.8.1' | '1.12.2';

/** `[x, y, z, id, meta]`: the studio's own cell shape, in canvas coordinates (+x east, +z south, y may be negative). */
export type Cell = readonly [x: number, y: number, z: number, id: number, meta: number];
export type Pos = readonly [x: number, y: number, z: number];
export type Side = 'north' | 'east' | 'south' | 'west';

/**
 * The id undisturbed ground reads as in `stateAt` and in samples (the studio's earth id), with meta 0. The ground has no
 * block of its own, but it conducts, so a reader has to be able to tell it from air.
 */
export const GROUND_ID = -1;

export interface CircuitInput {
  version: RedstoneVersion;
  /**
   * The design as placed. Bits the game sets at run time (a dust level, a pressed plate or button, a comparator's powered
   * bit) are cleared on load; the states a builder chooses (a lever switched on, a repeater's delay, a comparator's mode)
   * are kept.
   */
  cells: readonly Cell[];
  /**
   * The plot's ground: every cell with y <= `topY` that is neither listed in `cells` nor in `dug` is a solid, immovable
   * conductor. Without it nothing stands under the design.
   */
  ground?: { topY: number; dug?: readonly Pos[] };
  /** State that is not in a cell's id and meta. */
  extras?: readonly CellExtra[];
}

/** `level`: what a container or an analog source (weighted plate, sensor) holds at the start, 0..15. */
export interface CellExtra {
  at: Pos;
  level?: number;
}

export type InputKind = 'lever' | 'button' | 'plate' | 'level';

/** What the user does to the circuit. An event for a cell that is not that kind of input does nothing. */
export type InputEvent =
  /** A lever. */
  | { kind: 'toggle'; at: Pos }
  /** A button; no effect while it is pressed. */
  | { kind: 'press'; at: Pos }
  /** A pressure plate or a detector rail, held down or let go. */
  | { kind: 'hold'; at: Pos; on: boolean }
  /** A container, a weighted plate or a sensor set to a level 0..15. */
  | { kind: 'level'; at: Pos; level: number };

/**
 * When a breakpoint hits. It is evaluated once per game tick, comparing the cell's value at the end of the previous game
 * tick with its value at the end of this one, so a cell that changes and changes back inside one game tick does not hit.
 * - `change`: the shown state or the power differs.
 * - `rising`: the power goes from 0 to above 0.
 * - `falling`: the power goes from above 0 to 0.
 * - `atLeast`: the power goes from below `value` to `value` or more.
 * - `state`: the shown state becomes `value`.
 */
export type BreakOn = 'change' | 'rising' | 'falling' | 'atLeast' | 'state';

export interface Breakpoint {
  id: string;
  at: Pos;
  on: BreakOn;
  /** `atLeast`: a power level 1..15. `state`: a shown state (`id << 4 | meta`). Without it those two never hit. */
  value?: number;
  enabled: boolean;
}

/**
 * `tick` is the game tick that just ended. `before` and `after` hold the power for `rising`, `falling` and `atLeast`, and
 * the shown state for `change` and `state`.
 */
export interface BreakpointHit {
  id: string;
  tick: number;
  at: Pos;
  before: number;
  after: number;
}

/**
 * Something that happened and left no block state to show: a torch burned out, TNT was primed (it is removed, nothing
 * explodes), a dispenser or dropper fired, a note block played, a piston destroyed a part, a piston could not move.
 */
export type SimEventKind = 'burnout' | 'primed' | 'fired' | 'played' | 'destroyed' | 'blocked';

export interface SimEvent {
  kind: SimEventKind;
  at: Pos;
  tick: number;
  note?: string;
}

/**
 * What the engine cannot simulate faithfully, or what looks like a mistake: a part with nothing to stand on, a block it
 * does not model, a part the version does not have, a slime block in a piston's path (moved as a plain block), a tick
 * that never settled, a push that would leave the plot, and a spot whose behaviour depends on the order of updates inside
 * one game tick (it can come out differently in the world).
 */
export type DiagnosticCode =
  'floating' | 'unsupported' | 'notInVersion' | 'slime' | 'unstable' | 'outOfPlot' | 'updateOrder';

export interface Diagnostic {
  at: Pos;
  code: DiagnosticCode;
  text: string;
}

/**
 * A cell at the end of a game tick. `state` is the shown state, `id << 4 | meta` (read it back with `state >> 4` and
 * `state & 15`; air is 0, undisturbed ground is `GROUND_ID << 4`). `power` is 0..15 as defined on `Simulation.powerAt`.
 * `powered` is `power > 0`: the on/off value a logic trace draws.
 */
export interface Sample {
  tick: number;
  power: number;
  powered: boolean;
  state: number;
}

/** A scheduled tick waiting for a cell. `priority` is 1.12.2's (lower runs first); b1.8.1 has none, so it is 0 there. */
export interface PendingTick {
  dueTick: number;
  priority: number;
}

export interface ExplainStep {
  at: Pos;
  why: string;
}

export interface StepResult {
  /** The clock after the call. */
  tick: number;
  /** Game ticks the call stepped forward; 0 for `stepBack` and `reset`. */
  ranTicks: number;
  /** Every cell whose shown state after the call differs from before it, with its new state. */
  changed: readonly Cell[];
  /** Every event of the ticks this call ran; empty for `stepBack` and `reset`. */
  events: readonly SimEvent[];
  /** The breakpoints that hit on the last tick the call ran. */
  hits: readonly BreakpointHit[];
  /** `Simulation.idle` after the call. */
  idle: boolean;
}

export interface Simulation {
  readonly version: RedstoneVersion;
  /** The clock in game ticks; 0 is the start state (the design after it has settled). */
  readonly tick: number;
  /** Game ticks that can be stepped back from here. */
  readonly historyDepth: number;
  /** Nothing is scheduled, queued or moving: without an input nothing will change. */
  readonly idle: boolean;

  stepGameTick(): StepResult;
  /** Two game ticks, or one when a breakpoint hits on the first. The next call runs two again: nothing is realigned. */
  stepRedstoneTick(): StepResult;
  /**
   * Runs whole game ticks until a breakpoint hits or `maxGameTicks` have run. `'change'` also stops after the first tick
   * that changed a shown state; `'idle'` also stops once the circuit is idle, and returns at once when it already is.
   * A tick the engine had to cut short (`unstable` in `diagnostics()`) ends any stepping call.
   */
  run(maxGameTicks: number, until: 'breakpoint' | 'change' | 'idle'): StepResult;
  /**
   * Rewinds by `gameTicks` (default 1, at most `historyDepth`). Never stops at breakpoints. Everything after the new tick
   * is dropped: samples, events, hits and inputs, queued ones included.
   */
  stepBack(gameTicks?: number): StepResult;
  /** Back to tick 0 as it was first built. Breakpoints and the watch list stay. */
  reset(): StepResult;

  /** Queues an input: it is applied at the start of the next game tick and is part of history from then on. */
  input(e: InputEvent): void;
  /** What a click on this cell would drive; null when it is not an input. */
  inputAt(at: Pos): InputKind | null;

  /** Replaces the breakpoints. */
  setBreakpoints(list: readonly Breakpoint[]): void;
  /**
   * Replaces the list of cells sampled at the end of every game tick. A watched cell always has a sample for the current
   * tick; a cell that leaves the list loses its samples.
   */
  watch(list: readonly Pos[]): void;
  /** A watched cell's samples from `fromTick` to `toTick` (both included), oldest first, as far as history reaches. */
  trace(at: Pos, fromTick: number, toTick: number): readonly Sample[];

  /** Every cell whose shown state differs from `cells` as they were given, with its shown state. */
  displayCells(): readonly Cell[];
  /** The shown state of a cell now (see `Sample.state`). */
  stateAt(at: Pos): number;
  /**
   * The power of a cell now, by what it holds:
   * - dust: its level;
   * - torch, lever, button, plate, repeater, observer, redstone block: 15 when on, else 0 (a weighted plate: its level);
   * - comparator: its output level;
   * - a solid conductor, the ground included: the strong power it receives, counting dust;
   * - lamp, piston, door, trapdoor, gate, dispenser, dropper, hopper, note block, TNT: the strongest power reaching it
   *   by the rule that block itself uses;
   * - anything else, and air: 0.
   */
  powerAt(at: Pos): number;
  /** A cell now; it does not have to be watched. */
  sample(at: Pos): Sample;
  /** Whether a lock holds the repeater at this cell now (1.12.2); false for anything else. */
  lockedAt(at: Pos): boolean;
  /** Every repeater a lock holds now. Always empty in b1.8.1, which has no locking. */
  lockedCells(): readonly Pos[];
  /** The scheduled ticks waiting for a cell, soonest first. */
  pendingAt(at: Pos): readonly PendingTick[];
  /** The chain that powers a cell, from the cell itself back to its source; empty when nothing powers it. */
  explain(at: Pos): readonly ExplainStep[];
  diagnostics(): readonly Diagnostic[];
}

export interface SimulationOptions {
  /** How many game ticks can be stepped back (default 2000). */
  historyTicks?: number;
  /**
   * How long the design may take to settle before tick 0 (default 200 game ticks). A circuit that never settles, such as
   * a clock, starts from wherever it is then.
   */
  settleTicks?: number;
}

export type CreateSimulation = (input: CircuitInput, opts?: SimulationOptions) => Simulation;

/**
 * A block's meta after `quarterTurns` clockwise quarter turns seen from above (north becomes east). The engine, the
 * library's layouts and the studio's own rotation must agree on this for every redstone part.
 */
export type RotateMeta = (id: number, meta: number, quarterTurns: number, v: RedstoneVersion) => number;

/** A block's meta mirrored across x (east and west swap) or across z (north and south swap). */
export type MirrorMeta = (id: number, meta: number, axis: 'x' | 'z', v: RedstoneVersion) => number;
