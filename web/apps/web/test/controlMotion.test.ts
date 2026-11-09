import { describe, expect, it } from 'vitest';
import { advanceControlBody, type ControlBody, type ControlBox, type ControlCollision } from '@/features/control/controlMotion';
import { idleInput, type PlayerInput } from '@/features/control/playerInput';

const ground: ControlBox = { minX: -100, maxX: 100, minY: 63, maxY: 64, minZ: -100, maxZ: 100 };
const world = (...boxes: ControlBox[]): ControlCollision => () => ({ loaded: true, boxes: [ground, ...boxes] });
const body = (): ControlBody => ({ x: 0, y: 64, z: 0, dim: 0, vx: 0, vy: 0, vz: 0, onGround: true, height: 1.8, jumpCooldown: 0 });
const walking = { ...idleInput(-90), forward: 1 };
function run(input: PlayerInput, terrain = world(), duration = 1000, step = 10) {
  const player = body();
  for (let now = 0; now < duration; now += step) advanceControlBody(player, input, step, terrain);
  return player;
}

describe('local control motion', () => {
  it('uses vanilla ground acceleration, normalized diagonal movement and display-independent integration', () => {
    const straight = run(walking), diagonal = run({ ...walking, strafe: 1 });
    expect(straight.x).toBeGreaterThan(4); expect(straight.x).toBeLessThan(4.2);
    expect(straight.y).toBe(64);
    expect(Math.hypot(diagonal.x, diagonal.z)).toBeCloseTo(straight.x, 5);
    expect(run(walking, world(), 1000, 5).x).toBeCloseTo(straight.x, 5);
    expect(run({ ...walking, sprint: true }).x).toBeCloseTo(straight.x * 1.3, 5);
    expect(run({ ...walking, sneak: true }).x).toBeCloseTo(straight.x * 0.3, 5);
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
});
