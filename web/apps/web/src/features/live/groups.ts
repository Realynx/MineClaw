import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';

export type BodyKind = HiveTreeNode['kind'];

/** One hive of the grid: the hive's own body first, then the bodies it drives (subhive and script-only). */
export interface BodyGroup {
  /** The hive's account (main's login for the main group). */
  id: string;
  /** The hive's own row (absent when the hive itself isn't listed, e.g. still spawning). */
  head: BotStatusView | undefined;
  kind: 'main' | 'hive';
  bodies: BotStatusView[];
  online: number;
}

export function kindOf(b: BotStatusView, main: string, node: HiveTreeNode | undefined): BodyKind {
  return b.kind ?? node?.kind ?? (b.name === main ? 'main' : 'hive');
}

/** The owning hive's account: the row's `owner`, else the tree's (whose root owner reads "main"), else main. */
export function ownerOf(b: BotStatusView, main: string, node: HiveTreeNode | undefined): string | null {
  if (b.name === main) return null;
  const o = b.owner ?? node?.owner ?? main;
  return o === 'main' ? main : o;
}

const shortNum = (b: BotStatusView) => {
  const n = Number(/(\d+)$/.exec(b.short ?? b.name)?.[1]);
  return Number.isFinite(n) ? n : Number.MAX_SAFE_INTEGER;
};
const byShort = (a: BotStatusView, b: BotStatusView) => shortNum(a) - shortNum(b) || a.name.localeCompare(b.name);

/**
 * Groups every body by the hive that drives it: main (with main's own subhive and script bodies), then each hive with its
 * bodies. A hive's sub-hives (a hive owned by a hive) get a group of their own after their parent. Pure (unit-tested).
 */
export function groupBodies(bots: readonly BotStatusView[], main: string, tree: ReadonlyMap<string, HiveTreeNode>): BodyGroup[] {
  const kind = new Map(bots.map((b) => [b.name, kindOf(b, main, tree.get(b.name))]));
  const groups = new Map<string, BodyGroup>();
  const group = (id: string): BodyGroup => {
    let g = groups.get(id);
    if (!g) groups.set(id, (g = { id, head: undefined, kind: id === main ? 'main' : 'hive', bodies: [], online: 0 }));
    return g;
  };
  group(main);
  for (const b of bots) {
    const k = kind.get(b.name);
    if (b.name === main || k === 'main') {
      group(main).head = b;
      continue;
    }
    if (k === 'hive') {
      group(b.name).head = b;
      continue;
    }
    const owner = ownerOf(b, main, tree.get(b.name)) ?? main;
    group(kind.get(owner) === 'hive' || (owner !== main && !kind.has(owner)) ? owner : main).bodies.push(b);
  }
  // Order: main, then hives depth-first in name order (a hive owned by a hive follows its parent).
  const parentOf = (id: string) => {
    const head = groups.get(id)?.head;
    const o = head ? ownerOf(head, main, tree.get(id)) : (tree.get(id)?.owner ?? main);
    return o === 'main' || !o ? main : o;
  };
  const ordered: BodyGroup[] = [];
  const seen = new Set<string>();
  const visit = (id: string, depth: number) => {
    if (seen.has(id) || depth > 16) return;
    seen.add(id);
    const g = groups.get(id);
    if (g) ordered.push(g);
    const kids = [...groups.keys()].filter((k) => k !== id && k !== main && parentOf(k) === id).sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
    for (const k of kids) visit(k, depth + 1);
  };
  visit(main, 0);
  for (const id of groups.keys()) visit(id, 1); // orphans (owner not listed)
  for (const g of ordered) {
    g.bodies.sort(byShort);
    g.online = [g.head, ...g.bodies].filter((b) => b?.status === 'online').length;
  }
  return ordered.filter((g) => g.head || g.bodies.length);
}
