import { describe, expect, it } from 'vitest';
import { PlayerCollisionCache, scanCollision } from '../src/render/playerCollision';
import { playerBlockBoxes } from '../src/world/playerCollisionShapes';
import type { CollisionCell } from '../src/world/playerCollisionTypes';
import { BlockDeltaFeed } from '../src/world/deltas';
import type { MeshHost } from '../src/mesh/host';
import type { WorldCache } from '../src/world/cache';
import type { WorldSync } from '../src/world/sync';
import type { WorldSource } from '../src/types';

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
    // The 2 s refresh fails; the region held keeps answering until it is 6 s old, then it is not trusted.
    cache.query(box, 0, 2000); await tick();
    expect(cache.query(box, 0, 2200).loaded).toBe(true); await tick();
    expect(cache.query(box, 0, 6000).loaded).toBe(false);
    cache.dispose();
  });
  it('never blocks a frame, shares one query, and trusts the fetched region while the body stays inside it', async () => {
    let calls = 0, finish: ((values: number[]) => void) | undefined, points: number[] = [];
    const cache = new PlayerCollisionCache({ blocksAt: (_dim, pts) => { calls++; points = pts; return new Promise(r => { finish = r; }); } }, () => '1.12.2');
    const solid = () => points.flatMap((_, i) => i % 3 === 0 ? [1, 0, 15] : []);
    for (let n = 0; n < 100; n++)expect(cache.query(box, 0, 0).loaded).toBe(false);
    expect(calls).toBe(1); expect(points.length).toBe(13 * 17 * 13 * 3);
    finish!(solid()); await tick();
    expect(cache.query(box, 0, 20).loaded).toBe(true);
    expect(cache.query(box, 0, 20).boxes.length).toBeGreaterThan(0);
    // No age limit inside the region: later frames are still predicted and nothing is asked again.
    expect(cache.query(box, 0, 600).loaded).toBe(true);
    expect(calls).toBe(1);
    // Two cells from the edge a region around the body is asked for; the old one answers meanwhile.
    const near = { ...box, minX: 5.2, maxX: 5.8 };
    expect(cache.query(near, 0, 700).loaded).toBe(true);
    expect(calls).toBe(2);
    const beyond = { ...box, minX: 6.2, maxX: 6.8 };
    expect(cache.query(beyond, 0, 710).loaded).toBe(false);
    finish!(solid()); await tick();
    expect(cache.query(beyond, 0, 720).loaded).toBe(true);
    expect(calls).toBe(2);
    // Every 2 s the region is read again (changes the feed cannot report: reloaded columns, a reconnect).
    cache.query(beyond, 0, 2700);
    expect(calls).toBe(3);
    cache.dispose();
    finish!(Array<number>(points.length).fill(0)); await tick();
    expect(cache.query(beyond, 0, 2800).loaded).toBe(false);
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

describe('collision cells that do not wait for the worker', () => {
  // Feet on the stone at y = 0, with the prediction's 0.6 step margin below them.
  const feet = { minX: .2, minY: .4, minZ: .2, maxX: .8, maxY: 2.8, maxZ: .8 };
  /** Stone at y <= 0, air above. */
  const ground = (pts: number[]) => pts.flatMap((_, i) => i % 3 === 0 ? [pts[i + 1] <= 0 ? 1 : 0, 0, 15] : []);
  const floorUnder = (r: { boxes: readonly { minY: number; maxY: number }[] }) => r.boxes.some(b => b.minY === 0 && b.maxY === 1);
  function slowWorld() {
    const replies: Array<() => void> = [];
    const host = { calls: 0, blocksAt: (_dim: number, pts: number[]) => { host.calls++; return new Promise<number[]>(r => replies.push(() => r(ground(pts)))); } };
    return { host, answer: async () => { replies.shift()!(); await tick(); } };
  }

  it('keeps predicting inside the fetched region while the worker takes 800 ms to answer a refresh', async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    cache.query(feet, 0, 0); await answer();
    expect(cache.query(feet, 0, 20).loaded).toBe(true);
    cache.query(feet, 0, 2000);
    expect(host.calls).toBe(2);
    for (const t of [2100, 2400, 2799]) expect(cache.query(feet, 0, t)).toMatchObject({ loaded: true });
    expect(host.calls).toBe(2);
    await answer();
    expect(floorUnder(cache.query(feet, 0, 2800))).toBe(true);
    cache.dispose();
  });

  it('takes a block change as it arrives from the delta feed, without a worker reply', async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    const received: Array<[number, unknown]> = [];
    const feed = new BlockDeltaFeed({} as WorldSource, {} as MeshHost, {} as WorldCache, {} as WorldSync, {
      onDirty: () => undefined,
      onReceived: (dim, blocks) => { received.push([dim, blocks]); cache.patch(dim, blocks); },
    });
    cache.query(feet, 0, 0); await answer();
    expect(floorUnder(cache.query(feet, 0, 20))).toBe(true);
    feed.push({ rev: 5, full: false, dim: 0, blocks: [[0, 0, 0, 0, 0]] });
    expect(received).toEqual([[0, [[0, 0, 0, 0, 0]]]]);
    expect(feed.queued).toBe(1); // the frame's flush and the worker patch are still to come
    const r = cache.query(feet, 0, 30);
    expect(r.loaded).toBe(true);
    expect(floorUnder(r)).toBe(false);
    expect(host.calls).toBe(1);
    // Another dimension's change is not ours.
    feed.push({ rev: 6, full: false, blocks: { '-1': [[0, 0, 0, 1, 0]] } });
    expect(received[1]).toEqual([-1, [[0, 0, 0, 1, 0]]]);
    expect(floorUnder(cache.query(feet, 0, 40))).toBe(false);
    cache.dispose();
  });

  it('lays a change that arrived while a fetch was out over its reply', async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    cache.query(feet, 0, 0);
    cache.patch(0, [[0, 0, 0, 0, 0]]); // the worker's read still has the stone
    await answer();
    const r = cache.query(feet, 0, 20);
    expect(r.loaded).toBe(true);
    expect(floorUnder(r)).toBe(false);
    cache.dispose();
  });

  it('lays a change received before a fetch over its reply while the worker may not have it yet', async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    cache.query(feet, 0, 0); await answer();
    cache.query(feet, 0, 20);
    cache.override(0, 0, 0, 0, 0, 0, 1000, 20); // the pawn's own dig
    cache.patch(0, [[0, 0, 0, 0, 0]]); // the world's change: the override goes, the region takes it
    expect(floorUnder(cache.query(feet, 0, 30))).toBe(false);
    // The delta feed has not flushed it to the busy worker yet, so the refresh is answered from the worker's old copy.
    cache.query(feet, 0, 2000); await answer();
    expect(host.calls).toBe(2);
    expect(floorUnder(cache.query(feet, 0, 2010))).toBe(false);
    // Long after the change, the worker's word stands again.
    cache.query(feet, 0, 4010); await answer();
    expect(floorUnder(cache.query(feet, 0, 4020))).toBe(true);
    cache.dispose();
  });

  it("holds the takeover's own dig and placement until they expire or the world's change arrives", async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    cache.query(feet, 0, 0); await answer();
    cache.override(0, 0, 0, 0, 0, 0, 1000, 100);
    expect(floorUnder(cache.query(feet, 0, 200))).toBe(false);
    expect(floorUnder(cache.query(feet, 0, 1100))).toBe(true);
    cache.override(0, 0, 2, 0, 1, 0, 1000, 1200);
    expect(cache.query(feet, 0, 1300).boxes).toContainEqual({ minX: 0, minY: 2, minZ: 0, maxX: 1, maxY: 3, maxZ: 1 });
    cache.patch(0, [[0, 2, 0, 0, 0]]);
    expect(cache.query(feet, 0, 1400).boxes.some(b => b.minY === 2)).toBe(false);
    expect(cache.boxesAt(0, 0, 0, 0)).toEqual([{ minX: 0, minY: 0, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 }]);
    expect(cache.boxesAt(0, 40, 0, 0)).toBeNull();
    expect(cache.boxesAt(-1, 0, 0, 0)).toBeNull();
    cache.dispose();
  });

  it('names the block of a held cell, with the overrides laid over it, and nothing outside the region', async () => {
    const { host, answer } = slowWorld();
    const cache = new PlayerCollisionCache(host, () => '1.12.2');
    expect(cache.blockAt(0, 0, 0, 0)).toBeNull(); // nothing fetched yet
    cache.query(feet, 0, 0); await answer();
    expect(cache.blockAt(0, 0, 0, 0)).toEqual({ id: 1, meta: 0 });
    expect(cache.blockAt(0, 0, 1, 0)).toEqual({ id: 0, meta: 0 });
    cache.override(0, 0, 0, 0, 0, 0, 1000, 100);
    expect(cache.blockAt(0, 0, 0, 0)).toEqual({ id: 0, meta: 0 });
    expect(cache.blockAt(0, 40, 0, 0)).toBeNull();
    expect(cache.blockAt(-1, 0, 0, 0)).toBeNull();
    cache.dispose();
  });
});

describe("the medium and the climb, with the game client's boxes", () => {
  const body = (x: number, y: number, z: number, height = 1.8) => ({ minX: x - .3, minY: y, minZ: z - .3, maxX: x + .3, maxY: y + height, maxZ: z + .3 });
  const scan = (cells: Record<string, CollisionCell>, b: ReturnType<typeof body>, version: '1.12.2' | 'b1.8.1' = '1.12.2') =>
    scanCollision(scene(cells), b, version);

  it('wades rather than swims where water only reaches the outer 0.4 of the body', () => {
    // Standing in a block of water on the floor: the water reaches into the shrunk body, as in the C# client.
    expect(scan({ '0,0,0': cell(9) }, body(.5, 0, .5)).medium).toBe('water');
    // Rising out of it: only the bottom 0.4 is still in the water.
    expect(scan({ '0,0,0': cell(9) }, body(.5, .65, .5)).medium).toBeNull();
    // Water above the head's top 0.4 only does not count either.
    expect(scan({ '0,2,0': cell(8, 3) }, body(.5, .3, .5)).medium).toBeNull();
    // Lava a body only grazes sideways by less than 0.1.
    expect(scan({ '1,0,0': cell(11) }, body(.75, 0, .5)).medium).toBeNull();
    expect(scan({ '1,0,0': cell(11) }, body(.85, 0, .5)).medium).toBe('lava');
  });

  it('climbs in the ladder cell under the feet only, and by version', () => {
    expect(scan({ '0,0,0': cell(65, 4) }, body(.5, 0, .5))).toMatchObject({ climbable: true, medium: 'ladder' });
    // A ladder at head height, or in the next cell the box reaches into, is near but not climbed.
    expect(scan({ '0,1,0': cell(65, 4) }, body(.5, 0, .5))).toMatchObject({ climbable: false, medium: 'ladder' });
    expect(scan({ '1,0,0': cell(65, 4) }, body(.9, 0, .5))).toMatchObject({ climbable: false, medium: 'ladder' });
    expect(scan({ '0,0,0': cell(106, 1) }, body(.5, 0, .5)).climbable).toBe(true);
    expect(scan({ '0,0,0': cell(106, 1) }, body(.5, 0, .5), 'b1.8.1').climbable).toBe(false);
    // 1.12.2: an open trapdoor over a ladder facing the same way (west: trapdoor meta 2, ladder meta 4).
    const trap = (meta: number) => scan({ '0,1,0': cell(96, meta), '0,0,0': cell(65, 4) }, body(.5, 1, .5)).climbable;
    expect(trap(4 | 2)).toBe(true);
    expect(trap(2)).toBe(false); // closed
    expect(trap(4 | 3)).toBe(false); // facing east
  });

  it("uses Beta's thinner ladder box", () => {
    const ladder = (version: '1.12.2' | 'b1.8.1') => playerBlockBoxes(scene({ '0,0,0': cell(65, 4) }), 0, 0, 0, version);
    expect(ladder('b1.8.1')).toEqual([{ minX: .875, minY: 0, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 }]);
    expect(ladder('1.12.2')).toEqual([{ minX: .8125, minY: 0, minZ: 0, maxX: 1, maxY: 1, maxZ: 1 }]);
  });
});
