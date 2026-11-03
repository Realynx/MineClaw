/** Search page (explorer-spec §5): the bot's own recall pipeline. Bridge-native: `/v1/search`. */
import { z } from 'zod';
import { Double, QInt, UnixMs } from './common.js';
import { SearchMode } from './overview.js';

export const SearchKind = z.enum([
  'all',
  'episode',
  'knowledge',
  'gamedata',
  'structure',
  'note',
  'player',
  'world',
  'claim',
  'skill',
  'routine',
  'summary',
  'selfnote',
]);
export type SearchKind = z.infer<typeof SearchKind>;

/**
 * GET /api/search. `mode`: `auto` = hybrid when embeddings match, else lexical (what the bot does); `lexical` forces FTS only.
 * `k` = result limit (the explorer uses 30).
 */
export const SearchQuery = z.object({
  q: z.string().min(1).max(1000),
  mode: z.enum(['auto', 'lexical']).default('auto'),
  k: QInt.min(1).max(200).default(30),
  kind: SearchKind.default('all'),
  dim: z.enum(['any', 'overworld', 'nether']).default('any'),
});
export type SearchQuery = z.infer<typeof SearchQuery>;

export const RaptorCrumb = z.object({
  index: z.number().int(),
  layer: z.number().int(),
  label: z.string(),
});
export type RaptorCrumb = z.infer<typeof RaptorCrumb>;

export const SearchSignals = z.object({
  bm25: Double.nullish(),
  cos: Double.nullish(),
  distance: Double.nullish().describe('metres from the query position (spatial), if any'),
});
export type SearchSignals = z.infer<typeof SearchSignals>;

export const SearchHit = z.object({
  rank: z.number().int().positive(),
  docId: z.number().int(),
  ref: z.string(),
  kind: z.string(),
  tag: z.string().nullish(),
  title: z.string(),
  snippet: z.string().describe('≤ 260 chars'),
  score: Double,
  signals: SearchSignals,
  updated: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
  where: z.string().nullish().describe('bounds text, e.g. "nether 12,64,-3..18,70,4"'),
  raptorLeaf: z.number().int().nullish(),
  raptorPath: z.array(RaptorCrumb).describe('root → leaf'),
});
export type SearchHit = z.infer<typeof SearchHit>;

export const TreeHit = z.object({
  list: z.enum(['collapsed', 'traversal']),
  index: z.number().int(),
  layer: z.number().int().describe('0 = leaf'),
  score: Double,
  preview: z.string(),
});
export type TreeHit = z.infer<typeof TreeHit>;

export const SearchResponse = z.object({
  query: z.string(),
  mode: SearchMode,
  modeNote: z.string().describe('why this mode (no embedding config, vectors from another model, embed call failed…)'),
  embedded: z.boolean().describe('the query was embedded'),
  elapsedMs: z.number(),
  hits: z.array(SearchHit),
  treeHits: z.array(TreeHit),
});
export type SearchResponse = z.infer<typeof SearchResponse>;
