// The per-version block table the worker meshes and queries with: the version's BlockSource (b1.8.1 render types over
// terrain.png, or 1.12.2 blockstates/models from the bundled pack) and the flat per-state lookups the hot loops read
// (culling/lighting flags, "draws nothing"). Without a pack the b1.8.1 shapes are drawn in the flat palette (1.12.2 too, as
// an approximation, until its bundle loads).

import { Shape, type GameVersion } from '../world/blocks';
import { Models1122 } from './models/index';
import { BetaSource, STATES, type BlockSource, type MeshAssets } from './source';

export { STATES };

export interface StateTable {
  version: GameVersion;
  /** Quads carry atlas cells (the shader samples the block atlas); false = flat colours. */
  textured: boolean;
  source: BlockSource;
  /** F_* per state (id << 4 | meta). */
  flags: Uint8Array;
  /** Shape.None for states that draw nothing (air, barriers, moving pistons), Shape.Cube otherwise. */
  shape: Uint8Array;
  /** Atlas grid (cells). */
  cols: number;
  rows: number;
}

const INVISIBLE = new Set([0, 36, 166, 217]);

/** 1.12.2 baked models with that version's assets; otherwise the b1.8.1 render types (textured only with beta assets). */
function sourceFor(version: GameVersion, assets: MeshAssets | null, textured: boolean, fastLeaves: boolean): { source: BlockSource; cols: number; rows: number } {
  if (assets && assets.kind === '1.12.2' && version === '1.12.2') return { source: new Models1122(assets, textured, fastLeaves), cols: assets.columns, rows: assets.rows };
  const beta = assets && assets.kind === 'b1.8.1' ? assets : null;
  return { source: new BetaSource(textured && !!beta, beta ? beta.info : null, fastLeaves), cols: 16, rows: 16 };
}

/** `fastLeaves`: opaque leaves that hide each other (vanilla "fast" graphics), for software rendering. */
export function buildStateTable(version: GameVersion, assets: MeshAssets | null = null, textured = assets != null, fastLeaves = false): StateTable {
  const { source, cols, rows } = sourceFor(version, assets, textured, fastLeaves);
  const shape = new Uint8Array(STATES);
  for (let s = 0; s < STATES; s++) shape[s] = INVISIBLE.has(s >> 4) ? Shape.None : Shape.Cube;
  return { version, textured: source.textured, source, flags: source.flags, shape, cols, rows };
}
