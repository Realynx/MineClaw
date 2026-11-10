/**
 * The engine's entry point: the `Simulation` contract (api.ts) on top of the engine core (engine.ts). This file turns
 * cells and positions into the contract's plain data, decides when a stepping call ends, and runs phase 6 of every game
 * tick: breakpoints, samples of the watched cells and history (history.ts).
 *
 * Breakpoints compare each cell's value at the end of the previous game tick with its value at the end of this one
 * (breakpoints.ts holds the rule). A rewind or a reset never hits; afterwards the comparison starts again from the
 * values at the tick it went back to.
 */
import type {
  Breakpoint,
  BreakpointHit,
  Cell,
  CircuitInput,
  Diagnostic,
  ExplainStep,
  InputEvent,
  InputKind,
  PendingTick,
  Pos,
  RedstoneVersion,
  Sample,
  SimEvent,
  Simulation,
  SimulationOptions,
  StepResult,
} from './api.js';
import { breakpointHit, type CellValue } from './breakpoints.js';
import { Engine, type EngineOptions, type Keyframe } from './engine.js';
import { explain } from './explain.js';
import { History } from './history.js';

/** How far back `stepBack` reaches unless the caller says otherwise. */
const HISTORY_TICKS = 2000;

/** The engine keeps the design as it is given: a caller that edits its arrays afterwards does not reach in. */
function copyInput(input: CircuitInput): CircuitInput {
  const copy: CircuitInput = {
    version: input.version,
    cells: input.cells.map((c): Cell => [c[0], c[1], c[2], c[3], c[4]]),
  };
  if (input.ground) {
    copy.ground = { topY: input.ground.topY };
    if (input.ground.dug) copy.ground.dug = input.ground.dug.map((p): Pos => [p[0], p[1], p[2]]);
  }
  if (input.extras) copy.extras = input.extras.map((x) => ({ ...x, at: [x.at[0], x.at[1], x.at[2]] as const }));
  return copy;
}

const keyOf = (p: Pos): string => `${p[0]},${p[1]},${p[2]}`;
const copyPos = (p: Pos): Pos => [p[0], p[1], p[2]];

/** One watched cell and its samples, oldest first, one per game tick it was watched. */
interface Watched {
  at: Pos;
  samples: Sample[];
}

export class RedstoneSimulation implements Simulation {
  readonly version: RedstoneVersion;
  /** The engine core. Tests read its counters; nothing outside the package holds it. */
  readonly engine: Engine;
  /** Tick 0 as the design settled: what `reset` goes back to. */
  private readonly origin: Keyframe;
  private readonly history: History;
  private readonly historyTicks: number;
  private armed: Breakpoint[] = [];
  /** Each armed breakpoint's cell at the end of the last game tick, in the order of `armed`. */
  private last: CellValue[] = [];
  private watching = new Map<string, Watched>();

  constructor(input: CircuitInput, opts: EngineOptions = {}) {
    this.version = input.version;
    this.engine = new Engine(copyInput(input), opts);
    this.origin = this.engine.save();
    this.historyTicks = Math.max(0, Math.floor(opts.historyTicks ?? HISTORY_TICKS));
    this.history = new History(this.historyTicks, this.engine, this.origin);
  }

  get tick(): number {
    return this.engine.tick;
  }

  get historyDepth(): number {
    return this.history.reach(this.engine.tick);
  }

  get idle(): boolean {
    return this.engine.idle;
  }

  // ------------------------------------------------------------------------------------------ stepping

  stepGameTick(): StepResult {
    return this.advance(1, () => false);
  }

  stepRedstoneTick(): StepResult {
    return this.advance(2, () => false);
  }

  run(maxGameTicks: number, until: 'breakpoint' | 'change' | 'idle'): StepResult {
    const max = until === 'idle' && this.idle ? 0 : Math.max(0, Math.floor(maxGameTicks));
    return this.advance(max, (moved) => (until === 'change' && moved) || (until === 'idle' && this.idle));
  }

  stepBack(gameTicks = 1): StepResult {
    const n = Number.isFinite(gameTicks) ? Math.floor(gameTicks) : 0;
    const target = this.engine.tick - Math.max(0, Math.min(n, this.historyDepth));
    return this.rewound(() => this.history.rewind(this.engine, target));
  }

  reset(): StepResult {
    return this.rewound(() => {
      this.engine.load(this.origin);
      this.history.restart(this.origin);
    });
  }

  input(e: InputEvent): void {
    const at: Pos = [e.at[0], e.at[1], e.at[2]];
    this.engine.enqueue({ ...e, at });
  }

  inputAt(at: Pos): InputKind | null {
    const i = this.engine.grid.indexOf(at);
    return i < 0 ? null : this.engine.inputKindAt(i);
  }

  setBreakpoints(list: readonly Breakpoint[]): void {
    this.armed = list.map((b) => {
      const copy: Breakpoint = { id: b.id, at: copyPos(b.at), on: b.on, enabled: b.enabled };
      if (b.value !== undefined) copy.value = b.value;
      return copy;
    });
    this.last = this.armed.map((b) => this.valueAt(b.at));
  }

  watch(list: readonly Pos[]): void {
    const next = new Map<string, Watched>();
    for (const at of list) {
      const k = keyOf(at);
      next.set(k, this.watching.get(k) ?? { at: copyPos(at), samples: [] });
    }
    this.watching = next;
    this.sampleWatched();
  }

  trace(at: Pos, fromTick: number, toTick: number): readonly Sample[] {
    const samples = this.watching.get(keyOf(at))?.samples;
    if (!samples) return [];
    const from = Math.max(fromTick, this.engine.tick - this.historyDepth);
    return samples.filter((s) => s.tick >= from && s.tick <= toTick).map((s) => ({ ...s }));
  }

  // ------------------------------------------------------------------------------------------ reading

  displayCells(): readonly Cell[] {
    return [...this.engine.differs].sort((a, b) => a - b).map((at) => this.cellAt(at));
  }

  stateAt(at: Pos): number {
    const grid = this.engine.grid;
    const i = grid.indexOf(at);
    return i < 0 ? grid.outside(at) : this.engine.shownAt(i);
  }

  powerAt(at: Pos): number {
    const i = this.engine.grid.indexOf(at);
    return i < 0 ? 0 : this.engine.powerAt(i);
  }

  sample(at: Pos): Sample {
    const power = this.powerAt(at);
    return { tick: this.tick, power, powered: power > 0, state: this.stateAt(at) };
  }

  lockedAt(at: Pos): boolean {
    const i = this.engine.grid.indexOf(at);
    return i >= 0 && this.engine.lockedAt(i);
  }

  lockedCells(): readonly Pos[] {
    return this.engine.lockedCells().map((at) => this.engine.pos(at));
  }

  pendingAt(at: Pos): readonly PendingTick[] {
    const i = this.engine.grid.indexOf(at);
    return i < 0 ? [] : this.engine.pendingAt(i);
  }

  explain(at: Pos): readonly ExplainStep[] {
    return explain(this.engine, this.engine.grid.indexOf(at));
  }

  diagnostics(): readonly Diagnostic[] {
    return this.engine.diagnostics();
  }

  // ------------------------------------------------------------------------------------------ inside

  private cellAt(at: number): Cell {
    const [x, y, z] = this.engine.pos(at);
    const state = this.engine.shownAt(at);
    return [x, y, z, state >> 4, state & 15];
  }

  private valueAt(at: Pos): CellValue {
    return { state: this.stateAt(at), power: this.powerAt(at) };
  }

  /** Runs up to `maxTicks` game ticks, ending early on a breakpoint hit, on a tick cut short, or when `done` says so. */
  private advance(maxTicks: number, done: (moved: boolean) => boolean): StepResult {
    const engine = this.engine;
    const before = new Map<number, number>();
    const events: SimEvent[] = [];
    let hits: BreakpointHit[] = [];
    let ran = 0;
    while (ran < maxTicks) {
      this.history.beginning(engine.tick + 1, engine.queued);
      const moved = engine.stepTick();
      ran += 1;
      for (const [at, shown] of engine.touched) if (!before.has(at)) before.set(at, shown);
      for (const e of engine.takeEvents()) events.push(e);
      hits = this.record();
      if (hits.length > 0 || engine.cut || done(moved)) break;
    }
    return this.result(ran, before, events, hits);
  }

  /** Phase 6 of a game tick that just ran: breakpoints, samples, history. */
  private record(): BreakpointHit[] {
    const tick = this.engine.tick;
    const hits: BreakpointHit[] = [];
    for (let i = 0; i < this.armed.length; i++) {
      const b = this.armed[i]!;
      const after = this.valueAt(b.at);
      const hit = breakpointHit(b, this.last[i]!, after, tick);
      if (hit) hits.push(hit);
      this.last[i] = after;
    }
    this.sampleWatched();
    const oldest = tick - this.historyTicks;
    for (const w of this.watching.values()) {
      let drop = 0;
      while (drop < w.samples.length && w.samples[drop]!.tick < oldest) drop++;
      if (drop > 0) w.samples.splice(0, drop);
    }
    this.history.ended(this.engine);
    return hits;
  }

  /** Gives every watched cell its sample for the current tick, unless it has it. */
  private sampleWatched(): void {
    const tick = this.engine.tick;
    for (const w of this.watching.values())
      if (w.samples[w.samples.length - 1]?.tick !== tick) w.samples.push(this.sample(w.at));
  }

  /**
   * Goes back in time with `go` and reports what moved. Samples after the new tick are dropped, the breakpoints start
   * comparing from here, and the watched cells get their sample of this tick.
   */
  private rewound(go: () => void): StepResult {
    const engine = this.engine;
    // Only a cell that differs from the design before or after can show something else.
    const before = new Map<number, number>();
    for (const at of engine.differs) before.set(at, engine.shownAt(at));
    go();
    for (const at of engine.differs) if (!before.has(at)) before.set(at, engine.grid.design[at]!);
    const tick = engine.tick;
    for (const w of this.watching.values()) {
      let keep = w.samples.length;
      while (keep > 0 && w.samples[keep - 1]!.tick > tick) keep--;
      w.samples.length = keep;
    }
    this.sampleWatched();
    this.last = this.armed.map((b) => this.valueAt(b.at));
    return this.result(0, before, [], []);
  }

  /** `before` holds what each cell that may have changed showed when the call began. */
  private result(
    ranTicks: number,
    before: ReadonlyMap<number, number>,
    events: SimEvent[],
    hits: BreakpointHit[],
  ): StepResult {
    const engine = this.engine;
    const moved: number[] = [];
    for (const [at, shown] of before) if (engine.shownAt(at) !== shown) moved.push(at);
    moved.sort((a, b) => a - b);
    return {
      tick: engine.tick,
      ranTicks,
      changed: moved.map((at) => this.cellAt(at)),
      events,
      hits,
      idle: engine.idle,
    };
  }
}

export function createSimulation(input: CircuitInput, opts?: SimulationOptions): Simulation {
  return new RedstoneSimulation(input, opts);
}
