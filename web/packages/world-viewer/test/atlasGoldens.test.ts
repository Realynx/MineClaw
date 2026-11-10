// Behaviour pin for the complexity refactor of render/blockAtlas.ts (docs/dev/complexity.md, WP12): BlockAtlas.update for
// both versions (1.12.2 sprite strips from the bundled anim.png through a stand-in canvas; b1.8.1 procedural effects) over
// seeded frame times, animation rates, budgets and visibility sets, against a recording renderer: every cell copy (source,
// region, target, pixels) and the time uniforms are hashed. The digest was taken from the code before it was reshaped.

import { existsSync, readFileSync } from 'node:fs';
import { PNG } from 'pngjs';
import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { AssetBundle, Pack } from '../src/assets/bundle';
import { BlockAtlas } from '../src/render/blockAtlas';
import { createChunkUniforms } from '../src/render/chunkMaterial';
import { Digest, rng } from './digest';
import { generatedFile, noPacks, pack1122, packBeta } from './pack';

const animPng = generatedFile('1.12.2', 'anim.png');
const anim = existsSync(animPng) ? PNG.sync.read(readFileSync(animPng)) : null!;

describe.skipIf(noPacks || !anim)('block atlas goldens', () => {
  const g = globalThis as unknown as { OffscreenCanvas?: unknown };
  let had: unknown;
  beforeAll(() => {
    had = g.OffscreenCanvas;
    g.OffscreenCanvas = class {
      getContext() {
        return { drawImage: () => {}, getImageData: () => ({ data: new Uint8ClampedArray(anim.data.buffer, anim.data.byteOffset, anim.data.byteLength) }) };
      }
    };
  });
  afterAll(() => {
    g.OffscreenCanvas = had;
  });

  it('update over both versions', () => {
    const d = new Digest();
    for (const pack of [pack1122(), packBeta()] as Pack[]) {
      const r = rng(pack.version === '1.12.2' ? 61 : 62);
      const bundle = { version: pack.version, pack, blocks: { width: 1, height: 1 }, anim: { width: anim.width, height: anim.height } } as unknown as AssetBundle;
      const atlas = new BlockAtlas(bundle);
      const names = new Map<unknown, string>([[atlas.texture, 'atlas']]);
      const renderer = {
        copyTextureToTexture: (src: THREE.DataTexture, dst: THREE.Texture, region: THREE.Box2 | null, at: THREE.Vector2) => {
          if (!names.has(src)) names.set(src, `tex${names.size}`);
          d.add(`copy ${names.get(src)} -> ${names.get(dst)} ${region ? [region.min.toArray(), region.max.toArray()] : 'null'} ${at.toArray()}`);
          if (names.get(src) !== 'tex1' || pack.version !== '1.12.2') d.bytes(src.image.data as Uint8Array);
        },
      } as unknown as THREE.WebGLRenderer;
      const cells = atlas.animatedCells();
      d.add(cells.join(','));
      const u = createChunkUniforms();
      let now = r() * 1000;
      for (let k = 0; k < 400; k++) {
        if (k === 50) atlas.bind(u);
        if (k % 40 === 0) {
          atlas.animHz = [20, 10, 0, 5, 30, 1][Math.floor(r() * 6)];
          atlas.animCellsPerTick = [0, 0, 1, 3, 100][Math.floor(r() * 5)];
          atlas.setVisible(r() < 0.5 ? null : cells.filter(() => r() < 0.5));
          atlas.starLayers = 5 + Math.floor(r() * 12);
        }
        now += r() < 0.1 ? 0 : r() < 0.1 ? 400 + r() * 300 : r() * 70;
        atlas.update(renderer, now);
        d.add(`${atlas.writes} ${u.uTime.value} ${u.uStarLayers.value}`);
      }
      atlas.dispose();
    }
    expect(d.hex()).toBe('6ae5a8b7a7c99cba674014eb65f22ded');
  });
});
