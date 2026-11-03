import type { InContextMemory, MemoryAccessEvent, MemoryHeatResponse } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import {
  brainColor,
  BRAIN_PALETTE,
  density,
  heatAt,
  lastAccessFromEvents,
  lastAccessFromServer,
  mergeLast,
  parseFilter,
  stalenessByNode,
  stalenessColor,
  stalenessShare,
} from '@/features/memory-activity/heat';

// tree: 1,2 → 10 → 100 ; 3 → 11 → 100 ; 3 also soft-clusters under 10
const parents = new Map<number, number[]>([
  [1, [10]],
  [2, [10]],
  [3, [11, 10]],
  [10, [100]],
  [11, [100]],
]);
const T = Date.parse('2026-11-02T10:00:00Z');
const iso = (ms: number) => new Date(ms).toISOString();
const ev = (seq: number, node: number, msAgo: number, brain = 'main', body = 'maid_chan'): MemoryAccessEvent => ({
  seq,
  at: iso(T - msAgo),
  brain,
  body,
  memoryKind: 'note',
  memoryId: `doc:${node}`,
  raptorNodeId: node,
  source: 'rag',
});

describe('lastAccessFromEvents', () => {
  const events = [ev(1, 1, 60_000), ev(2, 2, 10_000, 'maid_chan_67', 'maid_chan_67'), ev(3, 3, 30_000)];

  it('propagates the latest access up every parent link, so regions light up', () => {
    const m = lastAccessFromEvents(events, parents, T, { kind: 'all' });
    expect(m.get(1)?.at).toBe(T - 60_000);
    expect(m.get(10)).toEqual({ at: T - 10_000, brain: 'maid_chan_67' }); // newest child wins
    expect(m.get(11)?.at).toBe(T - 30_000);
    expect(m.get(100)?.at).toBe(T - 10_000);
    // soft cluster: leaf 3 also lights 10 (but 10 already has a newer access)
    expect(m.has(3)).toBe(true);
  });

  it('replays: ignores accesses after the scrubber time', () => {
    const m = lastAccessFromEvents(events, parents, T - 20_000, { kind: 'all' });
    expect(m.has(2)).toBe(false);
    expect(m.get(10)?.at).toBe(T - 30_000); // via the soft link from 3
  });

  it('filters by brain or by body', () => {
    expect([...lastAccessFromEvents(events, parents, T, { kind: 'brain', brain: 'main' }).keys()].sort((a, b) => a - b)).toEqual([1, 3, 10, 11, 100]);
    expect([...lastAccessFromEvents(events, parents, T, { kind: 'body', body: 'maid_chan_67' }).keys()].sort((a, b) => a - b)).toEqual([2, 10, 100]);
  });
});

describe('server heat', () => {
  const heat: MemoryHeatResponse = {
    at: iso(T),
    windowS: 900,
    halfLifeS: 300,
    stateKey: 'k',
    brains: ['main', 'maid_chan_67'],
    nodes: [
      {
        nodeId: 10,
        lastAccessAt: iso(T - 5_000),
        accessCount: 3,
        heat: 0.9,
        brains: [
          { brain: 'main', lastAccessAt: iso(T - 50_000), inContext: true, evicted: false },
          { brain: 'maid_chan_67', lastAccessAt: iso(T - 5_000), inContext: false, evicted: false },
        ],
      },
    ],
  };
  it('picks the right brain for the filter and merges with live events', () => {
    expect(lastAccessFromServer(heat, { kind: 'all' }).get(10)).toEqual({ at: T - 5_000, brain: 'maid_chan_67' });
    expect(lastAccessFromServer(heat, { kind: 'brain', brain: 'main' }).get(10)).toEqual({ at: T - 50_000, brain: 'main' });
    expect(lastAccessFromServer(heat, { kind: 'body', body: 'x' }).size).toBe(0); // needs the body's brain
    const merged = mergeLast(lastAccessFromServer(heat, { kind: 'brain', brain: 'main' }), new Map([[10, { at: T - 1_000, brain: 'main' }]]));
    expect(merged.get(10)?.at).toBe(T - 1_000);
  });

  it('decays brightness with the server half-life', () => {
    expect(heatAt({ at: T, brain: 'main' }, T, 300)).toBe(1);
    expect(heatAt({ at: T - 300_000, brain: 'main' }, T, 300)).toBeCloseTo(0.5);
    expect(heatAt({ at: T - 600_000, brain: 'main' }, T, 300)).toBeCloseTo(0.25);
    expect(heatAt(undefined, T, 300)).toBe(0);
  });
});

describe('staleness', () => {
  const ctx = (node: number, seenAgoMs: number, evicted: boolean, brain = 'main'): InContextMemory => ({
    brain,
    memoryKind: 'note',
    memoryId: `doc:${node}`,
    raptorNodeId: node,
    addedAt: iso(T - 900_000),
    lastSeenAt: iso(T - seenAgoMs),
    turnsInContext: 3,
    positionFromEnd: evicted ? null : 4,
  });
  it('reports seconds since the context refreshed a memory, hollow when evicted', () => {
    const m = stalenessByNode([ctx(1, 30_000, false), ctx(2, 10_000, true)], T, { kind: 'all' });
    expect(m.get(1)).toEqual({ stalenessS: 30, evicted: false, brain: 'main' });
    expect(m.get(2)?.evicted).toBe(true);
  });
  it('prefers a live entry over an evicted one in another brain, and filters by brain', () => {
    const m = stalenessByNode([ctx(1, 5_000, true, 'maid_chan_67'), ctx(1, 90_000, false, 'main')], T, { kind: 'all' });
    expect(m.get(1)).toMatchObject({ evicted: false, brain: 'main' });
    expect(stalenessByNode([ctx(1, 5_000, true, 'maid_chan_67')], T, { kind: 'brain', brain: 'main' }).size).toBe(0);
  });
  it('maps staleness to a ring weight and a fresh→stale colour', () => {
    expect(stalenessShare(0)).toBe(0);
    expect(stalenessShare(300)).toBe(0.5);
    expect(stalenessShare(10_000)).toBe(1);
    expect(stalenessColor(0)).toBe('#3fbf7f');
    expect(stalenessColor(600)).toBe('#e85a5a');
    expect(stalenessColor(300)).toBe('#f0b046');
  });
});

describe('brain colours, filters and density', () => {
  it('gives each brain a stable colour by its sorted position', () => {
    expect(brainColor('main', ['maid_chan_67', 'main'])).toBe(BRAIN_PALETTE[1]);
    expect(brainColor('maid_chan_67', ['main', 'maid_chan_67'])).toBe(BRAIN_PALETTE[0]);
  });
  it('parses filter keys from the URL', () => {
    expect(parseFilter(null)).toEqual({ kind: 'all' });
    expect(parseFilter('brain:main')).toEqual({ kind: 'brain', brain: 'main' });
    expect(parseFilter('body:maid_chan_68')).toEqual({ kind: 'body', body: 'maid_chan_68' });
  });
  it('buckets accesses for the timeline strip', () => {
    const d = density([ev(1, 1, 50_000), ev(2, 1, 40_000), ev(3, 1, 1_000)], T - 60_000, T, 6, { kind: 'all' });
    expect(d).toEqual([0, 1, 1, 0, 0, 1]);
  });
});
