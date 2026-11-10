'use client';

/**
 * The probes: each watched cell with its name, its colour (its marker on the canvas and its lane in the timeline), its
 * power 0..15 and on/off now, and the tick it last changed.
 */
import type { Pos, Sample } from '@mineclaw/redstone';
import { Plus, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { cn } from '@/lib/utils';
import type { Probe, SimControllerApi, SimSnapshot } from '../simController.types';
import { chordLabel, useKeyMap } from './debuggerKeys';
import { cssColor } from './timelineDraw';

export interface ProbeReading {
  /** The cell now; null while there is no simulation to read. */
  now: Sample | null;
  /** The last tick its power or state changed, as far back as history reaches; null when it has not. */
  changedAt: number | null;
}

/** A probe's reading from its samples (oldest first, ending at the current tick). */
export function readProbe(samples: readonly Sample[]): ProbeReading {
  const now = samples[samples.length - 1] ?? null;
  for (let i = samples.length - 1; i > 0; i--) {
    const a = samples[i - 1]!;
    const b = samples[i]!;
    if (a.power !== b.power || a.state !== b.state) return { now, changedAt: b.tick };
  }
  return { now, changedAt: null };
}

export function WatchList({
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
  const key = chordLabel(useKeyMap().toggleProbe);
  const { probes } = snap;
  // Read again when the controller says something moved (a rebuilt simulation can be at the same tick).
  const readings = useMemo(
    () =>
      new Map(snap.probes.map((p) => [p.id, readProbe(api.timeline(p.id, snap.tick - snap.canStepBack, snap.tick))])),
    [api, snap],
  );
  return (
    <section className="px-3 py-2" aria-label="Probes" data-testid="watch-list">
      <div className="flex items-center gap-1">
        <h3 className="text-[11px] font-semibold text-fg">Probes</h3>
        <span className="ml-auto" />
        <button
          type="button"
          onClick={() => selected && api.addProbe(selected)}
          disabled={!selected}
          title={selected ? `Watch ${selected.join(', ')}` : `Pick a cell first (or aim at one and press ${key})`}
          className="flex items-center gap-0.5 rounded px-1 py-0.5 text-[11px] text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-50"
        >
          <Plus className="size-3" /> on the picked cell
        </button>
      </div>
      {probes.length === 0 ? (
        <p className="mt-1 text-[11px] text-muted">
          None yet. Aim at a cell and press {key}, or pick one and add it here.
        </p>
      ) : (
        <ul className="mt-1 space-y-1">
          {probes.map((p) => (
            <ProbeRow key={p.id} p={p} reading={readings.get(p.id)} api={api} onJump={onJump} />
          ))}
        </ul>
      )}
    </section>
  );
}

function ProbeRow({
  p,
  reading,
  api,
  onJump,
}: {
  p: Probe;
  reading: ProbeReading | undefined;
  api: SimControllerApi;
  onJump: (at: Pos) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const commit = () => {
    if (draft !== null && draft.trim() && draft.trim() !== p.label) api.renameProbe(p.id, draft);
    setDraft(null);
  };
  const now = reading?.now ?? null;
  return (
    <li className="flex items-center gap-1.5 rounded border border-border px-1.5 py-1" data-testid={`probe-${p.id}`}>
      <button
        type="button"
        onClick={() => onJump(p.at)}
        title={`Show ${p.at.join(', ')}`}
        aria-label={`Show probe ${p.label}`}
        className="size-3 shrink-0 rounded-sm"
        style={{ backgroundColor: cssColor(p.color) }}
      />
      <input
        aria-label={`Probe ${p.id} name`}
        value={draft ?? p.label}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit();
          if (e.key === 'Escape') setDraft(null);
        }}
        className="h-6 w-16 min-w-0 rounded border border-transparent bg-transparent px-1 text-[11px] text-fg hover:border-border focus:border-border"
      />
      <span className="font-mono text-[11px] text-fg" data-testid={`probe-${p.id}-power`} title="Power 0..15">
        {now ? now.power : '–'}
      </span>
      <span
        className={cn(
          'rounded px-1 text-[10px] font-semibold',
          now?.powered ? 'bg-danger-soft text-danger' : 'bg-surface-2 text-muted',
        )}
      >
        {now ? (now.powered ? 'on' : 'off') : '–'}
      </span>
      <span
        className="ml-auto font-mono text-[10px] text-muted"
        data-testid={`probe-${p.id}-changed`}
        title="The last tick it changed"
      >
        {reading && reading.changedAt !== null ? `Δ ${reading.changedAt}` : ''}
      </span>
      <button
        type="button"
        onClick={() => api.removeProbe(p.id)}
        aria-label={`Remove probe ${p.label}`}
        className="rounded p-0.5 text-muted hover:bg-surface-2 hover:text-danger"
      >
        <Trash2 className="size-3" />
      </button>
    </li>
  );
}
