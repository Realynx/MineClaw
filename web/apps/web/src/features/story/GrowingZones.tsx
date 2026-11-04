'use client';

/**
 * Growing zones (docs/web/story-mode.md "Farming"): the plant picker the Zones tool shows while "Growing zone" is the tool, and
 * the Growing zones window beside the Colony tab: each zone's plant, what it has (sown, ripe), its seeds (how many, where they
 * come from, what is missing) and the explicit action that gets some (cut tall grass nearby, or a seed bill).
 */
import type { StoryColonyState, StoryPlant, StoryZone } from '@mineclaw/contracts';
import { Sprout } from 'lucide-react';
import { useSyncExternalStore } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';
import { ItemIcon } from './visuals';

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

export function GrowingZonesPanel({
  state,
  canWrite,
  onUpdate,
  onSeek,
  onShow,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { plant?: string | null; sow?: boolean; harvest?: boolean }) => void;
  onSeek: (z: StoryZone) => void;
  onShow: (z: StoryZone) => void;
}) {
  const zones = (state?.zones ?? []).filter((z) => z.kind === 'farm');
  if (!zones.length) return null;
  const plants = state?.plants ?? [];
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[60vh] w-[22rem] space-y-2 overflow-y-auto p-3 text-xs')}
      aria-label="Growing zones"
      data-testid="growing-zones"
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <Sprout className="size-3.5" aria-hidden /> Growing zones
      </div>
      <ul className="space-y-2">
        {zones.map((z) => {
          const plant = plants.find((p) => p.id === z.plant);
          const s = z.seeds;
          return (
            <li key={String(z.id)} className="space-y-1 rounded border border-border p-2" data-testid="growing-zone">
              <div className="flex items-center gap-2">
                {plant ? <PlantIcon p={plant} /> : null}
                <button type="button" className="font-semibold hover:underline" onClick={() => onShow(z)}>
                  {z.name || z.label || `Zone ${z.id}`}
                </button>
                <select
                  aria-label="Plant"
                  className="ml-auto rounded border border-border bg-surface px-1 py-0.5"
                  value={z.plant ?? ''}
                  disabled={!canWrite}
                  onChange={(e) => onUpdate(z, { plant: e.target.value || null })}
                >
                  <option value="">ripe crops only</option>
                  {plants.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.label}
                    </option>
                  ))}
                </select>
              </div>
              {z.status ? <div className="text-muted">{z.status}</div> : null}
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
              {z.needsHoe ? <div className="text-warn">Needs a hoe to till: store one, or a crafting table makes one</div> : null}
              {z.plant ? (
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
        })}
      </ul>
    </section>
  );
}
