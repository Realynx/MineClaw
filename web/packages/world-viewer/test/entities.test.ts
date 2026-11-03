import { describe, expect, it } from 'vitest';
import { EntityStore } from '../src/world/entities';
import type { BotDto, EntityDelta, EntityDto } from '../src/types';
import { normalizeEntities } from '../src/wire/normalize';

const mob = (id: number, x: number, extra: Partial<EntityDto> = {}): EntityDto => ({ kind: 'mob', dim: 0, x, y: 64, z: 0, id, type: 54, ...extra });
const bot = (x: number, extra: Partial<BotDto> = {}): BotDto => ({ name: 'Alice', short: 'main', main: true, dim: 0, x, y: 64, z: 0, hp: 20, food: 20, ...extra });
const delta = (rev: number, entities: EntityDto[], d: Partial<EntityDelta> = {}): EntityDelta => ({ rev, full: false, bots: [bot(0)], entities, removed: [], ...d });

function clocked() {
  let t = 1000;
  const s = new EntityStore(() => t);
  return { s, at: (ms: number) => (t = ms) };
}

describe('EntityStore interpolation (LiveHive/EntityStore.cs)', () => {
  it('glides from the drawn position to the new one over the measured update interval', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(7, 0)], { full: true }));
    at(1200);
    s.apply(delta(2, [mob(7, 6)]));
    // EWMA: 200·0.7 + 200·0.3 = 200 ms.
    expect(s.intervalMs).toBeCloseTo(200);
    at(1300);
    expect(s.entities(0)[0].x).toBeCloseTo(3);
    at(1400);
    expect(s.entities(0)[0].x).toBeCloseTo(6);
    at(2000);
    expect(s.entities(0)[0].x).toBeCloseTo(6);
  });

  it('a new update mid-glide starts from where the entity is drawn, not where it was sent', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(7, 0)], { full: true }));
    at(1200);
    s.apply(delta(2, [mob(7, 4)]));
    at(1300); // drawn at 2
    s.apply(delta(3, [mob(7, 6)]));
    expect(s.entities(0)[0].x).toBeCloseTo(2);
    at(1300 + s.intervalMs);
    expect(s.entities(0)[0].x).toBeCloseTo(6);
  });

  it('snaps jumps of more than 8 blocks', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(7, 0)], { full: true }));
    at(1200);
    s.apply(delta(2, [mob(7, 50)]));
    expect(s.entities(0)[0].x).toBe(50);
  });

  it('clamps the interval to 50..1000 ms', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [], { full: true }));
    for (let i = 1; i < 30; i++) {
      at(1000 + i * 5000);
      s.apply(delta(i + 1, []));
    }
    expect(s.intervalMs).toBe(1000);
  });

  it('deltas update and remove; a full reply replaces the set; rev is remembered for since=', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(1, 0), mob(2, 3)], { full: true }));
    at(1100);
    s.apply(delta(2, [mob(3, 6)], { removed: [[0, 1]] }));
    expect(s.entities(0).map((e) => e.id).sort()).toEqual([2, 3]);
    s.apply(delta(9, [mob(4, 0)], { full: true }));
    expect(s.entities(0).map((e) => e.id)).toEqual([4]);
    expect(s.rev).toBe(9);
    expect(s.entities(-1)).toEqual([]);
  });

  it('bots glide too, and are replaced wholesale each reply', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [], { full: true, bots: [bot(0), bot(0, { name: 'Bob', short: '67', main: false })] }));
    at(1200);
    s.apply(delta(2, [], { bots: [bot(4)] }));
    at(1300);
    expect(s.bot('main')!.x).toBeCloseTo(2);
    expect(s.bot('Bob')).toBeNull();
  });

  it('advances drop ages locally (ticks) and turns yaw the short way round', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(5, 0, { kind: 'item', age: 2, yaw: 350 })], { full: true }));
    at(2000);
    expect(s.entities(0)[0].ageTicks).toBeCloseTo(40 + 20);
    s.apply(delta(2, [mob(5, 0, { kind: 'item', age: 3, yaw: 10 })]));
    at(2000 + s.intervalMs / 2);
    const yaw = s.entities(0)[0].yaw;
    expect(((yaw % 360) + 360) % 360).toBeCloseTo(0, 0);
  });

  it('normalizes snake_case replies', () => {
    const d = normalizeEntities({ rev: 3, full: true, bots: [{ name: 'A', short: 'main', main: true, dim: 0, x: 1, y: 2, z: 3, pose_age: 0.5, hp: 20, food: 20, age_seconds: 4 }], entities: [], removed: [[0, 1]] });
    expect(d.bots[0].poseAge).toBe(0.5);
    expect(d.bots[0].ageSeconds).toBe(4);
    expect(d.removed).toEqual([[0, 1]]);
  });
});
