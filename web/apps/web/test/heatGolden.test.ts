import type { InContextMemory, MemoryHeatResponse } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { lastAccessFromServer, stalenessByNode, type ActivityFilter } from '@/features/memory-activity/heat';

const T = Date.parse('2026-11-01T12:00:00Z');
const iso = (ms: number) => new Date(T - ms).toISOString();

const HEAT = {
  nodes: [
    { nodeId: 1, lastAccessAt: iso(9_000), brains: [{ brain: 'main', lastAccessAt: iso(20_000) }, { brain: 'maid', lastAccessAt: iso(9_000) }] },
    { nodeId: 2, lastAccessAt: iso(4_000), brains: [] },
    { nodeId: 3, lastAccessAt: null, brains: [{ brain: 'main', lastAccessAt: 'not a date' }] },
    { nodeId: 4, lastAccessAt: iso(1_000), brains: [{ brain: 'maid', lastAccessAt: null }, { brain: 'main', lastAccessAt: iso(70_000) }] },
    { nodeId: 5, lastAccessAt: 'garbage', brains: [{ brain: 'x', lastAccessAt: null }] },
  ],
} as unknown as MemoryHeatResponse;

const ctx = (id: number | null, agoMs: number | null, evicted: boolean, brain: string): InContextMemory =>
  ({
    brain,
    memoryKind: 'note',
    memoryId: `doc:${id}`,
    raptorNodeId: id,
    addedAt: iso(100_000),
    lastSeenAt: agoMs === null ? 'nope' : iso(agoMs),
    turnsInContext: 1,
    positionFromEnd: evicted ? null : 2,
  });

const CTX = [
  ctx(1, 30_000, false, 'main'),
  ctx(1, 10_000, true, 'maid'),
  ctx(2, 5_000, true, 'main'),
  ctx(2, 50_000, true, 'maid'),
  ctx(3, null, false, 'main'),
  ctx(3, 1_000, false, 'maid'),
  ctx(null, 1_000, false, 'main'),
  ctx(4, 2_000, false, 'maid'),
  ctx(4, 2_000, true, 'maid'),
];

const FILTERS: Array<[ActivityFilter, string | undefined]> = [
  [{ kind: 'all' }, undefined],
  [{ kind: 'brain', brain: 'main' }, undefined],
  [{ kind: 'brain', brain: 'maid' }, 'main'],
  [{ kind: 'body', body: 'b' }, undefined],
  [{ kind: 'body', body: 'b' }, 'maid'],
];

describe('heat from the server table and context staleness (golden, pinned before they were split)', () => {
  it('reads the same last access and staleness for every filter', () => {
    const out = FILTERS.map(([f, bb]) => ({
      last: [...lastAccessFromServer(HEAT, f, bb)],
      stale: [...stalenessByNode(CTX, T, f, bb)],
    }));
    expect(lastAccessFromServer(undefined, { kind: 'all' }).size).toBe(0);
    expect(JSON.parse(JSON.stringify(out, (_k, v: unknown) => (v === Infinity ? 'Infinity' : v)))).toMatchSnapshot();
  });
});
