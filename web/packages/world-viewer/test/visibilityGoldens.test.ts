// Behaviour pin for cave culling (docs/dev/complexity.md, WP12): the face-connectivity masks of seeded sections (empty,
// solid, sparse, dense, tunnels, glass and leaves), and the visibility walk over seeded mask grids from seeded cameras,
// hashed in memory (the walk's output order included). Generated before the functions were reshaped.

import { describe, expect, it } from 'vitest';
import { ALL_VISIBLE, sectionVisibility, visibleSections, walkStart } from '../src/mesh/visibility';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';
import { noPacks, tableBeta } from './pack';

const r = rng(909);

describe.skipIf(noPacks)('visibility golden', () => {
  it('pins section masks and the walk', async () => {
    const d = new Digest();
    const blocks: B[] = [];
    // 4 × 4 sections at sy 0..3 of one column grid: each section a different fill.
    const fills = [0, 1, 0.02, 0.3, 0.6, 0.85, 0.97];
    for (let s = 0; s < 24; s++) {
      const cx = s % 6, sy = Math.floor(s / 6);
      const p = fills[s % fills.length];
      for (let x = 0; x < 16; x++)
        for (let z = 0; z < 16; z++)
          for (let y = 0; y < 16; y++) {
            const tunnel = s % 5 === 0 && ((y === 8 && z === 8) || (x === 3 && y === 2));
            if (tunnel || r() >= p) continue;
            blocks.push([cx * 16 + x, sy * 16 + y, z, r() < 0.1 ? (r() < 0.5 ? 20 : 18) : 1]);
          }
    }
    const store = await storeWith(blocks);
    for (let cx = -1; cx <= 6; cx++) for (let sy = -1; sy <= 9; sy++) d.add(`${cx},${sy} ${sectionVisibility(store, 0, cx, sy, 0, tableBeta())}`);
    // The walk over a seeded mask grid.
    const grid = new Map<string, number>();
    for (let cx = -12; cx <= 12; cx++) for (let cy = 0; cy < 8; cy++) for (let cz = -12; cz <= 12; cz++) grid.set(`${cx},${cy},${cz}`, r() < 0.3 ? ALL_VISIBLE : Math.floor(r() * 0x8000));
    const vis = (cx: number, cy: number, cz: number) => grid.get(`${cx},${cy},${cz}`) ?? ALL_VISIBLE;
    for (let i = 0; i < 40; i++) {
      const sx = Math.floor(r() * 9) - 4, sy = Math.floor(r() * 8), sz = Math.floor(r() * 9) - 4;
      const range = 2 + Math.floor(r() * 8), ymax = 4 + Math.floor(r() * 4);
      const v = i % 4 === 0 ? undefined : { x: r() * 2 - 1, y: r() * 2 - 1, z: r() * 2 - 1 };
      d.add([...visibleSections(sx, sy, sz, range, ymax, vis, v)].join(' '));
    }
    for (const [cam, dir] of [[{ x: 1, y: 60, z: 2 }, { x: 0, y: -1, z: 0 }], [{ x: 1, y: 900, z: 2 }, { x: 0.3, y: -0.9, z: 0.1 }], [{ x: 1, y: 900, z: 2 }, { x: 0, y: 0.2, z: 1 }]] as const)
      d.add(JSON.stringify(walkStart(cam, dir, 15, 3, 4)));
    expect(d.hex()).toBe('3c5abc231f7b47a695b7d6c263c6381e');
  });
});
