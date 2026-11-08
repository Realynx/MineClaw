import { expandBuildItem, normArea, type StoryArea, type StoryBuildItem } from './story.js';

/** Match the director's palette replacement: missing cells inside the bounding box are intentional air. */
export function replacementItems(items: readonly StoryBuildItem[]): { items: readonly StoryBuildItem[]; area?: StoryArea } {
  const tagged = items.filter((i) => i.replaceArea);
  if (!tagged.length) return { items };
  if (tagged.length !== 1) throw new Error('One replacement design per request');
  const area = normArea(tagged[0]!.replaceArea!);
  const volume = (area[3] - area[0] + 1) * (area[4] - area[1] + 1) * (area[5] - area[2] + 1);
  if (volume > 262144 || area[1] < 1 || area[4] > 255)
    throw new Error('Replacement design exceeds the world or 262144 cells');
  const cells = items.flatMap(expandBuildItem);
  if (items.some((i) => i.kind === 'mine') || cells.some((c) => !insideReplacement(area, c)))
    throw new Error('All replacement cells must be inside the replacement area');
  const explicit = new Set(cells.map((c) => c.slice(0, 3).join(',')));
  const dig: [number, number, number][] = [];
  for (let y = area[4]; y >= area[1]; y--)
    for (let x = area[0]; x <= area[3]; x++)
      for (let z = area[2]; z <= area[5]; z++)
        if (!explicit.has(`${x},${y},${z}`)) dig.push([x, y, z]);
  const group = tagged[0]!.group ?? 'replacement';
  const out: StoryBuildItem[] = [];
  for (let i = 0; i < dig.length; i += 4096) out.push({ kind: 'remove', group, blocks: dig.slice(i, i + 4096) });
  out.push(...items.map((i) => ({ ...i, group })));
  return { items: out, area };
}

export function insideReplacement(a: StoryArea, p: readonly [number, number, number, ...unknown[]]): boolean {
  return p[0] >= a[0] && p[0] <= a[3] && p[1] >= a[1] && p[1] <= a[4] && p[2] >= a[2] && p[2] <= a[5];
}
