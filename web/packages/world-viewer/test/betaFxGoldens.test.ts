// Behaviour pin for b1.8.1's procedural textures (docs/dev/complexity.md, WP12): every effect's pixels over 200 ticks after
// its warm-up, hashed in memory.

import { describe, expect, it } from 'vitest';
import { betaEffects } from '../src/atlas/betaFx';
import { Digest } from './digest';

describe('beta texture effects golden', () => {
  it('pins water, lava, fire and portal frames', () => {
    const d = new Digest();
    for (const warmup of [0, 120]) {
      const fx = betaEffects(warmup);
      for (let t = 0; t < 200; t++)
        for (const f of fx) {
          f.tick();
          d.add(String(f.tile));
          d.bytes(f.pixels);
        }
    }
    expect(d.hex()).toBe('ae7d77f6f50a84c9ddfbd5634f0314d8');
  });
});
