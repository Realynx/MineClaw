'use client';

import type { StoryStorage } from '@mineclaw/contracts';
import { useMemo, useState } from 'react';
import { Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { storageInventory } from './inventory';
import { panel } from './StoryHud';
import { ItemIcon } from './visuals';

export function InventoryPanel({ storages = [] }: { storages?: readonly StoryStorage[] | null }) {
  const [query, setQuery] = useState('');
  const [sort, setSort] = useState('name');
  const inventory = useMemo(() => storageInventory(storages ?? []), [storages]);
  const rows = inventory.items.filter((x) =>
    `${x.label} ${x.id}:${x.meta}`.toLowerCase().includes(query.trim().toLowerCase()),
  );
  if (sort === 'count') rows.sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
  return (
    <section
      className={cn(panel, 'pointer-events-auto flex max-h-[55vh] w-[27rem] max-w-full flex-col p-3')}
      aria-label="Colony inventory"
    >
      <h2 className="text-sm font-semibold">Inventory</h2>
      <p className="my-1 text-[11px] text-muted">
        {inventory.items.reduce((n, x) => n + x.count, 0).toLocaleString()} items · {inventory.items.length} types ·{' '}
        {inventory.chests} chests
      </p>
      <p className="mb-2 text-[10px] text-subtle">
        Last known contents of all colony storage. Double chests count once.
        {inventory.unknown ? ` ${inventory.unknown} not yet inspected.` : ''}
      </p>
      <div className="mb-2 flex gap-2">
        <Input
          aria-label="Search inventory"
          placeholder="Search items…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <select
          aria-label="Sort inventory"
          className="rounded border border-border bg-surface-2 text-xs"
          value={sort}
          onChange={(e) => setSort(e.target.value)}
        >
          <option value="name">Name</option>
          <option value="count">Count</option>
        </select>
      </div>
      <div className="min-h-0 overflow-auto">
        <table className="w-full text-xs">
          <thead>
            <tr className="text-left text-subtle">
              <th>Item</th>
              <th className="text-right">Count</th>
              <th className="text-right">Chests</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((x) => (
              <tr key={`${x.id}:${x.meta}`} className="border-t border-border">
                <td className="py-1.5">
                  <span className="flex items-center gap-2">
                    <ItemIcon id={x.id} meta={x.meta} size={20} />
                    {x.label}
                  </span>
                </td>
                <td className="text-right tabular-nums">{x.count.toLocaleString()}</td>
                <td className="text-right text-muted tabular-nums">{x.chests}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {!rows.length ? (
          <p className="py-4 text-xs text-muted">
            {query ? 'No matching items.' : 'No stored items have been observed yet.'}
          </p>
        ) : null}
      </div>
    </section>
  );
}
