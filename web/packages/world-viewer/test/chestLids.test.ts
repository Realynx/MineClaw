import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';
import { builtinQuads, chestLidModel, type SheetLookup } from '../src/mesh/models/builtin';
import { resolveState, type Nb } from '../src/mesh/models/states';
import type { Quad } from '../src/mesh/quad';
import { ChestLidStates, chestLidAngle, chestLidStep } from '../src/render/chestLids';
import { BlockEntityLayer } from '../src/render/blockEntityLayer';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import type { AssetBundle } from '../src/assets/bundle';
import type { MeshHost } from '../src/mesh/host';
import type { BlockEntityDto, EntityEvent } from '../src/types';

const sheets: SheetLookup = (name) => ({ c: 4, r: 8, wc: name.endsWith('_double') ? 8 : 4, hc: 4 });
const facing = { 2: 'north', 3: 'south', 4: 'west', 5: 'east' } as const;

describe('chest lid geometry', () => {
  it.each([2, 3, 4, 5] as const)('preserves the closed model and opens upward about the back hinge facing %s', (f) => {
    for (const kind of ['normal', 'trapped', 'ender']) {
      const pairs: Array<[number, number] | null> = kind === 'ender' ? [null] : [null, ...(f <= 3 ? [[1, 0], [-1, 0]] : [[0, 1], [0, -1]]) as Array<[number, number]>];
      for (const pair of pairs) {
        const props = { kind, facing: facing[f], ...(pair ? { pair: pair.join(',') } : {}) };
        const all: Quad[] = [], body: Quad[] = [];
        builtinQuads(54, { file: '', props, key: null, builtin: true }, null, sheets, all);
        builtinQuads(54, { file: '', props: { ...props, liveLid: 'true' }, key: null, builtin: true }, null, sheets, body);
        const lid = chestLidModel(kind, f, pair, sheets);
        const normalized = (quads: Quad[]) => quads.map((q) => JSON.stringify(q)).sort();
        expect(normalized([...body, ...lid.quads])).toEqual(normalized(all));
        expect(body.length).toBeGreaterThan(0);
        const hinge = new THREE.Vector3(...lid.hinge), rotation = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(...lid.axis), chestLidAngle(1));
        const ys = lid.quads.flatMap((q) => Array.from({ length: 4 }, (_, i) => new THREE.Vector3(...q.p.slice(i * 3, i * 3 + 3) as [number, number, number]).sub(hinge).applyQuaternion(rotation).add(hinge).y));
        expect(Math.max(...ys)).toBeCloseTo(1.5); // the latch reaches one pixel above the raised lid
      }
    }
  });

  it('keeps static closed lids until that cell has a chest block entity', () => {
    const nb: Nb = { wx: 0, wy: 64, wz: 0, id: () => 0, meta: () => 0 };
    expect(resolveState(54, 2, nb, () => false).props.liveLid).toBeUndefined();
    nb.be = () => ({ x: 0, y: 64, z: 0, kind: 'chest' });
    expect(resolveState(54, 2, nb, () => false).props.liveLid).toBe('true');
    expect(resolveState(130, 2, nb, () => false).props.liveLid).toBe('true');
  });
});

describe('authoritative chest animation state', () => {
  const event = (kind: string, at: number, x = 1, dim = 0): EntityEvent => ({ kind, at, x, y: 64, z: 3, dim });
  it('synchronizes both double-chest halves and ignores stale observer packets after a close', () => {
    const lids = new ChestLidStates();
    lids.receive([event('chest_open', 100)]);
    expect(lids.open(0, 2, 64, 3, [-1, 0])).toBe(true);
    expect(lids.open(-1, 1, 64, 3, null)).toBe(false);
    lids.receive([event('chest_close', 200, 2), event('chest_open', 100)]);
    expect(lids.open(0, 1, 64, 3, [1, 0])).toBe(false);
    lids.receive([event('chest_close', 300), event('chest_open', 100)]);
    expect(lids.open(0, 1, 64, 3, null)).toBe(false);
  });
  it('animates over half a second, reverses smoothly, and keeps the actual held-open state', () => {
    const lids = new ChestLidStates(); lids.receive([event('chest_open', 100)]);
    let amount = chestLidStep(0, true, 0.25);
    expect(amount).toBe(0.5); expect(chestLidAngle(amount)).toBeCloseTo(-Math.PI * 0.4375);
    amount = chestLidStep(amount, false, 0.1); expect(amount).toBeCloseTo(0.3);
    expect(chestLidStep(amount, true, 10)).toBe(1);
    expect(lids.open(0, 1, 64, 3, null)).toBe(true);
    lids.clear(); expect(lids.open(0, 1, 64, 3, null)).toBe(false);
  });
  it('shows quick open/close pairs received in one poll, without duplicates extending their visible hold', () => {
    const lids = new ChestLidStates();
    lids.receive([event('chest_open', 100), event('chest_close', 150)], 1000);
    expect(lids.open(0, 1, 64, 3, null, 1100)).toBe(true);
    lids.receive([event('chest_open', 100), event('chest_close', 150)], 1200);
    expect(lids.open(0, 1, 64, 3, null, 1301)).toBe(false);
  });
  it('restores a held-open chest from a fresh snapshot and closes missing entries even after a missed close event', () => {
    const lids = new ChestLidStates();
    lids.reconcile([{ dim: 0, x: 1, y: 64, z: 3, openCount: 2 }]);
    expect(lids.open(0, 2, 64, 3, [-1, 0], 1000)).toBe(true);
    lids.receive([event('chest_open', 100)], 1000);
    lids.reconcile([]);
    expect(lids.open(0, 1, 64, 3, null, 1301)).toBe(false);
    lids.receive([event('chest_open', 100)], 2000);
    expect(lids.open(0, 1, 64, 3, null, 2001)).toBe(false);
  });
});

it('animates live double lids without rebuilding geometry or querying the worker each frame, then removes them with the blocks', async () => {
  const cells = new Map([['1,64,3', [54, 2, 240]], ['2,64,3', [54, 2, 240]]]);
  const blocksAt = vi.fn((_dim: number, pts: number[]) => {
    const out: number[] = [];
    for (let i = 0; i < pts.length; i += 3) out.push(...cells.get(pts.slice(i, i + 3).join(',')) ?? [0, 0, -1]);
    return Promise.resolve(out);
  });
  const layer = new BlockEntityLayer({ blocksAt } as unknown as MeshHost, createChunkUniforms());
  layer.setBundle({ version: '1.12.2', blocks: { width: 256, height: 256 }, pack: { version: '1.12.2', blocks: { columns: 16, rows: 16, sheets: { 'entity/chest/normal_double': [4, 8, 8, 4] } } } } as unknown as AssetBundle);
  let bes: BlockEntityDto[] = [{ kind: 'chest', x: 1, y: 64, z: 3 }, { kind: 'chest', x: 2, y: 64, z: 3 }];
  const store = { revision: 1, all: () => bes };
  const frame = (now: number) => layer.update(store, { dim: 0, centre: new THREE.Vector3(0, 66, 0), now, ticks: now / 50, software: false });
  frame(1); await Promise.resolve();
  expect(layer.count).toBe(2);
  const objects = layer.group.children.map((g) => g.getObjectByName('chest-lid') as THREE.Mesh);
  const geometry = objects.map((o) => o.geometry);
  layer.receiveEvents([{ kind: 'chest_open', at: 1000, dim: 0, x: 1, y: 64, z: 3 }]);
  frame(251); frame(501);
  expect(objects[0].quaternion.angleTo(new THREE.Quaternion())).toBeCloseTo(Math.PI / 2);
  expect(objects[1].quaternion.toArray()).toEqual(objects[0].quaternion.toArray());
  layer.receiveEvents([{ kind: 'chest_close', at: 2000, dim: 0, x: 2, y: 64, z: 3 }]);
  frame(751); frame(1001);
  expect(objects[0].quaternion.angleTo(new THREE.Quaternion())).toBeCloseTo(0);
  expect(objects.map((o) => o.geometry)).toEqual(geometry);
  expect(blocksAt).toHaveBeenCalledOnce();
  const first = layer.group.children[0];
  cells.set('1,64,3', [54, 3, 240]); cells.set('2,64,3', [54, 3, 240]);
  layer.blocksChanged(0, [[1, 64, 3]]); frame(1002); await Promise.resolve();
  expect(layer.group.children[0]).not.toBe(first); // block metadata changed while block-entity JSON stayed identical
  expect(blocksAt).toHaveBeenCalledTimes(2);
  bes = []; store.revision++; frame(1002); await Promise.resolve();
  expect(layer.count).toBe(0); expect(layer.group.children).toHaveLength(0);
  layer.dispose();
});
