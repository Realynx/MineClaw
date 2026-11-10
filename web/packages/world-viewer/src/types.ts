// Wire types of the supervisor's world routes (docs/web/reference/explorer-spec.md §13), as the app hands them to the viewer.
// Nest mirrors the bridge with camelCase keys; `normalize*` (wire/normalize.ts) also accepts the raw snake_case replies.

import type { RuntimeMemory } from './render/runtime';

/** 0 overworld, -1 nether (1 = the End on 1.12.2). */
export type Dim = number;

/** A 64-bit column hash as it may arrive: a JSON number (precision lost above 2^53), a decimal string, or a bigint. */
export type WireHash = number | string | bigint;

export interface WorldIndexColumn {
  cx: number;
  cz: number;
  hash: WireHash;
  /** Unix ms the hive last saw the column. */
  seen: number;
  /** Body that saw it. */
  by?: string | null;
  /** Seen live (else remembered). */
  live: boolean;
}

/** `GET api/world/index?dim&x&z&r&light=1`. */
export interface WorldIndex {
  dim: Dim;
  at?: number;
  columns: WorldIndexColumn[];
  /** "b1.8.1" | "1.12.2" (older supervisors omit it: 256-high columns then mean 1.12.2). */
  version?: string | null;
  /** World time in ticks (negative from a 1.12.2 server whose daylight cycle is stopped: the absolute value is the time). */
  time?: number | null;
  /** True when the daylight cycle is stopped (`doDaylightCycle false`): the time is not advanced locally. */
  timeFrozen?: boolean | null;
  rain?: number | null;
  thunder?: number | null;
}

export interface BotDto {
  name: string;
  /** "main" / "67": the key /view and players use. */
  short: string;
  main: boolean;
  dim: Dim;
  x: number;
  y: number;
  z: number;
  yaw?: number | null;
  pitch?: number | null;
  poseAge?: number | null;
  hp: number;
  food: number;
  job?: string | null;
  brain?: string | null;
  held?: string | null;
  /** An in-process body's own armour and hand items (the entity slots); without it the bot holds `held` only. */
  equipment?: Equipment | null;
  claims?: string[] | null;
  live?: boolean;
  ageSeconds?: number;
}

/** An item or block: [id, meta/damage] (version-specific numbering), or null for an empty slot. */
export type ItemRef = [number, number] | null;

/** A worn or held item: [id, meta], or [id, meta, rgb] when it is dyed (1.12.2 leather armour's display.color). The wire's
 * trailing flags object (`{glint: true}`) is read into `Equipment.glint`. */
export type EquipRef = [number, number] | [number, number, number] | null;

export type EquipSlot = 'main' | 'off' | 'head' | 'chest' | 'legs' | 'feet';

/** What an entity holds and wears (players, bots, zombies, skeletons, armor stands…). */
export interface Equipment {
  main?: EquipRef;
  off?: EquipRef;
  head?: EquipRef;
  chest?: EquipRef;
  legs?: EquipRef;
  feet?: EquipRef;
  /** The slots whose item glints (the wire's `{glint: true}`: 1.12.2 enchantments, a potion with effects). */
  glint?: Partial<Record<EquipSlot, boolean>> | null;
}

/** One styled run of a 1.12.2 sign line (`rich`); absent styles are off, no colour = the sign's black. */
export interface SignRun {
  text: string;
  color?: string | null;
  bold?: boolean | null;
  italic?: boolean | null;
  underlined?: boolean | null;
  strikethrough?: boolean | null;
  obfuscated?: boolean | null;
}

/** Entity state flags as the wire names them (the decoder also accepts the older bitfield). */
export interface EntityFlags {
  onFire?: boolean;
  sneaking?: boolean;
  sprinting?: boolean;
  invisible?: boolean;
  glowing?: boolean;
  /** Riding something (boat, minecart, horse…). */
  vehicle?: boolean;
  usingItem?: boolean;
}

/** Bits of `EntityDto.flags` (the wire's bitmask): burning, sneaking, riding, sprinting, using an item, invisible, glowing. */
export const FLAG_ON_FIRE = 0x01, FLAG_SNEAKING = 0x02, FLAG_RIDING = 0x04, FLAG_SPRINTING = 0x08, FLAG_USING_ITEM = 0x10, FLAG_INVISIBLE = 0x20, FLAG_GLOWING = 0x40;
/** @deprecated alias of FLAG_RIDING. */
export const FLAG_VEHICLE = FLAG_RIDING;

export interface EntityDto {
  /** player | mob | item | painting | item_frame | armor_stand | arrow | boat | minecart | falling_block | tnt | xp_orb |
   * ender_crystal | firework | … */
  kind: string;
  name?: string | null;
  dim: Dim;
  x: number;
  y: number;
  z: number;
  id?: number | null;
  type?: number | null;
  item?: number | null;
  damage?: number | null;
  count?: number | null;
  vx?: number | null;
  vy?: number | null;
  vz?: number | null;
  /** Seconds since first seen. */
  age?: number | null;
  yaw?: number | null;
  by?: string | null;
  /** Bitmask: FLAG_ON_FIRE 0x01, FLAG_SNEAKING 0x02, FLAG_RIDING 0x04, FLAG_SPRINTING 0x08, FLAG_USING_ITEM 0x10,
   * FLAG_INVISIBLE 0x20, FLAG_GLOWING 0x40. */
  flags?: number | null;
  /** Legacy numeric variant (slime size, cat type…); prefer `look`. */
  variant?: number | null;
  /**
   * Comma-separated appearance tokens per kind: sheep "red,sheared"; wolf "tame,sitting,angry,collar=red"; pig "saddle";
   * creeper "powered,fusing"; slime/magma "size=N"; ghast "attacking"; blaze "burning"; enderman "screaming"; horse
   * "<colour>,<markings>,tame,saddle,armor=iron|gold|diamond"; donkey/mule/skeleton/zombie horse "tame,saddle,chest"; llama
   * "<colour>,chest,carpet=<dye>"; villager / zombie villager "profession=<farmer…nitwit>,converting"; ocelot
   * "wild|black|red|siamese" + tame flags; rabbit coat or "killer"; parrot colour; shulker colour; boat wood; minecart
   * "chest|furnace|tnt|spawner|hopper|command_block"; armor stand "small,arms,no_base,marker".
   */
  look?: string | null;
  /** The entity this one rides (its id). */
  vehicle?: number | null;
  /** Armor stand pose: [x, y, z] degrees per part. */
  pose?: Partial<Record<'head' | 'body' | 'leftArm' | 'rightArm' | 'leftLeg' | 'rightLeg', [number, number, number]>> | null;
  /** Arrows stuck in a block. */
  inGround?: boolean;
  /** Hand items and armour. */
  equipment?: Equipment | null;
  /** Head yaw (degrees, Minecraft convention) when it differs from the body's `yaw`. */
  headYaw?: number | null;
  /** Baby mob (rendered at the vanilla baby scale). */
  baby?: boolean;
  /** Paintings: the motive (from the wire's `name`: "Kebab", "Aztec"…); paintings and item frames: facing ("north" | "west" |
   * "south" | "east", or 0 S, 1 W, 2 N, 3 E). */
  motive?: string | null;
  facing?: number | string | null;
  /** Item frames: the item shown and its rotation (0..7, 45° steps). */
  rotation?: number | null;
  /** Item frames: the framed item; falling blocks: `block`. */
  frameItem?: ItemRef;
  block?: ItemRef;
  /** Minecarts: what they carry (from `look`: chest | furnace | tnt | spawner | hopper | command_block; absent = plain). */
  minecartType?: number | string | null;
  /** Primed TNT: fuse ticks left. */
  fuse?: number | null;
  /** Drops and item frames: the stack glints (enchanted, a potion with effects). */
  glint?: boolean | null;
  /** Endermen: the carried block. */
  carried?: ItemRef;
  /** Players (1.12.2): the parrots on their shoulders, by colour (red_blue | blue | green | yellow_blue | grey). */
  shoulders?: { left?: string | null; right?: string | null } | null;
  /** Living (1.12.2): arrows stuck in it (drawn on players, like vanilla's LayerArrow). */
  arrows?: number | null;
}

/** A one-off thing that happened to an entity (or at a place: lightning). */
export interface EntityEvent {
  sound?: string | null;
  volume?: number | null;
  pitch?: number | null;
  /** The entity (absent for lightning). */
  id?: number | null;
  /** [entity models] A hive bot the event is about (bots carry no entity id on the wire). */
  name?: string | null;
  dim?: Dim;
  kind: 'swing' | 'swing_off' | 'hurt' | 'death' | 'crit' | 'magic_crit' | 'eat' | 'teleport' | 'lightning' | string;
  /** Unix ms. */
  at: number;
  x?: number | null;
  y?: number | null;
  z?: number | null;
}

export interface ChestState { dim: Dim; x: number; y: number; z: number; openCount: number }

/** `GET api/world/entities[?since=rev]`. */
export interface EntityDelta {
  at?: number;
  rev: number;
  /** True = replace the whole set (no `since`, or the client fell too far behind). */
  full: boolean;
  /** Always every bot, every dimension. */
  bots: BotDto[];
  entities: EntityDto[];
  /** [dim, id] pairs gone since `since`. */
  removed?: Array<[number, number]> | null;
  /** Swings, hurts, deaths, crits, eating, teleports and lightning since `since` (oldest first). */
  events?: EntityEvent[] | null;
  /** Complete sparse snapshot even on deltas: any omitted chest is closed. Missing field = legacy event-only source. */
  chests?: ChestState[] | null;
  /** The number of the stream frame this read reflects (a cache-backed source): a store that already applied this frame leaves
   * every glide alone, so reads between frames never restart them. Absent = always applied. */
  frame?: number;
}

/** A block entity (`GET api/world/blockentities?dim&cols=`): what the block id/meta can't say. Tolerant: unknown fields are
 * kept, missing ones default. */
export interface BlockEntityDto {
  x: number;
  y: number;
  z: number;
  /** sign | bed | banner | skull | flower_pot | chest | trapped_chest | ender_chest | mob_spawner | … */
  kind: string;
  /** Signs: four lines of plain text. */
  lines?: string[] | null;
  /** 1.12.2 signs with styled text: the four lines as runs (drawn instead of `lines`). */
  rich?: SignRun[][] | null;
  /** Beds: dye colour (0..15 or a name). */
  color?: number | string | null;
  /** Banners: base dye colour and [pattern code, dye colour] layers, bottom first. */
  base?: number | string | null;
  patterns?: Array<[string, number | string]> | null;
  /** Skulls: type (0 skeleton, 1 wither, 2 zombie, 3 player, 4 creeper, 5 dragon, or the name; the wire's `skull`) and
   * rotation (0..15); `owner` for player heads. */
  type?: number | string | null;
  rotation?: number | null;
  owner?: string | null;
  /** Flower pots: the plant ([id, meta] or a name). */
  item?: ItemRef | string | null;
  /** Spawners: the mob ("Zombie", "minecraft:zombie"…). */
  mob?: string | null;
  [extra: string]: unknown;
}

export interface BlockEntityColumn {
  cx: number;
  cz: number;
  hash: WireHash;
  entities: BlockEntityDto[];
}

export interface BlockEntities {
  dim?: Dim;
  columns: BlockEntityColumn[];
}

export type Vec3i = [number, number, number];

export interface Mark {
  kind: string;
  text?: string | null;
  min: Vec3i;
  max: Vec3i;
  owner?: string | null;
}

/** [x, y, z, id, meta, done (0|1)] */
export type PlanBlock = [number, number, number, number, number, number];

export interface PlanDto {
  id: number | string;
  name: string;
  status?: string | null;
  part?: number | null;
  blocks: PlanBlock[];
}

/** `GET api/world/overlays?dim`. `notes` are status strings ("memory unavailable"), not places. */
export interface Overlays {
  at?: string | number;
  dim: Dim;
  claims: Mark[];
  places: Mark[];
  points: Mark[];
  plans: PlanDto[];
  dangers: Mark[];
  notes: string[];
}

/** One block change `[x, y, z, id, meta]`. */
export type BlockChange = [number, number, number, number, number];

/**
 * `GET api/world/blocks?since=REV` (or the bridge's SSE `blocks` event), as the bot sends it:
 * `{at, rev, full, more, blocks: {"<dim>": [[x,y,z,id,meta], …]}, columns: [{rev, at, dim, cx, cz, kind}]}`. Changes are in rev
 * order and a later entry for the same cell wins. The first reply (no `since`) is `full` with empty lists.
 */
export interface BlockDeltas {
  at?: number;
  rev: number;
  /** True (lists empty) on the first read, after a bot restart, a ring overflow or an idle gap: refetch by the index hash-diff. */
  full: boolean;
  /** More changes are waiting: ask again at once with `since = rev`. */
  more?: boolean;
  /** Changes per dimension (keys are the dimension as a string). A plain array means dimension `dim` (default 0). */
  blocks: Record<string, BlockChange[]> | BlockChange[];
  dim?: Dim;
  /** Columns that came into (`loaded`) or left (`unloaded`) a bot's view. `loaded` for a column already held means its
   * contents changed in bulk (b1.8.1 chunk updates for explosions, large edits): refetch it. */
  /** `block_entity`: the column's block entities changed (a sign edited, a banner placed): refetch them. */
  columns?: Array<{ rev?: number; at?: number; dim: Dim; cx: number; cz: number; kind: 'loaded' | 'unloaded' | 'block_entity' }>;
}

/** Deprecated: the bridge's runtime atlas. The viewer uses the pack imported from the user's own jar (loadBundle) and ignores it. */
export interface AtlasResource {
  image: ImageBitmap | HTMLImageElement | HTMLCanvasElement;
  map: unknown;
}

/**
 * Everything the viewer reads, injected by the app (authenticated fetches to its own `/api/world/*`). Each call may reject;
 * the viewer backs off per route. `chunks` must return the raw `application/octet-stream` MCWC body (ask with `light=1`).
 */
export interface WorldSource {
  /** Abort pending reads when this runtime has no active views; later calls may fetch again. Cancellable sources belong
   * to one runtime (its views may share it); create separate source instances for independent runtimes. */
  cancelPending?(): void;
  index(dim: Dim, x: number, z: number, r: number): Promise<WorldIndex>;
  chunks(dim: Dim, cols: Array<[number, number]>): Promise<ArrayBuffer>;
  entities(since?: number): Promise<EntityDelta>;
  /** Push hook: call `onFrame` whenever new entity data is ready (a stream frame landed), and the viewer reads `entities()`
   * at once instead of on its next poll; the poll then only backs the push up. Returns an unsubscribe function. A source with
   * this hook should number its frames (`EntityDelta.frame`), so a read between frames keeps the glides at their pace. */
  onEntities?(onFrame: () => void): () => void;
  overlays(dim: Dim): Promise<Overlays>;
  /** Deprecated and ignored: blocks are textured from the bundled atlas (RuntimeOptions.textures = false for flat colours). */
  atlas?(): Promise<AtlasResource | null>;
  /** Per-block changes since a revision (polled ~4 Hz when there is no `subscribe`). Optional: without it columns only
   * update through the index hash-diff. */
  blocks?(since?: number): Promise<BlockDeltas>;
  /** Block entities (signs, beds, banners, skulls, flower pots, spawners…) of some columns. Optional: without it those blocks
   * draw with defaults (blank signs, red beds, white banners, skeleton skulls, empty pots, an idle spawner). */
  blockEntities?(dim: Dim, cols: Array<[number, number]>): Promise<BlockEntities>;
  /** Push hook (the bridge's SSE `blocks` event): call `onBlocks` per delta, and `onReconnect` after the stream dropped and came
   * back (the viewer then refetches through the index). Returns an unsubscribe function. */
  subscribe?(onBlocks: (d: BlockDeltas) => void, onReconnect?: () => void): () => void;
  /** Push hook for chat bubbles: call `onSay` for every line a player or bot says (already filtered: no private speech, no
   * system lines). The viewer shows it as a speech bubble over the body of that name in every view. Returns an unsubscribe
   * function. Optional: `WorldRuntime.say` does the same imperatively. */
  chat?(onSay: (line: ChatSay) => void): () => void;
}

/** One spoken line for a chat bubble: the speaker's name (bot name or short key, or a player's username; any case). */
export interface ChatSay {
  from: string;
  text: string;
  /** chat (a player in game), said (a bot), whisper, viewer (sent from the web as the linked player). Default chat. */
  kind?: 'chat' | 'said' | 'whisper' | 'viewer';
}

/** follow (orbit), fly, iso (narrow-FOV dolly, 35° down), top (the same looking straight down, north up: the C# top panel). */
export type CameraMode = 'follow' | 'fly' | 'iso' | 'top';

/** auto = cut only when the followed body is hidden (the AI pictures' rule) or the camera is inside terrain. */
export type XRayMode = 'auto' | 'on' | 'off';

export interface LayerToggles {
  bots: boolean;
  /** Mobs, players, drops. */
  entities: boolean;
  claims: boolean;
  places: boolean;
  points: boolean;
  /** Build plans as ghost blocks (done vs todo). */
  plans: boolean;
  dangers: boolean;
  notes: boolean;
  weather: boolean;
  /** Roof cut-away above the followed bot's head when it is under cover (remeshed caps, hatched). */
  cutaway: boolean;
  /** Dim columns not seen live. */
  staleness: boolean;
  /** Briefly outline blocks that just changed (block deltas). */
  changes: boolean;
  /** Reading aids around the followed bot: ground grid with coordinate rulers, distance rings, compass. */
  aids: boolean;
  /** Chat bubbles over the heads of bots and players that speak. */
  bubbles: boolean;
}

export const DEFAULT_LAYERS: LayerToggles = {
  bots: true,
  entities: true,
  claims: true,
  places: true,
  points: true,
  plans: true,
  dangers: true,
  notes: true,
  weather: true,
  cutaway: true,
  staleness: true,
  changes: true,
  aids: false,
  bubbles: true,
};

/** What `onPick` reports for a clicked block. */
export interface PickedBlock {
  x: number;
  y: number;
  z: number;
  id: number;
  meta: number;
  /** Minecraft side order: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east. */
  face: number;
  name: string;
  dim: Dim;
  /** The column was seen live (else remembered). */
  live: boolean;
  seen: number;
  by: string | null;
}

export interface ViewerStats {
  fps: number;
  frameMs: number;
  drawCalls: number;
  triangles: number;
  sections: number;
  sectionsQueued: number;
  columns: number;
  bytesReceived: number;
  version: string;
  textured: boolean;
  gpu: string;
  /** Shared-runtime memory (one per page or provider): views, sections, section geometries, GPU and worker bytes. */
  memory: RuntimeMemory;
  /** Adaptive resolution factor of the interactive view (1 = full). */
  resolutionScale: number;
  /** Block changes applied from the delta feed. */
  blockChanges: number;
}
