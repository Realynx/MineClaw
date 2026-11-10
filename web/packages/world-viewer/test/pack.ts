// The locally packed bundles (generated/<version>/, never committed: built from your own client jars with
// `npm run assets:extract && npm run assets:pack`) for tests, and their textured state tables (built once per file).
// Suites that need them run under `describe.skipIf(noPacks)` / `it.skipIf(noPacks)` and skip in a clean checkout.

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { Pack, Pack1122, PackBeta } from '../src/assets/bundle';
import { meshAssets } from '../src/atlas/assets';
import { buildStateTable, type StateTable } from '../src/mesh/states';

/** A file of the local pack, e.g. generatedFile('1.12.2', 'pack.json'). */
export const generatedFile = (v: string, file: string) => fileURLToPath(new URL(`../generated/${v}/${file}`, import.meta.url));

/** True when either version's pack.json is missing: the pack-backed suites skip. */
export const noPacks = !existsSync(generatedFile('1.12.2', 'pack.json')) || !existsSync(generatedFile('b1.8.1', 'pack.json'));

const load = (v: string): Pack => {
  if (noPacks) throw new Error(`generated/${v}/pack.json is missing: run npm run assets:extract && npm run assets:pack (from your own jars) first`);
  return JSON.parse(readFileSync(generatedFile(v, 'pack.json'), 'utf8'));
};

let p1122: Pack1122 | null = null, pBeta: PackBeta | null = null;
export const pack1122 = () => (p1122 ??= load('1.12.2') as Pack1122);
export const packBeta = () => (pBeta ??= load('b1.8.1') as PackBeta);

let t1122: StateTable | null = null, tBeta: StateTable | null = null;
/** The bundled 1.12.2 table (textured). */
export const table1122 = () => (t1122 ??= buildStateTable('1.12.2', meshAssets(pack1122()), true));
/** The bundled b1.8.1 table (textured). */
export const tableBeta = () => (tBeta ??= buildStateTable('b1.8.1', meshAssets(packBeta()), true));
