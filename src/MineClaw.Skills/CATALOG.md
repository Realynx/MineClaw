# MineClaw skill catalog (b1.8.1)

Skills are deterministic C# jobs composed from Client primitives. The model never sees them as tools: the worker has
two fixed meta-tools, `find_skill {query}` (≤ 5 one-line signatures, ≤ 600 chars) and `skill {name, args}` (result
under the normal tool caps). Delegated tasks get ≤ 4 matching signatures appended to the task text (worker's first,
uncached message, ≤ 200 tokens). The same signatures are indexed as `skill` docs in the RAG store, so the brain's
`[memory]` auto-retrieval can surface them. Adding skills costs **zero** prefix tokens (test-enforced:
full catalog vs empty catalog ⇒ identical tool prefix).

Signature form: `name(args) summary | needs: X | safety[,nether][,stub]` (≤ 120 chars, ≤ 35 tokens).

Why not the API's tool search / `defer_loading`: our loop is hand-rolled over provider-neutral `LlmBlock`s, and tool
search adds server-side `tool_search_tool_result`/`tool_reference` blocks the loop and `AnthropicLlmClient` don't model;
worker contexts are short-lived, so every task would re-discover (and re-pay for) the same schemas; and a local catalog
is free to search, works offline/in tests, and also feeds the brain's zero-token auto-retrieval. Cost of the meta-tool
pair: ≈ 133 tokens in the worker's cached prefix (worker total ≈ 1,738 / 1,800; brain ≈ 1,918 / 2,000, unchanged).

Status: **ready** = runs on existing public Client calls · **primitive** = implemented, needs a Client primitive
(fails with `needs client primitive X` until it exists) · **stub** = documented, returns "not implemented yet" ·
**n/a** = not in b1.8.1.

## Coverage

| Area | Skill / feature | Status | Notes |
|---|---|---|---|
| Doors | `open_door`, `close_door`, `toggle_door` | ready | State from metadata (0x4 open, 0x8 upper half, both halves carry the bit); every click verified by the 0x35 block change; unchanged meta ⇒ "locked/claimed"; idempotent |
| Doors | iron doors | ready | Button/lever within 2 or a plate in front; otherwise reported as an obstacle |
| Doors | `goto_via_doors` | ready | Plans with `PathOptions.AllowOpenDoors`, opens, passes, restores the door's original state (plain `goto` now does the same by default) |
| Doors | pathfinder `OpenDoor`/`OpenGate`/`OpenTrapdoor`, hatch climb | ready | Client goto handles `Activate` (stop, face, right-click, wait for the open bit, pass, close behind); `GotoOptions.AllowOpenDoors`/`RestoreDoors` default on; protected doors and doors that ignored 3 clicks are vetoed for the re-plan; hatches hinged on the ladder's own wall are refused (the open panel blocks the head) |
| Doors | mechanisms (learned) | ready | `MechanismLearner` (World) records trigger → effects from block changes within 15 ticks/8 blocks of any button/lever/plate activation (players' too; 0x36 marks pistons), pulse = time until the effects revert, reinforced by repeats; persisted as `mechanism` observations. Pathfinder `MoveKind.Trigger` steps (press/flip/walk onto the plate) with pulse-window timing, lever restore unless it would lock me in, way-back-out check, plates linked to TNT avoided, never in an unknown piston's push path |
| Redstone | `mechanisms`, `how_to_open`, `teach_mechanism` | ready | List known links + unknown iron doors/piston walls; `how_to_open` answers from the graph or returns `needs_input` ("how do I open the iron door at x,y,z?", default: go around) and a chat answer naming the trigger teaches it; `teach_mechanism {trigger,target}` records a told link |
| Redstone | `use_lever`, `press_button`, `set_repeater`, `load_dispenser`, `tune_noteblock` | ready | Unknown triggers near TNT/dispensers refused, dangerous links need `confirm`, unknown levers in builds need `confirm`; Repeater delay = meta bits 2–3, verified per click; note pitch is tile-entity only |
| Redstone | pistons | ready | Driven via `use_lever`/`press_button` |
| Redstone | `play_song` | stub | Needs per-block pitch tracking |
| Redstone | `ignite_tnt` | stub | Never automated: punching TNT primes it in beta; needs explicit consent + retreat design |
| Liquids | `fill_bucket`, `empty_bucket` | ready | Look + use (server raytraces); water refused in the nether |
| Liquids | `infinite_water` (2×2 or 1×3) | ready | Leak + lava-contact checks before pouring |
| Liquids | `move_lava` | ready | Refuses any target whose flow area touches water (no accidental obsidian/cobble/stone) unless `allow_obsidian`, flammables within 2, or my own cell |
| Liquids | `make_obsidian` | ready | Pours beside the lava so water runs onto it, scoops the water back; skips protected (generator) lava |
| Farming | `harvest_wheat` (meta 7 + replant), `plant_seeds`, `harvest_cane`, `harvest_cactus` (bottoms kept), `harvest_melons` (stems kept), `plant_sapling` | ready | |
| Farming | `till_soil`, `apply_bonemeal` | ready | `UseItemOnBlockAsync` (0x0F top face; bone meal = dye 15) |
| Building | `place_line`, `fill_floor`, `build_wall`, `expand_island`, `pillar_up`, `pillar_down`, `light_area`, `spawn_map`, `place_ladder`, `place_torch` | ready | Support-ordered placement (never mid-air); island growth only next to floor; hostile/ghast/survival vetoes; staircase down never opens the void; light_area = fewest torches over the b1.8.1 light model (never over the void, never a design's spawn deck), spawn_map = the read-only count |
| Building | `build_cobble_gen`, `build_mob_farm`, `build_blueprint` | stub | |
| Smelting | `smelt_batch` (cobble→stone, log→charcoal, …), `collect_furnace`, `refuel_furnace` | ready | Fuel never the thing being smelted; spreads > 64 over furnaces |
| Storage | `move_items`, `arrange_chest` (by_category/by_id/compact/columns/template/saved), `organize_storage`, `deposit_everything`, `restock_loadout` | ready | Minimal click plans on the World's click prediction; replan after 0x6A rejects; never closes with a loaded cursor; hive chest claims; layouts saved per chest |
| Storage | `label_chest` | ready | Wall sign on a solid non-chest neighbour (else a sign post beside it), then `WriteSignAsync` |
| Items | `give_items`, `drop_items`, `equip_armor`, `hold_item`, `eat_food` | ready | Exact counts via window clicks; reserved items refused |
| Survival | `sleep_in_bed` | ready | Night window 12541–23458, no hostile within 8, never in the nether (beds explode) |
| Survival | `time_weather`, `read_compass` | ready | Day/night from 0x04, rain from 0x46; compass → spawn |
| Misc | `eat_cake`, `read_sign`, `use_jukebox` | ready | |
| Misc | `write_sign` | ready | Sign post on solid ground, else a wall sign; lines sanitized to 15 allowed chars, verified by the server's 0x82 echo |
| Misc | `make_map`, `place_painting` | stub | painting can now use `UseOnBlock` |
| Nether | `build_portal` (4×5, 10 obsidian + 4 corner blocks), `light_portal`, `enter_portal`, `return_portal` | ready | Portal pairs tracked with 1:8 scaling; arrival waits for chunks + footing |
| Nether | `hazard_scan`, `gather_glowstone`, `gather_netherrack`, `gather_soul_sand`, `gather_gravel` | ready | Lava, ghasts (edge work refused while one is in range), pigmen (never targeted), fire, soul sand, overhead gravel |
| Combat | `throw_projectile` (snowball/egg, gravity aim) | ready | Never at pigmen or players |
| Combat | `shoot_bow` (charge 20 ticks, gravity aim) | ready | `ShootAsync`: b1.8 arrow physics (speed 3.0, drag 0.99, g 0.05) solved per tick, target led by observed velocity; combat gate applies |
| Animals | `dye_wool` | ready | Crafting |
| Animals | `shear_sheep`, `milk_cow`, `tame_wolf` | ready | `UseEntityAsync` (0x07 right-click, LOS + 3 blocks); wolves fed until meta 0x4 (≤ 10 bones) |
| Animals | `go_fishing` | stub | Bobber tracking + reel timing |
| Vehicles | `place_boat` | ready | |
| Vehicles | `ride_vehicle` | ready | Enter = right-click boat/minecart (attach via 0x27); exit = sneak, fallback right-click again; local physics frozen while riding |
| Vehicles | `ride_rails` | stub | `UseOnBlock` + `UseEntity` |
| Learned | `promote_routine` → `<routine>`; taught structures → `use_<structure>` | ready (Bot) | Zero prefix cost; unknown structure steps return `needs_input` |
| — | breeding, enchanting, brewing, End/ender eyes, villager trading, hunger saturation display, horses, anvils | n/a | Not in b1.8.1 (1.0+) |

## Client primitives (implemented)

All former "needs client primitive" skills are live through `ClientGameActions` (`Supported` = UseOnBlock | UseEntity |
WriteSign | ReleaseUse). Signatures are unchanged, so the prefix stays identical.

1. `UseItemOnBlockAsync(BlockPos, BlockFace, int itemId, int? damage, ct)`: holds the item, looks at that face, 0x0F with
   that face; `onItemRightClick` items (buckets, bow, food...) get the vanilla follow-up 0x0F −1. Success only on a block or
   inventory change. Refuses interactables (they'd open), protected blocks for hoe/flint, fire next to TNT.
2. `PlaceBlockAsync(x, y, z, itemId, BlockFace, ct)`, `PlaceOnWallAsync(...)`, `WallFaceFor(target, exclude)`: click a
   chosen face (sneaking, EdgeGuard as usual); the wall helper never picks an interactable support.
3. `UseEntityAsync(entityId, itemId?, damage?, ct)` (0x07 LeftClick=false, not combat-gated), `DismountAsync`, `RidingEntityId`.
4. `WriteSignAsync(pos, lines, ct)` (C→S 0x82), `SignLines` sanitizer.
5. `ShootAsync(Vec3d|entityId, chargeTicks, ct)`, `ReleaseUseItemAsync` (0x0E status 5), `ArrowBallistics`.
6. Path follower `Activate` step (see Doors). 7. `DimensionChanged(int from, int to)`: action cancelled, signs, weather,
   riding and combat state cleared. 8. Bot/WorldIndex keying by dimension: dimension-memory workstream.

Still missing: `DropHeld`, `LeaveBed` primitives (no skill needs them yet).
