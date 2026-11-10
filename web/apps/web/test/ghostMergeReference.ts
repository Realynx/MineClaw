/** The Controller's ghost merge as it was before its index (lag lane S5): a map of every held ghost by "<build>|x,y,z" on each
 * delta. Kept as the reference the indexed `mergeGhosts` is held to, and as the "before" of the strip-mine cost test. */
import type { StoryColonyState } from '@mineclaw/contracts';

export type Ghosts = NonNullable<StoryColonyState['ghosts']>;
export type Ghost = Ghosts['blocks'][number];

export function plainMergeGhosts(
  prev: Ghosts | null | undefined,
  next: Ghosts | null | undefined,
  removedBuilds?: ReadonlyArray<unknown> | null,
  removedGhosts?: ReadonlyArray<string> | null,
): Ghosts | null | undefined {
  if (!prev) return next;
  const key = (g: Ghost) => `${g[5]}|${g[0]},${g[1]},${g[2]}`;
  const gone = new Set((removedBuilds ?? []).map(String));
  const cells = new Set((removedGhosts ?? []).map(String));
  const fresh = next?.blocks ?? [];
  const touched = () => {
    if (fresh.length > 0) return true;
    if (!gone.size && !cells.size) return false;
    return prev.blocks.some((g) => gone.has(String(g[5])) || cells.has(key(g)));
  };
  if (!touched()) return prev;
  const map = new Map<string, Ghost>();
  for (const g of prev.blocks) if (!cells.has(key(g))) map.set(key(g), g);
  for (const g of fresh) map.set(key(g), g);
  return { rev: Math.max(prev.rev, next?.rev ?? 0), blocks: [...map.values()].filter((g) => !gone.has(String(g[5]))) };
}
