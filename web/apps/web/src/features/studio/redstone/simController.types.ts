/**
 * The contract between the redstone studio's simulation controller and the debugger's UI. The controller owns the
 * simulation, the run loop and the layer that shows simulated states on the canvas; the debugger renders `snapshot()` and
 * calls these methods, and reaches nothing else. Both sides are written against this file, so change it on purpose only.
 */
import type {
  Breakpoint,
  BreakpointHit,
  Diagnostic,
  ExplainStep,
  PendingTick,
  Pos,
  RedstoneVersion,
  Sample,
  SimEvent,
} from '@mineclaw/redstone';

/** `edit`: the canvas is being drawn and nothing runs. `paused`, `running`: the circuit is live, the canvas locked. */
export type SimMode = 'edit' | 'paused' | 'running';

/** Game ticks per second (20 is the game's own pace), or `'max'`: as many as fit in the frame budget. */
export type SimSpeed = number | 'max';
export const SIM_SPEEDS: readonly SimSpeed[] = [1, 2, 5, 10, 20, 'max'];
export const DEFAULT_SIM_SPEED: SimSpeed = 10;

/**
 * How many times a second simulated states are pushed to the canvas view. It only limits redrawing (re-meshing is what
 * costs frames); the simulation runs at its own speed and skipped states still reach the timeline.
 */
export type DisplayHz = 5 | 10 | 20;
export const DISPLAY_RATES: readonly DisplayHz[] = [5, 10, 20];
export const DEFAULT_DISPLAY_HZ: DisplayHz = 10;

/** A watched cell. `color` is 0xRRGGBB: its marker on the canvas and its lane in the timeline. */
export interface Probe {
  id: string;
  at: Pos;
  label: string;
  color: number;
}

/** What a container or an analog source holds when the circuit starts, 1..15: part of the design, set with `setLevel`. */
export interface StartLevel {
  at: Pos;
  level: number;
}

/** Referentially stable between changes, so it can back `useSyncExternalStore`. */
export interface SimSnapshot {
  mode: SimMode;
  /** The clock in game ticks; 0 is the start state. */
  tick: number;
  /** Nothing is scheduled, queued or moving: without an input nothing will change. */
  idle: boolean;
  speed: SimSpeed;
  /** The display rate the user chose. */
  displayHz: DisplayHz;
  /** The display rate in use: lower than `displayHz` while the frame rate is too low to keep up with it. */
  displayHzNow: DisplayHz;
  /** Game ticks that can be stepped back from here. */
  canStepBack: number;
  probes: readonly Probe[];
  breakpoints: readonly Breakpoint[];
  /** The design's start levels (levels set while the simulation is live are not among them). */
  levels: readonly StartLevel[];
  /** The breakpoints that stopped the last step or run. */
  lastHits: readonly BreakpointHit[];
  /** The newest 200, oldest first. */
  events: readonly SimEvent[];
  diagnostics: readonly Diagnostic[];
  version: RedstoneVersion;
}

/** One cell as the inspector shows it. */
export interface CellInspection {
  sample: Sample;
  /** A lock holds this repeater (1.12.2); false for anything else. */
  locked: boolean;
  pending: readonly PendingTick[];
  /** The chain that powers the cell, from the cell itself back to its source. */
  explain: readonly ExplainStep[];
}

export interface SimControllerApi {
  subscribe(fn: () => void): () => void;
  snapshot(): SimSnapshot;

  start(): void;
  pause(): void;
  /** Back to edit mode; the simulated states leave the canvas view. */
  stop(): void;
  /** A step from edit mode enters `paused` first. */
  stepGameTick(): void;
  stepRedstoneTick(): void;
  stepBack(): void;
  /** Rewinds to a tick that is still in history; nothing happens for one that is not. */
  stepBackTo(tick: number): void;
  runUntilChange(): void;
  /** Back to tick 0; probes and breakpoints stay. */
  reset(): void;
  setSpeed(speed: SimSpeed): void;
  setDisplayHz(hz: DisplayHz): void;

  /** Toggles a lever, presses a button, steps on or off a plate. False when the cell is not an input. */
  click(at: Pos): boolean;
  /** Sets what a container or an analog source holds, 0..15. */
  setLevel(at: Pos, level: number): void;

  addProbe(at: Pos): void;
  removeProbe(id: string): void;
  renameProbe(id: string, label: string): void;
  /** Adds the breakpoint, or replaces the one with the same id. */
  setBreakpoint(b: Breakpoint): void;
  removeBreakpoint(id: string): void;

  /** A probe's samples from `fromTick` to `toTick` (both included), oldest first, as far as history reaches. */
  timeline(probeId: string, fromTick: number, toTick: number): readonly Sample[];
  /** Null while there is no simulation to ask (it is still being built). */
  inspect(at: Pos): CellInspection | null;
}
