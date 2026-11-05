'use client';

/**
 * The colony's craft tasks (docs/web/story-mode.md "Craft tasks"): what the colony crafts for itself (tools pawns wait on, a
 * build's ingredients, a farm's hoe), kept apart from the owner's bills. The Work tab lists them as "Crafting needs" (immediate
 * first); a bench inspector shows one line for the tasks at that bench ("Also crafting: 7 wooden pickaxes for mining").
 */
import type { StoryCraftTask } from '@mineclaw/contracts';
import { Hammer, Trash2 } from 'lucide-react';
import { cn } from '@/lib/utils';

type V3 = readonly number[];

/** "Make Wooden Pickaxe (from any planks)" → "wooden pickaxe". */
export function taskItem(t: StoryCraftTask): string {
  const raw = t.label ?? t.item.replace(/^(craft|smelt):/, '').replace(/_/g, ' ');
  return raw
    .replace(/^(Make|Smelt) /, '')
    .replace(/ \(from any [^)]*\)$/, '')
    .toLowerCase();
}

/** "7 wooden pickaxe for mining: Cleo Z. (+1 spare)", a reason that isn't a "for …" set off with a dash. */
export function taskLine(t: StoryCraftTask): string {
  const what = `${t.count} ${taskItem(t)}`;
  if (!t.reason) return what;
  return /^for /.test(t.reason) ? `${what} ${t.reason}` : `${what} — ${t.reason}`;
}

/** Immediate tasks first, then by bench order as the colony sent them. */
export function sortTasks(tasks: readonly StoryCraftTask[] | null | undefined): StoryCraftTask[] {
  const list = (tasks ?? []).filter((t) => t.count > 0);
  return [...list.filter((t) => t.priority === 'immediate'), ...list.filter((t) => t.priority !== 'immediate')];
}

/** The bench inspector's line: the tasks at the bench at `pos`, or null when there are none. */
export function benchTaskLine(tasks: readonly StoryCraftTask[] | null | undefined, pos: V3): string | null {
  const here = sortTasks(tasks).filter(
    (t) => t.bench && t.bench[0] === pos[0] && t.bench[1] === pos[1] && t.bench[2] === pos[2],
  );
  if (here.length === 0) return null;
  const shown = here.slice(0, 3).map(taskLine);
  return `Also crafting: ${shown.join('; ')}${here.length > 3 ? ` and ${here.length - 3} more` : ''}`;
}

const STATUS: Record<string, string> = { crafting: 'crafting', waiting: 'waiting', paused: 'paused', queued: 'queued' };

/** The Work tab's "Crafting needs": each task with its count, reason and status; a bench task can be cancelled. */
export function CraftNeeds({
  tasks,
  canWrite,
  onCancel,
}: {
  tasks: readonly StoryCraftTask[] | null | undefined;
  canWrite: boolean;
  onCancel?: (t: StoryCraftTask) => void;
}) {
  const list = sortTasks(tasks);
  if (list.length === 0) return null;
  return (
    <div className="mt-2 border-t border-border pt-1.5 text-[11px]" data-testid="craft-needs">
      <div className="mb-1 flex items-center gap-1 px-1 font-semibold">
        <Hammer className="size-3.5" aria-hidden /> Crafting needs
        <span className="font-normal text-muted">· the colony's own crafts, not bills</span>
      </div>
      <ul className="space-y-0.5">
        {list.map((t) => (
          <li
            key={String(t.id)}
            data-task={String(t.id)}
            data-priority={t.priority}
            className={cn(
              'flex items-center gap-1.5 rounded px-1 py-0.5',
              t.priority === 'immediate' && 'bg-accent-soft',
            )}
            title={t.blocked ?? undefined}
          >
            {t.priority === 'immediate' ? (
              <span className="rounded bg-accent px-1 text-[10px] font-semibold text-accent-fg">now</span>
            ) : null}
            <span className="min-w-0 flex-1 truncate">{taskLine(t)}</span>
            <span className={cn('text-[10px]', t.status === 'waiting' ? 'text-danger' : 'text-muted')}>
              {STATUS[t.status] ?? t.status}
            </span>
            {onCancel && t.bench ? (
              <button
                type="button"
                disabled={!canWrite}
                onClick={() => onCancel(t)}
                aria-label={`Cancel ${taskItem(t)}`}
                title="Cancel: it isn't made by itself (a build waits for the item instead)"
                className="rounded p-0.5 text-danger hover:bg-danger-soft"
              >
                <Trash2 className="size-3" />
              </button>
            ) : null}
          </li>
        ))}
      </ul>
    </div>
  );
}
