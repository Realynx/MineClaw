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
  /** World time in ticks. */
  time?: number | null;
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
  claims?: string[] | null;
  live?: boolean;
  ageSeconds?: number;
}

/** An item or block: [id, meta/damage] (version-specific numbering), or null for an empty slot. */
export type ItemRef = [number, number] | null;

/** What an entity holds and wears (players, bots, zombies, skeletons, armor stands…). */
export interface Equipment {
  main?: ItemRef;
  off?: ItemRef;
  head?: ItemRef;
  chest?: ItemRef;
  legs?: ItemRef;
  feet?: ItemRef;
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
}

/** Bit values of `EntityDto.flags` after normalisation (the bitfield form the renderer reads). */
export const FLAG_ON_FIRE = 0x01, FLAG_SNEAKING = 0x02, FLAG_SPRINTING = 0x08, FLAG_INVISIBLE = 0x20, FLAG_GLOWING = 0x40, FLAG_VEHICLE = 0x80;

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
  /** Bitfield after normalisation: FLAG_ON_FIRE 0x01, FLAG_SNEAKING 0x02, FLAG_SPRINTING 0x08, FLAG_INVISIBLE 0x20,
   * FLAG_GLOWING 0x40, FLAG_VEHICLE 0x80 (the wire's `flags` object is folded into this). */
  flags?: number | null;
  /** Per-kind variant: a number (slime size, cat type, rabbit type, villager profession…) or a compact string such as
   * "white,sheared" / "creamy,white_spots,saddle,iron" / "collar:red,angry,tame" / "powered" (decoded by the renderer). */
  variant?: number | string | null;
  /** Hand items and armour. */
  equipment?: Equipment | null;
  /** Head yaw (degrees, Minecraft convention) when it differs from the body's `yaw`. */
  headYaw?: number | null;
  /** Baby mob (rendered at the vanilla baby scale). */
  baby?: boolean;
  /** Paintings: the motive ("Kebab", "Aztec"… or the 1.12.2 id "kebab"); paintings and item frames: facing (0 S, 1 W, 2 N,
   * 3 E, or "north"…). */
  motive?: string | null;
  facing?: number | string | null;
  /** Item frames: the item shown and its rotation (0..7, 45° steps). */
  rotation?: number | null;
  /** Item frames: the framed item; falling blocks: `block`. */
  frameItem?: ItemRef;
  block?: ItemRef;
  /** Minecarts: type (0 plain, 1 chest, 2 furnace, 3 tnt, 4 spawner, 5 hopper, 6 command block, or the name). */
  minecartType?: number | string | null;
  /** Primed TNT: fuse ticks left. */
  fuse?: number | null;
}

/** A one-off thing that happened to an entity (or at a place: lightning). */
export interface EntityEvent {
  /** The entity (absent for lightning). */
  id?: number | null;
  dim?: Dim;
  kind: 'swing' | 'swing_off' | 'hurt' | 'death' | 'crit' | 'magic_crit' | 'eat' | 'teleport' | 'lightning' | string;
  /** Unix ms. */
  at: number;
  x?: number | null;
  y?: number | null;
  z?: number | null;
}

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
  /** Beds: dye colour (0..15 or a name). */
  color?: number | string | null;
  /** Banners: base dye colour and [pattern code, dye colour] layers, bottom first. */
  base?: number | string | null;
  patterns?: Array<[string, number | string]> | null;
  /** Skulls: type (0 skeleton, 1 wither, 2 zombie, 3 player, 4 creeper, 5 dragon, or the name) and rotation (0..15). */
  type?: number | string | null;
  rotation?: number | null;
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

/** Deprecated: the bridge's runtime atlas. The viewer ships its own textures (generated/<version>/) and ignores it. */
export interface AtlasResource {
  image: ImageBitmap | HTMLImageElement | HTMLCanvasElement;
  map: unknown;
}

/**
 * Everything the viewer reads, injected by the app (authenticated fetches to its own `/api/world/*`). Each call may reject;
 * the viewer backs off per route. `chunks` must return the raw `application/octet-stream` MCWC body (ask with `light=1`).
 */
export interface WorldSource {
  index(dim: Dim, x: number, z: number, r: number): Promise<WorldIndex>;
  chunks(dim: Dim, cols: Array<[number, number]>): Promise<ArrayBuffer>;
  entities(since?: number): Promise<EntityDelta>;
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
