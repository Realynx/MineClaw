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
    expect(layer.stats().preview).toEqual({ ok: 1, blocked: 1 });
    layer.setPreview(null);
    expect(layer.stats().preview).toEqual({ ok: 0, blocked: 0 });
    layer.dispose();
  });
});
