import { describe, expect, it } from 'vitest';
import {
  GROUND_ID,
  type Breakpoint,
  type CircuitInput,
  type InputEvent,
  type Pos,
  type Simulation,
} from '../src/index.js';
import { FakeSimulation, fakeSimulations, type FakeOptions, type FakeScript } from '../src/testing.js';

const LEVER: Pos = [0, 0, 0];
const DUST: Pos = [1, 0, 0];
const TORCH: Pos = [3, 0, 0];
const state = (id: number, meta: number) => (id << 4) | meta;

/** A lever, one dust and a torch on the ground; one ground cell is dug out. */
const input: CircuitInput = {
  version: '1.12.2',
  cells: [
    [0, 0, 0, 69, 5],
    [1, 0, 0, 55, 0],
    [3, 0, 0, 76, 1],
  ],
  ground: { topY: -1, dug: [[2, -1, 0]] },
};

/** The lever goes on at tick 2 and lights the dust; the torch goes out at tick 4; a dispenser fires at tick 5. */
const script: FakeScript = {
  0: { power: [[3, 0, 0, 15]] },
  2: {
    cells: [
      [1, 0, 0, 55, 15],
      [0, 0, 0, 69, 13],
    ],
    power: [
      [1, 0, 0, 15],
      [0, 0, 0, 15],
    ],
  },
  4: { cells: [[3, 0, 0, 75, 1]], power: [[3, 0, 0, 0]] },
  5: { events: [{ kind: 'fired', at: [5, 0, 0], note: 'dispenser' }] },
};

const make = (options: FakeOptions = {}) => new FakeSimulation(input, script, options);
const torchFalls: Breakpoint = { id: 'torch', at: TORCH, on: 'falling', enabled: true };

describe('FakeSimulation: the start state', () => {
  it('is tick 0 with the design shown as given, the scripted start applied and work still ahead', () => {
    const sim: Simulation = make({ inputs: [[LEVER, 'lever']] });
    expect([sim.version, sim.tick, sim.historyDepth, sim.idle]).toEqual(['1.12.2', 0, 0, false]);
    expect(sim.displayCells()).toEqual([]);
    expect(sim.stateAt(TORCH)).toBe(state(76, 1));
    expect(sim.sample(TORCH)).toEqual({ tick: 0, power: 15, powered: true, state: state(76, 1) });
    expect(sim.sample(DUST)).toEqual({ tick: 0, power: 0, powered: false, state: state(55, 0) });
    expect(sim.inputAt(LEVER)).toBe('lever');
    expect(sim.inputAt(DUST)).toBeNull();
  });

  it('reads the ground as the ground, a dug cell and the open air as air', () => {
    const sim = make();
    expect(sim.stateAt([1, -1, 0])).toBe(GROUND_ID << 4);
    expect(sim.stateAt([1, -1, 0]) >> 4).toBe(GROUND_ID);
    expect(sim.stateAt([2, -1, 0])).toBe(0);
    expect(sim.stateAt([1, 5, 0])).toBe(0);
    expect(new FakeSimulation({ version: 'b1.8.1', cells: [] }).stateAt([1, -1, 0])).toBe(0);
  });

  it('is idle when nothing is scripted and nothing is queued', () => {
    const sim = new FakeSimulation(input);
    expect(sim.idle).toBe(true);
    sim.input({ kind: 'toggle', at: LEVER });
    expect(sim.idle).toBe(false);
    expect(sim.stepGameTick().idle).toBe(true);
  });
});

describe('FakeSimulation: stepping forward', () => {
  it('runs one game tick per step and reports the cells that changed, sorted, with what is shown now', () => {
    const sim = make();
    expect(sim.stepGameTick()).toEqual({ tick: 1, ranTicks: 1, changed: [], events: [], hits: [], idle: false });
    const second = sim.stepGameTick();
    expect(second.changed).toEqual([
      [0, 0, 0, 69, 13],
      [1, 0, 0, 55, 15],
    ]);
    expect(sim.displayCells()).toEqual(second.changed);
    expect(sim.powerAt(DUST)).toBe(15);
    expect(sim.historyDepth).toBe(2);
  });

  it('applies a queued input at the start of the next game tick and logs the tick it began', () => {
    const sim = make();
    const toggle: InputEvent = { kind: 'toggle', at: LEVER };
    sim.input(toggle);
    expect(sim.queued).toEqual([toggle]);
    expect(sim.inputLog).toEqual([]);
    sim.stepGameTick();
    expect(sim.queued).toEqual([]);
    expect(sim.inputLog).toEqual([{ tick: 1, event: toggle }]);
  });

  it('a redstone tick is two game ticks, cut short by a breakpoint and never realigned', () => {
    const sim = make();
    sim.setBreakpoints([{ id: 'dust', at: DUST, on: 'rising', enabled: true }]);
    sim.stepGameTick();
    const cut = sim.stepRedstoneTick();
    expect([cut.tick, cut.ranTicks]).toEqual([2, 1]);
    expect(cut.hits).toEqual([{ id: 'dust', tick: 2, at: DUST, before: 0, after: 15 }]);
    const next = sim.stepRedstoneTick();
    expect([next.tick, next.ranTicks, next.hits.length]).toEqual([4, 2, 0]);
    expect(next.changed).toEqual([[3, 0, 0, 75, 1]]);
  });

  it('stamps events with their tick and reports idle once the script has run out', () => {
    const sim = make();
    const r = sim.run(5, 'breakpoint');
    expect(r.events).toEqual([{ kind: 'fired', at: [5, 0, 0], note: 'dispenser', tick: 5 }]);
    expect([r.tick, r.ranTicks, r.idle, sim.idle]).toEqual([5, 5, true, true]);
  });
});

describe('FakeSimulation: run', () => {
  it("'change' stops after the first tick that changed a shown state", () => {
    const sim = make();
    const r = sim.run(100, 'change');
    expect([r.tick, r.ranTicks]).toEqual([2, 2]);
    expect(r.changed).toHaveLength(2);
    expect(sim.run(100, 'change').tick).toBe(4);
  });

  it("'idle' stops once the circuit is idle and returns at once when it already is", () => {
    const sim = make();
    expect(sim.run(100, 'idle')).toMatchObject({ tick: 5, ranTicks: 5, idle: true });
    expect(sim.run(100, 'idle')).toEqual({ tick: 5, ranTicks: 0, changed: [], events: [], hits: [], idle: true });
  });

  it("'breakpoint' runs to the limit unless a breakpoint hits, and a disabled one never does", () => {
    const sim = make();
    sim.setBreakpoints([{ ...torchFalls, enabled: false }]);
    expect(sim.run(7, 'breakpoint')).toMatchObject({ tick: 7, ranTicks: 7, hits: [] });
    sim.reset();
    sim.setBreakpoints([torchFalls]);
    expect(sim.breakpoints).toEqual([torchFalls]);
    const r = sim.run(100, 'breakpoint');
    expect([r.tick, r.ranTicks]).toEqual([4, 4]);
    expect(r.hits).toEqual([{ id: 'torch', tick: 4, at: TORCH, before: 15, after: 0 }]);
    expect(sim.run(0, 'breakpoint')).toMatchObject({ tick: 4, ranTicks: 0, hits: [] });
  });
});

describe('FakeSimulation: history', () => {
  it('steps back to the state of an earlier tick and reports what the view has to redraw', () => {
    const sim = make();
    sim.run(5, 'breakpoint');
    expect(sim.stepBack()).toEqual({ tick: 4, ranTicks: 0, changed: [], events: [], hits: [], idle: false });
    const back = sim.stepBack(2);
    expect(back.tick).toBe(2);
    expect(back.changed).toEqual([[3, 0, 0, 76, 1]]);
    expect(sim.powerAt(TORCH)).toBe(15);
    expect(sim.historyDepth).toBe(2);
    const start = sim.stepBack(10);
    expect(start.tick).toBe(0);
    expect(start.changed).toEqual([
      [0, 0, 0, 69, 5],
      [1, 0, 0, 55, 0],
    ]);
    expect(sim.displayCells()).toEqual([]);
  });

  it('never stops at a breakpoint while rewinding', () => {
    const sim = make();
    sim.setBreakpoints([{ id: 'any', at: TORCH, on: 'change', enabled: true }]);
    sim.run(5, 'idle');
    sim.stepGameTick();
    expect(sim.stepBack(3).hits).toEqual([]);
  });

  it('drops the inputs made after the tick it rewinds to, queued ones included', () => {
    const sim = make();
    sim.run(2, 'breakpoint');
    sim.input({ kind: 'press', at: DUST });
    sim.stepGameTick();
    sim.input({ kind: 'toggle', at: LEVER });
    expect(sim.inputLog.map((i) => i.tick)).toEqual([3]);
    sim.stepBack();
    expect(sim.inputLog).toEqual([]);
    expect(sim.queued).toEqual([]);
  });

  it('reaches back only as far as its history', () => {
    const sim = make({ historyTicks: 3 });
    sim.run(5, 'breakpoint');
    expect(sim.historyDepth).toBe(3);
    expect(sim.stepBack(10).tick).toBe(2);
    expect(sim.historyDepth).toBe(0);
    expect(sim.stepBack().tick).toBe(2);
  });

  it('reset goes back to tick 0 as it was built and keeps the breakpoints and the watch list', () => {
    const sim = make({ historyTicks: 2 });
    sim.setBreakpoints([torchFalls]);
    sim.watch([TORCH]);
    sim.input({ kind: 'toggle', at: LEVER });
    sim.run(5, 'idle');
    const r = sim.reset();
    expect(r).toMatchObject({ tick: 0, ranTicks: 0, events: [], hits: [], idle: false });
    expect(r.changed).toEqual([
      [0, 0, 0, 69, 5],
      [1, 0, 0, 55, 0],
      [3, 0, 0, 76, 1],
    ]);
    expect([sim.historyDepth, sim.inputLog.length]).toEqual([0, 0]);
    expect(sim.breakpoints).toEqual([torchFalls]);
    expect(sim.watched).toEqual([TORCH]);
    expect(sim.trace(TORCH, 0, 100)).toEqual([{ tick: 0, power: 15, powered: true, state: state(76, 1) }]);
    expect(sim.run(100, 'breakpoint').tick).toBe(4);
  });

  it('replays a function script with the logged inputs, so a rewind lands on the same state', () => {
    const reacting: FakeScript = (tick, inputs) =>
      inputs.some((i) => i.kind === 'toggle') ? { cells: [[0, 0, 0, 69, 13]], power: [[0, 0, 0, tick]] } : null;
    const sim = new FakeSimulation(input, reacting);
    sim.stepGameTick();
    sim.input({ kind: 'toggle', at: LEVER });
    expect(sim.stepGameTick().changed).toEqual([[0, 0, 0, 69, 13]]);
    sim.stepGameTick();
    expect(sim.stepBack().changed).toEqual([]);
    expect([sim.tick, sim.stateAt(LEVER), sim.powerAt(LEVER)]).toEqual([2, state(69, 13), 2]);
    expect(sim.stepBack().changed).toEqual([[0, 0, 0, 69, 5]]);
    expect(sim.powerAt(LEVER)).toBe(0);
  });
});

describe('FakeSimulation: watched cells', () => {
  it('samples a watched cell at the current tick and after every game tick', () => {
    const sim = make();
    sim.watch([DUST]);
    expect(sim.trace(DUST, 0, 0)).toEqual([{ tick: 0, power: 0, powered: false, state: state(55, 0) }]);
    sim.run(3, 'breakpoint');
    expect(sim.trace(DUST, 0, 100).map((s) => [s.tick, s.power, s.powered])).toEqual([
      [0, 0, false],
      [1, 0, false],
      [2, 15, true],
      [3, 15, true],
    ]);
    expect(sim.trace(DUST, 2, 2)).toEqual([{ tick: 2, power: 15, powered: true, state: state(55, 15) }]);
    expect(sim.trace(TORCH, 0, 100)).toEqual([]);
  });

  it('starts a newly watched cell now and forgets a cell that leaves the list', () => {
    const sim = make();
    sim.watch([DUST]);
    sim.run(3, 'breakpoint');
    sim.watch([TORCH]);
    expect(sim.trace(DUST, 0, 100)).toEqual([]);
    expect(sim.trace(TORCH, 0, 100).map((s) => s.tick)).toEqual([3]);
    sim.watch([TORCH, DUST]);
    sim.stepGameTick();
    expect(sim.trace(TORCH, 0, 100).map((s) => s.tick)).toEqual([3, 4]);
    expect(sim.trace(DUST, 0, 100).map((s) => s.tick)).toEqual([3, 4]);
  });

  it('drops the samples after a rewind and keeps one for the tick it lands on', () => {
    const sim = make();
    sim.run(2, 'breakpoint');
    sim.watch([DUST]);
    sim.run(2, 'breakpoint');
    sim.stepBack(1);
    expect(sim.trace(DUST, 0, 100).map((s) => s.tick)).toEqual([2, 3]);
    sim.stepBack(2);
    expect(sim.trace(DUST, 0, 100)).toEqual([{ tick: 1, power: 0, powered: false, state: state(55, 0) }]);
  });

  it('traces only as far back as history reaches', () => {
    const sim = make({ historyTicks: 2 });
    sim.watch([DUST]);
    sim.run(5, 'breakpoint');
    expect(sim.trace(DUST, 0, 100).map((s) => s.tick)).toEqual([3, 4, 5]);
  });
});

describe('FakeSimulation: what the debugger reads', () => {
  it('lists the locked repeaters from the tick the script locks them, sorted', () => {
    const sim = new FakeSimulation(input, {
      1: {
        locked: [
          [4, 1, 2],
          [9, 0, 2],
          [2, 0, 2],
        ],
      },
      2: { locked: [] },
    });
    expect(sim.lockedCells()).toEqual([]);
    sim.stepGameTick();
    expect(sim.lockedCells()).toEqual([
      [2, 0, 2],
      [9, 0, 2],
      [4, 1, 2],
    ]);
    expect([sim.lockedAt([9, 0, 2]), sim.lockedAt(DUST)]).toEqual([true, false]);
    sim.stepGameTick();
    expect([sim.lockedCells().length, sim.lockedAt([9, 0, 2])]).toEqual([0, false]);
    expect(sim.stepBack().tick).toBe(1);
    expect(sim.lockedAt([9, 0, 2])).toBe(true);
  });

  it('answers pending ticks until they are due, the explain chain and the diagnostics it was given', () => {
    const chain = [
      { at: DUST, why: 'dust at level 15' },
      { at: LEVER, why: 'the lever is on' },
    ];
    const sim = make({
      pending: [
        { at: TORCH, dueTick: 4, priority: 0 },
        { at: TORCH, dueTick: 2, priority: -1 },
        { at: DUST, dueTick: 9, priority: 0 },
      ],
      explain: (at) => (at === DUST ? chain : []),
      diagnostics: [{ at: TORCH, code: 'floating', text: 'nothing under the torch' }],
    });
    expect(sim.pendingAt(TORCH)).toEqual([
      { dueTick: 2, priority: -1 },
      { dueTick: 4, priority: 0 },
    ]);
    sim.run(2, 'breakpoint');
    expect(sim.pendingAt(TORCH)).toEqual([{ dueTick: 4, priority: 0 }]);
    expect(sim.explain(DUST)).toBe(chain);
    expect(sim.explain(TORCH)).toEqual([]);
    expect(sim.diagnostics()).toEqual([{ at: TORCH, code: 'floating', text: 'nothing under the torch' }]);
    expect(new FakeSimulation(input).diagnostics()).toEqual([]);
  });
});

describe('fakeSimulations', () => {
  it('stands in for createSimulation: one fake per call from the same script, kept newest last', () => {
    const { create, made } = fakeSimulations(script, { historyTicks: 50 });
    const first = create(input);
    const second = create({ ...input, cells: [] }, { historyTicks: 1 });
    expect(made).toEqual([first, second]);
    expect(made[1]!.circuit.cells).toEqual([]);
    first.run(5, 'breakpoint');
    second.run(5, 'breakpoint');
    expect([first.historyDepth, second.historyDepth]).toEqual([5, 1]);
    expect(first.displayCells()).toHaveLength(3);
  });
});
