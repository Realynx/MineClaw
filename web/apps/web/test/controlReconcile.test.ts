import { describe, expect, it } from 'vitest';
import {
  decaySpring,
  InputLog,
  MATCH_FALLBACK_MS,
  matchTime,
  OFFSET_TAU_MS,
  PoseHistory,
  reconcile,
  RECONCILE_LIMITS,
  restSpring,
  TICK_MS,
  TickClock,
  type Pose,
  type Spring,
  type Vec3,
} from '@/features/control/controlReconcile';

const AXES = ['x', 'y', 'z', 'vx', 'vy', 'vz'] as const;
const spring = (x = 0, y = 0, z = 0, vx = 0, vy = 0, vz = 0): Spring => ({ x, y, z, vx, vy, vz });
const pose = (x: number, more: Partial<Pose> = {}): Pose => ({
  x,
  y: 64,
  z: 0,
  vx: 0,
  vy: 0,
  vz: 0,
  onGround: true,
  ...more,
});
function decayIn(start: Spring, count: number, dtMs: number): Spring {
  let s = start;
  for (let i = 0; i < count; i++) s = decaySpring(s, dtMs, OFFSET_TAU_MS);
  return s;
}

describe('decaySpring', () => {
  it('gives the same result however the time is cut into frames', () => {
    const start = spring(0.3, -0.12, 0.05, 0.004, 0, -0.002);
    const once = decaySpring(start, 100, OFFSET_TAU_MS);
    for (const [count, dtMs] of [
      [10, 10],
      [6, 100 / 6],
      [100, 1],
    ] as const) {
      const cut = decayIn(start, count, dtMs);
      for (const axis of AXES) expect(Math.abs(cut[axis] - once[axis]), `${count} steps, ${axis}`).toBeLessThan(1e-9);
    }
    // The input is left alone unless it is also named as the output.
    expect(start).toEqual(spring(0.3, -0.12, 0.05, 0.004, 0, -0.002));
    const inPlace = { ...start };
    expect(decaySpring(inPlace, 100, OFFSET_TAU_MS, inPlace)).toBe(inPlace);
    expect(inPlace).toEqual(once);
  });

  it('from rest goes straight to zero: never back, never past it, and under 2 % is left after 300 ms', () => {
    const start = spring(0.3, -0.2, 1);
    let s = start;
    for (let t = 5; t <= 600; t += 5) {
      const next = decaySpring(s, 5, OFFSET_TAU_MS);
      expect(next.x).toBeGreaterThan(0);
      expect(next.x).toBeLessThan(s.x);
      expect(next.y).toBeLessThan(0);
      expect(next.y).toBeGreaterThan(s.y);
      expect(next.z).toBeGreaterThan(0);
      expect(next.z).toBeLessThan(s.z);
      s = next;
      // (1 + t/tau) * e^(-t/tau) of the start is left: 40.6 % at 100 ms, 9.2 % at 200, 1.7 % at 300.
      if (t === 100) expect(s.z).toBeCloseTo(3 * Math.exp(-2), 12);
      if (t === 200) expect(s.z).toBeCloseTo(5 * Math.exp(-4), 12);
      if (t === 300)
        for (const axis of ['x', 'y', 'z'] as const) expect(Math.abs(s[axis] / start[axis])).toBeLessThan(0.02);
    }
  });

  it('does nothing for no time, and is at rest after an endless step or with no decay time', () => {
    const start = spring(0.3, 0.1, -0.2, 0.01, 0, 0.02);
    expect(decaySpring(start, 0, OFFSET_TAU_MS)).toEqual(start);
    expect(decaySpring(start, -16, OFFSET_TAU_MS)).toEqual(start);
    expect(decaySpring(start, Number.NaN, OFFSET_TAU_MS)).toEqual(start);
    expect(decaySpring(start, Number.POSITIVE_INFINITY, OFFSET_TAU_MS)).toEqual(restSpring());
    expect(decaySpring(start, 16, 0)).toEqual(restSpring());
  });
});

describe('reconcile', () => {
  const predicted = { x: 10, y: 64, z: -3 };
  const offset = spring(0.05, 0, -0.02, 0.001, 0, -0.0005);
  const rendered = (body: Vec3, shift: Vec3, o: Spring) => ({
    x: body.x + shift.x + o.x,
    y: body.y + shift.y + o.y,
    z: body.z + shift.z + o.z,
  });

  it('leaves an error inside the deadband alone', () => {
    const r = reconcile(predicted, { x: 10.006, y: 64, z: -3.007 }, offset, RECONCILE_LIMITS);
    expect(r).toEqual({ shift: { x: 0, y: 0, z: 0 }, offset, snapped: false });
  });

  it('snaps beyond the snap distance, or when told to, and clears the offset', () => {
    const far = reconcile(predicted, { x: 10, y: 64, z: 1.5 }, offset, RECONCILE_LIMITS);
    expect(far.snapped).toBe(true);
    expect(far.offset).toEqual(restSpring());
    expect(far.shift).toEqual({ x: 0, y: 0, z: 4.5 });
    // Just inside the snap distance is still an ordinary correction.
    expect(reconcile(predicted, { x: 10, y: 64, z: 0.9 }, offset, RECONCILE_LIMITS).snapped).toBe(false);
    // A dimension change or a server teleport snaps however near the pawn landed.
    const forced = reconcile(predicted, { x: 10.2, y: 64, z: -3 }, offset, { ...RECONCILE_LIMITS, force: true });
    expect(forced.snapped).toBe(true);
    expect(forced.offset).toEqual(restSpring());
    expect(forced.shift.x).toBeCloseTo(0.2, 12);
  });

  it('moves the body by the whole error and the offset by its opposite, so the rendered point stays put', () => {
    const authority = { x: 10.3, y: 64.5, z: -3.2 };
    const r = reconcile(predicted, authority, offset, RECONCILE_LIMITS);
    expect(r.snapped).toBe(false);
    expect(predicted.x + r.shift.x).toBeCloseTo(authority.x, 12);
    expect(predicted.y + r.shift.y).toBeCloseTo(authority.y, 12);
    expect(predicted.z + r.shift.z).toBeCloseTo(authority.z, 12);
    const before = rendered(predicted, { x: 0, y: 0, z: 0 }, offset);
    const after = rendered(predicted, r.shift, r.offset);
    for (const axis of ['x', 'y', 'z'] as const) expect(after[axis]).toBeCloseTo(before[axis], 12);
    // The rates are untouched, so the speed on screen does not change either.
    expect([r.offset.vx, r.offset.vy, r.offset.vz]).toEqual([offset.vx, offset.vy, offset.vz]);
    // The caller's offset is not modified.
    expect(offset).toEqual(spring(0.05, 0, -0.02, 0.001, 0, -0.0005));
  });

  it('keeps position and rate continuous when a second correction lands in the middle of a decay', () => {
    const body = { x: 0, y: 64, z: 0 };
    const first = reconcile(body, { x: 0.3, y: 64, z: 0 }, restSpring(), RECONCILE_LIMITS);
    const moved = { x: body.x + first.shift.x, y: 64, z: 0 };
    const decaying = decaySpring(first.offset, 60, OFFSET_TAU_MS);
    expect(decaying.vx).toBeGreaterThan(0);
    const second = reconcile(moved, { x: moved.x - 0.2, y: 64, z: 0 }, decaying, RECONCILE_LIMITS);
    const point = moved.x + decaying.x;
    expect(moved.x + second.shift.x + second.offset.x).toBeCloseTo(point, 12);
    expect(second.offset.vx).toBe(decaying.vx);
    // And a moment later the rendered point has moved as far as it would have without the correction, to first order.
    const moment = 0.001;
    const without = moved.x + decaySpring(decaying, moment, OFFSET_TAU_MS).x - point;
    const withIt = moved.x + second.shift.x + decaySpring(second.offset, moment, OFFSET_TAU_MS).x - point;
    expect(Math.abs(without)).toBeGreaterThan(1e-7);
    expect(Math.abs(withIt - without)).toBeLessThan(1e-9);
  });

  it('ignores an error that is not a number', () => {
    const r = reconcile(predicted, { x: Number.NaN, y: 64, z: -3 }, offset, RECONCILE_LIMITS);
    expect(r).toEqual({ shift: { x: 0, y: 0, z: 0 }, offset, snapped: false });
  });
});

describe('TickClock', () => {
  /** Nine frames, one per tick, whose inputs each waited 20 ms: every one puts tick 400 at local time 1020. */
  function steady(clock: TickClock): void {
    for (let k = 0; k < 9; k++) clock.observe(1000 + TICK_MS * k, 20, 400 + k);
  }

  it('knows nothing before the first observation', () => {
    expect(new TickClock().timeOf(400)).toBeNull();
  });

  it('places a tick half a tick after the moment its input was sent plus the wait', () => {
    const clock = new TickClock();
    clock.observe(1000, 20, 400);
    expect(clock.timeOf(400)).toBe(1000 + 20 + 25);
    expect(clock.timeOf(402)).toBe(1000 + 20 + 125);
    expect(clock.timeOf(399)).toBe(1000 + 20 - 25);
  });

  it('is not moved by one outlier in nine observations', () => {
    const late = new TickClock();
    steady(late);
    // This input took 60 ms longer to reach the server than the others, so it puts the ticks 60 ms early. It joins
    // the window and the median stays where it was.
    late.observe(1000 + TICK_MS * 9 - 60, 20, 409);
    expect(late.timeOf(400)).toBe(1045);
    // One that is far off is not let in at all, so it cannot move even an estimate built on a single observation.
    const wild = new TickClock();
    wild.observe(1000, 20, 400);
    wild.observe(1000 + TICK_MS + 700, 20, 401);
    expect(wild.timeOf(400)).toBe(1045);
  });

  it('follows the median of the last nine, not of everything it has seen', () => {
    const clock = new TickClock();
    steady(clock);
    for (let k = 9; k < 13; k++) clock.observe(1000 + TICK_MS * k + 40, 20, 400 + k);
    expect(clock.timeOf(400)).toBe(1045);
    clock.observe(1000 + TICK_MS * 13 + 40, 20, 413);
    expect(clock.timeOf(400)).toBe(1085);
  });

  it('learns nothing from a stopped sample, an unacknowledged one, or one whose input waited over 200 ms', () => {
    const clock = new TickClock();
    clock.observe(1000, 20, 400, true);
    clock.observe(undefined, 20, 400);
    clock.observe(1000, undefined, 400);
    clock.observe(1000, 20, undefined);
    clock.observe(1000, 201, 400);
    clock.observe(1000, -1, 400);
    clock.observe(1000, Number.NaN, 400);
    expect(clock.timeOf(400)).toBeNull();
    clock.observe(1000, 200, 400);
    expect(clock.timeOf(400)).toBe(1225);
    // The same holds once it has an estimate.
    clock.observe(5000, 20, 401, true);
    clock.observe(5000, 250, 401);
    clock.observe(5000, 250, 402);
    clock.observe(5000, 250, 403);
    expect(clock.timeOf(400)).toBe(1225);
  });

  it('starts over after three observations in a row more than 100 ms off, and not after two', () => {
    const clock = new TickClock();
    steady(clock);
    // The server's tick loop dropped a backlog: its ticks now fall 300 ms later.
    clock.observe(1000 + TICK_MS * 9 + 300, 20, 409);
    clock.observe(1000 + TICK_MS * 10 + 300, 20, 410);
    expect(clock.timeOf(400)).toBe(1045);
    // An ordinary observation in between breaks the run.
    clock.observe(1000 + TICK_MS * 11, 20, 411);
    clock.observe(1000 + TICK_MS * 12 + 300, 20, 412);
    clock.observe(1000 + TICK_MS * 13 + 300, 20, 413);
    expect(clock.timeOf(400)).toBe(1045);
    clock.observe(1000 + TICK_MS * 14 + 310, 20, 414);
    expect(clock.timeOf(400)).toBe(1345);
    // The new estimate is built from those three alone, so the next ordinary sample on the new phase keeps it.
    clock.observe(1000 + TICK_MS * 15 + 300, 20, 415);
    expect(clock.timeOf(400)).toBe(1345);
    // 101 ms off is a stray, 100 ms is not.
    const edge = new TickClock();
    edge.observe(1000, 20, 400);
    for (let k = 1; k <= 2; k++) edge.observe(1000 + TICK_MS * k + 101, 20, 400 + k);
    expect(edge.timeOf(400)).toBe(1045);
    edge.observe(1000 + TICK_MS * 3 + 100, 20, 403);
    expect(edge.timeOf(400)).toBe(1095);
  });

  it('forgets everything on reset', () => {
    const clock = new TickClock();
    steady(clock);
    clock.reset();
    expect(clock.timeOf(400)).toBeNull();
    clock.observe(9000, 0, 7);
    expect(clock.timeOf(7)).toBe(9025);
  });

  it('matches a start to within half a tick of movement, whatever the link delay and the moment of the key press', () => {
    // A server that ticks on its own phase and gives each tick the newest input that has arrived, against a body that
    // is at full speed from the moment the key is pressed. A heartbeat goes out every 50 ms, as in the app.
    const speed = 0.2159;
    const tickAt = (k: number) => 13 + TICK_MS * k;
    for (const uplink of [3, 40, 110]) {
      let firstErrors = 0;
      let starts = 0;
      for (let pressed = 1000; pressed < 1050; pressed++) {
        const clock = new TickClock();
        const log = new InputLog();
        const inputs: Array<{ sentAt: number; moving: boolean }> = [{ sentAt: pressed, moving: true }];
        for (let at = 600; at < 1400; at += TICK_MS) inputs.push({ sentAt: at, moving: at >= pressed });
        inputs.sort((a, b) => a.sentAt - b.sentAt);
        inputs.forEach((input, seq) => log.record(seq, input.sentAt));
        let authority = 0;
        for (let k = 14; tickAt(k) < 1400; k++) {
          let seq = -1;
          inputs.forEach((input, i) => {
            if (input.sentAt + uplink <= tickAt(k)) seq = i;
          });
          const input = inputs[seq]!;
          if (input.moving) authority += speed;
          clock.observe(log.at(seq), tickAt(k) - input.sentAt - uplink, k);
          const predicted = (speed * Math.max(0, clock.timeOf(k)! - pressed)) / TICK_MS;
          // "Has not reacted yet" is no longer a disagreement of speed x latency: 25 ms of movement at the most.
          expect(Math.abs(authority - predicted)).toBeLessThanOrEqual(speed / 2 + 1e-9);
          if (authority === speed) {
            firstErrors += authority - predicted;
            starts++;
          }
        }
      }
      // Over every moment in a tick at which the key can go down, the error at the first moving tick averages zero.
      expect(starts).toBe(50);
      expect(Math.abs(firstErrors / starts)).toBeLessThan(speed * 0.02);
    }
  });
});

describe('matchTime', () => {
  it('steps a fixed 75 ms back from the arrival until the clock knows the tick', () => {
    const clock = new TickClock();
    expect(matchTime(clock, undefined, 2000)).toBe(2000 - MATCH_FALLBACK_MS);
    expect(matchTime(clock, 400, 2000)).toBe(1925);
    clock.observe(1000, 20, 400);
    expect(matchTime(clock, 402, 2000)).toBe(1145);
    // A frame from an older server has no tick even when the clock has learned from others.
    expect(matchTime(clock, undefined, 2000)).toBe(1925);
  });
});

describe('InputLog', () => {
  it('returns when an input was sent, and nothing for one it does not know', () => {
    const log = new InputLog();
    expect(log.at(1)).toBeUndefined();
    log.record(1, 100.5);
    log.record(2, 150.25);
    expect(log.at(1)).toBe(100.5);
    expect(log.at(2)).toBe(150.25);
    expect(log.at(3)).toBeUndefined();
    // The server acknowledges "no input yet" with a negative number.
    expect(log.at(-1)).toBeUndefined();
    expect(log.at(1.5)).toBeUndefined();
    log.clear();
    expect(log.at(1)).toBeUndefined();
  });

  it('remembers the last 256 inputs', () => {
    const log = new InputLog();
    for (let seq = 1; seq <= 257; seq++) log.record(seq, seq * 10);
    expect(log.at(1)).toBeUndefined();
    expect(log.at(2)).toBe(20);
    expect(log.at(257)).toBe(2570);
  });
});

describe('PoseHistory', () => {
  it('interpolates between entries and clamps at both ends', () => {
    const history = new PoseHistory();
    expect(history.at(100)).toBeNull();
    history.push(100, pose(1, { vx: 0.1, onGround: true }));
    history.push(110, pose(2, { y: 65, vx: 0.3, vy: -0.08, onGround: false }));
    history.push(120, pose(4, { y: 65, z: 2, onGround: false }));
    const mid = history.at(105)!;
    expect(mid.x).toBeCloseTo(1.5, 12);
    expect(mid.y).toBeCloseTo(64.5, 12);
    expect(mid.vx).toBeCloseTo(0.2, 12);
    expect(mid.vy).toBeCloseTo(-0.04, 12);
    expect(history.at(117.5)).toMatchObject({ x: 3.5, z: 1.5 });
    // The flag is not a number to blend: it comes from the nearer entry.
    expect(history.at(104)!.onGround).toBe(true);
    expect(history.at(106)!.onGround).toBe(false);
    expect(history.at(110)).toEqual(pose(2, { y: 65, vx: 0.3, vy: -0.08, onGround: false }));
    expect(history.at(50)).toEqual(pose(1, { vx: 0.1 }));
    expect(history.at(5000)).toEqual(pose(4, { y: 65, z: 2, onGround: false }));
    expect(history.at(Number.NaN)).toEqual(pose(4, { y: 65, z: 2, onGround: false }));
  });

  it('moves every entry on a shift, and the velocity too when asked', () => {
    const history = new PoseHistory();
    for (let i = 0; i < 5; i++) history.push(i * 10, pose(i, { vx: 0.2 }));
    history.shift({ x: 0.3, y: -1, z: 2 });
    for (let i = 0; i < 5; i++) expect(history.at(i * 10)).toEqual(pose(i + 0.3, { y: 63, z: 2, vx: 0.2 }));
    history.shift({ x: 0, y: 0, z: 0 }, { x: -0.2, y: 0.42, z: 0.1 });
    for (let i = 0; i < 5; i++)
      expect(history.at(i * 10)).toEqual(pose(i + 0.3, { y: 63, z: 2, vx: 0, vy: 0.42, vz: 0.1 }));
  });

  it('keeps the last second, with one entry at its start to interpolate from', () => {
    const history = new PoseHistory();
    for (let t = 0; t <= 3000; t += 10) history.push(t, pose(t));
    expect(history.size).toBe(101);
    expect(history.at(0)!.x).toBe(2000);
    expect(history.at(2005)!.x).toBeCloseTo(2005, 9);
    expect(history.at(3000)!.x).toBe(3000);
    history.clear();
    expect(history.size).toBe(0);
    expect(history.at(3000)).toBeNull();
  });

  it('forgets the oldest entry when it is full, however short the time it then covers', () => {
    const history = new PoseHistory(1000, 4);
    for (let t = 1; t <= 6; t++) history.push(t, pose(t));
    expect(history.size).toBe(4);
    expect(history.at(0)!.x).toBe(3);
    expect(history.at(4.5)!.x).toBeCloseTo(4.5, 12);
    expect(history.at(6)!.x).toBe(6);
  });

  it('never runs backwards: a push that is not later replaces the newest entry', () => {
    const history = new PoseHistory();
    history.push(100, pose(1));
    history.push(116, pose(2));
    history.push(116, pose(2.5));
    history.push(110, pose(3));
    expect(history.size).toBe(2);
    expect(history.at(116)!.x).toBe(3);
    expect(history.at(108)!.x).toBeCloseTo(2, 12);
  });
});

describe('the pieces together', () => {
  it('take a sideways error seen through 100 ms of delay once, and ease it in without overshoot', () => {
    // The local body walks along x at walking speed. From tick 10 on the pawn is really 0.3 blocks to the side. Each
    // tick's sample arrives 100 ms after the moment it describes, which is where the old camera overshot by half.
    const step = 10;
    const speed = 0.2159;
    const body = pose(0, { vx: speed });
    const history = new PoseHistory();
    const clock = new TickClock();
    let offset = restSpring();
    let corrections = 0;
    let previous = 0;
    let firstCorrection = 0;
    const side: number[] = [];
    for (let now = 0; now <= 1500; now += step) {
      if (now > 0) decaySpring(offset, step, OFFSET_TAU_MS, offset);
      body.x = (speed * now) / TICK_MS;
      history.push(now, body);
      const sampledAt = now - 100;
      if (sampledAt >= 0 && sampledAt % TICK_MS === 0) {
        const tick = sampledAt / TICK_MS;
        // An input that waited the average half tick: the clock then compares tick k with the time 50 * k exactly.
        clock.observe(sampledAt - TICK_MS, 25, tick);
        const at = matchTime(clock, tick, now);
        expect(at).toBe(sampledAt);
        const authority = { x: (speed * sampledAt) / TICK_MS, y: 64, z: tick >= 10 ? 0.3 : 0 };
        const r = reconcile(history.at(at)!, authority, offset, RECONCILE_LIMITS);
        expect(r.snapped).toBe(false);
        if (r.shift.x || r.shift.y || r.shift.z) {
          corrections++;
          firstCorrection ||= now;
          body.z += r.shift.z;
          history.shift(r.shift);
        }
        offset = r.offset;
      }
      const z = body.z + offset.z;
      expect(z).toBeGreaterThanOrEqual(previous);
      expect(z).toBeLessThanOrEqual(0.3 + 1e-12);
      previous = z;
      side.push(z);
    }
    expect(corrections).toBe(1);
    expect(firstCorrection).toBe(600);
    // Nothing moves on screen at the instant of the correction; 100 ms later 59 % of it is in.
    expect(side[firstCorrection / step]).toBe(0);
    expect(side[(firstCorrection + 100) / step]).toBeCloseTo(0.3 * (1 - 3 * Math.exp(-2)), 9);
    expect(side[(firstCorrection + 300) / step]!).toBeGreaterThan(0.3 * 0.98);
    expect(body.z).toBeCloseTo(0.3, 12);
  });
});
