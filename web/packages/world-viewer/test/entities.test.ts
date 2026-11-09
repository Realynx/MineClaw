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
  it('preserves authoritative chest snapshots through wire normalization and same-revision frames', () => {
    const { s } = clocked();
    s.apply(normalizeEntities({ rev: 1, frame: 1, bots: [], entities: [], chests: [{ dim: -1, x: 2, y: 64, z: 3, open_count: 2 }] }));
    expect(s.chests).toEqual([{ dim: -1, x: 2, y: 64, z: 3, openCount: 2 }]);
    s.apply(normalizeEntities({ rev: 1, frame: 2, bots: [], entities: [], chests: [] }));
    expect(s.chests).toEqual([]);
    s.clear(); expect(s.chests).toBeNull();
    s.apply(normalizeEntities({ rev: 1, bots: [], entities: [] }));
    expect(s.chests).toBeNull(); // legacy sources keep event-driven lids
  });
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

  it('resets the cadence after long silence instead of treating idle time as motion duration', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [], { full: true }));
    for (let i = 1; i < 30; i++) {
      at(1000 + i * 5000);
      s.apply(delta(i + 1, []));
    }
    expect(s.intervalMs).toBe(200);
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

/**
 * Live 11-04 "players are moving around slowly": a walking bot as the browser draws it. The bridge polls the bot at ~5 Hz (a
 * frame every 200 ms plus the bot's answer time, 0..80 ms here), the frame lands 50 ms later. Before: the viewer polled its own
 * cache every 200 ms and re-applied the last frame each time (every glide restarted, the interval EWMA measured the polls).
 * After: each frame is read when it lands (`onEntities`) and numbered, so a read between frames changes nothing.
 */
describe('stream-driven motion (frames pushed, numbered)', () => {
  function simulate(push: boolean) {
    let seed = 7;
    const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
    const speed = 4.3 / 1000; // blocks per ms (walking)
    let t = 0;
    const s = new EntityStore(() => t);
    // Frames: sampled at the bot, delivered 50 ms later.
    const frames: Array<{ at: number; x: number; n: number }> = [];
    for (let at = 0, n = 1; at < 12_000; n++) {
      at += 200 + rnd() * 80;
      frames.push({ at: at + 50, x: at * speed, n });
    }
    let latest: { x: number; n: number } | null = null;
    let fi = 0;
    let nextPoll = 37; // the viewer's poll phase is unrelated to the frames
    const read = () => {
      if (!latest) return;
      s.apply({ rev: 1, full: false, bots: [bot(latest.x)], entities: [], removed: [], ...(push ? { frame: latest.n } : {}) });
    };
    const drawn: number[] = [];
    const truth: number[] = [];
    for (t = 0; t < 12_000; t += 1000 / 60) {
      while (fi < frames.length && frames[fi].at <= t) {
        latest = frames[fi++];
        if (push) read();
      }
      if (t >= nextPoll) {
        read();
        nextPoll += push ? 1000 : 200;
      }
      if (t > 2000) {
        drawn.push(s.bot('main')?.x ?? 0);
        truth.push(t * speed);
      }
    }
    const step = speed * (1000 / 60);
    let stalls = 0;
    const speeds: number[] = [];
    for (let i = 1; i < drawn.length; i++) {
      const d = drawn[i] - drawn[i - 1];
      speeds.push(d / step);
      if (d < step * 0.25) stalls++;
    }
    const mean = speeds.reduce((a, b) => a + b, 0) / speeds.length;
    const sd = Math.sqrt(speeds.reduce((a, b) => a + (b - mean) ** 2, 0) / speeds.length);
    const lagMs = truth.reduce((a, x, i) => a + (x - drawn[i]), 0) / truth.length / speed;
    return { stallPct: (100 * stalls) / speeds.length, speedJitter: sd / mean, lagMs };
  }

  it('glides without stalls when frames are pushed and numbered', () => {
    const before = simulate(false);
    const after = simulate(true);
    console.log(
      `walking bot drawn at 60 fps: polled ${before.stallPct.toFixed(1)}% stalled frames, speed jitter ${before.speedJitter.toFixed(2)}, lag ${before.lagMs.toFixed(0)} ms; ` +
        `pushed ${after.stallPct.toFixed(1)}% stalled, jitter ${after.speedJitter.toFixed(2)}, lag ${after.lagMs.toFixed(0)} ms`,
    );
    expect(after.stallPct).toBeLessThan(before.stallPct);
    expect(after.stallPct).toBeLessThan(5);
    expect(after.speedJitter).toBeLessThan(before.speedJitter);
    expect(after.lagMs).toBeLessThan(before.lagMs);
  });

  it('a read between frames leaves the glide alone; a new frame number moves it', () => {
    const { s, at } = clocked();
    s.apply({ ...delta(1, [], { full: true, bots: [bot(0)] }), frame: 1 });
    at(1200);
    s.apply({ ...delta(1, [], { bots: [bot(4)] }), frame: 2 });
    at(1300);
    s.apply({ ...delta(1, [], { bots: [bot(4)] }), frame: 2 }); // the same frame read again: no restart
    expect(s.bot('main')!.x).toBeCloseTo(2);
    at(1400);
    expect(s.bot('main')!.x).toBeCloseTo(4); // still arrives on time (a restart would put it at 3)
    expect(s.intervalMs).toBeCloseTo(200);
  });
});
