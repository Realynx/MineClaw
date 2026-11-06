// Behaviour pin for the local relight (docs/dev/complexity.md, WP12): a seeded hilly world of 3×3 lit columns (light from
// a uniform payload), patched with seeded bursts of roofs, torches, glowstone, water, leaves and digging; every burst's
// dirty sections and every column's light afterwards are hashed in memory. Generated before relight was reshaped.

import { describe, expect, it } from 'vitest';
import { relight } from '../src/mesh/light';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';

const r = rng(77);
const PLACE = [0, 0, 0, 1, 1, 50, 89, 9, 18, 20, 79, 30, 4, 11, 76];

describe('relight golden', () => {
  it('pins light after seeded bursts of changes', async () => {
    const blocks: B[] = [];
    for (let x = -16; x < 32; x++)
      for (let z = -16; z < 32; z++) {
        const h = 60 + Math.floor(4 * Math.sin(x / 5) + 3 * Math.cos(z / 7));
        for (let y = 50; y <= h; y++) blocks.push([x, y, z, y === h ? 2 : 1]);
        if ((x * 7 + z * 3) % 23 === 0) for (let y = h + 1; y < h + 5; y++) blocks.push([x, y, z, 17]);
      }
    const store = await storeWith(blocks);
    const d = new Digest();
    const lightOf = () => {
      for (const c of store.cols.values()) {
        d.add(`${c.cx},${c.cz} ${c.light ? c.light.uniform.join(',') : 'none'}`);
        for (const f of c.light?.full ?? []) d.bytes(f);
      }
    };
    for (let burst = 0; burst < 25; burst++) {
      const changes: Array<[number, number, number]> = [];
      const n = 1 + Math.floor(r() * 12);
      const cx = Math.floor(r() * 40) - 12, cz = Math.floor(r() * 40) - 12;
      for (let i = 0; i < n; i++) {
        const x = cx + Math.floor(r() * 7) - 3, z = cz + Math.floor(r() * 7) - 3, y = 55 + Math.floor(r() * 20);
        const id = PLACE[Math.floor(r() * PLACE.length)];
        d.add(JSON.stringify(store.patch(0, [[x, y, z, id, 0]])));
        changes.push([x, y, z]);
      }
      d.add(JSON.stringify(relight(store, 0, changes)));
      lightOf();
    }
    d.add(JSON.stringify(relight(store, 0, [])));
    d.add(JSON.stringify(relight(store, 0, [[1000, 60, 1000]])));
    // 11-05: lava (11, in PLACE) takes all light now, as vanilla's does. Was 87842f946066f3f67eb94b09cf52fb8b.
    expect(d.hex()).toBe('9fd46a466d6df3ff5c067cae1ab13afc');
  });
});
