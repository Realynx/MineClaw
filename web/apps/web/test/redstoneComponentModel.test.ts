/**
 * The redstone component model behind a mouse drag: `check` answers a preview without changing anything, a move can
 * snap a port onto another component's port (either way round) or onto the end of a dust line and lays the joint's
 * dust, ports say what they are joined to, a component can be duplicated, combined with others into one of the design's
 * own (which remembers its parts) and ungrouped again in any turn and mirror, what a box covered is saved with it, and
 * saved components that no longer match the circuit open as plain blocks with a note.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { LIBRARY, type Pos } from '@mineclaw/redstone';
import { afterEach, describe, expect, it } from 'vitest';
import { setPlotMode, type Cell } from '@/features/studio/canvas';
import { authoringStoreOf } from '@/features/studio/redstone/authoring';
import {
  readCustomDefs,
  readInstances,
  writeDef,
  writeInstance,
  type StorageLike,
} from '@/features/studio/redstone/componentData';
import { composePlacement, placeLayout, snapCorner, type Mirror } from '@/features/studio/redstone/ports';
import { RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import { Studio, type StudioApi } from '@/features/studio/studio';

const DUST = 55;
const COBBLE = 4;

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

function memoryStorage(): StorageLike {
  const map = new Map<string, string>();
  return { getItem: (k) => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v) };
}

const studios: Studio[] = [];
const controllers: RedstoneComponentController[] = [];
afterEach(() => {
  for (const c of controllers.splice(0)) c.dispose();
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
});

function mount(over: Partial<DesignerSession> = {}) {
  const studio = new Studio(session(over), api, 60_000);
  studios.push(studio);
  const c = new RedstoneComponentController(studio, memoryStorage());
  controllers.push(c);
  return { studio, c };
}

const sorted = (cells: Cell[]): Cell[] =>
  cells.sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0] || a[3] - b[3]);
const blocks = (s: Studio): Cell[] => sorted(s.canvas.cells());
const layoutBlocks = (def: string, at: Pos, turns = 0, mirror: Mirror = null): Cell[] =>
  sorted(
    placeLayout(LIBRARY.find((d) => d.id === def)!.layouts['1.12.2']!, { at, turns, mirror }, '1.12.2').cells.filter(
      (c) => c[3] > 0,
    ),
  );
const ids = (c: RedstoneComponentController) => c.snapshot().components.map((x) => x.id);
const envelope = (s: Studio) => JSON.stringify(authoringStoreOf(s).current);

/** Two NOTs in a row, the second's input snapped onto the first's output (x 10..12, joint 13, x 14..16). */
function chain() {
  const m = mount();
  m.c.place('gate.not', [10, 0, 10]);
  m.c.place('gate.not', [14, 0, 11], { snap: true });
  return m;
}

describe('check, for a drag preview', () => {
  it('gives the move rule’s reason, or null, and writes nothing', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [20, 0, 10]);
    const cells = blocks(studio);
    const text = envelope(studio);
    expect(c.check('c2', [30, 0, 30])).toBeNull();
    expect(c.check('c2', [13, 0, 10])).toMatch(/^NOT would touch NOT: keep one free cell/);
    expect(c.check('c2', [62, 0, 10])).toBe('NOT does not fit on the plot there.');
    expect(c.check('c2', [30.5, 0, 30])).toBe('Place components on whole block coordinates.');
    // A component is never too close to itself.
    expect(c.check('c1', [11, 0, 11])).toBeNull();
    expect(c.check('c9', [30, 0, 30])).toBe('There is no placed component "c9".');
    expect(blocks(studio)).toEqual(cells);
    expect(envelope(studio)).toBe(text);
    expect(studio.snapshot().undoLabel).toBe('place NOT');
    expect(c.snapshot().error).toBeNull();
  });

  it('checks the shape as turned, and reuses one prepared shape per turn and mirror', () => {
    const { c } = mount();
    const def = LIBRARY.find((d) => {
      const s = d.layouts['1.12.2']?.size;
      return !!s && s[0] > s[2];
    })!;
    const [sx, , sz] = def.layouts['1.12.2']!.size;
    c.place(def.id, [10, 0, 10]);
    // Against the plot's east edge only the turned shape (sz wide) fits.
    expect(c.check('c1', [64 - sz, 0, 10])).toMatch(/does not fit/);
    expect(c.check('c1', [64 - sz, 0, 10], 1)).toBeNull();
    expect(c.check('c1', [64 - sx, 0, 10])).toBeNull();
    expect(c.shapeOf('c1')).toBe(c.shapeOf('c1'));
    expect(c.shapeOf('c1', 1)).toBe(c.shapeOf('c1', 5));
    expect(c.shapeOf('c1', 1)).not.toBe(c.shapeOf('c1'));
    expect(c.shapeOf('c1', 1)!.box.max).toEqual([sz - 1, def.layouts['1.12.2']!.size[1] - 1, sx - 1]);
  });
});

describe('moving', () => {
  it('snaps an input onto an output nearby, lays the joint’s dust, and is one undo step', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [30, 0, 30]);
    const snap = c.snapMoveCorner('c2', [14, 0, 11])!;
    expect(snap).toEqual({ at: [14, 0, 10], joint: [13, 0, 11] });
    expect(c.previewPorts('c2', snap.at, undefined, undefined, snap.joint)).toEqual({
      open: [[17, 0, 11]],
      joined: [[13, 0, 11]],
    });
    expect(c.move('c2', snap.at, { joint: snap.joint })).toBe(true);
    expect(studio.canvas.get(13, 0, 11)).toEqual([DUST, 0]);
    expect(c.portsOf('c1').find((p) => p.name === 'Y')!.link).toEqual({ to: 'port', id: 'c2', port: 'A' });
    studio.undo();
    expect(c.snapshot().components[1]!.at).toEqual([30, 0, 30]);
    expect(studio.canvas.get(13, 0, 11)).toEqual([0, 0]);
  });

  it('snaps an output onto an input too, and an output never onto an output', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [20, 0, 10]);
    c.place('gate.not', [40, 0, 40], { turns: 2 });
    expect(c.snapMoveCorner('c1', [15, 0, 11])).toEqual({ at: [16, 0, 10], joint: [19, 0, 11] });
    expect(c.snapMoveCorner('c1', [40, 0, 20])).toBeNull();
    // Turned round, the third's output faces west and its input east: neither takes the first's output on its east.
    expect(c.snapMoveCorner('c3', [14, 0, 10])).toBeNull();
  });

  it('skips a snap the gap rule refuses', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [16, 0, 10]);
    c.place('gate.not', [40, 0, 40]);
    // Onto the first's output it would overlap the second; onto the second's input it would overlap the first.
    expect(c.snapMoveCorner('c3', [14, 0, 11])).toBeNull();
  });

  it('snaps a port onto the end of a dust line that runs straight on, never onto its middle', () => {
    const { studio, c } = mount();
    studio.edit(
      [
        [28, 0, 31, DUST, 0],
        [29, 0, 31, DUST, 0],
        [30, 0, 31, DUST, 0],
      ],
      'place',
      'wire',
    );
    c.place('gate.not', [10, 0, 10]);
    // The line runs west from x 30: an input on the west face takes it, the box east of it.
    const snap = c.snapMoveCorner('c1', [32, 0, 30])!;
    expect(snap).toEqual({ at: [31, 0, 30], joint: [30, 0, 31] });
    expect(c.move('c1', snap.at, { joint: snap.joint })).toBe(true);
    expect(c.portsOf('c1').find((p) => p.name === 'A')!.link).toEqual({ to: 'dust' });
    // Its middle (x 29) is no end: an input aimed right at it goes to the end beside it instead.
    c.move('c1', [10, 0, 10]);
    expect(c.snapMoveCorner('c1', [30, 0, 30])).toEqual({ at: [31, 0, 30], joint: [30, 0, 31] });
  });

  it('never snaps back onto its own joint’s dust, so a joined component can be dragged a cell or two', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    // Its output (13, 0, 11) carries a line on east.
    studio.edit(
      [
        [13, 0, 11, DUST, 0],
        [14, 0, 11, DUST, 0],
        [15, 0, 11, DUST, 0],
      ],
      'place',
      'wire',
    );
    expect(c.portsOf('c1').find((p) => p.name === 'Y')!.link).toEqual({ to: 'dust' });
    // One cell south: the line's own end under the output is nearest, and it is where the gate stands now.
    expect(c.snapMoveCorner('c1', [10, 0, 11])?.at).not.toEqual([10, 0, 10]);
    expect(c.snapMoveCorner('c1', [11, 0, 10])?.at).not.toEqual([10, 0, 10]);
  });

  it('moves with one group are one undo step (a held key)', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.move('c1', [11, 0, 10], { group: 'arrows' });
    c.move('c1', [12, 0, 10], { group: 'arrows' });
    expect(blocks(studio)).toEqual(layoutBlocks('gate.not', [12, 0, 10]));
    studio.undo();
    expect(c.snapshot().components[0]!.at).toEqual([10, 0, 10]);
    expect(blocks(studio)).toEqual(layoutBlocks('gate.not', [10, 0, 10]));
    expect(studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('a move can turn and mirror too, and the ports follow', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.move('c1', [20, 0, 20], { turns: 1, mirror: 'x' });
    expect(blocks(studio)).toEqual(layoutBlocks('gate.not', [20, 0, 20], 1, 'x'));
    const want = placeLayout(
      LIBRARY.find((d) => d.id === 'gate.not')!.layouts['1.12.2']!,
      {
        at: [20, 0, 20],
        turns: 1,
        mirror: 'x',
      },
      '1.12.2',
    ).ports;
    expect(c.portsOf('c1').map((p) => [p.name, p.at, p.side])).toEqual(want.map((p) => [p.name, p.at, p.side]));
  });
});

describe('ports', () => {
  it('say what they are joined to: another component’s port, dust, or nothing', () => {
    const { studio, c } = chain();
    expect(c.portsOf('c1').map((p) => [p.name, p.link])).toEqual([
      ['A', null],
      ['Y', { to: 'port', id: 'c2', port: 'A' }],
    ]);
    expect(c.portsOf('c2').map((p) => [p.name, p.link])).toEqual([
      ['A', { to: 'port', id: 'c1', port: 'Y' }],
      ['Y', null],
    ]);
    studio.edit([[17, 0, 11, DUST, 0]], 'place', 'wire');
    expect(c.portsOf('c2')[1]!.link).toEqual({ to: 'dust' });
    expect(c.portMarkers()).toEqual({
      open: [[9, 0, 11]],
      joined: [
        [13, 0, 11],
        [17, 0, 11],
      ],
    });
  });

  it('turn and mirror with the component', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.turn('c1');
    expect(c.portsOf('c1').map((p) => [p.name, p.side])).toEqual([
      ['A', 'north'],
      ['Y', 'south'],
    ]);
    c.mirror('c1', 'z');
    expect(c.portsOf('c1').map((p) => [p.name, p.side])).toEqual([
      ['A', 'south'],
      ['Y', 'north'],
    ]);
  });

  it('a corner snap lists every match nearest first, a port before a dust end', () => {
    const ports = placeLayout(
      LIBRARY.find((d) => d.id === 'gate.not')!.layouts['1.12.2']!,
      {
        at: [0, 0, 0],
        turns: 0,
        mirror: null,
      },
      '1.12.2',
    ).ports;
    const found = snapCorner(
      ports,
      [10, 0, 10],
      [
        { at: [9, 0, 12], kind: 'dust', side: 'west' },
        { at: [9, 0, 10], kind: 'out', side: 'east' },
        { at: [13, 0, 11], kind: 'out', side: 'east' },
      ],
    );
    expect(found.map((s) => [s.to, s.joint, s.d])).toEqual([
      ['port', [9, 0, 10], 1],
      ['dust', [9, 0, 12], 1],
    ]);
  });
});

describe('the outliner’s rows', () => {
  it('count blocks and port links once per change of the components or the canvas', () => {
    const { studio, c } = chain();
    const first = c.rows();
    expect(c.rows()).toBe(first);
    expect(first.get('c2')!.ports.map((p) => p.link)).toEqual([{ to: 'port', id: 'c1', port: 'Y' }, null]);
    expect(first.get('c1')!.blocks).toBe(layoutBlocks('gate.not', [10, 0, 10]).length);
    // Dust drawn at the open output changes no component, but the row sees it.
    studio.edit([[17, 0, 11, DUST, 0]], 'place', 'wire');
    const wired = c.rows();
    expect(wired).not.toBe(first);
    expect(wired.get('c2')!.ports[1]!.link).toEqual({ to: 'dust' });
    expect(c.rows()).toBe(wired);
    c.move('c1', [10, 0, 30]);
    expect(c.rows()).not.toBe(wired);
    expect(c.rows().get('c2')!.ports[0]!.link).not.toEqual({ to: 'port', id: 'c1', port: 'Y' });
  });
});

describe('duplicating', () => {
  it('copies beside it, turned, mirrored and labelled alike, as one undo step', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10], { turns: 1, mirror: 'x', label: 'inv' });
    const copy = c.duplicate('c1')!;
    expect(copy).toEqual({ id: 'c2', def: 'gate.not', at: [14, 0, 10], turns: 1, mirror: 'x', label: 'inv' });
    expect(blocks(studio)).toEqual(
      sorted([...layoutBlocks('gate.not', [10, 0, 10], 1, 'x'), ...layoutBlocks('gate.not', [14, 0, 10], 1, 'x')]),
    );
    expect(studio.snapshot().undoLabel).toBe('duplicate inv');
    studio.undo();
    expect(ids(c)).toEqual(['c1']);
    expect(c.duplicate('c1', [30, 0, 30])!.at).toEqual([30, 0, 30]);
    expect(c.duplicate('c1', [12, 0, 12])).toBeNull();
    expect(c.snapshot().error).toMatch(/would touch inv/);
  });

  it('goes further round when the east side is taken', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [14, 0, 10]);
    expect(c.duplicate('c1')!.at).toEqual([10, 0, 14]);
  });
});

describe('combining and ungrouping', () => {
  it('combines into one component whose ports are the outward ones, keeping every cell, as one undo step', () => {
    const { studio, c } = chain();
    const cells = blocks(studio);
    const combined = c.combine(['c2', 'c1'], 'buffer')!;
    expect(combined.def).toMatch(/^u\./);
    expect(combined).toEqual({ id: 'c3', def: combined.def, at: [10, 0, 10], turns: 0, mirror: null });
    expect(blocks(studio)).toEqual(cells);
    expect(ids(c)).toEqual(['c3']);
    const def = c.snapshot().customDefs[0]!;
    expect(def.name).toBe('buffer');
    expect(def.layout.size).toEqual([7, 1, 3]);
    expect(def.layout.ports).toEqual([
      { name: 'A', kind: 'in', at: [-1, 0, 1], side: 'west' },
      { name: 'Y', kind: 'out', at: [7, 0, 1], side: 'east' },
    ]);
    expect(def.parts).toEqual([
      { def: 'gate.not', at: [0, 0, 0], turns: 0, mirror: null },
      { def: 'gate.not', at: [4, 0, 0], turns: 0, mirror: null },
    ]);
    expect(studio.snapshot().undoLabel).toBe('combine buffer');
    studio.undo();
    expect(ids(c)).toEqual(['c1', 'c2']);
    expect(c.snapshot().customDefs).toEqual([]);
    studio.redo();
    expect(ids(c)).toEqual(['c3']);
  });

  it('moves the wire between its parts with it, leaving nothing behind', () => {
    const { studio, c } = chain();
    const cells = blocks(studio);
    c.combine(['c1', 'c2']);
    expect(c.move('c3', [30, 0, 30])).toBe(true);
    expect(blocks(studio)).toEqual(cells.map((b): Cell => [b[0] + 20, b[1], b[2] + 20, b[3], b[4]]));
  });

  it('numbers port names that repeat', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [10, 0, 14]);
    c.combine(['c1', 'c2']);
    expect(c.snapshot().customDefs[0]!.layout.ports.map((p) => [p.name, p.at])).toEqual([
      ['A1', [-1, 0, 1]],
      ['Y1', [3, 0, 1]],
      ['A2', [-1, 0, 5]],
      ['Y2', [3, 0, 5]],
    ]);
  });

  it('refuses one component, and a box that would take in another', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [20, 0, 10]);
    c.place('gate.not', [15, 0, 10], { label: 'middle' });
    expect(c.combine(['c1'])).toBeNull();
    expect(c.snapshot().error).toBe('Select at least two components to combine.');
    expect(c.combine(['c1', 'c2'])).toBeNull();
    expect(c.snapshot().error).toBe('middle lies in or beside the combined box: select it too, or move it away.');
    expect(ids(c)).toEqual(['c1', 'c2', 'c3']);
  });

  const orientations: Array<[number, Mirror]> = [0, 1, 2, 3].flatMap((t) =>
    (['x', 'z', null] as const).map((m): [number, Mirror] => [t, m]),
  );
  it.each(orientations)('ungroups a combined component turned %i, mirrored %s, without changing a cell', (t, m) => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10], { label: 'first' });
    expect(c.place('gate.and', [10, 0, 14], { turns: 1, mirror: 'z' })).not.toBeNull();
    expect(c.place('gate.not', [14, 0, 11], { snap: true })!.at).toEqual([14, 0, 10]);
    expect(c.place('gate.not', [18, 0, 10], { turns: 1 })).not.toBeNull();
    const combined = c.combine(['c1', 'c2', 'c3', 'c4'])!;
    expect(c.move(combined.id, [24, 0, 24], { turns: t, mirror: m })).toBe(true);
    const cells = blocks(studio);
    // Each part's cells as the ungroup lays them must be what stands there, or the canvas would change.
    const parts = c.ungroup(combined.id)!;
    expect(parts).toHaveLength(4);
    expect(blocks(studio)).toEqual(cells);
    expect(parts[0]!.label).toBe('first');
    expect(parts.map((p) => p.def)).toEqual(['gate.not', 'gate.and', 'gate.not', 'gate.not']);
    // The joint between the two NOTs is still joined.
    expect(c.portsOf(parts[2]!.id).find((p) => p.name === 'A')!.link).toMatchObject({ to: 'port', id: parts[0]!.id });
    // Nothing else used the combined definition.
    expect(c.snapshot().customDefs).toEqual([]);
    expect(studio.snapshot().undoLabel).toBe('ungroup combined');
    studio.undo();
    expect(ids(c)).toEqual([combined.id]);
  });

  it('keeps the combined definition while another copy uses it', () => {
    const { c } = chain();
    c.combine(['c1', 'c2']);
    c.duplicate('c3', [30, 0, 30]);
    expect(c.ungroup('c3')).toHaveLength(2);
    expect(c.snapshot().customDefs).toHaveLength(1);
  });

  it('keeps a combined definition another one is made from, so that one still gives its parts back', () => {
    const { c } = mount();
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [10, 0, 14]);
    const pair = c.combine(['c1', 'c2'], 'pair')!;
    const copy = c.duplicate(pair.id, [30, 0, 30])!;
    c.place('gate.not', [40, 0, 30], { label: 'third' });
    const outer = c.combine([copy.id, 'c5'], 'outer')!;
    expect(outer).not.toBeNull();
    expect(
      c
        .snapshot()
        .customDefs.find((d) => d.id === outer.def)!
        .parts!.map((p) => p.def),
    ).toContain(pair.def);
    // No placed copy uses the pair any more, but the outer one is made from it.
    expect(c.ungroup(pair.id)).toHaveLength(2);
    expect(c.snapshot().customDefs.map((d) => d.id)).toContain(pair.def);
    expect(c.removeDef(pair.def)).toBe(false);
    expect(c.snapshot().error).toBe('Another of your components is made from this one.');
    const parts = c.ungroup(outer.id)!;
    expect(parts.map((p) => p.def).sort()).toEqual([pair.def, 'gate.not'].sort());
    // Once nothing is made from the outer one or places it, it goes.
    expect(c.snapshot().customDefs.map((d) => d.id)).toEqual([pair.def]);
  });

  it('ungroups a library gate to plain blocks', () => {
    const { studio, c } = mount();
    c.place('gate.not', [10, 0, 10]);
    const cells = blocks(studio);
    expect(c.ungroup('c1')).toEqual([]);
    expect(ids(c)).toEqual([]);
    expect(blocks(studio)).toEqual(cells);
    expect(studio.snapshot().undoLabel).toBe('unlink NOT');
  });

  it('turns parts with the component around them', () => {
    expect(composePlacement({ turns: 1, mirror: null }, { turns: 2, mirror: 'x' })).toEqual({ turns: 3, mirror: 'x' });
    expect(composePlacement({ turns: 1, mirror: 'x' }, { turns: 2, mirror: null })).toEqual({ turns: 3, mirror: 'x' });
    expect(composePlacement({ turns: 0, mirror: 'z' }, { turns: 1, mirror: null })).toEqual({ turns: 3, mirror: 'z' });
    expect(composePlacement({ turns: 0, mirror: 'x' }, { turns: 1, mirror: 'x' })).toEqual({ turns: 3, mirror: null });
    expect(composePlacement({ turns: 0, mirror: 'x' }, { turns: 0, mirror: 'z' })).toEqual({ turns: 2, mirror: null });
  });
});

describe('saving', () => {
  it('reads and writes `under` and `parts`, and an envelope without them reads as before', () => {
    const old = { id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 0, mirror: null };
    expect(readInstances([old]).map(writeInstance)).toEqual([old]);
    const withUnder = { ...old, under: [[1, 0, 0, 64], [0, 0, 0, -1], 'bad', [40, 0, 0, 1], [0, 0, 0, 9999]] };
    expect(readInstances([withUnder]).map(writeInstance)).toEqual([
      {
        ...old,
        under: [
          [1, 0, 0, 64],
          [0, 0, 0, -1],
        ],
      },
    ]);
    const layout = { size: [1, 1, 1], cells: [[0, 0, 0, 1, 0]], ports: [] };
    const def = { id: 'u.a', name: 'pair', layout, parts: [{ def: 'gate.not', at: [0, 0, 0], turns: 1, mirror: 'x' }] };
    expect(readCustomDefs([def]).map(writeDef)).toEqual([def]);
    // A part that does not read drops them all: the definition then ungroups to plain blocks.
    expect(readCustomDefs([{ ...def, parts: [...def.parts, { def: 'gate.not', at: [0, 0] }] }])[0]).toEqual({
      id: 'u.a',
      name: 'pair',
      layout,
    });
    expect(readCustomDefs([{ id: 'u.b', name: 'old', layout }])[0]).toEqual({ id: 'u.b', name: 'old', layout });
  });

  it('a combined component opens again from the saved envelope and still ungroups', () => {
    const first = chain();
    first.c.combine(['c1', 'c2'], 'buffer');
    const cells = first.studio.canvas.cells();
    const again = mount({ blocks: cells, designId: 5, rev: 1, redstoneAuthoring: envelope(first.studio) });
    expect(ids(again.c)).toEqual(['c3']);
    expect(again.studio.snapshot().dirty).toBe(false);
    expect(again.c.ungroup('c3')!.map((p) => p.at)).toEqual([
      [10, 0, 10],
      [14, 0, 10],
    ]);
    expect(blocks(again.studio)).toEqual(sorted([...cells]));
  });

  it('remembers what a box covered across a reload, so a remove gives it back', () => {
    const first = mount({ blocks: [[11, 0, 10, COBBLE, 0]] });
    first.c.place('gate.not', [10, 0, 10]);
    expect(authoringStoreOf(first.studio).current.components).toEqual([
      { id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 0, mirror: null, under: [[1, 0, 0, COBBLE * 16]] },
    ]);
    const again = mount({
      blocks: first.studio.canvas.cells(),
      designId: 5,
      rev: 1,
      redstoneAuthoring: envelope(first.studio),
    });
    expect(again.studio.snapshot().dirty).toBe(false);
    expect(again.c.remove('c1')).toBe(true);
    expect(blocks(again.studio)).toEqual([[11, 0, 10, COBBLE, 0]]);
  });
});

describe('opening saved components that no longer match', () => {
  const saved = (n: number) =>
    JSON.stringify({
      redstone: 1,
      components: Array.from({ length: n }, (_, i) => ({
        id: `c${i + 1}`,
        def: 'gate.not',
        at: [10 + 10 * i, 0, 10],
        turns: 0,
        mirror: null,
      })),
      customDefs: [],
    });
  /** The NOTs at x 10, 20, 30…, the ones listed broken (their torch taken). */
  const canvas = (n: number, broken: number[]) =>
    Array.from({ length: n }, (_, i) =>
      layoutBlocks('gate.not', [10 + 10 * i, 0, 10]).filter((b) => !broken.includes(i) || b[3] !== 76),
    ).flat();

  it('says how many were opened as plain blocks', () => {
    const one = mount({ blocks: canvas(2, [1]), designId: 5, rev: 1, redstoneAuthoring: saved(2) });
    expect(ids(one.c)).toEqual(['c1']);
    expect(one.c.restoreNote).toBe(
      '1 of 2 saved components no longer matches the circuit and was opened as plain blocks. The saved revision still has it.',
    );
    const two = mount({ id: 10, blocks: canvas(3, [0, 2]), designId: 5, rev: 1, redstoneAuthoring: saved(3) });
    expect(ids(two.c)).toEqual(['c2']);
    expect(two.c.restoreNote).toBe(
      '2 of 3 saved components no longer match the circuit and were opened as plain blocks. The saved revision still has them.',
    );
    const only = mount({ id: 11, blocks: canvas(1, [0]), designId: 5, rev: 1, redstoneAuthoring: saved(1) });
    expect(only.c.restoreNote).toBe(
      'The saved component no longer matches the circuit and was opened as plain blocks. The saved revision still has it.',
    );
  });

  it('says nothing when all match, or when the canvas is empty', () => {
    expect(mount({ blocks: canvas(2, []), designId: 5, rev: 1, redstoneAuthoring: saved(2) }).c.restoreNote).toBeNull();
    expect(mount({ id: 10, designId: 5, rev: 1, redstoneAuthoring: saved(2) }).c.restoreNote).toBeNull();
  });
});
