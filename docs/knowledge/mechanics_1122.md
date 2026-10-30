# Minecraft 1.12.2 mechanics — field notes for any world

<!-- pack version=1.12.2 -->
<!--
Knowledge convention (MineClaw RAG): the block between the "core" and "/core" HTML-comment markers below is always
in the bot's cached system prompt, so keep it short (most important rules only). Everything else is split by heading
into ~150-300-token chunks and retrieved on demand (auto [memory] lines or the recall tool). A file without core
markers contributes its first section as core. Editing a file re-indexes it automatically at the next start.

Pack marker: "pack version=1.12.2" puts this file in the 1.12.2 version pack (shards/defaults/1.12.2-gamedata.mcshard):
mechanics that hold in every 1.12.2 world. World-mode advice (skyblock, minigames, server rules) belongs in that
mode's own doc, never here. HTML comments are stripped by the indexer, so each fact's source class sits in a comment
right after it. Facts were read from the decompiled 1.12.2 client (MCP names).
-->

<!-- core -->
## Core rules (1.12.2)
- These 1.12.2 facts beat general Minecraft knowledge (older and newer versions differ).
- Health regenerates only while food is 18+, fastest at a full 20 with saturation left; every heal costs hunger. Eat before long jobs. <!-- FoodStats.onUpdate -->
- Combat has a cooldown: wait for the weapon to recharge (sword about 0.6 s, axe about 1-1.25 s). Spam-clicking deals as little as 20% damage. <!-- EntityPlayer.attackTargetEntityWithCurrentItem, getCooldownPeriod -->
- Ores need a pickaxe tier or drop nothing: stone+ for iron and lapis, iron+ for gold, diamond, emerald and redstone, diamond for obsidian. <!-- ItemPickaxe.canHarvestBlock -->
- Hostile mobs spawn at light 7 or less on solid, opaque, full-top blocks, never within 24 blocks of a player. Light everything to 8+ to stop them. <!-- EntityMob.isValidLightLevel, WorldEntitySpawner -->
- Beds work only at night or in a thunderstorm with no hostile mob within 8 blocks; they set your spawn, and they explode in the Nether and the End. <!-- EntityPlayer.trySleep, BlockBed.onBlockActivated -->
<!-- /core -->

## Hunger, saturation and regeneration
- Food bar 20, starting saturation 5. Saturation can never exceed the current food level. <!-- FoodStats fields, addStats -->
- Each time exhaustion passes 4.0, it drops by 4 and you lose 1 saturation; only once saturation is 0 do you lose 1 food point (not on Peaceful). Exhaustion is capped at 40. <!-- FoodStats.onUpdate, addExhaustion -->
- Fast regen: at food 20 with saturation left, heal every 10 ticks by saturation/6 (max 1 health), costing that much saturation as exhaustion. <!-- FoodStats.onUpdate -->
- Slow regen: at food 18+, heal 1 health (half a heart) every 80 ticks (4 s), costing 6 exhaustion. Both need the naturalRegeneration gamerule. <!-- FoodStats.onUpdate -->
- Starving at food 0: 1 damage every 4 s. Easy stops at 10 health (5 hearts), Normal at 1 (half a heart), Hard can kill. <!-- FoodStats.onUpdate -->
- Exhaustion costs: sprinting 0.1 per metre; swimming 0.01 per metre; jump 0.05; sprint-jump 0.2; breaking a block 0.005; attacking 0.1; taking damage 0.1 (0 for armour-bypassing damage such as falls, drowning, fire ticks, starving); Hunger effect 0.005 per level per tick. Walking and sneaking cost nothing. <!-- EntityPlayer.addMovementStat, jump, attackTargetEntityWithCurrentItem, damageEntity; Block.harvestBlock; DamageSource.setDamageBypassesArmor; Potion.performEffect -->
- Sprinting needs food above 6. <!-- EntityPlayerSP.onLivingUpdate -->

## Food values
Hunger points / saturation gained (saturation = hunger × modifier × 2). <!-- Item.registerItems ItemFood registrations, ItemFishFood.FishType, ItemSoup, FoodStats.addStats -->
- Best: golden carrot 6 / 14.4; steak and cooked porkchop 8 / 12.8; rabbit stew 10 / 12; cooked mutton 6 / 9.6; cooked salmon 6 / 9.6; golden apple 4 / 9.6.
- Good: cooked chicken 6 / 7.2; mushroom stew and beetroot soup 6 / 7.2; bread, baked potato, cooked cod, cooked rabbit 5 / 6; pumpkin pie 8 / 4.8.
- Poor: carrot 3 / 3.6; apple and chorus fruit 4 / 2.4; raw beef, porkchop, rabbit 3 / 1.8; melon slice 2 / 1.2; raw chicken and mutton 2 / 1.2; beetroot 1 / 1.2; potato 1 / 0.6; cookie 2 / 0.4; raw cod or salmon 2 / 0.4; cake slice 2 / 0.4 (7 slices).
- Rotten flesh 4 / 0.8 with an 80% chance of Hunger for 30 s; raw chicken has a 30% chance of the same. <!-- Item.registerItems setPotionEffect -->
- Spider eye 2 / 3.2 always poisons for 5 s; poisonous potato 2 / 1.2 poisons 60% of the time. <!-- Item.registerItems -->
- Pufferfish 1 / 0.2: Poison IV 60 s, Hunger III 15 s, Nausea II 15 s. Never eat it. <!-- ItemFishFood.onFoodEaten -->
- Golden apple: Regeneration II 5 s + Absorption 2 min. Enchanted (meta 1): Regeneration II 20 s, Resistance and Fire Resistance 5 min, Absorption IV 2 min. Both can be eaten on a full bar. <!-- ItemAppleGold.onFoodEaten -->
- Chorus fruit (edible when full) teleports you up to 8 blocks in each direction to a safe spot, then a 1 s cooldown. <!-- ItemChorusFruit.onItemUseFinish -->

## Combat: cooldown, crits, sweeps, shields
- Cooldown ticks = 20 / attack speed. Hit damage × (0.2 + 0.8 × charge²), charge 0-1; enchantment bonus damage scales by charge. <!-- EntityPlayer.getCooldownPeriod, getCooledAttackStrength, attackTargetEntityWithCurrentItem -->
- Fist: speed 4, damage 1. Swords: speed 1.6 (12.5 ticks), damage wood 4, stone 5, iron 6, diamond 7, gold 4. <!-- SharedMonsterAttributes.ATTACK_SPEED, EntityPlayer.applyEntityAttributes, ItemSword -->
- Axes: wood 7 dmg / 0.8 speed, stone 9 / 0.8, iron 9 / 0.9, diamond 9 / 1.0, gold 7 / 1.0. Pickaxe speed 1.2 (dmg 2-5), shovel 1.0 (2.5-5.5), hoe 1 (wood, gold) to 4 (diamond) with dmg 1. <!-- ItemAxe ATTACK_DAMAGES/ATTACK_SPEEDS, ItemPickaxe, ItemSpade, ItemHoe -->
- Critical hit (×1.5): charge > 0.9, falling, not on the ground, not on a ladder or vine, not in water, not blind, not riding, not sprinting. <!-- EntityPlayer.attackTargetEntityWithCurrentItem -->
- Sprint hit: at charge > 0.9 while sprinting, adds one level of knockback and ends your sprint (and is never a crit). <!-- EntityPlayer.attackTargetEntityWithCurrentItem -->
- Sweep (swords only): charge > 0.9, on the ground, not a crit or sprint hit, barely moving. Hits other mobs within 1 block of the target and 3 of you for 1 damage (more with Sweeping Edge). <!-- EntityPlayer.attackTargetEntityWithCurrentItem -->
- Shield: blocks all blockable damage from in front once raised for 5 ticks. Damage that bypasses armour (falls, fire, drowning) isn't blocked. A blocked melee hit from anyone holding an axe (players, vindicators) disables it for 5 s. <!-- EntityLivingBase.isActiveItemStackBlocking, canBlockDamageSource, attackEntityFrom; EntityPlayer.blockUsingShield, disableShield -->

## Hostile spawning, light and despawning
- Light test: if sky light at the feet > random(0-31) the spawn fails; then the block light (including daylight) must be ≤ random(0-7). So light 7 is the most a mob can spawn in, and darker is likelier. Thunderstorms count as night. <!-- EntityMob.isValidLightLevel -->
- The block below must be top-solid: opaque full cube, a top slab, upside-down stairs or a full snow block. Not glass, bottom slabs, leaves, bedrock or barrier. The spawn cell and the one above must be non-solid, non-liquid, not rails or redstone power sources. <!-- WorldEntitySpawner.canCreatureTypeSpawnAtLocation, isValidEmptySpawnBlock; Block/BlockSlab/BlockStairs/BlockSnow.isTopSolid -->
- Never within 24 blocks of a player or of the world spawn point. Packs pick spots up to 5 blocks around a random point. <!-- WorldEntitySpawner.findChunksForSpawning -->
- Caps (for the 17×17 chunks around a lone player): hostile 70, animals 10, bats 15, squid 5. Animals are only attempted every 400 ticks, need grass (mooshrooms mycelium) below and light above 8. <!-- EnumCreatureType, WorldEntitySpawner MOB_COUNT_DIV, WorldServer.tick, EntityAnimal.getCanSpawnHere -->
- Despawning: a mob more than 128 blocks from every player vanishes at once; after 30 s with no player within 32 blocks it has a 1-in-800 chance per tick. Animals never despawn. <!-- EntityLiving.despawnEntity, EntityAnimal.canDespawn -->
- Slimes: in slime chunks (1 in 10) below Y 40 at any light, or in swamps at Y 51-69 in darkness, more often near a full moon. Not on Peaceful. <!-- EntitySlime.getCanSpawnHere -->

## Finding animals to hunt
- Passive animals (cows, pigs, sheep, chickens) appear mostly when a chunk is first generated; after that a world tick gives
  them a chance only once every 400 ticks (20 s), and only up to the creature cap. Around a base they get hunted out and
  stay gone. <!-- WorldEntitySpawner.performWorldGenSpawning; WorldServer.tick: getWorldTotalTime() % 400 == 0 -->
- A body only sees animals in its loaded chunks (hive bodies: about 64 blocks). To hunt, walk out into fresh land in a
  direction of your own (scout('cow')), each hunter a different way, then bring the meat home before night.
- Breeding (wheat for cows and sheep, carrots for pigs, seeds for chickens) is the way to keep food near home.

## Farming and breeding
- Random ticks: 3 per 16×16×16 section per game tick, so any block gets one about every 68 s. <!-- GameRules randomTickSpeed, WorldServer.updateBlocks -->
- Wheat, carrots, potatoes, beetroot need light 9+ to grow. Each random tick advances one stage with chance 1/(floor(25/f)+1): f is 4 on wet farmland alone, up to 10 when all 8 surrounding cells are wet farmland, about 1 in 3 per tick. Halved if the same crop is beside it on two axes or on a diagonal, so plant single rows or alternate crops. <!-- BlockCrops.updateTick, getGrowthChance -->
- Bone meal adds 2-5 stages (beetroot 0-1). Beetroot skips a third of its random ticks. <!-- BlockCrops.getBonemealAgeIncrease, BlockBeetroot -->
- Farmland stays wet with water within 4 blocks horizontally (same level or one above) or rain. Dry farmland with nothing planted turns to dirt. Walking never tramples it; landing on it does, with chance (fall distance − 0.5), for players and large mobs. <!-- BlockFarmland.updateTick, hasWater, onFallenUpon -->
- Sugar cane: on grass, dirt or sand with water directly beside that block. Cactus: on sand, no solid block or lava on any side, no liquid above; it deals 1 damage on contact. Both grow to 3 tall, one block per 16 random ticks. <!-- BlockReed, BlockCactus -->
- Pumpkin and melon stems grow like crops; when ripe, each growth roll picks a random side and places the fruit there if it's air over farmland, dirt or grass, unless a fruit already touches the stem. <!-- BlockStem.updateTick -->
- Nether wart: soul sand only, any light, 1 in 10 per random tick, 4 stages; bone meal does nothing. <!-- BlockNetherWart -->
- Saplings: light 9+, 1 in 7 per random tick, two stages before the tree; bone meal advances a stage 45% of the time. <!-- BlockSapling.updateTick, grow, canUseBonemeal -->
- Breeding food: cows, mooshrooms, sheep wheat; pigs carrot, potato, beetroot; chickens any seeds; rabbits carrot, golden carrot, dandelion; tamed wolves any meat; tamed ocelots raw fish; tamed horses and donkeys golden carrot or golden apple; tamed llamas hay bales. Parents wait 5 min; babies grow up in 20 min. <!-- EntityAnimal.isBreedingItem overrides, AbstractHorse.handleEating, EntityLlama.handleEating, EntityAIMate -->

## Water and lava
- Water flows 7 blocks and ticks every 5 game ticks. Lava flows 3 blocks every 30 ticks in the Overworld, 7 blocks every 10 ticks in the Nether. <!-- BlockDynamicLiquid.updateTick, BlockLiquid.tickRate -->
- A flowing water cell becomes a source when 2+ sources touch it sideways and the block below is solid or a water source (infinite water: 2×2 or a 3-long trough with sources at both ends). <!-- BlockDynamicLiquid.updateTick -->
- Water touching a lava source turns it into obsidian; touching flowing lava near its source (level 1-4) turns it into cobblestone. Lava flowing down onto water makes stone. <!-- BlockLiquid.checkForMixing, BlockDynamicLiquid.updateTick -->
- Lava sets fires without touching: air up to 2 blocks above it next to something flammable, or on top of flammable blocks beside it. Keep wood away. <!-- BlockStaticLiquid.updateTick -->

## Trees, leaves, gravel and grass
- Leaves decay when no log is within 4 blocks along the leaves; leaves you placed never decay. <!-- BlockLeaves.updateTick -->
- A decaying or broken leaf drops a sapling 1 in 20 (jungle 1 in 40); Fortune I/II/III make it 1 in 16/12/10 (jungle 36/32/24). Oak and dark oak also drop an apple 1 in 200 (180/160/120 with Fortune). Shears drop the leaf block itself. <!-- BlockLeaves.dropBlockAsItemWithChance, BlockOldLeaf, BlockNewLeaf -->
- Gravel drops flint 1 in 10; Fortune I 1 in 7, II 1 in 4, III always. <!-- BlockGravel.getItemDropped -->
- Grass (tall grass) drops wheat seeds 1 in 8, never beetroot seeds; Fortune raises the count. Shears give the grass block. <!-- BlockTallGrass.getItemDropped, quantityDroppedWithBonus, harvestBlock -->

## Tools and digging
- Tiers (harvest level, uses, speed, extra damage): wood 0, 59, 2, 0; stone 1, 131, 4, 1; iron 2, 250, 6, 2; diamond 3, 1561, 8, 3; gold 0, 32, 12, 0. <!-- Item.ToolMaterial -->
- Any pickaxe: stone, cobble, coal ore, quartz ore and other rock. Stone+: iron and lapis. Iron+: gold, diamond, emerald, redstone. Diamond only: obsidian. Rock, metal blocks, snow and cobwebs drop nothing without the right tool. <!-- ItemPickaxe.canHarvestBlock, Material.setRequiresTool, InventoryPlayer.canHarvestBlock -->
- Dig progress per tick = speed / hardness / 30, or / 100 if the tool can't harvest the block (3.3× slower). Speed is 1 by hand or the tool tier's speed on its blocks; Efficiency adds level² + 1; Haste adds 20% per level. Underwater without Aqua Affinity and while airborne each divide speed by 5. <!-- Block.getPlayerRelativeBlockHardness, EntityPlayer.getDigSpeed -->
- Pickaxe: anything of rock or metal, plus rails. Axe: wood, plants and vines, pumpkins, melons, ladders, wooden buttons and plates. Shovel: dirt, grass, farmland, path, sand, gravel, clay, mycelium, soul sand, snow, concrete powder; snow needs one to drop. Swords and shears cut cobwebs; shears also cut leaves and wool fast. <!-- ItemPickaxe/ItemAxe/ItemSpade EFFECTIVE_ON and getDestroySpeed, ItemSword, ItemShears -->

## Beds and sleep
- You can sleep only when it is dark enough (night or a thunderstorm), within 3 blocks horizontally and 2 vertically of the bed, with no hostile mob within 8 blocks horizontally and 5 vertically (zombie pigmen count only when angry). <!-- EntityPlayer.trySleep, bedInRange; World.isDaytime; EntityMob/EntityPigZombie.isPreventingPlayerRest -->
- Lying down then getting up (or waking at dawn) sets your spawn at the bed. If the bed is gone or boxed in when you die, you respawn at world spawn. <!-- EntityPlayer.wakeUpPlayer, getBedSpawnLocation; NetHandlerPlayServer STOP_SLEEPING -->
- In the Nether or the End a bed explodes (power 5, with fire) when used. <!-- BlockBed.onBlockActivated -->
- When every non-spectator player has slept 5 s, time jumps to the next dawn and rain and thunder clear. <!-- WorldServer.areAllPlayersAsleep, tick, wakeAllPlayers; EntityPlayer.isPlayerFullyAsleep -->

## Falling, drowning and fire
- Fall damage is 1 per block beyond 3 (Jump Boost raises the safe height by its level). <!-- EntityLivingBase.fall -->
- You have 15 s of air underwater, then take 2 damage per second. <!-- EntityLivingBase.onEntityUpdate -->
- Burning deals 1 damage per second; lava deals 4 per hit and sets you on fire for 15 s. <!-- Entity.onEntityUpdate, setOnFireFromLava -->
