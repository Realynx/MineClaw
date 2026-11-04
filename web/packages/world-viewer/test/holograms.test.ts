import type * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import { InlineMeshHost } from '../src/mesh/host';
import { meshCells, meshHolograms } from '../src/mesh/holo';
import { buildStateTable } from '../src/mesh/states';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import { HologramLayer } from '../src/render/holograms';
import { HOLO_CAP, HOLO_LOD_PX, HOLO_STYLES, holoState, planHolograms, type HoloCell } from '../src/render/holoPlan';
import { cellIndex } from '../src/wire/chunk';
import { encodeColumn, encodeMcwc, lightSections } from '../src/wire/encode';
import { combineHash } from '../src/wire/hash';
import { storeWith } from './helpers';
import { table1122, tableBeta } from './pack';

const cell = (x: number, y: number, z: number, id = 4, state: HoloCell['state'] = 'todo', build: string | number = 1): HoloCell => ({ x, y, z, id, meta: 0, build, state });

/** A world of one column (0, 0) holding `blocks`, applied to the inline host's store. */
async function hostWith(blocks: Array<[number, number, number, number]>): Promise<InlineMeshHost> {
  const host = new InlineMeshHost();
  await host.configure('b1.8.1', null, false);
  const ids = new Uint8Array(256 * 128), meta = new Uint8Array(256 * 128);
  for (const [x, y, z, id] of blocks) ids[cellIndex(x & 15, y, z & 15)] = id;
  const e = encodeColumn(ids, meta, lightSections(128, () => 15, () => 0));
  await host.engine.store.applyPayload(encodeMcwc(0, [{ cx: 0, cz: 0, hash: combineHash(e.blockHash, e.lightHash!), seenMs: 1, by: 't', live: true, data: e.data, light: e.light }]));
  return host;
}

describe('hologram states', () => {
  it('maps a ghost and its build to the state it is drawn in', () => {
    expect(holoState('todo', 'queued')).toBe('todo');
    expect(holoState('todo', 'active')).toBe('todo');
    expect(holoState('leased', 'active')).toBe('active');
    expect(holoState('todo', 'blocked')).toBe('waiting'); // missing materials
    expect(holoState('todo', 'paused')).toBe('waiting');
    expect(holoState('blocked', 'active')).toBe('blocked'); // failed three times
    expect(holoState('unsupported', 'queued')).toBe('blocked');
    expect(holoState('done', 'active')).toBeNull();
    expect(holoState('todo', 'done')).toBeNull();
    expect(holoState('misrotated', 'active')).toBe('blocked'); // placed turned the wrong way: shown over it
    expect(holoState('misrotated', 'done')).toBe('blocked'); // even once its build is done
  });

  it('gives each state its own calm look', () => {
    const s = HOLO_STYLES;
    // cyan-blue holograms, 35-50 % opaque
    for (const k of ['todo', 'active'] as const) {
      expect(s[k].tint[2]).toBeGreaterThan(s[k].tint[0]);
      expect(s[k].opacity).toBeGreaterThanOrEqual(0.35);
      expect(s[k].opacity).toBeLessThanOrEqual(0.5);
    }
    expect(s.active.boost).toBeGreaterThan(s.todo.boost); // in progress: brighter
    expect(s.waiting.saturation).toBeLessThan(0.5); // waiting for materials: desaturated
    expect(s.blocked.stripes).toBe(1); // unsupported / failed: striped red
    expect(s.blocked.tint[0]).toBeGreaterThan(s.blocked.tint[2]);
    expect(s.place.tint[1]).toBeGreaterThan(s.place.tint[0]); // a placement that fits: green
    expect(s.placeBlocked.tint[0]).toBeGreaterThan(s.placeBlocked.tint[1]); // where it is blocked: red
  });
});

describe('planHolograms', () => {
  it('batches cells into one group per state, digs apart, one box per build', () => {
    const p = planHolograms([
      cell(0, 64, 0),
      cell(1, 64, 0, 4, 'active'),
      cell(2, 64, 0, 4, 'waiting', 2),
      cell(3, 63, 0, 0, 'todo', 3),
      cell(4, 70, 2, 5, 'blocked'),
    ]);
    expect(p.groups.todo).toEqual([0, 64, 0, 4, 0]);
    expect(p.groups.active).toEqual([1, 64, 0, 4, 0]);
    expect(p.groups.waiting).toHaveLength(5);
    expect(p.groups.blocked).toHaveLength(5);
    expect(p.dig).toEqual([3, 63, 0, 0, 0]);
    expect(p.cells).toBe(5);
    expect(p.boxes).toHaveLength(3);
    const b1 = p.boxes.find((b) => b.build === '1')!;
    expect(b1.min).toEqual([0, 64, 0]);
    expect(b1.max).toEqual([5, 71, 3]);
    expect(b1.state).toBe('active'); // the liveliest state of the build
    expect(p.boxes.find((b) => b.build === '3')!.state).toBe('dig');
  });

  it('caps the cells drawn; the capped builds keep their box', () => {
    const cells = [...Array.from({ length: 30 }, (_, i) => cell(i, 64, 0, 4, 'todo', 'a')), ...Array.from({ length: 30 }, (_, i) => cell(i, 65, 0, 4, 'todo', 'b'))];
    const p = planHolograms(cells, 40);
    expect(p.cells).toBe(40);
    expect(p.groups.todo.length / 5).toBe(40);
    expect([...p.capped]).toEqual(['b']);
    expect(p.boxes.find((b) => b.build === 'b')!.max[0]).toBe(30); // the box covers the whole build
    expect(HOLO_CAP).toBeGreaterThanOrEqual(20_000);
  });
});

describe('meshing holograms (worker side)', () => {
  const table = buildStateTable('b1.8.1', null, false);

  it('merges a group into one vertex set; faces between its own cells are culled', () => {
    const one = meshCells([0, 64, 0, 4, 0], table)!;
    expect(one.quads).toBe(6);
    const two = meshCells([0, 64, 0, 4, 0, 1, 64, 0, 4, 0], table)!;
    expect(two.quads).toBe(10);
    expect(two.index).toBeInstanceOf(Uint32Array);
    expect(two.cell.length).toBe(two.pos.length); // a block cell per vertex
    // across sections and columns: still one set, positions relative to the origin
    const far = meshCells([0, 64, 0, 4, 0, 40, 100, -20, 4, 0], table)!;
    expect(far.quads).toBe(12);
    expect(far.origin).toEqual([0, 64, -32]);
    expect(Math.max(...far.pos.filter((_, i) => i % 3 === 0))).toBe(41);
  });

  it('leaves out built cells (the world already has the block) and dug cells (already air)', async () => {
    const store = await storeWith([
      [0, 64, 0, 4],
      [2, 60, 0, 1],
    ]);
    const rs = meshHolograms(store, 0, table, [
      { key: 'todo', blocks: [0, 64, 0, 4, 0, 1, 64, 0, 4, 0], mode: 'build' },
      { key: 'dig', blocks: [2, 60, 0, 0, 0, 3, 60, 0, 0, 0], mode: 'dig' },
      { key: 'p', blocks: [0, 64, 0, 5, 0, 1, 64, 0, 5, 0], mode: 'place' },
    ]);
    expect(rs.map((r) => r.key)).toEqual(['todo', 'dig', 'p:ok', 'p:blocked']);
    expect(rs[0].cells).toEqual([1, 64, 0, 4, 0]); // (0, 64, 0) is built
    expect(rs[0].mesh!.quads).toBe(6);
    expect(rs[1].cells).toEqual([2, 60, 0, 0, 0]); // (3, 60, 0) is air already
    expect(rs[1].mesh).toBeNull();
    expect(rs[2].cells).toEqual([1, 64, 0, 5, 0]); // fits
    expect(rs[3].cells).toEqual([0, 64, 0, 5, 0]); // cobblestone in the way
  });
});

describe('HologramLayer', () => {
  it('rebuilds only when the revision moves, and drops a cell once it is built', async () => {
    const host = await hostWith([]);
    const layer = new HologramLayer(host, createChunkUniforms());
    const cells = [cell(0, 64, 0), cell(1, 64, 0), cell(2, 64, 0, 0)];
    expect(layer.set(cells, 'r1')).toBe(true);
    await layer.idle;
    expect(layer.stats()).toMatchObject({ cells: 2, digs: 0, builds: 1, rev: 'r1' }); // (2, 64, 0) is air: nothing to dig
    expect(layer.has(0, 64, 0)).toBe(true);
    // Same revision every frame: no work.
    for (let i = 0; i < 5; i++) expect(layer.set(cells, 'r1')).toBe(false);
    await layer.idle;
    expect(layer.stats().builds).toBe(1);
    // A pawn places (0, 64, 0): the change lands on a queued cell, the hologram goes.
    await host.patch(0, [[0, 64, 0, 4, 0]]);
    layer.blocksChanged(0, [[0, 64, 0]]);
    await layer.idle;
    expect(layer.stats()).toMatchObject({ cells: 1, builds: 2 });
    expect(layer.has(0, 64, 0)).toBe(false);
    expect(layer.has(1, 64, 0)).toBe(true);
    // A change elsewhere does nothing.
    layer.blocksChanged(0, [[9, 9, 9]]);
    await layer.idle;
    expect(layer.stats().builds).toBe(2);
    // A new revision with fewer cells.
    expect(layer.set([cell(1, 64, 0, 4, 'active')], 'r2')).toBe(true);
    await layer.idle;
    expect(layer.stats()).toMatchObject({ cells: 1, builds: 3, rev: 'r2' });
    const meshes = layer.group.children.flatMap((g) => g.children).filter((o) => o.name.startsWith('holo '));
    expect(meshes.map((m) => m.name)).toEqual(['holo active']);
    layer.dispose();
  });

  it('switches to one outline box per build when blocks are a few pixels', async () => {
    const host = await hostWith([]);
    const layer = new HologramLayer(host, createChunkUniforms());
    layer.set([cell(0, 64, 0, 4, 'todo', 'a'), cell(5, 64, 5, 4, 'todo', 'b')], 'r');
    await layer.idle;
    const [cells, boxes] = layer.group.children;
    layer.show(12);
    expect(layer.stats().lod).toBe(false);
    expect(cells.visible).toBe(true);
    expect(boxes.children.every((b) => !b.visible)).toBe(true);
    layer.show(HOLO_LOD_PX - 1);
    expect(layer.stats()).toMatchObject({ lod: true, boxes: 2 });
    expect(cells.visible).toBe(false);
    expect(boxes.children.some((b) => b.visible)).toBe(true);
    layer.dispose();
  });

  it('previews a placement in two looks: fits and blocked', async () => {
    const host = await hostWith([[3, 64, 0, 1]]);
    const layer = new HologramLayer(host, createChunkUniforms());
    layer.setPreview([2, 64, 0, 5, 0, 3, 64, 0, 5, 0, 4, 63, 0, 0, 0], 'k1');
    await new Promise((r) => setTimeout(r, 20));
    expect(layer.stats().preview).toEqual({ ok: 1, blocked: 1, dig: 1 });
    layer.setPreview(null);
    expect(layer.stats().preview).toEqual({ ok: 0, blocked: 0, dig: 0 });
    layer.dispose();
  });
});

describe('every queued block kind bakes a hologram', () => {
  // [x, y, z, id, meta, …] as the bot queues them (furniture with meta 0; a door's and a bed's second half come with the first).
  const KINDS: Record<string, number[]> = {
    chest: [0, 64, 0, 54, 0],
    'double chest': [0, 64, 0, 54, 2, 1, 64, 0, 54, 2],
    bed: [0, 64, 0, 26, 0, 0, 64, 1, 26, 8],
    'standing sign': [0, 64, 0, 63, 4],
    'wall sign': [0, 64, 0, 68, 2],
    door: [0, 64, 0, 64, 1, 0, 65, 0, 64, 8],
    torch: [0, 64, 0, 50, 0],
    'wall torch': [0, 64, 0, 50, 3],
    slab: [0, 64, 0, 44, 0],
    stairs: [0, 64, 0, 53, 2],
    fence: [0, 64, 0, 85, 0],
    pane: [0, 64, 0, 102, 0],
    ladder: [0, 64, 0, 65, 2],
    furnace: [0, 64, 0, 61, 0],
    'crafting table': [0, 64, 0, 58, 0],
  };
  const ONLY_1122: Record<string, number[]> = {
    'trapped chest': [0, 64, 0, 146, 2],
    'ender chest': [0, 64, 0, 130, 2],
    'standing banner': [0, 64, 0, 176, 0],
    'wall banner': [0, 64, 0, 177, 2],
    skull: [0, 64, 0, 144, 1],
    'shulker box': [0, 64, 0, 219, 1],
  };
  type Mesh = NonNullable<ReturnType<typeof meshCells>>;
  const textured = (m: Mesh) => m.tile.some((v, i) => i % 4 === 2 && (v & 1) === 1);
  const axis = (m: Mesh, k: number) => m.pos.filter((_, i) => i % 3 === k);

  for (const [name, table] of [
    ['b1.8.1', tableBeta],
    ['1.12.2', table1122],
  ] as const)
    it(`${name}: block entities (chests, beds, signs…) and partial models in their own textured shape`, () => {
      const t = table();
      const kinds = name === '1.12.2' ? { ...KINDS, ...ONLY_1122 } : KINDS;
      for (const [kind, cells] of Object.entries(kinds)) {
        const m = meshCells(cells, t);
        expect(m, kind).not.toBeNull();
        expect(m!.quads, kind).toBeGreaterThan(0);
        expect(textured(m!), kind).toBe(true);
        // Every vertex belongs to one of the item's cells (the level cut and the "built" match use it).
        const at = new Set<string>();
        for (let i = 0; i < cells.length; i += 5) at.add(`${cells[i] - m!.origin[0]},${cells[i + 1] - m!.origin[1]},${cells[i + 2] - m!.origin[2]}`);
        for (let v = 0; v < m!.cell.length; v += 3) expect(at.has(`${m!.cell[v]},${m!.cell[v + 1]},${m!.cell[v + 2]}`), kind).toBe(true);
      }
      // Not box stand-ins: a slab is half a block high, a door has both halves, a chest is its smaller model, a bed is two
      // cells long and low.
      expect(Math.max(...axis(meshCells(KINDS.slab, t)!, 1))).toBeCloseTo(0.5, 3);
      expect(Math.max(...axis(meshCells(KINDS.door, t)!, 1))).toBeCloseTo(2, 3);
      // (b1.8.1's chest is a full block with its chest faces, 1.12.2's the entity model.)
      expect(Math.max(...axis(meshCells(KINDS.chest, t)!, 1))).toBeCloseTo(name === '1.12.2' ? 0.875 : 1, 3);
      const bed = meshCells(KINDS.bed, t)!;
      expect(Math.max(...axis(bed, 2))).toBeCloseTo(2, 3);
      expect(Math.max(...axis(bed, 1))).toBeLessThan(0.6);
      // A double chest is one wide chest, not two singles.
      expect(meshCells(KINDS['double chest'], t)!.quads).toBeLessThan(2 * meshCells(KINDS.chest, t)!.quads);
      // A door's two halves stand on the same side of the cell for every facing (b1.8.1's upper half takes the lower's
      // facing: the queue sends it as plain 8).
      for (const meta of [0, 1, 2, 3]) {
        const m = meshCells([0, 64, 0, 64, meta, 0, 65, 0, 64, 8], t)!;
        const span = (lower: boolean) => {
          const xs: number[] = [], zs: number[] = [];
          for (let v = 0; v < m.pos.length; v += 3)
            if ((m.cell[v + 1] === 0) === lower) {
              xs.push(m.pos[v]);
              zs.push(m.pos[v + 2]);
            }
          return [Math.min(...xs), Math.max(...xs), Math.min(...zs), Math.max(...zs)];
        };
        expect(span(false), `door meta ${meta}`).toEqual(span(true));
      }
    });

  it('the layer draws a queued chest, a door and a torch, and drops the chest once it is placed', async () => {
    const host = await hostWith([]);
    const layer = new HologramLayer(host, createChunkUniforms());
    const cells: HoloCell[] = [
      { x: 2, y: 64, z: 2, id: 54, meta: 0, build: 'chest', state: 'todo' },
      { x: 4, y: 64, z: 2, id: 64, meta: 1, build: 'door', state: 'todo' },
      { x: 4, y: 65, z: 2, id: 64, meta: 8, build: 'door', state: 'todo' },
      { x: 6, y: 64, z: 2, id: 50, meta: 5, build: 'torch', state: 'active' },
    ];
    layer.set(cells, 'r1');
    await layer.idle;
    expect(layer.stats()).toMatchObject({ cells: 4, digs: 0 });
    for (const c of cells) expect(layer.has(c.x, c.y, c.z)).toBe(true);
    const meshes = layer.group.children.flatMap((g) => g.children).filter((o) => o.name.startsWith('holo ')) as THREE.Mesh[];
    expect(meshes.map((m) => m.name).sort()).toEqual(['holo active', 'holo todo']);
    for (const m of meshes) expect(m.geometry.index!.count).toBeGreaterThan(0);
    await host.patch(0, [[2, 64, 2, 54, 0]]);
    layer.blocksChanged(0, [[2, 64, 2]]);
    await layer.idle;
    expect(layer.has(2, 64, 2)).toBe(false);
    expect(layer.stats().cells).toBe(3);
    layer.dispose();
  });
});

describe('a water trench: dig first, then pour', () => {
  /** World: dirt at (0..2, 63, 0), air above. */
  const ground: Array<[number, number, number, number]> = [
    [0, 63, 0, 3],
    [1, 63, 0, 3],
    [2, 63, 0, 3],
  ];
  const trench = (state: HoloCell['state'] = 'todo'): HoloCell[] => [
    // The remove build's dig cells and the water build's pour cells: the same cells, two builds.
    ...[0, 1, 2].map((x): HoloCell => ({ x, y: 63, z: 0, id: 0, meta: 0, build: 'dig', state })),
    ...[0, 1, 2].map((x): HoloCell => ({ x, y: 63, z: 0, id: 9, meta: 0, build: 'water', state })),
  ];

  it('the worker leaves a pour out while its cell is still solid, and shows it once dug', async () => {
    const store = await storeWith([...ground, [3, 63, 0, 9]]);
    const t = buildStateTable('b1.8.1', null, false);
    const [rs] = meshHolograms(store, 0, t, [{ key: 'todo', blocks: [0, 63, 0, 9, 0, 3, 63, 0, 9, 0, 4, 63, 0, 9, 0], mode: 'build' }]);
    // (0, 63, 0) is dirt (waits for its dig), (3, 63, 0) already holds the water (done), (4, 63, 0) is air: shown.
    expect(rs.cells).toEqual([4, 63, 0, 9, 0]);
  });

  it('the layer shows the dig outlines over solid ground first, then the water once a cell is dug', async () => {
    const host = await hostWith(ground);
    const layer = new HologramLayer(host, createChunkUniforms());
    layer.set(trench(), 'r1');
    await layer.idle;
    expect(layer.stats()).toMatchObject({ cells: 0, digs: 3 });
    for (const x of [0, 1, 2]) {
      expect(layer.has(x, 63, 0, 'dig')).toBe(true);
      expect(layer.has(x, 63, 0, 'block')).toBe(false);
    }
    // The outline stands out of the cell (over the ground's surface), and the exposed tops get a fill just above y 64.
    const marks = layer.group.children.flatMap((g) => g.children);
    const lines = marks.find((o) => o.name === 'holo dig') as THREE.LineSegments;
    const fill = marks.find((o) => o.name === 'holo dig fill') as THREE.Mesh;
    const ly = (lines.geometry.getAttribute('position').array as Float32Array).filter((_, i) => i % 3 === 1);
    expect(Math.max(...ly) + lines.position.y).toBeGreaterThan(64);
    const fy = (fill.geometry.getAttribute('position').array as Float32Array).filter((_, i) => i % 3 === 1);
    expect(fy.length).toBe(3 * 6);
    expect(Math.min(...fy) + fill.position.y).toBeGreaterThan(64);
    // A pawn digs (1, 63, 0): its outline goes, the water hologram shows there.
    await host.patch(0, [[1, 63, 0, 0, 0]]);
    layer.blocksChanged(0, [[1, 63, 0]]);
    await layer.idle;
    expect(layer.has(1, 63, 0, 'dig')).toBe(false);
    expect(layer.has(1, 63, 0, 'block')).toBe(true);
    expect(layer.stats()).toMatchObject({ cells: 1, digs: 2 });
    layer.dispose();
  });

  it('a trench preview: dig outlines for the cells, and the water in them is not "blocked" by the ground it digs out', async () => {
    const host = await hostWith(ground);
    const layer = new HologramLayer(host, createChunkUniforms());
    const blocks = [0, 1, 2].flatMap((x) => [x, 63, 0, 0, 0]).concat([0, 1, 2].flatMap((x) => [x, 63, 0, 9, 0]), [3, 63, 0, 8, 4]);
    layer.setPreview(blocks, 'trench');
    await new Promise((r) => setTimeout(r, 20));
    expect(layer.stats().preview).toEqual({ ok: 4, blocked: 0, dig: 3 });
    const names = layer.group.children.flatMap((g) => g.children).map((o) => o.name);
    expect(names).toEqual(expect.arrayContaining(['holo dig', 'holo dig fill', 'holo p:ok']));
    layer.dispose();
  });
});
