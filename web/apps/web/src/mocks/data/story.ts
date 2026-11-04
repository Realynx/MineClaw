/**
 * Story Mode in mock mode (MSW): the shared synthetic director (`@mineclaw/contracts/testing`) playing on the world viewer's
 * fixture island, so every count is real: three trees, a wheat field, tall grass and flowers, a double and a single chest,
 * the island's cows and the cobblestone drop. A camp (crafting table, furnace) is added at the colony's home through the
 * mock world source's block deltas, and so is everything the pawns build or fell. The hive's bodies are the AI bodies.
 */
import { StorySim, type SimEntity } from '@mineclaw/contracts/testing';
import type { StoryAiBody } from '@mineclaw/contracts';
import { FixtureWorld, surface } from '@mineclaw/world-viewer/testing';
import { BOTS } from './live';

const world = new FixtureWorld('b1.8.1', 8);
const columns = new Map<string, { ids: Uint8Array; meta: Uint8Array }>();
const overrides = new Map<string, [number, number]>();
/** Block changes for the viewer (the mock world source drains them into its block deltas). */
export const pendingBlocks: Array<[number, number, number, number, number]> = [];

const cellIndex = (lx: number, y: number, lz: number) => ((y >> 7) << 15) | ((lx * 16 + lz) * 128 + (y & 127));

function blockAt(x: number, y: number, z: number): readonly [number, number] {
  const o = overrides.get(`${x},${y},${z}`);
  if (o) return o;
  if (y < 0 || y >= world.height) return [0, 0];
  const cx = x >> 4, cz = z >> 4;
  if (!world.has(cx, cz)) return [0, 0];
  const key = `${cx},${cz}`;
  let col = columns.get(key);
  if (!col) columns.set(key, (col = world.column(cx, cz)));
  const i = cellIndex(x & 15, y, z & 15);
  return [col.ids[i]!, col.meta[i]!];
}

/** Every block the director changed (re-sent after the viewer loads columns, whose fixture data lacks them). */
export function allOverrides(): Array<[number, number, number, number, number]> {
  return [...overrides].map(([k, [id, meta]]) => {
    const [x, y, z] = k.split(',').map(Number) as [number, number, number];
    return [x, y, z, id, meta];
  });
}

function setBlock(x: number, y: number, z: number, id: number, meta: number): void {
  overrides.set(`${x},${y},${z}`, [id, meta]);
  pendingBlocks.push([x, y, z, id, meta]);
}

/** Where the mock colony settles: an open patch west of the stairs, by the island centre. */
export const STORY_HOME: [number, number, number] = [-2, surface(-2, 1), 1];

// The camp: a crafting table and a furnace by home.
setBlock(-4, surface(-4, -2) + 1, -2, 58, 0);
setBlock(-3, surface(-3, -2) + 1, -2, 61, 3);
// Two beds by the camp (foot, then head one block south: meta 8).
for (const bx of [-6, -8]) {
  setBlock(bx, surface(bx, 2) + 1, 2, 26, 0);
  setBlock(bx, surface(bx, 3) + 1, 3, 26, 8);
}

let entityCache: SimEntity[] = [];
let botCache: Array<{ name: string; x: number; y: number; z: number; hp?: number; food?: number; job?: string }> = [];

/** Kept fresh from the mock world source the viewer draws (the same instance, so positions agree). */
async function refreshEntities(): Promise<void> {
  try {
    const { createMockWorldSource } = await import('@/features/world/mockSource');
    const d = await createMockWorldSource().entities(0);
    entityCache = d.entities.map((e) => ({ id: e.id ?? -1, kind: e.kind, type: e.type ?? undefined, item: e.item ?? undefined, x: e.x, y: e.y, z: e.z }));
    botCache = d.bots.map((b) => ({ name: b.name, x: b.x, y: b.y, z: b.z, hp: b.hp, food: b.food, job: b.job ?? undefined }));
  } catch {
    /* node test env: no viewer module */
  }
}

function aiBodies(): StoryAiBody[] {
  return BOTS.filter((b) => b.kind !== 'script' && b.dim === 0).map((b) => {
    const at = botCache.find((x) => x.name === b.name);
    return {
      name: b.name,
      kind: b.kind,
      owner: b.owner,
      online: b.status === 'online',
      hp: at?.hp ?? b.hp,
      food: at?.food ?? b.food,
      pos: at ? [at.x, at.y, at.z] : b.pos,
      dim: b.dim,
      task: at?.job ?? b.task,
      brainState: b.kind === 'subhive' ? `driven by ${b.owner}` : 'awake',
      ...(b.script ? { script: b.script.name } : {}),
    };
  });
}

export const storySim = new StorySim({
  defaultHome: STORY_HOME,
  world: {
    blockAt,
    ground: (x, z) => {
      for (let y = 100; y > 0; y--) {
        const [id] = blockAt(x, y, z);
        if (id !== 0 && id !== 18 && id !== 17 && id !== 31 && id !== 37 && id !== 38 && id !== 59 && id !== 50 && id !== 8 && id !== 9) return y;
      }
      return surface(x, z);
    },
    entities: () => entityCache,
    setBlock,
    ai: aiBodies,
  },
});

let started = false;
const sent = new Map<string, number>();

/** Starts the director's clock (500 ms steps) and returns `story` frames to broadcast (camelCase, with the colony id). */
export function storyFrames(): Array<Record<string, unknown>> {
  const out: Array<Record<string, unknown>> = [];
  for (const c of storySim.colonies.values()) {
    const last = sent.get(c.id);
    if (last === c.rev) continue;
    sent.set(c.id, c.rev);
    if (last === undefined) continue;
    out.push({ ...storySim.state(c.id, last), id: c.id });
  }
  return out;
}

export function startStory(emit: (event: string, data: unknown) => void): void {
  if (started) return;
  started = true;
  void refreshEntities();
  setInterval(() => void refreshEntities(), 1500);
  setInterval(() => {
    storySim.step(500);
    for (const f of storyFrames()) emit('story', f);
  }, 500);
}

function busy(): boolean {
  try {
    return sessionStorage.getItem('mcx-mock-story') === 'busy';
  } catch {
    return false;
  }
}

/**
 * The perf seed (`sessionStorage['mcx-mock-story'] = 'busy'`, the Story perf e2e): ten pawns and a few hundred designations
 * the way a live colony has them: a surface mine over the camp (one per column), every plant cut, the trees chopped, and a
 * band of failed ones with three reasons (labelled clusters on the map).
 */
function seedBusy(): void {
  const { id } = storySim.create({ name: 'Busy', pawns: 10 });
  const blocks: Array<[number, number, number]> = [];
  for (let x = -16; x <= 16; x++) for (let z = -16; z <= 16; z++) if ((x + z) % 3 !== 0) blocks.push([x, surface(x, z), z]);
  storySim.designations(id, { op: 'add', kind: 'mine', blocks: blocks.slice(0, 600) });
  storySim.designations(id, { op: 'add', kind: 'cut', area: [-20, 50, -20, 20, 90, 20] });
  storySim.designations(id, { op: 'add', kind: 'chop', area: [-20, 50, -20, 20, 90, 20] });
  const ids = (storySim.state(id).designations ?? []).filter((d) => d.kind === 'mine').map((d) => d.id);
  const reasons = [
    "can't reach dirt at the bottom of the pit",
    'reported done, but the grass is still there',
    "won't dig: someone is standing on it",
  ];
  reasons.forEach((r, i) => storySim.failDesignations(id, ids.slice(i * 40, i * 40 + 40), r));
}

/** The e2e and the screenshots can start from no colony (`sessionStorage['mcx-mock-story'] = 'empty'`). */
export function seedStory(): void {
  let empty = false;
  try {
    empty = sessionStorage.getItem('mcx-mock-story') === 'empty';
  } catch {
    /* node */
  }
  if (empty || storySim.colonies.size > 0) return;
  if (busy()) return seedBusy();
  const { id } = storySim.create({ name: 'Hollow', pawns: 5 });
  // A lived-in start: one tree marked, the chests in use, a bill at the bench, a small shed queued.
  storySim.designations(id, { op: 'add', kind: 'chop', area: [-10, 50, -4, -6, 90, 0] });
  storySim.storage(id, { op: 'assign_area', area: [1, 50, -5, 7, 90, -3], priority: 'preferred' });
  const bench = storySim.state(id).workbenches?.[0]?.pos;
  if (bench) storySim.bills(id, { op: 'add', bench: [bench[0], bench[1], bench[2]], bill: { recipe: 'torch', mode: 'count', target: 8 } });
  const y = surface(-8, 4) + 1;
  storySim.builds(id, { op: 'add', items: [{ kind: 'wall', area: [-10, y, 3, -6, y, 6], material: 'cobblestone', height: 2 }] });
  // Live 11-03 as the mock plays it: the server's seats are tight (1 free, 2 kept for offline regulars). One pawn was kicked
  // "not white-listed" (main re-adds it; it is back in a few minutes, or at once with "Bring online"), one didn't fit.
  storySim.setSeats(id, 1, 2);
  const names = [...storySim.colony(id).pawns.keys()];
  storySim.setOffline(id, names[names.length - 1]!, 'whitelist', 5 * 60_000);
  storySim.setOffline(id, names[names.length - 2]!, 'cap');
  // A cut over the tall grass by the camp that kept failing (a hive claim): kept on the map with why, resting.
  const cut = storySim.designations(id, { op: 'add', kind: 'cut', area: [-14, 50, -14, 14, 90, 14] });
  if (cut.count > 0) {
    const ids = (storySim.state(id).designations ?? []).filter((d) => d.kind === 'cut').map((d) => d.id);
    storySim.failDesignations(id, ids.slice(0, 6), 'double_plant at 5,64,2 is busy: another hive bot is working on that part right now');
  }
}
