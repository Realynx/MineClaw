/**
 * Library components in a redstone design: an instance owns its turned and mirrored cells, and placing, moving, turning,
 * mirroring and removing it is one undo step each, which brings both the cells and the instances back; boxes keep a free
 * cell between them; snapping joins an input port to an output port with one dust; a hand edit detaches an instance
 * (undoing that edit brings it back); the instances and the user's definitions ride in the design's saved metadata, and
 * this browser's copy of unsaved work comes back only when every cell it owns still matches the canvas. Then the
 * geometry helpers and the quick library's storage, export and import.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerCell, type DesignerSession } from '@mineclaw/contracts';
import { createSimulation, LIBRARY, type Pos } from '@mineclaw/redstone';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { setPlotMode, type Cell } from '@/features/studio/canvas';
import { LibraryTab } from '@/features/studio/redstone/LibraryTab';
import { useRedstoneComponents } from '@/features/studio/redstone/useRedstoneComponents';
import { authoringStoreOf } from '@/features/studio/redstone/authoring';
import {
  exportLibrary,
  importLibrary,
  loadQuickLibrary,
  quickLibraryKey,
  saveQuickLibrary,
  type CustomDef,
  type StorageLike,
} from '@/features/studio/redstone/componentData';
import { layoutOf, placeLayout, portSideOf, snapPlacement, tooClose, type Box } from '@/features/studio/redstone/ports';
import { KEEP_COPIES, RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import { Studio, type StudioApi } from '@/features/studio/studio';

const NOT = LIBRARY.find((d) => d.id === 'gate.not')!;
const STONE = 1;
const DUST = 55;

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 9,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'gates',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
  ...over,
});

const api: StudioApi = {
  message: () => Promise.reject(new Error('n/a')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('n/a')),
};

/** A Storage that lives in the test. */
function memoryStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return { map, getItem: (k) => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v) };
}

/** A Storage that also lists and removes, as the browser's does. */
function fullStorage(): StorageLike & { map: Map<string, string> } {
  const map = new Map<string, string>();
  return {
    map,
    getItem: (k) => map.get(k) ?? null,
    setItem: (k, v) => void map.set(k, v),
    removeItem: (k) => void map.delete(k),
    key: (i) => [...map.keys()][i] ?? null,
    get length() {
      return map.size;
    },
  };
}

const studios: Studio[] = [];
const controllers: RedstoneComponentController[] = [];
afterEach(() => {
  for (const c of controllers.splice(0)) c.dispose();
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
});

function mount(over: Partial<DesignerSession> = {}, storage: StorageLike | null = memoryStorage()) {
  const studio = new Studio(session(over), api, 60_000);
  studios.push(studio);
  const c = new RedstoneComponentController(studio, storage);
  controllers.push(c);
  return { studio, c, storage };
}

/** Every block of the canvas, sorted. */
const blocks = (s: Studio): Cell[] =>
  s.canvas.cells().sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0] || a[3] - b[3]);

/** The blocks a placed layout puts down (its air left out), sorted like `blocks`. */
const layoutBlocks = (at: Pos, turns = 0, mirror: 'x' | 'z' | null = null): Cell[] =>
  placeLayout(NOT.layouts['1.12.2']!, { at, turns, mirror }, '1.12.2')
    .cells.filter((c) => c[3] > 0)
    .sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);

const ids = (c: RedstoneComponentController) => c.snapshot().components.map((x) => x.id);

describe('placing a library component', () => {
  it('puts its turned cells on the canvas, lists it and saves it in the design’s metadata', () => {
    const { studio, c } = mount();
    expect(studio.snapshot().dirty).toBe(false);
    const inst = c.place('gate.not', [10, 0, 10], { turns: 1, label: 'inverter' })!;
    expect(inst).toMatchObject({
      id: 'c1',
      def: 'gate.not',
      at: [10, 0, 10],
      turns: 1,
      mirror: null,
      label: 'inverter',
    });
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10], 1));
    expect(ids(c)).toEqual(['c1']);
    expect(authoringStoreOf(studio).current.components).toEqual([
      { id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 1, mirror: null, label: 'inverter' },
    ]);
    expect(studio.snapshot().dirty).toBe(true);
    expect(studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('clears what stood in its box and gives it back when the component leaves', () => {
    const { studio, c } = mount({ blocks: [[11, 0, 10, 4, 0]] });
    c.place('gate.not', [10, 0, 10]);
    expect(studio.canvas.get(11, 0, 10)).not.toEqual([4, 0]);
    expect(c.move('c1', [20, 0, 20])).toBe(true);
    expect(studio.canvas.get(11, 0, 10)).toEqual([4, 0]);
    expect(blocks(studio)).toEqual([[11, 0, 10, 4, 0], ...layoutBlocks([20, 0, 20])]);
    expect(c.remove('c1')).toBe(true);
    expect(blocks(studio)).toEqual([[11, 0, 10, 4, 0]]);
    expect(ids(c)).toEqual([]);
  });

  it('turns and mirrors in place; mirroring twice across one axis is no mirror', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.turn('c1');
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10], 1));
    // Seen across x after an odd turn is across z in the layout's own frame.
    c.mirror('c1', 'x');
    expect(c.snapshot().components[0]).toMatchObject({ turns: 1, mirror: 'z' });
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10], 1, 'z'));
    c.mirror('c1', 'x');
    expect(c.snapshot().components[0]).toMatchObject({ turns: 1, mirror: null });
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10], 1));
  });

  it('a move to where it already is, or the label it already has, is no undo step', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    expect(c.move('c1', [10, 0, 10])).toBe(true);
    expect(c.rename('c1', '  ')).toBe(true);
    expect(studio.snapshot().undoLabel).toBe('place NOT');
    studio.undo();
    expect(studio.snapshot().canUndo).toBe(false);
  });

  it('refuses a component it does not have, and one off the plot', () => {
    const { c } = mount({ version: 'b1.8.1' });
    expect(c.place('memory.lock-cell', [10, 0, 10])).toBeNull();
    expect(c.snapshot().error).toMatch(/no component "memory.lock-cell" for b1.8.1/);
    expect(c.place('gate.not', [62, 0, 10])).toBeNull();
    expect(c.snapshot().error).toMatch(/does not fit on the plot/);
    expect(c.place('gate.not', [10, 0, 10])).not.toBeNull();
    expect(c.snapshot().error).toBeNull();
  });
});

describe('undo and redo', () => {
  it('bring back both the cells and the instances of every component step', () => {
    const { studio, c } = mount();
    const states: Array<{ cells: Cell[]; comps: unknown }> = [];
    const keep = () => states.push({ cells: blocks(studio), comps: c.snapshot().components });
    keep();
    c.place('gate.not', [10, 0, 10]);
    keep();
    c.move('c1', [14, 0, 10]);
    keep();
    c.turn('c1');
    keep();
    c.rename('c1', 'not');
    keep();
    c.remove('c1');
    keep();
    for (let i = states.length - 2; i >= 0; i--) {
      studio.undo();
      expect({ cells: blocks(studio), comps: c.snapshot().components }).toEqual(states[i]);
    }
    for (let i = 1; i < states.length; i++) {
      studio.redo();
      expect({ cells: blocks(studio), comps: c.snapshot().components }).toEqual(states[i]);
    }
  });
});

describe('the gap between components', () => {
  it('refuses a box that touches another, even at a corner, and takes one a free cell away', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]); // 3 x 1 x 3: x 10..12, z 10..12
    expect(c.place('gate.not', [13, 0, 10])).toBeNull();
    expect(c.snapshot().error).toMatch(/would touch NOT/);
    expect(c.place('gate.not', [13, 0, 13])).toBeNull();
    expect(c.place('gate.not', [14, 0, 10])).not.toBeNull();
    // A move is checked the same way.
    expect(c.move('c2', [11, 0, 13])).toBe(false);
    expect(ids(c)).toEqual(['c1', 'c2']);
  });

  it('snaps an input port onto a nearby output port, with one dust in the gap, and the chain works', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    const out = c.placedOf('c1')!.ports.find((p) => p.kind === 'out')!;
    const second = c.place('gate.not', [out.at[0] + 1, 0, out.at[2] + 1], { snap: true })!;
    const input = c.placedOf(second.id)!.ports.find((p) => p.kind === 'in')!;
    expect(input.at).toEqual(out.at);
    expect(studio.canvas.get(...(out.at as [number, number, number]))).toEqual([DUST, 0]);
    expect(tooClose(c.placedOf('c1')!.box, c.placedOf(second.id)!.box)).toBe(false);

    // Two inverters in a row: the second's output follows the first's input.
    const a = c.placedOf('c1')!.ports.find((p) => p.kind === 'in')!;
    const y = c.placedOf(second.id)!.ports.find((p) => p.kind === 'out')!;
    const feed: DesignerCell[] = [
      [a.at[0], 0, a.at[2], DUST, 0],
      [a.at[0] - 1, 0, a.at[2], STONE, 0],
      [a.at[0] - 1, 1, a.at[2], 69, 5],
      [y.at[0], 0, y.at[2], DUST, 0],
    ];
    studio.edit(feed, 'test', 'feeder');
    const sim = createSimulation({ version: '1.12.2', cells: studio.canvas.cells(), ground: { topY: -1 } });
    expect(sim.powerAt(y.at)).toBe(0);
    sim.input({ kind: 'toggle', at: [a.at[0] - 1, 1, a.at[2]] });
    sim.run(40, 'idle');
    expect(sim.powerAt(y.at)).toBeGreaterThan(0);
  });

  it('refuses a snap with no output port in reach', () => {
    const { c } = mount();
    expect(c.place('gate.not', [30, 0, 30], { snap: true })).toBeNull();
    expect(c.snapshot().error).toMatch(/No output port near there/);
  });
});

describe('a hand edit of an owned cell', () => {
  it('detaches that instance, its cells staying as they are, and undoing the edit brings it back', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [20, 0, 10]);
    const before = blocks(studio);
    studio.edit([[11, 0, 11, 0, 0]], 'remove', 'remove block'); // the NOT's block
    expect(ids(c)).toEqual(['c2']);
    expect(blocks(studio)).toEqual(before.filter((b) => !(b[0] === 11 && b[2] === 11)));
    expect(authoringStoreOf(studio).current.components).toEqual([
      { id: 'c2', def: 'gate.not', at: [20, 0, 10], turns: 0, mirror: null },
    ]);
    studio.undo();
    expect(ids(c)).toEqual(['c1', 'c2']);
    expect(blocks(studio)).toEqual(before);
    studio.redo();
    expect(ids(c)).toEqual(['c2']);
    // An edit of the free cells around it is no edit of the component.
    studio.edit([[9, 0, 11, DUST, 0]], 'place', 'place dust');
    expect(ids(c)).toEqual(['c2']);
    studio.edit([[19, 0, 11, DUST, 0]], 'place', 'place dust');
    expect(ids(c)).toEqual(['c2']);
  });

  it('also detaches for the AI’s and the bot’s changes, which have no undo step of the component', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    studio.correct([[12, 0, 11, 76, 2]]); // the bot turns the torch
    expect(ids(c)).toEqual([]);
  });

  it('unlinking keeps the cells as plain ones, as one undo step', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    const before = blocks(studio);
    c.detach('c1');
    expect(ids(c)).toEqual([]);
    expect(blocks(studio)).toEqual(before);
    studio.undo();
    expect(ids(c)).toEqual(['c1']);
  });
});

describe('the design’s own components', () => {
  it('saves a selection with picked ports and places it like a library gate', () => {
    const { studio, c } = mount();
    // A torch inverter drawn by hand: a repeater, a block and a torch hanging on it.
    studio.edit(
      [
        [30, 0, 30, 93, 1],
        [31, 0, 30, STONE, 0],
        [32, 0, 30, 76, 1],
      ],
      'place',
      'draw',
    );
    const sel = { min: [30, 0, 29] as [number, number, number], max: [32, 0, 31] as [number, number, number] };
    expect(c.saveSelection('my not', sel, [{ name: 'A', kind: 'in', at: [30, 0, 30] }])).toBeNull();
    expect(c.snapshot().error).toMatch(/A port must be a cell just outside/);
    const def = c.saveSelection('my not', sel, [
      { name: 'A', kind: 'in', at: [29, 0, 30] },
      { name: 'Y', kind: 'out', at: [33, 0, 30] },
    ])!;
    expect(def.layout).toEqual({
      size: [3, 1, 3],
      cells: [
        [0, 0, 1, 93, 1],
        [1, 0, 1, STONE, 0],
        [2, 0, 1, 76, 1],
      ],
      ports: [
        { name: 'A', kind: 'in', at: [-1, 0, 1], side: 'west' },
        { name: 'Y', kind: 'out', at: [3, 0, 1], side: 'east' },
      ],
    });
    expect(def.version).toBe('1.12.2');
    expect(c.snapshot().customDefs.map((d) => d.id)).toEqual([def.id]);
    expect(c.place(def.id, [40, 0, 40])).not.toBeNull();
    expect(studio.canvas.get(42, 0, 41)).toEqual([76, 1]);
    // A definition in use cannot be dropped.
    expect(c.removeDef(def.id)).toBe(false);
  });

  it('takes another saved design’s cells as a portless component, flagged when drawn for the other version', () => {
    const { c } = mount();
    const def = c.insertDesign(
      'clock',
      [
        [5, 0, 7, 93, 0],
        [6, 1, 9, 55, 0],
        [7, 0, 7, 0, 0],
      ],
      'b1.8.1',
    )!;
    expect(def.layout).toEqual({
      size: [2, 2, 3],
      cells: [
        [0, 0, 0, 93, 0],
        [1, 1, 2, 55, 0],
      ],
      ports: [],
    });
    expect(c.defOf(def.id)?.otherVersion).toBe('b1.8.1');
  });
});

describe('the saved metadata and this browser’s copy', () => {
  it('opens the saved instances whose cells still match, and drops the one edited since', () => {
    const cells = [...layoutBlocks([10, 0, 10]), ...layoutBlocks([20, 0, 10]).filter((b) => b[3] !== 76)];
    const redstoneAuthoring = JSON.stringify({
      redstone: 1,
      components: [
        { id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 0, mirror: null },
        { id: 'c2', def: 'gate.not', at: [20, 0, 10], turns: 0, mirror: null },
      ],
      customDefs: [],
    });
    const { studio, c } = mount({ blocks: cells, designId: 5, rev: 1, redstoneAuthoring });
    expect(ids(c)).toEqual(['c1']);
    expect(c.snapshot().recovered).toBe(false);
    // What is open now differs from the saved revision: that is unsaved work.
    expect(studio.snapshot().dirty).toBe(true);
  });

  it('opening a design whose instances all match is no change', () => {
    const redstoneAuthoring = JSON.stringify({
      redstone: 1,
      components: [{ id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 0, mirror: null }],
      customDefs: [],
      probes: [],
    });
    const { studio, c } = mount({ blocks: layoutBlocks([10, 0, 10]), designId: 5, rev: 1, redstoneAuthoring });
    expect(ids(c)).toEqual(['c1']);
    expect(studio.snapshot().dirty).toBe(false);
  });

  it('brings the unsaved copy back only when every cell it owns matches the canvas', () => {
    const storage = memoryStorage();
    const first = mount({}, storage);
    first.c.place('gate.not', [10, 0, 10], { label: 'kept' });
    const canvas = first.studio.canvas.cells();

    // The page is opened again on the same session: the canvas came back from the bot, the instance from the browser.
    const again = mount({ blocks: canvas }, storage);
    expect(again.c.snapshot().components).toEqual(first.c.snapshot().components);
    expect(again.c.snapshot().recovered).toBe(true);
    expect(again.studio.snapshot().dirty).toBe(true);

    // One owned cell is not what the copy says: nothing comes back.
    const changed = canvas.map((b): DesignerCell => (b[3] === 76 ? [b[0], b[1], b[2], 76, 5] : b));
    const stale = mount({ blocks: changed }, storage);
    expect(ids(stale.c)).toEqual([]);
    expect(stale.c.snapshot().recovered).toBe(false);

    // Another session never reads this one's copy.
    const other = mount({ id: 10, blocks: canvas }, storage);
    expect(ids(other.c)).toEqual([]);
  });

  it('drops this session’s copy once the design is saved, and writes it again for the next unsaved change', async () => {
    const storage = fullStorage();
    const saving: StudioApi = {
      ...api,
      save: () => Promise.resolve({ designId: 5, rev: 1, name: 'gates', blocks: 1, created: true, text: 'saved' }),
    };
    const studio = new Studio(session(), saving, 60_000);
    studios.push(studio);
    const c = new RedstoneComponentController(studio, storage, () => 1000);
    controllers.push(c);
    const key = 'mineclaw.redstone.components.v1:poofimafox:9:1.12.2';
    expect(storage.map.has(key)).toBe(false); // opened clean: nothing to keep
    c.place('gate.not', [10, 0, 10]);
    expect(JSON.parse(storage.map.get(key)!)).toMatchObject({ at: 1000, components: [{ id: 'c1' }] });
    await studio.save('gates');
    expect(studio.dirty).toBe(false);
    expect(storage.map.has(key)).toBe(false);
    c.turn('c1');
    expect(storage.map.has(key)).toBe(true);
    studio.undo();
    expect(studio.dirty).toBe(false);
    expect(storage.map.has(key)).toBe(false);
  });

  it('keeps an account’s newest copies of other sessions and drops the older ones when a design opens', () => {
    const storage = fullStorage();
    const copy = (at: number) => JSON.stringify({ at, components: [], customDefs: [] });
    for (let i = 1; i <= 12; i++)
      storage.map.set(`mineclaw.redstone.components.v1:poofimafox:${100 + i}:1.12.2`, copy(i));
    storage.map.set('mineclaw.redstone.components.v1:poofimafox:99:b1.8.1', 'not json');
    storage.map.set('mineclaw.redstone.components.v1:kiwi:5:1.12.2', copy(0));
    storage.map.set('mineclaw.redstone.library.v1:poofimafox', '[]');
    mount({}, storage);
    const left = [...storage.map.keys()].sort();
    expect(left).toEqual(
      [
        ...Array.from(
          { length: KEEP_COPIES },
          (_, i) => `mineclaw.redstone.components.v1:poofimafox:${100 + 12 - i}:1.12.2`,
        ),
        'mineclaw.redstone.components.v1:kiwi:5:1.12.2',
        'mineclaw.redstone.library.v1:poofimafox',
      ].sort(),
    );
  });

  it('keeps the debugger’s keys when it writes, and the debugger keeps its keys', () => {
    const { studio, c } = mount();
    const store = authoringStoreOf(studio);
    store.setDebugger({ ...store.debugger, probes: [{ id: 'p1', at: [1, 0, 1], label: 'P1', color: 1 }] });
    c.place('gate.not', [10, 0, 10]);
    expect(store.current.probes).toEqual([{ id: 'p1', at: [1, 0, 1], label: 'P1', color: 1 }]);
    store.setDebugger({ ...store.debugger, probes: [] });
    expect(store.current.components).toHaveLength(1);
  });
});

describe('ports and boxes', () => {
  const box: Box = { min: [10, 0, 10], max: [12, 1, 12] };

  it('a port is a cell just outside one of the four sides, at a height inside the box', () => {
    expect(portSideOf(box, [9, 0, 11])).toBe('west');
    expect(portSideOf(box, [13, 1, 10])).toBe('east');
    expect(portSideOf(box, [11, 0, 9])).toBe('north');
    expect(portSideOf(box, [12, 0, 13])).toBe('south');
    expect(portSideOf(box, [9, 0, 9])).toBeNull(); // a corner
    expect(portSideOf(box, [9, 2, 11])).toBeNull(); // above it
    expect(portSideOf(box, [11, 0, 11])).toBeNull(); // inside
  });

  it('boxes one free cell apart do not touch; closer ones do, diagonally too', () => {
    expect(tooClose(box, { min: [14, 0, 10], max: [15, 0, 10] })).toBe(false);
    expect(tooClose(box, { min: [13, 0, 10], max: [15, 0, 10] })).toBe(true);
    expect(tooClose(box, { min: [13, 2, 13], max: [14, 2, 14] })).toBe(true);
    expect(tooClose(box, { min: [10, 3, 10], max: [10, 3, 10] })).toBe(false);
  });

  it('places a layout with every box cell, air included, and its ports moved along', () => {
    const p = placeLayout(NOT.layouts['1.12.2']!, { at: [5, 0, 5], turns: 0, mirror: null }, '1.12.2');
    expect(p.box).toEqual({ min: [5, 0, 5], max: [7, 0, 7] });
    expect(p.cells).toHaveLength(9);
    expect(p.ports.map((x) => [x.name, x.at, x.side])).toEqual([
      ['A', [4, 0, 6], 'west'],
      ['Y', [8, 0, 6], 'east'],
    ]);
  });

  it('a snap needs an input facing the output across the gap', () => {
    const layout = NOT.layouts['1.12.2']!;
    // An output on the east face takes an input on the west face: the NOT as drawn.
    expect(snapPlacement(layout, 0, null, '1.12.2', [9, 0, 6], [{ at: [8, 0, 6], side: 'east' }])).toEqual({
      at: [9, 0, 5],
      joint: [8, 0, 6],
    });
    // An output on the south face needs the NOT turned so its input faces north.
    expect(snapPlacement(layout, 0, null, '1.12.2', [8, 0, 7], [{ at: [8, 0, 6], side: 'south' }])).toBeNull();
    expect(snapPlacement(layout, 1, null, '1.12.2', [8, 0, 7], [{ at: [8, 0, 6], side: 'south' }])?.joint).toEqual([
      8, 0, 6,
    ]);
  });

  it('a layout from cells keeps only blocks inside the box, relative to its corner', () => {
    expect(
      layoutOf(
        { min: [2, 0, 2], max: [3, 0, 2] },
        [
          [2, 0, 2, STONE, 0],
          [9, 0, 9, STONE, 0],
          [3, 0, 2, 0, 0],
        ],
        [],
      ),
    ).toEqual({ size: [2, 1, 1], cells: [[0, 0, 0, STONE, 0]], ports: [] });
  });
});

describe('the Library tab', () => {
  function Tab({ studio }: { studio: Studio }) {
    const components = useRedstoneComponents({
      enabled: true,
      studio,
      viewer: { current: null },
      stage: { current: null },
      live: false,
    });
    return createElement(LibraryTab, { components, studio, version: '1.12.2' });
  }

  it('lists the version’s proven gates, places one at typed coordinates and removes it again', () => {
    localStorage.clear();
    const studio = new Studio(session(), api, 60_000);
    studios.push(studio);
    const qc = new QueryClient();
    const view = render(createElement(QueryClientProvider, { client: qc }, createElement(Tab, { studio })));
    expect(screen.getByRole('button', { name: 'Repeater-lock memory cell' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Place …/ })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'NOT' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Place at x y z' }), { target: { value: '10 0 10' } });
    fireEvent.click(screen.getByRole('button', { name: 'Place at' }));
    expect(screen.getAllByTestId('placed-component')).toHaveLength(1);
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10]));
    fireEvent.click(screen.getByRole('button', { name: 'Turn c1' }));
    expect(blocks(studio)).toEqual(layoutBlocks([10, 0, 10], 1));
    fireEvent.click(screen.getByRole('button', { name: 'Remove c1' }));
    expect(screen.queryAllByTestId('placed-component')).toHaveLength(0);
    expect(blocks(studio)).toEqual([]);
    view.unmount();
  });
});

describe('the quick library in this browser', () => {
  const def: CustomDef = {
    id: 'u.abc',
    name: 'my latch',
    layout: {
      size: [1, 1, 1],
      cells: [[0, 0, 0, STONE, 0]],
      ports: [{ name: 'A', kind: 'in', at: [-1, 0, 0], side: 'west' }],
    },
    version: 'b1.8.1',
  };

  it('keeps the definitions per account and reads back only valid ones', () => {
    const storage = memoryStorage();
    expect(saveQuickLibrary(storage, 'fox', [def])).toBe(true);
    expect(loadQuickLibrary(storage, 'fox')).toEqual([def]);
    expect(loadQuickLibrary(storage, 'robin')).toEqual([]);
    storage.map.set(
      quickLibraryKey('fox'),
      JSON.stringify([def, { id: 'u.bad', name: 'x', layout: { size: [0, 1, 1] } }]),
    );
    expect(loadQuickLibrary(storage, 'fox')).toEqual([def]);
    storage.map.set(quickLibraryKey('fox'), 'not json');
    expect(loadQuickLibrary(storage, 'fox')).toEqual([]);
    expect(saveQuickLibrary(null, 'fox', [def])).toBe(false);
  });

  it('exports as JSON and imports that back, or a bare list', () => {
    expect(importLibrary(exportLibrary([def]))).toEqual([def]);
    expect(importLibrary(JSON.stringify([def]))).toEqual([def]);
    expect(importLibrary('{"other":1}')).toBeNull();
    expect(importLibrary('nope')).toBeNull();
  });

  it('a definition from the library joins the design once', () => {
    const { c } = mount();
    expect(c.addDef(def)).toBe('u.abc');
    expect(c.addDef(def)).toBe('u.abc');
    expect(c.snapshot().customDefs).toEqual([def]);
    expect(c.defOf('u.abc')?.otherVersion).toBe('b1.8.1');
    expect(c.removeDef('u.abc')).toBe(true);
    expect(c.snapshot().customDefs).toEqual([]);
  });
});
