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

export function memoryCloudMock(tree: readonly CloudMockNode[], o: CloudMockOptions = {}): MemoryCloudResponse {
  const r = rng(o.seed ?? 11);
  const gauss = () => {
    const u = Math.max(1e-9, r());
    return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * r());
  };
  const byId = new Map(tree.map((n) => [n.id, n]));
  const nodes: CloudMockNode[] = [...tree];
  const l1 = tree.filter((n) => n.layer === 1);
  const extra = o.extraLeaves ?? 0;
  const extraFrom = o.extraFrom ?? 100_000;
  const leavesOf = new Map<number, CloudMockNode[]>();
  for (const n of tree) if (n.layer === 0) for (const p of n.parents) leavesOf.set(p, [...(leavesOf.get(p) ?? []), n]);
  for (let i = 0; i < extra && l1.length; i++) {
    const p = l1[Math.floor(r() * l1.length)]!;
    const sib = leavesOf.get(p.id)?.[Math.floor(r() * (leavesOf.get(p.id)?.length ?? 1))];
    const leaf: CloudMockNode = {
      id: extraFrom + i,
      layer: 0,
      parents: [p.id],
      title: sib ? `${sib.title} (${i % 97})` : `memory ${extraFrom + i}`,
      kind: sib?.kind ?? 'fact',
    };
    nodes.push(leaf);
    byId.set(leaf.id, leaf);
  }
  const top = Math.max(0, ...nodes.map((n) => n.layer));
  const counts = new Map<number, number>();
  for (const n of nodes) counts.set(n.layer, (counts.get(n.layer) ?? 0) + 1);
  let clusterLayer = Math.min(1, top);
  for (let l = top; l >= 1; l--)
    if ((counts.get(l) ?? 0) >= 6) {
      clusterLayer = l;
      break;
    }
  // The primary ancestor chain: first parent each step.
  const ancestorAt = (n: CloudMockNode, layer: number): CloudMockNode | undefined => {
    let cur: CloudMockNode | undefined = n;
    for (let guard = 0; cur && cur.layer < layer && guard < 32; guard++)
      cur = cur.parents.length ? byId.get(cur.parents[0]!) : undefined;
    return cur && cur.layer === layer ? cur : undefined;
  };
  const leaves = nodes.filter((n) => n.layer === 0).sort((a, b) => a.id - b.id);
  const clusterNodes = nodes.filter((n) => n.layer === clusterLayer).sort((a, b) => a.id - b.id);
  // Island centres on a jittered spiral, sub-islands (layer 1) around them.
  const centre = new Map<number, [number, number]>();
  clusterNodes.forEach((c, i) => {
    const a = i * 2.39996 + r() * 0.4;
    const rad = 0.12 + 0.3 * Math.sqrt((i + 0.5) / clusterNodes.length);
    centre.set(c.id, [0.5 + rad * Math.cos(a), 0.5 + rad * Math.sin(a)]);
  });
  const sub = new Map<number, [number, number]>();
  for (const s of l1) {
    const c = ancestorAt(s, clusterLayer);
    const [cx, cy] = c ? centre.get(c.id)! : [0.5, 0.5];
    sub.set(s.id, [cx + gauss() * 0.035, cy + gauss() * 0.035]);
  }
  const pos = new Map<number, [number, number]>();
  for (const l of leaves) {
    const p = l.parents[0];
    const [sx, sy] = (p !== undefined && sub.get(p)) || [0.5 + gauss() * 0.2, 0.5 + gauss() * 0.2];
    pos.set(l.id, [r4(sx + gauss() * 0.014), r4(sy + gauss() * 0.014)]);
  }
  const arrangement = o.arrangement ?? 'mds';
  if (arrangement !== 'mds') {
    // Same islands, another look: UMAP tightens islands and spreads them apart, PCA shears and squashes onto two axes, the tree
    // pulls each leaf to its layer-1 parent.
    for (const l of leaves) {
      const [x, y] = pos.get(l.id)!;
      let nx: number;
      let ny: number;
      if (arrangement === 'umap') {
        const c = ancestorAt(l, clusterLayer);
        const [cx, cy] = (c && centre.get(c.id)) || [0.5, 0.5];
        nx = 0.5 + (cx - 0.5) * 1.12 + (x - cx) * 0.55;
        ny = 0.5 + (cy - 0.5) * 1.12 + (y - cy) * 0.55;
      } else if (arrangement === 'pca') {
        nx = 0.5 + (x - 0.5) * 0.9 + (y - 0.5) * 0.35;
        ny = 0.5 + (y - 0.5) * 0.45 - (x - 0.5) * 0.1;
      } else {
        const p = l.parents[0];
        const [sx, sy] = (p !== undefined && sub.get(p)) || [x, y];
        nx = sx + (x - sx) * 0.4;
        ny = sy + (y - sy) * 0.4;
      }
      pos.set(l.id, [r4(nx), r4(ny)]);
    }
  }
  const sizes = new Map<number, number>();
  const clusterOfLeaf = new Map<number, number>();
  for (const l of leaves) {
    const c = ancestorAt(l, clusterLayer);
    if (!c) continue;
    clusterOfLeaf.set(l.id, c.id);
    sizes.set(c.id, (sizes.get(c.id) ?? 0) + 1);
  }
  const ordered = clusterNodes
    .filter((c) => sizes.get(c.id))
    .sort((a, b) => sizes.get(b.id)! - sizes.get(a.id)! || a.id - b.id);
  const slot = new Map(ordered.map((c, i) => [c.id, i]));
  const kinds: string[] = [];
  const kindIx = (k: string) => {
    let i = kinds.indexOf(k);
    if (i < 0) i = kinds.push(k) - 1;
    return i;
  };
  const clusters = ordered.map((c, i) => {
    const members = leaves.filter((l) => clusterOfLeaf.get(l.id) === c.id).map((l) => pos.get(l.id)!);
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
  });
  // Summaries at the mean of their children, bottom-up.
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
    const c = s.layer <= clusterLayer ? ancestorAt(s, clusterLayer) : undefined;
    summaries.id.push(s.id);
    summaries.xy.push(x, y);
    summaries.layer.push(s.layer);
    summaries.cluster.push(c ? (slot.get(c.id) ?? -1) : -1);
    summaries.parents.push([...s.parents]);
    for (const p of s.parents) add(p, x, y);
  }
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
  const stateKey = o.stateKey ?? `mock-${nodes.length}`;
  return {
    status: 'ready',
    version: `${stateKey}|mock`,
    stateKey,
    clusterLayer,
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
