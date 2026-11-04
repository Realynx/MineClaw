// Behaviour pin for the complexity refactor of render/sky.ts and starfield.starPasses (docs/dev/complexity.md, WP12): the
// sky layer with bundles of each shape (both versions, with and without moon phases, sun or clouds, none) placed for seeded
// cameras, times, rain, dimensions and cloud settings; every object's and material's state is hashed. starPasses is
// compared with the original ternary ladder over every integer distance and the edge values. The digest was taken from the
// code before it was reshaped.

import * as THREE from 'three';
import { describe, expect, it } from 'vitest';
import type { AssetBundle } from '../src/assets/bundle';
import { SkyLayer } from '../src/render/sky';
import { starPasses } from '../src/render/starfield';
import { Digest, rng } from './digest';

const img = (tag: string) => ({ tag, width: 16, height: 16 }) as unknown as ImageBitmap;
const BUNDLES: Array<AssetBundle | null> = [
  { version: '1.12.2', env: { sun: img('sun'), moon_phases: img('phases'), clouds: img('clouds') } },
  { version: 'b1.8.1', env: { sun: img('sun'), moon: img('moon'), clouds: img('clouds') } },
  { version: '1.12.2', env: { moon: img('moon') } },
  { version: 'b1.8.1', env: { sun: img('sun'), moon_phases: img('phases'), moon: img('moon') } },
  { version: '1.12.2', env: {} },
  null,
] as unknown as Array<AssetBundle | null>;

const n = (v: number) => (Object.is(v, -0) ? '-0' : String(v));
function state(d: Digest, sky: SkyLayer): void {
  d.add(String(sky.group.visible));
  for (const o of sky.group.children as Array<THREE.Mesh | THREE.Points>) {
    const m = o.material as THREE.MeshBasicMaterial;
    d.add(
      [o.visible, o.renderOrder, ...o.position.toArray(), ...o.scale.toArray(), ...o.quaternion.toArray(), ...o.rotation.toArray().slice(0, 3), m.opacity, m.version, m.color.getHexString(), ...m.color.toArray()]
        .map((v) => (typeof v === 'number' ? n(v) : String(v)))
        .join(','),
    );
    const t = m.map;
    d.add(t ? [(t.image as { tag: string }).tag, t.wrapS, t.wrapT, ...t.repeat.toArray(), ...t.offset.toArray(), t.magFilter, t.flipY, t.version].join(',') : 'nomap');
  }
}

describe('sky goldens', () => {
  it('SkyLayer.setBundle / update', () => {
    const d = new Digest();
    const r = rng(41);
    const sky = new SkyLayer();
    state(d, sky);
    for (let k = 0; k < 600; k++) {
      if (k % 50 === 0) {
        sky.setBundle(BUNDLES[(k / 50) % BUNDLES.length]);
        state(d, sky);
      }
      const cam = new THREE.Vector3((r() - 0.5) * 1000, r() < 0.2 ? 200 + r() * 100 : 40 + r() * 80, (r() - 0.5) * 1000);
      const time = r() < 0.15 ? null : Math.floor(r() * 24000 * 20);
      const rain = r() < 0.5 ? 0 : r() * 1.4 - 0.2;
      sky.update(cam, time, rain, r() < 0.1, r() < 0.8, Math.floor(r() * 1e7), r() < 0.5 ? undefined : 50 + r() * 300);
      state(d, sky);
    }
    sky.dispose();
    expect(d.hex()).toBe('959e5dbd3bc305134c7c588a01462006');
  });

  it('starPasses equals the ladder', () => {
    const ladder = (d2: number, gateway = false) => {
      const i = d2 > 36864 ? 1 : d2 > 25600 ? 3 : d2 > 16384 ? 5 : d2 > 9216 ? 7 : d2 > 4096 ? 9 : d2 > 1024 ? 11 : d2 > 576 ? 13 : d2 > 256 ? 14 : 15;
      return i + 1 + (gateway ? 1 : 0);
    };
    const values = [NaN, Infinity, -Infinity, -1, -0, 0.5, 256.0001, 576.5, 36864.25, 1e9];
    for (let v = 0; v <= 40000; v++) values.push(v);
    for (const v of values) {
      expect(starPasses(v)).toBe(ladder(v));
      expect(starPasses(v, false)).toBe(ladder(v, false));
      expect(starPasses(v, true)).toBe(ladder(v, true));
    }
  });
});
