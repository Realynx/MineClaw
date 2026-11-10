/**
 * The optimistic layer's pure parts (features/story/optimistic.ts): each patch makes the change as the bot does and keeps
 * the identity of everything it does not touch, `view` lays the shown changes over the server state by revision, and a
 * failed colony write is told apart as refused or of unknown outcome, with the page's wording.
 */
import { StoryColonyState, type StoryStorage } from '@mineclaw/contracts';
import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { warnOn } from '@/features/story/mode/actions';
import {
  ACK_KEEP_MS,
  BUSY_TEXT,
  OptimisticStore,
  patchBedOwner,
  patchBenchFuel,
  patchBill,
  patchBillRemove,
  patchBillsReorder,
  patchStorageSet,
  patchStorageStop,
  patchStorageUse,
  shows,
  storyWriteError,
} from '@/features/story/optimistic';

type V3 = [number, number, number];
const A: V3 = [1, 64, 1];
const A2: V3 = [2, 64, 1]; // A's other half
const B: V3 = [5, 64, 5];
const C: V3 = [9, 64, 9];
const BENCH: V3 = [0, 64, 4];
const OTHER_BENCH: V3 = [0, 64, 8];

const sign = { at: [1, 65, 1] as V3, lines: ['food', '', '', ''], allow: ['food'], deny: [] };

const stateOf = (rev: number, rows: Record<string, unknown> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, ...rows });

function colony(rev = 10): StoryColonyState {
  return stateOf(rev, {
    storages: [
      { chest: A, doubleWith: A2, priority: 'normal', filter: { allow: ['*'], deny: [] }, label: 'Main' },
      { chest: B, priority: 'critical', filter: { allow: ['food'], deny: [] }, signs: [sign] },
    ],
    workbenches: [
      {
        pos: BENCH,
        kind: 'furnace',
        fuel: 'auto',
        bills: [
          { id: 'b1', recipe: 'smelt:iron_ore', mode: 'count' },
          { id: 'b2', recipe: 'smelt:sand', mode: 'forever' },
          { id: 'b3', recipe: 'smelt:cobblestone', mode: 'count' },
        ],
      },
      { pos: OTHER_BENCH, kind: 'crafting_table', bills: [{ id: 'c1', recipe: 'craft:stick', mode: 'count' }] },
    ],
    beds: [
      { pos: [3, 64, 3], pawn: 'ann' },
      { pos: [4, 64, 3], pawn: null },
      { pos: [5, 64, 3], pawn: 'bob' },
    ],
  });
}

const storageAt = (s: StoryColonyState, c: V3) =>
  (s.storages ?? []).find((x) => x.chest.join() === c.join()) as StoryStorage;

afterEach(() => {
  vi.useRealTimers();
});

describe('storage patches', () => {
  it('a chest without signs takes the filter, priority and label; nothing else changes identity', () => {
    const s = colony();
    const next = patchStorageSet([A], {
      filter: { allow: ['ores'], deny: [] },
      priority: 'important',
      label: 'x'.repeat(50),
    })(s);
    expect(storageAt(next, A)).toMatchObject({
      filter: { allow: ['ores'], deny: [] },
      priority: 'important',
      label: 'x'.repeat(40),
    });
    expect(storageAt(next, A).pendingFilter ?? null).toBeNull();
    expect(storageAt(next, B)).toBe(storageAt(s, B));
    expect(next.workbenches).toBe(s.workbenches);
    expect(next.beds).toBe(s.beds);
    expect(next.designations).toBe(s.designations);
  });

  it('either half of a double chest addresses its storage, once', () => {
    const s = colony();
    const next = patchStorageSet([A, A2], { priority: 'low' })(s);
    expect(next.storages).toHaveLength(2);
    expect(storageAt(next, A).priority).toBe('low');
    expect(storageAt(next, A).filter).toBe(storageAt(s, A).filter);
  });

  it('a chest that signs control keeps its filter and priority and gains the requested ones', () => {
    const s = colony();
    const next = patchStorageSet([B], { filter: { allow: [], deny: [] }, priority: 'low' })(s);
    const row = storageAt(next, B);
    expect(row.filter).toEqual({ allow: ['food'], deny: [] });
    expect(row.priority).toBe('critical');
    expect(row.pendingFilter).toEqual({ allow: [], deny: [] });
    expect(row.pendingPriority).toBe('low');
  });

  it('a priority alone on a signed chest requests the filter it already has (or the one already requested)', () => {
    const s = colony();
    const first = patchStorageSet([B], { priority: 'preferred' })(s);
    expect(storageAt(first, B)).toMatchObject({
      pendingFilter: { allow: ['food'], deny: [] },
      pendingPriority: 'preferred',
    });
    const requested = patchStorageSet([B], { filter: { allow: ['ores'], deny: [] } })(s);
    const both = patchStorageSet([B], { priority: 'low' })(requested);
    expect(storageAt(both, B)).toMatchObject({ pendingFilter: { allow: ['ores'], deny: [] }, pendingPriority: 'low' });
  });

  it('"Use as storage" registers a new chest for every item at normal priority', () => {
    const s = colony();
    const next = patchStorageUse([C])(s);
    expect(next.storages).toHaveLength(3);
    expect(storageAt(next, C)).toMatchObject({
      chest: C,
      priority: 'normal',
      filter: { allow: ['*'], deny: [] },
      contents: [],
    });
    expect(storageAt(next, A)).toBe(storageAt(s, A));
  });

  it('"Stop using" removes a chest without signs and asks a signed one for an empty filter', () => {
    const s = colony();
    const next = patchStorageStop([A2, B])(s);
    expect(next.storages!.map((r) => r.chest)).toEqual([B]);
    expect(storageAt(next, B)).toMatchObject({
      filter: { allow: ['food'], deny: [] },
      pendingFilter: { allow: [], deny: [] },
    });
    expect(patchStorageStop([C])(s)).toBe(s);
  });
});

describe('bill, bench and bed patches', () => {
  it('a bill takes its fields; its bench is a new row, the other bench is not', () => {
    const s = colony();
    const next = patchBill('b2', { priority: 1, paused: true, fuel: 'coal' })(s);
    const [bench, other] = next.workbenches!;
    expect(bench!.bills[1]).toMatchObject({ id: 'b2', priority: 1, paused: true, fuel: 'coal' });
    expect(bench!.bills[0]).toBe(s.workbenches![0]!.bills[0]);
    expect(other).toBe(s.workbenches![1]);
    expect(next.storages).toBe(s.storages);
  });

  it('a removed bill leaves its bench', () => {
    const next = patchBillRemove('b1')(colony());
    expect(next.workbenches![0]!.bills.map((b) => b.id)).toEqual(['b2', 'b3']);
  });

  it('a reorder puts the named bills first, the rest after as they were', () => {
    const s = colony();
    const next = patchBillsReorder(BENCH, ['b3', 'b1'])(s);
    expect(next.workbenches![0]!.bills.map((b) => b.id)).toEqual(['b3', 'b1', 'b2']);
    expect(next.workbenches![1]).toBe(s.workbenches![1]);
    expect(patchBillsReorder(BENCH, ['b1', 'b2'])(s)).toBe(s);
  });

  it("a furnace's fuel", () => {
    const s = colony();
    const next = patchBenchFuel(BENCH, 'planks')(s);
    expect(next.workbenches![0]!.fuel).toBe('planks');
    expect(next.workbenches![1]).toBe(s.workbenches![1]);
    expect(patchBenchFuel(BENCH, 'auto')(s)).toBe(s);
  });

  it("a bed moving a pawn frees the pawn's other bed", () => {
    const s = colony();
    const next = patchBedOwner([4, 64, 3], 'ann')(s);
    expect(next.beds!.map((b) => b.pawn ?? null)).toEqual([null, 'ann', 'bob']);
    expect(next.beds![2]).toBe(s.beds![2]);
    const freed = patchBedOwner([5, 64, 3], null)(s);
    expect(freed.beds!.map((b) => b.pawn ?? null)).toEqual(['ann', null, null]);
    expect(patchBedOwner([3, 64, 3], 'ann')(s)).toBe(s);
  });

  it('a patch for something the state does not have returns the state itself', () => {
    const s = colony();
    expect(patchBill('nope', { paused: true })(s)).toBe(s);
    expect(patchBillRemove('nope')(s)).toBe(s);
    expect(patchBenchFuel([7, 7, 7], 'coal')(s)).toBe(s);
    expect(patchBedOwner([7, 7, 7], null)(s)).toBe(s);
  });
});

describe('shows (what decides an unknown outcome when a write brings no test of its own)', () => {
  it('is true only for a state that already has the change, as the colony would send it', () => {
    const s = colony();
    const patches = [
      patchStorageSet([A2], { priority: 'low', label: 'Ores' }),
      patchStorageSet([B], { priority: 'low' }), // signed: a requested priority
      patchStorageUse([C]),
      patchStorageStop([A]),
      patchBill('b2', { paused: true }),
      patchBillRemove('b1'),
      patchBillsReorder(BENCH, ['b3']),
      patchBenchFuel(BENCH, 'coal'),
      patchBedOwner([4, 64, 3], 'ann'),
    ];
    for (const p of patches) {
      expect(shows(p)(s)).toBe(false);
      // A fresh copy through the wire schema, not the patch's own objects.
      const sent = StoryColonyState.parse(JSON.parse(JSON.stringify(p(s))));
      expect(shows(p)(sent)).toBe(true);
    }
  });
});

describe('view', () => {
  const priority = (s: StoryColonyState | undefined) => storageAt(s!, A).priority;

  it('lays changes over the server state in order, and is the server state itself with none', () => {
    const store = new OptimisticStore(new QueryClient(), 'view-order');
    const server = colony();
    expect(store.view(server)).toBe(server);
    store.add('storage:a', patchStorageSet([A], { priority: 'low' }));
    store.add('storage:a', patchStorageSet([A], { priority: 'important' }));
    const shown = store.view(server);
    expect(priority(shown)).toBe('important');
    expect(store.view(server)).toBe(shown); // the same object while nothing moves
    expect(server.storages![0]!.priority).toBe('normal'); // the server's state is never written
  });

  it('an acked change applies while the revision is below its ackRev, again when the state steps back, and goes a minute after', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    const store = new OptimisticStore(new QueryClient(), 'view-ack');
    const e = store.add('storage:a', patchStorageSet([A], { priority: 'low' }));
    store.ack(e, 12);
    expect(e).toMatchObject({ status: 'acked', ackRev: 12 });
    expect(priority(store.view(colony(11)))).toBe('low');
    const caught = colony(12);
    expect(store.view(caught)).toBe(caught);
    // A whole read from a bot that restarted, or one that came back older: the change shows again.
    expect(priority(store.view(colony(9)))).toBe('low');
    vi.advanceTimersByTime(ACK_KEEP_MS);
    expect(store.list()).toEqual([]);
    const old = colony(9);
    expect(store.view(old)).toBe(old);
  });
});

describe('a failed colony write', () => {
  const err = (status: number, code?: string) => new ApiError(status, 'server text', { code });

  it.each([
    [400, undefined],
    [403, 'forbidden'],
    [404, undefined],
    [409, 'conflict'],
    [503, 'busy'],
    [0, 'offline'],
  ])('%i %s is refused: nothing changed', (status, code) => {
    expect(storyWriteError(err(status, code)).kind).toBe('refused');
  });

  it.each([
    [0, 'upstream_unavailable'],
    [502, 'upstream_error'],
    [503, 'upstream_unavailable'],
    [504, undefined],
  ])('%i %s has an unknown outcome', (status, code) => {
    expect(storyWriteError(err(status, code)).kind).toBe('unknown');
  });

  it('says the colony is busy for a 503 busy (not the renderer), and keeps the server text for a 400', () => {
    expect(storyWriteError(err(503, 'busy')).text).toBe(BUSY_TEXT);
    expect(storyWriteError(err(400)).text).toBe('server text');
    expect(storyWriteError(err(503, 'upstream_unavailable')).text).toBe('The bot is not reachable right now.');
    const say = vi.fn();
    warnOn({ say })(err(503, 'busy'));
    expect(say).toHaveBeenCalledWith('The colony is busy; the change was not saved.', 'warn');
  });
});
