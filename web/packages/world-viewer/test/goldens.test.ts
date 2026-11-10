// Behaviour pins for the complexity refactor (docs/dev/complexity.md, WP12): digests of the block-state resolver, the
// b1.8.1 tile table and whole section meshes, generated from the code before it was reshaped. Every input is enumerated
// (id × meta × face × fancy; id × meta × seeded neighbourhoods) or a seeded random world, so any output change shows up
// as a digest mismatch. The digests hash in-memory values only (no files), so they don't depend on line endings.

import { describe, expect, it } from 'vitest';
import type { BlockEntityDto } from '../src/types';
import { meshSection, type SectionMesh } from '../src/mesh/mesher';
import { buildStateTable, type StateTable } from '../src/mesh/states';
import { cacheKey, potContents, resolveState, skullType, type Nb } from '../src/mesh/models/states';
import { tileFor } from '../src/mesh/beta/textures';
import { meshAssets } from '../src/atlas/assets';
import { storeWith, type B } from './helpers';
import { Digest, rng } from './digest';
import { noPacks, pack1122, packBeta, table1122, tableBeta } from './pack';

// Ids that the neighbour-dependent properties look for, drawn more often than chance.
const INTERESTING = [0, 0, 0, 1, 2, 3, 17, 18, 20, 26, 51, 53, 54, 55, 64, 67, 69, 75, 76, 77, 78, 80, 85, 86, 93, 94, 101, 102, 103, 104, 105, 106, 107, 108, 113, 130, 131, 134, 139, 143, 146, 147, 149, 151, 152, 160, 161, 175, 178, 183, 188, 193, 203, 218, 219];
const BES: Array<BlockEntityDto | undefined> = [
  undefined,
  { x: 0, y: 0, z: 0, kind: 'banner' },
  { x: 0, y: 0, z: 0, kind: 'bed', color: 'red' },
  { x: 0, y: 0, z: 0, kind: 'bed', color: 3 },
  { x: 0, y: 0, z: 0, kind: 'skull', type: 3, rotation: 7 },
  { x: 0, y: 0, z: 0, kind: 'skull', type: 'minecraft:wither_skeleton_skull', rotation: 21 },
  { x: 0, y: 0, z: 0, kind: 'flower_pot', item: [38, 4] as never },
  { x: 0, y: 0, z: 0, kind: 'flower_pot', item: 'minecraft:red_flower' },
  { x: 0, y: 0, z: 0, kind: 'flower_pot', item: { id: 6, meta: 5 } as never },
];

function randomNb(r: () => number): Nb {
  const cells = new Map<string, [number, number]>();
  const cell = (dx: number, dy: number, dz: number) => {
    const k = `${dx},${dy},${dz}`;
    let c = cells.get(k);
    if (!c) cells.set(k, (c = [r() < 0.6 ? INTERESTING[Math.floor(r() * INTERESTING.length)] : Math.floor(r() * 256), Math.floor(r() * 16)]));
    return c;
  };
  const be = BES[Math.floor(r() * BES.length)];
  const wx = Math.floor(r() * 2000) - 1000, wy = Math.floor(r() * 256), wz = Math.floor(r() * 2000) - 1000;
  return { id: (dx, dy, dz) => cell(dx, dy, dz)[0], meta: (dx, dy, dz) => cell(dx, dy, dz)[1], wx, wy, wz, be: () => be };
}

const solidFull = (id: number, meta: number) => (id * 31 + meta * 7) % 3 !== 0;

function stateDigest(): string {
  const d = new Digest();
  const r = rng(1122);
  for (let id = 0; id < 256; id++)
    for (let meta = 0; meta < 16; meta++) {
      const s = resolveState(id, meta, null, solidFull);
      d.add(JSON.stringify([s.file, s.key, s.builtin, s.props]) + cacheKey(s));
      for (let k = 0; k < 12; k++) {
        const w = resolveState(id, meta, randomNb(r), solidFull);
        d.add(JSON.stringify([w.file, w.key, w.builtin, w.props]) + cacheKey(w));
      }
    }
  return d.hex();
}

function tileDigest(): string {
  const d = new Digest();
  for (let id = 0; id < 256; id++)
    for (let meta = 0; meta < 16; meta++)
      for (let f = 0; f < 6; f++) {
        const a = tileFor(id, meta, f), b = tileFor(id, meta, f, false);
        d.add(`${a.tile},${a.tint},${a.overlay},${a.overlayTint};${b.tile},${b.tint},${b.overlay},${b.overlayTint}`);
      }
  return d.hex();
}

function meshDigest(d: Digest, m: SectionMesh): void {
  d.add(`${m.cx},${m.sy},${m.cz},${m.quads},${JSON.stringify(m.animCells ?? null)}`);
  for (const a of [m.solid, m.translucent]) {
    if (!a) {
      d.add('-');
      continue;
    }
    d.add(`${a.quads},${a.capIndex}`);
    for (const b of [a.pos, a.uv, a.tile, a.color, a.light, a.cell, a.index]) d.bytes(b);
  }
}

// b1.8.1 piston heads with facing 6/7 (never sent by a server) throw in BetaBuilder.build (N[meta & 7] is undefined), which
// would blank a whole section, so the worlds turn them into facing 4/5; the throw itself is pinned below.

/** A 2×2-column world: a grid holding every id × meta alone, over a seeded random jumble of blocks (neighbour states). */
function worldBlocks(seed: number, height: number): B[] {
  const r = rng(seed);
  const out: B[] = [];
  for (let id = 1; id < 256; id++)
    for (let meta = 0; meta < 16; meta++) {
      const i = (id - 1) * 16 + meta;
      // Every other cell on a 16×16 layer, layers 2 apart: each block stands alone.
      out.push([(i % 16) * 2, 4 + Math.floor(i / 256) * 2, Math.floor(i / 16) % 16 * 2, id, meta]);
    }
  for (let y = 40; y < Math.min(height - 1, 72); y++)
    for (let z = 0; z < 32; z++)
      for (let x = 0; x < 32; x++)
        if (r() < 0.55) out.push([x, y, z, r() < 0.6 ? INTERESTING[Math.floor(r() * INTERESTING.length)] : Math.floor(r() * 256), Math.floor(r() * 16)]);
  return out.map(([x, y, z, id, meta = 0]) => [x, y, z, id, id === 34 && (meta & 7) >= 6 ? meta - 2 : meta]);
}

async function worldDigest(tables: StateTable[], height: number): Promise<string> {
  const store = await storeWith(worldBlocks(7, height), { height });
  const r = rng(99);
  for (const [cx, cz] of [[0, 0], [0, 1], [1, 0], [1, 1]]) {
    const list: BlockEntityDto[] = [];
    for (let y = 40; y < 72; y++)
      for (let z = 0; z < 16; z++)
        for (let x = 0; x < 16; x++) {
          const be = BES[Math.floor(r() * BES.length)];
          if (be && r() < 0.3) list.push({ ...be, x: cx * 16 + x, y, z: cz * 16 + z });
        }
    store.setBlockEntities(0, cx, cz, list);
  }
  const d = new Digest();
  for (const table of tables)
    for (let sy = 0; sy < height >> 4; sy++)
      for (const [cx, cz] of [[0, 0], [0, 1], [1, 0], [1, 1]]) {
        meshDigest(d, meshSection(store, 0, cx, sy, cz, table));
        if (sy === 3) meshDigest(d, meshSection(store, 0, cx, sy, cz, table, { y: 60, x: 16, z: 16, r: 12 }));
      }
  return d.hex();
}

describe('refactor goldens (behaviour pins)', () => {
  it('resolveState over every id × meta, alone and in seeded neighbourhoods', () => {
    expect(stateDigest()).toBe('3f8564cd0743d5e18bd892616e327961');
  });

  it('skullType / potContents over their input shapes', () => {
    const skulls = [0, 1, 2, 3, 4, 5, 6, -1, null, undefined, {}, 'skeleton', 'minecraft:wither_skeleton_skull', 'Zombie_Head', 'char', 'steve', 'human', 'ender_dragon', 'enderdragon', 'creeper', '3', '9', 'pig'];
    const pots = [null, undefined, false, 0, 37, 38, 39, 40, 32, 31, 81, 6, 'minecraft:red_flower', 'poppy', 'daisy', 'cactus', 'nope', '38', '81', [38, 3], [38, 20], [6, 13], [31, 2], [31, 1], ['x', 'y'], { id: 'minecraft:orchid' }, { id: 38, meta: 8 }, { item: 6, damage: 2 }, { id: 31, damage: 2 }, {}];
    const d = new Digest();
    d.add(JSON.stringify([skulls.map(skullType), pots.map(potContents)]));
    expect(d.hex()).toBe('13664b1005f32c7aff164169fd5780d0');
  });

  it('tileFor over every id × meta × face × fancy', () => {
    expect(tileDigest()).toBe('60f5c13c7ded370b37c750e5e9b5ef80');
  });

  it.skipIf(noPacks)('b1.8.1 section meshes (flat, textured fancy and fast leaves)', async () => {
    const tables = [buildStateTable('b1.8.1', null), tableBeta(), buildStateTable('b1.8.1', meshAssets(packBeta()), true, true)];
    expect(await worldDigest(tables, 128)).toBe('c3cdf476b9d809420673773e3c2d4f02');
  });

  it.skipIf(noPacks)('b1.8.1 piston heads facing 6/7 still throw (a latent quirk kept as is)', async () => {
    const store = await storeWith([[4, 20, 4, 34, 6]]);
    expect(() => meshSection(store, 0, 0, 1, 0, tableBeta())).toThrow();
  });

  it.skipIf(noPacks)('1.12.2 section meshes (models, fancy and fast leaves)', async () => {
    const tables = [table1122(), buildStateTable('1.12.2', meshAssets(pack1122()), true, true)];
    expect(await worldDigest(tables, 256)).toBe('e1628e7eb3d5eb338a1e18bbe1941c74');
  });
});
