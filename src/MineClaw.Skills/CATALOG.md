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
| Doors | `goto_via_doors` | ready | Plans with `PathOptions.AllowOpenDoors`, opens, passes, restores the door's original state |
| Doors | pathfinder `OpenDoor`/`OpenGate`/`OpenTrapdoor`, hatch climb | ready (World) | Closed panels are obstacles unless opening is allowed; trapdoor over the void/deadly drop is never a floor; follower emits `Activate` and restores doors |
| Redstone | `use_lever`, `press_button`, `set_repeater`, `load_dispenser`, `tune_noteblock` | ready | Repeater delay = meta bits 2–3, verified per click; note pitch is tile-entity only |
| Redstone | pistons | ready | Driven via `use_lever`/`press_button` |
| Redstone | `play_song` | stub | Needs per-block pitch tracking |
| Redstone | `ignite_tnt` | stub | Never automated: punching TNT primes it in beta; needs explicit consent + retreat design |
| Liquids | `fill_bucket`, `empty_bucket` | ready | Look + use (server raytraces); water refused in the nether |
| Liquids | `infinite_water` (2×2 or 1×3) | ready | Leak + lava-contact checks before pouring |
| Liquids | `move_lava` | ready | Refuses any target whose flow area touches water (no accidental obsidian/cobble/stone) unless `allow_obsidian`, flammables within 2, or my own cell |
| Liquids | `make_obsidian` | ready | Pours beside the lava so water runs onto it, scoops the water back; skips protected (generator) lava |
| Farming | `harvest_wheat` (meta 7 + replant), `plant_seeds`, `harvest_cane`, `harvest_cactus` (bottoms kept), `harvest_melons` (stems kept), `plant_sapling` | ready | |
| Farming | `till_soil`, `apply_bonemeal` | primitive | `UseOnBlock` |
| Building | `place_line`, `fill_floor`, `build_wall`, `expand_island`, `pillar_up`, `pillar_down`, `light_area`, `place_ladder`, `place_torch` | ready | Support-ordered placement (never mid-air); island growth only next to floor; hostile/ghast/survival vetoes; staircase down never opens the void |
| Building | `build_cobble_gen`, `build_mob_farm`, `build_blueprint` | stub | |
| Smelting | `smelt_batch` (cobble→stone, log→charcoal, …), `collect_furnace`, `refuel_furnace` | ready | Fuel never the thing being smelted; spreads > 64 over furnaces |
| Storage | `move_items`, `arrange_chest` (by_category/by_id/compact/columns/template/saved), `organize_storage`, `deposit_everything`, `restock_loadout` | ready | Minimal click plans on the World's click prediction; replan after 0x6A rejects; never closes with a loaded cursor; hive chest claims; layouts saved per chest |
| Storage | `label_chest` | primitive | `WriteSign` |
| Items | `give_items`, `drop_items`, `equip_armor`, `hold_item`, `eat_food` | ready | Exact counts via window clicks; reserved items refused |
| Survival | `sleep_in_bed` | ready | Night window 12541–23458, no hostile within 8, never in the nether (beds explode) |
| Survival | `time_weather`, `read_compass` | ready | Day/night from 0x04, rain from 0x46; compass → spawn |
| Misc | `eat_cake`, `read_sign`, `use_jukebox` | ready | |
| Misc | `write_sign` | primitive | `WriteSign` |
| Misc | `make_map`, `place_painting` | stub | painting needs `UseOnBlock` |
| Nether | `build_portal` (4×5, 10 obsidian + 4 corner blocks), `light_portal`, `enter_portal`, `return_portal` | ready | Portal pairs tracked with 1:8 scaling; arrival waits for chunks + footing |
| Nether | `hazard_scan`, `gather_glowstone`, `gather_netherrack`, `gather_soul_sand`, `gather_gravel` | ready | Lava, ghasts (edge work refused while one is in range), pigmen (never targeted), fire, soul sand, overhead gravel |
| Combat | `throw_projectile` (snowball/egg, gravity aim) | ready | Never at pigmen or players |
| Combat | `shoot_bow` (charge 20 ticks, gravity aim) | primitive | `ReleaseUse` |
| Animals | `dye_wool` | ready | Crafting |
| Animals | `shear_sheep`, `milk_cow`, `tame_wolf` | primitive | `UseEntity` |
| Animals | `go_fishing` | stub | Bobber tracking + reel timing |
| Vehicles | `place_boat` | ready | |
| Vehicles | `ride_vehicle` | primitive | `UseEntity` |
| Vehicles | `ride_rails` | stub | `UseOnBlock` + `UseEntity` |
| Learned | `promote_routine` → `<routine>`; taught structures → `use_<structure>` | ready (Bot) | Zero prefix cost; unknown structure steps return `needs_input` |
| — | breeding, enchanting, brewing, End/ender eyes, villager trading, hunger saturation display, horses, anvils | n/a | Not in b1.8.1 (1.0+) |

## Client primitives needed (exact additions)

1. `Task<ActionResult> UseItemOnBlockAsync(BlockPos pos, BlockFace face, int itemId, CancellationToken)` — select item, aim at that face, send 0x0F with that face. Lights up: till_soil, apply_bonemeal, place_painting, rails/minecarts, face-specific flint and steel.
2. `Task<ActionResult> UseEntityAsync(int entityId, CancellationToken)` — 0x07 `LeftClick=false` (not gated by the combat attack check). Lights up: shear_sheep, milk_cow, tame_wolf, ride_vehicle.
3. `Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken)` — C→S 0x82 after placing a sign (4 lines × ≤ 15 chars). Lights up: write_sign, label_chest, storage labels.
4. `Task<ActionResult> ReleaseUseItemAsync(CancellationToken)` — 0x0E status 5. Lights up: shoot_bow (and ranged combat in `CombatEngine`).
5. `PlaceBlockAsync(x, y, z, itemId, BlockFace face)` overload — ladders/wall signs/torches on a chosen wall (the nearest-face aim can pick the floor; `place_ladder` verifies meta 2–5 meanwhile).
6. Path follower: handle `FollowerActionKind.Activate` in `FollowPathAsync` (right-click via `ClickBlockAsync`, wait for the block change), then set `AllowOpenDoors = true` and `CanOpen = !IsBlockProtected` in `BuildPathOptions`. **Until then keep `AllowOpenDoors` off**: the current code treats every non-Break action as Place.
7. Dimension change (0x09 with a new dimension): abort the current action, clear `_signs`, reset `IsRaining`, reset combat targets, raise a `DimensionChanged` event. (Already correct: world/entities cleared, window closed, movement frozen until chunks + footing, edge guard re-armed on the next 0x0D — tested.)
8. Bot/WorldIndex (not Client): key observations by dimension so nether coordinates don't mix with overworld ones.
