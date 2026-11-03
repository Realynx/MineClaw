import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { StorySim, type StoryWorld } from '../src/testing.js';
import { loadFixture } from './helpers.js';

/** A flat world (grass at y 63) with a 3-tree grove, a ripe wheat row, a camp with a crafting table and two chests. */
function world(): StoryWorld & { changed: string[] } {
  const blocks = new Map<string, [number, number]>();
  const k = (x: number, y: number, z: number) => `${x},${y},${z}`;
  for (const [x, z] of [[10, 0], [14, 0], [10, 5]] as const) for (let y = 64; y <= 68; y++) blocks.set(k(x, y, z), [17, 0]);
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
    ai: () => [{ name: 'maid_chan', kind: 'main', owner: null, online: true, hp: 20, food: 20, pos: [0, 64, 0], dim: 0, task: 'mining' }],
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
    expect(C.StoryScheduleRequest.safeParse({ pawns: ['a'], hours: { '0': 'sleep', '23': 'free' } }).success).toBe(true);
    expect(C.StoryOrderRequest.safeParse({ pawns: ['a'], order: { kind: 'chop', target: { x: 1, y: 2, z: 3 } }, queue: true }).success).toBe(true);
    expect(C.StoryOrderRequest.safeParse({ pawns: ['a'], order: { kind: 'chop', target: { x: 1, y: 2, z: 3, who: 'x' } } }).success).toBe(false);
    expect(C.StoryBuildRequest.safeParse({ op: 'add', items: [{ kind: 'wall', area: [0, 64, 0, 4, 64, 4], material: 'oak_planks', height: 13 }] }).success).toBe(false);
    expect(C.StoryBillsRequest.safeParse({ op: 'add', bench: [1, 2, 3], bill: { recipe: 'sticks', mode: 'until', target: 64, minSkill: 3 } }).success).toBe(true);
    expect(C.StoryStorageRequest.safeParse({ op: 'set', chests: [[1, 2, 3]], filter: { allow: ['wood'] }, priority: 'preferred' }).success).toBe(true);
    expect(C.StoryDirectiveRequest.safeParse({ bots: ['maid_chan'], directive: { kind: 'text', text: 'come here' }, requestId: 'r-12345678' }).success).toBe(true);
  });

  it('hours and clock labels', () => {
    expect(C.storyHour(0)).toBe(0);
    expect(C.storyHour(23999)).toBe(23);
    expect(C.storyClock(0)).toBe('06:00');
    expect(C.storyClock(20)).toBe('02:00');
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
    expect(C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'slab' } }).every((b) => b[3] === 44)).toBe(true);
    const gable = C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'gable' } });
    // Ridge along x (the longer side): z 0 and 4 are eaves (y 67), z 2 is the ridge (y 69).
    expect(gable.filter((b) => b[2] === 0).every((b) => b[1] === 67)).toBe(true);
    expect(gable.filter((b) => b[2] === 2).every((b) => b[1] === 69)).toBe(true);
    expect(C.expandBuildItem({ kind: 'roof', area, material: 'oak_planks', roof: { style: 'flat', overhang: 1 } })).toHaveLength(9 * 7);
  });

  it('doors are two high, furniture beds two long, remove places air', () => {
    expect(C.expandBuildItem({ kind: 'door', blocks: [[2, 64, 0]], material: 'oak_planks' }).map((b) => b[1])).toEqual([64, 65]);
    const bed = C.expandBuildItem({ kind: 'bed', blocks: [[0, 64, 0]], facing: 'e' });
    expect(bed.map((b) => [b[0], b[2], b[4]])).toEqual([[0, 0, 3], [1, 0, 11]]);
    expect(C.expandBuildItem({ kind: 'remove', area: [0, 64, 0, 1, 65, 0], material: 'stone' }).every((b) => b[3] === 0)).toBe(true);
  });
});

describe('tool matching', () => {
  it('chop counts whole trees; harvest only ripe crops; mine filters', () => {
    expect(C.countTrees([[0, 64, 0], [0, 65, 0], [1, 64, 1], [5, 64, 5]])).toBe(2);
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

  it('pawns pick jobs by priority, lease them and finish them', () => {
    const w = world();
    let t = 1_762_000_000_000;
    const sim = new StorySim({ world: w, defaultHome: [0, 63, 0], now: () => t });
    const { id } = sim.create({ name: 'Test', pawns: 2 });
    const names = sim.state(id).pawns.map((p) => p.name);
    sim.priorities(id, { pawns: names, work: Object.fromEntries(C.STORY_WORK_TYPES.map((k) => [k, k === 'chop' ? 1 : 0])) });
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
    const built = sim.builds(id, { op: 'add', items: [{ kind: 'floor', area: [-6, 63, 4, -4, 63, 6], material: 'cobblestone' }, { kind: 'wall', area: [-6, 64, 4, -4, 64, 6], material: 'cobblestone', height: 2 }] });
    expect(built.ids).toHaveLength(2);
    sim.builds(id, { op: 'reorder', ids: [built.ids![1]!, built.ids![0]!] });
    expect(sim.state(id).builds?.find((x) => x.id === built.ids![1])?.order).toBe(1);
    sim.priorities(id, { pawns: [b], work: { build: 1, haul: 0, chop: 0, mine: 0, farm: 0, craft: 0, cook: 0, hunt: 0, guard: 0, doctor: 0 } });
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
    const body = C.toSnake({ op: 'add', bench: [1, 2, 3], bill: { recipe: 'sticks', mode: 'count', minSkill: 4 } }, { opaque: C.REQUEST_OPAQUE_KEYS });
    expect((body as { bill: Record<string, unknown> }).bill.min_skill).toBe(4);
  });

  it('a story frame carries the colony id and pawn details', () => {
    const f = C.StoryColonyFrame.parse(C.toCamel({ ...(live.storyState as object), id: 'story-1' }));
    expect(f.pawns[0]?.skills.chop?.level).toBeTypeOf('number');
    expect(f.timeOfDay).toBeTypeOf('number');
    expect(f.builds?.[0]?.blocksTotal).toBeGreaterThan(0);
  });
});
