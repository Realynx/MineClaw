/**
 * A released drag on the map. The release guard (tools.ts `ReleaseGuard`): a release waiting for the worker still acts
 * after a new press (quick clicks each send their command), while Esc, the other button and a tool, view, level or build
 * change cancel it, and it stays drawn until it acted. The requests (mode/release.ts): Cut and Harvest send exactly the
 * previewed cells, Chop and the entity tools keep the area, and every add leaves the targets out of the answer. What a
 * command draws at once (pending.ts) is added before its request and settled by the colony's outcome (`runAction`).
 */
import { StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { runAction, type RunResult, type Story } from '@/features/story/mode/commands';
import { releaseTool } from '@/features/story/mode/release';
import { BUSY_TEXT } from '@/features/story/optimistic';
import { PendingCommands, treeTops } from '@/features/story/pending';
import type { DragResult } from '@/features/story/StoryMap';
import { ReleaseGuard } from '@/features/story/tools';

type V3 = [number, number, number];

describe('ReleaseGuard', () => {
  it('a new press of the same button keeps a waiting release: three quick clicks all act', () => {
    const g = new ReleaseGuard<string>();
    const acted: number[] = [];
    const tickets = [1, 2, 3].map(() => {
      const t = g.begin(0, null);
      expect(g.press(0)).toBe(false); // the next click goes down while this one waits for its scan
      return t;
    });
    for (const t of tickets) if (g.current(t)) acted.push(t);
    expect(acted).toEqual(tickets);
  });

  it('a middle press (panning) never cancels; the other button does', () => {
    const g = new ReleaseGuard();
    const t = g.begin(0);
    expect(g.press(1)).toBe(false);
    expect(g.current(t)).toBe(true);
    expect(g.press(2)).toBe(true);
    expect(g.current(t)).toBe(false);
    expect(g.waiting).toBe(false);
    // A right release waiting is cancelled by a left press.
    const r = g.begin(2);
    expect(g.press(0)).toBe(true);
    expect(g.current(r)).toBe(false);
  });

  it('Esc, leaving full screen and a tool, view, level or build change cancel every waiting release', () => {
    const g = new ReleaseGuard();
    const a = g.begin(0);
    const b = g.begin(0);
    expect(g.waiting).toBe(true);
    expect(g.cancel()).toBe(true);
    expect(g.current(a)).toBe(false);
    expect(g.current(b)).toBe(false);
    expect(g.cancel()).toBe(false); // nothing was waiting: Esc does its usual things
  });

  it('a waiting release stays drawn until it acted or was cancelled', () => {
    const g = new ReleaseGuard<{ rect: string }>();
    const a = g.begin(0, { rect: 'a' });
    g.begin(0, null); // a click or the select tool draws nothing
    const c = g.begin(0, { rect: 'c' });
    expect(g.shown()).toEqual([{ rect: 'a' }, { rect: 'c' }]);
    g.finish(a);
    expect(g.shown()).toEqual([{ rect: 'c' }]);
    expect(g.current(c)).toBe(true);
    g.cancel();
    expect(g.shown()).toEqual([]);
  });
});

describe('releaseTool requests', () => {
  const story = (id: string) => {
    const run = vi.fn().mockResolvedValue({ ok: true, count: 1, rev: 3 });
    const c = {
      tool: { kind: 'designate', id },
      lv: 64,
      cutY: null,
      mineFilter: 'all',
      build: { height: 3 },
      canWrite: true,
      state: { designations: [] },
      run,
      setTool: vi.fn(),
      say: vi.fn(),
    } as unknown as Story;
    return { c, run };
  };
  const drag = (cells: V3[], extra: Partial<DragResult> = {}): DragResult => ({
    rect: [0, 0, 3, 3],
    a: [0, 64, 0],
    b: [3, 64, 3],
    cells,
    entities: [],
    count: cells.length,
    moved: true,
    ...extra,
  });
  const bodyOf = (run: ReturnType<typeof vi.fn>) => (run.mock.calls[0]![0] as { body: Record<string, unknown> }).body;
  const plants: V3[] = [
    [1, 64, 1],
    [2, 65, 2],
  ];

  it('Cut and Harvest send exactly the previewed cells, without targets in the answer', async () => {
    for (const id of ['cut', 'harvest']) {
      const { c, run } = story(id);
      await releaseTool(c, drag(plants), false);
      expect(bodyOf(run)).toEqual({ op: 'add', kind: id, blocks: plants, includeTargets: false });
    }
  });

  it('a truncated preview keeps the area; Chop keeps the area', async () => {
    const cut = story('cut');
    await releaseTool(cut.c, drag(plants, { truncated: true }), false);
    expect(bodyOf(cut.run)).toEqual({ op: 'add', kind: 'cut', area: [0, 56, 0, 3, 104, 3], includeTargets: false });
    const chop = story('chop');
    await releaseTool(chop.c, drag([[1, 70, 1]], { count: 1 }), false);
    expect(bodyOf(chop.run)).toEqual({ op: 'add', kind: 'chop', area: [0, 56, 0, 3, 104, 3], includeTargets: false });
  });

  it('Mine by its height cells leaves the targets out too; Cancel sends only its ids', async () => {
    const m = story('mine');
    const cells: V3[] = [[1, 63, 1]];
    await releaseTool(
      m.c,
      drag(cells, { height: { cells, columns: 1, top: 63, bottom: 63, layers: 1, tooMany: false } }),
      false,
    );
    expect(bodyOf(m.run)).toEqual({ op: 'add', kind: 'mine', blocks: cells, includeTargets: false });
    const cancel = story('cancel');
    await releaseTool(cancel.c, drag([], { count: 1 }), false);
    expect(bodyOf(cancel.run)).toEqual({ op: 'cancel', ids: [] });
  });
});

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const stateOf = (rev: number, rows: Record<string, unknown> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, ...rows });

describe('a released command is drawn at once (pending marks)', () => {
  const setup = (tool: Record<string, unknown>, extra: Record<string, unknown> = {}) => {
    const reply = deferred<RunResult>();
    const run = vi.fn(() => reply.promise);
    const pending = new PendingCommands();
    const say = vi.fn();
    const c = {
      tool,
      lv: 64,
      cutY: null,
      mineFilter: 'all',
      build: { height: 3, roof: 'flat', overhang: false, facing: 's', material: 'oak_planks' },
      stock: new Map(),
      canWrite: true,
      state: { designations: [], zones: [] },
      split: { pawns: [], ai: [] },
      run,
      pending,
      setTool: vi.fn(),
      say,
      ...extra,
    } as unknown as Story;
    return { c, run, reply, pending, say };
  };
  const drag = (cells: V3[], extra: Partial<DragResult> = {}): DragResult => ({
    rect: [0, 0, 3, 3],
    a: [0, 64, 0],
    b: [3, 64, 3],
    cells,
    entities: [],
    count: cells.length,
    moved: true,
    ...extra,
  });

  it('a Mine drag shows exactly its previewed cells and its toast before the answer; they go at the answer rev', async () => {
    const cells: V3[] = [
      [1, 63, 1],
      [2, 63, 1],
      [2, 62, 1],
    ];
    const { c, reply, pending, say, run } = setup({ kind: 'designate', id: 'mine' });
    const height = { cells, columns: 2, top: 63, bottom: 62, layers: 2, tooMany: false };
    const done = releaseTool(c, drag(cells, { height }), false);
    // Drawn and said before the request is answered.
    expect(run).toHaveBeenCalledTimes(1);
    expect(pending.view(undefined).marks).toEqual(cells.map((cell) => ({ cell, kind: 'mine' })));
    expect(say).toHaveBeenCalledWith('Mine: 3 blocks · colony: designated', undefined);
    reply.resolve({ ok: true, out: { ok: true, count: 3, rev: 7 } });
    await done;
    expect(pending.reconcile(stateOf(6), Date.now())).toEqual([]);
    expect(pending.view(undefined).marks).toHaveLength(3); // the state has not reached the answer yet
    pending.reconcile(stateOf(7), Date.now());
    expect(pending.size).toBe(0);
  });

  it('Chop draws one mark per tree, on its top log', () => {
    const logs: V3[] = [
      [0, 64, 0],
      [0, 65, 0],
      [1, 66, 0], // a branch over the next column: the same tree
      [5, 64, 5],
      [5, 65, 5],
    ];
    expect(treeTops(logs)).toEqual([
      [1, 66, 0],
      [5, 65, 5],
    ]);
    const { c, pending } = setup({ kind: 'designate', id: 'chop' });
    void releaseTool(c, drag(logs, { count: 2 }), false);
    expect(pending.view(undefined).marks).toEqual([
      { cell: [1, 66, 0], kind: 'chop' },
      { cell: [5, 65, 5], kind: 'chop' },
    ]);
  });

  it('a refused command takes its marks away at once and is never sent again', async () => {
    const { c, reply, pending, run } = setup({ kind: 'designate', id: 'cut' });
    const done = releaseTool(c, drag([[1, 64, 1]]), false);
    expect(pending.size).toBe(1);
    reply.resolve({ ok: false, error: new ApiError(503, 'busy', { code: 'busy' }) });
    await done;
    expect(pending.size).toBe(0);
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('a Cancel hides the designations it takes while pending; a refusal brings them back', async () => {
    const designations = [
      { id: 'd1', kind: 'mine', block: [1, 64, 1] },
      { id: 'far', kind: 'mine', block: [40, 64, 40] },
    ];
    const { c, reply, pending } = setup({ kind: 'designate', id: 'cancel' }, { state: { designations } });
    const done = releaseTool(c, drag([], { count: 1 }), false);
    expect([...pending.view(undefined).hidden]).toEqual(['d1']);
    reply.resolve({ ok: false, error: new ApiError(400, 'no', {}) });
    await done;
    expect(pending.view(undefined).hidden.size).toBe(0);
  });

  it('a zone paint is drawn as the colony will make it until the state reaches the answer', async () => {
    const answer = deferred<unknown>();
    const write = vi.fn(() => answer.promise);
    const { c, pending, run } = setup({ kind: 'zone', id: 'farm', op: 'add' }, { write });
    const done = releaseTool(c, drag([], { rect: [0, 0, 2, 1], count: 0 }), false);
    expect(run).not.toHaveBeenCalled();
    expect(write).toHaveBeenCalledWith('story.zones', expect.objectContaining({ op: 'add', kind: 'farm' }));
    const zones = pending.view([]).zones!;
    expect(zones).toHaveLength(1);
    expect(zones[0]).toMatchObject({ kind: 'farm' });
    answer.resolve({ ok: true, rev: 4 });
    await done;
    pending.reconcile(stateOf(4), Date.now());
    expect(pending.view([]).zones).toBeNull();
  });

  it('a structure shows its blocks as holograms at once', () => {
    const { c, pending } = setup({ kind: 'build', id: 'floor' });
    void releaseTool(c, drag([], { a: [0, 64, 0], b: [1, 64, 1] }), false);
    const holo = pending.view(undefined).holo;
    expect(holo).toHaveLength(4);
    expect(holo.every((h) => h.build === 'pending' && h.state === 'todo' && h.id > 0)).toBe(true);
    expect(holo.map((h) => `${h.x},${h.z}`).sort()).toEqual(['0,0', '0,1', '1,0', '1,1']);
  });

  it('nothing is drawn without the story role, and a Story without pending marks still sends', async () => {
    const ro = setup({ kind: 'designate', id: 'cut' }, { canWrite: false });
    void releaseTool(ro.c, drag([[1, 64, 1]]), false);
    expect(ro.pending.size).toBe(0);
    const bare = setup({ kind: 'designate', id: 'cut' }, { pending: undefined, split: undefined });
    const done = releaseTool(bare.c, drag([[1, 64, 1]]), false);
    bare.reply.resolve({ ok: true, out: undefined });
    await done;
    expect(bare.run).toHaveBeenCalledTimes(1);
    expect(bare.say).not.toHaveBeenCalled();
  });
});

describe('runAction (the colony part of an action)', () => {
  const deps = (write: ReturnType<typeof vi.fn>, canWrite = true) => {
    const say = vi.fn();
    return { d: { canWrite, split: { pawns: [], ai: [] }, write, say } as never, say };
  };
  const call = { route: 'story.designations', body: { op: 'add' } } as const;

  it('returns the answer and says it; a refusal returns the error and says why', async () => {
    const ok = deps(vi.fn().mockResolvedValue({ ok: true, count: 2, warnings: ['in a preserve'] }));
    const res = await runAction(ok.d, call, null, 'designated', () => 'Cut: 2 plants');
    expect(res).toEqual({ ok: true, out: { ok: true, count: 2, warnings: ['in a preserve'] } });
    expect(ok.say).toHaveBeenCalledWith('Cut: 2 plants · colony: designated · in a preserve', 'warn');
    const busy = new ApiError(503, 'busy', { code: 'busy' });
    const no = deps(vi.fn().mockRejectedValue(busy));
    expect(await runAction(no.d, call, null, 'designated')).toEqual({ ok: false, error: busy });
    expect(no.say).toHaveBeenCalledWith(BUSY_TEXT, 'warn');
  });

  it('without the story role nothing is sent', async () => {
    const write = vi.fn();
    const ro = deps(write, false);
    expect(await runAction(ro.d, call, null, 'designated')).toEqual({ ok: false, error: null });
    expect(write).not.toHaveBeenCalled();
  });
});
