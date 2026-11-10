import type * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { HoloRequest, HoloResult } from '../src/mesh/holo';
import { InlineMeshHost, type MeshHost } from '../src/mesh/host';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import { HologramLayer } from '../src/render/holograms';
import { HOLO_REGION, HOLO_REGION_MIN, planHolograms, planRegions, type HoloCell } from '../src/render/holoPlan';

/** A floor of `w` x `d` stone cells at y 64 (block holograms) and, under it, a row of dig cells along x at y 60. */
function queue(w: number, d: number): HoloCell[] {
  const cells: HoloCell[] = [];
  for (let x = 0; x < w; x++)
    for (let z = 0; z < d; z++) cells.push({ x, y: 64, z, id: 1, meta: 0, build: 'floor', state: 'todo' });
  for (let x = 0; x < w; x++) cells.push({ x, y: 60, z: 0, id: 0, meta: 0, build: 'mine', state: 'todo' });
  return cells;
}

/** The inline host, recording what each round trip asks for. */
async function recordingHost(): Promise<{ host: MeshHost; asked: HoloRequest[][]; inline: InlineMeshHost }> {
  const inline = new InlineMeshHost();
  await inline.configure('b1.8.1', null, false);
  const asked: HoloRequest[][] = [];
  const host = {
    holo: (dim: number, reqs: HoloRequest[]): Promise<HoloResult[]> => {
      asked.push(reqs);
      return inline.holo(dim, reqs);
    },
  } as unknown as MeshHost;
  return { host, asked, inline };
}

const cellsAsked = (reqs: HoloRequest[]) => reqs.reduce((n, r) => n + r.blocks.length / 5, 0);
const regionsAsked = (reqs: HoloRequest[]) => [...new Set(reqs.map((r) => r.key.split('@')[1]))].sort();
const objects = (layer: HologramLayer) =>
  layer.group.children[0]!.children.flatMap((g) => g.children) as THREE.Object3D[];

describe('hologram regions', () => {
  it('splits a plan by region, each region in the plan’s order', () => {
    const plan = planHolograms([
      { x: 1, y: 64, z: 1, id: 4, meta: 0, build: 'a', state: 'todo' },
      { x: 40, y: 64, z: 1, id: 4, meta: 2, build: 'a', state: 'active' },
      { x: 2, y: 64, z: 1, id: 5, meta: 0, build: 'a', state: 'todo' },
      { x: -1, y: 60, z: -40, id: 0, meta: 0, build: 'b', state: 'todo' },
    ]);
    const regions = [...planRegions(plan).values()];
    expect(regions.map((r) => r.key)).toEqual(['0,0', '1,0', '-1,-2']);
    expect(regions[0]!.groups.todo).toEqual([1, 64, 1, 4, 0, 2, 64, 1, 5, 0]);
    expect(regions[1]!.groups.active).toEqual([40, 64, 1, 4, 2]);
    expect(regions[2]!.dig).toEqual([-1, 60, -40, 0, 0]);
  });

  it('a big queue re-meshes only the regions a change touches', async () => {
    const { host, asked } = await recordingHost();
    const layer = new HologramLayer(host, createChunkUniforms());
    const cells = queue(4 * HOLO_REGION, 48); // 6,272 cells over 8 regions
    expect(cells.length).toBeGreaterThan(HOLO_REGION_MIN);
    layer.set(cells, 'r1');
    await layer.idle;
    expect(cellsAsked(asked[0]!)).toBe(cells.length);
    expect(layer.stats()).toMatchObject({ cells: 4 * HOLO_REGION * 48, digs: 4 * HOLO_REGION, builds: 1 });
    const before = objects(layer);

    // One cell leased (x 70, z 10: region 2,0): that region alone goes to the worker; the others keep their meshes.
    const leased = cells.map((c) => (c.x === 70 && c.z === 10 && c.y === 64 ? { ...c, state: 'active' as const } : c));
    layer.set(leased, 'r2');
    await layer.idle;
    expect(regionsAsked(asked[1]!)).toEqual(['2,0']);
    expect(asked[1]!.map((r) => r.key)).toEqual(['todo@2,0', 'active@2,0', 'dig@2,0']);
    const after = objects(layer);
    const kept = before.filter((o) => after.includes(o));
    expect(kept.length).toBe(before.length - 3); // the region's todo mesh, dig outline and dig fill were replaced
    expect(layer.has(70, 64, 10, 'block')).toBe(true);
    expect(layer.stats()).toMatchObject({ cells: 4 * HOLO_REGION * 48, digs: 4 * HOLO_REGION, builds: 2 });

    // A pawn's dig lands on a queued dig cell: only that cell's region is meshed again.
    layer.blocksChanged(0, [[5, 60, 0]]);
    await layer.idle;
    expect(regionsAsked(asked[2]!)).toEqual(['0,0']);
    // A change on no queued cell asks for nothing.
    layer.blocksChanged(0, [[5, 61, 0]]);
    await layer.idle;
    expect(asked).toHaveLength(3);
    // Same revision: nothing.
    expect(layer.set(leased, 'r2')).toBe(false);
    layer.dispose();
  });

  it('a region whose cells all went is dropped; the rest stay', async () => {
    const { host, asked } = await recordingHost();
    const layer = new HologramLayer(host, createChunkUniforms());
    const cells = queue(4 * HOLO_REGION, 48);
    layer.set(cells, 'r1');
    await layer.idle;
    const kept = cells.filter((c) => c.x < 3 * HOLO_REGION || c.y === 60);
    // Still a big queue without the floor's last region (x 96-127): only the dig row is left there.
    expect(kept.length).toBeGreaterThan(HOLO_REGION_MIN);
    layer.set(kept, 'r2');
    await layer.idle;
    // Region 3,0 keeps its digs (meshed again); 3,1 held only floor and goes.
    expect(regionsAsked(asked[1]!)).toEqual(['3,0']);
    expect(layer.has(100, 64, 5)).toBe(false);
    expect(layer.has(100, 60, 0, 'dig')).toBe(true);
    expect(layer.stats()).toMatchObject({ cells: 3 * HOLO_REGION * 48, digs: 4 * HOLO_REGION });
    expect(layer.group.children[0]!.children.map((g) => g.name)).not.toContain('holo region 3,1');
    layer.dispose();
  });

  it('an answer for cells that changed while it was meshed is dropped and asked for again', async () => {
    const { inline } = await recordingHost();
    const asked: HoloRequest[][] = [];
    const gate: { open?: () => void } = {};
    const host = {
      holo: async (dim: number, reqs: HoloRequest[]) => {
        asked.push(reqs);
        if (asked.length === 2) await new Promise<void>((r) => (gate.open = r));
        return inline.holo(dim, reqs);
      },
    } as unknown as MeshHost;
    const layer = new HologramLayer(host, createChunkUniforms());
    const cells = queue(4 * HOLO_REGION, 48);
    layer.set(cells, 'r1');
    await layer.idle;
    const leased = (x: number) =>
      cells.map((c) => (c.x === x && c.z === 10 && c.y === 64 ? { ...c, state: 'active' as const } : c));
    layer.set(leased(70), 'r2'); // on its way…
    await Promise.resolve();
    layer.set(leased(71), 'r3'); // …and 71 replaces 70 meanwhile
    gate.open!();
    await layer.idle;
    expect(asked).toHaveLength(3);
    const activeAsked = (i: number) => asked[i]!.find((r) => r.key === 'active@2,0')!.blocks.slice(0, 3);
    expect(activeAsked(1)).toEqual([70, 64, 10]);
    expect(activeAsked(2)).toEqual([71, 64, 10]);
    const active = objects(layer).filter((o) => o.name === 'holo active@2,0');
    expect(active).toHaveLength(1);
    expect(layer.stats().builds).toBe(2); // the answer for r2 was never drawn
    layer.dispose();
  });
});
