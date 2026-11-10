import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { scanCollision } from '@mineclaw/world-viewer/testing';
import { ControlCamera, type ControlSample } from '@/features/control/controlCamera';
import { advanceControlBody, type ControlBody, type ControlCollision } from '@/features/control/controlMotion';
import { idleInput, type PlayerInput } from '@/features/control/playerInput';
import { rng } from '@/mocks/util';

const position = (x: number, y = 64, dim = 0) => ({ x, y, z: 2, dim, onGround: true, food: 20 });
const floor: ControlCollision = () => ({
  loaded: true,
  boxes: [{ minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 }],
});
const unknown: ControlCollision = () => ({ loaded: false, boxes: [] });
/** The floor, and a 10 block ledge to walk off at x = 1. */
const ledge: ControlCollision = () => ({
  loaded: true,
  boxes: [
    { minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 },
    { minX: -3, maxX: 1, minY: 64, maxY: 74, minZ: -3, maxZ: 7 },
  ],
});
/** The floor, and a 20 block pillar to walk off at x = 1. */
const pillar: ControlCollision = () => ({
  loaded: true,
  boxes: [
    { minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 },
    { minX: -3, maxX: 1, minY: 64, maxY: 84, minZ: -3, maxZ: 7 },
  ],
});
const walking = { ...idleInput(-90), forward: 1 };
/** Walking speed on flat ground, in blocks per ms. */
const WALK = 4.317 / 1000;

interface Frame {
  at: number;
  x: number;
  y: number;
  z: number;
  free: { x: number; y: number; z: number };
  /** The visual offset after this frame. */
  offset: number;
}
interface Run {
  camera: ControlCamera;
  server: ControlBody;
  frames: Frame[];
  /** When each state frame reached the browser, and what it said. */
  arrivals: Array<{ at: number; state: ControlSample }>;
}
interface Script {
  until: number;
  /** Input changes: at this local time the keys become `input`. */
  inputs?: Array<[number, Partial<PlayerInput>]>;
  /** Fixed one-way delays and a seeded extra delay of 0 to `jitter` ms on each state frame. Frames stay in order. */
  up?: number;
  down?: number;
  jitter?: number;
  /** The server's ticks fall at `tickPhase + 50 * n`. */
  tickPhase?: number;
  frameMs?: number;
  start?: Partial<ControlBody>;
  /** The terrain of the game server, and the one the browser's prediction sees (the same unless given). */
  world?: ControlCollision;
  view?: ControlCollision;
  /** State frames made in [from, to) are held back until `to`, as a stalled connection delivers them. */
  hold?: [number, number];
  /** Changes or replaces the frame of one tick (null: send nothing). Runs after the tick moved the server's body. */
  publish?: (state: ControlSample, server: ControlBody, previous: ControlSample | null) => ControlSample | null;
  /** The game version both sides move by (1.12.2 when absent). */
  version?: 'b1.8.1' | '1.12.2';
}

/**
 * A browser and a game server in one loop, one millisecond at a time. The server moves its body once per 50 ms tick with
 * the newest input that has arrived, as the C# client does, and reports that tick's sample. The browser sends an input
 * on every change and every 50 ms, and draws a frame every `frameMs`. `free` is the browser's prediction with no
 * corrections at all, for comparison.
 */
function drive(script: Script): Run {
  const {
    until,
    up = 60,
    down = 60,
    jitter = 0,
    tickPhase = 0,
    frameMs = 10,
    world = floor,
    view = world,
    version,
  } = script;
  const start: ControlBody = {
    x: 0,
    y: 64,
    z: 2,
    dim: 0,
    vx: 0,
    vy: 0,
    vz: 0,
    onGround: true,
    height: 1.8,
    jumpCooldown: 0,
    ...script.start,
  };
  const random = rng(7),
    camera = new ControlCamera(),
    server = { ...start },
    free = { ...start };
  const changes = [...(script.inputs ?? [])];
  let input = idleInput(-90),
    seq = 0,
    tick = 100,
    freeAt = 0,
    lastDelivery = 0;
  let keys = input,
    keysSeq = -1,
    keysAt = 0;
  let previous: ControlSample | null = null;
  const uplink: Array<{ arrive: number; seq: number; input: PlayerInput }> = [];
  const downlink: Array<{ at: number; state: ControlSample }> = [];
  const frames: Frame[] = [],
    arrivals: Run['arrivals'] = [];
  const freeTo = (t: number) => {
    for (; freeAt < t;) {
      const ms = Math.min(10, t - freeAt);
      advanceControlBody(free, input, ms, world, { version });
      freeAt += ms;
    }
  };
  const send = (t: number) => {
    camera.sent(++seq, t);
    uplink.push({ arrive: t + up, seq, input: { ...input } });
  };
  camera.push({ ...start, stopped: true }, 0);
  camera.position(0, input, view, version);
  for (let t = 1; t <= until; t++) {
    while (changes[0] && changes[0][0] <= t) {
      freeTo(t);
      input = { ...input, ...changes.shift()![1] };
      camera.setInput(input, t);
      send(t);
    }
    if (t % 50 === 20) send(t);
    if (t >= tickPhase && (t - tickPhase) % 50 === 0) {
      while (uplink[0] && uplink[0].arrive <= t) {
        const next = uplink.shift()!;
        keys = next.input;
        keysSeq = next.seq;
        keysAt = next.arrive;
      }
      advanceControlBody(server, keys, 50, world, { version });
      const sample: ControlSample = {
        x: server.x,
        y: server.y,
        z: server.z,
        dim: server.dim,
        vx: server.vx,
        vy: server.vy,
        vz: server.vz,
        onGround: server.onGround,
        yaw: keys.yaw,
        pitch: keys.pitch,
        hp: 20,
        food: 20,
        heldSlot: 0,
        tick: ++tick,
        ackSeq: keysSeq,
        ackWaitMs: t - keysAt,
        stopped: false,
      };
      const state: ControlSample | null = script.publish ? script.publish(sample, server, previous) : sample;
      if (state) {
        previous = state;
        let at = Math.max(lastDelivery, t + down + Math.floor(random() * (jitter + 1)));
        if (script.hold && t >= script.hold[0] && t < script.hold[1]) at = Math.max(at, script.hold[1]);
        downlink.push({ at, state: { ...state } });
        lastDelivery = at;
      }
    }
    while (downlink[0] && downlink[0].at <= t) {
      const next = downlink.shift()!;
      arrivals.push(next);
      camera.push(next.state, next.at);
    }
    if (t % frameMs === 0) {
      freeTo(t);
      const p = camera.position(t, input, view, version)!;
      frames.push({
        at: t,
        x: p.x,
        y: p.y,
        z: p.z,
        free: { x: free.x, y: free.y, z: free.z },
        offset: camera.stats().offset,
      });
    }
  }
  return { camera, server, frames, arrivals };
}

/** A parity fixture of the C# client (tests/fixtures/control-parity): its world, and a `publish` that reports the C#
 * client's own ticks from the first tick the server walks, so the authority is the real one, not this prediction. */
function csharp(version: 'b1.8.1' | '1.12.2', name: string) {
  const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
  const file = path.join(
    here,
    '..',
    '..',
    '..',
    '..',
    'tests',
    'fixtures',
    'control-parity',
    `${version}-${name}.json`,
  );
  const f = JSON.parse(readFileSync(file, 'utf8')) as {
    cells: Array<[number, number, number, number, number]>;
    start: { x: number; y: number; z: number; vx: number; vy: number; vz: number };
    ticks: Array<{ x: number; y: number; z: number; vx: number; vy: number; vz: number; onGround: boolean }>;
  };
  const cells = new Map(f.cells.map(([x, y, z, id, meta]) => [`${x},${y},${z}`, { id, meta }]));
  const read = (x: number, y: number, z: number) => cells.get(`${x},${y},${z}`) ?? { id: 0, meta: 0 };
  const world: ControlCollision = (box) => scanCollision(read, box, version);
  let n = 0;
  const publish = (state: ControlSample, server: ControlBody) => {
    if (n >= f.ticks.length || (n === 0 && server.x <= f.start.x + 1e-9)) return state;
    const { x, y, z, vx, vy, vz, onGround } = f.ticks[n++]!;
    Object.assign(server, { x, y, z, vx, vy, vz, onGround });
    return { ...state, x, y, z, vx, vy, vz, onGround };
  };
  return { world, publish, start: { x: f.start.x, y: f.start.y, z: f.start.z, vy: f.start.vy }, version };
}

const steps = (frames: Frame[], axis: 'x' | 'y' | 'z' = 'x') =>
  frames.slice(1).map((f, i) => f[axis] - frames[i]![axis]);
const between = (frames: Frame[], from: number, to: number) => frames.filter((f) => f.at >= from && f.at < to);
const spread = (values: number[]) => {
  const mean = values.reduce((a, b) => a + b, 0) / values.length;
  return { mean, low: Math.min(...values) / mean, high: Math.max(...values) / mean };
};

describe('controlled pawn prediction', () => {
  it('moves on the first render after a key press without any server movement reply', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0);
    camera.position(0, idleInput(), floor);
    camera.setInput(walking, 0);
    expect(camera.position(16)?.x).toBeGreaterThan(0.015);
    expect(camera.position(32)?.x).toBeGreaterThan(0.04);
  });

  it('settles to a stopped body while inventory is open and resumes from that position without old lead', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0);
    camera.position(0, idleInput(), floor);
    camera.setInput(walking, 0);
    camera.position(150);
    camera.setInput(idleInput(-90), 150, true);
    for (let now = 200; now <= 1000; now += 50) {
      camera.push(position(0.25), now);
      camera.position(now + 10);
    }
    expect(camera.position(1010)!.x).toBeCloseTo(0.25, 2);
    camera.setInput(walking, 1010);
    expect(camera.position(1026)!.x).toBeGreaterThan(0.265);
  });

  it('only queues a state frame: the body moves in position and setInput, nowhere else', () => {
    const camera = new ControlCamera();
    camera.push(position(0), 0);
    camera.position(0, idleInput(), floor);
    camera.push(position(1), 50);
    expect(camera.stats().samples).toBe(1);
    expect(camera.position(50)!.x).toBe(0); // the correction lands in the offset, not on screen
    expect(camera.stats().samples).toBe(2);
    expect(camera.stats().offset).toBeCloseTo(1, 9);
  });

  // C2a: a frame from before the key press took effect is not a disagreement.
  it.each([60, 120, 200, 300])(
    'keeps the whole lead of a key press until the first moving frame at %i ms loop latency',
    (latency) => {
      for (const press of [1001, 1024, 1046]) {
        const run = drive({
          until: press + latency + 200,
          up: latency / 2,
          down: latency / 2,
          inputs: [[press, { forward: 1 }]],
        });
        const moved = run.arrivals.find((a) => a.state.x > 1e-9)!;
        const before = run.frames.filter((f) => f.at > press && f.at < moved.at && f.free.x > 0.001);
        expect(before.length).toBeGreaterThan(latency / 20);
        for (const f of before) expect(f.x / f.free.x).toBeGreaterThanOrEqual(0.95);
        expect(run.camera.stats().snaps).toBe(0);
      }
    },
  );

  // C2b and C1: after a stop the picture finishes the server's last tick of walking at most, in one direction.
  it('ends a stop within a quarter tick of walking, without sliding past and back', () => {
    for (const release of [3001, 3013, 3027, 3044]) {
      const run = drive({
        until: release + 1500,
        up: 75,
        down: 75,
        inputs: [
          [1000, { forward: 1 }],
          [release, { forward: 0 }],
        ],
      });
      const end = run.frames.at(-1)!;
      const settled = run.frames.find((f) => f.at > release && end.free.x - f.free.x < 0.001)!;
      const after = run.frames.filter((f) => f.at >= settled.at);
      expect(Math.abs(end.x - settled.x)).toBeLessThanOrEqual(WALK * 25 + 0.03);
      const moves = steps(after).filter((d) => Math.abs(d) > 1e-5);
      expect(moves.every((d) => Math.sign(d) === Math.sign(moves[0] ?? 0))).toBe(true);
      expect(Math.abs(end.x - run.server.x)).toBeLessThanOrEqual(0.0101); // what the deadband leaves
      expect(run.camera.stats().snaps).toBe(0);
    }
  });

  // C1: the old loop overshot a sideways error by 11 to 90 %.
  it('takes a sideways error of 0.3 blocks monotonically with under 2 % overshoot', () => {
    const run = drive({
      until: 3500,
      up: 50,
      down: 50,
      inputs: [[500, { forward: 1 }]],
      publish: (state, server) => {
        if (state.tick === 140) {
          server.z += 0.3;
          state.z = server.z;
        }
        return state;
      },
    });
    const after = run.frames.filter((f) => f.at >= 2000);
    expect(Math.min(...steps(after, 'z'))).toBeGreaterThanOrEqual(-1e-12);
    expect(Math.max(...after.map((f) => f.z))).toBeLessThanOrEqual(2.306);
    expect(after.at(-1)!.z).toBeCloseTo(2.3, 3);
  });

  // C3: a repeated tick is dropped and a double step is matched by its tick, so nothing surges.
  it('keeps per-frame steps within 10 % when frames repeat a tick and then carry two', () => {
    const run = drive({
      until: 4000,
      up: 60,
      down: 60,
      inputs: [[500, { forward: 1 }]],
      publish: (state, _server, previous) => (state.tick! % 7 === 0 && previous ? { ...previous } : state),
    });
    const s = spread(steps(between(run.frames, 2000, 4000)));
    expect(s.low).toBeGreaterThan(0.9);
    expect(s.high).toBeLessThan(1.1);
    expect(run.camera.stats().duplicates).toBeGreaterThan(8);
  });

  // C11: frames matched by their tick do not care when the handler ran.
  it('keeps per-frame steps within 10 % when frames arrive with 0 to 30 ms of seeded jitter', () => {
    const run = drive({ until: 4000, up: 60, down: 60, jitter: 30, inputs: [[500, { forward: 1 }]] });
    const s = spread(steps(between(run.frames, 2000, 4000)));
    expect(s.low).toBeGreaterThan(0.9);
    expect(s.high).toBeLessThan(1.1);
    expect(run.camera.stats().ticked).toBeGreaterThan(70);
  });

  // C4: no clamp freezes a fall and no reset throws away a sprint-jump.
  it('falls 20 blocks and sprint-jumps for 3 s at 150 ms without a snap or a frame without progress', () => {
    const fall = drive({
      until: 4000,
      up: 75,
      down: 75,
      world: pillar,
      start: { y: 84 },
      inputs: [
        [500, { forward: 1 }],
        [1200, { forward: 0 }],
      ],
    });
    const air = fall.frames.slice(fall.frames.findIndex((f) => f.y < 83.99) - 1);
    const landed = air.findIndex((f) => f.y < 64.001);
    expect(landed).toBeGreaterThan(50);
    expect(steps(air.slice(0, landed + 1), 'y').every((d) => d < 0)).toBe(true);
    expect(fall.frames.at(-1)!.y).toBeCloseTo(64, 6);
    expect(fall.camera.stats().snaps).toBe(0);
    const jumps = drive({ until: 4000, up: 75, down: 75, inputs: [[500, { forward: 1, sprint: true, jump: true }]] });
    expect(steps(between(jumps.frames, 600, 3500)).every((d) => d > 0)).toBe(true);
    expect(jumps.camera.stats().snaps).toBe(0);
  });

  // C4: a stalled stream eases the prediction to a stop instead of snapping back and then forward.
  it('holds smoothly through 600 ms without frames and catches up without a jump', () => {
    const run = drive({ until: 4000, up: 60, down: 60, inputs: [[500, { forward: 1 }]], hold: [2000, 2600] });
    const all = steps(between(run.frames, 1000, 4000));
    expect(Math.min(...all)).toBeGreaterThanOrEqual(-1e-12);
    expect(Math.max(...all)).toBeLessThan(0.15);
    // The held frames all arrive at 2600 and are used by the frame drawn then.
    const resume = run.frames.findIndex((f) => f.at >= 2600);
    const [a, b, c] = run.frames.slice(resume - 1, resume + 2);
    expect(b!.x - a!.x).toBeLessThanOrEqual(0.05);
    expect(c!.x - b!.x).toBeLessThanOrEqual(0.05);
    // Frozen, not walking on, by the end of the gap.
    const stall = between(run.frames, 2530, 2600);
    expect(stall.at(-1)!.x - stall[0]!.x).toBeLessThan(0.005);
    expect(run.camera.stats()).toMatchObject({ snaps: 0, holds: 1 });
    expect(run.camera.stats().offset).toBeLessThan(0.01);
    expect(Math.abs(run.frames.at(-1)!.x - run.server.x)).toBeLessThan(WALK * 150);
  });

  // Takes the place of the old stale-state snap and of its "clock reset" case.
  it('snaps in one frame with no offset left for 10 blocks, a new dimension and a server teleport', () => {
    const camera = new ControlCamera();
    camera.push({ ...position(0), teleports: 3 }, 0);
    camera.position(0, walking, floor);
    camera.position(500);
    camera.push({ ...position(0.9), teleports: 3 }, 500);
    camera.position(510);
    expect(camera.stats().offset).toBeGreaterThan(0.1);
    camera.push({ ...position(12), teleports: 3 }, 520);
    expect(camera.position(520)).toMatchObject({ x: 12, y: 64, z: 2, dim: 0 });
    expect(camera.stats()).toMatchObject({ snaps: 1, offset: 0 });
    camera.position(600);
    camera.push({ ...position(11, 70, -1), teleports: 3 }, 600);
    expect(camera.position(600)).toMatchObject({ x: 11, y: 70, dim: -1 });
    camera.position(700);
    camera.push({ ...position(12, 70, -1), teleports: 4, vx: 0.2, vy: 0, vz: 0 }, 700);
    expect(camera.position(700)).toMatchObject({ x: 12, y: 70, z: 2, dim: -1 });
    expect(camera.stats()).toMatchObject({ snaps: 3, offset: 0 });
    camera.clear();
    expect(camera.position(1000)).toBeNull();
  });

  // Takes the place of the old snap to the authority while terrain is not loaded.
  it('follows the authority smoothly where the terrain is not loaded', () => {
    const run = drive({ until: 4000, up: 50, down: 50, view: unknown, inputs: [[500, { forward: 1 }]] });
    const s = spread(steps(between(run.frames, 1500, 4000)));
    expect(s.high).toBeLessThanOrEqual(2);
    expect(s.low).toBeGreaterThan(0);
    expect(Math.abs(run.frames.at(-1)!.x - run.server.x)).toBeLessThan(1);
    expect(run.camera.stats().snaps).toBe(0);
    expect(run.camera.stats().terrainMisses).toBeGreaterThan(100);
  });

  // The rule that adopts velocity only on a snap, a stop or a push must not cancel a jump the server has not applied.
  it.each([5, 45])('draws a whole jump pressed %i ms before a tick, rising without a dip', (early) => {
    const run = drive({
      until: 2000,
      up: 75,
      down: 75,
      inputs: [
        [1000 - early, { jump: true }],
        [1200 - early, { jump: false }],
      ],
    });
    const ys = run.frames.filter((f) => f.at >= 900).map((f) => f.y);
    const peak = ys.indexOf(Math.max(...ys));
    expect(ys[peak]! - 64).toBeGreaterThanOrEqual(1.2);
    expect(steps(run.frames.filter((f) => f.at >= 900).slice(0, peak + 1), 'y').every((d) => d >= -1e-9)).toBe(true);
    expect(run.camera.stats().snaps).toBe(0);
  });

  it('follows a pawn the server moves without ticks (riding) and never snaps', () => {
    let ridden = 0;
    const run = drive({
      until: 3000,
      up: 60,
      down: 60,
      inputs: [[500, { forward: 1 }]],
      publish: () => {
        ridden += 0.4;
        return {
          x: ridden,
          y: 64,
          z: 2,
          dim: 0,
          yaw: 0,
          pitch: 0,
          hp: 20,
          food: 20,
          heldSlot: 0,
          onGround: true,
          stopped: true,
        };
      },
    });
    const late = between(run.frames, 1000, 3000);
    expect(Math.min(...steps(late))).toBeGreaterThanOrEqual(-1e-12);
    expect(Math.abs(late.at(-1)!.x - ridden)).toBeLessThan(2);
    expect(run.camera.stats().snaps).toBe(0);
  });

  it('adopts the server velocity when something pushed the pawn', () => {
    const camera = new ControlCamera();
    camera.push({ ...position(0), impulses: 0 }, 0);
    camera.position(0, idleInput(), floor);
    camera.position(200);
    camera.push({ ...position(0), impulses: 1, vx: 0.4, vy: 0, vz: 0, tick: 10 }, 200);
    camera.position(200);
    expect(camera.position(300)!.x).toBeGreaterThan(0.3);
  });

  // The acceptance threshold of the rebuild: a parity bug or a returning loop shows here first.
  it('keeps the offset under 0.15 blocks walking and turning on flat ground at 150 ms and 30 ms jitter', () => {
    const run = drive({
      until: 6000,
      up: 75,
      down: 75,
      jitter: 30,
      frameMs: 16,
      inputs: [
        [500, { forward: 1 }],
        [2000, { yaw: 0 }],
        [3000, { yaw: -45, strafe: 1 }],
        [4000, { yaw: -90, strafe: 0, sprint: true }],
      ],
    });
    expect(run.camera.stats().offsetP95).toBeLessThan(0.15);
    expect(run.camera.stats().snaps).toBe(0);
  });

  it('never jumps more than 0.15 blocks in a frame through a 10 block fall, stops, turns, jumps and sprint-jumps', () => {
    const run = drive({
      until: 10000,
      up: 75,
      down: 75,
      jitter: 30,
      frameMs: 16,
      world: ledge,
      start: { y: 74 },
      inputs: [
        [500, { forward: 1 }],
        [2500, { forward: 0 }],
        [3000, { yaw: 0, forward: 1 }],
        [4000, { yaw: -45 }],
        [5000, { jump: true }],
        [5100, { jump: false }],
        [6000, { yaw: -90, sprint: true, jump: true }],
        [8000, { forward: 0, sprint: false, jump: false }],
      ],
    });
    expect(run.camera.stats().snaps).toBe(0);
    expect(Math.min(...run.frames.map((f) => f.y))).toBeLessThan(64.5); // it did fall
    // A jump is a change of the per-frame step that the motion itself does not explain: a landing changes the step by
    // a third of a block on its own. So each frame's kick is compared with the largest kick of the uncorrected local
    // simulation within 64 ms of it (the two land and take off a frame or two apart).
    const kicks = (pick: (f: Frame) => { x: number; y: number; z: number }) =>
      run.frames.slice(2).map((f, i) => {
        const [a, b, c] = [pick(run.frames[i]!), pick(run.frames[i + 1]!), pick(f)];
        return { at: f.at, kick: Math.hypot(c.x - 2 * b.x + a.x, c.y - 2 * b.y + a.y, c.z - 2 * b.z + a.z) };
      });
    const free = kicks((f) => f.free);
    const excess = kicks((f) => f).map(
      ({ at, kick }) => kick - Math.max(...free.filter((k) => Math.abs(k.at - at) <= 64).map((k) => k.kick)),
    );
    expect(Math.max(...excess)).toBeLessThan(0.15);
    expect(Math.hypot(run.frames.at(-1)!.x - run.server.x, run.frames.at(-1)!.z - run.server.z)).toBeLessThan(0.0101);
  });

  // A3 with WP5: a walk off a ledge falls from the first tick on both sides, so no correction tugs the picture.
  it.each(['b1.8.1', '1.12.2'] as const)(
    'keeps the offset under 0.15 blocks walking off a 1 and a 4 block drop at 150 ms (%s)',
    (version) => {
      const step: ControlCollision = () => ({
        loaded: true,
        boxes: [
          { minX: -100, maxX: 100, minY: 62, maxY: 63, minZ: -100, maxZ: 100 },
          { minX: -3, maxX: 1, minY: 63, maxY: 64, minZ: -3, maxZ: 7 },
        ],
      });
      const small = drive({ until: 2500, up: 75, down: 75, world: step, version, inputs: [[500, { forward: 1 }]] });
      expect(small.frames.at(-1)!.y).toBeCloseTo(63, 6);
      const drop = csharp(version, 'ledge');
      const large = drive({
        until: 2800,
        up: 75,
        down: 75,
        world: drop.world,
        start: drop.start,
        publish: drop.publish,
        version,
        inputs: [[500, { forward: 1 }]],
      });
      expect(large.frames.at(-1)!.y).toBeCloseTo(60, 6);
      for (const run of [small, large]) {
        expect(Math.max(...run.frames.map((f) => f.offset))).toBeLessThan(0.15);
        expect(run.camera.stats().snaps).toBe(0);
      }
    },
  );
});
