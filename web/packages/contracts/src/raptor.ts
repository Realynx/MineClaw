/**
 * RAPTOR tree and graph (explorer-spec §4). Bridge-native: `/v1/raptor/tree`, `/v1/raptor/node/{id}`,
 * `/v1/raptor/node/{id}/leaves`, `/v1/raptor/graph`. Node ids are RAPTOR indexes (`docs.raptor_idx`).
 */
import { z } from 'zod';
import { Double, QInt, UnixMs } from './common.js';
import { RaptorMeta } from './overview.js';
import { RaptorCrumb } from './search.js';

export const RaptorNodeParams = z.object({ id: QInt.min(0) });

/** One tree row. */
export const RaptorTreeNode = z.object({
  index: z.number().int(),
  layer: z.number().int().describe('0 = leaf'),
  isLeaf: z.boolean(),
  title: z.string().describe('leaf: "docKind · title"; summary: first 90 chars'),
  kindLabel: z.string().describe('"L2" or the leaf doc kind'),
  tokens: z.number().int(),
  childCount: z.number().int(),
  leavesBelow: z.number().int(),
  dirty: z.boolean(),
  parents: z.array(z.number().int()).describe('all parents (multi-parent soft clusters); primary first'),
  children: z
    .array(z.number().int())
    .nullish()
    .describe('child indexes sorted by children desc, then index; present only when within the requested depth'),
  docRef: z.string().nullish(),
});
export type RaptorTreeNode = z.infer<typeof RaptorTreeNode>;

/** GET /api/raptor/tree. `root` omitted = the roots; `depth` = levels below root to include (1 = root + its children). */
export const RaptorTreeQuery = z.object({
  root: QInt.min(0).optional(),
  depth: QInt.min(0).max(8).default(1),
});
export type RaptorTreeQuery = z.infer<typeof RaptorTreeQuery>;

export const RaptorTreeResponse = z.object({
  meta: RaptorMeta.nullish(),
  note: z.string().nullish().describe('set when there is no tree'),
  stateKey: z.string().describe('changes whenever the tree changes (cache key for graph/labels)'),
  root: z.number().int().nullish(),
  top: z.array(z.number().int()).describe('the top-level indexes of this response (roots, or [root])'),
  nodes: z.array(RaptorTreeNode).describe('flat; follow `children`'),
});
export type RaptorTreeResponse = z.infer<typeof RaptorTreeResponse>;

export const RaptorNodeRef = z.object({
  index: z.number().int(),
  layer: z.number().int(),
  label: z.string(),
  tokens: z.number().int(),
});
export type RaptorNodeRef = z.infer<typeof RaptorNodeRef>;

export const RaptorDocInfo = z.object({
  id: z.number().int(),
  ref: z.string(),
  kind: z.string(),
  tag: z.string().nullish(),
  title: z.string().nullish(),
  source: z.string().nullish(),
  updated: UnixMs.nullish(),
  dimension: z.number().int().nullish(),
});
export type RaptorDocInfo = z.infer<typeof RaptorDocInfo>;

/** GET /api/raptor/node/:id. */
export const RaptorNodeDetail = z.object({
  node: RaptorTreeNode,
  title: z.string().describe('leaf: doc title; summary: "Layer L summary #i"'),
  text: z.string(),
  pinned: z.boolean(),
  placeholder: z.boolean().describe('summary text is a placeholder (not yet summarized)'),
  doc: RaptorDocInfo.nullish(),
  breadcrumb: z.array(RaptorCrumb).describe('root → node'),
  children: z.array(RaptorNodeRef).describe('sorted by tokens desc'),
  parents: z.array(RaptorNodeRef),
});
export type RaptorNodeDetail = z.infer<typeof RaptorNodeDetail>;

export const RaptorLeaf = z.object({
  index: z.number().int(),
  docRef: z.string(),
  kind: z.string(),
  title: z.string(),
  tokens: z.number().int(),
});
export type RaptorLeaf = z.infer<typeof RaptorLeaf>;

/** GET /api/raptor/node/:id/leaves: every leaf below the node, deduplicated (also the shard "subtree" leaf refs). */
export const RaptorLeavesResponse = z.object({
  node: z.number().int(),
  leaves: z.array(RaptorLeaf),
  tokenSum: z.number().int(),
});
export type RaptorLeavesResponse = z.infer<typeof RaptorLeavesResponse>;

// ---- graph ----

export const GraphLayout = z.enum(['icicle', 'radial', 'nodelink']);
export type GraphLayout = z.infer<typeof GraphLayout>;
export const GraphColorBy = z.enum(['kind', 'time', 'cluster']);
export type GraphColorBy = z.infer<typeof GraphColorBy>;
export const GraphCategory = z.enum(['Episode', 'Activity', 'Fact', 'Recipe', 'Chat', 'World', 'Self', 'Other']);
export type GraphCategory = z.infer<typeof GraphCategory>;

/** GET /api/raptor/graph. `focus` = a node index whose subtree is laid out alone (node-link is best with one). */
export const RaptorGraphQuery = z.object({
  layout: GraphLayout.default('icicle'),
  colorBy: GraphColorBy.default('kind'),
  focus: QInt.min(0).optional(),
});
export type RaptorGraphQuery = z.infer<typeof RaptorGraphQuery>;

/**
 * A laid-out node. Coordinates are in a unit layout space: x ∈ [0,1] left→right, y ∈ [0,1] top→bottom (roots on top for
 * icicle; the centre is (0.5,0.5) for radial). `x,y` is always the node's centre/anchor; the shape fields depend on the layout:
 * icicle → `w,h` (rectangle centred on x,y); radial → `a0,a1` (radians, 0 = up, clockwise) and `r0,r1` (0..0.5);
 * nodelink → `r` (circle radius).
 */
export const GraphNode = z.object({
  id: z.number().int().describe('RAPTOR index'),
  parent: z.number().int().nullish().describe('primary parent; null = root (or the focus root)'),
  layer: z.number().int(),
  isLeaf: z.boolean(),
  x: z.number(),
  y: z.number(),
  w: z.number().nullish(),
  h: z.number().nullish(),
  a0: z.number().nullish(),
  a1: z.number().nullish(),
  r0: z.number().nullish(),
  r1: z.number().nullish(),
  r: z.number().nullish(),
  label: z.string().describe('topic label (TF-IDF terms, deterministic) or the leaf title'),
  terms: z.array(z.string()),
  category: GraphCategory.describe('majority category of the leaves below'),
  newest: UnixMs.nullish().describe('newest leaf time below'),
  timeRank: Double.describe('0..1 time-quantile rank of `newest`'),
  cluster: z.number().int().describe('root cluster index'),
  weight: z.number().int().describe('leaf count'),
  tokens: z.number().int(),
  color: z
    .string()
    .regex(/^#[0-9a-fA-F]{6}$/)
    .describe('server colour for the requested colorBy'),
  opacity: Double.describe('0..1, follows tokens'),
});
export type GraphNode = z.infer<typeof GraphNode>;

export const GraphEdge = z.object({
  source: z.number().int().describe('parent index'),
  target: z.number().int().describe('child index'),
  primary: z.boolean().describe('false = an extra soft-cluster parent link'),
  weight: Double,
});
export type GraphEdge = z.infer<typeof GraphEdge>;

export const GraphLegendEntry = z.object({ key: z.string(), label: z.string(), color: z.string() });
export type GraphLegendEntry = z.infer<typeof GraphLegendEntry>;

export const RaptorGraph = z.object({
  layout: GraphLayout,
  colorBy: GraphColorBy,
  stateKey: z
    .string()
    .describe('the tree state key the layout was computed for (memory-activity heat uses the same ids)'),
  focus: z.number().int().nullish(),
  breadcrumb: z.array(RaptorCrumb).describe('"all memory › L3 term › …" for the focus'),
  nodes: z.array(GraphNode),
  edges: z.array(GraphEdge),
  legend: z.array(GraphLegendEntry),
});
export type RaptorGraph = z.infer<typeof RaptorGraph>;
