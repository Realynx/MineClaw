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
import { mergeColony } from '@/features/story/model';
import { publishBlocks } from '@/features/world/blocks';
import { publishFeed } from '@/features/world/chatBubbles';
import { qk } from '../api/query';
import { mergeEntities, mergeEvents, mergeFeed, mergeMemoryActivity } from './merge';
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
  switch (event) {
    case 'bots':
      qc.setQueryData<StatusResponse>(liveKeys.status, data as StatusResponse);
      break;
    case 'now': {
      const now = data as BotNow;
      qc.setQueryData<BotNow>(liveKeys.now(now.bot), now);
      break;
    }
    case 'feed':
      qc.setQueryData<FeedResponse>(liveKeys.feed, (prev) => mergeFeed(prev, data as FeedResponse));
      publishFeed(data as FeedResponse); // chat bubbles over heads in the world views (new lines only)
      break;
    case 'event':
      qc.setQueryData<EventsResponse>(liveKeys.events, (prev) => mergeEvents(prev, data as EventsResponse));
      break;
    case 'entities':
      qc.setQueryData<WorldEntities>(liveKeys.entities, (prev) => mergeEntities(prev, data as WorldEntities));
      break;
    case 'hive':
      qc.setQueryData<HiveTreeNode>(liveKeys.hive, data as HiveTreeNode);
      break;
    case 'memoryActivity':
      qc.setQueryData<MemoryActivityResponse>(liveKeys.memoryActivity, (prev) =>
        mergeMemoryActivity(prev, data as MemoryActivityResponse),
      );
      break;
    case 'status':
      qc.setQueryData<LiveStatus>(liveKeys.connection, data as LiveStatus);
      break;
    case 'blocks':
      // Live per-block world updates go to the world viewer's subscribers, not the query cache (they are a stream).
      publishBlocks(data as WorldBlockDeltas);
      break;
    case 'story': {
      // One colony's delta: merged into that colony's state query (fetched once by the Story Mode page).
      const f = data as StoryColonyFrame;
      qc.setQueryData(qk('story.get', { id: f.id }), (prev: StoryColonyFrame | undefined) => (prev ? mergeColony(prev, f) : prev));
      break;
    }
  }
}

/** After a resync: re-fetch the REST state of every live topic (merges keep what the stream already delivered). */
export function resyncLive(qc: QueryClient): Promise<void> {
  const ids = ['status', 'bots.now', 'feed', 'events', 'world.entities', 'hive.tree', 'memoryActivity.list', 'story.get'];
  return qc.invalidateQueries({ predicate: (q) => ids.includes(String(q.queryKey[0])) });
}
