/**
 * What the debugger reads from the engine (report 4.7): breakpoints judged once per game tick in phase 6 with the
 * defined before and after values, watched cells and their traces, and `explain`.
 */
import { describe, expect, it } from 'vitest';
import type { Breakpoint, Pos } from '../src/api.js';
import {
  CHEST,
  COMPARATOR,
  DUST,
  LAMP,
  LEVER,
  READS_WEST,
  REPEATER,
  REPEATER_ON,
  STICKS_EAST,
  STONE,
  TORCH,
  TORCH_ON_WEST,
  VERSIONS,
  build,
  cell,
  toggle,
} from './circuit.js';

const LEVER_AT: Pos = [-1, 0, 0];
const REP: Pos = [0, 0, 0];
const D1: Pos = [1, 0, 0];
const D2: Pos = [2, 0, 0];
const K: Pos = [3, 0, 0];
const TORCH_AT: Pos = [4, 0, 0];
const AFTER: Pos = [5, 0, 0];
/** A lever feeding a 4 game tick repeater, two dust, a block, a torch on it and dust after the torch. */
const cells = [
  cell([-2, 0, 0], STONE),
  cell(LEVER_AT, LEVER, STICKS_EAST),
  cell(REP, REPEATER, READS_WEST | 4),
  cell(D1, DUST),
  cell(D2, DUST),
  cell(K, STONE),
  cell(TORCH_AT, TORCH, TORCH_ON_WEST),
  cell(AFTER, DUST),
];

const bp = (id: string, at: Pos, on: Breakpoint['on'], value?: number): Breakpoint =>
  value === undefined ? { id, at, on, enabled: true } : { id, at, on, value, enabled: true };

describe.each(VERSIONS)('breakpoints in %s', (v) => {
  it('stop a run on the game tick the cell rises, with the power before and after', () => {
    const sim = build(v, cells);
    sim.setBreakpoints([bp('r', D1, 'rising'), bp('a', D2, 'atLeast', 14), bp('never', D2, 'atLeast', 15)]);
    sim.input(toggle(LEVER_AT));
    const r = sim.run(50, 'breakpoint');
    expect([r.tick, r.ranTicks]).toEqual([4, 4]);
    expect(r.hits).toEqual([
      { id: 'r', tick: 4, at: D1, before: 0, after: 15 },
      { id: 'a', tick: 4, at: D2, before: 0, after: 14 },
    ]);
    // The torch goes out 2 game ticks later; nothing else hits on the way to idle.
    expect(sim.run(50, 'breakpoint')).toMatchObject({ tick: 54, ranTicks: 50, hits: [] });
  });

  it('report shown states for change and state, and falling power', () => {
    const sim = build(v, cells);
    const on = (REPEATER_ON << 4) | READS_WEST | 4;
    const off = (REPEATER << 4) | READS_WEST | 4;
    sim.setBreakpoints([bp('c', LEVER_AT, 'change'), bp('s', REP, 'state', on), bp('f', AFTER, 'falling')]);
    sim.input(toggle(LEVER_AT));
    expect(sim.run(50, 'breakpoint').hits).toEqual([
      { id: 'c', tick: 1, at: LEVER_AT, before: (LEVER << 4) | STICKS_EAST, after: (LEVER << 4) | STICKS_EAST | 8 },
    ]);
    expect(sim.run(50, 'breakpoint').hits).toEqual([{ id: 's', tick: 4, at: REP, before: off, after: on }]);
    expect(sim.run(50, 'breakpoint').hits).toEqual([{ id: 'f', tick: 6, at: AFTER, before: 15, after: 0 }]);
  });

  it('end a redstone tick after one game tick when it hits on the first; the next runs two again', () => {
    const sim = build(v, cells);
    sim.setBreakpoints([bp('r', D1, 'rising')]);
    sim.input(toggle(LEVER_AT));
    sim.stepGameTick();
    sim.stepRedstoneTick();
    expect(sim.stepRedstoneTick()).toMatchObject({ tick: 4, ranTicks: 1, hits: [{ id: 'r', tick: 4 }] });
    expect(sim.stepRedstoneTick()).toMatchObject({ tick: 6, ranTicks: 2, hits: [] });
  });

  it('never hit while disabled, and keep their own copy of the list', () => {
    const sim = build(v, cells);
    const list = [{ ...bp('r', D1, 'rising'), enabled: false }];
    sim.setBreakpoints(list);
    list[0]!.enabled = true;
    sim.input(toggle(LEVER_AT));
    expect(sim.run(20, 'breakpoint')).toMatchObject({ tick: 20, hits: [] });
  });

  it('never hit on a rewind; afterwards they compare from the tick it went back to', () => {
    const sim = build(v, cells);
    sim.setBreakpoints([bp('r', D1, 'rising')]);
    sim.input(toggle(LEVER_AT));
    expect(sim.run(50, 'breakpoint').tick).toBe(4);
    sim.run(5, 'breakpoint');
    expect(sim.stepBack(6)).toMatchObject({ tick: 3, hits: [] });
    expect(sim.stepGameTick()).toMatchObject({ tick: 4, hits: [{ id: 'r', tick: 4, before: 0, after: 15 }] });
    // A breakpoint set on a powered cell starts from its value now: it needs a fall and a rise to hit.
    sim.setBreakpoints([bp('r2', D1, 'rising')]);
    expect(sim.run(20, 'breakpoint').hits).toEqual([]);
  });

  it('stay through a reset', () => {
    const sim = build(v, cells);
    sim.setBreakpoints([bp('r', D1, 'rising')]);
    sim.input(toggle(LEVER_AT));
    sim.run(20, 'idle');
    sim.reset();
    sim.input(toggle(LEVER_AT));
    expect(sim.run(50, 'breakpoint').hits).toMatchObject([{ id: 'r', tick: 4 }]);
  });
});

describe.each(VERSIONS)('watched cells in %s', (v) => {
  it('are sampled at the end of every game tick from the tick they are watched', () => {
    const sim = build(v, cells);
    const far: Pos = [40, 3, 40];
    sim.watch([D1, far]);
    expect(sim.trace(D1, 0, 100)).toEqual([{ tick: 0, power: 0, powered: false, state: DUST << 4 }]);
    sim.input(toggle(LEVER_AT));
    sim.run(6, 'breakpoint');
    const trace = sim.trace(D1, 0, 100);
    expect(trace.map((s) => [s.tick, s.power])).toEqual([
      [0, 0],
      [1, 0],
      [2, 0],
      [3, 0],
      [4, 15],
      [5, 15],
      [6, 15],
    ]);
    expect(trace[4]).toEqual({ tick: 4, power: 15, powered: true, state: (DUST << 4) | 15 });
    expect(sim.trace(D1, 2, 4).map((s) => s.tick)).toEqual([2, 3, 4]);
    expect(sim.trace(far, 0, 100)).toHaveLength(7);
    expect(sim.trace(far, 0, 100).every((s) => s.state === 0 && s.power === 0)).toBe(true);
    // An unwatched cell has no trace.
    expect(sim.trace(D2, 0, 100)).toEqual([]);
  });

  it('lose their samples when they leave the list, and start again from now', () => {
    const sim = build(v, cells);
    sim.watch([D1]);
    sim.run(5, 'breakpoint');
    sim.watch([D2]);
    expect(sim.trace(D1, 0, 100)).toEqual([]);
    sim.watch([D2, D1]);
    expect(sim.trace(D1, 0, 100).map((s) => s.tick)).toEqual([5]);
    expect(sim.trace(D2, 0, 100).map((s) => s.tick)).toEqual([5]);
  });

  it('are traced only as far back as history reaches', () => {
    const sim = build(v, cells, { historyTicks: 10 });
    sim.watch([D1]);
    sim.run(30, 'breakpoint');
    expect(sim.trace(D1, 0, 30).map((s) => s.tick)).toEqual(Array.from({ length: 11 }, (_, i) => 20 + i));
  });

  it('give the caller copies', () => {
    const sim = build(v, cells);
    sim.watch([D1]);
    const first = sim.trace(D1, 0, 0)[0] as { power: number };
    first.power = 9;
    expect(sim.trace(D1, 0, 0)[0]!.power).toBe(0);
  });
});

describe.each(VERSIONS)('explain in %s', (v) => {
  it('walks from a cell back to its source', () => {
    const sim = build(v, cells);
    expect(sim.explain(AFTER)).toEqual([
      { at: AFTER, why: 'Redstone dust at 15, fed from the west' },
      { at: TORCH_AT, why: 'Redstone torch, lit: the block it hangs on is not powered' },
    ]);
    expect(sim.explain(D2)).toEqual([]);
    sim.input(toggle(LEVER_AT));
    sim.run(20, 'idle');
    expect(sim.explain(K)).toEqual([
      { at: K, why: 'Block, strongly powered to 14 from the west' },
      { at: D2, why: 'Redstone dust at 14, from the dust to the west' },
      { at: D1, why: 'Redstone dust at 15, fed from the west' },
      { at: REP, why: 'Repeater, on: reads from the west' },
      { at: LEVER_AT, why: 'Lever, giving 15: the source' },
    ]);
    expect(sim.explain(AFTER)).toEqual([]);
  });

  it('says why a repeater is still on when its input has gone', () => {
    const sim = build(v, cells);
    sim.input(toggle(LEVER_AT));
    sim.run(20, 'idle');
    sim.input(toggle(LEVER_AT));
    sim.stepGameTick();
    expect(sim.explain(D1)).toEqual([
      { at: D1, why: 'Redstone dust at 15, fed from the west' },
      { at: REP, why: 'Repeater, on: its input has gone; it turns off when its scheduled tick runs' },
    ]);
  });
});

it('explains a comparator reading a container through a block, and a lamp it lights (1.12.2)', () => {
  const sim = build(
    '1.12.2',
    [
      cell([-2, 0, 0], CHEST),
      cell([-1, 0, 0], STONE),
      cell([0, 0, 0], COMPARATOR, READS_WEST | 4),
      cell([1, 0, 0], DUST),
      cell([2, 0, 0], LAMP),
    ],
    { extras: [{ at: [-2, 0, 0], level: 9 }] },
  );
  expect(sim.explain([2, 0, 0])).toEqual([
    { at: [2, 0, 0], why: 'Redstone lamp, powered to 9 from the west' },
    { at: [1, 0, 0], why: 'Redstone dust at 9, fed from the west' },
    { at: [0, 0, 0], why: 'Comparator (subtract) putting out 9: reads the container two cells to the west' },
    { at: [-2, 0, 0], why: 'Container, filled to level 9: the source' },
  ]);
});
