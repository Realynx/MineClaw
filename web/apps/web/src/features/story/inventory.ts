import { storyItemLabel, type StoryStorage } from '@mineclaw/contracts';

/** A double chest is one inventory, even if both halves appear in a state update. */
export function uniqueStorages(storages: readonly StoryStorage[]) {
  const groups: StoryStorage[][] = [];
  for (const storage of storages) {
    const cells = [storage.chest.join(','), storage.doubleWith?.join(',')].filter(Boolean);
    const matches = groups.filter((g) =>
      g.some((s) => cells.includes(s.chest.join(',')) || (s.doubleWith && cells.includes(s.doubleWith.join(',')))),
    );
    const merged = [storage, ...matches.flat()];
    for (const g of matches) groups.splice(groups.indexOf(g), 1);
    groups.push(merged);
  }
  const time = (t: StoryStorage['knownAt']) => (typeof t === 'number' ? t : Date.parse(t ?? '') || 0);
  return groups.map((g) => g.reduce((a, b) => (time(b.knownAt) > time(a.knownAt) ? b : a)));
}

export function storageInventory(storages: readonly StoryStorage[]) {
  const chests = uniqueStorages(storages);
  const totals = new Map<string, { id: number; meta: number; label: string; count: number; chests: number }>();
  for (const chest of chests) {
    const seen = new Set<string>();
    for (const [id, meta, count] of chest.contents) {
      if (count <= 0) continue;
      const key = `${id}:${meta}`;
      const item = totals.get(key) ?? { id, meta, label: storyItemLabel(id, meta), count: 0, chests: 0 };
      item.count += count;
      if (!seen.has(key)) item.chests++;
      seen.add(key);
      totals.set(key, item);
    }
  }
  return {
    chests: chests.length,
    unknown: chests.filter((s) => !s.knownAt && !s.contents.length).length,
    items: [...totals.values()].sort((a, b) => a.label.localeCompare(b.label) || a.meta - b.meta),
  };
}
