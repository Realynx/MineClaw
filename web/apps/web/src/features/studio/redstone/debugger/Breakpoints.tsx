'use client';

/**
 * The breakpoints: each with its cell, its condition and whether it is on, and the hits that stopped the last step or
 * run, each a way to the cell. A breakpoint is checked once per game tick against the cell at the end of the tick before.
 */
import type { BreakOn, Breakpoint, BreakpointHit, Pos } from '@mineclaw/redstone';
import { Crosshair, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { cn } from '@/lib/utils';
import type { SimControllerApi, SimSnapshot } from '../simController.types';
import { chordLabel, nextId, useKeyMap } from './debuggerKeys';

export const CONDITIONS: ReadonlyArray<{ on: BreakOn; label: string; title: string }> = [
  { on: 'change', label: 'any change', title: 'The shown state or the power changes' },
  { on: 'rising', label: 'rising', title: 'The power goes from 0 to above 0' },
  { on: 'falling', label: 'falling', title: 'The power goes from above 0 to 0' },
  { on: 'atLeast', label: 'power at least', title: 'The power reaches the level you set, from below it' },
  { on: 'state', label: 'state becomes', title: 'The block becomes the id:meta you set' },
];

/** `atLeast` and `state` need a value; the other conditions take none. */
const takesValue = (on: BreakOn) => on === 'atLeast' || on === 'state';

/** A shown state (`id << 4 | meta`) as `id:meta`. */
export const stateText = (state: number): string => `${state >> 4}:${state & 15}`;

/** `id:meta` (or a bare id, meta 0) as a shown state; null when it is not one. */
export function parseState(text: string): number | null {
  const m = /^\s*(-?\d+)\s*(?::\s*(\d+))?\s*$/.exec(text);
  if (!m) return null;
  const id = Number(m[1]);
  const meta = m[2] === undefined ? 0 : Number(m[2]);
  if (!Number.isSafeInteger(id) || id < -1 || id > 4095 || meta > 15) return null;
  return id * 16 + meta;
}

/** A power level 1..15, or null. */
export function parsePower(text: string): number | null {
  const n = Number(text.trim());
  return Number.isInteger(n) && n >= 1 && n <= 15 ? n : null;
}

const valueText = (b: Breakpoint): string =>
  b.value === undefined ? '' : b.on === 'state' ? stateText(b.value) : String(b.value);

const hitText = (h: BreakpointHit, on: BreakOn | undefined): string =>
  on === 'change' || on === 'state'
    ? `${stateText(h.before)} → ${stateText(h.after)}`
    : `power ${h.before} → ${h.after}`;

export function Breakpoints({
  api,
  snap,
  selected,
  onJump,
}: {
  api: SimControllerApi;
  snap: SimSnapshot;
  selected: Pos | null;
  onJump: (at: Pos) => void;
}) {
  const add = () => {
    if (!selected) return;
    api.setBreakpoint({
      id: nextId(
        'b',
        snap.breakpoints.map((b) => b.id),
      ),
      at: [selected[0], selected[1], selected[2]],
      on: 'change',
      enabled: true,
    });
  };
  const hitIds = new Set(snap.lastHits.map((h) => h.id));
  const key = chordLabel(useKeyMap().toggleBreakpoint);
  return (
    <section className="px-3 py-2" aria-label="Breakpoints" data-testid="breakpoints">
      <div className="flex items-center gap-1">
        <h3 className="text-[11px] font-semibold text-fg">Breakpoints</h3>
        <span className="ml-auto" />
        <button
          type="button"
          onClick={add}
          disabled={!selected}
          title={selected ? `Break on ${selected.join(', ')}` : `Pick a cell first (or aim at one and press ${key})`}
          className="flex items-center gap-0.5 rounded px-1 py-0.5 text-[11px] text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-50"
        >
          <Plus className="size-3" /> on the picked cell
        </button>
      </div>
      {snap.breakpoints.length === 0 ? (
        <p className="mt-1 text-[11px] text-muted">
          None yet. Aim at a cell and press {key}, or pick one and add it here.
        </p>
      ) : (
        <ul className="mt-1 space-y-1">
          {snap.breakpoints.map((b) => (
            <BreakpointRow key={b.id} b={b} hit={hitIds.has(b.id)} api={api} onJump={onJump} />
          ))}
        </ul>
      )}
      <Hits snap={snap} onJump={onJump} />
    </section>
  );
}

function BreakpointRow({
  b,
  hit,
  api,
  onJump,
}: {
  b: Breakpoint;
  hit: boolean;
  api: SimControllerApi;
  onJump: (at: Pos) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const commit = () => {
    if (draft === null) return;
    const value = b.on === 'state' ? parseState(draft) : parsePower(draft);
    setDraft(null);
    if (value !== null && value !== b.value) api.setBreakpoint({ ...b, value });
  };
  const condition = (on: BreakOn) => {
    // A value means something else under another condition (a power level is no block state), so it goes.
    const { value: _old, ...rest } = b;
    api.setBreakpoint({ ...rest, on });
  };
  return (
    <li
      className={cn('rounded border border-border px-1.5 py-1', hit && 'border-danger bg-danger-soft')}
      data-testid={`breakpoint-${b.id}`}
    >
      <div className="flex items-center gap-1.5">
        <input
          type="checkbox"
          checked={b.enabled}
          onChange={(e) => api.setBreakpoint({ ...b, enabled: e.target.checked })}
          aria-label={`Breakpoint ${b.id} on`}
        />
        <button
          type="button"
          onClick={() => onJump(b.at)}
          title="Show this cell"
          className="flex items-center gap-0.5 font-mono text-[11px] text-fg hover:underline"
        >
          <Crosshair className="size-3 text-danger" /> {b.at.join(', ')}
        </button>
        <span className="ml-auto" />
        <button
          type="button"
          onClick={() => api.removeBreakpoint(b.id)}
          aria-label={`Remove breakpoint ${b.id}`}
          className="rounded p-0.5 text-muted hover:bg-surface-2 hover:text-danger"
        >
          <Trash2 className="size-3" />
        </button>
      </div>
      <div className="mt-1 flex items-center gap-1">
        <select
          aria-label={`Breakpoint ${b.id} condition`}
          value={b.on}
          onChange={(e) => condition(e.target.value as BreakOn)}
          title={CONDITIONS.find((c) => c.on === b.on)?.title}
          className="h-6 rounded border border-border bg-surface px-1 text-[11px] text-fg"
        >
          {CONDITIONS.map((c) => (
            <option key={c.on} value={c.on}>
              {c.label}
            </option>
          ))}
        </select>
        {takesValue(b.on) ? (
          <input
            aria-label={`Breakpoint ${b.id} value`}
            value={draft ?? valueText(b)}
            placeholder={b.on === 'state' ? 'id:meta' : '1-15'}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={commit}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commit();
            }}
            className="h-6 w-16 rounded border border-border bg-surface px-1 font-mono text-[11px] text-fg"
          />
        ) : null}
        {takesValue(b.on) && b.value === undefined ? (
          <span className="text-[10px] text-warn">needs a value to hit</span>
        ) : null}
      </div>
    </li>
  );
}

/** The breakpoints that stopped the last step or run, each a way to its cell. */
function Hits({ snap, onJump }: { snap: SimSnapshot; onJump: (at: Pos) => void }) {
  if (snap.lastHits.length === 0) return null;
  const onOf = new Map(snap.breakpoints.map((b) => [b.id, b.on]));
  return (
    <div className="mt-2" aria-label="Breakpoint hits" data-testid="breakpoint-hits">
      <h3 className="text-[11px] font-semibold text-danger">Stopped at tick {snap.lastHits[0]!.tick}</h3>
      <ul className="mt-0.5 space-y-0.5">
        {snap.lastHits.map((h) => (
          <li key={h.id}>
            <button
              type="button"
              onClick={() => onJump(h.at)}
              title="Show this cell"
              className="w-full rounded px-1 py-0.5 text-left text-[11px] text-fg hover:bg-surface-2"
            >
              <span className="font-mono text-muted">{h.id}</span> {h.at.join(', ')} · {hitText(h, onOf.get(h.id))}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
