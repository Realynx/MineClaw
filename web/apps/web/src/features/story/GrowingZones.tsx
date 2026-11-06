'use client';

/**
 * Growing zones (docs/web/story-mode.md "Farming"): the plant picker the Zones tool shows while "Growing zone" is the tool, and
 * the Growing zones window beside the Colony tab: each zone's planting bills (add with a plant picker, mode and target, drag or
 * arrows to reorder, pause, remove; the active one is what is sown), what it has (sown, ripe), its seeds (how many, where they
 * come from, what is missing) and the explicit action that gets some (cut tall grass nearby, or a seed bill).
 */
import type { StoryColonyState, StoryGrowBill, StoryPlant, StoryZone } from '@mineclaw/contracts';
import { Sprout } from 'lucide-react';
import { useState, useSyncExternalStore, type ReactNode } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';
import { ItemIcon } from './visuals';
import { ZonePreserve } from './zones/PreservePanel';
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
  onIrrigate,
}: {
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { plant?: string | null; sow?: boolean; harvest?: boolean }) => void;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onExtend: (z: StoryZone) => void;
  onSeek: (z: StoryZone) => void;
  onShow: (z: StoryZone) => void;
  /** "Add irrigation": the water channels the zone wants, pre-filled for the owner to queue (docs/web/story-mode.md "Water"). */
  onIrrigate?: (z: StoryZone) => void;
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
            onIrrigate={onIrrigate}
          />
        ))}
      </ul>
    </section>
  );
}

/** A growing zone short of water: cells too far from it to till ("N too far from water"), or sugar cane with none beside it. */
export function needsWater(z: StoryZone): boolean {
  return (
    /too far from water|no water within/i.test(z.status ?? '') ||
    (z.activePlant === 'sugar_cane' && /no water|beside water/i.test(z.status ?? ''))
  );
}

/** The warning line: how many cells are dry. */
export function dryText(z: StoryZone): string {
  const m = /(\d+) too far from water/.exec(z.status ?? '');
  return m
    ? `${m[1]} cell${m[1] === '1' ? '' : 's'} have no water within 4 blocks`
    : 'No water within 4 blocks of the zone';
}

type ZoneProps = {
  z: StoryZone;
  plants: readonly StoryPlant[];
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { plant?: string | null; sow?: boolean; harvest?: boolean }) => void;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  onExtend: (z: StoryZone) => void;
  onSeek: (z: StoryZone) => void;
  onShow: (z: StoryZone) => void;
  onIrrigate?: (z: StoryZone) => void;
};

function GrowingZone(p: ZoneProps) {
  const { z, plants, canWrite } = p;
  const active = plants.find((x) => x.id === z.activePlant);
  const growing = !!(z.plant || z.bills);
  const [adding, setAdding] = useState(false);
  return (
    <li className="space-y-1 rounded border border-border p-2" data-testid="growing-zone" data-zone={String(z.id)}>
      <ZoneHeader z={z} active={active} canWrite={canWrite} onShow={p.onShow} onExtend={p.onExtend} />
      <SowingLine growing={growing} active={active} />
      {z.status ? <div className="text-muted">{z.status}</div> : null}
      {needsWater(z) ? <DryWarning z={z} canWrite={canWrite} onIrrigate={p.onIrrigate} /> : null}
      {z.bills?.length ? (
        <GrowBills z={z} bills={z.bills} plants={plants} canWrite={canWrite} onBill={p.onBill} />
      ) : null}
      {canWrite ? <AddGrowBill z={z} plants={plants} onBill={p.onBill} adding={adding} setAdding={setAdding} /> : null}
      <ZoneSeeds seeds={z.seeds} />
      {z.needsHoe ? (
        <div className="text-warn">Needs a hoe to till: store one, or a crafting table makes one</div>
      ) : null}
      {growing ? <ZoneSwitches z={z} canWrite={canWrite} onUpdate={p.onUpdate} onSeek={p.onSeek} /> : null}
      <ZonePreserve z={z} />
    </li>
  );
}

/** What the zone sows now (its active bill's plant), or why nothing. */
function SowingLine({ growing, active }: { growing: boolean; active: StoryPlant | undefined }) {
  if (!growing)
    return (
      <div className="text-muted">Ripe crops and bare farmland only: add a planting bill to choose what is sown.</div>
    );
  return (
    <div data-testid="zone-active-plant" className={active ? 'text-fg' : 'text-muted'}>
      {active ? `Sowing: ${active.label}` : 'Sowing nothing: every bill is met or paused'}
    </div>
  );
}

function ZoneHeader({
  z,
  active,
  canWrite,
  onShow,
  onExtend,
}: {
  z: StoryZone;
  active: StoryPlant | undefined;
  canWrite: boolean;
  onShow: (z: StoryZone) => void;
  onExtend: (z: StoryZone) => void;
}) {
  return (
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
  );
}

function DryWarning({
  z,
  canWrite,
  onIrrigate,
}: {
  z: StoryZone;
  canWrite: boolean;
  onIrrigate?: (z: StoryZone) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-warn" data-testid="zone-dry">
      {dryText(z)}
      {onIrrigate && canWrite ? (
        <Button size="sm" variant="subtle" className="h-6" onClick={() => onIrrigate(z)} data-testid="zone-irrigate">
          Add irrigation
        </Button>
      ) : null}
    </div>
  );
}

/** The planting bills, in order: drag or the arrows reorder them. */
function GrowBills({
  z,
  bills,
  plants,
  canWrite,
  onBill,
}: {
  z: StoryZone;
  bills: readonly StoryGrowBill[];
  plants: readonly StoryPlant[];
  canWrite: boolean;
  onBill: (z: StoryZone, change: GrowBillChange) => void;
}) {
  const [dragging, setDragging] = useState<number | null>(null);
  const move = (from: number, to: number) => {
    if (to < 0 || to >= bills.length || from === to) return;
    const ids = bills.map((b) => b.id);
    const [m] = ids.splice(from, 1);
    ids.splice(to, 0, m!);
    onBill(z, { op: 'bill_reorder', ids });
  };
  return (
    <ol className="space-y-1" aria-label="Planting bills" data-testid="grow-bills">
      {bills.map((b, i) => (
        <GrowBillRow
          key={String(b.id)}
          b={b}
          plant={plants.find((x) => x.id === b.plant)}
          first={i === 0}
          last={i === bills.length - 1}
          canWrite={canWrite}
          onChange={(change) => onBill(z, change)}
          onMove={(d) => move(i, i + d)}
          onDragStart={() => setDragging(i)}
          onDrop={() => {
            if (dragging !== null) move(dragging, i);
            setDragging(null);
          }}
        />
      ))}
    </ol>
  );
}

/** "3/5 stored", "2/20 harvested", or nothing for a bill that runs forever. */
function billProgress(b: StoryGrowBill): string | null {
  if (b.mode === 'until') return `${b.stored ?? 0}/${b.target} stored`;
  return b.mode === 'count' ? `${b.done}/${b.target} harvested` : null;
}

function billBadge(b: StoryGrowBill): ReactNode {
  if (b.active) return <span className="text-accent">sowing</span>;
  return b.met ? <span className="text-ok">met</span> : null;
}

function billRowClass(b: StoryGrowBill): string {
  return cn(
    'flex flex-wrap items-center gap-1.5 rounded border px-1.5 py-1',
    b.active ? 'border-accent bg-accent-soft' : 'border-border',
    b.paused || b.met ? 'opacity-70' : '',
  );
}

function billLabel(b: StoryGrowBill, plant: StoryPlant | undefined): string {
  return b.label ?? plant?.label ?? b.plant;
}

/** What an unmet bill is short of (its seeds). */
function BillMissing({ b }: { b: StoryGrowBill }) {
  return b.seeds?.missing && !b.met ? <div className="w-full text-warn">{b.seeds.missing}</div> : null;
}

function GrowBillRow({
  b,
  plant,
  first,
  last,
  canWrite,
  onChange,
  onMove,
  onDragStart,
  onDrop,
}: {
  b: StoryGrowBill;
  plant: StoryPlant | undefined;
  first: boolean;
  last: boolean;
  canWrite: boolean;
  onChange: (change: GrowBillChange) => void;
  onMove: (d: 1 | -1) => void;
  onDragStart: () => void;
  onDrop: () => void;
}) {
  const name = b.label ?? b.plant;
  const progress = billProgress(b);
  return (
    <li
      data-testid="grow-bill"
      data-bill={String(b.id)}
      data-active={b.active ? 'true' : 'false'}
      draggable={canWrite}
      onDragStart={onDragStart}
      onDragOver={(e) => e.preventDefault()}
      onDrop={onDrop}
      className={billRowClass(b)}
    >
      <span className="cursor-grab text-subtle" aria-hidden>
        ⋮⋮
      </span>
      {plant ? <PlantIcon p={plant} size={14} /> : null}
      <span className="font-medium">{billLabel(b, plant)}</span>
      <BillModeSelect b={b} name={name} canWrite={canWrite} onChange={onChange} />
      {b.mode !== 'forever' ? <BillTarget b={b} name={name} canWrite={canWrite} onChange={onChange} /> : null}
      {progress ? <span className="text-subtle tabular-nums">{progress}</span> : null}
      {billBadge(b)}
      <BillControls
        b={b}
        name={name}
        first={first}
        last={last}
        canWrite={canWrite}
        onChange={onChange}
        onMove={onMove}
      />
      <BillMissing b={b} />
    </li>
  );
}

/** Up, down, pause / resume and remove. */
function BillControls({
  b,
  name,
  first,
  last,
  canWrite,
  onChange,
  onMove,
}: BillFieldProps & { first: boolean; last: boolean; onMove: (d: 1 | -1) => void }) {
  return (
    <span className="ml-auto flex gap-0.5">
      <BillButton disabled={!canWrite || first} label={`Move ${name} up`} onClick={() => onMove(-1)}>
        ↑
      </BillButton>
      <BillButton disabled={!canWrite || last} label={`Move ${name} down`} onClick={() => onMove(1)}>
        ↓
      </BillButton>
      <BillButton
        disabled={!canWrite}
        label={`${b.paused ? 'Resume' : 'Pause'} ${name}`}
        onClick={() => onChange({ op: b.paused ? 'bill_resume' : 'bill_pause', bill: b.id })}
      >
        {b.paused ? '▶' : '❚❚'}
      </BillButton>
      <BillButton
        disabled={!canWrite}
        label={`Remove ${name}`}
        onClick={() => onChange({ op: 'bill_remove', bill: b.id })}
      >
        ×
      </BillButton>
    </span>
  );
}

function BillButton({
  disabled,
  label,
  onClick,
  children,
}: {
  disabled: boolean;
  label: string;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <Button size="sm" variant="ghost" className="h-5 px-1" disabled={disabled} aria-label={label} onClick={onClick}>
      {children}
    </Button>
  );
}

type BillFieldProps = {
  b: StoryGrowBill;
  name: string;
  canWrite: boolean;
  onChange: (change: GrowBillChange) => void;
};

function BillModeSelect({ b, name, canWrite, onChange }: BillFieldProps) {
  return (
    <select
      aria-label={`Mode of ${name}`}
      className="rounded border border-border bg-surface px-1 py-0.5"
      value={b.mode}
      disabled={!canWrite}
      onChange={(e) => onChange({ op: 'bill_update', bill: b.id, mode: e.target.value as StoryGrowBill['mode'] })}
    >
      {(Object.keys(MODE_LABEL) as Array<StoryGrowBill['mode']>).map((m) => (
        <option key={m} value={m}>
          {MODE_LABEL[m]}
        </option>
      ))}
    </select>
  );
}

function BillTarget({ b, name, canWrite, onChange }: BillFieldProps) {
  return (
    <input
      type="number"
      min={1}
      max={100000}
      aria-label={`Target of ${name}`}
      className="w-14 rounded border border-border bg-surface px-1 py-0.5 tabular-nums"
      defaultValue={b.target}
      disabled={!canWrite}
      onBlur={(e) => {
        const t = Math.round(Number(e.target.value));
        if (t >= 1 && t !== b.target) onChange({ op: 'bill_update', bill: b.id, target: t });
      }}
    />
  );
}

/** "Add bill", then the plants to pick from. */
function AddGrowBill({
  z,
  plants,
  onBill,
  adding,
  setAdding,
}: {
  z: StoryZone;
  plants: readonly StoryPlant[];
  onBill: (z: StoryZone, change: GrowBillChange) => void;
  adding: boolean;
  setAdding: (on: boolean) => void;
}) {
  if (!adding)
    return (
      <Button size="sm" variant="subtle" className="h-6" onClick={() => setAdding(true)}>
        Add bill
      </Button>
    );
  return (
    <div className="flex flex-wrap gap-1" role="group" aria-label="Add a planting bill" data-testid="grow-bill-add">
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
  );
}

/** The zone's seeds: how many, the cells still to sow, where they come from, and what is missing. */
function ZoneSeeds({ seeds: s }: { seeds: StoryZone['seeds'] }) {
  if (!s) return null;
  return (
    <>
      <div className="text-muted" data-testid="zone-seeds">
        Seeds: {s.have} {s.item}
        {s.neededForEmpty ? ` · ${s.neededForEmpty} cell${s.neededForEmpty === 1 ? '' : 's'} to sow` : ''}
        {s.sourceHint ? <div className="text-subtle">From: {s.sourceHint}</div> : null}
      </div>
      {s.missing ? (
        <div className="text-warn" data-testid="zone-missing">
          {s.missing}
        </div>
      ) : null}
    </>
  );
}

/** Sow / Harvest switches and the explicit seed action. */
function ZoneSwitches({
  z,
  canWrite,
  onUpdate,
  onSeek,
}: {
  z: StoryZone;
  canWrite: boolean;
  onUpdate: (z: StoryZone, change: { sow?: boolean; harvest?: boolean }) => void;
  onSeek: (z: StoryZone) => void;
}) {
  const action = z.seeds?.action;
  return (
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
      {action && canWrite ? (
        <Button size="sm" variant="subtle" onClick={() => onSeek(z)} data-testid="zone-seek-seeds">
          {action === 'seek_seeds' ? 'Designate grass cutting nearby' : 'Craft seeds (a bill)'}
        </Button>
      ) : null}
    </div>
  );
}
