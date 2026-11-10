/**
 * The redstone studio's simulation controller against a scripted engine (`FakeSimulation`): the edit-mode rebuild, the
 * modes and what moves between them, the run loop's speed and its slice of a frame, the display rate and its back-off, the
 * pointer while a simulation is live, and the rule that any change of the canvas ends it. Then the layer drawn over the
 * circuit, and the hook that ties the controller to the studio page. Time is a number the tests move: nothing sleeps.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerCell, type DesignerSession } from '@mineclaw/contracts';
import type { Breakpoint, Cell, CreateSimulation, Diagnostic, Pos } from '@mineclaw/redstone';
import { fakeSimulations, type FakeOptions, type FakeScript } from '@mineclaw/redstone/testing';
import type { AimTarget, WorldViewerHandle } from '@mineclaw/world-viewer';
import { act, renderHook } from '@testing-library/react';
import { type BufferAttribute, type InstancedMesh, Matrix4, type Mesh, Scene, Vector3 } from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { MAX_LOCKS, MAX_MARKERS, PowerOverlay } from '@/features/studio/redstone/powerOverlay';
import {
  MAX_POWER_LABELS,
  REBUILD_MS,
  RedstoneSimController,
  RUN_CAP_TICKS,
  SLICE_MS,
  type SimMarker,
  type SimMarks,
} from '@/features/studio/redstone/RedstoneSimController';
import { useRedstoneSim } from '@/features/studio/redstone/useRedstoneSim';
import { Studio, type StudioApi } from '@/features/studio/studio';
import type { StudioWorld } from '@/features/studio/studioWorld';

const LEVER: Pos = [2, 0, 2];
const DUST: Pos = [3, 0, 2];
const TORCH: Pos = [5, 0, 2];
const BUTTON: Pos = [2, 1, 4];
const PLATE: Pos = [6, 0, 6];
const CHEST: Pos = [8, 0, 8];
/** A lever, dust, a block and a torch on its far side. */
const BLOCKS: DesignerCell[] = [
  [2, 0, 2, 69, 5],
  [3, 0, 2, 55, 0],
  [4, 0, 2, 1, 0],
  [5, 0, 2, 76, 1],
];
const TORCH_OUT: Cell = [5, 0, 2, 75, 1];
/** A circuit that is never idle and never shows anything new. */
const busy: FakeScript = () => ({ idle: false });

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'not gate',
  rev: 0,
  blocks: BLOCKS,
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
  ...over,
});

const api: StudioApi = {
  message: () => Promise.reject(new Error('n/a')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('n/a')),
};

/** The clock the controller reads. A script may move it to stand for the time its ticks take. */
const clock = { t: 0 };
const studios: Studio[] = [];
beforeEach(() => {
  clock.t = 0;
});
afterEach(() => {
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

interface Marks {
  levels: number[];
  locks: SimMarks['locks'];
  markers: readonly SimMarker[];
}

function mount(
  o: { script?: FakeScript; options?: FakeOptions; session?: Partial<DesignerSession>; create?: CreateSimulation } = {},
) {
  const studio = new Studio(session(o.session), api, 60_000);
  studios.push(studio);
  const fake = fakeSimulations(o.script, o.options);
  const shown: Array<readonly Cell[] | null> = [];
  const marks: Marks[] = [];
  const c = new RedstoneSimController({
    studio,
    version: '1.12.2',
    create: o.create ?? fake.create,
    now: () => clock.t,
    display: {
      cells: (cells) => shown.push(cells),
      marks: (m) =>
        marks.push({ levels: Array.from(m.levels.subarray(0, m.levelCount * 4)), locks: m.locks, markers: m.markers }),
    },
  });
  const off = c.connect();
  // The page's animation frame is always running: the first one is at time 0.
  c.frame();
  /** One animation frame, `ms` after the last. */
  const frame = (ms = 16) => {
    clock.t += ms;
    c.frame();
  };
  return {
    studio,
    c,
    off,
    shown,
    marks,
    made: fake.made,
    /** The engine the controller built last. */
    sim: () => fake.made[fake.made.length - 1]!,
    snap: () => c.snapshot(),
    frame,
    /** Frames `step` ms apart for `ms` in all. */
    run: (ms: number, step = 10) => {
      for (let passed = 0; passed < ms; passed += step) frame(step);
    },
  };
}

describe('the simulation of the canvas in edit mode', () => {
  it('is built a moment after the last edit, from the canvas as it is, on the plot’s ground', () => {
    const h = mount({ session: { dug: [[9, -1, 9]] } });
    h.frame(REBUILD_MS - 1);
    expect(h.made).toHaveLength(0);
    expect(h.c.inspect(LEVER)).toBeNull();
    h.frame(1);
    expect(h.made).toHaveLength(1);
    expect(h.sim().circuit).toEqual({ version: '1.12.2', cells: BLOCKS, ground: { topY: -1, dug: [[9, -1, 9]] } });
    expect(h.snap()).toMatchObject({ mode: 'edit', tick: 0, version: '1.12.2', canStepBack: 0 });
    expect(h.c.inspect(LEVER)).not.toBeNull();

    // An edit makes it stale at once; a second edit starts the wait again.
    h.studio.edit([[6, 0, 2, 55, 0]], 'place', 'dust');
    expect(h.c.inspect(LEVER)).toBeNull();
    h.frame(100);
    h.studio.edit([[7, 0, 2, 55, 0]], 'place', 'dust');
    h.frame(REBUILD_MS - 1);
    expect(h.made).toHaveLength(1);
    h.frame(1);
    expect(h.made).toHaveLength(2);
    expect(h.sim().circuit.cells).toEqual([...BLOCKS, [6, 0, 2, 55, 0], [7, 0, 2, 55, 0]]);

    // It is built once per change, and edit mode shows the canvas as drawn.
    h.run(1000);
    expect(h.made).toHaveLength(2);
    expect(h.shown).toEqual([]);
    expect(h.c.live).toBe(false);
  });

  it('reports what the engine has to say about the design, and keeps its snapshot until something changes', () => {
    const diagnostics: Diagnostic[] = [{ at: DUST, code: 'floating', text: 'the dust has nothing to stand on' }];
    const h = mount({ options: { diagnostics } });
    const heard = vi.fn();
    const off = h.c.subscribe(heard);
    const before = h.snap();
    h.frame(REBUILD_MS);
    expect(heard).toHaveBeenCalledTimes(1);
    const built = h.snap();
    expect(built).not.toBe(before);
    expect(built.diagnostics).toEqual(diagnostics);
    h.run(500);
    expect(heard).toHaveBeenCalledTimes(1);
    expect(h.snap()).toBe(built);
    off();
    h.c.start();
    expect(heard).toHaveBeenCalledTimes(1);
  });

  it('leaves the page working when the engine cannot build the design, and says why', () => {
    const fake = fakeSimulations();
    let broken = true;
    const create = vi.fn<CreateSimulation>((input, opts) => {
      if (broken) throw new Error('the engine is not written yet');
      return fake.create(input, opts);
    });
    const h = mount({ create });
    h.frame(REBUILD_MS);
    expect(h.c.failure).toBe('the engine is not written yet');
    expect(h.snap().mode).toBe('edit');
    // It is not tried again on every frame, only when the user asks or the design changes.
    h.run(1000);
    expect(create).toHaveBeenCalledTimes(1);
    h.c.start();
    h.c.stepGameTick();
    expect(create).toHaveBeenCalledTimes(3);
    expect(h.c.live).toBe(false);
    expect(h.shown).toEqual([]);
    broken = false;
    h.c.start();
    expect(h.c.live).toBe(true);
    expect(h.c.failure).toBeNull();
  });
});

describe('the modes', () => {
  it('start enters the simulation with a fresh build, pause holds it, stop gives the canvas view back', () => {
    const h = mount({ script: { 0: { cells: [TORCH_OUT] } } });
    h.c.start();
    // No waiting for the edit-mode rebuild: the simulation is built there and then, and its start state shows at once.
    expect(h.made).toHaveLength(1);
    expect(h.snap()).toMatchObject({ mode: 'running', tick: 0 });
    expect(h.c.live).toBe(true);
    expect(h.shown).toEqual([[TORCH_OUT]]);

    h.c.pause();
    expect(h.snap().mode).toBe('paused');
    expect(h.c.live).toBe(true);
    h.c.start();
    expect(h.snap().mode).toBe('running');
    h.run(300);
    expect(h.made).toHaveLength(1);
    expect(h.sim().tick).toBe(3);

    h.c.stop();
    expect(h.snap()).toMatchObject({ mode: 'edit', tick: 0, canStepBack: 0 });
    expect(h.c.live).toBe(false);
    expect(h.shown.at(-1)).toBeNull();
    // What stays for the inspector is the settled design, not the tick the run stopped at.
    expect(h.sim().tick).toBe(0);

    const pushes = h.shown.length;
    h.c.stop();
    h.c.pause();
    h.run(500);
    expect(h.snap().mode).toBe('edit');
    expect(h.shown).toHaveLength(pushes);
    expect(h.made).toHaveLength(1);
  });

  it('a step from edit mode enters paused first, a step while running pauses, and history steps back', () => {
    const h = mount({ script: (tick) => ({ cells: [[3, 0, 2, 55, tick % 16]], idle: false }) });
    h.c.stepGameTick();
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 1 });
    h.c.stepRedstoneTick();
    expect(h.snap().tick).toBe(3);
    h.c.start();
    h.c.stepGameTick();
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 4, canStepBack: 4 });

    h.c.stepBack();
    expect(h.snap().tick).toBe(3);
    h.c.stepBackTo(1);
    expect(h.snap()).toMatchObject({ tick: 1, canStepBack: 1 });
    // Not in history, not a tick, or not behind: nothing happens.
    h.c.stepBackTo(99);
    h.c.stepBackTo(-5);
    h.c.stepBackTo(0.5);
    h.c.stepBackTo(1);
    expect(h.snap().tick).toBe(1);

    h.c.start();
    h.c.reset();
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 0 });
    h.c.stepBack();
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 0 });

    // Edit mode has no history to walk.
    h.c.stop();
    h.c.stepBack();
    h.c.stepBackTo(0);
    h.c.reset();
    expect(h.snap().mode).toBe('edit');
  });

  it('a breakpoint hit pauses on its tick and is reported until the next step', () => {
    const rising: Breakpoint = { id: 'b1', at: TORCH, on: 'rising', enabled: true };
    const h = mount({ script: { 3: { power: [[5, 0, 2, 15]] }, 9: {} } });
    h.c.setBreakpoint(rising);
    h.c.setSpeed('max');
    h.c.start();
    h.frame();
    expect(h.snap()).toMatchObject({
      mode: 'paused',
      tick: 3,
      lastHits: [{ id: 'b1', tick: 3, at: TORCH, before: 0, after: 15 }],
    });
    h.c.stepGameTick();
    expect(h.snap()).toMatchObject({ tick: 4, lastHits: [] });
  });

  it('run until the next change stops on the first tick that shows something new, and not at all when nothing can', () => {
    const h = mount({ script: { 5: { cells: [TORCH_OUT] }, 9: { cells: [[5, 0, 2, 76, 1]] } } });
    h.c.runUntilChange();
    expect(h.snap().mode).toBe('running');
    h.frame(100);
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 5 });
    expect(h.shown.at(-1)).toEqual([TORCH_OUT]);
    h.c.runUntilChange();
    h.frame(100);
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 9 });
    expect(h.shown.at(-1)).toEqual([]);
    // Idle: nothing is scheduled, queued or moving, so no number of ticks would change anything.
    h.c.runUntilChange();
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 9, idle: true });

    // A circuit that stays busy without ever showing a change is given up on at the cap.
    const never = mount({ script: busy });
    never.c.runUntilChange();
    never.frame();
    expect(never.snap()).toMatchObject({ mode: 'paused', tick: RUN_CAP_TICKS });
  });

  it('an engine that throws while running ends the simulation, not the page', () => {
    const h = mount({
      script: (tick) => {
        if (tick === 3) throw new Error('lost at tick 3');
        return { idle: false };
      },
    });
    h.c.setSpeed('max');
    h.c.start();
    h.frame();
    expect(h.c.failure).toBe('lost at tick 3');
    expect(h.snap().mode).toBe('edit');
    expect(h.c.live).toBe(false);
    expect(h.shown.at(-1)).toBeNull();
    h.run(1000);
    expect(h.made).toHaveLength(1);
  });
});

describe('the run loop', () => {
  it('runs game ticks at the chosen speed whatever the frame rate, and drops what a long gap would owe', () => {
    const h = mount({ script: busy });
    h.c.start();
    for (let i = 0; i < 6; i++) h.frame(16);
    expect(h.sim().tick).toBe(0); // 96 ms at 10 ticks a second: not one yet
    h.frame(16);
    expect(h.sim().tick).toBe(1);
    h.run(1000);
    expect(h.sim().tick).toBe(11);

    h.c.setSpeed(20);
    h.run(1000);
    expect(h.sim().tick).toBe(31);
    h.c.setSpeed(1);
    h.run(3000, 100);
    expect(h.sim().tick).toBe(34);

    // A minute in a hidden tab owes a quarter of a second, not twelve hundred ticks.
    h.c.setSpeed(20);
    h.frame(60_000);
    expect(h.sim().tick).toBe(39);

    for (const bad of [0, -3, Number.NaN, Number.POSITIVE_INFINITY]) h.c.setSpeed(bad);
    expect(h.snap().speed).toBe(20);
  });

  it('at full speed runs what fits in a frame’s slice of work, and a free engine only up to the cap', () => {
    // Each scripted tick takes a millisecond of the clock.
    const slow = mount({
      script: () => {
        clock.t += 1;
        return { idle: false };
      },
    });
    slow.c.setSpeed('max');
    slow.c.start();
    slow.frame();
    expect(slow.sim().tick).toBe(SLICE_MS);
    slow.frame();
    expect(slow.sim().tick).toBe(2 * SLICE_MS);

    // A quarter of a millisecond each: the engine is asked for a millisecond's worth at a time, and the slice still holds.
    const quick = mount({
      script: () => {
        clock.t += 0.25;
        return { idle: false };
      },
    });
    quick.c.setSpeed('max');
    quick.c.start();
    quick.frame();
    expect(quick.sim().tick).toBe(17);
    quick.frame();
    expect(quick.sim().tick).toBe(33);

    const free = mount({ script: busy });
    free.c.setSpeed('max');
    free.c.start();
    free.frame();
    expect(free.sim().tick).toBe(RUN_CAP_TICKS);
  });
});

describe('the display rate', () => {
  it('shows the simulated states at the display rate, not once per tick', () => {
    const h = mount({ script: (tick) => ({ cells: [[3, 0, 2, 55, tick % 16]], idle: false }) });
    h.c.setSpeed(20);
    h.c.start();
    expect(h.shown).toEqual([[]]);
    h.run(1000);
    expect(h.sim().tick).toBe(20);
    // Ten redraws for twenty ticks, each with the newest state.
    expect(h.shown).toHaveLength(11);
    expect(h.shown.at(-1)).toEqual([[3, 0, 2, 55, 4]]);

    h.c.setDisplayHz(20);
    h.run(1000);
    expect(h.shown).toHaveLength(31);
    h.c.setDisplayHz(5);
    h.run(1000);
    expect(h.shown).toHaveLength(36);
    expect(h.snap()).toMatchObject({ displayHz: 5, displayHzNow: 5 });
    h.c.setDisplayHz(7 as never);
    expect(h.snap().displayHz).toBe(5);

    // Paused: a step shows at once when the last redraw is long ago; steps in quick succession share one redraw.
    h.c.pause();
    h.frame(1000);
    const before = h.shown.length;
    h.c.stepGameTick();
    expect(h.shown).toHaveLength(before + 1);
    h.c.stepGameTick();
    h.c.stepGameTick();
    expect(h.shown).toHaveLength(before + 1);
    h.frame(199);
    expect(h.shown).toHaveLength(before + 1);
    h.frame(1);
    expect(h.shown).toHaveLength(before + 2);
    expect(h.shown.at(-1)).toEqual([[3, 0, 2, 55, h.sim().tick % 16]]);
  });

  it('halves under 30 frames a second, down to 5 Hz, and comes back one step after five good seconds', () => {
    const h = mount({ script: busy });
    const rate = () => [h.snap().displayHz, h.snap().displayHzNow];
    h.c.setDisplayHz(20);
    h.c.start();
    h.c.setFps(25);
    h.run(1000, 250);
    expect(rate()).toEqual([20, 10]);
    h.run(1000, 250);
    expect(rate()).toEqual([20, 5]);
    h.run(3000, 250);
    expect(rate()).toEqual([20, 5]);

    // Between the two thresholds it holds.
    h.c.setFps(35);
    h.run(10_000, 250);
    expect(rate()).toEqual([20, 5]);

    h.c.setFps(45);
    h.run(5750, 250);
    expect(rate()).toEqual([20, 5]);
    h.frame(250);
    expect(rate()).toEqual([20, 10]);
    h.run(4750, 250);
    expect(rate()).toEqual([20, 10]);
    h.frame(250);
    expect(rate()).toEqual([20, 20]);
    h.run(10_000, 250);
    expect(rate()).toEqual([20, 20]);

    // Only a running simulation is watched, and the next one starts at the chosen rate again.
    h.c.setFps(20);
    h.run(1000, 250);
    expect(rate()).toEqual([20, 10]);
    h.c.pause();
    h.run(5000, 250);
    expect(rate()).toEqual([20, 10]);
    h.c.stop();
    h.c.start();
    expect(rate()).toEqual([20, 20]);
    h.run(1000, 250);
    expect(rate()).toEqual([20, 10]);
    h.c.setDisplayHz(10);
    expect(rate()).toEqual([10, 10]);
  });

  it('tells the debugger at the display rate while running, and at once when the run stops', () => {
    const h = mount({ script: busy });
    h.c.setSpeed(20);
    h.c.start();
    const heard = vi.fn();
    h.c.subscribe(heard);
    h.run(1000);
    expect(h.sim().tick).toBe(20);
    expect(heard).toHaveBeenCalledTimes(10);
    h.c.pause();
    expect(heard).toHaveBeenCalledTimes(11);
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 20 });
  });
});

describe('the pointer while a simulation is live', () => {
  const inputs: FakeOptions['inputs'] = [
    [LEVER, 'lever'],
    [BUTTON, 'button'],
    [PLATE, 'plate'],
    [CHEST, 'level'],
  ];

  it('a click uses a lever, a button or a plate, entering the simulation from edit mode; anything else is not one', () => {
    const h = mount({ script: { 2: { power: [[6, 0, 6, 15]] } }, options: { inputs } });
    expect(h.c.live).toBe(false);
    expect(h.c.click([9, 0, 9])).toBe(false);
    expect(h.c.click(CHEST)).toBe(false);
    expect(h.snap().mode).toBe('edit');

    expect(h.c.click(LEVER)).toBe(true);
    expect(h.snap()).toMatchObject({ mode: 'paused', tick: 0, idle: false });
    expect(h.c.live).toBe(true);
    expect(h.c.click(BUTTON)).toBe(true);
    // A plate is stepped on, and off again by the next click, also before a tick has run.
    expect(h.c.click(PLATE)).toBe(true);
    expect(h.c.click(PLATE)).toBe(true);
    expect(h.sim().queued).toEqual([
      { kind: 'toggle', at: LEVER },
      { kind: 'press', at: BUTTON },
      { kind: 'hold', at: PLATE, on: true },
      { kind: 'hold', at: PLATE, on: false },
    ]);

    // After the ticks the plate reads as held (the script powers it at tick 2): the next click lets go.
    h.c.stepRedstoneTick();
    expect(h.sim().queued).toEqual([]);
    expect(h.c.click(PLATE)).toBe(true);
    expect(h.sim().queued).toEqual([{ kind: 'hold', at: PLATE, on: false }]);

    // A click does not pause a running simulation.
    h.c.start();
    expect(h.c.click(LEVER)).toBe(true);
    expect(h.snap().mode).toBe('running');
  });

  it('a start level is part of the design in edit mode and only a poke while the simulation is live', () => {
    const h = mount({ options: { inputs } });
    h.frame(REBUILD_MS);
    h.c.setLevel(CHEST, 7);
    expect(h.c.inspect(CHEST)).toBeNull();
    h.frame(REBUILD_MS);
    expect(h.sim().circuit.extras).toEqual([{ at: CHEST, level: 7 }]);
    h.c.setLevel(CHEST, 99);
    h.frame(REBUILD_MS);
    expect(h.sim().circuit.extras).toEqual([{ at: CHEST, level: 15 }]);

    // The same level again, a cell that holds nothing, not a number: the design did not change.
    const built = h.made.length;
    h.c.setLevel(CHEST, 15);
    h.c.setLevel(LEVER, 5);
    h.c.setLevel(CHEST, Number.NaN);
    h.run(500);
    expect(h.made).toHaveLength(built);

    h.c.setLevel(CHEST, 0);
    h.frame(REBUILD_MS);
    expect(h.sim().circuit).not.toHaveProperty('extras');

    h.c.setLevel(CHEST, 4);
    h.c.start();
    expect(h.sim().circuit.extras).toEqual([{ at: CHEST, level: 4 }]);
    const live = h.made.length;
    h.c.setLevel(CHEST, 9);
    expect(h.sim().queued).toEqual([{ kind: 'level', at: CHEST, level: 9 }]);
    expect(h.snap().mode).toBe('running');
    expect(h.made).toHaveLength(live);
    // Like a lever flipped while it ran: afterwards the design holds what was drawn.
    h.c.stop();
    h.studio.edit([[9, 0, 9, 4, 0]], 'place', 'cobble');
    h.frame(REBUILD_MS);
    expect(h.sim().circuit.extras).toEqual([{ at: CHEST, level: 4 }]);
  });
});

describe('a change of the canvas', () => {
  const changes: Array<[string, (s: Studio) => void]> = [
    ['an edit', (s) => void s.edit([[9, 0, 9, 4, 0]], 'place', 'cobble')],
    ['an undo', (s) => void s.undo()],
    ['a block from the AI', (s) => void s.applyAi([{ kind: 'place', label: 'a block', cells: [[9, 0, 9, 4, 0]] }])],
    ['a fix from the bot', (s) => s.correct([[5, 0, 2, 76, 2]])],
  ];

  it.each(changes)('%s ends the simulation, and the view and the next build follow the canvas', (_name, change) => {
    const h = mount({ script: { 0: { cells: [TORCH_OUT] }, 50: {} } });
    h.studio.edit([[8, 0, 8, 4, 0]], 'place', 'cobble');
    h.c.start();
    h.run(300);
    const old = h.sim();
    expect(old.tick).toBe(3);

    change(h.studio);
    expect(h.snap()).toMatchObject({ mode: 'edit', tick: 0 });
    expect(h.c.live).toBe(false);
    expect(h.shown.at(-1)).toBeNull();
    expect(h.c.inspect(LEVER)).toBeNull();

    h.run(REBUILD_MS);
    expect(h.made).toHaveLength(2);
    expect(h.sim().circuit.cells).toEqual(h.studio.canvas.cells());
    // The simulation of the old design was dropped, not run on, and nothing starts by itself.
    expect(old.tick).toBe(3);
    h.run(500);
    expect(h.snap().mode).toBe('edit');
    expect(h.sim().tick).toBe(0);
  });

  it('is no longer followed once the controller is disconnected, and a live simulation leaves the view', () => {
    const h = mount({ script: { 0: { cells: [TORCH_OUT] } } });
    h.c.start();
    h.off();
    expect(h.snap().mode).toBe('edit');
    expect(h.shown.at(-1)).toBeNull();
    h.studio.edit([[9, 0, 9, 4, 0]], 'place', 'cobble');
    h.run(1000);
    expect(h.made).toHaveLength(1);
  });
});

describe('probes, breakpoints and the inspector', () => {
  it('a probe is a watched cell with a label and a colour, and its timeline is the engine’s samples', () => {
    const h = mount({ script: (tick) => ({ power: [[5, 0, 2, tick % 2 ? 15 : 0]], idle: false }) });
    h.c.addProbe(TORCH);
    h.c.addProbe(TORCH);
    h.c.addProbe(LEVER);
    const [first, second] = h.snap().probes;
    expect(h.snap().probes).toHaveLength(2);
    expect(first).toMatchObject({ id: 'p1', at: TORCH, label: 'P1' });
    expect(second).toMatchObject({ id: 'p2', at: LEVER, label: 'P2' });
    expect(first!.color).not.toBe(second!.color);
    expect(h.c.timeline('p1', 0, 9)).toEqual([]);

    // The simulation that is built next watches them from its first tick.
    h.c.stepRedstoneTick();
    expect(h.sim().watched).toEqual([TORCH, LEVER]);
    expect(h.c.timeline('p1', 0, 9).map((s) => [s.tick, s.power, s.powered])).toEqual([
      [0, 0, false],
      [1, 15, true],
      [2, 0, false],
    ]);
    expect(h.c.timeline('nobody', 0, 9)).toEqual([]);

    h.c.renameProbe('p1', '  sum  ');
    h.c.renameProbe('p1', '   ');
    expect(h.snap().probes[0]!.label).toBe('sum');
    const heard = vi.fn();
    h.c.subscribe(heard);
    h.c.removeProbe('p2');
    h.c.removeProbe('p2');
    h.c.renameProbe('p1', 'sum');
    expect(heard).toHaveBeenCalledTimes(1);
    expect(h.sim().watched).toEqual([TORCH]);
    h.c.addProbe(DUST);
    expect(h.snap().probes.map((p) => p.id)).toEqual(['p1', 'p2']);

    // They outlive the simulation: the one built after an edit watches them too.
    h.studio.edit([[9, 0, 9, 4, 0]], 'place', 'cobble');
    h.c.start();
    expect(h.made).toHaveLength(2);
    expect(h.sim().watched).toEqual([TORCH, DUST]);
  });

  it('breakpoints are kept by id and handed to every simulation that is built', () => {
    const h = mount();
    const b1: Breakpoint = { id: 'b1', at: TORCH, on: 'change', enabled: true };
    const b2: Breakpoint = { id: 'b2', at: LEVER, on: 'atLeast', value: 8, enabled: true };
    h.c.setBreakpoint(b1);
    h.c.setBreakpoint(b2);
    h.c.setBreakpoint({ ...b1, enabled: false });
    expect(h.snap().breakpoints).toEqual([{ ...b1, enabled: false }, b2]);
    h.frame(REBUILD_MS);
    expect(h.sim().breakpoints).toEqual([{ ...b1, enabled: false }, b2]);
    h.c.removeBreakpoint('b1');
    h.c.removeBreakpoint('gone');
    expect(h.sim().breakpoints).toEqual([b2]);
    h.studio.edit([[9, 0, 9, 4, 0]], 'place', 'cobble');
    h.frame(REBUILD_MS);
    expect(h.made).toHaveLength(2);
    expect(h.sim().breakpoints).toEqual([b2]);
  });

  it('inspect reads one cell from the simulation', () => {
    const why = [{ at: TORCH, why: 'the block it hangs on is powered' }];
    const h = mount({
      script: { 0: { cells: [TORCH_OUT], locked: [[7, 0, 7]] } },
      options: { pending: [{ at: TORCH, dueTick: 2, priority: -1 }], explain: (at) => (at[0] === 5 ? why : []) },
    });
    h.frame(REBUILD_MS);
    expect(h.c.inspect(TORCH)).toEqual({
      sample: { tick: 0, power: 0, powered: false, state: (75 << 4) | 1 },
      locked: false,
      pending: [{ dueTick: 2, priority: -1 }],
      explain: why,
    });
    expect(h.c.inspect([7, 0, 7])).toMatchObject({ locked: true, pending: [], explain: [] });
  });

  it('keeps the newest 200 events and forgets those after a tick it stepped back to', () => {
    const h = mount({
      script: (tick) => (tick ? { events: [{ kind: 'fired', at: LEVER }], idle: false } : { idle: false }),
    });
    h.c.setSpeed('max');
    h.c.start();
    h.frame();
    h.c.pause();
    const events = h.snap().events;
    expect(events).toHaveLength(200);
    expect([events[0]!.tick, events.at(-1)!.tick]).toEqual([RUN_CAP_TICKS - 199, RUN_CAP_TICKS]);
    h.c.stepBackTo(RUN_CAP_TICKS - 100);
    expect(h.snap().events).toHaveLength(100);
    expect(h.snap().events.at(-1)).toEqual({ kind: 'fired', at: LEVER, tick: RUN_CAP_TICKS - 100 });
    h.c.reset();
    expect(h.snap().events).toEqual([]);
  });
});

describe('what is drawn over the circuit', () => {
  const extra: DesignerCell[] = [...BLOCKS, [3, 0, 3, 55, 0], [7, 0, 7, 93, 5]];

  it('marks powered dust with its level and held repeaters while live, and probes and breakpoints always', () => {
    const h = mount({
      session: { blocks: extra },
      script: { 0: { cells: [[3, 0, 2, 55, 15], TORCH_OUT], locked: [[7, 0, 7]] } },
    });
    expect(h.marks.at(-1)).toEqual({ levels: [], locks: [], markers: [] });
    h.c.start();
    // Only the dust that carries power gets a number; the repeater comes with its meta, which says how it lies.
    expect(h.marks.at(-1)).toEqual({ levels: [3, 0, 2, 15], locks: [[7, 0, 7, 5]], markers: [] });
    h.c.stop();
    expect(h.marks.at(-1)).toEqual({ levels: [], locks: [], markers: [] });

    h.c.addProbe(TORCH);
    h.c.setBreakpoint({ id: 'b1', at: LEVER, on: 'change', enabled: true });
    const [probe, armed] = h.marks.at(-1)!.markers;
    expect(probe).toEqual({ at: TORCH, kind: 'probe', color: h.snap().probes[0]!.color });
    expect(armed).toMatchObject({ at: LEVER, kind: 'breakpoint' });
    h.c.setBreakpoint({ id: 'b1', at: LEVER, on: 'change', enabled: false });
    expect(h.marks.at(-1)!.markers[1]!.color).not.toBe(armed!.color);
    h.c.start();
    expect(h.marks.at(-1)).toMatchObject({
      levels: [3, 0, 2, 15],
      markers: [{ kind: 'probe' }, { kind: 'breakpoint' }],
    });
  });

  const marksOf = (levels: number[], over: Partial<SimMarks> = {}): SimMarks => ({
    levels: Int16Array.from(levels),
    levelCount: levels.length / 4,
    locks: [],
    markers: [],
    ...over,
  });
  const part = <T = Mesh>(o: PowerOverlay, name: string) => o.group.getObjectByName(name) as unknown as T;
  const attribute = (mesh: Mesh, name: string) => mesh.geometry.getAttribute(name) as BufferAttribute;

  it('writes one quad per number, and again only when a level changed', () => {
    const o = new PowerOverlay();
    const labels = part(o, 'power-levels');
    const position = attribute(labels, 'position');
    o.set(marksOf([3, 0, 2, 15, 4, 0, 2, 14]));
    expect(labels.geometry.drawRange.count).toBe(12);
    const written = position.version;
    o.set(marksOf([3, 0, 2, 15, 4, 0, 2, 14]));
    expect(position.version).toBe(written);
    o.set(marksOf([3, 0, 2, 15, 4, 0, 2, 13]));
    expect(position.version).toBe(written + 1);
    o.set(marksOf([]));
    expect(labels.geometry.drawRange.count).toBe(0);
    expect(labels.visible).toBe(false);

    const many = Array.from({ length: MAX_POWER_LABELS + 10 }, (_, i) => [i % 64, 0, Math.floor(i / 64), 1]).flat();
    o.set(marksOf(many));
    expect(labels.geometry.drawRange.count).toBe(MAX_POWER_LABELS * 6);
    o.dispose();
  });

  it('lays a number flat on its cell, cut from the atlas, turned to read upright from where the camera looks', () => {
    const o = new PowerOverlay();
    const position = attribute(part(o, 'power-levels'), 'position');
    const uv = attribute(part(o, 'power-levels'), 'uv');
    const corners = () => [0, 1, 2, 3].map((i) => [position.getX(i), position.getZ(i)]);
    o.set(marksOf([3, 0, 2, 15]));
    // Bottom left, bottom right, top right, top left of the text, for a camera that looks north.
    expect(corners()).toEqual([
      [3.25, 2.75],
      [3.75, 2.75],
      [3.75, 2.25],
      [3.25, 2.25],
    ]);
    expect(position.getY(0)).toBeCloseTo(64.07);
    // 15 is the last square of the 4 x 4 atlas: the bottom right one.
    expect([uv.getX(0), uv.getY(0), uv.getX(2), uv.getY(2)]).toEqual([0.75, 0, 1, 0.25]);
    o.setFacing('east');
    expect(corners()).toEqual([
      [3.25, 2.25],
      [3.25, 2.75],
      [3.75, 2.75],
      [3.75, 2.25],
    ]);
    o.dispose();
  });

  it('boxes probes and breakpoints in one line object, and bars each held repeater across its signal', () => {
    const o = new PowerOverlay();
    const lines = part(o, 'sim-markers');
    const bars = part<InstancedMesh>(o, 'repeater-locks');
    const markers: SimMarker[] = [
      { at: TORCH, kind: 'probe', color: 0x00ff00 },
      { at: TORCH, kind: 'breakpoint', color: 0xff0000 },
    ];
    o.set(
      marksOf([], {
        markers,
        locks: [
          [7, 0, 7, 0],
          [8, 0, 7, 1],
        ],
      }),
    );
    expect(lines.visible).toBe(true);
    expect(lines.geometry.drawRange.count).toBe(2 * 24);
    const colour = attribute(lines, 'color');
    expect([colour.getX(0), colour.getY(0), colour.getZ(0)]).toEqual([0, 1, 0]);
    expect([colour.getX(24), colour.getY(24), colour.getZ(24)]).toEqual([1, 0, 0]);
    // On one cell the breakpoint's box goes around the probe's.
    const position = attribute(lines, 'position');
    expect(position.getX(0)).toBeCloseTo(5 - 0.05);
    expect(position.getX(24)).toBeCloseTo(5 - 0.11);

    expect(bars.count).toBe(2);
    const scaleOf = (i: number) => {
      const m = new Matrix4();
      bars.getMatrixAt(i, m);
      return new Vector3().setFromMatrixScale(m);
    };
    // Meta 0 runs north to south, so its bar lies east to west; meta 1 the other way.
    expect(scaleOf(0).x).toBeGreaterThan(scaleOf(0).z);
    expect(scaleOf(1).z).toBeGreaterThan(scaleOf(1).x);

    // The same lists again are not written again; more than fit are cut off.
    const written = position.version;
    o.set(
      marksOf([], {
        markers,
        locks: [
          [7, 0, 7, 0],
          [8, 0, 7, 1],
        ],
      }),
    );
    expect(position.version).toBe(written);
    const crowd = Array.from({ length: MAX_MARKERS + 5 }, (_, i): SimMarker => ({
      at: [i % 64, 0, 0],
      kind: 'probe',
      color: 1,
    }));
    const held = Array.from({ length: MAX_LOCKS + 5 }, (_, i) => [i % 64, 0, 1, 0] as const);
    o.set(marksOf([], { markers: crowd, locks: held }));
    expect(lines.geometry.drawRange.count).toBe(MAX_MARKERS * 24);
    expect(bars.count).toBe(MAX_LOCKS);
    o.set(marksOf([]));
    expect(lines.visible).toBe(false);
    expect(bars.visible).toBe(false);
    o.dispose();
  });
});

describe('the simulation in the studio page', () => {
  function page(enabled = true) {
    const studio = new Studio(session(), api, 60_000);
    studios.push(studio);
    const fake = fakeSimulations({ 0: { cells: [TORCH_OUT] }, 50: {} }, { inputs: [[LEVER, 'lever']] });
    let frames: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb));
    const cancel = vi.fn();
    vi.stubGlobal('cancelAnimationFrame', cancel);
    vi.spyOn(performance, 'now').mockImplementation(() => clock.t);
    const scene = new Scene();
    const target = (at: Pos): AimTarget => ({
      kind: 'block',
      x: at[0],
      y: at[1] + 64,
      z: at[2],
      id: 1,
      meta: 0,
      face: 1,
      name: 'block',
      distance: 3,
    });
    const engine = {
      runtime: { scene },
      focus: vi.fn(),
      aimNow: vi.fn(() => Promise.resolve<AimTarget | null>(target(LEVER))),
    };
    const stage = document.createElement('div');
    const surface = document.createElement('canvas');
    stage.append(surface);
    document.body.append(stage);
    const setDisplay = vi.fn();
    const props = {
      enabled,
      studio,
      version: '1.12.2' as const,
      world: { setDisplay } as unknown as StudioWorld,
      mod: { PowerOverlay, createSimulation: fake.create },
      viewer: { current: { engine: () => engine } as unknown as WorldViewerHandle },
      stage: { current: stage },
      ready: true,
      fps: 60,
      aim: null as AimTarget | null,
      look: 'north' as const,
    };
    const hook = renderHook((p: typeof props) => useRedstoneSim(p), { initialProps: props });
    return {
      ...hook,
      props,
      studio,
      fake,
      scene,
      engine,
      stage,
      surface,
      setDisplay,
      cancel,
      target,
      /** The animation frame, `ms` later. */
      frame: (ms: number) => {
        clock.t += ms;
        const due = frames;
        frames = [];
        act(() => due.forEach((f) => f(clock.t)));
      },
      remove: () => stage.remove(),
    };
  }

  it('is inert for a design that is not a redstone one', () => {
    const h = page(false);
    expect(h.result.current).toMatchObject({ api: null, live: false, failure: null, selected: null });
    expect(h.scene.children).toHaveLength(0);
    h.frame(1000);
    expect(h.fake.made).toHaveLength(0);
    h.remove();
  });

  it('shows the simulation through the world, runs it on the animation frame and follows the canvas', () => {
    const h = page();
    const sim = h.result.current.api!;
    expect(h.result.current.live).toBe(false);
    expect(h.scene.children).toHaveLength(1);

    act(() => sim.start());
    expect(h.result.current.live).toBe(true);
    expect(h.setDisplay).toHaveBeenLastCalledWith([TORCH_OUT]);
    // The first frame only starts the clock; at ten ticks a second each later one is a tick.
    h.frame(100);
    h.frame(100);
    h.frame(100);
    expect(h.fake.made[0]!.tick).toBe(2);

    act(() => void h.studio.edit([[9, 0, 9, 4, 0]], 'place', 'cobble'));
    expect(h.result.current.live).toBe(false);
    expect(h.setDisplay).toHaveBeenLastCalledWith(null);

    // Leaving the page takes a live simulation and the layer over it away, and stops the frames.
    act(() => sim.start());
    const shown = h.setDisplay.mock.calls.length;
    h.unmount();
    expect(h.setDisplay).toHaveBeenCalledTimes(shown + 1);
    expect(h.setDisplay).toHaveBeenLastCalledWith(null);
    expect(h.scene.children).toHaveLength(0);
    expect(h.cancel).toHaveBeenCalled();
    h.remove();
  });

  /** The aim is worked out in a few promise steps (no timers): this lets them all run. */
  const aimSettled = async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  };

  it('gives a locked click to the circuit while live, and tells the page what is aimed at and picked', async () => {
    const h = page();
    const sim = h.result.current.api!;
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: h.surface });
    const press = (button = 0) => {
      const event = new PointerEvent('pointerdown', { bubbles: true, cancelable: true, button });
      h.surface.dispatchEvent(event);
      return event;
    };
    try {
      // Edit mode: the click is the studio's.
      expect(press().defaultPrevented).toBe(false);
      act(() => {
        sim.start();
        sim.pause();
      });
      await act(async () => {
        expect(press(2).defaultPrevented).toBe(true);
        await aimSettled();
      });
      expect(h.fake.made[0]!.queued).toEqual([{ kind: 'toggle', at: LEVER }]);
      expect(h.result.current.selected).toBeNull();

      // Not an input: the cell goes to the inspector.
      h.engine.aimNow.mockResolvedValue(h.target(DUST));
      await act(async () => {
        press();
        await aimSettled();
      });
      expect(h.result.current.selected).toEqual(DUST);

      h.rerender({ ...h.props, aim: h.target(TORCH) });
      expect(h.result.current.aimed()).toEqual(TORCH);
      h.result.current.jump(TORCH);
      expect(h.engine.focus).toHaveBeenCalledWith(5, 64, 2, { distance: 12 });
    } finally {
      Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
      h.unmount();
      h.remove();
    }
  });
});
