/**
 * Water (docs/web/story-mode.md "Water"): a placement is checked by the director first (its cells, where the water would
 * run); one that would flood beyond what was drawn, or that the irrigation planner pre-filled, waits for the owner's Queue.
 */
import { waterItems, type StoryBuildItem, type StoryZone } from '@mineclaw/contracts';
import { useMemo } from 'react';
import { errorText } from '@/lib/api/client';
import { pendingHolo, type PendingWater } from '../WaterTools';
import type { Story } from './commands';

/** The map's hologram of the placement waiting for its confirm (keyed so the map redraws only when it changes). */
export function usePendingWaterHolo(pendingWater: PendingWater | null) {
  return useMemo(
    () =>
      pendingWater
        ? {
            key: `${pendingWater.label}|${pendingWater.cells}|${JSON.stringify(pendingWater.items.at(-1)?.blocks?.[0] ?? null)}`,
            blocks: pendingHolo(pendingWater),
          }
        : null,
    [pendingWater],
  );
}

/** Queues a checked placement. */
export async function queueWater(c: Story, p: PendingWater) {
  c.setPendingWater(null);
  await c.run(
    { route: 'story.builds', body: { op: 'add', items: p.items } },
    { kind: 'build', build: p.items },
    'queued',
    (out) => {
      const w = (out as { warnings?: string[] | null } | null)?.warnings;
      return `Queued ${p.label}: ${p.cells} source${p.cells === 1 ? '' : 's'}${w?.length ? ` (${w[0]})` : ''}`;
    },
  );
}

/** The water sources the items place. */
const waterCells = (items: StoryBuildItem[]) =>
  items.filter((i) => i.kind === 'water').reduce((n, i) => n + (i.blocks?.length ?? 0), 0);

/** A placement waits for the confirm when asked to, when its water would spread beyond the drawn cells, or when it warns. */
const holdForConfirm = (confirm: boolean, spread: number | undefined, warnings: string[]) =>
  confirm || (spread ?? 0) > 0 || warnings.length > 0;

/** Checks a placement with the director: queued at once, or held for the confirm when it spreads, warns or `confirm` asks. */
export async function placeWater(c: Story, items: StoryBuildItem[], label: string, confirm = false) {
  if (!c.canWrite) return c.say('Needs the story role', 'warn');
  try {
    const out = await c.write('story.builds', { op: 'preview', items } as never);
    if (out.error) return c.say(out.error, 'warn');
    const pending: PendingWater = {
      label,
      items,
      cells: waterCells(items),
      flow: out.flow ?? null,
      warnings: out.warnings ?? [],
    };
    if (holdForConfirm(confirm, out.flow?.spread, pending.warnings)) c.setPendingWater(pending);
    else await queueWater(c, pending);
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}

/** The irrigation plan's items: the trench it digs (when any), then the channel, grouped so they go together. */
function irrigationItems(z: StoryZone, cells: Array<[number, number, number]>, dig: Array<[number, number, number]>) {
  const group = `irrigation-${String(z.id)}`.slice(0, 40);
  const name = `irrigation (${z.name || z.label || `zone ${z.id}`})`;
  const items: StoryBuildItem[] = [
    ...(dig.length ? [{ kind: 'remove' as const, blocks: dig, group }] : []),
    { ...waterItems('water_channel', cells, { dig: false, name })[0]!, ...(dig.length ? { group } : {}) },
  ];
  return items;
}

/** A growing zone's irrigation: the planner's channel, shown on the map and held for the confirm. */
export async function irrigate(c: Story, z: StoryZone) {
  try {
    const out = await c.write('story.zones', { op: 'irrigation', id: z.id } as never);
    const cells = out.cells ?? [];
    if (!cells.length) return c.say(out.note ?? 'Nothing to irrigate', 'ok');
    const items = irrigationItems(z, cells, out.dig ?? []);
    c.map.current?.focus(cells[0]![0], cells[0]![2], cells[0]![1]);
    await placeWater(c, items, out.note ? `Irrigation: ${out.note}` : 'Irrigation', true);
  } catch (e) {
    c.say(errorText(e), 'warn');
  }
}
