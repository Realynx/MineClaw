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

describe('when a body was sampled', () => {
  const EPOCH = 1_700_000_000_000;
  const stamped = (ms: number, bots: BotDto[], frame: number): EntityDelta => ({
    rev: 1,
    full: true,
    bots,
    entities: [],
    at: EPOCH + ms,
    frame,
  });

  it('a live body is dated by its pose age; an age above 0.25 s, or on a body that is not live, is not used', () => {
    const { s, at } = clocked();
    // A block every 100 ms; every body is at the same place in every frame.
    for (let i = 0; i <= 20; i++) {
      at(1000 + i * 100);
      s.apply(
        stamped(
          i * 100,
          [
            bot(i, { name: 'aged', short: 'a', live: true, poseAge: 0.1 }),
            bot(i, { name: 'plain', short: 'p', live: true }),
            bot(i, { name: 'old', short: 'o', live: true, poseAge: 0.3 }),
            bot(i, { name: 'nolive', short: 'n', poseAge: 0.1 }),
          ],
          i + 1,
        ),
      );
    }
    at(3050);
    s.beginFrame();
    const plain = s.bot('plain')!.x;
    expect(s.bot('aged')!.x - plain).toBeCloseTo(1); // its samples are 100 ms older: drawn where it was 100 ms later
    expect(s.bot('old')!.x).toBe(plain);
    expect(s.bot('nolive')!.x).toBe(plain);
  });
});

// An AI hive body (`live: false`) is published in every frame, but its place is the hub's snapshot, which changes every
// 1 to 2.5 s. Played out by the frames' stamps it stood for a second, then crossed the distance in one frame interval.
describe('AI hive bodies (live: false)', () => {
  const EPOCH = 1_700_000_000_000;
  const hive = (x: number, extra: Partial<BotDto> = {}) =>
    bot(x, { name: 'Hive', short: 'h1', main: false, live: false, ...extra });
  const stamped = (ms: number, b: BotDto, frame: number): EntityDelta => ({
    rev: 1,
    full: true,
    bots: [b],
    entities: [],
    at: EPOCH + ms,
    frame,
  });

  it('glide on, one update behind, instead of standing and dashing', () => {
    const { s, at } = clocked();
    let frame = 0;
    const steps: number[] = [];
    let last = NaN;
    // Ten identical frames, then a 6-block change, over and over: 6 blocks a second on average.
    for (let k = 0; k < 120; k++) {
      for (let t = 0; t < 100; t += 25) {
        at(1000 + k * 100 + t);
        if (k >= 30) {
          const x = s.bot('Hive')!.x;
          if (!Number.isNaN(last)) steps.push(x - last);
          last = x;
        }
        if (t === 0) s.apply(stamped(k * 100, hive(6 * Math.floor(k / 10)), ++frame));
      }
    }
    const average = (6 / 1000) * 25;
    expect(Math.max(...steps)).toBeLessThan(2 * average);
    expect(Math.min(...steps)).toBeGreaterThanOrEqual(0); // never back
    expect(steps.filter((d) => d > 0).length).toBeGreaterThan(steps.length * 0.9); // moving nearly all the time
    expect(s.bot('Hive')!.x).toBeGreaterThan(6 * 10); // one update behind the newest place (66)
  });

  it('after a second away a hive body that went far appears there; a near one glides', () => {
    const { s, at } = clocked();
    s.apply(stamped(0, hive(0), 1));
    at(1100);
    s.apply(stamped(100, hive(0), 2));
    at(2600);
    s.apply(stamped(1600, hive(3), 3));
    expect(s.bot('Hive')!.x).toBe(3);
    at(2700);
    s.apply(stamped(1700, hive(3), 4));
    at(4200);
    s.apply(stamped(3200, hive(4), 5));
    expect(s.bot('Hive')!.x).toBeCloseTo(3);
    at(4200 + 1600 / 2);
    expect(s.bot('Hive')!.x).toBeCloseTo(3.5); // over the 1.6 s since its last change
  });

  it('a standing hive body turns its head', () => {
    const { s, at } = clocked();
    s.apply(stamped(0, hive(0, { yaw: 0 }), 1));
    at(1100);
    s.apply(stamped(100, hive(0, { yaw: 90 }), 2));
    at(1300);
    s.apply(stamped(300, hive(0, { yaw: 90 }), 3));
    at(1300 + s.intervalMs);
    expect(s.bot('Hive')!.yaw).toBeCloseTo(90);
    expect(s.bot('Hive')!.x).toBe(0);
  });
});

// The models are placed at the start of a frame, the selection outline and the page's overlay marks later in it. Each
// read used to take the clock again, so a ring led its body by the frame's own work (0.8 block at Faster and a 60 ms frame).
describe('one sample instant per frame (beginFrame)', () => {
  const stamped = (ms: number, x: number, frame: number): EntityDelta => ({
    ...delta(1, [mob(7, x, { kind: 'item', age: 1 })], { full: true, bots: [bot(x)] }),
    at: 1_700_000_000_000 + ms,
    frame,
  });

  it('every read of a frame returns the same positions while the clock runs on', () => {
    const { s, at } = clocked();
    s.apply(delta(1, [mob(7, 0)], { full: true }));
    at(1200);
    s.apply(delta(2, [mob(7, 6)], { bots: [bot(4)] }));
    at(1250);
    s.beginFrame();
    const body = s.bots()[0].x,
      ent = s.entities(0)[0].x;
    expect(body).toBeCloseTo(1);
    expect(ent).toBeCloseTo(1.5);
    at(1290); // 40 ms of the frame's work later, the outline and the overlay ask again
    expect(s.bot('main')!.x).toBe(body);
    expect(s.bots()[0].x).toBe(body);
    expect(s.entities(0)[0].x).toBe(ent);
    s.beginFrame(); // the next frame moves on
    expect(s.bot('main')!.x).toBeCloseTo(1.8);
    expect(s.entities(0)[0].x).toBeCloseTo(2.7);
  });

  it('holds for timestamped motion and for the ages that advance locally', () => {
    const { s, at } = clocked();
    for (let i = 0; i <= 30; i++) {
      at(1000 + i * 100);
      s.apply(stamped(i * 100, i * 0.43, i + 1));
    }
    at(4020);
    s.beginFrame();
    const body = s.bot('main')!.x,
      [drop] = s.entities(0);
    expect(body).toBeCloseTo((3020 - 250) * 0.0043, 1); // 250 ms behind the stream
    at(4080);
    expect(s.bot('main')!.x).toBe(body);
    expect(s.entities(0)[0]).toEqual(drop); // position and ageTicks alike
    s.beginFrame(4080);
    expect(s.bot('main')!.x - body).toBeCloseTo(60 * 0.0043);
    expect(s.entities(0)[0].ageTicks).toBeCloseTo(drop.ageTicks + 60 / 50);
  });

  it('a frame instant older than the last update never overshoots the blend that update started', () => {
    const { s, at } = clocked();
    for (let i = 0; i <= 2; i++) {
      at(1000 + i * 100);
      s.apply(stamped(i * 100, i, i + 1));
    }
    s.beginFrame(); // drawn at 1200: 250 ms behind, still at the start
    expect(s.bot('main')!.x).toBe(0);
    // No frame was drawn for a while (the fps cap, a busy page), and an update lands that has a third of a block to blend.
    at(1900);
    s.apply(stamped(900, 2.5, 4));
    expect(Math.abs(s.bot('main')!.x)).toBeLessThan(0.4); // the blend once, not eight times over (two blocks off)
    s.beginFrame();
    expect(s.bot('main')!.x).toBeCloseTo(2); // the frame after it draws the pawn where it waited
  });
});
