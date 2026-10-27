# Game mechanics audit against mc-dev (Beta 1.8)

Source of truth: Bukkit **mc-dev** at commit `218a2a71673bc81741d2bd1b83600f1b35f06024` (the decompiled 1.8 server),
`net/minecraft/server/<Class>.java`. Every reference below has the form `Class.java:line` in that tree. The parkour
audit had already covered jump velocity and the sprint boost, ground and air friction, gravity and drag, the ladder
clamp, the basic liquid drags, the sneak edge clamp, the onGround rule, sneak step-up, exhaustion costs and the base
acceleration and sprint factors. Those are only cross-referenced here.

Status values:

- **✓**: ours matches mc-dev.
- **fixed**: ours was wrong and is fixed here, with a regression test.
- **added**: the mechanic was missing and is now modelled.
- **client-only-unverified**: the behaviour isn't in server code. See the list at the end.

Regression tests:

- `tests/MineClaw.World.Tests/MechanicsAuditTests.cs`
- `tests/MineClaw.Client.Tests/MechanicsAuditTests.cs`
- `tests/MineClaw.Data.Tests/DataTests.cs` (`McDevSpotChecks`, plus the extra assertions in `BlockTests.Collision`)

## Safety-relevant corrections (read these first)

1. **Shallow water does not break long falls.** b1.8 resets the fall distance only when the per-tick water check
   (`Entity.java:563-565`, run before the move) sees the feet in water. The server runs that check at the previous
   packet's position (`NetServerHandler.java:165`) and then applies the client's onGround flag (`:235`). The detection
   window is `[floor, waterTop + 0.6)`. The tick step at a fall of 3.35 blocks is already 0.65, so 1-deep water is no
   better than dry ground: a 10-block drop into it does `ceil(10 − 3) = 7` damage whenever a tick skips the window.
   The pathfinder previously allowed drops of up to 12 blocks into any water. `Safety.WaterLanding` now models this,
   and `IsDeadlyColumn`, the edge guard, `Pathfinder.ComputeDeadly` and `TryDrop` all use it. 2-deep water holds up
   to about 23 blocks.
2. **Vines are not climbable in b1.8.** `EntityLiving.p()` tests `== Block.LADDER.id` only (`EntityLiving.java:620-625`).
   Vines became climbable in 1.2. The pathfinder and physics would have planned and simulated vine climbs that really
   end in a fall.
3. **Sprint-hit knockback.** A sprinting player's hit adds `(−sin·1.0, +0.1, cos·1.0)` on top of the normal 0.4/0.4
   (`EntityHuman.java:631-636`). That throws the victim about 7.9 blocks instead of about 2.1. During a spar the
   positioning code now uses `SprintKnockbackDepth` = 9 while the opponent's sprint flag (metadata 0, bit 3) is set.
   The bot never sprint-hits: combat input clears Sprint, and the stop-sprint 0x13 goes out before the attack.
4. **Creeper blast.** The blast reaches 2·power = 6 blocks and does up to 49 damage (`Explosion.java:82-115`), not
   the 25 we had. It still does 19 at the 3-block ignite range, plus up to 1 b/t of knockback. The retreat target
   (cancel range 7 + 0.5) is outside the blast radius.
5. **Webs don't reset the fall distance in b1.8** (`BlockWeb.java:12` → `Entity.q()` at `Entity.java:1115` only sets
   the web flag). Falling onto a web over a floor still hurts on landing. Ours already matched this, and a test now
   pins it.

## 1. Collision and selection shapes (`src/MineClaw.World/Physics/BlockShapes.cs`)

In mc-dev the collision box is `Block.e(World,x,y,z)` or `a(World,..,AABB,list)`. The selection box (raycast) is the
bounds after `a(IBlockAccess,x,y,z)`.

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Slab | 0.5 | 0.5 (single), full (double) | BlockStep.java:14 | ✓ |
| Stairs collision | 2 boxes by meta 0..3 | 2 boxes: 0 `[0,.5]×.5 + [.5,1]×1` … | BlockStairs.java:35-60 | ✓ |
| Stairs selection | the 2 collision boxes | full cube | BlockStairs.java:19-20 | fixed |
| Fence | 1.5 high, selection full | 1.5, bounds full | BlockFence.java:14 | ✓ |
| Fence gate | 1.5 closed, none when open (bit 4) | same | BlockFenceGate.java:16, :53 | ✓ |
| Door | 0.1875 slab per state, state = `(m&4)==0 ? m−1&3 : m&3` | same | BlockDoor.java:61-79, :181 | ✓ |
| Trapdoor | floor 0.1875; open by `m&3` | same | BlockTrapdoor.java:35-55, :172 | ✓ |
| Ladder | 0.125 plate, meta 2..5 | same | BlockLadder.java:11-31 | ✓ |
| Vine collision | none | none | BlockVine.java:82-84 | ✓ |
| Vine selection | full cube | 1/16 plate per attached side, or under the block above | BlockVine.java:20-80 | fixed |
| Snow layer | 0.5 if `m&7 ≥ 3`, else none; selection 2(1+m)/16 | same | BlockSnow.java:13-17, :27-32 | ✓ |
| Cactus collision | inset 1/16, top 15/16 | same | BlockCactus.java:33-37 | ✓ |
| Cactus selection | the inset box | full cube | BlockCactus.java (no bounds) | fixed |
| Soul sand | collision 0.875; selection 0.875 | collision 0.875, selection full | BlockSlowSand.java:9-13 | fixed (selection) |
| Farmland | collision full; selection full | collision **full**, bounds 0.9375 | BlockSoil.java:15-17, :11 | ✓ / fixed (selection) |
| Cake | `x ∈ [(1+2m)/16, 15/16]`, top 7/16 | same; selection top 0.5 | BlockCake.java:21-28, :12-19 | ✓ / fixed (selection) |
| Bed | 0.5625 | 0.5625 | BlockBed.java:137 | ✓ |
| Cobweb | no collision | `e()` = null | BlockWeb.java:19-21 | ✓ |
| Glass pane, iron bars | full cube | connected 1/8 bars (opaque cube, same block, glass) | BlockThin.java:22-53, :91 | fixed |
| Pane selection | full cube | bounds of the arms | BlockThin.java:55-88 | fixed |
| Piston base collision | full (data) | always full, even when extended | BlockPiston.java:172-175 | ✓ |
| Piston base selection | full | shortened by 0.25 when extended (`m&8`) | BlockPiston.java:139-170 | fixed |
| Piston head | full cube | plate + arm by facing `m&7` | BlockPistonExtension.java:61-108 | fixed |
| Piston head selection | full | plate only | BlockPistonExtension.java:110-140 | fixed |
| Moving piston (36) | no collision | tile-entity shape interpolated by progress | BlockPistonMoving.java:77-90 | not modelled (transient) |
| Repeater | 0.125 | 0.125 (bounds, default `e()`) | BlockDiode.java:14 | ✓ |
| Torch, sign, plates, button, lever, rails, redstone, plants, reeds, portal, fire | no collision | `e()` = null | BlockTorch.java:12, BlockSign.java:21, BlockPressurePlate.java:23, BlockButton.java:12, BlockLever.java:9, BlockMinecartTrack.java:29, BlockRedstoneWire.java:22, BlockFlower.java:44, BlockReed.java:58, BlockPortal.java:11, BlockFire.java:33 | ✓ |
| Plant selection | 0.1–0.9 × 0.8 for all | flower/rose 0.3–0.7×0.6, mushroom ×0.4, sapling/grass/bush 0.1–0.9×0.8, crops full×0.25, reeds 0.125–0.875×1, stems 0.375–0.625×(2m+2)/16 | BlockFlower.java:11-13, BlockMushroom.java:9-11, BlockSapling.java:9-11, BlockLongGrass.java:9-11, BlockCrops.java:11-13, BlockReed.java:10-12, BlockStem.java:127-132 | fixed |
| Torch selection | centre post or a centred box | wall boxes per meta 1..4, floor 0.4–0.6×0.6 | BlockTorch.java:123-140 | fixed |
| Lever / button / wall sign / sign post selection | full cube | per-meta boxes (button 1/8, pressed 1/16) | BlockLever.java:109-125, BlockButton.java:98-120, BlockSign.java:25-50, :15-18 | fixed |
| Rail selection | 0.125 | 0.625 when ascending (meta 2..5) | BlockMinecartTrack.java:42-49 | fixed |
| Pressure plate selection | 0.0625 | 0.03125 when pressed (meta 1) | BlockPressurePlate.java:124-132 | fixed |
| Portal selection | full | 1/4 thick along the frame axis | BlockPortal.java:15-28 | fixed |
| Opaque-cube table (pane links) | — | `a()` overrides | BlockLeaves.java:146 (opaque on server), BlockGlass/BlockBreakable.java:13 … | added |

## 2. Special movement (`PlayerPhysics.cs`, `Safety.cs`, `Pathfinder.cs`)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Ice slipperiness | 0.98 | 0.98 (default 0.6) | BlockIce.java:9, Block.java:157 | ✓ |
| Soul sand | motX, motZ ×0.4 for blocks inside the hitbox shrunk by 0.001 | same | BlockSlowSand.java:15-18, Entity.java:483-500 | ✓ |
| Cobweb | dx, dz ×0.25, dy ×0.05, motion zeroed, flag per tick | same | Entity.java:274-283, BlockWeb.java:11-13, Entity.java:1115-1117 | ✓ |
| Cobweb resets fall distance | no | no | Entity.java:1115-1117 | ✓ (test) |
| Water detection | box grown (0, −0.4, 0), shrunk 0.001 | same | Entity.java:563-565, World.java:1214-1256 | ✓ |
| Water current | normalized flow sum × 0.014, Y included | same | World.java:1245-1251 | ✓ |
| Flow: side neighbour test | collision box | `!material.isSolid()` (signs and plates block, ladders and repeaters don't) | BlockFluids.java:105-112 | fixed |
| Flow: falling-water side test | full cube | material not the same, not ice, `isBuildable` (slabs, fences, glass, web count) | BlockFluids.java:60-64, :119-157, Block.java:240-242 | fixed |
| Lava detection | box grown (−0.1, −0.4, −0.1), any lava cell | same | Entity.java:588-590, World.java:1258-1279 | ✓ |
| Liquid movement | accel 0.02; drag water 0.8 / lava 0.5; −0.02; wall hop 0.3 | same | EntityLiving.java:516-540 | ✓ |
| Swim up | +0.04 in water and in lava | same | EntityLiving.java:733-737 | ✓ |
| Climbable blocks | ladder **and vine** | ladder only | EntityLiving.java:620-625 | fixed |
| Ladder clamp / climb / sneak | 0.15 / 0.2 / hold | same | EntityLiving.java:566-599 | ✓ (parkour audit) |
| Fall accumulation | `fall −= dy` when dy < 0; landing applies, then resets | same | Entity.java:528-537 | ✓ |
| Fall damage | `ceil(fall − 3)` | same | EntityLiving.java:500-512 | ✓ |
| Water resets fall | yes, whenever InWater | yes, when the tick check sees water | Entity.java:179-205 | ✓ |
| Ladder resets fall | yes | yes | EntityLiving.java:586 | ✓ |
| Water as a landing cushion | any water not over the void, drops ≤ 12 | only when a tick samples the feet in `[floor, top + 0.6)` | Entity.java:563-565, NetServerHandler.java:165, :235 | fixed |
| Server floating kick | — | 80 ticks with nothing within (grown bb, −0.55 down) and dy ≥ −1/32 | NetServerHandler.java:217-231 | ✓ (info: the bot never hovers) |

## 3. Knockback and damage (`src/MineClaw.Client/Combat/CombatRules.cs`)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Knockback | doc only: ~0.4 + 0.4 | motion /2, −0.4·dir, +0.4 Y, Y cap 0.4 | EntityLiving.java:449-462 | added (`KnockbackVelocity`, `KnockbackTravel` ≈ 2.1 blocks) |
| Velocity packet | 0x1C replaces the client's motion | sent when `velocityChanged`, which only a fresh hit sets | Entity.java:730-732, EntityLiving.java:397, EntityTrackerEntry.java:130-133 | ✓ |
| Sprint-hit knockback | not modelled | `+(−sin·1.0, 0.1, cos·1.0)`; attacker ×0.6, sprint off | EntityHuman.java:631-636 | fixed (spar depth 9) |
| Armor value | `(Σpts − 1)·Σleft / Σmax + 1` | same | InventoryPlayer.java:276-299 | ✓ |
| Armor points / durability | 3, 8, 6, 3 / 11, 16, 15, 13 ×3 << tier (gold = chain) | same | ItemArmor.java:5-6, :18; Item.java:51-70 | ✓ |
| Armor reduction | `ceil(d·(25 − a)/25)` (worst case) | `k = d·(25 − a) + carry; d = k/25`, carry `k%25` | EntityHuman.java:566-572 | ✓ (conservative) |
| Invulnerability | 20 ticks, a new full hit at ≤ 10; attack every 11 | same; a stronger hit inside the window deals the difference | EntityLiving.java:10, :362-374 | ✓ |
| Critical hit | "+rand(d/2 + 2)", max `d + d/2 + 1` | `d·3/2 + 1`, deterministic; motY < 0, not on ground, ladder or water | EntityHuman.java:622-626 | fixed (doc + `IsCritical`; max value was already right) |
| Sword blocking | `(1 + d) >> 1` | same, before armor | EntityHuman.java:562-564 | ✓ |
| Sword damage | 4 + 2·material | same | ItemSword.java:11 | ✓ |
| Tool damage | spade 1, pick 2, axe 3, + material; hoe, hand 1 | same | ItemTool.java:17, ItemPickaxe.java:8, ItemSpade.java:8, ItemAxe.java:8 | ✓ |
| Material damage, efficiency | 0/1/2/3/0, 2/4/6/8/12 | same | EnumToolMaterial.java:5 | ✓ |
| Difficulty scaling | not modelled | monster or arrow damage to a player: peaceful 0, easy d/3 + 1, normal d, hard d·3/2 | EntityHuman.java:497-510 | added (`MonsterDamageToPlayer`) |
| Creeper fuse | 30 ticks; ignite < 3, cancel ≥ 7 | same | EntityCreeper.java:91, :104 | ✓ |
| Creeper blast | 25 max | power 3 (6 charged), radius 2·power, `(int)((i² + i)/2·8·2p + 1)`, 49 max | EntityCreeper.java:106-108, Explosion.java:82-115 | fixed (`ExplosionImpact`) |
| Explosion knockback | not modelled | motion += dir·impact, impact = (1 − d/2p)·exposure ≤ 1 | Explosion.java:106-111 | added |
| Skeleton arrow | speed 1.6, spread 12, every 60 ticks within 10 | same | EntitySkeleton.java:53-67 | ✓ |
| Arrow damage | — | `ceil(2·speed)` (4 at launch), crit `·3/2 + 1` | EntityArrow.java:161-165 | ✓ |
| Arrow flight | drag 0.99, gravity 0.05, water 0.8 | same | EntityArrow.java:232-248 | ✓ |
| Server attack reach | distSq < 36 | `canSee(entity) && distSq < 36` (needs line of sight) | NetServerHandler.java:520 | ✓ (engine checks LOS) |

## 4. Hunger (`src/MineClaw.Client/Survival/HungerRules.cs`, `Items.Food`)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Start values | 20 food / 5 saturation | same | FoodMetaData.java:5-6 | ✓ |
| Eating | food + n (cap 20), sat + n·m·2 (cap food) | same | FoodMetaData.java:13-16 | ✓ |
| Exhaustion | > 4 → −4, then −1 saturation, or −1 food (not on peaceful); cap 40 | same | FoodMetaData.java:26-33, :80 | ✓ |
| Regeneration | food ≥ 18, +1 every 80 ticks | same, while health is 1..19 | FoodMetaData.java:35-40, EntityHuman.java:991-993 | ✓ |
| Starvation | every 80 ticks; floor easy 10, normal 1, hard 0, **peaceful 20** | `health > 10 \|\| diff ≥ 3 \|\| (health > 1 && diff ≥ 2)` | FoodMetaData.java:41-48 | fixed (peaceful floor 10) |
| Food values | apple 4/.3, stew 8/.6, bread 5/.6, pork 3/.3, cooked pork 8/.8, golden apple 10/1.2, fish 2/.3, cooked fish 5/.6, cookie 1/.1, melon 2/.3, beef 3/.3, steak 8/.8, chicken 2/.3, cooked chicken 6/.6, rotten flesh 4/.1 | same | Item.java:13, 35, 50, 72-75, 102-103, 110, 113, 116-120; ItemSoup.java:6 → ItemFood.java:23-25 | ✓ |
| Poisoning | raw chicken 30 %, rotten flesh 80 %, hunger 30 s | same; hunger = 0.025·(amp + 1) per tick | Item.java:118, :120; ItemFood.java:28-31; MobEffectList.java:55-56 | ✓ |
| Golden apple | always edible, regeneration 30 s | same | Item.java:75 | ✓ |
| Eat duration | 32 ticks | same | ItemFood.java:17, :36-38 | ✓ |

## 5. Blocks and digging (`src/MineClaw.Data`)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Hardness (18 spot checks; all 110 compared) | jar values | same, stairs inherit their base block | Block.java:25-133, BlockStairs.java:13, BlockPiston.java:14 | ✓ |
| Pickaxe speed list | 17 blocks (no obsidian, furnace, bricks, redstone ore) | same | ItemPickaxe.java:5 | ✓ |
| Pickaxe harvest | obsidian diamond; diamond, gold, redstone iron; iron, lapis stone; any STONE/ORE material | same | ItemPickaxe.java:11-13 | ✓ |
| Spade list and harvest | 8 blocks; only snow needs it | same | ItemSpade.java:5, :11-13 | ✓ |
| Axe list | planks, bookshelf, log, chest | same | ItemAxe.java:5 | ✓ |
| Sword, shears | sword web 15 / else 1.5, harvests web; shears 15 web/leaves, 5 wool | same | ItemSword.java:15, :46; ItemShears.java:21-25 | ✓ |
| Tool-required materials | stone, ore, snow layer, snow block, web | `Material.e()` on exactly those | Material.java:9-10, 25-26, 32, 66-69 | ✓ |
| Break progress | `canHarvest ? str/h/30 : 1/h/100`; /5 in water, /5 airborne | same; the penalties only in the harvest branch | Block.java:298-300, EntityHuman.java:401-421 | ✓ |
| Sapling drop | 1/20 | `nextInt(20) == 0` | BlockLeaves.java:124-130 | ✓ |
| Vine data flag | climbable | not climbable | EntityLiving.java:620-625 | fixed |
| Farmland collision | 1.0 | 1.0 | BlockSoil.java:15-17 | ✓ |

## 6. Item entities (`src/MineClaw.World/Entities/ItemEntityRules.cs`, collect drops)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Pickup delay, blocks and mobs | — | 10 ticks | Block.java:330, Entity.java:853 | added |
| Pickup delay, player drops | waited 1.5 s after arriving | 40 ticks | EntityHuman.java:368 | fixed (waits the remaining delay too) |
| Pickup range | \|dx\|, \|dz\|, \|dy\| ≤ 1.2 | player box grown (1, 0, 1) ∩ item box ±0.125: \|dx\| < 1.425, item Y in (feet − 0.125, feet + 1.925) | EntityHuman.java:308, EntityItem.java:14-15 | fixed (an item a block below the feet is not in range) |
| Merging | — | none in b1.8 | EntityItem.java (no merge code) | ✓ |
| Despawn | 4.5 min (Bot activity tracker) | 6000 ticks | EntityItem.java:75 | ✓ |

## 7. Mobs (`CombatRules.Profile`)

| Item | Ours (before) | mc-dev | File:line | Status |
|---|---|---|---|---|
| Monster defaults | — | health 20, damage 2; hits every 20 ticks within 2.0 with Y overlap | EntityMonster.java:5-9, :58-63 | ✓ |
| Zombie | 20 / 5 | 20 / 5 | EntityZombie.java:9 | ✓ |
| Skeleton | 20 / 4 (arrow) | 20 / `ceil(2v)` | EntitySkeleton.java:53-67 | ✓ |
| Spider | **16** / 2 | 20 / 2, leaps at 2..6 | EntityMonster.java:9, EntitySpider.java:44-62 | fixed |
| Cave spider | **12** / 2 | 20 / 2, poison 7 s (normal) or 15 s (hard) | EntityCaveSpider.java:11-33 | fixed |
| Creeper | 20 / 25 | 20 / blast ≤ 49 | EntityCreeper.java:106-108 | fixed |
| Slime | size-scaled box | health size², damage size (only size > 1), reach 0.6·size; not a monster, so no difficulty scaling | EntitySlime.java:25-30, :124 | ✓ / added (`MeleeDamageOf`) |
| Zombie pigman | 20 / 5 | 20 / 5 | EntityPigZombie.java:15 | ✓ |
| Ghast | ignored, 4×4 | 10 hp, 4×4, fireball power 1 | EntityLiving.java:32, EntityGhast.java:17, EntityFireball.java:117 | ✓ |
| Enderman | **40 / 7** | 20 / 5, 0.6×2.9 | EntityEnderman.java:14-15 | fixed |
| Enderman gaze | dot > 1 − 0.025/d to the box centre (wider cone kept) | look · (to minY + h/2 from eye) > 1 − 0.025/d, needs LOS, pumpkin helmet immune, aggro on the 6th consecutive check | EntityEnderman.java:36-72 | ✓ |
| Silverfish | **8 / 1** | 20 / 0 (hurts only on easy: 0/3 + 1) | EntitySilverfish.java:12, EntityHuman.java:497-510 | fixed |
| Wolf | 8 / 2 | wild 8 / 2, tamed 20 / 4 | EntityWolf.java:21, :286-292 | ✓ |
| Sheep | **8** | 10 (default) | EntityLiving.java:32 | fixed |
| Chicken | 0.3×**0.4** | 0.3×0.7, 4 hp | EntityChicken.java:16-17 | fixed |
| Pig / cow | 10, 0.9×0.9 / 0.9×1.3 | same | EntityPig.java:8, EntityCow.java:8 | ✓ |

## Needs a live recording check (client-only, not in mc-dev)

These live in the client (`EntityPlayerSP`, `MovementInputFromOptions`, `PlayerControllerMP`, renderer), which the
server code doesn't contain. Verify them by comparing recordings of the players' jumps and fights:

- **Sprint food rule**: no sprinting at food ≤ 6 (`HungerRules.SprintFood = 7`).
- **Sneak input ×0.3** (`PlayerPhysics.SneakMultiplier`).
- **Sprint-stop conditions**: horizontal collision, forward < 0.8, sneaking, low food, and the double-tap window
  (`PlayerPhysics.Tick`).
- **Using-item slowdown ×0.2**: blocking or eating (`CombatRules.UseItemSlowdown`).
- **Critical-hit particles** (`Packet18` animation 6) and the client crit sound.
- **Mouse-over reach 3.0** (`EntityRenderer.getMouseOver`). The server accepts up to √36.
- **Dig cooldown of 5 ticks between blocks**, and the server accepting about 70 % of the break time (`Tools` remarks).

## Counts

The tables have 114 rows:

- **✓:** 72 rows match outright.
- **Fixed:** 35. Three of these rows are marked "✓ / fixed", where the collision box was right and only the
  selection box was fixed.
- **Added:** 6. These are helpers or mechanics that were missing.
- **Not modelled:** 1, the moving piston.
- **Client-only, needs live recording check:** 7 more, listed above.
