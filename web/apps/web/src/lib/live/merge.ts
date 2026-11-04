/**
 * Pure cursor merges for the delta topics (docs/web/api.md §5, "Cursors"): the REST route gives a starting set, stream payloads
 * add what is newer. Every merge is idempotent (replays after Last-Event-ID never duplicate).
 */
import type {
  EventsResponse,
  FeedLine,
  FeedResponse,
  MemoryActivityResponse,
  WorldEntities,
  WorldEntity,
} from '@mineclaw/contracts';

export const FEED_CAP = 1000;
export const EVENTS_CAP = 400;
export const ACTIVITY_CAP = 5000;

function dedupeBy<T>(items: T[], key: (t: T) => number): T[] {
  const map = new Map<number, T>();
  for (const it of items) map.set(key(it), it);
  return [...map.values()].sort((a, b) => key(a) - key(b));
}

export function mergeFeed(prev: FeedResponse | undefined, next: FeedResponse): FeedResponse {
  if (!prev) return { lines: dedupeBy(next.lines, (l) => l.seq).slice(-FEED_CAP), last: next.last };
  const lines = dedupeBy<FeedLine>([...prev.lines, ...next.lines], (l) => l.seq).slice(-FEED_CAP);
  return { lines, last: Math.max(prev.last, next.last) };
}

export function mergeEvents(prev: EventsResponse | undefined, next: EventsResponse): EventsResponse {
  const events = dedupeBy([...(prev?.events ?? []), ...next.events], (e) => e.id).slice(-EVENTS_CAP);
  return { events, last: Math.max(prev?.last ?? 0, next.last) };
}

export function mergeMemoryActivity(
  prev: MemoryActivityResponse | undefined,
  next: MemoryActivityResponse,
): MemoryActivityResponse {
  const events = dedupeBy([...(prev?.events ?? []), ...next.events], (e) => e.seq).slice(-ACTIVITY_CAP);
  // `inContext` is the full current snapshot in every payload: the newest one wins.
  const newer = !prev || next.last >= prev.last;
  return { events, inContext: newer ? next.inContext : prev.inContext, last: Math.max(prev?.last ?? 0, next.last) };
}

function entityKey(e: Pick<WorldEntity, 'dim' | 'id'>): string {
  return `${e.dim}:${e.id ?? 'x'}`;
}

/**
 * Applies an entity delta. `full` (or no previous state) replaces the set; stale revs are ignored. The bots are always the whole
 * current list and not part of the revision: a newer frame at the same revision (only bots moved: the bridge sends one whenever
 * a bot's pose changed) still moves them (live 11-04 such frames were dropped, so bots and pawns only moved when a mob or a drop
 * did).
 */
export function mergeEntities(prev: WorldEntities | undefined, next: WorldEntities): WorldEntities {
  if (!prev || next.full || prev.rev === null || prev.rev === undefined) return next;
  if (next.rev !== null && next.rev !== undefined && next.rev <= prev.rev)
    return next.rev === prev.rev && next.at > prev.at ? { ...prev, at: next.at, bots: next.bots } : prev;
  const map = new Map<string, WorldEntity>();
  for (const e of prev.entities) map.set(entityKey(e), e);
  for (const [dim, id] of next.removed ?? []) map.delete(`${dim}:${id}`);
  for (const e of next.entities) map.set(entityKey(e), e);
  return { ...next, full: false, bots: next.bots, entities: [...map.values()], removed: [] };
}
