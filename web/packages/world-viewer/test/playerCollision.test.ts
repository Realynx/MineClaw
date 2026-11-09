import { describe, expect, it } from 'vitest';
import { PlayerCollisionCache } from '../src/render/playerCollision';
import { playerBlockBoxes } from '../src/world/playerCollisionShapes';
import type { CollisionCell } from '../src/world/playerCollisionTypes';

const cell = (id = 0, meta = 0) => ({ id, meta });
const box = { minX: .2, minY: .9, minZ: .2, maxX: .8, maxY: 2.8, maxZ: .8 };
const scene = (cells: Record<string, CollisionCell>) => (x: number, y: number, z: number) => cells[`${x},${y},${z}`] ?? cell();
const shapes = (id: number, meta = 0, neighbors: Record<string, CollisionCell> = {}) =>
  playerBlockBoxes(scene({ '0,0,0': cell(id, meta), ...neighbors }), 0, 0, 0, '1.12.2');
const tick = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };

describe('player terrain collision', () => {
  it('uses collision data for solids, transparent blocks, plants and slabs rather than render opacity', () => {
    expect(shapes(1)).toEqual([{ minX: 0, minY: 0, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 }]);
    expect(shapes(20)).toEqual(shapes(1));
    expect(shapes(31)).toEqual([]);
    expect(shapes(8)).toEqual([]);
    expect(shapes(44)[0]).toMatchObject({ minY: 0, maxY: .5 });
    expect(shapes(44, 8)[0]).toMatchObject({ minY: .5, maxY: 1 });
  });
  it('retains stair risers and resolves corners and upside-down steps', () => {
    expect(shapes(53, 0)).toEqual([
      { minX: 0, minY: 0, minZ: 0, maxX: 1, maxY: .5, maxZ: 1 },
      { minX: .5, minY: .5, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 },
    ]);
    expect(shapes(53, 4)[0]).toMatchObject({ minY: .5, maxY: 1 });
    const outer = shapes(53, 0, { '1,0,0': cell(53, 3) });
    expect(outer).toHaveLength(2);
    expect(outer[1]).toMatchObject({ minX: .5, minZ: 0, maxX: 1, maxZ: .5 });
  });
  it('combines door halves and preserves an open door panel beside the passage', () => {
    const lower = shapes(64, 4, { '0,1,0': cell(64, 8) });
    expect(lower[0]).toMatchObject({ minZ: 0, maxZ: .1875 });
    const upper = playerBlockBoxes(scene({ '0,0,0': cell(64, 4), '0,1,0': cell(64, 8) }), 0, 1, 0, '1.12.2');
    expect(upper[0]).toMatchObject({ minY: 1, maxY: 2, minZ: 0, maxZ: .1875 });
  });
  it('keeps fence and wall collision taller than their visible top and extends paired chests', () => {
    expect(shapes(85).every(b => b.maxY === 1.5)).toBe(true);
    expect(shapes(85, 0, { '1,0,0': cell(85) })).toContainEqual({ minX: .625, minY: 0, minZ: .375, maxX: 1, maxY: 1.5, maxZ: .625 });
    expect(shapes(139)[0]?.maxY).toBe(1.5);
    expect(shapes(54, 0, { '1,0,0': cell(54) })[0]?.maxX).toBe(1);
  });
});

describe('bounded local collision cache', () => {
  it('rejects unsafe coordinates before starting worker queries or integer scans', () => {
    let calls = 0;
    const cache = new PlayerCollisionCache({ blocksAt: () => { calls++; return Promise.resolve([]); } }, () => '1.12.2');
    for (const x of [Infinity, NaN, 2 ** 53, -(2 ** 53)]) {
      expect(cache.query({ ...box, minX: x, maxX: x }, 0, 0).loaded).toBe(false);
    }
    expect(calls).toBe(0);
    cache.dispose();
  });
  it('finds piston rods extending into the query from an adjacent cell', async () => {
    const read = scene({ '0,0,0': cell(34, 5) });
    const cache = new PlayerCollisionCache({
      blocksAt: (_dim, pts) => Promise.resolve(pts.flatMap((_, i) => {
        if (i % 3 !== 0) return [];
        const block = read(pts[i], pts[i + 1], pts[i + 2]);
        return [block.id, block.meta, 15];
      }))
    }, () => '1.12.2');
    const rod = { minX: -.2, minY: .4, minZ: .4, maxX: -.05, maxY: .6, maxZ: .6 };
    cache.query(rod, 0, 0); await tick();
    expect(cache.query(rod, 0, 20)).toMatchObject({
      loaded: true, boxes: [
        { minX: -.25, minY: .375, minZ: .375, maxX: .75, maxY: .625, maxZ: .625 },
      ]
    });
    cache.dispose();
  });
  it('does not renew stale terrain after a worker failure', async () => {
    let fail = false;
    const cache = new PlayerCollisionCache({
      blocksAt: (_dim, pts) => {
        if (fail) return Promise.reject(new Error('worker unavailable'));
        return Promise.resolve(Array<number>(pts.length).fill(0));
      }
    }, () => '1.12.2');
    cache.query(box, 0, 0); await tick();
    expect(cache.query(box, 0, 20).loaded).toBe(true);
    fail = true;
    cache.query(box, 0, 200); await tick();
    expect(cache.query(box, 0, 600).loaded).toBe(false);
    cache.dispose();
  });
  it('never blocks a frame, shares one query, and stops trusting stale terrain', async () => {
    let calls = 0, finish: ((values: number[]) => void) | undefined, points: number[] = [];
    const cache = new PlayerCollisionCache({ blocksAt: (_dim, pts) => { calls++; points = pts; return new Promise(r => { finish = r; }); } }, () => '1.12.2');
    for (let n = 0; n < 100; n++)expect(cache.query(box, 0, 0).loaded).toBe(false);
    expect(calls).toBe(1); expect(points.length).toBe(441 * 3);
    finish!(points.flatMap((_, i) => i % 3 === 0 ? [1, 0, 15] : [])); await tick();
    expect(cache.query(box, 0, 20).loaded).toBe(true);
    expect(cache.query(box, 0, 20).boxes.length).toBeGreaterThan(0);
    expect(cache.query(box, 0, 600).loaded).toBe(false);
    expect(calls).toBe(2);
    cache.dispose();
    finish!(Array<number>(points.length).fill(0)); await tick();
    expect(cache.query(box, 0, 610).loaded).toBe(false);
  });
  it('discards a previous-dimension reply and treats unknown columns as unloaded', async () => {
    let finish: ((values: number[]) => void) | undefined, points: number[] = [];
    const cache = new PlayerCollisionCache({ blocksAt: (_dim, pts) => { points = pts; return new Promise(r => { finish = r; }); } }, () => '1.12.2');
    cache.query(box, 0, 0); cache.query(box, -1, 1);
    finish!(Array<number>(points.length).fill(0)); await tick();
    expect(cache.query(box, -1, 2).loaded).toBe(false);
    finish!(points.flatMap((_, i) => i % 3 === 0 ? [-1, 0, -1] : [])); await tick();
    expect(cache.query(box, -1, 3).loaded).toBe(false);
    cache.dispose();
  });
  it('reports local liquid and floor friction separately from obstacle boxes', async () => {
    const read = scene({ '0,0,0': cell(79), '0,1,0': cell(9) });
    const cache = new PlayerCollisionCache({
      blocksAt: (_dim, pts) => Promise.resolve(pts.flatMap((_, i) => i % 3 === 0 ?
        [read(pts[i], pts[i + 1], pts[i + 2]).id, 0, 15] : []))
    }, () => '1.12.2');
    const feet = { ...box, minY: 1 }; cache.query(feet, 0, 0); await tick();
    expect(cache.query(feet, 0, 20)).toMatchObject({ loaded: true, medium: 'water', slipperiness: .98 });
    cache.dispose();
  });
});
