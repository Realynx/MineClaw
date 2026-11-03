/**
 * Designs (explorer-spec §10): saved blueprints, their revisions (Git-style) and diffs, read from the db by the bridge.
 * The live supervisor blueprint routes (`/api/blueprints*`) are in live.ts.
 */
import { z } from 'zod';
import { QInt, UnixMs } from './common.js';

export const DesignListQuery = z.object({
  q: z.string().max(200).optional().describe('filter over id, name, status, requester'),
  offset: QInt.min(0).default(0),
  limit: QInt.min(1).max(500).default(100),
});
export type DesignListQuery = z.infer<typeof DesignListQuery>;

export const DesignSummary = z.object({
  id: z.number().int(),
  name: z.string(),
  status: z.string(),
  requester: z.string().nullish(),
  rev: z.number().int().describe('max(rev)'),
  revisions: z.number().int(),
  updated: UnixMs.nullish(),
  hasPicture: z.boolean(),
  version: z
    .string()
    .nullish()
    .describe('the game version it is drawn for ("b1.8.1" | "1.12.2"; a design saved before versions: the server\'s)'),
});
export type DesignSummary = z.infer<typeof DesignSummary>;

export const DesignListResponse = z.object({
  total: z.number().int(),
  revisionsTotal: z.number().int(),
  offset: z.number().int(),
  limit: z.number().int(),
  items: z.array(DesignSummary),
});
export type DesignListResponse = z.infer<typeof DesignListResponse>;

export const DesignParams = z.object({ id: QInt.min(1) });

export const DesignRevision = z.object({
  rev: z.number().int(),
  author: z.string().nullish(),
  authorKind: z.string().nullish(),
  message: z.string().nullish(),
  tag: z.string().nullish(),
  created: UnixMs,
  n: z.number().int().describe('block count'),
  version: z.string().nullish().describe('the game version this revision was saved for'),
});
export type DesignRevision = z.infer<typeof DesignRevision>;

export const DesignWorldNote = z.object({
  rev: z.number().int(),
  seen: UnixMs,
  seenBy: z.string().nullish(),
  diff: z.string().nullish(),
  causes: z.string().nullish(),
  text: z.string().describe('"last seen by X at rN: diff (causes)"'),
});
export type DesignWorldNote = z.infer<typeof DesignWorldNote>;

export const DesignDetail = z.object({
  design: DesignSummary,
  revisions: z.array(DesignRevision).describe('newest first'),
  world: DesignWorldNote.nullish(),
  defaultDiff: z
    .object({ a: z.string(), b: z.string() })
    .describe('HEAD→world when seen, else HEAD~1→HEAD (rev numbers or "world")'),
});
export type DesignDetail = z.infer<typeof DesignDetail>;

/** GET /api/designs/:id/diff?a&b. `a`,`b` = a revision number or "world" (the world as last seen); default = defaultDiff. */
export const DesignDiffQuery = z.object({
  a: z.union([z.literal('world'), z.string().regex(/^\d+$/)]).optional(),
  b: z.union([z.literal('world'), z.string().regex(/^\d+$/)]).optional(),
  max: QInt.min(1).max(5000).default(500),
});
export type DesignDiffQuery = z.infer<typeof DesignDiffQuery>;

export const DesignDiffCell = z.object({
  op: z.enum(['+', '-', '~']),
  x: z.number().int(),
  y: z.number().int(),
  z: z.number().int(),
  name: z.string().describe('block name[:meta] (version-specific names)'),
  from: z.string().nullish().describe('~ only'),
  to: z.string().nullish().describe('~ only'),
});
export type DesignDiffCell = z.infer<typeof DesignDiffCell>;

export const DesignDiff = z.object({
  id: z.number().int(),
  a: z.string(),
  b: z.string(),
  added: z.number().int(),
  removed: z.number().int(),
  changed: z.number().int(),
  unseen: z.number().int(),
  summary: z
    .string()
    .describe('"+12 stone, −3 planks, ~2 oak stairs facing (a added, r removed, c changed, u not seen)"'),
  cells: z.array(DesignDiffCell),
  total: z.number().int(),
  truncated: z.boolean(),
  text: z.string().describe('the diff as text (Copy)'),
});
export type DesignDiff = z.infer<typeof DesignDiff>;
