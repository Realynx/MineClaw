/**
 * Edits & audit (explorer-spec §11, supervisor `/api/admin/*`). C# sources: `src/MineClaw.Agent/Admin/AdminModels.cs`,
 * `MemoryEditSchema.cs`, `ActivityCompaction.cs`. `who` is never part of a public request: the bridge sets it from `X-MCX-User`.
 */
import { z } from 'zod';
import { Double, IsoDateTime, QInt } from './common.js';
import { EditKind } from './memory.js';

/** One field of the generic edit form (C# EditField). */
export const EditField = z.object({
  name: z.string(),
  label: z.string(),
  type: z.enum(['text', 'multiline', 'int', 'number', 'enum', 'bool']),
  required: z.boolean().default(false).describe('required when adding'),
  options: z.array(z.string()).nullish(),
  help: z.string().nullish(),
});
export type EditField = z.infer<typeof EditField>;

export const EditOp = z.enum(['add', 'update', 'delete']);
export type EditOp = z.infer<typeof EditOp>;

export const EditKindSchema = z.object({
  kind: EditKind,
  ops: z.array(EditOp).describe('observation: update/delete only'),
  fields: z.array(EditField),
});
export type EditKindSchema = z.infer<typeof EditKindSchema>;

/** GET /api/admin/schema. */
export const EditSchemaResponse = z.object({
  kinds: z.array(EditKindSchema),
  reasonField: z.literal('__reason').describe('hidden field carrying the edit reason'),
});
export type EditSchemaResponse = z.infer<typeof EditSchemaResponse>;

/**
 * POST /api/admin/memory (C# MemoryEdit minus `who`). `fields`: absent/null keeps a value, "" clears an optional field; update
 * sends only changed fields. Delete needs only `id`.
 */
export const MemoryEditRequest = z
  .object({
    op: EditOp,
    kind: EditKind,
    id: z.string().max(256).nullish(),
    fields: z.record(z.string(), z.string().nullable()).default({}),
    reason: z.string().max(1000).nullish(),
  })
  .strict()
  .refine((e) => e.op === 'add' || (e.id !== undefined && e.id !== null && e.id !== ''), {
    message: 'id is required for update and delete',
    path: ['id'],
  });
export type MemoryEditRequest = z.infer<typeof MemoryEditRequest>;

/** C# MemoryEditResult (also the 400 body when the store refuses). */
export const MemoryEditResult = z.object({
  ok: z.boolean(),
  message: z.string(),
  id: z.string().nullish(),
  before: z.string().nullish().describe('compact JSON before (null for add)'),
  after: z.string().nullish().describe('compact JSON after (null for delete/failure)'),
  reembedded: z.boolean().default(false),
});
export type MemoryEditResult = z.infer<typeof MemoryEditResult>;

/** C# RebuildRequest (public form: no `who`). */
export const RebuildRequest = z.object({
  scope: z.enum(['node', 'kind']),
  node: z.number().int().nullish(),
  kind: z.string().nullish().describe('episode, gamedata, fact, … (MEMORY_REBUILD_KIND)'),
  maxUsd: z.number().min(0).max(20).default(0.5),
});
export type RebuildRequest = z.infer<typeof RebuildRequest>;
export const RebuildRequestBody = RebuildRequest.strict();

/** C# RebuildEstimate. "Nothing to rebuild" when summaries = 0. Confirm cap = max(0.05, ceil(usd·1.5 cents)). */
export const RebuildEstimate = z.object({
  summaries: z.number().int(),
  inputTokens: z.number().int(),
  outputTokens: z.number().int(),
  embeddingTokens: z.number().int(),
  usd: z.number(),
  model: z.string(),
  note: z.string(),
});
export type RebuildEstimate = z.infer<typeof RebuildEstimate>;

/** The cost cap the UI confirms and sends as `maxUsd`. */
export function rebuildCap(estimateUsd: number): number {
  return Math.max(0.05, Math.ceil(estimateUsd * 1.5 * 100) / 100);
}

export const RebuildStarted = z.object({ jobId: z.string() });
export type RebuildStarted = z.infer<typeof RebuildStarted>;

export const RebuildJobParams = z.object({ jobId: z.string().min(1).max(128) });

export const SummaryDiff = z.object({
  index: z.number().int(),
  layer: z.number().int(),
  old: z.string().nullish().describe('null = new node'),
  new: z.string().nullish().describe('null = removed'),
});
export type SummaryDiff = z.infer<typeof SummaryDiff>;

/** GET /api/admin/rebuild/:jobId (poll every 1.5 s). `elapsed` is a .NET TimeSpan string ("00:00:12.3456789"). */
export const RebuildJob = z.object({
  state: z.enum(['running', 'done', 'failed', 'cancelled']),
  progress: z
    .object({ stage: z.string(), done: z.number().int(), total: z.number().int(), message: z.string().nullish() })
    .nullish(),
  result: z
    .object({
      ok: z.boolean(),
      message: z.string(),
      diffs: z.array(SummaryDiff),
      spentUsd: z.number(),
      elapsed: z.union([z.string(), z.number()]),
    })
    .nullish(),
  error: z.string().nullish(),
});
export type RebuildJob = z.infer<typeof RebuildJob>;

/** POST /api/admin/backup. `path` is the server-side backup file name (informational). */
export const BackupResponse = z.object({ path: z.string() });
export type BackupResponse = z.infer<typeof BackupResponse>;

/** C# AuditEntry (the bot's admin-audit.jsonl). */
export const AdminAuditEntry = z.object({
  at: IsoDateTime,
  who: z.string().describe('"fox (web)" — the bridge sets it from X-MCX-User'),
  target: z.string().describe('"local:{db}" or "live:{bot}"'),
  op: z.string().describe('add | update | delete | rebuild | backup | compact | import | export'),
  kind: z.string(),
  id: z.string().nullish(),
  before: z.string().nullish(),
  after: z.string().nullish(),
  reason: z.string().nullish(),
  ok: z.boolean(),
  message: z.string(),
});
export type AdminAuditEntry = z.infer<typeof AdminAuditEntry>;

export const AdminAuditQuery = z.object({ max: QInt.min(1).max(1000).default(300) });
export const AdminAuditResponse = z.object({ entries: z.array(AdminAuditEntry) });
export type AdminAuditResponse = z.infer<typeof AdminAuditResponse>;

/** POST /api/admin/activity/compact. */
export const CompactRequest = z.object({ dryRun: z.boolean().default(true) }).strict();
export type CompactRequest = z.infer<typeof CompactRequest>;

/** C# ActivityCompactionReport. */
export const ActivityCompactionReport = z.object({
  ok: z.boolean(),
  dryRun: z.boolean(),
  docsBefore: z.number().int(),
  docsAfter: z.number().int(),
  activityBefore: z.number().int(),
  activityAfter: z.number().int(),
  processed: z.number().int(),
  removed: z.number().int(),
  rewritten: z.number().int(),
  episodes: z.number().int(),
  dirtySummaries: z.number().int(),
  message: z.string(),
  samples: z.array(z.string()),
  backup: z.string().nullish(),
});
export type ActivityCompactionReport = z.infer<typeof ActivityCompactionReport>;

/** POST /api/groom/preview (read-only dry run of the bot's grooming pipeline; optional LLM rewrite costs money → admin). */
export const GroomSource = z.enum([
  'paste',
  'log',
  'activity',
  'episode',
  'journal',
  'memory',
  'structure',
  'rule',
  'claim',
  'hive',
]);
export type GroomSource = z.infer<typeof GroomSource>;

export const GroomPreviewRequest = z
  .object({
    source: GroomSource,
    text: z.string().max(200_000).nullish().describe('pasted text'),
    docRef: z.string().max(256).nullish().describe('act:…, ep:…'),
    rewrite: z.boolean().default(false).describe('run the LLM rewriter when Agent:Memory:Groom:Rewrite:Enabled'),
  })
  .strict();
export type GroomPreviewRequest = z.infer<typeof GroomPreviewRequest>;

export const GroomedDoc = z.object({
  kind: z.string(),
  source: z.string(),
  ref: z.string(),
  title: z.string(),
  body: z.string(),
});
export const GroomPreviewResponse = z.object({
  docs: z.array(GroomedDoc),
  inCount: z.number().int(),
  outCount: z.number().int(),
  stages: z.array(
    z.object({ stage: z.string(), note: z.string().nullish(), in: z.number().int(), out: z.number().int() }),
  ),
  rewriteCostUsd: Double.nullish(),
  note: z.string().nullish(),
});
export type GroomPreviewResponse = z.infer<typeof GroomPreviewResponse>;
