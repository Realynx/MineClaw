/**
 * Turns typed SSE events into query-cache updates. The keys are the same ones the REST hooks use, so a widget never knows
 * whether its data came from a fetch or from the stream.
 */
import type { QueryClient } from '@tanstack/react-query';
import type {
  BotNow,
  EventsResponse,
  FeedResponse,
  HiveTreeNode,
  LiveStatus,
  MemoryActivityResponse,
  StatusResponse,
  StoryColonyFrame,
  WorldBlockDeltas,
  WorldEntities,
} from '@mineclaw/contracts';
import { applyColonyFrame } from '@/features/story/colonySync';
import { noteStoryFrame } from '@/features/story/frames';
import { publishBlocks } from '@/features/world/blocks';
import { publishEntityFrame } from '@/features/world/entityFrames';
import { publishFeed } from '@/features/world/chatBubbles';
import { qk } from '../api/query';
import { mergeEntities, mergeEvents, mergeFeed, mergeMemoryActivity, staleEntities } from './merge';
import type { LiveEventName } from './stream';

/** Stable keys for the live topics (the REST hooks use exactly these). */
export const liveKeys = {
  status: qk('status'),
  now: (bot: string) => qk('bots.now', { bot }),
  feed: qk('feed'),
  events: qk('events'),
  entities: qk('world.entities'),
  hive: qk('hive.tree'),
  memoryActivity: qk('memoryActivity.list'),
  connection: ['live.connection'] as const,
};

export function applyLiveEvent(qc: QueryClient, event: LiveEventName, data: unknown): void {
  LIVE_HANDLERS[event]?.(qc, data);
}

/** What each stream topic does with its frame. */
const LIVE_HANDLERS: Partial<Record<LiveEventName, (qc: QueryClient, data: unknown) => void>> = {
  bots: (qc, data) => void qc.setQueryData<StatusResponse>(liveKeys.status, data as StatusResponse),
  now: (qc, data) => {
    const now = data as BotNow;
    qc.setQueryData<BotNow>(liveKeys.now(now.bot), now);
  },
  feed: (qc, data) => {
    qc.setQueryData<FeedResponse>(liveKeys.feed, (prev) => mergeFeed(prev, data as FeedResponse));
    publishFeed(data as FeedResponse); // chat bubbles over heads in the world views (new lines only)
  },
  event: (qc, data) => void qc.setQueryData<EventsResponse>(liveKeys.events, (prev) => mergeEvents(prev, data as EventsResponse)),
  entities: (qc, data) => {
    qc.setQueryData<WorldEntities>(liveKeys.entities, (prev) => mergeEntities(prev, data as WorldEntities));
    publishEntityFrame(); // the world viewers read the merged frame at once (features/world/entityFrames.ts)
  },
  hive: (qc, data) => void qc.setQueryData<HiveTreeNode>(liveKeys.hive, data as HiveTreeNode),
  memoryActivity: (qc, data) =>
    void qc.setQueryData<MemoryActivityResponse>(liveKeys.memoryActivity, (prev) => mergeMemoryActivity(prev, data as MemoryActivityResponse)),
  status: (qc, data) => void qc.setQueryData<LiveStatus>(liveKeys.connection, data as LiveStatus),
  // Live per-block world updates go to the world viewer's subscribers, not the query cache (they are a stream).
  blocks: (_qc, data) => publishBlocks(data as WorldBlockDeltas),
  story: (qc, data) => {
    // One colony's delta: merged into that colony's state query (fetched once by the Story Mode page).
    // A delta that starts after the revision the query holds is not merged: a catch-up closes the gap (colonySync.ts).
    const f = data as StoryColonyFrame;
    noteStoryFrame(f.id);
    applyColonyFrame(qc, f);
  },
};

/** After a resync: re-fetch the REST state of every live topic (merges keep what the stream already delivered). */
export function resyncLive(qc: QueryClient): Promise<void> {
  resyncEntities(qc);
  const ids = ['status', 'bots.now', 'feed', 'events', 'world.entities', 'hive.tree', 'memoryActivity.list', 'story.get'];
  return qc.invalidateQueries({ predicate: (q) => ids.includes(String(q.queryKey[0])) });
}

/**
 * Removals missed during the gap would stay as ghosts, and an invalidation refetches only a query with an observer (the
 * Controller has none): the cached set is marked incomplete, so whoever reads it next lays a REST snapshot under it.
 */
function resyncEntities(qc: QueryClient): void {
  qc.setQueryData<WorldEntities>(liveKeys.entities, (prev) => (prev ? staleEntities(prev) : prev));
}
