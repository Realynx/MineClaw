import { describe, expect, it } from 'vitest';
import { meshSection } from '../src/mesh/mesher';
import type { Models1122 } from '../src/mesh/models/index';
import { wireColour } from '../src/mesh/models/index';
import { resolveState, stairShape, type Nb } from '../src/mesh/models/states';
import { TINT_FIXED, TINT_GRASS } from '../src/mesh/quad';
import { NO_NB } from '../src/mesh/source';
import { F_OPAQUE } from '../src/world/blocks';
import { storeWith } from './helpers';
import { pack1122, table1122 } from './pack';

type W = Array<[number, number, number, number, number?]>;

/** A neighbourhood over a sparse block list, centred on (x, y, z). */
function nbAt(blocks: W, x = 0, y = 0, z = 0): Nb {
  const m = new Map(blocks.map(([bx, by, bz, id, meta]) => [`${bx},${by},${bz}`, [id, meta ?? 0]]));
  return { id: (dx, dy, dz) => m.get(`${x + dx},${y + dy},${z + dz}`)?.[0] ?? 0, meta: (dx, dy, dz) => m.get(`${x + dx},${y + dy},${z + dz}`)?.[1] ?? 0, wx: x, wy: y, wz: z };
}

const src = () => table1122().source as Models1122;
const solidFull = (id: number, meta: number) => (table1122().flags[(id << 4) | meta] & F_OPAQUE) !== 0;
const state = (blocks: W, x = 0, y = 0, z = 0) => {
  const nb = nbAt(blocks, x, y, z);
  const here = blocks.find((b) => b[0] === x && b[1] === y && b[2] === z)!;
  return resolveState(here[3], here[4] ?? 0, nb, solidFull);
};
const models = (blocks: W, x = 0, y = 0, z = 0) => src().modelsOf(state(blocks, x, y, z));

describe('1.12.2 blockstates', () => {
  it('classifies full cubes: stone and grass are opaque, glass and leaves are not, stairs never', () => {
    const f = table1122().flags;
    expect(f[1 << 4] & F_OPAQUE).toBeTruthy();
    expect(f[2 << 4] & F_OPAQUE).toBeTruthy();
    expect(f[20 << 4] & F_OPAQUE).toBeFalsy();
    expect(f[18 << 4] & F_OPAQUE).toBeFalsy();
    expect(f[53 << 4] & F_OPAQUE).toBeFalsy();
  });

  it('multipart: a lone fence is a post; a neighbour adds the side toward it', () => {
    expect(models([[0, 0, 0, 85]]).map((m) => m.model)).toEqual(['oak_fence_post']);
    const two = models([[0, 0, 0, 85], [1, 0, 0, 85]]);
    expect(two.map((m) => m.model)).toEqual(['oak_fence_post', 'oak_fence_side']);
    expect(two[1].y).toBe(90); // the east side is the north side turned
    // A full cube attaches; leaves (an attach exception) don't.
    expect(models([[0, 0, 0, 85], [0, 0, -1, 1]]).length).toBe(2);
    expect(models([[0, 0, 0, 85], [0, 0, -1, 18]]).length).toBe(1);
  });

  it('multipart: redstone dust is a dot alone, a line toward a neighbouring wire, and tinted by power', () => {
    const lone = models([[0, 0, 0, 55]]).map((m) => m.model);
    expect(lone).toContain('redstone_dot');
    const line = state([[0, 0, 0, 55, 15], [1, 0, 0, 55], [-1, 0, 0, 55]]);
    expect([line.props.east, line.props.west, line.props.north]).toEqual(['side', 'side', 'none']);
    expect(src().modelsOf(line).map((m) => m.model)).not.toContain('redstone_dot');
    const q = src().bake(55, line, NO_NB);
    expect(q.every((x) => x.tint === (TINT_FIXED | wireColour(15)))).toBe(true);
    expect(wireColour(0)).not.toBe(wireColour(15));
    // Climbing: a wire on the block beside and above.
    expect(state([[0, 0, 0, 55], [1, 0, 0, 1], [1, 1, 0, 55]]).props.east).toBe('up');
  });

  it('stair shapes: straight alone, outer and inner corners from the stairs in front of / behind', () => {
    // meta 0 = facing east; meta 3 = facing north.
    expect(stairShape(nbAt([[0, 0, 0, 53, 0]]), 0)).toBe('straight');
    expect(stairShape(nbAt([[0, 0, 0, 53, 0], [1, 0, 0, 53, 3]]), 0)).toBe('outer_left');
    expect(stairShape(nbAt([[0, 0, 0, 53, 0], [1, 0, 0, 53, 2]]), 0)).toBe('outer_right');
    expect(stairShape(nbAt([[0, 0, 0, 53, 0], [-1, 0, 0, 53, 3]]), 0)).toBe('inner_left');
    expect(stairShape(nbAt([[0, 0, 0, 53, 0], [-1, 0, 0, 53, 2]]), 0)).toBe('inner_right');
    // A different half never shapes.
    expect(stairShape(nbAt([[0, 0, 0, 53, 0], [1, 0, 0, 53, 3 | 4]]), 0)).toBe('straight');
    const outer = state([[0, 0, 0, 53, 0], [1, 0, 0, 53, 3]]);
    expect(src().modelsOf(outer)[0].model).toBe('oak_outer_stairs');
    // An inner corner has more faces than a straight stair, an outer one fewer.
    const n = (w: W) => src().bake(53, state(w), nbAt(w)).length;
    expect(n([[0, 0, 0, 53, 0], [-1, 0, 0, 53, 3]])).toBeGreaterThan(n([[0, 0, 0, 53, 0]]));
  });

  it('door halves share facing/open from the lower half and hinge from the upper', () => {
    const door: W = [[0, 0, 0, 64, 0 | 4], [0, 1, 0, 64, 8 | 1]];
    const lower = state(door, 0, 0, 0), upper = state(door, 0, 1, 0);
    expect([lower.props.half, upper.props.half]).toEqual(['lower', 'upper']);
    for (const s of [lower, upper]) {
      expect(s.props.facing).toBe('east');
      expect(s.props.open).toBe('true');
      expect(s.props.hinge).toBe('right');
    }
    // An open right-hinged door swings onto the left-hinged model turned 270°.
    expect(src().modelsOf(lower)[0]).toMatchObject({ model: 'wooden_door_bottom', y: 270 });
    expect(src().modelsOf(upper)[0]).toMatchObject({ model: 'wooden_door_top', y: 270 });
    expect(src().modelsOf(state([[0, 0, 0, 64, 0], [0, 1, 0, 64, 9]]))[0].model).toBe('wooden_door_bottom_rh');
  });

  it('variant rotations turn the geometry; uvlock keeps the texture projected in world space', () => {
    // Stairs facing south: y = 90 with uvlock — every top face's uv is its (x, z) position.
    const s = state([[0, 0, 0, 53, 2]]);
    expect(src().modelsOf(s)[0]).toMatchObject({ y: 90, uvlock: true });
    const tops = src().bake(53, s, NO_NB).filter((q) => q.dir === 1);
    expect(tops.length).toBeGreaterThan(0);
    for (const q of tops) for (let k = 0; k < 4; k++) {
      expect(q.uv[k * 2]).toBeCloseTo(q.p[k * 3]);
      expect(q.uv[k * 2 + 1]).toBeCloseTo(q.p[k * 3 + 2]);
    }
    // A sideways log (axis = x: x 90, y 90, no uvlock): its end faces point along x.
    const log = src().bake(17, resolveState(17, 4, null, solidFull), NO_NB);
    const ends = log.filter((q) => q.dir === 4 || q.dir === 5);
    const top = log.find((q) => q.dir === 1)!;
    const ring = pack1122().blocks.tiles['blocks/log_oak_top'];
    expect(ends.length).toBe(2);
    for (const q of ends) expect([q.c, q.r]).toEqual(ring);
    expect([top.c, top.r]).not.toEqual(ring);
  });

  it('ambient occlusion: model flag (torches, plants off), off for light emitters, on for cubes', () => {
    const q = (id: number, meta = 0) => src().quads((id << 4) | meta);
    expect(q(1).every((x) => x.ao)).toBe(true);
    expect(q(50, 5).every((x) => !x.ao)).toBe(true);
    expect(q(38).every((x) => !x.ao)).toBe(true);
    expect(q(89).every((x) => !x.ao && x.emissive)).toBe(true);
  });

  it('tints: grass top by biome, spruce leaves fixed; biome colours blend over the columns around', async () => {
    const grass = src().quads(2 << 4);
    expect(grass.find((x) => x.dir === 1)!.tint).toBe(TINT_GRASS);
    expect(src().quads((18 << 4) | 1)[0].tint).toBe(TINT_FIXED | 0x619961);
    expect(src().biomeColour(TINT_GRASS, 6)).not.toBe(src().biomeColour(TINT_GRASS, 1));
  });

  it('pairs chests into one large chest, each cell drawing its half inside its own cell', () => {
    const w: W = [[0, 0, 0, 54, 3], [1, 0, 0, 54, 3]];
    for (const x of [0, 1]) {
      const nb = nbAt(w, x, 0, 0);
      const s = resolveState(54, 3, nb, solidFull);
      expect(s.props.pair).toBe(x === 0 ? '1,0' : '-1,0');
      const qs = src().bake(54, s, nb);
      const xs = qs.flatMap((q) => [q.p[0], q.p[3], q.p[6], q.p[9]]);
      expect(Math.min(...xs)).toBeGreaterThanOrEqual(-1e-6);
      expect(Math.max(...xs)).toBeLessThanOrEqual(1 + 1e-6);
      // The seam side is open: the cell reaches its partner's boundary.
      expect(x === 0 ? Math.max(...xs) : Math.min(...xs)).toBeCloseTo(x === 0 ? 1 : 0);
      expect(qs.every((q) => q.span === ((8 << 8) | 4))).toBe(true);
    }
    // Three in a row: the middle has two neighbours, so none pair.
    expect(resolveState(54, 3, nbAt([[-1, 0, 0, 54, 3], [0, 0, 0, 54, 3], [1, 0, 0, 54, 3]]), solidFull).props.pair).toBeUndefined();
  });

  it('tile-entity blocks have geometry: beds, signs, banners, skulls, shulker boxes', () => {
    for (const [id, meta] of [[26, 0], [26, 8], [63, 0], [68, 2], [176, 0], [177, 3], [144, 1], [219, 1], [130, 2]])
      expect(src().quads((id << 4) | meta).length, `${id}:${meta}`).toBeGreaterThan(5);
  });

  it('meshes a 1.12.2 section with fences, panes, walls, doors, stairs and snowy grass', async () => {
    const Y = 70;
    const store = await storeWith(
      [[2, Y, 2, 85], [3, Y, 2, 85], [2, Y, 4, 102], [3, Y, 4, 102], [2, Y, 6, 139], [3, Y, 6, 139], [5, Y, 5, 64, 0], [5, Y + 1, 5, 64, 8], [7, Y, 7, 53, 0], [8, Y, 7, 53, 3], [9, Y, 9, 2], [9, Y + 1, 9, 78]],
      { height: 256 },
    );
    const m = meshSection(store, 0, 0, Y >> 4, 0, table1122());
    expect(m.solid!.quads).toBeGreaterThan(50);
    const snowy = resolveState(2, 0, nbAt([[0, 0, 0, 2], [0, 1, 0, 78]]), solidFull);
    expect(snowy.props.snowy).toBe('true');
    expect(src().modelsOf(snowy)[0].model).toBe('grass_snowed');
  });
});
