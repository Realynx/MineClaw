/**
 * Mock containers (test and mock helpers, exported from `@mineclaw/contracts/testing`) on the viewer's fixture island, for the
 * mock modes (MSW in the web app, Nest's mock bridge):
 * - the island's double chest (2,74,-4)+(3,74,-4): seen 3 min ago by maid_chan_102 (exact slots, both halves);
 * - the island's single chest (6,74,-4): Story's ledger (item counts, laid out);
 * - the gallery's double chest (17,70,-32)+(18,70,-32): open right now by maid_chan_70 (live; counts move with `tick`);
 * - a furnace (0,74,-6): smelting iron;
 * - anything else (the gallery's single chest at 20,70,-32 among them): never opened (source unknown).
 * Results are the public (camelCase) form; `toSnake` them for the bridge wire.
 */
import type { ContainerKind, ContainerList, ContainerSlot, ContainerSummary, ContainerView } from './containers.js';

type V3 = [number, number, number];
/** [slot, id, meta, count, name, display, maxDurability (0 = none)] */
type Item = [number, number, number, number, string, string, number?];

interface Fixture {
  cells: V3[];
  kind: ContainerKind;
  size: number;
  items: (tick: number) => Item[];
  source: ContainerView['source'];
  by: string | null;
  /** ms before now (null = now) */
  age: number | null;
  exact: boolean;
}

const MIN = 60_000;

const FIXTURES: Fixture[] = [
  {
    cells: [
      [2, 74, -4],
      [3, 74, -4],
    ],
    kind: 'double_chest',
    size: 54,
    source: 'memory',
    by: 'maid_chan_102',
    age: 3 * MIN,
    exact: true,
    items: () => [
      [0, 17, 0, 64, 'log', 'Wood'],
      [1, 17, 0, 64, 'log', 'Wood'],
      [2, 17, 2, 37, 'birch_log', 'Birch Wood'],
      [3, 5, 0, 48, 'planks', 'Wooden Planks'],
      [9, 280, 0, 32, 'stick', 'Stick'],
      [10, 6, 0, 12, 'sapling', 'Oak Sapling'],
      [13, 275, 31, 1, 'stone_axe', 'Stone Axe', 131],
      [26, 4, 0, 64, 'cobblestone', 'Cobblestone'],
      [27, 4, 0, 64, 'cobblestone', 'Cobblestone'],
      [28, 263, 0, 21, 'coal', 'Coal'],
      [40, 265, 0, 9, 'iron_ingot', 'Iron Ingot'],
      [53, 264, 0, 2, 'diamond', 'Diamond'],
    ],
  },
  {
    cells: [[6, 74, -4]],
    kind: 'chest',
    size: 27,
    source: 'story',
    by: 'story-1',
    age: 40_000,
    exact: false,
    items: () => [
      [0, 297, 0, 14, 'bread', 'Bread'],
      [1, 320, 0, 6, 'cooked_porkchop', 'Cooked Porkchop'],
      [2, 260, 0, 9, 'apple', 'Red Apple'],
      [3, 295, 0, 23, 'seeds', 'Seeds'],
    ],
  },
  {
    cells: [
      [17, 70, -32],
      [18, 70, -32],
    ],
    kind: 'double_chest',
    size: 54,
    source: 'live',
    by: 'maid_chan_70',
    age: null,
    exact: true,
    items: (tick) => [
      [0, 4, 0, 20 + (tick % 40), 'cobblestone', 'Cobblestone'],
      [1, 3, 0, 41, 'dirt', 'Dirt'],
      [4, 50, 0, 16, 'torch', 'Torch'],
      [8, 278, 400, 1, 'diamond_pickaxe', 'Diamond Pickaxe', 1561],
      [30, 35, 14, 6, 'red_wool', 'Red Wool'],
    ],
  },
  {
    cells: [[0, 74, -6]],
    kind: 'furnace',
    size: 3,
    source: 'memory',
    by: 'maid_chan',
    age: 25_000,
    exact: true,
    items: () => [
      [0, 15, 0, 5, 'iron_ore', 'Iron Ore'],
      [1, 263, 0, 3, 'coal', 'Coal'],
      [2, 265, 0, 2, 'iron_ingot', 'Iron Ingot'],
    ],
  },
];

function slotOf([slot, id, meta, count, name, display, max = 0]: Item): ContainerSlot {
  return {
    slot,
    id,
    meta,
    count,
    name,
    display,
    durability: max > 0 ? { max, left: Math.max(0, max - meta) } : null,
    enchanted: false,
  };
}

const same = (a: V3, b: V3) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

function view(f: Fixture, dim: number, tick: number, now: number): ContainerView {
  return {
    pos: f.cells[0]!,
    dim,
    kind: f.kind,
    size: f.size,
    slots: f.items(tick).map(slotOf),
    seenAt: f.age === null ? now : now - f.age,
    seenBy: f.by,
    source: f.source,
    version: 'b1.8.1',
    exact: f.exact,
    cells: f.cells,
  };
}

/** The container with a block at x,y,z (overworld only); an unknown chest anywhere else. */
export function mockContainer(dim: number, x: number, y: number, z: number, tick = 0, now = Date.now()): ContainerView {
  const f = dim === 0 ? FIXTURES.find((c) => c.cells.some((p) => same(p, [x, y, z]))) : undefined;
  if (f) return view(f, dim, tick, now);
  return {
    pos: [x, y, z],
    dim,
    kind: 'chest',
    size: 27,
    slots: [],
    seenAt: null,
    seenBy: null,
    source: 'unknown',
    version: 'b1.8.1',
    exact: true,
    cells: [[x, y, z]],
  };
}

/** The known containers with a block in the box, newest look first. */
export function mockContainers(
  dim: number,
  box: { x0: number; z0: number; x1: number; z1: number; y0?: number; y1?: number },
  limit = 200,
  tick = 0,
  now = Date.now(),
): ContainerList {
  const [x0, x1] = [Math.min(box.x0, box.x1), Math.max(box.x0, box.x1)];
  const [z0, z1] = [Math.min(box.z0, box.z1), Math.max(box.z0, box.z1)];
  const [y0, y1] = [box.y0 ?? 0, box.y1 ?? 319];
  const inBox = (p: V3) => p[0] >= x0 && p[0] <= x1 && p[2] >= z0 && p[2] <= z1 && p[1] >= y0 && p[1] <= y1;
  const all: ContainerSummary[] = (dim === 0 ? FIXTURES : [])
    .filter((f) => f.cells.some(inBox))
    .map((f) => {
      const v = view(f, dim, tick, now);
      const totals = new Map<string, { id: number; meta: number; name: string; count: number }>();
      for (const s of v.slots) {
        const k = `${s.id}:${s.durability ? 0 : s.meta}`;
        const t = totals.get(k) ?? { id: s.id, meta: s.durability ? 0 : s.meta, name: s.name, count: 0 };
        t.count += s.count;
        totals.set(k, t);
      }
      return {
        pos: v.pos,
        dim,
        kind: v.kind,
        size: v.size,
        used: v.slots.length,
        total: v.slots.reduce((n, s) => n + s.count, 0),
        top: [...totals.values()].sort((a, b) => b.count - a.count).slice(0, 5),
        seenAt: v.seenAt,
        seenBy: v.seenBy,
        source: v.source,
        cells: v.cells,
      };
    })
    .sort((a, b) => (b.seenAt ?? 0) - (a.seenAt ?? 0));
  return { dim, containers: all.slice(0, limit), capped: all.length > limit };
}

/** The fixture positions (every block of every known mock container). */
export const MOCK_CONTAINERS: ReadonlyArray<{ kind: ContainerKind; cells: readonly V3[] }> = FIXTURES.map((f) => ({
  kind: f.kind,
  cells: f.cells,
}));
