// Wire types of the supervisor's world routes (docs/web/reference/explorer-spec.md §13), as the app hands them to the viewer.
// Nest mirrors the bridge with camelCase keys; `normalize*` (wire/normalize.ts) also accepts the raw snake_case replies.

import type { AtlasMap } from './atlas/types';
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

export interface EntityDto {
  /** player | mob | item | … */
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
  /** 0x01 burning, 0x02 sneaking, 0x08 sprinting. */
  flags?: number | null;
  variant?: number | null;
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
  columns?: Array<{ rev?: number; at?: number; dim: Dim; cx: number; cz: number; kind: 'loaded' | 'unloaded' }>;
}

export interface AtlasResource {
  image: ImageBitmap | HTMLImageElement | HTMLCanvasElement;
  map: AtlasMap;
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
  /** Optional in practice: reject (or resolve null) and the viewer draws its flat-colour palette. */
  atlas(): Promise<AtlasResource | null>;
  /** Per-block changes since a revision (polled ~4 Hz when there is no `subscribe`). Optional: without it columns only
   * update through the index hash-diff. */
  blocks?(since?: number): Promise<BlockDeltas>;
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
