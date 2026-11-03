/**
 * The NEW viewer routes on the bot (architecture.md, Bot-side changes 1–3): chat as the signed-in player, the structured
 * chat feed, and the compact "what is it doing right now" snapshot.
 */
import { z } from 'zod';
import { DimId, IsoDateTime, QInt } from './common.js';

/**
 * POST /api/bots/:bot/chat (role `chat`). `player` and `allowCommands` are NEVER in the public body: the bridge injects
 * `player` from `X-MCX-Player` and sets `allow_commands` only when `X-MCX-Roles` contains `commands`.
 */
export const ChatRequest = z
  .object({
    text: z.string().trim().min(1).max(256),
    requestId: z
      .string()
      .regex(/^[A-Za-z0-9_-]{8,64}$/)
      .describe('client-generated idempotency/correlation id (e.g. a UUID without braces)'),
  })
  .strict();
export type ChatRequest = z.infer<typeof ChatRequest>;

/** The bridge → supervisor body (`POST /v1/sup/viewer/chat` → `/api/viewer/chat`), snake_case on the wire. */
export const SupervisorChatBody = z
  .object({
    bot: z.string(),
    text: z.string(),
    player: z.string().describe('from X-MCX-Player; must be in Agent:Self:TrustedPlayers'),
    allowCommands: z.boolean(),
    requestId: z.string(),
  })
  .strict();
export type SupervisorChatBody = z.infer<typeof SupervisorChatBody>;

export const ChatAccepted = z.object({
  accepted: z.boolean(),
  id: z
    .union([z.string(), z.number().int()])
    .describe('feed correlation id: the `viewer` FeedLine for this text carries it'),
  note: z.string().nullish().describe('why not accepted (rate limited, viewer chat disabled, player not trusted…)'),
  feedSeq: z
    .number()
    .int()
    .nullish()
    .describe('seq of the `viewer` FeedLine the bot recorded for this text (the bridge adds it; the line also carries requestId)'),
});
export type ChatAccepted = z.infer<typeof ChatAccepted>;

export const FeedKind = z.enum(['chat', 'said', 'whisper', 'internal', 'muted', 'system', 'viewer']);
export type FeedKind = z.infer<typeof FeedKind>;

/** One structured line of the feed ring (1000 entries). `internal` and `muted` are private speech (shown behind a toggle). */
export const FeedLine = z.object({
  seq: z.number().int(),
  at: IsoDateTime,
  bot: z.string().describe('the bot the line belongs to (speaker for said/internal/muted, hearer for chat/whisper)'),
  kind: FeedKind,
  from: z.string().nullish().describe('player or bot name; null for system lines'),
  text: z.string(),
  origin: z.string().nullish().describe('"game" | "viewer" (viewer chat) | null'),
  requestId: z.string().nullish().describe('viewer lines: the ChatRequest.requestId'),
  replyTo: z.number().int().nullish().describe('optional: seq of the line this answers, when the bot knows'),
});
export type FeedLine = z.infer<typeof FeedLine>;

export const FeedQuery = z.object({ since: QInt.min(0).default(0) });

/** GET /api/feed?since and the `feed` SSE payload. */
export const FeedResponse = z.object({ lines: z.array(FeedLine), last: z.number().int() });
export type FeedResponse = z.infer<typeof FeedResponse>;

/**
 * Body position. Canonical: [x, y, z] numbers. The "x,y,z" string form (what /api/status uses) is also accepted;
 * read either with `parsePos`.
 */
export const NowPos = z.union([z.tuple([z.number(), z.number(), z.number()]), z.string()]);
export type NowPos = z.infer<typeof NowPos>;

export function parsePos(pos: NowPos | null | undefined): [number, number, number] | null {
  if (pos === null || pos === undefined) return null;
  if (Array.isArray(pos)) return [pos[0], pos[1], pos[2]];
  const parts = pos.split(',').map((s) => Number(s.trim()));
  if (parts.length !== 3 || parts.some((n) => !Number.isFinite(n))) return null;
  return [parts[0] as number, parts[1] as number, parts[2] as number];
}

/** GET /api/bots/:bot/now and the `now` SSE payload (one per bot). */
export const BotNow = z.object({
  bot: z.string(),
  at: IsoDateTime,
  brainState: z.string().nullish().describe('"awake", "asleep (script cobble_loop 34%)"'),
  inFlight: z.object({ purpose: z.string(), model: z.string(), since: IsoDateTime }).nullish(),
  thought: z
    .object({
      text: z.string().nullish().describe('latest model text'),
      thinking: z.string().nullish().describe('summarized thinking (collapsed by default)'),
      at: IsoDateTime.nullish(),
    })
    .nullish(),
  tool: z.object({ name: z.string(), argsShort: z.string().nullish(), at: IsoDateTime.nullish() }).nullish(),
  task: z.string().nullish(),
  script: z
    .object({
      name: z.string(),
      version: z.number().int().nullish(),
      status: z.string(),
      line: z.number().int().nullish(),
      paused: z.boolean().nullish(),
    })
    .nullish(),
  body: z
    .object({
      hp: z.number().int(),
      food: z.number().int(),
      pos: NowPos.nullish(),
      dim: z.union([DimId, z.string()]).nullish(),
      held: z.string().nullish(),
    })
    .nullish(),
  kind: z.enum(['main', 'hive', 'subhive', 'script']).nullish(),
  owner: z.string().nullish().describe('the owning hive account; a subhive body has no brain of its own (its owner thinks for it)'),
});
export type BotNow = z.infer<typeof BotNow>;
