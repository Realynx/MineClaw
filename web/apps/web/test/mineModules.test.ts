import { afterEach, describe, expect, it, vi } from 'vitest';
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { DEPTH, keyOf, posOf, setPlotMode, type V3 } from '@/features/studio/canvas';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { MineModuleController } from '@/features/studio/mine/MineModuleController';
import { decodeModuleDrag, defaultModule, encodeModule, MINE_MODULES, moduleCells, modulePorts, snapModuleAt } from '@/features/studio/mine/modules';
import { MINE_PRESETS, presetCells, presetModules } from '@/features/studio/mine/presets';

const sessions: Studio[] = [], controllers: MineModuleController[] = [];
const api: StudioApi = { canvas: () => Promise.resolve({ rev: 1, applied: 1 }), message: () => Promise.reject(new Error('unused')), save: () => Promise.reject(new Error('unused')) };
function make(storage: Pick<Storage, 'getItem' | 'setItem'> | null = null, over: Partial<DesignerSession> = {}) {
  const s = new Studio({ id: 9, owner: 'test', model: 'main', designId: null, name: 'mine', rev: 0, blocks: [], anchor: null,
    messages: [], costUsd: 0, calls: 0, version: '1.12.2', palette: MOCK_DESIGNER_PALETTE, plot: { size: 64, height: 128, depth: 0 },
    revisions: [], kind: 'mine', seed: '12345', ...over }, api, 60000);
  const c = new MineModuleController(s, storage); sessions.push(s); controllers.push(c); return { s, c };
}
afterEach(() => { controllers.splice(0).forEach((c) => c.dispose()); sessions.splice(0).forEach((s) => s.dispose()); setPlotMode({ kind: 'build' }); localStorage.clear(); });
const at: V3 = [30, 20, 30];
const tunnel = () => ({ ...defaultModule('tunnel'), length: 7 });

describe('composable mine geometry', () => {
  it.each(MINE_PRESETS)('$id preserves the existing preset excavation, lighting and direction as editable components', (preset) => {
    const { s, c } = make(null, { version: preset.version });
    const expected = new Map(presetCells(preset, s.terrain).cells.map((cell) => [cell.slice(0, 3).join(','), cell]));
    const pieces = presetModules(preset, s.terrain);
    const actual = new Map(pieces.flatMap((piece) => moduleCells(piece.spec, piece.at).cells).map((cell) => [cell.slice(0, 3).join(','), cell]));
    expect(actual).toEqual(expected);
    const inserted = c.addMany(pieces, preset.name)!;
    expect(inserted).toHaveLength(preset.steps.length);
    expect(c.snapshot()).toHaveLength(2);
    s.undo(); expect(c.snapshot()).toHaveLength(0); expect(s.canvas.holes()).toHaveLength(0);
    s.redo(); expect(c.snapshot()).toHaveLength(2);
    const branch = c.snapshot()[1]!;
    expect(c.update(branch.id, { ...branch.spec, spacing: 3 })).toBe(true);
    expect(c.remove(branch.id)).toBe(true); expect(c.snapshot()).toHaveLength(1);
  });
  it.each(MINE_MODULES)('$kind fits around a central click and every entrance has two-block headroom', ({ kind }) => {
    for (let rotation = 0; rotation < 4; rotation++) {
      const spec = { ...defaultModule(kind), rotation }, shape = moduleCells(spec, at);
      const keys = new Set(shape.cells.map((c) => c.slice(0, 3).join(',')));
      expect(keys.size).toBe(shape.cells.length);
      expect(shape.cells.every(([x, y, z]) => x >= 0 && x < 64 && z >= 0 && z < 64 && y >= 1 && y < 128)).toBe(true);
      for (const p of modulePorts(spec, at)) {
        expect(keys.has(p.at.join(',')), `${kind} port ${p.at.join(',')}`).toBe(true);
        expect(keys.has([p.at[0], p.at[1] + 1, p.at[2]].join(','))).toBe(true);
      }
    }
  });
  it('descents preserve a supporting step beneath each walkable path cell', () => {
    for (const kind of ['stairs', 'spiral'] as const) {
      const shape = moduleCells(defaultModule(kind), at), digs = new Set(shape.cells.map((c) => c.slice(0, 3).join(',')));
      for (const [x, y, z] of shape.path) expect(digs.has([x, y - 1, z].join(','))).toBe(false);
    }
  });
  it('snaps opposite entrances into adjacent cells and never changes the chosen Y', () => {
    const spec = tunnel(), a = { id: 'a', spec, at };
    expect(snapModuleAt(spec, [38, 20, 30], [a])).toEqual([37, 20, 30]);
    expect(snapModuleAt(spec, [38, 21, 30], [a])).toEqual([38, 21, 30]);
    expect(snapModuleAt({ ...spec, rotation: 1 }, [38, 20, 30], [a])).toEqual([38, 20, 30]);
    expect(snapModuleAt(spec, [38, 20, 30], [a], 'a')).toEqual([38, 20, 30]);
  });
  it('validates drag payloads and bounds dimensions before geometry generation', () => {
    expect(decodeModuleDrag(encodeModule(tunnel()))).toEqual(tunnel());
    expect(decodeModuleDrag('{broken')).toBeNull();
    expect(decodeModuleDrag('{"kind":"unknown"}')).toBeNull();
    expect(decodeModuleDrag('{"kind":"tunnel","length":100000}')).toMatchObject({ length: 192 });
  });
});

describe('mine module edits and ownership', () => {
  it('saves metadata with cells and restores a fresh browser including groups, settings, visibility and removal baselines', async () => {
    const first = make(), { s, c } = first;
    s.edit([[30, 20, 30, 0, 0]], 'dig', 'existing');
    const a = c.add({ ...defaultModule('spiral'), clockwise: false, torchEvery: 6, landingDepths: [6] }, [30, 30, 30])!;
    const b = c.add(tunnel(), [30, 20, 30])!;
    const group = c.combine([a.id, b.id])!;
    c.setVisible(group.id, false);
    const save = vi.spyOn(api, 'save').mockResolvedValue({ designId: 111, rev: 4, name: 'saved', blocks: s.canvas.size, created: true, text: 'saved' });
    await s.save('saved');
    const mineAuthoring = save.mock.calls[0]![1].mineAuthoring!;
    expect(mineAuthoring).toBeTruthy(); expect(s.dirty).toBe(false);
    const reopened = make(null, { id: 100, designId: 111, mineAuthoring, blocks: s.canvas.cells(), dug: s.canvas.holes() });
    expect(reopened.c.snapshot()).toEqual(c.snapshot());
    expect(reopened.c.snapshot()[0]!.spec.parts![0]!.spec).toMatchObject({ clockwise: false, torchEvery: 6, landingDepths: [6] });
    reopened.c.setVisible(group.id, true); expect(reopened.s.dirty).toBe(true);
    reopened.c.setVisible(group.id, false); expect(reopened.s.dirty).toBe(false);
    reopened.c.remove(group.id);
    expect(reopened.s.canvas.holes()).toEqual([[30, 20, 30]]);
    save.mockRestore();
  });
  it('prefers exact server revision metadata over conflicting local metadata and never adopts another revision for legacy cells', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage); first.c.add(tunnel(), at);
    const raw = [...data.values()][0]!;
    const original = JSON.parse(raw) as { modules: Array<{ visible?: boolean }> }; original.modules[0]!.visible = false;
    const restored = make(storage, { mineAuthoring: JSON.stringify(original), dug: first.s.canvas.holes() });
    expect(restored.c.snapshot()[0]!.visible).toBe(false);
    const legacy = make(storage, { id: 999, mineAuthoring: null, dug: first.s.canvas.holes() });
    expect(legacy.c.snapshot()).toHaveLength(0);
  });
  it('recovers compatible legacy browser components for the exact opened revision and marks them for server migration', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage), a = first.c.add({ ...tunnel(), label: 'old revision' }, at)!;
    first.c.rememberDesign(12, 3);
    first.c.update(a.id, { ...a.spec, label: 'new revision' }); first.c.rememberDesign(12, 4);
    const saved = { blocks: first.s.canvas.cells(), dug: first.s.canvas.holes(), designId: 12, mineAuthoring: null, designRevision: 3, revisions: [{ rev: 4, created: 0, n: 14 }, { rev: 3, created: 0, n: 14 }] };
    const restored = make(storage, { ...saved, id: 77 });
    expect(restored.c.snapshot()[0]!.spec.label).toBe('old revision');
    expect(restored.c.recoveredLocal).toBe(true); expect(restored.s.dirty).toBe(true);
    const wrongRevision = make(storage, { ...saved, id: 78, designRevision: 2 });
    expect(wrongRevision.c.snapshot()).toHaveLength(0);
    const differentCells = make(storage, { ...saved, id: 79, dug: [] });
    expect(differentCells.c.snapshot()).toHaveLength(0);
    const wrongOwner = make(storage, { ...saved, id: 80, owner: 'someone else' });
    expect(wrongOwner.c.snapshot()).toHaveLength(0);
  });
  it('hides a large component without changing cells, rebuilding geometry or affecting exact selection from the outliner', () => {
    const { s, c } = make(), a = c.add({ ...defaultModule('chamber'), length: 96, width: 80 }, [100, 20, 100])!;
    const shape = moduleCells(a.spec, a.at), holes = s.canvas.holes();
    c.setVisible(a.id, false);
    expect(moduleCells(c.snapshot()[0]!.spec, c.snapshot()[0]!.at)).toBe(shape);
    expect(s.canvas.holes()).toEqual(holes);
    expect(c.atCell([100, 20, 100])?.id).toBe(a.id);
  });
  it('refuses layout changes while the designer is busy without changing metadata, cells or history', () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    const snapshot = c.snapshot(), history = s.history.peekUndo(), holes = s.canvas.holes();
    s.busy = true;
    expect(c.add(tunnel(), [30, 20, 40])).toBeNull();
    expect(c.move(a.id, [30, 20, 35])).toBe(false);
    expect(c.update(a.id, { ...tunnel(), length: 11 })).toBe(false);
    expect(c.replace(a.id, { ...tunnel(), length: 11 }, [30, 20, 35])).toBe(false);
    expect(c.remove(a.id)).toBe(false);
    expect(c.lastError).toContain('Wait for the designer');
    expect(c.snapshot()).toBe(snapshot);
    expect(s.history.peekUndo()).toBe(history);
    expect(s.canvas.holes()).toEqual(holes);
    s.busy = false;
    expect(c.move(a.id, [30, 20, 35])).toBe(true);
  });
  it('adds, moves, resizes and removes with coherent metadata through undo and redo', () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    expect(c.atCell(at)?.id).toBe(a.id);
    expect(c.move(a.id, [30, 20, 35])).toBe(true);
    expect(s.canvas.authoredState(keyOf(...at))).toBe(-1);
    s.undo(); expect(c.snapshot()[0]!.at).toEqual(at); expect(s.canvas.authoredState(keyOf(...at))).toBe(0);
    s.redo(); expect(c.snapshot()[0]!.at).toEqual([30, 20, 35]);
    c.update(a.id, { ...tunnel(), length: 11 }); expect(c.snapshot()[0]!.spec.length).toBe(11);
    s.undo(); expect(c.snapshot()[0]!.spec.length).toBe(7);
    c.remove(a.id); expect(c.snapshot()).toHaveLength(0); expect(s.canvas.dug.size).toBe(0);
    s.undo(); expect(c.snapshot()).toHaveLength(1); expect(s.canvas.dug.size).toBe(14);
  });
  it('keeps an overlapping tunnel when one layout moves or is removed', () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    const b = c.add({ ...tunnel(), rotation: 1 }, at)!;
    c.move(a.id, [30, 20, 40]);
    expect(s.canvas.authoredState(keyOf(...at))).toBe(0);
    c.remove(b.id); expect(s.canvas.authoredState(keyOf(...at))).toBe(-1);
    s.undo(); expect(s.canvas.authoredState(keyOf(...at))).toBe(0);
    s.undo(); expect(c.snapshot().find((m) => m.id === a.id)!.at).toEqual(at);
  });
  it('preserves manual blocks and pre-existing excavation when a module leaves', () => {
    const { s, c } = make();
    s.edit([[30, 20, 30, 0, 0]], 'dig', 'manual dig');
    const a = c.add(tunnel(), at)!;
    s.edit([[31, 20, 30, 50, 5]], 'place', 'manual torch');
    c.remove(a.id);
    expect(s.canvas.authoredState(keyOf(...at))).toBe(0);
    expect(s.canvas.get(31, 20, 30)).toEqual([50, 5]);
    expect(s.canvas.authoredState(keyOf(29, 20, 30))).toBe(-1);
    s.undo(); expect(c.snapshot()).toHaveLength(1); expect(s.canvas.get(31, 20, 30)).toEqual([50, 5]);
    s.undo(); expect(s.canvas.get(31, 20, 30)).toEqual([0, 0]);
    c.remove(a.id); expect(s.canvas.authoredState(keyOf(31, 20, 30))).toBe(-1);
  });
  it('undoes a fully overlapping metadata-only addition independently', () => {
    const { s, c } = make(); c.add(tunnel(), at); c.add(tunnel(), at);
    expect(c.snapshot()).toHaveLength(2); expect(s.canvas.dug.size).toBe(14);
    s.undo(); expect(c.snapshot()).toHaveLength(1); expect(s.canvas.dug.size).toBe(14);
    s.undo(); expect(c.snapshot()).toHaveLength(0); expect(s.canvas.dug.size).toBe(0);
    s.redo(); s.redo(); expect(c.snapshot()).toHaveLength(2);
  });
  it('coalesces repeated moves from one drag into one undo step', () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    c.move(a.id, [30, 20, 31], 'drag1'); c.move(a.id, [30, 20, 32], 'drag1'); c.move(a.id, [30, 20, 33], 'drag1');
    s.undo(); expect(c.snapshot()[0]!.at).toEqual(at); expect(s.canvas.dug.size).toBe(14);
    s.redo(); expect(c.snapshot()[0]!.at).toEqual([30, 20, 33]); expect(s.canvas.dug.size).toBe(14);
  });
  it('commits moving and resizing together atomically, without an undo for unchanged edits', () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    const original = s.history.peekUndo();
    c.move(a.id, at); c.update(a.id, tunnel());
    expect(s.history.peekUndo()).toBe(original);
    c.replace(a.id, { ...tunnel(), length: 11 }, [30, 20, 35]);
    expect(c.snapshot()[0]).toMatchObject({ at: [30, 20, 35], spec: { length: 11 } });
    s.undo(); expect(c.snapshot()[0]).toMatchObject({ at, spec: { length: 7 } });
    expect(s.canvas.dug.size).toBe(14);
  });
  it('rejects the entire operation at a plot edge or bedrock without clipping', () => {
    const { s, c } = make();
    expect(c.add(tunnel(), [0, 20, 0])).toBeNull(); expect(c.lastError).toContain('whole layout');
    expect(s.canvas.dug.size).toBe(0); expect(s.history.canUndo).toBe(false);
    expect(c.add({ ...defaultModule('stairs'), depth: 22 }, at)).toBeNull();
    const a = c.add(tunnel(), at)!;
    expect(c.move(a.id, [255, 20, 30])).toBe(false); expect(c.snapshot()[0]!.at).toEqual(at);
  });
  it('supports long components throughout the enlarged mine footprint', () => {
    const { s, c } = make();
    const a = c.add({ ...tunnel(), length: 192 }, [128, 20, 200]);
    expect(a).not.toBeNull();
    expect(s.canvas.holes()).toHaveLength(384);
    expect(s.canvas.authoredState(keyOf(224, 20, 200))).toBe(0);
    expect(c.move(a!.id, [200, 20, 200])).toBe(false);
  });
  it('combines overlapping rotated pieces without changing excavation or manual edits, and undoes atomically', () => {
    const { s, c } = make();
    const a = c.add(tunnel(), at)!, b = c.add({ ...tunnel(), rotation: 1, length: 11 }, at)!;
    s.edit([[31, 20, 30, 50, 5]], 'place', 'manual torch');
    const holes = s.canvas.holes(), blocks = s.canvas.cells();
    const group = c.combine([a.id, b.id, a.id])!;
    expect(group.spec.kind).toBe('custom');
    expect(group.spec.parts).toHaveLength(2);
    expect(group.spec.parts![1]!.spec).toMatchObject({ rotation: 1, length: 11 });
    expect(c.snapshot()).toEqual([group]);
    expect(s.canvas.holes()).toEqual(holes); expect(s.canvas.cells()).toEqual(blocks);
    s.undo(); expect(c.snapshot()).toEqual([a, b]); expect(s.canvas.holes()).toEqual(holes);
    s.redo(); expect(c.snapshot()).toEqual([group]);
    const oldShape = moduleCells(group.spec, group.at).cells;
    expect(c.update(group.id, { ...group.spec, rotation: 1 })).toBe(true);
    const rotated = new Set(moduleCells(c.snapshot()[0]!.spec, at).cells.map((cell) => cell.slice(0, 3).join(',')));
    for (const [x, y, z] of oldShape) expect(rotated.has([at[0] - (z - at[2]), y, at[2] + (x - at[0])].join(','))).toBe(true);
    expect(s.canvas.get(31, 20, 30)).toEqual([50, 5]);
    const copy = c.add(group.spec, [100, 20, 100])!;
    expect(copy.spec).toEqual(group.spec); expect(copy.spec).not.toBe(group.spec);
    expect(copy.spec.parts![0]!.spec).not.toBe(group.spec.parts![0]!.spec);
  });
  it('migrates original 64-column component metadata into the expanded canvas without moving edits', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage), a = first.c.add(tunnel(), at)!;
    first.s.edit([[31, 20, 30, 50, 5]], 'place', 'manual torch');
    for (const [key, raw] of data) {
      const saved = JSON.parse(raw) as { size?: number; baseDefault?: number; base: number[][]; overrides: number[][] };
      delete saved.size;
      delete saved.baseDefault;
      saved.base = moduleCells(a.spec, a.at).cells.map(([x, y, z]) => [keyOf(x, y, z), -1]);
      for (const values of [saved.base, saved.overrides]) for (const pair of values) {
        const [x, y, z] = posOf(pair[0]!); pair[0] = x + 64 * (z + 64 * (y + DEPTH));
      }
      data.set(key, JSON.stringify(saved));
    }
    const restored = make(storage, { blocks: first.s.canvas.cells(), dug: first.s.canvas.holes() });
    expect(restored.c.snapshot()[0]!.id).toBe(a.id);
    restored.c.remove(a.id);
    expect(restored.s.canvas.get(31, 20, 30)).toEqual([50, 5]);
    expect(restored.s.canvas.authoredState(keyOf(...at))).toBe(-1);
  });
  it('stores large groups compactly and restores pre-existing excavation when removing the reopened group', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage);
    first.s.edit([[100, 20, 100, 0, 0]], 'dig', 'existing tunnel');
    const a = first.c.add({ ...defaultModule('chamber'), length: 96, width: 80 }, [100, 20, 100])!;
    const b = first.c.add({ ...tunnel(), length: 192 }, [100, 20, 150])!;
    const group = first.c.combine([a.id, b.id])!;
    expect(group).not.toBeNull();
    expect([...data.values()].every((v) => v.length < 2000)).toBe(true);
    const restored = make(storage, { blocks: first.s.canvas.cells(), dug: first.s.canvas.holes() });
    expect(restored.c.snapshot()).toEqual([group]);
    restored.c.remove(group.id);
    expect(restored.s.canvas.holes()).toEqual([[100, 20, 100]]);
  });
  it('round-trips only metadata consistent with the loaded canvas; stale metadata never restores deleted cells', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage), a = first.c.add(tunnel(), at)!;
    first.s.edit([[31, 20, 30, 50, 5]], 'place', 'manual torch');
    const restored = make(storage, { blocks: first.s.canvas.cells(), dug: first.s.canvas.holes() });
    expect(restored.c.snapshot()[0]!.id).toBe(a.id);
    restored.c.remove(a.id); expect(restored.s.canvas.get(31, 20, 30)).toEqual([50, 5]);
    // Persist the original metadata again, then reopen a canvas that no longer contains its digs.
    first.c.move(a.id, [30, 20, 31]);
    const stale = make(storage);
    expect(stale.c.snapshot()).toHaveLength(0); expect(stale.s.canvas.dug.size).toBe(0);
  });
  it('reopens saved design pieces in a new session without leaking another owner’s metadata', () => {
    const data = new Map<string, string>(), storage = { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } };
    const first = make(storage); first.c.add(tunnel(), at); first.c.rememberDesign(12, 3);
    const saved = { blocks: first.s.canvas.cells(), dug: first.s.canvas.holes(), designId: 12, revisions: [{ rev: 3, created: 0, n: 14 }] };
    first.c.remove(first.c.snapshot()[0]!.id); // does not overwrite the saved revision metadata
    const reopened = make(storage, { ...saved, id: 10 });
    expect(reopened.c.snapshot()).toHaveLength(1);
    const otherOwner = make(storage, { ...saved, id: 11, owner: 'other-user' });
    expect(otherOwner.c.snapshot()).toHaveLength(0);
  });
  it('anchors a new group at the middle of its footprint, at the first piece’s level, so it turns in place', () => {
    const { s, c } = make();
    const a = c.add(tunnel(), [20, 20, 30])!, b = c.add(tunnel(), [50, 24, 34])!;
    const group = c.combine([a.id, b.id])!;
    // The two cover x 17..53 and z 30..34.
    expect(group.at).toEqual([35, 20, 32]);
    expect(group.spec.parts!.map((p) => p.at)).toEqual([[-15, 0, -2], [15, 4, 2]]);
    expect(c.update(group.id, { ...group.spec, rotation: 1 })).toBe(true);
    const xs = s.canvas.holes().map((p) => p[0]), zs = s.canvas.holes().map((p) => p[2]);
    expect([Math.min(...xs), Math.max(...xs), Math.min(...zs), Math.max(...zs)]).toEqual([33, 37, 14, 50]);
  });
  it('a group saved with its first piece as the anchor keeps it, and still turns about it', () => {
    const first = make();
    first.c.add(tunnel(), [20, 20, 30]); first.c.add(tunnel(), [50, 20, 30]);
    // As groups were saved before they were centred: anchored on the first piece, the other one 30 blocks east of it.
    const mineAuthoring = JSON.stringify({ size: 256, baseDefault: -1, base: [], overrides: [], modules: [{ id: 'old-group', at: [20, 20, 30],
      spec: { ...defaultModule('custom'), label: 'Old floor', parts: [{ spec: tunnel(), at: [0, 0, 0] }, { spec: tunnel(), at: [30, 0, 0] }] } }] });
    const { s, c } = make(null, { id: 100, mineAuthoring, dug: first.s.canvas.holes() });
    expect(c.snapshot()).toMatchObject([{ id: 'old-group', at: [20, 20, 30] }]);
    expect(c.snapshot()[0]!.spec.parts!.map((p) => p.at)).toEqual([[0, 0, 0], [30, 0, 0]]);
    expect(c.update('old-group', { ...c.snapshot()[0]!.spec, rotation: 1 })).toBe(true);
    // Turned about the first tunnel's middle: the other one swings from 30 east of it to 30 south.
    expect(s.canvas.authoredState(keyOf(20, 20, 60))).toBe(0);
    expect(s.canvas.authoredState(keyOf(50, 20, 30))).toBe(-1);
  });
  it('check says why a piece cannot stand somewhere (off the plot, into bedrock) without changing anything', () => {
    const { s, c } = make();
    expect(c.check(tunnel(), at)).toBeNull();
    expect(c.check(tunnel(), [0, 20, 30])).toContain('whole layout must fit');
    expect(c.check(tunnel(), [30, 0, 30])).toContain('whole layout must fit');
    let rock: V3 | null = null;
    for (let x = 10; x < 50 && !rock; x++) for (let z = 10; z < 50 && !rock; z++) if (s.terrain!.state(x, 1, z) >> 4 === 7) rock = [x, 1, z];
    expect(rock).not.toBeNull();
    expect(c.check(tunnel(), rock!)).toContain('reaches bedrock');
    // The same rule `add` applies, and asking places nothing.
    expect(c.add(tunnel(), rock!)).toBeNull();
    expect(c.lastError).toBe(c.check(tunnel(), rock!));
    expect(c.snapshot()).toHaveLength(0);
    expect(s.snapshot().canUndo).toBe(false);
  });
  it('ownsAny tells whether a box or a picked selection takes in a component cell', () => {
    const { c } = make();
    const item = c.add(tunnel(), at)!;
    expect(c.ownsAny({ min: at, max: at })).toBe(true);
    expect(c.ownsAny({ min: [0, 1, 0], max: [10, 60, 10] })).toBe(false);
    expect(c.ownsAny({ min: [0, 1, 0], max: [63, 60, 63], mask: new Set([keyOf(5, 5, 5)]) })).toBe(false);
    expect(c.ownsAny({ min: at, max: at, mask: new Set([keyOf(...at)]) })).toBe(true);
    c.remove(item.id);
    expect(c.ownsAny({ min: at, max: at })).toBe(false);
  });
  /** Every cell the layout owns, as the later-wins union the canvas holds: "x,y,z" → "id,meta". */
  const owned = (modules: readonly { spec: Parameters<typeof moduleCells>[0]; at: V3 }[]) =>
    new Map(modules.flatMap((m) => moduleCells(m.spec, m.at).cells.map((cell) => [cell.slice(0, 3).join(), cell.slice(3).join()] as const)));
  it.each([0, 1, 2, 3])('ungroups a group turned %i quarter turns in place, without changing a cell, as one undo step', (q) => {
    const { s, c } = make();
    const d = c.add({ ...defaultModule('spiral'), torchEvery: 4, clockwise: false }, [96, 26, 96])!;
    // Overlapping, lit and turned pieces: the torch metas and the later-wins order both have to come out the same.
    const a = c.add({ ...tunnel(), torchEvery: 3 }, [100, 20, 100])!, b = c.add({ ...defaultModule('chamber'), rotation: 1 }, [103, 20, 101])!;
    const combined = c.combine([a.id, b.id])!;
    expect(c.update(combined.id, { ...combined.spec, rotation: q })).toBe(true);
    const group = c.snapshot()[1]!, before = c.snapshot(), cells = owned(before);
    expect([...cells.values()].filter((v) => v.startsWith('50,')).length).toBeGreaterThanOrEqual(2);
    const holes = s.canvas.holes(), blocks = s.canvas.cells(), edits = vi.spyOn(s, 'editWithHistory');
    const parts = c.ungroup(group.id)!;
    expect(parts.map((p) => p.spec.rotation)).toEqual([q % 4, (1 + q) % 4]);
    expect(c.snapshot()).toEqual([d, ...parts]);
    expect(owned(c.snapshot())).toEqual(cells);
    expect(edits).toHaveBeenCalledOnce();
    expect(edits.mock.calls[0]![0]).toEqual([]);
    expect(s.canvas.holes()).toEqual(holes); expect(s.canvas.cells()).toEqual(blocks);
    s.undo();
    expect(c.snapshot()).toEqual(before);
    expect(s.canvas.holes()).toEqual(holes);
    s.redo();
    expect(c.snapshot()).toEqual([d, ...parts]);
  });
  it('ungroups a nested group one level at a time, keeping every cell and the hidden flag', () => {
    const { s, c } = make();
    const a = c.add({ ...tunnel(), torchEvery: 2 }, [100, 20, 100])!, b = c.add({ ...defaultModule('stairs'), rotation: 3 }, [104, 20, 106])!;
    const inner = c.combine([a.id, b.id])!;
    expect(c.update(inner.id, { ...inner.spec, rotation: 1 })).toBe(true);
    const e = c.add({ ...defaultModule('junction'), rotation: 2 }, [110, 20, 98])!;
    const outer = c.combine([c.snapshot()[0]!.id, e.id])!;
    expect(c.update(outer.id, { ...outer.spec, rotation: 3 })).toBe(true);
    c.setVisible(outer.id, false);
    const cells = owned(c.snapshot()), holes = s.canvas.holes(), blocks = s.canvas.cells();
    const [group, last] = c.ungroup(outer.id)!;
    expect(group!.spec.kind).toBe('custom');
    expect(group!.spec.rotation).toBe(0);
    expect([group!.visible, last!.visible]).toEqual([false, false]);
    expect(owned(c.snapshot())).toEqual(cells);
    const leaves = c.ungroup(group!.id)!;
    expect(c.snapshot()).toEqual([...leaves, last]);
    expect(c.snapshot().every((m) => m.spec.kind !== 'custom')).toBe(true);
    expect(owned(c.snapshot())).toEqual(cells);
    expect(s.canvas.holes()).toEqual(holes); expect(s.canvas.cells()).toEqual(blocks);
    // Only a group can be ungrouped, and the refusal changes nothing.
    const snapshot = c.snapshot();
    expect(c.ungroup(leaves[0]!.id)).toBeNull();
    expect(c.lastError).toBe('Select a combined component to ungroup it.');
    expect(c.snapshot()).toBe(snapshot);
  });
});

describe('reopening a mine layout', () => {
  const browser = () => {
    const data = new Map<string, string>();
    return { data, storage: { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); } } };
  };
  /** Saves as the page does: the request, then `rememberDesign`. Returns the layout the bot was sent. */
  async function saveAs(s: Studio, c: MineModuleController, rev: number): Promise<string> {
    const save = vi.spyOn(api, 'save').mockResolvedValue({ designId: 111, rev, name: 'saved', blocks: s.canvas.size, created: false, text: 'saved' });
    try {
      await s.save('saved');
      c.rememberDesign(111, rev);
      return save.mock.calls[0]![1].mineAuthoring!;
    } finally { save.mockRestore(); }
  }
  // The session as the bot returns it: its canvas, at the canvas rev the page last heard of.
  const design = (rev: number, mineAuthoring: string, s: Studio) =>
    ({ designId: 111, designRevision: rev, mineAuthoring, rev: s.rev, blocks: s.canvas.cells(), dug: s.canvas.holes() });

  it('a saved session keeps a combine made after the save across a reload', async () => {
    const { storage } = browser(), { s, c } = make(storage);
    const a = c.add(tunnel(), at)!, b = c.add(tunnel(), [30, 20, 40])!;
    const saved = await saveAs(s, c, 4);
    const group = c.combine([a.id, b.id])!;
    const reloaded = make(storage, design(4, saved, s));
    expect(reloaded.c.snapshot()).toEqual([group]);
    expect(reloaded.s.dirty).toBe(true); expect(reloaded.c.recoveredLocal).toBe(true);
    expect(reloaded.c.restoreNote).toBe('Recovered unsaved component changes from this browser. Save the design to keep them.');
    // Saved again, the next reload opens clean and has nothing to say.
    const again = await saveAs(reloaded.s, reloaded.c, 5);
    expect(reloaded.s.dirty).toBe(false);
    const settled = make(storage, design(5, again, s));
    expect(settled.c.snapshot()).toEqual([group]);
    expect(settled.s.dirty).toBe(false); expect(settled.c.recoveredLocal).toBe(false); expect(settled.c.restoreNote).toBeNull();
  });
  it('an ungrouped group is saved and reopens as its parts, with the same cells', async () => {
    const { s, c } = make();
    const a = c.add({ ...tunnel(), torchEvery: 3 }, at)!, b = c.add({ ...tunnel(), rotation: 1 }, [30, 20, 40])!;
    const group = c.combine([a.id, b.id])!;
    expect(c.update(group.id, { ...group.spec, rotation: 2 })).toBe(true);
    const parts = c.ungroup(group.id)!;
    const saved = await saveAs(s, c, 4);
    const reopened = make(null, { ...design(4, saved, s), id: 100 });
    expect(reopened.c.snapshot()).toEqual(parts);
    expect(reopened.s.canvas.holes()).toEqual(s.canvas.holes()); expect(reopened.s.canvas.cells()).toEqual(s.canvas.cells());
    expect(reopened.s.dirty).toBe(false); expect(reopened.c.restoreNote).toBeNull();
  });
  it('…and a move made after the save, which the saved layout alone can no longer open whole', async () => {
    const { storage } = browser(), { s, c } = make(storage);
    const a = c.add(tunnel(), at)!, b = c.add(tunnel(), [30, 20, 40])!;
    const saved = await saveAs(s, c, 4);
    expect(c.move(a.id, [30, 20, 35])).toBe(true);
    const reloaded = make(storage, design(4, saved, s));
    expect(reloaded.c.snapshot()).toEqual([{ ...a, at: [30, 20, 35] }, b]);
    expect(reloaded.s.dirty).toBe(true); expect(reloaded.c.recoveredLocal).toBe(true);
    // A browser without the copy opens what the saved layout still has right: the piece that did not move.
    const elsewhere = make(null, design(4, saved, s));
    expect(elsewhere.c.snapshot()).toEqual([b]);
    expect(elsewhere.s.dirty).toBe(false);
  });
  it('a server envelope saved elsewhere wins over a stale working copy', async () => {
    const { storage } = browser(), here = make(storage);
    const a = here.c.add(tunnel(), at)!, b = here.c.add(tunnel(), [30, 20, 40])!;
    const first = await saveAs(here.s, here.c, 4);
    // Another browser opens the session, combines the two and saves. This browser's copy still continues from the first save.
    const there = make(null, design(4, first, here.s));
    const group = there.c.combine([a.id, b.id])!;
    const second = await saveAs(there.s, there.c, 5);
    const back = make(storage, design(5, second, here.s));
    expect(back.c.snapshot()).toEqual([group]);
    expect(back.s.dirty).toBe(false); expect(back.c.recoveredLocal).toBe(false); expect(back.c.restoreNote).toBeNull();
  });
  it('one mismatching component is dropped, the others are restored, the canvas is unchanged, the note says so', async () => {
    const { s, c } = make();
    const a = c.add(tunnel(), at)!, b = c.add(tunnel(), [30, 20, 40])!, d = c.add(tunnel(), [30, 20, 50])!;
    const saved = await saveAs(s, c, 4);
    // The bot's canvas lost one cell of the second tunnel.
    const dug = s.canvas.holes().filter((p) => p.join() !== b.at.join());
    const reopened = make(null, { ...design(4, saved, s), id: 100, dug });
    expect(reopened.c.snapshot()).toEqual([a, d]);
    expect(reopened.s.canvas.holes()).toEqual(dug);
    expect(reopened.c.restoreNote).toBe('1 of 3 saved components no longer matches the excavation and was opened as plain cells. The saved revision still has it.');
    expect(reopened.s.dirty).toBe(false); expect(reopened.c.recoveredLocal).toBe(false);
    // What it dug is plain cells now: the others leave without touching them.
    expect(reopened.c.atCell([31, 20, 40])).toBeNull();
    reopened.c.remove(a.id); reopened.c.remove(d.id);
    expect(reopened.s.canvas.holes()).toEqual(dug.filter((p) => p[2] === 40));
    expect(reopened.s.canvas.dug.size).toBe(13);
  });
  it('a component that only matched under another one goes with it, and one that stands apart stays', async () => {
    const { s, c } = make();
    // The second tunnel crosses the first where its torch hangs and digs the torch away.
    const lit = c.add({ ...tunnel(), torchEvery: 4 }, at)!, cross = c.add({ ...tunnel(), rotation: 1 }, at)!, apart = c.add(tunnel(), [30, 20, 50])!;
    expect(moduleCells(lit.spec, lit.at).cells).toContainEqual([30, 21, 30, 50, 3]);
    expect(s.canvas.authoredState(keyOf(30, 21, 30))).toBe(0);
    const saved = await saveAs(s, c, 4);
    const whole = make(null, { ...design(4, saved, s), id: 100 });
    expect(whole.c.snapshot()).toEqual([lit, cross, apart]); expect(whole.c.restoreNote).toBeNull();
    // Without one cell of the crossing tunnel, far from the torch, the first tunnel's torch should hang where the canvas has air.
    const dug = s.canvas.holes().filter((p) => p.join() !== '30,20,27');
    const reopened = make(null, { ...design(4, saved, s), id: 101, dug });
    expect(reopened.c.snapshot()).toEqual([apart]);
    expect(reopened.s.canvas.holes()).toEqual(dug);
    expect(reopened.c.restoreNote).toBe('2 of 3 saved components no longer match the excavation and were opened as plain cells. The saved revision still has them.');
  });
  it('a component that would stand off the plot is left out, and the rest opens', async () => {
    const { s, c } = make(), a = c.add(tunnel(), at)!;
    c.add(tunnel(), [30, 20, 40]);
    const saved = JSON.parse(await saveAs(s, c, 4)) as { modules: Array<{ at: V3 }> };
    saved.modules[1]!.at = [254, 20, 40];
    const reopened = make(null, { ...design(4, JSON.stringify(saved), s), id: 100 });
    expect(reopened.c.snapshot()).toEqual([a]);
    expect(reopened.c.restoreNote).toBe('1 of 2 saved components no longer matches the excavation and was opened as plain cells. The saved revision still has it.');
    expect(reopened.s.canvas.dug.size).toBe(28);
  });
  it('a working copy that lost one component to an unsent edit still beats the older saved layout', async () => {
    const { storage } = browser(), { s, c } = make(storage), a = c.add(tunnel(), at)!;
    const saved = await saveAs(s, c, 4);
    const b = c.add(tunnel(), [30, 20, 40])!;
    c.add(tunnel(), [30, 20, 50]);
    // The page went away before the third tunnel's cells were sent: the bot's canvas holds the first two.
    const reloaded = make(storage, { ...design(4, saved, s), dug: s.canvas.holes().filter((p) => p[2] !== 50) });
    expect(reloaded.c.snapshot()).toEqual([a, b]);
    expect(reloaded.s.dirty).toBe(true);
    expect(reloaded.c.restoreNote).toBe('Recovered unsaved component changes from this browser. Save the design to keep them. '
      + '1 of 3 components no longer matches the excavation and was opened as plain cells.');
  });
  it('an empty copy of some other layout does not hide what the saved layout still has right', async () => {
    const { data, storage } = browser(), { s, c } = make(storage), a = c.add(tunnel(), at)!;
    const b = c.add(tunnel(), [30, 20, 40])!;
    const saved = await saveAs(s, c, 4);
    data.set('mineclaw.mine-modules.v2:test:9', JSON.stringify({ size: 256, modules: [], baseDefault: -1, base: [], overrides: [], descends: 'other' }));
    const reloaded = make(storage, { ...design(4, saved, s), dug: s.canvas.holes().filter((p) => p.join() !== b.at.join()) });
    expect(reloaded.c.snapshot()).toEqual([a]);
    expect(reloaded.s.dirty).toBe(false); expect(reloaded.c.recoveredLocal).toBe(false);
  });
  it('a seed or version change keeps the working copy', () => {
    const { storage } = browser(), { s, c } = make(storage);
    const group = c.combine([c.add(tunnel(), at)!.id, c.add(tunnel(), [30, 20, 40])!.id])!;
    const canvas = { blocks: s.canvas.cells(), dug: s.canvas.holes() };
    for (const changed of [{ seed: '777' }, { version: 'b1.8.1' }]) {
      const reopened = make(storage, { ...canvas, ...changed });
      expect(reopened.c.snapshot(), JSON.stringify(changed)).toEqual([group]);
      expect(reopened.s.dirty).toBe(true);
    }
  });
  it('still opens a working copy written before copies named what they continue from', () => {
    const { data, storage } = browser(), first = make(storage), a = first.c.add(tunnel(), at)!;
    const { descends, ...envelope } = JSON.parse([...data.values()][0]!) as { descends: string };
    expect(descends).toBe('');
    // The key such a copy had: the session with its version and seed.
    const old = new Map([['mineclaw.mine-modules.v1:test:9:1.12.2:12345', JSON.stringify(envelope)]]);
    const { s, c } = make({ getItem: (k) => old.get(k) ?? null, setItem: (k, v) => { old.set(k, v); } }, { dug: first.s.canvas.holes() });
    expect(c.snapshot()).toEqual([a]);
    expect(c.recoveredLocal).toBe(true); expect(s.dirty).toBe(true);
    c.setVisible(a.id, false);
    expect(JSON.parse(old.get('mineclaw.mine-modules.v2:test:9')!)).toMatchObject({ descends: '', modules: [{ id: a.id, visible: false }] });
  });
  it('a stale copy on a cleared canvas opens nothing and says nothing', () => {
    const { storage } = browser(), first = make(storage);
    first.c.add(tunnel(), at);
    const { s, c } = make(storage);
    expect(c.snapshot()).toHaveLength(0);
    expect(c.restoreNote).toBeNull(); expect(c.recoveredLocal).toBe(false); expect(s.dirty).toBe(false);
  });
  it('keeps the newest 20 session copies per owner, always this session’s and every revision copy', () => {
    const data = new Map<string, string>();
    const storage = {
      getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); },
      removeItem: (k: string) => { data.delete(k); }, key: (i: number) => [...data.keys()][i] ?? null,
      get length() { return data.size; },
    };
    const copy = JSON.stringify({ size: 256, modules: [], baseDefault: -1, base: [], overrides: [] });
    for (let id = 1; id <= 30; id++) if (id !== 9) data.set(`mineclaw.mine-modules.v2:test:${id}`, copy);
    for (let id = 1; id <= 25; id++) data.set(`mineclaw.mine-modules.v2:robin:${id}`, copy);
    // This session's only copy is an old-style one, older than every other: it is what this page opens from.
    data.set('mineclaw.mine-modules.v1:test:9:1.12.2:12345', copy);
    for (let rev = 1; rev <= 8; rev++) data.set(`mineclaw.mine-modules.design.v1:test:1.12.2:12345:12:${rev}`, copy);
    for (let rev = 1; rev <= 3; rev++) data.set(`mineclaw.mine-modules.design.v1:test:1.12.2:12345:13:${rev}`, copy);
    data.set('something.else', 'kept');
    const { c } = make(storage);
    const left = (pattern: RegExp) => [...data.keys()].filter((k) => pattern.test(k)).map((k) => Number(k.split(':').at(-1))).sort((a, b) => a - b);
    expect(left(/^mineclaw\.mine-modules\.v2:test:/)).toEqual(Array.from({ length: 20 }, (_, i) => i + 11));
    expect(left(/^mineclaw\.mine-modules\.v2:robin:/)).toEqual(Array.from({ length: 20 }, (_, i) => i + 6));
    expect(data.has('mineclaw.mine-modules.v1:test:9:1.12.2:12345')).toBe(true);
    // A design saved before the bot kept layouts has them only here: no revision copy is let go.
    expect(left(/:12:\d+$/)).toEqual([1, 2, 3, 4, 5, 6, 7, 8]);
    expect(left(/:13:\d+$/)).toEqual([1, 2, 3]);
    expect(data.get('something.else')).toBe('kept');
    c.rememberDesign(12, 9);
    expect(left(/:12:\d+$/)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9]);
    expect(data.has('mineclaw.mine-modules.v2:test:9')).toBe(true);
  });
  it('keeps the working copy of a session "Open in studio" offers to resume, however old', () => {
    const data = new Map<string, string>();
    const storage = {
      getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => { data.set(k, v); },
      removeItem: (k: string) => { data.delete(k); }, key: (i: number) => [...data.keys()][i] ?? null,
      get length() { return data.size; },
    };
    const copy = JSON.stringify({ size: 256, modules: [], baseDefault: -1, base: [], overrides: [] });
    for (let id = 1; id <= 30; id++) if (id !== 9) data.set(`mineclaw.mine-modules.v2:test:${id}`, copy);
    // Session 3 was left with unsaved work on design 12; robin's session 4 on design 13 does not cover test's session 4.
    data.set('mineclaw.studio.unsaved:test:12', '3');
    data.set('mineclaw.studio.unsaved:robin:13', '4');
    make(storage);
    const left = [...data.keys()].filter((k) => k.startsWith('mineclaw.mine-modules.v2:test:')).map((k) => Number(k.split(':').at(-1)));
    expect(left.sort((a, b) => a - b)).toEqual([3, ...Array.from({ length: 20 }, (_, i) => i + 11)]);
  });
  it('a controller created without attaching changes nothing until attached', async () => {
    const { data, storage } = browser(), first = make(storage), a = first.c.add(tunnel(), at)!;
    // A development build has React build two controllers for one studio and keep the first.
    const s = new Studio({ ...first.s.session, dug: first.s.canvas.holes() }, api, 60000);
    const kept = new MineModuleController(s, storage), spare = new MineModuleController(s, storage, { attach: false });
    sessions.push(s); controllers.push(kept, spare);
    const b = kept.add(tunnel(), [30, 20, 40])!;
    s.edit([[31, 20, 30, 50, 5]], 'place', 'manual torch');
    const copy = () => JSON.parse([...data.values()][0]!) as { modules: Array<{ id: string }>; overrides: number[][] };
    expect(spare.snapshot()).toEqual([a]);
    expect(copy().modules.map((m) => m.id)).toEqual([a.id, b.id]);
    expect(copy().overrides).toEqual([[keyOf(31, 20, 30), 50 * 16 + 5]]);
    expect((JSON.parse(await saveAs(s, kept, 4)) as { modules: unknown[] }).modules).toHaveLength(2);
    // Attached, it follows the canvas like any other and writes what it holds; detached, it is silent again.
    const detach = spare.attach();
    s.edit([[32, 20, 30, 50, 5]], 'place', 'another torch');
    expect(copy().modules.map((m) => m.id)).toEqual([a.id]);
    detach();
    s.edit([[33, 20, 30, 50, 5]], 'place', 'a third torch');
    expect(copy().modules.map((m) => m.id)).toEqual([a.id, b.id]);
    expect(copy().overrides).toHaveLength(3);
  });
});
