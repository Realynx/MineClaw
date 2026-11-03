// The atlas JSON the C# ViewerBridge emits beside atlas.png (`/v1/world/atlas.json`). Format: ../../ATLAS.md.

/** Render pass of a quad or a whole state (GeoQuad.Pass / RenderPass): opaque, alpha-tested at 0.5, alpha-blended. */
export type AtlasLayer = 'opaque' | 'cutout' | 'translucent';

/**
 * How the viewer builds a state's geometry:
 * - `cube`: a full cube from `faces` (BlockMesh.IsCube: Cube, CutoutCube, TranslucentCube); neighbour culling applies.
 * - `quads`: exactly the listed `quads` (BlockMesh.Quads over a lone block), static.
 * - the procedural names: neighbour-dependent shapes the viewer builds itself, textured from `faces`.
 * - `none`: draws nothing (air, barriers, structure void).
 */
export type AtlasShape = 'cube' | 'quads' | 'fluid' | 'chest' | 'fence' | 'pane' | 'wall' | 'none';

/** Flags (GpuVertex flags): 2 = UVs wrap inside the cell (procedural liquids), 4 = UVs span a 4×2 block of cells (entities). */
export const ATLAS_FLAG_WRAP = 2;
export const ATLAS_FLAG_SPAN = 4;

export type BiomeTintKind = 'grass' | 'foliage' | 'water';

/** One textured face: the atlas cell (column, row) the texture fills, its multiply tint and an optional overlay. */
export interface AtlasFace {
  /** Cell column (0 .. columns-1). */
  c: number;
  /** Cell row (0 .. rows-1). */
  r: number;
  /** 0xRRGGBB multiply tint (grass, leaves, redstone); absent = white. */
  tint?: number;
  /** Second texture drawn over the first where its alpha ≥ 0.5 (1.12.2 grass side fringe), with its own tint. */
  ov?: { c: number; r: number; tint?: number };
  flags?: number;
  /** Swap the tint for the column's biome colour of this kind (only when it is the default/plains tint). */
  biome?: BiomeTintKind;
  /** Per-corner UVs (8 numbers, cell units) for the face's canonical corners (ATLAS.md); absent = the vanilla mapping. Needed
   * when a model rotates a face texture (sideways logs, uvlock). */
  uv?: number[];
}

/** One quad of a `quads` state, block-local (0..1) corners P0..P3 in winding order (triangles 0,1,2 + 0,2,3). */
export interface AtlasQuad {
  /** 12 numbers: x0,y0,z0, x1,y1,z1, x2,y2,z2, x3,y3,z3. */
  p: number[];
  /** 8 numbers: u0,v0 … u3,v3, in cell units (0..1 inside the cell; v down; > 1 only with the wrap/span flags). */
  uv: number[];
  c: number;
  r: number;
  tint?: number;
  ov?: { c: number; r: number; tint?: number };
  flags?: number;
  /** GeoQuad.LightFace: the side whose neighbour cell lights it (0..5), or -1 / absent = the block's own cell. */
  face?: number;
  /** GeoQuad.Shade (face shade, 1 for plants/torches); absent = 1. */
  shade?: number;
  pass?: AtlasLayer;
  /** Visible from both sides (plants, ladders, rails): the viewer emits both windings. */
  twoSided?: boolean;
  /** Constant opacity for translucent quads without texture alpha (portal); absent = from the texel. */
  alpha?: number;
  /** Fallback 0xRRGGBB when the quad has no texture. */
  flat?: number;
  biome?: BiomeTintKind;
  /** No texture (QuadTex.Flat or a missing tile): draw `flat`. */
  untextured?: boolean;
}

export interface AtlasStateEntry {
  shape: AtlasShape;
  layer: AtlasLayer;
  /** Six faces in Minecraft side order: down, up, north (-z), south (+z), west (-x), east (+x). Required except for quads/none. */
  faces?: Array<AtlasFace | null>;
  quads?: AtlasQuad[];
  /** Full brightness (torches, glowstone, lava, sea lanterns). */
  emissive?: boolean;
  /** Fallback 0xRRGGBB for faces without a texture (BlockGeometry.FlatColour of the top). */
  flat?: number;
}

export interface AtlasMap {
  /** Literally "mineclaw-atlas". */
  format: 'mineclaw-atlas';
  /** Format revision; this viewer reads 1. */
  formatVersion: 1;
  /** Game version the textures and states describe ("b1.8.1" | "1.12.2"). */
  version: string;
  /** Pixels per cell (TextureAtlas.Tile; 16 for vanilla). */
  tileSize: number;
  /** Cells per row (GpuAtlas.Cols = 16). */
  columns: number;
  /** Rows in the image (GpuAtlas.RowsFor). */
  rows: number;
  /** Fixed cells of GpuAtlas's extras row, [column, row]. */
  special?: Partial<Record<'white' | 'grassSide' | 'shadow' | 'waterStill' | 'waterFlow' | 'lavaStill' | 'lavaFlow' | 'fireA' | 'fireB', [number, number]>>;
  /** Default tints the states were baked with (plains), so biome swaps know what to replace. */
  defaultTints?: Partial<Record<BiomeTintKind, number>>;
  /** Biome id → colours (1.12.2 BiomeTints); absent = no biome tinting. */
  biomes?: Record<string, Partial<Record<BiomeTintKind, number>>>;
  /** "id" (every meta) or "id:meta" (that meta; wins over "id") → entry. Missing ids use the flat palette. */
  states: Record<string, AtlasStateEntry>;
  /** Item icons for held-item labels: item id (or "id:damage") → cell. Optional. */
  items?: Record<string, [number, number]>;
}
