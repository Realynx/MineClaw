# What the bridge's atlas routes emit

`GET /v1/world/atlas.png?version=` and `GET /v1/world/atlas.json?version=` (`Atlas/AtlasService.cs`). The format is the world
viewer's `ATLAS.md` (`web/packages/world-viewer/ATLAS.md`, types in `src/atlas/types.ts`, `AtlasMap`). This file lists only
where the bridge's output differs from it or adds to it.

## Serving

- `version` is `b1.8.1` or `1.12.2`. When it is omitted, the bridge uses the server version (`Bridge:ServerVersion`, else
  `server-version.txt` beside the db, else b1.8.1). Any other value is a 400.
- Each version is built once per process and cached. Both replies carry `ETag: "<version>-<sha256[..24]>"` and
  `Cache-Control: private, max-age=86400`, and a matching `If-None-Match` gets a 304.
- Textures come from `TextureLocator.Load` exactly as the freecam loads them: `Bridge:AssetsDir` (default `<DataDir>/assets`),
  the 1.12.2 jar (`Bridge:ModernJar` / the bot's `Render:Assets:Jar`), and the launcher folders. Downloading is off unless
  `Bridge:AllowAssetDownload` is set.
- With no textures, the image still has the extras row (procedural water, lava and fire, white, shadow) and the entity
  stand-ins. `textured` is then false, and the states keep their geometry and `flat` colours.

## Image

`AtlasImage.Build` is a line-for-line copy of `GpuAtlas.Build` from `tools/MemoryExplorer/LiveHive/Gpu/GpuMesh.cs`. It was copied
rather than moved, because `src/` is off-limits for this change (moving it into MineClaw.Render is a later clean-up). The PNG is
straight RGBA via `MineClaw.Render.Png.Encode`.

## JSON

- **Keys are snake_case on the bridge** (`format_version`, `tile_size`, `default_tints`, `two_sided`), like every `/v1` reply.
  Nest's `toCamel` turns them into the camelCase of `AtlasMap`. Keys that are data pass through unchanged: state keys (`"17:4"`),
  biome ids, and the `special` names (already camelCase).
- **Extra top-level fields:**
  - `width` and `height` in px, which the contract's `WorldAtlasJson` requires
  - `textured`: false = flat colours only
- **Not emitted yet:** `items` (it is optional).
- **States** follow ATLAS.md:
  - A state is `"id"` when all 16 metas give identical JSON.
  - Otherwise `"id"` holds the most common meta's entry and `"id:meta"` holds each meta that differs from it. ATLAS.md allows
    this, since `"id:meta"` wins over `"id"`, and the payload shrinks.
  - Ids that `Blocks.Exists` (under the version's GameData) rejects are left out.
  - On 1.12.2, 166 and 217 are `none`.
- **Procedural shapes:**
  - `fluid` is 8–11.
  - `chest` is 54, plus 130 and 146 on 1.12.2, plus 95 on b1.8.1.
  - `fence` is 85, plus 113 and 188–192 on 1.12.2.
  - `pane` is 101 and 102, plus 160 on 1.12.2.
  - `wall` is 139 on 1.12.2.
  - Faces come from the lone block's quads: the first quad with `LightFace == f`, then a quad whose normal faces `f`, then any
    quad.
  - Their `layer` is the cube classification when there is one, otherwise `cutout` for panes and `opaque` for the rest.
- **Fluids** always use the procedural cells with flag 2, on both versions: still on up/down, flow on the sides. Water is
  `translucent`, lava is `opaque` and `emissive`.
- **Faces:**
  - A face is `null` when `QuadEmit.Cell` would return false.
  - `uv` is emitted only when a cube face's UVs differ from `BlockGeometry.FaceUv`. It is then written in the canonical corner
    order of ATLAS.md, matched by corner position and multiplied by the uv scale.
  - `biome` follows the BiomeTinted id list (grass: 2, 31, 175, 83; foliage: 18, 161, 106), and only when the quad carries the
    default plains tint.
- **Quads** carry every field of ATLAS.md. Optional fields are left out at their default: `face` −1, `shade` 1, `pass` opaque,
  `two_sided` false, `alpha` 0. `untextured` appears only when true.

## Sizes measured (Fox's PC, real jars found in the launcher folders)

| version | image | png | json |
|---|---|---|---|
| b1.8.1 | 256×688 | 89 KB | 400 KB (574 state keys) |
| 1.12.2 | 256×1200 | 227 KB | 2.3 MB (1918 state keys) |
