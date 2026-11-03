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

/**
 * GET /api/memory-activity/cloud: the memory cloud (architecture.md, "Memory activity tab" → Cloud). Every RAPTOR leaf at its
 * 2-D UMAP position (the bot's own reducer over the stored leaf embeddings, cosine, seeded), in the unit square. Leaves inserted
 * since the last RAPTOR build are placed from their nearest build leaves, so the picture never reshuffles between builds. Columns
 * are parallel arrays (index `i` is one leaf) to keep tens of thousands of points small on the wire. Ids are RAPTOR node ids,
 * the same as `/api/raptor/graph` and the heat routes.
 */
export const CloudPoints = z.object({
  id: z.array(z.number().int()),
  xy: z.array(z.number()).describe('x, y interleaved, each 0..1 (y down)'),
  cluster: z.array(z.number().int()).describe('index into `clusters`, −1 when the leaf hangs under no cluster'),
  kind: z.array(z.number().int()).describe('index into `kinds`'),
  title: z.array(z.string()).describe('the doc title (≤ 80 chars)'),
  parents: z.array(z.array(z.number().int())).describe('every RAPTOR parent (soft clusters give several)'),
  inserted: z.array(z.boolean()).describe('inserted since the last build (placed, not laid out)'),
});
export type CloudPoints = z.infer<typeof CloudPoints>;

/** Summaries at the mean of their children (bottom-up): the faded link overlay's inner ends. */
export const CloudSummaries = z.object({
  id: z.array(z.number().int()),
  xy: z.array(z.number()),
  layer: z.array(z.number().int()),
  cluster: z.array(z.number().int()).describe('the cluster the summary lies in, −1 above the cluster layer'),
  parents: z.array(z.array(z.number().int())),
});
export type CloudSummaries = z.infer<typeof CloudSummaries>;

export const CloudCluster = z.object({
  id: z.number().int().describe('position in `clusters` (largest first); colour slots follow it'),
  node: z.number().int().describe('the RAPTOR summary node this cluster is'),
  layer: z.number().int(),
  label: z.string().describe('TF-IDF topic terms of the summary, " · "-joined (deterministic, no model calls)'),
  terms: z.array(z.string()),
  summary: z.string().describe('the summary text, ≤ 180 chars'),
  size: z.number().int().describe('leaves in the cluster'),
  x: z.number(),
  y: z.number(),
  spread: z.number().describe('RMS distance of its leaves from (x, y)'),
});
export type CloudCluster = z.infer<typeof CloudCluster>;

export const MemoryCloudStatus = z.enum(['ready', 'computing', 'empty', 'error']);
export type MemoryCloudStatus = z.infer<typeof MemoryCloudStatus>;

export const MemoryCloudResponse = z.object({
  status: MemoryCloudStatus.describe('computing: the layout for the current tree is running; the last one (if any) is served meanwhile'),
  version: z.string().describe('changes when the layout changes (tree state + algorithm)'),
  stateKey: z.string().describe('the RAPTOR tree state the layout was computed for'),
  note: z.string().nullish(),
  clusterLayer: z.number().int().describe('the summary layer the clusters come from: the highest with ≥ 6 nodes'),
  kinds: z.array(z.string()),
  points: CloudPoints,
  summaries: CloudSummaries,
  clusters: z.array(CloudCluster),
  anchors: z.number().int().describe('leaves the UMAP ran on (an even sample above 2000)'),
  placedSinceBuild: z.number().int().describe('leaves placed from their neighbours instead'),
  computeMs: Double.nullish(),
  computedAt: IsoDateTime.nullish(),
  computingStateKey: z.string().nullish(),
});
export type MemoryCloudResponse = z.infer<typeof MemoryCloudResponse>;

/** Client helper: brightness of a node now, from its lastAccessAt and the server half-life. */
export function decayHeat(lastAccessAtMs: number | null | undefined, nowMs: number, halfLifeS: number): number {
  if (lastAccessAtMs === null || lastAccessAtMs === undefined || halfLifeS <= 0) return 0;
  const dt = Math.max(0, (nowMs - lastAccessAtMs) / 1000);
  return Math.pow(0.5, dt / halfLifeS);
}
