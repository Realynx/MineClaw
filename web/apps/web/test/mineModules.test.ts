import { afterEach, describe, expect, it } from 'vitest';
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { DEPTH, keyOf, posOf, setPlotMode, type V3 } from '@/features/studio/canvas';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { MineModuleController } from '@/features/studio/mine/MineModuleController';
import { decodeModuleDrag, defaultModule, encodeModule, MINE_MODULES, moduleCells, modulePorts, snapModuleAt } from '@/features/studio/mine/modules';

const sessions: Studio[] = [], controllers: MineModuleController[] = [];
const api: StudioApi = { canvas: () => Promise.resolve({ rev: 1, applied: 1 }), message: () => Promise.reject(new Error('unused')), save: () => Promise.reject(new Error('unused')) };
function make(storage: Pick<Storage, 'getItem' | 'setItem'> | null = null, over: Partial<DesignerSession> = {}) {
  const s = new Studio({ id: 9, owner: 'test', model: 'main', designId: null, name: 'mine', rev: 0, blocks: [], anchor: null,
    messages: [], costUsd: 0, calls: 0, version: '1.12.2', palette: MOCK_DESIGNER_PALETTE, plot: { size: 64, height: 128, depth: 0 },
    revisions: [], kind: 'mine', seed: '12345', ...over }, api, 60000);
  const c = new MineModuleController(s, storage); sessions.push(s); controllers.push(c); return { s, c };
}
afterEach(() => { controllers.splice(0).forEach((c) => c.dispose()); sessions.splice(0).forEach((s) => s.dispose()); setPlotMode({ kind: 'build' }); });
const at: V3 = [30, 20, 30];
const tunnel = () => ({ ...defaultModule('tunnel'), length: 7 });

describe('composable mine geometry', () => {
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
});
