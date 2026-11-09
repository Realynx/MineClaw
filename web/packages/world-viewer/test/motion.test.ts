import { describe, expect, it } from 'vitest';
import { EntityStore } from '../src/world/entities';

function harness(timestamped = true) {
  let now = 1000, frame = 0;
  const store = new EntityStore(() => now);
  const receive = (at: number, x: number, y = 64, dim = 0, sampled = at) => {
    now = at;
    store.apply({ rev: 1, frame: ++frame, full: true, ...(timestamped ? { at: 1_700_000_000_000 + sampled } : {}), entities: [],
      bots: [{ name: 'pawn', short: '1', main: false, hp: 20, food: 20, dim, x, y, z: 0, yaw: 0 }] });
  };
  return { store, receive, at: (at: number) => { now = at; return store.bot('pawn')!; } };
}

function simulate(cadence: number[], timestamped: boolean) {
  const h = harness(timestamped), speed = 0.0043, samples: Array<{ at: number; x: number }> = [];
  let next = 1000, n = 0;
  for (let t = 1000; t < 21000; t += 1000 / 60) {
    if (t >= next) { h.receive(t, (next - 1000) * speed, 64, 0, next); next += cadence[n++ % cadence.length]; }
    if (t > 4000) samples.push({ at: t, x: h.at(t).x });
  }
  const speeds = samples.slice(1).map((s, i) => (s.x - samples[i].x) / (s.at - samples[i].at));
  return { stalled: speeds.filter((v) => v < speed * 0.25).length / speeds.length,
    jitter: Math.sqrt(speeds.reduce((sum, v) => sum + (v - speed) ** 2, 0) / speeds.length) / speed,
    lag: samples.reduce((sum, p) => sum + ((p.at - 1000) * speed - p.x) / speed, 0) / samples.length };
}

describe('timestamped viewer motion', () => {
  it('reduces the spectator buffer to 150ms for the faster 10Hz stream', () => {
    const result = simulate([100], true);
    expect(result.stalled).toBeLessThan(0.01);
    expect(result.lag).toBeGreaterThan(140);
    expect(result.lag).toBeLessThan(175);
  });
  it('keeps walking smooth across occasional delayed frames, without adding an unbounded display delay', () => {
    const cadence = [200, 200, 400, 200, 200];
    const before = simulate(cadence, false), after = simulate(cadence, true);
    expect(after.stalled).toBeLessThan(0.06);
    expect(after.stalled).toBeLessThan(before.stalled / 2);
    expect(after.jitter).toBeLessThan(before.jitter);
    expect(after.lag).toBeLessThan(300);
  });

  it('handles burst delivery using sample timestamps instead of accelerating every short arrival interval', () => {
    const h = harness();
    for (let t = 1000; t <= 2000; t += 200) h.receive(t, (t - 1000) * 0.004);
    const before = h.at(2500).x;
    h.receive(2500, 4.8, 64, 0, 2200);
    expect(h.at(2500).x).toBeCloseTo(before);
    h.receive(2510, 5.6, 64, 0, 2400);
    expect(h.at(2610).x).toBeCloseTo(5.44);
    expect(h.at(2650).x).toBeCloseTo(5.6);
  });

  it('resumes after quiet in 250ms, and resets its estimate instead of interpolating over the idle time', () => {
    const h = harness(); h.receive(1000, 0); h.receive(11000, 0.86);
    expect(h.store.intervalMs).toBe(200);
    expect(h.at(11125).x).toBeCloseTo(0.43);
    expect(h.at(11250).x).toBeCloseTo(0.86);
  });

  it('bounds prediction to 0.4 blocks and settles to the known position when updates stop', () => {
    const h = harness();
    h.receive(1000, 0); h.receive(1200, 0.86); h.receive(1400, 1.72);
    for (let t = 1400; t < 2400; t += 10) {
      expect(h.at(t).x).toBeLessThanOrEqual(2.12);
      expect(h.at(t).y).toBe(64);
    }
    expect(h.at(2000).x).toBeCloseTo(1.72);
    h.receive(2400, 1.72);
    expect(h.at(2650).x).toBeCloseTo(1.72);
  });

  it('never predicts vertical movement, snaps teleports, and discards old dimension history', () => {
    const h = harness();
    h.receive(1000, 0); h.receive(1200, 0.8, 65); h.receive(1400, 1.6, 66);
    expect(h.at(1800).y).toBe(66);
    expect(h.at(1800).x).toBe(1.6);
    h.receive(1900, 50, 70);
    expect(h.at(1900).x).toBe(50);
    h.receive(2000, 1, 40, -1);
    expect(h.at(2000)).toMatchObject({ x: 1, y: 40, dim: -1 });
  });
});
