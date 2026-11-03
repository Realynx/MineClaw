/**
 * Memory activity tab (architecture.md, "Memory activity tab"): the RAPTOR memory map lit by each hive brain's recent memory
 * use. Brightness = recency of access (exponential decay, propagated to ancestors); staleness = how long a memory has sat in
 * a brain's live context without a refresh (hollow when evicted).
 */
import { z } from 'zod';
import { Double, IsoDateTime, QInt } from './common.js';

export const MemoryAccessSource = z.enum(['rag', 'tool', 'search', 'recall', 'other']);
export type MemoryAccessSource = z.infer<typeof MemoryAccessSource>;

/** One memory access by a brain (supervisor ring). */
export const MemoryAccessEvent = z.object({
  seq: z.number().int(),
  at: IsoDateTime,
  brain: z.string(),
  body: z.string().nullish(),
  memoryKind: z.string().describe('doc kind / memory kind of the accessed record'),
  memoryId: z.string(),
  raptorNodeId: z.number().int().nullish().describe('the RAPTOR leaf index, when the record is in the tree'),
  source: MemoryAccessSource,
  score: Double.nullish(),
  turn: z.number().int().nullish().describe('the brain turn id (controller context) that made the access'),
});
export type MemoryAccessEvent = z.infer<typeof MemoryAccessEvent>;

/** A memory currently in (or recently evicted from) a brain's live context. */
export const InContextMemory = z.object({
  brain: z.string(),
  memoryKind: z.string(),
  memoryId: z.string(),
  raptorNodeId: z.number().int().nullish(),
  addedAt: IsoDateTime,
  lastSeenAt: IsoDateTime.describe('last call whose context still carried it'),
  turnsInContext: z.number().int(),
  positionFromEnd: z.number().int().nullish().describe('messages from the end of the context; null = evicted'),
});
export type InContextMemory = z.infer<typeof InContextMemory>;

/** GET /api/memory-activity?since&brain. `brain` omitted = every brain of the hive. */
export const MemoryActivityQuery = z.object({
  since: QInt.min(0).default(0),
  brain: z.string().max(64).optional(),
});
export type MemoryActivityQuery = z.infer<typeof MemoryActivityQuery>;

/** Also the `memoryActivity` SSE payload: incremental `events` (seq > the last one sent) plus the full `inContext` snapshot. */
export const MemoryActivityResponse = z.object({
  events: z.array(MemoryAccessEvent),
  inContext: z.array(InContextMemory),
  last: z.number().int(),
});
export type MemoryActivityResponse = z.infer<typeof MemoryActivityResponse>;

/** GET /api/memory-activity/heat?brain&windowS (bridge-native aggregate over the ring, propagated up the tree). */
export const MemoryHeatQuery = z.object({
  brain: z.string().max(64).optional(),
  windowS: QInt.min(10)
    .max(24 * 3600)
    .default(900),
});
export type MemoryHeatQuery = z.infer<typeof MemoryHeatQuery>;

export const NodeBrainHeat = z.object({
  brain: z.string(),
  lastAccessAt: IsoDateTime.nullish(),
  inContext: z.boolean(),
  stalenessS: Double.nullish().describe('seconds since the context last refreshed it; null when never in context'),
  evicted: z.boolean().default(false).describe("was in this brain's context in the window and has been dropped"),
});
export type NodeBrainHeat = z.infer<typeof NodeBrainHeat>;

/** Heat of one RAPTOR node (leaf or ancestor). Ids are the same as `/api/raptor/graph` node ids. */
export const NodeHeat = z.object({
  nodeId: z.number().int(),
  lastAccessAt: IsoDateTime.nullish().describe('max over the node and everything below it'),
  accessCount: z.number().int(),
  heat: Double.describe('0..1 brightness at `at`: 0.5^((at − lastAccessAt)/halfLifeS), max-propagated to ancestors'),
  brains: z.array(NodeBrainHeat),
});
export type NodeHeat = z.infer<typeof NodeHeat>;

export const MemoryHeatResponse = z.object({
  at: IsoDateTime,
  brain: z.string().nullish(),
  windowS: z.number().int(),
  halfLifeS: Double.describe('decay half-life the bridge used for `heat` (client re-decays between refreshes)'),
  stateKey: z.string().describe('RAPTOR tree state key; equal to RaptorGraph.stateKey when the ids line up'),
  brains: z.array(z.string()).describe('brains seen in the window (for the per-brain colour legend)'),
  nodes: z.array(NodeHeat).describe('only nodes with any access or context in the window'),
});
export type MemoryHeatResponse = z.infer<typeof MemoryHeatResponse>;

/** Client helper: brightness of a node now, from its lastAccessAt and the server half-life. */
export function decayHeat(lastAccessAtMs: number | null | undefined, nowMs: number, halfLifeS: number): number {
  if (lastAccessAtMs === null || lastAccessAtMs === undefined || halfLifeS <= 0) return 0;
  const dt = Math.max(0, (nowMs - lastAccessAtMs) / 1000);
  return Math.pow(0.5, dt / halfLifeS);
}
