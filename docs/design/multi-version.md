# Multi-version: b1.8.1 and 1.12.2 behind one set of seams

Status: P0 and P1 are implemented (seams + detection; 1.12.2 login, chat, game state, movement and chunks in a 256-high
world, section 5.1). P2 to P6 are planned. Ground truth for 1.12.2: the decompiled client at `C:\Users\dev\source\mc-1.12.2-src`
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

Call sites: ~1,500 `Blocks.` + ~540 `Items.` static uses. P5 makes the static facades delegate to the process profile (see 3.3).

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
| `Combat` | `ICombatRules` (Client) | `Beta181CombatRules` → `CombatRules` | (P4) |
| `Hunger` | `IHungerRules` (Client) | `Beta181HungerRules` → `HungerRules` | (P4) |
| `Data` | `IGameData` (Data) | `Beta181GameData` → `Blocks`/`Items`/`Recipes`/`Smelting`/`Tools` | generated tables (P5) |
| render | `IRenderAssets` (Render; registered by Bot's vision setup, since Render is not below Client) | the atlas + `BlockTextures` | blockstates + models (P5) |
| entity rules (P4) | `IEntityRules` (World): metadata indices, mob profiles | the current tables | the 1.12.2 tables |

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
| **P2** inventory, dig, place, use | Slot + NBT. Window open/items/set slot/property/close/confirm with string types and JSON titles. Click modes. **Offhand slot 45.** Held item. Digging statuses (server dig-time check). **Placement hand + cursor** and **Use Item**. Item entities (Spawn Object 2 + metadata) and Collect. Signs via block entities. The FakeServer 1.12.2 codec and the parametrized client tests. | M-L (4–6) | Medium: the cursor decides slab/stair halves; the BuildPlanner's Hidden underside clicks place **top** slabs |
| **P3** physics + pathing | `Release1122PhysicsRules`: the step-0.6/sneak/sprint/swim/ladder deltas, the new collision shapes (connecting fences/panes/walls, stair shapes, state-dependent slabs), slipperiness of new blocks, the vanilla server's movement checks. Route the tick, `SwimSim`, parkour, `EdgeGuard` and pathfinder costs through the seam. Replay tests vs recordings. | L (5–8) | High: fidelity needs the decompiled source; rubber-banding shows up only live |
| **P4** entities + combat | `IEntityRules` (metadata tables, new mob profiles), entity translation finished. Attack cooldown and charge-aware CombatEngine, sweep, crit rules, shields (offhand), the new damage and armor tables. `IHungerRules` for 1.12.2 (the 1.11 exhaustion values, saturation regen). | L (5–7) | Medium-high |
| **P5** data, recipes, knowledge pack, render | `IGameData` 1.12.2: blocks/items/foods from minecraft-data, recipes from the jar's 432 JSON files, smelting and drops from the decompiled source. Static facades over `GameData.Current`. `IRenderAssets` 1.12.2 (blockstates → models → textures, the meta → state table). `1.12.2-gamedata` pack + `mechanics_1122.md` via tools/ShardPacks. | L-XL (7–10) | Medium: wide but mechanical; the meta → blockstate mapping needs ground truth |
| **P6** skills audit | Every skill, SDK helper, prompt and knowledge line checked for b1.8.1/skyblock assumptions: void edges, the gen, 128 height, item names, terrain worlds (caves, lava, cliffs, villages). Live soak on the 1.12.2 server (with the user's go-ahead). | M-L (4–6) | Medium |

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
  game data are the b1.8.1 ones until P4/P5.
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
- Block ids 123–255 and the changed metas read through the b1.8.1 tables (unknown ids) until P5.
- Player List supplies names, but entity spawns aren't translated yet (P2/P4).
- The version-change exit 75 on an "Outdated" kick isn't wired.
- The FakeServer 1.12.2 codec and the parametrized client tests are P2.

**Behaviour change in production:** none for an explicit version. With the default `auto`, one ping pair runs at
startup: about 10 ms on a LAN, plus one "Protocol error" log line on the b1.8.1 server. To skip it, set
`Bot:Server:Version=b1.8.1`.
