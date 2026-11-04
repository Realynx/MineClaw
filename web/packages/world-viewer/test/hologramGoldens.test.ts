// Behaviour pin for the complexity refactor of render/holograms.ts (docs/dev/complexity.md, WP12): seeded build queues and
// placement previews through a HologramLayer whose stand-in mesh host answers deterministically (some cells left out as
// built, blocked placement cells, a failing round trip now and then). The requests, the groups' objects (names, positions,
// geometry), the stats and the shown-cell lookups are hashed after every step; the digest was taken from the code before it
// was reshaped.

import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { HoloRequest, HoloResult } from '../src/mesh/holo';
import type { MeshHost } from '../src/mesh/host';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import { HOLO_STATES, HologramLayer, type HoloCell } from '../src/render/holograms';
import { Digest, rng } from './digest';

function fakeHost(d: Digest, r: () => number): MeshHost {
  const mesh = (blocks: number[]) => {
    const n = Math.max(1, blocks.length / 5);
    const f = (k: number) => Float32Array.from({ length: n * k }, (_, i) => (blocks[i % blocks.length] ?? 0) + i * 0.25);
    return { origin: [blocks[0] ?? 0, blocks[1] ?? 0, blocks[2] ?? 0] as [number, number, number], pos: f(3), uv: f(2), tile: Uint16Array.from({ length: n * 4 }, (_, i) => i), color: Uint8Array.from({ length: n * 4 }, (_, i) => i * 7), cell: f(3), index: Uint32Array.from({ length: n }, (_, i) => i), quads: n };
  };
  const keep = (blocks: number[], p: number) => {
    const out: number[] = [];
    for (let i = 0; i + 4 < blocks.length; i += 5) if (r() < p) out.push(...blocks.slice(i, i + 5));
    return out;
  };
  return {
    holo: async (dim: number, reqs: HoloRequest[]): Promise<HoloResult[]> => {
      d.add(`holo ${dim} ${JSON.stringify(reqs)}`);
      await Promise.resolve();
      if (r() < 0.08) throw new Error('worker gone');
      const out: HoloResult[] = [];
      for (const q of reqs) {
        if (q.mode === 'place') {
          const ok = keep(q.blocks, 0.7), bad = keep(q.blocks, 0.3);
          out.push({ key: `${q.key}:ok`, mesh: ok.length && r() < 0.9 ? mesh(ok) : null, cells: ok });
          if (bad.length || r() < 0.5) out.push({ key: `${q.key}:blocked`, mesh: bad.length ? mesh(bad) : null, cells: bad });
          continue;
        }
        const cells = keep(q.blocks, 0.75);
        out.push({ key: q.key, mesh: cells.length && q.mode !== 'dig' && r() < 0.9 ? mesh(cells) : null, cells });
      }
      return out;
    },
  } as unknown as MeshHost;
}

function geometryHash(d: Digest, g: THREE.BufferGeometry): void {
  for (const name of Object.keys(g.attributes).sort()) d.bytes((g.attributes[name] as THREE.BufferAttribute).array as Float32Array);
  d.bytes((g.index?.array as Uint32Array | undefined) ?? null);
  d.add(String(g.boundingSphere ? [g.boundingSphere.center.toArray(), g.boundingSphere.radius] : 'nosphere'));
}

function layerHash(d: Digest, layer: HologramLayer, probes: number[][]): void {
  d.add(JSON.stringify(layer.stats()));
  layer.group.traverse((o) => {
    const m = o as THREE.Mesh;
    d.add(`${o.type} ${o.name} ${o.visible} ${o.renderOrder} ${o.position.toArray()} ${o.children.length} ${o.frustumCulled}`);
    if (m.geometry) geometryHash(d, m.geometry);
    if (m.material) d.add(String((m.material as THREE.ShaderMaterial).uniforms?.uOpacity?.value));
  });
  d.add(probes.map(([x, y, z]) => `${layer.has(x, y, z)}${layer.has(x, y, z, 'dig')}${layer.has(x, y, z, 'block')}`).join(''));
}

describe('hologram goldens', () => {
  it('queues, digs, boxes and previews', async () => {
    const d = new Digest();
    const r = rng(51);
    const layer = new HologramLayer(fakeHost(d, rng(52)), createChunkUniforms());
    const probes: number[][] = [];
    for (let k = 0; k < 60; k++) probes.push([Math.floor(r() * 12), 60 + Math.floor(r() * 6), Math.floor(r() * 12)]);
    for (let step = 0; step < 120; step++) {
      const cells: HoloCell[] = [];
      const builds = 1 + Math.floor(r() * 4);
      for (let j = Math.floor(r() * 40); j > 0; j--)
        cells.push({ x: Math.floor(r() * 12), y: 60 + Math.floor(r() * 6), z: Math.floor(r() * 12), id: r() < 0.25 ? 0 : 1 + Math.floor(r() * 100), meta: Math.floor(r() * 16), build: r() < 0.5 ? `b${Math.floor(r() * builds)}` : Math.floor(r() * builds), state: HOLO_STATES[Math.floor(r() * 4)] });
      const op = r();
      if (op < 0.4) d.add(`set ${layer.set(cells, `r${Math.floor(r() * 30)}`)}`);
      else if (op < 0.65) {
        const blocks: number[] = [];
        for (const c of cells) blocks.push(c.x, c.y, c.z, c.id, c.meta);
        layer.setPreview(r() < 0.15 ? null : blocks, r() < 0.3 ? 'same' : '');
      } else if (op < 0.75) layer.blocksChanged(r() < 0.8 ? 0 : 1, probes.slice(0, Math.floor(r() * 10)).map((p) => [p[0], p[1], p[2]] as const));
      else if (op < 0.8) layer.refresh();
      else if (op < 0.83) layer.setDim(Math.floor(r() * 2));
      layer.show(r() < 0.3 ? null : r() * 6);
      await layer.idle;
      for (let t = 0; t < 6; t++) await Promise.resolve();
      layerHash(d, layer, probes);
    }
    layer.dispose();
    layerHash(d, layer, probes);
    expect(d.hex()).toBe('3ab4f135a2ce252f508105e72995f67d');
  });
});
