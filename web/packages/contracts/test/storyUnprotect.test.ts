import { describe, expect, it } from 'vitest';
import { StoryBuildRequest, StoryBuildResponse } from '../src/story.js';
import { StorySim } from '../src/storySim.js';

describe('stop rebuilding selected cells', () => {
  it('accepts only a bounded explicit cell selection', () => {
    expect(StoryBuildRequest.safeParse({ op: 'unprotect', cells: [[1, 64, 1]] }).success).toBe(true);
    expect(StoryBuildRequest.safeParse({ op: 'unprotect', cells: [] }).success).toBe(false);
    expect(StoryBuildRequest.safeParse({ op: 'unprotect', cells: [[1, 64, 1]], ids: ['whole-build'] }).success).toBe(
      false,
    );
  });

  it('releases selected build and preserve cells without altering blocks, neighbors or the zone', () => {
    let writes = 0;
    const sim = new StorySim({
      world: {
        ground: () => 63,
        entities: () => [],
        blockAt: () => [4, 0],
        setBlock: () => {
          writes++;
        },
      },
    });
    const { id } = sim.create({ name: 'Release cells', pawns: 1 });
    sim.zones(id, { op: 'add', kind: 'preserve', area: [1, 64, 1, 2, 64, 1], below: 0, above: 0 });
    sim.builds(id, {
      op: 'add',
      items: [
        {
          kind: 'block',
          material: [4, 0],
          blocks: [
            [1, 64, 1],
            [2, 64, 1],
          ],
        },
      ],
    });
    const reply = sim.builds(id, {
      op: 'unprotect',
      cells: [
        [1, 64, 1],
        [1, 64, 1],
      ],
    });
    expect(StoryBuildResponse.parse(reply)).toMatchObject({ cells: 1, buildCells: 1, preservedCells: 1 });
    expect(sim.state(id).ghosts?.blocks.map((g) => g.slice(0, 3))).toEqual([[2, 64, 1]]);
    const zone = sim.state(id).zones.find((z) => z.kind === 'preserve')!;
    expect(zone.preserve).toMatchObject({ on: true, cells: 1 });
    sim.step(0);
    expect(sim.state(id).zones.find((z) => z.id === zone.id)?.preserve?.cells).toBe(1);
    expect(writes).toBe(0);
    sim.zones(id, { op: 'preserve', id: zone.id, retake: true });
    expect(sim.state(id).zones.find((z) => z.id === zone.id)?.preserve?.cells).toBe(2);
  });
});
