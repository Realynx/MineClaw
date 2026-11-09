import { describe, expect, it } from 'vitest';
import { StoryBill, StoryBillsRequest, STORY_WORK_TYPES, type StoryBillSpec } from '../src/index.js';
import { StorySim, type StoryWorld } from '../src/testing.js';

const near: [number, number, number] = [2, 64, 0];
const far: [number, number, number] = [12, 64, 0];
const world: StoryWorld = {
  blockAt: (x, y, z) => y === 64 && z === 0 && (x === near[0] || x === far[0]) ? [58, 0] : y < 64 ? [2, 0] : [0, 0],
  ground: () => 63,
  entities: () => [],
};

function fixture(work: Record<string, number> = { craft: 1 }) {
  const sim = new StorySim({ world, defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
  const { id } = sim.create({ name: 'Bill priorities', pawns: 1 });
  const pawn = sim.state(id).pawns[0]!.name;
  sim.priorities(id, { pawns: [pawn], work: Object.fromEntries(STORY_WORK_TYPES.map((k) => [k, work[k] ?? 0])) });
  sim.schedule(id, { pawns: [pawn], hours: Object.fromEntries(Array.from({ length: 24 }, (_, h) => [String(h), 'work'])) });
  const add = (bench: typeof near, recipe: string, fields: Partial<StoryBillSpec> = {}) =>
    sim.bills(id, { op: 'add', bench, bill: { recipe, mode: 'forever', ...fields } }).id!;
  const pick = () => { sim.step(10); return sim.state(id).pawns[0]!.job!; };
  return { sim, id, add, pick };
}

describe('bill priority contracts', () => {
  it.each([1, 2, 3, 4])('accepts priority %i on add, update and state', (priority) => {
    expect(StoryBillsRequest.parse({ op: 'add', bench: near, bill: { recipe: 'sticks', mode: 'forever', priority } }))
      .toMatchObject({ bill: { priority } });
    expect(StoryBillsRequest.parse({ op: 'update', id: 'bill1', priority })).toMatchObject({ priority });
    expect(StoryBill.parse({ id: 'bill1', recipe: 'sticks', mode: 'forever', priority, waitingReason: 'waiting for wood' }))
      .toMatchObject({ priority, waitingReason: 'waiting for wood' });
  });

  it.each([0, 5, 1.5, '1', null])('rejects invalid priority %s on add, update and state', (priority) => {
    expect(StoryBillsRequest.safeParse({ op: 'add', bench: near, bill: { recipe: 'sticks', mode: 'forever', priority } }).success).toBe(false);
    expect(StoryBillsRequest.safeParse({ op: 'update', id: 'bill1', priority }).success).toBe(false);
    expect(StoryBill.safeParse({ id: 'bill1', recipe: 'sticks', mode: 'forever', priority }).success).toBe(false);
  });

  it('accepts legacy bills and nullable waiting state without allowing clients to write waitingReason', () => {
    const legacy = { id: 'bill1', recipe: 'sticks', mode: 'forever' };
    expect(StoryBill.parse(legacy).priority).toBeUndefined();
    expect(StoryBill.parse({ ...legacy, waitingReason: null }).waitingReason).toBeNull();
    expect(StoryBill.safeParse({ ...legacy, waitingReason: 12 }).success).toBe(false);
    expect(StoryBillsRequest.safeParse({ op: 'update', id: 'bill1', waitingReason: 'fake' }).success).toBe(false);
    expect(StoryBillsRequest.safeParse({ op: 'add', bench: near, bill: { recipe: 'sticks', mode: 'forever', waitingReason: 'fake' } }).success).toBe(false);
  });
});

describe('mock colony bill scheduling', () => {
  it('chooses a higher-priority bill at a farther table before the nearby one', () => {
    const f = fixture();
    f.add(near, 'sticks');
    f.add(far, 'torch', { priority: 1 });
    expect(f.pick()).toMatchObject({ type: 'craft', label: 'Crafting torches', target: { x: far[0], y: far[1], z: far[2] } });
  });

  it('treats an omitted priority as normal (3)', () => {
    const f = fixture();
    f.add(near, 'torch', { priority: 4 });
    f.add(far, 'sticks');
    expect(f.pick().label).toBe('Crafting sticks');
  });

  it('can select a higher-priority bill later in the same table queue', () => {
    const f = fixture();
    f.add(near, 'sticks', { priority: 4 });
    f.add(near, 'torch', { priority: 1 });
    expect(f.pick().label).toBe('Crafting torches');
  });

  it('keeps the local queue order for equal priorities, including explicit reorder', () => {
    const first = fixture();
    first.add(near, 'sticks');
    first.add(near, 'torch', { priority: 3 });
    expect(first.pick().label).toBe('Crafting sticks');
    const reordered = fixture();
    const sticks = reordered.add(near, 'sticks');
    const torches = reordered.add(near, 'torch', { priority: 3 });
    reordered.sim.bills(reordered.id, { op: 'reorder', bench: near, ids: [torches, sticks] });
    expect(reordered.pick().label).toBe('Crafting torches');
  });

  it('uses distance between equal-priority tables, not their unrelated local queue indices', () => {
    const f = fixture();
    f.add(near, 'planks', { paused: true });
    f.add(near, 'sticks');
    f.add(far, 'torch', { priority: 3 });
    expect(f.pick().label).toBe('Crafting sticks');
  });

  it('preserves the pawn’s work-type priority before comparing bill priority', () => {
    const f = fixture({ craft: 1, cook: 2 });
    f.add(near, 'bread', { priority: 1 });
    f.add(near, 'sticks', { priority: 4 });
    expect(f.pick()).toMatchObject({ type: 'craft', label: 'Crafting sticks' });
  });

  it('ranks manual bills ahead of ordinary automatic build crafting', () => {
    const f = fixture();
    f.sim.builds(f.id, { op: 'add', items: [{ kind: 'fence', area: [5, 64, 5, 5, 64, 5] }] });
    expect(f.sim.state(f.id).craftTasks?.length).toBeGreaterThan(0);
    f.add(far, 'torch', { priority: 4 });
    expect(f.pick().label).toBe('Crafting torches');
  });

  it('updates priority and clears stale waiting/suspension without unpausing the bill', () => {
    const f = fixture();
    const id = f.add(near, 'sticks', { paused: true });
    // Seed the simulator's server-owned status just as a failed/waiting execution would.
    const bill = f.sim.state(f.id).workbenches!.flatMap((b) => b.bills).find((b) => b.id === id)!;
    bill.suspendedReason = 'missing wood';
    bill.waitingReason = 'waiting for wood';
    f.sim.bills(f.id, { op: 'update', id, priority: 1 });
    expect(f.sim.state(f.id).workbenches!.flatMap((b) => b.bills).find((b) => b.id === id))
      .toMatchObject({ priority: 1, paused: true, suspendedReason: null, waitingReason: null });
    f.sim.bills(f.id, { op: 'resume', id });
    expect(f.pick().label).toBe('Crafting sticks');
  });
});
