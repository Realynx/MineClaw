/**
 * One of the owner's bills in the Bills inspector (StoryInspector.tsx `BillsInspector`): drag to reorder, click to edit, the
 * furnace's fuel for it, pause / resume, delete.
 */
import type { StoryBill, StoryColonyState, StoryRecipe, StoryWorkbench } from '@mineclaw/contracts';
import { GripVertical, Pause, Play, Trash2 } from 'lucide-react';
import { cn } from '@/lib/utils';
import {
  billKey,
  ignoreError,
  mergeBillBodies,
  patchBill,
  patchBillRemove,
  trackOrWrite,
  type Patch,
  type TrackedWrites,
} from '../optimistic';
import { billProgress, billSummary, enchantLabel, fuelLabel, fuelLine } from '../storage';
import type { StoryWrite } from '../useStory';
import { ItemIcon } from '../visuals';
import { BillPrioritySelect } from './BillPrioritySelect';

/** "· Ann only" when the bill is one pawn's. */
function pawnOnly(b: StoryBill, state: StoryColonyState | undefined): string {
  if (b.pawn === 'any') return '';
  return ` · ${(state?.pawns ?? []).find((p) => p.name === b.pawn)?.display ?? b.pawn} only`;
}

/** "prefers Efficiency II only · needs XP level 5" (nothing when neither applies). */
function BillNeeds({ b }: { b: StoryBill }) {
  if (!b.prefer && !b.xpNeeded) return null;
  return (
    <div className="text-[10.5px] text-muted">
      {b.prefer ? `prefers ${enchantLabel(b.prefer)}${b.preferOnly ? ' only' : ''}` : ''}
      {b.prefer && b.xpNeeded ? ' · ' : ''}
      {b.xpNeeded ? `needs XP level ${b.xpNeeded}` : ''}
    </div>
  );
}

/** The bill's lines: its recipe, mode and progress, who does it, needs, fuel, the last run, a suspension. */
function BillText({ b, name, state }: { b: StoryBill; name: string; state: StoryColonyState | undefined }) {
  return (
    <>
      <div className="truncate font-semibold">{name}</div>
      <div className="text-[10.5px] text-muted">
        {billSummary(b)} · {billProgress(b)}
        {pawnOnly(b, state)}
      </div>
      <BillNeeds b={b} />
      {b.fuelNeed ? (
        <div className={cn('text-[10.5px]', b.fuelNeed.short ? 'text-danger' : 'text-muted')} data-testid="bill-fuel">
          {fuelLine(b.fuelNeed)}
        </div>
      ) : null}
      {b.last ? <div className="truncate text-[10.5px] text-subtle">Last: {b.last}</div> : null}
      {b.waitingReason ? <div className="text-[10.5px] text-muted">Waiting: {b.waitingReason}</div> : null}
      {b.suspendedReason ? <div className="text-[10.5px] text-danger">Suspended: {b.suspendedReason}</div> : null}
    </>
  );
}

/** A furnace bill's own fuel (the furnace's unless set). */
function BillFuelSelect({
  b,
  name,
  bench,
  canWrite,
  update,
}: {
  b: StoryBill;
  name: string;
  bench: StoryWorkbench;
  canWrite: boolean;
  update: (fields: Partial<StoryBill>, body: Record<string, unknown>) => void;
}) {
  if (bench.kind !== 'furnace' || !bench.fuelChoices?.length) return null;
  return (
    <select
      aria-label={`Fuel for ${name}`}
      title="Fuel for this bill (the furnace's unless set)"
      disabled={!canWrite}
      value={b.fuel ?? ''}
      onChange={(e) => update({ fuel: e.target.value || null }, { fuel: e.target.value || 'inherit' })}
      className={cn(
        'h-6 max-w-[6.5rem] rounded border border-border bg-surface px-0.5 text-[10.5px]',
        b.fuel && 'border-accent',
      )}
      data-testid="bill-fuel-chip"
    >
      <option value="">Furnace's ({fuelLabel(bench.fuel).split(' (')[0]})</option>
      {bench.fuelChoices.map((f) => (
        <option key={f} value={f}>
          {fuelLabel(f)}
        </option>
      ))}
    </select>
  );
}

export interface BillRowProps extends TrackedWrites {
  bill: StoryBill;
  recipe: StoryRecipe | undefined;
  bench: StoryWorkbench;
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
  onDragStart: () => void;
  onDrop: () => void;
  onEdit: () => void;
}

/** An owner's bill: drag handle, icon, its text (click to edit), fuel, pause / resume, delete. */
export function BillRow({
  bill: b,
  recipe: r,
  bench,
  state,
  canWrite,
  write,
  track,
  onError = ignoreError,
  onDragStart,
  onDrop,
  onEdit,
}: BillRowProps) {
  const name = r?.label ?? b.recipe;
  // Priority, fuel and delete share the bill's key (two updates merge, a delete replaces them); pause has its own, so a
  // quick pause after a priority change never replaces it.
  const send = (key: string, apply: Patch, body: Record<string, unknown>) => {
    const spec = { key, apply, merge: mergeBillBodies };
    void trackOrWrite(track, write, spec, 'story.bills', body as never).catch(onError);
  };
  const update = (fields: Partial<StoryBill>, body: Record<string, unknown>) =>
    send(billKey(b.id), patchBill(b.id, fields), { op: 'update', id: b.id, ...body });
  return (
    <li
      data-bill={b.id}
      draggable={canWrite}
      onDragStart={onDragStart}
      onDragOver={(e) => e.preventDefault()}
      onDrop={onDrop}
      className={cn(
        'flex items-center gap-1.5 rounded-md border border-border bg-surface-2 px-1.5 py-1',
        b.paused && 'opacity-60',
      )}
    >
      <GripVertical className="size-3 cursor-grab text-subtle" aria-hidden />
      {r ? <ItemIcon id={r.output[0]} meta={r.output[1]} size={18} /> : null}
      <button type="button" onClick={onEdit} className="min-w-0 flex-1 text-left">
        <BillText b={b} name={name} state={state} />
      </button>
      <BillPrioritySelect value={b.priority ?? 3} label={`Priority for ${name}`} disabled={!canWrite}
        onChange={(priority) => update({ priority }, { priority })} />
      <BillFuelSelect b={b} name={name} bench={bench} canWrite={canWrite} update={update} />
      <button
        type="button"
        disabled={!canWrite}
        onClick={() =>
          send(`${billKey(b.id)}:pause`, patchBill(b.id, { paused: !b.paused }), {
            op: b.paused ? 'resume' : 'pause',
            id: b.id,
          })
        }
        aria-label={b.paused ? `Resume ${name}` : `Pause ${name}`}
        className="rounded p-0.5 hover:bg-surface-3"
      >
        {b.paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
      </button>
      <button
        type="button"
        disabled={!canWrite}
        onClick={() => send(billKey(b.id), patchBillRemove(b.id), { op: 'remove', id: b.id })}
        aria-label={`Delete ${name}`}
        className="rounded p-0.5 text-danger hover:bg-danger-soft"
      >
        <Trash2 className="size-3.5" />
      </button>
    </li>
  );
}
