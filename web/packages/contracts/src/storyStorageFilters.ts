import type { StoryFilter, StoryStorage, StoryStorageSign } from './story.js';

/** Bulk entry changes keep unrelated rules. Exclusions remain explicit even after broader categories are added later. */
export function changeStorageEntries(
  base: StoryFilter,
  entries: StoryFilter,
  op: 'add' | 'remove_entries',
): StoryFilter {
  const allow = new Set(base.allow),
    deny = new Set(base.deny);
  if (op === 'add') {
    for (const entry of entries.allow) {
      allow.add(entry);
      deny.delete(entry);
    }
    for (const entry of entries.deny) {
      deny.add(entry);
      allow.delete(entry);
    }
  } else {
    for (const entry of entries.allow) allow.delete(entry);
    for (const entry of entries.deny) deny.delete(entry);
    for (const entry of entries.allow) deny.add(entry);
  }
  return { allow: [...allow], deny: [...deny] };
}

/** Attached recognized signs combine their entries; an exclusion-only sign accepts the remaining items. */
export function storageSignFilter(signs: readonly StoryStorageSign[]): StoryFilter {
  const allow = [...new Set(signs.flatMap((s) => s.allow))];
  return { allow: allow.length ? allow : ['*'], deny: [...new Set(signs.flatMap((s) => s.deny))] };
}

/** Signs outrank all ordinary chests, including ordinary critical storage. */
export function compareStoragePriority(a: StoryStorage, b: StoryStorage): number {
  const signed = Number(!!b.signs?.length) - Number(!!a.signs?.length);
  if (signed) return signed;
  const order = ['critical', 'important', 'preferred', 'normal', 'low'];
  return order.indexOf(a.priority) - order.indexOf(b.priority);
}
