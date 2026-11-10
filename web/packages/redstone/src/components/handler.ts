/**
 * The engine's inside contract: what a kind of part (dust, a torch, a repeater, a piston...) is to the engine, and what
 * the engine is to a part. A part is a stateless handler; everything that changes lives in the world it is handed, so a
 * keyframe can copy it.
 *
 * Cells are addressed by their index in the grid (grid.ts): `at + w.step[dir]` is the neighbour on that side, and a
 * smaller index comes first in the y, z, x order. A cell's state is `id << 4 | meta`; the ground is `GROUND` (below
 * zero) and a part that is left out of the simulation has `INERT` set, so both fail every `id ===` test.
 *
 * Directions in the power calls are from the part that gives the power to the cell that reads it (`toward`). The games
 * number it the other way round (from the reader to the giver).
 */
import type { DiagnosticCode, InputEvent, InputKind, Pos, RedstoneVersion, SimEventKind } from '../api.js';
import type { Dir } from '../orientation.js';
import type { Profile } from '../profile.js';

/** State a keyframe copies: `save` returns a copy that later changes do not touch, `load` takes one back. */
export interface Store {
  save(): unknown;
  load(saved: unknown): void;
  /** Moves every game tick the store holds back by `by` (the clock is set to 0 once the design has settled). */
  rebase?(by: number): void;
}

/** The engine as a part sees it. */
export interface World {
  readonly version: RedstoneVersion;
  readonly profile: Profile;
  /** The clock in game ticks. While inputs are applied it still reads the tick before. */
  readonly tick: number;
  /** Every cell's state. Read freely; write through `set`. */
  readonly cells: Int16Array;
  /** The index step to the neighbour on each side, by direction. */
  readonly step: Int32Array;
  /** The handler of each block id in this version (undefined: a plain block). */
  readonly handlers: ReadonlyArray<ComponentHandler | undefined>;
  /** True while dust works out its own level: dust then gives no power, so it never feeds itself. */
  dustHidden: boolean;

  /** Writes a cell's state. It tells nobody: the part that changes says who hears of it (`notify...`). */
  set(at: number, state: number): void;
  /** Whether the cell holds a full solid block that passes power on (a "normal cube"); the ground is one. */
  solid(at: number): boolean;
  /** Whether a block may be put in the cell. The rim of the engine's box is read only: nothing may leave the plot. */
  writable(at: number): boolean;

  /**
   * A neighbour update for one cell, as if the block at `from` (id `sourceId`) had changed. A part that no longer has
   * anything to hold it drops off instead (a `destroyed` event), as in the game. For a block that was replaced, b1.8.1
   * names the new block and 1.12.2 the old one; the parts that care (doors, dispensers) read it as the game does.
   */
  notify(at: number, from: number, sourceId: number): void;
  /** A neighbour update for the six cells around `center`: west, east, down, up, north, south. */
  notifyAround(center: number, sourceId: number): void;
  /** `notifyAround` without the cell on side `skip` of `center`. */
  notifyAroundExcept(center: number, sourceId: number, skip: Dir): void;

  /**
   * Asks for a scheduled tick of the block now at `at`, `delay` game ticks from the clock. It is dropped when an equal
   * one already waits (the version's rule). `priority` is 1.12.2's: lower runs first on the same tick.
   */
  schedule(at: number, delay: number, priority?: number): void;
  /** 1.12.2: whether a tick of this cell is due on the tick being run and has not run yet. Always false in b1.8.1. */
  tickPendingNow(at: number): boolean;

  /** What the user holds a manual source at (a plate held down, a container's fill): 0..15, 0 when never set. */
  level(at: number): number;
  setLevel(at: number, level: number): void;

  /** Records something that happened and left no state to show. */
  event(kind: SimEventKind, at: number, note?: string): void;
  /** Raises a diagnostic while running; the same one is kept once. */
  diagnose(code: DiagnosticCode, at: number, text: string): void;

  /** A named store of this world, made on first use and copied with every keyframe. */
  state<T extends Store>(name: string, make: () => T): T;
  /** Adds work to a later phase of every game tick: 4 runs after the scheduled ticks, 5 after that. */
  onPhase(phase: 4 | 5, run: () => void): void;
  /** Adds a reason the circuit is not idle (something still moving). */
  onBusy(busy: () => boolean): void;

  pos(at: number): Pos;
}

/**
 * One kind of part. Every hook gets the cell's index and its state as it is at the call. A hook that is left out does
 * nothing (no power, no reaction).
 */
export interface ComponentHandler {
  /** What a diagnostic calls the part. */
  readonly name: string;
  /** The block ids this handler owns in a version, on and off twins included; empty when the version lacks the part. */
  readonly ids: (v: RedstoneVersion) => readonly number[];

  /** Whether the version has this state at all (b1.8.1 has no ceiling lever). Default: yes. */
  readonly valid?: (state: number, v: RedstoneVersion) => boolean;
  /** The state the part starts in: bits the game sets at run time are cleared, a twin becomes its base block. */
  readonly normalize?: (state: number, v: RedstoneVersion) => number;
  /** Whether the block it stands or hangs on can hold it. A part that is not held is left out and reported. */
  readonly supported?: (w: World, at: number, state: number) => boolean;

  /** Weak power 0..15 for the reader on side `toward`. */
  readonly weak?: (w: World, at: number, state: number, toward: Dir) => number;
  /** Strong power 0..15 for the reader on side `toward`: what a solid block passes on. */
  readonly strong?: (w: World, at: number, state: number, toward: Dir) => number;

  /** Called once per engine before the design is loaded: the place to add stores, phases and busy checks. */
  readonly init?: (w: World) => void;
  /** The first update, when the design is loaded. Default: `update` with itself as the source. */
  readonly start?: (w: World, at: number, state: number) => void;
  /** A neighbour changed. `sourceId` is the block id the update comes from. */
  readonly update?: (w: World, at: number, state: number, from: number, sourceId: number) => void;
  /** A scheduled tick of this cell ran. */
  readonly tick?: (w: World, at: number, state: number) => void;
  /** The scheduler's identity for the block: twins share one where the version says so. Default: the block id. */
  readonly tickKey?: (id: number, v: RedstoneVersion) => number;

  /** What a click on the cell drives; null or left out: not an input. */
  readonly inputKind?: (state: number, v: RedstoneVersion) => InputKind | null;
  /** A user input for this cell. An event of the wrong kind does nothing. */
  readonly input?: (w: World, at: number, state: number, e: InputEvent) => void;

  /** `Simulation.powerAt` for the cell. Default: 0. */
  readonly power?: (w: World, at: number, state: number) => number;
  /** `Simulation.lockedAt` for the cell: whether a lock holds the part now. Default: no. */
  readonly locked?: (w: World, at: number, state: number) => boolean;

  /** The state the cell shows when it is not its own (a moving block shows what it carries). Default: its own. */
  readonly shown?: (w: World, at: number, state: number) => number;
  /**
   * 1.12.2: the cell `changed`, one of this cell's six neighbours, took a new state. The engine calls it for every state
   * change, as the game does for observers, whether or not the change tells its neighbours.
   */
  readonly observed?: (w: World, at: number, state: number, changed: number) => void;
  /** A container's fill level 0..15, which a comparator reads (set by the user, `level` input). Default: none. */
  readonly fill?: (w: World, at: number, state: number) => number;
  /** The block was put in place at run time (a piston's move landed): the game's "block added". Default: nothing. */
  readonly placed?: (w: World, at: number, state: number) => void;
}
