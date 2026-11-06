// Behaviour pin for the flat palette (docs/dev/complexity.md, WP12): flatColour over every version × id × meta × face, hashed
// in memory. Generated from the switch before it became a per-id table.

import { describe, expect, it } from 'vitest';
import { flatColour, type GameVersion } from '../src/world/blocks';
import { emission, opacity } from '../src/mesh/light';
import { Digest } from './digest';

describe('flat colour golden', () => {
  it('pins every state', () => {
    const d = new Digest();
    for (const v of ['b1.8.1', '1.12.2'] as GameVersion[])
      for (let id = 0; id < 256; id++) {
        const row: number[] = [];
        for (let meta = 0; meta < 16; meta++) row.push(flatColour(v, id, meta, true), flatColour(v, id, meta, false));
        d.add(`${v} ${id} ${row.join(',')} ${opacity(v, id)} ${emission(id)}`);
      }
    // 11-05: opacity follows vanilla (lava, farmland, slabs, stairs, grass path 15; pistons and 1.12.2 cauldron, anvil, hopper,
    // chorus, end gateway, shulker boxes 0; b1.8.1 locked chest 15). Was 52909cc4c97d027c2b6a29961a42d9cf.
    expect(d.hex()).toBe('9d670bb7423b308047f00b69b40b48cd');
  });
});
