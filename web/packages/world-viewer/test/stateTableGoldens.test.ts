// Behaviour pin for the state tables (docs/dev/complexity.md, WP12): every state's flags, world dependence and shape for both
// versions, textured or flat, fancy or fast leaves, hashed in memory.

import { describe, expect, it } from 'vitest';
import { meshAssets } from '../src/atlas/assets';
import { buildStateTable } from '../src/mesh/states';
import { Digest } from './digest';
import { pack1122, packBeta } from './pack';

describe('state table golden', () => {
  it('pins flags, dynamic and shape of every state', () => {
    const d = new Digest();
    const assets = [null, meshAssets(packBeta()), meshAssets(pack1122())];
    for (const version of ['b1.8.1', '1.12.2'] as const)
      for (const a of assets)
        for (const textured of [false, true])
          for (const fast of [false, true]) {
            const t = buildStateTable(version, a, textured, fast);
            d.add(`${version} ${a?.kind ?? 'none'} ${textured} ${fast} ${t.textured} ${t.cols} ${t.rows} ${t.source.version}`);
            d.bytes(t.flags);
            d.bytes(t.shape);
            d.bytes(t.source.dynamic);
          }
    expect(d.hex()).toBe('9d4fed66da2e7b590c49d19c2e453a72');
  });
});
