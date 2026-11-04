import { StoryDesignationRequest } from '@mineclaw/contracts';
import { encodeColumn, encodeMcwc, lightSections, MeshEngine } from '@mineclaw/world-viewer/testing';
import { describe, expect, it } from 'vitest';
import {
  clampLayers,
  DragGate,
  heightBody,
  heightLabel,
  heightQuery,
  heightTargets,
  keyAction,
  MAX_DRAG_CELLS,
  NOT_GROUND_IDS,
  releaseIsNoop,
  type HeightMode,
} from '@/features/story/tools';

// Mine height modes (Surface, Level, Down to level, Flatten here, Layers): which cells a drag designates over a known
// heightmap, read through the viewer engine's real `stacks` query; and cancelling a drag (Esc, the other button).

type B = [number, number, number, number, number?];
const H = 128;
const cellIndex = (lx: number, y: number, lz: number) => ((y >> 7) << 15) | ((lx * 16 + lz) * 128 + (y & 127));

/** An engine holding these blocks (one or more chunk columns, everything else air). */
async function engineWith(blocks: B[]): Promise<MeshEngine> {
  const cols = new Map<string, { cx: number; cz: number; ids: Uint8Array; meta: Uint8Array }>();
  for (const [x, y, z, id, meta] of blocks) {
    const k = `${x >> 4},${z >> 4}`;
    let c = cols.get(k);
    if (!c) cols.set(k, (c = { cx: x >> 4, cz: z >> 4, ids: new Uint8Array(256 * H), meta: new Uint8Array(256 * H) }));
    c.ids[cellIndex(x & 15, y, z & 15)] = id;
    c.meta[cellIndex(x & 15, y, z & 15)] = meta ?? 0;
  }
  const entries = [...cols.values()].map((c) => {
    const e = encodeColumn(c.ids, c.meta, lightSections(H, () => 15, () => 0));
    return { cx: c.cx, cz: c.cz, hash: e.blockHash, seenMs: 1, by: 't', live: true, data: e.data, light: e.light };
  });
  const engine = new MeshEngine();
  engine.configure('b1.8.1', null, false);
  await engine.apply(encodeMcwc(0, entries));
  return engine;
}

/**
 * A 4×4 patch (x 0..3, z 0..3): stone up to y 60, dirt 61..62, grass at 63 (a heightmap of 63) except a hill column (0,0)
 * up to 66 (dirt 64, 65, grass 66) and a pit column (3,3) whose grass is at 61 (air 62..63). Tall grass on (1,1) at 64, a
 * poppy on the hill top at 67, a tree trunk on (2,2) at 64..66 with leaves at 67, and a cave pocket at (1,2) y 62.
 */
function patch(): B[] {
  const out: B[] = [];
  for (let x = 0; x < 4; x++)
    for (let z = 0; z < 4; z++) {
      const top = x === 0 && z === 0 ? 66 : x === 3 && z === 3 ? 61 : 63;
      for (let y = 50; y <= top; y++) out.push([x, y, z, y === top ? 2 : y > 60 ? 3 : 1]);
    }
  out.push([1, 64, 1, 31, 1], [0, 67, 0, 38], [2, 64, 2, 17], [2, 65, 2, 17], [2, 66, 2, 17], [2, 67, 2, 18]);
  return out.map((b) => (b[0] === 1 && b[1] === 62 && b[2] === 2 ? ([1, 62, 2, 0] as B) : b)); // the cave pocket
}

async function designate(mode: HeightMode, level: number, layers = 2, cutY: number | null = null, tool: 'mine' | 'deconstruct' = 'mine', blocks = patch()) {
  const e = await engineWith(blocks);
  const q = heightQuery(mode, level, layers, cutY);
  const raw = e.stacks(0, [0, q.min, 0], [3, q.max, 3], [...NOT_GROUND_IDS], q.depth, q.above, 4 * MAX_DRAG_CELLS);
  return heightTargets(tool, mode, raw, level);
}
const key = (c: readonly number[]) => c.join(',');
const has = (cells: ReadonlyArray<readonly number[]>, x: number, y: number, z: number) => cells.some((c) => key(c) === `${x},${y},${z}`);

describe('mine height modes', () => {
  it('Surface takes exactly the top block of each column and the plants on it: one layer, never a tree, never air', async () => {
    const t = await designate('surface', 63);
    // 16 columns: 16 tops, + the tall grass on (1,1) and the poppy on the hill.
    expect(t.cells).toHaveLength(18);
    expect(t.columns).toBe(16);
    expect(t.layers).toBe(2); // a top and the plant on it
    expect(has(t.cells, 0, 66, 0)).toBe(true); // the hill's top
    expect(has(t.cells, 0, 67, 0)).toBe(true); // the poppy on it
    expect(has(t.cells, 1, 64, 1)).toBe(true); // tall grass
    expect(has(t.cells, 3, 61, 3)).toBe(true); // the pit's top, below the level
    expect(has(t.cells, 2, 63, 2)).toBe(true); // under the tree: the ground, not the trunk
    expect(t.cells.some(([x, y, z]) => x === 2 && z === 2 && y >= 64)).toBe(false); // the tree stays (Chop takes it)
    expect(t.cells.filter(([x, , z]) => !(x === 0 && z === 0) && !(x === 1 && z === 1)).every(([, y]) => y <= 63)).toBe(true);
    expect(t.top).toBe(67);
    expect(t.bottom).toBe(61);
  });

  it('Level takes only the blocks at the working level', async () => {
    const t = await designate('level', 63);
    expect(t.cells.every(([, y]) => y === 63)).toBe(true);
    // every column has a block at 63 except the pit (air at 63)
    expect(t.cells).toHaveLength(15);
    expect(has(t.cells, 3, 63, 3)).toBe(false);
  });

  it('Down to level digs from the surface down to and including the level, never below it', async () => {
    const t = await designate('down', 62);
    expect(t.cells.every(([, y]) => y >= 62)).toBe(true);
    // the hill column: poppy 67, 66, 65, 64, 63, 62
    expect(t.cells.filter(([x, , z]) => x === 0 && z === 0).map(([, y]) => y)).toEqual([67, 66, 65, 64, 63, 62]);
    // the cave pocket at (1,62,2) is air: not designated
    expect(has(t.cells, 1, 62, 2)).toBe(false);
    expect(has(t.cells, 1, 63, 2)).toBe(true);
    // the pit (top 61) has nothing at or above 62
    expect(t.cells.some(([x, , z]) => x === 3 && z === 3)).toBe(false);
  });

  it('Flatten here takes everything above the level: plants on a level column too, the level itself never', async () => {
    const t = await designate('flatten', 63);
    expect(t.cells.every(([, y]) => y > 63)).toBe(true);
    expect(t.cells.map(key).sort()).toEqual(['0,64,0', '0,65,0', '0,66,0', '0,67,0', '1,64,1'].sort());
    expect(heightLabel('Mine', 'flatten', t)).toBe('Mine (Flatten here): y 67 → 64 · 4 layers · 5 blocks');
  });

  it('Layers: N blocks deep from each column top, the stepper clamped to 1–8', async () => {
    const t = await designate('layers', 63, 3);
    for (const [x, z] of [[1, 0], [3, 2]] as const)
      expect(t.cells.filter((c) => c[0] === x && c[2] === z).map((c) => c[1])).toEqual([63, 62, 61]);
    expect(t.cells.filter((c) => c[0] === 0 && c[2] === 0).map((c) => c[1])).toEqual([67, 66, 65, 64]);
    expect(clampLayers(0)).toBe(1);
    expect(clampLayers(12)).toBe(8);
  });

  it('the x-ray cut hides what is over it, the filter narrows, deconstruct keeps to placed blocks', async () => {
    const cut = await designate('surface', 63, 1, 65); // nothing at or over y 65 is seen: the hill top is 64
    expect(has(cut.cells, 0, 64, 0)).toBe(true);
    expect(cut.cells.some(([, y]) => y >= 65)).toBe(false);
    // Flatten here takes the whole column above the level whatever the cut: the hill comes down even x-rayed.
    const flat = await designate('flatten', 63, 1, 65);
    expect(has(flat.cells, 0, 66, 0)).toBe(true);
    const e = await engineWith(patch());
    const q = heightQuery('surface', 63, 1, null);
    const soilOnly = heightTargets('mine', 'surface', e.stacks(0, [0, q.min, 0], [3, q.max, 3], [...NOT_GROUND_IDS], q.depth, q.above), 63, 'soil');
    expect(soilOnly.cells).toHaveLength(16); // grass tops only: the plants go with the filter
    // A plank floor at 64 over (1..2, 0): Deconstruct, down to the level, takes the planks and leaves the grass.
    const d = await designate('down', 63, 1, null, 'deconstruct', [...patch(), [1, 64, 0, 5], [2, 64, 0, 5]]);
    expect(d.cells.map(key).sort()).toEqual(['1,64,0', '2,64,0']);
  });

  it('caps a drag: over 4096 cells it designates nothing and says so', () => {
    const many: number[] = [];
    for (let i = 0; i <= MAX_DRAG_CELLS; i++) many.push(i, 63, 0, 2, 0, 3);
    const t = heightTargets('mine', 'surface', many, 63);
    expect(t.tooMany).toBe(true);
    expect(heightLabel('Mine', 'surface', t)).toMatch(/too many \(max 4096\)/);
    expect(heightLabel('Mine', 'surface', heightTargets('mine', 'surface', [], 63))).toBe('Mine (Surface): nothing here');
  });

  it('sends exactly the cells as blocks (a valid request), the filter only when narrowed', () => {
    const body = heightBody('mine', [[1, 63, 2], [1, 62, 2]], 'all');
    expect(body).toEqual({ op: 'add', kind: 'mine', blocks: [[1, 63, 2], [1, 62, 2]] });
    expect(StoryDesignationRequest.safeParse(body).success).toBe(true);
    expect(heightBody('mine', [[0, 1, 0]], 'ores')).toMatchObject({ filter: 'ores' });
  });

  it('1–5 pick the mode and + / − the layers only while Mine or Deconstruct is the tool', () => {
    expect(keyAction({ code: 'Digit1', key: '1' }, false, true)).toBe('height:surface');
    expect(keyAction({ code: 'Digit4', key: '4' }, false, true)).toBe('height:flatten');
    expect(keyAction({ code: 'Numpad5', key: '5' }, false, true)).toBe('height:layers');
    expect(keyAction({ code: 'Equal', key: '=' }, false, true)).toBe('layersUp');
    expect(keyAction({ code: 'Digit1', key: '1' }, false, false)).toBeNull();
    expect(keyAction({ code: 'Equal', key: '=' }, true, false)).toBe('heightUp');
  });
});

describe('cancelling a drag', () => {
  it('Esc cancels a left drag; its release does nothing, the next drag works', () => {
    const g = new DragGate();
    g.start(0);
    expect(g.escape()).toBe(true);
    expect(g.escape()).toBe(false); // once
    expect(g.release(0)).toBe('ignore');
    g.start(0);
    expect(g.release(0)).toBe('act');
  });

  it('a right press during a left drag cancels it; nothing acts until every button is up', () => {
    const g = new DragGate();
    g.start(0);
    expect(g.chord(2)).toBe(true);
    expect(g.release(1)).toBe('wait'); // the right button came up, left still held
    expect(g.release(0)).toBe('ignore');
    expect(g.button).toBeNull();
  });

  it('a left press cancels a right drag (a formation line); a middle drag (pan) is never cancelled', () => {
    const g = new DragGate();
    g.start(2);
    expect(g.chord(0)).toBe(true);
    expect(g.release(0)).toBe('ignore');
    g.start(1);
    expect(g.escape()).toBe(false);
    expect(g.chord(2)).toBe(false);
    expect(g.release(0)).toBe('act');
    expect(new DragGate().escape()).toBe(false); // no drag: Esc does its usual things
  });

  it('a click paints no zone and an empty designate box posts nothing', () => {
    expect(releaseIsNoop('zone', false, 0)).toBe(true);
    expect(releaseIsNoop('zone', true, 0)).toBe(false);
    expect(releaseIsNoop('storage', false, 0)).toBe(true);
    expect(releaseIsNoop('designate', false, 0)).toBe(true);
    expect(releaseIsNoop('designate', false, 1)).toBe(false); // a click on a tree still chops it
    expect(releaseIsNoop('other', false, 0)).toBe(false); // a door is a click
  });
});
