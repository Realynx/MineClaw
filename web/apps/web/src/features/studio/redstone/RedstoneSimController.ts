/**
 * The redstone studio's simulation controller, outside React: it owns the simulation built from the canvas, the run loop
 * and what the canvas view shows of it, and it is all the debugger talks to (`SimControllerApi`).
 *
 * Edit mode keeps a settled simulation of the canvas for the inspector and the diagnostics, rebuilt a moment after the
 * last edit. Paused and running are live: the pointer cannot edit the canvas, clicks go to the circuit, and any change of
 * the canvas (a key, an undo, the AI's blocks, the bot's fixes) ends the simulation, so nothing runs on a stale design.
 * The simulation never changes the design: a lever flipped while it runs is as it was drawn again afterwards.
 *
 * Time comes from `frame()`, called once per animation frame. The simulated states reach the view at the display rate,
 * not once per tick: re-meshing is what costs frames, so the simulation may run faster than it is drawn.
 */
import type {
  Breakpoint,
  BreakpointHit,
  Cell,
  CreateSimulation,
  Diagnostic,
  InputEvent,
  Pos,
  RedstoneVersion,
  Sample,
  SimEvent,
  Simulation,
  StepResult,
} from '@mineclaw/redstone';
import type { Studio } from '../studio';
import type { DebuggerAuthoring } from './authoring';
import {
  DEFAULT_DISPLAY_HZ,
  DEFAULT_SIM_SPEED,
  DISPLAY_RATES,
  type CellInspection,
  type DisplayHz,
  type Probe,
  type SimControllerApi,
  type SimMode,
  type SimSnapshot,
  type SimSpeed,
  type StartLevel,
} from './simController.types';

/** How long the canvas has to rest before the edit-mode simulation is built again. */
export const REBUILD_MS = 150;
/** The most simulation work one frame takes on, so the view keeps its frame rate whatever the speed. */
export const SLICE_MS = 4;
/** The most game ticks one frame runs, and where "run until the next change" gives up. */
export const RUN_CAP_TICKS = 2000;
/** The most dust cells that get their power drawn on them. */
export const MAX_POWER_LABELS = 1024;

/** Canvas y of the plot's grass: everything from here down is ground unless it is dug or built over. */
const GROUND_TOP = -1;
const DUST = 55;
const MAX_EVENTS = 200;
/** A frame that took longer than this (a hidden tab, a long task) owes no more ticks than this much time. */
const MAX_FRAME_MS = 250;
/** A simulation the engine cannot run at the chosen speed falls behind by at most this much, then drops ticks. */
const MAX_BACKLOG_S = 0.25;
const OWED_SLACK = 1e-9;
const MAX_CHUNK = 256;
/** How often the frame rate is looked at while running. */
const RATE_CHECK_MS = 1000;
/** Below this the display rate is halved. */
const LOW_FPS = 30;
/** At this (the viewer's own target) for `RATE_RECOVER_MS`, the display rate goes one step back up. */
const GOOD_FPS = 40;
const RATE_RECOVER_MS = 5000;

const PROBE_COLORS = [0x38bdf8, 0xfacc15, 0x4ade80, 0xf472b6, 0xa78bfa, 0xfb923c, 0x2dd4bf, 0xe2e8f0];
const BREAKPOINT_COLOR = 0xef4444;
const BREAKPOINT_OFF_COLOR = 0x7f1d1d;

/** A probe's or a breakpoint's cell, marked on the canvas view. */
export interface SimMarker {
  at: Pos;
  kind: 'probe' | 'breakpoint';
  /** 0xRRGGBB. */
  color: number;
}

/** What is drawn over the circuit. */
export interface SimMarks {
  /**
   * Powered dust as runs of `x, y, z, level`: the first `levelCount` of them. Empty in edit mode. The buffer is reused, so
   * it is only good during the call.
   */
  levels: Int16Array;
  levelCount: number;
  /** The repeaters a lock holds, each with its meta (which says how it lies). Empty in edit mode. */
  locks: ReadonlyArray<readonly [x: number, y: number, z: number, meta: number]>;
  /** Probes and breakpoints, in edit mode too. */
  markers: readonly SimMarker[];
}

/** Where the controller shows the simulation. */
export interface SimDisplay {
  /** Draws these states instead of the canvas's own, and no others; null takes them all away again. */
  cells(cells: readonly Cell[] | null): void;
  marks?(marks: SimMarks): void;
}

export interface SimControllerOptions {
  studio: Pick<Studio, 'canvas' | 'onCells'>;
  version: RedstoneVersion;
  /** The engine (tests pass a scripted one). */
  create: CreateSimulation;
  display: SimDisplay;
  /** Milliseconds on a clock that only runs forward. */
  now?: () => number;
}

const NO_EVENTS: readonly SimEvent[] = [];
const NO_HITS: readonly BreakpointHit[] = [];
const NO_DIAGNOSTICS: readonly Diagnostic[] = [];
const NO_LOCKS: SimMarks['locks'] = [];

const keyOf = (p: Pos): string => `${p[0]},${p[1]},${p[2]}`;
const samePos = (a: Pos, b: Pos): boolean => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];
const copyPos = (p: Pos): Pos => [p[0], p[1], p[2]];
const textOf = (e: unknown): string => (e instanceof Error ? e.message : String(e));

function sameDiagnostics(a: readonly Diagnostic[], b: readonly Diagnostic[]): boolean {
  if (a === b) return true;
  if (a.length !== b.length) return false;
  return a.every((d, i) => d.code === b[i]!.code && d.text === b[i]!.text && samePos(d.at, b[i]!.at));
}

/** How many game ticks the next engine call runs: about a millisecond's worth, so a frame's slice is cut in good time. */
function nextChunk(chunk: number, ms: number, ticks: number): number {
  if (ms <= 0) return Math.min(MAX_CHUNK, chunk * 2);
  return Math.max(1, Math.min(MAX_CHUNK, Math.floor(ticks / ms)));
}

/** One step along the display rates (5, 10, 20), never past `limit` going up. */
function stepRate(hz: DisplayHz, by: -1 | 1, limit: DisplayHz): DisplayHz {
  const i = Math.max(0, DISPLAY_RATES.indexOf(hz) + by);
  return DISPLAY_RATES[Math.min(i, DISPLAY_RATES.indexOf(limit))] ?? hz;
}

export class RedstoneSimController implements SimControllerApi {
  private readonly studio: SimControllerOptions['studio'];
  private readonly version: RedstoneVersion;
  private readonly create: CreateSimulation;
  private readonly display: SimDisplay;
  private readonly now: () => number;
  private readonly listeners = new Set<() => void>();

  /** In edit mode: the settled design, or null while it is stale or could not be built. Never null while live. */
  private sim: Simulation | null = null;
  private mode: SimMode = 'edit';
  /** When the design last changed with the simulation not rebuilt since; null when the simulation matches it. */
  private staleSince: number | null;
  private error: string | null = null;

  private speed: SimSpeed = DEFAULT_SIM_SPEED;
  private displayHz: DisplayHz = DEFAULT_DISPLAY_HZ;
  private displayHzNow: DisplayHz = DEFAULT_DISPLAY_HZ;
  private probes: readonly Probe[] = [];
  private breakpoints: readonly Breakpoint[] = [];
  private markers: readonly SimMarker[] = [];
  /** What the design's containers and analog sources hold at the start. */
  private readonly extras = new Map<string, StartLevel>();
  private levels: readonly StartLevel[] = [];
  private events: readonly SimEvent[] = NO_EVENTS;
  private lastHits: readonly BreakpointHit[] = NO_HITS;
  private diagnostics: readonly Diagnostic[] = NO_DIAGNOSTICS;

  /** "Run until the next change": the game ticks it may still take. */
  private goal: { left: number } | null = null;
  /** Game ticks the running clock owes, with the fraction of the next one. */
  private owed = 0;
  private chunk = 1;
  /** Plates stepped on or off since the last tick: their state is not in the simulation yet. */
  private readonly holds = new Map<string, boolean>();
  private frameAt: number | null = null;

  /** The view shows an older tick than the simulation is at. */
  private dirty = false;
  private shownAt = -Infinity;
  private unpublished = false;
  private publishedAt = -Infinity;
  private fps = 0;
  private rateCheckedAt = 0;
  private goodSince: number | null = null;

  /** The canvas's dust cells, the first `MAX_POWER_LABELS` of them. */
  private dust: Pos[] = [];
  private readonly labels = new Int16Array(MAX_POWER_LABELS * 4);
  private snap: SimSnapshot;

  constructor(o: SimControllerOptions) {
    this.studio = o.studio;
    this.version = o.version;
    this.create = o.create;
    this.display = o.display;
    this.now = o.now ?? (() => performance.now());
    this.staleSince = this.now();
    this.snap = this.build();
  }

  // ------------------------------------------------------------------ the page's side

  /** Paused or running: the canvas is locked for the pointer and clicks go to the circuit. */
  get live(): boolean {
    return this.mode !== 'edit';
  }

  /** Why the simulator could not build or run this design, until it works again. */
  get failure(): string | null {
    return this.error;
  }

  /** Starts following the canvas. The function it returns stops that and takes the simulation off the view. */
  connect(): () => void {
    const off = this.studio.onCells(this.designChanged);
    this.staleSince = this.now();
    this.mark();
    return () => {
      off();
      if (this.mode === 'edit') return;
      this.leave();
      this.publish();
    };
  }

  /** The viewer's frame rate as the studio reads it (about twice a second). */
  setFps(fps: number): void {
    this.fps = fps;
  }

  /** One animation frame: the edit-mode rebuild when it is due, the ticks a running simulation owes, then the view. */
  frame(): void {
    const now = this.now();
    const elapsed = this.frameAt === null ? 0 : Math.min(MAX_FRAME_MS, Math.max(0, now - this.frameAt));
    this.frameAt = now;
    if (this.mode === 'edit') {
      if (this.staleSince !== null && now - this.staleSince >= REBUILD_MS) this.rebuild();
      return;
    }
    if (this.mode === 'running') {
      this.run(elapsed);
      this.watchFrameRate(now);
    }
    this.show(now);
    // While it runs the debugger hears no more often than the view is redrawn; a stop is told at once.
    if (this.unpublished && (this.mode !== 'running' || now - this.publishedAt >= 1000 / this.displayHzNow))
      this.publish();
  }

  // ------------------------------------------------------------------ SimControllerApi: the store

  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  };

  snapshot = (): SimSnapshot => this.snap;

  // ------------------------------------------------------------------ SimControllerApi: running

  start = (): void => {
    if (this.mode === 'running' && !this.goal) return;
    if (!this.enter()) return;
    this.mode = 'running';
    this.goal = null;
    this.owed = 0;
    this.after();
  };

  pause = (): void => {
    if (this.mode !== 'running') return;
    this.settle();
    this.after();
  };

  stop = (): void => {
    if (this.mode === 'edit') return;
    this.leave();
    // What stays for the inspector is the settled design again.
    const sim = this.sim;
    if (sim && !this.guard(() => sim.reset())) return;
    this.refreshDiagnostics();
    this.publish();
  };

  stepGameTick = (): void => this.step((sim) => sim.stepGameTick());

  stepRedstoneTick = (): void => this.step((sim) => sim.stepRedstoneTick());

  stepBack = (): void => this.rewind(1);

  stepBackTo = (tick: number): void => {
    if (this.sim && Number.isInteger(tick)) this.rewind(this.sim.tick - tick);
  };

  runUntilChange = (): void => {
    const sim = this.enter();
    if (!sim) return;
    this.settle();
    // An idle circuit has nothing scheduled, queued or moving: no number of ticks would change it.
    if (!sim.idle) {
      this.mode = 'running';
      this.goal = { left: RUN_CAP_TICKS };
    }
    this.after();
  };

  /** Back to tick 0, paused. */
  reset = (): void => {
    const sim = this.sim;
    if (this.mode === 'edit' || !sim) return;
    this.settle();
    const r = this.guard(() => sim.reset());
    if (!r) return;
    this.absorb(r);
    this.events = NO_EVENTS;
    this.refreshDiagnostics();
    this.after();
  };

  setSpeed = (speed: SimSpeed): void => {
    if (speed !== 'max' && !(Number.isFinite(speed) && speed > 0)) return;
    if (speed === this.speed) return;
    this.speed = speed;
    this.owed = 0;
    this.publish();
  };

  setDisplayHz = (hz: DisplayHz): void => {
    if (!DISPLAY_RATES.includes(hz) || (hz === this.displayHz && hz === this.displayHzNow)) return;
    this.displayHz = hz;
    this.displayHzNow = hz;
    this.goodSince = null;
    this.rateCheckedAt = this.now();
    this.publish();
  };

  // ------------------------------------------------------------------ SimControllerApi: inputs

  /**
   * Uses the lever, button or pressure plate at a cell; from edit mode the simulation is entered, paused, first. False for
   * anything else, a container or an analog source included (those are set with `setLevel`).
   */
  click = (at: Pos): boolean => {
    const kind = this.current()?.inputAt(at);
    if (kind !== 'lever' && kind !== 'button' && kind !== 'plate') return false;
    const sim = this.enter();
    if (!sim) return false;
    sim.input(this.inputFor(kind, at, sim));
    this.after();
    return true;
  };

  /**
   * In edit mode this is part of the design: what the container or the analog source holds when the circuit starts.
   * While the simulation is live it only changes the running circuit, like a lever flipped there.
   */
  setLevel = (at: Pos, level: number): void => {
    const sim = this.current();
    if (!sim || sim.inputAt(at) !== 'level' || !Number.isFinite(level)) return;
    const value = Math.max(0, Math.min(15, Math.round(level)));
    if (this.mode !== 'edit') {
      sim.input({ kind: 'level', at: copyPos(at), level: value });
      this.after();
      return;
    }
    const k = keyOf(at);
    if ((this.extras.get(k)?.level ?? 0) === value) return;
    if (value === 0) this.extras.delete(k);
    else this.extras.set(k, { at: copyPos(at), level: value });
    this.levels = [...this.extras.values()];
    this.designChanged();
    // The design kept a new start level, also when nothing else of the snapshot moved.
    if (this.snap.levels !== this.levels) this.publish();
  };

  /**
   * Puts back what a saved design keeps of the debugger (./authoring.ts): its probes, breakpoints, start levels, speed and
   * redraw rate. A live simulation ends, since its start levels may no longer be the design's.
   */
  restore = (state: DebuggerAuthoring): void => {
    if (this.mode !== 'edit') this.leave();
    this.probes = state.probes.map((p) => ({ ...p, at: copyPos(p.at) }));
    this.breakpoints = state.breakpoints.map((b) => ({ ...b, at: copyPos(b.at) }));
    this.extras.clear();
    for (const l of state.levels) this.extras.set(keyOf(l.at), { at: copyPos(l.at), level: l.level });
    this.levels = [...this.extras.values()];
    if (state.speed === 'max' || (Number.isFinite(state.speed) && state.speed > 0)) this.speed = state.speed;
    if (DISPLAY_RATES.includes(state.displayHz)) this.displayHz = this.displayHzNow = state.displayHz;
    this.sim = null;
    this.staleSince = this.now();
    this.remark();
    this.publish();
  };

  // ------------------------------------------------------------------ SimControllerApi: probes and breakpoints

  addProbe = (at: Pos): void => {
    if (this.probes.some((p) => samePos(p.at, at))) return;
    const n = 1 + Math.max(0, ...this.probes.map((p) => Number(/^p(\d+)$/.exec(p.id)?.[1] ?? 0)));
    const color = PROBE_COLORS[(n - 1) % PROBE_COLORS.length]!;
    this.probes = [...this.probes, { id: `p${n}`, at: copyPos(at), label: `P${n}`, color }];
    this.probesChanged();
  };

  removeProbe = (id: string): void => {
    const next = this.probes.filter((p) => p.id !== id);
    if (next.length === this.probes.length) return;
    this.probes = next;
    this.probesChanged();
  };

  renameProbe = (id: string, label: string): void => {
    const text = label.trim().slice(0, 40);
    if (!text || !this.probes.some((p) => p.id === id && p.label !== text)) return;
    this.probes = this.probes.map((p) => (p.id === id ? { ...p, label: text } : p));
    this.publish();
  };

  setBreakpoint = (b: Breakpoint): void => {
    const own: Breakpoint = { ...b, at: copyPos(b.at) };
    const known = this.breakpoints.some((x) => x.id === b.id);
    this.breakpoints = known ? this.breakpoints.map((x) => (x.id === b.id ? own : x)) : [...this.breakpoints, own];
    this.breakpointsChanged();
  };

  removeBreakpoint = (id: string): void => {
    const next = this.breakpoints.filter((b) => b.id !== id);
    if (next.length === this.breakpoints.length) return;
    this.breakpoints = next;
    this.breakpointsChanged();
  };

  // ------------------------------------------------------------------ SimControllerApi: reading

  timeline = (probeId: string, fromTick: number, toTick: number): readonly Sample[] => {
    const probe = this.probes.find((p) => p.id === probeId);
    return probe && this.sim ? this.sim.trace(probe.at, fromTick, toTick) : [];
  };

  inspect = (at: Pos): CellInspection | null => {
    const sim = this.sim;
    if (!sim) return null;
    return { sample: sim.sample(at), locked: sim.lockedAt(at), pending: sim.pendingAt(at), explain: sim.explain(at) };
  };

  // ------------------------------------------------------------------ the simulation and its modes

  private build(): SimSnapshot {
    const sim = this.sim;
    return {
      mode: this.mode,
      tick: sim?.tick ?? 0,
      idle: sim?.idle ?? true,
      speed: this.speed,
      displayHz: this.displayHz,
      displayHzNow: this.displayHzNow,
      canStepBack: this.mode === 'edit' ? 0 : (sim?.historyDepth ?? 0),
      probes: this.probes,
      breakpoints: this.breakpoints,
      levels: this.levels,
      lastHits: this.lastHits,
      events: this.events,
      diagnostics: this.diagnostics,
      version: this.version,
    };
  }

  private publish(): void {
    this.unpublished = false;
    this.publishedAt = this.now();
    this.snap = this.build();
    for (const l of this.listeners) l();
  }

  /** After something the user did: it shows and is told at once (the display rate still holds for the view). */
  private after(): void {
    this.show(this.now());
    this.publish();
  }

  /** The canvas or a start level changed: the simulation is of an older design, and a live one ends here. */
  private readonly designChanged = (): void => {
    const live = this.mode !== 'edit';
    this.sim = null;
    this.staleSince = this.now();
    if (live) this.leave();
    if (live || !this.snap.idle) this.publish();
  };

  /** Builds the simulation of the canvas as it is now. False when the engine could not. */
  private rebuild(): boolean {
    this.staleSince = null;
    const canvas = this.studio.canvas;
    const cells = canvas.cells();
    this.dust = [];
    for (const c of cells) if (c[3] === DUST && this.dust.length < MAX_POWER_LABELS) this.dust.push([c[0], c[1], c[2]]);
    let sim: Simulation | null;
    try {
      sim = this.create({
        version: this.version,
        cells,
        ground: { topY: GROUND_TOP, dug: canvas.holes() },
        ...(this.extras.size > 0 ? { extras: [...this.extras.values()] } : {}),
      });
      sim.setBreakpoints(this.breakpoints);
      sim.watch(this.probes.map((p) => p.at));
      this.error = null;
    } catch (e) {
      sim = null;
      this.error = textOf(e);
    }
    this.sim = sim;
    this.events = NO_EVENTS;
    this.lastHits = NO_HITS;
    this.refreshDiagnostics();
    this.publish();
    return sim !== null;
  }

  /** The simulation of the canvas as it is now; edit mode builds it when it is missing or stale. */
  private current(): Simulation | null {
    if (this.mode === 'edit' && (this.sim === null || this.staleSince !== null)) this.rebuild();
    return this.sim;
  }

  /** The live simulation, entered (paused, at tick 0) from edit mode. Null when it cannot be built. */
  private enter(): Simulation | null {
    if (this.mode !== 'edit') return this.sim;
    const sim = this.current();
    if (!sim) return null;
    this.mode = 'paused';
    this.displayHzNow = this.displayHz;
    this.rateCheckedAt = this.now();
    this.goodSince = null;
    // The settled state is not what the canvas shows (dust has its level, torches are out): it goes up at once.
    this.dirty = true;
    this.shownAt = -Infinity;
    return sim;
  }

  /** Running → paused. */
  private settle(): void {
    if (this.mode !== 'running') return;
    this.mode = 'paused';
    this.goal = null;
    this.owed = 0;
    this.refreshDiagnostics();
    this.unpublished = true;
  }

  /** Live → edit: the simulated states leave the view; probes and breakpoints stay marked. */
  private leave(): void {
    this.mode = 'edit';
    this.goal = null;
    this.owed = 0;
    this.dirty = false;
    this.holds.clear();
    this.events = NO_EVENTS;
    this.lastHits = NO_HITS;
    this.display.cells(null);
    this.mark();
  }

  /** An engine call. A throw ends the simulation and is kept as `failure`; the page goes on. */
  private guard<T>(call: () => T): T | null {
    try {
      return call();
    } catch (e) {
      this.error = textOf(e);
      this.sim = null;
      this.staleSince = null;
      this.diagnostics = NO_DIAGNOSTICS;
      if (this.mode !== 'edit') this.leave();
      this.publish();
      return null;
    }
  }

  private refreshDiagnostics(): void {
    const next = this.sim?.diagnostics() ?? NO_DIAGNOSTICS;
    if (sameDiagnostics(this.diagnostics, next)) return;
    this.diagnostics = next;
    this.unpublished = true;
  }

  // ------------------------------------------------------------------ stepping

  /** What a stepping call did: the view is behind, its events and hits are news, queued plate states are in. */
  private absorb(r: StepResult): void {
    if (r.changed.length > 0) this.dirty = true;
    if (r.events.length > 0) this.events = [...this.events, ...r.events].slice(-MAX_EVENTS);
    if (r.hits.length > 0 || this.lastHits.length > 0) this.lastHits = r.hits;
    this.holds.clear();
    this.unpublished = true;
  }

  /** One step by hand; from edit mode the simulation is entered first, and a running one is paused. */
  private step(call: (sim: Simulation) => StepResult): void {
    const sim = this.enter();
    if (!sim) return;
    this.settle();
    const r = this.guard(() => call(sim));
    if (!r) return;
    this.absorb(r);
    this.refreshDiagnostics();
    this.after();
  }

  private rewind(ticks: number): void {
    const sim = this.sim;
    if (this.mode === 'edit' || !sim || ticks < 1 || ticks > sim.historyDepth) return;
    this.settle();
    const r = this.guard(() => sim.stepBack(ticks));
    if (!r) return;
    this.absorb(r);
    // The engine dropped what came after the new tick; so does the list here.
    if (this.events.some((e) => e.tick > r.tick)) this.events = this.events.filter((e) => e.tick <= r.tick);
    this.refreshDiagnostics();
    this.after();
  }

  /** The ticks a running simulation owes this frame, cut off after `SLICE_MS` of work. */
  private run(elapsed: number): void {
    const sim = this.sim;
    if (!sim) return;
    const goal = this.goal;
    let due = RUN_CAP_TICKS;
    if (goal) due = goal.left;
    else if (this.speed !== 'max') {
      const backlog = Math.max(1, this.speed * MAX_BACKLOG_S);
      this.owed = Math.min(this.owed + (elapsed * this.speed) / 1000, backlog);
      // Ten frames of a tenth of a tick add up to a hair under one: the hair must not cost a frame.
      due = Math.floor(this.owed + OWED_SLACK);
    }
    due = Math.min(due, RUN_CAP_TICKS);
    const began = this.now();
    let ran = 0;
    while (ran < due) {
      const want = Math.min(due - ran, this.chunk);
      const before = this.now();
      const r = this.guard(() => sim.run(want, goal ? 'change' : 'breakpoint'));
      if (!r) return;
      const after = this.now();
      ran += r.ranTicks;
      this.absorb(r);
      if (goal) goal.left -= r.ranTicks;
      // A breakpoint hit, a tick the engine cut short, or what "run until the next change" was waiting for.
      const reached = goal !== null && (r.changed.length > 0 || r.idle || goal.left <= 0);
      if (r.hits.length > 0 || r.ranTicks < want || reached) {
        this.settle();
        break;
      }
      this.chunk = nextChunk(this.chunk, after - before, r.ranTicks);
      if (after - began >= SLICE_MS) break;
    }
    if (!goal && this.speed !== 'max') this.owed = Math.max(0, this.owed - ran);
  }

  /** Halves the display rate while the frame rate is low, and gives it back, slowly, once it has recovered. */
  private watchFrameRate(now: number): void {
    if (now - this.rateCheckedAt < RATE_CHECK_MS) return;
    this.rateCheckedAt = now;
    let next = this.displayHzNow;
    if (this.fps > 0 && this.fps < LOW_FPS) {
      this.goodSince = null;
      next = stepRate(next, -1, this.displayHz);
    } else if (this.fps >= GOOD_FPS && next !== this.displayHz) {
      this.goodSince ??= now;
      if (now - this.goodSince >= RATE_RECOVER_MS) {
        this.goodSince = now;
        next = stepRate(next, 1, this.displayHz);
      }
    } else this.goodSince = null;
    if (next === this.displayHzNow) return;
    this.displayHzNow = next;
    this.unpublished = true;
  }

  private inputFor(kind: 'lever' | 'button' | 'plate', at: Pos, sim: Simulation): InputEvent {
    if (kind === 'lever') return { kind: 'toggle', at: copyPos(at) };
    if (kind === 'button') return { kind: 'press', at: copyPos(at) };
    const k = keyOf(at);
    const on = !(this.holds.get(k) ?? sim.powerAt(at) > 0);
    this.holds.set(k, on);
    return { kind: 'hold', at: copyPos(at), on };
  }

  // ------------------------------------------------------------------ the view

  /** Sends the simulated states to the view when they changed and the display rate allows another redraw. */
  private show(now: number): void {
    const sim = this.sim;
    if (!this.dirty || !sim || this.mode === 'edit' || now - this.shownAt < 1000 / this.displayHzNow) return;
    this.dirty = false;
    this.shownAt = now;
    this.display.cells(sim.displayCells());
    this.mark();
    this.refreshDiagnostics();
  }

  /** What is drawn over the circuit now: the dust's power and the held repeaters while live, the markers always. */
  private mark(): void {
    if (!this.display.marks) return;
    const sim = this.mode === 'edit' ? null : this.sim;
    let n = 0;
    let locks = NO_LOCKS;
    if (sim) {
      for (const at of this.dust) {
        const state = sim.stateAt(at);
        if (state >> 4 !== DUST || (state & 15) === 0) continue;
        const o = n * 4;
        this.labels[o] = at[0];
        this.labels[o + 1] = at[1];
        this.labels[o + 2] = at[2];
        this.labels[o + 3] = state & 15;
        n++;
      }
      const held = sim.lockedCells();
      if (held.length > 0) locks = held.map((at) => [at[0], at[1], at[2], sim.stateAt(at) & 15] as const);
    }
    this.display.marks({ levels: this.labels, levelCount: n, locks, markers: this.markers });
  }

  private remark(): void {
    this.markers = [
      ...this.probes.map((p): SimMarker => ({ at: p.at, kind: 'probe', color: p.color })),
      ...this.breakpoints.map((b): SimMarker => ({
        at: b.at,
        kind: 'breakpoint',
        color: b.enabled ? BREAKPOINT_COLOR : BREAKPOINT_OFF_COLOR,
      })),
    ];
    this.mark();
  }

  private probesChanged(): void {
    this.sim?.watch(this.probes.map((p) => p.at));
    this.remark();
    this.publish();
  }

  private breakpointsChanged(): void {
    this.sim?.setBreakpoints(this.breakpoints);
    this.remark();
    this.publish();
  }
}
