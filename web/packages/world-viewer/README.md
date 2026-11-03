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
  skulls, shulker boxes, end portal/gateway. The mesher reads the column's block entities (`ColumnStore.blockEntityAt`):
  bed colours, skull type (skeleton, wither, zombie and player with the hat layer, creeper, dragon head) and 16-step
  rotation, flower pot contents (item id or name); without one the defaults stay (red bed, skeleton skull, empty pot).
  End portals and gateways carry a starfield flag: the section shader draws the client's layered, screen-projected,
  scrolling starfield (`render/starfield.ts`; the native 128 px end_sky and 256 px end_portal sheets, colours from
  `java.util.Random(31100)`, getPasses' layer count by the squared distance to the block: 16 within 16 blocks down to 2
  beyond 192, the gateway one more).
  Flowing water and lava sample their native 32 px textures on 2×2-cell sheets (animated at 32 px).
- **Block-entity layer** (`render/blockEntityLayer.ts`, main thread, shared by every view, rebuilt only when the
  block-entity revision or the dimension changes):
  - Sign text on the board fronts in the version's own bitmap font (`render/font.ts`: 1.12.2 `textures/font/ascii.png`,
    b1.8.1 `font/default.png` with `font.txt`'s character table; per-glyph advances measured from the image at pack time
    like `FontRenderer.readFontTexture`). § colour codes; on 1.12.2 also bold, italic, underline, strikethrough,
    obfuscated (a fixed same-width stand-in) and reset. 1.12.2 cuts a long line at the last space that fits 90 px
    (`splitText`); b1.8.1 draws it whole, at its slightly larger 1/90 text scale. One small texture per sign, LRU-capped,
    within 24 blocks in the software profile, 48 otherwise. Characters outside the font image (1.12.2's unicode pages)
    are not drawn.
  - Banners with their base colour and pattern layers composited like `LayeredColorMaskTexture` and swaying (those
    banners leave the section mesh).
  - Spawners' display mobs through the entity layer's `showDisplayMob` hook: the named mob, else the default pig (every
    b1.8.1 spawner, whose client never learns the type), scaled like `renderMob` (1.12.2 0.53125 over the larger hitbox
    side above a block, b1.8.1 0.4375), leaning back 30°, spinning and giving off flame and smoke particles only while a
    player or bot is within 16 blocks. No blob shadow under them.
  `RuntimeOptions.fastLeaves` opts into opaque fast leaves (not on by default: no measurable gain under SwiftShader).
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
- **X-ray.** The cone is `ViewCut`, applied per fragment toward the followed bot, or toward up to two bots in front when
  flying. The roof cut removes cells above the bot's head within 9 blocks when it is under cover and the camera looks down.
  Both are per-view uniforms.
  - **Cap pass.** The cut-away cap pass draws back faces after the solids. Each fragment walks its ray to where it leaves the
    cut volume and writes a hatched wall there with `gl_FragDepth`: the cone's subject plane or floor, the roof cut's floor
    or side. The result is a true section wall at the cut surface, never a hole.
  - **Follow camera.** It is pulled in front of solid blocks between it and the bot, like vanilla's third person. Blocks the
    roof cut removes don't count, so with the bot in a tunnel the camera stays up and looks down into it.
- **Entity light.** Each entity model takes the sky/block light of its cell, which the worker reports a few times a
  second.
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
| frame target (measured work) | 16 ms (60 fps) | 22 ms (40 fps with the compositor) |
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
(`generated/<version>/entity.png`, which also holds the painting sheets; `loadBundle`). This covers both versions and every
entity kind the wire reports.

- **Data.** The models are our own data files in `src/render/entities/models/`; `types.ts` documents the format.
  - A model has named parts with a pivot, a rest rotation and children.
  - Each part holds cuboids (origin, size, inflate, mirror, texture offset or per-face rectangles).
  - A model also has a texture size, optional texture layers per part (wool, saddle, chests, carpet, charge…), an optional
    vanilla child layout and a pose function (the vanilla `setRotationAngles`).
  - `builder.ts` turns a model into geometry.
  - `registry.ts` maps a mob type or name, an object kind or a player to a model, a skin and its layers.
- **Appearance.** `variant.ts` decodes the legacy numeric `variant` together with the `look` tokens (`red,sheared`,
  `creamy,white_dots,tame,saddle,armor=iron`, `tame,sitting,collar=red`, `powered,fusing`, `size=4`,
  `profession=librarian`…). Each look reads the tokens it knows and ignores the rest. The decoded variants cover:
  - sheep colour and shearing
  - wolf collar colour, anger and taming
  - pig saddle
  - the powered creeper's scrolling aura
  - slime and magma cube size
  - horse coat, markings, armour and saddle; donkey and mule chests
  - llama colour, carpet and chests
  - villager and zombie villager professions
  - cat types, rabbit coats, parrot colours, shulker colours and lid, boat wood
  - minecart contents
  - armor stand options and pose
- **Equipment.** `equipment` (`[id, meta]`, or `[id, meta, rgb]` for a dyed item) draws on the renderers that have the
  layer (players, bots, armor stands, zombies and their kin, skeletons and their kin, zombie villagers; the giant wears
  armour but no head item; villagers wear head items but no armour):
  - armour pieces in their material; 1.12.2 leather tinted by its dye (default `0xA06540`) with its untinted overlay,
    b1.8.1's brown cloth as it is; zombie villagers wear `LayerVillagerArmor`'s taller helmet
  - main- and off-hand items (item sprites, or block cubes)
  - head items like `LayerCustomHead`: a skull (item 397, its meta the type) as the skull model, the dragon head with
    its jaw moving with the walk; a block (pumpkin) at its 0.625 head scale; a 1.12.2 flat item with its "head"
    transform; the child and villager offsets
  - elytra in the chest slot (`LayerElytra`, spread while sneaking)
- **Implied layers.** The snow golem's pumpkin (unless the look says `sheared`) and an adult mooshroom's three crossed
  red mushrooms.
- **Flags.** The flag bits drive these effects:
  - fire on burning entities
  - sneaking and riding poses (a sneaking 1.12.2 biped sinks 0.2 like `ModelBiped.render`)
  - faster limb swing while sprinting
  - the item-use pose (drawing a bow, eating)
  - invisibility (the body is hidden; armour and held items stay)
  - glowing (lit)
- **Babies.** Babies use vanilla's child layouts: a big head on a half-size body for bipeds and animals, per-group
  proportions for foals (`ModelHorse` with `getHorseSize()` 0.5: long thin legs, a half-size body, a 0.625 head) and baby
  llamas (`ModelLlama`'s per-axis head, body and leg scales), and other models at half size.
- **Looks from the variant.** A fusing creeper swells and flashes like `RenderCreeper` (from when the look said it began);
  primed TNT flashes at `RenderTNTPrimed`'s fading strength; a screaming enderman opens its jaw and jitters; a converting
  zombie villager shakes.
- **GPU skinning.** Every frame each entity's part matrices are written into one row of a float texture. A vertex fetches
  its part's matrix (`texelFetch`). A view only fills small per-instance arrays, so there is one draw call per model kind
  in view, plus armour, item sprites (held items, drops, paintings, particles, fire), item cubes, shadows and LOD boxes.
  - A skin and its layers are instances of the same geometry; a layer instance draws only the parts tagged with its layer.
  - With 20 walking zombies on SwiftShader at 1280×800, the models cost about 0.7–1.5 ms over plain boxes
    (`/models.html?n=20`, `__gallery.bench()`).
- **Animation.** Animation comes from motion and events:
  - Limb swing follows the distance walked; the body yaw turns toward the walk direction.
  - The head follows `headYaw` (or the look yaw), within 75° of the body, plus bot pitch.
  - Arms sway when idle.
- **Events** (`onEvents`, fed by the runtime from the delta stream):
  - `swing` and `swing_off` swing the right or left arm.
  - `hurt` gives a red flash.
  - `death` makes the body fall over, shrink away with a smoke puff, then disappear. It stays drawn that long even when
    removed.
  - `crit` and `magic_crit` throw spark bursts.
  - `eat` puts the hand to the mouth (a sheep puts its head down).
  - `teleport` leaves portal puffs at the old and new places.
  - `lightning` draws a jagged additive bolt for half a second.
  - Bots also flash on an hp drop and fall at hp 0.
  - `swing(key)`, `hurt(key)` and `kill(key)` are direct hooks.
- **Display mobs.** `showDisplayMob(key, dim, x, y, z, mob, scale, spinRad)` and `hideDisplayMob(key)` draw a mob (by name)
  inside a block, as spawner cages do. They share the entity draws.
- **Other kinds.** These have their own render paths:
  - paintings (motive and facing)
  - item frames (frame plate plus item, rotated in 45° steps)
  - minecarts with their block; boats; arrows aimed along their velocity
  - falling blocks; primed TNT that flashes by fuse and swells
  - XP orbs, fireworks, ender crystals
- **Detail.** Beyond `detailDistance` (the perf controller sets it from the quality profile), entities draw as plain coloured
  boxes and are not posed. The distance is scaled by the view's field of view, so iso views keep detail further out.
- **Light.** Entities take the world's time-of-day light and fog uniforms (`setWorldUniforms`), and per-entity light via
  `lightAt`.
- **Not on the wire.** These are still missing or picked by entity id:
  - skins and capes: bots wear Steve; other players get Steve or Alex by a name hash; player skulls show Steve
  - the enchantment glint on enchanted armour and items
  - an enderman's carried block, an iron golem's offered flower, parrots on shoulders, arrows stuck in players
  - JSON text styles on 1.12.2 sign lines (the bot flattens them to plain text; § codes typed in the text do show)
  - horse coats and villager professions when the look gives none
- **Gallery.** `/models.html` in the dev server shows every model on a flat floor, with scripted events. It takes
  `?version=b1.8.1`, `?walk=0`, `?spin=1`, `?events=0`, `?n=20` (the perf scene) and `?set=looks` (only the equipment,
  head item, elytra, baby and implied-layer showcase). `node scripts/shot.mjs --page models.html --query set=looks --eval
  "g.set(yaw, pitch, dist, x, z, y)"` screenshots it headlessly.
- **Fixture.** The fixture world (`zoo`, on in the demo) has three areas:
  - a zoo of every mob type south of the island
  - a showcase of equipment, babies, variants, paintings, an item frame and minecarts in the final wire shape
  - scheduled events: Alice swings every second, a pigman is hurt and dies every 12 s, an enderman teleports, and lightning
    strikes every 20 s

## Development

```
npm install            # in web/ (npm workspaces)
npm run dev            # demo at http://localhost:5178 (fixture world): ?version=1.12.2 ?atlas=0 (flat colours) ?dense=1 ?mobs=20 ?quality=software ?thumbs=0 ?rain=1
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
    colormap textures with their `.mcmeta` animation files, and the default font (`textures/font/ascii.png`).
  - `assets/b1.8.1` holds terrain, items, mob, armor, environment, misc, terrain/sun+moon and the font
    (`font/default.png`, `font.txt`).
  - `generated/<version>` holds what the browser loads (`src/assets/bundle.ts`).
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
  - Entity models: every model builds inside its texture, every mob type has a model and existing textures in both
    versions, poses from walking, yaw and pitch, LOD, and held and dropped item resolution.
  - Entity wire: variant and look parsing per kind, equipment to armour, event timings (swing, hurt, death fade), baby
    layouts, painting motives, item frames, minecart contents, display mobs and lightning; dyed leather passes, worn
    skulls, head items and elytra per renderer, villager armour, foal and baby llama proportions, the creeper swell and
    flash, the screaming enderman, the snow golem's pumpkin and the mooshroom's mushrooms, spawner mobs without shadows.
  - The bitmap font: glyph tables and advances from both font images, § codes, the 1.12.2 sign line cut, rasterising;
    spawner mob scales and feet; starfield passes by distance.
  - Hash-diff fetching.
  - Shared meshing: N views mesh each section once.
  - Block deltas: exact dirty sections, burst coalescing, fallbacks and local relighting.
  - Cave-culling graph.
  - Quality controller.
  - Precipitation by biome.
- **Exports.** `@mineclaw/world-viewer/testing` exports the encoders and `createFixtureSource()` for stories and app tests.
