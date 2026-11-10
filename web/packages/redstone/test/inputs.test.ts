/** Levers, buttons and pressure plates: what they power, and how long a button or plate stays down. */
import { describe, expect, it } from 'vitest';
import type { InputEvent, Pos } from '../src/api.js';
import {
  DUST,
  LEVER,
  LIGHT_PLATE,
  ON,
  STICKS_EAST,
  STONE,
  STONE_BUTTON,
  STONE_PLATE,
  VERSIONS,
  WOODEN_BUTTON,
  WOODEN_PLATE,
  build,
  cell,
  edges,
  play,
  toggle,
} from './circuit.js';

const press = (at: Pos): InputEvent => ({ kind: 'press', at });
const hold = (at: Pos, on: boolean): InputEvent => ({ kind: 'hold', at, on });
const level = (at: Pos, l: number): InputEvent => ({ kind: 'level', at, level: l });

/** A part at P sticking out east of block K at (0,0,0), with dust south of it to read its power. */
const P: Pos = [1, 0, 0];
const READ: Pos = [1, 0, 1];

describe.each(VERSIONS)('inputs in %s', (v) => {
  it('a lever weakly powers every cell around it and strongly the block it sits on', () => {
    const sim = build(v, [
      cell([0, 0, 0], STONE),
      cell(P, LEVER, STICKS_EAST | ON),
      cell([2, 0, 0], STONE),
      cell([3, 0, 0], DUST),
      cell(READ, DUST),
      cell([-1, 0, 0], DUST),
    ]);
    expect(sim.inputAt(P)).toBe('lever');
    expect(sim.inputAt([0, 0, 0])).toBeNull();
    expect([sim.powerAt(P), sim.powerAt(READ)]).toEqual([15, 15]);
    // Strong into its block, which passes it on to the dust beyond.
    expect([sim.powerAt([0, 0, 0]), sim.powerAt([-1, 0, 0])]).toEqual([15, 15]);
    // Weak into the block beside it, which passes nothing on.
    expect([sim.powerAt([2, 0, 0]), sim.powerAt([3, 0, 0])]).toEqual([0, 0]);

    sim.input(toggle(P));
    const r = sim.stepGameTick();
    expect(r.changed.map((c) => [c[0], c[1], c[2], c[3], c[4]])).toContainEqual([1, 0, 0, LEVER, STICKS_EAST]);
    expect([sim.powerAt(READ), sim.powerAt([-1, 0, 0])]).toEqual([0, 0]);
    // An event of the wrong kind does nothing.
    sim.input(press(P));
    sim.stepGameTick();
    expect(sim.powerAt(READ)).toBe(0);
  });

  it('a stone button lets go 20 game ticks after the press; a second press while down does nothing', () => {
    const sim = build(v, [cell([0, 0, 0], STONE), cell(P, STONE_BUTTON, STICKS_EAST), cell(READ, DUST)]);
    expect(sim.inputAt(P)).toBe('button');
    const power = play(sim, 60, { 1: [press(P)], 10: [press(P)], 25: [press(P)] }, (s) => s.powerAt(READ));
    expect(edges(power, true)).toEqual([1, 25]);
    expect(edges(power, false)).toEqual([20, 44]);
    expect(sim.powerAt([0, 0, 0])).toBe(0);
    expect(sim.idle).toBe(true);
  });

  it.each([
    ['stone', STONE_PLATE],
    ['wooden', WOODEN_PLATE],
  ])('a %s plate goes down at once and comes up at its next look, every 20 game ticks', (_name, id) => {
    const at: Pos = [0, 0, 0];
    const sim = build(v, [cell(at, id), cell([1, 0, 0], DUST)]);
    expect(sim.inputAt(at)).toBe('plate');
    const power = play(
      sim,
      120,
      { 1: [hold(at, true)], 5: [hold(at, false)], 25: [hold(at, true)], 70: [hold(at, false)] },
      (s) => s.powerAt([1, 0, 0]),
    );
    // Down from tick 1; let go at 5 but seen only at the look on 20. Down again at 25; looks at 44, 64, 84.
    expect(edges(power, true)).toEqual([1, 25]);
    expect(edges(power, false)).toEqual([20, 84]);
    expect(sim.idle).toBe(true);
  });

  it('a plate strongly powers the block under it', () => {
    const sim = build(v, [cell([0, 0, 0], STONE_PLATE)]);
    sim.input(hold([0, 0, 0], true));
    sim.stepGameTick();
    expect([sim.powerAt([0, 0, 0]), sim.powerAt([0, -1, 0]), sim.powerAt([1, -1, 0])]).toEqual([15, 15, 0]);
  });
});

describe('1.12.2 inputs', () => {
  it('a wooden button lets go after 30 game ticks', () => {
    const sim = build('1.12.2', [cell([0, 0, 0], STONE), cell(P, WOODEN_BUTTON, STICKS_EAST), cell(READ, DUST)]);
    const power = play(sim, 50, { 1: [press(P)] }, (s) => s.powerAt(READ));
    expect(edges(power, false)).toEqual([30]);
  });

  it('a weighted plate takes a new level at its next look, every 10 game ticks', () => {
    const at: Pos = [0, 0, 0];
    const sim = build('1.12.2', [cell(at, LIGHT_PLATE), cell([1, 0, 0], DUST)]);
    expect(sim.inputAt(at)).toBe('level');
    const power = play(sim, 40, { 1: [level(at, 4)], 3: [level(at, 9)], 12: [level(at, 0)] }, (s) =>
      s.powerAt([1, 0, 0]),
    );
    expect([power.get(1), power.get(9), power.get(10), power.get(19), power.get(20)]).toEqual([4, 4, 9, 9, 0]);
    expect(sim.powerAt(at)).toBe(0);
  });
});
