// Behaviour pin for the complexity refactor of render/entityLayer.ts (docs/dev/complexity.md, WP12): the exported label
// text and LOD box helpers (entityText, entityLook) over seeded entities of every kind, both versions. The digest was taken
// from the code before it was reshaped.

import { describe, expect, it } from 'vitest';
import { entityLook, entityText } from '../src/render/entityLayer';
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
});
