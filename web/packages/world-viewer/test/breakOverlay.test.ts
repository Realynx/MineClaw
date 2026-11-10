import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { AssetBundle, Pack } from '../src/assets/bundle';
import {
  BREAK_POOL,
  BreakOverlay,
  breakCells,
  breakStage,
  crackMasks,
  type BreakEntry,
} from '../src/render/breakOverlay';

const pack1122 = (stages = 10): Pack =>
  ({
    version: '1.12.2',
    tileSize: 16,
    blocks: {
      columns: 32,
      rows: 40,
      tiles: Object.fromEntries(
        Array.from({ length: stages }, (_, n) => [
          `blocks/destroy_stage_${n}`,
          [(30 + n) % 32, 2 + Math.floor((30 + n) / 32)],
        ]),
      ),
      info: {},
      sheets: {},
    },
  }) as unknown as Pack;
const packBeta = { version: 'b1.8.1', tileSize: 16, terrain: { columns: 16, rows: 16 } } as unknown as Pack;
const bundle = (pack: Pack, fallback = false): AssetBundle =>
  ({
    version: pack.version,
    pack,
    fallback,
    blocks: {},
    items: {},
    anim: null,
    entity: {},
    env: {},
  }) as unknown as AssetBundle;
const atlas = (b: AssetBundle) => ({ texture: new THREE.Texture(), bundle: b });
const entry = (x: number, stage: number, more: Partial<BreakEntry> = {}): BreakEntry => ({
  dim: 0,
  x,
  y: 64,
  z: 0,
  stage,
  ...more,
});
const uvs = (m: THREE.Mesh) =>
  Array.from((m.geometry.getAttribute('uv') as THREE.BufferAttribute).array as Float32Array);
const positions = (m: THREE.Mesh) =>
  Array.from((m.geometry.getAttribute('position') as THREE.BufferAttribute).array as Float32Array);

describe('break stage', () => {
  it('shows nothing below 10 % and stages 0 to 9 above', () => {
    expect(breakStage(0)).toBe(-1);
    expect(breakStage(0.09)).toBe(-1);
    expect(breakStage(0.1)).toBe(0);
    expect(breakStage(0.55)).toBe(4);
    expect(breakStage(0.99)).toBe(8);
    expect(breakStage(1)).toBe(9);
    expect(breakStage(1.5)).toBe(9);
    expect(breakStage(NaN)).toBe(-1);
  });
});

describe('crack cells', () => {
  it('finds the ten destroy stages of a 1.12.2 pack', () => {
    const c = breakCells(bundle(pack1122()))!;
    expect(c).toMatchObject({ columns: 32, rows: 40 });
    expect(c.cells[0]).toEqual([30, 2]);
    expect(c.cells[2]).toEqual([0, 3]);
    expect(c.cells[9]).toEqual([7, 3]);
  });

  it('takes b1.8.1 terrain tiles 240 to 249: stage 3 is column 3 of row 15', () => {
    const c = breakCells(bundle(packBeta))!;
    expect(c).toMatchObject({ columns: 16, rows: 16 });
    expect(c.cells[3]).toEqual([3, 15]);
  });

  it('has none for the placeholder bundle or a pack missing a stage', () => {
    expect(breakCells(bundle(pack1122(), true))).toBeNull();
    expect(breakCells(bundle(packBeta, true))).toBeNull();
    expect(breakCells(bundle(pack1122(9)))).toBeNull();
    expect(breakCells(null)).toBeNull();
  });

  it('draws ten procedural masks that only grow from stage to stage', () => {
    const px = crackMasks();
    expect(px.length).toBe(160 * 16 * 4);
    const covered = (n: number) => {
      const out = new Set<number>();
      for (let y = 0; y < 16; y++)
        for (let x = 0; x < 16; x++) if (px[(y * 160 + n * 16 + x) * 4 + 3] > 0) out.add(y * 16 + x);
      return out;
    };
    let before = covered(0);
    expect(before.size).toBeGreaterThan(0);
    for (let n = 1; n < 10; n++) {
      const now = covered(n);
      for (const i of before) expect(now.has(i)).toBe(true);
      expect(now.size).toBeGreaterThanOrEqual(before.size);
      before = now;
    }
    expect(before.size).toBeGreaterThan(covered(0).size);
  });
});

describe('break overlay', () => {
  it('is a ShaderMaterial without a colour-space include, with the crack blend that leaves the canvas alpha alone', () => {
    const o = new BreakOverlay();
    const m = o.material;
    expect(m).toBeInstanceOf(THREE.ShaderMaterial);
    expect(m).not.toBeInstanceOf(THREE.MeshBasicMaterial);
    expect(m.fragmentShader).not.toMatch(/colorspace|#include/i);
    expect(m.vertexShader).not.toMatch(/#include/);
    expect(m.fragmentShader).toMatch(/discard/);
    expect(m).toMatchObject({
      transparent: true,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -4,
      blending: THREE.CustomBlending,
      blendSrc: THREE.DstColorFactor,
      blendDst: THREE.SrcColorFactor,
      blendSrcAlpha: THREE.ZeroFactor,
      blendDstAlpha: THREE.OneFactor,
    });
    o.dispose();
  });

  it('samples the block atlas itself when it has the stages, and the masks when the cell is missing', () => {
    const o = new BreakOverlay();
    const masks = o.texture;
    expect(masks).toBeInstanceOf(THREE.DataTexture);
    const a = atlas(bundle(packBeta));
    o.setAtlas(a);
    expect(o.texture).toBe(a.texture);
    o.setAtlas(atlas(bundle(pack1122(9))));
    expect(o.texture).toBe(masks);
    o.setAtlas(atlas(bundle(packBeta, true)));
    expect(o.texture).toBe(masks);
    o.setAtlas(null);
    expect(o.texture).toBe(masks);
    o.dispose();
  });

  it("bakes the stage's cell into the UVs and redraws only when the crack changed", () => {
    const o = new BreakOverlay();
    o.setAtlas(atlas(bundle(packBeta)));
    o.set('view:1', entry(5, 3));
    o.showFor(0, 1);
    const mesh = o.pool[0];
    expect(mesh.visible).toBe(true);
    expect(mesh.position.toArray()).toEqual([5, 64, 0]);
    const t = uvs(mesh);
    for (let i = 0; i < t.length; i += 2) {
      expect(t[i]).toBeGreaterThan(3 / 16);
      expect(t[i]).toBeLessThan(4 / 16);
      expect(t[i + 1]).toBeGreaterThan(15 / 16);
      expect(t[i + 1]).toBeLessThan(1);
    }
    // The whole cell, a hair larger.
    const p = positions(mesh);
    expect(Math.min(...p)).toBeCloseTo(-0.002);
    expect(Math.max(...p)).toBeCloseTo(1.002);
    const uv = mesh.geometry.getAttribute('uv') as THREE.BufferAttribute;
    const version = uv.version;
    o.showFor(0, 1);
    expect(uv.version).toBe(version);
    o.set('view:1', entry(5, 4));
    o.showFor(0, 1);
    expect(uv.version).toBeGreaterThan(version);
    o.dispose();
  });

  it("crops to the cell's boxes: a bottom slab cracks its lower half", () => {
    const o = new BreakOverlay();
    o.set('view:1', entry(2, 0, { boxes: [{ minX: 2, minY: 64, minZ: 0, maxX: 3, maxY: 64.5, maxZ: 1 }] }));
    o.showFor(0, 1);
    const p = positions(o.pool[0]);
    const ys = p.filter((_, i) => i % 3 === 1);
    expect(Math.max(...ys)).toBeCloseTo(0.502);
    // Side faces (vertices 0-7 and 16-23) sample the lower half of the stage's cell (procedural masks: a 10 × 1 grid).
    const t = uvs(o.pool[0]);
    for (const v of [0, 1, 2, 3, 4, 5, 6, 7, 16, 17, 18, 19, 20, 21, 22, 23]) {
      expect(t[v * 2 + 1]).toBeGreaterThan(0.49);
      expect(t[v * 2]).toBeLessThan(0.1);
    }
    expect(o.shown).toBe(1);
    o.dispose();
  });

  it("hides another dimension's cracks and another view's", () => {
    const o = new BreakOverlay();
    o.set('view:1', entry(0, 2, { view: 1 }));
    o.set('nether', entry(1, 2, { dim: -1 }));
    o.showFor(0, 1);
    expect(o.shown).toBe(1);
    o.showFor(0, 2);
    expect(o.shown).toBe(0);
    o.showFor(-1, 2);
    expect(o.shown).toBe(1);
    o.set('view:1', null);
    o.showFor(0, 1);
    expect(o.shown).toBe(0);
    o.dispose();
  });

  it('ignores a stage outside 0..9 and removes the entry it replaces', () => {
    const o = new BreakOverlay();
    o.set('a', entry(0, 4));
    o.set('a', entry(0, -1));
    expect(o.showFor(0)).toBe(0);
    expect(o.shown).toBe(0);
    expect(o.group.visible).toBe(false);
    o.dispose();
  });

  it('reconcile removes entries absent from the snapshot', () => {
    const o = new BreakOverlay();
    o.reconcile([entry(0, 1), entry(1, 2), entry(2, 3)]);
    o.showFor(0);
    expect(o.shown).toBe(3);
    o.reconcile([entry(1, 2)]);
    o.showFor(0);
    expect(o.shown).toBe(1);
    expect(o.pool[0].position.x).toBe(1);
    o.reconcile(null);
    o.showFor(0);
    expect(o.shown).toBe(0);
    o.dispose();
  });

  it(`shows at most ${BREAK_POOL}`, () => {
    const o = new BreakOverlay();
    for (let i = 0; i < 20; i++) o.set(`k${i}`, entry(i, 5));
    expect(o.showFor(0)).toBe(BREAK_POOL);
    expect(o.shown).toBe(BREAK_POOL);
    expect(o.group.visible).toBe(true);
    o.dispose();
  });
});
