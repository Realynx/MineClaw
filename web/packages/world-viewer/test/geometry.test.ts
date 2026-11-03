import { describe, expect, it } from 'vitest';
import { BetaSource } from '../src/mesh/source';
import { cornerHeight, flowAngle, flowVector, fluidCorners, percentAir } from '../src/mesh/fluids';
import type { Nb } from '../src/mesh/models/states';
import type { Quad } from '../src/mesh/quad';
import { tableBeta } from './pack';

type W = Array<[number, number, number, number, number?]>;
function nbAt(blocks: W, x = 0, y = 0, z = 0): Nb {
  const m = new Map(blocks.map(([bx, by, bz, id, meta]) => [`${bx},${by},${bz}`, [id, meta ?? 0]]));
  return { id: (dx, dy, dz) => m.get(`${x + dx},${y + dy},${z + dz}`)?.[0] ?? 0, meta: (dx, dy, dz) => m.get(`${x + dx},${y + dy},${z + dz}`)?.[1] ?? 0, wx: x, wy: y, wz: z };
}

describe('fluids', () => {
  it('corner heights: a source among sources sits at 8/9; a lone source slopes to its air corners', () => {
    const sea: W = [];
    for (let x = -1; x <= 1; x++) for (let z = -1; z <= 1; z++) sea.push([x, 0, z, 9, 0]);
    expect(fluidCorners(nbAt(sea), 9).every((h) => Math.abs(h - 8 / 9) < 1e-6)).toBe(true);
    // One source (weight 11 · 1/9) and three air cells (1 each): 1 − (11/9 + 3) / 14.
    const lone = cornerHeight(nbAt([[0, 0, 0, 9, 0]]), 0, 0, 9);
    expect(lone).toBeCloseTo(1 - (11 / 9 + 3) / 14);
    // The same fluid above any of the four cells makes the corner full.
    expect(cornerHeight(nbAt([[0, 0, 0, 9, 0], [0, 1, 0, 9, 0]]), 1, 1, 9)).toBe(1);
    // Solid neighbours don't count; a flowing level 3 cell weighs 1.
    expect(percentAir(3)).toBeCloseTo(4 / 9);
    expect(cornerHeight(nbAt([[0, 0, 0, 9, 3], [-1, 0, 0, 1], [0, 0, -1, 1], [-1, 0, -1, 1]]), 0, 0, 9)).toBeCloseTo(1 - 4 / 9);
  });

  it('flows toward lower neighbours; still water has no flow angle', () => {
    const run: W = [[0, 0, 0, 8, 1], [-1, 0, 0, 9, 0], [1, 0, 0, 8, 2], [0, 0, -1, 1], [0, 0, 1, 1]];
    const [fx, , fz] = flowVector(nbAt(run), 8);
    expect(fx).toBeGreaterThan(0.9);
    expect(Math.abs(fz)).toBeLessThan(1e-9);
    expect(flowAngle(nbAt(run), 8)).toBeCloseTo(-Math.PI / 2);
    const still: W = [];
    for (let x = -1; x <= 1; x++) for (let z = -1; z <= 1; z++) still.push([x, 0, z, 9, 0]);
    expect(flowAngle(nbAt(still), 9)).toBe(-1000);
  });

  it('textures: the still tile on flat tops, the flowing tile (rotated) on slopes and sides', () => {
    const src = tableBeta().source as BetaSource;
    const out: Quad[] = [];
    const sea: W = [];
    for (let x = -1; x <= 1; x++) for (let z = -1; z <= 1; z++) sea.push([x, 0, z, 9, 0]);
    src.build(9, 0, nbAt(sea), out);
    expect(out.length).toBe(2); // one (two-sided) top + the bottom over air; sides face the same water
    const top = out.find((q) => q.dir === 1)!;
    expect(top.r * 16 + top.c).toBe(205);
    expect(top.twoSided).toBe(true);
    const flowing: Quad[] = [];
    src.build(8, 1, nbAt([[0, 0, 0, 8, 1], [-1, 0, 0, 9, 0], [1, 0, 0, 8, 2]]), flowing);
    const ftop = flowing.find((q) => q.dir === 1)!;
    expect(ftop.r * 16 + ftop.c).toBe(206);
    expect(ftop.wrap).toBe(true);
    // The window is turned: its UVs are not the axis-aligned 0..1 square.
    expect(ftop.uv.some((v) => v > 1 || v < 0 || (v > 0.01 && v < 0.99))).toBe(true);
    expect(flowing.filter((q) => q.dir >= 2).every((q) => q.r * 16 + q.c === 206)).toBe(true);
  });
});

describe('b1.8.1 shapes', () => {
  const src = () => tableBeta().source as BetaSource;
  const count = (id: number, meta = 0, w: W = [[0, 0, 0, id, meta]]) => {
    const out: Quad[] = [];
    src().build(id, meta, nbAt(w), out);
    return out;
  };
  it('face counts per render type', () => {
    expect(count(50, 5).length).toBe(5); // torch: four planes + cap (two-sided, mirrored by the mesher)
    expect(count(38).length).toBe(2); // crossed squares
    expect(count(59, 7).length).toBe(4); // crops: four planes in a #
    expect(count(64, 0).length).toBe(6); // door box
    expect(count(65, 2).length).toBe(1); // ladder plane
    expect(count(66, 0).length).toBe(1); // rail
    expect(count(53, 0).length).toBe(12); // stairs: two boxes
    expect(count(85).length).toBe(6); // a lone fence post
    expect(count(85, 0, [[0, 0, 0, 85], [1, 0, 0, 85]]).length).toBe(18); // post + two bars
    expect(count(69, 5).length).toBe(12); // lever base + handle
    expect(count(81).length).toBe(6); // cactus: top, bottom, four inset planes
    expect(count(26, 0).length).toBe(6); // bed
    expect(count(93, 0).length).toBe(6 + 10); // repeater slab + two torches
    expect(count(33, 1).length).toBe(6); // piston
    expect(count(101).length).toBe(12); // a lone pane: both full arms
    expect(count(104, 3).length).toBe(2); // stem
    expect(count(106, 5).length).toBe(2); // vine on two sides
    expect(count(107, 0).length).toBe(4 * 6); // closed gate: two posts + two bars
    expect(count(63, 4).length).toBe(12); // standing sign: board + stick
    expect(count(68, 2).length).toBe(6); // wall sign
  });

  it('wall torches lean away from their wall; rails ascend; doors turn with their state', () => {
    const t = count(50, 1); // on the west block's face: the foot against it (x ≈ 0), the head leaning east
    const side = t[1];
    const top = Math.max(side.p[1], side.p[4], side.p[7], side.p[10]);
    const at = (h: number) => [0, 1, 2, 3].filter((k) => Math.abs(side.p[k * 3 + 1] - h) < 1e-6).map((k) => side.p[k * 3]);
    expect(Math.min(...at(top))).toBeGreaterThan(Math.max(...at(top - 1)));
    expect(Math.min(...at(top - 1))).toBeLessThan(0.1);
    const asc = count(66, 2)[0]; // ascending east
    const ys = [asc.p[1], asc.p[4], asc.p[7], asc.p[10]];
    expect(Math.max(...ys) - Math.min(...ys)).toBeCloseTo(1);
    const door0 = count(64, 0), door1 = count(64, 1);
    const xs = (qs: Quad[]) => qs.flatMap((q) => [q.p[0], q.p[3], q.p[6], q.p[9]]);
    expect(Math.max(...xs(door0)) - Math.min(...xs(door0))).not.toBeCloseTo(Math.max(...xs(door1)) - Math.min(...xs(door1)));
  });

  it('redstone wire uses the line tile along one axis, the trimmed dot otherwise', () => {
    const line = count(55, 15, [[0, 0, 0, 55, 15], [1, 0, 0, 55], [-1, 0, 0, 55]]);
    expect(line[0].r * 16 + line[0].c).toBe(165);
    const dot = count(55, 0);
    expect(dot[0].r * 16 + dot[0].c).toBe(164);
    const corner = count(55, 0, [[0, 0, 0, 55], [1, 0, 0, 55], [0, 0, 1, 55]]);
    const xs = [corner[0].p[0], corner[0].p[6]];
    expect(Math.min(...xs)).toBeCloseTo(0.3125);
  });

  it('cube flags: opaque cubes cull, glass self-culls, leaves draw every face', () => {
    const f = tableBeta().flags;
    expect(f[1 << 4] & 1).toBe(1);
    expect(f[20 << 4] & 1).toBe(0);
    expect(f[54 << 4] & 1).toBe(1);
    expect(f[44 << 4] & 1).toBe(0);
  });
});
