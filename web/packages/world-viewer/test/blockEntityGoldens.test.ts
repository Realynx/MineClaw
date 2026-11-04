// Behaviour pin for the block-entity layer's per-frame update (docs/dev/complexity.md, WP12): signs (plain, obfuscated, out
// of reach), banners (standing and wall, swaying), spawners (spinning while a player is near, their particles and display
// mobs) on a fake mesh host, stepped through moving centres, the software profile, rebuilds and a dimension change. Every
// frame records the display-mob hook calls and each object's visible state, text, sway and spin, hashed in memory.

import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import type { MeshHost } from '../src/mesh/host';
import { BlockEntityLayer, type BlockEntityLayerUpdate } from '../src/render/blockEntityLayer';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import type { BlockEntityDto, Dim } from '../src/types';
import { Digest } from './digest';

const log: string[] = [];
const rec = (s: string, v: unknown) => log.push(`${s} ${JSON.stringify(v)}`);

/** id, meta and light per cell: signs 63/68, banners 176/177, spawner 52 (one cell holds the wrong block). */
const CELLS: Record<string, [number, number, number]> = {
  '0,70,0': [63, 4, 0xf3],
  '3,70,1': [68, 2, 0xa5],
  '40,70,0': [63, 9, 0xf0],
  '5,71,5': [176, 7, 0xe1],
  '6,71,-2': [177, 5, 0x72],
  '-4,69,3': [52, 0, 0x90],
  '-30,69,3': [52, 0, 0x90],
  '9,70,9': [1, 0, 0xf0],
};

const host = {
  blocksAt: async (_dim: number, pts: number[]) => {
    const out: number[] = [];
    for (let i = 0; i < pts.length; i += 3) out.push(...(CELLS[`${pts[i]},${pts[i + 1]},${pts[i + 2]}`] ?? [0, 0, -1]));
    return out;
  },
} as unknown as MeshHost;

const BES: BlockEntityDto[] = [
  { x: 0, y: 70, z: 0, kind: 'sign', lines: ['Hello', '', 'world', ''] },
  { x: 3, y: 70, z: 1, kind: 'sign', lines: ['§kabc', 'x'] },
  { x: 40, y: 70, z: 0, kind: 'sign', lines: ['far'] },
  { x: 5, y: 71, z: 5, kind: 'banner', base: 'red', patterns: [['bs', 3], ['cr', 'blue']] },
  { x: 6, y: 71, z: -2, kind: 'banner', base: 2 },
  { x: -4, y: 69, z: 3, kind: 'mob_spawner', mob: 'zombie' },
  { x: -30, y: 69, z: 3, kind: 'mob_spawner' },
  { x: 9, y: 70, z: 9, kind: 'sign', lines: ['wrong block'] },
  { x: 1, y: 1, z: 1, kind: 'chest' },
];

function fakeCtx(w: number, h: number) {
  return {
    clearRect: () => undefined,
    fillText: (ch: string, x: number, y: number) => rec('fillText', [ch, x, y]),
    measureText: (ch: string) => ({ width: ch.length * 5 }),
    getImageData: () => ({ data: new Uint8ClampedArray(w * h * 4).map((_, i) => (i * 37) & 255) }),
    putImageData: () => undefined,
    set fillStyle(_v: string) {},
    set textBaseline(_v: string) {},
    set font(_v: string) {},
  };
}

beforeAll(() => {
  const g = globalThis as Record<string, unknown>;
  const doc = (g.document ??= {}) as Record<string, unknown>;
  doc.createElement = () => {
    const c = { width: 0, height: 0, getContext: () => fakeCtx(c.width, c.height) };
    return c;
  };
});
afterAll(() => vi.restoreAllMocks());

const flush = async () => {
  for (let i = 0; i < 20; i++) await Promise.resolve();
};

describe('block-entity layer golden', () => {
  it('pins update over a scripted session', async () => {
    const layer = new BlockEntityLayer(host, createChunkUniforms());
    // A bundle with an entity sheet (banner base and two patterns) but no font: banners composite, signs use the canvas path.
    const rects: Record<string, [number, number, number, number]> = { 'entity/banner_base': [0, 0, 64, 64], 'entity/banner/stripe_bottom': [64, 0, 64, 64], 'entity/banner/cross': [0, 64, 64, 64] };
    const L = layer as unknown as Record<string, unknown>;
    L.bundle = { version: '1.12.2', pack: { entity: { rects } } };
    L.pixels = { data: new Uint8ClampedArray(128 * 128 * 4).map((_, i) => (i * 13) & 255), width: 128 };
    L.fontData = null;
    const store = { revision: 1, dim: 0 as Dim, all: (dim: Dim) => (dim === 0 ? BES.slice(0, store.n) : []), n: BES.length };
    const near = { on: true };
    const calls: BlockEntityLayerUpdate['displayMobs'] = {
      showDisplayMob: (...a) => rec('show', a),
      hideDisplayMob: (k) => rec('hide', k),
      spawnerParticles: (...a) => rec('particles', a),
    };
    const objs = L.objs as Map<string, Record<string, unknown> & { group: THREE.Group; slate?: THREE.Mesh; text?: THREE.Mesh }>;
    const centre = new THREE.Vector3();
    let now = 1000;
    const frame = async (label: string, p: Partial<BlockEntityLayerUpdate> & { at: [number, number, number] }, dt = 50) => {
      now += dt;
      centre.set(...p.at);
      log.push(`--- ${label}`);
      layer.update(store, { dim: p.dim ?? 0, centre, software: p.software ?? false, now, ticks: now / 50, displayMobs: p.displayMobs === undefined ? calls : p.displayMobs, playerNear: p.playerNear === undefined ? (_d, x) => near.on && x > -10 : p.playerNear });
      for (const o of objs.values())
        rec('obj', [o.key, o.kind, o.group.visible, !!o.text, o.text?.geometry?.attributes?.position ? 'mesh' : o.text ? 'empty' : null, o.slate?.rotation.x ?? null, o.spin ?? null, o.ticks ?? null, o.usedAt, o.obfuscated ?? null, o.textAt ?? null]);
      rec('count', [layer.count, layer.group.children.length, layer.signDistance, layer.detailDistance, layer.maxTextures]);
      await flush();
    };
    await frame('build', { at: [0, 70, 0] });
    for (let i = 0; i < 6; i++) await frame(`near ${i}`, { at: [i, 70, i] }, 30 + i * 20);
    near.on = false;
    await frame('nobody near', { at: [2, 70, 2] });
    await frame('no hook', { at: [2, 70, 2], playerNear: null as never });
    await frame('software', { at: [10, 70, 0], software: true }, 120);
    await frame('far', { at: [100, 70, 0] });
    await frame('back', { at: [36, 70, 0] }, 200);
    store.revision = 2;
    store.n = 5;
    BES[0] = { ...BES[0], lines: ['Changed'] };
    await frame('rebuild fewer', { at: [0, 70, 0] });
    await frame('after rebuild', { at: [0, 70, 0] });
    await frame('other dim', { at: [0, 70, 0], dim: -1 });
    await frame('no display mobs', { at: [-4, 69, 3], displayMobs: null as never });
    layer.maxTextures = 1;
    await frame('again', { at: [0, 70, 0] });
    const d = new Digest();
    for (const l of log) d.add(l);
    expect(log.filter((l) => l.startsWith('show')).length).toBeGreaterThan(5);
    if (process.env.GOLDEN_DUMP) (await import('node:fs')).writeFileSync(process.env.GOLDEN_DUMP, log.join('\n'));
    expect(d.hex()).toBe('abb4903e88229ff395b79c12a806ee5e');
  });
});
