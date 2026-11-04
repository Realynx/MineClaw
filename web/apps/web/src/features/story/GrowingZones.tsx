'use client';

/**
 * Growing zones (docs/web/story-mode.md "Farming"): the plant picker the Zones tool shows while "Growing zone" is the tool, and
 * the Growing zones window beside the Colony tab: each zone's planting bills (add with a plant picker, mode and target, drag or
 * arrows to reorder, pause, remove; the active one is what is sown), what it has (sown, ripe), its seeds (how many, where they
 * come from, what is missing) and the explicit action that gets some (cut tall grass nearby, or a seed bill).
 */
import type { StoryColonyState, StoryGrowBill, StoryPlant, StoryZone } from '@mineclaw/contracts';
import { Sprout } from 'lucide-react';
import { useState, useSyncExternalStore } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';
import { ItemIcon } from './visuals';
import { zoneCellCount } from './zones/zonePaint';

// ------------------------------------------------------------------ the plant the next growing zone gets

let chosen = 'wheat';
const listeners = new Set<() => void>();

/** The plant a newly painted growing zone grows (kept while the page is open). */
export function growPlantNow(): string {
  return chosen;
}

export function useGrowPlant(): [string, (id: string) => void] {
  const v = useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => void listeners.delete(l);
    },
    () => chosen,
    () => chosen,
  );
  return [
    v,
    (id) => {
      chosen = id;
      for (const l of listeners) l();
    },
  ];
}

function PlantIcon({ p, size = 16 }: { p: StoryPlant; size?: number }) {
  const [id, meta] = p.seedItem ?? [p.block ?? 0, 0];
  return <ItemIcon id={id} meta={meta} size={size} />;
}

/** The plant picker under the Zones tools while "Growing zone" is the tool. */
export function GrowPlantPicker({ plants }: { plants: readonly StoryPlant[] | null | undefined }) {
  const [plant, setPlant] = useGrowPlant();
  if (!plants?.length) return null;
  return (
    <div className="w-full" data-testid="grow-plant-picker">
      <div className="mb-1 text-[11px] font-semibold text-muted">Plant</div>
      <div className="flex flex-wrap gap-1" role="radiogroup" aria-label="Plant">
        {plants.map((p) => (
          <button
            key={p.id}
            type="button"
            role="radio"
            aria-checked={plant === p.id}
            title={`${p.label}: sows ${p.seed} on ${p.soil}. Seeds: ${p.source ?? ''}`}
            onClick={() => setPlant(p.id)}
            className={cn(
              'flex items-center gap-1 rounded border px-1.5 py-0.5 text-[11px]',
              plant === p.id ? 'border-accent bg-accent-soft text-fg' : 'border-border text-muted hover:text-fg',
            )}
          >
            <PlantIcon p={p} />
            {p.label}
          </button>
        ))}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ the Growing zones window

/** A planting bill change, sent on the zones route with the zone's id (docs/web/story-mode.md "Farming"). */
export type GrowBillChange =
  | { op: 'bill_add'; plant: string; mode?: StoryGrowBill['mode']; target?: number }
  | { op: 'bill_update'; bill: string | number; plant?: string; mode?: StoryGrowBill['mode']; target?: number }
  | { op: 'bill_remove' | 'bill_pause' | 'bill_resume'; bill: string | number }
  | { op: 'bill_reorder'; ids: Array<string | number> };

const MODE_LABEL: Record<StoryGrowBill['mode'], string> = {
  forever: 'Forever',
  until: 'Until you have',
  count: 'Harvest',
};

export function GrowingZonesPanel({
  state,
  canWrite,
  onUpdate,
  onBill,
  onExtend,
  onSeek,
  onShow,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { plant?: string | null; sow?: boolean; harvest?: boolean }) => void;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onExtend: (z: StoryZone) => void;
  onSeek: (z: StoryZone) => void;
  onShow: (z: StoryZone) => void;
}) {
  const zones = (state?.zones ?? []).filter((z) => z.kind === 'farm');
  if (!zones.length) return null;
  const plants = state?.plants ?? [];
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[60vh] w-[24rem] space-y-2 overflow-y-auto p-3 text-xs')}
      aria-label="Growing zones"
      data-testid="growing-zones"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Sprout className="size-3.5" aria-hidden /> Growing zones
      </div>
      <ul className="space-y-2">
        {zones.map((z) => (
          <GrowingZone
            key={String(z.id)}
            z={z}
            plants={plants}
            canWrite={canWrite}
            onUpdate={onUpdate}
            onBill={onBill}
            onExtend={onExtend}
            onSeek={onSeek}
            onShow={onShow}
          />
        ))}
      </ul>
    </section>
  );
}

function GrowingZone({
  z,
  plants,
  canWrite,
  onUpdate,
  onBill,
  onExtend,
  onSeek,
  onShow,
}: {
  z: StoryZone;
  plants: readonly StoryPlant[];
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { plant?: string | null; sow?: boolean; harvest?: boolean }) => void;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onExtend: (z: StoryZone) => void;
  onSeek: (z: StoryZone) => void;
  onShow: (z: StoryZone) => void;
}) {
  const [adding, setAdding] = useState(false);
  const [dragging, setDragging] = useState<number | null>(null);
  const active = plants.find((p) => p.id === z.activePlant);
  const s = z.seeds;
  const bills = z.bills ?? [];
  const growing = !!(z.plant || z.bills);
  const move = (from: number, to: number) => {
    if (to < 0 || to >= bills.length || from === to) return;
    const ids = bills.map((b) => b.id);
    const [m] = ids.splice(from, 1);
    ids.splice(to, 0, m!);
    onBill(z, { op: 'bill_reorder', ids });
  };
  return (
    <li className="space-y-1 rounded border border-border p-2" data-testid="growing-zone" data-zone={String(z.id)}>
      <div className="flex items-center gap-2">
        {active ? <PlantIcon p={active} /> : null}
        <button type="button" className="font-semibold hover:underline" onClick={() => onShow(z)}>
          {z.name || z.label || `Zone ${z.id}`}
        </button>
        <span className="text-subtle tabular-nums" data-testid="zone-cells">
          {zoneCellCount(z)} cells
        </span>
        <Button
          size="sm"
          variant="ghost"
          className="ml-auto h-6 px-1.5"
          disabled={!canWrite}
          onClick={() => onExtend(z)}
          title="Paint cells into this zone (they take its plant)"
        >
          Extend
        </Button>
      </div>
      {growing ? (
        <div data-testid="zone-active-plant" className={active ? 'text-fg' : 'text-muted'}>
          {active ? `Sowing: ${active.label}` : 'Sowing nothing: every bill is met or paused'}
        </div>
      ) : (
        <div className="text-muted">Ripe crops and bare farmland only: add a planting bill to choose what is sown.</div>
      )}
      {z.status ? <div className="text-muted">{z.status}</div> : null}
      {bills.length ? (
        <ol className="space-y-1" aria-label="Planting bills" data-testid="grow-bills">
          {bills.map((b, i) => {
            const p = plants.find((x) => x.id === b.plant);
            const progress =
              b.mode === 'until'
                ? `${b.stored ?? 0}/${b.target} stored`
                : b.mode === 'count'
                  ? `${b.done}/${b.target} harvested`
                  : null;
            return (
              <li
                key={String(b.id)}
                data-testid="grow-bill"
                data-bill={String(b.id)}
                data-active={b.active ? 'true' : 'false'}
                draggable={canWrite}
                onDragStart={() => setDragging(i)}
                onDragOver={(e) => e.preventDefault()}
                onDrop={() => {
                  if (dragging !== null) move(dragging, i);
                  setDragging(null);
                }}
                className={cn(
                  'flex flex-wrap items-center gap-1.5 rounded border px-1.5 py-1',
                  b.active ? 'border-accent bg-accent-soft' : 'border-border',
                  b.paused || b.met ? 'opacity-70' : '',
                )}
              >
                <span className="cursor-grab text-subtle" aria-hidden>
                  ⋮⋮
                </span>
                {p ? <PlantIcon p={p} size={14} /> : null}
                <span className="font-medium">{b.label ?? p?.label ?? b.plant}</span>
                <select
                  aria-label={`Mode of ${b.label ?? b.plant}`}
                  className="rounded border border-border bg-surface px-1 py-0.5"
                  value={b.mode}
                  disabled={!canWrite}
                  onChange={(e) =>
                    onBill(z, { op: 'bill_update', bill: b.id, mode: e.target.value as StoryGrowBill['mode'] })
                  }
                >
                  {(Object.keys(MODE_LABEL) as Array<StoryGrowBill['mode']>).map((m) => (
                    <option key={m} value={m}>
                      {MODE_LABEL[m]}
                    </option>
                  ))}
                </select>
                {b.mode !== 'forever' ? (
                  <input
                    type="number"
                    min={1}
                    max={100000}
                    aria-label={`Target of ${b.label ?? b.plant}`}
                    className="w-14 rounded border border-border bg-surface px-1 py-0.5 tabular-nums"
                    defaultValue={b.target}
                    disabled={!canWrite}
                    onBlur={(e) => {
                      const t = Math.round(Number(e.target.value));
                      if (t >= 1 && t !== b.target) onBill(z, { op: 'bill_update', bill: b.id, target: t });
                    }}
                  />
                ) : null}
                {progress ? <span className="text-subtle tabular-nums">{progress}</span> : null}
                {b.active ? (
                  <span className="text-accent">sowing</span>
                ) : b.met ? (
                  <span className="text-ok">met</span>
                ) : null}
                <span className="ml-auto flex gap-0.5">
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-5 px-1"
                    disabled={!canWrite || i === 0}
                    aria-label={`Move ${b.label ?? b.plant} up`}
                    onClick={() => move(i, i - 1)}
                  >
                    ↑
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-5 px-1"
                    disabled={!canWrite || i === bills.length - 1}
                    aria-label={`Move ${b.label ?? b.plant} down`}
                    onClick={() => move(i, i + 1)}
                  >
                    ↓
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-5 px-1"
                    disabled={!canWrite}
                    aria-label={`${b.paused ? 'Resume' : 'Pause'} ${b.label ?? b.plant}`}
                    onClick={() => onBill(z, { op: b.paused ? 'bill_resume' : 'bill_pause', bill: b.id })}
                  >
                    {b.paused ? '▶' : '❚❚'}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-5 px-1"
                    disabled={!canWrite}
                    aria-label={`Remove ${b.label ?? b.plant}`}
                    onClick={() => onBill(z, { op: 'bill_remove', bill: b.id })}
                  >
                    ×
                  </Button>
                </span>
                {b.seeds?.missing && !b.met ? <div className="w-full text-warn">{b.seeds.missing}</div> : null}
              </li>
            );
          })}
        </ol>
      ) : null}
      {canWrite ? (
        adding ? (
          <div
            className="flex flex-wrap gap-1"
            role="group"
            aria-label="Add a planting bill"
            data-testid="grow-bill-add"
          >
            {plants.map((p) => (
              <button
                key={p.id}
                type="button"
                title={`${p.label}: sows ${p.seed} on ${p.soil}`}
                onClick={() => {
                  setAdding(false);
                  onBill(z, { op: 'bill_add', plant: p.id });
                }}
                className="flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-[11px] text-muted hover:text-fg"
              >
                <PlantIcon p={p} />
                {p.label}
              </button>
            ))}
            <Button size="sm" variant="ghost" className="h-6" onClick={() => setAdding(false)}>
              Cancel
            </Button>
          </div>
        ) : (
          <Button size="sm" variant="subtle" className="h-6" onClick={() => setAdding(true)}>
            Add bill
          </Button>
        )
      ) : null}
      {s ? (
        <div className="text-muted" data-testid="zone-seeds">
          Seeds: {s.have} {s.item}
          {s.neededForEmpty ? ` · ${s.neededForEmpty} cell${s.neededForEmpty === 1 ? '' : 's'} to sow` : ''}
          {s.sourceHint ? <div className="text-subtle">From: {s.sourceHint}</div> : null}
        </div>
      ) : null}
      {s?.missing ? (
        <div className="text-warn" data-testid="zone-missing">
          {s.missing}
        </div>
      ) : null}
      {z.needsHoe ? (
        <div className="text-warn">Needs a hoe to till: store one, or a crafting table makes one</div>
      ) : null}
      {growing ? (
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex items-center gap-1">
            <input
              type="checkbox"
              checked={z.sow !== false}
              disabled={!canWrite}
              onChange={(e) => onUpdate(z, { sow: e.target.checked })}
            />
            Sow
          </label>
          <label className="flex items-center gap-1">
            <input
              type="checkbox"
              checked={z.harvest !== false}
              disabled={!canWrite}
              onChange={(e) => onUpdate(z, { harvest: e.target.checked })}
            />
            Harvest
          </label>
          {s?.action && canWrite ? (
            <Button size="sm" variant="subtle" onClick={() => onSeek(z)} data-testid="zone-seek-seeds">
              {s.action === 'seek_seeds' ? 'Designate grass cutting nearby' : 'Craft seeds (a bill)'}
            </Button>
          ) : null}
        </div>
      ) : null}
    </li>
  );
}
