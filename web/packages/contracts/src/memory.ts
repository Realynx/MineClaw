/** Memory browser (explorer-spec §6). Bridge-native: `/v1/memory/kinds`, `/v1/memory/{kind}`, `/v1/memory/{kind}/{id}`. */
import { z } from 'zod';
import { DimFilter, Double, JsonValue, QInt, UnixMs } from './common.js';

/** The 16 browser kinds, in nav order. */
export const MemoryKindKey = z.enum([
  'facts',
  'claims',
  'beliefs',
  'personality',
  'structures',
  'places',
  'chests',
  'gencells',
  'observed',
  'lessons',
  'episodes',
  'activity',
  'chat',
  'routines',
  'recipes',
  'knowledge',
]);
export type MemoryKindKey = z.infer<typeof MemoryKindKey>;
export const MEMORY_KINDS = MemoryKindKey.options;

/** Edit kinds of `/api/admin/memory` (MemoryEditSchema). */
export const EditKind = z.enum(['fact', 'claim', 'trait', 'structure', 'place', 'observation', 'lesson']);
export type EditKind = z.infer<typeof EditKind>;

/** Browser kind → edit kind (null = read-only kind). */
export const MEMORY_EDIT_KIND: Readonly<Record<MemoryKindKey, EditKind | null>> = {
  facts: 'fact',
  claims: 'claim',
  beliefs: 'claim',
  personality: 'trait',
  structures: 'structure',
  places: 'place',
  chests: 'observation',
  gencells: 'observation',
  observed: 'observation',
  lessons: 'lesson',
  episodes: null,
  activity: null,
  chat: null,
  routines: null,
  recipes: null,
  knowledge: null,
};

/** Browser kind → RAPTOR rebuild kind (`RebuildRequest.kind`), null = not in the tree. */
export const MEMORY_REBUILD_KIND: Readonly<Partial<Record<MemoryKindKey, string>>> = {
  facts: 'fact',
  claims: 'claim',
  beliefs: 'claim',
  structures: 'structure',
  places: 'place',
  lessons: 'lesson',
  observed: 'observation',
  chests: 'observation',
  gencells: 'observation',
  episodes: 'episode',
  recipes: 'gamedata',
  knowledge: 'knowledge',
};

export const MemoryKindInfo = z.object({
  key: MemoryKindKey,
  label: z.string(),
  group: z.string().describe('nav group heading'),
  count: z.number().int().nonnegative().describe('0 when the table is missing'),
  available: z.boolean().describe('false when the table is missing in this db'),
  editKind: EditKind.nullish(),
  states: z.array(z.string()).describe('distinct state values present (for the state filter)'),
});
export type MemoryKindInfo = z.infer<typeof MemoryKindInfo>;

export const MemoryKindsResponse = z.object({ kinds: z.array(MemoryKindInfo) });
export type MemoryKindsResponse = z.infer<typeof MemoryKindsResponse>;

export const MemorySort = z.enum(['updated', 'name', 'type', 'state', 'dim', 'conf', 'id']);
export type MemorySort = z.infer<typeof MemorySort>;

/** GET /api/memory/:kind query. `q`: every word must appear in `id name type state text` (case-insensitive). */
export const MemoryListQuery = z.object({
  q: z.string().max(500).optional(),
  sort: MemorySort.default('updated'),
  dir: z.enum(['asc', 'desc']).default('desc'),
  dim: DimFilter.default('any'),
  state: z.string().max(64).optional(),
  offset: QInt.min(0).default(0),
  limit: QInt.min(1).max(500).default(100),
});
export type MemoryListQuery = z.infer<typeof MemoryListQuery>;

export const MemoryKindParams = z.object({ kind: MemoryKindKey });
export const MemoryItemParams = z.object({ kind: MemoryKindKey, id: z.string().min(1).max(256) });

/** One grid row (the same columns for every kind). */
export const MemoryRow = z.object({
  id: z.string().describe('stable id within the kind (row id, or name for structures/places/routines)'),
  name: z.string(),
  type: z.string().nullish(),
  state: z.string().nullish(),
  dim: z.enum(['overworld', 'nether', '']).describe('"" = no position'),
  conf: Double.nullish(),
  updated: UnixMs.nullish(),
  preview: z.string().describe('text preview, ≤ 200 chars'),
  docRef: z.string().nullish().describe('note:12, st:tower, wp:home… when the row has a RAG doc'),
  raptorLeaf: z.number().int().nullish().describe('RAPTOR leaf index when the doc is in the tree'),
});
export type MemoryRow = z.infer<typeof MemoryRow>;

export const MemoryPage = z.object({
  kind: MemoryKindKey,
  total: z.number().int().nonnegative().describe('rows matching the filter'),
  offset: z.number().int().nonnegative(),
  limit: z.number().int().positive(),
  items: z.array(MemoryRow),
});
export type MemoryPage = z.infer<typeof MemoryPage>;

// ---- per-kind raw records (the detail pane). Column names follow explorer-spec §6, camelCased. Times are unix ms. ----

const Pos = {
  dimension: z.number().int().nullish(),
  x: z.number().nullish(),
  y: z.number().nullish(),
  z: z.number().nullish(),
};

export const FactRecord = z.object({
  kind: z.literal('facts'),
  id: z.number().int(),
  subject: z.string(),
  text: z.string(),
  created: UnixMs.nullish(),
});

export const ClaimEvidence = z.object({
  at: UnixMs,
  kind: z.string(),
  polarity: z.number(),
  weight: Double,
  ref: z.string().nullish(),
  note: z.string().nullish(),
});
export type ClaimEvidence = z.infer<typeof ClaimEvidence>;

const claimShape = {
  id: z.number().int(),
  claimant: z.string(),
  text: z.string(),
  category: z.string().nullish(),
  stakes: z.string().nullish(),
  confidence: Double.nullish(),
  status: z.string().describe('unverified | verified | disputed | refuted | provisional'),
  reason: z.string().nullish(),
  plan: z.string().nullish(),
  risk: z.string().nullish(),
  directive: z.union([z.boolean(), z.number()]).nullish(),
  attempts: z.number().int().nullish(),
  checked: UnixMs.nullish(),
  created: UnixMs.nullish(),
  updated: UnixMs.nullish(),
  cloneId: z.string().nullish(),
  ...Pos,
  evidence: z.array(ClaimEvidence),
};
export const ClaimRecord = z.object({ kind: z.literal('claims'), ...claimShape });
export const BeliefRecord = z.object({ kind: z.literal('beliefs'), ...claimShape });

export const TraitRecord = z.object({
  kind: z.literal('personality'),
  id: z.number().int(),
  section: z.string(),
  player: z.string().nullish(),
  text: z.string(),
  strength: Double,
  created: UnixMs.nullish(),
  reinforced: UnixMs.nullish(),
  reason: z.string().nullish(),
  scope: z.string().nullish(),
  observations: z.number().int().nullish(),
});

export const StructureRecord = z.object({
  kind: z.literal('structures'),
  name: z.string(),
  structureKind: z.string().nullish().describe('the `structures.kind` column'),
  taughtBy: z.string().nullish(),
  confidence: Double.nullish(),
  data: JsonValue.describe('camelCase JSON `Structure` {min,max,note,steps,keyBlocks,history}; opaque'),
  updated: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
});

export const PlaceRecord = z.object({
  kind: z.literal('places'),
  name: z.string(),
  x: z.number(),
  y: z.number(),
  z: z.number(),
  note: z.string().nullish(),
  updated: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
});

export const ContainerItem = z.object({
  name: z.string(),
  count: z.number().int(),
  damage: z.number().int().nullish(),
});
export type ContainerItem = z.infer<typeof ContainerItem>;

const observationShape = {
  id: z.number().int(),
  observationKind: z.string().describe('the `observations.kind` column (chest, cobble_generator, portal…)'),
  key: z.string().nullish(),
  minX: z.number().int().nullish(),
  minY: z.number().int().nullish(),
  minZ: z.number().int().nullish(),
  maxX: z.number().int().nullish(),
  maxY: z.number().int().nullish(),
  maxZ: z.number().int().nullish(),
  text: z.string().nullish(),
  source: z.string().nullish(),
  confidence: Double.nullish(),
  annotation: z.string().nullish(),
  firstSeen: UnixMs.nullish(),
  lastSeen: UnixMs.nullish(),
  seenCount: z.number().int().nullish(),
  removed: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
  contentsAt: UnixMs.nullish(),
  contentsBy: z.string().nullish(),
};
export const ChestRecord = z.object({
  kind: z.literal('chests'),
  ...observationShape,
  contents: z.array(ContainerItem),
});
export const GenCellRecord = z.object({ kind: z.literal('gencells'), ...observationShape });
export const ObservedRecord = z.object({ kind: z.literal('observed'), ...observationShape });

export const LessonRecord = z.object({
  kind: z.literal('lessons'),
  id: z.string(),
  title: z.string(),
  correction: z.string(),
  source: z.string().nullish(),
  updated: UnixMs.nullish(),
});

const docShape = {
  id: z.number().int(),
  ref: z.string(),
  docKind: z.string().describe('the `docs.kind` column'),
  tag: z.string().nullish(),
  title: z.string().nullish(),
  body: z.string(),
  source: z.string().nullish(),
  updated: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
  minX: z.number().int().nullish(),
  raptorIdx: z.number().int().nullish(),
};
export const EpisodeRecord = z.object({ kind: z.literal('episodes'), ...docShape });
export const RecipeRecord = z.object({ kind: z.literal('recipes'), ...docShape });
export const KnowledgeRecord = z.object({ kind: z.literal('knowledge'), ...docShape });

export const ActivityRecord = z.object({
  kind: z.literal('activity'),
  id: z.number().int(),
  at: UnixMs,
  player: z.string().nullish(),
  activityKind: z.string().describe('the `activity_log.kind` column'),
  category: z.string().nullish(),
  text: z.string(),
  placeName: z.string().nullish(),
  ...Pos,
  confidence: Double.nullish(),
});

export const ChatRecord = z.object({
  kind: z.literal('chat'),
  id: z.number().int(),
  at: UnixMs,
  chatKind: z.string().describe('chat | whisper | emote | join | leave | bot | bot_whisper | viewer_chat | test_*'),
  player: z.string().nullish(),
  text: z.string(),
});

export const RoutineRecord = z.object({
  kind: z.literal('routines'),
  name: z.string(),
  routineType: z.enum(['script', 'routine']).describe('"script" when data.script.code is non-empty'),
  data: JsonValue.describe('JSON `Routine`; opaque'),
  updated: UnixMs.nullish(),
});

/** The detail record, discriminated by browser kind. */
export const MemoryRecord = z.discriminatedUnion('kind', [
  FactRecord,
  ClaimRecord,
  BeliefRecord,
  TraitRecord,
  StructureRecord,
  PlaceRecord,
  ChestRecord,
  GenCellRecord,
  ObservedRecord,
  LessonRecord,
  EpisodeRecord,
  ActivityRecord,
  ChatRecord,
  RoutineRecord,
  RecipeRecord,
  KnowledgeRecord,
]);
export type MemoryRecord = z.infer<typeof MemoryRecord>;

export const MemoryProp = z.object({ key: z.string(), label: z.string(), value: z.string().nullish() });
export type MemoryProp = z.infer<typeof MemoryProp>;

/** GET /api/memory/:kind/:id. */
export const MemoryDetail = z.object({
  kind: MemoryKindKey,
  id: z.string(),
  row: MemoryRow,
  props: z.array(MemoryProp).describe('the props table, in display order'),
  record: MemoryRecord,
  editKind: EditKind.nullish(),
  editPrefill: z
    .record(z.string(), z.string().nullish())
    .nullish()
    .describe('field name → current value for the edit form (EditPrefill)'),
  people: z
    .array(z.string())
    .describe('player / claimant / subject values, to prefill "export everything about this person"'),
});
export type MemoryDetail = z.infer<typeof MemoryDetail>;
