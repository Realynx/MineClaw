/**
 * Live hive (explorer-spec §12–13, bot-api-spec §1.2–1.3): supervisor pass-through routes.
 * C# sources: `src/MineClaw.App/Supervisor/SupervisorModels.cs`, `SupervisorWorldRoutes.cs`, `LiveSupervisorData.cs`.
 */
import { z } from 'zod';
import { DimId, Double, IsoDateTime, JsonValue, QBool, QInt, UnixMs, Vec3i } from './common.js';

export const BotParams = z.object({ bot: z.string().min(1).max(64).describe('name, short ("67") or "main"') });

/** What a body is in the hive tree: main, a hive (own brain), a subhive body (driven by its owner hive's brain), script-only. */
export const HiveBodyKind = z.enum(['main', 'hive', 'subhive', 'script']);
export type HiveBodyKind = z.infer<typeof HiveBodyKind>;

/** The script a body runs right now (additive on the bot lists; the full debugger is /api/bodies/{name}/script). */
export const BodyScriptBrief = z.object({
  name: z.string(),
  status: z.string().nullish().describe('"running" | "paused" | …'),
  line: z.number().int().nullish(),
  paused: z.boolean().nullish(),
  version: z.number().int().nullish(),
});
export type BodyScriptBrief = z.infer<typeof BodyScriptBrief>;

/** Additive hierarchy fields every bot list row carries (absent on older bots: fall back to the hive tree). */
const BodyPlace = {
  kind: HiveBodyKind.nullish(),
  owner: z.string().nullish().describe('the owning hive account (main’s login for main’s own children); null for main'),
  script: BodyScriptBrief.nullish(),
  state: z.string().nullish().describe('the hive tree state: "spawning" | "running" | "releasing"'),
};

/** C# BotTile. */
export const BotTile = z.object({
  name: z.string(),
  short: z.string(),
  main: z.boolean(),
  dim: DimId,
  x: z.number(),
  y: z.number(),
  z: z.number(),
  hp: z.number().int(),
  food: z.number().int(),
  job: z.string().nullish(),
  brain: z.string().nullish(),
  ageSeconds: Double,
  claims: z.array(z.string()),
  ...BodyPlace,
  online: z.boolean().nullish().describe('false = in the hive tree but not in game yet (spawning) or gone'),
});
export type BotTile = z.infer<typeof BotTile>;

/** GET /api/bots. */
export const BotsResponse = z.object({ at: IsoDateTime, bots: z.array(BotTile) });
export type BotsResponse = z.infer<typeof BotsResponse>;

export const ThoughtToolCall = z.object({
  at: IsoDateTime,
  name: z.string(),
  args: z.string().nullish(),
  result: z.string().nullish().describe('null = still in flight'),
  ok: z.boolean().nullish(),
});
export type ThoughtToolCall = z.infer<typeof ThoughtToolCall>;

/** C# ThoughtSnapshot (capped: output 400, thinking 600, tool args 60, tool result 120 chars). */
export const ThoughtSnapshot = z.object({
  updatedAt: IsoDateTime,
  turnAt: IsoDateTime.nullish(),
  header: z.string().nullish(),
  events: z.string().nullish(),
  digest: z.string().nullish(),
  memory: z.string().nullish(),
  output: z.string().nullish(),
  thinking: z.string().nullish(),
  tools: z.array(ThoughtToolCall).nullish(),
  task: z.string().nullish(),
  delegate: z.string().nullish(),
  journal: z.string().nullish(),
});
export type ThoughtSnapshot = z.infer<typeof ThoughtSnapshot>;

export const TierUsage = z.object({
  tier: z.string(),
  usd: z.number(),
  calls: z.number().int(),
  cacheHit: Double,
  promptTokens: z.number().int(),
  outputTokens: z.number().int(),
});

export const BotUsage = z.object({
  todayUsd: z.number(),
  monthUsd: z.number(),
  dailyCapUsd: z.number(),
  monthlyCapUsd: z.number(),
  mode: z.string(),
  cacheHit: Double,
  today: z.array(TierUsage),
  month: z.array(TierUsage),
  callsLastHour: z.number().int(),
  callsPerHour: z.array(z.number().int()).describe('24 buckets'),
  last: z
    .object({
      at: IsoDateTime,
      tier: z.string(),
      model: z.string(),
      purpose: z.string().nullish(),
      costUsd: z.number(),
    })
    .nullish(),
  latency: JsonValue.describe('LatencySummary; shape owned by the bot'),
});
export type BotUsage = z.infer<typeof BotUsage>;

export const BotStatus = z.enum(['offline', 'connecting', 'online', 'dead', 'paused']);
export type BotStatus = z.infer<typeof BotStatus>;

export const BotStatusView = z.object({
  name: z.string(),
  status: BotStatus,
  at: IsoDateTime,
  pos: z.string().nullish().describe('"x,y,z" floored'),
  dim: DimId,
  hp: z.number().int(),
  food: z.number().int(),
  task: z.string().nullish(),
  brain: z.string().nullish(),
  budgetMode: z.string(),
  header: z.string().nullish().describe('selected bot only'),
  usage: BotUsage.nullish(),
  thought: ThoughtSnapshot.nullish().describe('selected bot only'),
  chat: z.array(z.string()).describe('last 60 (selected) or 12 raw lines, "HH:mm:ss " prefixed'),
  short: z.string().nullish().describe('"main", "67"'),
  ...BodyPlace,
});
export type BotStatusView = z.infer<typeof BotStatusView>;

export const HiveSlot = z.object({
  name: z.string(),
  number: z.union([z.number().int(), z.string()]).describe('the clone number; the bot sends it as a string ("111")'),
  state: z.string(),
  connected: z.boolean(),
  task: z.string().nullish(),
  pending: z.union([z.boolean(), z.number().int()]).nullish().describe('the bot sends a bool (a task waiting)'),
  progress: z.union([z.string(), z.number()]).nullish(),
  lastProgress: z.union([IsoDateTime, z.number()]).nullish(),
  result: z.string().nullish(),
  deaths: z.number().int().nullish(),
  lastExit: z.string().nullish(),
});
export type HiveSlot = z.infer<typeof HiveSlot>;

export const StatusQuery = z.object({ bot: z.string().max(64).optional() });

/** GET /api/status?bot (also the `bots` SSE payload, without a selected bot). */
export const StatusResponse = z.object({
  at: IsoDateTime,
  main: z.string(),
  bots: z.array(BotStatusView),
  hive: z
    .object({
      list: JsonValue,
      clonesTodayUsd: z.number(),
      clonesMonthUsd: z.number(),
      slots: z.array(HiveSlot),
    })
    .nullish(),
  claims: z.array(z.object({ owner: z.string(), what: z.string() })),
});
export type StatusResponse = z.infer<typeof StatusResponse>;

/** C# FeedEvent. Kinds: danger, death, conn, move, players, hive. */
export const SupervisorEvent = z.object({
  id: z.number().int(),
  at: IsoDateTime,
  kind: z.string(),
  level: z.string(),
  text: z.string(),
});
export type SupervisorEvent = z.infer<typeof SupervisorEvent>;

export const SinceQuery = z.object({ since: QInt.min(0).default(0) });

/** GET /api/events?since (≤ 200 per call from a 400-entry ring). Also the `event` SSE payload. */
export const EventsResponse = z.object({ events: z.array(SupervisorEvent), last: z.number().int() });
export type EventsResponse = z.infer<typeof EventsResponse>;

/** GET /api/hive/tree (HiveTreeNodeDto). Also the `hive` SSE payload. */
export interface HiveTreeNode {
  id: string;
  name: string;
  kind: 'main' | 'hive' | 'subhive' | 'script';
  brain?: string | null | undefined;
  owner?: string | null | undefined;
  state: string;
  task?: string | null | undefined;
  children?: HiveTreeNode[] | null | undefined;
}
export const HiveTreeNode: z.ZodType<HiveTreeNode> = z.lazy(() =>
  z.object({
    id: z.string(),
    name: z.string(),
    kind: z.enum(['main', 'hive', 'subhive', 'script']),
    brain: z.string().nullish(),
    owner: z.string().nullish(),
    state: z.string(),
    task: z.string().nullish(),
    children: z.array(HiveTreeNode).nullish(),
  }),
);

export const LocateQuery = z.object({ q: z.string().min(1).max(200) });
/** GET /api/locate?q (404 = "no place called …"). */
export const LocateResult = z.object({ name: z.string(), dim: DimId, x: z.number(), y: z.number(), z: z.number() });
export type LocateResult = z.infer<typeof LocateResult>;

// ---- world ----

export const WorldIndexQuery = z.object({
  dim: QInt.default(0),
  x: QInt,
  z: QInt,
  r: QInt.min(16).max(256).default(96),
  light: QBool.optional(),
});
export type WorldIndexQuery = z.infer<typeof WorldIndexQuery>;

export const WorldColumnInfo = z.object({
  cx: z.number().int(),
  cz: z.number().int(),
  hash: z
    .union([z.string().regex(/^-?\d+$/), z.number()])
    .describe('i64 as a decimal string (Nest parses bridge JSON with `parseBridgeJson`); compare with `hashKey`'),
  seen: UnixMs,
  by: z.string().nullish(),
  live: z.boolean(),
});
export type WorldColumnInfo = z.infer<typeof WorldColumnInfo>;

/** GET /api/world/index. `at` is unix ms here. */
export const WorldIndex = z.object({
  dim: DimId,
  at: UnixMs,
  columns: z.array(WorldColumnInfo),
  version: z.enum(['b1.8.1', '1.12.2']),
  time: z.number().describe('world ticks (negative from a 1.12.2 server whose daylight cycle is stopped)'),
  timeFrozen: z.boolean().nullish().describe('true = the daylight cycle is stopped: the time does not advance'),
  rain: Double.describe('0..1'),
  thunder: Double.describe('0..1'),
});
export type WorldIndex = z.infer<typeof WorldIndex>;

/**
 * i64 hashes beyond 2^53 lose precision in a plain JSON.parse. Nest parses bridge JSON with `parseBridgeJson`, which turns
 * every `hash` number into its exact decimal string, so the public API always carries strings. Clients compare with this key.
 */
export function hashKey(hash: number | string): string {
  return typeof hash === 'string' ? hash : String(hash);
}

/** GET /api/world/chunks: binary MCWC (application/octet-stream), documented in api.md. */
export const WorldChunksQuery = z.object({
  dim: QInt.default(0),
  cols: z
    .string()
    .regex(/^-?\d+,-?\d+(;-?\d+,-?\d+){0,63}$/)
    .describe('"cx,cz;cx,cz;…" ≤ 64 columns'),
  light: QBool.optional(),
});
export type WorldChunksQuery = z.infer<typeof WorldChunksQuery>;

/** An item or block [id, meta/damage] (version-specific numbering), or null for an empty slot. */
export const ItemRef = z.tuple([z.number().int(), z.number().int()]).nullable();
export type ItemRef = z.infer<typeof ItemRef>;

/** An item's render flags, the optional last element of an equipment slot: `glint` = vanilla draws the enchantment glint
 * (1.12.2 enchantments or a potion with effects; b1.8.1 items carry no NBT). Loose: later flags pass through. */
export const ItemFlags = z.looseObject({ glint: z.boolean().optional() });
export type ItemFlags = z.infer<typeof ItemFlags>;

/** A worn or held item: [id, meta/damage], plus the dye colour 0xRRGGBB as a third element when the item is dyed (1.12.2
 * leather armour's display.color; b1.8.1 has no dyed items), plus a trailing {@link ItemFlags} object when it glints
 * (`[id, meta, {glint: true}]`, `[id, meta, rgb, {glint: true}]`), or null for an empty slot. A reader that only takes numbers
 * ignores the object. */
export const EquipmentItem = z
  .union([
    z.tuple([z.number().int(), z.number().int()]),
    z.tuple([z.number().int(), z.number().int(), z.number().int()]),
    z.tuple([z.number().int(), z.number().int(), ItemFlags]),
    z.tuple([z.number().int(), z.number().int(), z.number().int(), ItemFlags]),
  ])
  .nullable();
export type EquipmentItem = z.infer<typeof EquipmentItem>;

/** Whether an equipment slot glints (its trailing flags object says `glint: true`). */
export function equipmentGlints(v: EquipmentItem | undefined): boolean {
  const last = v ? v[v.length - 1] : null;
  return !!last && typeof last === 'object' && last.glint === true;
}

/** A player's shoulder parrots by colour (red_blue | blue | green | yellow_blue | grey); a free shoulder is absent. */
export const WorldShoulders = z.looseObject({ left: z.string().nullish(), right: z.string().nullish() });
export type WorldShoulders = z.infer<typeof WorldShoulders>;

/** Hand items and armour. */
export const WorldEquipment = z.looseObject({
  main: EquipmentItem.optional(),
  off: EquipmentItem.optional(),
  head: EquipmentItem.optional(),
  chest: EquipmentItem.optional(),
  legs: EquipmentItem.optional(),
  feet: EquipmentItem.optional(),
});
export type WorldEquipment = z.infer<typeof WorldEquipment>;

/** C# WorldBot. */
export const WorldBot = z.object({
  name: z.string(),
  short: z.string(),
  main: z.boolean(),
  dim: DimId,
  x: z.number(),
  y: z.number(),
  z: z.number(),
  yaw: z.number().nullish(),
  pitch: z.number().nullish(),
  poseAge: Double.nullish(),
  hp: z.number().int(),
  food: z.number().int(),
  job: z.string().nullish(),
  brain: z.string().nullish(),
  held: z.string().nullish(),
  claims: z.array(z.string()),
  live: z.boolean(),
  ageSeconds: Double,
  kind: HiveBodyKind.nullish(),
  owner: z.string().nullish(),
  online: z.boolean().nullish(),
  equipment: WorldEquipment.nullish().describe('an in-process body: what it wears and holds, the entity slots (glint included)'),
});
export type WorldBot = z.infer<typeof WorldBot>;


/** Armor stand pose: [x, y, z] degrees per part. */
export const ArmorStandPose = z.looseObject({
  head: z.tuple([z.number(), z.number(), z.number()]).optional(),
  body: z.tuple([z.number(), z.number(), z.number()]).optional(),
  leftArm: z.tuple([z.number(), z.number(), z.number()]).optional(),
  rightArm: z.tuple([z.number(), z.number(), z.number()]).optional(),
  leftLeg: z.tuple([z.number(), z.number(), z.number()]).optional(),
  rightLeg: z.tuple([z.number(), z.number(), z.number()]).optional(),
});

/**
 * C# WorldEntity. Velocity in blocks/tick. Fields are omitted when unknown or default. Loose: fields the bot adds pass
 * through to the viewer, which decodes tolerantly. Kind-specific fields: painting `name` (motive) + `facing`; item_frame
 * `item`/`damage` + `rotation` + `facing`; armor_stand `pose` + equipment + `look`; arrow `in_ground`; falling_block `block`;
 * tnt `fuse`; xp_orb `count`; firework `item`; object `name`. The last looks (2026-11): equipment slots may end in a
 * `{glint: true}` flags object; item / item_frame `glint`; enderman `carried`; player `shoulders`; living `arrows`; iron golem
 * `look` token `holding_rose` (for 400 ticks after Entity Status 11, until 34).
 */
export const WorldEntity = z.looseObject({
  kind: z
    .string()
    .describe('player | mob | item | painting | item_frame | armor_stand | arrow | boat | minecart | falling_block | tnt | xp_orb | ender_crystal | firework | …'),
  name: z.string().nullish(),
  dim: DimId,
  x: z.number(),
  y: z.number(),
  z: z.number(),
  id: z.number().int().nullish(),
  type: z.number().int().nullish(),
  item: z.number().int().nullish(),
  damage: z.number().int().nullish(),
  count: z.number().int().nullish(),
  vx: z.number().nullish(),
  vy: z.number().nullish(),
  vz: z.number().nullish(),
  age: Double.nullish(),
  yaw: z.number().nullish(),
  by: z.string().nullish(),
  flags: z.number().int().nullish().describe('0x01 burning, 0x02 sneaking, 0x04 riding, 0x08 sprinting, 0x10 using item, 0x20 invisible, 0x40 glowing'),
  variant: z.number().int().nullish().describe('legacy; prefer look'),
  look: z.string().nullish().describe('comma-separated appearance tokens per kind ("red,sheared", "profession=farmer", …)'),
  equipment: WorldEquipment.nullish().describe('[id, meta], [id, meta, rgb] (dyed), plus a trailing {glint: true} when enchanted, per slot; empty slots and an empty object omitted'),
  headYaw: z.number().nullish().describe('1.12.2'),
  baby: z.boolean().nullish().describe('1.12.2'),
  vehicle: z.number().int().nullish().describe('the ridden entity id'),
  facing: z.string().nullish().describe('north | west | south | east'),
  rotation: z.number().int().nullish(),
  pose: ArmorStandPose.nullish(),
  inGround: z.boolean().nullish(),
  block: ItemRef.nullish(),
  fuse: z.number().int().nullish(),
  glint: z.boolean().nullish().describe('item / item_frame: the dropped or framed stack glints (1.12.2 enchantments, a potion with effects)'),
  carried: ItemRef.nullish().describe('enderman: the carried block [id, meta] (1.12.2 CARRIED_BLOCK, b1.8.1 metadata 16/17)'),
  shoulders: WorldShoulders.nullish().describe('player (1.12.2): the parrots on its shoulders by colour'),
  arrows: z.number().int().nullish().describe('living (1.12.2): arrows stuck in it (vanilla draws them on players)'),
});
export type WorldEntity = z.infer<typeof WorldEntity>;

/** A one-off entity event (lightning: a position and no id). */
export const WorldEntityEvent = z.looseObject({
  id: z.number().int().nullish(),
  dim: DimId.nullish(),
  kind: z.string().describe('swing | swing_off | hurt | death | crit | magic_crit | eat | teleport | lightning | sound'),
  at: UnixMs,
  x: z.number().nullish(),
  y: z.number().nullish(),
  z: z.number().nullish(),
  sound: z.string().nullish(),
  volume: z.number().nullish(),
  pitch: z.number().nullish(),
});
export type WorldEntityEvent = z.infer<typeof WorldEntityEvent>;

export const WorldEntitiesQuery = z.object({ dim: QInt.optional(), since: QInt.min(0).optional() });

/**
 * GET /api/world/entities[?since=rev] and the `entities` SSE payload. Without `since` (or since=0): everything, `full` true.
 * `full` true = replace the whole set; otherwise `entities` are changed ones and `removed` = [[dim,id],…]. Bots always in full.
 */
export const WorldEntities = z.object({
  at: UnixMs,
  rev: z.number().int().nullish().describe('absent on the plain ?dim form'),
  full: z.boolean().nullish(),
  bots: z.array(WorldBot),
  entities: z.array(WorldEntity),
  removed: z.array(z.tuple([z.number().int(), z.number().int()])).nullish(),
  events: z.array(WorldEntityEvent).nullish().describe('swings, hurts, deaths, crits, eating, teleports, lightning since `since`'),
});
export type WorldEntities = z.infer<typeof WorldEntities>;

/** C# WorldMark. */
export const WorldMark = z.object({
  kind: z.string(),
  text: z.string(),
  min: Vec3i,
  max: Vec3i,
  owner: z.string().nullish(),
});
export type WorldMark = z.infer<typeof WorldMark>;

/** Ghost blocks [x, y, z, id, meta, done(0/1)]. */
export const WorldPlanBlock = z.tuple([
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
]);
export const WorldPlan = z.object({
  id: z.number().int(),
  name: z.string(),
  status: z.string(),
  part: z.number().int(),
  blocks: z.array(WorldPlanBlock),
});
export type WorldPlan = z.infer<typeof WorldPlan>;

export const DimQuery = z.object({ dim: QInt.default(0) });

/** GET /api/world/blocks?since=rev[&dim]: per-block world changes since a revision (dim omitted = every dimension). */
export const WorldBlocksQuery = z.object({ since: QInt.min(0).default(0), dim: QInt.optional() });
export type WorldBlocksQuery = z.infer<typeof WorldBlocksQuery>;

/** One changed block [x, y, z, id, meta]. */
export const WorldBlockChange = z.tuple([z.number().int(), z.number().int(), z.number().int(), z.number().int(), z.number().int()]);
export type WorldBlockChange = z.infer<typeof WorldBlockChange>;

/**
 * A column event of the block feed: `loaded` = (re)fetch this column through /api/world/chunks (on b1.8.1 it can also mean a
 * partial chunk update), `unloaded` = it left live view (keep it as remembered), `block_entity` = its block entities changed
 * (refetch /api/world/blockentities for it).
 */
export const WorldColumnChange = z.object({
  rev: z.number().int(),
  at: UnixMs,
  dim: DimId,
  cx: z.number().int(),
  cz: z.number().int(),
  kind: z.enum(['loaded', 'unloaded', 'block_entity']),
});
export type WorldColumnChange = z.infer<typeof WorldColumnChange>;

/** GET /api/world/blockentities?dim&cols=cx,cz;… (≤ 64 columns). */
export const WorldBlockEntitiesQuery = z.object({
  dim: QInt.default(0),
  cols: z
    .string()
    .regex(/^-?\d+,-?\d+(;-?\d+,-?\d+){0,63}$/)
    .describe('"cx,cz;cx,cz;…" ≤ 64 columns'),
});
export type WorldBlockEntitiesQuery = z.infer<typeof WorldBlockEntitiesQuery>;

/** One styled piece of a 1.12.2 sign line (`rich`): only the styles that are on are present; no colour = the sign's black. */
export const SignRun = z.looseObject({
  text: z.string(),
  color: z.string().nullish().describe('black | dark_blue | dark_green | dark_aqua | dark_red | dark_purple | gold | gray | dark_gray | blue | green | aqua | red | light_purple | yellow | white'),
  bold: z.boolean().nullish(),
  italic: z.boolean().nullish(),
  underlined: z.boolean().nullish(),
  strikethrough: z.boolean().nullish(),
  obfuscated: z.boolean().nullish(),
});
export type SignRun = z.infer<typeof SignRun>;

/**
 * One block entity. Loose (kind-specific fields pass through): sign `lines` [4] (plain) and, for a styled 1.12.2 sign, `rich`
 * [4][runs]; bed `color`; banner `color` (base) +
 * `patterns` [{pattern, color}]; skull `skull` (type) + `rotation` + `owner`; flower_pot `item` + `data`; mob_spawner `mob`;
 * jukebox `record`; note_block `note`; chest / trapped_chest / ender_chest. b1.8.1 sends sign data only.
 */
export const WorldBlockEntity = z.looseObject({
  x: z.number().int(),
  y: z.number().int(),
  z: z.number().int(),
  kind: z.string().describe('sign | bed | banner | skull | flower_pot | chest | trapped_chest | ender_chest | mob_spawner | jukebox | note_block | …'),
  lines: z.array(z.string()).nullish(),
  rich: z.array(z.array(SignRun)).nullish().describe('1.12.2 signs with JSON styles: the four lines as styled runs (component styles, extra, § codes)'),
  color: z.union([z.number().int(), z.string()]).nullish(),
  patterns: z.array(z.looseObject({ pattern: z.string(), color: z.union([z.number().int(), z.string()]) })).nullish(),
  skull: z.union([z.number().int(), z.string()]).nullish(),
  rotation: z.number().int().nullish(),
  owner: z.string().nullish(),
  item: z.union([z.number().int(), z.string()]).nullish(),
  data: z.number().int().nullish(),
  mob: z.string().nullish(),
  record: z.union([z.number().int(), z.string()]).nullish(),
  note: z.number().int().nullish(),
});
export type WorldBlockEntity = z.infer<typeof WorldBlockEntity>;

/** A column's block entities with their hash (unchanged hash = nothing to redraw). */
export const WorldBlockEntityColumn = z.object({
  cx: z.number().int(),
  cz: z.number().int(),
  hash: z.union([z.number(), z.string()]).describe('FNV-1a of the column\'s block entities; 0 when none'),
  entities: z.array(WorldBlockEntity),
});

/** GET /api/world/blockentities: columns asked for that have block entities (a column left out has none). */
export const WorldBlockEntities = z.object({
  dim: DimId.nullish(),
  columns: z.array(WorldBlockEntityColumn),
});
export type WorldBlockEntities = z.infer<typeof WorldBlockEntities>;

/**
 * GET /api/world/blocks?since=rev[&dim] (the bot's BlockDeltaLog) and the `blocks` SSE payload. Pass `rev` back as `since`.
 * `blocks` is grouped per dimension (key = the dim id as a string, "0" / "-1"), in rev order; a later entry for the same cell
 * wins. `more: true` = the page was capped, ask again at once. `full: true` (lists empty) = the cursor is unusable (since=0, newer
 * than the log after a bot restart, older than the ring, or after an idle gap): fall back to the hash-diff refetch
 * (/api/world/index + /api/world/chunks). The bot only records while someone read in the last 2 min, so a first call is `full`.
 */
export const WorldBlockDeltas = z.object({
  at: UnixMs,
  rev: z.number().int(),
  full: z.boolean(),
  more: z.boolean(),
  blocks: z.record(z.string(), z.array(WorldBlockChange)),
  columns: z.array(WorldColumnChange),
});
export type WorldBlockDeltas = z.infer<typeof WorldBlockDeltas>;

/** GET /api/world/overlays?dim. */
export const WorldOverlays = z.object({
  at: IsoDateTime,
  dim: DimId,
  claims: z.array(WorldMark),
  places: z.array(WorldMark),
  points: z.array(WorldMark),
  plans: z.array(WorldPlan),
  dangers: z.array(WorldMark),
  notes: z.array(z.string()),
});
export type WorldOverlays = z.infer<typeof WorldOverlays>;

/** GET /api/bots/:bot/tile.png — the server-rendered per-bot view (supervisor /view/{bot}). */
export const TileQuery = z.object({
  mode: z.enum(['sheet', 'brain', 'eye', 'iso', 'shoulder', 'orbit', 'high', 'top', 'side', 'layers']).default('iso'),
  radius: QInt.min(3).max(12).optional(),
  xray: z.enum(['on', 'off', 'auto']).optional(),
});
export type TileQuery = z.infer<typeof TileQuery>;

// ---- live designs (supervisor blueprints) ----

export const BlueprintSummary = z.object({
  id: z.number().int(),
  name: z.string(),
  status: z.string(),
  summary: z.string().nullish(),
  requester: z.string().nullish(),
  rev: z.number().int().nullish(),
  costUsd: z.number().nullish(),
  picture: z.union([z.string(), z.boolean()]).nullish(),
  updated: z.union([IsoDateTime, z.number()]).nullish(),
});
export type BlueprintSummary = z.infer<typeof BlueprintSummary>;

/** GET /api/blueprints. */
export const BlueprintsResponse = z.object({ designs: z.array(BlueprintSummary) });
export type BlueprintsResponse = z.infer<typeof BlueprintsResponse>;

export const BlueprintParams = z.object({ id: QInt.min(1) });

/** GET /api/blueprints/:id — the design bill/eval JSON (BlueprintService.BillJson); shape owned by the bot. */
export const BlueprintBill = z.looseObject({});
/** GET /api/buildplans/:id — build plan parts as JSON; shape owned by the bot. */
export const BuildPlanData = z.looseObject({});
