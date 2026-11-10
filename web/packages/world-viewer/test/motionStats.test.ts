import { describe, expect, it } from 'vitest';
import { EntityStore } from '../src/world/entities';
import { RecentFigures } from '../src/world/motion';
import type { BotDto, EntityDelta } from '../src/types';

// The frame-rate overlay's motion line reads these figures to tell where an uneven pawn comes from: the bot or the
// bridge (src), a relay or the network (arr), or the playout itself (buf, pauses).

const EPOCH = 1_700_000_000_000;
const WALK = 0.0043;
const pawn = (x: number): BotDto => ({
  name: 'pawn',
  short: 'p',
  main: false,
  dim: 0,
  x,
  y: 64,
  z: 0,
  hp: 20,
  food: 20,
});
const frameOf = (frame: number, sampled: number): EntityDelta => ({
  rev: 1,
  frame,
  full: true,
  at: EPOCH + sampled,
  bots: [pawn(WALK * sampled)],
  entities: [],
});

describe('ten-second figures', () => {
  it('counts add up, gaps keep their longest, and a second leaves the window whole', () => {
    const f = new RecentFigures(2);
    f.add(500, 0);
    f.add(900, 0);
    f.max(900, 1, 40);
    f.max(1200, 1, 30);
    expect(f.sum(1200, 0)).toBe(2);
    expect(f.most(1200, 1)).toBe(40);
    expect(f.most(9_999, 1)).toBe(40);
    expect(f.most(10_000, 1)).toBe(30); // the second 0..1 s is out
    expect(f.most(12_000, 1)).toBe(0);
    expect(f.sum(12_000, 0)).toBe(0);
    // Twenty in the first two seconds: ten a second, not two (the window is not full yet).
    const r = new RecentFigures(1);
    for (let t = 0; t < 2000; t += 100) r.add(t, 0);
    expect(r.rate(2000, 0)).toBeCloseTo(10);
    expect(r.rate(30_000, 0)).toBe(0);
  });

  it('the store reports frames a second, the longest step at the source and on arrival, the delay and the pauses', () => {
    let now = 0,
      frame = 0;
    const store = new EntityStore(() => now);
    const land = (sampled: number, arrives: number) => {
      now = arrives;
      store.apply(frameOf(++frame, sampled));
    };
    // 5 s of a clean 10 Hz stream, 20 ms on the way.
    for (let s = 0; s < 5000; s += 100) land(s, s + 20);
    let m = store.motionFigures();
    expect(m.rate).toBeCloseTo(10, 0);
    expect(m.src).toBe(100);
    expect(m.arr).toBe(100);
    expect(m.buf).toBeCloseTo(250, 0);
    expect(m.pauses).toBe(0);
    // A relay holds three frames back and hands them over together: the source kept its pace, the arrivals did not.
    land(5000, 5020);
    for (const s of [5100, 5200, 5300]) land(s, 5420);
    m = store.motionFigures();
    expect(m.src).toBe(100);
    expect(m.arr).toBe(400);
    expect(m.pauses).toBe(1); // the pawn's playout ran dry before they came
    expect(m.buf).toBeGreaterThan(250);
    // Then the bot misses four frames: a 500 ms step at the source.
    land(5400, 5520);
    land(5900, 5920);
    for (let s = 6000; s < 8000; s += 100) land(s, s + 20);
    m = store.motionFigures();
    expect(m.src).toBe(500);
    expect(m.arr).toBe(400);
    expect(m.pauses).toBe(store.pauses);
    // A quiet moment (nothing changed for 3 s) is no step in the stream.
    land(11_000, 11_020);
    // Ten seconds later the hiccups are out of the window; the total stays.
    for (let s = 11_100; s < 19_000; s += 100) land(s, s + 20);
    m = store.motionFigures();
    expect(m.src).toBe(100);
    expect(m.arr).toBe(100);
    expect(m.pauses).toBe(0);
    expect(store.pauses).toBeGreaterThanOrEqual(1);
  });

  it('a stream without sender stamps measures its arrivals at both ends', () => {
    let now = 0;
    const store = new EntityStore(() => now);
    for (let i = 0; i < 20; i++) {
      now = 1000 + i * 100 + (i >= 10 ? 150 : 0);
      store.apply({ rev: i + 1, full: true, bots: [pawn(i)], entities: [] });
    }
    const m = store.motionFigures();
    expect(m.src).toBe(250);
    expect(m.arr).toBe(250);
    expect(m.buf).toBe(0); // no timestamps, no playout delay
  });
});
