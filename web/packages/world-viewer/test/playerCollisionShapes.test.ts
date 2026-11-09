import { describe, expect, it } from 'vitest';
import { playerBlockBoxes } from '../src/world/playerCollisionShapes';
import type { CollisionCell } from '../src/world/playerCollisionTypes';

function world(self: number, cells: Array<[number, number, number, number, number]> = []) {
  const blocks = new Map<string, CollisionCell>([['0,0,0', { id: self, meta: 0 }]]);
  for (const [x, y, z, id, meta] of cells) blocks.set(`${x},${y},${z}`, { id, meta });
  return () => playerBlockBoxes((x, y, z) => blocks.get(`${x},${y},${z}`) ?? { id: 0, meta: 0 }, 0, 0, 0, '1.12.2');
}

describe('player collision attachment faces', () => {
  it('includes each chorus arm to a neighboring plant or flower', () => {
    const boxes = world(199, [
      [-1, 0, 0, 199, 0],
      [1, 0, 0, 200, 0],
      [0, 1, 0, 199, 0],
      [0, -1, 0, 200, 0],
      [0, 0, -1, 199, 0],
      [0, 0, 1, 200, 0],
    ])();
    expect(boxes).toEqual([
      { minX: 0.1875, minY: 0.1875, minZ: 0.1875, maxX: 0.8125, maxY: 0.8125, maxZ: 0.8125 },
      { minX: 0, minY: 0.1875, minZ: 0.1875, maxX: 0.1875, maxY: 0.8125, maxZ: 0.8125 },
      { minX: 0.8125, minY: 0.1875, minZ: 0.1875, maxX: 1, maxY: 0.8125, maxZ: 0.8125 },
      { minX: 0.1875, minY: 0.8125, minZ: 0.1875, maxX: 0.8125, maxY: 1, maxZ: 0.8125 },
      { minX: 0.1875, minY: 0, minZ: 0.1875, maxX: 0.8125, maxY: 0.1875, maxZ: 0.8125 },
      { minX: 0.1875, minY: 0.1875, minZ: 0, maxX: 0.8125, maxY: 0.8125, maxZ: 0.1875 },
      { minX: 0.1875, minY: 0.1875, minZ: 0.8125, maxX: 0.8125, maxY: 0.8125, maxZ: 1 },
    ]);
  });

  it('adds only a bottom chorus arm for end stone and ignores unrelated adjacent blocks', () => {
    expect(world(199)()).toHaveLength(1);
    const boxes = world(199, [
      [0, -1, 0, 121, 0],
      [0, 1, 0, 121, 0],
      [1, 0, 0, 121, 0],
      [-1, 0, 0, 1, 0],
    ])();
    expect(boxes).toHaveLength(2);
    expect(boxes[1]).toMatchObject({ minY: 0, maxY: 0.1875, minX: 0.1875, maxX: 0.8125 });
  });

  it.each([20, 95, 18, 161, 79, 89, 138, 166, 219])('fences and walls do not attach to excluded full cube %i', (id) => {
    expect(world(85, [[1, 0, 0, id, 0]])()).toHaveLength(1);
    expect(world(139, [[1, 0, 0, id, 0]])()[0].maxX).toBe(0.75);
  });

  it('panes connect to glass and other panes, but not leaves, ice or pumpkins', () => {
    for (const id of [20, 95, 101, 102, 160]) expect(world(102, [[1, 0, 0, id, 0]])()).toHaveLength(2);
    for (const id of [18, 79, 86, 91]) expect(world(102, [[1, 0, 0, id, 0]])()).toHaveLength(1);
  });

  it('wood fences share wood connections while nether fences retain their own material', () => {
    expect(world(85, [[1, 0, 0, 188, 0]])()).toHaveLength(2);
    expect(world(113, [[1, 0, 0, 113, 0]])()).toHaveLength(2);
    expect(world(85, [[1, 0, 0, 113, 0]])()).toHaveLength(1);
    expect(world(113, [[1, 0, 0, 85, 0]])()).toHaveLength(1);
  });

  it.each([85, 113, 139])('block %i attaches to fence gates only across their spanning axis', (id) => {
    const maxX = (gate: number, meta: number) =>
      Math.max(...world(id, [[1, 0, 0, gate, meta]])().map((box) => box.maxX));
    for (const gate of [107, 183, 184, 185, 186, 187]) {
      expect(maxX(gate, 0)).toBe(1); // A south-facing gate spans east/west.
      expect(maxX(gate, 2)).toBe(1);
      expect(maxX(gate, 4)).toBe(1); // Opening the gate does not remove its attachment poles.
      expect(maxX(gate, 1)).toBe(id === 139 ? 0.75 : 0.625);
      expect(maxX(gate, 3)).toBe(id === 139 ? 0.75 : 0.625);
    }
  });

  it.each([85, 102, 139])('block %i attaches to the solid back of stairs, including inner corners', (id) => {
    const maxX = (meta: number) => Math.max(...world(id, [[1, 0, 0, 53, meta]])().map((box) => box.maxX));
    expect(maxX(1)).toBe(1); // West-facing stair's full-height west face meets this block.
    expect(maxX(0)).toBeLessThan(1);
    const inner = [
      [0, 0, -1, 53, 0],
      [-1, 0, -1, 53, 2],
    ] as Array<[number, number, number, number, number]>;
    expect(Math.min(...world(id, inner)().map((box) => box.minZ))).toBe(0);
    const outer = [...inner, [1, 0, -1, 53, 2] as [number, number, number, number, number]];
    expect(Math.min(...world(id, outer)().map((box) => box.minZ))).toBeGreaterThan(0);
  });
});
