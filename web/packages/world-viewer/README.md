# @mineclaw/world-viewer

This package is the browser voxel world viewer for MineClaw hives: a React + three.js component that follows the C# Live
Hive free camera. It reads the supervisor's world routes through an injected `WorldSource`:

- the world index and MCWC chunk payloads
- the entity delta stream
- overlays
- block deltas
- an optional texture atlas

It decodes and meshes every chunk in a Web Worker and renders everything in the browser. No server-side pictures are used.

## Usage

```tsx
import { WorldViewer, WorldViewerProvider, WorldThumbnail, type WorldSource } from '@mineclaw/world-viewer';

const source: WorldSource = {
  index: (dim, x, z, r) => api.get(`/api/world/index?dim=${dim}&x=${x}&z=${z}&r=${r}&light=1`),
  chunks: (dim, cols) => api.getBinary(`/api/world/chunks?dim=${dim}&cols=${cols.map((c) => c.join(',')).join(';')}&light=1`),
  entities: (since) => api.get(`/api/world/entities${since ? `?since=${since}` : ''}`),
  overlays: (dim) => api.get(`/api/world/overlays?dim=${dim}`),
  atlas: async () => ({ image: await loadBitmap('/api/world/atlas.png'), map: await api.get('/api/world/atlas.json') }),
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
- **Entities and overlays.** There is one entity store and one entity layer: entities are posed once per frame into one
  bone texture, and each view only picks instances (one instanced draw per model kind; see [Entity models](#entity-models)).
  There is one overlay layer. Labels show only in views that want them.

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

The runtime survives page navigation. Providers with the same `persist` key (default `"default"`) attach to one module-level
runtime, so going from the hive grid to a bot view keeps:

- the world store
- the meshes
- the WebGL context

A new `source` object for the same key is swapped in without dropping the world. The network loops pause while no view is
mounted. Call `disposeWorldRuntimes()` on logout. Pass `persist={false}` for a runtime that lives and dies with its provider.

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

Light is recomputed locally around each batch of changes: sky light falls and spreads, and block light floods out from
emitters, within ±15 blocks. A dug hole lets the sky in and a placed torch lights its room at once. Cells just outside the
region seed it, so the patch joins seamlessly. The column's light still refreshes when its changed hash brings it in again.
Changed blocks glow briefly (`layers.changes`).

### Rendering notes

- **Versions.** b1.8.1 and 1.12.2 block ids and meta are both supported. The version comes from the index's `version`, else
  256-high columns mean 1.12.2.
- **Meshing.** Faces are culled against opaque neighbours (and same-id glass, ice and fluids). Each vertex gets smooth sky and
  block light from the payload plus ambient occlusion. Light is stored raw, so day/night (world time from the index, advanced
  at 20 ticks/s) and rain/thunder darkening are uniforms, never a remesh. Remembered columns without light estimate sky above
  the opaque top.
- **Passes.** The solid pass covers opaque blocks and alpha-tested cut-outs (leaves, glass, plants). The translucent pass
  covers water, ice and stained glass.
- **Shapes.** Slabs, stairs, fences, panes, walls, doors, trapdoors, torches, plants, ladders, vines, rails, plates and snow
  layers have their own shapes. Double chests pair by the `ChestPairs` rule.
- **Textures.** With an atlas (format in [ATLAS.md](ATLAS.md)) blocks are textured exactly as the C# freecam; without one,
  the flat palette is used.
- **X-ray.** The cone is `ViewCut`: per fragment, toward the followed bot, or toward up to two bots in front when flying. The
  roof cut removes cells above the bot's head within 9 blocks when it is under cover and the camera looks down. The faces a
  cut opens are hatched.
- **Sky.** Sun, moon (with its phase), stars at night and drifting clouds come from the version's own textures and follow the
  celestial angle. Rain or snow falls by the biome under the view: cold biomes and high ground snow, dry biomes stay clear.
  Thunder flashes.
- **Cameras.** `follow`, `fly`, `iso`, and `top` (straight down, north up).
- **Reading aids.** The optional `layers.aids` draws the C# reading aids around the followed bot: a ground grid with
  coordinate rulers, distance rings at 2/4/8 blocks, and a compass.
- **Performance.**
  - Sections out of draw distance are hidden. Sections far from every view are freed, and least recently rendered sections
    are evicted beyond `maxSections`.
  - Cave culling follows Minecraft's chunk visibility graph. The worker computes each section's face connectivity, and each
    view walks it from its camera, so caves and hillsides behind rock are not drawn. Culling is off while a cut-away is open.

### Quality: GPU vs software WebGL

With hardware acceleration off, Chrome renders WebGL on SwiftShader. The viewer detects this from the renderer string
(SwiftShader, llvmpipe, Microsoft Basic Render Driver) or from a refused `failIfMajorPerformanceCaveat` probe, and picks a
profile:

| | GPU | software |
|---|---|---|
| frame target | 16 ms (60 fps) | 25 ms (40 fps) |
| MSAA / pixel ratio cap | on / 2 | off / 1 |
| draw distance cap (min) | 256 (64) | 128 (48) |
| translucent pass beyond | 256 blocks | 64 blocks |
| entity detail beyond | 64 blocks | 24 blocks (boxes) |
| animated textures | 20 Hz, all | 10 Hz, ≤ 8 cells per tick |
| thumbnails | ×1 rate, 2 per frame | ×0.5 rate, 1 per frame |
| clouds | on | off |

A frame-time controller runs once a second. It times one whole frame to completion, with a one-pixel readback because
`gl.finish` returns early on ANGLE/SwiftShader. It lowers the render resolution first (to 0.5× on software), then the draw
distance, and gives them back when there is headroom.

The options are:

- `options.quality`: `'auto'` (default), `'gpu'` or `'software'`
- `options.adaptiveResolution`: set it to `false` to freeze the levers

`stats().memory` shows the profile, the renderer string, why the profile was picked, the frame time, the draw factor and the
culled sections.

`npm run perf` is the benchmark. It serves the demo, opens it in Playwright's Chromium with SwiftShader and then with the GPU,
and measures sustained fps at the follow camera over the dense fixture world (lava lake, water channel, glass pillars,
20 mobs). It prints one JSON line per run and exits non-zero when software rendering falls under 40 fps.

### Entity models

Bots, players, mobs, drops and objects are drawn as vanilla-shaped box models, textured from the bundled entity sheet
(`generated/<version>/entity.png`; `loadBundle`). This covers both versions and every mob type the wire can report, plus
the layers: spider and enderman eyes, sheep wool in its colour, pig saddle, the stray overlay, the wolf collar, slime gel
and dragon eyes.

- **Data.** The models are our own data files in `src/render/entities/models/`; `types.ts` documents the format. A model
  has named parts with a pivot, a rest rotation and children. Each part holds cuboids (origin, size, inflate, mirror, texture
  offset or per-face rectangles), and the model has a texture size, an optional layer per part and a pose function (the
  vanilla `setRotationAngles`). `builder.ts` turns a model into geometry, and `registry.ts` maps a mob type, Mob16
  variant, object type or player to a model, a skin and its layers.
- **GPU skinning.** Every frame each entity's part matrices are written into one row of a float texture. A vertex fetches
  its part's matrix (`texelFetch`). A view only fills small per-instance arrays, so there is one draw call per model kind
  in view, plus item sprites, item cubes, shadows and LOD boxes. A skin and its layers are instances of the same geometry;
  a layer instance draws only the parts tagged with its layer. With 20 walking mobs on SwiftShader at 1280×800, the models
  cost about 1.5 ms over plain boxes (`/models.html?n=20`, `__gallery.bench()`).
- **Animation.** All of these are driven by data the wire already has:
  - Limb swing comes from the distance walked (the vanilla limb-swing filter) and runs faster while sprinting.
  - The body yaw turns toward the walk direction and keeps the head within 75°.
  - Bots get head yaw and pitch; mobs have one yaw on the wire.
  - Arms sway when idle. Players crouch while sneaking.
  - A creeper flashes while fusing and primed TNT flashes.
  - Burning entities (flag 0x01) show fire.
  - A bot flashes red when its hp drops and falls over at hp 0.
- **Hooks.** `swing(key)`, `hurt(key)` and `kill(key)` drive what the wire does not report yet.
- **Detail.** Beyond `detailDistance` (default 48 blocks; the perf controller sets it), entities draw as plain coloured
  boxes and are not posed. The distance is scaled by the view's field of view, so iso views keep detail further out.
- **Light.** Entities take the world's time-of-day light and fog uniforms (`setWorldUniforms`). `lightAt(dim, x, y, z)` is
  the hook for per-entity sky and block light once that is queryable on the main thread.
- **Items.** A bot's held item comes from its `held` name; a player's comes from its held item id. Held items and drops
  resolve to an item sprite or a block cube: on 1.12.2 through the packed item models and textures, on b1.8.1 through the
  `items.png` icons and terrain tiles. Drops bob and spin with vanilla's stack copies. XP orbs, boats, minecarts, arrows,
  falling blocks and primed TNT have render paths, but the supervisor sends only players, mobs and drops today.
- **Not on the wire today.** These render paths exist but get no data:
  - equipment and armour (the layer reads an optional DTO `armor: [helmet, chest, legs, boots]`)
  - skins (bots wear Steve; other players get Steve or Alex by a name hash)
  - mob health and hurt events, mob deaths and arm swings
  - separate mob head yaw
  - villager professions and horse coats (picked by entity id)
  - baby flags
- **Gallery.** `/models.html` in the dev server shows every model on a flat floor. It takes `?version=b1.8.1`, `?walk=0`,
  `?spin=1` and `?n=20` (the perf scene). The fixture world has a zoo of every mob type south of the island (`?zoo=0` turns
  it off; `?version=1.12.2` switches the version).

## Development

```
npm install            # in web/ (npm workspaces)
npm run dev            # demo at http://localhost:5178 (fixture world): ?version=1.12.2 ?dense=1 ?mobs=20 ?quality=software ?thumbs=0 ?rain=1
npm run build          # plain ESM + d.ts (tsc, then scripts/fix-esm.mjs makes relative imports fully specified)
npm test               # vitest
npm run perf           # Playwright fps benchmark (software + GPU)
npm run assets:extract # re-extract assets/ from the client jars (PrismLauncher / .minecraft; read as zips, never run)
npm run assets:pack    # rebuild generated/ (atlases, animation sheet, entity sheet, pack.json) from assets/
```

The package is published as plain ESM, not a bundle. `new Worker(new URL('./worker.js', import.meta.url))` and the
`new URL('../../generated/…', import.meta.url)` asset references reach the app's bundler verbatim. Next/Turbopack, webpack and
Vite bundle the mesh worker and emit the atlases themselves, with no copy step.

- **Assets.** The Minecraft assets are committed: the repo is private and unreleased (docs/web/architecture.md, "Render
  parity").
  - `assets/1.12.2` holds the blockstates, the block and item models, and the block, item, entity, environment, misc and
    colormap textures with their `.mcmeta` animation files.
  - `assets/b1.8.1` holds terrain, items, mob, armor, environment, misc and terrain/sun+moon.
  - `generated/<version>` holds what the browser loads (`src/assets/bundle.ts`).
```

- **Tests.**
  - MCWC decoding against payloads written by the production C# encoders: `test/fixtures/csharp-payload.json`, regenerated
    with `dotnet run --project web/packages/world-viewer/test/fixtures/gen`. This covers both inflate paths, hashes and
    error cases.
  - Mesher face counts.
  - Entity interpolation.
  - Entity models: every model builds inside its texture, every mob type has a model and existing textures in both
    versions, poses from walking, yaw and pitch, LOD, and held and dropped item resolution.
  - Hash-diff fetching.
  - Shared meshing: N views mesh each section once.
  - Block deltas: exact dirty sections, burst coalescing, fallbacks and local relighting.
  - Cave-culling graph.
  - Quality controller.
  - Precipitation by biome.
- **Exports.** `@mineclaw/world-viewer/testing` exports the encoders and `createFixtureSource()` for stories and app tests.
