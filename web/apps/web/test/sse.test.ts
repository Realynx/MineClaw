import { QueryClient } from '@tanstack/react-query';
import type { FeedResponse, MemoryActivityResponse, WorldEntities } from '@mineclaw/contracts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetBlocks, subscribeBlocks } from '@/features/world/blocks';
import { applyLiveEvent, liveKeys } from '@/lib/live/dispatch';
import { createFrameBatcher } from '@/lib/live/LiveProvider';
import { mergeEntities, mergeFeed } from '@/lib/live/merge';
import { LiveStream, type EventSourceLike } from '@/lib/live/stream';

class FakeEventSource implements EventSourceLike {
  readyState = 0;
  onopen: ((ev: Event) => unknown) | null = null;
  onerror: ((ev: Event) => unknown) | null = null;
  closed = false;
  listeners = new Map<string, Array<(ev: MessageEvent) => void>>();
  constructor(readonly url: string) {}
  addEventListener(type: string, listener: (ev: MessageEvent) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }
  close() {
    this.closed = true;
    this.readyState = 2;
  }
  open() {
    this.readyState = 1;
    this.onopen?.(new Event('open'));
  }
  emit(type: string, data: unknown, id = '1') {
    for (const l of this.listeners.get(type) ?? []) l(new MessageEvent(type, { data: JSON.stringify(data), lastEventId: id }));
  }
  fail(closed: boolean) {
    this.readyState = closed ? 2 : 0;
    this.onerror?.(new Event('error'));
  }
}

const at = '2026-11-02T10:00:00+00:00';
const feedLine = (seq: number) => ({ seq, at, bot: 'maid_chan', kind: 'said' as const, from: 'maid_chan', text: `t${seq}` });

describe('LiveStream', () => {
  let sources: FakeEventSource[];
  const factory = (url: string) => {
    const s = new FakeEventSource(url);
    sources.push(s);
    return s;
  };
  beforeEach(() => {
    sources = [];
    vi.useFakeTimers();
  });
  afterEach(() => vi.useRealTimers());

  it('dispatches typed events and tracks Last-Event-ID', () => {
    const onMessage = vi.fn();
    const states: string[] = [];
    const live = new LiveStream({ onMessage, onState: (s) => states.push(s) }, { factory, validate: true });
    live.start();
    sources[0]!.open();
    sources[0]!.emit('feed', { lines: [feedLine(3)], last: 3 }, '42');
    expect(onMessage).toHaveBeenCalledWith('feed', { lines: [feedLine(3)], last: 3 }, '42');
    expect(live.lastEventId).toBe('42');
    expect(states).toEqual(['connecting', 'open']);
    live.stop();
  });

  it('drops payloads that break the contract (validation on)', () => {
    const onMessage = vi.fn();
    const err = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const live = new LiveStream({ onMessage }, { factory, validate: true });
    live.start();
    sources[0]!.emit('now', { nope: true });
    expect(onMessage).not.toHaveBeenCalled();
    err.mockRestore();
    live.stop();
  });

  it('lets the browser retry transient errors, and reopens (with a resync) when the source closed', () => {
    const onResync = vi.fn();
    const states: string[] = [];
    const live = new LiveStream({ onMessage: vi.fn(), onResync, onState: (s) => states.push(s) }, { factory, minBackoffMs: 100 });
    live.start();
    sources[0]!.open();
    sources[0]!.fail(false); // still CONNECTING: the browser retries with Last-Event-ID
    expect(sources).toHaveLength(1);
    expect(states.at(-1)).toBe('reconnecting');
    sources[0]!.fail(true); // CLOSED: we must reopen
    vi.advanceTimersByTime(200);
    expect(sources).toHaveLength(2);
    sources[1]!.open();
    expect(onResync).toHaveBeenCalledWith('reopened');
    expect(states.at(-1)).toBe('open');
    live.stop();
  });

  it('asks for a resync when the server says the client fell too far behind', () => {
    const onResync = vi.fn();
    const live = new LiveStream({ onMessage: vi.fn(), onResync }, { factory });
    live.start();
    sources[0]!.emit('status', { at, upstream: 'connected', bot: 'up', resync: true });
    expect(onResync).toHaveBeenCalledWith('server');
    live.stop();
  });
});

describe('applyLiveEvent', () => {
  it('merges feed frames by seq into the same key the REST hook uses', () => {
    const qc = new QueryClient();
    qc.setQueryData<FeedResponse>(liveKeys.feed, { lines: [feedLine(1), feedLine(2)], last: 2 });
    applyLiveEvent(qc, 'feed', { lines: [feedLine(2), feedLine(3)], last: 3 });
    const feed = qc.getQueryData<FeedResponse>(liveKeys.feed)!;
    expect(feed.lines.map((l) => l.seq)).toEqual([1, 2, 3]);
    expect(feed.last).toBe(3);
  });

  it('stores one now-snapshot per bot', () => {
    const qc = new QueryClient();
    applyLiveEvent(qc, 'now', { bot: 'maid_chan', at });
    applyLiveEvent(qc, 'now', { bot: 'maid_chan_67', at });
    expect(qc.getQueryData(liveKeys.now('maid_chan'))).toMatchObject({ bot: 'maid_chan' });
    expect(qc.getQueryData(liveKeys.now('maid_chan_67'))).toMatchObject({ bot: 'maid_chan_67' });
  });

  it('applies entity deltas, removals and full replaces', () => {
    const base: WorldEntities = {
      at: 1,
      rev: 5,
      full: true,
      bots: [],
      entities: [
        { kind: 'mob', dim: 0, x: 0, y: 0, z: 0, id: 1 },
        { kind: 'mob', dim: 0, x: 0, y: 0, z: 0, id: 2 },
      ],
    };
    const delta = mergeEntities(base, { at: 2, rev: 6, full: false, bots: [], entities: [{ kind: 'mob', dim: 0, x: 9, y: 0, z: 0, id: 1 }], removed: [[0, 2]] });
    expect(delta.entities).toEqual([{ kind: 'mob', dim: 0, x: 9, y: 0, z: 0, id: 1 }]);
    expect(mergeEntities(delta, { ...base, rev: 4, full: false })).toBe(delta); // stale rev ignored
    expect(mergeEntities(delta, { ...base, rev: 9, full: true }).entities).toHaveLength(2);
  });

  it('appends memory activity events and replaces the in-context snapshot', () => {
    const qc = new QueryClient();
    const ev = (seq: number) => ({ seq, at, brain: 'main', memoryKind: 'note', memoryId: `doc:${seq}`, source: 'rag' as const });
    applyLiveEvent(qc, 'memoryActivity', { events: [ev(1)], inContext: [], last: 1 });
    applyLiveEvent(qc, 'memoryActivity', {
      events: [ev(1), ev(2)],
      inContext: [{ brain: 'main', memoryKind: 'note', memoryId: 'doc:2', addedAt: at, lastSeenAt: at, turnsInContext: 1 }],
      last: 2,
    });
    const m = qc.getQueryData<MemoryActivityResponse>(liveKeys.memoryActivity)!;
    expect(m.events.map((e) => e.seq)).toEqual([1, 2]);
    expect(m.inContext).toHaveLength(1);
  });

  it('routes block deltas to the world viewer subscribers, not the cache', () => {
    resetBlocks();
    const qc = new QueryClient();
    const got = vi.fn();
    subscribeBlocks(got);
    const d = { at: 1, rev: 3, full: false, more: false, blocks: { '0': [[1, 64, 2, 4, 0]] }, columns: [] };
    applyLiveEvent(qc, 'blocks', d);
    applyLiveEvent(qc, 'blocks', d); // same rev again: dropped
    expect(got).toHaveBeenCalledTimes(1);
    expect(got.mock.calls[0]![0]).toEqual(d);
  });

  it('keeps the newest status for the connection indicator', () => {
    const qc = new QueryClient();
    applyLiveEvent(qc, 'status', { at, upstream: 'connected', bot: 'down', resync: false });
    expect(qc.getQueryData(liveKeys.connection)).toMatchObject({ bot: 'down' });
  });
});

describe('frame batching', () => {
  it('coalesces a burst of frames into one apply per animation frame', () => {
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => frames.push(cb));
    const apply = vi.fn();
    const b = createFrameBatcher(apply);
    b.push('feed', 1);
    b.push('now', 2);
    b.push('event', 3);
    expect(apply).not.toHaveBeenCalled();
    expect(frames).toHaveLength(1);
    frames[0]!(0);
    expect(apply).toHaveBeenCalledTimes(1);
    expect(apply.mock.calls[0]![0]).toEqual([
      ['feed', 1],
      ['now', 2],
      ['event', 3],
    ]);
    vi.unstubAllGlobals();
  });
});

describe('mergeFeed', () => {
  it('caps the ring at 1000 lines', () => {
    const lines = Array.from({ length: 1200 }, (_, i) => feedLine(i + 1));
    const r = mergeFeed(undefined, { lines, last: 1200 });
    expect(r.lines).toHaveLength(1000);
    expect(r.lines[0]!.seq).toBe(201);
  });
});
