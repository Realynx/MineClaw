// Behaviour pin for the fluid geometry (docs/dev/complexity.md, WP12): flow vectors, flow angles and fluid quads over seeded
// neighbourhoods of water, lava, solids, ice and air at every level, for beta (tiling) and 1.12.2 (sprite, 2×2 flow sheet)
// textures, hashed in memory.

import { describe, expect, it } from 'vitest';
import { flowAngle, flowVector, fluidQuads, type FluidTex } from '../src/mesh/fluids';
import type { Nb } from '../src/mesh/models/states';
import type { Quad } from '../src/mesh/quad';
import { Digest, rng } from './digest';

const r = rng(8642);
const IDS = [0, 0, 8, 9, 9, 10, 11, 1, 20, 79, 2, 18];

function nbOf(centre: number): Nb {
  const cells = new Map<string, [number, number]>();
  const cell = (dx: number, dy: number, dz: number) => {
    const k = `${dx},${dy},${dz}`;
    let c = cells.get(k);
    if (!c) cells.set(k, (c = dx === 0 && dy === 0 && dz === 0 ? [centre, Math.floor(r() * 16)] : [IDS[Math.floor(r() * IDS.length)], Math.floor(r() * 16)]));
    return c;
  };
  return { id: (x, y, z) => cell(x, y, z)[0], meta: (x, y, z) => cell(x, y, z)[1], wx: 0, wy: 0, wz: 0, be: () => undefined };
}

const TEX: FluidTex[] = [
  { stillC: 1, stillR: 2, flowC: 3, flowR: 4, beta: true, tint: 0x123456, flat: 0x2f5cff, textured: true },
  { stillC: 5, stillR: 6, flowC: 7, flowR: 8, flowSpan: (2 << 8) | 2, beta: false, tint: 0x654321, flat: 0xf55a10, textured: false },
];

describe('fluid golden', () => {
  it('pins flow and fluid quads', () => {
    const d = new Digest();
    for (let i = 0; i < 3000; i++) {
      const id = [8, 9, 10, 11][i % 4];
      const nb = nbOf(id);
      d.add(`${flowVector(nb, id).join(',')} ${flowAngle(nb, id)}`);
      const out: Quad[] = [];
      fluidQuads(nb, id, TEX[i % 2], (x, y, z) => nb.id(x, y, z) === 1 || nb.id(x, y, z) === 2, out);
      for (const q of out) d.add(JSON.stringify(q));
    }
    expect(d.hex()).toBe('b026dd659a5424cd2cdcae61e6a8c743');
  });
});
