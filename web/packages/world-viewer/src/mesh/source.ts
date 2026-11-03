// What the section mesher asks a game version for: per block state (id << 4 | meta) its culling/lighting flags, its quads
// when they depend on nothing but the state (cached), or a builder over the neighbourhood when they do. b1.8.1 is data-free
// code over terrain.png (beta/geometry.ts); 1.12.2 resolves blockstates and bakes models from the bundled pack
// (models/index.ts). Both run in the mesh worker.

import { F_EMISSIVE, F_LAVA, F_LEAVES, F_OPAQUE, F_SELF_CULL, F_WATER, type GameVersion } from '../world/blocks';
import { BetaBuilder, BetaShape, classifyBeta } from './beta/geometry';
import { emissiveBeta } from './beta/textures';
import type { Nb } from './models/states';
import type { Quad } from './quad';

export const STATES = 4096;

export interface BlockSource {
  readonly version: GameVersion;
  readonly textured: boolean;
  /** F_* flags per state (F_OPAQUE: a full opaque cube that hides faces, occludes AO and blocks the x-ray). */
  readonly flags: Uint8Array;
  /** 1 = the state's quads depend on the neighbourhood (call `build`); 0 = `quads(s)` is fixed. */
  readonly dynamic: Uint8Array;
  /** The fixed quads of a state (cached; empty for air/invisible). */
  quads(s: number): Quad[];
  /** The quads of a neighbour-dependent state at nb's origin. */
  build(id: number, meta: number, nb: Nb, out: Quad[]): void;
  /** Colour of a biome tint kind (1 grass, 2 foliage, 3 water) for a biome id (0xRRGGBB). */
  biomeColour(kind: number, biome: number): number;
  /** Atlas cells (row * columns + column) of animated textures, for the per-section `animCells` list. */
  readonly animated: Set<number>;
  readonly columns: number;
}

/** The parts of the bundled b1.8.1 pack the worker needs. */
export interface BetaAssets {
  kind: 'b1.8.1';
  /** Per terrain tile [coverage, semi, average rgb]. */
  info: Array<[number, number, number]> | null;
}

/** The parts of the bundled 1.12.2 pack the worker needs (pack.json: blocks, blockstates, models, biomes, anims). */
export interface Assets1122 {
  kind: '1.12.2';
  columns: number;
  rows: number;
  tiles: Record<string, [number, number]>;
  info: Record<string, [number, number, number]>;
  sheets: Record<string, [number, number, number, number]>;
  blockstates: Record<string, unknown>;
  models: Record<string, unknown>;
  biomes: Record<string, { grass: number | null; foliage: number | null; water?: number | null; name?: string }>;
  /** Animated texture cells. */
  anims: Array<[number, number]>;
}

export type MeshAssets = BetaAssets | Assets1122;

/** The b1.8.1 cells the procedural TextureFX animate (water/lava still + flow, fire, portal). */
export const BETA_ANIMATED = [205, 206, 237, 238, 31, 47, 14];

export class BetaSource implements BlockSource {
  readonly version = 'b1.8.1' as const;
  readonly flags = new Uint8Array(STATES);
  readonly dynamic = new Uint8Array(STATES);
  readonly animated = new Set(BETA_ANIMATED);
  readonly columns = 16;
  private readonly cache: Array<Quad[] | null> = new Array(STATES).fill(null);
  private readonly builder: BetaBuilder;

  constructor(
    readonly textured: boolean,
    info: Array<[number, number, number]> | null,
  ) {
    this.builder = new BetaBuilder(textured, (t) => (info && info[t] ? info[t][2] : null));
    for (let id = 0; id < 256; id++) {
      const shape = classifyBeta(id);
      let f = 0;
      if (shape === BetaShape.Cube) f |= F_OPAQUE;
      if (id === 20 || id === 79) f |= F_SELF_CULL;
      if (emissiveBeta(id)) f |= F_EMISSIVE;
      if (id === 8 || id === 9) f |= F_WATER;
      if (id === 10 || id === 11) f |= F_LAVA;
      if (id === 18) f |= F_LEAVES;
      // Neighbour-dependent: chests (halves), fluids, fences/panes, wire, tall grass jitter, portals.
      const dyn = [54, 8, 9, 10, 11, 85, 101, 102, 55, 31, 90].includes(id) ? 1 : 0;
      for (let m = 0; m < 16; m++) {
        this.flags[(id << 4) | m] = f;
        this.dynamic[(id << 4) | m] = dyn;
      }
    }
  }

  quads(s: number): Quad[] {
    let q = this.cache[s];
    if (!q) {
      q = [];
      const id = s >> 4;
      if (id !== 0) this.builder.build(id, s & 15, NO_NB, q);
      this.cache[s] = q;
    }
    return q;
  }

  build(id: number, meta: number, nb: Nb, out: Quad[]): void {
    this.builder.build(id, meta, nb, out);
  }

  biomeColour(): number {
    return 0xffffff;
  }
}

/** A neighbourhood of air (static states never read it). */
export const NO_NB: Nb = { id: () => 0, meta: () => 0, wx: 0, wy: 0, wz: 0 };
