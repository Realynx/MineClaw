/**
 * A scripted stand-in for the engine, for tests of the code around it (the studio's simulation controller, the debugger).
 * It keeps the whole `Simulation` contract (the clock, queued inputs, history, breakpoints, watched samples, shown cells)
 * and takes what the circuit does from a script instead of working it out. Never used by the app itself.
 */
import {
  GROUND_ID,
  type Breakpoint,
  type BreakpointHit,
  type Cell,
  type CircuitInput,
  type CreateSimulation,
  type Diagnostic,
  type ExplainStep,
  type InputEvent,
  type InputKind,
  type PendingTick,
  type Pos,
  type RedstoneVersion,
  type Sample,
  type SimEvent,
  type Simulation,
  type StepResult,
} from './api.js';
import { breakpointHit, type CellValue } from './breakpoints.js';

/** What one scripted game tick does. Whatever a tick leaves out stays as it was. */
export interface FakeTick {
  /** Shown states this tick sets. */
  cells?: readonly Cell[];
  /** Power levels this tick sets. */
  power?: ReadonlyArray<readonly [x: number, y: number, z: number, level: number]>;
  /** Events this tick raises; the tick number is filled in. */
  events?: ReadonlyArray<Omit<SimEvent, 'tick'>>;
  /** The locked repeaters from this tick on. */
  locked?: readonly Pos[];
  /**
   * Whether the circuit has nothing left to do after this tick. Default: a table script is busy until its last scripted
   * tick has run; a function script is idle unless it says otherwise.
   */
  idle?: boolean;
}

/**
 * The script: a table by game tick, or a function of the tick and the inputs applied at its start. Tick 0 is the start
 * state. A function is called again for the same tick when the simulation replays after a rewind, so it has to give the
 * same answer for the same arguments.
 */
export type FakeScript =
  Readonly<Record<number, FakeTick>> | ((tick: number, inputs: readonly InputEvent[]) => FakeTick | null | undefined);

export interface FakeOptions {
  /** What `inputAt` answers; a cell that is not listed is not an input. */
  inputs?: ReadonlyArray<readonly [at: Pos, kind: InputKind]>;
  /** What `pendingAt` answers, until the clock reaches each `dueTick`. */
  pending?: ReadonlyArray<PendingTick & { at: Pos }>;
  explain?: (at: Pos) => readonly ExplainStep[];
  diagnostics?: readonly Diagnostic[];
  /** How far back `stepBack` reaches (default 2000). */
  historyTicks?: number;
}

type Shown = readonly [at: Pos, state: number];

const keyOf = (p: Pos | Cell): string => `${p[0]},${p[1]},${p[2]}`;
const byYZX = (a: Pos | Cell, b: Pos | Cell): number => a[1] - b[1] || a[2] - b[2] || a[0] - b[0];
const stateOf = (id: number, meta: number): number => (id << 4) | (meta & 15);
const cellOf = (at: Pos, state: number): Cell => [at[0], at[1], at[2], state >> 4, state & 15];

export class FakeSimulation implements Simulation {
  readonly version: RedstoneVersion;
  /** Every input that took effect, with the game tick it began. None is judged: one for a plain block is kept too. */
  readonly inputLog: Array<{ tick: number; event: InputEvent }> = [];

  private readonly design = new Map<string, number>();
  private readonly dug = new Set<string>();
  private readonly kinds = new Map<string, InputKind>();
  private readonly shown = new Map<string, Shown>();
  private readonly power = new Map<string, number>();
  private readonly samples = new Map<string, Sample[]>();
  private readonly historyTicks: number;
  private watching = new Map<string, Pos>();
  private locked: Pos[] = [];
  private queue: InputEvent[] = [];
  private armed: Breakpoint[] = [];
  private clock = 0;
  private floor = 0;
  private busy = false;

  constructor(
    /** The circuit it was built from, as given. */
    readonly circuit: CircuitInput,
    private readonly script: FakeScript = {},
    private readonly options: FakeOptions = {},
  ) {
    this.version = circuit.version;
    this.historyTicks = options.historyTicks ?? 2000;
    for (const c of circuit.cells) this.design.set(keyOf(c), stateOf(c[3], c[4]));
    for (const p of circuit.ground?.dug ?? []) this.dug.add(keyOf(p));
    for (const [at, kind] of options.inputs ?? []) this.kinds.set(keyOf(at), kind);
    this.replay(0);
  }

  get tick(): number {
    return this.clock;
  }

  get historyDepth(): number {
    return this.clock - this.floor;
  }

  get idle(): boolean {
    return !this.busy && this.queue.length === 0;
  }

  /** The inputs waiting for the next game tick. */
  get queued(): readonly InputEvent[] {
    return this.queue;
  }

  get breakpoints(): readonly Breakpoint[] {
    return this.armed;
  }

  get watched(): readonly Pos[] {
    return [...this.watching.values()];
  }

  // ------------------------------------------------------------------ stepping

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
    return this.rewind(this.clock - Math.max(0, Math.min(Math.floor(gameTicks), this.historyDepth)));
  }

  reset(): StepResult {
    this.floor = 0;
    return this.rewind(0);
  }

  input(e: InputEvent): void {
    this.queue.push(e);
  }

  inputAt(at: Pos): InputKind | null {
    return this.kinds.get(keyOf(at)) ?? null;
  }

  setBreakpoints(list: readonly Breakpoint[]): void {
    this.armed = list.map((b) => ({ ...b }));
  }

  watch(list: readonly Pos[]): void {
    this.watching = new Map(list.map((p) => [keyOf(p), p] as const));
    for (const k of [...this.samples.keys()]) if (!this.watching.has(k)) this.samples.delete(k);
    this.sampleWatched();
  }

  trace(at: Pos, fromTick: number, toTick: number): readonly Sample[] {
    const from = Math.max(fromTick, this.floor);
    return (this.samples.get(keyOf(at)) ?? []).filter((s) => s.tick >= from && s.tick <= toTick);
  }

  // ------------------------------------------------------------------ reading

  displayCells(): readonly Cell[] {
    const out: Cell[] = [];
    for (const [k, [at, state]] of this.shown) if (state !== this.designed(k, at)) out.push(cellOf(at, state));
    return out.sort(byYZX);
  }

  stateAt(at: Pos): number {
    const k = keyOf(at);
    return this.shown.get(k)?.[1] ?? this.designed(k, at);
  }

  powerAt(at: Pos): number {
    return this.power.get(keyOf(at)) ?? 0;
  }

  sample(at: Pos): Sample {
    const power = this.powerAt(at);
    return { tick: this.clock, power, powered: power > 0, state: this.stateAt(at) };
  }

  lockedAt(at: Pos): boolean {
    const k = keyOf(at);
    return this.locked.some((p) => keyOf(p) === k);
  }

  lockedCells(): readonly Pos[] {
    return this.locked;
  }

  pendingAt(at: Pos): readonly PendingTick[] {
    const k = keyOf(at);
    return (this.options.pending ?? [])
      .filter((p) => keyOf(p.at) === k && p.dueTick > this.clock)
      .map(({ dueTick, priority }) => ({ dueTick, priority }))
      .sort((a, b) => a.dueTick - b.dueTick || a.priority - b.priority);
  }

  explain(at: Pos): readonly ExplainStep[] {
    return this.options.explain?.(at) ?? [];
  }

  diagnostics(): readonly Diagnostic[] {
    return this.options.diagnostics ?? [];
  }

  // ------------------------------------------------------------------ the scripted world

  /** What the design holds at a cell: its block, the ground under the plot, or air. */
  private designed(k: string, at: Pos): number {
    const state = this.design.get(k);
    if (state !== undefined) return state;
    const ground = this.circuit.ground;
    return ground && at[1] <= ground.topY && !this.dug.has(k) ? stateOf(GROUND_ID, 0) : 0;
  }

  private valueAt(at: Pos): CellValue {
    return { state: this.stateAt(at), power: this.powerAt(at) };
  }

  /** The cells whose shown state differs from what it was in `before` (an earlier copy of the shown states). */
  private changedSince(before: ReadonlyMap<string, Shown>): Cell[] {
    const out: Cell[] = [];
    for (const [k, [at, state]] of this.shown)
      if (state !== (before.get(k)?.[1] ?? this.designed(k, at))) out.push(cellOf(at, state));
    // A cell that was shown before and is not any more is back to what the design holds.
    for (const [k, [at, state]] of before) {
      const now = this.designed(k, at);
      if (!this.shown.has(k) && state !== now) out.push(cellOf(at, now));
    }
    return out.sort(byYZX);
  }

  private frame(tick: number, inputs: readonly InputEvent[]): FakeTick {
    return (typeof this.script === 'function' ? this.script(tick, inputs) : this.script[tick]) ?? {};
  }

  /** Whether a table script still has a tick to run after `tick`. */
  private scriptedAfter(tick: number): boolean {
    return typeof this.script !== 'function' && Object.keys(this.script).some((t) => Number(t) > tick);
  }

  private apply(tick: number, inputs: readonly InputEvent[]): FakeTick {
    const f = this.frame(tick, inputs);
    for (const c of f.cells ?? []) this.shown.set(keyOf(c), [[c[0], c[1], c[2]], stateOf(c[3], c[4])]);
    for (const [x, y, z, level] of f.power ?? []) this.power.set(keyOf([x, y, z]), level);
    if (f.locked) this.locked = [...f.locked].sort(byYZX);
    this.busy = f.idle === undefined ? this.scriptedAfter(tick) : !f.idle;
    return f;
  }

  /** The state at `target`, built again from tick 0 with the inputs that are still in the log. */
  private replay(target: number): void {
    this.shown.clear();
    this.power.clear();
    this.locked = [];
    this.apply(0, []);
    for (let t = 1; t <= target; t++) {
      const inputs = this.inputLog.filter((i) => i.tick === t).map((i) => i.event);
      this.apply(t, inputs);
    }
    this.clock = target;
  }

  private sampleWatched(): void {
    for (const [k, at] of this.watching) {
      let list = this.samples.get(k);
      if (!list) this.samples.set(k, (list = []));
      if (list[list.length - 1]?.tick !== this.clock) list.push(this.sample(at));
    }
  }

  private runTick(): { events: SimEvent[]; hits: BreakpointHit[]; moved: boolean } {
    const armed = this.armed.filter((b) => b.enabled);
    const before = armed.map((b) => this.valueAt(b.at));
    const shownBefore = new Map(this.shown);
    const inputs = this.queue;
    this.queue = [];
    this.clock += 1;
    const tick = this.clock;
    for (const event of inputs) this.inputLog.push({ tick, event });
    const f = this.apply(tick, inputs);
    this.floor = Math.max(this.floor, tick - this.historyTicks);
    this.sampleWatched();
    return {
      events: (f.events ?? []).map((e) => ({ ...e, tick })),
      hits: armed.flatMap((b, i) => breakpointHit(b, before[i]!, this.valueAt(b.at), tick) ?? []),
      moved: this.changedSince(shownBefore).length > 0,
    };
  }

  /** Runs up to `maxTicks` game ticks, ending early on a breakpoint hit or when `done` says so after a tick. */
  private advance(maxTicks: number, done: (moved: boolean) => boolean): StepResult {
    const shownBefore = new Map(this.shown);
    const events: SimEvent[] = [];
    let hits: BreakpointHit[] = [];
    let ran = 0;
    while (ran < maxTicks) {
      const t = this.runTick();
      ran += 1;
      events.push(...t.events);
      hits = t.hits;
      if (hits.length > 0 || done(t.moved)) break;
    }
    return { tick: this.clock, ranTicks: ran, changed: this.changedSince(shownBefore), events, hits, idle: this.idle };
  }

  private rewind(target: number): StepResult {
    const shownBefore = new Map(this.shown);
    while (this.inputLog.length > 0 && this.inputLog[this.inputLog.length - 1]!.tick > target) this.inputLog.pop();
    this.queue = [];
    this.replay(target);
    for (const list of this.samples.values())
      while (list.length > 0 && list[list.length - 1]!.tick > target) list.pop();
    this.sampleWatched();
    return {
      tick: target,
      ranTicks: 0,
      changed: this.changedSince(shownBefore),
      events: [],
      hits: [],
      idle: this.idle,
    };
  }
}

/** A `createSimulation` stand-in: every call makes a `FakeSimulation` from the same script and keeps it, newest last. */
export function fakeSimulations(
  script: FakeScript = {},
  options: FakeOptions = {},
): { create: CreateSimulation; made: FakeSimulation[] } {
  const made: FakeSimulation[] = [];
  const create: CreateSimulation = (input, opts) => {
    const historyTicks = opts?.historyTicks ?? options.historyTicks;
    const sim = new FakeSimulation(input, script, {
      ...options,
      ...(historyTicks === undefined ? {} : { historyTicks }),
    });
    made.push(sim);
    return sim;
  };
  return { create, made };
}
