// The slice of a bundled pack the mesh worker needs (structured-cloned to it once per configure).

import type { Pack } from '../assets/bundle';
import type { MeshAssets } from '../mesh/source';

export function meshAssets(pack: Pack): MeshAssets {
  if (pack.version === 'b1.8.1') return { kind: 'b1.8.1', info: pack.terrain.info ?? null };
  return {
    kind: '1.12.2',
    columns: pack.blocks.columns,
    rows: pack.blocks.rows,
    tiles: pack.blocks.tiles,
    info: pack.blocks.info ?? {},
    sheets: pack.blocks.sheets ?? {},
    blockstates: pack.blockstates,
    models: pack.models,
    biomes: pack.biomes,
    anims: pack.anims.filter((a) => a.atlas === 'blocks').map((a) => a.cell),
  };
}
