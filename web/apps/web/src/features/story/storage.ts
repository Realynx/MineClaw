/**
 * Chest storage settings and workbench bills, the genre's way: a loot category tree with tri-state checkboxes over an
 * allow/deny filter, copy/paste of settings, and the bill dialog's state → request bodies. Pure, unit-tested.
 */
import {
  STORY_DEFAULT_CATEGORIES,
  storyItemLabel,
  type StoryBill,
  type StoryBillSpec,
  type StoryCategory,
  type StoryFilter,
  type StoryRecipe,
  type StoryStoragePriority,
  type StoryWorkbench,
} from '@mineclaw/contracts';

type V3 = [number, number, number];

export interface TreeNode {
  id: string;
  label: string;
  children: TreeNode[];
  /** Item leaves ("17:-1"). */
  items: Array<{ key: string; id: number; meta: number; label: string }>;
  parent: TreeNode | null;
}

/** The category tree (flat `children` ids or nested categories); roots are the categories nobody lists. */
export function buildTree(categories: readonly StoryCategory[] | null | undefined): TreeNode[] {
  const list = categories?.length ? categories : STORY_DEFAULT_CATEGORIES;
  const byId = new Map<string, StoryCategory>();
  const index = (cs: ReadonlyArray<StoryCategory | string>) => {
    for (const c of cs) {
      if (typeof c === 'string') continue;
      byId.set(c.id, c);
      index(c.children);
    }
  };
  index(list);
  const childIds = new Set<string>();
  for (const c of byId.values()) for (const ch of c.children) childIds.add(typeof ch === 'string' ? ch : ch.id);
  const make = (c: StoryCategory, parent: TreeNode | null, seen: Set<string>): TreeNode => {
    const n: TreeNode = {
      id: c.id,
      label: c.label,
      children: [],
      items: c.items.map(([id, meta]) => ({ key: `${id}:${meta}`, id, meta, label: storyItemLabel(id, Math.max(0, meta)) })),
      parent,
    };
    seen.add(c.id);
    for (const ch of c.children) {
      const cc = typeof ch === 'string' ? byId.get(ch) : ch;
      if (cc && !seen.has(cc.id)) n.children.push(make(cc, n, seen));
    }
    return n;
  };
  return [...byId.values()].filter((c) => !childIds.has(c.id)).map((c) => make(c, null, new Set()));
}

export type Tri = 'on' | 'off' | 'partial';

function ancestors(n: TreeNode | null): TreeNode[] {
  const out: TreeNode[] = [];
  for (let p = n; p; p = p.parent) out.push(p);
  return out;
}

/** Is one item leaf allowed under its node path? Deny on the item or any ancestor wins; allow needs the item or an ancestor. */
export function itemAllowed(filter: StoryFilter, node: TreeNode, key: string): boolean {
  const path = ancestors(node).map((a) => a.id);
  const id = key.split(':')[0]!;
  const covers = (list: readonly string[]) => list.includes(key) || list.includes(`${id}:-1`) || list.includes(id) || list.includes('*') || path.some((p) => list.includes(p));
  if (covers(filter.deny)) return false;
  return covers(filter.allow);
}

/** A node's checkbox: on (all its items allowed), off (none) or partial. */
export function nodeState(filter: StoryFilter, node: TreeNode): Tri {
  let on = 0, off = 0;
  const walk = (n: TreeNode) => {
    for (const it of n.items) {
      if (itemAllowed(filter, n, it.key)) on++;
      else off++;
    }
    n.children.forEach(walk);
  };
  walk(node);
  if (on === 0) return 'off';
  return off === 0 ? 'on' : 'partial';
}

function subtreeIds(n: TreeNode): string[] {
  return [n.id, ...n.items.map((i) => i.key), ...n.children.flatMap(subtreeIds)];
}

/**
 * Clicks a node's checkbox: anything not fully on turns on, on turns off. The filter stays small: the node's own entries
 * replace its descendants' (an allowed ancestor is overridden with a deny entry, a denied one with an allow entry).
 */
export function toggleNode(filter: StoryFilter, node: TreeNode): StoryFilter {
  const turnOn = nodeState(filter, node) !== 'on';
  const inner = new Set(subtreeIds(node));
  const allow = filter.allow.filter((k) => !inner.has(k));
  const deny = filter.deny.filter((k) => !inner.has(k));
  const covered = (list: string[]) => list.includes('*') || ancestors(node.parent).some((a) => list.includes(a.id));
  if (turnOn) {
    if (!covered(allow) || covered(deny)) allow.push(node.id);
  } else if (covered(allow)) deny.push(node.id);
  return { allow, deny };
}

/** Clicks one item leaf. */
export function toggleItem(filter: StoryFilter, node: TreeNode, key: string): StoryFilter {
  const on = itemAllowed(filter, node, key);
  const allow = filter.allow.filter((k) => k !== key);
  const deny = filter.deny.filter((k) => k !== key);
  if (on) deny.push(key);
  else allow.push(key);
  return { allow, deny };
}

/** Everything (`"*"`, what a new chest allows). */
export function allowAll(_tree?: readonly TreeNode[]): StoryFilter {
  return { allow: ['*'], deny: [] };
}

export const CLEAR_FILTER: StoryFilter = { allow: [], deny: [] };

/** The tree filtered by a search (a category stays when it or any item below it matches). */
export function searchTree(tree: readonly TreeNode[], q: string): TreeNode[] {
  const s = q.trim().toLowerCase();
  if (!s) return [...tree];
  const keep = (n: TreeNode): TreeNode | null => {
    const self = n.label.toLowerCase().includes(s);
    const items = self ? n.items : n.items.filter((i) => i.label.toLowerCase().includes(s));
    const children = n.children.map(keep).filter((c): c is TreeNode => !!c);
    if (!self && items.length === 0 && children.length === 0) return null;
    return { ...n, items, children };
  };
  return tree.map(keep).filter((n): n is TreeNode => !!n);
}

/** A short summary of a filter ("Wood, Stone +2"). */
export function filterSummary(tree: readonly TreeNode[], filter: StoryFilter): string {
  const all: TreeNode[] = [];
  const walk = (n: TreeNode) => {
    all.push(n);
    n.children.forEach(walk);
  };
  tree.forEach(walk);
  const on = all.filter((n) => nodeState(filter, n) === 'on' && (!n.parent || nodeState(filter, n.parent) !== 'on'));
  if (on.length === 0) return all.some((n) => nodeState(filter, n) !== 'off') ? 'Some items' : 'Nothing';
  const names = on.map((n) => n.label);
  return names.length > 3 ? `${names.slice(0, 3).join(', ')} +${names.length - 3}` : names.join(', ');
}

export const PRIORITY_LABELS: Record<StoryStoragePriority, string> = {
  low: 'Low',
  normal: 'Normal',
  preferred: 'Preferred',
  important: 'Important',
  critical: 'Critical',
};

export interface StorageClip {
  filter: StoryFilter;
  priority: string;
}

/** `set` for chests (several chests edited together). */
export function storageSetBody(chests: readonly V3[], patch: { filter?: StoryFilter; priority?: string; label?: string }) {
  return { op: 'set' as const, chests: chests.map((c) => [c[0], c[1], c[2]] as V3), ...patch };
}

/** Paste copied settings onto other chests. */
export function pasteBody(clip: StorageClip, chests: readonly V3[]) {
  return storageSetBody(chests, { filter: clip.filter, priority: clip.priority });
}

// ------------------------------------------------------------------ bills

export function billSummary(b: Pick<StoryBill, 'mode' | 'target'>): string {
  if (b.mode === 'forever') return 'Do forever';
  if (b.mode === 'until') return `Do until you have ${b.target}`;
  return `Do ${b.target} time${b.target === 1 ? '' : 's'}`;
}

export function billProgress(b: Pick<StoryBill, 'mode' | 'target' | 'done'>): string {
  return b.mode === 'forever' ? `${b.done} made` : `${Math.min(b.done, b.target)}/${b.target}`;
}

export interface BillDraft {
  recipe: string;
  mode: 'count' | 'until' | 'forever';
  target: number;
  paused: boolean;
  ingredients: StoryFilter;
  radius: number;
  pawn: string;
  minSkill: number | null;
  store: 'best' | { chest: V3 };
  /** An enchant bill: the highest offer (1–3), the preferred enchantment, prefer-only. Null for other bills. */
  option: number | null;
  prefer: string | null;
  preferOnly: boolean;
}

export function draftOf(b: StoryBill): BillDraft {
  return {
    recipe: b.recipe,
    mode: (['count', 'until', 'forever'].includes(b.mode) ? b.mode : 'count') as BillDraft['mode'],
    target: b.target,
    paused: b.paused,
    ingredients: b.ingredients,
    radius: b.radius,
    pawn: b.pawn,
    minSkill: b.minSkill ?? null,
    store: b.store === 'best' ? 'best' : { chest: b.store.chest },
    option: b.option ?? null,
    prefer: b.prefer ?? null,
    preferOnly: b.preferOnly ?? false,
  };
}

/** A new bill's defaults for a recipe (its ingredient options all allowed). */
export function newDraft(r: StoryRecipe, mode: BillDraft['mode'] = 'count', target = 1): BillDraft {
  const modes = recipeModes(r);
  return {
    recipe: r.id,
    mode: modes.includes(mode) ? mode : modes[0]!,
    target,
    paused: false,
    ingredients: { allow: r.inputs.flatMap((i) => (i.category ? [i.category] : (i.options ?? []).map(([a, m]) => `${a}:${m}`))), deny: [] },
    radius: 32,
    pawn: 'any',
    minSkill: null,
    store: 'best',
    option: r.workbench === 'enchanting_table' ? 3 : null,
    prefer: null,
    preferOnly: false,
  };
}

export function specOf(d: BillDraft): StoryBillSpec {
  return {
    recipe: d.recipe,
    mode: d.mode,
    ...(d.mode !== 'forever' ? { target: Math.max(1, Math.min(9999, Math.round(d.target))) } : {}),
    paused: d.paused,
    ingredients: d.ingredients,
    radius: Math.max(4, Math.min(128, Math.round(d.radius))),
    pawn: d.pawn,
    ...((d.minSkill !== null && d.minSkill !== undefined) ? { minSkill: d.minSkill } : {}),
    store: d.store,
    ...(d.option !== null && d.option !== undefined ? { option: Math.max(1, Math.min(3, Math.round(d.option))) } : {}),
    ...(d.prefer ? { prefer: d.prefer } : {}),
    ...(d.preferOnly ? { preferOnly: true } : {}),
  };
}

export function addBillBody(bench: V3, d: BillDraft) {
  return { op: 'add' as const, bench, bill: specOf(d) };
}

/** Only what changed between the bill and the edited draft (null = nothing). */
export function updateBillBody(bill: StoryBill, d: BillDraft) {
  const before = specOf(draftOf(bill)) as Record<string, unknown>;
  const after = specOf(d) as Record<string, unknown>;
  const spec: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(after)) if (JSON.stringify(before[k]) !== JSON.stringify(v)) spec[k] = v;
  if (d.minSkill === null && (bill.minSkill !== null && bill.minSkill !== undefined)) spec.minSkill = 0;
  if (!d.prefer && bill.prefer) spec.prefer = null;
  if (!d.preferOnly && bill.preferOnly) spec.preferOnly = false;
  if (Object.keys(spec).length === 0) return null;
  return { op: 'update' as const, id: bill.id, ...spec };
}

/** Moves the bill at `from` to `to`; the reorder request lists every bill in the new order. */
export function reorderBills(bench: V3, ids: ReadonlyArray<string | number>, from: number, to: number) {
  const list = [...ids];
  const [m] = list.splice(from, 1);
  list.splice(Math.max(0, Math.min(list.length, to)), 0, m!);
  return { op: 'reorder' as const, bench, ids: list };
}

/** The recipe picker: search on label and ingredient names, grouped by category (or work type). */
export function searchRecipes(recipes: readonly StoryRecipe[], q: string, tree?: readonly TreeNode[]): Array<{ group: string; label: string; recipes: StoryRecipe[] }> {
  const s = q.trim().toLowerCase();
  const labels = new Map<string, string>();
  const walk = (n: TreeNode) => {
    labels.set(n.id, n.label);
    n.children.forEach(walk);
  };
  (tree ?? buildTree(null)).forEach(walk);
  const hit = recipes.filter(
    (r) => !s || r.label.toLowerCase().includes(s) || r.inputs.some((i) => (i.options ?? []).some(([id, m]) => storyItemLabel(id, Math.max(0, m)).toLowerCase().includes(s))),
  );
  const groups = new Map<string, StoryRecipe[]>();
  for (const r of hit) {
    const g = r.group ?? r.workType;
    groups.set(g, [...(groups.get(g) ?? []), r]);
  }
  return [...groups].map(([group, rs]) => ({ group, label: labels.get(group) ?? group.replace(/_/g, ' '), recipes: rs }));
}

/** The bill modes a recipe takes (an enchanted or repaired item can't be told apart in storage: no "until"). */
export function recipeModes(r: Pick<StoryRecipe, 'modes'> | undefined): Array<BillDraft['mode']> {
  const all: Array<BillDraft['mode']> = ['count', 'until', 'forever'];
  return r?.modes?.length ? all.filter((m) => r.modes!.includes(m)) : all;
}

/** "silk_touch" → "Silk Touch". */
export function enchantLabel(name: string): string {
  return name
    .split('_')
    .map((w) => (w === 'of' ? w : w.charAt(0).toUpperCase() + w.slice(1)))
    .join(' ');
}

/** What a station recipe asks: "XP 1+ · lapis 1–3 · fuel: blaze powder · water nearby" (empty for crafting and smelting). */
export function requiresLine(r: StoryRecipe | undefined, option?: number | null): string {
  const q = r?.requires;
  if (!q) return '';
  const parts: string[] = [];
  if (q.xpLevels) parts.push(`XP level ${Math.max(q.xpLevels, option ?? 0)}+`);
  if (q.lapis) parts.push(option ? `${option} lapis` : `lapis 1–${q.lapis}`);
  if (q.fuelItem) parts.push(`fuel: ${q.fuelItem.replace(/_/g, ' ')}`);
  if (q.water) parts.push('water nearby (source or cauldron)');
  return parts.join(' · ');
}

/** The bench inspector's station line: bookshelves and the top offer, an anvil's wear, a stand's water and fuel. */
export function stationLines(bench: Pick<StoryWorkbench, 'kind' | 'station'>): string[] {
  const s = bench.station;
  if (!s) return [];
  const out: string[] = [];
  if (s.present === false) out.push(`The ${bench.kind.replace(/_/g, ' ')} is gone`);
  if (s.bookshelves !== null && s.bookshelves !== undefined) {
    const [lo, hi] = s.topOffer ?? [];
    out.push(
      `Bookshelves ${s.bookshelves}/${s.maxBookshelves ?? 15}` +
        (lo !== undefined && hi !== undefined ? ` · top offer level ${lo === hi ? lo : `${lo}–${hi}`}` : ''),
    );
  }
  if (s.condition) out.push(`Condition: ${s.condition}`);
  if (bench.kind === 'brewing_stand') {
    out.push(s.water ? `Water: ${s.water.join(', ')}` : 'No water nearby (a source block or a filled cauldron)');
    if (s.fuel !== null && s.fuel !== undefined) out.push(`Fuel: ${s.fuel} brew${s.fuel === 1 ? '' : 's'} left`);
  }
  return out;
}

/** "2 planks + 1 coal → 4 torches". */
export function recipeLine(r: StoryRecipe): string {
  const ins = r.inputs.map((i) => `${i.count} ${i.category && !i.options?.length ? i.category : storyItemLabel(i.options?.[0]?.[0] ?? 0, Math.max(0, i.options?.[0]?.[1] ?? 0)).toLowerCase()}`);
  return `${ins.join(' + ')} → ${r.output[2]} ${storyItemLabel(r.output[0], r.output[1]).toLowerCase()}`;
}
