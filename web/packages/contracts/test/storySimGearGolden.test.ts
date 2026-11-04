/**
 * The mock director's bulk gear op (storySimGear.ts `gearOp`) over every op, pinned as one digest per step: the pawns'
 * equipment and inventories, the storages and the answers.
 */
import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import type { StoryPawn, StoryStorage } from '../src/index.js';
import { DEFAULT_POLICIES, gearOp, StorySim } from '../src/testing.js';

describe('the mock gear op: a golden run', () => {
  it('arm, armour, tools, pickup and return over a stocked colony (pins gearOp)', () => {
    const sim = new StorySim({
      world: { blockAt: () => [0, 0], ground: () => 63, entities: () => [] },
      defaultHome: [0, 63, 0],
      now: () => 1_762_000_000_000,
      timeRateMs: 0,
    });
    const { id } = sim.create({ name: 'Gear', pawns: 5 });
    const pawns = (JSON.parse(JSON.stringify(sim.state(id).pawns)) as StoryPawn[]).map((p) => ({ p }));
    pawns[1]!.p.drafted = true;
    pawns[2]!.p.priorities = { ...pawns[2]!.p.priorities, guard: 1, mine: 2, chop: 0, farm: 3 };
    pawns[3]!.p.policy = 'nope';
    const storage = (chest: [number, number, number], contents: Array<[number, number, number]>): StoryStorage => ({
      chest,
      doubleWith: null,
      label: null,
      priority: 'normal',
      filter: { allow: ['*'], deny: [] },
      contents,
      freeSlots: 20,
    });
    const storages = [
      storage(
        [0, 64, 0],
        [
          [267, 0, 2],
          [272, 0, 1],
          [276, 0, 1],
          [307, 0, 1],
          [298, 0, 2],
          [301, 0, 1],
          [274, 0, 2],
          [292, 0, 1],
          [271, 0, 1],
          [4, 0, 40],
        ],
      ),
      storage(
        [5, 64, 0],
        [
          [261, 0, 1],
          [310, 0, 1],
          [278, 5, 1],
          [258, 0, 1],
          [4, 0, 3],
          [17, 2, 6],
        ],
      ),
    ];
    const gear = {
      policies: JSON.parse(JSON.stringify(DEFAULT_POLICIES)) as typeof DEFAULT_POLICIES,
      rules: [],
      seq: 1,
      heldBefore: new Map(),
      stocked: false,
    };
    const label = (i: number, m: number) => `item ${i}:${m}`;
    const trace: unknown[] = [];
    const run = (name: string, body: { op: string; item?: [number, number]; count?: number }, who = pawns) => {
      try {
        trace.push([name, gearOp(gear, who, storages, body, label), who.map((x) => x.p), storages]);
      } catch (e) {
        trace.push([name, 'error', (e as Error).message]);
      }
    };
    run('arm', { op: 'arm' });
    run('arm again', { op: 'arm' });
    run('armor', { op: 'armor' });
    run('tools', { op: 'tools' });
    run('pickup', { op: 'pickup', item: [4, 0], count: 20 });
    run('pickup any meta', { op: 'pickup', item: [17, -1], count: 4 }, pawns.slice(0, 2));
    run('pickup gear', { op: 'pickup', item: [258, 0] }, pawns.slice(2, 3));
    run('pickup none', { op: 'pickup' });
    run('return', { op: 'return' }, pawns.slice(0, 3));
    run('return empty', { op: 'return' }, pawns.slice(0, 1));
    run('armor after', { op: 'armor' });
    run('nonsense', { op: 'dance' });
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });
});
