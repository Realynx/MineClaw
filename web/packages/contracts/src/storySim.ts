/**
 * A synthetic Story Mode colony director for the mocks (the Nest mock bridge and the web's MSW mode). It speaks the public
 * camelCase shapes of `./story.ts`; the Nest mock converts with `toSnake`. Exported as `@mineclaw/contracts/testing`.
 *
 * Pawns walk, pick jobs from their priorities and schedule (ties to skill, then distance), lease designations so two never
 * take the same target, eat when hungry, sleep at home, obey drafted orders and queues, build ghosts in queue order, haul
 * drops into chests whose storage filter accepts them, and work bills at workbenches. Area designations are expanded against
 * the world it is given, so counts are real. AI bodies take directives that go sent → acknowledged → done with a reply.
 */
import {
  categoryItems,
  filterAccepts,
  STORY_DEFAULT_CATEGORIES,
  STORY_DEFAULT_RECIPES,
  storyItemLabel,
} from './storyData.js';
import {
  cellKey,
  cellsTouch,
  cellX,
  cellZ,
  eraseZones,
  paintZones,
  quarryRing,
  quarryStep,
  rectCells,
  zoneCellSet,
} from './storyZones.js';

const QUARRY_TOO_NARROW =
  'a quarry needs at least 2×2 (a 1-wide strip is a trench: paint it as Ores only, or designate Mine)';
import { irrigationPlan, liquidFlow } from './storyWater.js';
import {
  CHEST_IDS,
  expandBuildItem,
  liquidOf,
  LOG_IDS,
  matchesTool,
  ANIMAL_TYPES,
  materialKey,
  normArea,
  storyHour,
  type StoryGrowBill,
  STORY_FURNITURE,
  targetPos,
  STORY_WORK_TYPES,
  type StoryAiBody,
  type StoryAlert,
  type StoryArea,
  type StoryBed,
  type StoryBedsRequest,
  type StoryBill,
  type StoryBillsRequest,
  type StoryArchitectEntry,
  type StoryArchitectResponse,
  type StoryBuild,
  type StoryBuildMaterial,
  type StoryBuildItem,
  type StoryBuildRequest,
  type StoryColonyOptions,
  type StoryOptionsRequest,
  type StoryWorkbenchesRequest,
  type StoryWorkbenchesResponse,
  type StoryColonyState,
  type StoryCreateRequest,
  type StoryDesignation,
  type StoryDesignationKind,
  type StoryDesignationRequest,
  type StoryDirective,
  type StoryDirectiveRequest,
  type StoryFilter,
  type StoryOrder,
  type StoryOrderRequest,
  type StoryOnlineBlocked,
  type StoryOnlineRequest,
  type StoryOnlineResponse,
  type StoryPawn,
  type StorySpeed,
  type StoryPawnsRequest,
  type StoryPawnsResponse,
  type StoryPlant,
  type StoryRole,
  type StoryRolesRequest,
  type StoryStorage,
  type StoryStorageRequest,
  type StoryWorkbench,
  type StoryZone,
  type StoryZoneRequest,
} from './story.js';
import type { StoryGearRequest, StoryMaterialRulesRequest, StoryPoliciesRequest } from './storyGear.js';
import { applyRole, defaultRoles, rolesOp, rolesState } from './storyRoles.js';
import {
  checkAttack,
  draftGear,
  gearOp,
  materialRulesOp,
  newSimGear,
  policiesOp,
  seedEquipment,
  SIM_ARMORY,
  SIM_MATERIALS,
  type AttackTarget,
  type SimGear,
} from './storySimGear.js';

// The package builds without DOM or Node typings; both runtimes have these.
declare function setTimeout(fn: () => void, ms: number): unknown;
declare function structuredClone<T>(value: T): T;

export interface SimEntity {
  id: number;
  kind: string;
  type?: number;
  item?: number;
  count?: number;
  name?: string | null;
  x: number;
  y: number;
  z: number;
}

/** The world the sim plays in (the mock's terrain). */
export interface StoryWorld {
  /** [id, meta] at a block. */
  blockAt(x: number, y: number, z: number): readonly [number, number];
  /** Y of the highest solid block of a column (the ground a pawn stands on is y + 1). */
  ground(x: number, z: number): number;
  /** Mobs and dropped items right now. */
  entities(): SimEntity[];
  /** Optional: the sim changed a block (a tree felled, a ghost built). */
  setBlock?(x: number, y: number, z: number, id: number, meta: number): void;
  /** AI bodies (main, hives, subhive bodies) to show beside the pawns. */
  ai?(): StoryAiBody[];
}

export interface StorySimOptions {
  world: StoryWorld;
  /** Where a colony settles when created without a home. */
  defaultHome?: [number, number, number];
  /** The op account time changes run as (null = none: POST /api/story/time answers 409). Default `maid_chan`. */
  timeOp?: string | null;
  /** Least time between two time changes (ms; default 1000, as the bot limits it). */
  timeRateMs?: number;
  now?: () => number;
}

type Key = string | number;
type Fn<T> = { rev: number; v: T };
/** A build's ghost cell: x, y, z, block id, meta, the build, its state, what it does (dig, place, pour), its failure. */
type GhostCell = [number, number, number, number, number, Key, string, ('dig' | 'place' | 'pour')?, (string | null)?];
/** A recipe of the sim's catalogue. */
type SimRecipe = (typeof STORY_DEFAULT_RECIPES)[number];

interface PawnSim {
  rev: number;
  p: StoryPawn;
  x: number;
  y: number;
  z: number;
  goal: [number, number, number] | null;
  work: number;
  task: { kind: string; ref: Key | null; label: string; target: [number, number, number] | null; type: string } | null;
  idleSince: number;
  lastAlert: number;
  /** Offline: when it comes back by itself (a whitelist re-add, a login under way); null = it waits for a seat. */
  backAt?: number | null;
}

interface Colony {
  id: string;
  name: string;
  created: string;
  paused: boolean;
  released: boolean;
  home: [number, number, number];
  rev: number;
  tod: number;
  /** The server's free seats and those kept for offline regulars (mock). */
  capFree: number;
  kept: number;
  pawns: Map<string, PawnSim>;
  zones: Map<Key, Fn<StoryZone>>;
  designations: Map<Key, Fn<StoryDesignation>>;
  orders: Map<Key, Fn<StoryOrder>>;
  builds: Map<Key, Fn<StoryBuild>>;
  ghostCells: Map<Key, GhostCell[]>;
  ghostsRev: number;
  storages: Map<string, Fn<StoryStorage>>;
  benches: Map<string, Fn<StoryWorkbench>>;
  beds: Map<string, StoryBed>;
  bedsRev: number;
  directives: Map<Key, Fn<StoryDirective>>;
  alerts: Array<Fn<StoryAlert>>;
  removed: Array<{
    rev: number;
    kind: keyof NonNullable<StoryColonyState['removed']>;
    key: Key | [number, number, number];
  }>;
  seq: number;
  stock: Map<string, number>;
  /** Pawns asked for (the + button): the ones not here yet are "joining…". */
  wanted?: number;
  options: StoryColonyOptions;
  gear: SimGear;
  /** Work roles (docs/web/story-mode.md "Roles"). */
  roles: Array<Omit<StoryRole, 'pawns'>>;
}

/** What growing zones can grow (the mock speaks 1.12.2). */
export const STORY_SIM_PLANTS: StoryPlant[] = [
  {
    id: 'wheat',
    label: 'Wheat',
    block: 59,
    seed: 'wheat_seeds',
    seedItem: [295, 0],
    soil: 'farmland',
    harvest: 'crop',
    source: 'cut tall grass (seeds drop) or harvest wheat',
  },
  {
    id: 'carrots',
    label: 'Carrots',
    block: 141,
    seed: 'carrot',
    seedItem: [391, 0],
    soil: 'farmland',
    harvest: 'crop',
    source: 'the carrot itself: harvests, zombie drops, village farms and chests',
  },
  {
    id: 'potatoes',
    label: 'Potatoes',
    block: 142,
    seed: 'potato',
    seedItem: [392, 0],
    soil: 'farmland',
    harvest: 'crop',
    source: 'the potato itself: harvests, zombie drops, village farms and chests',
  },
  {
    id: 'beetroot',
    label: 'Beetroot',
    block: 207,
    seed: 'beetroot_seeds',
    seedItem: [435, 0],
    soil: 'farmland',
    harvest: 'crop',
    source: 'harvest beetroot; village farms and chests',
  },
  {
    id: 'pumpkin',
    label: 'Pumpkin',
    block: 104,
    seed: 'pumpkin_seeds',
    seedItem: [361, 0],
    soil: 'farmland',
    harvest: 'fruit',
    source: 'craft them from a pumpkin (a bill at a crafting table)',
  },
  {
    id: 'melon',
    label: 'Melon',
    block: 105,
    seed: 'melon_seeds',
    seedItem: [362, 0],
    soil: 'farmland',
    harvest: 'fruit',
    source: 'craft them from a melon slice (a bill at a crafting table)',
  },
  {
    id: 'sugar_cane',
    label: 'Sugar cane',
    block: 83,
    seed: 'reeds',
    seedItem: [338, 0],
    soil: 'cane',
    harvest: 'stalk',
    source: 'the cane itself: found by water',
  },
  {
    id: 'cactus',
    label: 'Cactus',
    block: 81,
    seed: 'cactus',
    seedItem: [81, 0],
    soil: 'sand',
    harvest: 'stalk',
    source: 'the cactus itself: found in deserts',
  },
  {
    id: 'oak',
    label: 'Oak sapling',
    block: 6,
    seed: 'sapling',
    seedItem: [6, 0],
    soil: 'dirt',
    harvest: 'tree',
    source: 'leaves of chopped oaks drop them',
  },
  {
    id: 'birch',
    label: 'Birch sapling',
    block: 6,
    seed: 'birch_sapling',
    seedItem: [6, 2],
    soil: 'dirt',
    harvest: 'tree',
    source: 'leaves of chopped birches drop them',
  },
];

/** What an "until N in storage" planting bill counts (the produce's item id). */
const SIM_PRODUCE: Record<string, number> = {
  wheat: 296,
  carrots: 391,
  potatoes: 392,
  beetroot: 434,
  pumpkin: 86,
  melon: 360,
  sugar_cane: 338,
  cactus: 81,
  nether_wart: 372,
};

const SEED_HINT: Record<string, string> = {
  wheat: 'No wheat seeds — cut tall grass to find some',
  carrots: 'Needs carrots — none in storage (harvests, zombie drops, village farms)',
  potatoes: 'Needs potatoes — none in storage (harvests, zombie drops, village farms)',
  pumpkin: 'No pumpkin seeds — store a pumpkin and a crafting table makes them',
  melon: 'No melon seeds — store a melon slice and a crafting table makes them',
};

/** What to do about missing seeds: seek wheat seeds (cut grass), craft a fruit's seeds, else nothing to offer. */
function seedAction(plant: StoryPlant): 'seek_seeds' | 'craft_seeds' | null {
  if (plant.id === 'wheat') return 'seek_seeds';
  return plant.harvest === 'fruit' ? 'craft_seeds' : null;
}

/** A plant's seeds in storage (and what is missing when there are none) for `needed` empty cells. */
function seedsOf(stock: ReadonlyMap<string, number>, plant: StoryPlant, needed: number) {
  const have = stock.get(`${plant.seedItem![0]}:${plant.seedItem![1]}`) ?? 0;
  const none = have === 0;
  return {
    item: plant.seed,
    have,
    neededForEmpty: needed,
    sourceHint: plant.source ?? null,
    missing: none ? (SEED_HINT[plant.id] ?? `No ${plant.label.toLowerCase()} to sow — ${plant.source}`) : null,
    action: none ? seedAction(plant) : null,
  };
}

/** A growing zone with a plant or bills (else it reports nothing). */
const isGrowing = (z: StoryZone) => z.kind === 'farm' && (!!z.plant || !!z.bills);

/** A zone's cell count (its area's when it has no cell set). */
const zoneCellCount = (z: StoryZone) => z.cells ?? (z.area[3] - z.area[0] + 1) * (z.area[5] - z.area[2] + 1);

/** The plant a growing zone sows now: its active bill's (with bills), else its plant. */
function sownPlant(z: StoryZone, active: StoryGrowBill | null): StoryPlant | undefined {
  const id = z.bills ? (active?.plant ?? null) : (z.plant ?? null);
  return STORY_SIM_PLANTS.find((p) => p.id === id);
}

/** A growing zone sowing `plant` (mock: nothing sown yet): its seeds, and whether farmland wants a hoe nobody has. */
function sowingStatus<B extends object>(base: B, stock: ReadonlyMap<string, number>, plant: StoryPlant, cells: number) {
  return {
    ...base,
    status: `0/${cells} sown, 0 ripe`,
    seeds: seedsOf(stock, plant, cells),
    needsHoe: plant.soil === 'farmland' && (stock.get('290:0') ?? 0) === 0,
  };
}

/** A growing zone with nothing to sow: every bill met, or none. */
function idleGrowStatus<B extends object>(base: B, bills: number) {
  return {
    ...base,
    status: `${bills ? 'all bills met' : 'no planting bills'}: sowing nothing, 0 growing, 0 ripe`,
    seeds: null,
  };
}

/** A zone's bills after its plant is set: none (plant cleared), the first bill's plant changed, or a first bill. */
function plantBills(
  bills: StoryGrowBill[] | null | undefined,
  plant: string | null,
  newId: () => string,
): StoryGrowBill[] | null {
  if (!plant) return null;
  if (bills?.length) return [{ ...bills[0]!, plant }, ...bills.slice(1)];
  return [{ id: newId(), plant, mode: 'forever' as const, target: 64, done: 0, paused: false }];
}

/** The state's `ghosts`: every blueprint cell. */
const ghostsState = (c: Colony) => ({ ghosts: { rev: c.ghostsRev, blocks: [...c.ghostCells.values()].flat() } });

/** A delta's `removed`: what went since `since`, by kind (no key when nothing did). */
function removedSince(gone: Colony['removed'], since: number) {
  const removed: Record<string, Array<Key | [number, number, number]>> = {};
  for (const r of gone) if (r.rev > since) (removed[r.kind] ??= []).push(r.key);
  return Object.keys(removed).length ? { removed } : {};
}

type ArchitectRow = (typeof SIM_ARCHITECT)[number];

/** Why a placeable is locked: its bench is missing (a crafting table for workbench tiers and glass panes, or a furnace). */
function benchReason(eid: string, tier: string, table: boolean, furnace: boolean): string | null {
  if ((tier === 'workbench' || eid === 'glass_pane') && !table)
    return 'Needs a crafting table — place one or designate an existing one';
  if (tier === 'furnace' && !furnace) return 'Needs a furnace — place one or designate an existing one';
  return null;
}

/** How a placeable is made from its raw material (`rawKey`, `per` made from one): the chain, craftable now, from / short. */
function craftFields(
  stock: ReadonlyMap<string, number>,
  row: ArchitectRow,
  have: number,
): Pick<StoryArchitectEntry, 'chain' | 'craftable' | 'from' | 'short'> {
  const [, label, , tier, , , , rawKey, per] = row;
  if (!rawKey) return { chain: [], craftable: 0, from: [], short: have > 0 ? [] : [`1 ${label}`] };
  const craftable = have > 0 ? 0 : Math.min(64, Math.floor((stock.get(rawKey) ?? 0) * per));
  const [rid, rmeta] = rawKey.split(':').map(Number) as [number, number];
  const need = `${Math.max(1, Math.ceil(1 / per))} ${storyItemLabel(rid, rmeta)}`;
  return {
    chain: [`${tier === 'furnace' ? 'smelt' : 'craft'} ${label} from ${storyItemLabel(rid, rmeta)}`],
    craftable,
    from: craftable > 0 ? [need] : [],
    short: have > 0 || craftable > 0 ? [] : [need],
  };
}

/** One placeable of the Architect: locked by its bench, what storage has, how it is made. */
function architectEntry(
  row: ArchitectRow,
  stock: ReadonlyMap<string, number>,
  table: boolean,
  furnace: boolean,
): StoryArchitectEntry {
  const [eid, label, tab, tier, item, material, kinds] = row;
  const reason = benchReason(eid, tier, table, furnace);
  const have = stock.get(`${item[0]}:${item[1]}`) ?? 0;
  const made = craftFields(stock, row, have);
  return {
    id: eid,
    label,
    tab,
    tier,
    unlocked: reason === null,
    reason,
    item,
    material,
    kind: kinds[0]!,
    kinds,
    chain: made.chain,
    have,
    craftable: made.craftable,
    from: made.from,
    short: made.short,
  };
}

/** The colony options a request copies when given, in this order (`speed` has its own step). */
const OPTION_FIELDS = [
  'idle',
  'autoCraftTools',
  'spareTools',
  'autoPlaceBenches',
  'detectBenches',
  'rescueTeleport',
  'unfilteredStorageTakesAnything',
  'keepDisallowed',
  'stuckTeleportAfterS',
  'allowLava',
  'speedEffect',
] as const;

const spareToolsCopy = (t: NonNullable<StoryOptionsRequest['spareTools']>) => ({ count: t.count, kinds: [...t.kinds] });

const SPEED_MULTIPLIER: Record<StorySpeed, number> = { normal: 1, fast: 2, faster: 3 };

type BillRequest = Extract<StoryZoneRequest, { op: `bill_${string}` }>;
type BillOf<O extends BillRequest['op']> = Extract<BillRequest, { op: O }>;

const billError = (message: string) => Object.assign(new Error(message), { status: 400 });

/** Refuses a plant the director doesn't grow. */
function knownPlant(p: string): void {
  if (!STORY_SIM_PLANTS.some((x) => x.id === p))
    throw billError(`plant must be one of ${STORY_SIM_PLANTS.map((x) => x.id).join('|')}`);
}

/** The bill's index (refused when the zone has no such bill). */
function billIndex(bills: readonly StoryGrowBill[], id: string | number): number {
  const i = bills.findIndex((b) => String(b.id) === String(id));
  if (i < 0) throw billError('bill: no such bill in the zone');
  return i;
}

/** `bill_add`: a new bill at the end (16 at most); its id. */
function addGrowBill(bills: StoryGrowBill[], body: BillOf<'bill_add'>, newId: () => string): string {
  knownPlant(body.plant);
  if (bills.length >= 16) throw billError('a zone takes at most 16 bills');
  const id = newId();
  bills.push({
    id,
    plant: body.plant,
    mode: body.mode ?? 'forever',
    target: body.target ?? 64,
    done: 0,
    paused: body.paused ?? false,
  });
  return id;
}

/** `bill_update`: the fields given change; a new plant or mode starts the count again. */
function updateGrowBill(bills: StoryGrowBill[], body: BillOf<'bill_update'>): void {
  const i = billIndex(bills, body.bill);
  const old = bills[i]!;
  if (body.plant) knownPlant(body.plant);
  const next = {
    ...old,
    ...(body.plant ? { plant: body.plant } : {}),
    ...(body.mode ? { mode: body.mode } : {}),
    ...(body.target !== undefined ? { target: body.target } : {}),
    ...(body.paused !== undefined ? { paused: body.paused } : {}),
  };
  if (next.plant !== old.plant || next.mode !== old.mode) next.done = 0;
  bills[i] = next;
}

/** `bill_reorder`: the named bills first (in that order), the rest after. */
function reorderGrowBills(bills: StoryGrowBill[], ids: ReadonlyArray<string | number>): StoryGrowBill[] {
  const order = ids.map((id) => bills[billIndex(bills, id)]!);
  return [...new Set(order), ...bills.filter((b) => !order.includes(b))];
}

/** Applies a bill op to the zone's bills (a copy); `made` is a new bill's id. */
function applyBillOp(
  bills: StoryGrowBill[],
  body: BillRequest,
  newId: () => string,
): { bills: StoryGrowBill[]; made: string | null } {
  switch (body.op) {
    case 'bill_add':
      return { bills, made: addGrowBill(bills, body, newId) };
    case 'bill_update':
      updateGrowBill(bills, body);
      return { bills, made: null };
    case 'bill_reorder':
      return { bills: reorderGrowBills(bills, body.ids), made: null };
    default: {
      const i = billIndex(bills, body.bill);
      if (body.op === 'bill_remove') bills.splice(i, 1);
      else bills[i] = { ...bills[i]!, paused: body.op === 'bill_pause' };
      return { bills, made: null };
    }
  }
}

const BENCH_KIND: Record<number, string> = {
  58: 'crafting_table',
  61: 'furnace',
  62: 'furnace',
  117: 'brewing_stand',
  145: 'anvil',
  116: 'enchanting_table',
};

/**
 * The sim's Architect catalog: [id, label, tab, tier, item, block material, kinds, raw "id:meta" it is made from, outputs per
 * raw]. Tiers as the bot's (gathered | hand | workbench | furnace); the bot derives them from the version's recipes.
 */
const SIM_ARCHITECT: Array<
  [string, string, string, string, [number, number], [number, number], string[], string | null, number]
> = [
  ['planks', 'Oak Planks', 'structure', 'hand', [5, 0], [5, 0], ['wall', 'floor', 'block', 'roof'], '17:0', 4],
  ['cobblestone', 'Cobblestone', 'structure', 'gathered', [4, 0], [4, 0], ['wall', 'floor', 'block', 'roof'], null, 1],
  ['stone', 'Stone', 'structure', 'furnace', [1, 0], [1, 0], ['wall', 'floor', 'block', 'roof'], '4:0', 1],
  [
    'stonebrick',
    'Stone Bricks',
    'structure',
    'furnace',
    [98, 0],
    [98, 0],
    ['wall', 'floor', 'block', 'roof'],
    '4:0',
    1,
  ],
  ['log', 'Oak Wood', 'structure', 'gathered', [17, 0], [17, 0], ['wall', 'floor', 'block'], null, 1],
  ['dirt', 'Dirt', 'structure', 'gathered', [3, 0], [3, 0], ['wall', 'floor', 'block'], null, 1],
  ['glass', 'Glass', 'structure', 'furnace', [20, 0], [20, 0], ['window', 'wall', 'block'], '12:0', 1],
  ['glass_pane', 'Glass Pane', 'structure', 'furnace', [102, 0], [102, 0], ['window', 'block'], '12:0', 2],
  ['oak_stairs', 'Oak Wood Stairs', 'structure', 'workbench', [53, 0], [53, 0], ['stairs'], '17:0', 2],
  ['stone_stairs', 'Cobblestone Stairs', 'structure', 'workbench', [67, 0], [67, 0], ['stairs'], '4:0', 0.66],
  ['stone_slab', 'Stone Slab', 'structure', 'workbench', [44, 0], [44, 0], ['floor', 'block', 'roof'], '4:0', 2],
  ['fence', 'Oak Fence', 'structure', 'workbench', [85, 0], [85, 0], ['fence'], '17:0', 1.5],
  ['wooden_door', 'Oak Door', 'structure', 'workbench', [324, 0], [64, 0], ['door'], '17:0', 1.5],
  ['wool', 'White Wool', 'structure', 'hand', [35, 0], [35, 0], ['wall', 'floor', 'block'], '287:0', 0.25],
  ['bed', 'Bed', 'furniture', 'workbench', [355, 0], [26, 0], ['bed'], '35:0', 0.33],
  ['bookshelf', 'Bookshelf', 'furniture', 'workbench', [47, 0], [47, 0], ['wall', 'block'], '17:0', 0.5],
  ['crafting_table', 'Crafting Table', 'production', 'hand', [58, 0], [58, 0], ['crafting_table'], '17:0', 1],
  ['furnace', 'Furnace', 'production', 'workbench', [61, 0], [61, 0], ['furnace'], '4:0', 0.125],
  ['chest', 'Chest', 'storage', 'workbench', [54, 0], [54, 0], ['chest'], '17:0', 0.5],
  ['torch', 'Torch', 'lighting', 'hand', [50, 0], [50, 0], ['torch'], '263:0', 4],
  ['glowstone', 'Glowstone', 'lighting', 'gathered', [89, 0], [89, 0], ['wall', 'floor', 'block'], null, 1],
  ['ladder', 'Ladder', 'misc', 'workbench', [65, 0], [65, 0], ['block'], '17:0', 2],
];

const NAMES = ['Ash', 'Bryn', 'Cato', 'Dara', 'Elio', 'Fenn', 'Gwen', 'Hollis', 'Ivo', 'June', 'Kit', 'Lark'];
const TRAITS = [
  { id: 'night_owl', label: 'Night owl', effect: 'Works best at night' },
  { id: 'brave', label: 'Brave', effect: 'Stays in a fight longer' },
  { id: 'skittish', label: 'Skittish', effect: 'Flees early (hp 12, food 9)' },
  { id: 'industrious', label: 'Industrious', effect: 'Shorter idle between jobs' },
  { id: 'green_thumb', label: 'Green thumb', effect: 'Farming skill grows faster' },
  { id: 'steady_hands', label: 'Steady hands', effect: 'Building skill grows faster' },
];
/** By clock hour (index 0 = 00:00, as the bot): work 06–19, anything 20–21, free 22, sleep 23–05. */
const DEFAULT_SCHEDULE = Array.from({ length: 24 }, (_, h) =>
  h >= 6 && h < 20 ? 'work' : h < 6 || h === 23 ? 'sleep' : h < 22 ? 'anything' : 'free',
);
const SPEED = 4.3; // blocks per second
const WORK_MS: Record<string, number> = {
  chop: 3500,
  mine: 1400,
  harvest: 900,
  cut: 700,
  haul: 1500,
  build: 700,
  deconstruct: 900,
  hunt: 2500,
  craft: 2600,
  cook: 2600,
};
/** Which work type a designation feeds. */
const WORK_OF: Record<string, string> = {
  chop: 'chop',
  mine: 'mine',
  harvest: 'farm',
  cut: 'farm',
  haul: 'haul',
  hunt: 'hunt',
  deconstruct: 'build',
  build: 'build',
};

function hash(s: string): number {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619);
  return h >>> 0;
}

function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const keyOf = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;
const dist = (a: readonly unknown[], b: readonly unknown[]) =>
  Math.hypot(Number(a[0]) - Number(b[0]), Number(a[2]) - Number(b[2]));

/** The fuel choices the sim's (b1.8.1) furnaces burn. */
const SIM_FUELS = ['auto', 'coal', 'charcoal', 'coal_or_charcoal', 'planks', 'logs', 'lava_bucket', 'any'] as const;

export class StorySim {
  readonly colonies = new Map<string, Colony>();
  private nextId = 1;
  private readonly now: () => number;
  private listeners = new Set<(colony: string) => void>();

  constructor(private readonly opts: StorySimOptions) {
    this.now = opts.now ?? (() => Date.now());
  }

  /** Called with a colony id whenever its revision moves (the mocks push a `story` frame). */
  onChange(fn: (colony: string) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  private iso(): string {
    return new Date(this.now()).toISOString().replace('Z', '+00:00');
  }

  private bump(c: Colony): number {
    return ++c.rev;
  }

  // ------------------------------------------------------------------ colonies

  list() {
    return {
      colonies: [...this.colonies.values()].map((c) => ({
        id: c.id,
        name: c.name,
        created: c.created,
        paused: c.paused,
        active: !c.released,
        pawns: c.released ? [] : [...c.pawns.keys()],
        version: c.rev,
      })),
      ai: this.opts.world.ai?.() ?? [],
      directives: this.directives(),
      timeControl: this.timeControl(),
    };
  }

  private timeAt = -Infinity;

  /** Whether the time of day can be set, and as whom. */
  timeControl() {
    const by = this.opts.timeOp === undefined ? 'maid_chan' : this.opts.timeOp;
    return { available: !!by, by: by ?? null };
  }

  /** Sets every colony's time of day (as the bot does through its op account): at most once a second. */
  time(body: { time?: unknown }) {
    const t = body?.time;
    if (typeof t !== 'number' || !Number.isInteger(t) || t < 0 || t > 23999)
      throw Object.assign(new Error('time must be an integer 0..23999'), { status: 400 });
    const tc = this.timeControl();
    if (!tc.available) throw Object.assign(new Error('no op account'), { status: 409 });
    const now = this.now();
    if (now - this.timeAt < (this.opts.timeRateMs ?? 1000))
      throw Object.assign(new Error('rate limited: one time change a second'), { status: 429 });
    this.timeAt = now;
    for (const c of this.colonies.values()) {
      c.tod = t;
      for (const p of c.pawns.values()) p.rev = this.bump(c);
    }
    return { time: t, by: tc.by };
  }

  create(req: StoryCreateRequest): { id: string } {
    const n = this.nextId++;
    const id = `story-${n}`;
    const h = req.home ? [req.home.x, req.home.y, req.home.z] : (this.opts.defaultHome ?? [0, 64, 0]);
    const home: [number, number, number] = [h[0]!, this.opts.world.ground(h[0]!, h[2]!), h[2]!];
    const c: Colony = {
      id,
      name: req.name,
      created: this.iso(),
      paused: false,
      released: false,
      home,
      rev: 1,
      tod: 3000,
      capFree: 6,
      kept: 2,
      pawns: new Map(),
      zones: new Map(),
      designations: new Map(),
      orders: new Map(),
      builds: new Map(),
      ghostCells: new Map(),
      ghostsRev: 1,
      storages: new Map(),
      benches: new Map(),
      beds: new Map(),
      bedsRev: 1,
      directives: new Map(),
      alerts: [],
      removed: [],
      seq: 1,
      stock: new Map([
        ['5:0', 96],
        ['4:0', 128],
        ['17:0', 40],
        ['20:0', 12],
        ['280:0', 16],
        ['263:0', 8],
      ]),
      options: { autoPlaceBenches: true, detectBenches: false, idle: 'recreation', autoCraftTools: true, spareTools: { count: 0, kinds: [] }, rescueTeleport: true, unfilteredStorageTakesAnything: true, keepDisallowed: false, stuckTeleportAfterS: 20, speed: 'normal', speedEffect: true, allowLava: false },
      gear: newSimGear(),
      roles: defaultRoles(),
    };
    for (let i = 0; i < req.pawns; i++) {
      const display = NAMES[(i + n * 3) % NAMES.length]!;
      const name = `maid_s${n}_${display.toLowerCase()}`;
      c.pawns.set(name, this.newPawn(c, name, display, i));
    }
    this.colonies.set(id, c);
    // Workbenches and chests already standing near home count from the start.
    this.discover(c, [home[0] - 24, home[1] - 6, home[2] - 24, home[0] + 24, home[1] + 10, home[2] + 24]);
    this.alert(c, 'info', `${req.name} was founded with ${req.pawns} pawn${req.pawns === 1 ? '' : 's'}.`);
    return { id };
  }

  private newPawn(c: Colony, name: string, display: string, i: number): PawnSim {
    const r = rng(hash(name));
    const skills: StoryPawn['skills'] = {};
    for (const t of STORY_WORK_TYPES)
      if (t !== 'fish') skills[t] = { level: Math.floor(r() * 15) + (r() < 0.2 ? 5 : 0), xp: 0, passion: r() < 0.25 };
    // Fish came later: drawn from its own seed so every other draw stays as it was.
    const rf = rng(hash(`${name}/fish`));
    skills.fish = { level: Math.floor(rf() * 15) + (rf() < 0.2 ? 5 : 0), xp: 0, passion: rf() < 0.25 };
    const priorities: Record<string, number> = {};
    for (const t of STORY_WORK_TYPES) {
      const lv = skills[t]!.level;
      priorities[t] = lv >= 12 ? 1 : lv >= 8 ? 2 : lv >= 4 ? 3 : 0;
    }
    priorities.haul = Math.max(priorities.haul ?? 0, 3);
    const traits = [...TRAITS].sort(() => r() - 0.5).slice(0, 1 + Math.floor(r() * 3));
    if (traits.some((t) => t.id === 'brave') && traits.some((t) => t.id === 'skittish'))
      traits.splice(
        traits.findIndex((t) => t.id === 'skittish'),
        1,
      );
    const x = c.home[0] + ((i % 4) - 1.5) * 1.6 + 0.5;
    const z = c.home[2] + (Math.floor(i / 4) - 1) * 1.6 + 0.5;
    const y = this.opts.world.ground(Math.floor(x), Math.floor(z)) + 1;
    return {
      rev: c.rev,
      x,
      y,
      z,
      goal: null,
      work: 0,
      task: null,
      idleSince: this.now(),
      lastAlert: 0,
      p: {
        name,
        display,
        kind: 'pawn',
        online: true,
        hp: 20,
        food: 14 + Math.floor(r() * 6),
        pos: [x, y, z],
        dim: 0,
        drafted: false,
        state: 'idle',
        job: null,
        queue: [],
        skills,
        traits,
        priorities,
        role: null,
        schedule: [...DEFAULT_SCHEDULE],
        held: null,
        inventory: [],
        equipment: seedEquipment(i),
        policy: i === 0 ? 'soldier' : null,
        gearTasks: [],
        xpLevel: Math.floor(r() * 18),
        speed: { level: 'normal', multiplier: 1, requested: 1, serverFactor: 1, rollbacks: 0 },
      },
    };
  }

  colony(id: string): Colony {
    const c = this.colonies.get(id);
    if (!c) throw Object.assign(new Error(`no colony ${id}`), { status: 404 });
    return c;
  }

  /** The colony state in full, or the delta since `since`. */
  state(id: string, since?: number): StoryColonyState {
    const c = this.colony(id);
    const s = since ?? 0;
    const full = s === 0;
    const pick = <T>(m: Map<unknown, Fn<T>>) => [...m.values()].filter((f) => full || f.rev > s).map((f) => f.v);
    return {
      id: c.id,
      name: c.name,
      rev: c.rev,
      at: this.iso(),
      full,
      timeOfDay: Math.round(c.tod),
      hour: storyHour(c.tod),
      paused: c.paused,
      active: !c.released,
      since: since ?? null,
      tieBreak: 'column',
      options: { ...c.options },
      timeControl: this.timeControl(),
      home: c.home,
      workTypes: [...STORY_WORK_TYPES],
      pawns: [...c.pawns.values()].filter((p) => full || p.rev > s).map((p) => structuredClone(p.p)),
      seats: this.seatsState(c),
      zones: pick(c.zones).map((z) => this.growStatus(c, z)),
      ...(full ? { plants: STORY_SIM_PLANTS } : {}),
      designations: pick(c.designations),
      orders: pick(c.orders),
      alerts: c.alerts
        .filter((a) => full || a.rev > s)
        .map((a) => a.v)
        .slice(-40),
      builds: pick(c.builds),
      ...(full || c.ghostsRev > s ? ghostsState(c) : {}),
      ...(full ? { categories: STORY_DEFAULT_CATEGORIES } : {}),
      storages: pick(c.storages),
      workbenches: pick(c.benches),
      ...this.bedsSince(c, full, s),
      ai: this.aiBodies(),
      directives: pick(c.directives),
      policies: c.gear.policies.map((x) => ({ ...x, tools: [...x.tools] })),
      roles: rolesState(
        c.roles,
        [...c.pawns.values()].map((p) => p.p),
      ),
      materialRules: c.gear.rules.map((x) => ({ ...x })),
      ...(full ? { materials: SIM_MATERIALS } : {}),
      ...(full ? {} : removedSince(c.removed, s)),
    };
  }

  /** The colony's seats: online now, wanted, the cap's free seats, those kept for regulars, pawns waiting for one. */
  private seatsState(c: Colony) {
    return {
      online: [...c.pawns.values()].filter((p) => p.p.online).length,
      wanted: Math.max(c.wanted ?? 0, c.pawns.size),
      capFree: this.seatsOf(c).capFree,
      keptForRegulars: this.seatsOf(c).kept,
      pending: Math.max(0, (c.wanted ?? 0) - [...c.pawns.values()].filter((p) => !p.p.leaving).length),
    };
  }

  /** The state's `beds` when they changed since `since` (or a full state). */
  private bedsSince(c: Colony, full: boolean, since: number) {
    return full || c.bedsRev > since ? { beds: this.bedList(c) } : {};
  }

  /** The AI bodies the world shows beside the pawns. */
  private aiBodies(): StoryAiBody[] {
    return this.opts.world.ai?.() ?? [];
  }

  recipes(workbench?: string) {
    return STORY_DEFAULT_RECIPES.filter((r) => !workbench || r.workbench === workbench);
  }

  // ------------------------------------------------------------------ writes

  release(id: string) {
    const c = this.colony(id);
    c.released = true;
    for (const p of c.pawns.values()) {
      p.p.online = false;
      p.p.state = 'offline';
      p.p.offlineReason = 'released';
      p.backAt = null;
      p.p.job = null;
      p.rev = this.bump(c);
    }
    this.alert(c, 'info', 'The pawns were released; the colony is saved.');
    return { ok: true, rev: c.rev };
  }

  resume(id: string) {
    const c = this.colony(id);
    c.released = false;
    for (const p of c.pawns.values()) {
      p.p.online = true;
      p.p.state = 'idle';
      p.p.offlineReason = null;
      p.rev = this.bump(c);
    }
    this.alert(c, 'info', 'The pawns are back.');
    return { id: c.id, pawns: c.pawns.size, note: 'spawned again', rev: c.rev };
  }

  // ------------------------------------------------------------------ seats (offline pawns: a whitelist kick, the player cap)

  /** The server's seats as the bot's slot policy would see them (mock: set by `setSeats`). */
  private seatsOf(c: Colony) {
    return { capFree: c.capFree, kept: c.kept };
  }

  /** Mock knobs: the free seats and those kept for offline regulars. */
  setSeats(id: string, capFree: number, kept: number): void {
    const c = this.colony(id);
    c.capFree = capFree;
    c.kept = kept;
    for (const p of c.pawns.values()) p.rev = this.bump(c);
  }

  private capReason(c: Colony): string {
    return c.capFree <= 0
      ? 'server full (7/7 seats taken)'
      : `server full (player cap: ${c.capFree} free, ${c.kept} kept for regulars)`;
  }

  /**
   * Takes a pawn offline the way the bot sees it: `whitelist` = kicked "not white-listed" (main re-adds it; it is back after
   * `backInMs`), `cap` = it didn't fit under the player cap (it waits for a seat or "Bring online" with the regulars' seats).
   */
  setOffline(id: string, pawn: string, why: 'whitelist' | 'cap', backInMs = 20000): void {
    const c = this.colony(id);
    const p = c.pawns.get(pawn);
    if (!p) throw Object.assign(new Error(`no pawn '${pawn}'`), { status: 404 });
    this.drop(c, p);
    p.p.online = false;
    p.p.state = 'offline';
    p.p.offlineReason = why === 'whitelist' ? 'not whitelisted — re-adding' : this.capReason(c);
    p.backAt = why === 'whitelist' ? this.now() + backInMs : null;
    p.rev = this.bump(c);
    this.alert(c, 'warn', `${p.p.display} can't come online: ${p.p.offlineReason}`, pawn);
  }

  /** POST /api/story/colonies/{id}/online: offline pawns (null = all) are whitelisted again and started, the cap permitting. */
  online(id: string, body: StoryOnlineRequest): StoryOnlineResponse {
    const c = this.colony(id);
    const names = body.pawns === null ? this.offlinePawns(c) : body.pawns.map((n) => this.pawnNamed(c, n).p.name);
    const started: string[] = [];
    const blocked: StoryOnlineBlocked[] = [];
    for (const n of names) {
      const reason = this.startPawn(c, n, !!body.overCap);
      if (reason) blocked.push({ pawn: n, reason });
      else started.push(n);
    }
    if (started.length)
      this.alert(c, 'info', `Bringing ${started.length} pawn${started.length === 1 ? '' : 's'} online.`);
    return { started, blocked };
  }

  /** Every offline pawn's account. */
  private offlinePawns(c: Colony): string[] {
    return [...c.pawns.values()].filter((p) => !p.p.online).map((p) => p.p.name);
  }

  /** A pawn by account or shown name (404 when none). */
  private pawnNamed(c: Colony, n: string) {
    const p = c.pawns.get(n) ?? [...c.pawns.values()].find((x) => x.p.display.toLowerCase() === n.toLowerCase());
    if (!p) throw Object.assign(new Error(`no pawn '${n}'`), { status: 404 });
    return p;
  }

  /**
   * Brings one pawn online (online already counts as started); the reason when it can't. A hosted pawn (kicked "not
   * white-listed") is re-added and retried now; one that never got a seat needs one (the regulars' seats with `overCap`).
   */
  private startPawn(c: Colony, n: string, overCap: boolean): string | null {
    const p = c.pawns.get(n)!;
    if (c.released) return 'the colony is released: resume it first';
    if (p.p.online) return null;
    const hosted = p.backAt !== null && p.backAt !== undefined;
    if (!hosted && c.capFree - 1 < (overCap ? 0 : c.kept)) {
      const reason = this.capReason(c);
      p.p.offlineReason = reason;
      p.rev = this.bump(c);
      return reason;
    }
    if (!hosted) c.capFree--;
    p.p.offlineReason = 'connecting';
    p.backAt = this.now() + 1500;
    p.rev = this.bump(c);
    return null;
  }


  /** Mock: marks designations failed (3 tries) with a reason, resting 10 minutes. */
  failDesignations(id: string, ids: Key[], reason: string): void {
    const c = this.colony(id);
    for (const k of ids) {
      const f = c.designations.get(k);
      if (!f) continue;
      f.v = { ...f.v, status: 'failed', fails: 3, reason, retryAt: new Date(this.now() + 600000).toISOString() };
      f.rev = this.bump(c);
    }
  }

  private bedList(c: Colony): StoryBed[] {
    const zoneOf = (p: readonly number[]) =>
      [...c.zones.values()].find((z) => z.v.kind === 'home' && zoneCellSet(z.v).has(cellKey(p[0]!, p[2]!)))?.v.id ??
      null;
    return [...c.beds.values()].map((b) => ({ ...b, zone: zoneOf(b.pos) }));
  }

  /** Beds: give one to a pawn (its other bed is freed: one bed a pawn) or free one. */
  /** The bed at a block: its foot, or the head half beside it (a bed is named by its foot). */
  private bedAt(c: Colony, bp: { x: number; y: number; z: number }) {
    const bed =
      c.beds.get(keyOf([bp.x, bp.y, bp.z])) ??
      [...c.beds.values()].find(
        (b) => b.pos[1] === bp.y && Math.abs(b.pos[0] - bp.x) + Math.abs(b.pos[2] - bp.z) === 1,
      );
    if (!bed) throw Object.assign(new Error('no bed there'), { status: 400 });
    return bed;
  }

  beds(id: string, body: StoryBedsRequest) {
    const c = this.colony(id);
    const bp = body?.bed;
    if (!bp || ![bp.x, bp.y, bp.z].every((v) => Number.isInteger(v)))
      throw Object.assign(new Error('bed must be {x,y,z}'), { status: 400 });
    const bed = this.bedAt(c, bp);
    if (body.op === 'assign') {
      if (!c.pawns.has(body.pawn)) throw Object.assign(new Error('no such pawn'), { status: 404 });
      for (const b of c.beds.values()) if (b.pawn === body.pawn) b.pawn = null;
      bed.pawn = body.pawn;
    } else if (body.op === 'unassign') bed.pawn = null;
    else throw Object.assign(new Error('op must be assign|unassign'), { status: 400 });
    c.bedsRev = this.bump(c);
    return { beds: this.bedList(c) };
  }

  /** Moves home (as the director does): pawns farther than 48 blocks from the new home are brought next to it. */
  home(id: string, body: { home?: { x?: unknown; y?: unknown; z?: unknown } }) {
    const c = this.colony(id);
    const h = body?.home;
    if (!h || ![h.x, h.y, h.z].every((v) => Number.isInteger(v)))
      throw Object.assign(new Error('home must be {x,y,z}'), { status: 400 });
    const x = h.x as number,
      z = h.z as number;
    c.home = [x, this.opts.world.ground(x, z), z];
    this.bump(c);
    this.discover(c, [x - 24, c.home[1] - 6, z - 24, x + 24, c.home[1] + 10, z + 24]);
    let i = 0;
    for (const p of c.pawns.values()) {
      if (Math.hypot(p.x - x, p.z - z) <= 48) continue;
      this.drop(c, p);
      p.x = x + ((i % 4) - 1.5) * 1.6 + 0.5;
      p.z = z + (Math.floor(i / 4) - 1) * 1.6 + 0.5;
      p.y = this.opts.world.ground(Math.floor(p.x), Math.floor(p.z)) + 1;
      p.p.pos = [Math.round(p.x * 100) / 100, p.y, Math.round(p.z * 100) / 100];
      p.rev = this.bump(c);
      i++;
    }
    this.alert(c, 'info', i ? `Home moved; ${i} pawn${i === 1 ? ' is' : 's are'} brought there.` : 'Home moved.');
    return { home: c.home };
  }

  pause(id: string, paused: boolean) {
    const c = this.colony(id);
    c.paused = paused;
    this.bump(c);
    for (const p of c.pawns.values()) p.rev = c.rev;
    return { ok: true, rev: c.rev };
  }

  private pawnsOf(c: Colony, names: readonly string[]): PawnSim[] {
    const out = names.map((n) => c.pawns.get(n)).filter((p): p is PawnSim => !!p);
    if (out.length === 0) throw Object.assign(new Error('no such pawns'), { status: 400 });
    return out;
  }

  /** POST …/priorities: cells (a pawn with a role turns Custom, values kept) or a role (null = Custom). */
  priorities(
    id: string,
    body: { pawns: string[]; work: Partial<Record<string, number>> } | { pawns: string[]; role: string | null },
  ) {
    const c = this.colony(id);
    const role = 'role' in body && body.role ? c.roles.find((r) => r.id === body.role) : null;
    if ('role' in body && body.role && !role)
      throw Object.assign(new Error(`role: no such role '${body.role}'`), { status: 404 });
    for (const p of this.pawnsOf(c, body.pawns)) {
      if ('work' in body) {
        p.p.role = null;
        Object.assign(p.p.priorities, body.work);
      } else {
        p.p.role = role?.id ?? null;
        if (role) applyRole(p.p, role);
      }
      p.rev = this.bump(c);
    }
    return { ok: true, rev: c.rev };
  }

  /** POST …/roles: add, update (its pawns follow), remove (its pawns turn Custom), reset_defaults. */
  roles(id: string, body: StoryRolesRequest) {
    const c = this.colony(id);
    const pawns = [...c.pawns.values()];
    const out = rolesOp(
      c.roles,
      pawns.map((p) => p.p),
      body,
    );
    for (const p of pawns) p.rev = this.bump(c);
    return {
      ...out,
      roles: rolesState(
        c.roles,
        pawns.map((p) => p.p),
      ),
    };
  }

  schedule(id: string, body: { pawns: string[]; hours: Partial<Record<string, string>> }) {
    const c = this.colony(id);
    for (const p of this.pawnsOf(c, body.pawns)) {
      for (const [h, v] of Object.entries(body.hours)) if (v) p.p.schedule[Number(h)] = v;
      p.rev = this.bump(c);
    }
    return { ok: true, rev: c.rev };
  }

  draft(id: string, body: { pawns: string[]; drafted: boolean }) {
    const c = this.colony(id);
    for (const p of this.pawnsOf(c, body.pawns)) {
      if (p.p.drafted !== body.drafted) draftGear(c.gear, p.p, body.drafted);
      p.p.drafted = body.drafted;
      this.drop(c, p);
      if (!body.drafted) p.p.queue = [];
      p.p.state = body.drafted ? 'drafted' : 'idle';
      p.rev = this.bump(c);
    }
    return { ok: true, rev: c.rev };
  }

  orders(id: string, body: StoryOrderRequest) {
    const c = this.colony(id);
    const pawns = this.pawnsOf(c, body.pawns);
    if (body.order.kind === 'attack') checkAttack(this.attackTarget(c, body.order.target), body.order.confirm);
    pawns.forEach((p, i) => {
      const o: StoryOrder = {
        id: `o${c.seq++}`,
        kind: body.order.kind,
        target: body.order.target,
        design: body.order.design ?? null,
        pawn: p.p.name,
        state: 'queued',
        at: this.iso(),
      };
      // A group move fans out around the spot so the pawns do not stack.
      if (
        o.kind === 'move' &&
        o.target &&
        typeof o.target === 'object' &&
        !Array.isArray(o.target) &&
        'x' in o.target &&
        pawns.length > 1
      ) {
        const t = o.target as { x: number; y: number; z: number };
        o.target = { x: t.x + ((i % 3) - 1), y: t.y, z: t.z + (Math.floor(i / 3) - 1) };
      }
      if (o.kind === 'stop') {
        p.p.queue = [];
        this.drop(c, p);
      } else {
        if (!body.queue) {
          p.p.queue = [];
          this.drop(c, p);
        }
        p.p.queue.push(o);
      }
      c.orders.set(o.id!, { rev: this.bump(c), v: o });
      p.rev = c.rev;
    });
    return { ok: true, rev: c.rev };
  }

  zones(id: string, body: StoryZoneRequest): ZonesAnswer {
    const c = this.colony(id);
    const edited = this.editZone(c, body);
    if (edited !== undefined) return edited;
    return this.paintOrErase(c, body);
  }

  /** The ops on one zone: a mine zone's mode, a growing zone's settings and seeds, irrigation, its bills, its name. */
  private editZone(c: Colony, body: StoryZoneRequest) {
    if (body.op === 'update' && body.mode) return this.zoneMode(c, body.id, body.mode);
    if (body.op === 'update' || body.op === 'seek_seeds') return this.growOp(c, body);
    if (body.op === 'irrigation') return this.irrigation(c, String(body.id));
    if (body.op.startsWith('bill_')) return this.billOp(c, body as Extract<StoryZoneRequest, { op: `bill_${string}` }>);
    if (body.op === 'rename') return this.renameZone(c, body.id, body.name);
    return undefined;
  }

  /** A mine zone's mode: quarry (everything to bedrock, a stair) or ores only. */
  private zoneMode(c: Colony, id: Key, mode: 'ores' | 'quarry') {
    const f = c.zones.get(String(id));
    if (!f) throw Object.assign(new Error('id: no such zone'), { status: 404 });
    if (f.v.kind !== 'mine') throw Object.assign(new Error('only mine zones take a mode (ores | quarry)'), { status: 400 });
    if (mode === 'quarry' && !quarryRing(zoneCellSet(f.v))) throw Object.assign(new Error(QUARRY_TOO_NARROW), { status: 400 });
    f.v = { ...f.v, mode };
    f.rev = this.bump(c);
    return { id: f.v.id, rev: c.rev };
  }

  private renameZone(c: Colony, id: Key, name: string) {
    const f = c.zones.get(String(id));
    if (!f) throw Object.assign(new Error('no such zone'), { status: 404 });
    f.v = { ...f.v, name: name || null };
    f.rev = this.bump(c);
    return { id: f.v.id, name: f.v.name ?? '', rev: c.rev };
  }

  /** Deletes a zone by id, erases an area's cells (of a kind, or any), or paints an area. */
  private paintOrErase(c: Colony, body: StoryZoneRequest) {
    const list = [...c.zones.values()].map((f) => f.v);
    if (body.op === 'remove' && 'id' in body) {
      if (!c.zones.has(String(body.id))) throw Object.assign(new Error('no such zone'), { status: 404 });
      this.setZones(
        c,
        list.filter((z) => String(z.id) !== String(body.id)),
      );
      return { removed: [body.id], deleted: [body.id], rev: c.rev };
    }
    if (!('area' in body)) throw Object.assign(new Error('area must be [x1,y1,z1,x2,y2,z2]'), { status: 400 });
    const a = normArea(body.area);
    const rect = [a[0], a[2], a[3], a[5]] as const;
    const newId = () => `z${c.seq++}`;
    if (body.op === 'remove') {
      const out = eraseZones(list, body.kind, rect, newId);
      this.setZones(c, out.zones);
      return { ...out.result, removed: out.result.deleted, rev: c.rev };
    }
    if (body.op !== 'add')
      throw Object.assign(new Error('op must be add|remove|rename|update|seek_seeds|bill_*'), { status: 400 });
    return this.addZone(c, body, list, a, rect, newId);
  }

  /** Paints an area as a zone (RimWorld-style: joins, merges, overwrites; `into` extends one zone). */
  private addZone(
    c: Colony,
    body: Extract<StoryZoneRequest, { op: 'add' }>,
    list: StoryZone[],
    a: StoryArea,
    rect: readonly [number, number, number, number],
    newId: () => string,
  ) {
    const into = this.zoneInto(c, body.into);
    const kind = into?.kind ?? body.kind ?? 'home';
    if (!into) checkNewZone(body, kind, list, rect);
    const water = kind === 'fish' ? this.fishWater(rect, a, list) : null;
    const make = (k: string, plant: string | null): StoryZone =>
      into ? { ...into, id: newId(), name: null, rows: null } : newZone(c, body, a, newId, k, plant);
    const out = paintZones(
      list,
      { kind, plant: body.plant ?? null, rect, y: [a[1], a[4]], into: body.into, cells: water },
      make,
      newId,
    );
    this.setZones(c, out.zones);
    return { ...out.result, removed: out.result.deleted, rev: c.rev };
  }

  /** The zone an `into` names (400 when there is none). */
  private zoneInto(c: Colony, into: Key | undefined): StoryZone | undefined {
    const z = into !== undefined ? c.zones.get(String(into))?.v : undefined;
    if (into !== undefined && !z) throw Object.assign(new Error('into: no such zone'), { status: 400 });
    return z;
  }

  /** A fishing zone keeps only still water (a source block, open above, near the level); it needs 4 cells at least. */
  private fishWater(rect: readonly [number, number, number, number], a: StoryArea, list: readonly StoryZone[]): Set<number> {
    const water = new Set(
      [...rectCells(rect[0], rect[1], rect[2], rect[3])].filter((k) => this.stillWaterAt(cellX(k), cellZ(k), a[1])),
    );
    if (water.size + list.filter((z) => z.kind === 'fish').reduce((n, z) => n + (z.cells ?? 0), 0) < 4)
      throw Object.assign(new Error('a fishing zone needs at least 4 water cells'), { status: 400 });
    return water;
  }

  private stillWaterAt(x: number, z: number, level: number): boolean {
    for (let y = level + 3; y >= level - 4; y--) {
      const [id, meta] = this.opts.world.blockAt(x, y, z);
      if (id === 8 || id === 9) return meta === 0;
      if (id !== 0) return false;
    }
    return false;
  }

  /** The colony's zones become `next` (in order): unchanged objects keep their revision; changed and new ones are bumped. */
  private setZones(c: Colony, next: StoryZone[]) {
    const old = new Map(c.zones);
    const keep = new Set(next.map((z) => String(z.id)));
    let homes = false;
    for (const [k, f] of old)
      if (!keep.has(String(k))) {
        c.removed.push({ rev: this.bump(c), kind: 'zones', key: k });
        homes ||= f.v.kind === 'home';
      }
    c.zones.clear();
    for (const z of next) {
      const f = old.get(String(z.id));
      if (f && f.v === z) c.zones.set(String(z.id), f);
      else {
        c.zones.set(String(z.id), { rev: this.bump(c), v: z });
        homes ||= z.kind === 'home';
      }
    }
    if (homes) c.bedsRev = this.bump(c); // beds belong to the house they stand in
  }

  /** A planting bill met? until: the produce in stock; count: harvested (the mock grows nothing). */
  private billMet(c: Colony, b: StoryGrowBill): boolean {
    if (b.mode === 'count') return (b.done ?? 0) >= b.target;
    if (b.mode === 'until') return this.produceStored(c, b.plant) >= b.target;
    return false;
  }

  private produceStored(c: Colony, plant: string): number {
    const item = SIM_PRODUCE[plant];
    if (!item) return 0;
    let n = 0;
    for (const [k, v] of c.stock) if (k.startsWith(`${item}:`)) n += v;
    return n;
  }

  private activeBill(c: Colony, z: StoryZone): StoryGrowBill | null {
    return (
      z.bills?.find((b) => !b.paused && STORY_SIM_PLANTS.some((p) => p.id === b.plant) && !this.billMet(c, b)) ?? null
    );
  }

  /** A growing zone's planting bills: add, update, remove, pause, resume, reorder (docs/web/story-mode.md "Farming"). */
  private billOp(c: Colony, body: BillRequest) {
    const f = c.zones.get(String(body.id));
    if (!f) throw Object.assign(new Error('id: no such zone'), { status: 404 });
    if (f.v.kind !== 'farm')
      throw Object.assign(new Error('only farm (growing) zones take planting bills'), { status: 400 });
    const { bills, made } = applyBillOp([...(f.v.bills ?? [])], body, () => `gb${c.seq++}`);
    f.v = { ...f.v, bills, plant: bills[0]?.plant ?? null };
    f.rev = this.bump(c);
    const active = this.activeBill(c, f.v);
    return {
      id: f.v.id,
      ids: bills.map((b) => b.id),
      activeBill: active?.id ?? null,
      warnings: [],
      ...(made ? { bill: made } : {}),
      rev: c.rev,
    };
  }

  /** A quarry's progress as the bot reports it (mock: nothing is dug yet; bedrock at y 0..2). */
  private quarryStatus(z: StoryZone): StoryZone {
    const mode = z.mode ?? 'ores';
    if (mode !== 'quarry') return { ...z, mode };
    const ring = quarryRing(zoneCellSet(z));
    const top = z.area[1],
      bottom = 3;
    const stair = ring ? Array.from({ length: Math.max(0, top - bottom + 1) }, (_, k) => quarryStep(ring, top, k)) : [];
    const cells = z.cells ?? zoneCellSet(z).size;
    return {
      ...z,
      mode,
      quarry: {
        top,
        bottom,
        depth: top - bottom + 1,
        layer: 0,
        y: top,
        cellsLeft: Math.max(0, cells - 1),
        stairFixes: 0,
        sealed: 0,
        done: false,
        stair,
        status: `layer 1 of ${top - bottom + 1} (y ${top}), ${Math.max(0, cells - 1)} cell(s) left`,
      },
    };
  }

  /** A growing zone's fields as the bot reports them (mock: nothing grows; seeds from the colony's stock). */
  private growStatus(c: Colony, z: StoryZone): StoryZone {
    if (z.kind === 'mine') return this.quarryStatus(z);
    if (!isGrowing(z)) return z;
    const cells = zoneCellCount(z);
    const active = z.bills ? this.activeBill(c, z) : null;
    const plant = sownPlant(z, active);
    const base = { ...this.withBillRows(c, z, active, cells), activePlant: plant?.id ?? null };
    return plant ? sowingStatus(base, c.stock, plant, cells) : idleGrowStatus(base, z.bills?.length ?? 0);
  }

  /** The zone with its bills as rows and the active bill's id (a zone without bills as it is). */
  private withBillRows(c: Colony, z: StoryZone, active: StoryGrowBill | null, cells: number): StoryZone {
    if (!z.bills) return { ...z };
    return { ...z, bills: z.bills.map((b) => this.growBillRow(c, b, active, cells)), activeBill: active?.id ?? null };
  }

  /** A planting bill as the zone reports it: its label, met / active, the stored produce (until), its seeds. */
  private growBillRow(c: Colony, b: StoryGrowBill, active: StoryGrowBill | null, cells: number) {
    const p = STORY_SIM_PLANTS.find((x) => x.id === b.plant);
    return {
      ...b,
      label: p?.label ?? b.plant,
      met: this.billMet(c, b),
      active: b === active,
      ...(b.mode === 'until' ? { stored: this.produceStored(c, b.plant) } : {}),
      ...(p ? { seeds: seedsOf(c.stock, p, b === active ? cells : 0) } : {}),
    };
  }

  /** zones irrigation (docs/web/story-mode.md "Water"): the channels a growing zone wants, at soil level; nothing queued. */
  private irrigation(c: Colony, id: string) {
    const z = c.zones.get(id)?.v;
    if (!z) throw Object.assign(new Error('id: no such zone'), { status: 404 });
    if (z.kind !== 'farm') throw Object.assign(new Error('irrigation is for growing zones'), { status: 400 });
    const w = this.opts.world;
    const water = (x: number, y: number, z2: number) => {
      const id2 = w.blockAt(x, y, z2)[0];
      return id2 === 8 || id2 === 9;
    };
    const cane = (z.activePlant ?? z.plant) === 'sugar_cane';
    const plan = irrigationPlan(
      z,
      cane,
      (x, z2) => w.ground(x, z2),
      (x, y, z2) => {
        const r = cane ? 1 : 4;
        for (let dx = -r; dx <= r; dx++)
          for (let dz = -r; dz <= r; dz++)
            if (cane ? Math.abs(dx) + Math.abs(dz) === 1 : true)
              for (let dy = 0; dy <= (cane ? 0 : 1); dy++) if (water(x + dx, y + dy, z2 + dz)) return true;
        return false;
      },
      (x, y, z2) => w.blockAt(x, y, z2)[0],
    );
    return { id: z.id, ...plan, rev: c.rev };
  }

  private growOp(c: Colony, body: Extract<StoryZoneRequest, { op: 'update' | 'seek_seeds' }>) {
    const f = c.zones.get(String(body.id));
    if (!f) throw Object.assign(new Error('id: no such zone'), { status: 404 });
    return body.op === 'update' ? this.updateGrowZone(c, f, body) : this.seekSeeds(c, f);
  }

  /** zones update: a growing zone's plant (its first bill's), sowing and harvesting. */
  private updateGrowZone(c: Colony, f: Fn<StoryZone>, body: Extract<StoryZoneRequest, { op: 'update' }>) {
    if (f.v.kind !== 'farm')
      throw Object.assign(new Error('only farm (growing) zones take plant, sow and harvest'), { status: 400 });
    if (body.plant) knownPlant(body.plant);
    f.v = {
      ...f.v,
      ...(body.plant !== undefined
        ? { plant: body.plant || null, bills: plantBills(f.v.bills, body.plant, () => `gb${c.seq++}`) }
        : {}),
      ...(body.sow !== undefined ? { sow: body.sow } : {}),
      ...(body.harvest !== undefined ? { harvest: body.harvest } : {}),
    };
    f.rev = this.bump(c);
    return { id: f.v.id, warnings: [], rev: c.rev };
  }

  /** zones seek_seeds: wheat seeds come from cutting tall grass around the zone; a fruit's from a crafting bill. */
  private seekSeeds(c: Colony, f: Fn<StoryZone>) {
    const plant = STORY_SIM_PLANTS.find((p) => p.id === (this.activeBill(c, f.v)?.plant ?? f.v.plant));
    if (!plant) throw Object.assign(new Error('the zone grows nothing: set its plant first'), { status: 400 });
    if (plant.id === 'wheat') {
      const [x1, y1, z1, x2, , z2] = f.v.area;
      const out = this.designations(c.id, {
        op: 'add',
        kind: 'cut',
        area: [x1 - 24, y1 - 6, z1 - 24, x2 + 24, y1 + 6, z2 + 24],
      } as never) as { count?: number };
      return { count: out.count ?? 0, kind: 'cut', rev: c.rev };
    }
    if (plant.harvest === 'fruit') {
      this.alert(c, 'info', `Added a bill for 4 ${plant.seed} at the crafting table.`);
      return { count: 4, kind: 'bill', rev: c.rev };
    }
    throw Object.assign(new Error(SEED_HINT[plant.id] ?? `No ${plant.label.toLowerCase()} to sow — ${plant.source}`), {
      status: 400,
    });
  }

  // ------------------------------------------------------------------ pawns (+ / −)

  /** POST /api/story/colonies/{id}/pawns: + joins as seats allow (the rest wait), − stores its things and leaves. */
  pawns(id: string, body: StoryPawnsRequest): StoryPawnsResponse {
    const c = this.colony(id);
    if (body.op === 'add') return this.pawnsAnswer(c, this.addPawns(c, body.count));
    const p = this.pawnNamed(c, body.pawn);
    if (!p.p.leaving) this.removePawn(c, p);
    return this.pawnsAnswer(c, null);
  }

  /** The pawns that are not leaving. */
  private staying(c: Colony) {
    return [...c.pawns.values()].filter((p) => !p.p.leaving);
  }

  /** The pawns route's answer: who is here, wanted, the seats still waiting (and why), who is leaving. */
  private pawnsAnswer(c: Colony, why: string | null): StoryPawnsResponse {
    const here = this.staying(c).map((p) => p.p.name);
    const pending = Math.max(0, (c.wanted ?? here.length) - here.length);
    return {
      pawns: here,
      wanted: c.wanted ?? here.length,
      pending: Array.from({ length: pending }, () => ({ reason: why ?? 'waiting for a seat' })),
      leaving: [...c.pawns.values()].filter((p) => p.p.leaving).map((p) => p.p.name),
      blocked: why && pending ? [{ reason: why }] : [],
    };
  }

  /** pawns add: up to 12 wanted; as many join now as the seats allow (the rest wait); why some wait (null: none do). */
  private addPawns(c: Colony, count: number): string | null {
    if (c.released) throw Object.assign(new Error('the colony is released: resume it first'), { status: 400 });
    const wanted = Math.max(c.wanted ?? 0, this.staying(c).length);
    if (wanted + count > 12) throw Object.assign(new Error(`a colony has at most 12 pawns (${wanted} now)`), { status: 400 });
    c.wanted = wanted + count;
    const now = Math.max(0, Math.min(count, c.capFree - c.kept));
    for (let i = 0; i < now; i++) this.joinPawn(c);
    c.capFree -= now;
    const why = now < count ? this.capReason(c) : null;
    this.alert(c, 'info', `${count} pawn${count === 1 ? '' : 's'} joining${why ? ` (${count - now} wait: ${why})` : ''}.`);
    return why;
  }

  /** A new pawn, connecting. */
  private joinPawn(c: Colony): void {
    const k = c.pawns.size;
    const display = NAMES[(k + 5) % NAMES.length]!;
    const name = `maid_${c.id.replace('story-', 's')}_${display.toLowerCase()}${c.seq++}`;
    const p = this.newPawn(c, name, display, k);
    p.p.online = false;
    p.p.state = 'offline';
    p.p.offlineReason = 'connecting';
    p.backAt = this.now() + 1500;
    p.rev = this.bump(c);
    c.pawns.set(name, p);
  }

  /** pawns remove: the pawn leaves (its things go to storage first; its bed is free); gone 1.5 s later. Not the last one. */
  private removePawn(c: Colony, p: ReturnType<StorySim['pawnNamed']>): void {
    if (this.staying(c).length <= 1)
      throw Object.assign(new Error("the last pawn can't be removed: release the colony instead"), { status: 400 });
    this.drop(c, p);
    p.p.leaving = true;
    p.rev = this.bump(c);
    c.wanted = Math.max(this.staying(c).length, (c.wanted ?? this.staying(c).length + 1) - 1);
    for (const [k, b] of c.beds) if (b.pawn === p.p.name) c.beds.set(k, { ...b, pawn: null });
    c.bedsRev = this.bump(c);
    this.alert(c, 'info', `${p.p.display} is leaving the colony: its things go to storage first.`, p.p.name);
    setTimeout(() => {
      if (!c.pawns.delete(p.p.name)) return;
      c.removed.push({ rev: this.bump(c), kind: 'pawns', key: p.p.name });
      this.alert(c, 'info', `${p.p.display} left the colony.`);
    }, 1500);
  }


  /** Every target of a block tool in an area (capped). */
  private scan(
    a: StoryArea,
    kind: StoryDesignationKind,
    filter: StoryDesignationRequest['filter'],
  ): Array<[number, number, number]> {
    const w = this.opts.world;
    const out: Array<[number, number, number]> = [];
    const [x1, y1, z1, x2, y2, z2] = a;
    if ((x2 - x1 + 1) * (z2 - z1 + 1) > 128 * 128) return out;
    for (let x = x1; x <= x2; x++)
      for (let z = z1; z <= z2; z++)
        for (let y = Math.max(1, y1); y <= Math.min(255, y2); y++) {
          const [id, meta] = w.blockAt(x, y, z);
          if (id === 0) continue;
          if (matchesTool(kind, id, meta, w.blockAt(x, y - 1, z)[0], filter ?? 'all')) out.push([x, y, z]);
          if (out.length >= 4096) return out;
        }
    return out;
  }

  designations(id: string, body: StoryDesignationRequest): DesignationsAck {
    const c = this.colony(id);
    const a = body.area ? normArea(body.area) : null;
    if (body.op === 'retry') return this.retryDesignations(c, body.ids);
    if (body.op === 'remove' || body.op === 'cancel') return this.removeDesignations(c, body, a);
    const kind = body.kind ?? 'mine';
    const held = new Set(
      [...c.designations.values()].flatMap((f) => (f.v.targets ?? (f.v.block ? [f.v.block] : [])).map(keyOf)),
    );
    if (designatesEntities(kind, body)) return this.designateEntities(c, body, kind, a, held);
    return this.designateBlocks(c, body, kind, a, held);
  }

  /** The failed designations named by `ids` are tried again now (their rest is over). */
  private retryDesignations(c: Colony, idList: StoryDesignationRequest['ids']): DesignationsAck {
    const ids = (idList ?? []).map(String);
    if (!ids.length) throw Object.assign(new Error('ids required'), { status: 400 });
    const retried: Key[] = [];
    for (const [k, f] of c.designations) {
      if (!ids.includes(String(k))) continue;
      f.v = { ...f.v, status: 'open', retryAt: null, state: 'todo' };
      f.rev = this.bump(c);
      retried.push(k);
    }
    return { ok: true, count: retried.length, retried, rev: c.rev } as never;
  }

  /** Cancels the designations (of a kind, if given) in the area or named by ids, blocks or entities; their pawns let go. */
  private removeDesignations(c: Colony, body: StoryDesignationRequest, a: StoryArea | null): DesignationsAck {
    let n = 0;
    for (const [k, f] of c.designations) {
      const d = f.v;
      if (body.kind && d.kind !== body.kind) continue;
      const cells = d.targets ?? (d.block ? [d.block] : []);
      if (!this.designationInArea(d, cells, a) && !designationListed(d, k, cells, body)) continue;
      c.designations.delete(k);
      c.removed.push({ rev: this.bump(c), kind: 'designations', key: k });
      for (const p of c.pawns.values()) if (p.task?.ref === k) this.drop(c, p);
      n++;
    }
    return { ok: true, count: n, rev: c.rev };
  }

  private designationInArea(d: StoryDesignation, cells: ReadonlyArray<readonly number[]>, a: StoryArea | null): boolean {
    if (!a) return false;
    return cells.some((p) => inside(a, p)) || (d.entity !== null && d.entity !== undefined && this.entityIn(d.entity, a));
  }

  private addDesignation(c: Colony, d: Omit<StoryDesignation, 'id'>): void {
    const des: StoryDesignation = { id: `d${c.seq++}`, ...d };
    c.designations.set(des.id, { rev: this.bump(c), v: des });
  }

  /** Haul (drops), hunt (animals), forbid / allow over an area (drops, and doors too) or by entities; held ones skipped. */
  private designateEntities(
    c: Colony,
    body: StoryDesignationRequest,
    kind: StoryDesignationKind,
    a: StoryArea | null,
    held: ReadonlySet<string>,
  ): DesignationsAck {
    const heldEnt = new Set(
      [...c.designations.values()].map((f) => f.v.entity).filter((e) => e !== null && e !== undefined),
    );
    const ents = this.opts.world
      .entities()
      .filter((e) => (kind === 'hunt' ? e.kind === 'mob' && ANIMAL_TYPES.has(e.type ?? -1) : e.kind === 'item'))
      .filter((e) => (a ? inside(a, [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], 2) : body.entities?.includes(e.id)));
    const fresh = ents.filter((e) => !heldEnt.has(e.id));
    for (const e of fresh)
      this.addDesignation(c, { kind, entity: e.id, block: [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], state: 'todo' });
    // Doors too, for forbid/allow over an area.
    const doors = a && (kind === 'forbid' || kind === 'allow') ? this.scan(a, kind, undefined) : [];
    for (const b of doors) if (!held.has(keyOf(b))) this.addDesignation(c, { kind, block: b, state: 'todo' });
    return {
      ok: true,
      count: fresh.length + doors.length,
      entities: fresh.map((e) => e.id),
      targets: doors,
      rev: c.rev,
    };
  }

  /** A block tool over an area (scanned) or blocks; chop is one designation per tree (its touching log columns). */
  private designateBlocks(
    c: Colony,
    body: StoryDesignationRequest,
    kind: StoryDesignationKind,
    a: StoryArea | null,
    held: ReadonlySet<string>,
  ): DesignationsAck {
    const cells = a
      ? this.scan(a, kind, body.filter)
      : (body.blocks ?? []).map((b) => [b[0], b[1], b[2]] as [number, number, number]);
    const fresh = cells.filter((p) => !held.has(keyOf(p)));
    if (kind === 'chop') {
      // One designation per tree: its log columns that touch, lowest log first.
      const byTree = groupTrees(fresh);
      for (const logs of byTree) this.addDesignation(c, { kind, block: logs[0], targets: logs, state: 'todo' });
      return { ok: true, count: byTree.length, targets: fresh, rev: c.rev };
    }
    const design = body.design !== null && body.design !== undefined ? { design: body.design } : {};
    for (const b of fresh) this.addDesignation(c, { kind, block: b, state: 'todo', ...design });
    return { ok: true, count: fresh.length, targets: fresh, rev: c.rev };
  }

  private entityIn(id: number, a: StoryArea): boolean {
    const e = this.opts.world.entities().find((x) => x.id === id);
    return !!e && inside(a, [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], 2);
  }

  /**
   * Water items (the director's checks, simplified): lava only while allowed; each cell open (air, plants, flowing liquid) or
   * dug first by its group's remove item; the expected flow beyond the drawn cells. Returns why not, or the warnings and flow.
   */
  private checkWater(c: Colony, items: readonly StoryBuildItem[]): WaterCheck {
    const r: WaterCheck = { error: null, warnings: [], spread: [], cells: 0, done: 0 };
    const dug: Array<[number, number, number]> = [];
    const queuedDigs = [...c.ghostCells.values()].flat().filter((g) => g[7] === 'dig' && g[6] !== 'done');
    for (const item of items) {
      if (item.kind === 'remove' && item.group) for (const b of expandBuildItem(item)) dug.push([b[0], b[1], b[2]]);
      if (item.kind !== 'water') continue;
      const error = this.checkWaterItem(c, item, dug, queuedDigs, r);
      if (error) return { error, warnings: r.warnings, spread: r.spread, cells: r.cells, done: r.done };
    }
    return r;
  }

  /** One water item: its liquid allowed, its cells open or dug (counting those already full), its flow; why not, or null. */
  private checkWaterItem(
    c: Colony,
    item: StoryBuildItem,
    dug: Array<[number, number, number]>,
    queuedDigs: readonly GhostCell[],
    r: WaterCheck,
  ): string | null {
    const liquid = liquidOf(item.material);
    if (!liquid) return "a water item's material is water|lava";
    if (liquid === 'lava' && !c.options.allowLava) return 'lava is off: turn on the colony option allow_lava first';
    const own = expandBuildItem(item).map((b) => [b[0], b[1], b[2]] as [number, number, number]);
    r.cells += own.length;
    const blocked = this.unopenCell(own, liquid, dug, queuedDigs, r);
    if (blocked) return blocked;
    const f = liquidFlow((x, y, z) => this.opts.world.blockAt(x, y, z), own, liquid === 'lava', dug);
    r.spread.push(...f.spread);
    if (f.spread.length)
      r.warnings.push(
        `the ${liquid} will spread to ${f.spread.length} cell${f.spread.length === 1 ? '' : 's'} beyond what was drawn (e.g. ${f.spread[0]!.join(',')}): place dam blocks first, or dig the channel into the ground`,
      );
    if (f.touches) r.warnings.push(f.touches);
    return null;
  }

  /** The first of a water item's cells it can't pour into (solid and not dug first), as the reason; cells already holding a
   * source of the liquid count as done. */
  private unopenCell(
    own: ReadonlyArray<[number, number, number]>,
    liquid: 'water' | 'lava',
    dug: ReadonlyArray<[number, number, number]>,
    queuedDigs: readonly GhostCell[],
    r: WaterCheck,
  ): string | null {
    const want = liquid === 'lava' ? [10, 11] : [8, 9];
    for (const [x, y, z] of own) {
      const [bid, bm] = this.opts.world.blockAt(x, y, z);
      if (want.includes(bid) && bm === 0) {
        r.done++;
        continue;
      }
      const open = bid === 0 || [8, 9, 10, 11, 31, 32, 37, 38, 78, 106, 175].includes(bid);
      if (!open && !isDugCell(x, y, z, dug, queuedDigs))
        return `${x},${y},${z} holds ${storyItemLabel(bid, bm).toLowerCase()}: dig it first (a remove item in the same group, or the channel tool's "Dig the trench")`;
    }
    return null;
  }

  builds(id: string, body: StoryBuildRequest): BuildsAck {
    const c = this.colony(id);
    if (body.op === 'retry' || body.op === 'retry_all') return this.retryBuilds(c, body);
    if (body.op === 'preview') {
      const r = this.checkWater(c, body.items);
      return {
        ok: true,
        rev: c.rev,
        cells: r.cells,
        done: r.done,
        error: r.error,
        warnings: r.warnings,
        flow: { spread: r.spread.length, cells: r.spread.slice(0, 256) },
      };
    }
    if (body.op === 'add') return this.addBuilds(c, body.items);
    const ids = body.ids.map(String);
    if (body.op === 'reorder') return this.reorderBuilds(c, ids);
    for (const bid of ids) {
      const f = c.builds.get(bid);
      if (!f) continue;
      if (body.op === 'remove') this.removeBuild(c, bid);
      else {
        f.v.status = body.op === 'pause' ? 'paused' : 'queued';
        f.rev = this.bump(c);
      }
    }
    c.ghostsRev = this.bump(c);
    this.plan(c);
    return { ok: true, rev: c.rev };
  }

  /** Failed (blocked) cells start over: all of a build's, the given ones, or every build's. */
  private retryBuilds(c: Colony, body: Extract<StoryBuildRequest, { op: 'retry' | 'retry_all' }>): BuildsAck {
    const want =
      body.op === 'retry' ? new Set([body.id, ...(body.ids ?? [])].filter((x) => x !== undefined).map(String)) : null;
    const cells = body.op === 'retry' && body.cells ? new Set(body.cells.map((p) => p.join(','))) : null;
    if (want && ![...c.builds.keys()].some((k) => want.has(String(k))))
      throw Object.assign(new Error('no such build'), { status: 400 });
    let reopened = 0;
    const ids: Key[] = [];
    for (const [bid, list] of c.ghostCells) {
      if (want && !want.has(String(bid))) continue;
      ids.push(bid);
      reopened += reopenCells(list, cells);
      const f = c.builds.get(bid);
      if (f && f.v.status === 'blocked') {
        f.v.status = 'queued';
        f.v.reason = null;
        f.rev = this.bump(c);
      }
    }
    c.ghostsRev = this.bump(c);
    this.plan(c);
    return { ok: true, ids, rev: c.rev, reopened, reasons: [] };
  }

  private addBuilds(c: Colony, items: readonly StoryBuildItem[]): BuildsAck {
    const ids: Key[] = [];
    const water = items.some((i) => i.kind === 'water') ? this.checkWater(c, items) : null;
    if (water?.error) throw Object.assign(new Error(water.error), { status: 400 });
    for (const item of items) ids.push(this.addBuild(c, item, items));
    c.ghostsRev = this.bump(c);
    this.plan(c);
    return {
      ok: true,
      ids,
      rev: c.rev,
      ...(water?.warnings.length ? { warnings: water.warnings } : {}),
      ...(water ? { flow: { spread: water.spread.length, cells: water.spread.slice(0, 256) } } : {}),
    };
  }

  /** Queues one build: its label, what it is short of, and its ghost cells (a pour with nothing under it fails at once). */
  private addBuild(c: Colony, item: StoryBuildItem, items: readonly StoryBuildItem[]): Key {
    const bid = `b${c.seq++}`;
    const cells = expandBuildItem(item);
    const [mid, mmeta] = materialKey(item.material);
    const order = Math.max(0, ...[...c.builds.values()].map((f) => f.v.order)) + 1;
    const need = cells.filter((b) => b[3] !== 0).length;
    const have = c.stock.get(`${mid}:${mmeta}`) ?? 0;
    const liquid = item.kind === 'water' ? liquidOf(item.material) : null;
    const b: StoryBuild = {
      id: bid,
      kind: item.kind,
      label: liquid ? waterBuildLabel(item, liquid, cells.length) : buildLabel(item.kind, cells.length, item.material),
      status: 'queued',
      ...(liquid ? { liquid, renewable: false } : {}),
      reason: lacksMaterial(item, need, have) ? `needs ${need - have} more ${storyItemLabel(mid, mmeta).toLowerCase()}` : null,
      blocksTotal: cells.length,
      blocksDone: 0,
      material: item.material,
      order,
      created: this.iso(),
      materials: [],
    };
    c.builds.set(bid, { rev: this.bump(c), v: b });
    c.ghostCells.set(
      bid,
      cells.map((cell) => this.ghostOf(cell, cells, bid, item, items, liquid)),
    );
    return bid;
  }

  /** A pour cell with nothing under it (open ground, not dug by its group, not another cell): the mock's pawns fail it as the
   * bot's did before its queue-time check, so the Retry tool and "Retry failed cells" have something to show. */
  private ghostOf(
    [x, y, z, bi, bm]: [number, number, number, number, number],
    cells: ReadonlyArray<[number, number, number, number, number]>,
    bid: Key,
    item: StoryBuildItem,
    items: readonly StoryBuildItem[],
    liquid: 'water' | 'lava' | null,
  ): GhostCell {
    const below = this.opts.world.blockAt(x, y - 1, z)[0];
    const own = cells.some((o) => o[0] === x && o[1] === y - 1 && o[2] === z);
    const dug = items.some(
      (i) =>
        i.kind === 'remove' &&
        i.group &&
        i.group === item.group &&
        expandBuildItem(i).some((d) => d[0] === x && d[1] === y - 1 && d[2] === z),
    );
    if (liquid && !own && (dug || [0, 8, 9, 10, 11, 31, 32, 37, 38, 78, 106, 175].includes(below)))
      return [x, y, z, bi, bm, bid, 'blocked', 'pour', `nothing under ${x},${y},${z} to hold the ${liquid}: it would pour down`];
    return [x, y, z, bi, bm, bid, 'todo', bi === 0 ? 'dig' : liquid ? 'pour' : 'place'];
  }

  /** The listed builds take the front of the queue in the listed order; the rest keep theirs after them. */
  private reorderBuilds(c: Colony, ids: string[]): BuildsAck {
    const rest = [...c.builds.values()]
      .map((f) => f.v)
      .filter((b) => !ids.includes(String(b.id)))
      .sort((x, y) => x.order - y.order);
    const front = ids.map((i) => c.builds.get(i)?.v).filter((b): b is StoryBuild => !!b);
    [...front, ...rest].forEach((b, i) => {
      b.order = i + 1;
      c.builds.set(b.id, { rev: this.bump(c), v: b });
    });
    this.plan(c);
    return { ok: true, rev: c.rev };
  }

  private removeBuild(c: Colony, bid: string): void {
    c.builds.delete(bid);
    c.ghostCells.delete(bid);
    c.removed.push({ rev: this.bump(c), kind: 'builds', key: bid });
    for (const p of c.pawns.values()) if (p.task?.ref === bid) this.drop(c, p);
  }

  /** Registers the chests and workbenches that stand in an area. */
  private discover(
    c: Colony,
    area: StoryArea,
    filter?: StoryFilter,
    priority?: string,
  ): Array<[number, number, number]> {
    const a = normArea(area);
    const found: Array<[number, number, number]> = [];
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++)
        for (let y = Math.max(1, a[1]); y <= Math.min(255, a[4]); y++) this.noteFurniture(c, x, y, z, found);
    if (filter === undefined && priority === undefined) return [];
    const out: Array<[number, number, number]> = [];
    const taken = new Set<string>();
    for (const p of found) {
      if (taken.has(keyOf(p))) continue;
      // Double chests are one storage: the other half is a chest next to it.
      const other = found.find((q) => q !== p && Math.abs(q[0] - p[0]) + Math.abs(q[2] - p[2]) === 1 && q[1] === p[1]);
      if (other) taken.add(keyOf(other));
      taken.add(keyOf(p));
      this.registerStorage(c, p, other, filter, priority);
      out.push(p);
    }
    return out;
  }

  /** A block of the area: a chest (into `found`), a bed's foot (a bed the colony knows), or a workbench (the colony's). */
  private noteFurniture(c: Colony, x: number, y: number, z: number, found: Array<[number, number, number]>): void {
    const w = this.opts.world;
    const [id] = w.blockAt(x, y, z);
    const benchKind = BENCH_KIND_OF.get(id);
    if (CHEST_IDS.has(id)) found.push([x, y, z]);
    else if (id === 26 && (w.blockAt(x, y, z)[1] & 8) === 0 && !c.beds.has(keyOf([x, y, z]))) {
      c.beds.set(keyOf([x, y, z]), { pos: [x, y, z], dim: 0, pawn: null, zone: null });
      c.bedsRev = this.bump(c);
    } else if (benchKind && !c.benches.has(keyOf([x, y, z]))) {
      c.benches.set(keyOf([x, y, z]), {
        rev: this.bump(c),
        v: {
          pos: [x, y, z],
          kind: benchKind,
          label: null,
          supported: true,
          station: this.stationOf(benchKind, [x, y, z]),
          ...this.fuelOf(benchKind),
          bills: [],
        },
      });
    }
  }

  /** A chest (and its other half) as a storage: its settings as given, else as it was, else the defaults. */
  private registerStorage(
    c: Colony,
    p: [number, number, number],
    other: [number, number, number] | undefined,
    filter: StoryFilter | undefined,
    priority: string | undefined,
  ): void {
    const k = keyOf(p);
    const prev = c.storages.get(k)?.v;
    const s: StoryStorage = {
      chest: p,
      doubleWith: other ?? null,
      label: prev?.label ?? null,
      ...storageSettings(prev, filter, priority),
      contents: prev?.contents ?? this.stock(c, seedContents(p)),
      freeSlots: prev?.freeSlots ?? (other ? 46 : 22),
      knownAt: this.iso(),
    };
    c.storages.set(k, { rev: this.bump(c), v: s });
  }

  storage(
    id: string,
    body: StoryStorageRequest,
  ): { ok: boolean; count: number; chests?: Array<[number, number, number]>; rev: number } {
    const c = this.colony(id);
    switch (body.op) {
      case 'assign_area': {
        const chests = this.discover(c, body.area, body.filter ?? undefined, body.priority ?? 'normal');
        return { ok: true, count: chests.length, chests, rev: c.rev };
      }
      case 'set':
        return { ok: true, count: this.setStorages(c, body), rev: c.rev };
      case 'copy':
        return { ok: true, count: this.copyStorage(c, body), rev: c.rev };
      case 'remove':
        return { ok: true, count: this.removeStorages(c, body.chests), rev: c.rev };
    }
  }

  /** The storage a chest belongs to (either half of a double chest). */
  private findStorage(c: Colony, p: readonly number[]) {
    return (
      c.storages.get(keyOf(p)) ??
      [...c.storages.values()].find((f) => f.v.doubleWith && keyOf(f.v.doubleWith) === keyOf(p))
    );
  }

  /** storage set: each chest's filter / priority / label (a chest not yet storage is discovered first); how many changed. */
  private setStorages(c: Colony, body: Extract<StoryStorageRequest, { op: 'set' }>): number {
    let n = 0;
    for (const p of body.chests) {
      let f = this.findStorage(c, p);
      if (!f) {
        this.discover(c, [p[0], p[1], p[2], p[0], p[1], p[2]], body.filter, body.priority);
        f = this.findStorage(c, p);
      }
      if (!f) continue;
      if (body.filter) f.v.filter = body.filter;
      if (body.priority) f.v.priority = body.priority;
      if (body.label !== undefined) f.v.label = body.label || null;
      f.rev = this.bump(c);
      n++;
    }
    return n;
  }

  /** storage copy: the source storage's filter and priority onto each storage listed; how many took them. */
  private copyStorage(c: Colony, body: Extract<StoryStorageRequest, { op: 'copy' }>): number {
    const src = this.findStorage(c, body.from);
    if (!src) throw Object.assign(new Error('the source chest is not a storage'), { status: 400 });
    let n = 0;
    for (const p of body.to) {
      const f = this.findStorage(c, p);
      if (!f) continue;
      f.v.filter = structuredClone(src.v.filter);
      f.v.priority = src.v.priority;
      f.rev = this.bump(c);
      n++;
    }
    return n;
  }

  /** storage remove: the chests stop being storage; how many were. */
  private removeStorages(c: Colony, chests: ReadonlyArray<readonly number[]>): number {
    let n = 0;
    for (const p of chests) {
      const f = this.findStorage(c, p);
      if (!f) continue;
      c.storages.delete(keyOf(f.v.chest));
      c.removed.push({ rev: this.bump(c), kind: 'storages', key: f.v.chest });
      n++;
    }
    return n;
  }

  /** A furnace's fuel setting (auto) and the choices the sim's version burns. */
  private fuelOf(kind: string): Pick<StoryWorkbench, 'fuel' | 'fuelChoices' | 'fuelCredit'> {
    return kind === 'furnace' ? { fuel: 'auto', fuelChoices: [...SIM_FUELS], fuelCredit: 0 } : {};
  }

  /** A furnace bill's fuel for its next batch (the sim's stock is not counted: have is what the storages list). */
  private fuelNeedOf(b: StoryBill, choice: string): StoryBill['fuelNeed'] {
    const items = Math.max(1, Math.min(8, b.mode === 'count' ? b.target - b.done : 8));
    const fuel = choice === 'planks' ? { item: [5, -1] as [number, number], name: 'planks', per: 1.5 }
      : choice === 'logs' ? { item: [17, -1] as [number, number], name: 'log', per: 1.5 }
      : choice === 'lava_bucket' ? { item: [327, 0] as [number, number], name: 'lava_bucket', per: 100 }
      : { item: [263, choice === 'charcoal' ? 1 : choice === 'coal' ? 0 : -1] as [number, number], name: choice === 'charcoal' ? 'charcoal' : 'coal', per: 8 };
    return { choice, item: fuel.item, name: fuel.name, need: Math.ceil(items / fuel.per), have: 0, short: false, perItem: Math.round((1 / fuel.per) * 1000) / 1000 };
  }

  /** The bench inspector's station facts (an enchanting table's bookshelves, an anvil's wear, a brewing stand's water). */
  private stationOf(kind: string, pos: readonly number[]): StoryWorkbench['station'] {
    const w = this.opts.world;
    if (kind === 'enchanting_table') {
      let n = 0;
      for (let dx = -2; dx <= 2; dx++)
        for (let dz = -2; dz <= 2; dz++)
          for (let dy = 0; dy <= 1; dy++)
            if (Math.max(Math.abs(dx), Math.abs(dz)) === 2 && w.blockAt(pos[0]! + dx, pos[1]! + dy, pos[2]! + dz)[0] === 47) n++;
      n = Math.min(15, n);
      return { present: true, bookshelves: n, maxBookshelves: 15, topOffer: [Math.max(1 + (n >> 1), n * 2), Math.max(8 + (n >> 1) + n, n * 2)] };
    }
    if (kind === 'anvil') {
      const meta = w.blockAt(pos[0]!, pos[1]!, pos[2]!)[1];
      return { present: true, condition: ['intact', 'chipped', 'damaged'][Math.min(2, (meta >> 2) & 3)]! };
    }
    if (kind === 'brewing_stand') return { present: true, water: null, fuel: null };
    return null;
  }

  bills(id: string, body: StoryBillsRequest): BillsAck {
    const c = this.colony(id);
    if (body.op === 'enchant_tools') return this.enchantBills(c, body);
    // The sim reserves nothing for builds' chains: a release is a no-op that says so.
    if (body.op === 'release_claim') return { ok: true, rev: c.rev, note: `build ${body.build} holds nothing for ${body.item}` };
    if (body.op === 'reorder') return this.reorderBills(c, body);
    if (body.op !== 'add') this.guardAutoBill(c, body.id, body.op);
    const { f, newId } = this.changeBill(c, body);
    f.rev = this.bump(c);
    if (body.op === 'remove') this.plan(c);
    return { ok: true, ...(newId ? { id: newId } : {}), rev: c.rev };
  }

  /** A designated bench that takes bills (404 none there; 400 one this version has no bills for). */
  private billBench(c: Colony, pos: readonly number[]): Fn<StoryWorkbench> {
    const f = c.benches.get(keyOf(pos));
    if (!f) throw Object.assign(new Error('no workbench there'), { status: 404 });
    if (f.v.supported === false)
      throw Object.assign(new Error(`this version has no ${f.v.kind.replace(/_/g, ' ')} bills`), { status: 400 });
    return f;
  }

  /** The bench holding a bill (404 none). */
  private billHolder(c: Colony, billId: Key): Fn<StoryWorkbench> {
    for (const f of c.benches.values()) if (f.v.bills.some((b) => String(b.id) === String(billId))) return f;
    throw Object.assign(new Error('no such bill'), { status: 404 });
  }

  /** The tools hook: a count bill per pickaxe kind at the colony's enchanting table, preferring the enchantment. */
  private enchantBills(c: Colony, body: Extract<StoryBillsRequest, { op: 'enchant_tools' }>): BillsAck {
    const table = [...c.benches.values()].find((x) => x.v.kind === 'enchanting_table');
    if (!table) throw Object.assign(new Error('no enchanting table: place one and designate it'), { status: 400 });
    const prefer = body.prefer ?? enchantFor(body.material);
    const ids: Key[] = [];
    for (const item of body.items ?? ['diamond_pickaxe']) {
      const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === `enchant:${item}`);
      if (!recipe || table.v.bills.some((b) => b.recipe === recipe.id && b.prefer === prefer)) continue;
      const b: StoryBill = {
        id: `bill${c.seq++}`,
        recipe: recipe.id,
        mode: 'count',
        target: body.count ?? 1,
        done: 0,
        paused: false,
        suspendedReason: null,
        ingredients: { allow: ['*'], deny: [] },
        radius: 32,
        pawn: 'any',
        minSkill: null,
        store: 'best',
        option: 3,
        prefer,
        preferOnly: body.preferOnly ?? false,
      };
      table.v.bills.push(b);
      ids.push(b.id);
    }
    table.rev = this.bump(c);
    return { ok: true, ids, rev: c.rev };
  }

  /** An auto bill (for a build) can only be cancelled; cancelled, its build waits for the item instead of crafting it. */
  private guardAutoBill(c: Colony, billId: Key, op: string): void {
    const auto = [...c.benches.values()]
      .flatMap((x) => x.v.bills)
      .find((x) => String(x.id) === String(billId) && (x.forBuild ?? null) !== null);
    if (!auto) return;
    if (op !== 'remove')
      throw Object.assign(new Error(`an auto bill (for build ${auto.forBuild}) can only be cancelled`), {
        status: 400,
      });
    const bf = c.builds.get(String(auto.forBuild)) ?? c.builds.get(auto.forBuild!);
    if (!bf || !auto.forItem) return;
    bf.v.noCraft = [...(bf.v.noCraft ?? []), auto.forItem];
    bf.rev = this.bump(c);
  }

  private reorderBills(c: Colony, body: Extract<StoryBillsRequest, { op: 'reorder' }>): BillsAck {
    const f = this.billBench(c, body.bench);
    const ids = body.ids.map(String);
    const front = ids.map((i) => f.v.bills.find((b) => String(b.id) === i)).filter((b): b is StoryBill => !!b);
    f.v.bills = [...front, ...f.v.bills.filter((b) => !ids.includes(String(b.id)))];
    f.rev = this.bump(c);
    return { ok: true, ids: f.v.bills.map((b) => b.id), rev: c.rev };
  }

  /** Adds, updates, removes, pauses or resumes a bill; the bench it is on (and a new bill's id). */
  private changeBill(
    c: Colony,
    body: Extract<StoryBillsRequest, { op: 'add' | 'update' | 'remove' | 'pause' | 'resume' }>,
  ): { f: Fn<StoryWorkbench>; newId?: Key } {
    if (body.op === 'add') return this.addBill(c, body);
    const f = this.billHolder(c, body.id);
    if (body.op === 'update') this.updateBill(f, body);
    else if (body.op === 'remove') f.v.bills = f.v.bills.filter((b) => String(b.id) !== String(body.id));
    else
      for (const b of f.v.bills)
        if (String(b.id) === String(body.id)) {
          b.paused = body.op === 'pause';
          if (body.op === 'resume') b.suspendedReason = null;
        }
    return { f };
  }

  private addBill(c: Colony, body: Extract<StoryBillsRequest, { op: 'add' }>): { f: Fn<StoryWorkbench>; newId: Key } {
    const f = this.billBench(c, body.bench);
    const spec = body.bill;
    const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === spec.recipe);
    if (!recipe) throw Object.assign(new Error('unknown recipe'), { status: 400 });
    if (recipe.workbench !== f.v.kind)
      throw Object.assign(new Error(`no recipe '${recipe.id}' at a ${f.v.kind}`), { status: 400 });
    if (recipe.modes && !recipe.modes.includes(spec.mode))
      throw Object.assign(new Error(`mode must be ${recipe.modes.join('|')} for this recipe`), { status: 400 });
    const b = newBill(`bill${c.seq++}`, recipe, spec);
    if (recipe.workbench === 'furnace') b.fuelNeed = this.fuelNeedOf(b, b.fuel ?? f.v.fuel ?? 'auto');
    f.v.bills.push(b);
    return { f, newId: b.id };
  }

  /** A bill's fields change (fuel only on a furnace's; `inherit` = the bench's); a resumed bill is no longer suspended. */
  private updateBill(f: Fn<StoryWorkbench>, body: Extract<StoryBillsRequest, { op: 'update' }>): void {
    const b = f.v.bills.find((x) => String(x.id) === String(body.id))!;
    const { op: _op, id: _id, ...fields } = body;
    if (fields.fuel !== undefined && f.v.kind !== 'furnace')
      throw Object.assign(new Error('fuel is for furnace bills (a brewing stand burns blaze powder)'), { status: 400 });
    Object.assign(b, {
      ...fields,
      ...(fields.fuel !== undefined ? { fuel: fields.fuel === 'inherit' ? null : fields.fuel } : {}),
    });
    if (f.v.kind === 'furnace') b.fuelNeed = this.fuelNeedOf(b, b.fuel ?? f.v.fuel ?? 'auto');
    if (fields.paused === false) b.suspendedReason = null;
  }

  // ------------------------------------------------------------------ builds craft what they need; designated benches; tiers

  private hasBench(c: Colony, kind: string): boolean {
    return [...c.benches.values()].some((f) => f.v.kind === kind);
  }

  /**
   * The sim's materials pass (the bot's, simplified to one raw material per item): builds reserve stock in queue order; a
   * shortfall with a recipe at a designated bench becomes an auto bill ("for build bN", count = what is still to make); a
   * missing bench is made and placed near home when the colony allows it; what can't be made is short, with its raw material.
   */
  private plan(c: Colony): void {
    const pool = new Map(c.stock);
    const benches: PlanBenches = { table: this.hasBench(c, 'crafting_table'), furnace: this.hasBench(c, 'furnace') };
    const wants: PlanWant[] = [];
    for (const f of [...c.builds.values()].sort((a, b) => a.v.order - b.v.order)) {
      const b = f.v;
      if (b.status === 'done' || b.status === 'paused' || b.kind === 'remove') continue;
      if (b.kind === 'water') this.planWater(c, f, pool);
      else this.planBuild(c, f, pool, benches, wants);
    }
    // Auto bills follow the wants: targets track the shortfall, the rest go, new ones join the bench's front.
    for (const f of c.benches.values()) this.syncAutoBills(c, f, wants);
  }

  /** A liquid build needs one bucket (kept, never used up), not one per cell. */
  private planWater(c: Colony, f: Fn<StoryBuild>, pool: Map<string, number>): void {
    const b = f.v;
    const buckets = (pool.get('325:0') ?? 0) + (pool.get('326:0') ?? 0) + (pool.get('327:0') ?? 0);
    const mats: StoryBuildMaterial[] = [
      {
        item: 'bucket',
        label: 'Bucket',
        need: 1,
        have: buckets > 0 ? 1 : 0,
        crafting: 0,
        short: buckets > 0 ? 0 : 1,
        via: 'craft',
        chain: ['craft 1 Bucket from 3 Iron Ingot (table)'],
        raw: buckets > 0 ? [] : ['3 Iron Ingot'],
      },
    ];
    if (JSON.stringify(mats) === JSON.stringify(b.materials)) return;
    b.materials = mats;
    f.rev = this.bump(c);
  }

  /** One build's materials: stock reserved from the pool, the shortfall crafted (an auto bill) or short; its status. */
  private planBuild(c: Colony, f: Fn<StoryBuild>, pool: Map<string, number>, benches: PlanBenches, wants: PlanWant[]): void {
    const b = f.v;
    const { left, cat, key, label } = this.placedOf(c, b);
    const have = Math.min(left, pool.get(key) ?? 0);
    pool.set(key, (pool.get(key) ?? 0) - have);
    const short0 = left - have;
    const m = this.planShortfall(c, b, cat, label, short0, pool, benches, wants);
    const mats: StoryBuildMaterial[] = left
      ? [
          {
            item: cat?.[0] ?? key,
            label,
            need: left,
            have,
            crafting: m.crafting,
            short: short0 - m.crafting,
            via: m.via,
            chain: m.chain,
            raw: m.raw,
          },
        ]
      : [];
    const status = planStatus(b.status, short0, m.crafting);
    const reason = planReason(short0, m.crafting, label);
    if (
      JSON.stringify([mats, m.station, status, reason]) ===
      JSON.stringify([b.materials, b.station ?? null, b.status, b.reason ?? null])
    )
      return;
    b.materials = mats;
    b.station = m.station;
    b.status = status;
    b.reason = reason;
    f.rev = this.bump(c);
  }

  /** What a build places and how many are left: its ghosts' block (a fence's fence, a door's door), else its material. */
  private placedOf(c: Colony, b: StoryBuild): { left: number; cat: ArchitectEntry | undefined; key: string; label: string } {
    const cells = (c.ghostCells.get(b.id) ?? []).filter((g) => g[3] !== 0);
    const [mid, mmeta] = cells[0] ? [cells[0][3], cells[0][4]] : materialKey(b.material);
    const left = cells.filter((g) => g[6] !== 'done').length;
    const cat = architectEntryOf(mid, mmeta);
    const key = cat ? `${cat[4][0]}:${cat[4][1]}` : `${mid}:${mmeta}`;
    return { left, cat, key, label: cat?.[1] ?? storyItemLabel(mid, mmeta) };
  }

  /** A build's shortfall: crafted from its raw material when there is a recipe (and the build allows it), else short. */
  private planShortfall(
    c: Colony,
    b: StoryBuild,
    cat: ArchitectEntry | undefined,
    label: string,
    short0: number,
    pool: Map<string, number>,
    benches: PlanBenches,
    wants: PlanWant[],
  ): PlanCraft {
    const out: PlanCraft = { crafting: 0, via: 'none', chain: [], raw: [], station: null };
    if (short0 <= 0) return out;
    if (cat && cat[7] && !(b.noCraft ?? []).includes(cat[0])) this.planCraft(c, b, cat, label, short0, pool, benches, wants, out);
    else out.raw.push(`${short0} ${label}`);
    return out;
  }

  /** Crafts (or smelts) what the raw stock allows: an auto bill when its bench is designated, else a bench to make and place
   * (when the colony auto-places benches); the rest is short in raw material. */
  private planCraft(
    c: Colony,
    b: StoryBuild,
    cat: ArchitectEntry,
    label: string,
    short0: number,
    pool: Map<string, number>,
    benches: PlanBenches,
    wants: PlanWant[],
    out: PlanCraft,
  ): void {
    const raw = cat[7]!;
    const [rid, rmeta] = raw.split(':').map(Number) as [number, number];
    const smelt = cat[3] === 'furnace';
    out.via = smelt ? 'smelt' : 'craft';
    out.chain.push(`${out.via} ${short0} ${label} from ${storyItemLabel(rid, rmeta)}${cat[3] === 'workbench' ? ' (table)' : ''}`);
    const rawHave = pool.get(raw) ?? 0;
    const can = Math.min(short0, Math.floor(rawHave * cat[8]));
    const benchOk = benchReady(cat, smelt, benches);
    if (can > 0) {
      pool.set(raw, rawHave - Math.ceil(can / cat[8]));
      out.crafting = can;
      if (benchOk)
        wants.push({ build: b.id, item: cat[0], recipe: recipeOf(cat), n: can, bench: smelt ? 'furnace' : 'crafting_table' });
    }
    if (!benchOk && c.options.autoPlaceBenches) out.station = autoStation(c, benches);
    if (short0 - can > 0) out.raw.push(`${Math.ceil((short0 - can) / cat[8])} ${storyItemLabel(rid, rmeta)}`);
  }

  /** A bench's auto bills follow the wants (taken from `wants`): targets track the shortfall, the rest go, new ones join its
   * front. */
  private syncAutoBills(c: Colony, f: Fn<StoryWorkbench>, wants: PlanWant[]): void {
    let changed = false;
    const kept: StoryBill[] = [];
    for (const bill of f.v.bills) changed = keepAutoBill(bill, f.v.kind, wants, kept) || changed;
    for (const w of wants.filter((x) => x.bench === f.v.kind)) {
      kept.unshift(this.autoBill(c, w));
      changed = true;
    }
    for (let i = wants.length - 1; i >= 0; i--) if (wants[i]!.bench === f.v.kind) wants.splice(i, 1);
    if (!changed) return;
    f.v.bills = [...kept.filter((x) => (x.forBuild ?? null) !== null), ...kept.filter((x) => (x.forBuild ?? null) === null)];
    f.rev = this.bump(c);
  }

  private autoBill(c: Colony, w: PlanWant): StoryBill {
    return {
      id: `bill${c.seq++}`,
      recipe: w.recipe,
      mode: 'count',
      target: w.n,
      done: 0,
      paused: false,
      suspendedReason: null,
      ingredients: { allow: ['*'], deny: [] },
      radius: 256,
      pawn: 'any',
      minSkill: null,
      store: 'best',
      forBuild: w.build,
      forItem: w.item,
      step: 0,
    };
  }

  /** POST …/workbenches: designate a placed bench as the colony's, or release it. */
  workbenches(id: string, body: StoryWorkbenchesRequest): StoryWorkbenchesResponse {
    const c = this.colony(id);
    const k = keyOf(body.pos);
    if (body.op === 'release') {
      const f = c.benches.get(k);
      if (!f) throw Object.assign(new Error("that workbench isn't the colony's"), { status: 400 });
      c.benches.delete(k);
      c.removed.push({ rev: this.bump(c), kind: 'workbenches', key: body.pos });
      this.plan(c);
      return { pos: body.pos, kind: f.v.kind, designated: false };
    }
    const have = c.benches.get(k);
    if (body.op === 'fuel') {
      // A furnace's fuel choice: the sim's version is b1.8.1 (no blaze rods).
      const f = have ?? null;
      if (!f || f.v.kind !== 'furnace')
        throw Object.assign(new Error('only a furnace takes a fuel choice'), { status: 400 });
      if (body.fuel === 'blaze_rod') throw Object.assign(new Error('fuel must be ' + SIM_FUELS.join('|')), { status: 400 });
      f.v.fuel = body.fuel;
      for (const b of f.v.bills) if (!b.fuel) b.fuelNeed = this.fuelNeedOf(b, body.fuel);
      f.rev = this.bump(c);
      return { pos: body.pos, kind: f.v.kind, designated: true, fuel: body.fuel };
    }
    if (have) return { pos: body.pos, kind: have.v.kind, designated: true };
    const [bid] = this.opts.world.blockAt(body.pos[0], body.pos[1], body.pos[2]);
    const kind = BENCH_KIND[bid];
    if (!kind) throw Object.assign(new Error('no workbench there'), { status: 400 });
    c.benches.set(k, {
      rev: this.bump(c),
      v: { pos: body.pos, kind, label: null, supported: true, station: this.stationOf(kind, body.pos), ...this.fuelOf(kind), bills: [] },
    });
    this.plan(c);
    return { pos: body.pos, kind, designated: true };
  }

  /** POST …/options. */
  /** Fast / Faster: every online pawn's body runs at the multiplier (the mock server never rejects it). */
  private setPawnSpeed(c: Colony, speed: StorySpeed) {
    c.options.speed = speed;
    const m = SPEED_MULTIPLIER[speed];
    for (const p of c.pawns.values()) {
      p.p.speed = p.p.online === false ? null : { level: speed, multiplier: m, requested: m, serverFactor: 1, rollbacks: 0 };
      p.rev = this.bump(c);
    }
  }

  options(id: string, body: StoryOptionsRequest): StoryColonyOptions {
    const c = this.colony(id);
    if (Object.values(body).every((v) => v === undefined))
      throw Object.assign(new Error('autoPlaceBenches, detectBenches, idle, autoCraftTools, spareTools, rescueTeleport, unfilteredStorageTakesAnything, keepDisallowed, speed or speedEffect required'), { status: 400 });
    for (const k of OPTION_FIELDS) {
      const v = body[k];
      if (v === undefined) continue;
      Object.assign(c.options, { [k]: k === 'spareTools' ? spareToolsCopy(body.spareTools!) : v });
    }
    if (body.speed !== undefined) this.setPawnSpeed(c, body.speed);
    this.bump(c);
    this.plan(c);
    return { ...c.options };
  }

  /** The first chest the colony takes over also holds its armory (weapons, armour, spare tools) for the gear ops. */
  private stock(c: Colony, contents: Array<[number, number, number]>): Array<[number, number, number]> {
    if (c.gear.stocked) return contents;
    c.gear.stocked = true;
    return [...contents, ...SIM_ARMORY.map((x) => [...x] as [number, number, number])];
  }

  /** Who an attack order is on: a player or bot (confirm needed), one of the colony's pawns (refused), or a mob. */
  private attackTarget(c: Colony, t: StoryOrderRequest['order']['target']): AttackTarget | null {
    if (!t || typeof t !== 'object') return null;
    const ai = this.opts.world.ai?.() ?? [];
    const kindOf = (name: string): AttackTarget['kind'] =>
      c.pawns.has(name) || [...c.pawns.values()].some((p) => p.p.display === name)
        ? 'pawn'
        : ai.some((a) => a.name === name)
          ? 'bot'
          : 'player';
    if ('player' in t) return { name: t.player, kind: kindOf(t.player) };
    if ('entity' in t) {
      const e = this.opts.world.entities().find((x) => x.id === t.entity);
      if (e?.kind === 'player') return { name: e.name ?? `#${e.id}`, kind: kindOf(e.name ?? '') };
      return null;
    }
    return null;
  }

  /** POST …/policies. */
  policies(id: string, body: StoryPoliciesRequest) {
    const c = this.colony(id);
    const out = policiesOp(c.gear, c.pawns, body);
    for (const p of c.pawns.values()) p.rev = this.bump(c);
    return out;
  }

  /** POST …/material-rules. */
  materialRules(id: string, body: StoryMaterialRulesRequest) {
    const c = this.colony(id);
    const out = materialRulesOp(c.gear, body, null);
    this.bump(c);
    return out;
  }

  /** POST …/gear: one op for the selection (fair spread, each stored unit to one pawn). */
  gear(id: string, body: StoryGearRequest) {
    const c = this.colony(id);
    const pawns = this.pawnsOf(c, body.pawns);
    const stores = [...c.storages.values()].map((f) => f.v);
    const out = gearOp(c.gear, pawns, stores, body, (i, m) => storyItemLabel(i, Math.max(0, m)).toLowerCase());
    for (const f of c.storages.values()) f.rev = this.bump(c);
    for (const p of pawns) p.rev = this.bump(c);
    return out;
  }

  /** GET …/architect: the sim catalog with tiers unlocked by the colony's designated benches. */
  architect(id: string): StoryArchitectResponse {
    const c = this.colony(id);
    const table = this.hasBench(c, 'crafting_table');
    const furnace = this.hasBench(c, 'furnace');
    const entries: StoryArchitectEntry[] = SIM_ARCHITECT.map((row) => architectEntry(row, c.stock, table, furnace));
    // The Water tab (docs/web/story-mode.md "Water"): the bucket's tier; a bucket in storage unlocks them whatever the benches.
    const buckets = (c.stock.get('325:0') ?? 0) + (c.stock.get('326:0') ?? 0) + (c.stock.get('327:0') ?? 0);
    const ingots = c.stock.get('265:0') ?? 0;
    const water = (eid: string, label: string, kind: string, item: [number, number], block: number): StoryArchitectEntry => ({
      id: eid,
      label,
      tab: 'water',
      tier: 'workbench',
      unlocked: buckets > 0 || table,
      reason: buckets > 0 || table ? null : 'Needs a bucket — 3 iron ingots at a crafting table (place one or designate an existing one)',
      item,
      material: [block, 0],
      kind,
      kinds: [kind],
      chain: ['craft Bucket from Iron Ingot (table)'],
      have: buckets,
      craftable: buckets > 0 ? 0 : Math.floor(ingots / 3),
      from: buckets === 0 && ingots >= 3 ? ['3 Iron Ingot'] : [],
      short: buckets === 0 && ingots < 3 ? ['3 Iron Ingot'] : [],
    });
    entries.push(
      water('water_source', 'Water source', 'water', [326, 0], 9),
      water('water_channel', 'Water channel', 'water_channel', [326, 0], 9),
      water('infinite_water', 'Infinite water source', 'infinite_water', [326, 0], 9),
      ...(c.options.allowLava ? [water('lava_source', 'Lava source', 'lava', [327, 0], 11)] : []),
    );
    return {
      entries,
      benches: { crafting_table: table ? 1 : 0, furnace: furnace ? 1 : 0 },
      tabs: ['structure', 'furniture', 'production', 'storage', 'lighting', 'water', 'misc'],
    };
  }

  /** Every directive of every colony (newest last). */
  directives(): StoryDirective[] {
    return [...this.colonies.values()].flatMap((c) => [...c.directives.values()].map((f) => f.v));
  }

  directiveDetail(id: string) {
    const d = this.directives().find((x) => String(x.id) === id);
    if (!d) throw Object.assign(new Error('no such directive'), { status: 404 });
    return { ...d, line: `${d.summary} [${d.id}]`, delivered: d.bots, detail: null };
  }

  directive(body: StoryDirectiveRequest): { id: string; delivered: string[]; line: string } {
    const c = [...this.colonies.values()].at(-1);
    const d: StoryDirective = {
      id: `dir-${body.requestId}`,
      at: this.iso(),
      bots: body.bots,
      kind: body.directive.kind,
      summary: directiveSummary(body.directive),
      status: 'sent',
      reply: null,
    };
    if (c) {
      c.directives.set(d.id, { rev: this.bump(c), v: d });
      this.emit(c);
      const step = (status: string, reply: string | null, ms: number) =>
        setTimeout(() => {
          d.status = status;
          if (reply) d.reply = reply;
          c.directives.set(d.id, { rev: this.bump(c), v: d });
          this.emit(c);
        }, ms);
      step('acknowledged', `On it: ${d.summary.toLowerCase()}.`, 1500);
      step('done', 'Done!', 7000);
    }
    return { id: String(d.id), delivered: body.bots, line: `${d.summary} [${d.id}]` };
  }

  // ------------------------------------------------------------------ the director's loop

  /** Advances every colony by `dtMs`. */
  step(dtMs: number): void {
    for (const c of this.colonies.values()) {
      if (c.released) continue;
      const before = c.rev;
      c.tod = (c.tod + dtMs / 50) % 24000;
      if (c.builds.size) this.plan(c); // stock and ghosts move: the builds' materials and auto bills follow
      for (const p of c.pawns.values()) {
        if (p.p.online) continue;
        // An offline pawn comes back when its re-add / login is through (backAt); one waiting for a seat stays out.
        if (p.backAt !== null && p.backAt !== undefined && this.now() >= p.backAt) {
          p.backAt = null;
          p.p.online = true;
          p.p.state = 'idle';
          p.p.offlineReason = null;
          p.rev = this.bump(c);
        }
      }
      if (!c.paused) for (const p of c.pawns.values()) if (p.p.online) this.stepPawn(c, p, dtMs);
      this.prune(c);
      if (c.rev !== before) this.emit(c);
    }
  }

  private emit(c: Colony): void {
    for (const fn of this.listeners) fn(c.id);
  }

  private prune(c: Colony): void {
    if (c.removed.length > 500) c.removed.splice(0, c.removed.length - 500);
    if (c.alerts.length > 60) c.alerts.splice(0, c.alerts.length - 60);
  }

  private alert(c: Colony, level: string, text: string, pawn?: string, fix?: StoryAlert['fix']): void {
    c.alerts.push({
      rev: this.bump(c),
      v: { id: `a${c.seq++}`, at: this.iso(), level, text, ...(pawn ? { pawn } : {}), ...(fix ? { fix } : {}) },
    });
  }

  /** Releases a pawn's lease (the designation or ghost goes back to the pool). */
  private drop(c: Colony, p: PawnSim): void {
    const t = p.task;
    p.task = null;
    p.goal = null;
    p.work = 0;
    p.p.job = null;
    if (!t?.ref) return;
    const d = c.designations.get(t.ref);
    if (d && d.v.pawn === p.p.name) {
      d.v.pawn = null;
      d.v.state = 'todo';
      d.rev = this.bump(c);
    }
    const cells = c.ghostCells.get(t.ref);
    if (cells && t.target) {
      const g = cells.find((x) => x[0] === t.target![0] && x[1] === t.target![1] && x[2] === t.target![2]);
      if (g && g[6] === 'leased') {
        g[6] = 'todo';
        c.ghostsRev = this.bump(c);
      }
    }
  }

  /** One pawn's tick: needs, then (in order) eating, its orders, its schedule and work; then it walks to its goal or works. */
  private stepPawn(c: Colony, p: PawnSim, dt: number): void {
    const pawn = p.p;
    let changed = this.tickNeeds(p, dt);
    // 1. urgent needs (not while drafted)
    this.eatIfHungry(c, p);
    // 2. orders (drafted pawns only follow orders; undrafted ones do theirs first too)
    if (!p.task && pawn.queue.length) {
      this.takeOrder(c, p);
      changed = true;
    }
    // 3. schedule and work
    if (!p.task && !pawn.drafted) changed = this.followSchedule(c, p) || changed;
    if (!p.task) {
      this.restIdle(c, p, changed);
      return;
    }
    p.idleSince = this.now();
    // Move toward the goal, then work.
    if (p.goal && Math.hypot(p.goal[0] - p.x, p.goal[2] - p.z) > arriveWithin(p.task.kind)) this.walk(p, p.goal, dt);
    else this.work(c, p, dt);
    pawn.pos = [Math.round(p.x * 100) / 100, p.y, Math.round(p.z * 100) / 100];
    if (p.task) pawn.job = this.jobOf(p, p.task);
    p.rev = this.bump(c);
  }

  /** Hunger, a bulk gear op finishing (in a few seconds), healing; true when the gear work changed. */
  private tickNeeds(p: PawnSim, dt: number): boolean {
    const pawn = p.p;
    let changed = false;
    pawn.food = Math.max(0, (pawn.food ?? 20) - dt / 40000);
    if (pawn.gearTasks?.length && Math.random() < dt / 3000) {
      pawn.gearTasks = [];
      changed = true;
    }
    if ((pawn.hp ?? 20) < 20 && Math.random() < dt / 8000) pawn.hp = Math.min(20, (pawn.hp ?? 20) + 1);
    return changed;
  }

  /** An undrafted pawn at its hunger line drops what it does and eats. */
  private eatIfHungry(c: Colony, p: PawnSim): void {
    const pawn = p.p;
    if (pawn.drafted || p.task?.kind === 'eat' || (pawn.food ?? 20) > hungerLine(pawn)) return;
    this.drop(c, p);
    p.task = { kind: 'eat', ref: null, label: 'Eating', target: null, type: 'eat' };
    p.work = 2500;
  }

  /** The first queued order becomes the task (its target: a position, else the entity's). */
  private takeOrder(c: Colony, p: PawnSim): void {
    const o = p.p.queue[0]!;
    const at = this.orderTarget(o.target);
    p.task = { kind: `order:${o.kind}`, ref: o.id ?? null, label: orderLabel(o.kind), target: at, type: o.kind };
    p.goal = at;
    p.work = orderWork(o.kind);
    o.state = 'active';
    const of = o.id !== null && o.id !== undefined ? c.orders.get(o.id) : undefined;
    if (of) of.rev = this.bump(c);
  }

  private orderTarget(t: StoryOrder['target']): [number, number, number] | null {
    const ent = t && typeof t === 'object' && !Array.isArray(t) ? t.entity : null;
    return targetPos(t) ?? (ent !== null && ent !== undefined ? this.entityPos(ent) : null);
  }

  /** The schedule's hour: sleep at home, relax near it, else work (or idle); true when the pawn's state changed. */
  private followSchedule(c: Colony, p: PawnSim): boolean {
    const block = p.p.schedule[storyHour(c.tod)] ?? 'anything';
    let changed = false;
    if (block === 'sleep') {
      p.task = { kind: 'sleep', ref: null, label: 'Sleeping', target: c.home, type: 'sleep' };
      p.goal = [c.home[0] + 0.5, c.home[1] + 1, c.home[2] + 0.5];
      p.work = 20000;
    } else if (block === 'free') {
      const a = Math.random() * Math.PI * 2;
      p.task = { kind: 'free', ref: null, label: 'Relaxing near home', target: null, type: 'idle' };
      p.goal = [c.home[0] + Math.cos(a) * 4, c.home[1] + 1, c.home[2] + Math.sin(a) * 4];
      p.work = 4000;
    } else if (!this.pickWork(c, p)) changed = this.goIdle(c, p);
    return changed || !!p.task;
  }

  /** Nothing to do: idle (an alert after a minute, at most every two); true when it wasn't idle already. */
  private goIdle(c: Colony, p: PawnSim): boolean {
    const pawn = p.p;
    const was = pawn.state !== 'idle';
    pawn.state = 'idle';
    pawn.job = null;
    const now = this.now();
    if (now - p.idleSince > 60_000 && now - p.lastAlert > 120_000) {
      p.lastAlert = now;
      this.alert(c, 'warn', `${pawn.display} is idle: nothing to do in their enabled work.`, pawn.name, {
        tab: 'work',
        pawn: pawn.name,
      });
    }
    return was;
  }

  /** No task this tick: a drafted pawn shows as drafted; the pawn is re-sent only when something changed. */
  private restIdle(c: Colony, p: PawnSim, changed: boolean): void {
    const pawn = p.p;
    if (pawn.drafted && pawn.state !== 'drafted') {
      pawn.state = 'drafted';
      changed = true;
    }
    if (changed) p.rev = this.bump(c);
  }

  private walk(p: PawnSim, goal: [number, number, number], dt: number): void {
    const pawn = p.p;
    const dx = goal[0] - p.x,
      dz = goal[2] - p.z;
    const d = Math.hypot(dx, dz);
    const s = Math.min(d, (SPEED * (pawn.food! < 4 ? 0.6 : 1) * dt) / 1000);
    p.x += (dx / d) * s;
    p.z += (dz / d) * s;
    p.y = this.opts.world.ground(Math.floor(p.x), Math.floor(p.z)) + 1;
    pawn.state = pawn.drafted ? 'drafted' : p.task!.kind === 'flee' ? 'fleeing' : 'moving';
  }

  /** At the goal: works on the task (night owls faster at night) and finishes it when the work is done. */
  private work(c: Colony, p: PawnSim, dt: number): void {
    const pawn = p.p;
    pawn.state = workState(p.task!, pawn.drafted);
    p.work -= dt * workRate(pawn, c.tod);
    if (p.work <= 0) this.finish(c, p);
  }

  private jobOf(p: PawnSim, task: NonNullable<PawnSim['task']>): StoryPawn['job'] {
    const total = WORK_MS[task.type] ?? 1500;
    return {
      type: task.type,
      label: task.label,
      ...(task.target ? { target: { x: task.target[0], y: task.target[1], z: task.target[2] } } : {}),
      progress: Number.isFinite(p.work) ? Math.max(0, Math.min(1, 1 - p.work / total)) : null,
      since: this.iso(),
    };
  }

  private entityPos(id: number): [number, number, number] | null {
    const e = this.opts.world.entities().find((x) => x.id === id);
    return e ? [e.x, e.y, e.z] : null;
  }

  /** The job picker: the best-priority work type with an available job; ties to skill, then distance. */
  private pickWork(c: Colony, p: PawnSim): boolean {
    const here: [number, number, number] = [p.x, p.y, p.z];
    const cands: WorkCand[] = [];
    this.designationWork(c, p, here, cands);
    this.buildWork(c, p, here, cands);
    this.billWork(c, p, here, cands);
    if (cands.length === 0) return false;
    cands.sort((a, b) => a.pri - b.pri || a.col - b.col || b.skill - a.skill || a.d - b.d);
    cands[0]!.take();
    return true;
  }

  /** A job of a work type the pawn does (priority > 0) as a candidate. */
  private addWork(
    pawn: StoryPawn,
    here: [number, number, number],
    cands: WorkCand[],
    type: string,
    at: readonly unknown[],
    take: () => void,
  ): void {
    const pri = pawn.priorities[type] ?? 0;
    if (pri <= 0) return;
    cands.push({
      pri,
      col: STORY_WORK_TYPES.indexOf(type as never),
      skill: pawn.skills[type]?.level ?? 0,
      d: dist(here, at),
      take,
    });
  }

  /** Open designations (not leased, done, a door mark, or resting after a failure). */
  private designationWork(c: Colony, p: PawnSim, here: [number, number, number], cands: WorkCand[]): void {
    for (const [k, f] of c.designations) {
      const d = f.v;
      if (!this.designationOpen(d)) continue;
      const type = WORK_OF[d.kind];
      if (!type || !d.block) continue;
      const at = d.block;
      this.addWork(p.p, here, cands, type, at, () => this.leaseDesignation(c, p, k, f, type, at));
    }
  }

  private designationOpen(d: StoryDesignation): boolean {
    if (d.pawn || d.state === 'done' || d.kind === 'forbid' || d.kind === 'allow') return false;
    // A failed one rests until its retry.
    return !(d.retryAt && (typeof d.retryAt === 'number' ? d.retryAt : Date.parse(d.retryAt)) > this.now());
  }

  private leaseDesignation(c: Colony, p: PawnSim, k: Key, f: Fn<StoryDesignation>, type: string, at: readonly number[]): void {
    const d = f.v;
    const pawn = p.p;
    d.pawn = pawn.name;
    d.state = 'leased';
    f.rev = this.bump(c);
    p.task = { kind: d.kind, ref: k, label: designationLabel(d.kind), target: [at[0]!, at[1]!, at[2]!], type: d.kind };
    p.goal = [at[0]! + 0.5, at[1]!, at[2]! + 0.5];
    p.work =
      (WORK_MS[d.kind] ?? 1500) *
      (d.kind === 'chop' ? Math.max(1, (d.targets?.length ?? 1) / 4) : 1) *
      (1.4 - (pawn.skills[type]?.level ?? 0) / 40);
  }

  /** Builds in queue order: the first unpaused build with a todo ghost (its lowest, nearest cell). */
  private buildWork(c: Colony, p: PawnSim, here: [number, number, number], cands: WorkCand[]): void {
    const queue = [...c.builds.values()]
      .map((f) => f.v)
      .filter((b) => b.status !== 'paused' && b.status !== 'done')
      .sort((a, b) => a.order - b.order);
    for (const b of queue) {
      const cells = c.ghostCells.get(b.id) ?? [];
      const g = cells.filter((x) => x[6] === 'todo').sort((a, b2) => a[1] - b2[1] || dist(here, a) - dist(here, b2))[0];
      if (!g) continue;
      this.addWork(p.p, here, cands, 'build', g, () => {
        g[6] = 'leased';
        c.ghostsRev = this.bump(c);
        p.task = {
          kind: 'build',
          ref: b.id,
          label: `Building ${b.label.toLowerCase()}`,
          target: [g[0], g[1], g[2]],
          type: 'build',
        };
        p.goal = [g[0] + 0.5, g[1], g[2] + 0.5];
        p.work = WORK_MS.build! * (1.4 - (p.p.skills.build?.level ?? 0) / 40);
      });
      break;
    }
  }

  /** Bills at workbenches, top to bottom: each bench's first bill this pawn may do. */
  private billWork(c: Colony, p: PawnSim, here: [number, number, number], cands: WorkCand[]): void {
    const pawn = p.p;
    for (const f of c.benches.values()) {
      const bench = f.v;
      const bill = bench.bills.find((b) => billWanted(b) && billOpenTo(b, pawn));
      if (!bill) continue;
      const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === bill.recipe);
      const type = recipe?.workType === 'cook' ? 'cook' : 'craft';
      this.addWork(pawn, here, cands, type, bench.pos, () => {
        p.task = {
          kind: 'bill',
          ref: bill.id,
          label: `${type === 'cook' ? 'Cooking' : 'Crafting'} ${recipe?.label.toLowerCase() ?? bill.recipe}`,
          target: bench.pos,
          type,
        };
        p.goal = [bench.pos[0] + 0.5, bench.pos[1], bench.pos[2] + 0.5];
        p.work = WORK_MS.craft!;
      });
    }
  }

  private grow(c: Colony, p: PawnSim, type: string): void {
    const s = p.p.skills[type];
    if (!s) return;
    const fast =
      (type === 'farm' && p.p.traits.some((t) => t.id === 'green_thumb')) ||
      (type === 'build' && p.p.traits.some((t) => t.id === 'steady_hands'));
    s.xp = (s.xp ?? 0) + (s.passion ? 60 : 30) * (fast ? 2 : 1);
    if (s.xp >= 1000 && s.level < 20) {
      s.xp -= 1000;
      s.level++;
      this.alert(c, 'info', `${p.p.display} is now level ${s.level} in ${type}.`, p.p.name);
    }
  }

  /** A task's work is done: eat, an order, a bill, a build cell or a designation completes; the pawn is free again. */
  private finish(c: Colony, p: PawnSim): void {
    const t = p.task!;
    if (t.kind === 'eat') p.p.food = 20;
    else if (t.kind.startsWith('order:')) this.finishOrder(c, p);
    else if (t.kind === 'bill') this.finishBill(c, p, t);
    else if (t.kind === 'build' && t.ref !== null && t.ref !== undefined) this.finishBuild(c, p, t, t.ref);
    else if (t.ref !== null && t.ref !== undefined) this.finishDesignation(c, p, t.ref);
    p.task = null;
    p.goal = null;
    p.p.job = null;
    p.rev = this.bump(c);
  }

  private finishOrder(c: Colony, p: PawnSim): void {
    const o = p.p.queue.shift();
    if (o?.id === null || o?.id === undefined) return;
    const of = c.orders.get(o.id);
    if (!of) return;
    of.v.state = 'done';
    of.rev = this.bump(c);
  }

  private finishBill(c: Colony, p: PawnSim, t: NonNullable<PawnSim['task']>): void {
    for (const f of c.benches.values()) {
      const bill = f.v.bills.find((b) => b.id === t.ref);
      if (bill) this.billMade(c, f, bill);
    }
    this.grow(c, p, t.type);
  }

  /** One round of a bill: made (stock grows; an `until` bill is done once the stock reaches it), or, sometimes, suspended for
   * an ingredient the colony lacks. */
  private billMade(c: Colony, f: Fn<StoryWorkbench>, bill: StoryBill): void {
    const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === bill.recipe);
    const missing = recipe?.inputs.find((i) => !hasInput(c.stock, i));
    if (missing && bill.mode !== 'forever' && Math.random() < 0.15) this.suspendBill(c, f, bill, recipe, missing);
    else stockMade(c.stock, bill, recipe);
    f.rev = this.bump(c);
  }

  private suspendBill(
    c: Colony,
    f: Fn<StoryWorkbench>,
    bill: StoryBill,
    recipe: SimRecipe | undefined,
    missing: SimRecipe['inputs'][number],
  ): void {
    bill.suspendedReason = `missing ${storyItemLabel(missing.options?.[0]?.[0] ?? 0).toLowerCase()} within ${bill.radius} blocks`;
    this.alert(c, 'warn', `A bill was suspended: ${recipe?.label ?? bill.recipe} (${bill.suspendedReason}).`, undefined, {
      tab: 'bills',
      workbench: f.v.pos,
    });
  }

  /** A build cell is placed (when it is still there); the pawn's build skill grows. */
  private finishBuild(c: Colony, p: PawnSim, t: NonNullable<PawnSim['task']>, ref: Key): void {
    const cells = c.ghostCells.get(ref) ?? [];
    const g = cells.find((x) => t.target && x[0] === t.target[0] && x[1] === t.target[1] && x[2] === t.target[2]);
    const f = c.builds.get(ref);
    if (g && f) this.placeBuildCell(c, f, cells, g);
    this.grow(c, p, 'build');
  }

  /** A ghost cell is built; a finished build says so, and its furniture becomes a workbench or a chest the colony can use. */
  private placeBuildCell(
    c: Colony,
    f: Fn<StoryBuild>,
    cells: GhostCell[],
    g: GhostCell,
  ): void {
    g[6] = 'done';
    this.opts.world.setBlock?.(g[0], g[1], g[2], g[3], g[4]);
    f.v.blocksDone = cells.filter((x) => x[6] === 'done').length;
    f.v.status = f.v.blocksDone >= f.v.blocksTotal ? 'done' : 'active';
    f.rev = this.bump(c);
    c.ghostsRev = this.bump(c);
    if (f.v.status !== 'done') return;
    for (const [x, y, z, id] of cells)
      if (id === 58 || id === 61 || CHEST_IDS.has(id)) this.discover(c, [x, y, z, x, y, z], undefined);
    this.alert(c, 'info', `Finished building: ${f.v.label}.`);
  }

  /** A designation is done: its blocks cleared (chop, mine, cut, harvest, deconstruct), logs stocked, a drop hauled. */
  private finishDesignation(c: Colony, p: PawnSim, ref: Key): void {
    const f = c.designations.get(ref);
    if (!f) return;
    const d = f.v;
    this.clearDesignated(d);
    if (d.kind === 'chop') c.stock.set('17:0', (c.stock.get('17:0') ?? 0) + (d.targets?.length ?? 1));
    if (d.kind === 'haul') this.haul(c, p);
    c.designations.delete(ref);
    c.removed.push({ rev: this.bump(c), kind: 'designations', key: ref });
    this.grow(c, p, WORK_OF[d.kind] ?? d.kind);
  }

  private clearDesignated(d: StoryDesignation): void {
    if (!CLEARS_BLOCKS.has(d.kind)) return;
    for (const b of d.targets ?? (d.block ? [d.block] : [])) this.opts.world.setBlock?.(b[0], b[1], b[2], 0, 0);
  }

  /** A hauled drop goes to the best storage that accepts it (by priority), or raises an alert. */
  private haul(c: Colony, p: PawnSim): void {
    const r = rng(hash(`${p.p.name}${c.seq}`));
    const options: Array<[number, number]> = [
      [17, 0],
      [4, 0],
      [3, 0],
      [295, 0],
      [367, 0],
      [319, 0],
      [287, 0],
    ];
    const [id, meta] = options[Math.floor(r() * options.length)]!;
    const order = ['critical', 'important', 'preferred', 'normal', 'low'];
    const best = [...c.storages.values()]
      .map((f) => f)
      .filter((f) => filterAccepts(STORY_DEFAULT_CATEGORIES, f.v.filter, id, meta) && (f.v.freeSlots ?? 1) > 0)
      .sort((a, b) => order.indexOf(a.v.priority) - order.indexOf(b.v.priority))[0];
    if (!best) {
      this.alert(c, 'warn', `No storage accepts ${storyItemLabel(id, meta).toLowerCase()}.`, p.p.name, {
        tab: 'storage',
        item: [id, meta],
      });
      return;
    }
    const s = best.v;
    const slot = s.contents.find((x) => x[0] === id && x[1] === meta && x[2] < 64);
    if (slot) slot[2]++;
    else {
      s.contents.push([id, meta, 1]);
      s.freeSlots = Math.max(0, (s.freeSlots ?? 1) - 1);
    }
    s.knownAt = this.iso();
    best.rev = this.bump(c);
  }
}

// ------------------------------------------------------------------ helpers

function inside(a: StoryArea, p: readonly number[], slackY = 0): boolean {
  return (
    p[0]! >= a[0] && p[0]! <= a[3] && p[2]! >= a[2] && p[2]! <= a[5] && p[1]! >= a[1] - slackY && p[1]! <= a[4] + slackY
  );
}

function groupTrees(logs: Array<[number, number, number]>): Array<Array<[number, number, number]>> {
  const left = new Map(logs.map((l) => [keyOf(l), l]));
  const out: Array<Array<[number, number, number]>> = [];
  for (const start of logs) {
    if (!left.has(keyOf(start))) continue;
    const tree: Array<[number, number, number]> = [];
    const stack = [start];
    left.delete(keyOf(start));
    while (stack.length) {
      const b = stack.pop()!;
      tree.push(b);
      for (const [k, o] of left)
        if (Math.abs(o[0] - b[0]) <= 1 && Math.abs(o[2] - b[2]) <= 1 && Math.abs(o[1] - b[1]) <= 1) {
          left.delete(k);
          stack.push(o);
        }
    }
    tree.sort((a, b) => a[1] - b[1]);
    out.push(tree);
  }
  return out;
}

function seedContents(p: readonly number[]): Array<[number, number, number]> {
  const r = rng(hash(keyOf(p)));
  const pool: Array<[number, number]> = [
    [4, 0],
    [17, 0],
    [5, 0],
    [263, 0],
    [295, 0],
    [296, 0],
    [280, 0],
    [3, 0],
  ];
  return Array.from({ length: 2 + Math.floor(r() * 4) }, () => {
    const [id, meta] = pool[Math.floor(r() * pool.length)]!;
    return [id, meta, 1 + Math.floor(r() * 64)] as [number, number, number];
  });
}

function buildLabel(kind: string, cells: number, material: unknown): string {
  const [id, meta] = materialKey(material as never);
  const what = storyItemLabel(id, meta).toLowerCase();
  const furniture = (STORY_FURNITURE as readonly string[]).includes(kind);
  if (furniture) {
    const [fid, fmeta] = materialKey(kind);
    return storyItemLabel(fid, fmeta);
  }
  const name =
    {
      wall: 'Wall',
      floor: 'Floor',
      roof: 'Roof',
      door: 'Door',
      window: 'Window',
      stairs: 'Stairs',
      fence: 'Fence',
      block: 'Blocks',
      remove: 'Clear blocks',
    }[kind] ?? kind;
  return kind === 'door' || kind === 'window' ? name : `${name} (${cells} ${what})`;
}

function orderLabel(kind: string): string {
  return (
    {
      move: 'Moving to the spot',
      attack: 'Attacking',
      chop: 'Chopping (ordered)',
      mine: 'Mining (ordered)',
      haul: 'Hauling (ordered)',
      build: 'Building (ordered)',
      hold: 'Holding position',
      stop: 'Stopping',
    }[kind] ?? kind
  );
}

function designationLabel(kind: string): string {
  return (
    {
      chop: 'Chopping a tree',
      mine: 'Mining',
      harvest: 'Harvesting',
      cut: 'Cutting plants',
      haul: 'Hauling',
      hunt: 'Hunting',
      deconstruct: 'Deconstructing',
    }[kind] ?? kind
  );
}

/** A directive's summary for its kind alone: "Chop in 4×3 (2 targets)". */
function directiveKindSummary(d: StoryDirectiveRequest['directive']): string {
  const area = d.area ? ` in ${Math.abs(d.area[3] - d.area[0]) + 1}×${Math.abs(d.area[5] - d.area[2]) + 1}` : '';
  return `${d.kind[0]!.toUpperCase()}${d.kind.slice(1)}${area}${d.targets?.length ? ` (${d.targets.length} targets)` : ''}`;
}

type DirectiveSpec = StoryDirectiveRequest['directive'];

/** The summaries of the directive kinds that say more than their name. */
const DIRECTIVE_SUMMARY: Partial<Record<string, (d: DirectiveSpec) => string>> = {
  text: (d) => d.text ?? 'Message',
  build: (d) => {
    const n = d.build?.length ?? 0;
    return `Build ${n} item${n === 1 ? '' : 's'}`;
  },
  craft: (d) => `Craft ${d.bill?.recipe ?? 'items'}`,
  move: (d) => `Move to ${d.targets?.[0]?.join(', ') ?? 'the spot'}`,
};

function directiveSummary(d: DirectiveSpec): string {
  return (DIRECTIVE_SUMMARY[d.kind] ?? directiveKindSummary)(d);
}

/** True when a world block is a log (exported for the mocks' world builders). */
export const isLog = (id: number) => LOG_IDS.has(id);

// ------------------------------------------------------------------ the materials pass (plan)

/** A catalogue entry: id, label, group, tier, item [id, meta], placed block [id, meta], build kinds, raw item, yield per raw. */
type ArchitectEntry = (typeof SIM_ARCHITECT)[number];
/** Which benches the colony has designated. */
interface PlanBenches {
  table: boolean;
  furnace: boolean;
}
/** A build's shortfall to craft at a bench (an auto bill). */
interface PlanWant {
  build: Key;
  item: string;
  recipe: string;
  n: number;
  bench: string;
}
/** How a build's shortfall is covered: crafted (via, the chain), short in raw material, a bench to make first. */
interface PlanCraft {
  crafting: number;
  via: string;
  chain: string[];
  raw: string[];
  station: StoryBuild['station'];
}

/** The catalogue entry of a placed block: by block and meta, else by item id, else by block id. */
function architectEntryOf(mid: number, mmeta: number): ArchitectEntry | undefined {
  return (
    SIM_ARCHITECT.find((e) => e[5][0] === mid && e[5][1] === mmeta) ??
    SIM_ARCHITECT.find((e) => e[4][0] === mid) ??
    SIM_ARCHITECT.find((e) => e[5][0] === mid)
  );
}

/** The recipe an auto bill crafts an entry with. */
function recipeOf(cat: ArchitectEntry): string {
  return STORY_DEFAULT_RECIPES.find((r) => r.output[0] === cat[4][0])?.id ?? `craft:${cat[0]}`;
}

/** Is the bench an entry is made at designated (a crafting table for workbench recipes and panes, a furnace to smelt)? */
function benchReady(cat: ArchitectEntry, smelt: boolean, benches: PlanBenches): boolean {
  const needsTable = cat[3] === 'workbench' || cat[0] === 'glass_pane';
  return (!needsTable || benches.table) && (!smelt || benches.furnace);
}

/** A waiting build's status from its shortfall: queued (nothing short), waiting (being crafted), blocked; others keep theirs. */
function planStatus(status: StoryBuild['status'], short0: number, crafting: number): StoryBuild['status'] {
  if (status !== 'queued' && status !== 'blocked' && status !== 'waiting') return status;
  return short0 === 0 ? 'queued' : crafting > 0 ? 'waiting' : 'blocked';
}

function planReason(short0: number, crafting: number, label: string): string | null {
  if (short0 === 0) return null;
  return crafting > 0
    ? `waiting for ${crafting} ${label.toLowerCase()} (being crafted)`
    : `need ${short0} ${label.toLowerCase()}`;
}

/** Keeps a bench's bill in a materials pass (its want taken from `wants`); true when that changed it (dropped or retargeted). */
function keepAutoBill(bill: StoryBill, bench: string, wants: PlanWant[], kept: StoryBill[]): boolean {
  if ((bill.forBuild ?? null) === null) {
    kept.push(bill);
    return false;
  }
  const w = wants.find(
    (x) => String(x.build) === String(bill.forBuild) && x.recipe === bill.recipe && x.bench === bench,
  );
  if (!w) return true;
  wants.splice(wants.indexOf(w), 1);
  kept.push(bill);
  if (bill.target === bill.done + w.n) return false;
  bill.target = bill.done + w.n;
  return true;
}

// ------------------------------------------------------------------ a pawn's tick

/** A job a pawn could take now: its priority, work-type column, skill, distance, and how it leases it. */
interface WorkCand {
  pri: number;
  col: number;
  skill: number;
  d: number;
  take: () => void;
}

/** Designations whose blocks go when done. */
const CLEARS_BLOCKS: ReadonlySet<string> = new Set(['chop', 'mine', 'cut', 'harvest', 'deconstruct']);

/** How near its goal a pawn must be to work: on the spot for a move, sleep or rest, else within reach. */
const arriveWithin = (kind: string): number =>
  kind.startsWith('order:move') || kind === 'sleep' || kind === 'free' ? 0.3 : 1.6;

/** An order's work: hold forever, a move none, others by kind. */
const orderWork = (kind: string): number => (kind === 'hold' ? Infinity : (WORK_MS[kind] ?? (kind === 'move' ? 0 : 1500)));

/** The food level a pawn stops to eat at: skittish pawns early, brave ones late. */
function hungerLine(pawn: StoryPawn): number {
  if (pawn.traits.some((t) => t.id === 'skittish')) return 9;
  return pawn.traits.some((t) => t.id === 'brave') ? 4 : 6;
}

/** What a pawn at its task shows as. */
function workState(t: NonNullable<PawnSim['task']>, drafted: boolean | null | undefined): string {
  if (t.kind === 'eat') return 'eating';
  if (t.kind === 'sleep') return 'sleeping';
  if (t.kind === 'free') return 'idle';
  if (t.type === 'attack') return 'fighting';
  return drafted ? 'drafted' : 'working';
}

/** Night owls work a quarter faster from 20:00 to 06:00. */
function workRate(pawn: StoryPawn, tod: number): number {
  const night = storyHour(tod) >= 20 || storyHour(tod) < 6;
  return pawn.traits.some((x) => x.id === 'night_owl') && night ? 1.25 : 1;
}

/** A bill with work left: not paused or suspended, forever or below its target. */
const billWanted = (b: StoryBill): boolean => !b.paused && !b.suspendedReason && (b.mode === 'forever' || b.done < b.target);

/** May this pawn do the bill (anyone or it, with the skill asked)? */
function billOpenTo(b: StoryBill, pawn: StoryPawn): boolean {
  if (b.pawn !== 'any' && b.pawn !== pawn.name) return false;
  return b.minSkill === null || b.minSkill === undefined || (pawn.skills.craft?.level ?? 0) >= b.minSkill;
}

/** A bill round made: its count, the output into stock; an `until` bill is done once the stock reaches its target. */
function stockMade(stock: Map<string, number>, bill: StoryBill, recipe: SimRecipe | undefined): void {
  const k = recipe ? `${recipe.output[0]}:${recipe.output[1]}` : '';
  bill.done++;
  if (recipe) stock.set(k, (stock.get(k) ?? 0) + recipe.output[2]);
  if (bill.mode === 'until' && (stock.get(k) ?? 0) >= bill.target) bill.done = bill.target;
}

/** The bench a build waits for, made and placed by home: a crafting table first, then a furnace. */
function autoStation(c: Colony, benches: PlanBenches): StoryBuild['station'] {
  return { kind: !benches.table ? 'crafting_table' : 'furnace', pos: [c.home[0] + 2, c.home[1], c.home[2]] };
}

/** Does the stock hold enough of a recipe input (any item of its category or options)? */
function hasInput(stock: ReadonlyMap<string, number>, i: (typeof STORY_DEFAULT_RECIPES)[number]['inputs'][number]): boolean {
  const keys = i.category
    ? categoryItems(STORY_DEFAULT_CATEGORIES, i.category)
    : (i.options ?? []).map(([a, m]) => `${a}:${m === -1 ? 0 : m}`);
  return keys.some((key) => (stock.get(key.replace(':-1', ':0')) ?? 0) >= i.count);
}

// ------------------------------------------------------------------ bills

/** What a bills request answers. */
type BillsAck = { ok: boolean; id?: Key; ids?: Key[]; rev: number; note?: string | null };
type BillSpec = Extract<StoryBillsRequest, { op: 'add' }>['bill'];

/** The enchantment tools are enchanted toward for a material: fortune for ores, silk touch for glass and ice, else efficiency. */
function enchantFor(material: string | undefined): string {
  if (material && /ore|coal|diamond|lapis|redstone|emerald|quartz/.test(material)) return 'fortune';
  return material && /glass|ice|glowstone/.test(material) ? 'silk_touch' : 'efficiency';
}

/** A new bill from its spec: defaults for what it leaves out; an enchanting table's option, a furnace's fuel. */
function newBill(id: string, recipe: SimRecipe, spec: BillSpec): StoryBill {
  return {
    id,
    recipe: recipe.id,
    mode: spec.mode,
    target: spec.target ?? (spec.mode === 'until' ? 64 : 1),
    done: 0,
    paused: spec.paused ?? false,
    suspendedReason: null,
    ingredients: spec.ingredients ?? { allow: recipeIngredients(recipe), deny: [] },
    radius: spec.radius ?? 32,
    pawn: spec.pawn ?? 'any',
    minSkill: spec.minSkill ?? null,
    store: spec.store ?? 'best',
    ...enchantFields(recipe, spec),
    ...fuelFields(recipe, spec),
  };
}

/** A recipe's inputs as a bill's ingredient filter (categories, item keys). */
const recipeIngredients = (recipe: SimRecipe): string[] =>
  recipe.inputs.flatMap((i) => (i.category ? [i.category] : (i.options ?? []).map(([a, m]) => `${a}:${m}`)));

function enchantFields(recipe: SimRecipe, spec: BillSpec): Partial<StoryBill> {
  if (recipe.workbench !== 'enchanting_table') return {};
  return { option: spec.option ?? 3, prefer: spec.prefer ?? null, preferOnly: spec.preferOnly ?? false };
}

/** A furnace bill's own fuel (`inherit` or none: the bench's). */
function fuelFields(recipe: SimRecipe, spec: BillSpec): Partial<StoryBill> {
  if (recipe.workbench !== 'furnace') return {};
  return { fuel: spec.fuel && spec.fuel !== 'inherit' ? spec.fuel : null };
}

// ------------------------------------------------------------------ designations, builds, storage

type DesignationsAck = {
  ok: boolean;
  count: number;
  targets?: Array<[number, number, number]>;
  entities?: number[];
  rev: number;
};

type BuildsAck = {
  ok: boolean;
  ids?: Key[];
  rev: number;
  warnings?: string[];
  flow?: { spread: number; cells: Array<[number, number, number]> };
  cells?: number;
  done?: number;
  error?: string | null;
  reopened?: number;
  reasons?: Array<{ id: Key; reason: string }>;
};

/** Water items checked: why not (null = fine), warnings, the expected spread, cells and those already full. */
interface WaterCheck {
  error: string | null;
  warnings: string[];
  spread: Array<[number, number, number]>;
  cells: number;
  done: number;
}

/** Designates entities (drops, animals) rather than blocks: haul, hunt, and forbid / allow without blocks. */
function designatesEntities(kind: StoryDesignationKind, body: StoryDesignationRequest): boolean {
  return kind === 'haul' || kind === 'hunt' || ((kind === 'forbid' || kind === 'allow') && !body.blocks);
}

/** Is a designation named by a remove request's ids, blocks or entities? */
function designationListed(
  d: StoryDesignation,
  k: Key,
  cells: ReadonlyArray<readonly number[]>,
  body: StoryDesignationRequest,
): boolean {
  return !!(
    body.ids?.map(String).includes(String(k)) ||
    body.blocks?.some((b) => cells.some((p) => keyOf(p) === keyOf(b))) ||
    (d.entity !== null && d.entity !== undefined && body.entities?.includes(d.entity))
  );
}

/** Reopens a build's failed cells (blocked, or with a failure's reason; only `cells` when given); how many. */
function reopenCells(list: GhostCell[], cells: ReadonlySet<string> | null): number {
  let n = 0;
  for (const g of list)
    if (reopenable(g) && (!cells || cells.has(`${g[0]},${g[1]},${g[2]}`))) {
      g[6] = 'todo';
      g.length = 8;
      n++;
    }
  return n;
}

const reopenable = (g: GhostCell): boolean =>
  g[6] === 'blocked' || (!!g[8] && g[6] !== 'done' && g[6] !== 'unsupported');

/** Is a cell dug first: by a remove item in the request, or a dig already queued? */
function isDugCell(
  x: number,
  y: number,
  z: number,
  dug: ReadonlyArray<[number, number, number]>,
  queuedDigs: readonly GhostCell[],
): boolean {
  return (
    dug.some((d) => d[0] === x && d[1] === y && d[2] === z) ||
    queuedDigs.some((g) => g[0] === x && g[1] === y && g[2] === z)
  );
}

/** A water build's label: its name, else an infinite source, a source, or a channel. */
function waterBuildLabel(item: StoryBuildItem, liquid: string, cells: number): string {
  if (item.name !== null && item.name !== undefined) return item.name;
  if (item.infinite) return `infinite ${liquid} source (${cells} cells)`;
  return cells === 1 ? `${liquid} source` : `${liquid} channel (${cells} sources)`;
}

/** A build short of its material at queue time (removals, mines, water and furniture never are). */
function lacksMaterial(item: StoryBuildItem, need: number, have: number): boolean {
  return (
    have < need &&
    item.kind !== 'remove' &&
    item.kind !== 'mine' &&
    item.kind !== 'water' &&
    !(STORY_FURNITURE as readonly string[]).includes(item.kind)
  );
}

/** Workbench block ids and their kinds. */
const BENCH_KIND_OF: ReadonlyMap<number, string> = new Map([
  [58, 'crafting_table'],
  [61, 'furnace'],
  [62, 'furnace'],
  [117, 'brewing_stand'],
  [145, 'anvil'],
  [116, 'enchanting_table'],
]);

/** A storage's priority and filter: as given, else as it was, else normal and everything. */
function storageSettings(
  prev: StoryStorage | undefined,
  filter: StoryFilter | undefined,
  priority: string | undefined,
): Pick<StoryStorage, 'priority' | 'filter'> {
  return {
    priority: priority ?? prev?.priority ?? 'normal',
    filter: filter ?? prev?.filter ?? { allow: ['*'], deny: [] },
  };
}

// ------------------------------------------------------------------ zones

type ZoneAdd = Extract<StoryZoneRequest, { op: 'add' }>;

/** A new zone's request: plant / sow / harvest only on growing zones (a known plant); a new quarry at least 2×2 unless it
 * extends one it touches. */
function checkNewZone(body: ZoneAdd, kind: string, list: readonly StoryZone[], rect: readonly [number, number, number, number]): void {
  if (body.plant || body.sow !== undefined || body.harvest !== undefined) checkGrowFields(body.plant, kind);
  if (
    kind === 'mine' &&
    (body.mode ?? 'quarry') === 'quarry' &&
    (rect[0] === rect[2] || rect[1] === rect[3]) &&
    !list.some((z) => z.kind === 'mine' && z.mode === 'quarry' && cellsTouch(zoneCellSet(z), rectCells(...rect)))
  )
    throw Object.assign(new Error(QUARRY_TOO_NARROW), { status: 400 });
}

function checkGrowFields(plant: string | undefined, kind: string): void {
  if (kind !== 'farm') throw Object.assign(new Error('only farm (growing) zones take plant, sow and harvest'), { status: 400 });
  if (plant && !STORY_SIM_PLANTS.some((p) => p.id === plant))
    throw Object.assign(new Error(`plant must be one of ${STORY_SIM_PLANTS.map((p) => p.id).join('|')}`), {
      status: 400,
    });
}

/** A painted zone: its kind and area, a name, a mine zone's mode, a growing zone's plant with its first planting bill. */
function newZone(c: Colony, body: ZoneAdd, a: StoryArea, newId: () => string, k: string, plant: string | null): StoryZone {
  return {
    id: newId(),
    kind: k,
    area: a,
    ...(body.name ? { name: body.name } : {}),
    ...(k === 'mine' ? { mode: body.mode ?? 'quarry' } : {}),
    ...(k === 'farm' && plant ? growingFields(c, body, plant) : {}),
  };
}

function growingFields(c: Colony, body: ZoneAdd, plant: string): Partial<StoryZone> {
  return {
    plant,
    sow: body.sow ?? true,
    harvest: body.harvest ?? true,
    bills: [{ id: `gb${c.seq++}`, plant, mode: 'forever' as const, target: 64, done: 0, paused: false }],
  };
}

/** What a zones request answers (one shape per op: a mode, seeds, irrigation, a bill, a rename, a delete, a paint). */
type ZonesAnswer =
  | { id: string | number; warnings: never[]; rev: number; count?: undefined; kind?: undefined }
  | { count: number; kind: string; rev: number; id?: undefined; warnings?: undefined }
  | {
      rev: number;
      cells: [number, number, number][];
      dig: [number, number, number][];
      axis: 'x' | 'z';
      lines: number;
      note: string;
      id: string | number;
    }
  | {
      rev: number;
      bill?: string | undefined;
      id: string | number;
      ids: (string | number)[];
      activeBill: string | number | null;
      warnings: never[];
    }
  | { id: string | number; rev: number; name?: undefined; removed?: undefined; deleted?: undefined }
  | { id: string | number; name: string; rev: number; removed?: undefined; deleted?: undefined }
  | { removed: (string | number)[]; deleted: (string | number)[]; rev: number; id?: undefined; name?: undefined }
  | {
      removed: (string | number)[];
      rev: number;
      id?: string | number;
      created: Array<string | number>;
      merged: Array<{ into: string | number; from: Array<string | number> }>;
      grown: Array<string | number>;
      shrunk: Array<string | number>;
      split: Array<{ from: string | number; into: Array<string | number> }>;
      deleted: Array<string | number>;
      name?: undefined;
    };
