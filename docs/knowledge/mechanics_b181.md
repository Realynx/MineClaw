# Beta 1.8.1 mechanics — field notes for any world

<!-- pack version=b1.8.1 -->
<!--
Knowledge convention (MineClaw RAG): the block between the "core" and "/core" HTML-comment markers below is always
in the bot's cached system prompt, so keep it short (most important rules only). Everything else is split by heading
into ~150-300-token chunks and retrieved on demand (auto [memory] lines or the recall tool). A file without core
markers contributes its first section as core. Editing a file re-indexes it automatically at the next start.

Pack marker: "pack version=b1.8.1" puts this file in the b1.8.1 version pack (shards/defaults/b1.8.1-gamedata.mcshard):
mechanics that hold in every b1.8.1 world. World-mode advice (skyblock: the void, the starter island, unrenewables,
challenges) lives in its mode doc (skyblock_b181.md, "pack mode=skyblock"), which a world without that mode leaves out.
-->

<!-- core -->
## Core rules (b1.8.1)
- Health regenerates only while food is 18+; eat before long jobs.
- Food in b1.8.1: bread (3 wheat), melon slices, mushroom stew (bowl + red + brown mushroom), cake, cookies; pork (pigs), raw beef (cows), raw chicken (chickens), rotten flesh (zombies), fish by fishing; cook meat/fish in a furnace. Leaves drop only saplings: apples, so golden apples too, come only from dungeon chests.
- The seeded b1.8.1 knowledge and game data win over general Minecraft knowledge (later versions differ).
- Saplings can be **planted** any time, night included, on dirt/grass/farmland that can see the sky (or has light ≥8), so night alone never blocks planting. They only **grow** at light ≥9, so at night growth pauses unless torches are nearby.
<!-- /core -->

## Hunger and health
- Hunger bar is 20 (10 shanks). Health regenerates only while food ≥18. At 0 food you starve (Easy stops at 5 hearts, Normal at ½ heart, Hard can kill). **[thresholds from memory]**. Sprinting (double-tap forward) needs food >6 and burns hunger fast, as do jumping and mining.
- Food (hunger points): steak/pork 8, stew 8, cooked chicken 6, bread 5, cooked fish 5, apple 4, rotten flesh 4 (80% chance of food poisoning), raw chicken 2 (30%), melon 2, cookie 1. Peaceful regenerates health and has no hostile mobs.
- Fall damage is 1 half-heart per block beyond 3.

## Fluids, fire and stone
- Water flows 7 blocks, lava 3 (lava flows slowly). A water source forms only between ≥2 sources with solid ground or water below.
- Water running onto a lava *source* turns it into obsidian, and there's no getting the lava back. Water touching *flowing* lava makes cobblestone; mine that cell and it refills (a cobblestone generator). Lava flowing down onto water makes stone.
- Infinite water: two sources in opposite corners of a 2×2 hole, 1 deep, with a solid floor. Any cell you take from refills.
- Break ice that sits on a solid block and it becomes water; ice drops no item.
- Fire spreads to wood, planks, leaves and wool. Lava can ignite flammable blocks nearby even without touching them, so keep lava away from wood.

## Mobs, animals and light
- Hostile mobs spawn on solid opaque blocks at light ≤7, and never closer than 24 blocks to a player. Mobs can't spawn on half slabs. Torches stop mob spawns nearby; creepers blow up dirt.
- Animals don't despawn in b1.8 and spawn rarely, on grass in light. **[unsure how often]**
- Beds skip the night and set your spawn.
- **Mob farm**: a cobblestone platform 24+ blocks from where you stand (mobs never spawn closer than 24). Add 1-wide walls/channels and a drop for the kill. Drops: zombie → rotten flesh; skeleton → bones (bone meal) and arrows; spider → string (4 string = wool → beds/paintings); creeper → gunpowder; enderman → ender pearl.
- Cactus **destroys dropped items that touch it** (item entities take its contact damage), so a cactus mob grinder loses most of its loot. To keep drops: kill by fall damage (23+ blocks, or a shorter drop and a finishing hit) onto plain blocks, or let water carry drops away from the cactus to a collection point.
- Endermen can pick up grass, dirt, sand, gravel, flowers, mushrooms, cactus, clay, pumpkins, melons and TNT. I'm not sure whether they drop a carried block when killed in b1.8.

## Trees, dirt and grass
- b1.8.1 has no way to make dirt. Grass only spreads onto dirt you already have (light ≥9 above).
- Plant saplings on dirt, grass or farmland with space (a big oak needs a lot). A sapling can be **planted** any time it can see the sky (or the light there is ≥8), so night is no reason to skip a replant. It only **grows** when the light at the sapling is ≥9 (a 1-in-30 check per random tick), so at night growth pauses unless torches are nearby.
- Leaves decay once the logs are cut. Oak leaves drop a sapling 1 time in 20 when they decay or are broken. Shears get you the leaf block instead of the chance at a sapling.

## Farming
- **Sugar cane**: plant on grass, dirt or sand with water directly beside it horizontally. Grows to 3 tall; harvest the top 2.
- **Cactus**: only on sand, with no blocks touching its sides. Grows to 3 tall and hurts on contact. Smelt it for cactus green.
- **Wheat**: seeds come from tall grass (1 in 8). Use bone meal on grass to grow tall grass. Hoe dirt within 4 blocks of water. Harvest only when fully grown (stage 7/7).
- **Farming procedure (Fox's order, every cell): harvest → hoe → plant.** Always hoe before planting, even when the cell still looks like farmland: farmland turns back to dirt when it dries out with nothing planted, or when someone walks on it (1 in 4 per step, not when sneaking). A hoe on farmland does nothing, so re-tilling is safe. Get a hoe first if you have none (2 sticks + 2 planks or cobble). Sneak across the field, never jump onto it. `harvest_wheat` does all of this.
- **Pumpkin/melon**: plant the stem on farmland. The fruit appears on a free neighbouring block that has dirt, grass or farmland under it. Harvest the fruit, keep the stem. A melon block drops 3–7 slices; 1 slice crafts 1 seed. b1.8 has no recipe for pumpkin seeds, so keep the stem alive.
- **Mushrooms** (mc-dev BlockMushroom / WorldGenHugeMushroom): a small mushroom never grows taller. It needs light below 13 and an opaque block under it; in brighter light it pops off. It **spreads** instead: each random tick has a 1-in-100 chance to put a copy on a free spot within 1 block sideways and ±1 up or down (same light and floor rules). Spreading stops once 5 of that colour are within 4 blocks. To farm them, put dark, opaque floor next to them. **Bone meal** turns it into a huge mushroom only when it sits on dirt or grass and a 7×7 column of air (leaves are fine) is clear above it for 5–8 blocks; otherwise the bone meal does nothing. A huge mushroom turns the block under it into dirt.

## Crafting, smelting and tools
- Paper is 3 canes for 3 paper; a book is 3 paper (no leather in beta); a bookshelf is 6 planks + 3 books.
- **Fishing**: rod = 3 sticks + 2 string. Raw fish smelts into cooked fish.
- **Slabs are kinds, not one item**: a cobblestone slab (44:3) is crafted straight from cobble (3 cobblestone in a row → 3 slabs), no furnace. A smooth stone slab (44:0, "stone slab") needs stone, which is cobble smelted first. A cobblestone slab and a stone slab never stand in for each other in a build.
- Furnace: 10 s per item. Coal or charcoal smelts 8 items, a plank or log 1.5, a stick 0.5. Smelt logs into charcoal for torches.
- Tool quirks: glowstone and anything stone-like drop nothing without a pickaxe. The axe only speeds up logs, planks, chests and bookshelves. Stairs drop planks or cobble, not stairs. Bookshelves drop nothing.
