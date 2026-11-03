/** AI vision sheets (explorer-spec §12.7). C# source: `src/MineClaw.Bot/Perception/Vision/VisionHistory.cs` (VisionSheetInfo). */
import { z } from 'zod';
import { IsoDateTime, QBool, QInt } from './common.js';

export const VisionSheetInfo = z.object({
  id: z.number().int(),
  at: IsoDateTime,
  body: z.string(),
  kind: z.string(),
  source: z.string(),
  mode: z.string().describe('perception | brain | …'),
  views: z.array(z.string()).describe('"eye", "iso yaw=200 pitch=50"…'),
  focus: z.array(z.number().int()).nullish().describe('[x,y,z]'),
  radius: z.number().int(),
  question: z.string().nullish(),
  call: z.string().nullish().describe('the `see` tool input JSON'),
  width: z.number().int(),
  height: z.number().int(),
  tokens: z.number().int(),
  bytes: z.number().int(),
  sha256: z.string(),
  legend: z.string(),
  answer: z.string().nullish().describe('perception answer; null until it arrives / in direct mode'),
  note: z.string().nullish(),
  model: z.string().nullish(),
  modelInputTokens: z.number().int().nullish(),
  modelOutputTokens: z.number().int().nullish(),
});
export type VisionSheetInfo = z.infer<typeof VisionSheetInfo>;

export const VisionListQuery = z.object({ since: QInt.min(0).optional() });

/** GET /api/bodies/:body/vision. */
export const VisionList = z.object({
  at: IsoDateTime,
  body: z.string(),
  capacity: z.number().int(),
  note: z.string().nullish(),
  sheets: z.array(VisionSheetInfo),
});
export type VisionList = z.infer<typeof VisionList>;

export const VisionSheetParams = z.object({ body: z.string().min(1).max(64), id: QInt.min(0) });

/** The `see` input (also what "Copy as see{…}" produces). View strings: `kind[ at][ k=v …]`, keys yaw pitch zoom dist dx dy dz axis. */
export const SeeCall = z.object({
  views: z.array(z.string().max(200)).min(1).max(12),
  focus: z.string().max(64).describe('"x,y,z"'),
  radius: z.number().int().min(3).max(12),
  aids: z.boolean().optional(),
  question: z.string().max(1000).optional(),
});
export type SeeCall = z.infer<typeof SeeCall>;

/** GET /api/bodies/:body/vision/render?see=<urlencoded SeeCall JSON>[&meta=1] → PNG (or ViewMeta with meta=1). */
export const VisionRenderQuery = z.object({
  see: z.string().min(2).max(4000).describe('JSON of SeeCall'),
  meta: QBool.optional(),
});

/** `meta=1` form of a render (supervisor ViewImage metadata). */
export const ViewMeta = z.object({
  width: z.number().int(),
  height: z.number().int(),
  tokens: z.number().int(),
  bytes: z.number().int().nullish(),
  legend: z.string(),
  facts: z.array(z.string()),
  seen: z.string().nullish(),
  note: z.string().nullish(),
});
export type ViewMeta = z.infer<typeof ViewMeta>;
