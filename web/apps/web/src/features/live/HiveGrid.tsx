'use client';

import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { ChevronDown } from 'lucide-react';
import { useMemo, useState } from 'react';
import { cn } from '@/lib/utils';
import { BotCard } from './BotCard';
import { KIND_LABEL } from './common';
import { groupBodies, type BodyGroup } from './groups';

const GRID = 'grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4';
const STORE = 'mcx-hive-collapsed';

function loadCollapsed(): Set<string> {
  try {
    return new Set(JSON.parse(sessionStorage.getItem(STORE) ?? '[]') as string[]);
  } catch {
    return new Set();
  }
}

function GroupHeader({ g, open, onToggle, tree }: { g: BodyGroup; open: boolean; onToggle: () => void; tree: ReadonlyMap<string, HiveTreeNode> }) {
  const total = (g.head ? 1 : 0) + g.bodies.length;
  const brain = tree.get(g.id)?.brain ?? (g.kind === 'main' ? 'opus' : null);
  return (
    <button
      onClick={onToggle}
      aria-expanded={open}
      className="flex w-full items-center gap-2.5 rounded-lg px-1 py-1.5 text-left hover:bg-surface-2"
      data-group={g.id}
    >
      <ChevronDown className={cn('size-4 shrink-0 text-subtle transition-transform', !open && '-rotate-90')} />
      <span className="text-[15px] font-semibold text-fg">{g.kind === 'main' ? `${g.id} · main hive` : g.id}</span>
      <span className="text-xs text-muted">
        {g.kind === 'main' ? 'main' : KIND_LABEL.hive}
        {brain ? ` · ${brain} brain` : ''}
      </span>
      <span className="ml-auto text-xs text-subtle tabular">
        {total} {total === 1 ? 'body' : 'bodies'} · {g.online} online
      </span>
    </button>
  );
}

/**
 * The hive grid: every body as a card, grouped by the hive that drives it (main and main's own bodies, then each hive and its
 * subhive bodies), each group collapsible. Collapsed groups unmount their thumbnails, so a large hive costs nothing folded.
 */
export function HiveGrid({ bots, main, tree }: { bots: readonly BotStatusView[]; main: string; tree: ReadonlyMap<string, HiveTreeNode> }) {
  const groups = useMemo(() => groupBodies(bots, main, tree), [bots, main, tree]);
  const [collapsed, setCollapsed] = useState<Set<string>>(loadCollapsed);
  const toggle = (id: string) =>
    setCollapsed((c) => {
      const n = new Set(c);
      if (n.has(id)) n.delete(id);
      else n.add(id);
      try {
        sessionStorage.setItem(STORE, JSON.stringify([...n]));
      } catch {
        // private mode: the choice lasts until reload
      }
      return n;
    });
  return (
    <div className="space-y-6">
      {groups.map((g) => {
        const open = !collapsed.has(g.id);
        return (
          <section key={g.id} aria-label={`${g.id} hive`} className="space-y-3">
            <GroupHeader g={g} open={open} onToggle={() => toggle(g.id)} tree={tree} />
            {open ? (
              <div className={GRID}>
                {g.head ? <BotCard bot={g.head} node={tree.get(g.head.name)} /> : null}
                {g.bodies.map((b) => (
                  <BotCard key={b.name} bot={b} node={tree.get(b.name)} compact />
                ))}
              </div>
            ) : null}
          </section>
        );
      })}
    </div>
  );
}
