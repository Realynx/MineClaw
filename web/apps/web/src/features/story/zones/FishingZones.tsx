'use client';

/**
 * Fishing zones (docs/web/story-mode.md "Fishing"), beside the Colony tab under the Growing zones: each zone's water cells, who is
 * fishing there now, the catch so far, its status, and its bills (fish forever, until N raw fish are stored, or N catches; pause,
 * remove), sent on the zones route like planting bills.
 */
import type { StoryColonyState, StoryGrowBill, StoryZone } from '@mineclaw/contracts';
import { Fish } from 'lucide-react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import type { GrowBillChange } from '../GrowingZones';
import { panel } from '../StoryHud';
import { ZonePreserve } from './PreservePanel';

export function FishingZonesPanel({
  state,
  canWrite,
  onBill,
  onShow,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onShow: (z: StoryZone) => void;
}) {
  const zones = (state?.zones ?? []).filter((z) => z.kind === 'fish');
  if (!zones.length) return null;
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[40vh] w-[22rem] space-y-2 overflow-y-auto p-3 text-xs')}
      aria-label="Fishing zones"
      data-testid="fishing-zones"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Fish className="size-3.5" aria-hidden /> Fishing zones
      </div>
      <ul className="space-y-2">
        {zones.map((z) => (
          <FishingZone key={String(z.id)} z={z} canWrite={canWrite} onBill={onBill} onShow={onShow} />
        ))}
      </ul>
    </section>
  );
}

/** One fishing zone: its water, catch, status and who fishes there, its bills and the quick bills to add. */
function FishingZone({
  z,
  canWrite,
  onBill,
  onShow,
}: {
  z: StoryZone;
  canWrite: boolean;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onShow: (z: StoryZone) => void;
}) {
  return (
    <li className="space-y-1 rounded border border-border p-2" data-testid="fishing-zone">
      <div className="flex items-center gap-2">
        <button type="button" className="font-semibold hover:underline" onClick={() => onShow(z)}>
          {z.name || z.label || `Zone ${z.id}`}
        </button>
        <span className="text-subtle tabular-nums">{z.waterCells ?? z.cells ?? 0} water cells</span>
        <span className="ml-auto tabular-nums" data-testid="zone-catches">
          {z.catches ?? 0} caught
        </span>
      </div>
      <FishingStatus z={z} />
      {(z.bills ?? []).map((b) => (
        <FishBill key={String(b.id)} z={z} b={b} canWrite={canWrite} onBill={onBill} />
      ))}
      {canWrite ? <QuickFishBills z={z} onBill={onBill} /> : null}
      <ZonePreserve z={z} />
    </li>
  );
}

/** The zone's status and who is fishing there now. */
function FishingStatus({ z }: { z: StoryZone }) {
  return (
    <div className="text-muted">
      {z.status ?? 'fishing'}
      {z.fishers?.length ? ` · ${z.fishers.join(', ')} fishing` : ''}
    </div>
  );
}

function QuickFishBills({ z, onBill }: { z: StoryZone; onBill: (z: StoryZone, change: GrowBillChange) => void }) {
  return (
    <div className="flex gap-1">
      <Button
        size="sm"
        variant="subtle"
        className="h-6"
        onClick={() => onBill(z, { op: 'bill_add', plant: 'fish', mode: 'until', target: 20 })}
      >
        Fish until 20 stored
      </Button>
      <Button
        size="sm"
        variant="ghost"
        className="h-6"
        onClick={() => onBill(z, { op: 'bill_add', plant: 'fish', mode: 'count', target: 10 })}
      >
        Catch 10
      </Button>
    </div>
  );
}

function FishBill({
  z,
  b,
  canWrite,
  onBill,
}: {
  z: StoryZone;
  b: StoryGrowBill;
  canWrite: boolean;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
}) {
  const what =
    b.mode === 'until'
      ? `until ${b.target} stored (${b.stored ?? 0})`
      : b.mode === 'count'
        ? `catch ${b.target} (${b.done})`
        : 'forever';
  return (
    <div
      className={cn(
        'flex items-center gap-1.5 rounded border px-1.5 py-0.5',
        b.active ? 'border-accent' : 'border-border',
      )}
    >
      <span>Fish {what}</span>
      {b.met ? <span className="text-ok">met</span> : null}
      <span className="ml-auto flex gap-0.5">
        <Button
          size="sm"
          variant="ghost"
          className="h-5 px-1"
          disabled={!canWrite}
          aria-label={b.paused ? 'Resume fishing bill' : 'Pause fishing bill'}
          onClick={() => onBill(z, { op: b.paused ? 'bill_resume' : 'bill_pause', bill: b.id })}
        >
          {b.paused ? '▶' : '❚❚'}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          className="h-5 px-1"
          disabled={!canWrite}
          aria-label="Remove fishing bill"
          onClick={() => onBill(z, { op: 'bill_remove', bill: b.id })}
        >
          ×
        </Button>
      </span>
    </div>
  );
}
