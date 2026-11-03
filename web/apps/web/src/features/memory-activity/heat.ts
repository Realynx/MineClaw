/**
 * Memory activity rendering helpers (architecture.md, "Memory activity tab").
 * - Brightness = recency of access: `heat = 0.5^((t − lastAccess)/halfLife)`, max-propagated from a leaf to every ancestor so
 *   a whole region glows.
 * - Staleness = how long a memory has sat in a brain's live context without a refresh; drawn as a ring. Evicted = hollow.
 * - Filter: every brain, one hive brain, or one body.
 * Pure functions: the page and the timeline replay call them with a time `t`.
 */
import { decayHeat, toNumber, type InContextMemory, type MemoryAccessEvent, type MemoryHeatResponse } from '@mineclaw/contracts';
import { ancestors } from '../graph/geometry';

export type ActivityFilter = { kind: 'all' } | { kind: 'brain'; brain: string } | { kind: 'body'; body: string };

export interface LastAccess {
  at: number;
  brain: string;
}

export function filterKey(f: ActivityFilter): string {
  return f.kind === 'all' ? 'all' : f.kind === 'brain' ? `brain:${f.brain}` : `body:${f.body}`;
}

export function parseFilter(key: string | null | undefined): ActivityFilter {
  if (!key || key === 'all') return { kind: 'all' };
  if (key.startsWith('brain:')) return { kind: 'brain', brain: key.slice(6) };
  if (key.startsWith('body:')) return { kind: 'body', body: key.slice(5) };
  return { kind: 'all' };
}

export function matches(e: Pick<MemoryAccessEvent, 'brain' | 'body'>, f: ActivityFilter): boolean {
  if (f.kind === 'all') return true;
  if (f.kind === 'brain') return e.brain === f.brain;
  return e.body === f.body;
}

function bump(map: Map<number, LastAccess>, id: number, at: number, brain: string): void {
  const cur = map.get(id);
  if (!cur || at > cur.at) map.set(id, { at, brain });
}

/**
 * Last access per graph node at time `t` from the access ring, propagated to every ancestor (all parent links, so soft
 * clusters light up too). Events after `t` are ignored (timeline replay).
 */
export function lastAccessFromEvents(
  events: readonly MemoryAccessEvent[],
  parents: ReadonlyMap<number, readonly number[]>,
  t: number,
  filter: ActivityFilter,
): Map<number, LastAccess> {
  const leaf = new Map<number, LastAccess>();
  for (const e of events) {
    if (e.raptorNodeId === null || e.raptorNodeId === undefined || !matches(e, filter)) continue;
    const at = Date.parse(e.at);
    if (Number.isNaN(at) || at > t) continue;
    bump(leaf, e.raptorNodeId, at, e.brain);
  }
  const out = new Map(leaf);
  for (const [id, la] of leaf) for (const a of ancestors(parents, id)) bump(out, a, la.at, la.brain);
  return out;
}

/** The server's aggregate (already propagated) as last-access per node, for the chosen filter. */
export function lastAccessFromServer(heat: MemoryHeatResponse | undefined, filter: ActivityFilter, bodyBrain?: string): Map<number, LastAccess> {
  const out = new Map<number, LastAccess>();
  if (!heat) return out;
  const brain = filter.kind === 'brain' ? filter.brain : filter.kind === 'body' ? bodyBrain : undefined;
  // A body filter cannot be answered from the per-brain aggregate alone; the events cover it.
  if (filter.kind === 'body' && !bodyBrain) return out;
  for (const n of heat.nodes) {
    let best: LastAccess | null = null;
    for (const b of n.brains) {
      if (brain && b.brain !== brain) continue;
      const at = b.lastAccessAt ? Date.parse(b.lastAccessAt) : NaN;
      if (!Number.isNaN(at) && (!best || at > best.at)) best = { at, brain: b.brain };
    }
    if (!best && !brain && n.lastAccessAt) best = { at: Date.parse(n.lastAccessAt), brain: n.brains[0]?.brain ?? '' };
    if (best) out.set(n.nodeId, best);
  }
  return out;
}

export function mergeLast(a: ReadonlyMap<number, LastAccess>, b: ReadonlyMap<number, LastAccess>): Map<number, LastAccess> {
  const out = new Map(a);
  for (const [id, la] of b) bump(out, id, la.at, la.brain);
  return out;
}

/** Brightness 0..1 of a node at `t`. */
export function heatAt(la: LastAccess | undefined, t: number, halfLifeS: number): number {
  return la ? decayHeat(la.at, t, halfLifeS) : 0;
}

export interface Staleness {
  stalenessS: number;
  evicted: boolean;
  brain: string;
}

/**
 * Staleness per RAPTOR leaf from the in-context snapshot: seconds since the context last refreshed it. Evicted memories
 * (positionFromEnd null) are kept so they draw hollow. For one brain/body only that brain's context counts; with several
 * brains the freshest wins.
 */
export function stalenessByNode(
  inContext: readonly InContextMemory[],
  now: number,
  filter: ActivityFilter,
  bodyBrain?: string,
): Map<number, Staleness> {
  const brain = filter.kind === 'brain' ? filter.brain : filter.kind === 'body' ? bodyBrain : undefined;
  const out = new Map<number, Staleness>();
  for (const c of inContext) {
    if (c.raptorNodeId === null || c.raptorNodeId === undefined) continue;
    if (brain && c.brain !== brain) continue;
    const seen = Date.parse(c.lastSeenAt);
    const s: Staleness = {
      stalenessS: Number.isNaN(seen) ? Infinity : Math.max(0, (now - seen) / 1000),
      evicted: c.positionFromEnd === null || c.positionFromEnd === undefined,
      brain: c.brain,
    };
    const cur = out.get(c.raptorNodeId);
    // prefer a live (not evicted) entry, then the freshest
    if (!cur || (cur.evicted && !s.evicted) || (cur.evicted === s.evicted && s.stalenessS < cur.stalenessS)) out.set(c.raptorNodeId, s);
  }
  return out;
}

/** Staleness beyond this reads as fully stale (full ring weight, red). */
export const STALE_MAX_S = 600;

export function stalenessShare(s: number): number {
  return Math.max(0, Math.min(1, s / STALE_MAX_S));
}

/** Fresh → green, ageing → amber, stale → red (as hex, mixed in sRGB). */
export function stalenessColor(s: number): string {
  const f = stalenessShare(s);
  const stops: Array<[number, [number, number, number]]> = [
    [0, [63, 191, 127]],
    [0.5, [240, 176, 70]],
    [1, [232, 90, 90]],
  ];
  let i = 0;
  while (i < stops.length - 2 && f > stops[i + 1]![0]) i++;
  const [f0, c0] = stops[i]!;
  const [f1, c1] = stops[i + 1]!;
  const t = (f - f0) / (f1 - f0 || 1);
  return `#${c0.map((v, k) => Math.round(v + (c1[k]! - v) * t).toString(16).padStart(2, '0')).join('')}`;
}

/** Per-brain colours: a categorical palette that reads on both themes. Stable by the brain's position in the sorted list. */
export const BRAIN_PALETTE = ['#3ad19a', '#6aa4ff', '#f0b046', '#e86fa6', '#b38cff', '#4fc7d8', '#f07a4a', '#a3d14a'];

export function brainColor(brain: string, brains: readonly string[]): string {
  const sorted = [...new Set(brains)].sort();
  const i = sorted.indexOf(brain);
  return BRAIN_PALETTE[(i < 0 ? sorted.length : i) % BRAIN_PALETTE.length]!;
}

/** Event counts per bucket for the timeline's density strip. */
export function density(events: readonly MemoryAccessEvent[], from: number, to: number, buckets: number, filter: ActivityFilter): number[] {
  const out = new Array<number>(buckets).fill(0);
  const span = Math.max(1, to - from);
  for (const e of events) {
    if (!matches(e, filter)) continue;
    const at = Date.parse(e.at);
    if (at < from || at > to) continue;
    out[Math.min(buckets - 1, Math.floor(((at - from) / span) * buckets))]!++;
  }
  return out;
}

export function halfLife(heat: MemoryHeatResponse | undefined): number {
  const h = heat ? toNumber(heat.halfLifeS) : NaN;
  return Number.isFinite(h) && h > 0 ? h : 300;
}
