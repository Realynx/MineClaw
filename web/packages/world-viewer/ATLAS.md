# World viewer atlas: `atlas.png` + `atlas.json`

The browser viewer textures blocks from one image and one JSON map that the ViewerBridge builds at runtime from
`MineClaw.Render` (`/v1/world/atlas.png`, `/v1/world/atlas.json`). Nothing is committed: the textures come from the client
jar on the bridge host exactly as they do for the C# renderers. Without an atlas, or for any state the map does not list, the
viewer draws its flat-colour palette (`src/world/blocks.ts`). TypeScript types: `src/atlas/types.ts`.

The format is the C# GPU freecam's own data, written out as it already is: the image is `GpuAtlas.Build(atlas)`, every cell
is `QuadEmit.Cell(...)` of the quad the freecam would draw, and every shape decision is `BlockMesh.Shape`. The bridge emits
what the freecam already computes. It does not re-derive anything.

## The image

`GpuAtlas.Build(TextureAtlas?)` from `tools/MemoryExplorer/LiveHive/Gpu/GpuMesh.cs` (move it into `MineClaw.Render` or
reference it from the bridge), encoded as PNG with straight (non-premultiplied) RGBA:

- a grid of `columns` (16) cells of `tileSize` px (`TextureAtlas.Tile`, 16 for vanilla)
- rows 0..15: `terrain.png`
- rows 16..31: `gui/items.png`
- row 32: the extras (white, the grass side with its fringe baked in, the shadow, the procedural water and lava still/flow,
  fire A/B)
- then the entity textures
- for 1.12.2, from row `GpuAtlas.BlockRow` on: the model textures, 16 per row

The viewer samples it nearest-neighbour with no mipmaps and no flip: row 0 is the top of the image, and v grows downwards as
in `GeoQuad` UVs.

## The JSON

```jsonc
{
  "format": "mineclaw-atlas",
  "formatVersion": 1,
  "version": "1.12.2",              // the version the states describe; must match the hive's (index `version`)
  "tileSize": 16,
  "columns": 16,
  "rows": 71,                       // GpuAtlas.RowsFor(atlas)
  "special": { "white": [0, 32], "grassSide": [1, 32], "shadow": [2, 32], "waterStill": [3, 32], "waterFlow": [4, 32],
               "lavaStill": [5, 32], "lavaFlow": [6, 32], "fireA": [7, 32], "fireB": [8, 32] },
  "defaultTints": { "grass": 9551193, "foliage": 7842607, "water": 4159204 },   // what states were baked with (plains)
  "biomes": { "1": { "grass": 9551193, "foliage": 7842607, "water": 4159204 }, "...": {} },  // 1.12.2 only (BiomeTints)
  "states": {
    "1":    { "shape": "cube", "layer": "opaque", "faces": [F, F, F, F, F, F] },   // every meta of id 1
    "17:4": { "shape": "cube", "layer": "opaque", "faces": [...] },               // one meta; wins over "17"
    "8":    { "shape": "fluid", "layer": "translucent", "faces": [...] },
    "50:5": { "shape": "quads", "layer": "cutout", "emissive": true, "quads": [Q, Q, ...] }
  },
  "items": { "4": [4, 16] }        // optional: item id (or "id:damage") → icon cell
}
```

Keys of `states` are `"id"` (it applies to all 16 metas) or `"id:meta"`. When the 16 metas produce identical JSON, emit `"id"`
once. Leave out ids that are air or that the version does not have.

### `shape`

| value   | when (C#)                                                   | the viewer builds                                       |
|---------|-------------------------------------------------------------|---------------------------------------------------------|
| `cube`  | `BlockMesh.IsCube(BlockMesh.Shape(models, id, meta))`        | six faces from `faces`, culled against opaque neighbours |
| `fluid` | ids 8–11                                                    | its own fluid (level from meta), textured from `faces`  |
| `chest` | 54, 146 (1.12.2), 130; b1.8.1: 54, 95                       | its own chest box; adjacent chests pair (ChestPairs)    |
| `fence` | 85, 113, 188–192                                           | post plus connections to neighbours, textured from `faces` |
| `pane`  | 101, 102, 160                                               | connected pane, textured from `faces`                   |
| `wall`  | 139                                                         | connected wall, textured from `faces`                   |
| `quads` | every other non-air state                                   | exactly `quads`                                          |
| `none`  | invisible states (barrier 166, structure void 217)          | nothing                                                  |

The procedural shapes are the ones whose look depends on neighbours (connections, pairs, fluid levels). The bridge only has a
lone block to build from, so for these the viewer does the neighbour logic and takes only textures from the bridge.

### `layer`

`RenderPass` of the state: `opaque`, `cutout` (alpha-tested at 0.5) or `translucent` (blended). For cubes:

| `BlockShape`      | `layer`       |
|-------------------|---------------|
| `Cube`            | `opaque`      |
| `CutoutCube`      | `cutout`      |
| `TranslucentCube` | `translucent` |

For quads, give `pass` per quad and `layer` = the most common pass. An `opaque` cube hides its neighbours' faces and occludes
ambient occlusion.

### `faces` (`F`)

Six entries in Minecraft side order: down, up, north (−z), south (+z), west (−x), east (+x). This is the C# `Face` enum
order.

```jsonc
{ "c": 3, "r": 0,                    // atlas cell (column, row): QuadEmit.Cell's col/row
  "tint": 9551193,                   // 0xRRGGBB multiply (QuadEmit.Cell's tint; omit for white)
  "ov": { "c": 6, "r": 74, "tint": 9551193 },   // 1.12.2 overlay (GeoQuad.Overlay/OverlayTint as a Block cell), alpha-tested over the base
  "flags": 2,                        // GpuVertex flags: 2 = UVs wrap (procedural liquids), 4 = UVs span 4×2 cells (entities)
  "biome": "grass",                  // swap the tint for the column's biome colour (only when it equals defaultTints)
  "uv": [0,0, 0,1, 1,1, 1,0] }       // optional per-corner UVs (cell units) for the canonical corners below
```

`null` means the face has no texture: draw the palette colour.

Per face `f` of a cube state, build the quad with `models?.CubeFace(w, 0, 64, 0, id, meta, f)` (1.12.2) or
`BlockMesh.CubeFace(w, 0, 64, 0, id, meta, f)` (b1.8.1). `w` is a lone-block `IVoxelWorld`: the block at (0, 64, 0), air
around it, sky 15. Then:

- `QuadEmit.Cell(q, textured: true, out col, out row, out tint, out uvScale, out flags)` gives `c`, `r`, `tint` and
  `flags`.
- If it returns false, the face is `null`.
- `ov`: for a `QuadTex.Block` quad with `Overlay >= 0`, `c = Overlay % 16`, `r = GpuAtlas.BlockRow + Overlay / 16`,
  `tint = OverlayTint`. b1.8.1's grass side needs no overlay, because `Cell` already maps it to the baked `grassSide` cell.
- `biome` is the kind that `BlockModels1122.BiomeTinted(id)` covers (grass 2, 31, 175, 83; foliage 18, 161, 106).
- `uv`: the model may rotate a face's texture (sideways logs, uvlock). When `q.T0..T3` differ from the vanilla mapping
  (`BlockGeometry.FaceUv` of the corner positions), emit `uv`: for each canonical corner below, find the `GeoQuad` corner
  at the same position and write its `T × uvScale`.

Canonical corners per face (block-local, triangles 0-1-2 and 0-2-3, counter-clockwise from outside; these are the corners
`BlockMesh.CubeFace` uses):

| face  | corners                                  |
|-------|------------------------------------------|
| down  | (0,0,1) (0,0,0) (1,0,0) (1,0,1)          |
| up    | (0,1,0) (0,1,1) (1,1,1) (1,1,0)          |
| north | (1,1,0) (1,0,0) (0,0,0) (0,1,0)          |
| south | (0,1,1) (0,0,1) (1,0,1) (1,1,1)          |
| west  | (0,1,0) (0,0,0) (0,0,1) (0,1,1)          |
| east  | (1,1,1) (1,0,1) (1,0,0) (1,1,0)          |

For `fluid`, `chest`, `fence`, `pane` and `wall`, fill `faces` from the quads `BlockMesh.Quads` builds for the lone block,
taking the first quad whose `LightFace == f`, or else any quad. For fluids, put the still texture on up/down and the flowing
texture on the sides (b1.8.1: the `waterStill`/`waterFlow`/`lavaStill`/`lavaFlow` cells with flag 2).

### `quads` (`Q`)

`BlockMesh.Quads(models, w, 0, 64, 0, list)` on the lone-block world, then one entry per `GeoQuad` (positions block-local):

```jsonc
{ "p": [x0,y0,z0, x1,y1,z1, x2,y2,z2, x3,y3,z3],   // P0..P3
  "uv": [u0,v0, u1,v1, u2,v2, u3,v3],               // T0..T3 × uvScale (cell units)
  "c": 13, "r": 0, "tint": 9551193, "flags": 0,      // QuadEmit.Cell
  "ov": { "c": 0, "r": 0, "tint": 0 },              // as for faces (1.12.2 Block quads with an overlay)
  "face": -1,                                        // GeoQuad.LightFace (−1 = the block's own cell)
  "shade": 0.8,                                      // GeoQuad.Shade
  "pass": "cutout",                                  // GeoQuad.Pass
  "twoSided": true,                                  // GeoQuad.TwoSided
  "alpha": 0.6,                                      // GeoQuad.Alpha when > 0
  "flat": 8421504,                                   // GeoQuad.Flat
  "untextured": false,                               // true when QuadEmit.Cell returned false
  "biome": "foliage" }
```

Shapes that read their neighbours (stairs corners, redstone wire, doors' other half) come out in their lone-block form. The
viewer draws doors, stairs, slabs, trapdoors and so on from metadata on its own only when the atlas leaves the id out.

### `emissive`

`IBlockModels.Emissive(id)` (1.12.2) or `BlockTextures.Emissive(id)` (b1.8.1): drawn at full brightness.

### `flat`

`BlockGeometry.FlatColour(id, Face.Up)` (optional): the colour of untextured faces.

## Sketch of the bridge endpoint

```csharp
var atlas = locator.Load(version);                       // the same TextureAtlas the freecam loads
var (w, h, rgba) = GpuAtlas.Build(atlas);                // → atlas.png
var world = new LoneBlockWorld();                        // IVoxelWorld: one block at (0,64,0), air around, sky 15
var states = new Dictionary<string, object>();
for (int id = 1; id < 256; id++)
{
    var perMeta = Enumerable.Range(0, 16).Select(meta => Entry(atlas.Models, world.Set(id, meta), id, meta)).ToArray();
    if (perMeta.All(e => Json(e) == Json(perMeta[0]))) states[$"{id}"] = perMeta[0];
    else for (int m = 0; m < 16; m++) states[$"{id}:{m}"] = perMeta[m];
}
```

Cache both responses per version (the textures only change when the jar does) and send them with `Cache-Control` and an
`ETag`.
