/**
 * Shards (explorer-spec §8): default packs, export (preview + download), import preview and apply.
 * C# sources: `src/MineClaw.Agent/Admin/Shards/ShardModels.cs`, `ShardExporter.cs`, `Packs/PackIndex.cs`.
 */
import { z } from 'zod';
import { IsoDateTime } from './common.js';
import { RebuildEstimate, RebuildRequest } from './admin.js';

export const ShardKind = z.enum([
  'fact',
  'claim',
  'rule',
  'trait',
  'lesson',
  'place',
  'structure',
  'episode',
  'knowledge',
  'gamedata',
]);
export type ShardKind = z.infer<typeof ShardKind>;

/** How a shard's records were chosen (C# ShardSelection). */
export const ShardSelection = z.object({
  mode: z.enum(['subtree', 'filter', 'person', 'pack']).default('filter'),
  node: z.number().int().nullish(),
  leafRefs: z.array(z.string()).nullish(),
  kinds: z.array(ShardKind).nullish(),
  dimension: z.number().int().nullish().describe('0 overworld, -1 nether'),
  query: z.string().nullish(),
  semantic: z.boolean().default(false),
  people: z.array(z.string()).nullish(),
  packVersion: z.string().nullish(),
  includeKnowledgeDocs: z.boolean().default(false),
  packMode: z.string().nullish(),
  knowledgeFiles: z.array(z.string()).nullish(),
  includeRules: z.boolean().default(false),
  includeWorld: z.boolean().default(false),
});
export type ShardSelection = z.infer<typeof ShardSelection>;

export const ShardPackInfo = z.object({
  kind: z.enum(['version', 'mode']),
  name: z.string(),
  version: z.string(),
  mode: z.string().nullish(),
  core: z.string().nullish(),
  files: z.array(z.string()),
  source: z.string().nullish(),
  sourceHash: z.string().nullish(),
  builder: z.string().nullish(),
  needsEmbedding: z.number().int(),
});
export type ShardPackInfo = z.infer<typeof ShardPackInfo>;

/** manifest.json of a `.mcshard` (C# ShardManifest). */
export const ShardManifest = z.object({
  format: z.number().int(),
  name: z.string(),
  description: z.string().nullish(),
  created: IsoDateTime,
  createdBy: z.string().nullish(),
  source: z.object({ db: z.string().nullish(), schemaVersion: z.number().int(), bot: z.string().nullish() }),
  kind: z.enum(['subtree', 'filter', 'person', 'pack']),
  selection: ShardSelection,
  packVersion: z.string().nullish(),
  counts: z.record(z.string(), z.number().int()),
  total: z.number().int(),
  contentHash: z.string(),
  embeddings: z
    .object({ model: z.string(), dims: z.number().int(), count: z.number().int(), sha256: z.string() })
    .nullish(),
  summaries: z.object({ count: z.number().int(), sha256: z.string() }).nullish(),
  secretsRedacted: z.number().int(),
  pack: ShardPackInfo.nullish(),
});
export type ShardManifest = z.infer<typeof ShardManifest>;

/** A default pack row (C# PackIndexEntry) plus its verification state. */
export const PackEntry = z.object({
  file: z.string(),
  name: z.string(),
  kind: z.enum(['version', 'mode']),
  version: z.string(),
  mode: z.string().nullish(),
  description: z.string().nullish(),
  counts: z.record(z.string(), z.number().int()),
  records: z.number().int(),
  bytes: z.number().int(),
  vectors: z.number().int(),
  needsEmbedding: z.number().int(),
  embeddingModel: z.string().nullish(),
  dims: z.number().int(),
  files: z.array(z.string()),
  source: z.string().nullish(),
  sourceHash: z.string().nullish(),
  contentHash: z.string().nullish(),
  builder: z.string().nullish(),
  kindLabel: z.string().describe('"knowledge pack b1.8.1", "mode pack skyblock (b1.8.1)"'),
  error: z.string().nullish().describe('file missing / manifest ContentHash differs from index.json'),
  active: z.boolean().describe('this pack (or mode) is active in the db'),
});
export type PackEntry = z.infer<typeof PackEntry>;

/** A `.mcshard` in the packs folder that is not in index.json (read from its manifest). */
export const ShardFileInfo = z.object({
  file: z.string(),
  bytes: z.number().int(),
  manifest: ShardManifest.nullish(),
  kindLabel: z.string(),
  error: z.string().nullish(),
});
export type ShardFileInfo = z.infer<typeof ShardFileInfo>;

/** GET /api/shards/packs. */
export const ShardPacksResponse = z.object({
  packs: z.array(PackEntry),
  extra: z.array(ShardFileInfo),
  activePack: z.string().nullish().describe('KnowledgePack.Active(db).Label'),
  activeModes: z.array(z.string()),
});
export type ShardPacksResponse = z.infer<typeof ShardPacksResponse>;

/** POST /api/shards/export/preview and POST /api/shards/export (C# ShardExportRequest). */
export const ShardExportRequest = z
  .object({
    name: z.string().min(1).max(128),
    description: z.string().max(2000).nullish(),
    selection: ShardSelection,
    includeEmbeddings: z.boolean().default(true),
    includeSummaries: z.boolean().default(true),
  })
  .strict();
export type ShardExportRequest = z.infer<typeof ShardExportRequest>;

/** C# ShardPreview. */
export const ShardPreview = z.object({
  counts: z.record(z.string(), z.number().int()),
  total: z.number().int(),
  vectors: z.number().int(),
  embeddingModel: z.string().nullish(),
  dims: z.number().int(),
  summaries: z.number().int(),
  bytes: z.number().int().describe('the exact file size'),
  secretsRedacted: z.number().int(),
  samples: z.array(z.string()),
  notes: z.array(z.string()),
  packVersion: z.string().nullish(),
});
export type ShardPreview = z.infer<typeof ShardPreview>;

export const ShardImportMode = z.enum(['merge', 'replace-section', 'swap-pack', 'add-mode', 'remove-mode']);
export type ShardImportMode = z.infer<typeof ShardImportMode>;
export const ShardConflictPolicy = z.enum(['keep-newer', 'prefer-shard', 'keep-mine']);
export type ShardConflictPolicy = z.infer<typeof ShardConflictPolicy>;

/**
 * The options part of the multipart import upload (POST /api/shards/import/preview, POST /api/admin/shard/import):
 * multipart/form-data with a `shard` file part (≤ 48 MB; the bridge base64-encodes it for the supervisor) and an `options`
 * part holding this JSON. `who` is never sent: the bridge sets it from `X-MCX-User`.
 */
export const ShardImportOptions = z
  .object({
    mode: ShardImportMode.default('merge'),
    conflict: ShardConflictPolicy.default('keep-newer'),
    includeRules: z.boolean().default(false),
    includeWorld: z.boolean().default(false),
    maxUsd: z.number().min(0).max(5).default(0.25).describe('cap for each RAPTOR section rebuild the import queues'),
  })
  .strict();
export type ShardImportOptions = z.infer<typeof ShardImportOptions>;

/** Max upload size of a `.mcshard` through Nest (the supervisor accepts 64 MB of base64). */
export const SHARD_UPLOAD_MAX_BYTES = 48 * 1024 * 1024;

export const ShardChange = z.enum(['added', 'changed', 'conflict', 'removed', 'unchanged', 'skipped']);
export type ShardChange = z.infer<typeof ShardChange>;

export const ShardDiffItem = z.object({
  id: z.string(),
  kind: z.string(),
  change: ShardChange,
  label: z.string(),
  apply: z.boolean().describe('false = shown "(kept)"'),
  note: z.string().nullish(),
});
export type ShardDiffItem = z.infer<typeof ShardDiffItem>;

/** C# ShardDiff. */
export const ShardDiff = z.object({
  shard: z.string(),
  mode: ShardImportMode,
  conflict: ShardConflictPolicy,
  items: z.array(ShardDiffItem),
  added: z.number().int(),
  changed: z.number().int(),
  conflicts: z.number().int(),
  removed: z.number().int(),
  unchanged: z.number().int(),
  skipped: z.number().int(),
  writes: z.number().int(),
  rebuild: z.array(RebuildRequest),
  estimate: RebuildEstimate.nullish(),
  reusableVectors: z.number().int(),
  toEmbed: z.number().int(),
  packFrom: z.string().nullish(),
  packTo: z.string().nullish(),
  modesFrom: z.array(z.string()).nullish(),
  modesTo: z.array(z.string()).nullish(),
  notes: z.array(z.string()),
  refused: z.string().nullish().describe('why the import cannot run; null = it can'),
});
export type ShardDiff = z.infer<typeof ShardDiff>;

/** Preview response: the diff plus the manifest that was read. */
export const ShardImportPreviewResponse = z.object({
  manifest: ShardManifest,
  diff: ShardDiff,
});
export type ShardImportPreviewResponse = z.infer<typeof ShardImportPreviewResponse>;

/** C# ShardImportResult. */
export const ShardImportResult = z.object({
  ok: z.boolean(),
  message: z.string(),
  diff: ShardDiff.nullish(),
  written: z.number().int(),
  removed: z.number().int(),
  reusedVectors: z.number().int(),
  embedded: z.number().int(),
  rebuild: z.array(RebuildRequest),
  jobId: z.string().nullish().describe('the background rebuild job (poll /api/admin/rebuild/:jobId)'),
  activePack: z.string().nullish(),
});
export type ShardImportResult = z.infer<typeof ShardImportResult>;
