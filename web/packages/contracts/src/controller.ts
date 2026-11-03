/**
 * Controller / brain context (explorer-spec §12.6, bot-api-spec §1.6).
 * C# source: `src/MineClaw.Agent/Telemetry/BrainContextModels.cs`. Needs `Telemetry:Enabled` on the bot (else a note).
 */
import { z } from 'zod';
import { IsoDateTime, QInt } from './common.js';

export const CtxBlockKind = z.enum([
  'text',
  'tool_use',
  'tool_result',
  'thinking',
  'redacted_thinking',
  'reasoning',
  'image',
]);
export type CtxBlockKind = z.infer<typeof CtxBlockKind>;

export const CtxBlock = z.object({
  kind: CtxBlockKind,
  text: z.string().nullish(),
  label: z.string().nullish().describe('"retrieved memory", "compaction summary", "turn input", thinking labels…'),
  name: z.string().nullish().describe('tool name'),
  id: z.string().nullish().describe('tool_use id'),
  input: z.string().nullish().describe('tool input as JSON text'),
  isError: z.boolean().default(false),
  bytes: z.number().int().nullish().describe('redacted thinking / image payload size (never the payload)'),
  target: z.boolean().default(false).describe('subhive filter: targeted the viewed body'),
});
export type CtxBlock = z.infer<typeof CtxBlock>;

export const CtxMessage = z.object({
  index: z.number().int(),
  role: z.enum(['user', 'assistant']),
  blocks: z.array(CtxBlock),
  tag: z.enum(['turn', 'journal', 'tool_results', 'response']).nullish(),
});
export type CtxMessage = z.infer<typeof CtxMessage>;

export const CtxSystemBlock = z.object({
  index: z.number().int(),
  text: z.string(),
  cache: z.boolean(),
  ttl: z.string().nullish(),
  tokens: z.number().int(),
  inCachedPrefix: z.boolean(),
});
export type CtxSystemBlock = z.infer<typeof CtxSystemBlock>;

export const CtxUsage = z.object({
  in: z.number().int(),
  out: z.number().int(),
  cacheRead: z.number().int(),
  cacheWrite: z.number().int(),
  cacheWrite1h: z.number().int(),
  cost: z.number(),
  latencyMs: z.number().int(),
  promptTokens: z.number().int(),
});
export type CtxUsage = z.infer<typeof CtxUsage>;

export const CtxInFlight = z.object({
  seq: z.number().int(),
  turn: z.number().int(),
  lane: z.string(),
  purpose: z.string(),
  model: z.string(),
  since: IsoDateTime,
  body: z.string().nullish(),
});
export type CtxInFlight = z.infer<typeof CtxInFlight>;

export const CtxLane = z.enum([
  'brain',
  'reply',
  'compaction',
  'worker',
  'author',
  'memory',
  'perception',
  'background',
]);
export type CtxLane = z.infer<typeof CtxLane>;

export const CtxTurnSummary = z.object({
  id: z.number().int(),
  lane: z.string().describe('CtxLane values; kept open for new lanes'),
  purpose: z.string(),
  start: IsoDateTime,
  end: IsoDateTime.nullish(),
  inFlight: z.boolean(),
  calls: z.number().int(),
  model: z.string(),
  tier: z.string(),
  provider: z.string().nullish(),
  in: z.number().int(),
  out: z.number().int(),
  cacheRead: z.number().int(),
  cacheWrite: z.number().int(),
  cost: z.number(),
  latencyMs: z.number().int(),
  preview: z.string(),
  tools: z.array(z.string()),
  hasThinking: z.boolean(),
  parent: z.number().int().nullish(),
  body: z.string().nullish(),
  targets: z.boolean(),
  version: z.number().int(),
  error: z.string().nullish(),
  droppedCalls: z.number().int().default(0),
});
export type CtxTurnSummary = z.infer<typeof CtxTurnSummary>;

export const CtxWindow = z.object({
  callSeq: z.number().int(),
  turnId: z.number().int(),
  at: IsoDateTime,
  model: z.string(),
  tier: z.string(),
  system: z.array(CtxSystemBlock),
  toolCount: z.number().int(),
  toolTokens: z.number().int(),
  toolNames: z.array(z.string()),
  messages: z.array(CtxMessage),
  promptTokens: z.number().int(),
  estimatedTokens: z.number().int(),
  limit: z.number().int(),
  compactAt: z.number().int(),
  cachedPrefixTokens: z.number().int(),
  usage: CtxUsage.nullish(),
  inFlight: z.boolean(),
});
export type CtxWindow = z.infer<typeof CtxWindow>;

export const BrainParams = z.object({ brain: z.string().min(1).max(64) });
export const BrainContextQuery = z.object({
  since: QInt.min(0).default(0).describe('pass the last `version` back'),
  body: z.string().max(64).optional(),
});

/** GET /api/brains/:brain/context. Equal version = nothing changed. Reset when version=0, note="full" or version goes back. */
export const BrainContextPage = z.object({
  brain: z.string(),
  version: z.number().int(),
  at: IsoDateTime,
  capacity: z.number().int(),
  enabled: z.boolean(),
  inFlight: CtxInFlight.nullish(),
  turns: z.array(CtxTurnSummary),
  window: CtxWindow.nullish(),
  evicted: z.array(z.number().int()),
  filter: z.string().nullish(),
  note: z.string().nullish(),
});
export type BrainContextPage = z.infer<typeof BrainContextPage>;

export const CtxCall = z.object({
  seq: z.number().int(),
  at: IsoDateTime,
  purpose: z.string(),
  tier: z.string(),
  model: z.string(),
  provider: z.string().nullish(),
  body: z.string().nullish(),
  round: z.number().int(),
  inFlight: z.boolean(),
  stopReason: z.string().nullish(),
  usage: CtxUsage.nullish(),
  error: z.string().nullish(),
  context: z.number().int().describe('messages of history before `added`'),
  added: z.array(CtxMessage),
  response: z.array(CtxBlock),
  maxTokens: z.number().int().nullish(),
  effort: z.string().nullish(),
  thinking: z.string().nullish(),
});
export type CtxCall = z.infer<typeof CtxCall>;

export const BrainTurnParams = z.object({ brain: z.string().min(1).max(64), n: QInt.min(0) });
export const BrainTurnQuery = z.object({ body: z.string().max(64).optional() });

/** GET /api/brains/:brain/turn/:n (30-turn ring). */
export const BrainTurnDetail = z.object({
  brain: z.string(),
  turn: CtxTurnSummary,
  system: z.array(CtxSystemBlock),
  toolNames: z.array(z.string()),
  calls: z.array(CtxCall),
  children: z.array(CtxTurnSummary),
  filter: z.string().nullish(),
});
export type BrainTurnDetail = z.infer<typeof BrainTurnDetail>;

/** GET /api/bodies/:body/controller. */
export const BodyControllerInfo = z.object({
  body: z.string(),
  kind: z.enum(['main', 'hive', 'subhive', 'script']),
  owner: z.string().nullish(),
  brain: z.string().nullish().describe('null = script-only'),
  brainKind: z.string().nullish(),
  model: z.string().nullish(),
  provider: z.string().nullish(),
  hasScript: z.boolean(),
  script: z.string().nullish(),
  tabs: z.array(z.enum(['brain', 'script'])),
  filter: z.string().nullish(),
  links: z.object({ context: z.string().nullish(), script: z.string().nullish(), owner: z.string().nullish() }),
  note: z.string().nullish(),
});
export type BodyControllerInfo = z.infer<typeof BodyControllerInfo>;
