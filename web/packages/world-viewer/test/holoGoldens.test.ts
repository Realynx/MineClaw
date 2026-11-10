// Behaviour pin for the hologram mesher (docs/dev/complexity.md, WP12): seeded build / dig / place requests (liquids,
// replaceable cells, cells already built or dug, cleared cells, beta doors, out-of-range heights) against a seeded world, with
// both versions' tables; every result's cells and mesh buffers are hashed in memory.

import { describe, expect, it } from 'vitest';
import { meshHolograms, type HoloMode, type HoloRequest } from '../src/mesh/holo';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';
import { noPacks, table1122, tableBeta } from './pack';

const r = rng(4711);
const WORLD_IDS = [0, 0, 1, 2, 8, 9, 31, 37, 78, 106, 64, 4, 10];
const WANT_IDS = [1, 4, 5, 8, 9, 10, 11, 20, 64, 71, 85, 53, 0, 50, 2];

describe.skipIf(noPacks)('hologram golden', () => {
  it('pins meshHolograms over seeded requests', async () => {
    const blocks: B[] = [];
    for (let i = 0; i < 1500; i++) blocks.push([Math.floor(r() * 40) - 8, 62 + Math.floor(r() * 10), Math.floor(r() * 40) - 8, WORLD_IDS[Math.floor(r() * WORLD_IDS.length)]]);
    const world = await storeWith(blocks);
    const d = new Digest();
    for (const table of [tableBeta(), table1122()]) {
      const reqs: HoloRequest[] = [];
      for (let q = 0; q < 24; q++) {
        const cells: number[] = [];
        const n = 1 + Math.floor(r() * 30);
        for (let i = 0; i < n; i++) {
          const y = q % 7 === 0 && i === 0 ? 300 : 62 + Math.floor(r() * 10);
          const id = WANT_IDS[Math.floor(r() * WANT_IDS.length)];
          cells.push(Math.floor(r() * 40) - 8, y, Math.floor(r() * 40) - 8, id, id === 64 || id === 71 ? (r() < 0.5 ? 8 : Math.floor(r() * 8)) : Math.floor(r() * 16));
          if (id === 64 && r() < 0.5) cells.push(cells[cells.length - 5], y + 1, cells[cells.length - 3], 64, 8);
        }
        const mode: HoloMode = (['build', 'dig', 'place'] as const)[q % 3];
        const clear = mode === 'place' && q % 2 === 0 ? cells.slice(0, 6) : undefined;
        reqs.push({ key: `r${q}`, blocks: cells, mode, clear });
      }
      for (const res of meshHolograms(world, 0, table, reqs)) {
        d.add(`${res.key} ${res.cells.join(',')} ${res.mesh ? `${res.mesh.origin} ${res.mesh.quads}` : 'none'}`);
        if (res.mesh) for (const a of [res.mesh.pos, res.mesh.uv, res.mesh.tile, res.mesh.color, res.mesh.cell, res.mesh.index]) d.bytes(a);
      }
    }
    expect(d.hex()).toBe('4b60e49f3b44c060e9c9d6152f278086');
  });
});
