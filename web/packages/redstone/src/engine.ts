/**
 * The engine core: the world the parts live in, and the game tick.
 *
 * One game tick (1/20 s) runs these phases in order:
 * 1. Inputs: what the user queued is applied. Anything it schedules counts from the clock before the tick, as in the
 *    game, where a click lands between two world ticks.
 * 2. Clock: the tick number goes up by one.
 * 3. Scheduled ticks that are due (schedule.ts).
 * 4. Block events and 5. moving blocks: empty until the parts that use them register (`World.onPhase`).
 * 6. Record: which shown states changed. Breakpoints, samples and history hang off this in simulation.ts and
 *    history.ts.
 *
 * A neighbour update is delivered at once and depth first, in the order the part that changed gives. Everything that
 * changes is in `cells`, the queue and the stores, so `save` and `load` copy a whole world.
 *
 * What is simplified is written where it happens: dust is solved per network (wire.ts), and the order of updates
 * inside one game tick is the engine's own where the game's comes from a hash set.
 */
import type {
  CircuitInput,
  Diagnostic,
  DiagnosticCode,
  InputEvent,
  InputKind,
  PendingTick,
  Pos,
  RedstoneVersion,
  SimEvent,
  SimEventKind,
  SimulationOptions,
} from './api.js';
import type { ComponentHandler, Store, World } from './components/handler.js';
import { HANDLERS } from './components/index.js';
import {
  DiagnosticList,
  floatingText,
  notInVersionText,
  unknownStateText,
  unstableText,
  unsupportedText,
} from './diagnostics.js';
import { Grid, INERT, shownState } from './grid.js';
import { DOWN, EAST, NORTH, SOUTH, UP, WEST, type Dir } from './orientation.js';
import { isSolid, strongInto } from './power.js';
import { profileOf, type Profile } from './profile.js';
import { Scheduler, type Entry } from './schedule.js';

export interface EngineOptions extends SimulationOptions {
  /**
   * How many neighbour updates and scheduled ticks one game tick may deliver before it is cut short and reported as
   * `unstable` (default 100,000).
   */
  updateLimit?: number;
}

/** A copy of everything in the engine that changes: what a rewind goes back to. */
export interface Keyframe {
  readonly tick: number;
  readonly cells: Int16Array;
  readonly queue: readonly InputEvent[];
  readonly stores: ReadonlyArray<readonly [name: string, saved: unknown]>;
}

/** What the user holds each manual source at; a cell that is not listed is at 0. */
class Levels implements Store {
  private map = new Map<number, number>();

  get(at: number): number {
    return this.map.get(at) ?? 0;
  }

  set(at: number, level: number): void {
    if (level > 0) this.map.set(at, level);
    else this.map.delete(at);
  }

  save(): Map<number, number> {
    return new Map(this.map);
  }

  load(saved: unknown): void {
    this.map = new Map(saved as Map<number, number>);
  }
}

/** Thrown inside a game tick that took too many updates; the tick loop catches it. */
class Runaway extends Error {}

const clampLevel = (level: number): number =>
  Number.isFinite(level) ? Math.max(0, Math.min(15, Math.floor(level))) : 0;

export class Engine implements World {
  readonly version: RedstoneVersion;
  readonly profile: Profile;
  readonly grid: Grid;
  readonly cells: Int16Array;
  readonly step: Int32Array;
  readonly handlers: ReadonlyArray<ComponentHandler | undefined>;
  dustHidden = false;

  /** The cells whose shown state differs from the design as it was given. */
  readonly differs = new Set<number>();
  /** For every cell the last game tick wrote: the state it showed when that tick began. */
  readonly touched = new Map<number, number>();
  /** The last game tick in numbers: the neighbour updates, inputs and scheduled ticks that reached a part. */
  readonly stats = { updates: 0 };
  /** Whether the last game tick was cut short (`unstable`). */
  cut = false;

  private clock = 0;
  private queue: InputEvent[] = [];
  private events: SimEvent[] = [];
  private readonly stores = new Map<string, Store>();
  private readonly scheduler: Scheduler;
  private readonly levels: Levels;
  private readonly notes = new DiagnosticList();
  /** The scheduler's identity of each block id (handler.tickKey). */
  private readonly tickKeys = new Int16Array(256);
  private readonly phases: readonly [Array<() => void>, Array<() => void>] = [[], []];
  private readonly busy: Array<() => boolean> = [];
  /** The cells that hold a part with a handler. */
  private readonly parts = new Set<number>();
  /** How many cells hold a part that watches its neighbours (1.12.2 observers). */
  private watchers = 0;
  private readonly updateLimit: number;
  /** Updates per cell in the second half of a tick's budget: where a runaway tick is reported. */
  private readonly heat = new Map<number, number>();

  constructor(input: CircuitInput, opts: EngineOptions = {}) {
    this.version = input.version;
    this.profile = profileOf(input.version);
    this.grid = new Grid(input);
    this.cells = this.grid.cells;
    this.step = this.grid.step;
    this.updateLimit = Math.max(1, Math.floor(opts.updateLimit ?? 100_000));

    const handlers = new Array<ComponentHandler | undefined>(256).fill(undefined);
    for (let id = 0; id < 256; id++) this.tickKeys[id] = id;
    for (const h of HANDLERS)
      for (const id of h.ids(this.version)) {
        handlers[id] = h;
        if (h.tickKey) this.tickKeys[id] = h.tickKey(id, this.version);
      }
    this.handlers = handlers;

    this.scheduler = this.state('ticks', () => new Scheduler(this.profile.modern, this.profile.ticksPerGameTick));
    this.levels = this.state('levels', () => new Levels());
    this.state('diagnostics', () => this.notes);
    for (const h of HANDLERS) if (h.ids(this.version).length > 0) h.init?.(this);

    this.place(input);
    this.settle(Math.max(0, Math.floor(opts.settleTicks ?? 200)));
  }

  // ------------------------------------------------------------------------------------------ the world, for parts

  get tick(): number {
    return this.clock;
  }

  set(at: number, state: number): void {
    const old = this.cells[at]!;
    if (old === state) return;
    if (!this.touched.has(at)) this.touched.set(at, this.display(at, old));
    this.cells[at] = state;
    if (this.display(at, state) === this.grid.design[at]) this.differs.delete(at);
    else this.differs.add(at);
    const was = old > 0 ? this.handlers[old >> 4] : undefined;
    const is = state > 0 ? this.handlers[state >> 4] : undefined;
    if (is && !was) this.parts.add(at);
    else if (was && !is) this.parts.delete(at);
    this.watchers += (is?.observed ? 1 : 0) - (was?.observed ? 1 : 0);
    if (this.watchers > 0) this.tellObservers(at);
  }

  solid(at: number): boolean {
    return isSolid(this, at);
  }

  writable(at: number): boolean {
    return this.grid.writable(at);
  }

  notify(at: number, from: number, sourceId: number): void {
    const state = this.cells[at]!;
    if (state <= 0) return;
    const h = this.handlers[state >> 4];
    if (!h) return;
    if (h.supported && !h.supported(this, at, state)) {
      this.count(at);
      this.drop(at, state, h.name);
      return;
    }
    if (!h.update) return;
    this.count(at);
    h.update(this, at, state, from, sourceId);
  }

  notifyAround(center: number, sourceId: number): void {
    const step = this.step;
    this.notify(center + step[WEST]!, center, sourceId);
    this.notify(center + step[EAST]!, center, sourceId);
    this.notify(center + step[DOWN]!, center, sourceId);
    this.notify(center + step[UP]!, center, sourceId);
    this.notify(center + step[NORTH]!, center, sourceId);
    this.notify(center + step[SOUTH]!, center, sourceId);
  }

  notifyAroundExcept(center: number, sourceId: number, skip: Dir): void {
    const step = this.step;
    if (skip !== WEST) this.notify(center + step[WEST]!, center, sourceId);
    if (skip !== EAST) this.notify(center + step[EAST]!, center, sourceId);
    if (skip !== DOWN) this.notify(center + step[DOWN]!, center, sourceId);
    if (skip !== UP) this.notify(center + step[UP]!, center, sourceId);
    if (skip !== NORTH) this.notify(center + step[NORTH]!, center, sourceId);
    if (skip !== SOUTH) this.notify(center + step[SOUTH]!, center, sourceId);
  }

  schedule(at: number, delay: number, priority = 0): void {
    const state = this.cells[at]!;
    if (state <= 0 || state >= INERT) return;
    this.scheduler.add(at, this.tickKeys[state >> 4]!, this.clock + delay, priority);
  }

  tickPendingNow(at: number): boolean {
    const state = this.cells[at]!;
    if (!this.profile.modern || state <= 0 || state >= INERT) return false;
    return this.scheduler.dueNow(at, this.tickKeys[state >> 4]!);
  }

  level(at: number): number {
    return this.levels.get(at);
  }

  setLevel(at: number, level: number): void {
    this.levels.set(at, clampLevel(level));
  }

  event(kind: SimEventKind, at: number, note?: string): void {
    const e: SimEvent = { kind, at: this.pos(at), tick: this.clock };
    if (note !== undefined) e.note = note;
    this.events.push(e);
  }

  diagnose(code: DiagnosticCode, at: number, text: string): void {
    this.notes.raise(code, this.pos(at), text);
  }

  state<T extends Store>(name: string, make: () => T): T {
    let store = this.stores.get(name);
    if (!store) {
      store = make();
      this.stores.set(name, store);
    }
    return store as T;
  }

  onPhase(phase: 4 | 5, run: () => void): void {
    this.phases[phase - 4]!.push(run);
  }

  onBusy(busy: () => boolean): void {
    this.busy.push(busy);
  }

  pos(at: number): Pos {
    return this.grid.pos(at);
  }

  // ------------------------------------------------------------------------------------------ the game tick

  /** Nothing is scheduled, queued or moving. */
  get idle(): boolean {
    return this.scheduler.size === 0 && this.queue.length === 0 && !this.busy.some((b) => b());
  }

  /** Queues an input for the start of the next game tick. */
  enqueue(e: InputEvent): void {
    this.queue.push(e);
  }

  /** The inputs waiting for the start of the next game tick. */
  get queued(): readonly InputEvent[] {
    return this.queue;
  }

  /** Replaces the inputs waiting for the next game tick: how a replay feeds back the ones it logged. */
  requeue(list: readonly InputEvent[]): void {
    this.queue = list.slice();
  }

  /** Runs one game tick. Returns whether any shown state is different after it. */
  stepTick(): boolean {
    this.touched.clear();
    this.heat.clear();
    this.stats.updates = 0;
    this.cut = false;
    const now = this.clock + 1;
    const inputs = this.queue;
    this.queue = [];
    let applied = 0;
    try {
      for (; applied < inputs.length; applied++) this.apply(inputs[applied]!);
      this.clock = now;
      this.scheduler.run(now, this.runEntry);
      for (const run of this.phases[0]) run();
      for (const run of this.phases[1]) run();
    } catch (err) {
      if (!(err instanceof Runaway)) throw err;
      this.overran();
      // The inputs the tick did not reach are not lost.
      this.queue = inputs.slice(applied + 1).concat(this.queue);
      this.clock = now;
    }
    for (const [at, before] of this.touched) if (this.shownAt(at) !== before) return true;
    return false;
  }

  /** The events since the last call. */
  takeEvents(): SimEvent[] {
    const out = this.events;
    this.events = [];
    return out;
  }

  // ------------------------------------------------------------------------------------------ reading

  /** The state the cell shows. */
  shownAt(at: number): number {
    return this.display(at, this.cells[at]!);
  }

  /** `Simulation.powerAt` for a cell of the box. */
  powerAt(at: number): number {
    const state = this.cells[at]!;
    if (state === 0 || state >= INERT) return 0;
    if (state > 0) {
      const h = this.handlers[state >> 4];
      if (h) return h.power ? h.power(this, at, state) : 0;
    }
    return isSolid(this, at) ? strongInto(this, at) : 0;
  }

  inputKindAt(at: number): InputKind | null {
    const state = this.cells[at]!;
    return state > 0 ? (this.handlers[state >> 4]?.inputKind?.(state, this.version) ?? null) : null;
  }

  lockedAt(at: number): boolean {
    const state = this.cells[at]!;
    return state > 0 && (this.handlers[state >> 4]?.locked?.(this, at, state) ?? false);
  }

  /** The cells of every part a lock holds, in y, z, x order. */
  lockedCells(): number[] {
    const out: number[] = [];
    for (const at of this.parts) if (this.lockedAt(at)) out.push(at);
    return out.sort((a, b) => a - b);
  }

  pendingAt(at: number): PendingTick[] {
    return this.scheduler.pendingAt(at);
  }

  diagnostics(): Diagnostic[] {
    return this.notes.list();
  }

  // ------------------------------------------------------------------------------------------ keyframes

  save(): Keyframe {
    return {
      tick: this.clock,
      cells: this.cells.slice(),
      queue: this.queue.slice(),
      stores: [...this.stores].map(([name, store]) => [name, store.save()] as const),
    };
  }

  load(k: Keyframe): void {
    const saved = new Map(k.stores);
    for (const [name, store] of this.stores) {
      if (!saved.has(name)) throw new Error(`@mineclaw/redstone: the store "${name}" is newer than the keyframe`);
      store.load(saved.get(name));
    }
    this.cells.set(k.cells);
    this.clock = k.tick;
    this.queue = k.queue.slice();
    this.events = [];
    this.touched.clear();
    this.heat.clear();
    this.stats.updates = 0;
    this.cut = false;
    this.dustHidden = false;
    this.derive();
  }

  // ------------------------------------------------------------------------------------------ inside

  private readonly runEntry = (e: Entry): void => {
    const state = this.cells[e.at]!;
    if (state <= 0 || state >= INERT) return;
    const id = state >> 4;
    // The entry was for the block that was here when it was asked for.
    if (this.tickKeys[id] !== e.key) return;
    const tick = this.handlers[id]?.tick;
    if (!tick) return;
    this.count(e.at);
    tick(this, e.at, state);
  };

  private apply(e: InputEvent): void {
    const at = this.grid.indexOf(e.at);
    if (at < 0) return;
    const state = this.cells[at]!;
    if (state <= 0) return;
    const input = this.handlers[state >> 4]?.input;
    if (!input) return;
    this.count(at);
    input(this, at, state, e);
  }

  /** One more update for a part; past the tick's budget the tick is cut short. */
  private count(at: number): void {
    const n = ++this.stats.updates;
    if (n <= this.updateLimit >> 1) return;
    this.heat.set(at, (this.heat.get(at) ?? 0) + 1);
    if (n > this.updateLimit) throw new Runaway();
  }

  /** Reports a tick that ran out of budget at the cell that took the most updates. */
  private overran(): void {
    let worst = -1;
    let most = 0;
    for (const [at, n] of this.heat)
      if (n > most || (n === most && at < worst)) {
        worst = at;
        most = n;
      }
    this.cut = true;
    this.dustHidden = false;
    if (worst >= 0) this.notes.raise('unstable', this.pos(worst), unstableText(this.updateLimit));
  }

  /** Loads the design: every part gets its start state, and the parts that cannot be simulated are reported. */
  private place(input: CircuitInput): void {
    const { cells, handlers, version, profile } = this;
    const other: RedstoneVersion = version === '1.12.2' ? 'b1.8.1' : '1.12.2';
    const own = [...new Set(input.cells.map((c) => this.grid.indexOf(c)))].sort((a, b) => a - b);

    for (const at of own) {
      const state = cells[at]!;
      if (state <= 0) continue;
      const id = state >> 4;
      if (id > profile.lastId) {
        this.notes.find('notInVersion', this.pos(at), notInVersionText(version, null));
        continue;
      }
      const h = handlers[id];
      if (!h) {
        if (profile.part[id] === 1) this.notes.find('unsupported', this.pos(at), unsupportedText());
        continue;
      }
      if (h.valid && !h.valid(state, version)) {
        if (h.valid(state, other)) this.notes.find('notInVersion', this.pos(at), notInVersionText(version, h.name));
        else this.notes.find('unsupported', this.pos(at), unknownStateText(h.name));
        cells[at] = state | INERT;
        continue;
      }
      if (h.normalize) cells[at] = h.normalize(state, version);
    }

    // Only blocks hold parts up, never other parts, so one pass over the parts finds every one that floats.
    for (const at of own) {
      const state = cells[at]!;
      if (state <= 0 || state >= INERT) continue;
      const h = handlers[state >> 4];
      if (!h?.supported || h.supported(this, at, state)) continue;
      this.notes.find('floating', this.pos(at), floatingText(h.name));
      cells[at] = state | INERT;
    }

    for (const extra of input.extras ?? []) {
      const at = this.grid.indexOf(extra.at);
      if (at < 0 || extra.level === undefined) continue;
      const state = cells[at]!;
      if (state > 0 && handlers[state >> 4]?.inputKind?.(state, version) === 'level') this.setLevel(at, extra.level);
    }
    this.derive();
  }

  /** Builds what is derived from the cells: which differ from the design, and which hold a part. */
  private derive(): void {
    const { cells, handlers, differs, parts } = this;
    const design = this.grid.design;
    differs.clear();
    parts.clear();
    this.watchers = 0;
    for (let at = 0; at < cells.length; at++) {
      const state = cells[at]!;
      if (this.display(at, state) !== design[at]) differs.add(at);
      const h = state > 0 ? handlers[state >> 4] : undefined;
      if (h) parts.add(at);
      if (h?.observed) this.watchers++;
    }
  }

  /** What a cell in `state` shows: a part may show something other than itself (a moving block, what it carries). */
  private display(at: number, state: number): number {
    if (state > 0 && state < INERT) {
      const shown = this.handlers[state >> 4]?.shown;
      if (shown) return shown(this, at, state);
    }
    return shownState(state);
  }

  /** Tells the observers beside a cell that it changed. */
  private tellObservers(at: number): void {
    const { cells, handlers, step } = this;
    for (let d = 0; d < 6; d++) {
      const n = at + step[d]!;
      const state = cells[n] ?? 0;
      if (state <= 0) continue;
      const observed = handlers[state >> 4]?.observed;
      if (observed) observed(this, n, state, at);
    }
  }

  /** A part with nothing left to hold it drops off, and its neighbours hear of it as of any removed block. */
  private drop(at: number, state: number, name: string): void {
    this.event('destroyed', at, `${name}: nothing holds it any more`);
    this.set(at, 0);
    this.notifyAround(at, this.profile.modern ? state >> 4 : 0);
  }

  /**
   * Lets the design come to rest: one start update for every part in y, z, x order, then game ticks until nothing is
   * left to do or `maxTicks` have run. Where it ends is tick 0. A circuit that never rests (a clock) starts from
   * wherever it is then; a memory cell starts in whichever state this order gives it.
   */
  private settle(maxTicks: number): void {
    try {
      for (const at of [...this.parts]) {
        const state = this.cells[at]!;
        if (state <= 0) continue;
        const h = this.handlers[state >> 4];
        if (h?.start) h.start(this, at, state);
        else if (h?.update) {
          this.count(at);
          h.update(this, at, state, at, state >> 4);
        }
      }
    } catch (err) {
      if (!(err instanceof Runaway)) throw err;
      this.overran();
    }
    for (let n = 0; n < maxTicks && !this.cut && !this.idle; n++) this.stepTick();

    const by = this.clock;
    if (by !== 0) for (const store of this.stores.values()) store.rebase?.(by);
    this.clock = 0;
    this.events = [];
    this.touched.clear();
    this.heat.clear();
    this.stats.updates = 0;
    this.cut = false;
  }
}
