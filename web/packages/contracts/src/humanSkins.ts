import { z } from 'zod';
import { QInt } from './common.js';

/** Deliberately separate from world entity / vision payloads: these resources are for human browsers only. */
export const HumanSkinSettings = z.object({ enabled: z.boolean(), cached: z.number().int().min(0), unavailable: z.number().int().min(0) });
export const HumanSkinSave = z.object({ enabled: z.boolean(), reason: z.string().trim().min(3).max(300) }).strict();
export type HumanSkinSave = z.infer<typeof HumanSkinSave>;
export const HumanSkinQuery = z.object({ epoch: z.string().max(64).optional(), since: QInt.min(0).default(0) });
export const HumanSkinFeed = z.object({
  enabled: z.boolean(), epoch: z.string(), revision: z.number().int().min(0),
  skins: z.array(z.object({ name: z.string().regex(/^[a-z0-9_]{1,16}$/), slim: z.boolean(), height: z.union([z.literal(32), z.literal(64)]), png: z.string().max(87384) })).max(512),
});
