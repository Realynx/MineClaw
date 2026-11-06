/**
 * Preserve zones (docs/web/story-mode.md "Preserve zones"): the request and state schemas (snake_case on the wire), the painter's
 * overlay rule, and the mock director's `preserve` op (on / off / range / retake, a mine zone refused, a Preserve zone always on).
 */
import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { StorySim, type StoryWorld } from '../src/testing.js';

const flat = (): StoryWorld => ({
  blockAt: (_x, y) => (y < 63 ? [1, 0] : y === 63 ? [2, 0] : [0, 0]),
  ground: () => 63,
  entities: () => [],
});

function colony() {
  const sim = new StorySim({ world: flat(), defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
  const { id } = sim.create({ name: 'Keep', pawns: 1 });
  const zone = (zid: string | number) => sim.state(id).zones.find((z) => String(z.id) === String(zid))!;
  const fails = (body: C.StoryZoneRequest) => {
    try {
      sim.zones(id, body);
    } catch (e) {
      return (e as Error).message;
    }
    return null;
  };
  return { sim, id, zone, fails };
}

describe('preserve zones: contracts', () => {
  it('the preserve op and the paint range are strict 0..32 whole numbers', () => {
    expect(C.StoryZoneRequest.safeParse({ op: 'preserve', id: 'z1', on: false }).success).toBe(true);
    expect(C.StoryZoneRequest.safeParse({ op: 'preserve', id: 'z1', below: 5, above: 0, retake: true }).success).toBe(true);
    expect(C.StoryZoneRequest.safeParse({ op: 'preserve', id: 'z1', below: 33 }).success).toBe(false);
    expect(C.StoryZoneRequest.safeParse({ op: 'preserve', id: 'z1', above: 1.5 }).success).toBe(false);
    expect(C.StoryZoneRequest.safeParse({ op: 'preserve', id: 'z1', depth: 2 }).success).toBe(false);
    const add = { op: 'add', kind: 'preserve', area: [0, 64, 0, 3, 64, 3], below: 2, above: 4 };
    expect(C.StoryZoneRequest.safeParse(add).success).toBe(true);
    expect(C.STORY_ZONE_KINDS).toContain('preserve');
    expect(C.isOverlayZone('preserve')).toBe(true);
  });

  it("a zone's preserve and the delta's removed ghosts come through the bridge's case conversion", () => {
    const wire = {
      id: 'c1',
      rev: 9,
      at: '2026-11-05T10:05:00Z',
      zones: [
        {
          id: 'z1',
          kind: 'farm',
          area: [0, 64, 0, 3, 64, 3],
          preserve: {
            on: true,
            below: 1,
            above: 3,
            y1: 63,
            y2: 67,
            cells: 80,
            taken_at: '2026-11-05T10:00:00Z',
            build: 'b7',
            unknown: 0,
            held: 2,
            damage: { missing: 9, foreign: 3, turned: 0 },
            to_repair: 12,
            status: '12 blocks to repair: 9 missing, 3 foreign; waiting for 6 dirt',
          },
        },
      ],
      removed: { ghosts: ['b7|1,64,2'] },
    };
    const s = C.StoryColonyState.parse(C.toCamel(wire));
    expect(s.zones[0]!.preserve).toMatchObject({ takenAt: '2026-11-05T10:00:00Z', toRepair: 12, build: 'b7', held: 2 });
    expect(s.removed?.ghosts).toEqual(['b7|1,64,2']);
    const ack = C.StoryAck.parse(C.toCamel({ id: 'z1', preserve: wire.zones[0]!.preserve, warnings: ['x'] }));
    expect(ack.preserve?.y2).toBe(67);
  });

  it('a Preserve zone is an overlay: painted over a growing zone it takes no cells, and merges only with its own kind', () => {
    const make = (kind: string) => ({ id: `n${kind}`, kind, area: [0, 64, 0, 0, 64, 0] as C.StoryArea });
    let n = 0;
    const farm = C.paintZones([], { kind: 'farm', plant: 'wheat', rect: [0, 0, 3, 3], y: [64, 64] }, make, () => `a${n++}`);
    const keep = C.paintZones(farm.zones, { kind: 'preserve', rect: [2, 2, 5, 5], y: [64, 64] }, make, () => `b${n++}`);
    expect(keep.result.shrunk).toEqual([]);
    expect(keep.zones.map((z) => [z.kind, C.zoneCellSet(z).size])).toEqual([
      ['farm', 16],
      ['preserve', 16],
    ]);
    const more = C.paintZones(keep.zones, { kind: 'preserve', rect: [6, 2, 6, 2], y: [64, 64] }, make, () => `c${n++}`);
    expect(more.result.grown).toEqual(['npreserve']);
  });
});

describe('preserve zones: the mock director', () => {
  it('paints a Preserve zone with its range (1 / 3 by default), always on, its y range and blocks reported', () => {
    const { sim, id, zone, fails } = colony();
    const a = sim.zones(id, { op: 'add', kind: 'preserve', area: [0, 64, 0, 3, 64, 1], below: 2, above: 4 });
    expect(zone(a.id!).preserve).toMatchObject({ on: true, below: 2, above: 4, y1: 62, y2: 68, cells: 8 * 7, status: 'intact' });
    const b = sim.zones(id, { op: 'add', kind: 'preserve', area: [10, 64, 10, 10, 64, 10] });
    expect(zone(b.id!).preserve).toMatchObject({ below: 1, above: 3, y1: 63, y2: 67 });
    expect(fails({ op: 'preserve', id: a.id!, on: false })).toBe('a Preserve zone is always on: erase or remove it instead');
    // 64 × 64 columns × 65 layers is too many.
    expect(fails({ op: 'add', kind: 'preserve', area: [20, 64, 20, 83, 64, 83], below: 32, above: 32 })).toBe(
      '266240 blocks to keep: at most 262144 (fewer columns, or less depth or height)',
    );
  });

  it('switches Preserve on, sets its range and re-takes a growing zone; a mine zone refuses; off removes it', () => {
    const { sim, id, zone, fails } = colony();
    const farm = sim.zones(id, { op: 'add', kind: 'farm', plant: 'wheat', area: [0, 64, 0, 3, 64, 2] });
    expect(zone(farm.id!).preserve ?? null).toBeNull();
    const on = sim.zones(id, { op: 'preserve', id: farm.id! }) as { preserve: C.StoryZonePreserve };
    expect(on.preserve).toMatchObject({ on: true, below: 1, above: 3, y1: 63, y2: 67, cells: 12 * 5 });
    const taken = on.preserve.takenAt;
    const deeper = sim.zones(id, { op: 'preserve', id: farm.id!, below: 5 }) as { preserve: C.StoryZonePreserve };
    expect(deeper.preserve).toMatchObject({ below: 5, above: 3, y1: 59, takenAt: taken });
    expect(zone(farm.id!).preserve).toMatchObject({ below: 5, y1: 59 });
    const again = sim.zones(id, { op: 'preserve', id: farm.id!, retake: true }) as { preserve: C.StoryZonePreserve };
    expect(again.preserve.below).toBe(5);
    sim.zones(id, { op: 'preserve', id: farm.id!, on: false });
    expect(zone(farm.id!).preserve ?? null).toBeNull();
    const mine = sim.zones(id, { op: 'add', kind: 'mine', area: [10, 64, 10, 12, 64, 12] });
    expect(fails({ op: 'preserve', id: mine.id! })).toBe(
      "a mine zone digs its blocks: it can't preserve them (paint a Preserve zone beside it instead)",
    );
  });

  it('a delta carries the zone whose Preserve changed', () => {
    const { sim, id } = colony();
    const farm = sim.zones(id, { op: 'add', kind: 'chop', area: [0, 64, 0, 1, 64, 1] });
    const rev = sim.state(id).rev;
    sim.zones(id, { op: 'preserve', id: farm.id!, above: 0 });
    const d = sim.state(id, rev);
    expect(d.zones.map((z) => [z.id, z.preserve?.y2])).toEqual([[farm.id, 64]]);
  });
});
