import { describe, expect, it } from 'vitest';
import { MeshEngine } from '../src/mesh/engine';
import { PlayerCollisionCache } from '../src/render/playerCollision';
import { cellIndex } from '../src/wire/chunk';
import { encodeColumn, encodeMcwc, lightSections } from '../src/wire/encode';
import { combineHash } from '../src/wire/hash';

/** A b1.8.1 world of one column (0, 0), 128 high, holding `blocks` [x, y, z, id]. */
async function engineWith(blocks: Array<[number, number, number, number]>): Promise<MeshEngine> {
  const engine = new MeshEngine();
  engine.configure('b1.8.1', null, false);
  const ids = new Uint8Array(256 * 128),
    meta = new Uint8Array(256 * 128);
  for (const [x, y, z, id] of blocks) ids[cellIndex(x & 15, y, z & 15)] = id;
  const e = encodeColumn(
    ids,
    meta,
    lightSections(
      128,
      () => 15,
      () => 0,
    ),
  );
  await engine.store.applyPayload(
    encodeMcwc(0, [
      {
        cx: 0,
        cz: 0,
        hash: combineHash(e.blockHash, e.lightHash!),
        seenMs: 1,
        by: 't',
        live: true,
        data: e.data,
        light: e.light,
      },
    ]),
  );
  return engine;
}

const ids = (out: number[]) => out.filter((_, i) => i % 3 === 0);

describe('blocks outside the world height', () => {
  it('are air in a held column and missing where the column is not held', async () => {
    const engine = await engineWith([[1, 0, 1, 7]]);
    const out = engine.blocksAt(
      0,
      [1, 0, 1, 1, -1, 1, 1, -40, 1, 1, 127, 1, 1, 128, 1, 1, 300, 1, 20, 64, 20, 20, -1, 20],
    );
    expect(ids(out)).toEqual([7, 0, 0, 0, 0, 0, -1, -1]);
    // Out of the column's height: meta 0 and no light.
    expect(out.slice(3, 6)).toEqual([0, 0, -1]);
    expect(out.slice(15, 18)).toEqual([0, 0, -1]);
  });

  it('let a body standing on the bottom layer be predicted (y = 0)', async () => {
    const floor: Array<[number, number, number, number]> = [];
    for (let x = 0; x < 16; x++) for (let z = 0; z < 16; z++) floor.push([x, 0, z, 7]);
    const engine = await engineWith(floor);
    const cache = new PlayerCollisionCache(
      { blocksAt: (dim, pts) => Promise.resolve(engine.blocksAt(dim, pts)) },
      () => 'b1.8.1',
    );
    // Feet on the bedrock at y = 0, with the prediction's 0.6 step margin below them: the scan reaches y = -1.
    const body = { minX: 7.2, minY: 0.4, minZ: 7.2, maxX: 7.8, maxY: 2.8, maxZ: 7.8 };
    cache.query(body, 0, 0);
    for (let i = 0; i < 3; i++) await Promise.resolve();
    const r = cache.query(body, 0, 20);
    expect(r.loaded).toBe(true);
    expect(r.boxes).toContainEqual({ minX: 7, minY: 0, minZ: 7, maxX: 8, maxY: 1, maxZ: 8 });
    // Near the top of the world too.
    const high = { minX: 7.2, minY: 126, minZ: 7.2, maxX: 7.8, maxY: 127.8, maxZ: 7.8 };
    expect(cache.query(high, 0, 200).loaded).toBe(false);
    for (let i = 0; i < 3; i++) await Promise.resolve();
    expect(cache.query(high, 0, 220).loaded).toBe(true);
    cache.dispose();
  });
});
