/**
 * Pistons (report 4.1 phases 4 and 5, 4.2, 4.2a, C14.5, C14.9): the push limit, what does not move and what breaks,
 * the sticky pull and its drop on a 2 game tick pulse, quasi-connectivity, landing on the third update, b1.8.1's event
 * inside the update against 1.12.2's at the end of the tick, and b1.8.1's per-kind deafness.
 */
import { describe, expect, it } from 'vitest';
import type { Pos, RedstoneVersion } from '../src/api.js';
import {
  DUST,
  FLOOR,
  LEVER,
  OBSIDIAN,
  PISTON,
  READS_SOUTH,
  REPEATER,
  SLIME,
  STICKS_WEST,
  STICKY_PISTON,
  STONE,
  TORCH,
  TORCH_ON_NORTH,
  VERSIONS,
  build,
  cell,
  drive,
  edges,
  heldId,
  idAt,
  play,
  toggle,
} from './circuit.js';

const HEAD = 34;
const MOVING = 36;
const UP = 1;
const EAST = 5;
const EXTENDED = 8;
/** A lever's meta when it sticks out south (its block is north of it). */
const STICKS_SOUTH = 3;

const P: Pos = [0, 0, 0];
/** A floor lever north of the piston: it powers the piston directly. */
const L: Pos = [0, 0, -1];
const at = (x: number, y = 0, z = 0): Pos => [x, y, z];
const stone = (x: number) => cell(at(x), STONE);

/** A piston at P facing east, the lever beside it, and whatever is put in front of it. */
const rig = (v: RedstoneVersion, front: ReturnType<typeof cell>[], id = PISTON) =>
  build(v, [cell(P, id, EAST), cell(L, LEVER, FLOOR), ...front]);

describe.each(VERSIONS)('the piston in %s', (v) => {
  it('pushes a row of 12 blocks and refuses 13', () => {
    const twelve = rig(
      v,
      Array.from({ length: 12 }, (_, i) => stone(i + 1)),
    );
    const events12 = drive(twelve, 10, { 1: [toggle(L)] });
    expect(idAt(twelve, at(1))).toBe(HEAD);
    expect(idAt(twelve, at(13))).toBe(STONE);
    expect(twelve.stateAt(P)).toBe((PISTON << 4) | EAST | EXTENDED);
    expect(events12).toEqual([]);

    const thirteen = rig(
      v,
      Array.from({ length: 13 }, (_, i) => stone(i + 1)),
    );
    const events13 = drive(thirteen, 10, { 1: [toggle(L)] });
    expect(idAt(thirteen, at(1))).toBe(STONE);
    expect(idAt(thirteen, at(14))).toBe(0);
    expect(thirteen.stateAt(P)).toBe((PISTON << 4) | EAST);
    expect(events13.map((e) => [e.kind, e.at.join()])).toEqual([['blocked', '0,0,0']]);
  });

  it('cannot move obsidian, also behind another block', () => {
    for (const front of [[cell(at(1), OBSIDIAN)], [stone(1), cell(at(2), OBSIDIAN)]]) {
      const sim = rig(v, front);
      drive(sim, 10, { 1: [toggle(L)] });
      expect(idAt(sim, at(1))).not.toBe(HEAD);
      expect(sim.idle).toBe(true);
    }
  });

  it('breaks the dust in its path and says so', () => {
    const sim = rig(v, [stone(1), cell(at(2), DUST)]);
    const events = drive(sim, 10, { 1: [toggle(L)] });
    expect([idAt(sim, at(1)), idAt(sim, at(2)), idAt(sim, at(3))]).toEqual([HEAD, STONE, 0]);
    expect(events.map((e) => [e.kind, e.at.join(), e.note])).toEqual([
      ['destroyed', '2,0,0', 'Redstone dust: broken by a piston'],
    ]);
  });

  it('lands a move on its third update, showing the block at its destination meanwhile', () => {
    const sim = rig(v, [stone(1)]);
    const held = play(sim, 4, { 1: [toggle(L)] }, (s) => heldId(s, at(2)));
    expect([...held.values()]).toEqual([0, MOVING, MOVING, STONE, STONE]);
    const shown = play(rig(v, [stone(1)]), 3, { 1: [toggle(L)] }, (s) => idAt(s, at(2)));
    expect([...shown.values()]).toEqual([0, STONE, STONE, STONE]);
    // A block in transit is not solid and gives no power.
    const mid = rig(v, [stone(1)]);
    drive(mid, 1, { 1: [toggle(L)] });
    expect(mid.engine.solid(mid.engine.grid.indexOf(at(2)))).toBe(false);
  });

  it('retracts and leaves the block it pushed (a plain piston)', () => {
    const sim = rig(v, [stone(1)]);
    drive(sim, 30, { 1: [toggle(L)], 11: [toggle(L)] });
    expect([idAt(sim, at(1)), idAt(sim, at(2))]).toEqual([0, STONE]);
    expect(sim.stateAt(P)).toBe((PISTON << 4) | EAST);
    expect(sim.idle).toBe(true);
  });

  describe('the sticky piston', () => {
    const pulse = (ticks: number) => {
      const sim = rig(v, [stone(1)], STICKY_PISTON);
      drive(sim, 40, { 1: [toggle(L)], [1 + ticks]: [toggle(L)] });
      return [idAt(sim, at(1)), idAt(sim, at(2))];
    };

    it('pulls its block back after a long pulse', () => {
      expect(pulse(10)).toEqual([STONE, 0]);
    });

    it('drops the block on a 2 game tick pulse: it is still on its way out when the piston retracts', () => {
      expect(pulse(2)).toEqual([0, STONE]);
    });

    it('pulls the block back on a 3 and a 4 game tick pulse: it has landed by then', () => {
      expect(pulse(3)).toEqual([STONE, 0]);
      expect(pulse(4)).toEqual([STONE, 0]);
    });

    it('leaves a block it does not pull (obsidian) and its head goes', () => {
      const sim = rig(v, [cell(at(2), OBSIDIAN)], STICKY_PISTON);
      drive(sim, 30, { 1: [toggle(L)], 11: [toggle(L)] });
      expect([idAt(sim, at(1)), idAt(sim, at(2))]).toEqual([0, OBSIDIAN]);
      expect(sim.stateAt(P)).toBe((STICKY_PISTON << 4) | EAST);
    });
  });

  describe('quasi-connectivity', () => {
    // A lever on the west face of a block beside the cell above the piston: that block powers the cell above.
    const qc = [cell(at(-1, 1), STONE), cell(at(-2, 1), LEVER, STICKS_WEST)];
    const QL = at(-2, 1);
    // A dust line south of the piston that runs past it: it never points at the piston, but its change updates it.
    const line = [
      cell(at(-2, 0, 1), LEVER, FLOOR),
      cell(at(-1, 0, 1), DUST),
      cell(at(0, 0, 1), DUST),
      cell(at(1, 0, 1), DUST),
    ];

    it('is powered through the cell above but stays put until an update reaches it', () => {
      const sim = build(v, [cell(P, PISTON, EAST), ...qc, ...line]);
      drive(sim, 10, { 1: [toggle(QL)] });
      expect(idAt(sim, at(1))).toBe(0);
      expect(sim.powerAt(P)).toBe(15);

      drive(sim, 20, { 11: [toggle(at(-2, 0, 1))] });
      expect(idAt(sim, at(1))).toBe(HEAD);
      // b1.8.1 moves it while the dust is telling its neighbours, where the game's half-updated dust could differ.
      const flagged = sim.diagnostics().map((d) => [d.code, d.at.join()]);
      expect(flagged).toEqual(v === 'b1.8.1' ? [['updateOrder', '0,0,0']] : []);
    });

    it('does not move for the dust line alone', () => {
      const sim = build(v, [cell(P, PISTON, EAST), ...qc, ...line]);
      drive(sim, 20, { 11: [toggle(at(-2, 0, 1))] });
      expect(idAt(sim, at(1))).toBe(0);
      expect(sim.powerAt(P)).toBe(0);
    });
  });

  it('keeps every move in the keyframe: a copy taken mid-move replays the same, and reset goes back', () => {
    const sim = rig(v, [stone(1), stone(2)], STICKY_PISTON);
    drive(sim, 2, { 1: [toggle(L)] });
    const mid = sim.engine.save();
    const trace = () => [...play(sim, 12, { 6: [toggle(L)] }, (s) => s.displayCells().length * 100 + heldId(s, at(2)))];
    const first = trace();
    sim.engine.load(mid);
    expect(trace()).toEqual(first);
    sim.reset();
    expect(sim.displayCells()).toEqual([]);
    expect(sim.idle).toBe(true);
  });

  it('can push a piston that is not extended, and that piston looks at its power where it lands', () => {
    // The second piston faces up. Where it lands a lever that is on powers it, and it extends.
    const sim = rig(v, [cell(at(1), PISTON, UP), cell(at(2, 0, -1), LEVER, FLOOR | 8)]);
    expect(sim.stateAt(at(1))).toBe((PISTON << 4) | UP);
    drive(sim, 20, { 1: [toggle(L)] });
    expect(sim.stateAt(at(2))).toBe((PISTON << 4) | UP | EXTENDED);
    expect(idAt(sim, at(2, 1))).toBe(HEAD);
  });
});

describe('when the move starts (C14.9, 4.1 phase 4)', () => {
  // The piston pushes B east. A lever on B's south face is switched in the same tick, after the piston's lever; a
  // repeater north of B reads B. In b1.8.1 B is already in transit (no power passes), in 1.12.2 it moves at the end
  // of the tick, so the switched lever powers B first and the repeater fires.
  const R: Pos = [1, 0, -1];
  const L3: Pos = [1, 0, 1];
  const cells = [
    cell(P, PISTON, EAST),
    cell(L, LEVER, FLOOR),
    stone(1),
    cell(L3, LEVER, STICKS_SOUTH),
    cell(R, REPEATER, READS_SOUTH),
    cell(at(1, 0, -2), DUST),
  ];
  const run = (v: RedstoneVersion) => {
    const sim = build(v, cells);
    const out = play(sim, 10, { 1: [toggle(L), toggle(L3)] }, (s) => s.powerAt(at(1, 0, -2)));
    return { sim, out };
  };

  it('b1.8.1 runs the piston inside the lever update: the repeater never sees B powered', () => {
    const { sim, out } = run('b1.8.1');
    expect(edges(out, true)).toEqual([]);
    expect(idAt(sim, at(2))).toBe(STONE);
  });

  it('1.12.2 runs it at the end of the tick: B is powered first, the repeater gives a pulse', () => {
    const { out } = run('1.12.2');
    expect(edges(out, true)).toEqual([2]);
    expect(edges(out, false)).toEqual([4]);
  });

  it('b1.8.1 also lets the lever drop at once: switching it tells its own block, which is gone', () => {
    const sim = build('b1.8.1', cells);
    const events = drive(sim, 1, { 1: [toggle(L), toggle(L3)] });
    expect(idAt(sim, L3)).toBe(0);
    expect(events.map((e) => [e.kind, e.at.join(), e.tick])).toEqual([['destroyed', '1,0,1', 0]]);
  });

  it('1.12.2 tells the cells around a block that left; b1.8.1 tells nobody until the move lands', () => {
    // A torch hangs on the south face of the block the piston pushes.
    const torchAt: Pos = [1, 0, 1];
    const drops = (v: RedstoneVersion) => {
      const sim = rig(v, [stone(1), cell(torchAt, TORCH, TORCH_ON_NORTH)]);
      return drive(sim, 6, { 1: [toggle(L)] }).map((e) => [e.kind, e.at.join(), e.tick]);
    };
    expect(drops('1.12.2')).toEqual([['destroyed', '1,0,1', 1]]);
    expect(drops('b1.8.1')).toEqual([['destroyed', '1,0,1', 3]]);
  });

  it('1.12.2 drops a move whose piston lost its power within the same tick, and flags it', () => {
    const sim = rig('1.12.2', [stone(1)]);
    drive(sim, 10, { 1: [toggle(L), toggle(L)] });
    expect(idAt(sim, at(1))).toBe(STONE);
    expect(sim.diagnostics().map((d) => [d.code, d.at.join()])).toEqual([['updateOrder', '0,0,0']]);
  });

  it('b1.8.1 moves at once on both flips of that one tick: out and straight back in', () => {
    const sim = rig('b1.8.1', [stone(1)]);
    drive(sim, 10, { 1: [toggle(L), toggle(L)] });
    expect([idAt(sim, at(1)), idAt(sim, at(2))]).toEqual([0, STONE]);
    expect(sim.stateAt(P)).toBe((PISTON << 4) | EAST);
  });
});

describe("b1.8.1's per-kind deafness (C14.5)", () => {
  // P2, south of P1, is powered through the cell above it and stuck. P1's extension tells its neighbours, P2 among them.
  const P2: Pos = [0, 0, 1];
  const QL: Pos = [-2, 1, 1];
  const cells = (first: number, second: number) => [
    cell(P, first, EAST),
    cell(L, LEVER, FLOOR),
    cell(P2, second, EAST),
    cell(at(-1, 1, 1), STONE),
    cell(QL, LEVER, STICKS_WEST),
  ];
  const run = (v: RedstoneVersion, first: number, second: number) => {
    const sim = build(v, cells(first, second));
    drive(sim, 5, { 1: [toggle(QL)] });
    expect(idAt(sim, at(1, 0, 1))).toBe(0);
    drive(sim, 20, { 6: [toggle(L)] });
    return sim;
  };

  it('a piston of the same kind misses the update while the first one moves, and it is flagged', () => {
    const sim = run('b1.8.1', PISTON, PISTON);
    expect(idAt(sim, at(1))).toBe(HEAD);
    expect(idAt(sim, at(1, 0, 1))).toBe(0);
    expect(sim.diagnostics().map((d) => [d.code, d.at.join()])).toEqual([['updateOrder', '0,0,1']]);
  });

  it('a piston of the other kind hears it and moves', () => {
    const sim = run('b1.8.1', PISTON, STICKY_PISTON);
    expect(idAt(sim, at(1, 0, 1))).toBe(HEAD);
    expect(sim.diagnostics()).toEqual([]);
  });

  it('1.12.2 has no such flag: the second piston moves', () => {
    const sim = run('1.12.2', PISTON, PISTON);
    expect(idAt(sim, at(1, 0, 1))).toBe(HEAD);
  });
});

describe('slime (1.12.2)', () => {
  it('moves as a plain block and is reported', () => {
    const sim = rig('1.12.2', [cell(at(1), SLIME), cell(at(1, 1), STONE)]);
    drive(sim, 10, { 1: [toggle(L)] });
    expect([idAt(sim, at(2)), idAt(sim, at(1, 1))]).toEqual([SLIME, STONE]);
    expect(sim.diagnostics().map((d) => [d.code, d.at.join()])).toEqual([['slime', '1,0,0']]);
  });
});
