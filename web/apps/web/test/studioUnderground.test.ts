/**
 * The Design Studio below the grass and its unsaved-work guard: the canvas's ground (earth until dug, dug cells, filling back,
 * undo), the tools that dig only in underground mode, transforms carrying dug cells, the underground toggle and level cut,
 * the dirty-state rules and the navigation guard's decisions.
 */
import type { AimTarget } from '@mineclaw/world-viewer';
import type { DesignerSession } from '@mineclaw/contracts';
import { MOCK_DESIGNER_PALETTE, StoryBuildItem } from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Canvas, DEPTH, EARTH, inside, keyOf, posOf, selected } from '@/features/studio/canvas';
import { Studio, type StudioApi } from '@/features/studio/studio';
import {
  aimHit,
  brush,
  erase,
  move,
  pasteCells,
  copy,
  toggleUnder,
  underCut,
  UNDER_DEFAULT,
} from '@/features/studio/tools';
import { designCells, designItems, placeDesign } from '@/features/story/tools';
import {
  addGuard,
  blockingGuard,
  guardedHref,
  guardedNavigate,
  pendingNavigation,
  resolveNavigation,
} from '@/lib/navGuard';

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
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  ...over,
});

const aim = (x: number, y: number, z: number, face = 1): AimTarget =>
  ({ kind: 'block', x, y, z, face, id: 2, name: 'grass' }) as unknown as AimTarget;

describe('the ground under the canvas', () => {
  it('reaches 16 layers down; earth until dug; air digs, EARTH fills back; undo of a dig fills it back', () => {
    expect(DEPTH).toBe(16);
    expect(inside(0, -16, 0)).toBe(true);
    expect(inside(0, -17, 0)).toBe(false);
    expect(posOf(keyOf(5, -16, 7))).toEqual([5, -16, 7]);
    const c = new Canvas();
    expect(c.get(5, -1, 5)).toEqual([EARTH, 0]);
    expect(c.get(5, 0, 5)).toEqual([0, 0]);
    const dig = c.apply([
      [5, -1, 5, 0, 0],
      [5, 2, 5, 0, 0],
    ]);
    expect(dig.forward).toEqual([[5, -1, 5, 0, 0]]); // air above the grass changes nothing
    expect(dig.backward).toEqual([[5, -1, 5, EARTH, 0]]);
    expect(c.earth(5, -1, 5)).toBe(false);
    c.apply([[5, -2, 5, 4, 0]]);
    expect(c.dug.has(keyOf(5, -2, 5))).toBe(false);
    c.apply([[5, -2, 5, 0, 0]]); // removing a block underground leaves it dug
    expect(c.get(5, -2, 5)).toEqual([0, 0]);
    c.apply(dig.backward);
    expect(c.earth(5, -1, 5)).toBe(true);
    expect(c.bounds()).toEqual({ min: [5, -2, 5], max: [5, -2, 5] });
    const d = new Canvas();
    d.load(
      [[1, -3, 1, 4, 0]],
      [
        [1, -2, 1],
        [1, 5, 1],
      ],
    );
    expect(d.holes()).toEqual([[1, -2, 1]]); // dug only below the grass
    expect(d.size).toBe(1);
  });

  it('tools dig earth only in underground mode; a box erase digs its ground; moves carry the dug cells', () => {
    const c = new Canvas();
    expect(brush(c, [3, -1, 3], 2, 'cube', 'remove', 4, 0)).toEqual([]); // the grassland stays as it is
    expect(brush(c, [3, -1, 3], 1, 'cube', 'remove', 4, 0, undefined, true)).toEqual([[3, -1, 3, 0, 0]]);
    expect(brush(c, [3, -1, 3], 1, 'cube', 'paint', 4, 0)).toEqual([]);
    const box = { min: [2, -2, 2] as [number, number, number], max: [3, 0, 3] as [number, number, number] };
    expect(erase(c, box)).toEqual([]);
    c.apply(erase(c, box, true));
    expect(c.dug.size).toBe(8); // y -2..-1 of a 2×2
    c.apply([[2, -2, 2, 4, 0]]);
    // Moving the box 4 east: its block and dug cells go along, the ground they leave is filled back.
    const m = move(c, box, [4, 0, 0]);
    c.apply(m.cells);
    expect(c.get(6, -2, 2)).toEqual([4, 0]);
    expect(c.dug.has(keyOf(7, -1, 3))).toBe(true);
    expect(c.earth(2, -1, 2)).toBe(true);
    expect(c.earth(2, -2, 2)).toBe(true);
    expect(selected(c, { min: [6, -2, 2], max: [7, -1, 3] }, true)).toHaveLength(8);
    // A copied hole pastes below the grass only.
    const clip = copy(c, { min: [6, -2, 2], max: [7, -1, 3] })!;
    expect(pasteCells(clip, [20, -2, 20]).filter((x) => x[3] === 0)).toHaveLength(7);
    expect(pasteCells(clip, [20, 3, 20]).filter((x) => x[3] === 0)).toHaveLength(0);
  });

  it('the aim treats undisturbed ground as the ground unless underground; the toggle and its level cut', () => {
    const c = new Canvas();
    expect(aimHit(aim(10, 63, 10), c, false)).toEqual({ hit: null, place: [10, 0, 10], face: 1 });
    expect(aimHit(aim(10, 63, 10), c, true)!.hit).toEqual([10, -1, 10]);
    c.apply([[10, -3, 10, 4, 0]]);
    expect(aimHit(aim(10, 61, 10), c, false)!.hit).toEqual([10, -3, 10]); // a block down there is always a block
    expect(toggleUnder(null)).toBe(UNDER_DEFAULT);
    expect(toggleUnder(-5)).toBeNull();
    expect(underCut(null)).toBeNull();
    expect(underCut(-1)).toEqual({ y: 64 }); // everything from the grass's top up is cut away
    expect(underCut(-5)).toEqual({ y: 60 });
    expect(underCut(-40)).toEqual({ y: 49 }); // clamped to the deepest layer
  });
});

describe('unsaved changes', () => {
  const api = (): StudioApi => ({
    canvas: vi.fn(() => Promise.resolve({ rev: 1, applied: 1 })),
    message: vi.fn(),
    save: vi.fn(() => Promise.resolve({ designId: 5, rev: 1, name: 'hut', blocks: 1, created: true, text: 'saved' })),
  });

  it('an untouched design is clean; edits, digs, AI ops and clears are dirty; undo back to the saved state is clean', async () => {
    const s = new Studio(session({ blocks: [[1, 0, 1, 4, 0]] }), api(), 60_000);
    expect(s.snapshot().dirty).toBe(false);
    s.edit([[2, 0, 2, 5, 0]], 'place', 'placed');
    expect(s.snapshot().dirty).toBe(true);
    s.undo();
    expect(s.snapshot().dirty).toBe(false); // back where it was saved
    s.redo();
    expect(s.dirty).toBe(true);
    s.undo();
    s.edit([[3, -1, 3, 0, 0]], 'remove', 'dug');
    expect(s.dirty).toBe(true);
    s.edit([[3, -1, 3, EARTH, 0]], 'fill', 'filled');
    expect(s.dirty).toBe(false);
    s.applyAi([{ kind: 'fill', label: 'floor', by: 'ai', cells: [[9, 0, 9, 4, 0]] }]);
    expect(s.dirty).toBe(true);
    await s.save('hut');
    expect(s.dirty).toBe(false);
    s.edit([[1, 0, 1, 0, 0]], 'remove', 'cleared'); // removing a saved block
    expect(s.dirty).toBe(true);
    s.dispose();
  });

  it('a change made while the save is on its way stays unsaved', async () => {
    let done: () => void = () => undefined;
    const a = api();
    const save = vi.fn(
      () =>
        new Promise<Awaited<ReturnType<StudioApi['save']>>>((r) => {
          done = () => r({ designId: 5, rev: 1, name: 'hut', blocks: 1, created: true, text: 'saved' });
        }),
    );
    a.save = save;
    const s = new Studio(session(), a, 60_000);
    s.edit([[2, 0, 2, 5, 0]], 'place', 'placed');
    const p = s.save('hut');
    await vi.waitFor(() => expect(save).toHaveBeenCalled());
    s.edit([[4, 0, 4, 5, 0]], 'place', 'placed');
    done();
    await p;
    expect(s.dirty).toBe(true);
    s.undo();
    expect(s.dirty).toBe(false);
    s.dispose();
  });
});

describe('navigation guard', () => {
  const ev = (over: Partial<MouseEvent> = {}) => ({
    button: 0,
    metaKey: false,
    ctrlKey: false,
    shiftKey: false,
    altKey: false,
    defaultPrevented: false,
    ...over,
  });
  const a = (href: string, target = '', download = false) => ({
    href,
    target,
    hasAttribute: (n: string) => n === 'download' && download,
  });
  const here = { origin: 'https://x.test', pathname: '/designs/studio' };
  let off: Array<() => void> = [];
  afterEach(() => {
    for (const f of off) f();
    off = [];
    void resolveNavigation('stay');
  });

  it('asks about plain clicks on links to other pages of the app only', () => {
    expect(guardedHref(ev(), a('https://x.test/live'), here)).toBe('/live');
    expect(guardedHref(ev(), a('/story?colony=1'), here)).toBe('/story?colony=1');
    expect(guardedHref(ev(), a('/designs/studio?session=4'), here)).toBeNull(); // the same page
    expect(guardedHref(ev({ ctrlKey: true }), a('/live'), here)).toBeNull(); // a new tab keeps this one
    expect(guardedHref(ev({ button: 1 }), a('/live'), here)).toBeNull();
    expect(guardedHref(ev(), a('/live', '_blank'), here)).toBeNull();
    expect(guardedHref(ev(), a('/x.png', '', true), here)).toBeNull();
    expect(guardedHref(ev(), a('https://other.test/'), here)).toBeNull(); // the browser's own prompt covers it
    expect(guardedHref(ev({ defaultPrevented: true }), a('/live'), here)).toBeNull();
    expect(guardedHref(ev(), null, here)).toBeNull();
  });

  it('navigation is free while nothing is dirty, held while something is; leave, save and stay', async () => {
    let dirty = false;
    const save = vi.fn(() => Promise.resolve(true));
    off.push(addGuard({ dirty: () => dirty, save }));
    const go = vi.fn();
    expect(guardedNavigate(go)).toBe(true);
    expect(go).toHaveBeenCalledTimes(1);
    dirty = true;
    expect(blockingGuard()).not.toBeNull();
    expect(guardedNavigate(go)).toBe(false);
    expect(pendingNavigation()).not.toBeNull();
    expect(await resolveNavigation('stay')).toBe(false);
    expect(go).toHaveBeenCalledTimes(1);
    guardedNavigate(go);
    expect(await resolveNavigation('leave')).toBe(true);
    expect(go).toHaveBeenCalledTimes(2);
    guardedNavigate(go);
    expect(await resolveNavigation('save')).toBe(true);
    expect(save).toHaveBeenCalledTimes(1);
    expect(go).toHaveBeenCalledTimes(3);
    // A failed save keeps the question open.
    save.mockResolvedValueOnce(false);
    guardedNavigate(go);
    expect(await resolveNavigation('save')).toBe(false);
    expect(pendingNavigation()).not.toBeNull();
    expect(go).toHaveBeenCalledTimes(3);
  });
});

describe('a placed design with a basement (Story Mode)', () => {
  it('keeps the studio ground on the clicked level and queues the dig first in one group', () => {
    const cells = designCells({
      blocks: [
        [30, 0, 30, 5, 0],
        [30, -3, 30, 4, 0],
      ],
      dug: [
        [30, -2, 30],
        [30, -1, 30],
      ],
    });
    expect(cells).toEqual([
      [0, 0, 0, 5, 0],
      [0, -3, 0, 4, 0],
      [0, -2, 0, 0, 0],
      [0, -1, 0, 0, 0],
    ]);
    // A design that doesn't dig stands its lowest layer on the clicked level, as before.
    expect(designCells({ blocks: [[30, 2, 30, 5, 0]] })).toEqual([[0, 0, 0, 5, 0]]);
    const items = designItems(placeDesign(cells, [100, 64, 100], 'n'), 'cellar', 'g1');
    expect(items.map((i) => i.kind)).toEqual(['remove', 'block', 'block']);
    expect(items[0]!.blocks).toEqual([
      [100, 62, 100],
      [100, 63, 100],
    ]);
    expect(items.every((i) => i.group === 'g1')).toBe(true);
    for (const i of items) expect(StoryBuildItem.safeParse(i).success).toBe(true);
    expect(designItems(placeDesign([[0, 0, 0, 5, 0]], [1, 64, 1], 'n'))[0]!.group).toBeUndefined();
  });
});
