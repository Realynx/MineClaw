/**
 * `GET /api/live/stream` (SSE). Nest holds ONE upstream `GET /v1/live/stream` to the bridge and fans it out to browsers.
 *
 * Frame format (text/event-stream): `event: <name>\nid: <n>\ndata: <one-line JSON payload, camelCase>\n\n`.
 * - Event names are identical on the bridge and public stream; only payload keys differ (snake_case upstream).
 * - `id` is Nest's own monotonically increasing counter (per Nest process). Nest keeps the last 256 frames and replays those
 *   after `Last-Event-ID` on reconnect; if the id is older, it sends `status` with `resync: true` and the client re-fetches.
 * - A `: ping` comment every 15 s keeps proxies open. Browsers reconnect with `retry: 3000`.
 * - Delta topics are cursor-based: on (re)connect the client GETs the REST route once (feed/events/entities/memory-activity),
 *   then applies stream payloads whose cursor (`seq` / `id` / `rev`) is newer than what it holds.
 */
import { z } from 'zod';
import { IsoDateTime } from './common.js';
import { EventsResponse, HiveTreeNode, StatusResponse, WorldBlockDeltas, WorldEntities } from './live.js';
import { MemoryActivityResponse } from './memoryActivity.js';
import { StoryColonyFrame } from './story.js';
import { BotNow, FeedResponse } from './viewer.js';

/** Nest-native connection state (drives the single header indicator). */
export const LiveStatus = z.object({
  at: IsoDateTime,
  upstream: z.enum(['connected', 'connecting', 'reconnecting', 'down']),
  bot: z.enum(['up', 'down', 'unknown']).describe('supervisor reachable from the bridge'),
  latencyMs: z.number().nullish().describe('EWMA of bridge poll latency'),
  resync: z.boolean().default(false).describe('the client missed frames: re-fetch REST state'),
  message: z.string().nullish(),
});
export type LiveStatus = z.infer<typeof LiveStatus>;

/** Event name → payload schema. Bridge poll cadence in parentheses (bridge polls the supervisor on loopback). */
export const SSE_EVENTS = {
  /** `/api/status` without a selected bot (1 Hz; emitted on change). */
  bots: StatusResponse,
  /** New supervisor events since the last frame (2 Hz; only when non-empty). */
  event: EventsResponse,
  /** New feed lines (2 Hz; only when non-empty). */
  feed: FeedResponse,
  /** One bot's now-snapshot (2 Hz per watched bot; emitted on change). */
  now: BotNow,
  /** Entity delta (up to 10 Hz, only when something changed). `full: true` = replace the set. */
  entities: WorldEntities,
  /** Hive tree (0.33 Hz; emitted on change). */
  hive: HiveTreeNode,
  /** New memory accesses + the current in-context snapshot (1 Hz; emitted on change). */
  memoryActivity: MemoryActivityResponse,
  /** Per-block world changes (5 Hz; when non-empty, rev order). `full: true` = refetch through index/chunks. */
  blocks: WorldBlockDeltas,
  /** One Story Mode colony's delta (2 Hz while a client watches; when its revision moved). Cursor: `rev` per colony id. */
  story: StoryColonyFrame,
  /** Nest-native: upstream connection state (on change, and every 15 s). Never sent by the bridge. */
  status: LiveStatus,
} as const;

export type SseEventName = keyof typeof SSE_EVENTS;
export const SSE_EVENT_NAMES = Object.keys(SSE_EVENTS) as SseEventName[];
export type SsePayload<E extends SseEventName> = z.infer<(typeof SSE_EVENTS)[E]>;
export type SseMessage = { [E in SseEventName]: { event: E; id: number; data: SsePayload<E> } }[SseEventName];

/** Topics a browser can subscribe to (`status` is always sent). */
export const SseTopic = z.enum(['bots', 'event', 'feed', 'now', 'entities', 'hive', 'memoryActivity', 'blocks', 'story']);
export type SseTopic = z.infer<typeof SseTopic>;

/**
 * GET /api/live/stream query. Filtering is per browser, done by Nest's fan-out (the upstream carries everything):
 * - `topics`: comma list of SseTopic (default: all).
 * - `bots`: comma list of bot names/shorts that `now` and `feed` are limited to ("*" or omitted = all bots).
 * - `brain`: limit `memoryActivity` to one brain.
 * - `dim`: limit `entities` to one dimension (bots are always sent in full).
 */
export const LiveStreamQuery = z.object({
  topics: z
    .string()
    .regex(/^[A-Za-z,]*$/)
    .optional(),
  bots: z.string().max(1000).optional(),
  brain: z.string().max(64).optional(),
  dim: z.coerce.number().int().optional(),
});
export type LiveStreamQuery = z.infer<typeof LiveStreamQuery>;

/** Parses the `topics` query value (unknown names ignored; empty = all). */
export function parseTopics(value: string | undefined): SseTopic[] {
  const all = SseTopic.options;
  if (!value) return [...all];
  const picked = value
    .split(',')
    .map((s) => s.trim())
    .filter((s): s is SseTopic => (all as readonly string[]).includes(s));
  return picked.length > 0 ? picked : [...all];
}

/** Validates one decoded SSE frame (dev/test helper; production clients may skip it). */
export function parseSseFrame(event: string, data: unknown): { event: SseEventName; data: unknown } | null {
  if (!(event in SSE_EVENTS)) return null;
  const schema = SSE_EVENTS[event as SseEventName] as z.ZodType;
  return { event: event as SseEventName, data: schema.parse(data) };
}

/** Serializes one frame (Nest side). */
export function formatSseFrame(event: SseEventName, id: number, data: unknown): string {
  return `event: ${event}\nid: ${id}\ndata: ${JSON.stringify(data)}\n\n`;
}
