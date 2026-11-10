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

/** Removed keys an incomplete cached set keeps for its REST seed (bounded like the bot's entity cap). */
export const REMOVED_CAP = 2048;

/**
 * Whether a cached entity set is the whole set: it descends from a `full` frame or a REST snapshot. A set that began with a
 * delta (a page that joined a running stream, or one marked stale by a resync) has `full` false and keeps the keys its
 * deltas removed in `removed`, so a REST snapshot laid under it later drops them too (`seedEntities`).
 */
export function entitiesComplete(e: WorldEntities): boolean {
  return e.full === true;
}

/**
 * Applies an entity delta. `full` (or no previous state) replaces the set; stale revs are ignored. The bots are always the whole
 * current list and not part of the revision: a newer frame at the same revision (only bots moved: the bridge sends one whenever
 * a bot's pose changed) still moves them (live 11-04 such frames were dropped, so bots and pawns only moved when a mob or a drop
 * did). The result is complete when `prev` was.
 */
export function mergeEntities(prev: WorldEntities | undefined, next: WorldEntities): WorldEntities {
  if (!prev || next.full || prev.rev === null || prev.rev === undefined) return next;
  if (next.rev !== null && next.rev !== undefined && next.rev <= prev.rev) return botsOnly(prev, next);
  return applyDelta(prev, next);
}

/**
 * A frame no newer than what we have: only its bots move, and only when it is the same revision but later. The previous
 * frame's events stay behind: they were delivered with that frame, and readers take a frame's events as new.
 */
function botsOnly(prev: WorldEntities, next: WorldEntities): WorldEntities {
  if (next.rev !== prev.rev || next.at <= prev.at) return prev;
  const { events: _delivered, ...rest } = prev;
  return {
    ...rest,
    at: next.at,
    bots: next.bots,
    ...(next.chests !== null && next.chests !== undefined ? { chests: next.chests } : {}),
  };
}

/** A newer delta: removed entities go, changed and new ones replace by key. */
function applyDelta(prev: WorldEntities, next: WorldEntities): WorldEntities {
  const map = new Map<string, WorldEntity>();
  for (const e of prev.entities) map.set(entityKey(e), e);
  for (const [dim, id] of next.removed ?? []) map.delete(`${dim}:${id}`);
  for (const e of next.entities) map.set(entityKey(e), e);
  const complete = entitiesComplete(prev);
  const removed = complete ? [] : keptRemovals(prev, next);
  return { ...next, full: complete, bots: next.bots, entities: [...map.values()], removed };
}

/** The removals an incomplete set has seen so far, minus the keys this delta brings back. */
function keptRemovals(prev: WorldEntities, next: WorldEntities): Array<[number, number]> {
  const back = new Set(next.entities.map(entityKey));
  const kept = new Map<string, [number, number]>();
  for (const r of [...(prev.removed ?? []), ...(next.removed ?? [])]) {
    const key = `${r[0]}:${r[1]}`;
    if (back.has(key)) continue;
    kept.delete(key);
    kept.set(key, r);
  }
  return [...kept.values()].slice(-REMOVED_CAP);
}

/**
 * Lays a REST snapshot (`since: 0`) under the cached set and returns a complete set.
 * - A complete cache newer than the snapshot is kept (a slow REST reply must not rewind a newer stream pose).
 * - An incomplete cache newer than the snapshot is overlaid: its removals apply, its entities replace by key.
 * - Otherwise the snapshot is the set; an incomplete cache only keeps its bots when its frame is later.
 */
export function seedEntities(snapshot: WorldEntities, cached: WorldEntities | undefined): WorldEntities {
  // A seeded set is a new frame, and readers take a frame's events as new: only stream frames deliver events (the
  // snapshot's last second, and the cache's own, were already played or are past).
  const { events: _snapshotEvents, ...snap } = snapshot;
  const fresh: WorldEntities = { ...snap, full: true };
  if (!cached) return fresh;
  const revAhead = (cached.rev ?? 0) > (snapshot.rev ?? 0);
  const posesAhead = revAhead || (cached.rev === snapshot.rev && cached.at > snapshot.at);
  if (entitiesComplete(cached)) return posesAhead ? cached : fresh;
  if (cached.rev === null || cached.rev === undefined || !revAhead) {
    return posesAhead
      ? { ...fresh, at: cached.at, bots: cached.bots, chests: cached.chests ?? snapshot.chests }
      : fresh;
  }
  const map = new Map<string, WorldEntity>();
  for (const e of snapshot.entities) map.set(entityKey(e), e);
  for (const [dim, id] of cached.removed ?? []) map.delete(`${dim}:${id}`);
  for (const e of cached.entities) map.set(entityKey(e), e);
  const { events: _cachedEvents, ...rest } = cached;
  return { ...rest, full: true, entities: [...map.values()], removed: [], chests: cached.chests ?? snapshot.chests };
}

/** After a stream gap the cached set may miss removals: it no longer counts as complete, so the next read reseeds it. */
export function staleEntities(prev: WorldEntities): WorldEntities {
  return entitiesComplete(prev) ? { ...prev, full: false, removed: [] } : prev;
}
