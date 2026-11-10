// Behaviour pin for the state tables (docs/dev/complexity.md, WP12): every state's flags, world dependence and shape for both
// versions, textured or flat, fancy or fast leaves, hashed in memory.

import { describe, expect, it } from 'vitest';
import { meshAssets } from '../src/atlas/assets';
import { buildStateTable } from '../src/mesh/states';
import { Digest } from './digest';
import { noPacks, pack1122, packBeta } from './pack';

describe.skipIf(noPacks)('state table golden', () => {
  it('pins flags, dynamic and shape of every state', () => {
    const d = new Digest();
    const beforeAnimatedEnderChest = new Digest();
    const assets = [null, meshAssets(packBeta()), meshAssets(pack1122())];
    for (const version of ['b1.8.1', '1.12.2'] as const)
      for (const a of assets)
        for (const textured of [false, true])
          for (const fast of [false, true]) {
            const t = buildStateTable(version, a, textured, fast);
            const header = `${version} ${a?.kind ?? 'none'} ${textured} ${fast} ${t.textured} ${t.cols} ${t.rows} ${t.source.version}`;
            d.add(header);
            d.bytes(t.flags);
            d.bytes(t.shape);
            d.bytes(t.source.dynamic);
            const previousDynamic = t.source.dynamic.slice();
            if (version === '1.12.2' && a?.kind === '1.12.2') {
              // Ender chests now consult their block entity to separate the animated lid from the chunk mesh.
              expect([...previousDynamic.slice(130 << 4, 131 << 4)]).toEqual(Array(16).fill(1));
              previousDynamic.fill(0, 130 << 4, 131 << 4);
            }
            beforeAnimatedEnderChest.add(header);
            beforeAnimatedEnderChest.bytes(t.flags);
            beforeAnimatedEnderChest.bytes(t.shape);
            beforeAnimatedEnderChest.bytes(previousDynamic);
          }
    // Pin the exact migration: no other dynamic bit, culling flag or shape changed.
    expect(beforeAnimatedEnderChest.hex()).toBe('9d4fed66da2e7b590c49d19c2e453a72');
    expect(d.hex()).toBe('e9f4cefe5eea2c1cd1f70198323f8ad0');
  });
});
