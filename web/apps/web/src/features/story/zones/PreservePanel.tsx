'use client';

/**
 * A zone's "Preserve blocks" settings (docs/web/story-mode.md "Preserve zones"), the same section in every zone's settings: the
 * switch (a mine zone can't: it digs its blocks; a Preserve zone is always on), how deep and how high it keeps (0..32 under and
 * over the painted level, sent when committed), the y range kept, the repair status and counts, and "Re-take snapshot". Also the
 * depth and height a newly painted Preserve zone gets (under the Zones tools).
 */
import type { StoryZone, StoryZonePreserve } from '@mineclaw/contracts';
import { ShieldCheck } from 'lucide-react';
import { createContext, useContext, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import {
  parseRange,
  preserveCounts,
  preserveRangeText,
  preserveRefused,
  usePreservePaintRange,
  type PreserveChange,
} from './preserve';

/** What the zone panels write through: the Story page's zones route (none outside the page: the section is not shown). */
export interface PreserveWriter {
  canWrite: boolean;
  onPreserve: (z: StoryZone, change: PreserveChange) => void;
}

export const PreserveContext = createContext<PreserveWriter | null>(null);

/** The zone's Preserve section, wired to the page's writer (nothing without one). */
export function ZonePreserve({ z }: { z: StoryZone }) {
  const w = useContext(PreserveContext);
  return w ? <PreservePanel z={z} canWrite={w.canWrite} onPreserve={w.onPreserve} /> : null;
}

type Props = { z: StoryZone; canWrite: boolean; onPreserve: (z: StoryZone, change: PreserveChange) => void };

export function PreservePanel({ z, canWrite, onPreserve }: Props) {
  const refused = preserveRefused(z);
  const p = z.preserve?.on ? z.preserve : null;
  return (
    <div className="w-full space-y-1 rounded border border-border/70 px-1.5 py-1" data-testid="zone-preserve">
      <PreserveSwitch z={z} on={!!p} refused={refused} canWrite={canWrite} onPreserve={onPreserve} />
      {refused ? <div className="text-[10.5px] text-subtle">{refused}</div> : null}
      {p ? <PreserveDetails z={z} p={p} canWrite={canWrite} onPreserve={onPreserve} /> : null}
    </div>
  );
}

/** The section's head: the switch, or "Always on" for a Preserve zone. */
function PreserveSwitch({
  z,
  on,
  refused,
  canWrite,
  onPreserve,
}: Omit<Props, 'z'> & { z: StoryZone; on: boolean; refused: string | null }) {
  if (z.kind === 'preserve')
    return (
      <div className="flex items-center gap-1.5">
        <ShieldCheck className="size-3.5 text-[#e879c9]" aria-hidden />
        <span className="font-medium">Preserve blocks</span>
        <span className="ml-auto text-[10.5px] text-muted" title="Erase or remove the zone to stop keeping its blocks">
          Always on
        </span>
      </div>
    );
  return (
    <label className="flex items-center gap-1.5" title={refused ?? 'Pawns keep these blocks as they are and repair any change'}>
      <input
        type="checkbox"
        role="switch"
        aria-label="Preserve blocks"
        checked={on}
        disabled={!canWrite || !!refused}
        onChange={(e) => onPreserve(z, { on: e.target.checked })}
      />
      <span className="font-medium">Preserve blocks</span>
    </label>
  );
}

/** A preserved zone's range, status, counts and the re-take button. */
function PreserveDetails({ z, p, canWrite, onPreserve }: Props & { p: StoryZonePreserve }) {
  const damaged = p.toRepair > 0;
  return (
    <>
      <div className="flex flex-wrap items-center gap-2">
        <RangeInput
          label="Depth below"
          value={p.below}
          disabled={!canWrite}
          onCommit={(below) => onPreserve(z, { below })}
        />
        <RangeInput
          label="Height above"
          value={p.above}
          disabled={!canWrite}
          onCommit={(above) => onPreserve(z, { above })}
        />
        <span className="text-subtle tabular-nums" data-testid="preserve-range">
          {preserveRangeText(p)}
        </span>
      </div>
      <div className={cn(damaged ? 'text-warn' : 'text-ok')} data-testid="preserve-status">
        {p.status ?? (damaged ? `${p.toRepair} blocks to repair` : 'intact')}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-[10.5px] text-subtle tabular-nums" data-testid="preserve-counts">
          {preserveCounts(p)}
        </span>
        <Button
          size="sm"
          variant="ghost"
          className="ml-auto h-6 px-1.5"
          disabled={!canWrite}
          onClick={() => onPreserve(z, { retake: true })}
          title="Keep the blocks as they are now (the old snapshot is forgotten)"
        >
          Re-take snapshot
        </Button>
      </div>
      {z.kind === 'preserve' ? (
        <div className="text-[10.5px] text-subtle">Erase or remove the zone to stop keeping its blocks.</div>
      ) : null}
    </>
  );
}

/**
 * A 0..32 number field sent when committed (Enter or leaving it); a value that isn't one goes back to the current. It follows
 * the server's value when that changes.
 */
export function RangeInput({
  label,
  value,
  disabled,
  onCommit,
}: {
  label: string;
  value: number;
  disabled?: boolean;
  onCommit: (v: number) => void;
}) {
  const [text, setText] = useState(String(value));
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setText(String(value));
  }
  const commit = () => {
    const n = parseRange(text);
    if (n === null || n === value) setText(String(value));
    else onCommit(n);
  };
  return (
    <label className="flex items-center gap-1 text-muted">
      {label}
      <input
        type="number"
        min={0}
        max={32}
        step={1}
        inputMode="numeric"
        aria-label={label}
        value={text}
        disabled={disabled}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur();
          else if (e.key === 'Escape') setText(String(value));
        }}
        className="h-6 w-12 rounded border border-border bg-surface px-1 text-fg tabular-nums disabled:opacity-50"
      />
    </label>
  );
}

/** Under the Zones tools while Preserve is the tool: how deep and how high the next painted zone keeps. */
export function PreserveRangePicker() {
  const [r, setR] = usePreservePaintRange();
  return (
    <div className="w-full space-y-1" data-testid="preserve-paint-range">
      <div className="text-[11px] font-semibold text-muted">Preserve</div>
      <div className="flex flex-wrap items-center gap-2 text-[11px]">
        <RangeInput label="Depth below" value={r.below} onCommit={(below) => setR({ below })} />
        <RangeInput label="Height above" value={r.above} onCommit={(above) => setR({ above })} />
      </div>
      <p className="text-[11px] leading-snug text-muted">
        Paint it flat: the blocks from {r.below} under the level to {r.above} over it are kept (change it later in the zone's
        settings).
      </p>
    </div>
  );
}
