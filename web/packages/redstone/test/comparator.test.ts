/**
 * The comparator (report 4.2, C14.7): compare and subtract for every rear and side level, a container's fill read
 * directly and through one solid block, its 2 game tick delay, and its tick priority against a repeater. b1.8.1 has none.
 */
import { describe, expect, it } from 'vitest';
import type { Pos } from '../src/api.js';
import {
  CHEST,
  COMPARATOR,
  DUST,
  FLOOR,
  LEVER,
  LIGHT_PLATE,
  READS_WEST,
  REDSTONE_BLOCK,
  REPEATER,
  STICKS_NORTH,
  STONE,
  build,
  cell,
  edges,
  play,
  toggle,
} from './circuit.js';

const SUBTRACT = 4;
const C: Pos = [0, 0, 0];
const OUT: Pos = [1, 0, 0];
const levels = Array.from({ length: 16 }, (_, i) => i);

/**
 * A comparator reading west. Behind it a chest filled to `rear`; north of it dust fed by a light weighted plate held at
 * `side`; dust in front shows what it puts out.
 */
function rig(rear: number, side: number, mode: number) {
  return build(
    '1.12.2',
    [
      cell([-1, 0, 0], CHEST),
      cell(C, COMPARATOR, READS_WEST | mode),
      cell([0, 0, -1], DUST),
      cell([0, 0, -2], LIGHT_PLATE),
      cell(OUT, DUST),
    ],
    {
      extras: [
        { at: [-1, 0, 0], level: rear },
        { at: [0, 0, -2], level: side },
      ],
    },
  );
}

describe('the comparator (1.12.2)', () => {
  it('compare mode: the rear level while it is at least the side, else 0', () => {
    for (const rear of levels)
      for (const side of levels) {
        const sim = rig(rear, side, 0);
        expect([rear, side, sim.powerAt(C)]).toEqual([rear, side, rear >= side ? rear : 0]);
      }
  });

  it('subtract mode: rear minus side, never below 0', () => {
    for (const rear of levels)
      for (const side of levels) {
        const sim = rig(rear, side, SUBTRACT);
        expect([rear, side, sim.powerAt(C)]).toEqual([rear, side, Math.max(rear - side, 0)]);
      }
  });

  it('feeds dust in front at its level and shows powered in meta bit 8', () => {
    const sim = rig(9, 3, SUBTRACT);
    expect(sim.powerAt(OUT)).toBe(6);
    expect(sim.stateAt(C)).toBe((COMPARATOR << 4) | READS_WEST | SUBTRACT | 8);
    expect(rig(0, 3, SUBTRACT).stateAt(C)).toBe((COMPARATOR << 4) | READS_WEST | SUBTRACT);
  });

  it('reads a container through one solid block, unless that block is powered at 15', () => {
    const through = (leverOn: boolean) =>
      build(
        '1.12.2',
        [
          cell([-2, 0, 0], CHEST),
          cell([-1, 0, 0], STONE),
          cell([-1, 0, -1], LEVER, STICKS_NORTH | (leverOn ? 8 : 0)),
          cell(C, COMPARATOR, READS_WEST),
          cell(OUT, DUST),
        ],
        { extras: [{ at: [-2, 0, 0], level: 3 }] },
      ).powerAt(C);
    expect(through(false)).toBe(3);
    expect(through(true)).toBe(15);
  });

  it('follows a change of the fill level 2 game ticks later', () => {
    const sim = rig(5, 0, 0);
    const power = play(sim, 10, { 3: [{ kind: 'level', at: [-1, 0, 0], level: 9 }] }, (s) => s.powerAt(C));
    expect([power.get(3), power.get(4), power.get(5)]).toEqual([5, 9, 9]);
    expect(sim.inputAt([-1, 0, 0])).toBe('level');
  });

  it('changes 2 game ticks after its rear input, on and off', () => {
    const sim = build('1.12.2', [cell([-1, 0, 0], LEVER, FLOOR), cell(C, COMPARATOR, READS_WEST), cell(OUT, DUST)]);
    const power = play(sim, 30, { 1: [toggle([-1, 0, 0])], 21: [toggle([-1, 0, 0])] }, (s) => s.powerAt(C));
    expect(edges(power, true)).toEqual([2]);
    expect(edges(power, false)).toEqual([22]);
  });

  it('reads a redstone block on its side as 15 and a repeater pointing in by its strong power', () => {
    const side = (part: ReturnType<typeof cell>) =>
      build('1.12.2', [cell([-1, 0, 0], CHEST), cell(C, COMPARATOR, READS_WEST), part, cell(OUT, DUST)], {
        extras: [{ at: [-1, 0, 0], level: 14 }],
      });
    expect(side(cell([0, 0, -1], REDSTONE_BLOCK)).powerAt(C)).toBe(0);
    // A repeater north of it pointing south, fed by a lever that is on.
    const repeaterIn = side(cell([0, 0, -1], REPEATER, 2));
    expect(repeaterIn.powerAt(C)).toBe(14);
    const fed = build(
      '1.12.2',
      [
        cell([-1, 0, 0], CHEST),
        cell(C, COMPARATOR, READS_WEST),
        cell([0, 0, -1], REPEATER, 2),
        cell([0, 0, -2], LEVER, FLOOR | 8),
        cell(OUT, DUST),
      ],
      { extras: [{ at: [-1, 0, 0], level: 14 }] },
    );
    expect(fed.powerAt(C)).toBe(0);
  });

  it('asks with priority -1 when it faces a repeater that does not face back, else 0', () => {
    const ask = (front: ReturnType<typeof cell>) => {
      const sim = build('1.12.2', [cell([-1, 0, 0], LEVER, FLOOR), cell(C, COMPARATOR, READS_WEST), front]);
      sim.input(toggle([-1, 0, 0]));
      sim.stepGameTick();
      return sim.pendingAt(C);
    };
    expect(ask(cell(OUT, REPEATER, READS_WEST))).toEqual([{ dueTick: 2, priority: -1 }]);
    expect(ask(cell(OUT, DUST))).toEqual([{ dueTick: 2, priority: 0 }]);
  });

  it('drives the repeater it faces, and a powered repeater pointing into its side does not lock it', () => {
    const sim = build('1.12.2', [
      cell([-1, 0, 0], LEVER, FLOOR),
      cell(C, COMPARATOR, READS_WEST),
      cell(OUT, REPEATER, READS_WEST),
      cell([2, 0, 0], DUST),
      // A powered repeater pointing into the comparator's side would lock a repeater; a comparator ignores it.
      cell([0, 0, 1], REPEATER, 0),
      cell([0, 0, 2], LEVER, FLOOR | 8),
    ]);
    const power = play(sim, 20, { 1: [toggle([-1, 0, 0])] }, (s) => s.powerAt([2, 0, 0]));
    expect(edges(power, true)).toEqual([4]);
    expect(sim.lockedAt(C)).toBe(false);
  });
});

describe('the comparator in b1.8.1', () => {
  it('is not in the version', () => {
    const sim = build('b1.8.1', [cell(C, COMPARATOR, READS_WEST)]);
    expect(sim.diagnostics().map((d) => d.code)).toEqual(['notInVersion']);
    expect(sim.powerAt(C)).toBe(0);
  });
});
