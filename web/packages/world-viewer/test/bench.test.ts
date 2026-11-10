import { it } from 'vitest';
import { InlineMeshHost } from '../src/mesh/host';
import { createFixtureSource } from '../src/fixtures/world';
import { meshSection } from '../src/mesh/mesher';
import { meshAssets } from '../src/atlas/assets';
import { noPacks, pack1122, packBeta } from './pack';

it.skipIf(!process.env.BENCH || noPacks)('mesher throughput (BENCH=1)', async () => {
  for (const [version, textured] of [['b1.8.1', false], ['b1.8.1', true], ['1.12.2', true]] as const) {
    const src = createFixtureSource({ radius: 2, now: () => 0, version });
    const host = new InlineMeshHost();
    await host.configure(version, textured ? meshAssets(version === '1.12.2' ? pack1122() : packBeta()) : null, textured);
    const cols: Array<[number, number]> = [];
    for (let x = -2; x <= 2; x++) for (let z = -2; z <= 2; z++) cols.push([x, z]);
    let t = performance.now();
    await host.apply(await src.chunks(0, cols));
    process.stderr.write(`${version} ${textured ? 'textured' : 'flat'}: apply 25 cols ${(performance.now() - t).toFixed(1)} ms\n`);
    t = performance.now();
    let q = 0, n = 0;
    for (let r = 0; r < 3; r++)
      for (let x = -1; x <= 1; x++) for (let z = -1; z <= 1; z++) for (let sy = 0; sy < 8; sy++) {
        q += meshSection(host.engine.store, 0, x, sy, z, host.engine.table).quads;
        n++;
      }
    process.stderr.write(`  mesh ${n} sections ${((performance.now() - t) / n).toFixed(2)} ms each, ${q / 3} quads\n`);
  }
});
