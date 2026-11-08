import { describe, expect, it } from 'vitest';
import { StoryBuildRequest, StoryBuildResponse } from '../src/story.js';
import { StorySim } from '../src/storySim.js';

function colony() {
  const blocks = new Map<string, [number, number]>([['1,64,1', [4, 0]]]);
  let writes = 0;
  const sim = new StorySim({ now: () => 0, world: { ground: () => 63, entities: () => [],
    blockAt: (x, y, z) => blocks.get(`${x},${y},${z}`) ?? [0, 0],
    setBlock: (x, y, z, id, meta) => { writes++; blocks.set(`${x},${y},${z}`, [id, meta]); },
  } });
  const { id } = sim.create({ name: 'Plan edits', pawns: 1 });
  return { sim, id, blocks, writes: () => writes };
}

describe('freecam plan edits', () => {
  it('accepts bounded exact states and explicit air, rejecting incomplete or invalid state tuples', () => {
    const edit = { op: 'edit', buildId: 'b1', cells: [[1, 64, 1, 53, 3], [1, 65, 1, 0, 0]] };
    expect(StoryBuildRequest.parse(edit)).toEqual(edit);
    for (const cells of [[], [[1, 64, 1]], [[1, 64, 1, 53, 16]], [[1, 0, 1, 4, 0]], [[1, 64, 1, -1, 0]], [[1, 64, 1, 0, 1]], [[1, 256, 1, 4, 0]]])
      expect(StoryBuildRequest.safeParse({ op: 'edit', cells }).success).toBe(false);
    expect(StoryBuildResponse.parse({ ids: ['b1'], buildId: 'b1', rev: 20 })).toMatchObject({ buildId: 'b1', rev: 20 });
  });

  it('keeps the building group, exact orientation and untouched cells without setting world blocks', () => {
    const { sim, id, writes } = colony();
    const old = sim.builds(id, { op: 'add', items: [{ kind: 'block', group: 'house', name: 'House',
      material: [4, 0], blocks: [[1, 64, 1], [2, 64, 1]] }] }).ids![0]!;
    const edited = sim.builds(id, { op: 'edit', cells: [[1, 64, 1, 53, 3]] });
    expect(sim.state(id).ghosts?.blocks.find((g) => g[0] === 1)?.slice(0, 5)).toEqual([1, 64, 1, 53, 3]);
    expect(sim.state(id).ghosts?.blocks.find((g) => g[0] === 2)?.[5]).toBe(old);
    expect(sim.state(id).builds?.find((b) => b.id === edited.buildId)).toMatchObject({ group: 'house', name: 'House' });
    const extended = sim.builds(id, { op: 'edit', buildId: edited.buildId, cells: [[1, 65, 1, 5, 2]] });
    expect(extended.buildId).toBe(edited.buildId);
    expect(writes()).toBe(0);
  });

  it('replaces overlapping old ownership permanently and reopens intentional air after a late old placement', () => {
    const { sim, id, blocks } = colony();
    sim.builds(id, { op: 'add', items: [{ kind: 'block', material: [4, 0], blocks: [[1, 64, 1]] }] });
    const removed = sim.builds(id, { op: 'edit', cells: [[1, 64, 1, 0, 0]] });
    expect(sim.state(id).ghosts?.blocks).toHaveLength(1);
    expect(sim.state(id).ghosts?.blocks[0]?.[7]).toBe('dig');
    blocks.set('1,64,1', [0, 0]); sim.step(0);
    expect(sim.state(id).ghosts?.blocks).toEqual([]);
    blocks.set('1,64,1', [4, 0]); sim.step(0);
    expect(sim.state(id).ghosts?.blocks[0]?.[7]).toBe('dig');
    sim.builds(id, { op: 'remove', ids: removed.ids! });
    expect(sim.state(id).ghosts?.blocks).toEqual([]);
  });

  it('validates the whole edit before touching the existing plan', () => {
    const { sim, id } = colony();
    sim.builds(id, { op: 'add', items: [{ kind: 'block', material: [4, 0], blocks: [[1, 64, 1]] }] });
    const before = JSON.stringify(sim.state(id));
    expect(() => sim.builds(id, { op: 'edit', cells: [[1, 64, 1, 5, 0], [1, 64, 1, 0, 0]] })).toThrow('duplicate');
    expect(() => sim.builds(id, { op: 'edit', buildId: 'missing', cells: [[1, 64, 1, 0, 0]] })).toThrow('no such');
    expect(JSON.stringify(sim.state(id))).toBe(before);
  });
});
