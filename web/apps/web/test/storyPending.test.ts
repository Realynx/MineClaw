/**
 * Map commands drawn at once (features/story/pending.ts): an op's marks, hidden designations, zone paint and holograms are
 * there the moment it is added, and they go when the colony state carries the command (its answer's `rev`, or the catch-up
 * after an answer without one), when the colony refused it, or when its outcome stayed unknown for 10 s. Time is passed in.
 */
import { StoryColonyState, type StoryZone } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { BUSY_TEXT, UNCONFIRMED_TEXT } from '@/features/story/optimistic';
import {
  holoOf,
  isPendingId,
  PENDING_HOLO_CAP,
  PENDING_LIMIT_MS,
  PendingCommands,
  serverIds,
  withPendingHolo,
  type PendingOp,
} from '@/features/story/pending';

type V3 = [number, number, number];
const T0 = 1_762_000_000_000;

const stateOf = (rev: number, rows: Record<string, unknown> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, ...rows });

const mine = (...cells: V3[]): PendingOp => ({ marks: cells.map((cell) => ({ cell, kind: 'mine' })) });
const zone = (id: string, x: number): StoryZone => ({ id, kind: 'farm', area: [x, 64, 0, x + 1, 64, 1] });
const holo = (x: number, id = 4) => ({ x, y: 64, z: 0, id, meta: 0, build: 'pending', state: 'todo' as const });

describe('PendingCommands', () => {
  it('draws an op the moment it is added, and its id never names a colony thing', () => {
    let changes = 0;
    const p = new PendingCommands(() => changes++);
    const id = p.add(mine([1, 64, 1], [2, 64, 1]), T0);
    expect(p.view(undefined).marks).toEqual([
      { cell: [1, 64, 1], kind: 'mine' },
      { cell: [2, 64, 1], kind: 'mine' },
    ]);
    expect(p.status(id)).toBe('sending');
    expect(changes).toBe(1);
    expect(isPendingId(id)).toBe(true);
    // A request built from what the page shows strips pending ids; colony ids pass.
    expect(serverIds(['d1', id, 7])).toEqual(['d1', 7]);
    expect(isPendingId('d1')).toBe(false);
  });

  it("an applied op goes once a state reaches its answer's rev, not before", () => {
    const p = new PendingCommands();
    const id = p.add(mine([1, 64, 1]), T0);
    expect(p.settle(id, { ok: true, out: { ok: true, count: 1, rev: 12 } }, T0 + 50)).toBeNull();
    expect(p.status(id)).toBe('applied');
    expect(p.reconcile(stateOf(11), T0 + 100)).toEqual([]);
    expect(p.view(undefined).marks).toHaveLength(1);
    expect(p.reconcile(stateOf(12), T0 + 200)).toEqual([]);
    expect(p.size).toBe(0);
    expect(p.view(undefined).marks).toEqual([]);
  });

  it('an answer whose rev the held state already reached drops the op at once', () => {
    const p = new PendingCommands();
    p.reconcile(stateOf(20), T0);
    const id = p.add(mine([1, 64, 1]), T0);
    p.settle(id, { ok: true, out: { rev: 19 } }, T0 + 10);
    expect(p.size).toBe(0);
  });

  it('without a rev it goes when the catch-up started after the answer has merged', async () => {
    let changes = 0;
    const p = new PendingCommands(() => changes++);
    const id = p.add(mine([1, 64, 1]), T0);
    let merged!: () => void;
    const caughtUp = new Promise<void>((r) => (merged = r));
    p.settle(id, { ok: true, out: { ok: true, count: 1 }, caughtUp }, T0 + 50);
    // A state that arrives meanwhile may predate the answer: it does not drop the op.
    p.reconcile(stateOf(30), T0 + 60);
    expect(p.size).toBe(1);
    const before = changes;
    merged();
    await caughtUp;
    await Promise.resolve();
    expect(p.size).toBe(0);
    expect(changes).toBe(before + 1);
  });

  it('without a rev or a catch-up it goes with the next state, and also after a failed catch-up', async () => {
    const p = new PendingCommands();
    const held = stateOf(5);
    p.reconcile(held, T0);
    const a = p.add(mine([1, 64, 1]), T0);
    p.settle(a, { ok: true, out: null }, T0 + 10);
    p.reconcile(held, T0 + 20); // the same state again: read before the answer
    expect(p.status(a)).toBe('applied');
    p.reconcile(stateOf(6), T0 + 30);
    expect(p.status(a)).toBeNull();

    const b = p.add(mine([2, 64, 1]), T0 + 40);
    const failed = Promise.reject(new Error('read failed'));
    p.settle(b, { ok: true, out: {}, caughtUp: failed }, T0 + 50);
    await failed.catch(() => undefined);
    await Promise.resolve();
    expect(p.status(b)).toBe('applied');
    p.reconcile(stateOf(7), T0 + 60);
    expect(p.status(b)).toBeNull();
  });

  it('a refusal (4xx, 503 busy, offline) removes the op at once and says why; nothing is retried', () => {
    const p = new PendingCommands();
    const cases: Array<[unknown, string]> = [
      [new ApiError(400, '4100 targets: designate a smaller area (max 4096)', {}), '4100 targets'],
      [new ApiError(503, 'busy', { code: 'busy', retryAfterMs: 1000 }), BUSY_TEXT],
      [new ApiError(0, 'offline', { code: 'offline' }), ''],
    ];
    for (const [error, text] of cases) {
      const id = p.add(mine([1, 64, 1]), T0);
      const f = p.settle(id, { ok: false, error }, T0 + 10);
      expect(f).toMatchObject({ id, kind: 'refused' });
      expect(f!.text).toContain(text);
      expect(p.size).toBe(0);
      expect(p.view(undefined).marks).toEqual([]);
      // A late settle of the same op does nothing.
      expect(p.settle(id, { ok: true, out: { rev: 1 } }, T0 + 20)).toBeNull();
    }
  });

  it('a cancel hides its designations while pending, and they come back when it is refused', () => {
    const p = new PendingCommands();
    const id = p.add({ hide: ['d1', 2] }, T0);
    expect([...p.view(undefined).hidden]).toEqual(['d1', '2']);
    p.settle(id, { ok: false, error: new ApiError(400, 'no', {}) }, T0 + 10);
    expect(p.view(undefined).hidden.size).toBe(0);
  });

  it('an unconfirmed op stays until a state shows it, else goes after 10 s with a warning', () => {
    const p = new PendingCommands();
    const lost = new ApiError(0, 'Network error', {});
    const shown = p.add(mine([1, 64, 1], [2, 64, 1]), T0);
    const never = p.add({ ...mine([9, 64, 9]) }, T0);
    p.settle(shown, { ok: false, error: lost }, T0 + 100);
    p.settle(never, { ok: false, error: new ApiError(502, 'upstream', {}) }, T0 + 100);
    expect(p.status(shown)).toBe('unconfirmed');
    expect(p.nextDeadline()).toBe(T0 + 100 + 10_000);
    // A designation of another kind on the cell is no evidence; one of the same kind on one of its cells is.
    expect(
      p.reconcile(stateOf(3, { designations: [{ id: 'x', kind: 'chop', target: [2, 64, 1] }] }), T0 + 200),
    ).toEqual([]);
    expect(p.status(shown)).toBe('unconfirmed');
    const evidence = stateOf(4, { designations: [{ id: 'd9', kind: 'mine', target: [2, 64, 1] }] });
    expect(p.reconcile(evidence, T0 + 300)).toEqual([]);
    expect(p.status(shown)).toBeNull();
    expect(p.reconcile(evidence, T0 + 100 + 9_999)).toEqual([]);
    expect(p.status(never)).toBe('unconfirmed');
    // Time alone ends it (the map calls reconcile at nextDeadline).
    expect(p.reconcile(undefined, T0 + 100 + 10_000)).toEqual([
      { id: never, kind: 'unconfirmed', text: UNCONFIRMED_TEXT },
    ]);
    expect(p.size).toBe(0);
  });

  it('an unconfirmed placement counts as applied when a build cell covers one of its holograms', () => {
    const p = new PendingCommands();
    const id = p.add({ holo: [holo(1), holo(2)] }, T0);
    p.settle(id, { ok: false, error: new Error('socket hang up') }, T0 + 10);
    expect(p.status(id)).toBe('unconfirmed');
    p.reconcile(stateOf(8, { ghosts: { rev: 8, blocks: [[2, 64, 0, 4, 0, 'b1', 'todo']] } }), T0 + 20);
    expect(p.status(id)).toBeNull();
  });

  it('no op is drawn longer than 15 s, whatever its state', () => {
    const p = new PendingCommands();
    const sending = p.add(mine([1, 64, 1]), T0);
    const applied = p.add(mine([2, 64, 1]), T0);
    p.settle(applied, { ok: true, out: { rev: 99 } }, T0 + 10);
    expect(p.nextDeadline()).toBe(T0 + PENDING_LIMIT_MS);
    expect(p.reconcile(stateOf(50), T0 + PENDING_LIMIT_MS - 1)).toEqual([]);
    expect(p.size).toBe(2);
    // The applied one goes quietly; the one that never got an answer says so.
    expect(p.reconcile(stateOf(50), T0 + PENDING_LIMIT_MS)).toEqual([
      { id: sending, kind: 'unconfirmed', text: UNCONFIRMED_TEXT },
    ]);
    expect(p.size).toBe(0);
    expect(p.nextDeadline()).toBeNull();
  });

  it('zone steps compose in order over the server zones; holograms and marks merge by cell (later wins)', () => {
    const p = new PendingCommands();
    const server = [zone('z1', 0)];
    expect(p.view(server).zones).toBeNull();
    p.add({ zones: (zs) => [...zs, zone('~a', 10)] }, T0);
    p.add({ zones: (zs) => zs.filter((z) => z.id !== 'z1').concat(zone('~b', 20)) }, T0);
    p.add({ holo: [holo(1), holo(2)] }, T0);
    p.add({ holo: [holo(2, 0)], marks: [{ cell: [1, 64, 1], kind: 'cut' }] }, T0);
    const v = p.view(server);
    expect(v.zones!.map((z) => z.id)).toEqual(['~a', '~b']);
    expect(server.map((z) => z.id)).toEqual(['z1']); // the server's list is not touched
    expect(v.holo.map((c) => [c.x, c.id])).toEqual([
      [1, 4],
      [2, 0],
    ]);
    expect(v.marks).toEqual([{ cell: [1, 64, 1], kind: 'cut' }]);
    // Memoised while neither the ops nor the zones change.
    expect(p.view(server)).toBe(v);
    expect(p.view([...server])).not.toBe(v);
  });

  it('new server zones repaint the pending zones but keep the marks and holograms as they were', () => {
    const p = new PendingCommands();
    p.add({ zones: (zs) => [...zs, zone('~a', 10)], holo: [holo(1)], marks: [{ cell: [1, 64, 1], kind: 'mine' }] }, T0);
    const v = p.view([zone('z1', 0)]);
    const w = p.view([zone('z2', 5)]);
    expect(w.zones!.map((z) => z.id)).toEqual(['z2', '~a']);
    expect(w.marks).toBe(v.marks);
    expect(w.holo).toBe(v.holo);
    expect(w.hidden).toBe(v.hidden);
  });

  it('an op going unconfirmed tells the page its deadline moved', () => {
    let changes = 0;
    const p = new PendingCommands(() => changes++);
    const id = p.add(mine([1, 64, 1]), T0);
    const rev = p.revision;
    p.settle(id, { ok: false, error: new ApiError(502, 'upstream', {}) }, T0 + 2_000);
    expect(changes).toBe(2);
    expect(p.revision).toBe(rev); // nothing drawn changed
    expect(p.nextDeadline()).toBe(T0 + 12_000);
  });

  it('pending holograms join the colony cells where it has none; its own cell wins', () => {
    const base = [{ ...holo(1, 7), build: 'b1' }];
    expect(withPendingHolo(base, [])).toBe(base);
    expect(withPendingHolo(base, [holo(1)])).toBe(base);
    expect(withPendingHolo(base, [holo(1), holo(2)]).map((c) => [c.x, c.id, c.build])).toEqual([
      [1, 7, 'b1'],
      [2, 4, 'pending'],
    ]);
    expect(holoOf([[3, 64, 0, 0, 0]])).toEqual([
      { x: 3, y: 64, z: 0, id: 0, meta: 0, build: 'pending', state: 'todo' },
    ]);
    expect(holoOf(Array.from({ length: PENDING_HOLO_CAP + 5 }, (_, i) => [i, 64, 0, 4, 0]))).toHaveLength(
      PENDING_HOLO_CAP,
    );
  });

  it('clear forgets every op', () => {
    const p = new PendingCommands();
    p.add(mine([1, 64, 1]), T0);
    const rev = p.revision;
    p.clear();
    expect(p.size).toBe(0);
    expect(p.revision).toBe(rev + 1);
  });
});
