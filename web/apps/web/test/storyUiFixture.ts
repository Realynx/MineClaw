/**
 * Colony states for the Story UI goldens (storyUiGolden.test.tsx): the mock director (StorySim) runs one scripted colony over a
 * small fixed world — zones, growing-zone bills, builds, storage, bills, pawns offline, policies — and hands back its state at a
 * few points (founded, ordered, worked). `Math.random` and the clock are seeded, so every state is the same on every run.
 */
import type { StoryColonyState } from '@mineclaw/contracts';
import { StorySim, type StoryWorld } from '@mineclaw/contracts/testing';
import { vi } from 'vitest';

/** A seeded generator (mulberry32) for Math.random. */
export function seeded(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Grass at y 63 over dirt and stone, a grove, crops, tall grass, a pond, a camp with benches, chests and beds. */
function world(): StoryWorld {
  const blocks = new Map<string, [number, number]>();
  const k = (x: number, y: number, z: number) => `${x},${y},${z}`;
  const set = (x: number, y: number, z: number, id: number, meta = 0) => blocks.set(k(x, y, z), [id, meta]);
  for (const [x, z, h] of [
    [10, 0, 5],
    [14, 0, 4],
    [10, 5, 6],
  ] as const) {
    for (let y = 64; y < 64 + h; y++) set(x, y, z, 17);
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) if (dx || dz) set(x + dx, 63 + h, z + dz, 18);
  }
  for (let x = 0; x < 6; x++) set(x, 64, 8, 59, x % 2 ? 7 : 3);
  for (let x = -6; x < -2; x++) set(x, 64, 12, 31, 1);
  for (let x = 20; x < 25; x++) for (let z = 20; z < 24; z++) set(x, 63, z, 9);
  set(-2, 64, -2, 58);
  set(-3, 64, -2, 61, 3);
  set(1, 64, -2, 54);
  set(2, 64, -2, 54);
  set(4, 64, -2, 54);
  set(-6, 64, 2, 26, 0);
  set(-6, 64, 3, 26, 8);
  const entityList = [
    { id: 7, kind: 'item', item: 4, x: 5.5, y: 64, z: 5.5 },
    { id: 8, kind: 'mob', type: 90, x: 6.5, y: 64, z: 6.5 },
    { id: 12, kind: 'mob', type: 92, x: -10.5, y: 64, z: -9.5 },
  ];
  return {
    blockAt: (x, y, z) =>
      blocks.get(k(x, y, z)) ?? (y > 63 ? [0, 0] : y === 63 ? [2, 0] : y > 59 ? [3, 0] : y <= 0 ? [7, 0] : [1, 0]),
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

export interface StoryUiStates {
  founded: StoryColonyState;
  ordered: StoryColonyState;
  worked: StoryColonyState;
  list: ReturnType<StorySim['list']>;
  architect: ReturnType<StorySim['architect']>;
}

/** Runs the scripted colony (call inside a test: it seeds Math.random and Date.now for the run, then restores them). */
export function storyUiStates(): StoryUiStates {
  let t = 1_762_000_000_000;
  const rnd = vi.spyOn(Math, 'random').mockImplementation(seeded(7));
  const now = vi.spyOn(Date, 'now').mockImplementation(() => t);
  try {
    const sim = new StorySim({ world: world(), defaultHome: [0, 63, 0], now: () => t });
    const step = (n: number, ms: number) => {
      for (let i = 0; i < n; i++) {
        t += ms;
        sim.step(ms);
      }
    };
    const { id } = sim.create({ name: 'Golden', pawns: 4 });
    const founded = sim.state(id);
    const [a, b, c, d] = founded.pawns.map((p) => p.name) as [string, string, string, string];
    /** Runs one request; a refused one (the director throws) leaves the state as it was. */
    const op = (f: () => unknown) => {
      try {
        f();
      } catch {
        /* refused */
      }
    };
    op(() =>
      sim.priorities(id, {
        pawns: [a, b],
        work: { chop: 1, mine: 2, haul: 3, build: 1, farm: 2, craft: 3, cook: 4, hunt: 2 },
      }),
    );
    op(() => sim.roles(id, { op: 'add', role: { name: 'Woodcutter', priorities: { chop: 1 } } } as never));
    op(() => sim.roles(id, { op: 'assign', id: 'woodcutter', pawns: [c] } as never));
    op(() => sim.schedule(id, { pawns: [d], hours: { '0': 'sleep', '12': 'work', '20': 'recreation' } }));
    op(() => sim.options(id, { autoPlaceBenches: true, idle: 'wander' } as never));
    op(() => sim.designations(id, { op: 'add', kind: 'chop', area: [8, 60, -2, 20, 80, 8] }));
    op(() => sim.designations(id, { op: 'add', kind: 'cut', area: [-8, 60, 12, -3, 70, 12] }));
    op(() => sim.designations(id, { op: 'add', kind: 'mine', area: [30, 55, 30, 33, 62, 33] }));
    op(() => sim.zones(id, { op: 'add', kind: 'farm', area: [0, 63, 14, 5, 63, 17], plant: 'wheat' } as never));
    op(() => sim.zones(id, { op: 'add', kind: 'farm', area: [6, 63, 14, 8, 63, 17], plant: 'carrots' } as never));
    op(() => sim.zones(id, { op: 'add', kind: 'home', area: [-9, 63, -3, 6, 63, 4], name: 'Camp' } as never));
    op(() => sim.zones(id, { op: 'add', kind: 'mine', area: [-20, 63, -20, -17, 63, -17] } as never));
    op(() => sim.zones(id, { op: 'add', kind: 'fish', area: [19, 63, 19, 25, 63, 24] } as never));
    op(() => sim.zones(id, { op: 'add', kind: 'dump', area: [12, 63, 12, 13, 63, 13] } as never));
    const farmId = () => String(sim.state(id).zones.find((z) => z.kind === 'farm')!.id);
    op(() => sim.zones(id, { op: 'bill_add', id: farmId(), plant: 'potatoes', mode: 'count', target: 20 } as never));
    op(() => sim.zones(id, { op: 'bill_add', id: farmId(), plant: 'pumpkin', mode: 'until', target: 5 } as never));
    op(() => sim.zones(id, { op: 'seek_seeds', id: farmId() } as never));
    op(() =>
      sim.builds(id, {
        op: 'add',
        items: [
          { kind: 'floor', area: [-6, 63, 5, -3, 63, 8], material: 'cobblestone' },
          { kind: 'wall', area: [-6, 64, 5, -3, 64, 8], material: 'oak_planks', height: 3 },
          { kind: 'door', blocks: [[-5, 64, 5]], material: 'oak_planks', facing: 's' },
          { kind: 'roof', area: [-6, 67, 5, -3, 67, 8], material: 'oak_planks', roof: { style: 'gable', overhang: 1 } },
          { kind: 'bed', blocks: [[-4, 64, 7]], facing: 'e' },
        ],
      } as never),
    );
    op(() => sim.storage(id, { op: 'assign_area', area: [0, 60, -3, 6, 70, -1] }));
    op(() => sim.workbenches(id, { op: 'designate', pos: [-2, 64, -2] }));
    op(() => sim.workbenches(id, { op: 'designate', pos: [-3, 64, -2] }));
    op(() =>
      sim.bills(id, { op: 'add', bench: [-2, 64, -2], bill: { recipe: 'torch', mode: 'count', target: 8 } } as never),
    );
    op(() =>
      sim.bills(id, { op: 'add', bench: [-2, 64, -2], bill: { recipe: 'sticks', mode: 'until', target: 64 } } as never),
    );
    op(() => sim.policies(id, { op: 'set', policy: { name: 'Miner', tools: ['pickaxe'], armor: false } } as never));
    op(() => sim.policies(id, { op: 'assign', id: 'miner', pawns: [b] } as never));
    op(() =>
      sim.materialRules(id, { op: 'set', rule: { material: 'iron_ore', tool: 'pickaxe', tier: 'stone' } } as never),
    );
    op(() => sim.draft(id, { pawns: [d], drafted: true }));
    op(() => sim.setSeats(id, 0, 1));
    op(() => sim.setOffline(id, b, 'cap', 60_000));
    const ordered = sim.state(id);
    step(300, 500);
    sim.failDesignations(
      id,
      sim
        .state(id)
        .designations.slice(0, 3)
        .map((x) => x.id),
      "can't reach it",
    );
    const worked = sim.state(id);
    return { founded, ordered, worked, list: sim.list(), architect: sim.architect(id) };
  } finally {
    rnd.mockRestore();
    now.mockRestore();
  }
}
