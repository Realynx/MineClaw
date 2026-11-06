'use client';

/**
 * The zones without a window of their own (Preserve zones, woodlots, keep-out areas, drop spots), beside the Colony tab after the
 * quarries: each one's kind, name and size, Show, its "Preserve blocks" settings, and Remove for a Preserve zone (it is always on:
 * removing it is how it stops).
 */
import type { StoryColonyState, StoryZone } from '@mineclaw/contracts';
import { Layers, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from '../StoryHud';
import { ZONE_TOOLS, zoneColor } from '../tools';
import { ZonePreserve } from './PreservePanel';
import { zoneCellCount } from './zonePaint';

/** The kinds listed here, in this order (growing, fishing and quarry zones and houses have their own settings). */
const KINDS = ['preserve', 'chop', 'forbidden', 'dump'] as const;
const kindLabel = (kind: string) => ZONE_TOOLS.find((t) => t.id === kind)?.label ?? kind;

export function OtherZonesPanel({
  state,
  canWrite,
  onShow,
  onRemove,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onShow: (z: StoryZone) => void;
  onRemove: (z: StoryZone) => void;
}) {
  const zones = KINDS.flatMap((k) => (state?.zones ?? []).filter((z) => z.kind === k));
  if (!zones.length) return null;
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[40vh] w-[22rem] space-y-2 overflow-y-auto p-3 text-xs')}
      aria-label="Other zones"
      data-testid="other-zones"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Layers className="size-3.5" aria-hidden /> Other zones
      </div>
      <ul className="space-y-2">
        {zones.map((z) => (
          <li
            key={String(z.id)}
            className="space-y-1 rounded border border-border p-2"
            data-testid="other-zone"
            data-zone={String(z.id)}
          >
            <OtherZoneHeader z={z} canWrite={canWrite} onShow={onShow} onRemove={onRemove} />
            <ZonePreserve z={z} />
          </li>
        ))}
      </ul>
    </section>
  );
}

function OtherZoneHeader({
  z,
  canWrite,
  onShow,
  onRemove,
}: {
  z: StoryZone;
  canWrite: boolean;
  onShow: (z: StoryZone) => void;
  onRemove: (z: StoryZone) => void;
}) {
  const name = z.name || z.label || `${kindLabel(z.kind)} ${z.id}`;
  return (
    <div className="flex items-center gap-2">
      <span className="size-2.5 shrink-0 rounded-sm" style={{ background: zoneColor(z.kind) }} aria-hidden />
      <button type="button" className="min-w-0 truncate font-semibold hover:underline" onClick={() => onShow(z)}>
        {name}
      </button>
      <span className="text-subtle">{kindLabel(z.kind)}</span>
      <span className="text-subtle tabular-nums">{zoneCellCount(z)} cells</span>
      {z.kind === 'preserve' ? (
        <Button
          size="sm"
          variant="ghost"
          className="ml-auto h-6 px-1.5 text-danger"
          disabled={!canWrite}
          onClick={() => onRemove(z)}
          aria-label={`Remove ${name}`}
          title="Remove the zone: its blocks are no longer kept"
        >
          <Trash2 className="size-3.5" />
        </Button>
      ) : null}
    </div>
  );
}
