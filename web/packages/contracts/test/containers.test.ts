import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { MOCK_CONTAINERS, mockContainer, mockContainers } from '../src/testing.js';

describe('containers', () => {
  it('every mock container parses from either block, and anything else is an unknown chest', () => {
    for (const m of MOCK_CONTAINERS)
      for (const [x, y, z] of m.cells) {
        const v = C.ContainerView.parse(mockContainer(0, x, y, z, 3));
        expect(v.kind).toBe(m.kind);
        expect(v.pos).toEqual(m.cells[0]);
        expect(v.slots.every((s) => s.slot >= 0 && s.slot < v.size)).toBe(true);
      }
    const unknown = C.ContainerView.parse(mockContainer(0, 20, 70, -32));
    expect(unknown).toMatchObject({ source: 'unknown', kind: 'chest', size: 27, slots: [] });
    expect(unknown.seenAt).toBeNull();
    expect(mockContainer(-1, 2, 74, -4).source).toBe('unknown');
  });

  it('the wire form converts and the slot range is enforced', () => {
    const wire = C.toSnake<Record<string, unknown>>(mockContainer(0, 2, 74, -4));
    expect(wire.seen_by).toBe('maid_chan_102');
    expect(C.ContainerView.safeParse(C.toCamel(wire)).success).toBe(true);
    const bad = mockContainer(0, 2, 74, -4);
    bad.slots[0] = { ...bad.slots[0]!, slot: 54 };
    expect(C.ContainerView.safeParse(bad).success).toBe(false);
    expect(C.ContainerView.safeParse({ ...mockContainer(0, 2, 74, -4), kind: 'barrel' }).success).toBe(false);
  });

  it('the box summary lists the known ones newest first, with top items, and caps', () => {
    const all = C.ContainerList.parse(mockContainers(0, { x0: -10, z0: -40, x1: 30, z1: 0 }));
    expect(all.containers.map((c) => c.kind)).toEqual(['double_chest', 'furnace', 'chest', 'double_chest']);
    expect(all.containers[0]!.source).toBe('live');
    const island = all.containers.find((c) => c.pos[0] === 2)!;
    expect(island.top[0]).toMatchObject({ name: 'log', count: 128 });
    expect(island.used).toBe(12);
    const two = mockContainers(0, { x0: -10, z0: -40, x1: 30, z1: 0 }, 2);
    expect(two.containers).toHaveLength(2);
    expect(two.capped).toBe(true);
    expect(mockContainers(0, { x0: 100, z0: 100, x1: 110, z1: 110 }).containers).toEqual([]);
  });

  it('both routes reach the bot through the world pass-through, as viewer reads', () => {
    expect(C.bridgePath('world.container', {})).toBe('/v1/sup/world/containers');
    expect(C.bridgePath('world.containers', {})).toBe('/v1/sup/world/containers');
    expect(C.ROUTES['world.container'].access).toBe('viewer');
    expect(C.ROUTES['world.containers'].access).toBe('viewer');
    expect(C.ContainerQuery.parse({ x: '2', y: '74', z: '-4' })).toEqual({ dim: 0, x: 2, y: 74, z: -4 });
    expect(C.ContainerQuery.safeParse({ x: '2', z: '-4' }).success).toBe(false);
    expect(C.ContainerBoxQuery.safeParse({ x0: 0, z0: 0, x1: 1, z1: 1, limit: 900 }).success).toBe(false);
  });
});
