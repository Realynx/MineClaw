/** Overview page (explorer-spec §2 header, §3). Bridge-native: `/v1/overview`. */
import { z } from 'zod';
import { Double, IsoDateTime } from './common.js';

export const CountCard = z.object({
  key: z
    .string()
    .describe(
      'facts, recipes, claims, beliefs, episodes, activity, chat, structures, places, observations, chunks, traits, lessons, routines, docs, vectors',
    ),
  label: z.string(),
  count: z.number().int().nonnegative(),
  detail: z.string().nullish().describe('"3 verified · 1 disputed", top observation kinds, sections…'),
  group: z.string().describe('card group heading'),
  memoryKind: z.string().nullish().describe('the Memory browser kind key this card opens, if any'),
});
export type CountCard = z.infer<typeof CountCard>;

export const VectorModelCount = z.object({ model: z.string(), dims: z.number().int(), count: z.number().int() });
export type VectorModelCount = z.infer<typeof VectorModelCount>;

/** Which recall pipeline the bridge's search runs, and why (also returned by search). */
export const SearchMode = z.enum(['hybrid', 'hybrid-no-tree', 'lexical']);
export type SearchMode = z.infer<typeof SearchMode>;

export const DbInfo = z.object({
  file: z.string().describe('file name only, never a full path'),
  sizeBytes: z.number().int().describe('db + -wal + -shm'),
  schemaVersion: z.number().int(),
  fts5: z.boolean(),
  rtree: z.boolean(),
  vectors: z.array(VectorModelCount),
  searchMode: SearchMode,
  searchNote: z.string().describe('"hybrid: FTS + vectors + RAPTOR" or the fallback reason'),
  serverVersion: z.enum(['b1.8.1', '1.12.2']),
  readAt: IsoDateTime,
});
export type DbInfo = z.infer<typeof DbInfo>;

export const KnowledgePackInfo = z.object({
  label: z.string().describe('"b1.8.1-gamedata (b1.8.1) + skyblock"'),
  version: z.string().nullish(),
  modes: z.array(z.string()),
  detail: z.string().nullish(),
});
export type KnowledgePackInfo = z.infer<typeof KnowledgePackInfo>;

export const EmbeddingsStatus = z.object({
  configured: z.boolean(),
  status: z.string().describe('"embeddings: configured (deployment X)" — never a key or an endpoint'),
  model: z.string().nullish(),
  dims: z.number().int().nullish(),
  matchesStored: z.boolean().describe('stored vectors use the configured model+dims, so hybrid search can run'),
});
export type EmbeddingsStatus = z.infer<typeof EmbeddingsStatus>;

/** `meta('raptor_meta')` (C# RaptorTreeMeta). */
export const RaptorMeta = z.object({
  numLayers: z.number().int(),
  nextIndex: z.number().int(),
  dimensions: z.number().int(),
  embeddingModel: z.string(),
  buildId: z.number().int(),
  builtAt: IsoDateTime,
  leavesAtBuild: z.number().int(),
  insertedSinceBuild: z.number().int(),
  removedSinceBuild: z.number().int(),
  baselineLeafParentSimilarity: Double,
  insertedSimilaritySum: Double,
  insertedSimilarityCount: z.number().int(),
  insertSeq: z.number().int(),
  baselineLeafParentSimilarityStd: Double.nullish(),
  insertTemperature: Double.nullish(),
});
export type RaptorMeta = z.infer<typeof RaptorMeta>;

export const RaptorLayerStat = z.object({
  layer: z.number().int(),
  name: z.string().describe('"leaves", "L1 summaries"…'),
  nodes: z.number().int(),
  avgChildren: Double,
  avgTokens: Double,
  maxTokens: z.number().int(),
  dirty: z.number().int().describe('dirty + pending summaries in this layer'),
  share: Double.describe('0..1 bar share of all nodes'),
});
export type RaptorLayerStat = z.infer<typeof RaptorLayerStat>;

export const RaptorStats = z.object({
  headline: z.string().describe('"N nodes · L leaves · K summary layers"'),
  nodes: z.number().int(),
  leaves: z.number().int(),
  summaryLayers: z.number().int(),
  layers: z.array(RaptorLayerStat),
  meta: RaptorMeta,
  insertedCosMean: Double.nullish().describe('mean inserted leaf→parent cosine (sum/count)'),
  dirtySummaries: z.number().int(),
  pendingSummaries: z.number().int(),
  storedLayers: z.number().int(),
});
export type RaptorStats = z.infer<typeof RaptorStats>;

export const Overview = z.object({
  db: DbInfo,
  knowledgePack: KnowledgePackInfo.nullish(),
  embeddings: EmbeddingsStatus,
  cards: z.array(CountCard),
  raptor: RaptorStats.nullish(),
  raptorNote: z
    .string()
    .nullish()
    .describe('"no RAPTOR tree stored in this db", "the stored tree could not be loaded"…'),
});
export type Overview = z.infer<typeof Overview>;
