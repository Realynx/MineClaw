import { describe, expect, it } from 'vitest';
import { StoryStorage, StoryStorageRequest, type StoryStorageSign } from '../src/story.js';
import { StorySim, type StoryWorld } from '../src/storySim.js';
import { changeStorageEntries, compareStoragePriority } from '../src/storyStorageFilters.js';

const chest: [number, number, number] = [2, 64, 2];
const other: [number, number, number] = [6, 64, 2];
const sign: StoryStorageSign = { at: [2, 64, 1], lines: ['food', '-367', '', ''], allow: ['food'], deny: ['367'] };

function rig() {
  const attachedSign = structuredClone(sign);
  const blocks = new Map<string, readonly [number, number]>([
    [chest.join(','), [54, 0]],
    [other.join(','), [54, 0]],
    [sign.at.join(','), [68, 2]],
  ]);
  const world: StoryWorld = {
    ground: () => 63,
    entities: () => [],
    blockAt: (x, y, z) => blocks.get([x, y, z].join(',')) ?? (y < 64 ? [1, 0] : [0, 0]),
    setBlock: (x, y, z, id, meta) => {
      blocks.set([x, y, z].join(','), [id, meta]);
    },
    storageSigns: (at) => (at.join(',') === chest.join(',') ? [attachedSign] : []),
  };
  let now = 1_762_000_000_000;
  const sim = new StorySim({ world, defaultHome: [0, 63, 0], now: () => now });
  const { id } = sim.create({ name: 'Signs', pawns: 1 });
  sim.storage(id, { op: 'assign_area', area: [0, 64, 0, 8, 64, 4] });
  const pawn = sim.state(id).pawns[0]!;
  sim.priorities(id, {
    pawns: [pawn.name],
    work: Object.fromEntries(Object.keys(pawn.priorities).map((work) => [work, work === 'build' ? 1 : 0])),
  });
  sim.schedule(id, {
    pawns: [pawn.name],
    hours: Object.fromEntries(Array.from({ length: 24 }, (_, h) => [String(h), 'work'])),
  });
  const advance = (steps = 60) => {
    for (let i = 0; i < steps; i++) {
      now += 500;
      sim.step(500);
    }
  };
  const storage = (at = chest) => sim.state(id).storages!.find((s) => s.chest.join(',') === at.join(','))!;
  return { sim, id, pawn: pawn.name, storage, world, advance, attachedSign };
}

describe('signed storage', () => {
  it('accepts sign state, pending edits and strict bulk entry requests', () => {
    const parsed = StoryStorage.parse({ chest, signs: [sign], pendingFilter: { allow: ['wood'], deny: [] } });
    expect(parsed.signs).toEqual([sign]);
    expect(parsed.pendingFilter?.allow).toEqual(['wood']);
    for (const op of ['add', 'remove_entries']) {
      expect(
        StoryStorageRequest.safeParse({ op, chests: [chest, other], filter: { allow: ['food'], deny: [] } }).success,
      ).toBe(true);
      expect(StoryStorageRequest.safeParse({ op, chests: [chest] }).success).toBe(false);
    }
    expect(StoryStorageRequest.parse({ op: 'remove', chests: [chest] }).op).toBe('remove');
  });

  it('keeps attached sign precedence until a pawn actually removes it, then restores configured priority', () => {
    const { sim, id, storage, advance, world } = rig();
    expect(storage().filter).toEqual({ allow: ['food'], deny: ['367'] });
    expect(storage().priority).toBe('critical');
    sim.storage(id, { op: 'set', chests: [chest], priority: 'low', filter: { allow: ['wood'], deny: [] } });
    expect(storage().filter.allow).toEqual(['food']);
    expect(storage().pendingFilter?.allow).toEqual(['wood']);
    expect(storage().priority).toBe('critical');
    advance(1);
    expect(sim.state(id).pawns[0]!.job?.label).toBe('Removing chest filter sign');
    advance();
    expect(world.blockAt(...sign.at)[0]).toBe(0);
    expect(storage().filter.allow).toEqual(['wood']);
    expect(storage().priority).toBe('low');
    expect(storage().pendingFilter).toBeUndefined();
    expect(storage().signs).toBeUndefined();
  });

  it('keeps a queued filter inactive while Build work is disabled, including subsequent entry changes', () => {
    const { sim, id, pawn, storage, advance } = rig();
    sim.priorities(id, { pawns: [pawn], work: { build: 0 } });
    sim.storage(id, { op: 'set', chests: [chest], filter: { allow: ['wood'], deny: [] } });
    sim.storage(id, { op: 'add', chests: [chest], filter: { allow: ['tools'], deny: [] } });
    advance();
    expect(storage().filter.allow).toEqual(['food']);
    expect(storage().pendingFilter?.allow).toEqual(['wood', 'tools']);
  });

  it('queues priority-only changes while the sign keeps effective critical priority', () => {
    const { sim, id, storage, advance, world } = rig();
    sim.storage(id, { op: 'set', chests: [chest], priority: 'low' });
    expect(storage().priority).toBe('critical');
    expect(storage().pendingPriority).toBe('low');
    expect(storage().pendingFilter).toBeUndefined();
    advance();
    expect(world.blockAt(...sign.at)[0]).toBe(0);
    expect(storage().priority).toBe('low');
    expect(storage().pendingPriority).toBeUndefined();
  });

  it('cancels queued settings when the physical sign is edited, preserving its original underlying priority', () => {
    const { sim, id, storage, advance, world, attachedSign } = rig();
    sim.storage(id, { op: 'set', chests: [chest], priority: 'low', filter: { allow: ['tools'], deny: [] } });
    attachedSign.lines = ['wood', '', '', ''];
    attachedSign.allow = ['wood'];
    attachedSign.deny = [];
    advance();
    expect(world.blockAt(...sign.at)[0]).toBe(68);
    expect(storage().filter.allow).toEqual(['wood']);
    expect(storage().pendingPriority).toBeUndefined();
    expect(storage().pendingFilter).toBeUndefined();
    world.setBlock!(...sign.at, 0, 0);
    advance(1);
    expect(storage().priority).toBe('normal');
  });

  it('excludes an apple even when food is allowed and reallows it without changing unrelated rules', () => {
    const excluded = changeStorageEntries(
      { allow: ['food', 'wood'], deny: ['367'] },
      { allow: ['260:0'], deny: [] },
      'remove_entries',
    );
    expect(excluded).toEqual({ allow: ['food', 'wood'], deny: ['367', '260:0'] });
    expect(changeStorageEntries(excluded, { allow: ['260:0'], deny: [] }, 'add')).toEqual({
      allow: ['food', 'wood', '260:0'],
      deny: ['367'],
    });
  });

  it('pulls matching contents out of ordinary critical storage and leaves unrelated contents there', () => {
    const { sim, id, pawn, storage, advance } = rig();
    sim.priorities(id, { pawns: [pawn], work: { build: 0, haul: 1 } });
    sim.storage(id, { op: 'set', chests: [other], priority: 'critical', filter: { allow: ['*'], deny: [] } });
    storage().contents = [];
    storage().freeSlots = 27;
    storage(other).contents = [
      [260, 0, 12],
      [4, 0, 7],
    ];
    storage(other).freeSlots = 25;
    expect(compareStoragePriority(storage(), storage(other))).toBeLessThan(0);
    advance();
    expect(storage().contents).toContainEqual([260, 0, 12]);
    expect(storage(other).contents).toEqual([[4, 0, 7]]);
  });
});
