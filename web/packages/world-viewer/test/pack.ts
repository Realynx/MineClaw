// The committed bundles (generated/<version>/pack.json) for tests, and their textured state tables (built once per file).

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { Pack, Pack1122, PackBeta } from '../src/assets/bundle';
import { meshAssets } from '../src/atlas/assets';
import { buildStateTable, type StateTable } from '../src/mesh/states';

const load = (v: string): Pack => JSON.parse(readFileSync(fileURLToPath(new URL(`../generated/${v}/pack.json`, import.meta.url)), 'utf8'));

let p1122: Pack1122 | null = null, pBeta: PackBeta | null = null;
export const pack1122 = () => (p1122 ??= load('1.12.2') as Pack1122);
export const packBeta = () => (pBeta ??= load('b1.8.1') as PackBeta);

let t1122: StateTable | null = null, tBeta: StateTable | null = null;
/** The bundled 1.12.2 table (textured). */
export const table1122 = () => (t1122 ??= buildStateTable('1.12.2', meshAssets(pack1122()), true));
/** The bundled b1.8.1 table (textured). */
export const tableBeta = () => (tBeta ??= buildStateTable('b1.8.1', meshAssets(packBeta()), true));
