/**
 * A synthetic RAPTOR tree (480 leaves → 48 L1 → 8 L2 → 2 L3 roots) with server-style layouts (icicle, radial, node-link)
 * computed the way the bridge does: unit space, leaf-weighted bands, deterministic labels.
 */
import type {
  GraphCategory,
  GraphColorBy,
  GraphEdge,
  GraphLayout,
  GraphNode,
  RaptorCrumb,
  RaptorGraph,
  RaptorLeavesResponse,
  RaptorNodeDetail,
  RaptorTreeNode,
  RaptorTreeResponse,
} from '@mineclaw/contracts';
import { isoAgo, minsAgo, pick, rng } from '../util';

export const STATE_KEY = 'raptor-b7-480';

interface Node {
  index: number;
  layer: number;
  parents: number[];
  children: number[];
  title: string;
  text: string;
  category: GraphCategory;
  docKind: string;
  docRef: string | null;
  tokens: number;
  newest: number;
  terms: string[];
  dirty: boolean;
}

const CATS: Array<{ cat: GraphCategory; kind: string; titles: string[]; terms: string[] }> = [
  {
    cat: 'Episode',
    kind: 'episode',
    titles: ['Built the cobble generator with Fox', 'First night on the island', 'Lost the bucket of lava', 'Expanded the platform east', 'Helped Robin plant melons', 'Fell into the void (respawned)'],
    terms: ['generator', 'island', 'night', 'platform', 'bucket'],
  },
  {
    cat: 'Activity',
    kind: 'activity',
    titles: ['Mined 64 cobblestone', 'Deposited stone in the main chest', 'Smelted 32 stone', 'Harvested wheat row 2', 'Crafted 4 torches', 'Ate bread'],
    terms: ['mined', 'deposit', 'smelt', 'harvest', 'craft'],
  },
  {
    cat: 'Fact',
    kind: 'note',
    titles: ['kiwi likes melons', 'Fox prefers day time', 'The shared chest is by the farm', 'Bone meal is kept in chest 3', 'Fox calls me claude', 'Robin builds on weekends'],
    terms: ['kiwi', 'fox', 'chest', 'melons', 'prefers'],
  },
  {
    cat: 'Recipe',
    kind: 'gamedata',
    titles: ['furnace: 8 cobblestone', 'torch: coal + stick', 'bread: 3 wheat', 'chest: 8 planks', 'stone pickaxe: 3 cobble + 2 sticks', 'smelt: cobblestone → stone'],
    terms: ['recipe', 'furnace', 'craft', 'smelt', 'planks'],
  },
  {
    cat: 'Chat',
    kind: 'player',
    titles: ['Fox asked for day', 'Robin thanked me for the bone meal', 'Fox asked how the cobble is going', 'Robin said good night', 'Fox: build a tower later'],
    terms: ['asked', 'thanks', 'night', 'tower', 'chat'],
  },
  {
    cat: 'World',
    kind: 'structure',
    titles: ['storage room (3 chests)', 'cobble generator at the edge', 'wheat farm, 9×9 with water', 'nether portal south', 'melon farm (Robin)', 'home platform'],
    terms: ['storage', 'farm', 'portal', 'platform', 'generator'],
  },
  {
    cat: 'Self',
    kind: 'selfnote',
    titles: ['I enjoy keeping chests tidy', 'I should check hunger more often', 'Short replies work best', 'I like helping Robin build'],
    terms: ['enjoy', 'tidy', 'helping', 'replies', 'hunger'],
  },
];

const r = rng(7);
const nodes = new Map<number, Node>();
const LEAVES = 480;
const L1 = 48;
const L2 = 8;
let roots: number[] = [];

(function build() {
  // Layer 1 clusters each lean to one category.
  const l1Cat: number[] = Array.from({ length: L1 }, (_, i) => (i * 5 + Math.floor(i / 7)) % CATS.length);
  for (let i = 0; i < LEAVES; i++) {
    const cluster = Math.floor(i / (LEAVES / L1));
    const ci = r() < 0.78 ? l1Cat[cluster]! : Math.floor(r() * CATS.length);
    const c = CATS[ci]!;
    const title = pick(r, c.titles);
    const age = (LEAVES - i) * 6 + r() * 30;
    nodes.set(i, {
      index: i,
      layer: 0,
      parents: [LEAVES + cluster],
      children: [],
      title: `${c.kind} · ${title}`,
      text: `${title}. ${pick(r, ['Noted while working the generator.', 'Fox was nearby.', 'Robin mentioned it in chat.', 'Seen during the morning loop.'])}`,
      category: c.cat,
      docKind: c.kind,
      docRef: `${c.kind === 'note' ? 'note' : c.kind === 'structure' ? 'st' : c.kind.slice(0, 3)}:${i + 1}`,
      tokens: 30 + Math.floor(r() * 120),
      newest: minsAgo(age),
      terms: [title.split(' ')[0]!.toLowerCase()],
      dirty: false,
    });
  }
  // A few leaves sit in a second soft cluster too.
  for (let k = 0; k < 24; k++) {
    const leaf = nodes.get(Math.floor(r() * LEAVES))!;
    const other = LEAVES + Math.floor(r() * L1);
    if (!leaf.parents.includes(other)) leaf.parents.push(other);
  }
  for (let j = 0; j < L1; j++) {
    const id = LEAVES + j;
    const parent = LEAVES + L1 + Math.floor(j / (L1 / L2));
    const kids = [...nodes.values()].filter((n) => n.layer === 0 && n.parents[0] === id).map((n) => n.index);
    const c = CATS[l1Cat[j]!]!;
    const terms = [pick(r, c.terms), pick(r, c.terms), pick(r, CATS[(l1Cat[j]! + 1) % CATS.length]!.terms)].filter((t, i, a) => a.indexOf(t) === i);
    nodes.set(id, {
      index: id,
      layer: 1,
      parents: [parent],
      children: kids,
      title: `${terms.join(' · ')}`,
      text: `Summary of ${kids.length} memories about ${terms.join(', ')}. ${c.cat === 'Fact' ? 'Mostly preferences of Fox and Robin.' : c.cat === 'World' ? 'Places and builds around the island.' : 'Things that happened around the generator and farms.'}`,
      category: c.cat,
      docKind: 'summary',
      docRef: `raptor:${id}`,
      tokens: 180 + Math.floor(r() * 140),
      newest: Math.max(...kids.map((k) => nodes.get(k)!.newest)),
      terms,
      dirty: r() < 0.08,
    });
  }
  const l2Names = ['island life', 'generator & stone', 'friends & chat', 'farms & food', 'builds & places', 'recipes', 'self & habits', 'nether trips'];
  for (let k = 0; k < L2; k++) {
    const id = LEAVES + L1 + k;
    const parent = LEAVES + L1 + L2 + (k < L2 / 2 ? 0 : 1);
    const kids = Array.from({ length: L1 / L2 }, (_, i) => LEAVES + k * (L1 / L2) + i);
    const cats = kids.map((x) => nodes.get(x)!.category);
    const top = cats.sort((a, b) => cats.filter((c) => c === b).length - cats.filter((c) => c === a).length)[0]!;
    nodes.set(id, {
      index: id,
      layer: 2,
      parents: [parent],
      children: kids,
      title: l2Names[k]!,
      text: `Layer 2 summary: ${l2Names[k]}. Covers ${kids.length} clusters.`,
      category: top,
      docKind: 'summary',
      docRef: `raptor:${id}`,
      tokens: 320 + Math.floor(r() * 120),
      newest: Math.max(...kids.map((x) => nodes.get(x)!.newest)),
      terms: l2Names[k]!.split(' & '),
      dirty: false,
    });
  }
  roots = [LEAVES + L1 + L2, LEAVES + L1 + L2 + 1];
  ['daily life', 'people & places'].forEach((name, k) => {
    const id = roots[k]!;
    const kids = Array.from({ length: L2 / 2 }, (_, i) => LEAVES + L1 + k * (L2 / 2) + i);
    nodes.set(id, {
      index: id,
      layer: 3,
      parents: [],
      children: kids,
      title: name,
      text: `Root summary: ${name}.`,
      category: k ? 'World' : 'Episode',
      docKind: 'summary',
      docRef: `raptor:${id}`,
      tokens: 480,
      newest: Math.max(...kids.map((x) => nodes.get(x)!.newest)),
      terms: name.split(' & '),
      dirty: false,
    });
  });
})();

export function raptorNode(id: number): Node | undefined {
  return nodes.get(id);
}
export function raptorLeaves(): number[] {
  return [...nodes.values()].filter((n) => n.layer === 0).map((n) => n.index);
}
/** child → parents (all parents; primary first). */
export function raptorParents(): Map<number, number[]> {
  return new Map([...nodes.values()].map((n) => [n.index, n.parents]));
}
export const RAPTOR_ROOTS = (): number[] => roots;

function leavesBelow(id: number): number {
  const n = nodes.get(id)!;
  return n.layer === 0 ? 1 : n.children.reduce((s, c) => s + leavesBelow(c), 0);
}

function label(n: Node): string {
  return n.layer === 0 ? n.title : `L${n.layer} ${n.terms.join(' · ')}`;
}

export function treeRow(n: Node, includeChildren: boolean): RaptorTreeNode {
  return {
    index: n.index,
    layer: n.layer,
    isLeaf: n.layer === 0,
    title: n.layer === 0 ? n.title : n.text.slice(0, 90),
    kindLabel: n.layer === 0 ? n.docKind : `L${n.layer}`,
    tokens: n.tokens,
    childCount: n.children.length,
    leavesBelow: leavesBelow(n.index),
    dirty: n.dirty,
    parents: n.parents,
    children: includeChildren ? [...n.children].sort((a, b) => nodes.get(b)!.children.length - nodes.get(a)!.children.length || a - b) : null,
    docRef: n.docRef,
  };
}

export function treeResponse(root: number | undefined, depth: number): RaptorTreeResponse {
  const top = root !== undefined ? [root] : roots;
  const out: RaptorTreeNode[] = [];
  const visit = (id: number, d: number) => {
    const n = nodes.get(id);
    if (!n) return;
    out.push(treeRow(n, d < depth));
    if (d < depth) for (const c of n.children) visit(c, d + 1);
  };
  for (const t of top) visit(t, 0);
  return { meta: null, note: null, stateKey: STATE_KEY, root: root ?? null, top, nodes: out };
}

export function crumbs(id: number): RaptorCrumb[] {
  const path: RaptorCrumb[] = [];
  let cur: Node | undefined = nodes.get(id);
  while (cur) {
    path.unshift({ index: cur.index, layer: cur.layer, label: label(cur) });
    cur = cur.parents[0] !== undefined ? nodes.get(cur.parents[0]) : undefined;
  }
  return path;
}

export function nodeDetail(id: number): RaptorNodeDetail | null {
  const n = nodes.get(id);
  if (!n) return null;
  const ref = (x: number) => {
    const m = nodes.get(x)!;
    return { index: x, layer: m.layer, label: label(m), tokens: m.tokens };
  };
  return {
    node: treeRow(n, true),
    title: n.layer === 0 ? n.title.split(' · ').slice(1).join(' · ') : `Layer ${n.layer} summary #${n.index}`,
    text: n.text,
    pinned: n.index % 50 === 0,
    placeholder: n.dirty,
    doc:
      n.layer === 0
        ? { id: 1000 + n.index, ref: n.docRef ?? '', kind: n.docKind, tag: null, title: n.title.split(' · ')[1] ?? n.title, source: 'bot', updated: n.newest, dimension: 0 }
        : null,
    breadcrumb: crumbs(id),
    children: n.children.map(ref).sort((a, b) => b.tokens - a.tokens),
    parents: n.parents.map(ref),
  };
}

export function leavesResponse(id: number): RaptorLeavesResponse {
  const set = new Set<number>();
  const walk = (x: number) => {
    const n = nodes.get(x);
    if (!n) return;
    if (n.layer === 0) set.add(x);
    else n.children.forEach(walk);
  };
  walk(id);
  const leaves = [...set].map((x) => {
    const n = nodes.get(x)!;
    return { index: x, docRef: n.docRef ?? '', kind: n.docKind, title: n.title, tokens: n.tokens };
  });
  return { node: id, leaves, tokenSum: leaves.reduce((s, l) => s + l.tokens, 0) };
}

// ---------- layouts ----------

const KIND_COLORS: Record<GraphCategory, string> = {
  Episode: '#d08a3a',
  Activity: '#7a8fd6',
  Fact: '#3fb68b',
  Recipe: '#c9a43a',
  Chat: '#d0608f',
  World: '#4aa3c9',
  Self: '#a77bd6',
  Other: '#8a929c',
};
const CLUSTER_COLORS = ['#4aa3c9', '#d08a3a', '#3fb68b', '#d0608f', '#a77bd6', '#c9a43a', '#7a8fd6', '#6fbf5f'];

function lerpHex(a: string, b: string, t: number): string {
  const pa = [1, 3, 5].map((i) => parseInt(a.slice(i, i + 2), 16));
  const pb = [1, 3, 5].map((i) => parseInt(b.slice(i, i + 2), 16));
  return `#${pa.map((v, i) => Math.round(v + (pb[i]! - v) * t).toString(16).padStart(2, '0')).join('')}`;
}

export function graph(layout: GraphLayout, colorBy: GraphColorBy, focus?: number): RaptorGraph {
  const top = focus !== undefined && nodes.has(focus) ? [focus] : roots;
  const order: Array<{ id: number; depth: number; parent: number | null; x0: number; x1: number }> = [];
  const total = top.reduce((s, t) => s + leavesBelow(t), 0);
  let maxDepth = 0;
  const place = (id: number, depth: number, parent: number | null, x0: number) => {
    const n = nodes.get(id)!;
    const w = leavesBelow(id) / total;
    order.push({ id, depth, parent, x0, x1: x0 + w });
    maxDepth = Math.max(maxDepth, depth);
    let cx = x0;
    for (const c of n.children) {
      place(c, depth + 1, id, cx);
      cx += leavesBelow(c) / total;
    }
  };
  let x = 0;
  for (const t of top) {
    place(t, 0, null, x);
    x += leavesBelow(t) / total;
  }
  const levels = maxDepth + 1;
  const times = [...order].map((o) => nodes.get(o.id)!.newest).sort((a, b) => a - b);
  const rootOf = (id: number): number => {
    let cur = nodes.get(id)!;
    while (cur.parents[0] !== undefined && !top.includes(cur.index)) cur = nodes.get(cur.parents[0])!;
    return top.indexOf(cur.index);
  };
  const out: GraphNode[] = order.map((o) => {
    const n = nodes.get(o.id)!;
    const timeRank = times.indexOf(n.newest) / Math.max(1, times.length - 1);
    const cluster = Math.max(0, rootOf(o.id));
    const color =
      colorBy === 'kind' ? KIND_COLORS[n.category] : colorBy === 'time' ? lerpHex('#3d4f7a', '#f2b84b', timeRank) : CLUSTER_COLORS[(n.layer >= 2 ? n.index : (nodes.get(n.parents[0] ?? -1)?.parents[0] ?? 0)) % CLUSTER_COLORS.length]!;
    const base = {
      id: o.id,
      parent: o.parent,
      layer: n.layer,
      isLeaf: n.layer === 0,
      label: label(n),
      terms: n.terms,
      category: n.category,
      newest: n.newest,
      timeRank,
      cluster,
      weight: leavesBelow(o.id),
      tokens: n.tokens,
      color,
      opacity: Math.min(1, 0.45 + n.tokens / 600),
    };
    if (layout === 'icicle') {
      const h = 1 / levels;
      return { ...base, x: (o.x0 + o.x1) / 2, y: (o.depth + 0.5) * h, w: o.x1 - o.x0, h };
    }
    if (layout === 'radial') {
      const band = 0.5 / (levels + 0.4);
      const r0 = (o.depth + 0.4) * band;
      const r1 = r0 + band;
      const a0 = o.x0 * Math.PI * 2;
      const a1 = o.x1 * Math.PI * 2;
      const am = (a0 + a1) / 2;
      const rm = (r0 + r1) / 2;
      return { ...base, x: 0.5 + rm * Math.sin(am), y: 0.5 - rm * Math.cos(am), a0, a1, r0, r1 };
    }
    const yy = 0.06 + (o.depth / Math.max(1, levels - 1)) * 0.88;
    return { ...base, x: 0.03 + ((o.x0 + o.x1) / 2) * 0.94, y: yy, r: Math.min(0.03, 0.004 + Math.sqrt(leavesBelow(o.id)) * 0.0022) };
  });
  const ids = new Set(out.map((n) => n.id));
  const edges: GraphEdge[] = [];
  for (const n of out) {
    const node = nodes.get(n.id)!;
    node.parents.forEach((p, i) => {
      if (ids.has(p)) edges.push({ source: p, target: n.id, primary: i === 0, weight: i === 0 ? 1 : 0.4 });
    });
  }
  const legend =
    colorBy === 'kind'
      ? (Object.keys(KIND_COLORS) as GraphCategory[]).filter((k) => k !== 'Other').map((k) => ({ key: k, label: k, color: KIND_COLORS[k] }))
      : colorBy === 'time'
        ? [
            { key: 'old', label: 'older', color: '#3d4f7a' },
            { key: 'new', label: 'newer', color: '#f2b84b' },
          ]
        : CLUSTER_COLORS.slice(0, 8).map((c, i) => ({ key: String(i), label: `cluster ${i + 1}`, color: c }));
  return {
    layout,
    colorBy,
    stateKey: STATE_KEY,
    focus: focus ?? null,
    breadcrumb: focus !== undefined ? crumbs(focus) : [],
    nodes: out,
    edges,
    legend,
  };
}

export function searchHits(q: string, k: number) {
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  const leaves = [...nodes.values()].filter((n) => n.layer === 0);
  const scored = leaves
    .map((n) => {
      const hay = `${n.title} ${n.text}`.toLowerCase();
      const hits = words.filter((w) => hay.includes(w)).length;
      return { n, score: hits + (n.index % 7) / 20 };
    })
    .filter((s) => s.score >= 1 || words.length === 0)
    .sort((a, b) => b.score - a.score);
  const base = scored.length ? scored : leaves.slice(0, k).map((n) => ({ n, score: 0.2 }));
  const seen = new Set<string>();
  return base
    .filter((s) => (seen.has(s.n.title) ? false : (seen.add(s.n.title), true)))
    .slice(0, k)
    .map((s, i) => ({
      rank: i + 1,
      docId: 1000 + s.n.index,
      ref: s.n.docRef ?? '',
      kind: s.n.docKind,
      tag: null,
      title: s.n.title.split(' · ').slice(1).join(' · '),
      snippet: s.n.text,
      score: Number((0.06 - i * 0.002 + s.score * 0.01).toFixed(4)),
      signals: { bm25: Number((4 + s.score * 2 - i * 0.1).toFixed(2)), cos: Number((0.82 - i * 0.01).toFixed(3)), distance: i % 3 === 0 ? 12 + i : null },
      updated: s.n.newest,
      dimension: 0,
      where: s.n.docKind === 'structure' ? '4,64,6..8,66,9' : null,
      raptorLeaf: s.n.index,
      raptorPath: crumbs(s.n.index),
    }));
}

export function raptorMetaIso() {
  return {
    numLayers: 4,
    nextIndex: 538,
    dimensions: 1536,
    embeddingModel: 'text-embedding-3-small',
    buildId: 7,
    builtAt: isoAgo(60 * 26),
    leavesAtBuild: 452,
    insertedSinceBuild: 31,
    removedSinceBuild: 3,
    baselineLeafParentSimilarity: 0.71,
    insertedSimilaritySum: 21.3,
    insertedSimilarityCount: 31,
    insertSeq: 31,
    baselineLeafParentSimilarityStd: 0.08,
    insertTemperature: 0.35,
  };
}
