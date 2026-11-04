// Behaviour pin for the complexity refactor of render/entityLayer.ts (docs/dev/complexity.md, WP12): the exported label
// text and LOD box helpers (entityText, entityLook) over seeded entities of every kind, both versions, and the experience
// orb icon table against the original threshold ladder over every value. The digest was taken from the code before it was
// reshaped.

import { describe, expect, it } from 'vitest';
import { entityLook, entityText, xpOrbIcon } from '../src/render/entityLayer';
import { MOB_NAMES } from '../src/render/entities/registry';
import type { EntityDto } from '../src/types';
import type { EntityView } from '../src/world/entities';
import { Digest, rng } from './digest';

const KINDS = ['item', 'player', 'mob', 'painting', 'item_frame', 'object', 'xp_orb', 'armor_stand', 'falling_block', 'constructor', '__proto__', 'toString'];
const NAMES = [null, 'Steve', 'Zombie', 'minecraft:pig', 'jeb_', '', 'EntityHorse', 'a_b_c'];

describe('entity text goldens', () => {
  it('entityText / entityLook', () => {
    const d = new Digest();
    const r = rng(81);
    const types = Object.keys(MOB_NAMES).map(Number).concat([-1, 0, 999]);
    for (let k = 0; k < 4000; k++) {
      const kind = KINDS[Math.floor(r() * KINDS.length)];
      const dto = {
        kind, variant: r() < 0.5 ? Math.floor(r() * 32) : r() < 0.5 ? 'red,tame,saddle' : null, baby: r() < 0.3,
        motive: r() < 0.5 ? ['Kebab', 'Pool', ''][Math.floor(r() * 3)] : undefined,
        frameItem: r() < 0.5 ? [[1, 0], [276, 0], [397, 3], [0, 0], [9999, 0]][Math.floor(r() * 5)] : undefined,
      } as unknown as EntityDto;
      const e = { key: `0:${k}`, id: k, dim: 0, kind, name: NAMES[Math.floor(r() * NAMES.length)], type: types[Math.floor(r() * types.length)], item: Math.floor(r() * 400), count: [0, 1, 2, 64][Math.floor(r() * 4)], x: 0, y: 0, z: 0, yaw: 0, ageTicks: 0, flags: 0, dto } as EntityView;
      d.add(`${entityText(e)} ${JSON.stringify(entityLook(e))} ${JSON.stringify(entityLook(e, 'b1.8.1'))}`);
    }
    expect(d.hex()).toBe('01c365691ca7fab19485ad98f8098407');
  });

  it('xpOrbIcon equals the getTextureByXP ladder', () => {
    const ladder = (v: number) => (v >= 2477 ? 10 : v >= 1237 ? 9 : v >= 617 ? 8 : v >= 307 ? 7 : v >= 149 ? 6 : v >= 73 ? 5 : v >= 37 ? 4 : v >= 17 ? 3 : v >= 7 ? 2 : v >= 3 ? 1 : 0);
    const values = [NaN, Infinity, -Infinity, -0, 2.5, 2476.9, 1e9];
    for (let v = -5; v <= 3000; v++) values.push(v);
    for (const v of values) expect(xpOrbIcon(v)).toBe(ladder(v));
  });
});
