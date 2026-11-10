/**
 * Shared bits for the engine tests: block ids, a builder that puts a design on flat ground (the studio's build plot),
 * and a way to play inputs into the clock and read a cell after every game tick.
 */
import type { Cell, InputEvent, Pos, RedstoneVersion, SimEvent } from '../src/api.js';
import { RedstoneSimulation } from '../src/simulation.js';
import type { EngineOptions } from '../src/engine.js';

export const VERSIONS: readonly RedstoneVersion[] = ['b1.8.1', '1.12.2'];

export const STONE = 1;
export const SLAB = 44;
export const DUST = 55;
export const LEVER = 69;
export const STONE_PLATE = 70;
export const WOODEN_PLATE = 72;
export const TORCH_OFF = 75;
export const TORCH = 76;
export const STONE_BUTTON = 77;
export const GLOWSTONE = 89;
export const REPEATER = 93;
export const REPEATER_ON = 94;
export const LAMP = 123;
export const WOODEN_BUTTON = 143;
export const LIGHT_PLATE = 147;
export const COMPARATOR = 149;
export const STICKY_PISTON = 29;
export const PISTON = 33;
export const POWERED_RAIL = 27;
export const OBSIDIAN = 49;
export const NOTE_BLOCK = 25;
export const TNT = 46;
export const DISPENSER = 23;
export const DROPPER = 158;
export const CHEST = 54;
export const HOPPER = 154;
export const OBSERVER = 218;
export const REDSTONE_BLOCK = 152;
export const SLIME = 165;
export const WOODEN_DOOR = 64;
export const TRAPDOOR = 96;
export const FENCE_GATE = 107;
export const DAYLIGHT = 151;
export const DETECTOR_RAIL = 28;

/** Torch metas: the block it hangs on is west (1), east (2), north (3), south (4) of it, or below it (5). */
export const TORCH_ON_WEST = 1;
export const TORCH_ON_NORTH = 3;
export const TORCH_ON_FLOOR = 5;
/** Repeater metas (delay 1): reads from the south (0), west (1), north (2) or east (3); puts out on the opposite side. */
export const READS_SOUTH = 0;
export const READS_WEST = 1;
export const READS_NORTH = 2;
export const READS_EAST = 3;
/** Lever and button metas: sticks out east (1), west (2), south (3), north (4); a floor lever 5; bit 8 is on. */
export const STICKS_EAST = 1;
export const STICKS_WEST = 2;
export const STICKS_NORTH = 4;
export const FLOOR = 5;
export const ON = 8;

export const cell = (at: Pos, id: number, meta = 0): Cell => [at[0], at[1], at[2], id, meta];

/** A design standing on flat ground whose top layer is y -1, as on the studio's build plot. */
export function build(
  version: RedstoneVersion,
  cells: readonly Cell[],
  opts: EngineOptions & { dug?: readonly Pos[]; extras?: Array<{ at: Pos; level: number }> } = {},
): RedstoneSimulation {
  const { dug, extras, ...engine } = opts;
  return new RedstoneSimulation(
    { version, cells, ground: dug ? { topY: -1, dug } : { topY: -1 }, ...(extras ? { extras } : {}) },
    engine,
  );
}

export const toggle = (at: Pos): InputEvent => ({ kind: 'toggle', at });

/**
 * Steps the clock up to `until`, queuing the inputs listed under a tick just before that tick (they land at its start),
 * and returns `read` before the first tick and after every tick, by tick number.
 */
export function play(
  sim: RedstoneSimulation,
  until: number,
  inputs: Readonly<Record<number, readonly InputEvent[]>>,
  read: (s: RedstoneSimulation) => number,
): Map<number, number> {
  const out = new Map<number, number>([[sim.tick, read(sim)]]);
  while (sim.tick < until) {
    for (const e of inputs[sim.tick + 1] ?? []) sim.input(e);
    sim.stepGameTick();
    out.set(sim.tick, read(sim));
  }
  return out;
}

/** Steps the clock up to `until` like `play`, and returns every event of those ticks. */
export function drive(
  sim: RedstoneSimulation,
  until: number,
  inputs: Readonly<Record<number, readonly InputEvent[]>>,
): SimEvent[] {
  const events: SimEvent[] = [];
  while (sim.tick < until) {
    for (const e of inputs[sim.tick + 1] ?? []) sim.input(e);
    events.push(...sim.stepGameTick().events);
  }
  return events;
}

/** The block id the engine holds in a cell, which for a block in transit is the moving block (36), not what it shows. */
export const heldId = (sim: RedstoneSimulation, at: Pos): number => sim.engine.cells[sim.engine.grid.indexOf(at)]! >> 4;

/** The ticks at which a played value goes from 0 to more (`rising`) or from more to 0. */
export function edges(values: ReadonlyMap<number, number>, rising: boolean): number[] {
  const out: number[] = [];
  let before: number | null = null;
  for (const [tick, v] of values) {
    if (before !== null && (rising ? before === 0 && v > 0 : before > 0 && v === 0)) out.push(tick);
    before = v;
  }
  return out;
}

/**
 * The RS NOR latch of report 8.3 (box 6 x 1 x 5), with a lever on each input port in place of the user's wire: R at
 * (-1,0,1) on a block to its west, S at (6,0,1) on a block to its east. Q is tapped at (2,0,0), Q-bar at (2,0,4).
 */
export const LATCH = {
  r: [-1, 0, 1] as Pos,
  s: [6, 0, 1] as Pos,
  q: [2, 0, 0] as Pos,
  qBar: [2, 0, 4] as Pos,
  t1: [2, 0, 1] as Pos,
  t2: [4, 0, 2] as Pos,
  cells: [
    cell([-2, 0, 1], STONE),
    cell([-1, 0, 1], LEVER, STICKS_EAST),
    cell([0, 0, 1], REPEATER, READS_WEST),
    cell([1, 0, 1], STONE),
    cell([2, 0, 1], TORCH, TORCH_ON_WEST),
    cell([3, 0, 1], DUST),
    cell([4, 0, 1], STONE),
    cell([5, 0, 1], REPEATER, READS_EAST),
    cell([6, 0, 1], LEVER, STICKS_WEST),
    cell([7, 0, 1], STONE),
    cell([4, 0, 2], TORCH, TORCH_ON_NORTH),
    cell([4, 0, 3], DUST),
    cell([3, 0, 3], DUST),
    cell([2, 0, 3], DUST),
    cell([1, 0, 3], DUST),
    cell([1, 0, 2], DUST),
    cell([2, 0, 0], DUST),
    cell([2, 0, 4], DUST),
  ] as readonly Cell[],
};

/** The id a cell shows. */
export const idAt = (sim: RedstoneSimulation, at: Pos): number => sim.stateAt(at) >> 4;
