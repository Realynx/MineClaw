/**
 * Story Mode (docs/web/story-mode.md): colonies of script-only pawns run by the bot's colony director, played from the
 * browser. Supervisor routes `/api/story*` (snake_case), passed through by the bridge (`/v1/sup/story*`), public camelCase.
 *
 * Response objects are lenient (the director may add fields; unknown enum values arrive as plain strings). Request bodies are
 * strict. Writes need the `story` role (or `admin`, see `hasRole`).
 *
 * Deltas: `GET /api/story/colonies/{id}?since=rev` returns only what changed after `rev` (items keyed by pawn name, zone /
 * designation / order / build id, storage chest position) plus `removed` keys; without `since`, or with `full: true`, the
 * reply is the whole state and replaces what the client holds. `categories`, when present, always replaces. A delta's
 * `ghosts.blocks` holds only the ghosts that changed (the bot's shape): the client merges them by build and cell, and a removed
 * build takes its ghosts with it.
 */
import { z } from 'zod';
import { IsoDateTime, Vec3, Vec3i } from './common.js';
import {
  StoryEnchantedStack,
  StoryEquipment,
  StoryGearTask,
  StoryMaterialInfo,
  StoryMaterialRule,
  StoryPolicy,
  StorySpareTools,
  StoryToolKind,
} from './storyGear.js';

// ------------------------------------------------------------------ vocabulary

/** Work types in their fixed column order (ties in priority go to the earlier column). */
export const STORY_WORK_TYPES = [
  'guard',
  'doctor',
  'cook',
  'hunt',
  'fish',
  'build',
  'farm',
  'mine',
  'chop',
  'craft',
  'haul',
] as const;
export const StoryWorkType = z.enum(STORY_WORK_TYPES);
export type StoryWorkType = z.infer<typeof StoryWorkType>;

/** One schedule hour. A schedule is indexed by the clock hour (0 = 00:00, the bot's StoryCharacter.Hour): world time 0 is 06:00, 1000 ticks an hour. */
export const STORY_SCHEDULE_BLOCKS = ['work', 'sleep', 'anything', 'free'] as const;
export const StoryScheduleBlock = z.enum(STORY_SCHEDULE_BLOCKS);
export type StoryScheduleBlock = z.infer<typeof StoryScheduleBlock>;

export const STORY_PAWN_STATES = [
  'idle',
  'working',
  'moving',
  'eating',
  'sleeping',
  'fleeing',
  'fighting',
  'drafted',
  'offline',
] as const;
export const STORY_ORDER_KINDS = ['move', 'attack', 'chop', 'mine', 'haul', 'build', 'hold', 'stop'] as const;
export const StoryOrderKind = z.enum(STORY_ORDER_KINDS);
export type StoryOrderKind = z.infer<typeof StoryOrderKind>;

/**
 * Zone kinds. Loot goes into chests with storage settings (`storage`): the director refuses a `stockpile` zone. `preserve` is an
 * overlay layer whose blocks pawns keep as they are (docs/web/story-mode.md "Preserve zones").
 */
export const STORY_ZONE_KINDS = ['chop', 'mine', 'farm', 'fish', 'home',
  'guard',
  'forbidden', 'dump', 'preserve',
] as const;
export const StoryZoneKind = z.enum(STORY_ZONE_KINDS);
export type StoryZoneKind = z.infer<typeof StoryZoneKind>;

export const STORY_DESIGNATION_KINDS = [
  'chop',
  'mine',
  'harvest',
  'haul',
  'build',
  'deconstruct',
  'cut',
  'hunt',
  'forbid',
  'allow',
] as const;
export const StoryDesignationKind = z.enum(STORY_DESIGNATION_KINDS);
export type StoryDesignationKind = z.infer<typeof StoryDesignationKind>;

/** Mine tool filter: every block, ores only, stone only, or dirt + gravel + sand. */
export const StoryMineFilter = z.enum(['all', 'ores', 'stone', 'soil']);
export type StoryMineFilter = z.infer<typeof StoryMineFilter>;

export const STORY_BUILD_KINDS = [
  'wall',
  'floor',
  'roof',
  'door',
  'window',
  'stairs',
  'fence',
  'block',
  'remove',
  'mine',
  'water',
  'crafting_table',
  'furnace',
  'chest',
  'bed',
  'torch',
] as const;
export const StoryBuildKind = z.enum(STORY_BUILD_KINDS);
export type StoryBuildKind = z.infer<typeof StoryBuildKind>;

export const StoryRoofStyle = z.enum(['flat', 'slab', 'gable', 'none']);
export type StoryRoofStyle = z.infer<typeof StoryRoofStyle>;

export const StoryFacing = z.enum(['n', 'e', 's', 'w']);
export type StoryFacing = z.infer<typeof StoryFacing>;

export const STORY_STORAGE_PRIORITIES = ['low', 'normal', 'preferred', 'important', 'critical'] as const;
export const StoryStoragePriority = z.enum(STORY_STORAGE_PRIORITIES);
export type StoryStoragePriority = z.infer<typeof StoryStoragePriority>;

/** An axis-aligned box of blocks [x1, y1, z1, x2, y2, z2] (corners inclusive, in any order). */
export const StoryArea = z.tuple([
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
]);
export type StoryArea = z.infer<typeof StoryArea>;

/** An item stack or block kind [id, meta] (meta -1 = any). */
export const StoryItemKey = z.tuple([z.number().int(), z.number().int()]);
export type StoryItemKey = z.infer<typeof StoryItemKey>;

/** [id, meta, count]. */
export const StoryStack = z.tuple([z.number().int(), z.number().int(), z.number().int()]);
export type StoryStack = z.infer<typeof StoryStack>;

/** A block material: a block name ("oak_planks", "cobblestone") or [id, meta]. Any block is allowed. */
export const StoryMaterial = z.union([z.string().min(1).max(64), StoryItemKey]);
export type StoryMaterial = z.infer<typeof StoryMaterial>;

const Num = z.number();
const When = z.union([IsoDateTime, z.number()]);

// ------------------------------------------------------------------ colony list / create

export const StoryColonySummary = z.object({
  id: z.string(),
  name: z.string(),
  created: When.nullish(),
  paused: z.boolean().nullish(),
  pawns: z.array(z.string()).default([]),
  active: z.boolean().nullish().describe('false once released (the saved state stays; resume spawns the pawns again)'),
  version: z.union([z.string(), z.number()]).nullish().describe('state revision (or the director version)'),
});
export type StoryColonySummary = z.infer<typeof StoryColonySummary>;

/** Whether the world's time of day can be set from Story Mode: it needs an op account (`by`) the bot runs it as. */
export const StoryTimeRange = z.object({ start: z.number().int().min(0).max(23999), end: z.number().int().min(0).max(23999) })
  .refine((r) => r.start !== r.end, 'Start and end must be different');
export type StoryTimeRange = z.infer<typeof StoryTimeRange>;
export const StoryTimeRangeRequest = z.object({ range: StoryTimeRange.nullable() }).strict();
export const StoryTimeControl = z.object({ available: z.boolean(), by: z.string().nullish(), range: StoryTimeRange.nullish() });
export type StoryTimeControl = z.infer<typeof StoryTimeControl>;

/** POST /api/story/time: sets the world's time of day (ticks, 0 = 06:00). */
export const StoryTimeRequest = z.object({ time: z.number().int().min(0).max(23999) }).strict();
export type StoryTimeRequest = z.infer<typeof StoryTimeRequest>;

export const StoryTimeResponse = z.object({ time: z.number().int(), by: z.string().nullish() });
export type StoryTimeResponse = z.infer<typeof StoryTimeResponse>;

/** GET /api/story (the AI bodies and their directives come along for the bar). */
export const StoryListResponse = z.object({
  timeControl: StoryTimeControl.nullish(),
  colonies: z.array(StoryColonySummary).default([]),
  ai: z.array(z.lazy(() => StoryAiBody)).nullish(),
  directives: z.array(z.lazy(() => StoryDirective)).nullish(),
});
export type StoryListResponse = z.infer<typeof StoryListResponse>;

/** POST /api/story/colonies: spawns `pawns` script-only bodies running `story_pawn`. */
export const StoryCreateRequest = z
  .object({
    name: z.string().trim().min(1).max(40),
    pawns: z.number().int().min(1), // no upper limit: the rest wait for a seat
    home: z.object({ x: z.number().int(), y: z.number().int(), z: z.number().int() }).strict().optional(),
  })
  .strict();
export type StoryCreateRequest = z.infer<typeof StoryCreateRequest>;

export const StoryCreateResponse = z.object({ id: z.string() });
export type StoryCreateResponse = z.infer<typeof StoryCreateResponse>;

export const StoryColonyParams = z.object({ id: z.string().regex(/^[A-Za-z0-9_.-]{1,64}$/) });
export type StoryColonyParams = z.infer<typeof StoryColonyParams>;

export const StoryStateQuery = z.object({ since: z.coerce.number().int().min(0).optional() });
export type StoryStateQuery = z.infer<typeof StoryStateQuery>;

/** What a write answers (the director's new revision when it knows it). */
export const StoryAck = z.object({
  ok: z.boolean().nullish(),
  rev: z.number().int().nullish(),
  message: z.string().nullish(),
  id: z.union([z.string(), z.number()]).nullish().describe('release / resume: the colony; zones add: the zone'),
  ids: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('orders: the new order ids'),
  released: z.boolean().nullish(),
  paused: z.boolean().nullish(),
  pawns: z
    .union([z.number(), z.array(z.string())])
    .nullish()
    .describe('resume: the pawns spawned again'),
  note: z.string().nullish(),
  name: z.string().nullish().describe('zones rename: the new name'),
  removed: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('zones add/remove: the zones that went (= deleted)'),
  created: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('zones add: the new zone (the painted cells touched none of the type)'),
  merged: z
    .array(z.object({ into: z.union([z.string(), z.number()]), from: z.array(z.union([z.string(), z.number()])) }))
    .nullish()
    .describe('zones add: zones of the type the paint joined into the oldest (or the `into` zone)'),
  grown: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('zones add: the zone that took the cells'),
  shrunk: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('zones add/remove: zones that lost cells (overwritten or erased)'),
  split: z
    .array(z.object({ from: z.union([z.string(), z.number()]), into: z.array(z.union([z.string(), z.number()])) }))
    .nullish()
    .describe('zones add/remove: a zone cut in parts (it keeps the largest; the others are new zones)'),
  deleted: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('zones add/remove: zones left empty'),
  bill: z.union([z.string(), z.number()]).nullish().describe('zones bill_add: the new planting bill'),
  activeBill: z
    .union([z.string(), z.number()])
    .nullish()
    .describe('zones bill_*: the bill that decides what is sown now'),
  warnings: z
    .array(z.string())
    .nullish()
    .describe('zones add/update (growing zones): what slows it, e.g. no water near'),
  count: z.number().int().nullish().describe('zones seek_seeds: the tall grass designated (or seeds billed)'),
  kind: z.string().nullish().describe('zones seek_seeds: cut | bill'),
  cells: z
    .array(Vec3i)
    .nullish()
    .describe('zones irrigation: the water cells a growing zone wants (soil level; nothing queued)'),
  dig: z.array(Vec3i).nullish().describe('zones irrigation: the cells among them that hold soil (dug first, one group)'),
  axis: z.string().nullish().describe('zones irrigation: x | z, the way the channels run'),
  lines: z.number().int().nullish().describe('zones irrigation: how many channels'),
  preserve: z
    .lazy(() => StoryZonePreserve)
    .nullish()
    .describe("zones preserve: the zone's Preserve now (null: switched off)"),
});
export type StoryAck = z.infer<typeof StoryAck>;

// ------------------------------------------------------------------ state

/** An order target: a block, an entity, or an area. */
export const StoryTargetOut = z.object({
  x: Num.nullish(),
  y: Num.nullish(),
  z: Num.nullish(),
  entity: z.number().int().nullish(),
  area: StoryArea.nullish(),
  player: z.string().nullish().describe('an attack on a player or bot by name'),
});
export type StoryTargetOut = z.infer<typeof StoryTargetOut>;

/** A target as the director may write it: `{x,y,z}` / `{entity}` / `{area}`, `[x,y,z]`, or `"x,y,z"`. */
export const StoryTargetAny = z.union([StoryTargetOut, Vec3, z.string()]);
export type StoryTargetAny = z.infer<typeof StoryTargetAny>;

/** The block position of a target, whatever its form (null for an entity without a known position). */
export function targetPos(t: StoryTargetAny | null | undefined): [number, number, number] | null {
  if (t === null || t === undefined) return null;
  if (typeof t === 'string') return parsedPos(t);
  if (Array.isArray(t)) return [t[0], t[1], t[2]];
  return objectPos(t);
}

/** `"x,y,z"` → its position (null unless three finite numbers). */
function parsedPos(t: string): [number, number, number] | null {
  const p = t.split(',').map(Number);
  return p.length === 3 && p.every(Number.isFinite) ? [p[0]!, p[1]!, p[2]!] : null;
}

/** `{x,y,z}` (y defaults to 0) or an area's centre on its floor; null for an entity. */
function objectPos(t: StoryTargetOut): [number, number, number] | null {
  if (t.x !== null && t.x !== undefined && t.z !== null && t.z !== undefined) return [t.x, t.y ?? 0, t.z];
  if (t.area) return [(t.area[0] + t.area[3]) / 2, t.area[1], (t.area[2] + t.area[5]) / 2];
  return null;
}

export const StoryOrder = z.object({
  id: z.union([z.string(), z.number()]).nullish(),
  kind: z.string(),
  target: StoryTargetAny.nullish(),
  design: z.union([z.string(), z.number()]).nullish(),
  pawn: z.string().nullish(),
  pawns: z.array(z.string()).nullish(),
  state: z.string().nullish().describe('queued | active | done | failed'),
  at: When.nullish(),
  given: When.nullish(),
  left: z.number().nullish(),
});
export type StoryOrder = z.infer<typeof StoryOrder>;

export const StoryJob = z.object({
  type: z.string().describe('a work type, an order kind, or eat / sleep / flee / idle'),
  label: z.string(),
  target: StoryTargetAny.nullish(),
  progress: Num.nullish().describe('0..1'),
  since: When.nullish(),
});
export type StoryJob = z.infer<typeof StoryJob>;

export const StorySkill = z.object({
  level: Num,
  xp: Num.nullish(),
  passion: z
    .union([z.boolean(), z.number()])
    .nullish()
    .describe('true (or 1/2: minor/major) when the pawn loves this work'),
});
export type StorySkill = z.infer<typeof StorySkill>;

export const StoryTrait = z.object({ id: z.string(), label: z.string(), effect: z.string().nullish() });
export type StoryTrait = z.infer<typeof StoryTrait>;

/**
 * A work role: a named priority template (docs/web/story-mode.md "Roles"). A pawn with a role works by the role's priorities;
 * editing the role changes every pawn that has it.
 */
export const StoryRole = z.object({
  id: z.string(),
  name: z.string(),
  icon: z.string().nullish().describe('an icon key (wheat, pickaxe, axe, brick, package, hammer, pot, fish, shield, shapes, …)'),
  builtin: z.boolean().default(false).describe('one of the shipped roles (reset_defaults restores it)'),
  priorities: z.record(z.string(), z.number().int()).default({}).describe('work type → 0 (off) .. 4'),
  pawns: z.array(z.string()).default([]).describe('the pawns that have this role'),
});
export type StoryRole = z.infer<typeof StoryRole>;

export const StoryPawn = z.object({
  name: z.string().describe('the body (account) name'),
  display: z.string(),
  kind: z.string().default('pawn'),
  online: z.boolean().nullish(),
  hp: Num.nullish(),
  food: Num.nullish(),
  pos: Vec3.nullish(),
  dim: z.number().int().nullish(),
  drafted: z.boolean().nullish(),
  state: z.string().nullish().describe(STORY_PAWN_STATES.join(' | ')),
  leaving: z.boolean().nullish().describe('removed from the colony: storing its things, then it logs out'),
  idleReason: z.string().nullish().describe('idle in its work hours while there is work of its kinds: why (the idle audit)'),
  stuck: z
    .object({ since: z.string().nullish(), at: Vec3i.nullish(), reason: z.string().nullish(), step: z.string().nullish(), attempts: z.number().int().nullish() })
    .nullish()
    .describe('while the colony takes it for stuck (a pit, a ravine, a cave, out-of-reach targets): since when, where, why, what it tries now'),
  offlineReason: z
    .string()
    .nullish()
    .describe(
      'why an offline pawn is not in game (null while online): released, connecting, not whitelisted — re-adding, server full (…)',
    ),
  job: StoryJob.nullish(),
  queue: z.array(StoryOrder).default([]),
  skills: z.record(z.string(), StorySkill).default({}),
  traits: z.array(StoryTrait).default([]),
  priorities: z
    .record(z.string(), z.number().int())
    .default({})
    .describe("work type → 0 (off) .. 4: the effective values (its role's while it has one)"),
  role: z.string().nullish().describe("its work role's id (null = Custom: its own priorities)"),
  schedule: z.array(z.string()).default([]).describe('24 blocks by clock hour (index 0 = 00:00)'),
  held: z.string().nullish(),
  inventory: z.array(StoryStack).default([]).describe("a 1.12.2 potion's meta is its potion type id (water 1, awkward 4, …)"),
  xpLevel: z
    .number()
    .int()
    .nullish()
    .describe('the experience level (earned mining ore, smelting, killing); enchanting and anvil bills spend it'),
  equipment: StoryEquipment.nullish().describe('held item, worn armour, carried tools and weapons (durability, enchantments)'),
  policy: z.string().nullish().describe("the pawn's gear policy id (null = the colony's default policy)"),
  gearTasks: z.array(StoryGearTask).nullish().describe('pending gear work (arm, armor, pickup, return, tools, …)'),
  speed: z
    .lazy(() => StoryPawnSpeed)
    .nullish()
    .describe("the body's movement boost as it runs (null while offline)"),
});
export type StoryPawn = z.infer<typeof StoryPawn>;

/** A planting bill of a growing zone: what to sow and for how long (docs/web/story-mode.md "Farming"). */
export const STORY_GROW_BILL_MODES = ['forever', 'until', 'count'] as const;
export const StoryGrowBill = z.object({
  id: z.union([z.string(), z.number()]),
  plant: z.string(),
  label: z.string().nullish(),
  mode: z
    .enum(STORY_GROW_BILL_MODES)
    .describe('forever | until (target of the produce in storage) | count (harvest target)'),
  target: z.number().int().default(64),
  done: z.number().int().default(0).describe('count: harvested so far'),
  stored: z.number().int().nullish().describe('until: the produce the colony has'),
  paused: z.boolean().default(false),
  met: z.boolean().nullish(),
  active: z.boolean().nullish().describe('the first unpaused bill whose target is not met: what is sown now'),
  seeds: z.lazy(() => StoryZoneSeeds).nullish(),
  missing: z.string().nullish().describe('the plant does not grow in this version'),
});
export type StoryGrowBill = z.infer<typeof StoryGrowBill>;

/** A quarry's progress and its stair (docs/web/story-mode.md "Quarries"). */
export const StoryQuarry = z.object({
  top: z.number().int().describe('the y digging starts at (the first step)'),
  bottom: z.number().int().describe('the lowest y it digs (over the bedrock)'),
  depth: z.number().int().describe('layers from top to bottom'),
  layer: z.number().int().describe('the layer being dug (0 = the top)'),
  y: z.number().int(),
  cellsLeft: z.number().int().default(0),
  stairFixes: z.number().int().default(0).describe('steps waiting for a block (air, liquid, sand or gravel)'),
  sealed: z.number().int().default(0).describe('cells left standing beside lava or water'),
  done: z.boolean().default(false),
  stair: z.array(z.tuple([z.number().int(), z.number().int(), z.number().int()])).default([]).describe('the steps, top first'),
  status: z.string().nullish(),
});
export type StoryQuarry = z.infer<typeof StoryQuarry>;

/** A zone row run: `[z, x1, x2]`. */
export const StoryZoneRow = z.tuple([z.number().int(), z.number().int(), z.number().int()]);

export const StoryZone = z.object({
  id: z.union([z.string(), z.number()]),
  kind: z.string(),
  /** The cells' bounding box (x, z) and the zone's level range (y). */
  area: StoryArea,
  /** The zone's cells as normalised row runs (null = the whole `area`: an older bot). */
  rows: z.array(StoryZoneRow).nullish(),
  cells: z.number().int().nullish().describe('how many cells (columns) the zone has'),
  /** The zone's name (the bot's field; houses are named home zones). */
  name: z.string().nullish(),
  label: z.string().nullish(),
  /** A growing zone (kind `farm`): its type's plant, the first bill's (a `plants` id; null = ripe crops and bare farmland only). */
  plant: z.string().nullish(),
  activePlant: z
    .string()
    .nullish()
    .describe("a growing zone: what it sows now (the active bill's plant; null = all bills met)"),
  bills: z.array(StoryGrowBill).nullish().describe('a growing zone: its planting bills, in order'),
  activeBill: z.union([z.string(), z.number()]).nullish(),
  waterCells: z.number().int().nullish().describe('a fishing zone: its still-water cells'),
  fishers: z.array(z.string()).nullish().describe('a fishing zone: who is fishing there now'),
  catches: z.number().int().nullish().describe('a fishing zone: fish caught there so far'),
  mode: z.enum(['ores', 'quarry']).nullish().describe('a mine zone: quarry (everything to bedrock, a stair) or ores only'),
  quarry: z.lazy(() => StoryQuarry).nullish(),
  sow: z.boolean().nullish().describe('pawns till and sow it'),
  harvest: z.boolean().nullish().describe('pawns harvest what is ripe'),
  status: z.string().nullish().describe('a growing zone: "12/40 sown, 6 ripe"'),
  seeds: z.lazy(() => StoryZoneSeeds).nullish(),
  needsHoe: z.boolean().nullish().describe('there is dirt to till and no hoe anywhere'),
  preserve: z
    .lazy(() => StoryZonePreserve)
    .nullish()
    .describe('its blocks kept as they are (null = off; always on for a zone of kind preserve)'),
});
export type StoryZone = z.infer<typeof StoryZone>;

/** The most a Preserve setting reaches under or over the zone's painted level. */
export const STORY_PRESERVE_MAX_RANGE = 32;
/** The most blocks (columns × layers) one zone keeps. */
export const STORY_PRESERVE_MAX_CELLS = 262_144;

/**
 * A zone's Preserve (docs/web/story-mode.md "Preserve zones"): a snapshot of every block from `below` under its painted level to
 * `above` over it (`y1`..`y2`, absolute). The cells that differ now are the ghosts of the zone's repair build (`build`, a build of
 * kind `preserve`): pawns put them back as the snapshot has them.
 */
export const StoryZonePreserve = z.object({
  on: z.boolean().default(true),
  below: z.number().int().default(1).describe('layers kept under the painted level'),
  above: z.number().int().default(3).describe('layers kept over the painted level'),
  y1: z.number().int().describe('the lowest y kept'),
  y2: z.number().int().describe('the highest y kept'),
  cells: z.number().int().default(0).describe('blocks kept (columns × layers)'),
  takenAt: When.nullish().describe('when the snapshot was taken'),
  build: z.union([z.string(), z.number()]).nullish().describe("the zone's repair build"),
  unknown: z.number().int().default(0).describe('blocks not seen yet (their chunk is not loaded)'),
  held: z.number().int().default(0).describe("cells an owner's build, designation or quarry holds now"),
  damage: z
    .object({
      missing: z.number().int().default(0),
      foreign: z.number().int().default(0),
      turned: z.number().int().default(0),
    })
    .default({ missing: 0, foreign: 0, turned: 0 }),
  toRepair: z.number().int().default(0).describe('blocks that differ from the snapshot now'),
  status: z.string().nullish().describe('"intact", "12 blocks to repair: 9 missing, 3 foreign; waiting for 6 dirt"'),
});
export type StoryZonePreserve = z.infer<typeof StoryZonePreserve>;

/** A growing zone's seeds: what is sown, how many the colony has, how many empty cells wait, and where they come from. */
export const StoryZoneSeeds = z.object({
  item: z.string(),
  have: z.number().int().default(0),
  neededForEmpty: z.number().int().default(0),
  sourceHint: z.string().nullish(),
  missing: z.string().nullish().describe('null = there are seeds (or nothing to sow)'),
  action: z.enum(['seek_seeds', 'craft_seeds']).nullish().describe('what the zone can do about it (explicit)'),
});
export type StoryZoneSeeds = z.infer<typeof StoryZoneSeeds>;

/** What a growing zone can grow in this version (`plants` in the full state). */
export const StoryPlant = z.object({
  id: z.string(),
  label: z.string(),
  block: z.number().int().nullish(),
  seed: z.string().describe('the item that is sown'),
  seedItem: z.tuple([z.number().int(), z.number().int()]).nullish().describe('[id, meta]'),
  soil: z.string().describe('farmland | sand | soul_sand | dirt | cane'),
  harvest: z.string().describe('crop | fruit | stalk | tree'),
  source: z.string().nullish(),
});
export type StoryPlant = z.infer<typeof StoryPlant>;

/**
 * One designated target. The director writes `target` ([x,y,z] or {entity}) and `blocks` (a tree's logs); the mocks also send
 * `block` / `targets` / `entity`. Read it with `designationCells` / `designationEntity`.
 */
export const StoryDesignation = z.object({
  id: z.union([z.string(), z.number()]),
  kind: z.string(),
  workType: z.string().nullish(),
  label: z.string().nullish(),
  created: When.nullish(),
  target: z.union([Vec3i, z.object({ entity: z.number().int() })]).nullish(),
  blocks: z.array(Vec3i).nullish(),
  block: Vec3i.nullish(),
  area: StoryArea.nullish(),
  entity: z.number().int().nullish(),
  targets: z.array(Vec3i).nullish().describe('the matching blocks an area expanded to'),
  design: z.union([z.string(), z.number()]).nullish(),
  pawn: z.string().nullish().describe('the pawn holding the lease'),
  state: z.string().nullish(),
  status: z.string().nullish().describe('open | failed (3 failures; still retried after its rest)'),
  reason: z.string().nullish().describe('why the last try failed'),
  fails: z.number().int().nullish(),
  retryAt: When.nullish().describe('when a resting (failed) designation is tried again; null when it is open'),
  wait: z
    .string()
    .nullish()
    .describe('why it waits without failing ("needs a stone pickaxe"): no pawn has, or can fetch, the right tool yet'),
});
export type StoryDesignation = z.infer<typeof StoryDesignation>;

/** The blocks a designation covers (a tree: every log, the base first). */
export function designationCells(d: StoryDesignation): Array<[number, number, number]> {
  const list = d.blocks?.length ? d.blocks : d.targets?.length ? d.targets : null;
  if (list) return list.map((b) => [b[0], b[1], b[2]]);
  const t = Array.isArray(d.target) ? d.target : d.block;
  return t ? [[t[0], t[1], t[2]]] : [];
}

/** The entity a designation is about (haul, hunt, forbid), if any. */
export function designationEntity(d: StoryDesignation): number | null {
  if (d.entity !== null && d.entity !== undefined) return d.entity;
  return d.target && !Array.isArray(d.target) ? d.target.entity : null;
}

export const StoryAlert = z.object({
  id: z.union([z.string(), z.number()]).nullish(),
  at: When,
  level: z.string().describe('info | warn | danger'),
  text: z.string(),
  pawn: z.string().nullish(),
  fix: z
    .object({
      tab: z.string().nullish(),
      chest: Vec3i.nullish(),
      workbench: Vec3i.nullish(),
      item: StoryItemKey.nullish(),
      pawn: z.string().nullish(),
    })
    .nullish()
    .describe('where the web sends the user to fix it'),
});
export type StoryAlert = z.infer<typeof StoryAlert>;

/**
 * One item a build still needs (the director's materials pass, in queue order): `have` = what the colony has for it (reserved
 * for this build), `crafting` = what its auto bills are making, `short` = what can't be made from what there is (`raw` says
 * which raw materials are missing). `via`: craft | smelt (the chain has a furnace step) | none (no recipe, or its crafting was
 * cancelled). `chain` = the recipe steps, ingredients first.
 */
export const StoryBuildMaterial = z.object({
  item: z.string(),
  label: z.string().nullish(),
  need: z.number().int(),
  have: z.number().int().default(0),
  crafting: z.number().int().default(0),
  short: z.number().int().default(0),
  via: z.string().default('none').describe('craft | smelt | none'),
  chain: z.array(z.string()).default([]),
  raw: z.array(z.string()).default([]).describe('raw materials short, e.g. "6 Wood"'),
});
export type StoryBuildMaterial = z.infer<typeof StoryBuildMaterial>;

export const StoryBuild = z.object({
  id: z.union([z.string(), z.number()]),
  kind: z.string(),
  label: z.string(),
  status: z.string().describe('queued | active | waiting (its materials are being crafted) | paused | blocked | done'),
  group: z.string().nullish().describe('items belonging to one placed building'),
  name: z.string().nullish().describe('name of the placed building'),
  reason: z.string().nullish(),
  blocksTotal: z.number().int().default(0),
  blocksDone: z.number().int().default(0),
  material: StoryMaterial.nullish(),
  order: z.number().int().default(0),
  created: When.nullish(),
  materials: z.array(StoryBuildMaterial).default([]),
  noCraft: z
    .array(z.string())
    .nullish()
    .describe('items whose auto-crafting the owner cancelled: the build waits for them'),
  station: z
    .object({ kind: z.string(), pos: Vec3i })
    .nullish()
    .describe('a bench the colony is making and placing for this build'),
  liquid: z.string().nullish().describe('a water build: water | lava (its cells are sources poured from a bucket)'),
  renewable: z.boolean().nullish().describe('a water build whose poured sources refill (an infinite pool): pawns fill there'),
});
export type StoryBuild = z.infer<typeof StoryBuild>;

/** Ghost block [x, y, z, id, meta, build_id, state, kind?] (state: todo | leased | done | blocked | unsupported: a block pawns
 * can't place yet, shown and alerted, never a job | misrotated: placed, but turned another way than the design (meta), shown over
 * it and alerted (an open cell the reconcile pass finds turned wrong is shown so until it is placed again) | clear: an open cell that
 * holds another block, dug out before its block goes in (the 9th element says what); kind: dig | place | pour — a dig ghost's id/meta
 * is the live world's block
 * there, the material the dig expects; a pour ghost is a liquid source, water 9 or lava 11, poured from a bucket). */
export const StoryGhostBlock = z.tuple([
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.union([z.string(), z.number()]),
  z.string(),
  z.enum(['dig', 'place', 'pour']).optional(),
  z.string().nullish().describe("a failed (or unsupported) cell's reason: the map's failed-cell marker"),
]);
export type StoryGhostBlock = z.infer<typeof StoryGhostBlock>;

export const StoryGhosts = z.object({ rev: z.number().int(), blocks: z.array(StoryGhostBlock).default([]) });
export type StoryGhosts = z.infer<typeof StoryGhosts>;

/** A loot category: a node of the storage filter tree (`children` are child ids or nested categories). */
export interface StoryCategory {
  id: string;
  label: string;
  children: Array<string | StoryCategory>;
  items: Array<[number, number]>;
}
export const StoryCategory: z.ZodType<StoryCategory> = z.lazy(() =>
  z.object({
    id: z.string(),
    label: z.string(),
    children: z.array(z.union([z.string(), StoryCategory])).default([]),
    items: z.array(StoryItemKey).default([]),
  }),
);

/** Allowed / denied entries: category ids or "id:meta" item keys ("17:-1" = every log). */
export const StoryFilter = z.object({
  allow: z.array(z.string().max(64)).max(512).default([]),
  deny: z.array(z.string().max(64)).max(512).default([]),
});
export type StoryFilter = z.infer<typeof StoryFilter>;

export const StoryStorageSign = z.object({
  at: Vec3i,
  lines: z.array(z.string()).max(4),
  allow: z.array(z.string()),
  deny: z.array(z.string()),
});
export type StoryStorageSign = z.infer<typeof StoryStorageSign>;

export const StoryStorage = z.object({
  chest: Vec3i,
  doubleWith: Vec3i.nullish().describe('the other half of a double chest'),
  label: z.string().nullish(),
  priority: z.string().default('normal'),
  filter: StoryFilter.default({ allow: [], deny: [] }),
  signs: z.array(StoryStorageSign).nullish().describe('recognized filter signs physically attached to either chest half; their combined filter takes precedence'),
  pendingFilter: StoryFilter.nullish().describe('requested viewer filter waiting for the controlling signs to be removed'),
  pendingPriority: StoryStoragePriority.nullish().describe('requested priority waiting for the controlling signs to be removed'),
  contents: z.array(StoryStack).default([]),
  freeSlots: z.number().int().nullish(),
  knownAt: When.nullish().describe('when a pawn last saw inside'),
  enchanted: z.array(StoryEnchantedStack).nullish().describe('enchanted tools known to be in the chest'),
});
export type StoryStorage = z.infer<typeof StoryStorage>;

export const STORY_WORKBENCH_KINDS = [
  'crafting_table',
  'furnace',
  'brewing_stand',
  'anvil',
  'enchanting_table',
] as const;
export const StoryWorkbenchKind = z.enum(STORY_WORKBENCH_KINDS);
export type StoryWorkbenchKind = z.infer<typeof StoryWorkbenchKind>;

export const StoryBillMode = z.enum(['count', 'until', 'forever']);
export type StoryBillMode = z.infer<typeof StoryBillMode>;

/** Where a bill's product goes: the best storage that accepts it, or one chest. */
export const StoryBillStore = z.union([z.literal('best'), z.object({ chest: Vec3i }).strict()]);
export type StoryBillStore = z.infer<typeof StoryBillStore>;

/** A furnace's fuel choices (only what the version burns is valid: b1.8.1 has no blaze rods). */
export const STORY_FUELS = [
  'auto',
  'coal',
  'charcoal',
  'coal_or_charcoal',
  'planks',
  'logs',
  'lava_bucket',
  'blaze_rod',
  'any',
] as const;
export const StoryFuelChoice = z.enum(STORY_FUELS);
export type StoryFuelChoice = z.infer<typeof StoryFuelChoice>;

/** One fuel of a recipe's fuel table: its item and how many items (a brew: brews) one unit smelts. */
export const StoryFuelEntry = z.object({ item: StoryItemKey, name: z.string(), itemsPerUnit: z.number() });
export type StoryFuelEntry = z.infer<typeof StoryFuelEntry>;

/** A bill's fuel for its next batch: the fuel it would burn, the units, the colony's stock of it, short when none covers it. */
export const StoryFuelNeed = z.object({
  choice: z.string(),
  item: StoryItemKey.nullish(),
  name: z.string().nullish(),
  need: z.number().int(),
  have: z.number().int(),
  short: z.boolean(),
  perItem: z.number().nullish().describe('fuel units per smelted item (coal 0.125, planks 0.667; blaze powder 0.05 per brew)'),
});
export type StoryFuelNeed = z.infer<typeof StoryFuelNeed>;

/** A build's claim on the colony's stock (docs/web/story-mode.md "Reservations"). */
export const StoryReservation = z.object({
  item: StoryItemKey,
  count: z.number().int(),
  build: z.string(),
  forItem: z.string(),
  chain: z.boolean().describe('an input of the chain that crafts the build item (else the build item itself)'),
});
export type StoryReservation = z.infer<typeof StoryReservation>;

export const StoryBill = z.object({
  id: z.union([z.string(), z.number()]),
  recipe: z.string(),
  mode: z.string().describe('count (do N times) | until (until you have N) | forever'),
  target: z.number().int().default(1),
  done: z.number().int().default(0),
  paused: z.boolean().default(false),
  suspendedReason: z.string().nullish(),
  ingredients: StoryFilter.default({ allow: [], deny: [] }),
  radius: z.number().int().default(32).describe('ingredient search radius, blocks'),
  pawn: z.string().default('any').describe('"any" or a pawn name'),
  minSkill: z.number().int().nullish(),
  store: z.union([z.literal('best'), z.object({ chest: Vec3i })]).default('best'),
  forBuild: z
    .union([z.string(), z.number()])
    .nullish()
    .describe('legacy: an auto bill (now a craft task, never in a bench\'s bills): the build it crafts for, or "tools"'),
  forItem: z.string().nullish().describe("legacy: an auto bill's item its chain ends in"),
  step: z.number().int().nullish().describe("legacy: an auto bill's place in the recipe chain (ingredients first)"),
  option: z.number().int().nullish().describe('an enchant bill: the highest offer (1–3) it takes; its lapis and levels'),
  prefer: z.string().nullish().describe('an enchant bill: the enchantment it prefers (the offer hinting it first)'),
  preferOnly: z.boolean().nullish().describe('only an offer hinting the preferred enchantment'),
  xpNeeded: z
    .number()
    .int()
    .nullish()
    .describe('the XP levels the last try needed (an offer, an anvil price): pawns with fewer leave it'),
  last: z.string().nullish().describe('what the last job made ("enchanted diamond pickaxe: Fortune II")'),
  fuel: z.string().nullish().describe("a furnace bill's fuel override (STORY_FUELS); null = the furnace's setting"),
  fuelNeed: StoryFuelNeed.nullish().describe("the fuel this bill's next batch burns: need / have / short"),
});
export type StoryBill = z.infer<typeof StoryBill>;

/**
 * One of the colony's own craft tasks (docs/web/story-mode.md "Craft tasks"): a build's chain step, the tools pawns need, a need's
 * one-off (hoe, rod, seeds). Never a bill in the bench's list; taken by pawns with the recipe's work type.
 */
export const StoryCraftTask = z.object({
  id: z.union([z.string(), z.number()]).describe('a bench task\'s id (cancel with bills {op:"remove", id}); "hand:…" for a 2×2 hand craft'),
  item: z.string().describe('the recipe id (craft:stick, smelt:iron_ore…)'),
  label: z.string().nullish(),
  count: z.number().int().describe('still to make'),
  reason: z.string().nullish().describe('why ("for mining: Cleo Z. (+1 spare)", "for the fence of build b4")'),
  for: z.union([z.string(), z.number()]).nullish().describe('a build id, "tools", or "need" (a zone\'s one-off)'),
  forItem: z.string().nullish(),
  priority: z.enum(['immediate', 'normal']).default('normal').describe('immediate: work waits on it now (crafted first)'),
  bench: Vec3i.nullish().describe('the bench it is made at; null = a hand craft (2×2 grid)'),
  status: z.enum(['queued', 'crafting', 'waiting', 'paused']).default('queued'),
  blocked: z.string().nullish().describe('what held it up at its last try'),
});
export type StoryCraftTask = z.infer<typeof StoryCraftTask>;

/** A station's facts for the bench inspector (1.12.2 brewing stand, enchanting table, anvil). */
export const StoryStationInfo = z.object({
  present: z.boolean().nullish().describe('false: the block is gone (a worn-through anvil)'),
  bookshelves: z.number().int().nullish().describe('an enchanting table: bookshelves powering it'),
  maxBookshelves: z.number().int().nullish(),
  topOffer: z.array(z.number().int()).nullish().describe("an enchanting table: the top offer's level range [min, max]"),
  condition: z.string().nullish().describe('an anvil: intact | chipped | damaged | gone'),
  water: Vec3i.nullish().describe('a brewing stand: the water source or cauldron its bottles are filled at'),
  fuel: z.number().int().nullish().describe('a brewing stand: brews of fuel left, as the colony last left it'),
});
export type StoryStationInfo = z.infer<typeof StoryStationInfo>;

export const StoryWorkbench = z.object({
  pos: Vec3i,
  kind: z.string(),
  label: z.string().nullish(),
  supported: z
    .boolean()
    .nullish()
    .describe('false: this version has no such bills (b1.8.1: no brewing stand, anvil or enchanting table)'),
  loaded: z.unknown().optional().describe('a furnace: what is smelting'),
  readyAt: When.nullish(),
  station: StoryStationInfo.nullish(),
  fuel: z.string().nullish().describe("a furnace's fuel choice (STORY_FUELS; default auto)"),
  fuelChoices: z.array(z.string()).nullish().describe('the fuel choices this version burns'),
  fuelCredit: z.number().nullish().describe('items the furnace can still smelt on the fuel the colony loaded'),
  smelting: z
    .object({
      recipe: z.string().nullish(),
      input: StoryStack.nullish(),
      output: StoryStack.nullish(),
      loadedAt: When.nullish(),
      readyAt: When.nullish(),
      estimated: z.boolean().nullish(),
    })
    .nullish()
    .describe('a loaded furnace as expected (an item every 10 s since loading): what is left to smelt and what is out'),
  bills: z.array(StoryBill).default([]),
});
export type StoryWorkbench = z.infer<typeof StoryWorkbench>;

export const StoryRecipeInput = z.object({
  options: z.array(StoryItemKey).nullish().describe('any of these items'),
  category: z.string().nullish().describe('or anything in this loot category'),
  count: z.number().int(),
});
export type StoryRecipeInput = z.infer<typeof StoryRecipeInput>;

export const StoryRecipe = z.object({
  id: z.string(),
  label: z.string(),
  workbench: z.string(),
  workType: z.string(),
  output: StoryStack,
  inputs: z.array(StoryRecipeInput).default([]),
  fuel: z.union([z.boolean(), z.number(), z.string()]).nullish(),
  group: z.string().nullish().describe('a loot category id for the picker (the web groups by it when present)'),
  any: z.boolean().nullish().describe('takes any variant of a material family (planks, logs, sand, coal): listed first, with an "any" badge'),
  family: z.string().nullish().describe('the family its any-variant input is: logs | planks | sand | coal'),
  anyOutput: z.boolean().nullish().describe("the output's variant follows the input (planks of the log's wood): its stock counts every variant"),
  requires: z
    .object({
      xpLevels: z.number().int().nullish().describe('the least XP levels a pawn needs'),
      lapis: z.number().int().nullish().describe('lapis per enchant (the bill option: 1–3)'),
      fuelItem: z.string().nullish(),
      water: z.boolean().nullish().describe('fills glass bottles at a water source or cauldron near the stand'),
      note: z.string().nullish(),
    })
    .nullish()
    .describe('a station recipe: what it asks of the pawn and the bench'),
  chain: z.array(z.string()).nullish().describe("a brew's steps from water bottles"),
  enchantments: z.array(z.string()).nullish().describe('an enchant: what it can prefer'),
  modes: z.array(StoryBillMode).nullish().describe('the bill modes it takes (null: all)'),
  fuels: z
    .array(StoryFuelEntry)
    .nullish()
    .describe('a furnace recipe: the fuels it can burn and how many items one smelts; a brew: blaze powder (brews per unit)'),
});
export type StoryRecipe = z.infer<typeof StoryRecipe>;

/** GET /api/story/recipes?workbench=<kind>. */
export const StoryRecipesQuery = z.object({ workbench: StoryWorkbenchKind.optional() });
export type StoryRecipesQuery = z.infer<typeof StoryRecipesQuery>;
export const StoryRecipesResponse = z.array(StoryRecipe);
export type StoryRecipesResponse = z.infer<typeof StoryRecipesResponse>;

/** An AI-driven body (main, a hive, a subhive body) shown beside the colony pawns; it takes directives, not director jobs. */
export const StoryAiBody = z.object({
  name: z.string(),
  kind: z.string().describe('main | hive | subhive'),
  owner: z.string().nullish(),
  online: z.boolean().nullish(),
  hp: Num.nullish(),
  food: Num.nullish(),
  pos: Vec3.nullish(),
  dim: z.number().int().nullish(),
  task: z.string().nullish(),
  brain: z.string().nullish(),
  brainState: z.string().nullish().describe('offline | script | working | idle'),
  script: z.string().nullish(),
});
export type StoryAiBody = z.infer<typeof StoryAiBody>;

export const STORY_DIRECTIVE_KINDS = [
  'chop',
  'cut',
  'harvest',
  'mine',
  'haul',
  'hunt',
  'deconstruct',
  'build',
  'move',
  'attack',
  'guard',
  'craft',
  'text',
] as const;
export const StoryDirectiveKind = z.enum(STORY_DIRECTIVE_KINDS);
export type StoryDirectiveKind = z.infer<typeof StoryDirectiveKind>;

/** A directive sent to AI bodies and how it went (sent → acknowledged → done | failed, or unknown). */
export const StoryDirective = z.object({
  id: z.union([z.string(), z.number()]),
  at: When,
  bots: z.array(z.string()).default([]),
  kind: z.string(),
  summary: z.string(),
  status: z.string().describe('sent | acknowledged | done | failed | unknown'),
  reply: z.string().nullish(),
  design: z.union([z.string(), z.number()]).nullish().describe('a build directive: the design saved for it'),
});
export type StoryDirective = z.infer<typeof StoryDirective>;

/** GET /api/story/directives. */
export const StoryDirectiveList = z.object({ directives: z.array(StoryDirective).default([]) });
export type StoryDirectiveList = z.infer<typeof StoryDirectiveList>;

export const StoryDirectiveParams = z.object({ id: z.string().regex(/^[A-Za-z0-9_.:-]{1,80}$/) });
export type StoryDirectiveParams = z.infer<typeof StoryDirectiveParams>;

/** GET /api/story/directives/{id}: the directive with the line it became, who got it, and the exact targets (`detail`). */
export const StoryDirectiveDetail = StoryDirective.extend({
  line: z.string().nullish(),
  delivered: z.array(z.string()).nullish(),
  detail: z.unknown().optional(),
});
export type StoryDirectiveDetail = z.infer<typeof StoryDirectiveDetail>;

export const StoryRemoved = z.object({
  pawns: z.array(z.string()).nullish(),
  zones: z.array(z.union([z.string(), z.number()])).nullish(),
  designations: z.array(z.union([z.string(), z.number()])).nullish(),
  orders: z.array(z.union([z.string(), z.number()])).nullish(),
  builds: z.array(z.union([z.string(), z.number()])).nullish(),
  storages: z
    .array(z.union([z.string(), Vec3i]))
    .nullish()
    .describe('chest keys "x,y,z"'),
  workbenches: z.array(z.union([z.string(), Vec3i])).nullish(),
  directives: z.array(z.union([z.string(), z.number()])).nullish(),
  alerts: z.array(z.union([z.string(), z.number()])).nullish(),
  ghosts: z
    .array(z.string())
    .nullish()
    .describe('"<build>|x,y,z": ghosts that left their build (a repaired cell of a preserved zone matches again)'),
});
export type StoryRemoved = z.infer<typeof StoryRemoved>;

/** Idle pawns with work about, grouped by why (the idle audit): "4 pawns idle: build b3: need 48 block of iron". */
export const StoryBlockedWork = z.object({
  reason: z.string(),
  count: z.number().int(),
  pawns: z.array(z.string()).default([]),
});
export type StoryBlockedWork = z.infer<typeof StoryBlockedWork>;

/** GET /api/story/colonies/{id}[?since=rev]. */
export const StoryColonyState = z.object({
  id: z.string().nullish(),
  timeControl: StoryTimeControl.nullish(),
  name: z.string().nullish(),
  rev: z.number().int(),
  at: When,
  full: z.boolean().nullish().describe('true = the whole state (replace); a reply without `since` is always whole'),
  since: z.number().int().nullish(),
  active: z.boolean().nullish(),
  tieBreak: z.string().nullish().describe('column | skill: equal priorities go by column order or by skill'),
  options: z.lazy(() => StoryColonyOptions).nullish(),
  architectKey: z
    .number()
    .int()
    .nullish()
    .describe('the Architect palette inputs (free stock, benches, version) as one number: refetch the palette when it moves'),
  version: z.union([z.string(), z.number()]).nullish(),
  timeOfDay: z.number().nullish().describe('world ticks 0..24000 (tick 0 = 06:00); negative = the daylight cycle is stopped'),
  timeFrozen: z.boolean().nullish().describe('true = the server stopped the daylight cycle (doDaylightCycle false)'),
  hour: z.number().int().nullish().describe('the clock hour 0..23 (0 = 00:00): the schedule column in force'),
  paused: z.boolean().nullish(),
  home: Vec3i.nullish(),
  workTypes: z.array(z.string()).default([...STORY_WORK_TYPES]),
  pawns: z.array(StoryPawn).default([]),
  seats: z.lazy(() => StorySeats).nullish(),
  unstoredKinds: z.array(z.string()).nullish().describe('item kinds no storage takes (a condition: "No storage for: …")'),
  blockedWork: z.array(StoryBlockedWork).nullish().describe('idle pawns with work about, by why, most first (a condition: "4 pawns idle: …")'),
  zones: z.array(StoryZone).default([]),
  plants: z.array(StoryPlant).nullish().describe('what growing zones can grow here (full state only)'),
  designations: z.array(StoryDesignation).default([]),
  orders: z.array(StoryOrder).default([]),
  alerts: z.array(StoryAlert).default([]),
  builds: z.array(StoryBuild).nullish(),
  ghosts: StoryGhosts.nullish(),
  categories: z.array(StoryCategory).nullish(),
  storages: z.array(StoryStorage).nullish(),
  workbenches: z.array(StoryWorkbench).nullish(),
  craftTasks: z
    .array(StoryCraftTask)
    .nullish()
    .describe("the colony's own craft tasks (never bills): immediate first; sent whole with every answer"),
  reservations: z
    .array(StoryReservation)
    .nullish()
    .describe("who holds what: a build's own item, or an input of the chain that crafts it (a bill making that chain's step may use it)"),
  beds: z
    .array(z.lazy(() => StoryBed))
    .nullish()
    .describe('every bed the colony knows, in full whenever it is sent'),
  ai: z.array(StoryAiBody).nullish(),
  directives: z.array(StoryDirective).nullish(),
  policies: z.array(StoryPolicy).nullish().describe('gear policies (always the default one)'),
  roles: z.array(StoryRole).nullish().describe('work roles: named priority templates (sent whole with every answer)'),
  materialRules: z.array(StoryMaterialRule).nullish().describe('per material / group: the tool, preferred tier, enchantment'),
  materials: z.array(StoryMaterialInfo).nullish().describe('full state only: the rules editor catalog with each floor'),
  removed: StoryRemoved.nullish(),
});
export type StoryColonyState = z.infer<typeof StoryColonyState>;

/** The SSE `story` frame: one colony's delta (the bridge adds the colony `id`). */
export const StoryColonyFrame = StoryColonyState.extend({ id: z.string() });
export type StoryColonyFrame = z.infer<typeof StoryColonyFrame>;

// ------------------------------------------------------------------ writes

const PawnList = z.array(z.string().min(1).max(64)).min(1); // any number of pawns (no upper limit)

/** The colony's seats: pawns in game, pawns wanted, the server's free seats and those kept for offline regulars (null = unknown). */
export const StorySeats = z.object({
  online: z.number().int().default(0),
  wanted: z.number().int().default(0),
  capFree: z.number().int().nullish(),
  keptForRegulars: z.number().int().nullish(),
  pending: z.number().int().nullish().describe('pawns added that are not here yet ("joining…")'),
  waiting: z.number().int().nullish().describe('wanted − online'),
  waitingReason: z
    .string()
    .nullish()
    .describe('the limit keeping waiting pawns out: "server full (20 slots, 2 kept for a and b)" or the memory ceiling; null = on their way'),
  keptFor: z.array(z.string()).nullish().describe('the offline regulars the kept seats are for'),
  slots: z.number().int().nullish().describe("the server's max players"),
  memoryRoom: z.number().int().nullish().describe('more bodies the memory ceiling takes (null = no ceiling)'),
});
export type StorySeats = z.infer<typeof StorySeats>;

/** POST /api/story/colonies/{id}/pawns: + one or more pawns (as seats allow; the rest join later), or − one pawn. */
export const StoryPawnsRequest = z.discriminatedUnion('op', [
  // No upper limit (Fox 11-05): the server's seats and the memory ceiling decide how many are in now; the rest wait.
  z.object({ op: z.literal('add'), count: z.number().int().min(1) }).strict(),
  z.object({ op: z.literal('remove'), pawn: z.string().min(1).max(64) }).strict(),
]);
export type StoryPawnsRequest = z.infer<typeof StoryPawnsRequest>;

export const StoryPawnsResponse = z.object({
  pawns: z.array(z.string()).default([]),
  wanted: z.number().int(),
  pending: z
    .array(z.object({ reason: z.string(), count: z.number().int().default(1) }))
    .default([])
    .describe('the pawns still to come: one entry per reason with a count (never one per pawn)'),
  leaving: z.array(z.string()).default([]),
  blocked: z.array(z.object({ reason: z.string() })).default([]),
});
export type StoryPawnsResponse = z.infer<typeof StoryPawnsResponse>;

/**
 * POST /api/story/colonies/{id}/online: bring offline pawns in now (`pawns: null` = every offline pawn). `overCap` is the
 * owner's explicit choice to use the seats kept for offline regulars: a regular may then find the server full until a pawn leaves.
 */
export const StoryOnlineRequest = z
  .object({
    pawns: PawnList.nullable(),
    overCap: z.boolean().optional(),
  })
  .strict();
export type StoryOnlineRequest = z.infer<typeof StoryOnlineRequest>;

export const StoryOnlineBlocked = z.object({ pawn: z.string(), reason: z.string() });
export type StoryOnlineBlocked = z.infer<typeof StoryOnlineBlocked>;

export const StoryOnlineResponse = z.object({
  started: z.array(z.string()).default([]),
  blocked: z.array(StoryOnlineBlocked).default([]),
});
export type StoryOnlineResponse = z.infer<typeof StoryOnlineResponse>;

/** Would the seats kept for offline regulars let it in? (`server full (player cap: N free, M kept for regulars)` with N ≥ 1, M ≥ 1.) */
export function regularsSeatsWouldHelp(reason: string | null | undefined): boolean {
  const m = /player cap: (\d+) free, (\d+) kept for regulars/.exec(reason ?? '');
  return !!m && Number(m[1]) > 0 && Number(m[2]) > 0;
}

export const StoryPauseRequest = z.object({ paused: z.boolean() }).strict();
export type StoryPauseRequest = z.infer<typeof StoryPauseRequest>;

/** A bed and who sleeps in it (`pos` is the bed's foot block; `zone` the home zone it stands in). */
export const StoryBed = z.object({
  pos: Vec3i,
  dim: z.number().int().nullish(),
  pawn: z.string().nullish(),
  zone: z.union([z.string(), z.number()]).nullish(),
});
export type StoryBed = z.infer<typeof StoryBed>;

const BedPos = z.object({ x: z.number().int(), y: z.number().int(), z: z.number().int() }).strict();

/** POST /api/story/colonies/{id}/beds: give a bed to a pawn (its other bed is freed), or free one. */
export const StoryBedsRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('assign'), bed: BedPos, pawn: z.string().min(1).max(64) }).strict(),
  z.object({ op: z.literal('unassign'), bed: BedPos }).strict(),
]);
export type StoryBedsRequest = z.infer<typeof StoryBedsRequest>;

export const StoryBedsResponse = z.object({ beds: z.array(StoryBed) });
export type StoryBedsResponse = z.infer<typeof StoryBedsResponse>;

/** POST /api/story/colonies/{id}/home: moves the colony's home; the director brings pawns far from it there. */
export const StoryHomeRequest = z
  .object({ home: z.object({ x: z.number().int(), y: z.number().int(), z: z.number().int() }).strict() })
  .strict();
export type StoryHomeRequest = z.infer<typeof StoryHomeRequest>;

export const StoryHomeResponse = z.object({ home: Vec3i });
export type StoryHomeResponse = z.infer<typeof StoryHomeResponse>;

/** `work`: type → 0 (off) .. 4; types left out keep their value. */
/** Set cells (a pawn with a role turns Custom, its other values kept), or give the pawns a role (null = Custom, values kept). */
export const StoryPrioritiesRequest = z.union([
  z.object({ pawns: PawnList, work: z.partialRecord(StoryWorkType, z.number().int().min(0).max(4)) }).strict(),
  z.object({ pawns: PawnList, role: z.string().min(1).max(64).nullable() }).strict(),
]);
export type StoryPrioritiesRequest = z.infer<typeof StoryPrioritiesRequest>;

const RolePriorities = z.partialRecord(StoryWorkType, z.number().int().min(0).max(4));
const RoleIcon = z
  .string()
  .max(24)
  .regex(/^[a-z0-9-]+$/);

/** POST /api/story/colonies/{id}/roles: add, update (priorities merge; its pawns follow), remove (its pawns turn Custom), reset_defaults. */
export const StoryRolesRequest = z.discriminatedUnion('op', [
  z
    .object({
      op: z.literal('add'),
      role: z.object({ name: z.string().min(1).max(40), icon: RoleIcon.nullish(), priorities: RolePriorities.optional() }).strict(),
    })
    .strict(),
  z
    .object({
      op: z.literal('update'),
      role: z
        .object({
          id: z.string().min(1).max(64),
          name: z.string().min(1).max(40).optional(),
          icon: RoleIcon.nullish(),
          priorities: RolePriorities.optional(),
        })
        .strict(),
    })
    .strict(),
  z.object({ op: z.literal('remove'), id: z.string().min(1).max(64) }).strict(),
  z.object({ op: z.literal('reset_defaults') }).strict(),
]);
export type StoryRolesRequest = z.infer<typeof StoryRolesRequest>;

export const StoryRolesResponse = z.object({
  id: z.string().nullish().describe('the role added, updated or removed'),
  removed: z.boolean().nullish(),
  roles: z.array(StoryRole).default([]),
});
export type StoryRolesResponse = z.infer<typeof StoryRolesResponse>;

export const STORY_HOURS = Array.from({ length: 24 }, (_, i) => String(i)) as [string, ...string[]];
export const StoryScheduleRequest = z
  .object({ pawns: PawnList, hours: z.partialRecord(z.enum(STORY_HOURS), StoryScheduleBlock) })
  .strict();
export type StoryScheduleRequest = z.infer<typeof StoryScheduleRequest>;

export const StoryDraftRequest = z.object({ pawns: PawnList, drafted: z.boolean() }).strict();
export type StoryDraftRequest = z.infer<typeof StoryDraftRequest>;

export const StoryTarget = z.union([
  z.object({ x: z.number().int(), y: z.number().int(), z: z.number().int() }).strict(),
  z.object({ entity: z.number().int() }).strict(),
  z.object({ area: StoryArea }).strict(),
  z
    .object({ player: z.string().min(1).max(64) })
    .strict()
    .describe('an attack on a player or bot by name (AI bodies have no entity id in the viewer)'),
]);
export type StoryTarget = z.infer<typeof StoryTarget>;

export const StoryOrderSpec = z
  .object({
    kind: StoryOrderKind,
    target: StoryTarget.optional(),
    design: z.union([z.string().max(64), z.number().int()]).optional(),
    confirm: z
      .boolean()
      .optional()
      .describe('attack: required (true) for a player or another bot, else 409; mobs and animals need none'),
  })
  .strict();
export type StoryOrderSpec = z.infer<typeof StoryOrderSpec>;

export const StoryOrderRequest = z
  .object({ pawns: PawnList, order: StoryOrderSpec, queue: z.boolean().optional() })
  .strict();
export type StoryOrderRequest = z.infer<typeof StoryOrderRequest>;

/** `remove` with an area erases those cells from zones of the kind (no kind or `any`: every kind). */
const ZoneId = z.union([z.string().min(1).max(64), z.number().int()]);
const BillSpec = {
  plant: z.string().min(1).max(32).optional(),
  mode: z.enum(STORY_GROW_BILL_MODES).optional(),
  target: z.number().int().min(1).max(100_000).optional(),
  paused: z.boolean().optional(),
};
/** A Preserve range: layers under (`below`) or over (`above`) the zone's painted level. */
const PreserveRange = z.number().int().min(0).max(STORY_PRESERVE_MAX_RANGE);

/**
 * Zones are cell sets painted RimWorld-style (docs/web/story-mode.md "Zones"; houses are zones of kind `home`): `add` paints an
 * area (cells touching a zone of the same type join it, several merge into the oldest; other work zones under it are overwritten;
 * home, guard, forbidden, dump and preserve are overlay layers; `into` extends one zone), `remove` erases an area's cells from zones of
 * a kind (or any; empty zones go, cut ones split) or deletes one zone by id, `rename` one zone (an empty name clears it). A
 * growing zone (kind `farm`) takes a `plant` (from `plants`: its first planting bill), `sow` and `harvest`; `update` changes them;
 * `bill_*` edit its planting bills (like a workbench's: add, update, remove, pause, resume, reorder); `seek_seeds` designates tall
 * grass to cut (wheat) or queues a seed bill (pumpkin, melon). Answers what changed: created, merged, grown, shrunk, split,
 * deleted. `preserve` keeps a zone's blocks as they are (docs/web/story-mode.md "Preserve zones"; any zone but a mine zone):
 * `on: false` stops it, `below` / `above` set how deep and how high, `retake` takes a new snapshot; a zone of kind `preserve` is
 * always on and takes its range when painted.
 */
export const StoryZoneRequest = z.union([
  z
    .object({
      op: z.literal('add'),
      kind: StoryZoneKind.optional(),
      area: StoryArea,
      into: ZoneId.optional().describe('extend this zone: the painted cells take its type'),
      mode: z.enum(['ores', 'quarry']).optional().describe('a mine zone: quarry (default) or ores only'),
      name: z.string().trim().max(40).optional(),
      plant: z.string().min(1).max(32).optional(),
      sow: z.boolean().optional(),
      harvest: z.boolean().optional(),
      below: PreserveRange.optional().describe('a Preserve zone: layers kept under the level (default 1)'),
      above: PreserveRange.optional().describe('a Preserve zone: layers kept over the level (default 3)'),
    })
    .strict(),
  z
    .object({
      op: z.literal('preserve'),
      id: ZoneId,
      on: z.boolean().optional().describe('default true; a zone of kind preserve is always on'),
      below: PreserveRange.optional(),
      above: PreserveRange.optional(),
      retake: z.boolean().optional().describe('take a new snapshot of the blocks as they are now'),
    })
    .strict(),
  z
    .object({
      op: z.literal('update'),
      id: ZoneId,
      plant: z.string().max(32).nullable().optional(),
      sow: z.boolean().optional(),
      harvest: z.boolean().optional(),
      mode: z.enum(['ores', 'quarry']).optional().describe('a mine zone: quarry or ores only'),
    })
    .strict(),
  z.object({ op: z.literal('seek_seeds'), id: ZoneId }).strict(),
  z
    .object({ op: z.literal('irrigation'), id: ZoneId })
    .strict()
    .describe('the water channels a growing zone wants (cells at soil level, those to dig first); nothing is queued'),
  z
    .object({ op: z.literal('remove'), kind: z.union([StoryZoneKind, z.literal('any')]).optional(), area: StoryArea })
    .strict(),
  z.object({ op: z.literal('bill_add'), id: ZoneId, ...BillSpec, plant: z.string().min(1).max(32) }).strict(),
  z.object({ op: z.literal('bill_update'), id: ZoneId, bill: ZoneId, ...BillSpec }).strict(),
  z.object({ op: z.enum(['bill_remove', 'bill_pause', 'bill_resume']), id: ZoneId, bill: ZoneId }).strict(),
  z.object({ op: z.literal('bill_reorder'), id: ZoneId, ids: z.array(ZoneId).min(1).max(16) }).strict(),
  z.object({ op: z.literal('remove'), id: z.union([z.string().min(1).max(64), z.number().int()]) }).strict(),
  z
    .object({
      op: z.literal('rename'),
      id: z.union([z.string().min(1).max(64), z.number().int()]),
      name: z.string().trim().max(40),
    })
    .strict(),
]);
export type StoryZoneRequest = z.infer<typeof StoryZoneRequest>;

/**
 * Designate by area (the director expands it to every matching target: whole trees for chop, ripe crops for harvest, items
 * for haul, animals for hunt…) or by blocks / entities. `remove` without a kind cancels every designation in the area.
 */
export const StoryDesignationRequest = z
  .object({
    op: z
      .enum(['add', 'remove', 'cancel', 'retry'])
      .describe('retry: the failed designations named by `ids` are tried again now'),
    kind: StoryDesignationKind.optional(),
    area: StoryArea.optional(),
    blocks: z.array(Vec3i).max(4096).optional(),
    ids: z
      .array(z.union([z.string(), z.number()]))
      .max(4096)
      .optional(),
    entities: z.array(z.number().int()).max(512).optional(),
    design: z.union([z.string().max(64), z.number().int()]).optional(),
    filter: StoryMineFilter.optional(),
  })
  .strict();
export type StoryDesignationRequest = z.infer<typeof StoryDesignationRequest>;

export const StoryDesignationResponse = z.object({
  ok: z.boolean().nullish(),
  count: z.number().int().default(0).describe('how many targets were designated (or removed)'),
  targets: z
    .union([z.array(Vec3i), z.array(z.number().int())])
    .nullish()
    .describe('blocks, or entity ids (haul, hunt)'),
  removed: z.array(z.union([z.string(), z.number()])).nullish(),
  retried: z
    .array(z.union([z.string(), z.number()]))
    .nullish()
    .describe('retry: the designations tried again now'),
  entities: z.array(z.number().int()).nullish(),
  rev: z.number().int().nullish(),
  warnings: z
    .array(z.string())
    .nullish()
    .describe('add: targets that lie in a preserved zone (done as asked, then kept as they leave it)'),
});
export type StoryDesignationResponse = z.infer<typeof StoryDesignationResponse>;

export const StoryRoofSpec = z
  .object({
    style: StoryRoofStyle,
    material: StoryMaterial.optional(),
    overhang: z.union([z.literal(0), z.literal(1)]).optional(),
  })
  .strict();
export type StoryRoofSpec = z.infer<typeof StoryRoofSpec>;

/**
 * One queued build. `area` y1 is the first block layer: walls rise `height` blocks from it, a floor fills it, a roof sits on
 * it. `blocks` lists single cells (doors, windows, a single block). `level` is the storey's floor Y (informational).
 */
export const StoryBuildItem = z
  .object({
    kind: StoryBuildKind,
    replaceArea: StoryArea.optional().describe(
      'palette design bounds: clear interior air and permanently release older overlapping build cells',
    ),
    area: StoryArea.optional(),
    blocks: z.array(Vec3i).max(4096).optional(),
    place: z
      .array(z.tuple([z.number().int(), z.number().int(), z.number().int(), z.number().int(), z.number().int()]))
      .max(4096)
      .optional()
      .describe('a mine item: blocks put in [x, y, z, id, meta] once their cell is dug (torches, ladders); `blocks` are its dig cells'),
    material: StoryMaterial.optional(),
    height: z.number().int().min(1).max(12).optional(),
    roof: StoryRoofSpec.optional(),
    facing: StoryFacing.optional(),
    level: z.number().int().optional(),
    name: z.string().max(40).optional(),
    group: z
      .string()
      .max(40)
      .optional()
      .describe("one placed design's items: its blocks wait until the group's dig item (queued first) is done"),
    infinite: z.boolean().optional().describe('a water item: an infinite source (a pool that refills; pawns fill there)'),
    indoor: z
      .boolean()
      .optional()
      .describe('a water item: pouring indoors in a house is allowed (the Water source tool\'s explicit click)'),
  })
  .strict();
export type StoryBuildItem = z.infer<typeof StoryBuildItem>;

/** Exact desired world cell, including intentionally empty space (id 0). */
export const StoryBuildCell = z.tuple([
  z.number().int().min(-30_000_000).max(30_000_000), z.number().int().min(1).max(255),
  z.number().int().min(-30_000_000).max(30_000_000),
  z.number().int().min(0).max(4095), z.number().int().min(0).max(15),
]).refine((cell) => cell[3] !== 0 || cell[4] === 0, 'air must have metadata 0');
export type StoryBuildCell = z.infer<typeof StoryBuildCell>;

export const StoryBuildRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('add'), items: z.array(StoryBuildItem).min(1).max(64) }).strict(),
  z.object({
    op: z.literal('edit'),
    buildId: z.string().min(1).optional(),
    cells: z.array(StoryBuildCell).min(1).max(4096),
  }).strict().describe('edit exact placed-plan cells and their protection; id 0 queues removal and owns the resulting air'),
  z.object({ op: z.literal('unprotect'), cells: z.array(Vec3i).min(1).max(4096) }).strict()
    .describe('stop rebuilding only these cells: release build ownership and preserved snapshots without changing blocks'),
  z
    .object({ op: z.literal('preview'), items: z.array(StoryBuildItem).min(1).max(64) })
    .strict()
    .describe('water items checked and their expected flow; nothing is queued'),
  z
    .object({
      op: z.literal('retry'),
      id: z.union([z.string(), z.number()]).optional(),
      ids: z
        .array(z.union([z.string(), z.number()]))
        .max(256)
        .optional(),
      cells: z.array(Vec3i).max(4096).optional(),
    })
    .strict()
    .describe("a build's failed or resting cells start over (all of them, or `cells`); a reason that still holds is said at once"),
  z.object({ op: z.literal('retry_all') }).strict().describe("every build's failed or resting cells start over"),
  z
    .object({
      op: z.literal('remove'),
      ids: z
        .array(z.union([z.string(), z.number()]))
        .min(1)
        .max(256),
    })
    .strict(),
  z
    .object({
      op: z.literal('pause'),
      ids: z
        .array(z.union([z.string(), z.number()]))
        .min(1)
        .max(256),
    })
    .strict(),
  z
    .object({
      op: z.literal('resume'),
      ids: z
        .array(z.union([z.string(), z.number()]))
        .min(1)
        .max(256),
    })
    .strict(),
  z
    .object({
      op: z.literal('reorder'),
      ids: z
        .array(z.union([z.string(), z.number()]))
        .min(1)
        .max(256),
    })
    .strict(),
]);
export type StoryBuildRequest = z.infer<typeof StoryBuildRequest>;

/** Where poured liquid would run beyond the drawn cells (a preview after the game's flow rules): how many cells, and some. */
export const StoryLiquidFlow = z.object({
  spread: z.number().int().default(0),
  cells: z.array(Vec3i).default([]),
});
export type StoryLiquidFlow = z.infer<typeof StoryLiquidFlow>;

export const StoryBuildResponse = z.object({
  ok: z.boolean().nullish(),
  ids: z.array(z.union([z.string(), z.number()])).nullish(),
  buildId: z.string().nullish().describe('edit: primary placed plan for subsequent extensions'),
  rev: z.number().int().nullish(),
  warnings: z
    .array(z.string())
    .nullish()
    .describe('add / preview of water items: the liquid will spread, or meet lava; add: blocks in a preserved zone'),
  flow: StoryLiquidFlow.nullish().describe('add / preview of water items: where the liquid would run'),
  cells: z.number().int().nullish().describe('preview: the water cells'),
  buildCells: z.number().int().nullish().describe('unprotect: build cells released'),
  preservedCells: z.number().int().nullish().describe('unprotect: preserved cells released'),
  done: z.number().int().nullish().describe('preview: cells already holding the source'),
  error: z.string().nullish().describe('preview: why the items would be refused'),
  reopened: z.number().int().nullish().describe('retry: the failed cells that start over'),
  reasons: z
    .array(z.object({ id: z.union([z.string(), z.number()]), reason: z.string() }))
    .nullish()
    .describe('retry: builds whose reason still holds (blocked again at once, with why)'),
});
export type StoryBuildResponse = z.infer<typeof StoryBuildResponse>;

const Chests = z.array(Vec3i).min(1).max(256);
export const StoryStorageRequest = z.discriminatedUnion('op', [
  z
    .object({
      op: z.literal('set'),
      chests: Chests,
      filter: StoryFilter.optional(),
      priority: StoryStoragePriority.optional(),
      label: z.string().max(40).optional(),
    })
    .strict(),
  z.object({ op: z.literal('add'), chests: Chests, filter: StoryFilter }).strict(),
  z.object({ op: z.literal('remove_entries'), chests: Chests, filter: StoryFilter }).strict(),
  z.object({ op: z.literal('remove'), chests: Chests }).strict(),
  z.object({ op: z.literal('copy'), from: Vec3i, to: Chests }).strict(),
  z
    .object({
      op: z.literal('assign_area'),
      area: StoryArea,
      filter: StoryFilter.optional(),
      priority: StoryStoragePriority.optional(),
      label: z.string().max(40).optional(),
    })
    .strict(),
]);
export type StoryStorageRequest = z.infer<typeof StoryStorageRequest>;

export const StoryStorageResponse = z.object({
  ok: z.boolean().nullish(),
  count: z.number().int().nullish(),
  chests: z.array(Vec3i).nullish(),
  rev: z.number().int().nullish(),
});
export type StoryStorageResponse = z.infer<typeof StoryStorageResponse>;

const BillId = z.union([z.string().max(80), z.number().int()]);
export const StoryBillSpec = z
  .object({
    recipe: z.string().min(1).max(64),
    mode: StoryBillMode,
    target: z.number().int().min(1).max(9999).optional(),
    paused: z.boolean().optional(),
    ingredients: StoryFilter.optional(),
    radius: z.number().int().min(4).max(128).optional(),
    pawn: z.string().min(1).max(64).optional(),
    minSkill: z.number().int().min(0).max(20).optional(),
    store: StoryBillStore.optional(),
    option: z.number().int().min(1).max(3).nullish().describe('an enchant bill: the highest offer to take (1–3)'),
    prefer: z.string().max(40).nullish().describe('an enchant bill: the preferred enchantment (fortune, silk_touch…)'),
    preferOnly: z.boolean().optional(),
    fuel: z
      .union([StoryFuelChoice, z.literal('inherit')])
      .nullish()
      .describe("a furnace bill's fuel override; inherit or null = the furnace's setting"),
  })
  .strict();
export type StoryBillSpec = z.infer<typeof StoryBillSpec>;

/**
 * POST /api/story/colonies/{id}/bills: `add` a bill to the bench at `bench`; `update` a bill's fields (flat); `remove` /
 * `pause` / `resume` one bill; `reorder` a bench's bills (top to bottom). (Nest converts `bill`'s keys too: the request side
 * does not treat `bill` as opaque.)
 */
export const StoryBillsRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('add'), bench: Vec3i, bill: StoryBillSpec }).strict(),
  StoryBillSpec.partial()
    .extend({ op: z.literal('update'), id: BillId })
    .strict(),
  z.object({ op: z.literal('remove'), id: BillId }).strict(),
  z.object({ op: z.literal('pause'), id: BillId }).strict(),
  z.object({ op: z.literal('resume'), id: BillId }).strict(),
  z.object({ op: z.literal('reorder'), bench: Vec3i, ids: z.array(BillId).min(1).max(64) }).strict(),
  // Frees what a build holds for one of its items: a chain claim cancels its crafting, its own item pauses the build.
  z.object({ op: z.literal('release_claim'), build: z.string().max(80), item: z.string().max(64) }).strict(),
  // The tools system's hook: enchant bills (count) at the colony's table for pickaxes (or `items`) preferring an enchantment,
  // or the one a mined material wants (`material`: fortune for ores, silk touch for glass and ice, else efficiency).
  z
    .object({
      op: z.literal('enchant_tools'),
      prefer: z.string().max(40).optional(),
      material: z.string().max(64).optional(),
      items: z.array(z.string().max(64)).max(16).optional(),
      count: z.number().int().min(1).max(64).optional(),
      preferOnly: z.boolean().optional(),
    })
    .strict(),
]);
export type StoryBillsRequest = z.infer<typeof StoryBillsRequest>;

export const StoryBillsResponse = z.object({
  ok: z.boolean().nullish(),
  id: z.union([z.string(), z.number()]).nullish().describe('the new bill (add)'),
  ids: z.array(z.union([z.string(), z.number()])).nullish(),
  note: z.string().nullish(),
  rev: z.number().int().nullish(),
});
export type StoryBillsResponse = z.infer<typeof StoryBillsResponse>;

/**
 * POST /api/story/colonies/{id}/workbenches: the owner marks a placed crafting table / furnace (any bench kind) as the
 * colony's (`designate`) or gives it back (`release`). Only designated benches take bills and unlock the Architect's tiers.
 */
export const StoryWorkbenchesRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('designate'), pos: Vec3i }).strict(),
  z.object({ op: z.literal('release'), pos: Vec3i }).strict(),
  // A furnace's fuel choice (docs/web/story-mode.md "Fuel").
  z.object({ op: z.literal('fuel'), pos: Vec3i, fuel: StoryFuelChoice }).strict(),
]);
export type StoryWorkbenchesRequest = z.infer<typeof StoryWorkbenchesRequest>;
export const StoryWorkbenchesResponse = z.object({
  pos: Vec3i,
  kind: z.string(),
  designated: z.boolean(),
  fuel: z.string().nullish(),
});
export type StoryWorkbenchesResponse = z.infer<typeof StoryWorkbenchesResponse>;

/** Movement speed of the colony's pawns (docs/web/story-mode.md "Speed"): normal, fast (×2), faster (×3). */
export const STORY_SPEEDS = ['normal', 'fast', 'faster'] as const;
export type StorySpeed = (typeof STORY_SPEEDS)[number];

/** A pawn body's speed boost as the client runs it: what it was asked for, what it runs at after the server's rejections. */
export const StoryPawnSpeed = z.object({
  level: z.enum(STORY_SPEEDS).describe('the effective level (after any step-down)'),
  multiplier: Num.describe('the effective movement multiplier (1, 2, 3)'),
  requested: Num.describe('the multiplier the colony asked for'),
  serverFactor: Num.default(1).describe("the server's own share: a Speed effect on 1.12.2 (1.4 = Speed II), else 1"),
  rollbacks: z.number().int().default(0).describe('server corrections of boosted moves since the body logged in'),
});
export type StoryPawnSpeed = z.infer<typeof StoryPawnSpeed>;

/** What idle pawns do (docs/web/story-mode.md "Idle behaviour"). */
export const STORY_IDLE_MODES = ['recreation', 'wander', 'stay'] as const;
export type StoryIdleMode = (typeof STORY_IDLE_MODES)[number];

/** The colony's options: auto-placing a bench a build needs (default on), detecting benches near home (default off). */
export const StoryColonyOptions = z.object({
  autoPlaceBenches: z.boolean().default(true),
  detectBenches: z.boolean().default(false),
  /** A pawn stuck where nothing gets it out is teleported home (op). */
  rescueTeleport: z.boolean().default(true),
  /** Loot no filter takes goes into any chest with room that doesn't deny it. */
  unfilteredStorageTakesAnything: z.boolean().default(true),
  /** A chest keeps what its filter no longer allows (off: dropped beside it, like the genre). */
  keepDisallowed: z.boolean().default(false),
  /** Seconds a stuck pawn tries to get out by itself (or 2 failed escape steps) before the teleport home (default 20, at least 5). */
  stuckTeleportAfterS: z.number().int().default(20),
  idle: z
    .enum(STORY_IDLE_MODES)
    .default('recreation')
    .describe('with nothing to do, in free hours and resting: recreation (pastimes), wander (strolls) or stay (hold)'),
  autoCraftTools: z.boolean().default(true).describe('a job waiting for a tool gets an auto bill "for tools"'),
  spareTools: StorySpareTools.default({ count: 0, kinds: [] }).describe('keep N spare tools of each kind in storage'),
  speed: z
    .enum(STORY_SPEEDS)
    .default('normal')
    .describe(
      "the pawns' movement speed: normal, fast (×2) or faster (×3) — a client-side boost, stepped down if the server rejects it",
    ),
  speedEffect: z
    .boolean()
    .default(true)
    .describe('1.12.2 with an op account: fast / faster also give the pawns a Speed effect (/effect, renewed)'),
  allowLava: z.boolean().default(false).describe('pawns may pour lava (never at home or beside what burns)'),
});
export type StoryColonyOptions = z.infer<typeof StoryColonyOptions>;
/** POST /api/story/colonies/{id}/options: any of the flags and the idle mode. */
export const StoryOptionsRequest = z
  .object({
    autoPlaceBenches: z.boolean().optional(),
    detectBenches: z.boolean().optional(),
    rescueTeleport: z.boolean().optional(),
    unfilteredStorageTakesAnything: z.boolean().optional(),
    keepDisallowed: z.boolean().optional(),
    stuckTeleportAfterS: z.number().int().min(5).max(3600).optional(),
    idle: z.enum(STORY_IDLE_MODES).optional(),
    autoCraftTools: z.boolean().optional(),
    spareTools: z
      .object({ count: z.number().int().min(0).max(8), kinds: z.array(StoryToolKind).max(6) })
      .strict()
      .optional(),
    speed: z.enum(STORY_SPEEDS).optional(),
    speedEffect: z.boolean().optional(),
    allowLava: z.boolean().optional(),
  })
  .strict()
  .describe('any of them (the bot answers 400 for none)');
export type StoryOptionsRequest = z.infer<typeof StoryOptionsRequest>;

export const STORY_ARCHITECT_TABS = ['structure', 'furniture', 'production', 'storage', 'lighting', 'water', 'misc'] as const;
export const STORY_TIERS = ['gathered', 'hand', 'workbench', 'furnace'] as const;

/**
 * One Architect placeable: its tab and tier (gathered = no recipe; hand = a pawn's 2×2 grid; workbench = a crafting table;
 * furnace = a furnace step in its chain), whether the colony unlocked it (a designated bench of that tier), and the colony's
 * stock: `have` (unreserved), `craftable` now from unreserved stock (max 64), `from` (what one takes), `short` (raw missing
 * for one, when neither). `material` is the block it places ([id, meta]: the build item's material), `kind` the default
 * build tool, `kinds` every tool it fits.
 */
export const StoryArchitectEntry = z.object({
  id: z.string(),
  label: z.string(),
  tab: z.string(),
  tier: z.string().describe('gathered | hand | workbench | furnace'),
  unlocked: z.boolean(),
  reason: z.string().nullish(),
  item: StoryItemKey,
  material: StoryItemKey,
  kind: z.string(),
  kinds: z.array(z.string()).default([]),
  chain: z.array(z.string()).default([]),
  have: z.number().int().default(0),
  craftable: z.number().int().default(0),
  from: z.array(z.string()).default([]),
  short: z.array(z.string()).default([]),
});
export type StoryArchitectEntry = z.infer<typeof StoryArchitectEntry>;

/** GET /api/story/colonies/{id}/architect. */
export const StoryArchitectResponse = z.object({
  entries: z.array(StoryArchitectEntry).default([]),
  benches: z.record(z.string(), z.number().int()).default({}),
  tabs: z.array(z.string()).default([...STORY_ARCHITECT_TABS]),
});
export type StoryArchitectResponse = z.infer<typeof StoryArchitectResponse>;

/**
 * POST /api/story/directives: the same payload a colony tool sends the director, addressed to AI bodies (they work it out
 * themselves). `text` is plain words. The signed-in player is the sender: the bridge adds `player` and `allow_commands` from
 * the signed identity, as for viewer chat (a body carrying either is refused).
 */
export const StoryDirectiveSpec = z
  .object({
    kind: StoryDirectiveKind,
    area: StoryArea.optional(),
    targets: z.array(Vec3i).max(4096).optional(),
    entities: z.array(z.number().int()).max(512).optional(),
    filter: StoryMineFilter.optional(),
    build: z.array(StoryBuildItem).max(64).optional(),
    bill: StoryBillSpec.extend({ bench: Vec3i }).strict().optional(),
    text: z.string().trim().min(1).max(500).optional(),
  })
  .strict();
export type StoryDirectiveSpec = z.infer<typeof StoryDirectiveSpec>;

export const StoryDirectiveRequest = z
  .object({
    bots: z.array(z.string().min(1).max(64)).min(1).max(64),
    directive: StoryDirectiveSpec,
    requestId: z.string().regex(/^[A-Za-z0-9_-]{8,64}$/),
  })
  .strict();
export type StoryDirectiveRequest = z.infer<typeof StoryDirectiveRequest>;

export const StoryDirectiveResponse = z.object({
  id: z.union([z.string(), z.number()]),
  delivered: z.union([z.boolean(), z.array(z.string())]).describe('true, or the bots that got it'),
  line: z.string().nullish().describe('the one chat line it became'),
  design: z.union([z.string(), z.number()]).nullish(),
});
export type StoryDirectiveResponse = z.infer<typeof StoryDirectiveResponse>;

// ------------------------------------------------------------------ helpers (the web's preview and the mocks share them)

/**
 * Time of day (ticks, tick 0 = 06:00) → the clock hour (0..23, 0 = midnight): the schedule column in force, as the bot's
 * StoryCharacter.Hour. A negative time (1.12.2, daylight cycle stopped) counts by its absolute value.
 */
export function storyHour(timeOfDay: number): number {
  const t = Math.abs(Math.floor(timeOfDay)) % 24000;
  return (Math.floor(t / 1000) + 6) % 24;
}

/** Clock hour (a schedule column) → its label ("06:00"). */
export function storyClock(hour: number): string {
  return `${String(((Math.floor(hour) % 24) + 24) % 24).padStart(2, '0')}:00`;
}

/** Time of day (ticks, 0 = 06:00, 1000 a game hour) → clock label to the minute ("13:30"). */
export function tickClock(tick: number): string {
  const t = (((Math.round(tick) % 24000) + 24000) % 24000) + 6000;
  const h = Math.floor(t / 1000) % 24,
    m = Math.floor(((t % 1000) * 60) / 1000);
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
}

/** Clock time (hours 0..23, minutes) → time of day in ticks (06:00 = 0, midnight = 18000). */
export function clockTick(hour: number, minute = 0): number {
  return ((Math.round((hour - 6) * 1000 + (minute * 1000) / 60) % 24000) + 24000) % 24000;
}

/** A normalised area: [minX, minY, minZ, maxX, maxY, maxZ]. */
export function normArea(a: readonly number[]): StoryArea {
  return [
    Math.min(a[0]!, a[3]!),
    Math.min(a[1]!, a[4]!),
    Math.min(a[2]!, a[5]!),
    Math.max(a[0]!, a[3]!),
    Math.max(a[1]!, a[4]!),
    Math.max(a[2]!, a[5]!),
  ];
}

/** Named build materials (any [id, meta] works too). */
export const STORY_MATERIALS: ReadonlyArray<{ name: string; label: string; id: number; meta: number }> = [
  { name: 'oak_planks', label: 'Oak planks', id: 5, meta: 0 },
  { name: 'spruce_planks', label: 'Spruce planks', id: 5, meta: 1 },
  { name: 'birch_planks', label: 'Birch planks', id: 5, meta: 2 },
  { name: 'cobblestone', label: 'Cobblestone', id: 4, meta: 0 },
  { name: 'stone', label: 'Stone', id: 1, meta: 0 },
  { name: 'stone_bricks', label: 'Stone bricks', id: 98, meta: 0 },
  { name: 'bricks', label: 'Bricks', id: 45, meta: 0 },
  { name: 'sandstone', label: 'Sandstone', id: 24, meta: 0 },
  { name: 'oak_log', label: 'Oak log', id: 17, meta: 0 },
  { name: 'glass', label: 'Glass', id: 20, meta: 0 },
  { name: 'white_wool', label: 'White wool', id: 35, meta: 0 },
  { name: 'dirt', label: 'Dirt', id: 3, meta: 0 },
  { name: 'crafting_table', label: 'Crafting table', id: 58, meta: 0 },
  { name: 'furnace', label: 'Furnace', id: 61, meta: 0 },
  { name: 'chest', label: 'Chest', id: 54, meta: 0 },
  { name: 'bed', label: 'Bed', id: 26, meta: 0 },
  { name: 'torch', label: 'Torch', id: 50, meta: 5 },
];

/** Furniture the Architect places through the build queue (each is its own build kind). */
export const STORY_FURNITURE = ['crafting_table', 'furnace', 'chest', 'bed', 'torch'] as const;

/** The liquid a water item pours: its material "water" (default) or "lava" (or a block id 8–11); null for anything else. */
export function liquidOf(m: StoryMaterial | null | undefined): 'water' | 'lava' | null {
  if (m === undefined || m === null) return 'water';
  if (Array.isArray(m)) return liquidOfId(m[0]);
  const n = m.trim().toLowerCase();
  return WATER_NAMES.has(n) ? 'water' : LAVA_NAMES.has(n) ? 'lava' : null;
}

const WATER_NAMES: ReadonlySet<string> = new Set(['water', 'still_water', 'flowing_water', 'water_bucket']);
const LAVA_NAMES: ReadonlySet<string> = new Set(['lava', 'still_lava', 'flowing_lava', 'lava_bucket']);
const liquidOfId = (id: number): 'water' | 'lava' | null =>
  id === 8 || id === 9 ? 'water' : id === 10 || id === 11 ? 'lava' : null;

/** Resolves a material to [id, meta] (unknown names → stone). */
export function materialKey(m: StoryMaterial | null | undefined): [number, number] {
  if (Array.isArray(m)) return [m[0], m[1]];
  const hit = STORY_MATERIALS.find((x) => x.name === m);
  return hit ? [hit.id, hit.meta] : [1, 0];
}

/** Block ids a build kind places besides its material. */
const DOOR = 64;
const FENCE = 85;
const SLAB = 44;
const OAK_STAIRS = 53;
const COBBLE_STAIRS = 67;
const STAIRS_META: Record<StoryFacing, number> = { e: 0, w: 1, s: 2, n: 3 };
/** A door's lower half: the placer's facing E S W N (the director's `DoorMeta`). */
const DOOR_META: Record<StoryFacing, number> = { e: 0, s: 1, w: 2, n: 3 };
/** A chest's or furnace's front N S W E = metas 2..5 (the director's `Faced`). */
export const FRONT_META: Record<StoryFacing, number> = { n: 2, s: 3, w: 4, e: 5 };
/** Block ids that are a door, a fence or stairs themselves (b1.8.1 and 1.12.2): such a material is placed as it is. */
const PLACED_DOOR_IDS = new Set([64, 71, 193, 194, 195, 196, 197]);
const FENCE_IDS = new Set([85, 113, 188, 189, 190, 191, 192]);
const STAIRS_IDS = new Set([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203]);

type BuildBlock = [number, number, number, number, number];
type BuildCell = [number, number, number];

/** An expansion's blocks in placement order, each cell once (the first put wins). */
class BuildBlocks {
  readonly out: BuildBlock[] = [];
  private readonly seen = new Set<string>();

  put(x: number, y: number, z: number, id: number, meta: number): void {
    const k = `${x},${y},${z}`;
    if (this.seen.has(k)) return;
    this.seen.add(k);
    this.out.push([x, y, z, id, meta]);
  }

  /** Takes a cell back out (a mine item's placement replaces the dig there). */
  unput(x: number, y: number, z: number): void {
    this.seen.delete(`${x},${y},${z}`);
    const at = this.out.findIndex((c) => c[0] === x && c[1] === y && c[2] === z);
    if (at >= 0) this.out.splice(at, 1);
  }

  cells(cells: readonly BuildCell[], id: number, meta: number): void {
    for (const [x, y, z] of cells) this.put(x, y, z, id, meta);
  }

  /** Every cell of the area's box (x, then y, then z). */
  box(a: StoryArea, id: number, meta: number): void {
    for (let x = a[0]; x <= a[3]; x++)
      for (let y = a[1]; y <= a[4]; y++) for (let z = a[2]; z <= a[5]; z++) this.put(x, y, z, id, meta);
  }
}

/** One build item being expanded: its material, its cells and normalised area, its height, the blocks so far. */
interface Expansion {
  item: StoryBuildItem;
  id: number;
  meta: number;
  cells: BuildCell[];
  a: StoryArea | null;
  h: number;
  b: BuildBlocks;
}

/** Is a column on the edge of the area's rectangle? */
const onOutline = (a: StoryArea, x: number, z: number) => x === a[0] || x === a[3] || z === a[2] || z === a[5];

/** A door's or a piece of furniture's cells: the area's first corner, else the item's cells. */
const anchorCells = (e: Expansion): BuildCell[] => (e.a ? [[e.a[0], e.a[1], e.a[2]]] : e.cells);

/** Wall: the outline of the area's rectangle, `height` blocks up from y1; cells `height` up. */
function expandWall({ a, h, cells, id, meta, b }: Expansion): void {
  if (a)
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++)
        if (onOutline(a, x, z)) for (let y = a[1]; y < a[1] + h; y++) b.put(x, y, z, id, meta);
  for (const [x, y, z] of cells) for (let k = 0; k < h; k++) b.put(x, y + k, z, id, meta);
}

/** Floor: the rectangle filled at y1, then the cells. */
function expandFloor({ a, cells, id, meta, b }: Expansion): void {
  if (a) for (let x = a[0]; x <= a[3]; x++) for (let z = a[2]; z <= a[5]; z++) b.put(x, a[1], z, id, meta);
  b.cells(cells, id, meta);
}

/** A roof's style, overhang and material (its own, else the item's); null when it places nothing. */
function roofSpec(e: Expansion): { style: string; o: number; rid: number; rmeta: number } | null {
  const style = e.item.roof?.style ?? 'flat';
  if (!e.a || style === 'none') return null;
  const [rid, rmeta] = e.item.roof?.material ? materialKey(e.item.roof.material) : [e.id, e.meta];
  return { style, o: e.item.roof?.overhang ?? 0, rid, rmeta };
}

/** Roof: flat = material, slab = slabs, gable = rising toward the ridge along the longer side; `overhang` grows it. */
function expandRoof(e: Expansion): void {
  const r = roofSpec(e);
  if (!r) return;
  const a = e.a!;
  const x1 = a[0] - r.o,
    x2 = a[3] + r.o,
    z1 = a[2] - r.o,
    z2 = a[5] + r.o,
    y0 = a[1];
  const slab = r.style === 'slab',
    gable = r.style !== 'flat' && !slab;
  const bid = slab ? SLAB : r.rid,
    bm = slab ? slabMeta(r.rid) : r.rmeta;
  // Gable: the ridge runs along the longer side; each row rises one block per step in from the eaves.
  const alongX = x2 - x1 >= z2 - z1;
  for (let x = x1; x <= x2; x++)
    for (let z = z1; z <= z2; z++) {
      const rise = gable ? (alongX ? Math.min(z - z1, z2 - z) : Math.min(x - x1, x2 - x)) : 0;
      e.b.put(x, y0 + rise, z, bid, bm);
    }
}

/** A slab's meta for a roof material: cobblestone and wood slabs, else stone. */
const slabMeta = (rid: number) => (rid === 4 ? 3 : rid === 5 ? 2 : 0);

/** Door: two high at each cell, the lower half turned to `facing`. */
function expandDoor(e: Expansion): void {
  const did = PLACED_DOOR_IDS.has(e.id) ? e.id : DOOR;
  for (const [x, y, z] of anchorCells(e)) {
    e.b.put(x, y, z, did, DOOR_META[e.item.facing ?? 'n']);
    e.b.put(x, y + 1, z, did, 8);
  }
}

/** Fence: the outline at y1, one high; then the cells. */
function expandFence({ a, cells, id, b }: Expansion): void {
  const fid = FENCE_IDS.has(id) ? id : FENCE;
  if (a)
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++) if (onOutline(a, x, z)) b.put(x, a[1], z, fid, 0);
  b.cells(cells, fid, 0);
}

/** Stairs: the cells, facing; an area is a run climbing toward `facing` (one step up per block along it). */
function expandStairs({ item, a, cells, id, b }: Expansion): void {
  const sid = STAIRS_IDS.has(id) ? id : id === 4 ? COBBLE_STAIRS : OAK_STAIRS;
  const f = item.facing ?? 'n';
  const m = STAIRS_META[f];
  b.cells(cells, sid, m);
  if (a)
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++) b.put(x, a[1] + stairStep(f, a, x, z), z, sid, m);
}

/** How far up a stair run is at a column (it climbs toward `f`). */
const stairStep = (f: StoryFacing, a: StoryArea, x: number, z: number) =>
  f === 'n' ? a[5] - z : f === 's' ? z - a[2] : f === 'e' ? x - a[0] : a[3] - x;

const BED_DIR: Record<StoryFacing, number> = { s: 0, w: 1, n: 2, e: 3 };
const BED_HEAD: Record<StoryFacing, readonly [number, number]> = { s: [0, 1], w: [-1, 0], n: [0, -1], e: [1, 0] };

/** Furniture: a bed is two cells (the foot here, the head one block toward `facing`); a chest's or furnace's front faces
 * `facing` (as the pawns place it); the rest as it is. */
function expandFurniture(e: Expansion): void {
  const f = e.item.facing ?? 's';
  const [fid, fmeta] = materialKey(e.item.kind);
  for (const [x, y, z] of anchorCells(e)) {
    if (fid === 26) {
      const [dx, dz] = BED_HEAD[f];
      e.b.put(x, y, z, 26, BED_DIR[f]);
      e.b.put(x + dx, y, z + dz, 26, BED_DIR[f] | 8);
    } else e.b.put(x, y, z, fid, fid === 54 || fid === 61 ? FRONT_META[f] : fmeta);
  }
}

/** Mine: each cell dug (air); `place` blocks go in once their cell is dug (replacing the dig there). */
function expandMine({ item, cells, b }: Expansion): void {
  b.cells(cells, 0, 0);
  for (const [x, y, z, pid, pm] of item.place ?? []) {
    b.unput(x, y, z);
    b.put(x, y, z, pid, pm);
  }
}

/** The cells, then the area's whole box, all of one block. */
function fillCellsAndBox({ a, cells, b }: Expansion, id: number, meta: number): void {
  b.cells(cells, id, meta);
  if (a) b.box(a, id, meta);
}

/** Each build kind's expansion. */
const EXPANDERS: ReadonlyMap<string, (e: Expansion) => void> = new Map<string, (e: Expansion) => void>([
  ['wall', expandWall],
  ['floor', expandFloor],
  ['roof', expandRoof],
  ['door', expandDoor],
  ['window', (e) => fillCellsAndBox(e, 20, 0)],
  ['fence', expandFence],
  ['stairs', expandStairs],
  ...STORY_FURNITURE.map((k) => [k, expandFurniture] as const),
  ['mine', expandMine],
  // Each cell a still source of its liquid (water 9, lava 11), poured from a bucket.
  ['water', (e) => fillCellsAndBox(e, liquidOf(e.item.material) === 'lava' ? 11 : 9, 0)],
  ['block', (e) => fillCellsAndBox(e, e.id, e.meta)],
  ['remove', (e) => fillCellsAndBox(e, 0, 0)],
]);

/**
 * The reference expansion of a build item into blocks [x, y, z, id, meta] (the director owns the real one; the web uses this
 * for the placement preview and its block count, the mocks for ghosts):
 * - wall: the outline of the area's rectangle (a line when it is one block thick), `height` blocks up from y1
 * - floor: the rectangle filled at y1; roof: by `roof.style` from y1 (flat = material, slab = slabs, gable = rising toward
 *   the ridge along the longer side, none = nothing), `overhang` grows it one block each way
 * - door: 2 high at each cell; window: glass at each cell; fence: the outline at y1, one high; stairs: the cells, facing
 * - block: each cell or the whole box; remove: the same with id 0
 */
export function expandBuildItem(item: StoryBuildItem): Array<[number, number, number, number, number]> {
  const [id, meta] = materialKey(item.material);
  const e: Expansion = {
    item,
    id,
    meta,
    cells: [...(item.blocks ?? [])],
    a: item.area ? normArea(item.area) : null,
    h: item.height ?? 1,
    b: new BuildBlocks(),
  };
  EXPANDERS.get(item.kind)?.(e);
  return e.b.out;
}

// ------------------------------------------------------------------ what each designate tool matches (preview + mocks)

export const LOG_IDS: ReadonlySet<number> = new Set([17, 162]);
export const PLANT_IDS: ReadonlySet<number> = new Set([6, 31, 32, 37, 38, 175]);
export const ORE_IDS: ReadonlySet<number> = new Set([14, 15, 16, 21, 56, 73, 74, 129, 153]);
export const STONE_IDS: ReadonlySet<number> = new Set([1, 4, 48, 87, 98, 24]);
export const SOIL_IDS: ReadonlySet<number> = new Set([2, 3, 12, 13, 82, 88]);
export const CHEST_IDS: ReadonlySet<number> = new Set([54, 146]);
export const DOOR_IDS: ReadonlySet<number> = new Set([64, 71, 96, 107, 193, 194, 195, 196, 197]);
/** Blocks that only exist because someone placed them (the deconstruct preview). */
export const STRUCTURE_IDS: ReadonlySet<number> = new Set([
  4, 5, 20, 26, 35, 43, 44, 45, 47, 50, 53, 54, 58, 61, 62, 64, 65, 67, 71, 85, 96, 98, 101, 102, 107, 108, 109, 126,
  134, 135, 136, 139, 146, 160, 171,
]);
/** Animal mob types (b1.8.1 and 1.12.2 network ids) for the hunt tool. */
export const ANIMAL_TYPES: ReadonlySet<number> = new Set([90, 91, 92, 93, 94, 95, 96, 98, 100, 101, 102, 103]);
const UNMINEABLE: ReadonlySet<number> = new Set([0, 7, 8, 9, 10, 11]);

/** Does this block ripen to harvest (fully grown crops, melons, pumpkins, cane/cactus above the base, ripe cocoa)? */
export function harvestable(id: number, meta: number, below: number): boolean {
  return RIPE.get(id)?.(meta, below, id) ?? false;
}

type Ripe = (meta: number, below: number, id: number) => boolean;
const grownAt =
  (stage: number): Ripe =>
  (meta) =>
    meta >= stage;
const always: Ripe = () => true;
/** On its own kind (cane, cactus): the base stays. */
const aboveItself: Ripe = (_meta, below, id) => below === id;
/** When each crop is ripe: wheat, carrots, potatoes fully grown; beetroot, nether wart at 3; melons and pumpkins; cane and
 * cactus above the base; cocoa at its last stage. */
const RIPE: ReadonlyMap<number, Ripe> = new Map([
  [59, grownAt(7)],
  [141, grownAt(7)],
  [142, grownAt(7)],
  [207, grownAt(3)],
  [115, grownAt(3)],
  [103, always],
  [86, always],
  [83, aboveItself],
  [81, aboveItself],
  [127, (meta) => meta >> 2 >= 2],
]);

/** Does a block match a block-based designate tool? (`below` = the id under it.) */
export function matchesTool(
  kind: StoryDesignationKind,
  id: number,
  meta: number,
  below: number,
  filter: StoryMineFilter = 'all',
): boolean {
  return TOOL_MATCHES.get(kind)?.(id, meta, below, filter) ?? false;
}

type ToolMatch = (id: number, meta: number, below: number, filter: StoryMineFilter) => boolean;
const inSet =
  (ids: ReadonlySet<number>): ToolMatch =>
  (id) =>
    ids.has(id);
/** The mine tool's filters (any other: every minable block but trees and plants). */
const MINE_FILTERS: ReadonlyMap<string, ReadonlySet<number>> = new Map([
  ['ores', ORE_IDS],
  ['stone', STONE_IDS],
  ['soil', SOIL_IDS],
]);
function minable(id: number, filter: StoryMineFilter): boolean {
  if (UNMINEABLE.has(id)) return false;
  const only = MINE_FILTERS.get(filter);
  if (only) return only.has(id);
  return !LOG_IDS.has(id) && !PLANT_IDS.has(id) && id !== 18 && id !== 161;
}
const TOOL_MATCHES: ReadonlyMap<string, ToolMatch> = new Map<string, ToolMatch>([
  ['chop', inSet(LOG_IDS)],
  ['cut', inSet(PLANT_IDS)],
  ['harvest', (id, meta, below) => harvestable(id, meta, below)],
  ['mine', (id, _meta, _below, filter) => minable(id, filter)],
  ['deconstruct', inSet(STRUCTURE_IDS)],
  ['forbid', inSet(DOOR_IDS)],
  ['allow', inSet(DOOR_IDS)],
]);

/** Groups log cells into trees (log columns that touch, diagonals included). Returns the tree count. */
export function countTrees(logs: ReadonlyArray<readonly [number, number, number]>): number {
  const cols = new Set(logs.map(([x, , z]) => `${x},${z}`));
  const seen = new Set<string>();
  let trees = 0;
  for (const c of cols) {
    if (seen.has(c)) continue;
    trees++;
    const stack = [c];
    seen.add(c);
    while (stack.length) {
      const [x, z] = stack.pop()!.split(',').map(Number) as [number, number];
      for (let dx = -1; dx <= 1; dx++)
        for (let dz = -1; dz <= 1; dz++) {
          const k = `${x + dx},${z + dz}`;
          if (cols.has(k) && !seen.has(k)) {
            seen.add(k);
            stack.push(k);
          }
        }
    }
  }
  return trees;
}

/** The filter key of one item ("17:0"), and of every meta of an id ("17:-1"). */
export function itemFilterKey(id: number, meta: number): string {
  return `${id}:${meta}`;
}
