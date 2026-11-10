import { describe, expect, it } from 'vitest';
import { scanCollision } from '@mineclaw/world-viewer/testing';
import {
  advanceControlBody,
  type ControlBody,
  type ControlBox,
  type ControlCollision,
  type ControlRules,
} from '@/features/control/controlMotion';
import { idleInput, type PlayerInput } from '@/features/control/playerInput';

const ground: ControlBox = { minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 };
const world = (...boxes: ControlBox[]): ControlCollision => () => ({ loaded: true, boxes: [ground, ...boxes] });
const body = (): ControlBody => ({ x: 0, y: 64, z: 0, dim: 0, vx: 0, vy: 0, vz: 0, onGround: true, height: 1.8, jumpCooldown: 0 });
const walking = { ...idleInput(-90), forward: 1 };
function run(input: PlayerInput, terrain = world(), duration = 1000, step = 10, rules: ControlRules = {}) {
  const player = body();
  for (let now = 0; now < duration; now += step) advanceControlBody(player, input, step, terrain, rules);
  return player;
}
/** A world of block cells ("x,y,z" → [id, meta]; everything else air) as the takeover's collision cache reads it. */
function blocks(cells: Record<string, [number, number]>, version: 'b1.8.1' | '1.12.2' = '1.12.2'): ControlCollision {
  const read = (x: number, y: number, z: number) => {
    const c = cells[`${x},${y},${z}`];
    return c ? { id: c[0], meta: c[1] } : { id: 0, meta: 0 };
  };
  return (box) => scanCollision(read, box, version);
}
/** Stone at y = 63 for x and z in -4..4, a stone wall at x = 1 (y 64..71) and a ladder on its west face at x = 0, z = 0. */
function shaft(ladder = true, version: 'b1.8.1' | '1.12.2' = '1.12.2') {
  const cells: Record<string, [number, number]> = {};
  for (let x = -4; x <= 4; x++) for (let z = -4; z <= 4; z++) cells[`${x},63,${z}`] = [1, 0];
  for (let y = 64; y <= 71; y++) {
    for (let z = -4; z <= 4; z++) cells[`1,${y},${z}`] = [1, 0];
    if (ladder) cells[`0,${y},0`] = [65, 4];
  }
  return blocks(cells, version);
}
const at = (x: number, y = 64, z = 0.5): ControlBody => ({ ...body(), x, y, z });

describe('local control motion', () => {
  it('uses vanilla ground acceleration, scales the keys before normalising them and integrates display-independently', () => {
    const straight = run(walking), diagonal = run({ ...walking, strafe: 1 });
    expect(straight.x).toBeGreaterThan(4); expect(straight.x).toBeLessThan(4.2);
    expect(straight.y).toBe(64);
    // ×0.98 first, then normalised only while longer than 1: W+A is 1/0.98 faster than W.
    expect(Math.hypot(diagonal.x, diagonal.z)).toBeCloseTo(straight.x / 0.98, 4);
    // Sneaking (×0.3) stays shorter than 1, so a sneaking diagonal is √2 faster than sneaking straight.
    const sneak = run({ ...walking, sneak: true }), sneakDiagonal = run({ ...walking, sneak: true, strafe: 1 });
    expect(Math.hypot(sneakDiagonal.x, sneakDiagonal.z)).toBeCloseTo(sneak.x * Math.SQRT2, 4);
    expect(run(walking, world(), 1000, 5).x).toBeCloseTo(straight.x, 5);
    expect(run({ ...walking, sprint: true }, world(), 1000, 10, { version: 'b1.8.1' }).x).toBeCloseTo(straight.x * 1.3, 5);
    expect(sneak.x).toBeCloseTo(straight.x * 0.3, 5);
  });

  it('sweeps against walls and ceilings instead of clipping, and steps onto half slabs', () => {
    const wall = { minX: 1, maxX: 2, minY: 64, maxY: 68, minZ: -2, maxZ: 2 };
    expect(run(walking, world(wall)).x).toBeCloseTo(0.7, 5);
    const slab = { ...wall, maxY: 64.5, maxX: 4 };
    const stepped = run(walking, world(slab), 500);
    expect(stepped.x).toBeGreaterThan(1); expect(stepped.y).toBeCloseTo(64.5);
    const roof = { minX: -10, maxX: 10, minY: 66, maxY: 67, minZ: -10, maxZ: 10 };
    expect(run({ ...idleInput(), jump: true }, world(roof), 100).y).toBeLessThanOrEqual(64.2 + 1e-6);
  });

  it('jumps immediately and falls back to the ground with vanilla jump height', () => {
    const player = body(), terrain = world();
    let peak = player.y;
    for (let now = 0; now < 1000; now += 10) {
      advanceControlBody(player, { ...idleInput(), jump: now < 20 }, 10, terrain);
      peak = Math.max(peak, player.y);
    }
    expect(peak - 64).toBeGreaterThan(1.2); expect(peak - 64).toBeLessThan(1.3);
    expect(player.y).toBeCloseTo(64); expect(player.onGround).toBe(true);
  });

  it('sneaks to the edge but does not walk off it, and never predicts into unloaded terrain', () => {
    const ledge: ControlCollision = () => ({ loaded: true, boxes: [{ ...ground, maxX: 1 }] });
    const sneaking = run({ ...walking, sneak: true }, ledge, 2000);
    expect(sneaking.x).toBeLessThan(1.3); expect(sneaking.y).toBe(64);
    expect(run(walking, () => ({ loaded: false, boxes: [] })).x).toBe(0);
  });

  // C12: at rest the C# body keeps -0.0784 of vertical speed, so its first tick past an edge already falls that far.
  it('falls from the first tick after walking off an edge', () => {
    const ledge = world({ minX: -100, maxX: 1, minY: 63, maxY: 64, minZ: -100, maxZ: 100 });
    for (const step of [50, 10]) {
      const player = at(1.31); // the box has just left the edge; the floor below is far away
      for (let t = 0; t < 250; t += step) advanceControlBody(player, idleInput(-90), step, () => ({ loaded: true, boxes: [] }));
      expect(64 - player.y).toBeCloseTo(0.0784 + 0.1552 + 0.2305 + 0.3043 + 0.3766, 2);
      expect(player.vy).toBeCloseTo(-0.447, 2);
    }
    // Standing still keeps the feet on the floor and the vertical speed at rest.
    const standing = run(idleInput(), ledge, 500);
    expect(standing.y).toBe(64); expect(standing.onGround).toBe(true);
  });

  // C6 and C13: a ladder climbs only in its own cell and only when pushed into, at the client's speed.
  it('climbs a ladder it pushes into at 2.35 blocks a second, and nothing else lifts it', () => {
    for (const step of [10, 50]) {
      const player = at(0.5);
      const ys: number[] = [];
      for (let t = 0; t < 2000; t += step) {
        advanceControlBody(player, walking, step, shaft());
        if ((t + step) % 1000 === 0) ys.push(player.y);
      }
      expect(ys[1]! - ys[0]!).toBeCloseTo(2.352, 2);
    }
    // Walking past the ladder along the wall touches its cell but never its block.
    const past = at(0.4, 64, -2);
    for (let t = 0; t < 1000; t += 10) advanceControlBody(past, { ...idleInput(0), forward: 1 }, 10, shaft());
    expect(past.z).toBeGreaterThan(1.5); expect(past.y).toBe(64);
    // Space alone is a jump, not a climb.
    const jumping = at(0.5);
    let top = 64;
    for (let t = 0; t < 2000; t += 10) {
      advanceControlBody(jumping, { ...idleInput(-90), jump: true }, 10, shaft());
      top = Math.max(top, jumping.y);
    }
    expect(top).toBeLessThan(65.3);
  });

  it('holds on while sneaking, slides down a ladder at 0.15 blocks a tick and leaves the top without a plunge', () => {
    const climber = at(0.5);
    for (let t = 0; t < 1000; t += 10) advanceControlBody(climber, walking, 10, shaft());
    const y = climber.y;
    expect(y).toBeGreaterThan(66);
    // C13: the ladder is gone (the query no longer has its cells): the next tick must not fall at free-fall speed.
    const next = { ...climber };
    advanceControlBody(next, walking, 50, shaft(false));
    expect(y - next.y).toBeLessThan(0.1);
    // The climb's own speed carries it a little higher first, as in the game.
    const holding = { ...climber };
    for (let t = 0; t < 300; t += 10) advanceControlBody(holding, { ...idleInput(-90), sneak: true }, 10, shaft());
    const held = holding.y;
    for (let t = 0; t < 1000; t += 10) advanceControlBody(holding, { ...idleInput(-90), sneak: true }, 10, shaft());
    expect(held - y).toBeLessThan(0.2);
    expect(holding.y).toBeCloseTo(held, 6);
    const sliding = at(0.5, 69);
    for (let t = 0; t < 500; t += 50) advanceControlBody(sliding, idleInput(-90), 50, shaft());
    const from = sliding.y;
    for (let t = 0; t < 500; t += 50) advanceControlBody(sliding, idleInput(-90), 50, shaft());
    expect(from - sliding.y).toBeCloseTo(1.5, 6);
  });

  // Sprint stops on a horizontal collision; b1.8.1 ignores hunger, and 1.12.2 moves a tick with the old sprint state.
  it('keeps the sprint rules of each version', () => {
    const wall = world({ minX: 1, maxX: 2, minY: 64, maxY: 70, minZ: -100, maxZ: 100 });
    const along = { ...idleInput(-45), forward: 1 }; // toward +x and +z: the wall stops x, z slides
    const slide = (input: PlayerInput, version: string) => {
      const player = at(0.7, 64, 0);
      for (let t = 0; t < 500; t += 10) advanceControlBody(player, input, 10, wall, { version });
      const z = player.z;
      for (let t = 0; t < 500; t += 10) advanceControlBody(player, input, 10, wall, { version });
      expect(player.x).toBeCloseTo(0.7, 6);
      return player.z - z;
    };
    for (const version of ['b1.8.1', '1.12.2'])
      expect(slide({ ...along, sprint: true }, version)).toBeCloseTo(slide(along, version), 4);
    const hungry = (version: string) => run({ ...walking, sprint: true }, world(), 1000, 10, { version, food: 2 }).x;
    const walked = run(walking).x;
    expect(hungry('b1.8.1')).toBeCloseTo(walked * 1.3, 5);
    expect(hungry('1.12.2')).toBeCloseTo(walked, 5);
    const first = (version: string) => {
      const player = body();
      advanceControlBody(player, { ...walking, sprint: true }, 50, world(), { version });
      return player.x;
    };
    expect(first('1.12.2')).toBeCloseTo(0.098, 4);
    expect(first('b1.8.1')).toBeCloseTo(0.1274, 4);
  });

  it('steps 0.5 blocks without shrinking or waiting between jumps on b1.8.1, and 0.6 on 1.12.2', () => {
    const ledge = world({ minX: 1, maxX: 4, minY: 64, maxY: 64.55, minZ: -100, maxZ: 100 });
    expect(run(walking, ledge, 1000, 10, { version: '1.12.2' }).y).toBeCloseTo(64.55, 6);
    expect(run(walking, ledge, 1000, 10, { version: 'b1.8.1' }).y).toBe(64);
    expect(run({ ...idleInput(), sneak: true }, world(), 100, 10, { version: '1.12.2' }).height).toBe(1.65);
    expect(run({ ...idleInput(), sneak: true }, world(), 100, 10, { version: 'b1.8.1' }).height).toBe(1.8);
    // Under a low ceiling a held jump lands quickly: b1.8.1 jumps again at once, 1.12.2 after its 10 ticks.
    const low = world({ minX: -100, maxX: 100, minY: 66, maxY: 67, minZ: -100, maxZ: 100 });
    const jumps = (version: string) => {
      const player = body();
      let count = 0;
      for (let t = 0; t < 2000; t += 50) {
        const grounded = player.onGround;
        advanceControlBody(player, { ...idleInput(), jump: true }, 50, low, { version });
        if (grounded && player.y > 64) count++;
      }
      return count;
    };
    expect(jumps('b1.8.1')).toBeGreaterThan(jumps('1.12.2') + 3);
  });

  it("moves by the pawn's effects and item use on 1.12.2 only", () => {
    const walked = run(walking).x;
    const effects = (speed: number, slowness: number, jumpBoost = -1) => ({ speed, slowness, jumpBoost });
    expect(run(walking, world(), 1000, 10, { effects: effects(1, -1) }).x).toBeCloseTo(walked * 1.4, 4);
    expect(run(walking, world(), 1000, 10, { effects: effects(-1, 0) }).x).toBeCloseTo(walked * 0.85, 4);
    expect(run(walking, world(), 1000, 10, { version: 'b1.8.1', effects: effects(1, -1) }).x).toBeCloseTo(walked, 5);
    expect(run(walking, world(), 1000, 10, { usingItem: true }).x).toBeCloseTo(walked * 0.2, 4);
    expect(run({ ...walking, sprint: true }, world(), 1000, 10, { usingItem: true }).x).toBeCloseTo(walked * 0.2, 4);
    const peak = (rules: ControlRules) => {
      const player = body();
      let top = 64;
      for (let t = 0; t < 1000; t += 50) {
        advanceControlBody(player, { ...idleInput(), jump: t === 0 }, 50, world(), rules);
        top = Math.max(top, player.y);
      }
      return top - 64;
    };
    expect(peak({})).toBeLessThan(1.3);
    expect(peak({ effects: effects(-1, -1, 0) })).toBeGreaterThan(1.7);
  });
});
