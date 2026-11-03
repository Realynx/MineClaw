/**
 * The Design Studio's pure logic: canvas ops and undo across AI and user ops, the shape tools, brushes, selection edits,
 * transforms with block facing per family, the palette search and the request bodies of messages and saves.
 */
import type { DesignerMessageResponse, DesignerSession } from '@mineclaw/contracts';
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';
import { describe, expect, it, vi } from 'vitest';
import {
  Canvas,
  connected,
  extend,
  History,
  invert,
  keyOf,
  maskSelection,
  selectAll,
  selected,
  type Cell,
} from '@/features/studio/canvas';
import { dirOfMcYaw, facingOf, mirrorMeta, placeMeta, rotateMeta, turn } from '@/features/studio/facing';
import { defaultHotbar, searchPalette } from '@/features/studio/palette';
import { Studio, type StudioApi } from '@/features/studio/studio';
import {
  boxCells,
  brush,
  copy,
  erase,
  extrude,
  faceArea,
  floodFace,
  lineCells,
  mirrorSel,
  move,
  pasteCells,
  raiseLower,
  replaceIn,
  rotate,
  selectionShape,
} from '@/features/studio/tools';

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'new design',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: 'b1.8.1',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48 },
  revisions: [],
  ...over,
});

describe('canvas', () => {
  it('applies cells, returns the inverse, skips no-ops and the outside of the plot', () => {
    const c = new Canvas();
    const ch = c.apply([
      [1, 0, 1, 4, 0],
      [1, 0, 1, 5, 0],
      [99, 0, 0, 4, 0],
      [2, 0, 2, 0, 0],
    ]);
    expect(ch.forward).toEqual([[1, 0, 1, 5, 0]]);
    expect(ch.backward).toEqual([[1, 0, 1, 0, 0]]);
    expect(c.size).toBe(1);
    c.apply(ch.backward);
    expect(c.size).toBe(0);
  });

  it('keeps doors whole', () => {
    const c = new Canvas();
    c.apply([[5, 0, 5, 64, 1]]);
    expect(c.get(5, 1, 5)).toEqual([64, 9]);
    const br = c.apply([[5, 1, 5, 0, 0]]); // breaking the top takes the bottom
    expect(c.size).toBe(0);
    expect(br.backward).toHaveLength(2);
  });

  it('history undoes and redoes, and merges a stroke into one step', () => {
    const h = new History();
    h.push({ label: 'a', by: 'user', forward: [[0, 0, 0, 4, 0]], backward: [[0, 0, 0, 0, 0]] });
    h.merge({ label: 'a', by: 'user', forward: [[1, 0, 0, 4, 0]], backward: [[1, 0, 0, 0, 0]] });
    expect(h.peekUndo()!.forward).toHaveLength(2);
    expect(h.undo()!.backward).toHaveLength(2);
    expect(h.canRedo).toBe(true);
    expect(h.redo()!.label).toBe('a');
  });
});

describe('shape tools', () => {
  it('box fill, hollow, walls and outline', () => {
    expect(boxCells([0, 0, 0], [2, 2, 2], 4, 0, 'fill')).toHaveLength(27);
    const hollow = boxCells([0, 0, 0], [2, 2, 2], 4, 0, 'hollow');
    expect(hollow.filter((c) => c[3] === 4)).toHaveLength(26);
    expect(hollow.find((c) => c[0] === 1 && c[1] === 1 && c[2] === 1)![3]).toBe(0);
    const walls = boxCells([0, 0, 0], [4, 2, 4], 5, 0, 'walls');
    expect(walls.filter((c) => c[3] === 5)).toHaveLength(16 * 3);
    expect(boxCells([0, 0, 0], [2, 2, 2], 4, 0, 'outline')).toHaveLength(20);
  });

  it('line covers both ends without repeats', () => {
    const l = lineCells([0, 0, 0], [5, 2, 0], 85, 0);
    expect(l[0]).toEqual([0, 0, 0, 85, 0]);
    expect(l[l.length - 1]).toEqual([5, 2, 0, 85, 0]);
    expect(new Set(l.map((c) => keyOf(c[0], c[1], c[2]))).size).toBe(l.length);
  });
});

describe('brushes', () => {
  it('add, remove, paint, replace and smooth', () => {
    const c = new Canvas();
    const add = brush(c, [10, 5, 10], 3, 'sphere', 'add', 1, 0);
    expect(add.length).toBeGreaterThan(14);
    c.apply(add);
    expect(brush(c, [10, 5, 10], 3, 'sphere', 'add', 1, 0)).toHaveLength(0);
    expect(brush(c, [10, 5, 10], 1, 'cube', 'paint', 4, 0)).toEqual([[10, 5, 10, 4, 0]]);
    c.apply([[10, 5, 10, 45, 0]]);
    expect(brush(c, [10, 5, 10], 3, 'cube', 'replace', 4, 0, 45)).toEqual([[10, 5, 10, 4, 0]]);
    // A lone block on a flat floor is smoothed away; a pit in it is filled.
    const f = new Canvas();
    f.apply(boxCells([0, 0, 0], [8, 1, 8], 1, 0));
    f.apply([
      [4, 2, 4, 1, 0],
      [6, 1, 6, 0, 0],
    ]);
    const sm = brush(f, [4, 2, 4], 2, 'cube', 'smooth', 0, 0);
    expect(sm).toContainEqual([4, 2, 4, 0, 0]);
    const pit = brush(f, [6, 1, 6], 1, 'cube', 'smooth', 0, 0);
    expect(pit).toEqual([[6, 1, 6, 1, 0]]);
    expect(brush(f, [4, 1, 4], 2, 'cube', 'remove', 0, 0).every((x) => x[3] === 0)).toBe(true);
  });
});

describe('selection', () => {
  const house = () => {
    const c = new Canvas();
    c.apply(boxCells([10, 0, 10], [14, 0, 14], 4, 0));
    c.apply(boxCells([10, 1, 10], [14, 3, 14], 5, 0, 'walls'));
    c.apply([[30, 0, 30, 20, 0]]);
    return c;
  };

  it('box, extend, all, invert, connected', () => {
    const c = house();
    const s = { min: [10, 1, 10] as [number, number, number], max: [14, 3, 10] as [number, number, number] };
    expect(selected(c, s)).toHaveLength(15); // the north wall
    const e = extend(s, [10, 0, 10], [10, 0, 14]);
    expect(e.min).toEqual([10, 0, 10]);
    expect(e.max).toEqual([14, 3, 14]);
    expect(selectAll(c)!.max).toEqual([30, 3, 30]);
    const inv = invert(c, { min: [0, 0, 0], max: [20, 10, 20] })!;
    expect(inv.mask!.size).toBe(1); // only the glass outside
    expect(connected(c, [10, 0, 10], true).size).toBe(25); // the cobblestone floor
    expect(connected(c, [10, 0, 10], false).size).toBe(25 + 48); // floor + walls, not the far glass
    expect(maskSelection(connected(c, [10, 1, 10], true)).max).toEqual([14, 3, 14]);
  });

  it('fill, hollow, walls, raise, lower, replace, delete', () => {
    const c = house();
    const s = { min: [10, 0, 10] as [number, number, number], max: [14, 3, 14] as [number, number, number] };
    expect(selectionShape(s, 45, 0, 'fill')).toHaveLength(100);
    expect(selectionShape(s, 0, 0, 'hollow')).toHaveLength(3 * 2 * 3);
    expect(selectionShape(s, 45, 0, 'walls')).toHaveLength(16 * 4);
    const up = raiseLower(c, s, true);
    expect(up.filter((x) => x[1] === 4 && x[3] === 5)).toHaveLength(16); // the walls grow
    expect(up.filter((x) => x[1] === 1 && x[3] === 4)).toHaveLength(9); // the open floor inside too
    expect(up).toHaveLength(25);
    expect(raiseLower(c, s, false).every((x) => x[1] === 3 || x[1] === 0)).toBe(true);
    expect(replaceIn(c, s, 4, 98, 0)).toHaveLength(25);
    expect(replaceIn(c, s, 'any', 98, 0)).toHaveLength(73);
    c.apply(erase(c, s));
    expect(c.size).toBe(1);
  });
});

describe('facing per block family', () => {
  it('placement turns the block toward the player as the game does', () => {
    expect(placeMeta(53, 0, 'north', 1, 'b1.8.1')).toBe(3); // stairs go up the way you look
    expect(placeMeta(64, 0, 'east', 1, 'b1.8.1')).toBe(0); // door: the placer's facing
    expect(placeMeta(54, 0, 'north', 1, 'b1.8.1')).toBe(3); // chest front faces you (south)
    expect(placeMeta(61, 0, 'west', 1, 'b1.8.1')).toBe(5);
    expect(placeMeta(65, 2, 'north', 3, 'b1.8.1')).toBe(3); // ladder on a south face: the wall is north
    expect(placeMeta(50, 5, 'north', 1, 'b1.8.1')).toBe(5); // a torch on the floor
    expect(placeMeta(50, 5, 'north', 5, 'b1.8.1')).toBe(1); // on an east face: the wall is west
    expect(placeMeta(86, 0, 'north', 1, 'b1.8.1')).toBe(0); // pumpkin face south (toward you)
    expect(placeMeta(107, 0, 'east', 1, 'b1.8.1')).toBe(3);
    expect(placeMeta(17, 1, 'north', 5, '1.12.2')).toBe(1 | 4); // a spruce log along x
    expect(placeMeta(17, 1, 'north', 5, 'b1.8.1')).toBe(1); // b1.8.1 logs have no axis
    expect(dirOfMcYaw(0)).toBe('south');
    expect(dirOfMcYaw(270)).toBe('east');
  });

  it.each([
    ['stairs', 53, 0, 1, 2], // east → south
    ['stairs upside down', 53, 4 | 3, 1, 4 | 0], // north → east, keeps bit 4
    ['door lower', 64, 0, 1, 1], // east → south
    ['door upper', 64, 8 | 3, 1, 8 | 0],
    ['door hinge', 64, 4 | 1, 2, 4 | 3],
    ['chest', 54, 2, 1, 5], // north → east
    ['furnace', 61, 4, 3, 3], // west → south (counter-clockwise)
    ['ladder', 65, 3, 1, 4],
    ['torch', 50, 3, 1, 2], // north wall → east wall
    ['floor torch', 50, 5, 1, 5],
    ['trapdoor', 96, 0, 1, 3],
    ['fence gate', 107, 4 | 0, 1, 4 | 1], // keeps open
    ['pumpkin', 86, 0, 1, 1],
    ['plain block', 4, 0, 1, 0],
  ])('rotates %s', (_name, id, meta, q, want) => {
    expect(rotateMeta(id, meta, q, 'b1.8.1')).toBe(want);
    expect(rotateMeta(id, rotateMeta(id, meta, q, 'b1.8.1'), 4 - q, 'b1.8.1')).toBe(meta); // and back
  });

  it('turns 1.12.2 log axes and mirrors per axis', () => {
    expect(rotateMeta(17, 4, 1, '1.12.2')).toBe(8);
    expect(rotateMeta(17, 8 | 2, 3, '1.12.2')).toBe(4 | 2);
    expect(rotateMeta(17, 0, 1, '1.12.2')).toBe(0);
    expect(mirrorMeta(53, 0, 'x')).toBe(1); // east ↔ west
    expect(mirrorMeta(53, 2, 'x')).toBe(2); // a south stair stays
    expect(mirrorMeta(53, 2, 'z')).toBe(3);
    expect(mirrorMeta(54, 4, 'x')).toBe(5);
    expect(mirrorMeta(50, 3, 'z')).toBe(4);
    expect(facingOf(53, 0)).toBe('east');
    expect(turn('north', -1)).toBe('west');
  });
});

describe('transforms', () => {
  it('moves, rotates a wall with its stairs, mirrors, copies and pastes', () => {
    const c = new Canvas();
    c.apply([
      [10, 0, 10, 4, 0],
      [11, 0, 10, 4, 0],
      [12, 0, 10, 53, 0],
    ]); // a row along x, the stairs going up east
    const s = { min: [10, 0, 10] as [number, number, number], max: [12, 0, 10] as [number, number, number] };
    const m = move(c, s, [0, 0, 2]);
    c.apply(m.cells);
    expect(c.id(10, 0, 12)).toBe(4);
    expect(c.id(10, 0, 10)).toBe(0);
    expect(m.selection!.min).toEqual([10, 0, 12]);
    const r = rotate(c, m.selection!, 1, 'b1.8.1');
    c.apply(r.cells);
    // The row now runs along z through its middle block, the stairs turned to go up south.
    expect(r.selection!.min).toEqual([11, 0, 11]);
    expect(r.selection!.max).toEqual([11, 0, 13]);
    expect(c.get(11, 0, 13)).toEqual([53, 2]);
    const f = mirrorSel(c, r.selection!, 'z');
    c.apply(f.cells);
    expect(c.get(11, 0, 11)).toEqual([53, 3]);
    const clip = copy(c, f.selection!)!;
    expect(clip.size).toEqual([1, 1, 3]);
    c.apply(pasteCells(clip, [20, 0, 20]));
    expect(c.get(20, 0, 20)).toEqual([53, 3]);
    expect(c.size).toBe(6);
  });

  it('rotating the whole design 4 times is the identity', () => {
    const c = new Canvas();
    c.apply(boxCells([5, 0, 5], [9, 0, 7], 4, 0));
    c.apply([
      [5, 1, 5, 64, 0],
      [9, 1, 7, 54, 2],
    ]);
    const before = JSON.stringify(c.cells().sort());
    let s = selectAll(c)!;
    for (let i = 0; i < 4; i++) {
      const t = rotate(c, s, 1, 'b1.8.1');
      c.apply(t.cells, false);
      s = t.selection!;
    }
    expect(JSON.stringify(c.cells().sort())).toBe(before);
  });

  it('face area flood and extrude', () => {
    const c = new Canvas();
    c.apply(boxCells([0, 0, 0], [3, 2, 0], 5, 0)); // a 4×3 wall facing north and south
    expect(faceArea(c, [1, 1, 0], 3)).toHaveLength(12);
    expect(floodFace(c, [1, 1, 0], 3, 45, 0).every((x) => x[3] === 45)).toBe(true);
    const push = extrude(c, [1, 1, 0], 3, 2);
    expect(push).toHaveLength(24);
    expect(push.every((x) => x[2] === 1 || x[2] === 2)).toBe(true);
    expect(extrude(c, [1, 1, 0], 3, -1).every((x) => x[3] === 0 && x[2] === 0)).toBe(true);
  });
});

describe('palette', () => {
  it('searches by words and ids, and fills the first hotbar', () => {
    expect(searchPalette(MOCK_DESIGNER_PALETTE, 'oak')[0]!.name).toBe('oak log');
    expect(searchPalette(MOCK_DESIGNER_PALETTE, 'st br').map((e) => e.name)).toContain('stone bricks');
    expect(searchPalette(MOCK_DESIGNER_PALETTE, 'red wool').map((e) => e.name)).toEqual(['red wool']);
    expect(searchPalette(MOCK_DESIGNER_PALETTE, '64')[0]!.name).toBe('wooden door');
    expect(searchPalette(MOCK_DESIGNER_PALETTE, 'zzz')).toEqual([]);
    const bar = defaultHotbar(MOCK_DESIGNER_PALETTE);
    expect(bar).toHaveLength(9);
    expect(bar[0]!.name).toBe('cobblestone');
  });
});

describe('studio controller', () => {
  const fakeApi = () => {
    const calls: Array<[string, unknown]> = [];
    const api: StudioApi = {
      canvas: vi.fn((id: number, body: unknown) => {
        calls.push(['canvas', body]);
        return Promise.resolve({ rev: 5, applied: 1 });
      }),
      message: vi.fn((id: number, body: unknown) => {
        calls.push(['message', body]);
        const ops = [
          {
            kind: 'place',
            label: 'placed an oak door',
            by: 'ai' as const,
            cells: [
              [3, 0, 3, 64, 1],
              [3, 1, 3, 64, 9],
            ] as Cell[],
          },
        ];
        const r: DesignerMessageResponse = {
          reply: 'Added a door.',
          ops,
          rev: 9,
          costUsd: 0.02,
          sessionCostUsd: 0.02,
          model: 'claude-opus-5-5',
          calls: 2,
          usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0 },
          message: {
            seq: 2,
            role: 'assistant',
            text: 'Added a door.',
            at: 1,
            model: 'claude-opus-5-5',
            costUsd: 0.02,
            ops: [{ kind: 'place', label: 'placed an oak door', cells: 2 }],
          },
          userMessage: { seq: 1, role: 'user', text: 'add a door', at: 1 },
        };
        return Promise.resolve(r);
      }),
      save: vi.fn((id: number, body: unknown) => {
        calls.push(['save', body]);
        return Promise.resolve({
          designId: 101,
          rev: 1,
          name: 'hut',
          blocks: 3,
          created: true,
          text: 'saved as design #101 r1',
        });
      }),
    };
    return { api, calls };
  };

  it('sends waiting edits with the message, the selection when asked, applies the AI ops, undoes across both', async () => {
    const { api, calls } = fakeApi();
    const s = new Studio(session(), api, 60_000);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'placed cobblestone');
    s.setSelection({ min: [0, 0, 0], max: [4, 2, 4] });
    const seen: string[] = [];
    s.onCells((_c, by) => seen.push(by));
    await s.send('add a door', { withSelection: true, model: 'worker' });
    const body = calls.find((c) => c[0] === 'message')![1] as {
      text: string;
      canvas: { rev: number; edits: Array<{ cells: Cell[] }> };
      selection: unknown;
      model: string;
    };
    expect(body.text).toBe('add a door');
    expect(body.canvas.edits[0]!.cells).toEqual([[1, 0, 1, 4, 0]]);
    expect(body.selection).toEqual({ min: [0, 0, 0], max: [4, 2, 4] });
    expect(body.model).toBe('worker');
    expect(s.canvas.get(3, 1, 3)).toEqual([64, 9]);
    expect(s.snapshot().messages.map((m) => m.role)).toEqual(['user', 'assistant']);
    expect(s.snapshot().costUsd).toBe(0.02);
    expect(seen).toContain('ai');
    // Undo the AI's door (a user edit the bot will hear about), then the user's cobblestone; redo brings the door back.
    expect(s.undo()!.by).toBe('ai');
    expect(s.canvas.id(3, 0, 3)).toBe(0);
    expect(s.undo()!.by).toBe('user');
    expect(s.canvas.size).toBe(0);
    s.redo();
    expect(s.canvas.id(1, 0, 1)).toBe(4);
    await s.flush();
    const canvasBody = calls.filter((c) => c[0] === 'canvas').at(-1)![1] as {
      ops: Array<{ kind: string; label: string }>;
    };
    expect(canvasBody.ops.map((o) => o.kind)).toEqual(['undo', 'undo', 'redo']);
    expect(canvasBody.ops[0]!.label).toBe('undo placed an oak door');
    s.dispose();
  });

  it('saves with the name, the summary and as-new', async () => {
    const { api, calls } = fakeApi();
    const s = new Studio(session({ blocks: [[1, 0, 1, 4, 0]] }), api);
    const r = await s.save('  hut ', 'first draft');
    expect(r.designId).toBe(101);
    expect(calls.find((c) => c[0] === 'save')![1]).toEqual({ name: 'hut', summary: 'first draft' });
    await s.save('hut 2', '', true);
    expect(calls.filter((c) => c[0] === 'save')[1]![1]).toEqual({ name: 'hut 2', asNew: true });
    expect(s.snapshot().designId).toBe(101);
    s.dispose();
  });
});
