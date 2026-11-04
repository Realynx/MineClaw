/**
 * A synthetic memory cloud for the mocks (MSW and the Nest mock bridge), laid out the way the bridge's is: clusters at the
 * highest summary layer with ≥ 6 nodes, leaves in Gaussian islands per cluster (sub-islands per lower summary), summaries at
 * the mean of their children. `extraLeaves` adds that many leaves (ids from `extraFrom`) under existing layer-1 summaries so the
 * cloud reaches a realistic size even when the mock tree is small. Deterministic for a given input and seed.
 */
import { toNumber } from './common.js';
import type { CloudArrangement, CloudDistanceResponse, CloudFidelity, MemoryCloudResponse } from './memoryActivity.js';

export interface CloudMockNode {
  id: number;
  layer: number;
  parents: readonly number[];
  title: string;
  kind: string;
  /** Topic terms (summaries); the cluster label. */
  terms?: readonly string[];
  text?: string;
}

export interface CloudMockOptions {
  extraLeaves?: number;
  extraFrom?: number;
  seed?: number;
  stateKey?: string;
  /** Which arrangement to fake (default `mds`): the same islands, transformed so each looks different and switching animates. */
  arrangement?: CloudArrangement;
}

export const CLOUD_MOCK_ARRANGEMENTS: ReadonlyArray<{ id: CloudArrangement; label: string; metric: boolean }> = [
  { id: 'mds', label: 'Distances (MDS)', metric: true },
  { id: 'umap', label: 'Neighbourhoods (UMAP)', metric: false },
  { id: 'pca', label: 'PCA', metric: true },
  { id: 'tree', label: 'Tree (force)', metric: false },
];

/** Plausible fidelity per arrangement (the live db's order of magnitude: MDS best at distances, UMAP and PCA weakest). */
export const CLOUD_MOCK_FIDELITY: Record<CloudArrangement, CloudFidelity> = {
  mds: { metric: 'cosine', stress: 0.12, pearson: 0.86, spearman: 0.85, unitDistance: 1.35, pairs: 20000 },
  umap: { metric: 'cosine', stress: 0.35, pearson: 0.66, spearman: 0.61, unitDistance: 1.34, pairs: 20000 },
  pca: {
    metric: 'cosine',
    stress: 0.38,
    pearson: 0.67,
    spearman: 0.67,
    unitDistance: 1.28,
    pairs: 20000,
    varianceExplained: [0.17, 0.07],
  },
  tree: { metric: 'cosine', stress: 0.33, pearson: 0.82, spearman: 0.82, unitDistance: 1.23, pairs: 20000 },
};

/**
 * The mock's embedding distance of two leaves: their drawn MDS distance on its scale, times a symmetric deterministic factor
 * (±15 %) so "actual" and "drawn" differ a little, as they do live. Null when either id is not a leaf of `mds`.
 */
export function cloudMockDistance(mds: MemoryCloudResponse, a: number, b: number): CloudDistanceResponse | null {
  const ia = mds.points.id.indexOf(a);
  const ib = mds.points.id.indexOf(b);
  if (ia < 0 || ib < 0) return null;
  const dx = mds.points.xy[2 * ia]! - mds.points.xy[2 * ib]!;
  const dy = mds.points.xy[2 * ia + 1]! - mds.points.xy[2 * ib + 1]!;
  const lo = Math.min(a, b);
  const hi = Math.max(a, b);
  const wobble = a === b ? 1 : 1 + 0.15 * Math.sin(lo * 12.9898 + hi * 78.233);
  const unit = mds.fidelity ? toNumber(mds.fidelity.unitDistance) : 1.35;
  const d = Math.min(2, Math.hypot(dx, dy) * unit * wobble);
  const distance = Math.round(d * 1e5) / 1e5;
  return { a, b, metric: 'cosine', distance, similarity: Math.round((1 - distance) * 1e5) / 1e5 };
}

function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const r4 = (v: number) => Math.round(Math.min(1, Math.max(0, v)) * 10000) / 10000;

type XY = [number, number];

/** The tree's shape the layout steps share: nodes by id, the cluster layer, and each node's ancestor at a layer. */
interface CloudShape {
  byId: Map<number, CloudMockNode>;
  clusterLayer: number;
  /** The primary ancestor chain (first parent each step) up to `layer`; undefined when it doesn't reach it. */
  ancestorAt: (n: CloudMockNode, layer: number) => CloudMockNode | undefined;
}

function leavesByParent(tree: readonly CloudMockNode[]): Map<number, CloudMockNode[]> {
  const leavesOf = new Map<number, CloudMockNode[]>();
  for (const n of tree) if (n.layer === 0) for (const p of n.parents) leavesOf.set(p, [...(leavesOf.get(p) ?? []), n]);
  return leavesOf;
}

/** An extra leaf under `p`, named after its sibling when it has one. */
function extraLeaf(id: number, i: number, p: CloudMockNode, sib: CloudMockNode | undefined): CloudMockNode {
  return {
    id,
    layer: 0,
    parents: [p.id],
    title: sib ? `${sib.title} (${i % 97})` : `memory ${id}`,
    kind: sib?.kind ?? 'fact',
  };
}

/** `extra` more leaves under random layer-1 summaries, named after a random sibling (ids from `extraFrom`). */
function addExtraLeaves(
  tree: readonly CloudMockNode[],
  nodes: CloudMockNode[],
  byId: Map<number, CloudMockNode>,
  extra: number,
  extraFrom: number,
  r: () => number,
): void {
  const l1 = tree.filter((n) => n.layer === 1);
  const leavesOf = leavesByParent(tree);
  for (let i = 0; i < extra && l1.length; i++) {
    const p = l1[Math.floor(r() * l1.length)]!;
    const sibs = leavesOf.get(p.id);
    const leaf = extraLeaf(extraFrom + i, i, p, sibs?.[Math.floor(r() * (sibs?.length ?? 1))]);
    nodes.push(leaf);
    byId.set(leaf.id, leaf);
  }
}

/** The highest summary layer with at least 6 nodes (layer 1, or 0 for a flat tree, when none has). */
function clusterLayerOf(nodes: readonly CloudMockNode[]): number {
  const top = Math.max(0, ...nodes.map((n) => n.layer));
  const counts = new Map<number, number>();
  for (const n of nodes) counts.set(n.layer, (counts.get(n.layer) ?? 0) + 1);
  for (let l = top; l >= 1; l--) if ((counts.get(l) ?? 0) >= 6) return l;
  return Math.min(1, top);
}

function ancestorFinder(byId: Map<number, CloudMockNode>) {
  return (n: CloudMockNode, layer: number): CloudMockNode | undefined => {
    let cur: CloudMockNode | undefined = n;
    for (let guard = 0; cur && cur.layer < layer && guard < 32; guard++)
      cur = cur.parents.length ? byId.get(cur.parents[0]!) : undefined;
    return cur && cur.layer === layer ? cur : undefined;
  };
}

/** Island centres on a jittered spiral. */
function islandCentres(clusterNodes: readonly CloudMockNode[], r: () => number): Map<number, XY> {
  const centre = new Map<number, XY>();
  clusterNodes.forEach((c, i) => {
    const a = i * 2.39996 + r() * 0.4;
    const rad = 0.12 + 0.3 * Math.sqrt((i + 0.5) / clusterNodes.length);
    centre.set(c.id, [0.5 + rad * Math.cos(a), 0.5 + rad * Math.sin(a)]);
  });
  return centre;
}

/** Sub-islands (layer 1) around their cluster's centre (the middle when they have none). */
function subIslands(shape: CloudShape, l1: readonly CloudMockNode[], centre: Map<number, XY>, gauss: () => number) {
  const sub = new Map<number, XY>();
  for (const s of l1) {
    const c = shape.ancestorAt(s, shape.clusterLayer);
    const [cx, cy] = c ? centre.get(c.id)! : [0.5, 0.5];
    sub.set(s.id, [cx + gauss() * 0.035, cy + gauss() * 0.035]);
  }
  return sub;
}

/** Leaves in a Gaussian blob around their parent's sub-island (a wide one around the middle without it). */
function placeLeaves(leaves: readonly CloudMockNode[], sub: Map<number, XY>, gauss: () => number): Map<number, XY> {
  const pos = new Map<number, XY>();
  for (const l of leaves) {
    const p = l.parents[0];
    const [sx, sy] = (p !== undefined && sub.get(p)) || [0.5 + gauss() * 0.2, 0.5 + gauss() * 0.2];
    pos.set(l.id, [r4(sx + gauss() * 0.014), r4(sy + gauss() * 0.014)]);
  }
  return pos;
}

interface Placed {
  shape: CloudShape;
  centre: Map<number, XY>;
  sub: Map<number, XY>;
}

type Rearrange = (l: CloudMockNode, x: number, y: number, at: Placed) => XY;

/**
 * Same islands, another look: UMAP tightens islands and spreads them apart, PCA shears and squashes onto two axes, the tree
 * pulls each leaf to its layer-1 parent.
 */
const REARRANGE: Record<Exclude<CloudArrangement, 'mds'>, Rearrange> = {
  umap: (l, x, y, at) => {
    const c = at.shape.ancestorAt(l, at.shape.clusterLayer);
    const [cx, cy] = (c && at.centre.get(c.id)) || [0.5, 0.5];
    return [0.5 + (cx - 0.5) * 1.12 + (x - cx) * 0.55, 0.5 + (cy - 0.5) * 1.12 + (y - cy) * 0.55];
  },
  pca: (_l, x, y) => [0.5 + (x - 0.5) * 0.9 + (y - 0.5) * 0.35, 0.5 + (y - 0.5) * 0.45 - (x - 0.5) * 0.1],
  tree: (l, x, y, at) => {
    const p = l.parents[0];
    const [sx, sy] = (p !== undefined && at.sub.get(p)) || [x, y];
    return [sx + (x - sx) * 0.4, sy + (y - sy) * 0.4];
  },
};

function rearrange(
  arrangement: CloudArrangement,
  leaves: readonly CloudMockNode[],
  pos: Map<number, XY>,
  at: Placed,
): void {
  if (arrangement === 'mds') return;
  // Anything but umap / pca moves like the tree.
  const move = Object.hasOwn(REARRANGE, arrangement) ? REARRANGE[arrangement] : REARRANGE.tree;
  for (const l of leaves) {
    const [x, y] = pos.get(l.id)!;
    const [nx, ny] = move(l, x, y, at);
    pos.set(l.id, [r4(nx), r4(ny)]);
  }
}

/** Each leaf's cluster node, and each cluster's leaf count. */
function membership(shape: CloudShape, leaves: readonly CloudMockNode[]) {
  const sizes = new Map<number, number>();
  const clusterOfLeaf = new Map<number, number>();
  for (const l of leaves) {
    const c = shape.ancestorAt(l, shape.clusterLayer);
    if (!c) continue;
    clusterOfLeaf.set(l.id, c.id);
    sizes.set(c.id, (sizes.get(c.id) ?? 0) + 1);
  }
  return { sizes, clusterOfLeaf };
}

/** A cluster at its members' mean, with their spread, its terms as the label and its text (or title) as the summary. */
function clusterEntry(c: CloudMockNode, i: number, members: readonly XY[]) {
  const mx = members.reduce((s, p) => s + p[0], 0) / members.length;
  const my = members.reduce((s, p) => s + p[1], 0) / members.length;
  const spread = Math.sqrt(members.reduce((s, p) => s + (p[0] - mx) ** 2 + (p[1] - my) ** 2, 0) / members.length);
  const terms = [...(c.terms ?? [])];
  return {
    id: i,
    node: c.id,
    layer: c.layer,
    label: terms.length ? terms.slice(0, 3).join(' · ') : `L${c.layer} #${c.id}`,
    terms,
    summary: (c.text ?? c.title).slice(0, 180),
    size: members.length,
    x: r4(mx),
    y: r4(my),
    spread: Math.round(spread * 10000) / 10000,
  };
}

/** Summaries at the mean of their children, bottom-up. */
function placeSummaries(
  shape: CloudShape,
  nodes: readonly CloudMockNode[],
  leaves: readonly CloudMockNode[],
  pos: Map<number, XY>,
  slot: Map<number, number>,
): MemoryCloudResponse['summaries'] {
  const acc = new Map<number, [number, number, number]>();
  const add = (p: number, x: number, y: number) => {
    const a = acc.get(p) ?? [0, 0, 0];
    acc.set(p, [a[0] + x, a[1] + y, a[2] + 1]);
  };
  for (const l of leaves) for (const p of l.parents) add(p, ...pos.get(l.id)!);
  const summaries: MemoryCloudResponse['summaries'] = { id: [], xy: [], layer: [], cluster: [], parents: [] };
  for (const s of nodes.filter((n) => n.layer > 0).sort((a, b) => a.layer - b.layer || a.id - b.id)) {
    const a = acc.get(s.id);
    if (!a) continue;
    const x = r4(a[0] / a[2]);
    const y = r4(a[1] / a[2]);
    summaries.id.push(s.id);
    summaries.xy.push(x, y);
    summaries.layer.push(s.layer);
    summaries.cluster.push(summaryCluster(shape, s, slot));
    summaries.parents.push([...s.parents]);
    for (const p of s.parents) add(p, x, y);
  }
  return summaries;
}

/** A summary's cluster slot (at or below the cluster layer), else -1. */
function summaryCluster(shape: CloudShape, s: CloudMockNode, slot: Map<number, number>): number {
  const c = s.layer <= shape.clusterLayer ? shape.ancestorAt(s, shape.clusterLayer) : undefined;
  return c ? (slot.get(c.id) ?? -1) : -1;
}

/** The leaves as columns; kinds are indexed in first-seen order. */
function pointsOf(
  leaves: readonly CloudMockNode[],
  pos: Map<number, XY>,
  clusterOfLeaf: Map<number, number>,
  slot: Map<number, number>,
  extraFrom: number,
) {
  const kinds: string[] = [];
  const kindIx = (k: string) => {
    let i = kinds.indexOf(k);
    if (i < 0) i = kinds.push(k) - 1;
    return i;
  };
  const points: MemoryCloudResponse['points'] = {
    id: [],
    xy: [],
    cluster: [],
    kind: [],
    title: [],
    parents: [],
    inserted: [],
  };
  for (const l of leaves) {
    const c = clusterOfLeaf.get(l.id);
    points.id.push(l.id);
    points.xy.push(...pos.get(l.id)!);
    points.cluster.push(c === undefined ? -1 : (slot.get(c) ?? -1));
    points.kind.push(kindIx(l.kind));
    points.title.push(l.title);
    points.parents.push([...l.parents]);
    points.inserted.push(l.id >= extraFrom);
  }
  return { points, kinds };
}

export function memoryCloudMock(tree: readonly CloudMockNode[], o: CloudMockOptions = {}): MemoryCloudResponse {
  const r = rng(o.seed ?? 11);
  const gauss = () => {
    const u = Math.max(1e-9, r());
    return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r());
  };
  const byId = new Map(tree.map((n) => [n.id, n]));
  const nodes: CloudMockNode[] = [...tree];
  const extraFrom = o.extraFrom ?? 100_000;
  addExtraLeaves(tree, nodes, byId, o.extraLeaves ?? 0, extraFrom, r);
  const shape: CloudShape = { byId, clusterLayer: clusterLayerOf(nodes), ancestorAt: ancestorFinder(byId) };
  const leaves = nodes.filter((n) => n.layer === 0).sort((a, b) => a.id - b.id);
  const clusterNodes = nodes.filter((n) => n.layer === shape.clusterLayer).sort((a, b) => a.id - b.id);
  const centre = islandCentres(clusterNodes, r);
  const sub = subIslands(
    shape,
    tree.filter((n) => n.layer === 1),
    centre,
    gauss,
  );
  const pos = placeLeaves(leaves, sub, gauss);
  const arrangement = o.arrangement ?? 'mds';
  rearrange(arrangement, leaves, pos, { shape, centre, sub });
  const { sizes, clusterOfLeaf } = membership(shape, leaves);
  const ordered = clusterNodes
    .filter((c) => sizes.get(c.id))
    .sort((a, b) => sizes.get(b.id)! - sizes.get(a.id)! || a.id - b.id);
  const slot = new Map(ordered.map((c, i) => [c.id, i]));
  const clusters = ordered.map((c, i) =>
    clusterEntry(
      c,
      i,
      leaves.filter((l) => clusterOfLeaf.get(l.id) === c.id).map((l) => pos.get(l.id)!),
    ),
  );
  const summaries = placeSummaries(shape, nodes, leaves, pos, slot);
  const { points, kinds } = pointsOf(leaves, pos, clusterOfLeaf, slot, extraFrom);
  const stateKey = o.stateKey ?? `mock-${nodes.length}`;
  return {
    status: 'ready',
    version: `${stateKey}|mock`,
    stateKey,
    clusterLayer: shape.clusterLayer,
    kinds,
    points,
    summaries,
    clusters,
    anchors: Math.min(2000, leaves.length),
    placedSinceBuild: Math.max(0, leaves.length - 2000),
    computeMs: 0,
    arrangement,
    fidelity: CLOUD_MOCK_FIDELITY[arrangement],
    arrangements: CLOUD_MOCK_ARRANGEMENTS.map((a) => ({ ...a, status: 'ready' as const })),
  };
}
