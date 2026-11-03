/** Personality page (explorer-spec §7). Bridge-native: `/v1/personality?scope`. Read-only. */
import { z } from 'zod';
import { Double, UnixMs } from './common.js';

export const SelfSection = z.enum([
  'trait',
  'like',
  'dislike',
  'opinion',
  'joke',
  'nickname',
  'relationship',
  'milestone',
]);
export type SelfSection = z.infer<typeof SelfSection>;

export const PersonalityQuery = z.object({ scope: z.string().max(128).default('main') });
export type PersonalityQuery = z.infer<typeof PersonalityQuery>;

export const TraitEntry = z.object({
  id: z.number().int(),
  section: SelfSection,
  text: z.string(),
  player: z.string().nullish().describe('"about {player}"'),
  strength: Double.describe('0..1'),
  observations: z.number().int().describe('seen N×'),
  created: UnixMs.nullish(),
  reinforced: UnixMs.nullish(),
  reason: z.string().nullish(),
});
export type TraitEntry = z.infer<typeof TraitEntry>;

export const TraitGroup = z.object({ section: SelfSection, entries: z.array(TraitEntry) });
export type TraitGroup = z.infer<typeof TraitGroup>;

export const SelfHistoryEntry = z.object({
  version: z.number().int(),
  at: UnixMs,
  op: z.enum(['add', 'reinforce', 'adjust', 'remove']),
  section: z.string(),
  text: z.string(),
  reason: z.string().nullish(),
  source: z.string().nullish(),
});
export type SelfHistoryEntry = z.infer<typeof SelfHistoryEntry>;

export const MoodEntry = z.object({
  dim: z.string().describe('mood dimension'),
  value: Double.describe('-1..1 (sum/n)'),
  n: z.number().int(),
});
export type MoodEntry = z.infer<typeof MoodEntry>;

export const SelfAbstraction = z.object({
  layer: z.number().int(),
  idx: z.number().int(),
  text: z.string(),
  leaves: z.number().int(),
});
export type SelfAbstraction = z.infer<typeof SelfAbstraction>;

export const PersonalityResponse = z.object({
  scopes: z.array(z.string()).describe('distinct scopes; hive clones have their own'),
  scope: z.string(),
  groups: z.array(TraitGroup).describe('in section order'),
  history: z.array(SelfHistoryEntry).describe('newest first, ≤ 500'),
  mood: z.array(MoodEntry),
  abstractions: z.array(SelfAbstraction).describe('layer desc'),
  summary: z.string().describe('"N entries · M versions · last change … (source)"'),
});
export type PersonalityResponse = z.infer<typeof PersonalityResponse>;
