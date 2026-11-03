// One flat lookup per block state (id << 4 | meta) for the mesher's hot loop, resolved from the atlas JSON where it lists the
// state and from the flat palette (world/blocks.ts) everywhere else.

import type { AtlasMap, AtlasQuad, AtlasShape, AtlasStateEntry, BiomeTintKind } from '../atlas/types';
import { F_BIOME_FOLIAGE, F_BIOME_GRASS, F_EMISSIVE, F_LAVA, F_LEAVES, F_OPAQUE, F_SELF_CULL, F_WATER, Layer, Shape, flatColour, tableFor, type GameVersion } from '../world/blocks';

export const STATES = 4096;

export interface ResolvedFace {
  c: number;
  r: number;
  /** 0xRRGGBB multiply. */
  tint: number;
  /** Overlay cell or -1. */
  ovC: number;
  ovR: number;
  ovTint: number;
  /** 2 wrap, 4 span. */
  flags: number;
  /** 0 none, 1 grass, 2 foliage, 3 water. */
  biome: number;
  uv?: number[];
}

export interface StateTable {
  version: GameVersion;
  textured: boolean;
  /** Atlas grid (cells); 16×1 without an atlas. */
  cols: number;
  rows: number;
  shape: Uint8Array;
  layer: Uint8Array;
  flags: Uint8Array;
  top: Uint32Array;
  side: Uint32Array;
  alpha: Float32Array;
  faces: Array<ResolvedFace[] | null>;
  quads: Array<AtlasQuad[] | null>;
  /** Biome id → [grass, foliage, water] (0xRRGGBB, -1 = keep). */
  biomes: Map<number, [number, number, number]>;
  defaultTints: [number, number, number];
}

const SHAPES: Record<AtlasShape, Shape> = {
  cube: Shape.Cube,
  quads: Shape.Quads,
  fluid: Shape.Fluid,
  chest: Shape.Chest,
  fence: Shape.Fence,
  pane: Shape.Pane,
  wall: Shape.Wall,
  none: Shape.None,
};

const LAYERS = { opaque: Layer.Opaque, cutout: Layer.Cutout, translucent: Layer.Translucent } as const;
const BIOME: Record<BiomeTintKind, number> = { grass: 1, foliage: 2, water: 3 };

function face(f: NonNullable<AtlasStateEntry['faces']>[number]): ResolvedFace | null {
  if (!f) return null;
  return {
    c: f.c,
    r: f.r,
    tint: f.tint ?? 0xffffff,
    ovC: f.ov ? f.ov.c : -1,
    ovR: f.ov ? f.ov.r : -1,
    ovTint: f.ov?.tint ?? 0xffffff,
    flags: f.flags ?? 0,
    biome: f.biome ? BIOME[f.biome] : 0,
    uv: f.uv && f.uv.length === 8 ? f.uv : undefined,
  };
}

export function buildStateTable(version: GameVersion, atlas: AtlasMap | null): StateTable {
  const t: StateTable = {
    version,
    textured: !!atlas,
    cols: atlas?.columns ?? 16,
    rows: atlas?.rows ?? 1,
    shape: new Uint8Array(STATES),
    layer: new Uint8Array(STATES),
    flags: new Uint8Array(STATES),
    top: new Uint32Array(STATES),
    side: new Uint32Array(STATES),
    alpha: new Float32Array(STATES),
    faces: new Array(STATES).fill(null),
    quads: new Array(STATES).fill(null),
    biomes: new Map(),
    defaultTints: [atlas?.defaultTints?.grass ?? 0x91bd59, atlas?.defaultTints?.foliage ?? 0x77ab2f, atlas?.defaultTints?.water ?? 0xffffff],
  };
  const table = tableFor(version);
  for (let id = 0; id < 256; id++) {
    const d = table[id];
    for (let meta = 0; meta < 16; meta++) {
      const s = (id << 4) | meta;
      if (!d) continue;
      t.shape[s] = d.shape;
      t.layer[s] = d.layer;
      t.flags[s] = d.flags;
      t.top[s] = flatColour(version, id, meta, true);
      t.side[s] = flatColour(version, id, meta, false);
      t.alpha[s] = d.alpha;
      if (!atlas) continue;
      const e = atlas.states[`${id}:${meta}`] ?? atlas.states[`${id}`];
      if (!e) continue;
      const shape = SHAPES[e.shape] ?? Shape.Cube;
      // Neighbour-dependent palette shapes the atlas can only describe statically stay procedural, textured from its faces.
      const keepProcedural = e.shape === 'quads' && e.faces && isProcedural(d.shape);
      t.shape[s] = keepProcedural ? d.shape : shape;
      t.layer[s] = LAYERS[e.layer] ?? Layer.Opaque;
      let flags = d.flags & (F_WATER | F_LAVA | F_LEAVES | F_BIOME_GRASS | F_BIOME_FOLIAGE);
      if (shape === Shape.Cube && t.layer[s] === Layer.Opaque) flags |= F_OPAQUE;
      if (e.emissive) flags |= F_EMISSIVE;
      // Glass, ice and fluids skip faces against themselves (BlockBreakable); fancy leaves draw every face.
      if ((shape === Shape.Cube && t.layer[s] !== Layer.Opaque && !(d.flags & F_LEAVES)) || shape === Shape.Fluid) flags |= F_SELF_CULL;
      t.flags[s] = flags;
      if (e.flat != null) t.top[s] = t.side[s] = e.flat;
      if (e.faces) t.faces[s] = e.faces.map(face) as ResolvedFace[];
      if (e.quads && !keepProcedural) t.quads[s] = e.quads;
      t.alpha[s] = 1;
    }
  }
  if (atlas?.biomes)
    for (const [k, v] of Object.entries(atlas.biomes)) t.biomes.set(+k, [v.grass ?? -1, v.foliage ?? -1, v.water ?? -1]);
  return t;
}

/** Shapes whose look depends on neighbours (connections, pairs, fluid levels) or on metadata the viewer resolves itself. */
function isProcedural(s: Shape): boolean {
  return s === Shape.Fence || s === Shape.Pane || s === Shape.Wall || s === Shape.Chest || s === Shape.Fluid;
}
