# Multi-version: b1.8.1 and 1.12.2 behind one set of seams

Status: P0, P1 and P2 are implemented (seams + detection; 1.12.2 login, chat, game state, movement and chunks in a 256-high
world, section 5.1; inventory, windows, dig, place, use, signs and item entities, section 5.2), and P5 (game data and render,
section 5.3), P3 (physics, shapes and pathing, section 5.4) and P4 (entities, combat, effects, hunger, section 5.5) and P6 (the audit: world mode, light/spawn rules, biomes, the end-to-end normal-world scenario, section 5.6). Ground truth for 1.12.2: the decompiled client at `C:\Users\dev\source\mc-1.12.2-src`
(section 4); class and method names below are its MCP stable_39 names.
Date: 2026-10-28.

**The user's decision (verbatim):** "The new version of Minecraft that we're going to be using is this, by the way. 1.12.2.
So we'll have to get the networking set up as switchable so that we can go from this version and also back to the old
version. I think dependency injection with stubbed out interfaces is a perfect way to handle this. We detect the version at
dependency injection time and swap out the implementation for the network protocol and whatnot to be handled. That way most
of our code stays the same."

**In short:**
- One process speaks one version.
- The composition root (App) resolves the version from config or a server-list ping, before any client, body or clone exists.
- It then injects one `GameVersionProfile` (protocol adapter, physics, combat, hunger, game data; render assets beside it).
- The client never branches on the version. Version differences live inside the implementations behind the seams.
- The wire protocol is translated to and from one **canonical message model**: today's `MineClaw.Protocol.Packets` classes,
  extended with a few version-neutral messages. This is the ViaVersion approach, applied inside the client. The ~58 send
  sites and the dispatch switch therefore stay as they are.

---

## 1. Version detection

`MineClaw.Protocol.Versioning.ServerPing` makes two attempts, each on its own TCP connection with a timeout
(`Bot:Server:PingTimeoutSeconds`, default 4 s). Neither attempt logs in or takes a player slot.

1. **Modern status (1.7+).** Send a VarInt-framed Handshake (`0x00`, protocol −1, host, port, next state 1), then a Status
   Request (`0x00`). A 1.12.2 server answers a Status Response containing JSON. `version.protocol` = 340 means 1.12.2
   (`version.name` "1.12.2").
   A b1.8.1 server reads the frame as garbage. The frame's first byte is the length, which b1.8.1 reads as a packet id.
   The server then does one of three things:
   - Kicks with "Protocol error". The reply starts `FF 00`, and no canonical VarInt starts that way. A 255-byte status
     frame starts `FF 01`; a test pins this.
   - Closes the connection (EOF).
   - Waits (timeout).

   All three fall through to the legacy attempt. The cost to the b1.8.1 server is one log line per startup.
2. **Legacy (`0xFE` alone).** b1.8.1 (NetLoginHandler, packet 254) answers a `0xFF` kick carrying `motd§online§max` as
   UTF-16BE. A 1.4+ server answers `§1\0protocol\0version\0motd\0online\0max`; a 1.7+ server reports protocol 127 there, and
   the version name tells which version it is. A plain `motd§online§max` reply has no protocol number. MineClaw treats it
   as b1.8.1, the only pre-netty version it speaks.

**Resolution** (`VersionResolver`, `App/Hosting/VersionSetup`):

| `Bot:Server:Version` (or `--server-version`) | Result |
|---|---|
| `b1.8.1` / `1.12.2` (also `17`, `340`, `beta 1.8.1`, `v1.12.2`) | That version, no ping |
| `auto` (default) and the server answers | The version the ping named, saved to `<data>/server-version.txt` |
| `auto`, server down or filtered | The last detected version from that file, else b1.8.1, with a WARN. A bot started while the server is offline still starts and retries its connect as before. |
| A version MineClaw has no name for (config or server) | Exit 2 (config error) with the reason |
| A known version without an implementation | Exit 2: "recognised but not implemented" (none since P1: b1.8.1 and 1.12.2 both have profiles) |

- **Hive clones** get the resolved id on their command line (`MainHive` adds `--server-version <id>`). They never ping, so
  the whole hive speaks main's version.
- **In-process bodies** share main's profile instance.
- **Planned for P1:** a mid-run server upgrade shows up as repeated pre-login failures or an "Outdated server/client" kick.
  The reconnect loop then re-pings, and if the version changed it exits 75 (TempFail), so systemd restarts the process and
  re-resolves.

## 2. Seam inventory

These are the places that are version-specific today. Each row names the interface that goes there and its two
implementations.

### 2.1 Connection and protocol: `IProtocolAdapter` + `IGameConnection` (MineClaw.Protocol)

**Login.** Both versions run in offline mode.
- b1.8.1: `0x02` handshake(name) → `0x02` hash "-" → `0x01` login → `0x01` reply. No framing.
- 1.12.2: Handshake(340, next 2) → Login Start(name) → optional Set Compression (`0x03`, threshold) → Login Success (`0x02`,
  UUID with hyphens, name), then the Play state.
- Framing: `VarInt length | VarInt id | data`. After Set Compression it becomes `VarInt packetLen | VarInt dataLen (0 =
  uncompressed) | zlib(id + data)`. Every packet at or over the threshold must be compressed.
- The 1.12.2 connection runs the whole login itself. It surfaces Join Game (`0x23`) as the canonical `LoginRequestPacket`:
  entity id, game mode (bit 3 = hardcore), dimension, difficulty, max players. 1.12.2 sends no seed.
- `IProtocolAdapter.LoginStart(name)` returns b1.8.1's handshake and nothing for 1.12.2.

**Canonical message model.** Each row gives the b1.8.1 packet, its 1.12.2 counterpart, and what the translator does:

| Canonical (b1.8.1 class) | 1.12.2 wire | Translation notes |
|---|---|---|
| KeepAlive 0x00 | S→C `0x1F` / C→S `0x0B` (long) | Answered inside the 1.12.2 connection; never surfaced |
| PlayerPositionLook 0x0D S→C | `0x2F` (doubles, relative flags X 0x01, Y 0x02, Z 0x04, yaw 0x08, pitch 0x10; VarInt teleport id) | Resolves relative fields against the last position. Sends Teleport Confirm `0x00` itself. Emits absolute X, Stance = Y+1.62, Y. |
| PlayerPosition/Look/OnGround C→S 0x0A–0x0D | `0x0C`–`0x0F` | Stance dropped. The 1.12.2 server ignores moves before the teleport is confirmed. |
| Chat 0x03 | S→C `0x0F` (JSON component + position 0 chat / 1 system / 2 action bar), C→S `0x02` (≤ 256 chars) | JSON flattened to §-coded legacy text, so `ChatText.TryParsePlayerChat` keeps working. `translate` keys (chat.type.text, chat.type.emote, multiplayer.player.joined/left, death.*) come from the jar's `assets/minecraft/lang/en_us.lang`, read at runtime like the textures. The canonical ChatPacket gains optional `Json` / `Position` properties; action-bar lines are not chat. `IProtocolAdapter.MaxChatLength` is 100 vs 256. |
| BlockChange 0x35 / MultiBlockChange 0x34 | `0x0B` (Position, VarInt state) / `0x10` (chunk x, z; records with a VarInt state) | Block state = `id << 4 \| meta`, so it maps one to one onto (id, meta) |
| PreChunk 0x32 (unload) | Unload Chunk `0x1D` | `PreChunkPacket { Load = false }` |
| MapChunk 0x33 (zlib region) | Chunk Data `0x20` (see below) | New neutral message `ChunkSectionsMessage`: decoded per-section ids, meta, block light, sky light + mask + biomes + block entities. Dispatch applies each section through the existing `WorldState.ApplyChunkRegion`. |
| Spawn: NamedEntitySpawn 0x14, MobSpawn 0x18, AddObjectVehicle 0x17, PickupSpawn 0x15, ExperienceOrb 0x1A, Painting 0x19, Thunderbolt 0x47 | `0x05` (UUID, no name), `0x03`, `0x00`, `0x01`, `0x04`, `0x02` | The player's name comes from Player List Item `0x2E` (uuid → name map kept by the connection). Item drops are Spawn Object type 2 + metadata slot: PickupSpawn is emitted when the metadata arrives in the same tick. Positions are doubles, converted to the canonical fixed point (1/32). |
| Entity move/look/teleport 0x1F–0x22 | `0x26`/`0x27`/`0x28` (short deltas /4096), `0x4C` | Re-quantized to 1/32. A delta the canonical byte can't hold (b1.8.1 ±4 blocks vs 1.12.2 ±8) becomes a teleport. |
| EntityMetadata 0x28 | `0x3C` (typed entries: byte, VarInt, float, string, chat, slot, bool, rotation, position, opt pos, direction, opt UUID, opt block, NBT) | Per-version index tables live in the profile's entity rules (P4). The canonical `Entity.Meta` keeps the b1.8.1 indices the combat and pickup code read (creeper fuse 16, wolf flags 16, item slot). |
| Window: OpenWindow 0x64, WindowItems 0x68, SetSlot 0x67, Transaction 0x6A, WindowClick 0x66, CloseWindow 0x65, ProgressBar 0x69 | `0x13` (string type "minecraft:chest"…, JSON title), `0x14`, `0x16`, `0x11`/`0x05`, `0x07` (mode VarInt), `0x12`/`0x08`, `0x15` | Window type strings map to ints 0..3. The slot format adds NBT (TAG_End = none). The player window is the same 0..44 layout plus **offhand 45**. Click mode 0/1 = b1.8.1 click/shift; the canonical WindowClick gains `Mode`. The confirm-transaction flow (rejected → client acks → server resyncs) is the same as b1.8.1's `0x6A`. |
| PlayerDigging 0x0E | `0x14` (VarInt status 0 start / 1 cancel / 2 finish / 3 drop stack / 4 drop / 5 release use / 6 swap hands, packed Position, face) | One to one. 1 and 6 are new. |
| PlayerBlockPlacement 0x0F | `0x1F` (Position, VarInt face, **VarInt hand**, **float cursor X/Y/Z 0..1**); direction −1 → Use Item `0x20` (hand) | The canonical message gains `Hand` and `Cursor`, default main hand and the face centre. The cursor decides slab and stair halves in 1.12.2 (see the risks). |
| UseEntity 0x07 | `0x0A` (type 0 interact / 1 attack / 2 interact at, hand) | Left click = attack, right click = interact main hand |
| Animation 0x12 C→S | `0x1D` (hand) | Swing only |
| EntityAction 0x13 (1 crouch, 2 uncrouch, 3 bed, 4/5 sprint) | `0x15` (0/1 sneak, 2 bed, 3/4 sprint) | Renumbered |
| HoldingChange 0x10 | `0x1A` | Same |
| UpdateHealth 0x08 (short hp, short food, float saturation) | `0x41` (float hp, VarInt food, float saturation) | The canonical message gains a float `HealthF`. Integer hp = ceil, so 0.5 hp isn't "dead". |
| TimeUpdate 0x04 | `0x47` (world age, time of day; negative = cycle frozen) | |
| Respawn 0x09 S→C / C→S | `0x35` (dimension, difficulty, game mode, level type) / Client Status `0x03` action 0 | Dimensions are the same: −1, 0, 1 |
| NewState 0x46 | Change Game State `0x1E` | Same reasons: 1/2 rain, 3 game mode… |
| Sign 0x82 | S→C: Update Block Entity `0x09` (NBT Text1..4 JSON), C→S `0x1C` | Needs a small NBT reader (also for chunk block entities) |
| CollectItem 0x16, EntityStatus 0x26, Velocity 0x1C, Equipment 0x05, Attach 0x27, Explosion 0x3C, BlockAction 0x36, Experience 0x2B, PlayerListItem 0xC9 | `0x4B`, `0x1B`, `0x3E`, `0x3F` (slot enum incl. offhand), `0x3D` + Set Passengers `0x43`, `0x1C`, `0x0A`, `0x40`, `0x2E` (actions) | Mostly field widening |

**Chunk Data `0x20` (1.12.2):**
- Header: chunk x, z; full (ground-up) flag; VarInt primary bit mask; size.
- Per section:
  - bits per block: ≤ 4 → 4; 5..8 → a palette of VarInt states; otherwise the global palette, 13 bits.
  - A long array. Entries **do** span longs in 1.12.2; the non-spanning layout came in 1.16.
  - 2048 bytes of block light, then 2048 bytes of sky light (overworld only).
- Then 256 biome bytes when the chunk is full, and a VarInt count of block-entity NBT compounds.
- The world is 256 high: 16 sections.

**World height.**
- `ChunkColumn.SizeY` is a constant 128 today. It is used in 3 files, and 128/127 literals appear in 24 more.
- It becomes the profile's `IPhysicsRules.WorldHeight` / `GameVersion.WorldHeight`.
- A 256-high world doubles the memory of the flat byte arrays (≈ 256 KB per column vs 128 KB; every in-process body holds its
  own columns under the 2.5 GB `GCHeapHardLimit`). So P1 moves `ChunkColumn` to lazily allocated 16³ sections, where null = air.
- The light engine work (`World/Light`) touches the same class, so P1 lands after it, coordinated.

### 2.2 Physics and movement: `IPhysicsRules` (MineClaw.World)

b1.8.1 is `PlayerPhysics` + `BlockShapes`, as they are. 1.12.2 deltas, each with the decompiled source to port it from (P3):
- **Step height** 0.6F (b1.8.1: 0.5): the `EntityLivingBase` constructor (`stepHeight = 0.6F`); the step itself is
  `Entity.move` (no ySize smoothing). *Verified; P1 ships it in `Release1122PhysicsRules`.*
- **Sneak:** the box is **1.65** high while sneaking, when it fits (`EntityPlayer.updateSize`); the eye is 1.54
  (`EntityPlayer.getEyeHeight`: 1.62 − 0.08). *Source correction: this doc said the box stays 1.8; the source disagrees.*
  Edge protection: the sneak loops in `Entity.move` (offset by −stepHeight). Speed ×0.3: `MovementInputFromOptions`.
- **Sprint:** the movement-speed attribute and the sprint modifier (`EntityLivingBase.setSprinting`), the 0.2 sprint-jump
  boost (`EntityLivingBase.jump`), no sprinting at food ≤ 6 (`EntityPlayerSP.onLivingUpdate`).
- **Jump:** `EntityLivingBase.getJumpUpwardsMotion` (0.42F) and `jump`.
- **Swim:** `EntityLivingBase.travel` (water 0.8, lava 0.5, Depth Strider), `Entity.handleWaterMovement`.
- **Ladders and vines:** `EntityLivingBase.isOnLadder` and the 0.15 clamp in `travel`; sneaking holds the player in place.
- **Collision:** `Block.addCollisionBoxToList` + `Block.getActualState` per block (`BlockFence`, `BlockPane`, `BlockWall`,
  `BlockStairs.getActualState` for the corner shapes, `BlockSlab`); slipperiness is the `Block.slipperiness` field.
- **Elytra:** ignored.
- **Flying:** creative only, ignored.
- **Server checks:** "moved too quickly" and "moved wrongly" (0.0625) in `NetHandlerPlayServer.processPlayer`; the floating
  kick after 80 ticks in `NetHandlerPlayServer.update`.
- **The server ignores moves until a teleport is confirmed** (`NetHandlerPlayServer.processConfirmTeleport`). The 1.12.2
  connection confirms at once, like `NetHandlerPlayClient.handlePlayerPosLook`.
- **Position cadence** (`EntityPlayerSP.onUpdateWalkingPlayer`): the same as b1.8.1 (moved more than 0.03 or every 20 ticks /
  rotated / neither), except that the bare on-ground packet goes out only when on-ground changed. The connection applies
  that rule; the client's tick is unchanged.

Consumers that move to the instance in P3: the client tick (`MinecraftClient.Tick`), `SwimSim`, the parkour simulators,
`EdgeGuard`, `Pathfinder` move costs, `PlaceRules`/`Raycast` reach. Hot loops capture the instance once per search.

### 2.3 Combat: `ICombatRules` (MineClaw.Client)

b1.8.1 is `CombatRules`: spam clicks, 10-tick invulnerability, sword blocking, crits while falling. 1.12.2 changes (numbers
from memory; P4 ports them from `EntityPlayer.attackTargetEntityWithCurrentItem`, `EntityPlayer.getCooledAttackStrength` /
`getCooldownPeriod`, `util/CombatRules.getDamageAfterAbsorb`, `ItemSword`, `ItemShield`,
`EntityLivingBase.canBlockDamageSource` and `attackEntityFrom`):
- **Attack cooldown (1.9+):**
  - Charge s = clamp((ticks since swing + 0.5) / (20 / attack speed)). Attack speeds: hand 4.0, sword 1.6 (12.5 ticks),
    axe 0.8–1.0, pickaxe 1.2, shovel 1.0.
  - Damage × (0.2 + 0.8·s²).
  - A crit needs s > 0.9 and adds ×1.5.
  - Sprint knockback needs s > 0.9.
- **Sweep:** on the ground, not sprinting, sword, s > 0.9.
- **Shields:** offhand use, active after 5 ticks; an axe hit can disable them.
- **Damage tables:** new weapon damage (axes hit hard) and armor toughness.
- **The CombatEngine strategy changes:** it waits for the charge instead of clicking fast. `AttackCooldownTicks > 0` is the
  flag it reads.
- The server reach check differs too.

### 2.4 Hunger and regeneration: `IHungerRules` (MineClaw.Client)

b1.8.1 is `HungerRules`: +1 hp every 80 ticks at food ≥ 18, at no exhaustion cost. 1.12.2 (`FoodStats.onUpdate` and the
`EntityPlayer.addExhaustion` call sites):
- **Fast regen:** at food 20 with saturation > 0, every 10 ticks heal min(sat, 6)/6, at an exhaustion cost of min(sat, 6).
  *Verified in `FoodStats.onUpdate`.*
- **Slow regen:** food ≥ 18, every 80 ticks, at 6 exhaustion per hp. *Verified.* Starvation: every 80 ticks at food 0, down
  to 10 hp (easy), 1 hp (normal), dead (hard). *Verified.*
- **Exhaustion values (the 1.11 cuts):** jump 0.05 and sprint-jump 0.2 (`EntityPlayer.jump`, *verified*), attack 0.1, damage
  `DamageSource.getHungerDamage`, mining 0.005, sprint 0.1/m, swim 0.01/m (`EntityPlayer.addMovementStat`).
- **New and changed foods:** beetroot, chorus fruit, rabbit, mutton; the golden apple changed.

### 2.5 Respawn, dimension, time

- Same dimensions (−1, 0, 1).
- The respawn request is Client Status 0.
- Time adds world age.
- The canonical messages cover all three (2.1). A dimension change also changes the world height only in custom worlds, so
  it is ignored.

### 2.6 Block, item and recipe data: `IGameData` (MineClaw.Data)

b1.8.1 is the hand-transcribed `Blocks`/`Items`/`Recipes`/`Smelting`/`Tools`/`Drops` tables, audited against mc-dev.

1.12.2 block and item numeric ids are a **superset** of b1.8.1's: the same numbers for the old entries; blocks go up to 255,
items up to the 2267 records. So the id constants (`Blocks.Stone`) stay valid and only the lookups swap. A few ids changed
meaning: 95 was the locked chest and is stained glass in 1.12.2; stone meta 1..6 became granite, diorite and andesite.

Sources for the 1.12.2 tables:
- **Recipes:** the client jar's own `assets/minecraft/recipes/*.json`, 432 files (`crafting_shaped` / `crafting_shapeless`
  with item names + data). No download needed.
- **Blocks, items and foods** (hardness, harvest tools, stack sizes, food values): PrismarineJS minecraft-data 1.12.2, or the
  decompiled `Block`/`Item` registries.
- **Smelting** (`FurnaceRecipes`) and **block drops** (`Block.getItemDropped`): only the decompiled source. Neither is in the
  jar's data files or in minecraft-data, which has no smelting tables.

Call sites: ~1,500 `Blocks.` + ~540 `Items.` static uses. P5 makes the static facades delegate to the process profile (see 3.3):
done for `Items`, `Recipes`, `Smelting`, `Drops` and `Tools.CanHarvest`/`DigTicks` (5.3); `Blocks` stays b1.8.1's until P3 moves
physics and collision onto the profile.

### 2.7 Mobs and AI facts (Client `CombatRules.Profile`, `MobTypes`, World entity rules)

- **Mob type ids:** the same for old mobs (creeper 50 … enderman 58, cave spider 59, silverfish 60, pig 90 … wolf 95). New
  ones (witch 66, guardian 68, husk 23, stray 6, zombie villager 27, …) need profiles.
- **Metadata:** the index layout changed entirely (1.9+). An example is the creeper state at a new index.
- **AI facts to re-verify** (P4, from the decompiled source):
  - skeleton strafing and bow timing: `EntityAIAttackRangedBow.updateTask`
  - creeper fuse (30 ticks) and blast radius: `EntityCreeper.onUpdate` / `explode`
  - zombies breaking doors (hard) and reinforcements: `EntityAIBreakDoor.updateTask`, `EntityZombie.attackEntityFrom`
  - spider climbing: `EntitySpider.isBesideClimbableBlock`
  - enderman aggro by gaze: `EntityEnderman.shouldAttackPlayer`
  - mob caps and the spawn cap per chunk: `WorldEntitySpawner.findChunksForSpawning`; light: `EntityMob.isValidLightLevel`
- **Metadata indices:** the `EntityDataManager.createKey` registrations of each entity class.

### 2.8 Light and spawn rules (World/Light, Skills lighting: in progress on b1.8.1)

- 1.12.2 hostile spawning passes the same two tests:
  - sky light > rand(32) → no spawn;
  - otherwise light ≤ rand(8), with the skylight subtracted under thunder.
- New: spawning on top slabs and upside-down stairs, and the transparent-block rules.
- The light opacity of new blocks.
- The chunk packet carries the server's light, so the engine only maintains it after block changes. That part works unchanged.

### 2.9 Render: `IRenderAssets` (MineClaw.Render, registered by the Bot layer)

- **b1.8.1:** the `terrain.png` / `gui/items.png` atlases plus `BlockTextures` (id + meta + face → tile), read from the
  user's jar at runtime.
- **1.12.2:** `assets/minecraft/blockstates` (407) → `models/block` (878, with parent inheritance, elements and per-face UVs)
  → `textures/blocks` (500 single PNGs). Item models and textures (343) and entity textures (271) are all in the client jar,
  read at runtime and never committed. The atlas is stitched at load time.
- **The missing link** is the meta → blockstate property string (`facing=east,half=bottom,shape=straight`). It lives in code
  (`Block.getStateFromMeta`), not in the jar, so it needs the decompiled source or a hand-checked table for the blocks the
  bot renders.
- `TextureAtlas` already takes a version for the jar path. `BlockGeometry` gets a model-driven implementation.

### 2.10 Knowledge pack (Agent/Admin/Shards/Packs, tools/ShardPacks)

- `MinecraftDataSource` (the minecraft-data adapter) already exists.
- A `1.12.2-gamedata` version pack is built by `tools/ShardPacks` from the vendored minecraft-data, plus a new
  `mechanics_1122.md` written from the decompiled source. It is swapped in with the existing `swap-pack` import; retrieval
  only ever holds the active pack.
- The world is moving to a default (non-skyblock) world: the first 1.12.2 deploy resets memory instead
  (`deploy-alice.ps1 -ResetMemory -Pack shards/defaults/1.12.2-gamedata.mcshard`: backup, fresh db with only the 1.12.2 pack +
  sdk-js, no mode).
- **Done (P5 knowledge part):** `1.12.2-gamedata.mcshard` (vendored minecraft-data + `shards/sources/1.12.2` smelting/fuel/mob facts
  + `mechanics_1122.md`), version-aware knowledge indexing (`AgentOptions.GameVersion`), `sdk-js.mcshard`, `--reset-memory`.

### 2.11 Skills, scripts and prompts (P6 audit)

These assume b1.8.1 names, skyblock (void edges, the cobble gen) and 128 height. Examples:
- `EdgeGuard`'s void rules
- "the gen"
- y-limits in pathing and the build planner
- SDK docs that name b1.8.1 items

Each needs an audit against 1.12.2 item names and a terrain world: caves, lava lakes, cliffs, villages.

## 3. DI design

### 3.1 The profile

```
App composition root (AppHost.RunAsync)
  config + logging
  VersionSetup.ResolveAsync(config, Minecraft:Host/Port, data dir)   ← Bot:Server:Version | --server-version | ping
      → GameVersion → GameVersionProfiles.For(version) → GameVersionProfile
  RunBotAsync / CloneMode.RunAsync / InProcBodies        (profile passed in)
      new MinecraftClient(new ClientOptions { …, Profile = profile })
      MainHive → clones: --server-version <id>
```

`GameVersionProfile` (MineClaw.Client/Versioning) is registered once per process:

| Member | Interface (project) | b1.8.1 implementation | 1.12.2 implementation (phase) |
|---|---|---|---|
| `Version` | `GameVersion` (Protocol) | `GameVersion.Beta181` | `Release1122` |
| `Protocol` | `IProtocolAdapter` + `IGameConnection` (Protocol) | `Beta181ProtocolAdapter` → `PacketConnection` | `Release1122ProtocolAdapter` → translating `NettyConnection` (P1–P2) |
| `Physics` | `IPhysicsRules` (World) | `Beta181PhysicsRules` → `PlayerPhysics`, `BlockShapes` | `Release1122PhysicsRules` (P3) |
| `Combat` | `ICombatRules` (Client) | `Beta181CombatRules` → `CombatRules` | `Release1122CombatRules` (P4) |
| `Hunger` | `IHungerRules` (Client) | `Beta181HungerRules` → `HungerRules` | `Release1122HungerRules` (P4) |
| `Data` | `IGameData` (Data) | `Beta181GameData` → `Blocks`/`Items`/`Recipes`/`Smelting`/`Tools` | `Release1122GameData` (P2 names/dig, P5 recipes/smelting/drops/food) |
| render | `IRenderAssets` (Render; registered by Bot's vision setup, since Render is not below Client) | the atlas + `BlockTextures` | blockstates + models (P5) |
| `Entities` | `IEntityRules` (World): mob profiles, type names (metadata layouts live in the translator, `EntityMeta1122`) | `Beta181EntityRules` (no stamps: the static tables) | `Release1122EntityRules` (P4) |

The DI is plain constructor injection of one profile object, without a container.
- There is exactly one decision point: the version, resolved before the graph. The graph is already composed by hand
  (`BotHost.Create`, `BodyFactory`).
- A container would add a package and a second composition style for no gain.
- If the user wants Microsoft.Extensions.DependencyInjection later (it is already in the restore graph through Logging), the
  profile maps onto `services.AddSingleton(profile.Physics)` and so on, one line each.

### 3.2 How the client partials consume it, without forking

- `MinecraftClient` captures `Options.Profile`'s members into readonly fields in its constructor.
- The partials call `_physics.Tick(...)`, `_combat.AttackCooldownTicks(...)` and so on, instead of the statics. This is a
  mechanical replacement per phase.
- **Rule:** no `if (version == …)` in client, bot, skills or scripting code. When a behaviour differs, it is a method or a
  fact on a seam.
- Capability facts that change strategy are data on the seam, for example `Combat.AttackCooldownTicks > 0`: wait for the
  charge. `Protocol.MaxChatLength`: split chat lines.
- The packet send sites keep building canonical messages. The new fields (hand, cursor, click mode) have defaults that mean
  "b1.8.1 behaviour".

### 3.3 Static tables (Blocks, Items, BlockShapes, PlayerPhysics)

- ~2,000 static call sites across every layer read pure tables.
- Threading the profile through all of them would fork every signature. Instead, P5 turns the statics into **facades over
  the process profile**: `GameData.Current`, set once by the composition root before anything reads a table.
- One process = one server = one version, so this is safe in production.
- Hot loops (A*, raycasts) capture `GameData.Current` once per call.
- Tests use `GameData.Override(profile)`: an AsyncLocal scope that flows into `Task.Run` and new threads, with the process
  default underneath. The 1.12.2 test classes join a non-parallel xUnit collection until every table is behind a facade.

### 3.4 How tests run both versions

- `FakeServer` keeps its game logic on canonical messages. It gets a wire codec per version: today `PacketConnection`
  (b1.8.1 server side). For 1.12.2, the server side of the translating connection (`Open(stream, inbound: ServerBound)`).
- The key client tests become `[Theory] [MemberData(nameof(Versions))]` over both profiles: login, dig, place, windows,
  chat, respawn, chunks.
- A round trip through our own translator can't prove wire correctness. So P1 adds golden byte fixtures for every 1.12.2
  packet the client reads or writes, from the protocol page's field tables (and later from recorder captures of a real
  1.12.2 session, once the user allows a live test).
- `ProtocolSmoke` / `ClientLive` gain `--server-version`.

### 3.5 Hive clones

- Clones read the version from their command line (`--server-version`, added by `MainHive` from the resolved id). They
  never ping.
- In-process bodies take `InProcBodies.Profile`, which is main's instance.
- A version change means a process restart (see section 1).

## 4. Phased plan

Each phase ships on its own and keeps every b1.8.1 suite green. Sizes are rough agent-days.

| Phase | Scope | Size | Risk |
|---|---|---|---|
| **P0** (done) | Seams + detection: `GameVersion`, `ServerPing` (modern + legacy), `VersionResolver`, `IGameConnection`/`IProtocolAdapter` (the client connects and logs in through it), `IPhysicsRules`, `ICombatRules`, `IHungerRules`, `IGameData` with b1.8.1 wrappers, `GameVersionProfile`, `Bot:Server:Version` + `--server-version`, clone pass-through, last-known fallback. No behaviour change. | S (1) | Low |
| **P1** 1.12.2 login → walk and see | VarInt/compression codec. Handshake/login/play state machine. Keepalive + teleport confirm inside the connection. Join Game, Respawn, Client Status, Spawn Position, Time, Health, Change Game State, Disconnect, Player List (uuid map). Chat JSON → legacy text (+ lang from the jar). Position in and out. **Chunk Data** (paletted, spanning longs, global palette, light, block entities → NBT reader), Unload Chunk, Block Change, Multi Block Change. **World height 256 + sectioned `ChunkColumn`** (coordinated with the light work). Golden fixtures. Version-change exit 75. | L (6–8) | Medium-high: the chunk decoder and the 128 → 256 sweep across 24 files are the bulk |
| **P2** (done) inventory, dig, place, use | Slot + NBT. Window open/items/set slot/property/close/confirm with string types and JSON titles. Click modes. **Offhand slot 45.** Held item. Digging statuses (server dig-time check). **Placement hand + cursor** and **Use Item**. Item entities (Spawn Object 2 + metadata) and Collect. Signs via block entities. The FakeServer 1.12.2 codec and the parametrized client tests. | M-L (4–6) | Medium: the cursor decides slab/stair halves; the BuildPlanner's Hidden underside clicks place **top** slabs |
| **P3** physics + pathing | `Release1122PhysicsRules`: the step-0.6/sneak/sprint/swim/ladder deltas, the new collision shapes (connecting fences/panes/walls, stair shapes, state-dependent slabs), slipperiness of new blocks, the vanilla server's movement checks. Route the tick, `SwimSim`, parkour, `EdgeGuard` and pathfinder costs through the seam. Replay tests vs recordings. | L (5–8) | High: fidelity needs the decompiled source; rubber-banding shows up only live |
| **P4** entities + combat | `IEntityRules` (metadata tables, new mob profiles), entity translation finished. Attack cooldown and charge-aware CombatEngine, sweep, crit rules, shields (offhand), the new damage and armor tables. `IHungerRules` for 1.12.2 (the 1.11 exhaustion values, saturation regen). | L (5–7) | Medium-high |
| **P5** data, recipes, knowledge pack, render | `IGameData` 1.12.2: blocks/items/foods from minecraft-data, recipes from the jar's 432 JSON files, smelting and drops from the decompiled source. Static facades over `GameData.Current`. `IRenderAssets` 1.12.2 (blockstates → models → textures, the meta → state table). `1.12.2-gamedata` pack + `mechanics_1122.md` via tools/ShardPacks. | L-XL (7–10) | Medium: wide but mechanical; the meta → blockstate mapping needs ground truth |
| **P6** (done) skills audit | Every skill, SDK helper, prompt and knowledge line checked for b1.8.1/skyblock assumptions: void edges, the gen, 128 height, item names, terrain worlds (caves, lava, cliffs, villages). Live soak on the 1.12.2 server (with the user's go-ahead). | M-L (4–6) | Medium |

### Risky unknowns and the ground truth each needs

| Unknown | Why it matters | Ground truth |
|---|---|---|
| 1.12.2 movement deltas (step, sneak edge, swim, ladder, sprint) and the server's movement checks | Rubber-banding and falls (P3) | `EntityPlayerSP.onLivingUpdate` / `onUpdateWalkingPlayer`, `EntityLivingBase.travel` / `jump`, `Entity.move`, `EntityPlayer.updateSize`, `NetHandlerPlayServer.processPlayer` / `update` |
| Collision shapes per state (stairs corners, fence/pane/wall connections) | Pathing, parkour, edge safety (P3) | `Block.addCollisionBoxToList` / `getActualState` per block (`BlockStairs`, `BlockFence`, `BlockPane`, `BlockWall`, `BlockSlab`) |
| meta → blockstate and the state → meta of placement (`getStateForPlacement`, cursor rules for slab/stairs/trapdoor) | Building and render (P2, P5) | `Block.getStateFromMeta` / `getMetaFromState` / `getStateForPlacement` per block (e.g. `BlockSlab.getStateForPlacement`) |
| Server-side place/dig/use-entity checks (distance only? raytrace?) | The Hidden technique and reach (P2) | `NetHandlerPlayServer.processTryUseItemOnBlock` / `processPlayerDigging` / `processUseEntity`, `PlayerInteractionManager.onBlockClicked` / `tryHarvestBlock` / `processRightClickBlock` |
| Attack cooldown, sweep, crit, shields, damage/armor | The combat engine (P4) | `EntityPlayer.attackTargetEntityWithCurrentItem` / `getCooledAttackStrength`, `util/CombatRules.getDamageAfterAbsorb`, `ItemSword`, `ItemShield`, `EntityLivingBase.canBlockDamageSource` |
| Entity metadata indices per class | Creeper fuse, wolves, items (P4) | `EntityDataManager.createKey` registrations per entity class |
| Hunger/regen numbers | The survival reflex (P4) | `FoodStats.onUpdate` / `addExhaustion` (the regen rows are verified, section 2.4), the `EntityPlayer.addExhaustion` call sites |
| Smelting, drops, tool rules, hardness | Acquisition, mining (P5) | `FurnaceRecipes.getSmeltingResult`, `Block.getItemDropped`, `ItemTool.getDestroySpeed`; minecraft-data for hardness and harvest tools |
| Wire formats (chunk sections, slot NBT, every field) | P1–P2 | The decompiled `readPacketData` / `writePacketData` of each packet class and `EnumConnectionState` (ids = registration order), protocol.json, golden fixtures. P1's formats are done (5.1). |

**Ground-truth options for 1.12.2** (the mc-dev equivalent):

| Option | What | Status |
|---|---|---|
| A. Decompiled source of the user's own jar (named with MCP mappings if available) | The mc-dev equivalent for 1.12.2. The client jar has the integrated server, so `NetHandlerPlayServer`, `PlayerInteractionManager` and the rest are in it. Lives **outside the repo** at `C:\Users\dev\source\mc-1.12.2-src\`; never committed. | **Available** (2026-10-28): SpecialSource 1.11.6 (notch → SRG with MCP SRG 1.12.2), then Vineflower 1.12.0, then MCP stable_39 names; 2,050 files; the tool and mapping hashes are in its `PROVENANCE.md`. Read-only ground truth: MineClaw's agents port the logic and never copy it into the repo. |
| B. PrismarineJS minecraft-data 1.12.2 | The blocks, items, recipes, foods, entities and protocol JSON that `data/dataPaths.json` maps for pc 1.12.2, vendored at `third_party/minecraft-data/` with its LICENSE and a README naming the source commit | **Approved** (relayed by the orchestrator). Not yet downloaded: the P0 agent's attempt was blocked by the session's permission check, so the orchestrator or the user runs it. |
| C. The protocol page (wiki.vg, protocol 340) | Packet ids and fields | Readable now (mirror above); used for this doc's ids |
| D. Already on disk, no download | The 1.12.2 client jar's `assets/minecraft/{blockstates,models,textures,lang,recipes,loot_tables}` | In use (reading only) |

Client jar recorded for reproducibility: `minecraft-1.12.2-client.jar`, 10,180,113 bytes, SHA-1
`0f275bc1547d01fa5f56ba34bdc87d981ee12daf` (Mojang's published 1.12.2 client hash), SHA-256
`8ada07da5ee77dad3527bd7278fbd05ee1fc8a597813b216a871a2d7d64cc64f`.

## 5. What P0 shipped

- **MineClaw.Protocol/Versioning:** `GameVersion` (ids, protocol numbers, netty flag, world height, parsing),
  `IGameConnection` (`PacketConnection` implements it; `PacketRecorder.Attach` takes it), `IProtocolAdapter` +
  `Beta181ProtocolAdapter`, `VarInt`, `ServerPing` (modern status + legacy 0xFE, parsers), `VersionResolver`.
- **MineClaw.World/Versioning:** `IPhysicsRules` + `Beta181PhysicsRules`.
- **MineClaw.Data/Versioning:** `IGameData` + `Beta181GameData`.
- **MineClaw.Client/Versioning:** `ICombatRules`, `IHungerRules` + their b1.8.1 wrappers, `GameVersionProfile`,
  `GameVersionProfiles` (b1.8.1; 1.12.2 → "not implemented yet").
- **`ClientOptions.Profile`** defaults to b1.8.1. **`ClientOptions.ConnectionFactory`** now returns `IGameConnection`;
  existing factories that return `PacketConnection` still compile.
- **`MinecraftClient`** opens its connection and sends its login start through `Profile.Protocol`. The recordings' header
  takes the protocol from the profile.
- **App:**
  - `VersionSetup` resolves the version before any client exists.
  - `Bot:Server:Version` (auto | b1.8.1 | 1.12.2) is the config key; `--server-version` overrides it.
  - `<data>/server-version.txt` holds the last detected version.
  - The profile goes into the main client, clones (`CloneMode`) and in-process bodies (`InProcBodies.Profile`).
  - `MainHive` passes `--server-version <id>` to clone processes.
- **Tests:**
  - Protocol `VersionDetectionTests`: names; parsers; pings against loopback fake servers (a b1.8.1 server that kicks,
    closes or stays silent on the status handshake, a 1.12.2 status with a 255-byte frame, no answer); the resolver.
  - Client `VersionProfileTests`: the b1.8.1 seams equal the statics; the physics seam ticks identically; the client
    connects and logs in through a profile adapter.
  - App `VersionSetupTests`: the CLI/config key, auto + legacy → b1.8.1 and remembered, server down → last known,
    explicit = no ping, 1.12.2 → a clear config error until P1.

### 5.1 What P1 shipped

- **Protocol/Release1122**, all ported from the decompiled client and cross-checked with protocol.json:
  - `Wire` (`WireReader`/`WireWriter`): VarInt/VarLong, strings, `BlockPos.toLong` positions, UUIDs, the offline UUID.
  - `Framing`: VarInt frames and zlib compression (`NettyCompressionDecoder`/`Encoder`). dataLength 0 = raw; a compressed
    payload below the threshold or over 2 MiB is rejected.
  - `Nbt`: a reader/writer for the network form (12 tag types including long arrays, modified UTF-8, depth 512).
  - `ChatComponent` + `LangTable`: JSON → legacy § text like `getFormattedText`. `translate` goes through the jar's
    `en_us.lang` (`LangTable.LoadDefault`, read once from the PrismLauncher or vanilla jar), else a built-in table of the
    chat, join/leave, kick and death keys (its values are verified against the jar).
  - `ChunkData`: Chunk Data → the new neutral `ChunkSectionsMessage`. Linear/hash-map palettes up to 8 bits; the global
    palette at 13 bits whatever the server sends; entries span longs; light nibbles low-first; sky light only in the
    overworld; biomes; block entities. A full column is one 16×256×16 region (absent sections = air with sky 15).
  - `Release1122Connection` (the translating `IGameConnection`) + `Release1122ProtocolAdapter`: the login state machine,
    keep-alive and teleport confirm inside, Client Settings + MC|Brand after Join Game, the canonical mappings of 2.1 for
    the P1 packet set. `CanSend` is false for the P2+ messages.
- **Seams:**
  - `IGameConnection.CanSend` (default true). The client's `Send` turns an unsupported message into a failed action
    ("digging is not supported on 1.12.2 yet"); on the reader/tick threads (reflexes) it is dropped with one log line.
  - `ChatPacket.Position` / `Json` (action-bar lines are not chat) and `UpdateHealthPacket.HealthExact`.
  - `IProtocolAdapter.LoginStart` returns the canonical handshake (the username) for both versions; the 1.12.2 connection
    turns it into Handshake + Login Start.
  - Chat lines split at `Protocol.MaxChatLength` (100 / 256).
- **World height:**
  - `ChunkColumn(cx, cz, height)` is 128 or 256 high. The index is `(y >> 7) << 15 | (x·16 + z)·128 + (y & 127)`: for
    y < 128 exactly the b1.8.1 index, so b1.8.1 columns, snapshots and every consumer of the lower half are unchanged.
  - `WorldState(height:)` / `WorldHeight` and `IBlockAccess.WorldHeight` (default 128). WorldState's storage, reads, writes,
    chunk regions and queries, `Safety`, `Raycast`, `SpawnRules` and the light engine (its height map is now `short`) use it.
  - `ChunkSnapshot` holds 128 or 256 (decode infers it). The client builds its world with the profile's height.
- **Physics:** `Release1122PhysicsRules` (step 0.6F, 256 high, the rest b1.8.1). The client tick now calls
  `Profile.Physics.Tick` (identical for b1.8.1); `PlayerPhysics.Tick` / `MoveEntity` take an optional step height.
- **Profile:** `GameVersionProfiles.Java1122`. VersionSetup's "not implemented" exit is gone for 1.12.2. Combat, hunger and
  game data were the b1.8.1 ones until P4/P5 (now the 1.12.2 ones, 5.3 and 5.5).
- **Tests:**
  - Protocol `Release1122Tests`: golden bytes for handshake, login start, settings, brand, movement, actions and chat;
    framing and compression; an NBT fixture; chat JSON; 5-bit entries spanning longs against a hand-computed long; 4/8/13-bit
    sections with light and biomes; the nether; block entities; the connection against a raw fake server; ids vs protocol.json.
  - World `WorldHeight256Tests`: index identity, blocks and queries above 128, the light engine on 256 columns, server-lit
    columns, snapshots.
  - Client `Release1122ClientTests`: the client end to end on the 1.12.2 profile (login → join → chunks with the ground at
    y 143 → teleport confirm → keep-alive → chat both ways → block changes → a P2 action failing clearly → kick).

**Source vs wiki.vg (the source wins):**
- *Position:* the wikivg.booky.dev revision's data-type text gives the 1.14 layout (x, z, y). 1.12.2's `BlockPos.toLong` is
  `x << 38 | y << 26 | z`, as protocol.json's bitfield says. P1 follows the source.
- *Global palette bits:* the client ignores a sent bits-per-block above 8 and uses log2(state count) = 13
  (`BlockStateContainer.setBits`); the registry palette still reads a VarInt length (`BlockStatePaletteRegistry.read`).
- *Rain:* reason 1 starts rain and 2 stops it (`WorldServer.updateWeather`, `PlayerList.updateTimeAndWeatherForPlayer`), the
  b1.8.1 numbering.

**Still open after P1** (outside the P1 packet set, or the P3/P6 sweeps):
- Consumers outside the world model still clip to `WorldState.Height` (128): pathing, building, skills, render and the
  Explorer see only the lower half of a 1.12.2 world (P3/P6).
- Columns are flat 256 KB arrays, not lazily allocated sections (2× a b1.8.1 column's memory).
- Block ids 123–255 and the changed metas read through the b1.8.1 tables (unknown ids) until P5 (P2: the client's names,
  stack sizes and dig rule come from `Release1122GameData`; physics and the static tables still don't know them).
- Player List supplies names; P2 translates player and item spawns; mobs and other objects: done in P4 (5.5).
- The version-change exit 75 on an "Outdated" kick isn't wired.
- ~~The FakeServer 1.12.2 codec and the parametrized client tests are P2.~~ Done (5.2).

**Behaviour change in production:** none for an explicit version. With the default `auto`, one ping pair runs at
startup: about 10 ms on a LAN, plus one "Protocol error" log line on the b1.8.1 server. To skip it, set
`Bot:Server:Version=b1.8.1`.

### 5.2 What P2 shipped

- **Protocol/Release1122** (wire formats from the decompiled `readPacketData`/`writePacketData`, cross-checked with protocol.json):
  - `Codec1122`: slots with NBT (`PacketBuffer.read/writeItemStack`; a count ≤ 0 is empty), entity metadata (all 14
    serializers skipped correctly; EntityItem's index 6 read), window type strings → canonical types and sizes (the crafting
    table, enchanting table and anvil arrive with slot count 0), furnace properties renumbered (1.12.2 0 burn / 1 item burn /
    2 cook / 3 total → b1.8.1 0 cook / 1 burn / 2 total), Set Slot window −2 (an `InventoryPlayer` index: 40 = off hand → 45),
    sign block entities (Text1..4 JSON).
  - `Release1122Connection.Play2.cs` (client): Open/Close Window, Window Items, Set Slot, Window Property, Confirm Transaction;
    Click Window with its click type, Confirm Transaction out (the apology), Close Window out; Player Digging 0..6 (drop,
    stack drop, release and swap carry `BlockPos.ORIGIN`/DOWN); Player Block Placement with hand + cursor (default the face
    centre), Use Item (hand); Use Entity (attack / interact / interact-at, hand); Animation (hand); Update Sign; Block Break
    Animation → the neutral `BlockBreakAnimationMessage`; Update Block Entity 9 and chunk block entities → `UpdateSignPacket`;
    Spawn Object 2 held until its metadata arrives → `PickupSpawnPacket`, later stack changes → `ItemEntityStackMessage`;
    Spawn Player (name from the Player List), relative moves resolved to exact absolute positions → canonical teleports (no
    drift), look, teleport, destroy (several ids), collect, equipment (off hand dropped), animation, entity status.
  - `Release1122ServerConnection`: the server side of the same wire for fake servers (`IProtocolAdapter.Open(inbound:
    ServerBound)`): answers the login, re-encodes b1.8.1 map chunks as paletted columns, pads window 0 to 46, spawns items as
    Spawn Object + metadata; reads every P2 C→S packet. Written from the server-side packet classes, so a client test over it
    checks both translators against the source.
  - Canonical additions: `WindowClickPacket.Mode/Button`, `PlayerBlockPlacementPacket.Hand/Cursor`, `UseEntityPacket.Hand/
    InteractAt`, `AnimationPacket.Hand`, dig statuses 1/3/6. `PacketConnection.CanSend` refuses those forms on b1.8.1.
- **World:** `InventoryModel` (b1.8.1 45 slots; 1.12.2 46 with the off hand at 45) and the 1.12.2 click prediction
  (`InventoryManager.Modern.cs`, a port of `Container.slotClick`, `mergeItemStack` and the containers' `transferStackInSlot`):
  modes 0..6, smart shift routing (armor, shield → off hand, smeltables and fuel into the furnace), the 1.12.2 expected item
  (a full shift move returns empty), crafting output taken whole by a right click, NBT-aware stacking. `PlaceCursor` /
  `PlaceHalf`: the slab/stair half from face + cursor (`BlockSlab`/`BlockStairs.getStateForPlacement`), log axis, torch facing.
- **Data:** `Release1122GameData` (ids, names, variant names, stack sizes, durability, hardness, harvest tools from the embedded
  minecraft-data pc/1.12.2) and `Release1122Dig` (`getPlayerRelativeBlockHardness`, `getDigSpeed` with efficiency, haste,
  fatigue, water, on-ground; `getDestroySpeed` per tool). `IGameData.DigTicks(in DigInput)`, `DisplayName`,
  `ItemByNameWithDamage` and the fact `HalvesFollowCursor`. `ItemStack.Tag` (`ItemTag`: the compound's bytes; equality by bytes).
- **Client:** the profile carries the inventory model and item rules; names, crafting lookups and dig ticks go through
  `Profile.Data`; `PlaceAgainstAsync(…, half)` sends the cursor for the wanted half and refuses a face that can't give it;
  `ClickSlotAsync(slot, ClickMode, button)`, `SwapHandsAsync`, `Inventory.Offhand`; `BlockBreakProgress` → `IGameClient` →
  `GameSignals` (player.punchBlock on 1.12.2). A slot update that agrees with the prediction is no race (1.12.2 sends the
  crafting output before the click's confirm). The "not supported on 1.12.2 yet" results are gone for every P2 feature.
- **Bot:** `BuildPlannerOptions.HalvesFollowCursor` (from the body's profile): a bottom slab/stair is never planned from an
  underside (Hidden) click, a top one never from a top face; `BuildBody` passes the step's half (meta bit 8 / 4).
- **Tests:** Protocol `Release1122P2Tests` (slot/NBT golden bytes, windows, every click type, dig/place/use/entity golden bytes,
  entities and signs, the server codec); Data `Release1122GameDataTests` (names, ids, dig ticks for twelve tool/block cases
  hand-computed in float from the source); World `Inventory1122Tests` (off hand, swap, shift returns, furnace routing, throw,
  double click, drag, NBT, cursor values for slabs/stairs/logs/torches); Client `Release1122PlayTests` (dig/place/chest on both
  versions; reject → apology → accept for every mode; the off hand; the crafting table; chop a log → craft planks → place;
  dig progress, signs and item stacks) over `FakeServer(release1122: true)`, which judges each click with the 1.12.2
  slotClick like a vanilla server; Bot: the punch wiring.

**Still open after P2:**
- ~~Mobs, objects other than items, velocity (knockback), effects (haste for the dig rule), the server's Held Item Change: P4.~~ Done (5.5).
- ~~Recipes and smelting are b1.8.1's (`Recipes.Match` predicts the crafting output; 1.12.2 planks keep the log's wood type, so a
  non-oak craft is predicted wrong once and resynced): P5. The static `Blocks`/`Items` facades outside the client: P5.~~ Done
  (5.3), except the `Blocks` facade.
- Placement prediction of 1.12.2 metas (log axis, slab halves) in the world model: the server's block change sets them.
- ~~Entity metadata other than an item's stack (creeper fuse, wolf flags): P4.~~ Done (5.5).

### 5.3 What P5 shipped (game data)

- **Static facades** (`MineClaw.Data/Versioning/GameData`): `GameData.Current` = an AsyncLocal `Override(data)` scope (tests; it
  flows into awaits, `Task.Run` and new threads) over the process default, which `VersionSetup` sets from the resolved profile.
  `Items` (Get, Exists, ByName(WithDamage), DisplayName, VariantName, Food, FoodPoisonChance), `Recipes` (All, ForOutput, Match,
  UsingIngredient, VariantTag, IsUnpack), `Smelting` (All, ResultFor, ForOutput, FuelTicks and everything built on them), `Drops`
  (For, Primary, SourcesOf) and `Tools.CanHarvest`/`DigTicks` answer from it. b1.8.1 keeps a direct path to its own tables (the
  `…B` members `Beta181GameData` reads), so every b1.8.1 result is unchanged. `MinecraftClient` runs each action under its
  profile's data (`RunActionAsync`), so a body's crafting, smelting and mining read its version even where the process default
  differs. **Not behind the facade:** `Blocks` (physics, collision and placement read it: P3) and `Items.All`/`Blocks.All`.
- **`IGameData` additions** (default members = b1.8.1): `AllRecipes`, `IsUnpack`, `AllSmelting`, `SmeltingFor(id, damage)`,
  `CookTicks`, `DropsFor(block, meta, held)`, `BlockIds`, `Food(id, damage)`, `FoodPoisonChance(id, damage)`, `VariantName`.
  `SmeltingRecipe` gains `InputDamage` (−1 = any) and `Experience`. `ItemRules.IsFuel`/`IsSmeltable` (the 1.12.2 shift-click
  routing into a furnace) come from the profile's data.
- **1.12.2 tables** (`Release1122Tables.cs`):
  - *Recipes:* minecraft-data `recipes.json` (embedded): the jar's 432 recipe files one to one (a test lays each jar recipe out
    with its first and its last alternative and matches it, when the jar is installed). The jar's variant lists (planks 0..5,
    wooden slabs, coal/charcoal, wool, sandstone…) are wildcards, so mixed planks make a crafting table, like `Ingredient.apply`.
    A bare damageable ingredient must be undamaged. Shaped (anywhere, mirrored) and shapeless matching is the shared
    `Recipes.MatchIn`; 2x2 vs 3x3 = the pattern size / ingredient count, as `canFit`. Variants list in id order (oak planks
    first). An unpack is the reverse of a 9-of-the-output recipe (blocks, nuggets, slime, hay, bone meal). Not included: the
    code-only special recipes (armor dyes, banners, fireworks, maps, books, tipped arrows, shulker colours, repair).
  - *Smelting:* `shards/sources/1.12.2/smelting.json` (the `FurnaceRecipes` constructor, the same file the knowledge pack reads),
    keyed by input metadata where the source is (wet sponge, stone bricks, cod/salmon, stained clay); experience kept.
    *Fuel:* `TileEntityFurnace.getItemBurnTime` ported in its order (wood-material block items 300 incl. the banner, wooden slab
    150, carpet 67, wooden tools 200, boats 400, doors 200, blaze rod 2400, coal block 16000…).
  - *Drops:* the `Block*.getItemDropped`/`quantityDropped`/`damageDropped`/`getDrops`/`harvestBlock` overrides by hand (stone
    variants, podzol, ores, leaves' sapling 1/20 (jungle 1/40) + oak/dark-oak apples 1/200, shears, double plants, crops and
    their seed tries, slabs ×2, upper door/bed halves, item forms such as reeds, signs, repeaters, the 1.12.2 stairs dropping
    themselves). Fortune and silk touch are ignored. minecraft-data's `drops` field is not used (wrong for reeds, doors, beds,
    huge brown mushrooms).
  - *Food:* the `ItemFood` registrations (`ItemSoup` 0.6, fish types per damage, the poison/hunger chances incl. pufferfish);
    minecraft-data's foods.json lists crop blocks and saturation ratios instead.
  - *Tool tiers:* `Item.ToolMaterial` (wood 0, stone 1, iron 2, diamond 3, gold 0 harvest level) with minecraft-data's harvest
    tools, as P2; now behind `Tools.CanHarvest`.
- **Consumers:** the Obtainer/`Crafter`/`RecipePlanner`, `FoodSupply`, the smelting skill and the SDK's `ItemSources` (now
  `IGameData.DropsFor`) use the 1.12.2 tables on 1.12.2; the client predicts the 1.12.2 crafting output (birch logs → birch
  planks), so P2's non-oak resync is gone; `FakeServer(release1122)` crafts with the 1.12.2 recipes like a vanilla server.
- **Knowledge pack:** `MinecraftDataSource` takes the bot's tables (`tables:`); `tools/ShardPacks` passes `Release1122GameData`
  for 1.12.2, so the pack's "Drops …" lines are the ported drop rules, not minecraft-data's. The recipes, smelting and food facts
  already came from the same sources.
- **Tests:** Data `Release1122P5Tests` (crafting table from any/mixed planks, planks keep the wood type, stone pickaxe, bed wool
  colours + any planks, furnace, torch, fire charge, unpacks; FurnaceRecipes by metadata; 33 burn times; fuel vs the pack's
  facts file; a drop table sample; foods; tool tiers; the facades and their scope; the planner's iron pickaxe tree; the jar's
  432 recipes); Bot `ObtainTests` (an iron pickaxe from three chests on 1.12.2: acacia logs → planks → sticks, iron ore
  smelted, crafted at the table); Client `Release1122PlayTests` (birch planks and a mixed-plank table, every click accepted).

### 5.3b What P5 shipped (render)

- **Models** (`MineClaw.Render/Models`): blockstates, block models and block textures read from the user's installed 1.12.2 jar
  at runtime (`Bot:Vision:ModernClientJar` or `Render:Assets:Jar`; else the PrismLauncher/vanilla folders; never committed).
  Parents, `#texture` references, elements with rotation/rescale, face uv and uv rotation, cullface, tintindex, shade, variant
  x/y rotation, multipart with OR; uvlock approximated. Quads are the shared `GeoQuad`s, cached per state, so the Vision sheets
  (EyeRenderer) and the Explorer's GPU freecam draw 1.12.2 blocks. The seam is `TextureAtlas.Models` (`IBlockModels`): null = every
  b1.8.1 path unchanged (the golden tests pass).
- **meta → state:** `getStateFromMeta` ported per block plus the neighbour-dependent `getActualState` properties (stair corners,
  fence/pane/wall connections, doors, double plants, snowy grass, vines, stems, redstone wire approximately); an unknown state
  takes the blockstate's first variant.
- **Tints:** `tints.json`, always the plains biome (the world model keeps no chunk biomes yet).
- **Entities:** the b1.8.1 models are reused (every mob texture keeps the old layout); the 64×64 steve/zombie/pigman textures are
  cut to their top 64×32, so the player's separate left limbs and the skin overlay layers are not drawn.
- **Fluids, fire, signs and beds** keep the procedural b1.8.1 shapes with 1.12.2 textures; chests and shulker boxes are small
  synthetic models cut from their entity textures.
- **Tests** (skipped without the jar): Bot `Render1122Tests` (stairs facing/half/outer/inner corners, fence multipart, a top slab,
  wall and standing torches, cubes and tints, doors, a fixture-chunk render); MemoryExplorer `GpuFreecam1122Tests`.

**Still open after P5:** the `Blocks` facade (with P3's physics); chunk biomes for tints; 1.12.2 water/lava textures; dropped and
held items with ids new in 1.12.2 (a flat colour); a skin model with separate left limbs; skulls, banners and the end portal;
the code-only special recipes.

### 5.4 What P3 shipped (physics, shapes, pathing)

- **The seam reaches every consumer through the world:** `IBlockAccess.Physics` (default b1.8.1; `WorldState(physics:)`, set
  from `Profile.Physics` by the client; overlays, snapshots and the plan worlds forward it and `WorldHeight`). The static
  `BlockShapes` entry points (`AddCollisionBoxes`, `GetCollidingBoxes`, `HasCollision`, `CollisionTop`, `IsFullCube`,
  `IsClimbableAt`) and `PlayerPhysics.Tick(w, …)` dispatch on it, so the pathfinder, `SwimSim`, parkour, `EdgeGuard`,
  `Safety`, `StuckProbe` and the building code get the version's shapes with no new parameters. On a b1.8.1 world the old
  code runs unchanged (`*Beta` internals); every b1.8.1 test passes as before.
- **`IPhysicsRules`** gains `SneakHeight`, `JumpVelocity`, `SneakEdgeProbe`, `AddCollisionBoxes(id, meta)`,
  `GetCollidingBoxes`, `IsClimbable(x, y, z)` and `FallDamage`.
- **`World/Release1122/Physics1122`**, the tick port in the client's order: `handleWaterMovement` / `isInLava`;
  `MovementInputFromOptions` (sneak ×0.3), item use ×0.2, the sprint rules (forward ≥ 0.8, food > 6 via `MoveInput.Hungry`,
  a horizontal collision stops it); the 0.003 motion cut; `jump` with `jumpTicks` 10; `travel` (water 0.02/×0.8F, lava ×0.5,
  slipperiness ice/packed/frosted 0.98 and slime 0.8, the ladder clamp, sneak hold and 0.2 climb); `Entity.move` (the three
  sneak loops probing one step height, 0.6, down; the two-candidate 0.6 step and its step-down; `onLanded` slime/bed bounce;
  `onFallenUpon` fall damage: hay ×0.2, bed ×0.5, slime 0 unless sneaking; slime walking slowdown; soul sand ×0.4 per block
  in the box; web); the movement attribute (0.1, sprint ×1.3) and air speed set *after* travel, so sprinting accelerates one
  tick late as in vanilla; `updateSize` (1.65F while sneaking when it fits, eye 1.54F). `PhysicsState` gained `Width`,
  `Height`, `EyeHeight`, `JumpTicks`, `LandSpeed`, `AirSpeed`, `FallDamage` (b1.8.1 keeps the defaults).
  Not modelled: `pushOutOfBlocks`, entity collision boxes (boats, shulkers), the world border, potions, Depth Strider,
  elytra, flying, riding; the server's movement checks are only respected by porting the client exactly.
- **`World/Release1122/Shapes1122`:** the collision box of every state from minecraft-data `blockCollisionShapes.json`
  (generated into `Shapes1122.Data.cs`), plus the `getActualState` shapes ported from the source: fences (post + arms;
  `canConnectTo`: SOLID faces outside the attach exceptions, fences of the same material, gates by axis), walls
  (`CLIP_AABB_BY_INDEX`, the thin straight run), panes/bars, stairs (straight/inner/outer corners), doors (halves share
  facing/open/hinge), double chests and chorus plants; `World.getCollisionBoxes`' scan (±1 block, edge columns skipped,
  unloaded columns empty).
- **Pathing:** `Pathfinder` captures step height (0.5 / 0.6), body height and world height per search (jump-up threshold,
  same-level walks, break/open moves; all y-limits use the world's height); climbing via `IsClimbableAt` (vines and the
  trapdoor-over-ladder rule in 1.12.2); `Openables` knows the 1.12.2 doors 193-197, gates 183-187 and the iron trapdoor 167
  (redstone only) and reads a door's panel plane from its collision box. `Parkour.AirTravel(dy, sprint, rules)` measures
  the reach bounds per version. The void-safety rules are unchanged; `StuckProbe`, `Mechanisms`, `PlaceRules` and parkour
  region hashes use the world's height. Outside World (Client, Bot, Skills, Scripting, Render geometry, the Explorer) the
  sweep moved every 128 clip to the world/column/snapshot height (`IVoxelWorld.WorldHeight` is new).
- **Tests (World):** `Physics1122Tests` (golden trajectories computed from the source formulas and float literals, and the
  published 1.12 speeds: walk 4.317, sprint 5.612, sneak 1.295 m/s, ladder 2.35 up / 3.0 down, swim 1.96, jump apex 1.2522
  in 12 ticks, the 10-tick jump wait under a low ceiling, the sprint lag, the sine-table sprint-jump boost, step 0.6 (bed,
  snow, slab yes; soul sand, stone no), fences/walls not jumpable, the sneak edge over a 0.75 drop vs off a half step, the 1.65
  box in a 1.75 gap, vines and trapdoors, soul sand per block, ice, slime bounce, fall damage, web); `Shapes1122Tests` (all
  ~4,000 states alone vs minecraft-data, which wrongly gives door upper halves lower-half shapes; the neighbour shapes vs the
  source's AABBs); `Pathing1122Tests` (closed loop: bed ledge walked, fence/wall rows walked around, a vine wall climbed,
  paths at y 200, the spruce door opened, EdgeGuard holding at the void with the 1.12.2 clamp). No 1.12.2 recordings exist,
  so there is no recorded-jump comparison for 1.12.2 yet.

**Still open after P3:**
- Columns are still flat arrays: a 256-high column is 4 × 64 KiB = 256 KiB (+ the 512 B height map) against 128 KiB for
  b1.8.1, about 113 MiB vs 56 MiB per body at view distance 10 (441 columns). Lazily allocated sections (or a lazily
  allocated upper half, which the index layout already separates) touch ~35 raw-array sites in `WorldState`, `LightEngine`
  and `ChunkSnapshot` plus the `IdData`/`MetaData` spans (snapshots, the supervisor hash); not done.
- Selection (raycast) boxes are b1.8.1's for the old ids; new 1.12.2 blocks fall back to their collision boxes.
- The pathfinder doesn't plan through 1.65-high gaps (it checks a 1.8 body); the client sends the sneak state the tick uses.
- A recorded 1.12.2 comparison once the user allows a live session.

### 5.5 What P4 shipped (entities, combat, effects, hunger)

- **Protocol/Release1122** (wire formats from the decompiled `SPacket*.readPacketData`, ids from protocol.json):
  - `Release1122Connection.Entities.cs`: Spawn Object (every type; data = shooter + 1 / facing / block state, velocity as a canonical
    velocity), Spawn Mob (+ its metadata, velocity and head look), Spawn Player (+ its metadata), Spawn Experience Orb, Spawn Global
    Entity (lightning), Spawn Painting (facing renumbered); relative moves, look-and-move and teleports as canonical teleports that
    also carry the exact doubles (`Exact`, also on every spawn), so 1/4096 deltas never drift or round to the 1/32 grid; Head Look,
    Velocity (our own id = knockback), Destroy; Entity Status (33/36/37 thorns/drowning/burning hurt → 2; 29/30 shield, 35 totem
    pass through); Equipment with the off hand as canonical slot 5; Entity Effect / Remove Entity Effect; Entity Properties
    (attributes + modifiers, `EntityAttribute.Value` = `computeValue`); Set Passengers → canonical attach/dismount (Attach Entity
    0x3D is a leash in 1.12.2); Use Bed; the server's Held Item Change; Set Experience; Combat Event ENTITY_DIED (the death screen's
    message); Set Cooldown.
  - `EntityMeta1122`: the typed metadata reader/writer (all 14 serializers) and the per-class layouts (`EntityDataManager.createKey`
    in hierarchy order) onto the canonical indices of `Data/EntityMeta`: flags (1.12.2's hand states compose 0x10 "using an item"),
    health, arrows, absorption/main hand (players), baby (ageables, zombies), the creeper's state/powered/ignited (b1.8.1's 16/17 +
    ignited), slime/magma size, wolf/ocelot/parrot tame flags, spider climbing, ghast/blaze, pig saddle, sheep wool, zombie arms /
    skeleton draw / illager aggression, zombie villager curing, enderman screaming, witch drinking, guardian target, shulker peek,
    evoker spell; objects: critical arrows, item frame / potion / firework items, the TNT fuse.
  - `Release1122ServerConnection`: the inverse for the fake servers (it remembers each spawn's class to write its layout).
  - New neutral messages: `EntityHeadLookMessage`, `EntityPropertiesMessage`, `ExperienceMessage`, `DeathMessage`, `ItemCooldownMessage`.
- **World:** `MobProfile`/`MobTactic` moved here; `IEntityRules` (`Beta181EntityRules` = no stamps; `Release1122EntityRules`: every
  1.12.2 mob's hitbox, max health, normal-difficulty melee damage with its held weapon, tactic, hostility; an unknown type gets a
  safe default: not hostile, not fought). The tracker stamps `Entity.Profile`/`TypeName` on spawn and keeps head yaw, effects
  (`StatusEffect` with expiry), attributes, the off hand; `Entity.Health/IsBaby/IsUsingItem/FramedItem/InGround` (an arrow that
  lost its critical flag or stopped moving). `PlayerState.Effects` (cleared on respawn), `HealthExact`, `Absorption`.
  `Physics1122`: speed/slowness as operation-2 modifiers of the movement attribute, jump boost +0.1 per level.
- **Combat (`ICombatRules` grew; defaults are b1.8.1's):** `Release1122CombatRules`: weapon attributes (`ItemSword`, `ItemTool`,
  `ItemAxe`'s tables, `ItemHoe`), `getCooldownPeriod`/`getCooledAttackStrength(0.5F)`, charged damage, crits (charge > 0.9, falling,
  no sprint/ladder/water/blindness/riding; ×1.5), the sweep (sword, charged, on the ground, a slow step, no crit or sprint knock;
  the target's box grown by 1, 0.25, 1), shields (either hand, 5-tick raise, axes disable for 100 ticks), the totem, the 1.9
  knockback (vertical only on the ground), `getDamageAfterAbsorb` with toughness, the easy/hard scaling, attack-before-swing.
  `CombatEngine.Rules` (the profile's): it waits for `CombatOptions.MinCharge` (0.9) counted from its own last swing — any arm swing
  the client sends resets it, as `EntityPlayerMP.swingArm` does — and from a held-item change; at or below
  `EmergencySwingHealth` (8, above the disengage line) it hits at any charge; it holds a sweeping hit when a player, pet or
  non-hostile mob is in the sweep box and jumps for a crit instead; it raises the off-hand shield between swings (Use Item, hand
  1), never against an attacker holding an axe or while the shield is on its cooldown (status 30 / Set Cooldown); profiles come
  from `CombatRules.ProfileOf(e)`. Status 35 (totem) raises a critical danger. b1.8.1 spam clicks and sword blocks as before.
- **Hunger:** `Release1122HungerRules` (fast regen at 20 with saturation, 6 exhaustion per slow-regen hp, the 1.11 exhaustion
  values, the same starvation floors, `RegenPerMinute`, `WantsTopUp`) and `FoodStats1122` (a tick-exact `FoodStats.onUpdate`). The
  survival reflex eats a 1.12.2 top-up meal (hurt at food 18..19 without saturation), gates fights on poison/wither, and the state
  header and health alerts carry the effect facts ("poisoned 7s (stops at 1 hp)", "withering (can kill)", "fire resistant"…).
- **Client:** `Profile.Entities` → the tracker; haste/fatigue feed the dig rule (`DigTicksFor`); speed/slowness/jump boost feed the
  tick; the server's Held Item Change, Set Experience, the death message (`LastDeathMessage`), item cooldowns, our own metadata
  (absorption), orbs collected (`XpOrbsCollected`); the respawn is Client Status 0 as before. Render: the newer mobs draw as their
  nearest b1.8.1 model (husk → zombie, stray → skeleton, magma cube → slime…). `MobTypes.Name/ByName` know the 1.12.2 names.
- **Tests:** Protocol `Release1122P4Tests` (metadata per class incl. every serializer skipped, hand states → flags, objects/orbs/
  lightning/paintings/item frames, 100 relative moves exact to 1e-12, effects/attributes/passengers/experience/death/cooldown/held
  item, the server codec round trip, Entity Effect golden bytes); Client `Release1122CombatRulesTests` (the cooldown curve for the
  hand, swords, axes, pickaxes, shovels and hoes in Java float: the sword's charge is 0.99999994 at 12 ticks, the wooden axe needs
  23 for 0.9; damage per item; crit and sweep conditions; shields and the axe; knockback, armor, difficulty; mob profiles),
  `Release1122HungerTests` (fast/slow regen and exhaustion tick by tick, starvation floors, the top-up, effect facts and gates),
  `Release1122CombatEngineTests` (the charge per weapon vs b1.8.1's clicking, the emergency, a dig swing resetting the charge, no
  sweep into a player, the shield), `Release1122FightTests` over `FakeServer(release1122)`, which now has mobs (`SpawnMob`, a chase AI,
  `ApplyEffect`) and judges hits like a vanilla server (its own charge clock, fall-tracking crits, hurt resistance, death → drop +
  orb): a zombie walks up, the bot waits for the charge, hits, crits, kills, collects the rotten flesh and the 5 xp; the off-hand
  shield and its disable; effects into dig and physics; held item; the death message and respawn. World `Entities1122Tests`.

**Still open after P4:**
- The spar model (`SparModel`) and `WeaponChoice` still rank and estimate with b1.8.1 damage (a sword still wins on 1.12.2 DPS, but
  spar estimates against a 1.12.2 opponent are off); totem handling is reactive only (no off-hand totem management).
- Mob AI facts of 2.7 (skeleton strafing, creeper fuse, door breaking, spider climbing, enderman gaze) are the b1.8.1 ones the engine
  already had; not re-verified against the 1.12.2 source.
- Self attributes are recorded but the tick computes movement speed from the effects (not from the server's attribute snapshot).
- Vision has no models of its own for the newer mobs (nearest b1.8.1 model).


### 5.6 What P6 shipped (the audit: b1.8.1 and skyblock assumptions in a normal 1.12.2 world)

**The world mode.** Skyblock is a fact about the world, not the version, so it is not on the profile.
- `Data/Versioning/WorldMode` works like `GameData`: a process default plus an AsyncLocal `Override` for tests. It is either `skyblock` or `normal`.
- Game code reads its facts, never the name: `HasVoidBelow`, `GeneratorStone`, `ScarceTerrain`, `Islands`.
- The db's active knowledge packs decide the mode (`AgentRuntime.World`):
  - the `skyblock` mode pack → skyblock;
  - a legacy db without pack metadata → skyblock;
  - a pack of another version → normal.
- The App makes it the process default (`AgentOptions.ApplyWorldMode`).
- The default is skyblock, so every b1.8.1 install and test behaves as before.
- A `--reset-memory` 1.12.2 db (1.12.2-gamedata + sdk-js, no mode) is a normal world.

**`Blocks` joined the facades.** `Get`, `Exists` and `ByName` answer from `GameData.Current`; b1.8.1 keeps its `…B` path. Names such as `log2`, `hopper` and `stained_hardened_clay` now resolve on 1.12.2 everywhere, and `unknown_162` no longer appears in model-facing text.

**Light and spawning.**
- `IPhysicsRules` gained `LightOpacity`, `LightEmission`, `IsSpawnFloor`, `IsSpawnCellClear` / `IsSpawnHeadClear` and `BiomeSpawnsMonsters`.
  - The b1.8.1 defaults are mc-dev's.
  - The 1.12.2 implementation is `World/Release1122/Light1122`, ported from `WorldEntitySpawner`, `Block.isTopSolid` and its overrides, `isValidEmptySpawnBlock`, `Material` translucency and the block registrations' light values.
- Spawn floors on 1.12.2:
  - Floors: top slabs, upside-down stairs, full snow layers, hoppers and retracted pistons.
  - Not floors: the translucent materials (glass, glowstone, sea lanterns, beacons, ice, leaves, TNT), plus bedrock, barriers and magma.
  - Rails and power sources (plates, buttons, levers, redstone, trapped chests, observers…) block the spawn cell.
  - The light ≤ 7 and 24-block rules are unchanged.
- The light engine captures the version's tables:
  - sea lanterns, end rods, lit lamps and magma emit light;
  - terracotta, concrete, log2 and the new slabs and stairs are opaque;
  - stained glass no longer counts as b1.8.1's glowing locked chest.
- **Chunk biomes** are stored (`ChunkColumn.Biomes`, `WorldState.GetBiome` / `SetBiomes`), read from the Chunk Data biome array.
  - The mushroom island, its shore and the void biome spawn no monsters.
  - The 1.12.2 models are recoloured per column (`IVoxelWorld.Biome`, grass and foliage tints from `tints.json`). An unknown biome renders as plains.

**Checklist** (fixed / OK / deferred):

| # | Item | Status |
|---|---|---|
| 1 | Skills: void-edge assumptions | **Fixed.** In a normal world, `expand_island`'s edge mode finds edges over a drop of 2 or more; it has the alias `extend_floor` and neutral wording. `pillar_down` refuses under sand or gravel. Dirt is a building block when it isn't scarce. |
| 1 | `expand_island` | **Fixed** (see above). The void checks stay as safety rules. |
| 1 | Cobble generator assumptions | **Fixed.** `build_cobble_gen` requires skyblock (`Preconditions.Requires` answers "not available in this world"). In a normal world, `harvest_generator at=auto` uses only taught or confirmed cells. |
| 1 | Sapling scarcity, unrenewable dirt | **Fixed.** These only apply with `ScarceTerrain`: tree stewardship, the "last trees" refusal, the replant streak, leaf clearing and the protection of the dirt under trunks. In a normal world `chop_tree` fells the logs only. |
| 1 | 24-block mob-farm rules | **OK.** `build_mob_farm` is a stub, and the 24-block spawn rule is the same in 1.12.2. |
| 1 | Version-only mechanics in skills | **Fixed:** log2/leaves2 (all six species; 2×2 trunks; dark oak needs four saplings), carrots/potatoes/beetroot, the iron trapdoor, the wooden button, weighted plates, trapped chests, fuels ranked by burn time, magma in `hazard_scan`, beds only in the overworld (a new End dimension). **Deferred:** a nether wart harvest skill; `eat_cake` reporting slices left (the data has no per-version bite count). |
| 2 | Prompts, brainstem, chat lines, ScriptPrompts, AuthorAgent, blueprint author, perception | **Fixed:** the cached C# text is world- and version-neutral ("a Minecraft world", "# World core rules", a neutral safety line); the skyblock rules come only from the skyblock pack's core; the worker also gets the pack line; blueprint messages name `GameData.Current.VersionId`; vision and blueprints ask gen questions only with `GeneratorStone`; ScriptPrompts' examples are mining and chopping (one gen example remains, labelled skyblock only). The brain's prefix is about 95 characters shorter. |
| 3 | WorldIndexer kinds | **Fixed.** In a normal world, chunk scans seed no floors, builds or generators: only observed placements count, and a gen only from an observed refill. Islands only with `Islands`. Adds the 1.12.2 stations, crops and log2. |
| 3 | ProtectionPolicy (gen walls) | **Fixed.** A real `floor` reason; gen walls only for confirmed gens; the 1.12.2 crops, fill blocks and containers. |
| 3 | FoodSupply | **Fixed.** The food and fuel lists come from `GameData.Current` (1.12.2 foods). Food checks use the item's damage value, so pufferfish is risky. |
| 3 | ChestStore | **Fixed.** Trapped chests are storage, paired only with other trapped chests. |
| 3 | Gen yield/honesty guards | **OK.** They are version-neutral. In a normal world, natural lava beside water is not a gen, and `mine stone` mines natural stone when no gen is near. |
| 3 | BuildSkips "floor over the void" | **Fixed.** "(over the void)" appears only over a real void; a normal world gets a foundation (`fill_floor`) hint. |
| 3 | Vision role classifier | **Fixed.** In a normal world, gen cells only for confirmed gens; doors via `Openables`; the 1.12.2 crops; void only with a void below. |
| 3 | Light/spawn rules | **Fixed** (see above). |
| 3 | Nether portal travel | **OK.** Travel flood-fills the portal sheet, so frame size doesn't matter; the 1:8 scaling and the 80-tick wait are the same; `build_portal` makes the 4×5 minimum, which is valid in both versions. **Deferred:** `light_portal` on a larger hand-built frame (1.12.2 allows insides up to 21×21). |
| 3 | Doors/mechanisms | **OK.** `Openables` already had the 1.12.2 states from P2/P3. **Fixed:** the wooden button and weighted plates are actuators; the iron trapdoor goes through redstone. |
| 4 | SDK primitives, compat, examples | **Fixed:** `block()`, `waitfor('block')`, `near()` and item lookups go through the session's `IGameData`; `ItemSources` chops log2 and harvests the new crops; lint is version-aware; the sdk-js pack is rebuilt. **OK:** one pack serves every version, and each callable is tagged with its versions. **Deferred:** skyblock tags on the gen callables in the pack. |
| 5 | Chunk biomes | **Fixed** (see above). |
| 5 | Spar model / weapon choice | **Fixed.** Both take the profile's `ICombatRules`. With a cooldown, weapons rank by damage per second over max(cooldown, the 10-tick hurt resistance): a wooden pickaxe beats the bare hand, and a sword beats an axe. b1.8.1 is unchanged. |
| 5 | `Blocks` facade leftovers | **Fixed** (see above). |
| 6 | Hive: clone accounts, whitelisting, player cap | **Fixed:** a whitelist add counts only once the server confirms it ("Added X to the whitelist", fed from main's chat), and launches wait for it (≤ 10 s); a not-whitelisted or server-full kick ends the clone with exit 77/78 and frees the slot (the built-in lang has both keys); the void-spawn refusal applies only with a void. **OK:** the cap comes from Join Game's max players in both versions; the `whitelist add` syntax is the same. |
| 7 | End-to-end scenario | **Passes:** `App.Tests/Release1122WorldScenarioTests`. |

**The scenario** (`tests/MineClaw.App.Tests/Release1122WorldScenarioTests.cs`, about 16 s):
- **Setup:**
  - `MemoryReset` builds a fresh db from the 1.12.2 pack.
  - The bot logs into `FakeServer(release1122)`, in a normal-world fixture: bedrock, stone, dirt and grass; a stone outcrop; oak, birch and acacia trees; a cave with lava beside water; a planks house with a door, panes and a chest.
  - Its prompt carries the 1.12.2 core and no skyblock words.
- **Scripted brain and worker rounds, in order:**
  1. Greet.
  2. Chop two trees.
  3. Craft a table (and place it) and a wooden pickaxe.
  4. Mine 34 natural stone, never touching the house or the cave.
  5. Craft and place a furnace, and smelt two porkchops.
  6. Before dusk, build a 3×3 hut with a doorway and a roof through the `build_wall` / `fill_floor` skills.
  7. Kill a zombie with charged hits (≥ 0.85).
  8. Eat a cooked porkchop when hungry.
- **Fake-server additions:** 1.12.2 furnace windows, sped-up furnace time, and registration of placed tables and furnaces.

**Still open after P6:**
- `light_portal` on frames larger than 4×5; nether wart harvesting; pigmen's 1.12.2 drops; `eat_cake` slices left.
- In a normal world, builds that stood before the bot first saw them are not flagged as player builds. Placements the bot observes are not persisted; its own placements are, in `PlacedBlockLog`.
- The user's `appsettings.json` persona still says "help foxy and robin play skyblock" / "your third island buddy". That is config, not code.
- `QuestionOptions.Jobs` ("top up cobblestone…") and the `harvest_generator` / `into_void` names stay in the cached tool text.
- Remembered (offline) columns carry no biomes, so they render with plains tints.
- A live soak on the 1.12.2 server, with the user's go-ahead.

### 5.7 The first live 1.12.2 session (2026-10-29): what broke and the fixes

**Drowning (main, 01:58-01:59).** The delegate's chop job worked a ravine lake's rim. After a stalled creeper fight, the body
stood on the lake's gravel bed (125,52,294), head under, and took 2 damage a second until "maid_chan drowned". The danger line
said "low health 8/20 …: work goes on".
- **Root cause, both versions:**
  - The pathfinder treated water cells as ordinary swim nodes. A walk along a lake bed cost the same as the surface, with no
    limit on head-under steps.
  - Nothing kept the head above water when no path step asked for it. An idle, finished or interrupted body sank (1.12.2
    `travel`: −0.02/tick, no jump) and stayed down.
  - Nothing tracked air, and damage carried no cause.
- **Fixes:**
  - `World/Physics/Breath`: the vanilla air rule (300, −1 a tick while `isInsideOfMaterial(WATER)` at the eye per
    `getLiquidHeightPercent`, 2 damage at −20, 300 at once out of water), bubbles, `NearestBreathable` (a BFS to the open surface
    or a 2-high air pocket, with its route).
  - Air comes from our own metadata index 1 (`EntityMeta.Air`: b1.8.1 short, 1.12.2 `Entity.AIR` VarInt), with a per-tick client
    estimate in between. It is shown in `status` and the state header ("air 4/10 bubbles (6.2s left) HEAD UNDER WATER").
  - `MinecraftClient.Breath`:
    - A head under water holds jump (a player holding space), and so does a body floating idle.
    - The drowning reflex fires once the air left only just covers the swim to the nearest air (`Breath.AirNeeded`), at
      `SurvivalOptions.SurfaceAtAir` (100), or after a drowning hit. It stops the job (`DangerKind.Drowning`, critical), follows
      the searched route with jump held, never deeper, and holds new jobs until the air is full. Then it raises "surfaced at
      x,y,z: air was N/10 bubbles".
  - `DamageCause`: every damage event names drowning, lava, fire, burning, suffocation, cactus, a fall, a mob (knockback or a
    hostile in reach), starvation, wither or poison.
  - `Pathfinder`: the k-th consecutive head-under step costs `k × HeadUnderStepCost` (4), and no non-rising move goes past
    `MaxHeadUnderSteps` (7). Both pass through `GotoOptions`.
- **Tests:** `Client.Tests/Release1122DrowningReplayTests` (a lake beside a spruce trunk, the fake server running the breath
  rule).

**Night defence (clones 69 and 1337).**
- **What happened:** a bare-handed clone started "defending myself" against a zombie and lost 22 hp in 3 s. The other started
  boxing in only at critical hp 4 and died building it. "Retreating inward" means nothing in a normal world.
- **Fixes:**
  - `CombatEngine.Outmatched` (1.9+ only): our best charged DPS against the melee threats' hp, and their damage per second
    against ours. When the fight is lost, the engine boxes in (`CanBoxIn`; logs and planks are walls) instead of starting it,
    and re-checks every 10 ticks during a fight.
  - A guarded player's fight is kept unless our own hp is low.
  - "Approaching" now means the mob moving toward us. It used to mean the gap shrinking, which also stopped the bot 5 m short of
    a zombie busy with Fox, until the 8 s give-up.
  - An idle body at night boxes in before a mob it can't beat comes within 12 blocks (`NightShelterRadius`).
  - Alerts name the mob instead of "work goes on" / "inward" (that wording stays for skyblock).
- **Tests:** `Client.Tests/Release1122NightDefenceTests`.

**Other fixes:**
- **Chat translations:**
  - A local build or publish copies `assets/1.12.2/…/en_us.lang` into its output, extracted from the installed jar (the
    App's `CopyLang1122`). The file is never committed.
  - `LangTable.LoadDefault` also reads the renderer's cached `assets/1.12.2-client.jar` and keeps looking until it finds a table.
  - The op, permission and whitelist answers are built in, and an unknown key reads as words (`CleanKey`).
- **Whitelist reconcile:** a removal counts only once the server confirms it. A permission error fails every line in flight.
  Unconfirmed removals stay flagged and are retried (`RetryDelay`).
- **1.12.2 placements in the ledger:** the server's Set Slot follows the Block Change by a tick (`detectAndSendChanges`), so a
  placement waits ≤ 6 ticks for it before ending. The fake server models the delay.
- **Recipe variants:** when every variant recipe of a wildcard ties on a shortfall, the shortfall is the wildcard ("1 more log",
  never "oak wood").
- **Night + no food:**
  - Walks or follows to a player within 24 blocks are always allowed (not at low hp).
  - A clone spawn (`hive op=spawn|handover`) is checked like a job: at night with no food it is refused with one line.
- **Chat:**
  - `MetaSpeech` drops idle and third-person status narration unless a player asked for status.
  - `RecentDeaths` drops advice queued for a player who just died.
- **Clone start:** every process logs the game data it really uses.

**Still open after 5.7:** placing a block to stand on while surfacing (the reflex swims to the surface or a pocket and floats);
Respiration/Depth Strider are not modelled.
