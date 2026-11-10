// Behaviour pin for the worker's world queries (docs/dev/complexity.md, WP12): block picking and the x-ray occlusion tests
// over a seeded world (stone, leaves, glass, water, plants) with seeded rays, with and without a roof cut, hashed in memory.
// Generated before the voxel walk was shared.

import { describe, expect, it } from 'vitest';
import { blocked, occluded, pick, type V3 } from '../src/mesh/query';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';
import { noPacks, tableBeta } from './pack';

const r = rng(31337);
const IDS = [1, 1, 1, 18, 20, 9, 8, 31, 37, 50, 0];

describe.skipIf(noPacks)('query golden', () => {
  it('pins pick, blocked and occluded', async () => {
    const blocks: B[] = [];
    for (let i = 0; i < 6000; i++) blocks.push([Math.floor(r() * 48) - 16, 60 + Math.floor(r() * 20), Math.floor(r() * 48) - 16, IDS[Math.floor(r() * IDS.length)]]);
    const store = await storeWith(blocks);
    const t = tableBeta();
    const d = new Digest();
    const pt = (): V3 => [r() * 48 - 16, 58 + r() * 26, r() * 48 - 16];
    for (let i = 0; i < 400; i++) {
      const o = pt();
      const dir: V3 = i % 37 === 0 ? [0, 0, 0] : i % 23 === 0 ? [0, -1, 0] : i % 19 === 0 ? [1, 0, 0] : [r() * 2 - 1, r() * 2 - 1, r() * 2 - 1];
      const cut = i % 3 === 0 ? { x: o[0], y: 68, z: o[2], r: 6 } : null;
      d.add(JSON.stringify(pick(store, t, 0, o, dir, 10 + (i % 50), cut)));
      const p = pt();
      d.add(`${blocked(store, t, 0, p, o, cut)} ${blocked(store, t, 0, p, p)} ${blocked(store, t, 0, [p[0], 250, p[2]], [p[0] + 3, 400, p[2]])}`);
    }
    for (let i = 0; i < 50; i++) d.add(JSON.stringify(occluded(store, t, 0, pt(), [...pt(), ...pt(), ...pt(), 1])));
    expect(d.hex()).toBe('2204395c50bd23cc26a11a9837c889b2');
  });
});
