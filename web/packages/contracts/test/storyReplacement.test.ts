import { describe, expect, it } from 'vitest';
import { replacementItems } from '../src/storyReplacement.js';
import type { StoryBuildItem } from '../src/story.js';
import { StorySim } from '../src/testing.js';

describe('palette design replacement volume', () => {
  const item: StoryBuildItem = {
    kind: 'block',
    material: [4, 0],
    blocks: [
      [10, 64, 10],
      [12, 66, 12],
    ],
    replaceArea: [10, 64, 10, 12, 66, 12],
    group: 'house',
  };
  it('clears old objects in unlisted interior air before placing the design', () => {
    const result = replacementItems([item]);
    expect(result.items[0]?.kind).toBe('remove');
    expect(result.items[0]?.blocks).toHaveLength(25);
    expect(result.items[0]?.blocks).toContainEqual([11, 65, 11]);
    expect(result.area).toEqual(item.replaceArea);
  });
  it('does not clear cells that the new design will place, or dig its foundation below the bounds', () => {
    const result = replacementItems([item]);
    expect(result.items[0]?.blocks).not.toContainEqual([10, 64, 10]);
    expect(result.items[0]?.blocks?.every((p) => p[1] >= 64)).toBe(true);
  });
  it('rejects oversized or inconsistent replacements before touching old ownership', () => {
    expect(() => replacementItems([{ ...item, replaceArea: [0, 1, 0, 10000, 100, 10000] }])).toThrow(
      '262144',
    );
    expect(() => replacementItems([{ ...item, blocks: [[100, 64, 100]] }])).toThrow('inside');
  });
  it('removes old ownership permanently while retaining the part outside the replacement', () => {
    const sim = new StorySim({
      world: {
        ground: () => 63,
        entities: () => [],
        blockAt: (x, y, z) => (x === 11 && y === 65 && z === 11 ? [54, 0] : [0, 0]),
        setBlock: () => {},
      },
    });
    const { id } = sim.create({ name: 'replacement', pawns: 1 });
    sim.builds(id, {
      op: 'add',
      items: [
        {
          kind: 'block',
          material: [5, 0],
          blocks: [
            [11, 65, 11],
            [20, 64, 20],
          ],
        },
      ],
    });
    const old = sim.state(id).builds![0]!.id;
    const placed = sim.builds(id, { op: 'add', items: [item] });
    expect(
      sim
        .state(id)
        .ghosts?.blocks.filter((g) => g[5] === old)
        .map((g) => g.slice(0, 3)),
    ).toEqual([[20, 64, 20]]);
    sim.builds(id, { op: 'remove', ids: placed.ids!.map(String) });
    expect(sim.state(id).ghosts?.blocks.map((g) => g.slice(0, 3))).toEqual([[20, 64, 20]]);
  });
  it('hides settled air but reopens a late old placement, and yields to a newer object', () => {
    let late = 0;
    const sim = new StorySim({ world: { ground: () => 63, entities: () => [],
      blockAt: (x, y, z) => [x === 11 && y === 65 && z === 11 ? late : 0, 0], setBlock: () => {} } });
    const { id } = sim.create({ name: 'late placement', pawns: 1 });
    sim.builds(id, { op: 'add', items: [item] });
    expect(sim.state(id).ghosts?.blocks.filter((g) => g[7] === 'dig')).toEqual([]);
    late = 5;
    sim.step(0);
    const clear = sim.state(id).ghosts!.blocks.find((g) => g[0] === 11 && g[1] === 65 && g[2] === 11)!;
    expect(clear[7]).toBe('dig');
    expect(['todo', 'leased']).toContain(clear[6]);
    sim.builds(id, { op: 'add', items: [{ kind: 'chest', blocks: [[11, 65, 11]] }] });
    // New placement owns this cell; the previous intentional-air job cannot remove its chest.
    expect(sim.state(id).ghosts?.blocks.find((g) => g[5] === clear[5] && g[0] === 11 && g[1] === 65 && g[2] === 11)?.[6]).toBe('blocked');
  });
});
