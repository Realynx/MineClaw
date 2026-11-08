import type { StoryBuild } from '@mineclaw/contracts';

export type BuildGroup = { key: string; title: string; grouped: boolean; builds: StoryBuild[]; active: StoryBuild[] };

/** Old clients named parts but omitted group ids. Only coalesce a tightly bounded creation batch, never names alone. */
function legacyPlacements(builds: readonly StoryBuild[]): Map<StoryBuild['id'], string> {
  const when = (b: StoryBuild) => (typeof b.created === 'number' ? b.created : Date.parse(b.created ?? ''));
  const named = builds
    .filter((b) => !b.group && b.name && b.kind !== 'preserve' && Number.isFinite(when(b)))
    .sort((a, b) => when(a) - when(b) || String(a.id).localeCompare(String(b.id)));
  const result = new Map<StoryBuild['id'], string>();
  let batch: StoryBuild[] = [];
  const finish = () => {
    if (batch.length > 1) for (const b of batch) result.set(b.id, `legacy:${batch[0]!.id}`);
  };
  for (const b of named) {
    if (batch.length && (b.name !== batch[0]!.name || when(b) - when(batch[0]!) > 100)) {
      finish();
      batch = [];
    }
    batch.push(b);
  }
  finish();
  return result;
}

/** Group by placement identity; legacy named batches get a presentation-only identity. */
export function buildGroups(builds: readonly StoryBuild[]): BuildGroup[] {
  const groups = new Map<string, BuildGroup>();
  const legacy = legacyPlacements(builds);
  for (const b of [...builds].sort((a, b) => a.order - b.order)) {
    const identity = b.kind === 'preserve' ? undefined : b.group ? `group:${b.group}` : legacy.get(b.id);
    const grouped = !!identity;
    const key = identity ?? `build:${b.id}`;
    const g = groups.get(key) ?? { key, title: b.name || b.label, grouped, builds: [], active: [] };
    if (b.name) g.title = b.name;
    g.builds.push(b);
    if (b.status !== 'done') g.active.push(b);
    groups.set(key, g);
  }
  return [...groups.values()];
}

export function reorderBuildGroup(groups: readonly BuildGroup[], from: string, onto: string): Array<string | number> {
  const moved = groups.find((g) => g.key === from);
  if (!moved || from === onto || !groups.some((g) => g.key === onto)) return [];
  const rest = groups.filter((g) => g !== moved);
  rest.splice(
    rest.findIndex((g) => g.key === onto),
    0,
    moved,
  );
  return rest.flatMap((g) => g.active.map((b) => b.id));
}
