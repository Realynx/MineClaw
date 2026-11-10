import { describe, expect, it } from 'vitest';
import { EntityStore } from '../src/world/entities';
import type { BotDto, EntityDelta, EntityDto } from '../src/types';

/**
 * Playout across holes in the stream. A pawn moves along a path that the bot samples every 100 ms. The samples travel
 * on one ordered socket: 20 ms plus jitter each, some late, some never published, none overtaking the one before it,
 * and a sample that changes nothing is not sent at all (the bridge's rule). The browser draws on a 25 ms grid, the
 * 40 fps the viewer must hold in software, and applies what arrived since the last picture 22 ms after it, newest only
 * (the live batcher keeps one frame per animation frame).
 *
 * What the eye catches in the drawn steps after the first 6 s:
 * - a freeze: 80 ms or more of steps slower than a quarter of the true speed;
 * - a jump: a step faster than twice the true speed;
 * - a backstep: more than 0.02 block against the direction of travel.
 */

type Vec = [number, number, number];

const EPOCH = 1_700_000_000_000;
/** Blocks per ms. */
const WALK = 0.0043;
/** Story's Fast: too fast for a walk to be continued past its last sample, so nothing hides a late frame. */
const FAST = 2 * WALK;
const LADDER = 0.00235;
const SNEAK = 0.0013;

interface Stream {
  speed: number;
  /** `steps`: walk one block, then stand until the second is over. */
  path: 'straight' | 'turns' | 'ladder' | 'steps';
  /** Delivery takes 20 ms plus uniform 0..jitter. */
  jitter: number;
  /** A share `p` of the frames takes `ms` longer. */
  late?: { p: number; ms: number };
  /** The source publishes nothing in the last `ms` of every `every` ms. */
  hole?: { every: number; ms: number };
  /** Called at every picture with the drawn position along the path's axis (x, or y on the ladder). */
  each?: (t: number, at: number, store: EntityStore) => void;
}

function pathOf(kind: Stream['path'], v: number): (t: number) => Vec {
  if (kind === 'straight') return (t) => [v * t, 64, 0];
  if (kind === 'ladder') return (t) => [0, 64 + v * t, 0];
  if (kind === 'steps') return (t) => [Math.floor(t / 1000) + Math.min(1, (t % 1000) * v), 64, 0];
  // A right-angle turn every second.
  return (t) => {
    const leg = Math.floor(t / 1000),
      into = t - leg * 1000,
      along = Math.ceil(leg / 2),
      across = Math.floor(leg / 2);
    return leg % 2 === 0
      ? [v * (along * 1000 + into), 64, v * across * 1000]
      : [v * along * 1000, 64, v * (across * 1000 + into)];
  };
}

const body = (name: string, [x, y, z]: Vec): BotDto => ({
  name,
  short: name,
  main: false,
  hp: 20,
  food: 20,
  dim: 0,
  x,
  y,
  z,
  yaw: 0,
});

/** A stream frame sampled at `sampled` ms of the source's clock. */
const frameOf = (frame: number, sampled: number, bots: BotDto[]): EntityDelta => ({
  rev: 1,
  frame,
  full: true,
  at: EPOCH + sampled,
  entities: [],
  bots,
});

const pawn = (frame: number, sampled: number, p: Vec): EntityDelta => frameOf(frame, sampled, [body('pawn', p)]);

function play(s: Stream, seconds = 180) {
  let seed = 7;
  const rnd = () => (seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff; // the generator of entities.test.ts
  const path = pathOf(s.path, s.speed);
  let now = 0;
  const store = new EntityStore(() => now);

  const frames: Array<{ at: number; p: Vec; arrives: number }> = [];
  for (let at = 0; at < seconds * 1000; at += 100) {
    if (s.hole && at % s.hole.every >= s.hole.every - s.hole.ms) continue;
    const p = path(at),
      sent = frames.at(-1);
    if (sent && sent.p.every((c, i) => c === p[i])) continue;
    let takes = 20 + rnd() * s.jitter;
    if (s.late && rnd() < s.late.p) takes += s.late.ms;
    frames.push({ at, p, arrives: Math.max(at + takes, (sent?.arrives ?? 0) + 0.5) });
  }

  const drawn: Array<{ t: number; p: Vec }> = [];
  let next = 0;
  for (let t = rnd() * 25; t < seconds * 1000; t += 25) {
    now = t;
    const b = store.bot('pawn');
    if (b) {
      if (t > 6000) drawn.push({ t, p: [b.x, b.y, b.z] });
      s.each?.(t, s.path === 'ladder' ? b.y : b.x, store);
    }
    let newest: (typeof frames)[number] | null = null;
    for (; next < frames.length && frames[next].arrives <= t; next++) newest = frames[next];
    if (!newest) continue;
    now = t + 22;
    store.apply(pawn(next, newest.at, newest.p));
  }

  let freezes = 0,
    jumps = 0,
    backsteps = 0,
    fastest = 0,
    lag = 0,
    still = 0,
    jumping = false,
    backing = false;
  for (let i = 1; i < drawn.length; i++) {
    const a = drawn[i - 1],
      b = drawn[i],
      ms = b.t - a.t;
    const step = b.p.map((c, k) => c - a.p[k]);
    const pace = Math.hypot(...step) / (s.speed * ms);
    // Where the path was heading when these positions were sampled, about 300 ms earlier.
    const to = path(b.t - 300),
      from = path(a.t - 300),
      heading = to.map((c, k) => c - from[k]);
    const along = step.reduce((sum, c, k) => sum + c * heading[k], 0) / (Math.hypot(...heading) || 1);
    if (pace < 0.25) {
      if (still < 80 && still + ms >= 80) freezes++;
      still += ms;
    } else still = 0;
    if (pace > 2 && !jumping) jumps++;
    jumping = pace > 2;
    if (along < -0.02 && !backing) backsteps++;
    backing = along < -0.02;
    fastest = Math.max(fastest, pace);
    lag += Math.hypot(...path(b.t).map((c, k) => c - b.p[k])) / s.speed;
  }
  return { freezes, jumps, backsteps, fastest, lag: lag / (drawn.length - 1), store };
}

describe('playout across holes in the stream', () => {
  it('jitter is absorbed', () => {
    const seen = play({ speed: FAST, path: 'straight', jitter: 120 });
    expect(seen).toMatchObject({ freezes: 0, jumps: 0, backsteps: 0 });
  });

  it('a late frame is not a stop', () => {
    // One frame in twenty takes 150 ms longer. With 50 ms of slack that was a stop about 17 times a minute.
    const seen = play({ speed: FAST, path: 'straight', jitter: 20, late: { p: 0.05, ms: 150 } });
    expect(seen).toMatchObject({ freezes: 0, jumps: 0, backsteps: 0 });
  });

  it('a short gap is not visible', () => {
    // Three frames missing every 5 s at a walk, two at Fast: both were a stop and a dash at every gap.
    const walking = play({ speed: WALK, path: 'straight', jitter: 20, hole: { every: 5000, ms: 300 } });
    expect(walking).toMatchObject({ freezes: 0, jumps: 0, backsteps: 0 });
    const fast = play({ speed: FAST, path: 'straight', jitter: 20, hole: { every: 5000, ms: 200 } });
    expect(fast).toMatchObject({ freezes: 0, jumps: 0, backsteps: 0 });
  });

  it('a long gap is a pause, never a jump or a step back', () => {
    // Six frames missing every 10 s. The pawn waits where the stream left it, walks on from there when it returns (it
    // used to cross the missed distance in 100 ms, up to 6 times too fast, after sliding back 0.4 block), and the delay
    // that leaves is worked off: five seconds later it is drawn as far behind as before.
    const after: number[] = [];
    let most = 0;
    const seen = play({
      speed: WALK,
      path: 'straight',
      jitter: 20,
      hole: { every: 10_000, ms: 600 },
      each: (t, _at, store) => {
        most = Math.max(most, store.playoutDelay);
        if (t > 10_000 && t % 10_000 >= 5000 && t % 10_000 < 5025) after.push(store.playoutDelay);
      },
    });
    expect(seen).toMatchObject({ jumps: 0, backsteps: 0 });
    expect(seen.fastest).toBeLessThan(1.3);
    expect(most).toBeGreaterThan(500);
    expect(most).toBeLessThanOrEqual(1000);
    expect(after).toHaveLength(17);
    for (const delay of after) {
      expect(delay).toBeGreaterThan(225);
      expect(delay).toBeLessThan(275);
    }
    expect(seen.store.pauses).toBe(17); // one for each hole the stream came back from
  });

  it('a hole on a ladder is a pause too: the pawn resumes along all three axes', () => {
    const seen = play({ speed: LADDER, path: 'ladder', jitter: 20, hole: { every: 10_000, ms: 600 } });
    expect(seen).toMatchObject({ jumps: 0, backsteps: 0 });
    expect(seen.fastest).toBeLessThan(1.3);
    expect(seen.store.pauses).toBe(17);
  });

  it('a slow body resumes at its own pace, not at a walk', () => {
    const seen = play({ speed: SNEAK, path: 'straight', jitter: 20, hole: { every: 10_000, ms: 600 } });
    expect(seen).toMatchObject({ jumps: 0, backsteps: 0 });
    expect(seen.fastest).toBeLessThan(1.3);
    expect(seen.store.pauses).toBe(17);
  });

  it('history covers the delay', () => {
    // Twenty frames at Fast, a 900 ms hole, then the stream again. The hole leaves the pawn 0.9 s behind, and while
    // that is worked off the playout reads samples that old. Eight stored points reached back 0.7 s: the stretch the
    // pawn was on was dropped under it, and it dashed 0.8 block ahead to the oldest point left.
    let now = 0,
      frame = 0;
    const store = new EntityStore(() => now);
    const land = (sampled: number) => {
      now = sampled + 20;
      store.apply(pawn(++frame, sampled, [FAST * sampled, 64, 0]));
    };
    for (let sampled = 0; sampled < 2000; sampled += 100) land(sampled);
    land(2800);
    expect(store.playoutDelay).toBeCloseTo(900);
    let last = store.bot('pawn')!.x;
    expect(last).toBeCloseTo(FAST * 1900); // it waited where the stream left it
    for (let t = 2845; t <= 4120; t += 25) {
      now = t;
      const x = store.bot('pawn')!.x,
        pace = (x - last) / (25 * FAST);
      expect(pace).toBeGreaterThan(0.9);
      expect(pace).toBeLessThan(1.3); // a little faster than life while the delay slews back
      last = x;
      if (t % 100 !== 20) continue;
      // As the next frame lands the last blend is over: the pawn is exactly where the path was one delay ago.
      expect(Math.abs(x - FAST * (t - 20 - store.playoutDelay))).toBeLessThan(0.02);
      land(t - 20);
    }
    expect(store.playoutDelay).toBeLessThan(750);
  });

  it('a stale first frame does not delay the picture', () => {
    // A tab that returns first reads the cache as its last flush left it, here 700 ms old, and anchors the stream
    // clock on it; the fresh frames that follow were then all drawn 700 ms too late until the stream next paused.
    let now = 10_000,
      frame = 0;
    const store = new EntityStore(() => now);
    const land = (sampled: number) => store.apply(pawn(++frame, sampled, [WALK * sampled, 64, 0]));
    land(9300);
    let lag = 0,
      pictures = 0;
    for (let t = 10_000; t < 16_000; t += 25) {
      now = t;
      if (t >= 12_000) {
        lag += t - store.bot('pawn')!.x / WALK;
        pictures++;
      }
      if (t % 100 !== 25) continue;
      now = t + 22;
      land(t - 25);
    }
    expect(lag / pictures).toBeGreaterThan(250);
    expect(lag / pictures).toBeLessThan(400);
  });

  it('starts over only for frames that come more than 120 ms faster than the one the clock is anchored on', () => {
    // Ten frames that each took 200 ms to get here, then one that took `latency`.
    const cadenceAfter = (latency: number) => {
      let now = 0;
      const store = new EntityStore(() => now);
      for (let sampled = 0; sampled <= 900; sampled += 100) {
        now = sampled + 200;
        store.apply(pawn(sampled + 1, sampled, [WALK * sampled, 64, 0]));
      }
      now = 1100 + latency;
      store.apply(pawn(1101, 1100, [WALK * 1100, 64, 0]));
      return store.intervalMs;
    };
    expect(cadenceAfter(90)).toBeLessThan(150); // ordinary jitter: the measured cadence carries on
    expect(cadenceAfter(70)).toBe(200); // the anchor was held up: a fresh start
  });

  describe('a stale anchor found in mid-stream', () => {
    // For three seconds every frame takes 200 ms to get here (a page that is still busy, say), then 60 ms: the clock
    // was anchored 140 ms late all along. Starting over at that point throws the history away, and a body that is
    // more than 2 blocks behind then jumps to its newest sample and stands there for a third of a second.
    function settle(
      speed: number,
      frameAt: (frame: number, sampled: number) => EntityDelta,
      read: (s: EntityStore) => number,
    ) {
      let now = 0,
        landed = 0,
        frame = 0,
        last = NaN;
      const store = new EntityStore(() => now);
      const paces: number[] = [];
      for (let t = 0; t <= 6000; t += 5) {
        now = t;
        if (t >= 2000 && t % 25 === 0) {
          const at = read(store);
          if (!Number.isNaN(last)) paces.push((at - last) / (25 * speed));
          last = at;
        }
        // One ordered socket: a frame cannot land before the one sent before it.
        const sampled = frame * 100;
        if (t < Math.max(sampled + (sampled < 3000 ? 200 : 60), landed + 5)) continue;
        store.apply(frameAt(++frame, sampled));
        landed = t;
      }
      return { store, paces, lag: 6000 - last / speed };
    }

    it('is corrected under the running playout: nothing jumps, nothing stops, and the delay it cost is worked off', () => {
      const seen = settle(
        FAST,
        (frame, sampled) => pawn(frame, sampled, [FAST * sampled, 64, 0]),
        (s) => s.bot('pawn')!.x,
      );
      expect(Math.min(...seen.paces)).toBeGreaterThan(0.9);
      expect(Math.max(...seen.paces)).toBeLessThan(1.3);
      expect(seen.lag).toBeGreaterThan(300); // 250 ms behind frames that take 60 ms; it was 450
      expect(seen.lag).toBeLessThan(320);
      expect(seen.store.intervalMs).toBeLessThan(110); // no fresh start: the measured cadence carried on
      expect(seen.store.pauses).toBe(0);
    });

    it('moves the clock under every body, also one that the frame does not mention', () => {
      // The app hands the entities over only with frames in which one of them changed; here a mob that walks is in
      // every other frame, and the frame that reveals the stale anchor is one without it.
      const mob = (sampled: number): EntityDto => ({
        kind: 'mob',
        dim: 0,
        x: SNEAK * sampled,
        y: 64,
        z: 0,
        id: 7,
        type: 54,
      });
      const seen = settle(
        SNEAK,
        (frame, sampled) => ({
          ...pawn(frame, sampled, [WALK * sampled, 64, 0]),
          rev: Math.ceil(frame / 2),
          full: frame % 2 === 1,
          entities: frame % 2 === 1 ? [mob(sampled)] : [],
        }),
        (s) => s.entities(0)[0].x,
      );
      expect(Math.min(...seen.paces)).toBeGreaterThan(0.9);
      expect(Math.max(...seen.paces)).toBeLessThan(1.3);
    });
  });

  it('after a second away, far bodies snap and near ones glide', () => {
    let now = 1000;
    const store = new EntityStore(() => now);
    const land = (sampled: number, far: number, near: number) =>
      store.apply(frameOf(sampled, sampled, [body('far', [far, 64, 0]), body('near', [near, 64, 0])]));
    land(1000, 0, 0);
    now = 1100;
    land(1100, 0, 0);
    now = 2600; // the tab was away for a second and a half
    land(2600, 3, 1);
    expect(store.bot('far')!.x).toBe(3); // it appears where it is, instead of dashing there through walls
    expect(store.bot('near')!.x).toBeCloseTo(0);
    now = 2775;
    expect(store.bot('near')!.x).toBeCloseTo(0.5); // the short glide of a walk that starts after a pause
    now = 2950;
    expect(store.bot('near')!.x).toBeCloseTo(1);
    expect(store.bot('far')!.x).toBe(3);
  });

  it('clean stream', () => {
    const seen = play({ speed: WALK, path: 'turns', jitter: 10 });
    expect(seen).toMatchObject({ freezes: 0, jumps: 0 });
    expect(seen.lag).toBeLessThan(310);
    expect(seen.store.pauses).toBe(0);
    expect(seen.store.playoutDelay).toBeCloseTo(250, 0);
  });
});

// A pawn's position changes once per game tick (50 ms) but the bot reads it once per frame (100 ms), at a time that
// wanders with its own work: 1 ms after the frame's tick, 11 ms on the two frames of three that first rebuild the entity
// log. With the tick's edge at 6 ms those reads land on either side of it, so the poses in consecutive frames are 50 or
// 150 ms apart while the frames' stamps are always 100 ms apart, and the pawn is drawn half and one and a half times as
// fast by turns. Each live body now says how old its pose was when it was read (`poseAge`), and its sample is dated by
// that instead.
describe('exact sample times (pose age)', () => {
  const TICK_EDGE = 6;
  function staircase(ages: boolean) {
    const tick = (t: number) => TICK_EDGE + 50 * Math.floor((t - TICK_EDGE) / 50);
    let now = 0;
    const store = new EntityStore(() => now);
    const frames: Array<{ at: number; arrives: number; x: number; age: number }> = [];
    for (let k = 1; k * 100 < 60_000; k++) {
      const read = 100 * k + 1 + (k % 3 === 0 ? 0 : 10);
      const age = Math.min(250, read - tick(read)) / 1000;
      frames.push({ at: 100 * k, arrives: read + 20, x: WALK * tick(read), age });
    }
    const paces: number[] = [];
    let next = 0,
      last = NaN;
    for (let t = 7; t < 60_000; t += 25) {
      now = t;
      const b = store.bot('pawn');
      if (b && t > 6000) {
        if (!Number.isNaN(last)) paces.push((b.x - last) / (WALK * 25));
        last = b.x;
      }
      let newest: (typeof frames)[number] | null = null;
      for (; next < frames.length && frames[next].arrives <= t; next++) newest = frames[next];
      if (!newest) continue;
      now = t + 22;
      const pose = { ...body('pawn', [newest.x, 64, 0]), live: true, poseAge: ages ? newest.age : 0 };
      store.apply(frameOf(next, newest.at, [pose]));
    }
    return Math.sqrt(paces.reduce((sum, p) => sum + (p - 1) ** 2, 0) / paces.length);
  }

  it('a pawn read on either side of its tick walks evenly once each sample carries its age', () => {
    expect(staircase(false)).toBeGreaterThan(0.25);
    expect(staircase(true)).toBeLessThan(0.2);
  });
});

// The bridge sends nothing while nothing changes, so a pawn that works alone (a walk, then it stands and digs) reaches
// the browser as bursts of frames with silence between them. To the playout a silence looks like a hole: its buffer runs
// dry, and the next frame shows the pawn somewhere else. But the pawn stood through it and only moved at its end.
// Replayed over the whole silence, as a hole is, every walk would be a crawl, drawn most of a second late, that ends as
// the next one begins.
describe('a quiet moment is not a hole', () => {
  /** A block's walk each second, then standing: the longest delay, and how far from its block the pawn is late in the second. */
  const steps = (speed: number) => {
    let off = 0,
      most = 0;
    const seen = play(
      {
        speed,
        path: 'steps',
        jitter: 20,
        each: (t, at, store) => {
          most = Math.max(most, store.playoutDelay);
          if (t > 6000 && t % 1000 >= 850) off = Math.max(off, Math.abs(at - (Math.floor(t / 1000) + 1)));
        },
      },
      60,
    );
    return { off, most, pauses: seen.store.pauses };
  };

  it('a first step after a quiet moment is a start, not a stretch to replay in slow motion', () => {
    // The pawn reaches its block 233 ms into the second; 0.6 s later it is drawn standing there.
    const seen = steps(WALK);
    expect(seen.off).toBeLessThan(0.02);
    expect(seen.most).toBeLessThanOrEqual(400);
    expect(seen.pauses).toBe(0);
  });

  it('a block hopped between two frames is replayed at half a walk at least', () => {
    // At three times the walk the whole step fits between two samples: every frame comes out of 0.9 s of silence, and
    // the history holds no step that shows how fast this pawn is.
    const seen = steps(3 * WALK);
    expect(seen.off).toBeLessThan(0.02);
    expect(seen.most).toBeLessThan(600);
  });

  it('a shuffle before the silence does not pass for the pace of the pawn', () => {
    let now = 20,
      frame = 0;
    const store = new EntityStore(() => now);
    const land = (sampled: number, x: number) => {
      now = sampled + 20;
      store.apply(pawn(++frame, sampled, [x, 64, 0]));
    };
    for (let sampled = 0; sampled <= 500; sampled += 100) land(sampled, 0);
    land(600, 0.01); // it shifted a hundredth of a block
    land(1200, 0.44); // 0.6 s of silence, then the first step of a walk
    expect(store.playoutDelay).toBeLessThan(400);
    expect(store.pauses).toBe(0);
  });

  it('counts a wait once: getting back what the slew took is no new pause', () => {
    // A block every 0.9 s, each from one frame. The first is replayed at half a walk and sets the pawn 0.47 s back;
    // every later one finds the delay 15 ms shorter (the slew) and only restores it.
    let now = 20,
      frame = 0;
    const store = new EntityStore(() => now);
    for (let hop = 0; hop <= 6; hop++) {
      now = hop * 900 + 20;
      store.apply(pawn(++frame, hop * 900, [hop, 64, 0]));
      if (hop > 0) expect(store.playoutDelay).toBeCloseTo(1 / (WALK / 2), 0);
    }
    expect(store.pauses).toBe(1);
  });
});
