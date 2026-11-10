/**
 * The parts that take power and the manual sources (report 4.2, C14.3, C14.4, C14.8, simplifications 4 and 5): lamp,
 * doors, trapdoors, fence gates, dispensers, droppers, note blocks, TNT, hopper, observer, redstone block, daylight
 * sensor, trapped chest and detector rail, in each version that has them.
 */
import { describe, expect, it } from 'vitest';
import type { Pos, RedstoneVersion } from '../src/api.js';
import {
  COMPARATOR,
  DAYLIGHT,
  DETECTOR_RAIL,
  DISPENSER,
  DROPPER,
  DUST,
  FENCE_GATE,
  FLOOR,
  HOPPER,
  LAMP,
  LEVER,
  NOTE_BLOCK,
  OBSERVER,
  ON,
  READS_NORTH,
  READS_WEST,
  REDSTONE_BLOCK,
  REPEATER,
  STICKS_WEST,
  STONE,
  TNT,
  TRAPDOOR,
  VERSIONS,
  WOODEN_DOOR,
  build,
  cell,
  drive,
  edges,
  idAt,
  play,
  toggle,
} from './circuit.js';

const TRAPPED_CHEST = 146;
const X: Pos = [0, 0, 0];
/** A floor lever west of the part: weak power only. */
const L: Pos = [-1, 0, 0];
const lever = cell(L, LEVER, FLOOR);
const kinds = (events: ReadonlyArray<{ kind: string; tick: number }>) => events.map((e) => [e.kind, e.tick]);

describe('the lamp (1.12.2)', () => {
  it('lights at once and goes out 4 game ticks after the power goes', () => {
    const sim = build('1.12.2', [cell(X, LAMP), lever]);
    const lit = play(sim, 20, { 1: [toggle(L)], 11: [toggle(L)] }, (s) => (idAt(s, X) === 124 ? 1 : 0));
    expect(edges(lit, true)).toEqual([1]);
    expect(edges(lit, false)).toEqual([14]);
  });

  it('stays lit through a power gap shorter than 4 game ticks', () => {
    const sim = build('1.12.2', [cell(X, LAMP), cell(L, LEVER, FLOOR | ON)]);
    const lit = play(sim, 20, { 3: [toggle(L)], 5: [toggle(L)] }, (s) => (idAt(s, X) === 124 ? 1 : 0));
    expect(edges(lit, false)).toEqual([]);
  });

  it('is not in b1.8.1', () => {
    expect(
      build('b1.8.1', [cell(X, LAMP)])
        .diagnostics()
        .map((d) => d.code),
    ).toEqual(['notInVersion']);
  });
});

describe('doors', () => {
  const door = [cell(X, WOODEN_DOOR, 0), cell([0, 1, 0], WOODEN_DOOR, 8)];
  const isOpen = (s: { stateAt(at: Pos): number }) => ((s.stateAt(X) & 4) !== 0 ? 1 : 0);

  it.each(VERSIONS)('%s: dust leading to the door opens and closes it', (v) => {
    const sim = build(v, [...door, cell([-2, 0, 0], LEVER, FLOOR), cell(L, DUST)]);
    const open = play(sim, 20, { 1: [toggle([-2, 0, 0])], 11: [toggle([-2, 0, 0])] }, isOpen);
    expect(edges(open, true)).toEqual([1]);
    expect(edges(open, false)).toEqual([11]);
  });

  const viaRepeater = (v: RedstoneVersion) => {
    const sim = build(v, [...door, cell([-2, 0, 0], LEVER, FLOOR), cell(L, REPEATER, READS_WEST)]);
    return play(sim, 20, { 1: [toggle([-2, 0, 0])] }, isOpen);
  };

  it('b1.8.1: a repeater pointing at the door does nothing, because a repeater is no power-source block', () => {
    expect(edges(viaRepeater('b1.8.1'), true)).toEqual([]);
  });

  it('1.12.2: a repeater pointing at the door opens it', () => {
    expect(edges(viaRepeater('1.12.2'), true)).toEqual([2]);
  });

  it.each(VERSIONS)('%s: the upper half follows and a door with nothing under it is left out', (v) => {
    const sim = build(v, [...door, lever]);
    drive(sim, 5, { 1: [toggle(L)] });
    expect(sim.stateAt([0, 1, 0]) & 8).toBe(8);
    expect(sim.powerAt([0, 1, 0])).toBe(15);
    const floating = build(v, [cell([0, 3, 0], WOODEN_DOOR, 0), cell([0, 4, 0], WOODEN_DOOR, 8)]);
    expect(floating.diagnostics().map((d) => d.code)).toEqual(['floating', 'floating']);
  });
});

describe('trapdoors and fence gates', () => {
  it.each(VERSIONS)('%s: a trapdoor opens with power', (v) => {
    // Meta 0 hangs on the block south of it (b1.8.1 needs one).
    const sim = build(v, [cell(X, TRAPDOOR, 0), cell([0, 0, 1], STONE), lever]);
    const open = play(sim, 10, { 1: [toggle(L)] }, (s) => ((s.stateAt(X) & 4) !== 0 ? 1 : 0));
    expect(edges(open, true)).toEqual([1]);
  });

  it('b1.8.1: a fence gate ignores power; 1.12.2: it opens', () => {
    const gate = (v: RedstoneVersion) => {
      const sim = build(v, [cell(X, FENCE_GATE, 0), lever]);
      drive(sim, 5, { 1: [toggle(L)] });
      return sim.stateAt(X) & 15;
    };
    expect(gate('b1.8.1')).toBe(0);
    expect(gate('1.12.2')).toBe(4 | 8);
  });
});

describe('dispensers and droppers', () => {
  // A second lever on the dispenser's other side: switching it is an update from a power source while powered.
  const L2: Pos = [1, 0, 0];
  const rig = (v: RedstoneVersion, id: number) => build(v, [cell(X, id, 2), lever, cell(L2, LEVER, FLOOR)]);
  const inputs = { 1: [toggle(L)], 11: [toggle(L2)], 21: [toggle(L2)] };

  it('b1.8.1: fires 4 game ticks after it is powered, and again on every update from a power source while held', () => {
    expect(kinds(drive(rig('b1.8.1', DISPENSER), 40, inputs))).toEqual([
      ['fired', 4],
      ['fired', 14],
      ['fired', 24],
    ]);
  });

  it.each([DISPENSER, DROPPER])('1.12.2: block %i fires once per rising edge', (id) => {
    const sim = rig('1.12.2', id);
    expect(kinds(drive(sim, 40, inputs))).toEqual([['fired', 4]]);
    expect(kinds(drive(sim, 60, { 41: [toggle(L)], 42: [toggle(L2)], 51: [toggle(L)] }))).toEqual([['fired', 45]]);
  });

  it('is powered through the cell above it too', () => {
    for (const v of VERSIONS) {
      const sim = build(v, [cell(X, DISPENSER, 2), cell([0, 1, 0], STONE), cell([-1, 1, 0], LEVER, STICKS_WEST)]);
      expect(kinds(drive(sim, 10, { 1: [toggle([-1, 1, 0])] }))).toEqual([['fired', 4]]);
    }
  });

  it('holds a fill level for comparators in 1.12.2 only', () => {
    expect(build('1.12.2', [cell(X, DISPENSER, 2)]).inputAt(X)).toBe('level');
    expect(build('b1.8.1', [cell(X, DISPENSER, 2)]).inputAt(X)).toBeNull();
  });
});

describe('note blocks', () => {
  const play1 = (v: RedstoneVersion, leverCell: ReturnType<typeof cell>, at: Pos) => {
    const sim = build(v, [cell(X, NOTE_BLOCK), leverCell]);
    return kinds(drive(sim, 10, { 1: [toggle(at)], 5: [toggle(at)] }));
  };

  it('b1.8.1 needs strong power: a lever beside it does nothing, a lever on it plays it', () => {
    expect(play1('b1.8.1', lever, L)).toEqual([]);
    expect(play1('b1.8.1', cell(L, LEVER, STICKS_WEST), L)).toEqual([['played', 0]]);
  });

  it('1.12.2 plays on any power rising', () => {
    expect(play1('1.12.2', lever, L)).toEqual([['played', 0]]);
    expect(play1('1.12.2', cell(L, LEVER, STICKS_WEST), L)).toEqual([['played', 0]]);
  });
});

describe('TNT', () => {
  it.each(VERSIONS)('%s: primed and removed when powered, nothing explodes', (v) => {
    const sim = build(v, [cell(X, TNT), lever, cell([1, 0, 0], STONE)]);
    const events = drive(sim, 5, { 1: [toggle(L)] });
    expect(events.map((e) => [e.kind, e.at.join()])).toEqual([['primed', '0,0,0']]);
    expect(idAt(sim, X)).toBe(0);
    expect(idAt(sim, [1, 0, 0])).toBe(STONE);
  });

  it('b1.8.1: a repeater pointing at TNT does not prime it; 1.12.2 it does', () => {
    const viaRepeater = (v: RedstoneVersion) => {
      const sim = build(v, [cell(X, TNT), cell(L, REPEATER, READS_WEST), cell([-2, 0, 0], LEVER, FLOOR)]);
      return kinds(drive(sim, 10, { 1: [toggle([-2, 0, 0])] }));
    };
    expect(viaRepeater('b1.8.1')).toEqual([]);
    expect(viaRepeater('1.12.2')).toEqual([['primed', 2]]);
  });
});

describe('the observer (1.12.2)', () => {
  // It faces east, watching the lever's cell; dust behind it shows its pulse.
  const rig = () => build('1.12.2', [cell(X, OBSERVER, 5), cell([1, 0, 0], LEVER, FLOOR), cell(L, DUST)]);

  it('gives a 2 game tick pulse out of its back, 2 game ticks after the cell it faces changes', () => {
    const sim = rig();
    const power = play(sim, 20, { 1: [toggle([1, 0, 0])], 11: [toggle([1, 0, 0])] }, (s) => s.powerAt(L));
    expect(edges(power, true)).toEqual([2, 12]);
    expect(edges(power, false)).toEqual([4, 14]);
  });

  it('does not fire when the design is built, and sees state changes that tell no neighbour', () => {
    const sim = rig();
    expect(sim.powerAt(L)).toBe(0);
    expect(sim.idle).toBe(true);
    // A lamp it watches: the lamp's change is written without neighbour updates.
    const lampWatch = build('1.12.2', [
      cell(X, OBSERVER, 5),
      cell([1, 0, 0], LAMP),
      cell([2, 0, 0], LEVER, FLOOR),
      cell(L, DUST),
    ]);
    const power = play(lampWatch, 10, { 1: [toggle([2, 0, 0])] }, (s) => s.powerAt(L));
    expect(edges(power, true)).toEqual([2]);
  });

  it('is not in b1.8.1', () => {
    expect(
      build('b1.8.1', [cell(X, OBSERVER, 5)])
        .diagnostics()
        .map((d) => d.code),
    ).toEqual(['notInVersion']);
  });
});

describe('the hopper (1.12.2)', () => {
  it('is disabled while powered', () => {
    const sim = build('1.12.2', [cell(X, HOPPER, 0), lever]);
    const disabled = play(sim, 10, { 1: [toggle(L)], 5: [toggle(L)] }, (s) => s.stateAt(X) & 8);
    expect(edges(disabled, true)).toEqual([1]);
    expect(edges(disabled, false)).toEqual([5]);
  });
});

describe('manual sources (1.12.2)', () => {
  it('a redstone block gives 15 on every side and does not conduct', () => {
    const sim = build('1.12.2', [cell(X, REDSTONE_BLOCK), cell([1, 0, 0], DUST), cell([0, 0, 1], LAMP)]);
    expect(sim.powerAt([1, 0, 0])).toBe(15);
    expect(idAt(sim, [0, 0, 1])).toBe(124);
    expect(sim.powerAt(X)).toBe(15);
  });

  it('a daylight sensor gives the level the user sets', () => {
    const sim = build('1.12.2', [cell(X, DAYLIGHT), cell([1, 0, 0], DUST)], { extras: [{ at: X, level: 7 }] });
    expect(sim.powerAt([1, 0, 0])).toBe(7);
    sim.input({ kind: 'level', at: X, level: 12 });
    sim.stepGameTick();
    expect(sim.powerAt([1, 0, 0])).toBe(12);
    expect(sim.inputAt(X)).toBe('level');
  });

  it('a trapped chest powers by how many look in, strongly into the block below, and feeds a comparator', () => {
    const sim = build('1.12.2', [
      cell(X, TRAPPED_CHEST),
      cell([1, 0, 0], DUST),
      cell([0, 0, 1], COMPARATOR, READS_NORTH),
    ]);
    sim.input({ kind: 'level', at: X, level: 4 });
    sim.run(5, 'idle');
    expect([sim.powerAt([1, 0, 0]), sim.powerAt([0, -1, 0]), sim.powerAt([0, 0, 1])]).toEqual([4, 4, 4]);
  });
});

describe.each(VERSIONS)('the detector rail in %s', (v) => {
  it('is held down and let go like a plate, strongly powering the block below', () => {
    const sim = build(v, [
      cell([0, 1, 0], STONE),
      cell([1, 1, 0], STONE),
      cell([0, 2, 0], DETECTOR_RAIL, 0),
      cell([1, 2, 0], DUST),
    ]);
    expect(sim.inputAt([0, 2, 0])).toBe('plate');
    sim.input({ kind: 'hold', at: [0, 2, 0], on: true });
    sim.stepGameTick();
    expect([sim.powerAt([1, 2, 0]) > 0, sim.powerAt([0, 1, 0])]).toEqual([true, 15]);
    sim.input({ kind: 'hold', at: [0, 2, 0], on: false });
    sim.stepGameTick();
    expect([sim.powerAt([1, 2, 0]), sim.powerAt([0, 1, 0])]).toEqual([0, 0]);
  });
});
