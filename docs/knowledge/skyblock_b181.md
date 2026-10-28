# SkyBlock on Beta 1.8.1 — field notes

<!-- pack mode=skyblock version=b1.8.1 name=skyblock -->
<!--
Knowledge convention (MineClaw RAG): the block between the "core" and "/core" HTML-comment markers below is always
in the bot's cached system prompt, so keep it short (most important rules only). Everything else is split by heading
into ~150-300-token chunks and retrieved on demand (auto [memory] lines or the recall tool). A file without core
markers contributes its first section as core. Editing a file re-indexes it automatically at the next start.

Pack marker: "pack mode=skyblock" puts this file in the skyblock mode pack (shards/defaults/skyblock.mcshard): only what
is true because the world is a skyblock (the void, the starter island, unrenewables, challenges). A world without the
skyblock mode leaves this file out of the prompt and of retrieval. Mechanics that hold in any b1.8.1 world (hunger,
fluids, light and spawning, farming, crafting quirks) live in mechanics_b181.md (the version pack).
-->

<!-- core -->
## Core rules
- The void is below: falling under y=0 kills you and the items are gone for good. Stay 1+ block from edges, sneak near edges, never dig the block you stand on, don't walk off the island, no bridges to land.
- Unrenewable: the single lava source (water onto a lava SOURCE makes obsidian, no undo), dirt (keep at least one grass block), sand and clay, the one bucket, saplings (always replant; catch them before they fall).
- Cobble generator: build it away from wood. Water touching flowing lava makes cobblestone; mine that cell and it refills. Wall it in and test the water side before placing the lava; if the layout looks wrong, scoop the lava back at once.
- Infinite water: two sources in opposite corners of a 2x2 hole, 1 deep. Never let it near the lava.
<!-- /core -->

## Start
- Noobcrew's original map (Sept 2011, v1.0/1.1): an L-shaped island of about 27 dirt/grass blocks with one oak tree and a chest. There are no other islands. v2.x adds a sand island and the Nether.
- v1 chest (per wiki): 12 string, 1 lava bucket, 1 bone, 1 sugar cane, 1 red + 1 brown mushroom, 2 ice, 1 pumpkin seeds, 1 melon slice, 1 cactus. **[unverified for this server — open the chest and check]**. Server plugins often use their own starter chest.
- Rules: don't jump off on purpose to heal, don't build bridges to land, play on Easy or harder.
- The void: falling below y=0 kills you and the items are **gone for good**. Before your first death, stay ≥1 block from edges, sneak near edges, and never dig the block you stand on.

## Challenges (v1.0/1.1 list, paraphrased)
Cobble generator; house; expand the island; farms for melon, pumpkin, sugar cane and wheat; giant red mushroom; bed; 40 stone bricks; 20 torches; infinite water source; furnace; small lake; mob platform 24 blocks from the island; 10 each of cactus green, mushroom stew, jack-o'-lanterns, bookshelves, bread, ender pearls and cooked fish; 10 each of black, gray, light gray, lime, red, yellow, pink, green and orange wool; 10 snow golems; 20 paintings. **Snow golems don't exist in b1.8.1, so skip that one. The list may have been edited after release.**

## Precious, unrenewable things
- **The single lava source.** Water running onto it turns it into obsidian, and there's no getting the lava back. If a layout looks wrong, scoop the lava back up with the empty bucket at once.
- **Dirt.** b1.8.1 can't make dirt and grass only spreads onto dirt you already have, so the island's dirt is all there is. Keep at least one grass block. Sand and clay can't be obtained either (v1). Iron doesn't exist on v1 (zombies drop rotten flesh), so **the one bucket is the only bucket**.
- **Saplings.** Always replant (1 in 20 leaves drops one; shears give no sapling).
- Endermen can take island blocks (grass, dirt, sand, cactus, pumpkins, melons…). Light up the island and fight them away from it.

## Core builds
- **Cobble generator** (build it away from wood, because lava sets nearby flammable blocks alight): dig a 1-deep straight trench 4 long. Make the 3rd cell 2 deep. Pour water into the 4th cell; it falls into the pit. Put lava in the 1st cell; it flows into the 2nd, touches the water and turns to cobblestone. Mine the 2nd cell repeatedly and it refills. Wall it in first, test the water side, then place the lava. **[Layout is the classic one, but test it before committing the lava]**
- **Infinite water**: the two starter ice blocks give two sources (break each on a solid block). Put the sources in opposite corners of a 2×2 hole, 1 deep, with a solid floor. Never let this water near the lava.
- **Trees**: catch saplings with a floor or walls so they don't fall into the void.
- **Mob platform**: build it 24+ blocks out from the island (mobs never spawn closer than 24). Light the main island fully so nothing spawns there; creepers blow up dirt.
