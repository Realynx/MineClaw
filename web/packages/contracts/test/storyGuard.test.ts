import { describe, expect, it } from 'vitest';
import { StoryZoneRequest, isOverlayZone, zoneCellSet } from '../src/index.js';
import { StorySim, type SimEntity, type StoryWorld } from '../src/storySim.js';

function colony() {
  const entities: SimEntity[] = [{ id: 1, kind: 'mob', type: 90, x: 4.5, y: 64, z: 1.5 }];
  const world: StoryWorld = {
    ground: () => 63,
    blockAt: (_x, y) => (y < 64 ? [1, 0] : [0, 0]),
    entities: () => entities,
  };
  const sim = new StorySim({ world, defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
  const { id } = sim.create({ name: 'Watch', pawns: 3 });
  const pawns = sim.state(id).pawns.map((p) => p.name);
  sim.priorities(id, { pawns, work: { guard: 1 } });
  sim.schedule(id, { pawns, hours: Object.fromEntries(Array.from({ length: 24 }, (_, h) => [String(h), 'work'])) });
  return { sim, id, pawns, entities, world };
}

describe('Guard zones', () => {
  it('accepts an independent overlay, extends it and erases it without changing work zones', () => {
    const { sim, id } = colony();
    const area = [2, 63, 0, 6, 63, 4] as const;
    sim.zones(id, { op: 'add', kind: 'chop', area: [...area] });
    const request = StoryZoneRequest.parse({ op: 'add', kind: 'guard', area });
    sim.zones(id, request);
    expect(isOverlayZone('guard')).toBe(true);
    expect(sim.state(id).zones.map((z) => z.kind)).toEqual(['chop', 'guard']);
    const guard = sim.state(id).zones.find((z) => z.kind === 'guard')!;
    sim.zones(id, { op: 'add', kind: 'guard', into: guard.id, area: [7, 63, 0, 7, 63, 4] });
    expect(zoneCellSet(sim.state(id).zones.find((z) => z.kind === 'guard')!).size).toBe(30);
    sim.zones(id, { op: 'remove', kind: 'guard', area: [2, 63, 0, 7, 63, 4] });
    expect(sim.state(id).zones.map((z) => z.kind)).toEqual(['chop']);
  });

  it('lets every available guard respond to the same animal, and stops after it leaves', () => {
    const { sim, id, entities } = colony();
    sim.zones(id, { op: 'add', kind: 'guard', area: [2, 10, 0, 6, 10, 4] }); // full-height columns
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job?.type === 'guard')).toBe(true);
    entities[0]!.x = 40;
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job === null)).toBe(true);
  });

  it('respects drafting, disabled Guard work, visibility and removing a zone', () => {
    const { sim, id, pawns, world } = colony();
    sim.zones(id, { op: 'add', kind: 'guard', area: [2, 63, 0, 6, 63, 4] });
    sim.draft(id, { pawns: [pawns[0]!], drafted: true });
    sim.priorities(id, { pawns: [pawns[1]!], work: { guard: 0 } });
    world.canSee = () => false;
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job === null)).toBe(true);
    world.canSee = () => true;
    sim.step(50);
    expect(
      sim
        .state(id)
        .pawns.filter((p) => p.job?.type === 'guard')
        .map((p) => p.name),
    ).toEqual([pawns[2]]);
    const guard = sim.state(id).zones.find((z) => z.kind === 'guard')!;
    sim.zones(id, { op: 'remove', id: guard.id });
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job === null)).toBe(true);
  });

  it('excludes players, drops, keep-out cells and mobs hidden by a wall', () => {
    const { sim, id, entities, world } = colony();
    entities.push({ id: 2, kind: 'player', x: 3, y: 64, z: 1 }, { id: 3, kind: 'item', x: 3, y: 64, z: 2 });
    sim.zones(id, { op: 'add', kind: 'guard', area: [2, 63, 0, 6, 63, 4] });
    world.blockAt = (x, y) => (y < 64 || x === 2 ? [1, 0] : [0, 0]);
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job === null)).toBe(true);
    world.blockAt = (_x, y) => (y < 64 ? [1, 0] : [0, 0]);
    sim.zones(id, { op: 'add', kind: 'forbidden', area: [4, 63, 1, 4, 63, 1] });
    sim.step(50);
    expect(sim.state(id).pawns.every((p) => p.job === null)).toBe(true);
  });
});
