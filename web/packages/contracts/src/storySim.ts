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
  CHEST_IDS,
  expandBuildItem,
  LOG_IDS,
  matchesTool,
  ANIMAL_TYPES,
  materialKey,
  normArea,
  storyHour,
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
  type StoryPawnsRequest,
  type StoryPawnsResponse,
  type StoryPlant,
  type StoryStorage,
  type StoryStorageRequest,
  type StoryWorkbench,
  type StoryZone,
  type StoryZoneRequest,
} from './story.js';

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
  ghostCells: Map<Key, Array<[number, number, number, number, number, Key, string]>>;
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

const SEED_HINT: Record<string, string> = {
  wheat: 'No wheat seeds — cut tall grass to find some',
  carrots: 'Needs carrots — none in storage (harvests, zombie drops, village farms)',
  potatoes: 'Needs potatoes — none in storage (harvests, zombie drops, village farms)',
  pumpkin: 'No pumpkin seeds — store a pumpkin and a crafting table makes them',
  melon: 'No melon seeds — store a melon slice and a crafting table makes them',
};

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
const DEFAULT_SCHEDULE = Array.from({ length: 24 }, (_, h) =>
  h < 14 ? 'work' : h < 16 ? 'anything' : h < 17 ? 'free' : 'sleep',
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
      options: { autoPlaceBenches: true, detectBenches: false },
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
      skills[t] = { level: Math.floor(r() * 15) + (r() < 0.2 ? 5 : 0), xp: 0, passion: r() < 0.25 };
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
        schedule: [...DEFAULT_SCHEDULE],
        held: null,
        inventory: [],
        xpLevel: Math.floor(r() * 18),
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
    const removed: Record<string, Array<Key | [number, number, number]>> = {};
    if (!full) for (const r of c.removed) if (r.rev > s) (removed[r.kind] ??= []).push(r.key);
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
      seats: {
        online: [...c.pawns.values()].filter((p) => p.p.online).length,
        wanted: Math.max(c.wanted ?? 0, c.pawns.size),
        capFree: this.seatsOf(c).capFree,
        keptForRegulars: this.seatsOf(c).kept,
        pending: Math.max(0, (c.wanted ?? 0) - [...c.pawns.values()].filter((p) => !p.p.leaving).length),
      },
      zones: pick(c.zones).map((z) => this.growStatus(c, z)),
      ...(full ? { plants: STORY_SIM_PLANTS } : {}),
      designations: pick(c.designations),
      orders: pick(c.orders),
      alerts: c.alerts
        .filter((a) => full || a.rev > s)
        .map((a) => a.v)
        .slice(-40),
      builds: pick(c.builds),
      ...(full || c.ghostsRev > s ? { ghosts: { rev: c.ghostsRev, blocks: [...c.ghostCells.values()].flat() } } : {}),
      ...(full ? { categories: STORY_DEFAULT_CATEGORIES } : {}),
      storages: pick(c.storages),
      workbenches: pick(c.benches),
      ...(full || c.bedsRev > s ? { beds: this.bedList(c) } : {}),
      ai: this.opts.world.ai?.() ?? [],
      directives: pick(c.directives),
      ...(Object.keys(removed).length ? { removed } : {}),
    };
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
    const names: string[] = [];
    if (body.pawns === null) {
      for (const p of c.pawns.values()) if (!p.p.online) names.push(p.p.name);
    } else
      for (const n of body.pawns) {
        const p = c.pawns.get(n) ?? [...c.pawns.values()].find((x) => x.p.display.toLowerCase() === n.toLowerCase());
        if (!p) throw Object.assign(new Error(`no pawn '${n}'`), { status: 404 });
        names.push(p.p.name);
      }
    const started: string[] = [];
    const blocked: StoryOnlineBlocked[] = [];
    for (const n of names) {
      const p = c.pawns.get(n)!;
      if (c.released) {
        blocked.push({ pawn: n, reason: 'the colony is released: resume it first' });
        continue;
      }
      if (p.p.online) {
        started.push(n);
        continue;
      }
      // A hosted pawn (kicked "not white-listed") is re-added and retried now; one that never got a seat needs one.
      const hosted = p.backAt !== null && p.backAt !== undefined;
      if (!hosted && c.capFree - 1 < (body.overCap ? 0 : c.kept)) {
        p.p.offlineReason = this.capReason(c);
        p.rev = this.bump(c);
        blocked.push({ pawn: n, reason: p.p.offlineReason });
        continue;
      }
      if (!hosted) c.capFree--;
      p.p.offlineReason = 'connecting';
      p.backAt = this.now() + 1500;
      p.rev = this.bump(c);
      started.push(n);
    }
    if (started.length)
      this.alert(c, 'info', `Bringing ${started.length} pawn${started.length === 1 ? '' : 's'} online.`);
    return { started, blocked };
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
      [...c.zones.values()].find(
        (z) =>
          z.v.kind === 'home' &&
          p[0]! >= z.v.area[0] &&
          p[0]! <= z.v.area[3] &&
          p[2]! >= z.v.area[2] &&
          p[2]! <= z.v.area[5],
      )?.v.id ?? null;
    return [...c.beds.values()].map((b) => ({ ...b, zone: zoneOf(b.pos) }));
  }

  /** Beds: give one to a pawn (its other bed is freed: one bed a pawn) or free one. */
  beds(id: string, body: StoryBedsRequest) {
    const c = this.colony(id);
    const bp = body?.bed;
    if (!bp || ![bp.x, bp.y, bp.z].every((v) => Number.isInteger(v)))
      throw Object.assign(new Error('bed must be {x,y,z}'), { status: 400 });
    // The head half names the bed too (a bed is named by its foot).
    const bed =
      c.beds.get(keyOf([bp.x, bp.y, bp.z])) ??
      [...c.beds.values()].find(
        (b) => b.pos[1] === bp.y && Math.abs(b.pos[0] - bp.x) + Math.abs(b.pos[2] - bp.z) === 1,
      );
    if (!bed) throw Object.assign(new Error('no bed there'), { status: 400 });
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

  priorities(id: string, body: { pawns: string[]; work: Partial<Record<string, number>> }) {
    const c = this.colony(id);
    for (const p of this.pawnsOf(c, body.pawns)) {
      Object.assign(p.p.priorities, body.work);
      p.rev = this.bump(c);
    }
    return { ok: true, rev: c.rev };
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

  zones(id: string, body: StoryZoneRequest) {
    const c = this.colony(id);
    if (body.op === 'update' || body.op === 'seek_seeds') return this.growOp(c, body);
    if (body.op === 'rename') {
      const f = c.zones.get(String(body.id));
      if (!f) throw Object.assign(new Error('no such zone'), { status: 404 });
      f.v = { ...f.v, name: body.name || null };
      f.rev = this.bump(c);
      return { id: f.v.id, name: f.v.name ?? '', rev: c.rev };
    }
    const drop = (k: Key, f: Fn<StoryZone>) => {
      c.zones.delete(k);
      c.removed.push({ rev: this.bump(c), kind: 'zones', key: k });
      if (f.v.kind === 'home') c.bedsRev = this.bump(c);
    };
    if (body.op === 'remove' && 'id' in body) {
      const f = c.zones.get(String(body.id));
      if (!f) throw Object.assign(new Error('no such zone'), { status: 404 });
      drop(String(body.id), f);
      return { removed: [body.id], rev: c.rev };
    }
    if (!('area' in body)) throw Object.assign(new Error('area must be [x1,y1,z1,x2,y2,z2]'), { status: 400 });
    const a = normArea(body.area);
    if (body.op === 'add') {
      const z: StoryZone = {
        id: `z${c.seq++}`,
        kind: body.kind ?? 'home',
        area: a,
        ...(body.name ? { name: body.name } : {}),
      };
      if (body.plant || body.sow !== undefined || body.harvest !== undefined) {
        if (z.kind !== 'farm')
          throw Object.assign(new Error('only farm (growing) zones take plant, sow and harvest'), { status: 400 });
        if (body.plant && !STORY_SIM_PLANTS.some((p) => p.id === body.plant))
          throw Object.assign(new Error(`plant must be one of ${STORY_SIM_PLANTS.map((p) => p.id).join('|')}`), {
            status: 400,
          });
        Object.assign(z, { plant: body.plant ?? null, sow: body.sow ?? true, harvest: body.harvest ?? true });
      }
      c.zones.set(z.id, { rev: this.bump(c), v: z });
      if (z.kind === 'home') c.bedsRev = this.bump(c); // beds in it now belong to that house
      return { id: z.id, rev: c.rev };
    }
    const removed: Array<string | number> = [];
    for (const [k, f] of c.zones)
      if ((!body.kind || f.v.kind === body.kind) && overlaps(f.v.area, a)) {
        drop(k, f);
        removed.push(f.v.id);
      }
    return { removed, rev: c.rev };
  }

  /** A growing zone's fields as the bot reports them (mock: nothing grows; seeds from the colony's stock). */
  private growStatus(c: Colony, z: StoryZone): StoryZone {
    const plant = z.kind === 'farm' && z.plant ? STORY_SIM_PLANTS.find((p) => p.id === z.plant) : undefined;
    if (!plant) return z;
    const [x1, , z1, x2, , z2] = z.area;
    const cells = (x2 - x1 + 1) * (z2 - z1 + 1);
    const have = c.stock.get(`${plant.seedItem![0]}:${plant.seedItem![1]}`) ?? 0;
    return {
      ...z,
      status: `0/${cells} sown, 0 ripe`,
      seeds: {
        item: plant.seed,
        have,
        neededForEmpty: cells,
        sourceHint: plant.source ?? null,
        missing:
          have === 0 ? (SEED_HINT[plant.id] ?? `No ${plant.label.toLowerCase()} to sow — ${plant.source}`) : null,
        action:
          have === 0 ? (plant.id === 'wheat' ? 'seek_seeds' : plant.harvest === 'fruit' ? 'craft_seeds' : null) : null,
      },
      needsHoe: plant.soil === 'farmland' && (c.stock.get('290:0') ?? 0) === 0,
    };
  }

  private growOp(c: Colony, body: Extract<StoryZoneRequest, { op: 'update' | 'seek_seeds' }>) {
    const f = c.zones.get(String(body.id));
    if (!f) throw Object.assign(new Error('id: no such zone'), { status: 404 });
    if (body.op === 'update') {
      if (f.v.kind !== 'farm')
        throw Object.assign(new Error('only farm (growing) zones take plant, sow and harvest'), { status: 400 });
      if (body.plant && !STORY_SIM_PLANTS.some((p) => p.id === body.plant))
        throw Object.assign(new Error(`plant must be one of ${STORY_SIM_PLANTS.map((p) => p.id).join('|')}`), {
          status: 400,
        });
      f.v = {
        ...f.v,
        ...(body.plant !== undefined ? { plant: body.plant || null } : {}),
        ...(body.sow !== undefined ? { sow: body.sow } : {}),
        ...(body.harvest !== undefined ? { harvest: body.harvest } : {}),
      };
      f.rev = this.bump(c);
      return { id: f.v.id, warnings: [], rev: c.rev };
    }
    const plant = STORY_SIM_PLANTS.find((p) => p.id === f.v.plant);
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
    const staying = () => [...c.pawns.values()].filter((p) => !p.p.leaving);
    const answer = (why: string | null): StoryPawnsResponse => {
      const here = staying().map((p) => p.p.name);
      const pending = Math.max(0, (c.wanted ?? here.length) - here.length);
      return {
        pawns: here,
        wanted: c.wanted ?? here.length,
        pending: Array.from({ length: pending }, () => ({ reason: why ?? 'waiting for a seat' })),
        leaving: [...c.pawns.values()].filter((p) => p.p.leaving).map((p) => p.p.name),
        blocked: why && pending ? [{ reason: why }] : [],
      };
    };
    if (body.op === 'add') {
      if (c.released) throw Object.assign(new Error('the colony is released: resume it first'), { status: 400 });
      const wanted = Math.max(c.wanted ?? 0, staying().length);
      if (wanted + body.count > 12)
        throw Object.assign(new Error(`a colony has at most 12 pawns (${wanted} now)`), { status: 400 });
      c.wanted = wanted + body.count;
      const now = Math.max(0, Math.min(body.count, c.capFree - c.kept));
      for (let i = 0; i < now; i++) {
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
      c.capFree -= now;
      const why = now < body.count ? this.capReason(c) : null;
      this.alert(
        c,
        'info',
        `${body.count} pawn${body.count === 1 ? '' : 's'} joining${why ? ` (${body.count - now} wait: ${why})` : ''}.`,
      );
      return answer(why);
    }
    const p =
      c.pawns.get(body.pawn) ??
      [...c.pawns.values()].find((x) => x.p.display.toLowerCase() === body.pawn.toLowerCase());
    if (!p) throw Object.assign(new Error(`no pawn '${body.pawn}'`), { status: 404 });
    if (p.p.leaving) return answer(null);
    if (staying().length <= 1)
      throw Object.assign(new Error("the last pawn can't be removed: release the colony instead"), { status: 400 });
    this.drop(c, p);
    p.p.leaving = true;
    p.rev = this.bump(c);
    c.wanted = Math.max(staying().length, (c.wanted ?? staying().length + 1) - 1);
    for (const [k, b] of c.beds) if (b.pawn === p.p.name) c.beds.set(k, { ...b, pawn: null });
    c.bedsRev = this.bump(c);
    this.alert(c, 'info', `${p.p.display} is leaving the colony: its things go to storage first.`, p.p.name);
    setTimeout(() => {
      if (!c.pawns.delete(p.p.name)) return;
      c.removed.push({ rev: this.bump(c), kind: 'pawns', key: p.p.name });
      this.alert(c, 'info', `${p.p.display} left the colony.`);
    }, 1500);
    return answer(null);
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

  designations(
    id: string,
    body: StoryDesignationRequest,
  ): { ok: boolean; count: number; targets?: Array<[number, number, number]>; entities?: number[]; rev: number } {
    const c = this.colony(id);
    const a = body.area ? normArea(body.area) : null;
    if (body.op === 'retry') {
      // The failed designations named by `ids` are tried again now (their rest is over).
      const ids = (body.ids ?? []).map(String);
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
    if (body.op === 'remove' || body.op === 'cancel') {
      let n = 0;
      for (const [k, f] of c.designations) {
        const d = f.v;
        if (body.kind && d.kind !== body.kind) continue;
        const cells = d.targets ?? (d.block ? [d.block] : []);
        const inArea = a
          ? cells.some((p) => inside(a, p)) ||
            (d.entity !== null && d.entity !== undefined && this.entityIn(d.entity, a))
          : false;
        const listed =
          body.ids?.map(String).includes(String(k)) ||
          body.blocks?.some((b) => cells.some((p) => keyOf(p) === keyOf(b))) ||
          (d.entity !== null && d.entity !== undefined && body.entities?.includes(d.entity));
        if (!inArea && !listed) continue;
        c.designations.delete(k);
        c.removed.push({ rev: this.bump(c), kind: 'designations', key: k });
        for (const p of c.pawns.values()) if (p.task?.ref === k) this.drop(c, p);
        n++;
      }
      return { ok: true, count: n, rev: c.rev };
    }
    const kind = body.kind ?? 'mine';
    const held = new Set(
      [...c.designations.values()].flatMap((f) => (f.v.targets ?? (f.v.block ? [f.v.block] : [])).map(keyOf)),
    );
    const heldEnt = new Set(
      [...c.designations.values()].map((f) => f.v.entity).filter((e) => e !== null && e !== undefined),
    );
    const add = (d: Omit<StoryDesignation, 'id'>) => {
      const des: StoryDesignation = { id: `d${c.seq++}`, ...d };
      c.designations.set(des.id, { rev: this.bump(c), v: des });
    };
    if (kind === 'haul' || kind === 'hunt' || ((kind === 'forbid' || kind === 'allow') && !body.blocks)) {
      const ents = this.opts.world
        .entities()
        .filter((e) => (kind === 'hunt' ? e.kind === 'mob' && ANIMAL_TYPES.has(e.type ?? -1) : e.kind === 'item'))
        .filter((e) =>
          a ? inside(a, [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], 2) : body.entities?.includes(e.id),
        );
      const fresh = ents.filter((e) => !heldEnt.has(e.id));
      for (const e of fresh)
        add({ kind, entity: e.id, block: [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], state: 'todo' });
      // Doors too, for forbid/allow over an area.
      const doors = a && (kind === 'forbid' || kind === 'allow') ? this.scan(a, kind, undefined) : [];
      for (const b of doors) if (!held.has(keyOf(b))) add({ kind, block: b, state: 'todo' });
      return {
        ok: true,
        count: fresh.length + doors.length,
        entities: fresh.map((e) => e.id),
        targets: doors,
        rev: c.rev,
      };
    }
    const cells = a
      ? this.scan(a, kind, body.filter)
      : (body.blocks ?? []).map((b) => [b[0], b[1], b[2]] as [number, number, number]);
    const fresh = cells.filter((p) => !held.has(keyOf(p)));
    if (kind === 'chop') {
      // One designation per tree: its log columns that touch, lowest log first.
      const byTree = groupTrees(fresh);
      for (const logs of byTree) add({ kind, block: logs[0], targets: logs, state: 'todo' });
      return { ok: true, count: byTree.length, targets: fresh, rev: c.rev };
    }
    for (const b of fresh)
      add({
        kind,
        block: b,
        state: 'todo',
        ...(body.design !== null && body.design !== undefined ? { design: body.design } : {}),
      });
    return { ok: true, count: fresh.length, targets: fresh, rev: c.rev };
  }

  private entityIn(id: number, a: StoryArea): boolean {
    const e = this.opts.world.entities().find((x) => x.id === id);
    return !!e && inside(a, [Math.floor(e.x), Math.floor(e.y), Math.floor(e.z)], 2);
  }

  builds(id: string, body: StoryBuildRequest): { ok: boolean; ids?: Key[]; rev: number } {
    const c = this.colony(id);
    if (body.op === 'add') {
      const ids: Key[] = [];
      for (const item of body.items) {
        const bid = `b${c.seq++}`;
        const cells = expandBuildItem(item);
        const [mid, mmeta] = materialKey(item.material);
        const order = Math.max(0, ...[...c.builds.values()].map((f) => f.v.order)) + 1;
        const need = cells.filter((b) => b[3] !== 0).length;
        const have = c.stock.get(`${mid}:${mmeta}`) ?? 0;
        const b: StoryBuild = {
          id: bid,
          kind: item.kind,
          label: buildLabel(item.kind, cells.length, item.material),
          status: 'queued',
          reason:
            have < need && item.kind !== 'remove' && !(STORY_FURNITURE as readonly string[]).includes(item.kind)
              ? `needs ${need - have} more ${storyItemLabel(mid, mmeta).toLowerCase()}`
              : null,
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
          cells.map(([x, y, z, bi, bm]) => [x, y, z, bi, bm, bid, 'todo']),
        );
        ids.push(bid);
      }
      c.ghostsRev = this.bump(c);
      this.plan(c);
      return { ok: true, ids, rev: c.rev };
    }
    const ids = body.ids.map(String);
    if (body.op === 'reorder') {
      // The listed builds take the front of the queue in the listed order; the rest keep theirs after them.
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
    for (const bid of ids) {
      const f = c.builds.get(bid);
      if (!f) continue;
      if (body.op === 'remove') {
        c.builds.delete(bid);
        c.ghostCells.delete(bid);
        c.removed.push({ rev: this.bump(c), kind: 'builds', key: bid });
        for (const p of c.pawns.values()) if (p.task?.ref === bid) this.drop(c, p);
      } else {
        f.v.status = body.op === 'pause' ? 'paused' : 'queued';
        f.rev = this.bump(c);
      }
    }
    c.ghostsRev = this.bump(c);
    this.plan(c);
    return { ok: true, rev: c.rev };
  }

  /** Registers the chests and workbenches that stand in an area. */
  private discover(
    c: Colony,
    area: StoryArea,
    filter?: StoryFilter,
    priority?: string,
  ): Array<[number, number, number]> {
    const w = this.opts.world;
    const a = normArea(area);
    const found: Array<[number, number, number]> = [];
    for (let x = a[0]; x <= a[3]; x++)
      for (let z = a[2]; z <= a[5]; z++)
        for (let y = Math.max(1, a[1]); y <= Math.min(255, a[4]); y++) {
          const [id] = w.blockAt(x, y, z);
          if (CHEST_IDS.has(id)) found.push([x, y, z]);
          else if (id === 26 && (w.blockAt(x, y, z)[1] & 8) === 0 && !c.beds.has(keyOf([x, y, z]))) {
            c.beds.set(keyOf([x, y, z]), { pos: [x, y, z], dim: 0, pawn: null, zone: null });
            c.bedsRev = this.bump(c);
          } else if (id === 58 || id === 61 || id === 62 || id === 117 || id === 145 || id === 116) {
            const k = keyOf([x, y, z]);
            if (!c.benches.has(k)) {
              const kind =
                id === 58
                  ? 'crafting_table'
                  : id === 117
                    ? 'brewing_stand'
                    : id === 145
                      ? 'anvil'
                      : id === 116
                        ? 'enchanting_table'
                        : 'furnace';
              c.benches.set(k, {
                rev: this.bump(c),
                v: {
                  pos: [x, y, z],
                  kind,
                  label: null,
                  supported: true,
                  station: this.stationOf(kind, [x, y, z]),
                  bills: [],
                },
              });
            }
          }
        }
    if (filter === undefined && priority === undefined) return [];
    const out: Array<[number, number, number]> = [];
    const taken = new Set<string>();
    for (const p of found) {
      if (taken.has(keyOf(p))) continue;
      // Double chests are one storage: the other half is a chest next to it.
      const other = found.find((q) => q !== p && Math.abs(q[0] - p[0]) + Math.abs(q[2] - p[2]) === 1 && q[1] === p[1]);
      if (other) taken.add(keyOf(other));
      taken.add(keyOf(p));
      const k = keyOf(p);
      const prev = c.storages.get(k)?.v;
      const s: StoryStorage = {
        chest: p,
        doubleWith: other ?? null,
        label: prev?.label ?? null,
        priority: priority ?? prev?.priority ?? 'normal',
        filter: filter ?? prev?.filter ?? { allow: ['*'], deny: [] },
        contents: prev?.contents ?? seedContents(p),
        freeSlots: prev?.freeSlots ?? (other ? 46 : 22),
        knownAt: this.iso(),
      };
      c.storages.set(k, { rev: this.bump(c), v: s });
      out.push(p);
    }
    return out;
  }

  storage(
    id: string,
    body: StoryStorageRequest,
  ): { ok: boolean; count: number; chests?: Array<[number, number, number]>; rev: number } {
    const c = this.colony(id);
    const find = (p: readonly number[]) =>
      c.storages.get(keyOf(p)) ??
      [...c.storages.values()].find((f) => f.v.doubleWith && keyOf(f.v.doubleWith) === keyOf(p));
    switch (body.op) {
      case 'assign_area': {
        const chests = this.discover(c, body.area, body.filter ?? undefined, body.priority ?? 'normal');
        return { ok: true, count: chests.length, chests, rev: c.rev };
      }
      case 'set': {
        let n = 0;
        for (const p of body.chests) {
          let f = find(p);
          if (!f) {
            this.discover(c, [p[0], p[1], p[2], p[0], p[1], p[2]], body.filter, body.priority);
            f = find(p);
          }
          if (!f) continue;
          if (body.filter) f.v.filter = body.filter;
          if (body.priority) f.v.priority = body.priority;
          if (body.label !== undefined) f.v.label = body.label || null;
          f.rev = this.bump(c);
          n++;
        }
        return { ok: true, count: n, rev: c.rev };
      }
      case 'copy': {
        const src = find(body.from);
        if (!src) throw Object.assign(new Error('the source chest is not a storage'), { status: 400 });
        let n = 0;
        for (const p of body.to) {
          const f = find(p);
          if (!f) continue;
          f.v.filter = structuredClone(src.v.filter);
          f.v.priority = src.v.priority;
          f.rev = this.bump(c);
          n++;
        }
        return { ok: true, count: n, rev: c.rev };
      }
      case 'remove': {
        let n = 0;
        for (const p of body.chests) {
          const f = find(p);
          if (!f) continue;
          c.storages.delete(keyOf(f.v.chest));
          c.removed.push({ rev: this.bump(c), kind: 'storages', key: f.v.chest });
          n++;
        }
        return { ok: true, count: n, rev: c.rev };
      }
    }
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

  bills(id: string, body: StoryBillsRequest): { ok: boolean; id?: Key; ids?: Key[]; rev: number; note?: string | null } {
    const c = this.colony(id);
    const benchAt = (pos: readonly number[]) => {
      const f = c.benches.get(keyOf(pos));
      if (!f) throw Object.assign(new Error('no workbench there'), { status: 404 });
      if (f.v.supported === false)
        throw Object.assign(new Error(`this version has no ${f.v.kind.replace(/_/g, ' ')} bills`), { status: 400 });
      return f;
    };
    const holder = (billId: Key) => {
      for (const f of c.benches.values()) if (f.v.bills.some((b) => String(b.id) === String(billId))) return f;
      throw Object.assign(new Error('no such bill'), { status: 404 });
    };
    let f: Fn<StoryWorkbench>;
    let newId: Key | undefined;
    if (body.op === 'enchant_tools') {
      // The tools hook: a count bill per pickaxe kind at the colony's enchanting table, preferring the enchantment.
      const table = [...c.benches.values()].find((x) => x.v.kind === 'enchanting_table');
      if (!table) throw Object.assign(new Error('no enchanting table: place one and designate it'), { status: 400 });
      const prefer = body.prefer ?? (body.material && /ore|coal|diamond|lapis|redstone|emerald|quartz/.test(body.material) ? 'fortune' : body.material && /glass|ice|glowstone/.test(body.material) ? 'silk_touch' : 'efficiency');
      const ids: Key[] = [];
      for (const item of body.items ?? ['diamond_pickaxe']) {
        const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === `enchant:${item}`);
        if (!recipe || table.v.bills.some((b) => b.recipe === recipe.id && b.prefer === prefer)) continue;
        const b: StoryBill = {
          id: `bill${c.seq++}`, recipe: recipe.id, mode: 'count', target: body.count ?? 1, done: 0, paused: false, suspendedReason: null,
          ingredients: { allow: ['*'], deny: [] }, radius: 32, pawn: 'any', minSkill: null, store: 'best', option: 3, prefer,
          preferOnly: body.preferOnly ?? false,
        };
        table.v.bills.push(b);
        ids.push(b.id);
      }
      table.rev = this.bump(c);
      return { ok: true, ids, rev: c.rev };
    }
    if (body.op !== 'add' && body.op !== 'reorder') {
      const auto = [...c.benches.values()]
        .flatMap((x) => x.v.bills)
        .find((x) => String(x.id) === String(body.id) && (x.forBuild ?? null) !== null);
      if (auto && body.op !== 'remove')
        throw Object.assign(new Error(`an auto bill (for build ${auto.forBuild}) can only be cancelled`), {
          status: 400,
        });
      if (auto) {
        // Cancelled: that build waits for the item instead of crafting it.
        const bf = c.builds.get(String(auto.forBuild)) ?? c.builds.get(auto.forBuild!);
        if (bf && auto.forItem) {
          bf.v.noCraft = [...(bf.v.noCraft ?? []), auto.forItem];
          bf.rev = this.bump(c);
        }
      }
    }
    switch (body.op) {
      case 'add': {
        f = benchAt(body.bench);
        const spec = body.bill;
        const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === spec.recipe);
        if (!recipe) throw Object.assign(new Error('unknown recipe'), { status: 400 });
        if (recipe.workbench !== f.v.kind)
          throw Object.assign(new Error(`no recipe '${recipe.id}' at a ${f.v.kind}`), { status: 400 });
        if (recipe.modes && !recipe.modes.includes(spec.mode))
          throw Object.assign(new Error(`mode must be ${recipe.modes.join('|')} for this recipe`), { status: 400 });
        const b: StoryBill = {
          id: `bill${c.seq++}`,
          recipe: recipe.id,
          mode: spec.mode,
          target: spec.target ?? (spec.mode === 'until' ? 64 : 1),
          done: 0,
          paused: spec.paused ?? false,
          suspendedReason: null,
          ingredients: spec.ingredients ?? {
            allow: recipe.inputs.flatMap((i) =>
              i.category ? [i.category] : (i.options ?? []).map(([a, m]) => `${a}:${m}`),
            ),
            deny: [],
          },
          radius: spec.radius ?? 32,
          pawn: spec.pawn ?? 'any',
          minSkill: spec.minSkill ?? null,
          store: spec.store ?? 'best',
          ...(recipe.workbench === 'enchanting_table'
            ? { option: spec.option ?? 3, prefer: spec.prefer ?? null, preferOnly: spec.preferOnly ?? false }
            : {}),
        };
        f.v.bills.push(b);
        newId = b.id;
        break;
      }
      case 'update': {
        f = holder(body.id);
        const b = f.v.bills.find((x) => String(x.id) === String(body.id))!;
        const { op: _op, id: _id, ...fields } = body;
        Object.assign(b, fields);
        if (fields.paused === false) b.suspendedReason = null;
        break;
      }
      case 'remove':
        f = holder(body.id);
        f.v.bills = f.v.bills.filter((b) => String(b.id) !== String(body.id));
        break;
      case 'pause':
      case 'resume':
        f = holder(body.id);
        for (const b of f.v.bills)
          if (String(b.id) === String(body.id)) {
            b.paused = body.op === 'pause';
            if (body.op === 'resume') b.suspendedReason = null;
          }
        break;
      case 'reorder': {
        f = benchAt(body.bench);
        const ids = body.ids.map(String);
        const front = ids.map((i) => f.v.bills.find((b) => String(b.id) === i)).filter((b): b is StoryBill => !!b);
        f.v.bills = [...front, ...f.v.bills.filter((b) => !ids.includes(String(b.id)))];
        f.rev = this.bump(c);
        return { ok: true, ids: f.v.bills.map((b) => b.id), rev: c.rev };
      }
    }
    f.rev = this.bump(c);
    if (body.op === 'remove') this.plan(c);
    return { ok: true, ...(newId ? { id: newId } : {}), rev: c.rev };
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
    const hasTable = this.hasBench(c, 'crafting_table');
    const hasFurnace = this.hasBench(c, 'furnace');
    const wants: Array<{ build: Key; item: string; recipe: string; n: number; bench: string }> = [];
    for (const f of [...c.builds.values()].sort((a, b) => a.v.order - b.v.order)) {
      const b = f.v;
      if (b.status === 'done' || b.status === 'paused' || b.kind === 'remove') continue;
      // What the build places: its ghosts' block (a fence's fence, a door's door), else its material.
      const cells = (c.ghostCells.get(b.id) ?? []).filter((g) => g[3] !== 0);
      const [mid, mmeta] = cells[0] ? [cells[0][3], cells[0][4]] : materialKey(b.material);
      const left = cells.filter((g) => g[6] !== 'done').length;
      const cat =
        SIM_ARCHITECT.find((e) => e[5][0] === mid && e[5][1] === mmeta) ??
        SIM_ARCHITECT.find((e) => e[4][0] === mid) ??
        SIM_ARCHITECT.find((e) => e[5][0] === mid);
      const key = cat ? `${cat[4][0]}:${cat[4][1]}` : `${mid}:${mmeta}`;
      const have = Math.min(left, pool.get(key) ?? 0);
      pool.set(key, (pool.get(key) ?? 0) - have);
      const label = cat?.[1] ?? storyItemLabel(mid, mmeta);
      const short0 = left - have;
      let crafting = 0;
      let via = 'none';
      const chain: string[] = [];
      const raw: string[] = [];
      let station: StoryBuild['station'] = null;
      if (short0 > 0 && cat && cat[7] && !(b.noCraft ?? []).includes(cat[0])) {
        const [rid, rmeta] = cat[7].split(':').map(Number) as [number, number];
        const smelt = cat[3] === 'furnace';
        via = smelt ? 'smelt' : 'craft';
        chain.push(
          `${smelt ? 'smelt' : 'craft'} ${short0} ${label} from ${storyItemLabel(rid, rmeta)}${cat[3] === 'workbench' ? ' (table)' : ''}`,
        );
        const rawHave = pool.get(cat[7]) ?? 0;
        const can = Math.min(short0, Math.floor(rawHave * cat[8]));
        const needsTable = cat[3] === 'workbench' || cat[0] === 'glass_pane';
        const benchOk = (!needsTable || hasTable) && (!smelt || hasFurnace);
        if (can > 0) {
          pool.set(cat[7], rawHave - Math.ceil(can / cat[8]));
          crafting = can;
          if (benchOk) {
            const recipe = STORY_DEFAULT_RECIPES.find((r) => r.output[0] === cat[4][0])?.id ?? `craft:${cat[0]}`;
            wants.push({ build: b.id, item: cat[0], recipe, n: can, bench: smelt ? 'furnace' : 'crafting_table' });
          }
        }
        if (!benchOk && c.options.autoPlaceBenches)
          station = { kind: !hasTable ? 'crafting_table' : 'furnace', pos: [c.home[0] + 2, c.home[1], c.home[2]] };
        if (short0 - can > 0) raw.push(`${Math.ceil((short0 - can) / cat[8])} ${storyItemLabel(rid, rmeta)}`);
      } else if (short0 > 0) raw.push(`${short0} ${label}`);
      const mats: StoryBuildMaterial[] = left
        ? [{ item: cat?.[0] ?? key, label, need: left, have, crafting, short: short0 - crafting, via, chain, raw }]
        : [];
      const status =
        b.status === 'queued' || b.status === 'blocked' || b.status === 'waiting'
          ? short0 === 0
            ? 'queued'
            : crafting > 0
              ? 'waiting'
              : 'blocked'
          : b.status;
      const reason =
        short0 === 0
          ? null
          : crafting > 0
            ? `waiting for ${crafting} ${label.toLowerCase()} (being crafted)`
            : `need ${short0} ${label.toLowerCase()}`;
      if (
        JSON.stringify([mats, station, status, reason]) !==
        JSON.stringify([b.materials, b.station ?? null, b.status, b.reason ?? null])
      ) {
        b.materials = mats;
        b.station = station;
        b.status = status;
        b.reason = reason;
        f.rev = this.bump(c);
      }
    }
    // Auto bills follow the wants: targets track the shortfall, the rest go, new ones join the bench's front.
    for (const f of c.benches.values()) {
      let changed = false;
      const kept: StoryBill[] = [];
      for (const bill of f.v.bills) {
        if ((bill.forBuild ?? null) === null) {
          kept.push(bill);
          continue;
        }
        const w = wants.find(
          (x) => String(x.build) === String(bill.forBuild) && x.recipe === bill.recipe && x.bench === f.v.kind,
        );
        if (!w) {
          changed = true;
          continue;
        }
        wants.splice(wants.indexOf(w), 1);
        if (bill.target !== bill.done + w.n) {
          bill.target = bill.done + w.n;
          changed = true;
        }
        kept.push(bill);
      }
      for (const w of wants.filter((x) => x.bench === f.v.kind)) {
        kept.unshift({
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
        });
        changed = true;
      }
      for (let i = wants.length - 1; i >= 0; i--) if (wants[i]!.bench === f.v.kind) wants.splice(i, 1);
      if (changed) {
        f.v.bills = [
          ...kept.filter((x) => (x.forBuild ?? null) !== null),
          ...kept.filter((x) => (x.forBuild ?? null) === null),
        ];
        f.rev = this.bump(c);
      }
    }
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
    if (have) return { pos: body.pos, kind: have.v.kind, designated: true };
    const [bid] = this.opts.world.blockAt(body.pos[0], body.pos[1], body.pos[2]);
    const kind = BENCH_KIND[bid];
    if (!kind) throw Object.assign(new Error('no workbench there'), { status: 400 });
    c.benches.set(k, {
      rev: this.bump(c),
      v: { pos: body.pos, kind, label: null, supported: true, station: this.stationOf(kind, body.pos), bills: [] },
    });
    this.plan(c);
    return { pos: body.pos, kind, designated: true };
  }

  /** POST …/options. */
  options(id: string, body: StoryOptionsRequest): StoryColonyOptions {
    const c = this.colony(id);
    if (body.autoPlaceBenches === undefined && body.detectBenches === undefined)
      throw Object.assign(new Error('autoPlaceBenches or detectBenches required'), { status: 400 });
    if (body.autoPlaceBenches !== undefined) c.options.autoPlaceBenches = body.autoPlaceBenches;
    if (body.detectBenches !== undefined) c.options.detectBenches = body.detectBenches;
    this.bump(c);
    this.plan(c);
    return { ...c.options };
  }

  /** GET …/architect: the sim catalog with tiers unlocked by the colony's designated benches. */
  architect(id: string): StoryArchitectResponse {
    const c = this.colony(id);
    const table = this.hasBench(c, 'crafting_table');
    const furnace = this.hasBench(c, 'furnace');
    const entries: StoryArchitectEntry[] = SIM_ARCHITECT.map(
      ([eid, label, tab, tier, item, material, kinds, rawKey, per]) => {
        const needsTable = tier === 'workbench' || eid === 'glass_pane';
        const reason =
          needsTable && !table
            ? 'Needs a crafting table — place one or designate an existing one'
            : tier === 'furnace' && !furnace
              ? 'Needs a furnace — place one or designate an existing one'
              : null;
        const have = c.stock.get(`${item[0]}:${item[1]}`) ?? 0;
        const rawHave = rawKey ? (c.stock.get(rawKey) ?? 0) : 0;
        const craftable = have > 0 || !rawKey ? 0 : Math.min(64, Math.floor(rawHave * per));
        const [rid, rmeta] = rawKey ? (rawKey.split(':').map(Number) as [number, number]) : [0, 0];
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
          chain: rawKey
            ? [`${tier === 'furnace' ? 'smelt' : 'craft'} ${label} from ${storyItemLabel(rid, rmeta)}`]
            : [],
          have,
          craftable,
          from: rawKey && craftable > 0 ? [`${Math.max(1, Math.ceil(1 / per))} ${storyItemLabel(rid, rmeta)}`] : [],
          short:
            have > 0 || craftable > 0
              ? []
              : rawKey
                ? [`${Math.max(1, Math.ceil(1 / per))} ${storyItemLabel(rid, rmeta)}`]
                : [`1 ${label}`],
        };
      },
    );
    return {
      entries,
      benches: { crafting_table: table ? 1 : 0, furnace: furnace ? 1 : 0 },
      tabs: ['structure', 'furniture', 'production', 'storage', 'lighting', 'misc'],
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

  private stepPawn(c: Colony, p: PawnSim, dt: number): void {
    const pawn = p.p;
    const brave = pawn.traits.some((t) => t.id === 'brave');
    const skittish = pawn.traits.some((t) => t.id === 'skittish');
    let changed = false;
    pawn.food = Math.max(0, (pawn.food ?? 20) - dt / 40000);
    if ((pawn.hp ?? 20) < 20 && Math.random() < dt / 8000) pawn.hp = Math.min(20, (pawn.hp ?? 20) + 1);
    // 1. urgent needs (not while drafted)
    if (!pawn.drafted && (pawn.food ?? 20) <= (skittish ? 9 : brave ? 4 : 6) && p.task?.kind !== 'eat') {
      this.drop(c, p);
      p.task = { kind: 'eat', ref: null, label: 'Eating', target: null, type: 'eat' };
      p.work = 2500;
    }
    // 2. orders (drafted pawns only follow orders; undrafted ones do theirs first too)
    if (!p.task && pawn.queue.length) {
      const o = pawn.queue[0]!;
      const t = o.target;
      const ent = t && typeof t === 'object' && !Array.isArray(t) ? t.entity : null;
      const at: [number, number, number] | null =
        targetPos(t) ?? (ent !== null && ent !== undefined ? this.entityPos(ent) : null);
      p.task = { kind: `order:${o.kind}`, ref: o.id ?? null, label: orderLabel(o.kind), target: at, type: o.kind };
      p.goal = at;
      p.work = o.kind === 'hold' ? Infinity : (WORK_MS[o.kind] ?? (o.kind === 'move' ? 0 : 1500));
      o.state = 'active';
      const of = o.id !== null && o.id !== undefined ? c.orders.get(o.id) : undefined;
      if (of) of.rev = this.bump(c);
      changed = true;
    }
    // 3. schedule and work
    if (!p.task && !pawn.drafted) {
      const block = pawn.schedule[storyHour(c.tod)] ?? 'anything';
      if (block === 'sleep') {
        p.task = { kind: 'sleep', ref: null, label: 'Sleeping', target: c.home, type: 'sleep' };
        p.goal = [c.home[0] + 0.5, c.home[1] + 1, c.home[2] + 0.5];
        p.work = 20000;
      } else if (block === 'free') {
        const a = Math.random() * Math.PI * 2;
        p.task = { kind: 'free', ref: null, label: 'Relaxing near home', target: null, type: 'idle' };
        p.goal = [c.home[0] + Math.cos(a) * 4, c.home[1] + 1, c.home[2] + Math.sin(a) * 4];
        p.work = 4000;
      } else if (!this.pickWork(c, p)) {
        if (pawn.state !== 'idle') changed = true;
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
      }
      if (p.task) changed = true;
    }
    if (!p.task) {
      if (pawn.drafted && pawn.state !== 'drafted') {
        pawn.state = 'drafted';
        changed = true;
      }
      if (changed) p.rev = this.bump(c);
      return;
    }
    p.idleSince = this.now();
    // Move toward the goal, then work.
    const t = p.task;
    if (
      p.goal &&
      Math.hypot(p.goal[0] - p.x, p.goal[2] - p.z) >
        (t.kind.startsWith('order:move') || t.kind === 'sleep' || t.kind === 'free' ? 0.3 : 1.6)
    ) {
      const dx = p.goal[0] - p.x,
        dz = p.goal[2] - p.z;
      const d = Math.hypot(dx, dz);
      const s = Math.min(d, (SPEED * (pawn.food < 4 ? 0.6 : 1) * dt) / 1000);
      p.x += (dx / d) * s;
      p.z += (dz / d) * s;
      p.y = this.opts.world.ground(Math.floor(p.x), Math.floor(p.z)) + 1;
      pawn.state = pawn.drafted ? 'drafted' : t.kind === 'flee' ? 'fleeing' : 'moving';
    } else {
      pawn.state =
        t.kind === 'eat'
          ? 'eating'
          : t.kind === 'sleep'
            ? 'sleeping'
            : t.kind === 'free'
              ? 'idle'
              : t.type === 'attack'
                ? 'fighting'
                : pawn.drafted
                  ? 'drafted'
                  : 'working';
      p.work -= dt * (pawn.traits.some((x) => x.id === 'night_owl') && storyHour(c.tod) >= 14 ? 1.25 : 1);
      if (p.work <= 0) this.finish(c, p);
    }
    pawn.pos = [Math.round(p.x * 100) / 100, p.y, Math.round(p.z * 100) / 100];
    if (p.task) {
      const total = WORK_MS[p.task.type] ?? 1500;
      pawn.job = {
        type: p.task.type,
        label: p.task.label,
        ...(p.task.target ? { target: { x: p.task.target[0], y: p.task.target[1], z: p.task.target[2] } } : {}),
        progress: Number.isFinite(p.work) ? Math.max(0, Math.min(1, 1 - p.work / total)) : null,
        since: this.iso(),
      };
    }
    p.rev = this.bump(c);
  }

  private entityPos(id: number): [number, number, number] | null {
    const e = this.opts.world.entities().find((x) => x.id === id);
    return e ? [e.x, e.y, e.z] : null;
  }

  /** The job picker: the best-priority work type with an available job; ties to skill, then distance. */
  private pickWork(c: Colony, p: PawnSim): boolean {
    const pawn = p.p;
    const here: [number, number, number] = [p.x, p.y, p.z];
    type Cand = { pri: number; col: number; skill: number; d: number; take: () => void };
    const cands: Cand[] = [];
    const add = (type: string, at: readonly unknown[], take: () => void) => {
      const pri = pawn.priorities[type] ?? 0;
      if (pri <= 0) return;
      cands.push({
        pri,
        col: STORY_WORK_TYPES.indexOf(type as never),
        skill: pawn.skills[type]?.level ?? 0,
        d: dist(here, at),
        take,
      });
    };
    for (const [k, f] of c.designations) {
      const d = f.v;
      if (d.pawn || d.state === 'done' || d.kind === 'forbid' || d.kind === 'allow') continue;
      // A failed one rests until its retry.
      if (d.retryAt && (typeof d.retryAt === 'number' ? d.retryAt : Date.parse(d.retryAt)) > this.now()) continue;
      const type = WORK_OF[d.kind];
      if (!type || !d.block) continue;
      const at = d.block;
      add(type, at, () => {
        d.pawn = pawn.name;
        d.state = 'leased';
        f.rev = this.bump(c);
        p.task = { kind: d.kind, ref: k, label: designationLabel(d.kind), target: [at[0], at[1], at[2]], type: d.kind };
        p.goal = [at[0] + 0.5, at[1], at[2] + 0.5];
        p.work =
          (WORK_MS[d.kind] ?? 1500) *
          (d.kind === 'chop' ? Math.max(1, (d.targets?.length ?? 1) / 4) : 1) *
          (1.4 - (pawn.skills[type]?.level ?? 0) / 40);
      });
    }
    // Builds in queue order: the first unpaused build with a todo ghost.
    const queue = [...c.builds.values()]
      .map((f) => f.v)
      .filter((b) => b.status !== 'paused' && b.status !== 'done')
      .sort((a, b) => a.order - b.order);
    for (const b of queue) {
      const cells = c.ghostCells.get(b.id) ?? [];
      const g = cells.filter((x) => x[6] === 'todo').sort((a, b2) => a[1] - b2[1] || dist(here, a) - dist(here, b2))[0];
      if (!g) continue;
      add('build', g, () => {
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
        p.work = WORK_MS.build! * (1.4 - (pawn.skills.build?.level ?? 0) / 40);
      });
      break;
    }
    // Bills at workbenches, top to bottom.
    for (const f of c.benches.values()) {
      const bench = f.v;
      const bill = bench.bills.find(
        (b) =>
          !b.paused &&
          !b.suspendedReason &&
          (b.mode === 'forever' || b.done < b.target) &&
          (b.pawn === 'any' || b.pawn === pawn.name) &&
          (b.minSkill === null || b.minSkill === undefined || (pawn.skills.craft?.level ?? 0) >= b.minSkill),
      );
      if (!bill) continue;
      const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === bill.recipe);
      const type = recipe?.workType === 'cook' ? 'cook' : 'craft';
      add(type, bench.pos, () => {
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
    if (cands.length === 0) return false;
    cands.sort((a, b) => a.pri - b.pri || a.col - b.col || b.skill - a.skill || a.d - b.d);
    cands[0]!.take();
    return true;
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

  private finish(c: Colony, p: PawnSim): void {
    const t = p.task!;
    const w = this.opts.world;
    if (t.kind === 'eat') p.p.food = 20;
    else if (t.kind.startsWith('order:')) {
      const o = p.p.queue.shift();
      if (o?.id !== null && o?.id !== undefined) {
        const of = c.orders.get(o.id);
        if (of) {
          of.v.state = 'done';
          of.rev = this.bump(c);
        }
      }
    } else if (t.kind === 'bill') {
      for (const f of c.benches.values()) {
        const bill = f.v.bills.find((b) => b.id === t.ref);
        if (!bill) continue;
        const recipe = STORY_DEFAULT_RECIPES.find((r) => r.id === bill.recipe);
        const k = recipe ? `${recipe.output[0]}:${recipe.output[1]}` : '';
        const missing = recipe?.inputs.find((i) => {
          const keys = i.category
            ? categoryItems(STORY_DEFAULT_CATEGORIES, i.category)
            : (i.options ?? []).map(([a, m]) => `${a}:${m === -1 ? 0 : m}`);
          return !keys.some((key) => (c.stock.get(key.replace(':-1', ':0')) ?? 0) >= i.count);
        });
        if (missing && bill.mode !== 'forever' && Math.random() < 0.15) {
          bill.suspendedReason = `missing ${storyItemLabel(missing.options?.[0]?.[0] ?? 0).toLowerCase()} within ${bill.radius} blocks`;
          this.alert(
            c,
            'warn',
            `A bill was suspended: ${recipe?.label ?? bill.recipe} (${bill.suspendedReason}).`,
            undefined,
            { tab: 'bills', workbench: f.v.pos },
          );
        } else {
          bill.done++;
          if (recipe) c.stock.set(k, (c.stock.get(k) ?? 0) + recipe.output[2]);
          if (bill.mode === 'until' && (c.stock.get(k) ?? 0) >= bill.target) bill.done = bill.target;
        }
        f.rev = this.bump(c);
      }
      this.grow(c, p, t.type);
    } else if (t.kind === 'build' && t.ref !== null && t.ref !== undefined) {
      const cells = c.ghostCells.get(t.ref) ?? [];
      const g = cells.find((x) => t.target && x[0] === t.target[0] && x[1] === t.target[1] && x[2] === t.target[2]);
      const f = c.builds.get(t.ref);
      if (g && f) {
        g[6] = 'done';
        w.setBlock?.(g[0], g[1], g[2], g[3], g[4]);
        f.v.blocksDone = cells.filter((x) => x[6] === 'done').length;
        f.v.status = f.v.blocksDone >= f.v.blocksTotal ? 'done' : 'active';
        f.rev = this.bump(c);
        c.ghostsRev = this.bump(c);
        if (f.v.status === 'done') {
          // Built furniture becomes a workbench or a chest the colony can use.
          for (const [x, y, z, id] of cells)
            if (id === 58 || id === 61 || CHEST_IDS.has(id))
              this.discover(c, [x, y, z, x, y, z], CHEST_IDS.has(id) ? undefined : undefined);
          this.alert(c, 'info', `Finished building: ${f.v.label}.`);
        }
      }
      this.grow(c, p, 'build');
    } else if (t.ref !== null && t.ref !== undefined) {
      const f = c.designations.get(t.ref);
      if (f) {
        const d = f.v;
        if (
          d.kind === 'chop' ||
          d.kind === 'mine' ||
          d.kind === 'cut' ||
          d.kind === 'harvest' ||
          d.kind === 'deconstruct'
        )
          for (const b of d.targets ?? (d.block ? [d.block] : [])) w.setBlock?.(b[0], b[1], b[2], 0, 0);
        if (d.kind === 'chop') c.stock.set('17:0', (c.stock.get('17:0') ?? 0) + (d.targets?.length ?? 1));
        if (d.kind === 'haul') this.haul(c, p);
        c.designations.delete(t.ref);
        c.removed.push({ rev: this.bump(c), kind: 'designations', key: t.ref });
        this.grow(c, p, WORK_OF[d.kind] ?? d.kind);
      }
    }
    p.task = null;
    p.goal = null;
    p.p.job = null;
    p.rev = this.bump(c);
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

function overlaps(a: StoryArea, b: StoryArea): boolean {
  return a[0] <= b[3] && b[0] <= a[3] && a[2] <= b[5] && b[2] <= a[5];
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

function directiveSummary(d: StoryDirectiveRequest['directive']): string {
  const area = d.area ? ` in ${Math.abs(d.area[3] - d.area[0]) + 1}×${Math.abs(d.area[5] - d.area[2]) + 1}` : '';
  switch (d.kind) {
    case 'text':
      return d.text ?? 'Message';
    case 'build':
      return `Build ${d.build?.length ?? 0} item${d.build?.length === 1 ? '' : 's'}`;
    case 'craft':
      return `Craft ${d.bill?.recipe ?? 'items'}`;
    case 'move':
      return `Move to ${d.targets?.[0]?.join(', ') ?? 'the spot'}`;
    default:
      return `${d.kind[0]!.toUpperCase()}${d.kind.slice(1)}${area}${d.targets?.length ? ` (${d.targets.length} targets)` : ''}`;
  }
}

/** True when a world block is a log (exported for the mocks' world builders). */
export const isLog = (id: number) => LOG_IDS.has(id);
