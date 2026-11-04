/**
 * Story Mode (docs/web/story-mode.md): colonies of script-only pawns run by the bot's colony director, played from the
 * browser. Supervisor routes `/api/story*` (snake_case), passed through by the bridge (`/v1/sup/story*`), public camelCase.
 *
 * Response objects are lenient (the director may add fields; unknown enum values arrive as plain strings). Request bodies are
 * strict. Writes need the `story` role (or `admin`, see `hasRole`).
 *
 * Deltas: `GET /api/story/colonies/{id}?since=rev` returns only what changed after `rev` (items keyed by pawn name, zone /
 * designation / order / build id, storage chest position) plus `removed` keys; without `since`, or with `full: true`, the
 * reply is the whole state and replaces what the client holds. `ghosts` and `categories`, when present, always replace.
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

/** Zone kinds. Loot goes into chests with storage settings (`storage`): the director refuses a `stockpile` zone. */
export const STORY_ZONE_KINDS = ['chop', 'mine', 'farm', 'fish', 'home', 'forbidden', 'dump'] as const;
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
export const StoryTimeControl = z.object({ available: z.boolean(), by: z.string().nullish() });
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
    pawns: z.number().int().min(1).max(12),
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
  if (typeof t === 'string') {
    const p = t.split(',').map(Number);
    return p.length === 3 && p.every(Number.isFinite) ? [p[0]!, p[1]!, p[2]!] : null;
  }
  if (Array.isArray(t)) return [t[0], t[1], t[2]];
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
  stuck: z.string().nullish().describe('why the colony takes it for stuck (a pit, a cave, out-of-reach targets), else null'),
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
  priorities: z.record(z.string(), z.number().int()).default({}).describe('work type → 0 (off) .. 4'),
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
  sow: z.boolean().nullish().describe('pawns till and sow it'),
  harvest: z.boolean().nullish().describe('pawns harvest what is ripe'),
  status: z.string().nullish().describe('a growing zone: "12/40 sown, 6 ripe"'),
  seeds: z.lazy(() => StoryZoneSeeds).nullish(),
  needsHoe: z.boolean().nullish().describe('there is dirt to till and no hoe anywhere'),
});
export type StoryZone = z.infer<typeof StoryZone>;

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
});
export type StoryBuild = z.infer<typeof StoryBuild>;

/** Ghost block [x, y, z, id, meta, build_id, state, kind?] (state: todo | leased | done | blocked | unsupported: a block pawns
 * can't place yet, shown and alerted, never a job; kind: dig | place — a dig ghost's id/meta is the live world's block there,
 * the material the dig expects). */
export const StoryGhostBlock = z.tuple([
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.number().int(),
  z.union([z.string(), z.number()]),
  z.string(),
  z.enum(['dig', 'place']).optional(),
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

export const StoryStorage = z.object({
  chest: Vec3i,
  doubleWith: Vec3i.nullish().describe('the other half of a double chest'),
  label: z.string().nullish(),
  priority: z.string().default('normal'),
  filter: StoryFilter.default({ allow: [], deny: [] }),
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
    .describe('an auto bill: the build it crafts for, or "tools" (a tool a job or the spare-tools option needs); cancel-only'),
  forItem: z.string().nullish().describe("an auto bill: the build's item its chain ends in"),
  step: z.number().int().nullish().describe("an auto bill's place in the recipe chain (ingredients first)"),
  option: z.number().int().nullish().describe('an enchant bill: the highest offer (1–3) it takes; its lapis and levels'),
  prefer: z.string().nullish().describe('an enchant bill: the enchantment it prefers (the offer hinting it first)'),
  preferOnly: z.boolean().nullish().describe('only an offer hinting the preferred enchantment'),
  xpNeeded: z
    .number()
    .int()
    .nullish()
    .describe('the XP levels the last try needed (an offer, an anvil price): pawns with fewer leave it'),
  last: z.string().nullish().describe('what the last job made ("enchanted diamond pickaxe: Fortune II")'),
});
export type StoryBill = z.infer<typeof StoryBill>;

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
});
export type StoryRemoved = z.infer<typeof StoryRemoved>;

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
  beds: z
    .array(z.lazy(() => StoryBed))
    .nullish()
    .describe('every bed the colony knows, in full whenever it is sent'),
  ai: z.array(StoryAiBody).nullish(),
  directives: z.array(StoryDirective).nullish(),
  policies: z.array(StoryPolicy).nullish().describe('gear policies (always the default one)'),
  materialRules: z.array(StoryMaterialRule).nullish().describe('per material / group: the tool, preferred tier, enchantment'),
  materials: z.array(StoryMaterialInfo).nullish().describe('full state only: the rules editor catalog with each floor'),
  removed: StoryRemoved.nullish(),
});
export type StoryColonyState = z.infer<typeof StoryColonyState>;

/** The SSE `story` frame: one colony's delta (the bridge adds the colony `id`). */
export const StoryColonyFrame = StoryColonyState.extend({ id: z.string() });
export type StoryColonyFrame = z.infer<typeof StoryColonyFrame>;

// ------------------------------------------------------------------ writes

const PawnList = z.array(z.string().min(1).max(64)).min(1).max(64);

/** The colony's seats: pawns in game, pawns wanted, the server's free seats and those kept for offline regulars (null = unknown). */
export const StorySeats = z.object({
  online: z.number().int().default(0),
  wanted: z.number().int().default(0),
  capFree: z.number().int().nullish(),
  keptForRegulars: z.number().int().nullish(),
  pending: z.number().int().nullish().describe('pawns added that are not here yet ("joining…")'),
});
export type StorySeats = z.infer<typeof StorySeats>;

/** POST /api/story/colonies/{id}/pawns: + one or more pawns (as seats allow; the rest join later), or − one pawn. */
export const StoryPawnsRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('add'), count: z.number().int().min(1).max(12) }).strict(),
  z.object({ op: z.literal('remove'), pawn: z.string().min(1).max(64) }).strict(),
]);
export type StoryPawnsRequest = z.infer<typeof StoryPawnsRequest>;

export const StoryPawnsResponse = z.object({
  pawns: z.array(z.string()).default([]),
  wanted: z.number().int(),
  pending: z.array(z.object({ reason: z.string() })).default([]),
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
export const StoryPrioritiesRequest = z
  .object({ pawns: PawnList, work: z.partialRecord(StoryWorkType, z.number().int().min(0).max(4)) })
  .strict();
export type StoryPrioritiesRequest = z.infer<typeof StoryPrioritiesRequest>;

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

/**
 * Zones are cell sets painted RimWorld-style (docs/web/story-mode.md "Zones"; houses are zones of kind `home`): `add` paints an
 * area (cells touching a zone of the same type join it, several merge into the oldest; other work zones under it are overwritten;
 * home and forbidden are overlay layers; `into` extends one zone), `remove` erases an area's cells from zones of a kind (or any;
 * empty zones go, cut ones split) or deletes one zone by id, `rename` one zone (an empty name clears it). A growing zone (kind
 * `farm`) takes a `plant` (from `plants`: its first planting bill), `sow` and `harvest`; `update` changes them; `bill_*` edit its
 * planting bills (like a workbench's: add, update, remove, pause, resume, reorder); `seek_seeds` designates tall grass to cut
 * (wheat) or queues a seed bill (pumpkin, melon). Answers what changed: created, merged, grown, shrunk, split, deleted.
 */
export const StoryZoneRequest = z.union([
  z
    .object({
      op: z.literal('add'),
      kind: StoryZoneKind.optional(),
      area: StoryArea,
      into: ZoneId.optional().describe('extend this zone: the painted cells take its type'),
      name: z.string().trim().max(40).optional(),
      plant: z.string().min(1).max(32).optional(),
      sow: z.boolean().optional(),
      harvest: z.boolean().optional(),
    })
    .strict(),
  z
    .object({
      op: z.literal('update'),
      id: ZoneId,
      plant: z.string().max(32).nullable().optional(),
      sow: z.boolean().optional(),
      harvest: z.boolean().optional(),
    })
    .strict(),
  z.object({ op: z.literal('seek_seeds'), id: ZoneId }).strict(),
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
  })
  .strict();
export type StoryBuildItem = z.infer<typeof StoryBuildItem>;

export const StoryBuildRequest = z.discriminatedUnion('op', [
  z.object({ op: z.literal('add'), items: z.array(StoryBuildItem).min(1).max(64) }).strict(),
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

export const StoryBuildResponse = z.object({
  ok: z.boolean().nullish(),
  ids: z.array(z.union([z.string(), z.number()])).nullish(),
  rev: z.number().int().nullish(),
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
export const StoryWorkbenchesRequest = z.object({ op: z.enum(['designate', 'release']), pos: Vec3i }).strict();
export type StoryWorkbenchesRequest = z.infer<typeof StoryWorkbenchesRequest>;
export const StoryWorkbenchesResponse = z.object({ pos: Vec3i, kind: z.string(), designated: z.boolean() });
export type StoryWorkbenchesResponse = z.infer<typeof StoryWorkbenchesResponse>;

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
  idle: z
    .enum(STORY_IDLE_MODES)
    .default('recreation')
    .describe('with nothing to do, in free hours and resting: recreation (pastimes), wander (strolls) or stay (hold)'),
  autoCraftTools: z.boolean().default(true).describe('a job waiting for a tool gets an auto bill "for tools"'),
  spareTools: StorySpareTools.default({ count: 0, kinds: [] }).describe('keep N spare tools of each kind in storage'),
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
    idle: z.enum(STORY_IDLE_MODES).optional(),
    autoCraftTools: z.boolean().optional(),
    spareTools: z
      .object({ count: z.number().int().min(0).max(8), kinds: z.array(StoryToolKind).max(6) })
      .strict()
      .optional(),
  })
  .strict()
  .describe('any of them (the bot answers 400 for none)');
export type StoryOptionsRequest = z.infer<typeof StoryOptionsRequest>;

export const STORY_ARCHITECT_TABS = ['structure', 'furniture', 'production', 'storage', 'lighting', 'misc'] as const;
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
/** Block ids that are a door, a fence or stairs themselves (b1.8.1 and 1.12.2): such a material is placed as it is. */
const PLACED_DOOR_IDS = new Set([64, 71, 193, 194, 195, 196, 197]);
const FENCE_IDS = new Set([85, 113, 188, 189, 190, 191, 192]);
const STAIRS_IDS = new Set([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203]);

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
  const out: Array<[number, number, number, number, number]> = [];
  const [id, meta] = materialKey(item.material);
  const cells: Array<[number, number, number]> = [...(item.blocks ?? [])];
  const a = item.area ? normArea(item.area) : null;
  const h = item.height ?? 1;
  const seen = new Set<string>();
  const put = (x: number, y: number, z: number, bid: number, bm: number) => {
    const k = `${x},${y},${z}`;
    if (seen.has(k)) return;
    seen.add(k);
    out.push([x, y, z, bid, bm]);
  };
  const outline = (fn: (x: number, z: number) => void) => {
    if (!a) return;
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++) if (x === a[0] || x === a[3] || z === a[2] || z === a[5]) fn(x, z);
  };
  switch (item.kind) {
    case 'wall':
      outline((x, z) => {
        for (let y = a![1]; y < a![1] + h; y++) put(x, y, z, id, meta);
      });
      for (const [x, y, z] of cells) for (let k = 0; k < h; k++) put(x, y + k, z, id, meta);
      break;
    case 'floor':
      if (a) for (let x = a[0]; x <= a[3]; x++) for (let z = a[2]; z <= a[5]; z++) put(x, a[1], z, id, meta);
      for (const [x, y, z] of cells) put(x, y, z, id, meta);
      break;
    case 'roof': {
      if (!a) break;
      const style = item.roof?.style ?? 'flat';
      if (style === 'none') break;
      const o = item.roof?.overhang ?? 0;
      const [rid, rmeta] = item.roof?.material ? materialKey(item.roof.material) : [id, meta];
      const x1 = a[0] - o,
        x2 = a[3] + o,
        z1 = a[2] - o,
        z2 = a[5] + o,
        y0 = a[1];
      for (let x = x1; x <= x2; x++)
        for (let z = z1; z <= z2; z++) {
          if (style === 'flat') put(x, y0, z, rid, rmeta);
          else if (style === 'slab') put(x, y0, z, SLAB, rid === 4 ? 3 : rid === 5 ? 2 : 0);
          else {
            // Gable: the ridge runs along the longer side; each row rises one block per step in from the eaves.
            const alongX = x2 - x1 >= z2 - z1;
            const rise = alongX ? Math.min(z - z1, z2 - z) : Math.min(x - x1, x2 - x);
            put(x, y0 + rise, z, rid, rmeta);
          }
        }
      break;
    }
    case 'door':
      for (const [x, y, z] of a ? [[a[0], a[1], a[2]] as [number, number, number]] : cells) {
        put(x, y, z, PLACED_DOOR_IDS.has(id) ? id : DOOR, STAIRS_META[item.facing ?? 's'] & 3);
        put(x, y + 1, z, PLACED_DOOR_IDS.has(id) ? id : DOOR, 8);
      }
      break;
    case 'window':
      for (const [x, y, z] of cells) put(x, y, z, 20, 0);
      if (a)
        for (let x = a[0]; x <= a[3]; x++)
          for (let y = a[1]; y <= a[4]; y++) for (let z = a[2]; z <= a[5]; z++) put(x, y, z, 20, 0);
      break;
    case 'fence':
      outline((x, z) => put(x, a![1], z, FENCE_IDS.has(id) ? id : FENCE, 0));
      for (const [x, y, z] of cells) put(x, y, z, FENCE_IDS.has(id) ? id : FENCE, 0);
      break;
    case 'stairs': {
      const sid = STAIRS_IDS.has(id) ? id : id === 4 ? COBBLE_STAIRS : OAK_STAIRS;
      const m = STAIRS_META[item.facing ?? 'n'];
      for (const [x, y, z] of cells) put(x, y, z, sid, m);
      if (a) {
        // A run climbing toward `facing`: one step up per block along it.
        const f = item.facing ?? 'n';
        for (let x = a[0]; x <= a[3]; x++)
          for (let z = a[2]; z <= a[5]; z++) {
            const step = f === 'n' ? a[5] - z : f === 's' ? z - a[2] : f === 'e' ? x - a[0] : a[3] - x;
            put(x, a[1] + step, z, sid, m);
          }
      }
      break;
    }
    case 'crafting_table':
    case 'furnace':
    case 'chest':
    case 'bed':
    case 'torch': {
      // A bed is two cells: the foot here, the head one block toward `facing`.
      const f = item.facing ?? 's';
      const [fid, fmeta] = materialKey(item.kind);
      for (const [x, y, z] of a ? [[a[0], a[1], a[2]] as [number, number, number]] : cells) {
        if (fid === 26) {
          const dir = { s: 0, w: 1, n: 2, e: 3 }[f];
          const [dx, dz] = ({ s: [0, 1], w: [-1, 0], n: [0, -1], e: [1, 0] } as const)[f];
          put(x, y, z, 26, dir);
          put(x + dx, y, z + dz, 26, dir | 8);
        } else put(x, y, z, fid, fmeta);
      }
      break;
    }
    case 'mine':
      for (const [x, y, z] of cells) put(x, y, z, 0, 0);
      for (const [x, y, z, pid, pm] of item.place ?? []) {
        seen.delete(`${x},${y},${z}`);
        const at = out.findIndex((c) => c[0] === x && c[1] === y && c[2] === z);
        if (at >= 0) out.splice(at, 1);
        put(x, y, z, pid, pm);
      }
      break;
    case 'block':
    case 'remove': {
      const bid = item.kind === 'remove' ? 0 : id,
        bm = item.kind === 'remove' ? 0 : meta;
      for (const [x, y, z] of cells) put(x, y, z, bid, bm);
      if (a)
        for (let x = a[0]; x <= a[3]; x++)
          for (let y = a[1]; y <= a[4]; y++) for (let z = a[2]; z <= a[5]; z++) put(x, y, z, bid, bm);
      break;
    }
  }
  return out;
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
  switch (id) {
    case 59:
    case 141:
    case 142:
      return meta >= 7;
    case 207:
    case 115:
      return meta >= 3;
    case 103:
    case 86:
      return true;
    case 83:
    case 81:
      return below === id;
    case 127:
      return meta >> 2 >= 2;
    default:
      return false;
  }
}

/** Does a block match a block-based designate tool? (`below` = the id under it.) */
export function matchesTool(
  kind: StoryDesignationKind,
  id: number,
  meta: number,
  below: number,
  filter: StoryMineFilter = 'all',
): boolean {
  switch (kind) {
    case 'chop':
      return LOG_IDS.has(id);
    case 'cut':
      return PLANT_IDS.has(id);
    case 'harvest':
      return harvestable(id, meta, below);
    case 'mine':
      if (UNMINEABLE.has(id)) return false;
      if (filter === 'ores') return ORE_IDS.has(id);
      if (filter === 'stone') return STONE_IDS.has(id);
      if (filter === 'soil') return SOIL_IDS.has(id);
      return !LOG_IDS.has(id) && !PLANT_IDS.has(id) && id !== 18 && id !== 161;
    case 'deconstruct':
      return STRUCTURE_IDS.has(id);
    case 'forbid':
    case 'allow':
      return DOOR_IDS.has(id);
    default:
      return false;
  }
}

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
