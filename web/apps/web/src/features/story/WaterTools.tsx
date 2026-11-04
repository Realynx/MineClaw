'use client';

/**
 * The Architect's water tools (docs/web/story-mode.md "Water"): the options bar a water tool docks (dig the cells into the ground
 * first), and the confirm bar for a placement whose water would run beyond what was drawn (the director's flow preview), or that
 * the irrigation planner pre-filled. Pure UI: StoryMode owns the requests.
 */
import { expandBuildItem, type StoryBuildItem, type StoryLiquidFlow, type WaterToolId } from '@mineclaw/contracts';
import { Droplets } from 'lucide-react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';
import { WATER_TOOLS, waterDig, type BuildOpts } from './tools';

const bar = panel;

/** A water placement waiting for the owner's word: what it queues, what it would flood, what the director says about it. */
export interface PendingWater {
  label: string;
  items: StoryBuildItem[];
  cells: number;
  flow: StoryLiquidFlow | null;
  warnings: string[];
}

/** The hologram cells a pending placement shows: the cells it digs first (id 0: red dig outlines, the trench), its sources, and
 * where the water would run (flowing water, level 4). */
export function pendingHolo(p: PendingWater): Array<[number, number, number, number, number]> {
  const out: Array<[number, number, number, number, number]> = [];
  for (const it of p.items) if (it.kind === 'remove') for (const [x, y, z] of expandBuildItem(it)) out.push([x, y, z, 0, 0]);
  for (const it of p.items)
    if (it.kind === 'water')
      for (const [x, y, z] of it.blocks ?? []) out.push([x, y, z, it.material === 'lava' ? 11 : 9, 0]);
  for (const [x, y, z] of p.flow?.cells ?? []) out.push([x, y, z, 8, 4]);
  return out.slice(0, 4096);
}

/** The line a pending placement shows: cells, and how far the water would run. */
export function pendingLine(p: PendingWater): string {
  const spread = p.flow?.spread ?? 0;
  return (
    `${p.label}: ${p.cells} source${p.cells === 1 ? '' : 's'}` +
    (spread > 0 ? ` · spreads to ${spread} more cell${spread === 1 ? '' : 's'}` : ' · stays where it is drawn')
  );
}

/** The bar a water tool docks bottom-left (where the material selector sits for the other build tools). */
export function WaterBar({
  toolId,
  build,
  onBuild,
  onClose,
}: {
  toolId: WaterToolId;
  build: BuildOpts;
  onBuild: (b: Partial<BuildOpts>) => void;
  onClose: () => void;
}) {
  const spec = WATER_TOOLS.find((t) => t.id === toolId);
  const dig = waterDig(toolId, build);
  return (
    <section
      className={cn(bar, 'pointer-events-auto flex max-w-[40rem] flex-wrap items-center gap-3 px-3 py-2 text-xs')}
      aria-label="Water tool"
      data-testid="water-bar"
    >
      <Droplets className="size-4 text-sky-400" aria-hidden />
      <span className="font-semibold">{spec?.label ?? 'Water'}</span>
      <span className="text-muted">{spec?.hint}</span>
      <label className="flex items-center gap-1" title="The cells go one block into the ground, dug out first (a trench, a sunken pool)">
        <input type="checkbox" checked={dig} onChange={(e) => onBuild({ dig: e.target.checked })} data-testid="water-dig" />
        Dig the trench
      </label>
      <span className="text-subtle">Pawns bring a bucket, fill it at still water and pour each cell</span>
      <Button size="sm" variant="ghost" className="ml-auto h-6" onClick={onClose}>
        Done
      </Button>
    </section>
  );
}

/** A water placement to confirm: where it would run beyond what was drawn, and the director's warnings. */
export function WaterConfirm({
  pending,
  onQueue,
  onCancel,
}: {
  pending: PendingWater;
  onQueue: () => void;
  onCancel: () => void;
}) {
  return (
    <section
      className={cn(bar, 'pointer-events-auto w-[30rem] space-y-1.5 px-3 py-2 text-xs')}
      aria-label="Confirm water"
      data-testid="water-confirm"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Droplets className="size-4 text-sky-400" aria-hidden />
        <span data-testid="water-confirm-line">{pendingLine(pending)}</span>
      </div>
      {pending.warnings.map((w) => (
        <div key={w} className="text-warn">
          {w}
        </div>
      ))}
      <div className="flex gap-2">
        <Button size="sm" onClick={onQueue} data-testid="water-queue">
          Queue
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel} data-testid="water-cancel">
          Cancel
        </Button>
      </div>
    </section>
  );
}
