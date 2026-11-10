'use client';

/**
 * A storage chest's contents as the colony's own ledger has them: item totals, the slots in use and when they were last
 * counted. The page already holds this for every storage chest (`state.storages`), so the chest inspector shows it the
 * moment a chest is clicked, in place of a loading skeleton, until the exact slot grid arrives and replaces it.
 */
import { storyItemLabel, type StoryStorage } from '@mineclaw/contracts';
import { useState } from 'react';
import { toMs } from '@/lib/utils';
import { freshnessText } from '@/features/live/inventory/containerLayout';
import { ItemIcon } from '../visuals';

/** Whether the ledger has anything to show for a chest: counted contents, or at least the time of a count. */
export function ledgerKnown(s: StoryStorage | undefined): s is StoryStorage {
  return !!s && (s.contents.length > 0 || toMs(s.knownAt) !== null);
}

export interface LedgerRow {
  id: number;
  meta: number;
  label: string;
  count: number;
}

/** The ledger's entries as one row per item, most first (the ledger keeps totals, not slots). */
export function ledgerRows(s: Pick<StoryStorage, 'contents'>): LedgerRow[] {
  const rows = new Map<string, LedgerRow>();
  for (const [id, meta, count] of s.contents) {
    if (count <= 0) continue;
    const key = `${id}:${meta}`;
    const row = rows.get(key) ?? { id, meta, label: storyItemLabel(id, Math.max(0, meta)), count: 0 };
    row.count += count;
    rows.set(key, row);
  }
  return [...rows.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
}

/** Slots in use of the chest's 27 or 54, once the chest has been counted (before that `freeSlots` is only a default). */
export function ledgerSlots(
  s: Pick<StoryStorage, 'doubleWith' | 'freeSlots' | 'knownAt'>,
): { used: number; slots: number } | null {
  if (toMs(s.knownAt) === null || s.freeSlots === null || s.freeSlots === undefined) return null;
  const slots = s.doubleWith ? 54 : 27;
  return { used: slots - Math.max(0, Math.min(slots, s.freeSlots)), slots };
}

/** `now` is for tests; the page reads the clock once, when the summary appears (it is replaced within moments). */
export function LedgerSummary({ storage, now }: { storage: StoryStorage; now?: number }) {
  const [opened] = useState(() => Date.now());
  const rows = ledgerRows(storage);
  const fill = ledgerSlots(storage);
  const counted = { source: 'story', seenAt: toMs(storage.knownAt), seenBy: null, exact: false } as const;
  return (
    <div className="w-[20rem] max-w-full space-y-1.5" data-testid="ledger-summary">
      <p className="flex items-baseline gap-2 text-[12px] text-muted" data-testid="ledger-freshness">
        <span>{freshnessText(counted, now ?? opened)} · counts only</span>
        {fill ? (
          <span className="ml-auto shrink-0 tabular-nums" data-testid="ledger-slots">
            {fill.used} / {fill.slots} slots
          </span>
        ) : null}
      </p>
      {rows.length ? (
        <ul
          className="grid max-h-[7.5rem] grid-cols-2 gap-x-3 gap-y-0.5 overflow-y-auto pr-1"
          aria-label="Ledger contents"
        >
          {rows.map((r) => (
            <li
              key={`${r.id}:${r.meta}`}
              className="flex min-w-0 items-center gap-1.5"
              data-item={`${r.id}:${r.meta}`}
              data-count={r.count}
            >
              <ItemIcon id={r.id} meta={r.meta} size={16} />
              <span className="min-w-0 flex-1 truncate">{r.label}</span>
              <span className="text-muted tabular-nums">{r.count}</span>
            </li>
          ))}
        </ul>
      ) : (
        <p className="py-1 text-muted">Empty when last counted.</p>
      )}
    </div>
  );
}
