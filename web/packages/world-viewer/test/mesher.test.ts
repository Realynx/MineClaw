import { describe, expect, it } from 'vitest';
import { meshSection } from '../src/mesh/mesher';
import { buildStateTable } from '../src/mesh/states';
import { LF_CUT, LF_REMEMBERED, TEX_TEXTURED } from '../src/mesh/sink';
import { storeWith, type B } from './helpers';
import { table1122, tableBeta } from './pack';

const flat = buildStateTable('b1.8.1', null);
const Y = 64;
const sy = Y >> 4;

async function quads(blocks: B[], table = flat, opts?: Parameters<typeof storeWith>[1]) {
  const store = await storeWith(blocks, opts);
  const m = meshSection(store, 0, 0, sy, 0, table);
  return { solid: m.solid?.quads ?? 0, trans: m.translucent?.quads ?? 0, m, store };
}

/** Atlas cells (c + r·16) of every quad in a pass. */
function cells(m: { tile: Uint16Array } | null): number[] {
  if (!m) return [];
  const out: number[] = [];
  for (let v = 0; v < m.tile.length / 4; v += 4) out.push(m.tile[v * 4 + 1] * 16 + m.tile[v * 4]);
  return out;
}

describe('section meshing: face culling', () => {
  it('a lone cube has 6 faces', async () => {
    expect((await quads([[4, Y, 4, 1]])).solid).toBe(6);
  });

  it('two touching cubes share no face (10), a 3³ block shows only its shell (54)', async () => {
    expect((await quads([[4, Y, 4, 1], [5, Y, 4, 1]])).solid).toBe(10);
    const cube: B[] = [];
    for (let x = 0; x < 3; x++) for (let y = 0; y < 3; y++) for (let z = 0; z < 3; z++) cube.push([4 + x, Y + y, 4 + z, 1]);
    expect((await quads(cube)).solid).toBe(54);
  });

  it('culls across section and column borders', async () => {
    // y 63 / 64 straddle sections 3 and 4; x 15 / 16 straddle columns 0 and 1.
    const store = await storeWith([[15, Y, 3, 1], [16, Y, 3, 1], [15, Y - 1, 3, 1]]);
    const a = meshSection(store, 0, 0, sy, 0, flat);
    const below = meshSection(store, 0, 0, sy - 1, 0, flat);
    const b = meshSection(store, 0, 1, sy, 0, flat);
    expect(a.solid!.quads).toBe(4);
    expect(below.solid!.quads).toBe(5);
    expect(b.solid!.quads).toBe(5);
  });

  it('draws faces toward a column that is not loaded (C# reads unknown columns as air)', async () => {
    expect((await quads([[15, Y, 3, 1]])).solid).toBe(6);
  });

  it('glass culls against itself in the (alpha-tested) solid pass; ice and water are translucent', async () => {
    const g = await quads([[4, Y, 4, 20], [5, Y, 4, 20]]);
    expect(g.solid).toBe(10);
    expect(g.trans).toBe(0);
    expect((await quads([[4, Y, 4, 79], [5, Y, 4, 79]])).trans).toBe(10);
    // Two still-water cells: per cell 1 top (+ its underside) + 1 bottom + 3 sides = 6.
    const w = await quads([[4, Y, 4, 9], [5, Y, 4, 9]]);
    expect(w.trans).toBe(12);
  });

  it('a cube next to glass or water still shows the shared face', async () => {
    expect((await quads([[4, Y, 4, 1], [5, Y, 4, 20]])).solid).toBe(6 + 5); // the glass face against the stone is culled
  });

  it('crossed plants are two two-sided planes; a torch is four two-sided planes and a cap; empty sections mesh to nothing', async () => {
    expect((await quads([[4, Y, 4, 38]])).solid).toBe(4);
    expect((await quads([[4, Y, 4, 50, 5]])).solid).toBe(10);
    const e = await quads([[4, 10, 4, 1]]);
    expect(e.m.solid).toBeNull();
    expect(e.m.translucent).toBeNull();
  });

  it('a slab on stone hides its own bottom (not the stone top: a slab is not opaque); stairs are two boxes', async () => {
    const s = await quads([[4, Y, 4, 1], [4, Y + 1, 4, 44]]);
    expect(s.solid).toBe(6 + 5);
    expect((await quads([[4, Y, 4, 53, 0]])).solid).toBe(12);
  });

  it('b1.8.1 chests are opaque cubes whose pairs show the wide front and back halves (ChestTile)', async () => {
    expect((await quads([[4, Y, 4, 54]])).solid).toBe(6);
    expect((await quads([[4, Y, 4, 54], [5, Y, 4, 54]])).solid).toBe(10);
    const pair = await quads([[4, Y, 4, 54], [5, Y, 4, 54]], tableBeta());
    const used = new Set(cells(pair.m.solid));
    for (const t of [41, 42, 57, 58]) expect(used.has(t)).toBe(true);
    expect(used.has(27)).toBe(false); // no single-chest front
    expect(new Set(cells((await quads([[4, Y, 4, 54]], tableBeta())).m.solid)).has(27)).toBe(true);
  });

  it('fences connect to each other', async () => {
    const one = (await quads([[4, Y, 4, 85]])).solid;
    const two = (await quads([[4, Y, 4, 85], [5, Y, 4, 85]])).solid;
    expect(one).toBe(6);
    expect(two).toBeGreaterThan(2 * one);
  });

  it('1.12.2: top slabs and 256-high columns', async () => {
    const r = await quads([[4, 200, 4, 44, 8]], table1122(), { height: 256 });
    const m = meshSection(r.store, 0, 0, 200 >> 4, 0, table1122());
    expect(m.solid!.quads).toBe(6);
    const ys = [...m.solid!.pos].filter((_, i) => i % 3 === 1);
    expect(Math.min(...ys)).toBeCloseTo((200 & 15) + 0.5);
  });
});

describe('section meshing: light, AO and flags', () => {
  it('stores raw sky/block levels per vertex and darkens corners next to walls (AO)', async () => {
    const { m } = await quads([[4, Y, 4, 1], [5, Y + 1, 4, 1]]);
    const l = m.solid!.light;
    // Every vertex is sky 15 (17×15 = 255) with no block light.
    for (let v = 0; v < l.length / 4; v++) expect(l[v * 4]).toBe(255);
    const ao = new Set<number>();
    for (let v = 0; v < l.length / 4; v++) ao.add(l[v * 4 + 2]);
    expect(Math.max(...ao)).toBe(255);
    expect(Math.min(...ao)).toBeLessThan(255);
  });

  it('flags remembered columns and faces the roof cut exposes', async () => {
    const r = await quads([[4, Y, 4, 1]], flat, { live: false, light: false });
    expect(r.m.solid!.light[3] & LF_REMEMBERED).toBeTruthy();
    const pillar: B[] = [];
    for (let y = Y; y < Y + 8; y++) pillar.push([4, y, 4, 1]);
    const store = await storeWith(pillar);
    const cut = meshSection(store, 0, 0, sy, 0, flat, { y: Y + 4, x: 4.5, z: 4.5, r: 3 });
    const l = cut.solid!.light;
    let cutFaces = 0;
    for (let v = 0; v < l.length / 4; v += 4) if (l[v * 4 + 3] & LF_CUT) cutFaces++;
    expect(cutFaces).toBe(1); // the top of y = Y+3 under the removed cells
    expect(cut.solid!.quads).toBe(1 + 4 * 4 + 1); // the cut cap + 4 levels of 4 sides + the bottom
  });
});

describe('section meshing: bundled atlas', () => {
  it('b1.8.1 cube faces carry their terrain cell and the textured flag; untextured tables do not', async () => {
    const { m } = await quads([[4, Y, 4, 1]], tableBeta());
    const t = m.solid!.tile;
    expect([t[0], t[1], t[2] & TEX_TEXTURED]).toEqual([1, 0, TEX_TEXTURED]);
    expect((await quads([[4, Y, 4, 1]])).m.solid!.tile[2] & TEX_TEXTURED).toBe(0);
  });

  it('grass sides carry their tinted overlay as a second quad (both versions)', async () => {
    expect((await quads([[4, Y, 4, 2]], tableBeta())).solid).toBe(6 + 4);
    expect((await quads([[4, Y, 4, 2]], table1122(), { height: 256 })).solid).toBe(6 + 4);
  });

  it('lists the animated cells a section samples (water, lava)', async () => {
    const b = await quads([[4, Y, 4, 9], [6, Y, 4, 11]], tableBeta());
    expect(b.m.animCells?.sort((x, y) => x - y)).toEqual([205, 206, 237, 238]); // still tops, flowing sides
    const m = await quads([[4, Y, 4, 9]], table1122(), { height: 256 });
    expect(m.m.animCells?.length).toBe(2);
  });
});
