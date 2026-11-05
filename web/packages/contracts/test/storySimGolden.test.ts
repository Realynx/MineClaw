/**
 * A golden run of the mock director (storySim.ts): one long scripted colony — every request kind, valid and refused, and a few
 * thousand simulated seconds of pawns working — recorded op by op with periodic full states. It pins the director's behaviour
 * so its code can be restructured without changing what the mocks and e2e tests see (`Math.random` and the clock are seeded).
 */
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StorySim, type StoryWorld } from '../src/testing.js';
import { STORY_DEFAULT_RECIPES } from '../src/index.js';

/** A seeded generator (mulberry32) for Math.random. */
function seeded(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * A 64×64 world: grass at y 63 over dirt and stone (ores here and there), a grove, a wheat field and carrots, tall grass, a
 * pond, a camp (crafting table, furnace, two double chests, beds), drops and animals.
 */
function world(): StoryWorld & { entityList: Array<ReturnType<StoryWorld['entities']>[number]> } {
  const blocks = new Map<string, [number, number]>();
  const k = (x: number, y: number, z: number) => `${x},${y},${z}`;
  const set = (x: number, y: number, z: number, id: number, meta = 0) => blocks.set(k(x, y, z), [id, meta]);
  for (const [x, z, h] of [
    [10, 0, 5],
    [14, 0, 4],
    [10, 5, 6],
    [18, 3, 5],
    [-12, 8, 4],
  ] as const) {
    for (let y = 64; y < 64 + h; y++) set(x, y, z, 17);
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) if (dx || dz) set(x + dx, 63 + h, z + dz, 18);
  }
  for (let x = 0; x < 6; x++) set(x, 64, 8, 59, x % 2 ? 7 : 3);
  for (let x = 0; x < 4; x++) set(x, 64, 10, 141, 7);
  for (let x = -6; x < -2; x++) set(x, 64, 12, 31, 1);
  set(-7, 64, 12, 37);
  set(-8, 64, 12, 38);
  for (let x = 20; x < 25; x++) for (let z = 20; z < 24; z++) set(x, 63, z, 9);
  set(-2, 64, -2, 58);
  set(-3, 64, -2, 61, 3);
  set(1, 64, -2, 54);
  set(2, 64, -2, 54);
  set(4, 64, -2, 54);
  set(5, 64, -2, 54);
  set(-6, 64, 2, 26, 0);
  set(-6, 64, 3, 26, 8);
  set(-8, 64, 2, 26, 0);
  set(-8, 64, 3, 26, 8);
  const ore = (x: number, y: number, z: number) => {
    const h = (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
    const r = ((h % 97) + 97) % 97;
    return r === 0 ? 56 : r < 3 ? 15 : r < 7 ? 16 : r === 8 ? 14 : 1;
  };
  const entityList = [
    { id: 7, kind: 'item', item: 4, x: 5.5, y: 64, z: 5.5 },
    { id: 11, kind: 'item', item: 17, x: -3.5, y: 64, z: 4.5 },
    { id: 8, kind: 'mob', type: 90, x: 6.5, y: 64, z: 6.5 },
    { id: 9, kind: 'mob', type: 54, x: 6.7, y: 64, z: 6.1 },
    { id: 12, kind: 'mob', type: 92, x: -10.5, y: 64, z: -9.5 },
    { id: 13, kind: 'player', name: 'Steve', x: 30.5, y: 64, z: 30.5 },
  ];
  return {
    entityList,
    blockAt: (x, y, z) =>
      blocks.get(k(x, y, z)) ??
      (y > 63 ? [0, 0] : y === 63 ? [2, 0] : y > 59 ? [3, 0] : y <= 0 ? [7, 0] : [ore(x, y, z), 0]),
    ground: (x, z) => (blocks.get(k(x, 63, z))?.[0] === 9 ? 62 : 63),
    entities: () => entityList,
    setBlock: (x, y, z, id, meta) => void blocks.set(k(x, y, z), [id, meta]),
    ai: () => [
      {
        name: 'maid_chan',
        kind: 'main',
        owner: null,
        online: true,
        hp: 20,
        food: 20,
        pos: [0, 64, 0],
        dim: 0,
        task: 'mining',
      },
      {
        name: 'maid_chan_2',
        kind: 'hive',
        owner: 'maid_chan',
        online: true,
        hp: 14,
        food: 9,
        pos: [3, 64, 3],
        dim: 0,
        task: null,
      },
    ],
  };
}

describe('the mock director: a golden run', () => {
  let t = 1_762_000_000_000;
  beforeEach(() => {
    t = 1_762_000_000_000;
    vi.spyOn(Math, 'random').mockImplementation(seeded(42));
    vi.spyOn(Date, 'now').mockImplementation(() => t);
  });
  afterEach(() => vi.restoreAllMocks());

  it('records every request and the colony as it works (pins storySim.ts)', () => {
    const w = world();
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => t });
    const trace: unknown[] = [];
    const call = (label: string, f: () => unknown) => {
      try {
        trace.push([label, f() ?? null]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
    };
    const step = (n: number, ms: number) => {
      for (let i = 0; i < n; i++) {
        t += ms;
        sim.step(ms);
      }
    };
    let rev = 0;
    /** The delta since the last snap (and the full state when `full`). */
    const snap = (label: string, full = false) => {
      call(`delta ${label}`, () => sim.state(id, rev));
      rev = sim.state(id).rev;
      if (full) call(`state ${label}`, () => sim.state(id));
    };

    call('list empty', () => sim.list());
    const { id } = sim.create({ name: 'Golden', pawns: 4 });
    call('create bad', () => sim.create({ name: '', pawns: 0 }));
    snap('founded', true);
    const names = sim.state(id).pawns.map((p) => p.name);
    const [a, b, c, d] = names as [string, string, string, string];

    // Work settings.
    call('priorities', () =>
      sim.priorities(id, {
        pawns: [a, b],
        work: { chop: 1, mine: 2, haul: 3, build: 1, farm: 2, craft: 3, cook: 4, hunt: 2 },
      }),
    );
    call('priorities bad', () => sim.priorities(id, { pawns: ['nobody'], work: { chop: 1 } }));
    call('roles add', () =>
      sim.roles(id, { op: 'add', role: { name: 'Woodcutter', priorities: { chop: 1 } } } as never),
    );
    call('roles assign', () => sim.roles(id, { op: 'assign', id: 'woodcutter', pawns: [c] } as never));
    call('schedule', () => sim.schedule(id, { pawns: [d], hours: { '0': 'sleep', '12': 'work', '20': 'recreation' } }));
    call('options', () => sim.options(id, { autoPlaceBenches: true, idle: 'wander' } as never));

    // Designations of every kind, by area, blocks and entities; a cancel; a retry.
    for (const [kind, area] of [
      ['chop', [8, 60, -2, 20, 80, 8]],
      ['harvest', [0, 60, 8, 6, 70, 10]],
      ['cut', [-8, 60, 12, -3, 70, 12]],
      ['haul', [-6, 60, 0, 8, 70, 8]],
      ['hunt', [-12, 60, -12, 8, 70, 8]],
      ['mine', [30, 55, 30, 33, 62, 33]],
      ['deconstruct', [4, 60, -2, 5, 70, -2]],
      ['forbid', [-4, 60, 4, -3, 70, 5]],
    ] as const)
      call(`designate ${kind}`, () => sim.designations(id, { op: 'add', kind, area: [...area] }));
    call('designate mine ores', () =>
      sim.designations(id, { op: 'add', kind: 'mine', area: [40, 40, 40, 44, 62, 44], filter: 'ores' }),
    );
    call('designate blocks', () =>
      sim.designations(id, {
        op: 'add',
        kind: 'mine',
        blocks: [
          [50, 62, 50],
          [50, 61, 50],
        ],
      }),
    );
    call('designate entities', () => sim.designations(id, { op: 'add', kind: 'hunt', entities: [12] }));
    call('designate bad', () => sim.designations(id, { op: 'add', kind: 'chop' }));
    call('cancel area', () => sim.designations(id, { op: 'remove', area: [18, 60, 3, 18, 80, 3] }));
    snap('designated');

    // Zones: farm, home (named), mine quarry and ores, fish, chop, forbidden, dump; bills; updates; removes.
    const zone = (label: string, body: unknown) => call(`zone ${label}`, () => sim.zones(id, body as never));
    zone('farm', { op: 'add', kind: 'farm', area: [0, 63, 14, 5, 63, 17], plant: 'wheat' });
    zone('farm 2', { op: 'add', kind: 'farm', area: [6, 63, 14, 8, 63, 17], plant: 'carrots' });
    zone('farm merge', { op: 'add', kind: 'farm', area: [5, 63, 14, 6, 63, 15], plant: 'wheat' });
    zone('home', { op: 'add', kind: 'home', area: [-9, 63, -3, 6, 63, 4], name: 'Camp' });
    zone('quarry', { op: 'add', kind: 'mine', area: [-20, 63, -20, -17, 63, -17] });
    zone('quarry small', { op: 'add', kind: 'mine', area: [-30, 63, -30, -30, 63, -25] });
    zone('ores', { op: 'add', kind: 'mine', area: [-30, 63, -30, -26, 63, -30], mode: 'ores' });
    zone('fish', { op: 'add', kind: 'fish', area: [19, 63, 19, 25, 63, 24] });
    zone('chop', { op: 'add', kind: 'chop', area: [9, 63, -1, 15, 63, 6] });
    zone('forbidden', { op: 'add', kind: 'forbidden', area: [-15, 63, 10, -13, 63, 12] });
    zone('dump', { op: 'add', kind: 'dump', area: [12, 63, 12, 13, 63, 13] });
    const farmId = () => String(sim.state(id).zones.find((z) => z.kind === 'farm')!.id);
    zone('bill add', { op: 'bill_add', id: farmId(), plant: 'potatoes', mode: 'count', target: 20 });
    zone('bill add 2', { op: 'bill_add', id: farmId(), plant: 'pumpkin' });
    zone('seek seeds', { op: 'seek_seeds', id: farmId() });
    zone('irrigation', { op: 'irrigation', id: farmId() });
    zone('update', { op: 'update', id: farmId(), sow: false, harvest: true });
    zone('erase', { op: 'remove', kind: 'farm', area: [2, 0, 14, 2, 90, 17] });
    zone('rename', { op: 'rename', id: farmId(), name: 'Fields' });
    zone('bad bill', { op: 'bill_pause', id: farmId(), bill: 'nope' });
    zone('remove id', { op: 'remove', id: 'zz-none' });
    snap('zoned');

    // Builds: structures, furniture, water, mine, remove; reorder, retry, preview.
    call('builds', () =>
      sim.builds(id, {
        op: 'add',
        items: [
          { kind: 'floor', area: [-6, 63, 5, -3, 63, 8], material: 'cobblestone' },
          { kind: 'wall', area: [-6, 64, 5, -3, 64, 8], material: 'oak_planks', height: 3 },
          { kind: 'door', blocks: [[-5, 64, 5]], material: 'oak_planks', facing: 's' },
          { kind: 'window', blocks: [[-6, 65, 6]], material: 'glass' },
          { kind: 'roof', area: [-6, 67, 5, -3, 67, 8], material: 'oak_planks', roof: { style: 'gable', overhang: 1 } },
          { kind: 'fence', area: [8, 64, -6, 12, 64, -6] },
          { kind: 'stairs', blocks: [[0, 64, -6]], material: 'cobblestone', facing: 'n' },
          { kind: 'furnace', blocks: [[-4, 64, -4]], facing: 'e' },
          { kind: 'chest', blocks: [[-5, 64, -4]], facing: 'n' },
          { kind: 'bed', blocks: [[-4, 64, 7]], facing: 'e' },
          { kind: 'torch', blocks: [[-4, 64, 6]] },
          { kind: 'crafting_table', blocks: [[-6, 64, -4]] },
          { kind: 'block', blocks: [[0, 64, -8]], material: 'stone' },
          { kind: 'remove', area: [30, 64, -5, 31, 65, -5], material: 'stone' },
          {
            kind: 'mine',
            blocks: [
              [30, 62, -5],
              [30, 61, -5],
            ],
            place: [[30, 61, -5, 50, 0]],
          },
        ],
      } as never),
    );
    call('builds water refused', () =>
      sim.builds(id, { op: 'add', items: [{ kind: 'water', blocks: [[1, 63, 18]], material: 'water' }] } as never),
    );
    call('builds water', () =>
      sim.builds(id, {
        op: 'add',
        items: [
          { kind: 'remove', blocks: [[26, 63, 21]], material: 'stone', group: 'ditch' },
          { kind: 'water', blocks: [[26, 63, 21]], material: 'water', group: 'ditch' },
        ],
      } as never),
    );
    call('builds preview', () =>
      sim.builds(id, { op: 'preview', items: [{ kind: 'water', blocks: [[0, 64, 20]], material: 'water' }] } as never),
    );
    call('builds bad', () => sim.builds(id, { op: 'add', items: [{ kind: 'wall' }] } as never));
    const buildIds = () => (sim.state(id).builds ?? []).map((x) => x.id);
    call('builds reorder', () => sim.builds(id, { op: 'reorder', ids: buildIds().slice().reverse() } as never));
    snap('built');

    // Storage, benches and bills.
    call('storage area', () => sim.storage(id, { op: 'assign_area', area: [0, 60, -3, 6, 70, -1] }));
    call('storage set', () =>
      sim.storage(id, {
        op: 'set',
        chests: [[1, 64, -2]],
        filter: { allow: ['wood', 'food'], deny: ['5:-1'] },
        priority: 'important',
      }),
    );
    call('storage copy', () => sim.storage(id, { op: 'copy', from: [1, 64, -2], to: [[4, 64, -2]] }));
    call('storage bad', () => sim.storage(id, { op: 'set', chests: [[90, 64, 90]], priority: 'low' }));
    call('bench designate', () => sim.workbenches(id, { op: 'designate', pos: [-2, 64, -2] }));
    call('furnace designate', () => sim.workbenches(id, { op: 'designate', pos: [-3, 64, -2] }));
    call('bench fuel', () => sim.workbenches(id, { op: 'fuel', pos: [-3, 64, -2], fuel: 'coal' } as never));
    call('recipes', () => sim.recipes());
    for (const [recipe, mode, target] of [
      ['sticks', 'until', 64],
      ['torch', 'count', 8],
      ['stone_pickaxe', 'forever', undefined],
      ['bread', 'count', 3],
      ['nonsense', 'count', 1],
    ] as const)
      call(`bill ${recipe}`, () =>
        sim.bills(id, {
          op: 'add',
          bench: [-2, 64, -2],
          bill: { recipe, mode, ...(target ? { target } : {}) },
        } as never),
      );
    call('bill smelt', () =>
      sim.bills(id, { op: 'add', bench: [-3, 64, -2], bill: { recipe: 'glass', mode: 'count', target: 2 } } as never),
    );
    call('policy set 2', () => sim.policies(id, { op: 'set', policy: { id: 'miner', name: 'Miner 2' } } as never));
    const bills = () => (sim.state(id).workbenches ?? []).flatMap((x) => x.bills.map((y) => y.id));
    call('bill pause', () => sim.bills(id, { op: 'pause', id: bills()[0]! }));
    call('bill resume', () => sim.bills(id, { op: 'resume', id: bills()[0]! }));
    call('bill reorder', () =>
      sim.bills(id, { op: 'reorder', bench: [-2, 64, -2], ids: bills().slice(0, 3).reverse() }),
    );
    snap('benches');

    // Pawns: draft, orders, gear, policies, material rules, beds, home, online, add / remove.
    call('draft', () => sim.draft(id, { pawns: [d], drafted: true }));
    call('order move', () => sim.orders(id, { pawns: [d], order: { kind: 'move', target: { x: 6, y: 64, z: 0 } } }));
    call('order queue', () =>
      sim.orders(id, { pawns: [d], order: { kind: 'chop', target: { x: 10, y: 64, z: 5 } }, queue: true }),
    );
    call('order attack mob', () => sim.orders(id, { pawns: [d], order: { kind: 'attack', target: { entity: 8 } } }));
    call('order attack player', () =>
      sim.orders(id, { pawns: [d], order: { kind: 'attack', target: { entity: 13 } } }),
    );
    call('order attack bot', () =>
      sim.orders(id, { pawns: [d], order: { kind: 'attack', target: { player: 'maid_chan_2' }, confirm: true } }),
    );
    call('order undrafted', () => sim.orders(id, { pawns: [a], order: { kind: 'hold' } }));
    call('gear arm', () => sim.gear(id, { op: 'arm', pawns: [a, b] }));
    call('gear tools', () => sim.gear(id, { op: 'tools', pawns: [c] }));
    call('gear pickup', () => sim.gear(id, { op: 'pickup', pawns: [c], item: '4:0', count: 3 } as never));
    call('policy set', () =>
      sim.policies(id, { op: 'set', policy: { name: 'Miner', tools: ['pickaxe'], armor: false } } as never),
    );
    call('policy assign', () => sim.policies(id, { op: 'assign', id: 'miner', pawns: [b] } as never));
    call('material rule', () =>
      sim.materialRules(id, { op: 'set', rule: { material: 'iron_ore', tool: 'pickaxe', tier: 'stone' } } as never),
    );
    call('beds assign', () => sim.beds(id, { op: 'assign', bed: { x: -6, y: 64, z: 2 }, pawn: a }));
    call('beds bad', () => sim.beds(id, { op: 'assign', bed: { x: 0, y: 0, z: 0 }, pawn: a }));
    call('pawns add', () => sim.pawns(id, { op: 'add', count: 2 }));
    call('offline', () => sim.setOffline(id, b, 'cap', 3000));
    call('seats', () => sim.setSeats(id, 0, 1));
    call('online', () => sim.online(id, { pawns: null }));
    call('online over cap', () => sim.online(id, { pawns: [b], overCap: true }));
    call('time', () => sim.time({ time: 13000 }));
    call('directive text', () =>
      sim.directive({
        bots: ['maid_chan'],
        directive: { kind: 'text', text: 'Gather wood' },
        requestId: 'req-00000001',
      } as never),
    );
    call('directive chop', () =>
      sim.directive({
        bots: ['maid_chan_2'],
        directive: { kind: 'chop', area: [0, 60, 0, 4, 70, 2], targets: [[1, 64, 1]] },
        requestId: 'req-00000002',
      } as never),
    );
    call('directive craft', () =>
      sim.directive({
        bots: ['maid_chan'],
        directive: { kind: 'craft', bill: { recipe: 'torch' } },
        requestId: 'req-00000003',
      } as never),
    );
    call('directives', () => sim.directives());
    call('architect', () => sim.architect(id));
    snap('ordered', true);

    // The colony at work: a thousand ticks of 500 ms, then a few short ones; the failing and the retry.
    for (let round = 0; round < 8; round++) {
      step(125, 500);
      snap(`work ${round}`);
    }
    call('fail', () =>
      sim.failDesignations(
        id,
        sim
          .state(id)
          .designations.slice(0, 2)
          .map((x) => x.id),
        'unreachable',
      ),
    );
    call('retry', () =>
      sim.designations(id, {
        op: 'retry',
        ids: sim
          .state(id)
          .designations.slice(0, 1)
          .map((x) => x.id),
      }),
    );
    call('builds retry all', () => sim.builds(id, { op: 'retry_all' } as never));
    call('home move', () => sim.home(id, { home: { x: 200, y: 70, z: -100 } }));
    call('pause', () => sim.pause(id, true));
    step(10, 500);
    call('resume', () => sim.pause(id, false));
    call('pawns remove', () => sim.pawns(id, { op: 'remove', pawn: c }));
    call('release', () => sim.release(id));
    step(20, 250);
    snap('end', true);
    call('list', () => sim.list());

    expect(JSON.stringify(trace).length).toBeGreaterThan(10_000);
    // Each step as its label and a hash of what it answered (a mismatch names the first step that changed; set
    // STORY_GOLDEN_DUMP=<file> to write the whole trace and diff two runs).
    if (process.env.STORY_GOLDEN_DUMP) writeFileSync(process.env.STORY_GOLDEN_DUMP, JSON.stringify(trace, null, 1));
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });

  it('materials: builds without benches, auto-placed stations, smelting, auto bills and their cancel (pins the plan pass)', () => {
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => t });
    const trace: unknown[] = [];
    const call = (label: string, f: () => unknown) => {
      try {
        trace.push([label, f() ?? null]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
    };
    const { id } = sim.create({ name: 'Plan', pawns: 2 });
    const builds = () => call('builds', () => sim.state(id).builds);
    const benches = () => call('benches', () => sim.state(id).workbenches);
    call('release table', () => sim.workbenches(id, { op: 'release', pos: [-2, 64, -2] }));
    call('add', () =>
      sim.builds(id, {
        op: 'add',
        items: [
          { kind: 'fence', area: [3, 64, 3, 6, 64, 3] },
          { kind: 'wall', area: [8, 64, 8, 10, 64, 10], material: 'stone', height: 2 },
          { kind: 'window', blocks: [[0, 65, 9]], material: 'glass' },
          { kind: 'door', blocks: [[1, 64, 9]], material: 'oak_planks' },
          { kind: 'floor', area: [0, 63, 12, 2, 63, 13], material: 'cobblestone' },
          { kind: 'water', blocks: [[22, 63, 21]], material: 'water' },
        ],
      } as never),
    );
    builds();
    call('no auto place', () => sim.options(id, { autoPlaceBenches: false }));
    call('add more', () =>
      sim.builds(id, { op: 'add', items: [{ kind: 'block', blocks: [[5, 64, 5]], material: 'bricks' }] } as never),
    );
    builds();
    call('auto place', () => sim.options(id, { autoPlaceBenches: true }));
    call('designate table', () => sim.workbenches(id, { op: 'designate', pos: [-2, 64, -2] }));
    builds();
    benches();
    call('designate furnace', () => sim.workbenches(id, { op: 'designate', pos: [-3, 64, -2] }));
    builds();
    benches();
    const auto = sim.state(id).craftTasks ?? [];
    if (auto[0]) call('cancel auto', () => sim.bills(id, { op: 'remove', id: auto[0]!.id }));
    builds();
    benches();
    for (let i = 0; i < 6; i++) {
      for (let k = 0; k < 40; k++) {
        t += 500;
        sim.step(500);
      }
      builds();
      benches();
    }
    call('reorder', () =>
      sim.builds(id, { op: 'reorder', ids: (sim.state(id).builds ?? []).map((b) => b.id).reverse() } as never),
    );
    builds();
    benches();
    expect(JSON.stringify(trace)).toMatch(/waiting|blocked/);
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });

  it('bills: every op, refused and accepted, at a table, a furnace and an enchanting table (pins bills)', () => {
    const w = world();
    const blockAt = w.blockAt.bind(w);
    // An enchanting table ringed by bookshelves, an anvil and a brewing stand by the camp.
    const extra = new Map<string, [number, number]>([
      ['-10,64,-6', [116, 0]],
      ['-12,64,-6', [47, 0]],
      ['-8,64,-6', [47, 0]],
      ['-10,64,-8', [47, 0]],
      ['-14,64,-6', [145, 4]],
      ['-16,64,-6', [117, 0]],
    ]);
    w.blockAt = (x, y, z) => extra.get(`${x},${y},${z}`) ?? blockAt(x, y, z);
    const sim = new StorySim({ world: w, defaultHome: [-6, 63, -4], now: () => t });
    const trace: unknown[] = [];
    const call = (label: string, f: () => unknown) => {
      try {
        trace.push([label, f() ?? null]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
    };
    const { id } = sim.create({ name: 'Bills', pawns: 1 });
    const bill = (label: string, body: unknown) => call(label, () => sim.bills(id, body as never));
    const benches = () => call('benches', () => sim.state(id).workbenches);
    for (const pos of [
      [-2, 64, -2],
      [-3, 64, -2],
      [-10, 64, -6],
      [-14, 64, -6],
      [-16, 64, -6],
    ])
      call(`designate ${pos.join(',')}`, () => sim.workbenches(id, { op: 'designate', pos: pos as never }));
    benches();
    bill('add no bench', { op: 'add', bench: [0, 0, 0], bill: { recipe: 'sticks', mode: 'count' } });
    bill('add unknown', { op: 'add', bench: [-2, 64, -2], bill: { recipe: 'nope', mode: 'count' } });
    bill('add wrong bench', { op: 'add', bench: [-3, 64, -2], bill: { recipe: 'sticks', mode: 'count' } });
    bill('add sticks', { op: 'add', bench: [-2, 64, -2], bill: { recipe: 'sticks', mode: 'until' } });
    bill('add torches', {
      op: 'add',
      bench: [-2, 64, -2],
      bill: {
        recipe: 'torch',
        mode: 'count',
        target: 16,
        paused: true,
        radius: 12,
        pawn: 'any',
        minSkill: 3,
        store: 'best',
        ingredients: { allow: ['coal'], deny: [] },
      },
    });
    bill('add glass', {
      op: 'add',
      bench: [-3, 64, -2],
      bill: { recipe: 'glass', mode: 'count', target: 9, fuel: 'planks' },
    });
    bill('add glass inherit', {
      op: 'add',
      bench: [-3, 64, -2],
      bill: { recipe: 'glass', mode: 'forever', fuel: 'inherit' },
    });
    for (const recipe of STORY_DEFAULT_RECIPES.slice(0, 80))
      bill(`add ${recipe.id}`, {
        op: 'add',
        bench:
          recipe.workbench === 'furnace'
            ? [-3, 64, -2]
            : recipe.workbench === 'enchanting_table'
              ? [-10, 64, -6]
              : recipe.workbench === 'anvil'
                ? [-14, 64, -6]
                : recipe.workbench === 'brewing_stand'
                  ? [-16, 64, -6]
                  : [-2, 64, -2],
        bill: { recipe: recipe.id, mode: recipe.modes?.[0] ?? 'count', target: 2, option: 2, prefer: 'fortune' },
      });
    benches();
    const ids = () => (sim.state(id).workbenches ?? []).flatMap((b) => b.bills.map((x) => x.id));
    const first = ids()[0]!;
    bill('update', { op: 'update', id: first, target: 5, paused: false, radius: 20 });
    bill('update fuel at table', { op: 'update', id: first, fuel: 'coal' });
    const glass = (sim.state(id).workbenches ?? []).find((b) => b.kind === 'furnace')!.bills[0]!.id;
    bill('update fuel', { op: 'update', id: glass, fuel: 'coal' });
    bill('update fuel inherit', { op: 'update', id: glass, fuel: 'inherit', mode: 'count', target: 3 });
    bill('pause', { op: 'pause', id: first });
    bill('resume', { op: 'resume', id: first });
    bill('pause missing', { op: 'pause', id: 'bill-none' });
    bill('reorder', { op: 'reorder', bench: [-2, 64, -2], ids: ids().slice(0, 3).reverse() });
    bill('release claim', { op: 'release_claim', build: 'b1', item: 'fence' });
    bill('enchant', {
      op: 'enchant_tools',
      items: ['diamond_pickaxe', 'iron_pickaxe', 'nope'],
      material: 'diamond_ore',
      count: 2,
    });
    bill('enchant again', { op: 'enchant_tools', material: 'glass' });
    bill('enchant plain', { op: 'enchant_tools', prefer: 'efficiency', preferOnly: true });
    bill('remove', { op: 'remove', id: first });
    benches();
    call('workbench fuel', () => sim.workbenches(id, { op: 'fuel', pos: [-3, 64, -2], fuel: 'logs' } as never));
    bill('add after fuel', { op: 'add', bench: [-3, 64, -2], bill: { recipe: 'glass', mode: 'count', target: 30 } });
    benches();
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });

  it('designations and builds: every op and selector, refused and accepted (pins designations / builds)', () => {
    const w = world();
    // A door to forbid and allow.
    const blockAt = w.blockAt.bind(w);
    w.blockAt = (x, y, z) =>
      x === -1 && z === 6 && (y === 64 || y === 65) ? [64, y === 65 ? 8 : 0] : blockAt(x, y, z);
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => t });
    const trace: unknown[] = [];
    const call = (label: string, f: () => unknown) => {
      try {
        trace.push([label, f() ?? null]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
    };
    const { id } = sim.create({ name: 'Ops', pawns: 2 });
    const des = (label: string, body: unknown) => call(label, () => sim.designations(id, body as never));
    const bld = (label: string, body: unknown) => call(label, () => sim.builds(id, body as never));
    const state = (label: string) => call(label, () => sim.state(id));
    des('chop', { op: 'add', kind: 'chop', area: [8, 60, -2, 20, 80, 8] });
    des('chop again', { op: 'add', kind: 'chop', area: [8, 60, -2, 20, 80, 8] });
    des('mine blocks', {
      op: 'add',
      kind: 'mine',
      blocks: [
        [40, 60, 40],
        [40, 59, 40],
        [41, 60, 40],
      ],
      design: 7,
    });
    des('mine stone', { op: 'add', kind: 'mine', area: [44, 58, 44, 46, 62, 46], filter: 'stone' });
    des('haul area', { op: 'add', kind: 'haul', area: [-6, 60, 0, 8, 70, 8] });
    des('haul entities', { op: 'add', kind: 'haul', entities: [11, 7] });
    des('hunt', { op: 'add', kind: 'hunt', area: [-20, 60, -20, 20, 70, 20] });
    des('forbid area', { op: 'add', kind: 'forbid', area: [-3, 60, 4, 1, 70, 8] });
    des('allow blocks', { op: 'add', kind: 'allow', blocks: [[-1, 64, 6]] });
    des('cut', { op: 'add', kind: 'cut', area: [-8, 60, 12, -3, 70, 12] });
    des('harvest none', { op: 'add', kind: 'harvest', area: [100, 60, 100, 101, 70, 101] });
    des('too big', { op: 'add', kind: 'mine', area: [0, 60, 0, 200, 60, 200] });
    des('default kind', { op: 'add', blocks: [[60, 62, 60]] });
    state('after adds');
    const ids = () => sim.state(id).designations.map((d) => d.id);
    des('remove ids', { op: 'remove', ids: ids().slice(0, 1) });
    des('remove blocks', { op: 'cancel', blocks: [[40, 59, 40]] });
    des('remove entities', { op: 'remove', entities: [11] });
    des('remove kind area', { op: 'remove', kind: 'hunt', area: [-20, 60, -20, 20, 70, 20] });
    des('remove nothing', { op: 'remove', area: [500, 0, 500, 501, 10, 501] });
    des('retry none', { op: 'retry' });
    call('fail', () => sim.failDesignations(id, ids().slice(0, 2), 'blocked'));
    des('retry', { op: 'retry', ids: ids().slice(0, 3) });
    state('after removes');
    bld('add', {
      op: 'add',
      items: [
        { kind: 'wall', area: [-6, 64, 5, -3, 64, 8], material: 'cobblestone', height: 2 },
        { kind: 'floor', area: [-6, 63, 5, -3, 63, 8], material: 'oak_planks' },
        { kind: 'chest', blocks: [[-4, 64, 6]] },
        { kind: 'remove', area: [30, 64, -5, 31, 64, -5], material: 'stone' },
        { kind: 'mine', blocks: [[30, 62, -5]] },
      ],
    });
    bld('water infinite', { op: 'add', items: [{ kind: 'water', blocks: [[22, 64, 21]], infinite: true }] });
    bld('water named', {
      op: 'add',
      items: [
        {
          kind: 'water',
          blocks: [
            [23, 64, 21],
            [24, 64, 21],
          ],
          name: 'Pond',
        },
      ],
    });
    bld('lava off', { op: 'add', items: [{ kind: 'water', blocks: [[23, 64, 22]], material: 'lava' }] });
    bld('water bad material', { op: 'preview', items: [{ kind: 'water', blocks: [[23, 64, 22]], material: 'dirt' }] });
    bld('water preview', {
      op: 'preview',
      items: [
        {
          kind: 'water',
          blocks: [
            [19, 64, 19],
            [19, 64, 20],
          ],
        },
      ],
    });
    bld('dug water', {
      op: 'add',
      items: [
        {
          kind: 'remove',
          blocks: [
            [0, 63, 30],
            [0, 62, 30],
          ],
          material: 'stone',
          group: 'g',
        },
        { kind: 'water', blocks: [[0, 63, 30]], group: 'g' },
      ],
    });
    state('after builds');
    const bids = () => (sim.state(id).builds ?? []).map((b) => b.id);
    bld('pause', { op: 'pause', ids: bids().slice(0, 2) });
    bld('resume', { op: 'resume', ids: bids().slice(0, 1) });
    bld('retry none', { op: 'retry', id: 'b-none' });
    bld('retry one', { op: 'retry', id: bids()[0], cells: [[-6, 64, 5]] });
    bld('retry ids', { op: 'retry', ids: bids().slice(0, 3) });
    bld('reorder', { op: 'reorder', ids: bids().slice(1, 3) });
    bld('remove', { op: 'remove', ids: [bids()[0], 'b-none'] });
    for (let i = 0; i < 60; i++) {
      t += 500;
      sim.step(500);
    }
    state('after work');
    bld('retry all', { op: 'retry_all' });
    state('end');
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });

  it('zones: modes, into, plants, quarries and strips, fishing water, removes (pins zones)', () => {
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => t });
    const trace: unknown[] = [];
    const call = (label: string, f: () => unknown) => {
      try {
        trace.push([label, f() ?? null]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
    };
    const { id } = sim.create({ name: 'Zones', pawns: 1 });
    const zone = (label: string, body: unknown) => call(label, () => sim.zones(id, body as never));
    const zid = (kind: string) => String(sim.state(id).zones.find((z) => z.kind === kind)?.id ?? 'none');
    zone('quarry', { op: 'add', kind: 'mine', area: [-20, 63, -20, -17, 63, -17], name: 'Pit' });
    zone('strip by quarry', { op: 'add', kind: 'mine', area: [-16, 63, -20, -16, 63, -14] });
    zone('strip alone', { op: 'add', kind: 'mine', area: [40, 63, 40, 40, 63, 45] });
    zone('strip ores', { op: 'add', kind: 'mine', area: [40, 63, 40, 40, 63, 45], mode: 'ores' });
    zone('mode ores', { op: 'update', id: zid('mine'), mode: 'ores' });
    zone('mode quarry', { op: 'update', id: zid('mine'), mode: 'quarry' });
    zone('mode missing', { op: 'update', id: 'z-none', mode: 'ores' });
    zone('farm', { op: 'add', kind: 'farm', area: [0, 63, 14, 3, 63, 16], plant: 'carrots', sow: false });
    zone('mode farm', { op: 'update', id: zid('farm'), mode: 'ores' });
    zone('into farm', { op: 'add', area: [4, 63, 14, 5, 63, 16], into: zid('farm') });
    zone('into missing', { op: 'add', area: [4, 63, 14, 5, 63, 16], into: 'z-none' });
    zone('plant on home', { op: 'add', kind: 'home', area: [0, 63, 0, 2, 63, 2], plant: 'wheat' });
    zone('bad plant', { op: 'add', kind: 'farm', area: [0, 63, 20, 2, 63, 22], plant: 'cactus' });
    zone('harvest flag', { op: 'add', kind: 'farm', area: [0, 63, 24, 2, 63, 26], harvest: false });
    zone('farm no plant', { op: 'add', kind: 'farm', area: [0, 63, 28, 2, 63, 30] });
    zone('default kind', { op: 'add', area: [-9, 63, -3, -5, 63, 1] });
    zone('fish dry', { op: 'add', kind: 'fish', area: [30, 63, 30, 32, 63, 32] });
    zone('fish', { op: 'add', kind: 'fish', area: [20, 63, 20, 22, 63, 22] });
    zone('fish more', { op: 'add', kind: 'fish', area: [23, 63, 20, 24, 63, 21] });
    zone('rename', { op: 'rename', id: zid('home'), name: '' });
    zone('rename missing', { op: 'rename', id: 'z-none', name: 'x' });
    zone('remove id', { op: 'remove', id: zid('fish') });
    zone('erase any', { op: 'remove', kind: 'any', area: [-20, 0, -20, -18, 90, -18] });
    zone('no area', { op: 'add', kind: 'farm' });
    call('state', () => sim.state(id).zones);
    const digest = (v: unknown) => createHash('sha256').update(JSON.stringify(v)).digest('hex').slice(0, 16);
    expect(trace.map((s) => [(s as unknown[])[0], digest((s as unknown[]).slice(1))])).toMatchSnapshot();
  });
});
