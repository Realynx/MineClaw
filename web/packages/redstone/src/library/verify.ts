/**
 * The harness that proves a component's spec against the engine. It wires the layout as a user would: for an input port
 * dust on the port cell, a stone one cell further out and a lever standing on that stone (the lever strongly powers the
 * stone, the stone powers the dust); for an output port dust on the port cell, read as on when its power is above 0. The
 * design stands on ground whose top layer is y -1.
 *
 * An analog input (1.12.2) gets a daylight sensor one cell further out instead of the stone and the lever: the harness
 * holds it at the level the spec asks for, and the dust on the port cell takes that level. An analog output is read as
 * the power of its port dust.
 *
 * A truth table is walked so that every row is reached from every other row, so a gate that only works from a fresh
 * start, or that holds on to an earlier row, fails.
 */
import type { Cell, Diagnostic, Pos, RedstoneVersion, Simulation } from '../api.js';
import type { EngineOptions } from '../engine.js';
import { EAST, NORTH, SOUTH, WEST, dirOfSide, leftOf, opposite, rightOf, type Dir } from '../orientation.js';
import { createSimulation } from '../simulation.js';
import type { ComponentDef, ComponentLayout, ComponentSpec, Feeder, Port, VerifyResult } from './types.js';

const STONE = 1;
const DUST = 55;
const LEVER = 69;
const DAYLIGHT = 151;
/** A lever standing on the block below it. */
const FLOOR_LEVER = 5;

/** The x and z step to the neighbour on each horizontal side. */
const STEP: Readonly<Record<Dir, readonly [number, number]>> = {
  [NORTH]: [0, -1],
  [SOUTH]: [0, 1],
  [WEST]: [-1, 0],
  [EAST]: [1, 0],
};

const add = (p: Pos, d: Dir): Pos => [p[0] + STEP[d]![0], p[1], p[2] + STEP[d]![1]];
const key = (p: Pos | Cell): string => `${p[0]},${p[1]},${p[2]}`;
const show = (p: Pos | Cell): string => `(${p[0]},${p[1]},${p[2]})`;

/** The cells the harness puts round one port; `analog` drives an input with a daylight sensor. */
export function harnessCells(port: Port, feeder: Feeder, analog = false): Cell[] {
  const at = port.at;
  if (port.kind === 'out') return [[at[0], at[1], at[2], DUST, 0]];
  const out = dirOfSide(port.side);
  const block = add(at, out);
  const cells: Cell[] = analog
    ? [
        [at[0], at[1], at[2], DUST, 0],
        [block[0], block[1], block[2], DAYLIGHT, 0],
      ]
    : [
        [at[0], at[1], at[2], DUST, 0],
        [block[0], block[1], block[2], STONE, 0],
        [block[0], block[1] + 1, block[2], LEVER, FLOOR_LEVER],
      ];
  if (feeder !== 'straight') {
    // Left or right as seen standing on the port and facing the box.
    const inward = opposite(out);
    const spur = add(at, feeder === 'spurLeft' ? leftOf(inward) : rightOf(inward));
    cells.push([spur[0], spur[1], spur[2], DUST, 0]);
  }
  return cells;
}

/** The layout with every port wired, or why it cannot be: a feeder cell on a cell already taken. */
function wire(
  layout: ComponentLayout,
  feeder: Feeder,
  analog: ReadonlySet<string>,
): { cells: Cell[]; failures: string[] } {
  const taken = new Map<string, string>();
  for (const c of layout.cells) taken.set(key(c), 'the layout');
  const cells: Cell[] = [...layout.cells];
  const failures: string[] = [];
  for (const port of layout.ports) {
    for (const c of harnessCells(port, feeder, analog.has(port.name))) {
      const owner = taken.get(key(c));
      if (owner) failures.push(`the ${feeder} feeder of ${port.name} needs ${show(c)}, which ${owner} holds`);
      else {
        taken.set(key(c), `port ${port.name}`);
        cells.push(c);
      }
    }
  }
  return { cells, failures };
}

/** Plays a spec against a wired layout. */
class Bench {
  private readonly inputs = new Map<string, number>();
  readonly failures: string[] = [];

  constructor(
    readonly sim: Simulation,
    private readonly layout: ComponentLayout,
    private readonly analog: ReadonlySet<string>,
  ) {}

  private port(name: string, kind: 'in' | 'out'): Port | null {
    const port = this.layout.ports.find((p) => p.name === name && p.kind === kind) ?? null;
    const missing = `the spec names ${kind === 'in' ? 'input' : 'output'} ${name}, which has no port`;
    if (!port && !this.failures.includes(missing)) this.failures.push(missing);
    return port;
  }

  /**
   * Sets an input: a lever on (any value above 0) or off, an analog input's sensor to the level. It takes effect at the
   * start of the next game tick.
   */
  set(name: string, value: number): void {
    const port = this.port(name, 'in');
    if (!port) return;
    const analog = this.analog.has(name);
    const next = analog ? value : value > 0 ? 1 : 0;
    if ((this.inputs.get(name) ?? 0) === next) return;
    const block = add(port.at, dirOfSide(port.side));
    if (analog) this.sim.input({ kind: 'level', at: block, level: next });
    else this.sim.input({ kind: 'toggle', at: [block[0], block[1] + 1, block[2]] });
    this.inputs.set(name, next);
  }

  /** An output: on (1) or off (0), or an analog output's level. */
  read(name: string): number {
    const port = this.port(name, 'out');
    if (!port) return 0;
    const power = this.sim.powerAt(port.at);
    return this.analog.has(name) ? power : power > 0 ? 1 : 0;
  }

  /**
   * Steps the clock. A torch that burns out is a fault of the layout, whatever the spec says, and so is a run the engine
   * cuts short (a tick it reports as unstable): the outputs read after it prove nothing.
   */
  run(gameTicks: number): void {
    if (gameTicks <= 0) return;
    const r = this.sim.run(gameTicks, 'breakpoint');
    for (const e of r.events)
      if (e.kind === 'burnout') this.failures.push(`the torch at ${show(e.at)} burned out on tick ${e.tick}`);
    if (r.ranTicks < gameTicks)
      this.failures.push(`the engine stopped after ${r.ranTicks} of ${gameTicks} game ticks, on tick ${r.tick}`);
  }
}

/** A walk through `n` rows that takes every step from one row to another once (an Euler circuit of the full graph). */
export function everyStep(n: number): number[] {
  if (n <= 1) return n === 1 ? [0] : [];
  const next = Array.from({ length: n }, (_, i) => Array.from({ length: n }, (_, j) => j).filter((j) => j !== i));
  const stack = [0];
  const walk: number[] = [];
  while (stack.length > 0) {
    const at = stack[stack.length - 1]!;
    const to = next[at]!.shift();
    if (to === undefined) walk.push(stack.pop()!);
    else stack.push(to);
  }
  return walk.reverse();
}

/** How long a settled gate may still have scheduled ticks waiting. */
const IDLE_WITHIN = 100;

const label = (names: readonly string[], values: readonly number[]): string =>
  names.map((n, i) => `${n}=${values[i]}`).join(' ');

function truth(bench: Bench, spec: Extract<ComponentSpec, { kind: 'truth' }>): void {
  const rows = spec.rows;
  // Start from the row with every input off, which is how the harness builds the circuit.
  const start = Math.max(
    0,
    rows.findIndex(([ins]) => ins.every((x) => x === 0)),
  );
  const walk = everyStep(rows.length).map((i) => (i + start) % rows.length);
  let from = 'the start';
  for (const r of walk) {
    const [ins, outs] = rows[r]!;
    spec.inputs.forEach((name, i) => bench.set(name, ins[i]!));
    bench.run(spec.settleTicks);
    const now = label(spec.inputs, ins);
    const check = (when: string): void =>
      spec.outputs.forEach((name, i) => {
        const got = bench.read(name);
        if (got !== outs[i])
          bench.failures.push(`${now} after ${from}: ${name} is ${got} ${when}, expected ${outs[i]}`);
      });
    check(`${spec.settleTicks} game ticks later`);
    // Settled means right from then on: the outputs hold every tick until nothing is left to run. A b1.8.1 torch can
    // still have a tick waiting that changes nothing.
    for (let t = 0; t < IDLE_WITHIN && !bench.sim.idle && bench.failures.length === 0; t++) {
      bench.run(1);
      check(`at tick ${bench.sim.tick}`);
    }
    if (!bench.sim.idle) bench.failures.push(`${now} after ${from}: still running ${IDLE_WITHIN} game ticks later`);
    from = now;
    if (bench.failures.length > 0) return;
  }
}

function sequence(bench: Bench, spec: Extract<ComponentSpec, { kind: 'sequence' }>): void {
  spec.steps.forEach((step, n) => {
    if (bench.failures.length > 0) return;
    for (const [name, value] of Object.entries(step.set ?? {})) bench.set(name, value);
    if (step.pulse) {
      if (step.pulse.ticks < spec.minPulseTicks)
        bench.failures.push(
          `step ${n + 1} pulses for ${step.pulse.ticks} game ticks, under the ${spec.minPulseTicks} minimum`,
        );
      bench.set(step.pulse.port, 15);
      bench.run(step.pulse.ticks);
      bench.set(step.pulse.port, 0);
    }
    bench.run(step.wait);
    for (const [name, want] of Object.entries(step.expect ?? {})) {
      const got = bench.read(name);
      if (got !== want)
        bench.failures.push(`step ${n + 1} (tick ${bench.sim.tick}): ${name} is ${got}, expected ${want}`);
    }
  });
}

/** Runs and samples the output from the settled start; three whole cycles must match the spec. */
function clock(bench: Bench, spec: Extract<ComponentSpec, { kind: 'clock' }>): void {
  const ticks = spec.periodTicks * 5;
  const values: number[] = [];
  for (let t = 0; t < ticks; t++) {
    bench.run(1);
    values.push(bench.read(spec.output));
  }
  const rises: number[] = [];
  for (let t = 1; t < values.length; t++) if (values[t - 1] === 0 && values[t] === 1) rises.push(t);
  if (rises.length < 4) {
    bench.failures.push(`${spec.output} rose ${rises.length} times in ${ticks} game ticks`);
    return;
  }
  for (let i = 0; i + 1 < rises.length; i++) {
    const period = rises[i + 1]! - rises[i]!;
    const high = values.slice(rises[i], rises[i + 1]).filter((x) => x === 1).length;
    if (period !== spec.periodTicks)
      bench.failures.push(`cycle ${i + 1} lasts ${period} game ticks, not ${spec.periodTicks}`);
    if (high !== spec.highTicks)
      bench.failures.push(`cycle ${i + 1} is high for ${high} game ticks, not ${spec.highTicks}`);
  }
}

export function verifyComponent(def: ComponentDef, v: RedstoneVersion, feeder: Feeder = 'straight'): VerifyResult {
  return verifyWith(def, v, feeder);
}

/** A diagnostic as a failure line. */
const diagnosticText = (d: Diagnostic): string => `${d.code} at ${show(d.at)}: ${d.text}`;

/**
 * `verifyComponent` with the engine's internal options (a test can lower the update budget to make a runaway cheap). A
 * diagnostic is a failure whenever it is raised: when the circuit is built, or while the spec runs.
 */
export function verifyWith(
  def: ComponentDef,
  v: RedstoneVersion,
  feeder: Feeder,
  engine: EngineOptions = {},
): VerifyResult {
  const layout = def.layouts[v];
  if (!layout) return { ok: false, failures: [`${def.id} has no layout for ${v}`] };
  const spec = def.spec;
  const analog = new Set(spec.kind === 'clock' ? [] : (spec.analog ?? []));
  if (analog.size > 0 && v !== '1.12.2')
    return { ok: false, failures: [`${def.id} has analog ports, which only 1.12.2 can drive`] };
  const wired = wire(layout, feeder, analog);
  if (wired.failures.length > 0) return { ok: false, failures: wired.failures };
  // The bench keeps no history: nothing here steps back, and a keyframe per call adds up over every placement.
  const opts: EngineOptions = { ...engine, historyTicks: 0 };
  const sim = createSimulation({ version: v, cells: wired.cells, ground: { topY: -1 } }, opts);
  const bench = new Bench(sim, layout, analog);
  const built = sim.diagnostics().map(diagnosticText);
  bench.failures.push(...built);
  if (bench.failures.length === 0) {
    if (spec.kind === 'truth') truth(bench, spec);
    else if (spec.kind === 'sequence') sequence(bench, spec);
    else clock(bench, spec);
    for (const d of sim.diagnostics().map(diagnosticText)) if (!built.includes(d)) bench.failures.push(d);
  }
  return { ok: bench.failures.length === 0, failures: bench.failures };
}
