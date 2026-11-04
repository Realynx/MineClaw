// Behaviour pin for the worker's box queries (docs/dev/complexity.md, WP12): `scan` and `stacks` over a seeded world (plants,
// trees, snow, stacked cane, air gaps) with seeded boxes, id filters, skip lists, depths and hit caps, hashed in memory.

import { describe, expect, it } from 'vitest';
import { MeshEngine } from '../src/mesh/engine';
import type { V3 } from '../src/mesh/query';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';

const r = rng(2468);
const IDS = [1, 2, 3, 17, 18, 31, 37, 59, 60, 78, 83, 0];

describe('scan golden', () => {
  it('pins scan and stacks', async () => {
    const blocks: B[] = [];
    for (let x = -8; x < 24; x++)
      for (let z = -8; z < 24; z++) {
        const h = 60 + Math.floor(r() * 6);
        for (let y = 56; y <= h; y++) blocks.push([x, y, z, y === h ? IDS[Math.floor(r() * IDS.length)] : 1, Math.floor(r() * 4)]);
        if (r() < 0.2) for (let y = h + 1; y < h + 1 + Math.floor(r() * 4); y++) blocks.push([x, y, z, IDS[Math.floor(r() * IDS.length)]]);
      }
    const engine = new MeshEngine();
    Object.assign(engine, { store: await storeWith(blocks) });
    const d = new Digest();
    const box = (): [V3, V3] => [
      [Math.floor(r() * 40) - 12, (r() < 0.2 ? -5 : 54) + Math.floor(r() * 8), Math.floor(r() * 40) - 12],
      [Math.floor(r() * 40) - 12, 58 + Math.floor(r() * 12), Math.floor(r() * 40) - 12],
    ];
    for (let i = 0; i < 60; i++) {
      const [a, b] = box();
      const ids = i % 3 === 0 ? [] : [IDS[i % IDS.length], 59];
      d.add(engine.scan(0, a, b, ids, i % 5 === 0 ? 7 : undefined).join(','));
      const skip = i % 2 === 0 ? [18, 31, 78] : [];
      d.add(engine.stacks(0, a, b, skip, i % 4, i % 3 === 0 ? undefined : i % 3, i % 7 === 0 ? 5 : undefined).join(','));
    }
    expect(d.hex()).toBe('fdf9bfff1fae127c38ca160d9195dd76');
  });
});
