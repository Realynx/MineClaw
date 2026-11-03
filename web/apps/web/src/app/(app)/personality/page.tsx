'use client';

import { toNumber } from '@mineclaw/contracts';
import { History, Layers, Smile } from 'lucide-react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useRef } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Card, CardHeader, ErrorState, PageHeader, Select, Skeleton } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { ago, dateTime } from '@/lib/utils';

const SECTION_LABEL: Record<string, string> = {
  trait: 'Traits',
  like: 'Likes',
  dislike: 'Dislikes',
  opinion: 'Opinions',
  joke: 'Jokes',
  nickname: 'Nicknames',
  relationship: 'Relationships',
  milestone: 'Milestones',
};

const OP_TONE = { add: 'ok', reinforce: 'info', adjust: 'warn', remove: 'danger' } as const;

function HistoryList({ items }: { items: Array<{ version: number; at: number; op: 'add' | 'reinforce' | 'adjust' | 'remove'; section: string; text: string; reason?: string | null; source?: string | null }> }) {
  const ref = useRef<HTMLDivElement>(null);
  const v = useVirtualizer({ count: items.length, getScrollElement: () => ref.current, estimateSize: () => 56, overscan: 8 });
  return (
    <div ref={ref} className="max-h-[32rem] overflow-auto">
      <div style={{ height: v.getTotalSize(), position: 'relative' }}>
        {v.getVirtualItems().map((it) => {
          const h = items[it.index]!;
          return (
            <div key={it.key} className="absolute inset-x-0 flex items-start gap-3 border-b border-border px-4 py-2.5" style={{ top: it.start, height: 56 }}>
              <Badge tone={OP_TONE[h.op]} className="mt-0.5 w-20 justify-center">
                {h.op}
              </Badge>
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] text-fg">{h.text}</p>
                <p className="truncate text-[11px] text-subtle">
                  {h.section} · v{h.version}
                  {h.reason ? ` · ${h.reason}` : ''}
                  {h.source ? ` · ${h.source}` : ''}
                </p>
              </div>
              <span className="shrink-0 text-[11px] text-subtle" title={dateTime(h.at)}>
                {ago(h.at)}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function PersonalityInner() {
  const params = useSearchParams();
  const router = useRouter();
  const { devDetails } = usePrefs();
  const scope = params.get('scope') ?? 'main';
  const q = useApi('personality', { query: { scope } });
  const p = q.data;

  return (
    <PageBody>
      <PageHeader
        title="Personality"
        subtitle={p?.summary ?? 'Who the bot thinks it is'}
        actions={
          p && p.scopes.length > 1 ? (
            <Select aria-label="Scope" value={scope} onChange={(e) => router.replace(`/personality?scope=${encodeURIComponent(e.target.value)}`)} className="h-8">
              {p.scopes.map((s) => (
                <option key={s} value={s}>
                  {s === 'main' ? 'main (the hive)' : s}
                </option>
              ))}
            </Select>
          ) : null
        }
      />
      {q.error ? (
        <Card>
          <ErrorState error={q.error} onRetry={() => void q.refetch()} />
        </Card>
      ) : !p ? (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 6 }, (_, i) => (
            <Skeleton key={i} className="h-48 rounded-card" />
          ))}
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_22rem]">
          <div className="space-y-4">
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              {p.groups
                .filter((g) => g.entries.length)
                .map((g) => (
                  <Card key={g.section}>
                    <CardHeader title={SECTION_LABEL[g.section] ?? g.section} subtitle={`${g.entries.length} entries`} />
                    <ul className="divide-y divide-border">
                      {g.entries.map((e) => (
                        <li key={e.id} className="px-4 py-3">
                          <div className="flex items-start gap-3">
                            <p className="min-w-0 flex-1 text-[13px] text-fg">
                              {e.text}
                              {e.player ? <span className="text-subtle"> · about {e.player}</span> : null}
                            </p>
                            <div className="mt-1.5 h-1.5 w-16 shrink-0 overflow-hidden rounded-full bg-surface-3" role="meter" aria-label="strength" aria-valuenow={toNumber(e.strength)} aria-valuemin={0} aria-valuemax={1}>
                              <div className="h-full rounded-full bg-accent" style={{ width: `${toNumber(e.strength) * 100}%` }} />
                            </div>
                          </div>
                          <p className="mt-0.5 text-[11px] text-subtle">
                            seen {e.observations}× · reinforced {ago(e.reinforced)}
                            {devDetails ? ` · #${e.id}` : ''}
                            {e.reason ? ` · ${e.reason}` : ''}
                          </p>
                        </li>
                      ))}
                    </ul>
                  </Card>
                ))}
            </div>
            <Card>
              <CardHeader title="History" subtitle={`${p.history.length} changes, newest first`} icon={<History className="size-4" />} />
              <HistoryList items={p.history} />
            </Card>
          </div>
          <div className="space-y-4">
            <Card>
              <CardHeader title="Mood" subtitle="Hours-scale average" icon={<Smile className="size-4" />} />
              <ul className="space-y-3 p-4">
                {p.mood.map((m) => {
                  const v = toNumber(m.value);
                  return (
                    <li key={m.dim}>
                      <div className="mb-1 flex justify-between text-xs">
                        <span className="text-fg capitalize">{m.dim}</span>
                        <span className="tabular text-muted">
                          {v >= 0 ? '+' : ''}
                          {v.toFixed(2)}
                        </span>
                      </div>
                      <div className="relative h-1.5 rounded-full bg-surface-3" role="meter" aria-label={m.dim} aria-valuenow={v} aria-valuemin={-1} aria-valuemax={1}>
                        <span className="absolute top-[-2px] left-1/2 h-2.5 w-px bg-border-strong" aria-hidden />
                        <div
                          className={v >= 0 ? 'absolute inset-y-0 left-1/2 rounded-r-full bg-ok' : 'absolute inset-y-0 rounded-l-full bg-warn'}
                          style={v >= 0 ? { width: `${v * 50}%` } : { right: '50%', width: `${-v * 50}%` }}
                        />
                      </div>
                    </li>
                  );
                })}
              </ul>
            </Card>
            <Card>
              <CardHeader title="How it sees itself" icon={<Layers className="size-4" />} subtitle="Self-tree abstractions" />
              <ul className="divide-y divide-border">
                {p.abstractions.map((a) => (
                  <li key={`${a.layer}-${a.idx}`} className="px-4 py-3">
                    <p className="text-[13px] text-fg">{a.text}</p>
                    <p className="mt-0.5 text-[11px] text-subtle">
                      layer {a.layer} · from {a.leaves} entries
                    </p>
                  </li>
                ))}
              </ul>
            </Card>
          </div>
        </div>
      )}
    </PageBody>
  );
}

export default function PersonalityPage() {
  return (
    <Suspense fallback={null}>
      <PersonalityInner />
    </Suspense>
  );
}
