# SkyBlock on Beta 1.8.1 — field notes

<!--
Knowledge convention (MineClaw RAG): the block between the "core" and "/core" HTML-comment markers below is always
in the bot's cached system prompt, so keep it short (most important rules only). Everything else is split by heading
into ~150-300-token chunks and retrieved on demand (auto [memory] lines or the recall tool). A file without core
markers contributes its first section as core. Editing a file re-indexes it automatically at the next start.
-->

<!-- core -->
## Core rules
- The void is below: falling under y=0 kills you and the items are gone for good. Stay 1+ block from edges, sneak near edges, never dig the block you stand on, don't walk off the island, no bridges to land.
- Unrenewable: the single lava source (water onto a lava SOURCE makes obsidian, no undo), dirt (keep at least one grass block), sand and clay, the one bucket, saplings (always replant; catch them before they fall).
- Cobble generator: build it away from wood. Water touching flowing lava makes cobblestone; mine that cell and it refills. Wall it in and test the water side before placing the lava; if the layout looks wrong, scoop the lava back at once.
- Infinite water: two sources in opposite corners of a 2x2 hole, 1 deep. Never let it near the lava.
- Health regenerates only while food is 18+; eat before long jobs.
- Food in b1.8.1: bread (3 wheat), melon slices, mushroom stew (bowl + red + brown mushroom), cake, cookies; pork (pigs), raw beef (cows), raw chicken (chickens), rotten flesh (zombies), fish by fishing; cook meat/fish in a furnace. Leaves drop only saplings: apples, so golden apples too, come only from dungeon chests.
- The seeded b1.8.1 knowledge and game data win over general Minecraft knowledge (later versions differ).
- Saplings can be **planted** any time, night included, on dirt/grass/farmland that can see the sky (or has light ≥8), so night alone never blocks planting. They only **grow** at light ≥9, so at night growth pauses unless torches are nearby.
<!-- /core -->

## Start
- Noobcrew's original map (Sept 2011, v1.0/1.1): an L-shaped island of about 27 dirt/grass blocks with one oak tree and a chest. There are no other islands. v2.x adds a sand island and the Nether.
- v1 chest (per wiki): 12 string, 1 lava bucket, 1 bone, 1 sugar cane, 1 red + 1 brown mushroom, 2 ice, 1 pumpkin seeds, 1 melon slice, 1 cactus. **[unverified for this server — open the chest and check]**. Server plugins often use their own starter chest.
- Rules: don't jump off on purpose to heal, don't build bridges to land, play on Easy or harder.
- The void: falling below y=0 kills you and the items are **gone for good**. Before your first death, stay ≥1 block from edges, sneak near edges, and never dig the block you stand on. Fall damage is 1 half-heart per block beyond 3.

## Challenges (v1.0/1.1 list, paraphrased)
Cobble generator; house; expand the island; farms for melon, pumpkin, sugar cane and wheat; giant red mushroom; bed; 40 stone bricks; 20 torches; infinite water source; furnace; small lake; mob platform 24 blocks from the island; 10 each of cactus green, mushroom stew, jack-o'-lanterns, bookshelves, bread, ender pearls and cooked fish; 10 each of black, gray, light gray, lime, red, yellow, pink, green and orange wool; 10 snow golems; 20 paintings. **Snow golems don't exist in b1.8.1, so skip that one. The list may have been edited after release.**

## Precious, unrenewable things
- **The single lava source.** Water running onto a lava *source* turns it into obsidian, and there's no getting the lava back. Water touching *flowing* lava makes cobblestone. Lava flowing down onto water makes stone. If a layout looks wrong, scoop the lava back up with the empty bucket at once.
- **Dirt.** b1.8.1 has no way to make dirt. Grass only spreads onto dirt you already have (light ≥9 above). Keep at least one grass block. Sand and clay can't be obtained either (v1). Iron doesn't exist on v1 (zombies drop rotten flesh), so **the one bucket is the only bucket**.
- **Saplings.** Always replant. Oak leaves drop a sapling 1 time in 20 when they decay or are broken. Shears get you the leaf block instead of the chance at a sapling.
- Endermen can pick up grass, dirt, sand, gravel, flowers, mushrooms, cactus, clay, pumpkins, melons and TNT, so they can take island blocks. Light up the island and fight them away from it. I'm not sure whether they drop a carried block when killed in b1.8.

## Core builds
- **Cobble generator** (build it away from wood, because lava sets nearby flammable blocks alight): dig a 1-deep straight trench 4 long. Make the 3rd cell 2 deep. Pour water into the 4th cell; it falls into the pit. Put lava in the 1st cell; it flows into the 2nd, touches the water and turns to cobblestone. Mine the 2nd cell repeatedly and it refills. Wall it in first, test the water side, then place the lava. **[Layout is the classic one, but test it before committing the lava]**
- **Infinite water**: two ice blocks give two sources. Break ice that sits on a solid block and it becomes water; ice drops no item. Put the sources in opposite corners of a 2×2 hole, 1 deep, with a solid floor. Any cell you take from refills. Never let this water near the lava.
- **Trees**: plant saplings on dirt, grass or farmland with space (a big oak needs a lot). A sapling can be **planted** any time it can see the sky (or the light there is ≥8), so night is no reason to skip a replant. It only **grows** when the light at the sapling is ≥9 (a 1-in-30 check per random tick), so at night growth pauses unless torches are nearby. Leaves decay once the logs are cut. Catch saplings with a floor or walls so they don't fall into the void.
- **Sugar cane**: plant on grass, dirt or sand with water directly beside it horizontally. Grows to 3 tall; harvest the top 2. Paper is 3 canes for 3 paper; a book is 3 paper (no leather in beta); a bookshelf is 6 planks + 3 books.
- **Cactus**: only on sand, with no blocks touching its sides. Grows to 3 tall and hurts on contact. Smelt it for cactus green. It also **destroys dropped items that touch it** (item entities take its contact damage), so a cactus mob grinder loses most of its loot. To keep drops: kill by fall damage (23+ blocks, or a shorter drop and a finishing hit) onto plain blocks, or let water carry drops away from the cactus to a collection point. Mobs can't spawn on half slabs.
- **Wheat**: seeds come from tall grass (1 in 8). Use bone meal on grass to grow tall grass. Hoe dirt within 4 blocks of water. Harvest only when fully grown (stage 7/7).
- **Farming procedure (Fox's order, every cell): harvest → hoe → plant.** Always hoe before planting, even when the cell still looks like farmland: farmland turns back to dirt when it dries out with nothing planted, or when someone walks on it (1 in 4 per step, not when sneaking). A hoe on farmland does nothing, so re-tilling is safe. Get a hoe first if you have none (2 sticks + 2 planks or cobble). Sneak across the field, never jump onto it. `harvest_wheat` does all of this.
- **Pumpkin/melon**: plant the stem on farmland. The fruit appears on a free neighbouring block that has dirt, grass or farmland under it. Harvest the fruit, keep the stem. A melon block drops 3–7 slices; 1 slice crafts 1 seed. b1.8 has no recipe for pumpkin seeds, so keep the stem alive.
- **Mushrooms** (mc-dev BlockMushroom / WorldGenHugeMushroom): a small mushroom never grows taller. It needs light below 13 and an opaque block under it; in brighter light it pops off. It **spreads** instead: each random tick has a 1-in-100 chance to put a copy on a free spot within 1 block sideways and ±1 up or down (same light and floor rules). Spreading stops once 5 of that colour are within 4 blocks. To farm them, put dark, opaque floor next to them. **Bone meal** turns it into a huge mushroom only when it sits on dirt or grass and a 7×7 column of air (leaves are fine) is clear above it for 5–8 blocks; otherwise the bone meal does nothing. A huge mushroom turns the block under it into dirt.
- **Mob farm**: a cobblestone platform 24+ blocks from where you stand (mobs never spawn closer than 24). Hostile mobs spawn on solid opaque blocks at light ≤7. Add 1-wide walls/channels and a drop for the kill. Drops: zombie → rotten flesh; skeleton → bones (bone meal) and arrows; spider → string (4 string = wool → beds/paintings); creeper → gunpowder; enderman → ender pearl. Light the main island fully so nothing spawns there; creepers blow up dirt.
- **Fishing**: rod = 3 sticks + 2 string. Raw fish smelts into cooked fish.
- **Slabs are kinds, not one item**: a cobblestone slab (44:3) is crafted straight from cobble (3 cobblestone in a row → 3 slabs), no furnace. A smooth stone slab (44:0, "stone slab") needs stone, which is cobble smelted first. A cobblestone slab and a stone slab never stand in for each other in a build.

## b1.8 mechanics
- Hunger bar is 20 (10 shanks). Health regenerates only while food ≥18. At 0 food you starve (Easy stops at 5 hearts, Normal at ½ heart, Hard can kill). **[thresholds from memory]**. Sprinting (double-tap forward) needs food >6 and burns hunger fast, as do jumping and mining.
- Food (hunger points): steak/pork 8, stew 8, cooked chicken 6, bread 5, cooked fish 5, apple 4, rotten flesh 4 (80% chance of food poisoning), raw chicken 2 (30%), melon 2, cookie 1. Peaceful regenerates health and has no hostile mobs.
- Animals don't despawn in b1.8 and spawn rarely, on grass in light. **[unsure how often]**
- Water flows 7 blocks, lava 3 (lava flows slowly). A water source forms only between ≥2 sources with solid ground or water below.
- Fire spreads to wood, planks, leaves and wool. Lava can ignite flammable blocks nearby even without touching them.
- Beds skip the night and set your spawn. Torches stop mob spawns nearby.
- Furnace: 10 s per item. Coal or charcoal smelts 8 items, a plank or log 1.5, a stick 0.5. Smelt logs into charcoal for torches.
- Tool quirks: glowstone and anything stone-like drop nothing without a pickaxe. The axe only speeds up logs, planks, chests and bookshelves. Stairs drop planks or cobble, not stairs. Bookshelves drop nothing.
