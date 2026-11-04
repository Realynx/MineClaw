import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { StorySim, type StoryWorld } from '../src/testing.js';
import { loadFixture } from './helpers.js';

/** A flat world (grass at y 63) with a 3-tree grove, a ripe wheat row, a camp with a crafting table and two chests. */
function world(): StoryWorld & { changed: string[] } {
  const blocks = new Map<string, [number, number]>();
  const k = (x: number, y: number, z: number) => `${x},${y},${z}`;
  for (const [x, z] of [
    [10, 0],
    [14, 0],
    [10, 5],
  ] as const)
    for (let y = 64; y <= 68; y++) blocks.set(k(x, y, z), [17, 0]);
  for (let x = 0; x < 4; x++) blocks.set(k(x, 64, 8), [59, 7]);
  blocks.set(k(3, 64, 8), [59, 3]);
  blocks.set(k(-2, 64, -2), [58, 0]);
  blocks.set(k(1, 64, -2), [54, 0]);
  blocks.set(k(2, 64, -2), [54, 0]);
  const changed: string[] = [];
  return {
    changed,
    blockAt: (x, y, z) => blocks.get(k(x, y, z)) ?? (y < 63 ? [1, 0] : y === 63 ? [2, 0] : [0, 0]),
    ground: () => 63,
    entities: () => [
      { id: 7, kind: 'item', item: 4, x: 5.5, y: 64, z: 5.5 },
      { id: 8, kind: 'mob', type: 90, x: 6.5, y: 64, z: 6.5 },
      { id: 9, kind: 'mob', type: 54, x: 6.7, y: 64, z: 6.1 },
    ],
    setBlock: (x, y, z, id) => {
      blocks.set(k(x, y, z), [id, 0]);
      changed.push(k(x, y, z));
    },
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
    ],
  };
}

describe('story contracts', () => {
  it('the story role (or admin) writes; everyone reads', () => {
    expect(C.hasRole(['story'], 'story')).toBe(true);
    expect(C.hasRole(['admin'], 'story')).toBe(true);
    expect(C.hasRole(['chat', 'commands', 'viewer'], 'story')).toBe(false);
    expect(C.hasRole(['story'], 'viewer')).toBe(true);
    expect(C.hasRole(['story'], 'admin')).toBe(false);
    for (const id of C.ROUTE_IDS.filter((i) => i.startsWith('story.'))) {
      const r: C.RouteDef = C.ROUTES[id];
      expect(r.access, id).toBe(r.method === 'POST' ? 'story' : 'viewer');
    }
  });

  it('request bodies are strict and bounded', () => {
    expect(C.StoryCreateRequest.safeParse({ name: 'Hollow', pawns: 13 }).success).toBe(false);
    expect(C.StoryPrioritiesRequest.safeParse({ pawns: ['a'], work: { chop: 5 } }).success).toBe(false);
    expect(C.StoryPrioritiesRequest.safeParse({ pawns: ['a'], work: { dance: 1 } }).success).toBe(false);
    expect(C.StoryScheduleRequest.safeParse({ pawns: ['a'], hours: { '24': 'work' } }).success).toBe(false);
    expect(C.StoryScheduleRequest.safeParse({ pawns: ['a'], hours: { '0': 'sleep', '23': 'free' } }).success).toBe(
      true,
    );
    expect(
      C.StoryOrderRequest.safeParse({
        pawns: ['a'],
        order: { kind: 'chop', target: { x: 1, y: 2, z: 3 } },
        queue: true,
      }).success,
    ).toBe(true);
    expect(
      C.StoryOrderRequest.safeParse({ pawns: ['a'], order: { kind: 'chop', target: { x: 1, y: 2, z: 3, who: 'x' } } })
        .success,
    ).toBe(false);
    expect(
      C.StoryBuildRequest.safeParse({
        op: 'add',
        items: [{ kind: 'wall', area: [0, 64, 0, 4, 64, 4], material: 'oak_planks', height: 13 }],
      }).success,
    ).toBe(false);
    expect(
      C.StoryBillsRequest.safeParse({
        op: 'add',
        bench: [1, 2, 3],
        bill: { recipe: 'sticks', mode: 'until', target: 64, minSkill: 3 },
      }).success,
    ).toBe(true);
    expect(
      C.StoryStorageRequest.safeParse({
        op: 'set',
        chests: [[1, 2, 3]],
        filter: { allow: ['wood'] },
        priority: 'preferred',
      }).success,
    ).toBe(true);
    expect(
      C.StoryDirectiveRequest.safeParse({
        bots: ['maid_chan'],
        directive: { kind: 'text', text: 'come here' },
        requestId: 'r-12345678',
      }).success,
    ).toBe(true);
  });

  it('hours and clock labels', () => {
    // The clock hour (the bot's StoryCharacter.Hour): tick 0 = 06:00, a schedule is indexed by it.
    expect(C.storyHour(0)).toBe(6);
    expect(C.storyHour(23999)).toBe(5);
    expect(C.storyHour(18000)).toBe(0);
    expect(C.storyHour(-6000)).toBe(12); // a stopped daylight cycle: the absolute value
    expect(C.storyClock(0)).toBe('00:00');
    expect(C.storyClock(6)).toBe('06:00');
    expect(C.storyClock(20)).toBe('20:00');
  });

  it('time of day: ticks and clock times map both ways (0 = 06:00, 6000 noon, 18000 midnight)', () => {
    expect(C.tickClock(0)).toBe('06:00');
    expect(C.tickClock(6000)).toBe('12:00');
    expect(C.tickClock(12000)).toBe('18:00');
    expect(C.tickClock(18000)).toBe('00:00');
    expect(C.tickClock(23999)).toBe('05:59');
    expect(C.tickClock(7500)).toBe('13:30');
    expect(C.tickClock(24000 + 500)).toBe('06:30');
    expect(C.clockTick(6)).toBe(0);
    expect(C.clockTick(12)).toBe(6000);
    expect(C.clockTick(0)).toBe(18000);
    expect(C.clockTick(5, 30)).toBe(23500);
    for (const t of [0, 1000, 7500, 18000, 23000])
      expect(C.clockTick(...(C.tickClock(t).split(':').map(Number) as [number, number]))).toBe(t);
    expect(C.StoryTimeRequest.safeParse({ time: 24000 }).success).toBe(false);
    expect(C.StoryTimeRequest.safeParse({ time: 12.5 }).success).toBe(false);
    expect(C.StoryTimeRequest.safeParse({ time: 0 }).success).toBe(true);
  });
});

describe('build expansion (the preview)', () => {
  it('walls are the rectangle outline, height blocks up; floors fill', () => {
    const wall = C.expandBuildItem({ kind: 'wall', area: [0, 64, 0, 4, 64, 4], material: 'cobblestone', height: 3 });
    expect(wall).toHaveLength(16 * 3);
    expect(new Set(wall.map((b) => b[1]))).toEqual(new Set([64, 65, 66]));
    expect(wall.some((b) => b[0] === 2 && b[2] === 2)).toBe(false);
    expect(wall.every((b) => b[3] === 4)).toBe(true);
    const line = C.expandBuildItem({ kind: 'wall', area: [0, 64, 0, 6, 64, 0], material: 'oak_planks', height: 2 });
    expect(line).toHaveLength(14);
    expect(C.expandBuildItem({ kind: 'floor', area: [0, 64, 0, 4, 64, 4], material: 'oak_planks' })).toHaveLength(25);
  });

  it('roofs: flat, slab, gable (rising to the ridge), none, and the overhang', () => {
    const area: C.StoryArea = [0, 67, 0, 6, 67, 4];
    expect(C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'flat' } })).toHaveLength(35);
    expect(C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'none' } })).toHaveLength(0);
    expect(
      C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'slab' } }).every(
        (b) => b[3] === 44,
      ),
    ).toBe(true);
    const gable = C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'gable' } });
    // Ridge along x (the longer side): z 0 and 4 are eaves (y 67), z 2 is the ridge (y 69).
    expect(gable.filter((b) => b[2] === 0).every((b) => b[1] === 67)).toBe(true);
    expect(gable.filter((b) => b[2] === 2).every((b) => b[1] === 69)).toBe(true);
    expect(
      C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'flat', overhang: 1 } }),
    ).toHaveLength(9 * 7);
  });

  it('doors are two high, furniture beds two long, remove places air', () => {
    expect(C.expandBuildItem({ kind: 'door', blocks: [[2, 64, 0]], material: 'oak_planks' }).map((b) => b[1])).toEqual([
      64, 65,
    ]);
    const bed = C.expandBuildItem({ kind: 'bed', blocks: [[0, 64, 0]], facing: 'e' });
    expect(bed.map((b) => [b[0], b[2], b[4]])).toEqual([
      [0, 0, 3],
      [1, 0, 11],
    ]);
    expect(
      C.expandBuildItem({ kind: 'remove', area: [0, 64, 0, 1, 65, 0], material: 'stone' }).every((b) => b[3] === 0),
    ).toBe(true);
  });
});

describe('tool matching', () => {
  it('chop counts whole trees; harvest only ripe crops; mine filters', () => {
    expect(
      C.countTrees([
        [0, 64, 0],
        [0, 65, 0],
        [1, 64, 1],
        [5, 64, 5],
      ]),
    ).toBe(2);
    expect(C.matchesTool('harvest', 59, 7, 60)).toBe(true);
    expect(C.matchesTool('harvest', 59, 3, 60)).toBe(false);
    expect(C.matchesTool('harvest', 83, 0, 2)).toBe(false);
    expect(C.matchesTool('harvest', 83, 0, 83)).toBe(true);
    expect(C.matchesTool('mine', 16, 0, 1, 'ores')).toBe(true);
    expect(C.matchesTool('mine', 1, 0, 1, 'ores')).toBe(false);
    expect(C.matchesTool('mine', 3, 0, 1, 'soil')).toBe(true);
    expect(C.matchesTool('mine', 7, 0, 1)).toBe(false);
    expect(C.matchesTool('cut', 31, 1, 2)).toBe(true);
  });

  it('storage filters: categories, item keys, deny wins', () => {
    const cats = C.STORY_DEFAULT_CATEGORIES;
    expect(C.filterAccepts(cats, { allow: ['wood'], deny: [] }, 17, 2)).toBe(true);
    expect(C.filterAccepts(cats, { allow: ['resources'], deny: ['5:-1'] }, 5, 0)).toBe(false);
    expect(C.filterAccepts(cats, { allow: ['food'], deny: [] }, 17, 0)).toBe(false);
    expect(C.filterAccepts(cats, { allow: ['367:0'], deny: [] }, 367, 0)).toBe(true);
    expect(C.categoryItems(cats, 'food')).toContain('320:-1');
  });
});

describe('the mock director', () => {
  it('founds a colony, expands designations against the world and answers deltas', () => {
    const w = world();
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 3 });
    const full = C.StoryColonyState.parse(C.toCamel(C.toSnake(sim.state(id))));
    expect(full.pawns).toHaveLength(3);
    expect(full.full).toBe(true);
    expect(full.workbenches?.[0]?.kind).toBe('crafting_table');
    expect(full.ai?.[0]?.name).toBe('maid_chan');
    const rev = full.rev;
    const chop = sim.designations(id, { op: 'add', kind: 'chop', area: [8, 60, -2, 16, 80, 6] });
    expect(chop.count).toBe(3);
    const haul = sim.designations(id, { op: 'add', kind: 'haul', area: [0, 60, 0, 8, 70, 8] });
    expect(haul.entities).toEqual([7]);
    const hunt = sim.designations(id, { op: 'add', kind: 'hunt', area: [0, 60, 0, 8, 70, 8] });
    expect(hunt.entities).toEqual([8]);
    const harvest = sim.designations(id, { op: 'add', kind: 'harvest', area: [0, 60, 8, 4, 70, 8] });
    expect(harvest.count).toBe(3);
    const delta = sim.state(id, rev);
    expect(delta.full).toBe(false);
    expect(delta.designations).toHaveLength(3 + 1 + 1 + 3);
    expect(delta.pawns).toHaveLength(0);
    const cancel = sim.designations(id, { op: 'remove', area: [8, 60, -2, 12, 80, 6] });
    expect(cancel.count).toBe(2);
    const after = sim.state(id, delta.rev);
    expect(after.removed?.designations).toHaveLength(2);
  });

  it('moves home: far pawns are brought there, near ones stay; bad bodies and unknown colonies fail', () => {
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 2 });
    const rev = sim.state(id).rev;
    expect(C.StoryHomeRequest.safeParse({ home: { x: 1, y: 2, z: 3 } }).success).toBe(true);
    expect(C.StoryHomeRequest.safeParse({ home: { x: 1.5, y: 2, z: 3 } }).success).toBe(false);
    expect(C.StoryHomeRequest.safeParse({ x: 1, y: 2, z: 3 }).success).toBe(false);
    // Nearby: the pawns keep their places.
    const before = sim.state(id).pawns.map((p) => p.pos);
    expect(C.StoryHomeResponse.parse(sim.home(id, { home: { x: 10, y: 70, z: 10 } }))).toEqual({ home: [10, 63, 10] });
    expect(sim.state(id).pawns.map((p) => p.pos)).toEqual(before);
    // Far: every pawn is brought next to the new home.
    sim.home(id, { home: { x: 400, y: 64, z: -300 } });
    const s = sim.state(id, rev);
    expect(s.home).toEqual([400, 63, -300]);
    for (const p of s.pawns) expect(Math.hypot((p.pos?.[0] ?? 0) - 400, (p.pos?.[2] ?? 0) + 300)).toBeLessThan(6);
    expect(() => sim.home(id, { home: { x: 1, y: 2 } })).toThrow(expect.objectContaining({ status: 400 }));
    expect(() => sim.home('story-99', { home: { x: 1, y: 2, z: 3 } })).toThrow(
      expect.objectContaining({ status: 404 }),
    );
  });

  it('zones are cell sets painted RimWorld-style; growing zones take planting bills (docs/web/story-mode.md "Zones")', () => {
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 1 });
    const farm = (area: C.StoryArea, plant = 'wheat') =>
      sim.zones(id, { op: 'add', kind: 'farm', area, plant }) as C.StoryAck;
    const a = farm([0, 63, 0, 4, 63, 2]);
    const b = farm([3, 63, 1, 8, 63, 2]); // overlaps: one zone
    expect(b).toMatchObject({ id: a.id, created: [], grown: [a.id] });
    const zones = () => sim.state(id).zones;
    expect(zones()).toHaveLength(1);
    expect(zones()[0]!.cells).toBe(15 + 12 - 4);
    expect(zones()[0]!.rows?.[0]).toEqual([0, 0, 4]);
    // Another plant overwrites; a house over it is an overlay layer.
    const c = farm([4, 63, 0, 4, 63, 2], 'carrots');
    expect(c.split).toEqual([{ from: a.id, into: [expect.any(String)] }]);
    sim.zones(id, { op: 'add', kind: 'home', area: [0, 63, 0, 8, 63, 2] });
    expect(
      zones()
        .filter((z) => z.kind === 'farm')
        .reduce((n, z) => n + (z.cells ?? 0), 0),
    ).toBe(23);
    // Erase any kind at another level: cells, not zones.
    const e = sim.zones(id, { op: 'remove', area: [0, 90, 0, 0, 90, 2] }) as C.StoryAck;
    expect(e.shrunk).toEqual(expect.arrayContaining([a.id]));
    expect(C.StoryZoneRequest.safeParse({ op: 'remove', kind: 'any', area: [0, 0, 0, 1, 1, 1] }).success).toBe(true);
    // Planting bills: the first unmet bill is sown; reorder and pause change it.
    const zid = String(a.id);
    const bill = sim.zones(id, { op: 'bill_add', id: zid, plant: 'potatoes' }) as C.StoryAck;
    const st = () => zones().find((z) => String(z.id) === zid)!;
    expect(st().bills?.map((x) => x.plant)).toEqual(['wheat', 'potatoes']);
    expect(st().activePlant).toBe('wheat');
    sim.zones(id, { op: 'bill_reorder', id: zid, ids: [bill.bill!] });
    expect(st().activePlant).toBe('potatoes');
    expect(st().plant).toBe('potatoes');
    sim.zones(id, { op: 'bill_pause', id: zid, bill: bill.bill! });
    expect(st().activePlant).toBe('wheat');
    sim.zones(id, { op: 'bill_update', id: zid, bill: st().bills![1]!.id, mode: 'count', target: 1 });
    expect(st().activePlant).toBe('wheat');
  });

  it('beds: found near home, one per pawn, in the house they stand in; houses are named home zones', () => {
    const w = world();
    const blocks = new Map<string, [number, number]>([
      ['3,64,3', [26, 0]],
      ['3,64,4', [26, 8]],
      ['5,64,3', [26, 0]],
      ['5,64,4', [26, 8]],
    ]);
    const inner = w.blockAt.bind(w);
    w.blockAt = (x, y, z) => blocks.get(`${x},${y},${z}`) ?? inner(x, y, z);
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 2 });
    const [a, b] = sim.state(id).pawns.map((p) => p.name) as [string, string];
    expect(sim.state(id).beds?.map((x) => x.pos)).toEqual([
      [3, 64, 3],
      [5, 64, 3],
    ]); // the foot halves only
    expect(C.StoryBedsRequest.safeParse({ op: 'assign', bed: { x: 3, y: 64, z: 3 }, pawn: a }).success).toBe(true);
    expect(C.StoryBedsRequest.safeParse({ op: 'unassign', bed: { x: 3, y: 64, z: 3 }, pawn: a }).success).toBe(false);
    sim.beds(id, { op: 'assign', bed: { x: 3, y: 64, z: 3 }, pawn: a });
    const out = C.StoryBedsResponse.parse(sim.beds(id, { op: 'assign', bed: { x: 5, y: 64, z: 3 }, pawn: a }));
    expect(out.beds.map((x) => x.pawn)).toEqual([null, a]); // one bed a pawn: the first one is freed
    sim.beds(id, { op: 'assign', bed: { x: 3, y: 64, z: 3 }, pawn: b });
    expect(sim.beds(id, { op: 'unassign', bed: { x: 5, y: 64, z: 3 } }).beds.map((x) => x.pawn)).toEqual([b, null]);
    expect(() => sim.beds(id, { op: 'unassign', bed: { x: 9, y: 9, z: 9 } })).toThrow(
      expect.objectContaining({ status: 400 }),
    );
    // the head half names the bed too
    expect(sim.beds(id, { op: 'unassign', bed: { x: 3, y: 64, z: 4 } }).beds.map((x) => x.pawn)).toEqual([null, null]);
    expect(() => sim.beds(id, { op: 'assign', bed: { x: 3, y: 64, z: 3 }, pawn: 'nobody' })).toThrow(
      expect.objectContaining({ status: 404 }),
    );
    // Two houses; a bed knows its house; a house can be renamed.
    const h1 = sim.zones(id, { op: 'add', kind: 'home', area: [2, 63, 2, 4, 63, 5], name: 'Cabin' }) as { id: string };
    sim.zones(id, { op: 'add', kind: 'home', area: [20, 63, 20, 24, 63, 24] });
    expect(sim.state(id).zones.filter((z) => z.kind === 'home')).toHaveLength(2);
    expect(sim.state(id).beds?.[0]?.zone).toBe(h1.id);
    expect(sim.zones(id, { op: 'rename', id: h1.id, name: 'Lodge' })).toMatchObject({ id: h1.id, name: 'Lodge' });
    expect(sim.state(id).zones.find((z) => z.id === h1.id)?.name).toBe('Lodge');
    expect(C.StoryZoneRequest.safeParse({ op: 'rename', id: 'z1' }).success).toBe(false);
    expect(C.StoryZoneRequest.safeParse({ op: 'add', kind: 'home' }).success).toBe(false);
    expect(sim.zones(id, { op: 'remove', id: h1.id })).toMatchObject({ removed: [h1.id] });
    expect(sim.state(id).zones.filter((z) => z.kind === 'home')).toHaveLength(1);
    sim.zones(id, { op: 'remove', kind: 'home', area: [0, 0, 0, 30, 90, 30] });
    expect(sim.state(id).zones.filter((z) => z.kind === 'home')).toHaveLength(0);
  });

  it('sets the time of day through the op account: 400 bad, 409 without an op, 429 above once a second', () => {
    let t = 1_762_000_000_000;
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => t });
    const { id } = sim.create({ name: 'Test', pawns: 1 });
    expect(sim.list().timeControl).toEqual({ available: true, by: 'maid_chan' });
    expect(C.StoryTimeResponse.parse(sim.time({ time: 13000 }))).toEqual({ time: 13000, by: 'maid_chan' });
    expect(sim.state(id).timeOfDay).toBe(13000);
    expect(() => sim.time({ time: 100 })).toThrow(expect.objectContaining({ status: 429 }));
    t += 1500;
    expect(() => sim.time({ time: 24000 })).toThrow(expect.objectContaining({ status: 400 }));
    expect(sim.time({ time: 100 }).time).toBe(100);
    const none = new StorySim({ world: world(), timeOp: null, now: () => t });
    expect(none.state(none.create({ name: 'X', pawns: 1 }).id).timeControl).toEqual({ available: false, by: null });
    expect(() => none.time({ time: 100 })).toThrow(expect.objectContaining({ status: 409 }));
  });

  it('pawns pick jobs by priority, lease them and finish them', () => {
    const w = world();
    let t = 1_762_000_000_000;
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => t });
    const { id } = sim.create({ name: 'Test', pawns: 2 });
    const names = sim.state(id).pawns.map((p) => p.name);
    sim.priorities(id, {
      pawns: names,
      work: Object.fromEntries(C.STORY_WORK_TYPES.map((k) => [k, k === 'chop' ? 1 : 0])),
    });
    sim.designations(id, { op: 'add', kind: 'chop', area: [8, 60, -2, 16, 80, 6] });
    for (let i = 0; i < 400; i++) {
      t += 100;
      sim.step(100);
    }
    const s = sim.state(id);
    expect(s.designations.length).toBeLessThan(3);
    expect(w.changed.length).toBeGreaterThan(0);
    // Two pawns never hold the same lease.
    const leased = s.designations.filter((d) => d.pawn).map((d) => d.pawn);
    expect(new Set(leased).size).toBe(leased.length);
  });

  it('drafted pawns only follow orders; builds fill in queue order; bills and storage', () => {
    const w = world();
    let t = 1_762_000_000_000;
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => t });
    const { id } = sim.create({ name: 'Test', pawns: 2 });
    const [a, b] = sim.state(id).pawns.map((p) => p.name) as [string, string];
    sim.draft(id, { pawns: [a], drafted: true });
    sim.orders(id, { pawns: [a], order: { kind: 'move', target: { x: 6, y: 64, z: 0 } } });
    const built = sim.builds(id, {
      op: 'add',
      items: [
        { kind: 'floor', area: [-6, 63, 4, -4, 63, 6], material: 'cobblestone' },
        { kind: 'wall', area: [-6, 64, 4, -4, 64, 6], material: 'cobblestone', height: 2 },
      ],
    });
    expect(built.ids).toHaveLength(2);
    sim.builds(id, { op: 'reorder', ids: [built.ids![1]!, built.ids![0]!] });
    expect(sim.state(id).builds?.find((x) => x.id === built.ids![1])?.order).toBe(1);
    sim.priorities(id, {
      pawns: [b],
      work: { build: 1, haul: 0, chop: 0, mine: 0, farm: 0, craft: 0, cook: 0, hunt: 0, guard: 0, doctor: 0 },
    });
    for (let i = 0; i < 300; i++) {
      t += 100;
      sim.step(100);
    }
    const s = sim.state(id);
    const pa = s.pawns.find((p) => p.name === a)!;
    expect(pa.drafted).toBe(true);
    expect(Math.round(pa.pos![0])).toBe(6);
    expect((s.ghosts?.blocks ?? []).some((g) => g[6] === 'done')).toBe(true);
    const bench = s.workbenches![0]!;
    const added = sim.bills(id, { op: 'add', bench: bench.pos, bill: { recipe: 'sticks', mode: 'until', target: 64 } });
    expect(added.id).toBeTruthy();
    const reg = sim.storage(id, { op: 'assign_area', area: [0, 60, -3, 3, 70, -1] });
    expect(reg.count).toBe(1); // a double chest is one storage
    sim.storage(id, { op: 'set', chests: [[1, 64, -2]], filter: { allow: ['wood'], deny: [] }, priority: 'preferred' });
    const st = sim.state(id).storages![0]!;
    expect(st.doubleWith).toEqual([2, 64, -2]);
    expect(st.priority).toBe('preferred');
  });

  it('builds craft what they need at designated benches; tiers unlock with them; auto bills are cancel-only', () => {
    const w = world();
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => 1_762_000_000_000 });
    const { id } = sim.create({ name: 'Test', pawns: 1 });
    // The camp's table was found at founding; release it: nothing designated.
    expect(sim.state(id).workbenches?.map((x) => x.pos)).toEqual([[-2, 64, -2]]);
    expect(sim.workbenches(id, { op: 'release', pos: [-2, 64, -2] }).designated).toBe(false);
    expect(sim.state(id, 1).removed?.workbenches).toContainEqual([-2, 64, -2]);
    const fence = () => sim.architect(id).entries.find((e) => e.id === 'fence')!;
    expect(fence()).toMatchObject({ tier: 'workbench', unlocked: false });
    expect(fence().reason).toMatch(/^Needs a crafting table/);
    expect(sim.architect(id).entries.find((e) => e.id === 'planks')).toMatchObject({ tier: 'hand', unlocked: true });
    // A fence build with no table: the colony makes one (auto-placing is on), and the fence waits.
    const built = sim.builds(id, { op: 'add', items: [{ kind: 'fence', area: [3, 64, 3, 5, 64, 3] }] });
    let b = sim.state(id).builds!.find((x) => x.id === built.ids![0])!;
    expect(b.materials[0]).toMatchObject({ need: 3, have: 0, crafting: 3, short: 0, via: 'craft' });
    expect(b.station?.kind).toBe('crafting_table');
    // Designated: the tier unlocks and the build's auto bill lands on the bench.
    expect(sim.workbenches(id, { op: 'designate', pos: [-2, 64, -2] }).designated).toBe(true);
    expect(fence().unlocked).toBe(true);
    const bench = sim.state(id).workbenches!.find((x) => x.pos[0] === -2)!;
    const auto = bench.bills.find((x) => x.forBuild === built.ids![0])!;
    expect(auto).toMatchObject({ recipe: 'fence', mode: 'count', target: 3, forItem: 'fence' });
    expect(() => sim.bills(id, { op: 'pause', id: auto.id })).toThrow(/only be cancelled/);
    sim.bills(id, { op: 'remove', id: auto.id });
    b = sim.state(id).builds!.find((x) => x.id === built.ids![0])!;
    expect(b.noCraft).toContain('fence');
    expect(b.materials[0]).toMatchObject({ via: 'none', crafting: 0, short: 3 });
    expect(() => sim.workbenches(id, { op: 'designate', pos: [9, 64, 9] })).toThrow(/no workbench/);
    expect(sim.options(id, { autoPlaceBenches: false })).toEqual({
      autoPlaceBenches: false,
      detectBenches: false,
      idle: 'recreation',
    });
    expect(sim.state(id).options?.autoPlaceBenches).toBe(false);
  });
});

describe('story fixtures', () => {
  const live = loadFixture('live');
  it.each([
    ['storyList', C.StoryListResponse],
    ['storyState', C.StoryColonyState],
    ['storyRecipes', C.StoryRecipesResponse],
    ['storyDesignation', C.StoryDesignationResponse],
  ] as const)('%s parses after toCamel', (key, schema) => {
    expect(live[key], key).toBeDefined();
    const r = (schema as C.RouteDef['response'] & object).safeParse(C.toCamel(live[key]));
    if (!r.success) throw new Error(JSON.stringify(r.error.issues.slice(0, 5)));
  });

  it("the director's own delta form parses: designation targets, string removed keys, array order targets", () => {
    const d = C.StoryColonyState.parse(C.toCamel(live.storyStateBot));
    expect(d.tieBreak).toBe('column');
    expect(C.designationCells(d.designations[0]!)).toHaveLength(3);
    expect(C.designationEntity(d.designations[1]!)).toBe(7);
    expect(d.removed?.storages).toEqual(['6,64,-4']);
    expect(C.targetPos(d.orders[0]!.target)).toEqual([3, 64, 3]);
    expect(C.targetPos(d.pawns[0]!.job!.target)).toEqual([10, 64, 0]);
    expect(d.workbenches?.[1]?.supported).toBe(false);
    expect(C.targetPos('1,2,3')).toEqual([1, 2, 3]);
  });

  it('a bill in a request is converted like any field (`bill` is opaque only in responses)', () => {
    const body = C.toSnake(
      { op: 'add', bench: [1, 2, 3], bill: { recipe: 'sticks', mode: 'count', minSkill: 4 } },
      { opaque: C.REQUEST_OPAQUE_KEYS },
    );
    expect((body as { bill: Record<string, unknown> }).bill.min_skill).toBe(4);
  });

  it('a story frame carries the colony id and pawn details', () => {
    const f = C.StoryColonyFrame.parse(C.toCamel({ ...(live.storyState as object), id: 'story-1' }));
    expect(f.pawns[0]?.skills.chop?.level).toBeTypeOf('number');
    expect(f.timeOfDay).toBeTypeOf('number');
    expect(f.builds?.[0]?.blocksTotal).toBeGreaterThan(0);
  });
});
