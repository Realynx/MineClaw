# @mineclaw/world-viewer

This package is the browser voxel world viewer for MineClaw hives: a React + three.js component that follows the C# Live
Hive free camera. It reads the supervisor's world routes through an injected `WorldSource`:

- the world index and MCWC chunk payloads
- the entity delta stream
- overlays
- block deltas

It decodes and meshes every chunk in a Web Worker and renders everything in the browser. No server-side pictures are used.

## Usage

```tsx
import { WorldViewer, WorldViewerProvider, WorldThumbnail, type WorldSource } from '@mineclaw/world-viewer';

const source: WorldSource = {
  index: (dim, x, z, r) => api.get(`/api/world/index?dim=${dim}&x=${x}&z=${z}&r=${r}&light=1`),
  chunks: (dim, cols) => api.getBinary(`/api/world/chunks?dim=${dim}&cols=${cols.map((c) => c.join(',')).join(';')}&light=1`),
  entities: (since) => api.get(`/api/world/entities${since ? `?since=${since}` : ''}`),
  overlays: (dim) => api.get(`/api/world/overlays?dim=${dim}`),
  blocks: (since) => api.get(`/api/world/blocks${since ? `?since=${since}` : ''}`),       // optional
  subscribe: (onBlocks, onReconnect) => sse.on('blocks', onBlocks, onReconnect),           // optional (push instead of poll)
};

<WorldViewerProvider source={source}>                {/* one WebGL context + one world for everything inside */}
  <WorldViewer ref={viewer} source={source} follow="Alice" layers={{ claims: true }} onSelectBot={setBot} onPick={show} />
  {bots.map((b) => <WorldThumbnail key={b.name} follow={b.name} fps={3} />)}
</WorldViewerProvider>
```

JSON may come snake_case (supervisor) or camelCase (Nest): `normalize*` accept both. `chunks` must return the raw
`application/octet-stream` body.

### `<WorldViewer>`

| prop | meaning |
|---|---|
| `source` | the `WorldSource` |
| `dim` | 0 overworld, −1 nether |
| `follow` | bot name or short key the follow/iso cameras track and the x-ray keeps in sight |
| `layers` | `bots entities claims places points plans dangers notes weather cutaway staleness changes` (all on by default) |
| `cameraMode` | `follow` (orbit), `fly` (WASD/QE + mouse, pointer lock, Shift fast, wheel = speed), `iso` (narrow-FOV dolly; Q/E rotate, Shift/right-drag pans) |
| `xray` | `auto` (cone only when the bot is hidden or the camera is inside terrain), `on`, `off` |
| `drawDistance` | blocks, 32..256 (default 128) |
| `onSelectBot(name)` / `onPick(block)` / `onStats(stats)` / `onCameraModeChange` / `onError(route, e)` | callbacks |
| `options` | runtime tuning when there is no provider (poll periods, `maxSections`, `createWorker`, `pixelRatio`, `adaptiveResolution`) |

The imperative handle offers:

- `focus(x, y, z)`
- `setCameraMode(m)`
- `getCameraMode()`
- `followBot(name)`
- `bots()`
- `stats()`
- `engine()`

Mode switches and focus glide over 0.6 s.

### `<WorldViewerProvider>` and `<WorldThumbnail>`: many views, one world

The world exists once in memory, whatever number of views draw it:

- **One world store per dimension.** It lives in the mesh worker: columns keyed by (dim, cx, cz) with their hash. Each column
  is fetched once, however many views or bots need it. World sync follows the union of every view's interest region, so bots
  near each other share columns. The column count per dimension is capped (nearest to any view are kept).
- **One mesh per section.** Each section pass is one `BufferGeometry`, meshed once in the worker. Its arrays are transferred
  (`Transferable`), not copied, and uploaded once to the single WebGL context. The cut-away "cap" pass reuses the solid
  geometry with another material. Solid, translucent and cap materials and the atlas texture are single shared instances.
- **One scene and one renderer.** Every view renders the same `THREE.Scene`. A view owns only:
  - its camera
  - its viewport
  - its per-view uniforms (sky and time, fog centre, x-ray cone, roof cut)

  Cut-aways are uniforms: the shader discards cut cells, and the cap pass hatches what they open. Geometry is never rebuilt
  per view.
- **Entities and overlays.** There is one entity store and one entity layer (bots, plus a single `InstancedMesh` for mobs,
  players and drops) and one overlay layer. Labels show only in views that want them.

The interactive `<WorldViewer>` renders straight into the shared canvas, placed in its own container. The thumbnails render
into a corner of that canvas in the same frame and are copied into their own 2D canvases:

- They run at a low rate (`fps`, default 3).
- They get at most `viewsPerFrame` (default 2) renders per animation frame, most overdue first.
- They pause while offscreen (IntersectionObserver) or while the page is hidden.
- They use a short draw distance (40) and a third-person camera behind their bot (`angle="fixed"` gives the Explorer's
  yaw 315).

Browsers cap live WebGL contexts at around 16, so cards must never get one context each: a whole hive grid costs exactly one.
A `<WorldViewer>` without a provider creates a private runtime. Two `<WorldViewer>`s under one provider share it: the second
one draws through a 2D-canvas copy.

`stats().memory` gives views, columns, sections, section geometries, section GPU bytes, approximate worker bytes, bytes
received, entities, draw calls and triangles, for a developer-details panel.

### Block deltas

`blocks(since)` (polled at 4 Hz) or `subscribe(onBlocks, onReconnect)` (SSE) delivers
`{rev, full, more, blocks: {"<dim>": [[x,y,z,id,meta]…]}, columns: [{dim,cx,cz,kind}]}`. Changes are queued and applied once
per animation frame:

1. The worker patches its block arrays in place.
2. The worker reports the touched section, plus the neighbouring section across every boundary the block sits on.
3. The section manager's dirty set coalesces a burst (an explosion, a tunnel being dug), so each section is re-meshed once.

The rest falls back to the index hash-diff:

- `more` asks again at once.
- `full` (after the first reply), `onReconnect`, or a change in a column the viewer does not hold re-runs the index. Only
  changed hashes are refetched.
- `loaded` for a held column means a bulk change, so that column is refetched.
- `unloaded` keeps the column but dims it as remembered.

Patched columns pick up their new light when their changed hash brings them in again. Changed blocks glow briefly
(`layers.changes`).

### Rendering notes

- **Versions.** b1.8.1 and 1.12.2 block ids and meta are both supported. The version comes from the index's `version`, else
  256-high columns mean 1.12.2.
- **Meshing.** Every block becomes the quads of its version's geometry (below). A quad with a cull face is dropped against an
  opaque full-cube neighbour (and same-id glass, ice, slime). Axis-aligned quads get smooth sky and block light plus ambient
  occlusion from the face plane they lie in (interpolated to the vertex, so slabs, stairs and model elements are smooth too);
  rotated quads (plants) take their cell's light. Model `ambientocclusion: false` and light-emitting blocks skip the occlusion. Light is stored raw, so day/night (world time from the index, advanced
  at 20 ticks/s) and rain/thunder darkening are uniforms, never a remesh. Remembered columns without light estimate sky above
  the opaque top.
- **Passes.** The solid pass covers opaque blocks and alpha-tested cut-outs (leaves, glass, plants, b1.8.1 glass). The
  translucent pass covers water, ice, stained glass (+ panes), slime and portals.
- **1.12.2 geometry** (`src/mesh/models/`, ported from the C# `BlockStates1122`/`BlockStateFile`/`ModelBaker`/`BlockModels1122`):
  id/meta → blockstate with the neighbour-dependent properties (stair shapes incl. inner/outer corners, fence/pane/wall/bar
  connections, door halves and hinges, snowy grass, vine tops, stems, redstone sides/climbs, fire, double plants) → variants
  or multipart → models with parent inheritance, elements, element rotation (+ rescale), x/y rotation, uvlock, face uv and
  rotation, cullface, tintindex (biome grass/foliage/water blended over 3×3 columns from the chunk's biome bytes; redstone
  power; stems; spruce/birch/lily pad), shade and `ambientocclusion`, baked once per state. Tile-entity blocks have our own
  ports of their entity models: chests (large chests drawn half per cell), trapped/ender chests, beds, signs, banners,
  skulls, shulker boxes (defaults where the chunk can't tell: red bed, white banner, skeleton skull).
- **b1.8.1 geometry** (`src/mesh/beta/`, ported from the C# `BlockGeometry`/`BlockTextures`): every render type (torches
  leaning, crossed plants, crops, doors, ladders, rails incl. curves/slopes, stairs, fences, levers, cactus, beds,
  repeaters with their torches, pistons, panes, stems, vines, fence gates, signs, redstone lines), chest halves via
  `ChestTile`, the grass side overlay, fixed b1.8.1 tints.
- **Fluids** (both): vanilla corner heights from the levels, the flowing texture turned to the flow angle on sloped tops,
  the still texture on flat ones, sides to the corner heights, the top drawn from below too; water translucent (biome
  tinted in 1.12.2), lava opaque and full bright.
- **Textures.** The bundled atlas (`generated/<version>/`, `loadBundle`) replaces the bridge's atlas: 1.12.2 `blocks.png`
  (with the tile-entity sheets appended; a quad's tile attribute `w` gives the sheet size in cells) or b1.8.1 `terrain.png`.
  `RuntimeOptions.textures = false` (demo `?atlas=0`) meshes flat colours (each texture's average). `WorldSource.atlas` is
  deprecated and ignored.
- **Animated textures.** `BlockAtlas` (`src/render/blockAtlas.ts`) rewrites only the animated cells in place
  (`copyTextureToTexture`, a 16×16 sub-upload each) at the game tick rate: 1.12.2 cells follow their `.mcmeta` timing from
  `anim.png` (interpolated where flagged); b1.8.1 water, lava, fire and portal cells run the procedural TextureFX
  recurrences. `animHz` lowers the rate, `animCellsPerTick` caps the uploads per tick (round-robin), and
  `setVisible(cells)` limits them to cells the visible sections sample (`SectionMesh.animCells`).
- **X-ray.** The cone is `ViewCut`: per fragment, toward the followed bot, or toward up to two bots in front when flying. The
  roof cut removes cells above the bot's head within 9 blocks when it is under cover and the camera looks down. The faces a
  cut opens are hatched.
- **Weather.** Rain streaks and thunder flashes.
- **Performance.** Sections out of draw distance are hidden. Sections far from every view are freed, and least recently
  rendered sections are evicted beyond `maxSections`. Resolution is adaptive: one primary frame every 1.5 s is timed with
  `gl.finish`, and the resolution steps down (to 0.5×) above 14 ms and back up below 7 ms.

## Development

```
npm install        # package-local; .npmrc sets legacy-peer-deps (npm 10.9 crashes on vitest's optional peers)
npm run dev        # demo at http://localhost:5178 (fixture world, no bot needed): ?atlas=0 flat colours, ?version=1.12.2, ?thumbs=0, ?rain=1 (block gallery at x 8..30, z -40..-26)
npm run build      # d.ts (tsc) + ES library with the worker emitted beside it (vite)
npm test           # vitest
```

- **Tests.**
  - MCWC decoding against payloads written by the production C# encoders: `test/fixtures/csharp-payload.json`, regenerated
    with `dotnet run --project web/packages/world-viewer/test/fixtures/gen`. This covers both inflate paths, hashes and
    error cases.
  - Mesher face counts (both versions, against the committed packs).
  - 1.12.2 blockstates: multipart (fences, redstone), stair shapes, door halves, variant rotations + uvlock, AO flags, tints,
    chest pairs and the tile-entity blocks.
  - b1.8.1 render-type shapes, fluid corner heights and flow, `.mcmeta` timing and interpolation, the atlas animation
    budget and visible-only filter, the TextureFX ranges.
  - Entity interpolation.
  - Hash-diff fetching.
  - Shared meshing: N views mesh each section once.
  - Block deltas.
- **Exports.** `@mineclaw/world-viewer/testing` exports the encoders and `createFixtureSource()` for stories and app tests.
