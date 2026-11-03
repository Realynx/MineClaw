'use client';

import { AlertTriangle, Bell, Box, LayoutGrid, Network } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useMemo, useState } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Card, CardHeader, EmptyState, ErrorState, PageHeader, Select, Skeleton } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { BotCard } from '@/features/live/BotCard';
import { useHiveIndex } from '@/features/live/common';
import { HiveTreeView } from '@/features/live/HiveTreeView';
import { WorldHost, WorldPane } from '@/features/world/integration';
import { useEvents, useStatus } from '@/lib/live/hooks';
import { ago, cn } from '@/lib/utils';

type View = 'grid' | 'tree' | 'world';

function EventsCard() {
  const events = useEvents();
  const list = [...(events.data?.events ?? [])].reverse().slice(0, 12);
  return (
    <Card>
      <CardHeader title="Recent events" icon={<Bell className="size-4" />} />
      <ul className="divide-y divide-border">
        {events.isPending ? (
          <li className="p-4">
            <Skeleton className="h-4 w-2/3" />
          </li>
        ) : list.length === 0 ? (
          <li className="px-4 py-6 text-center text-sm text-muted">Nothing has happened yet.</li>
        ) : (
          list.map((e) => (
            <li key={e.id} className="flex items-start gap-3 px-4 py-2.5">
              {e.level === 'warn' || e.level === 'error' ? (
                <AlertTriangle className={cn('mt-0.5 size-3.5 shrink-0', e.level === 'error' ? 'text-danger' : 'text-warn')} />
              ) : (
                <span className="mt-1.5 size-1.5 shrink-0 rounded-full bg-subtle" />
              )}
              <div className="min-w-0 flex-1 text-[13px] text-fg">{e.text}</div>
              <span className="shrink-0 text-[11px] text-subtle">{ago(e.at)}</span>
            </li>
          ))
        )}
      </ul>
    </Card>
  );
}

function LiveInner() {
  const params = useSearchParams();
  const router = useRouter();
  const view = (params.get('view') as View | null) ?? 'grid';
  const setView = (v: View) => router.replace(v === 'grid' ? '/live' : `/live?view=${v}`, { scroll: false });
  const status = useStatus();
  const hive = useHiveIndex();
  const bots = useMemo(() => {
    const list = status.data?.bots ?? [];
    return [...list].sort((a, b) => (a.name === status.data?.main ? -1 : b.name === status.data?.main ? 1 : a.name.localeCompare(b.name)));
  }, [status.data]);
  const [follow, setFollow] = useState<string>('');
  const followed = follow || bots[0]?.name || '';
  const online = bots.filter((b) => b.status === 'online').length;

  return (
    <PageBody wide>
      <PageHeader
        title="Live hive"
        subtitle={status.data ? `${online} of ${bots.length} bodies online · main is ${status.data.main}` : 'Connecting to the hive…'}
        actions={
          <Segmented<View>
            label="View"
            value={view}
            onChange={setView}
            options={[
              { value: 'grid', label: <><LayoutGrid className="size-3.5" /> Bots</> },
              { value: 'tree', label: <><Network className="size-3.5" /> Hive tree</> },
              { value: 'world', label: <><Box className="size-3.5" /> World</> },
            ]}
          />
        }
      />
      {status.error ? (
        <Card>
          <ErrorState error={status.error} onRetry={() => void status.refetch()} title="Could not reach the hive" />
        </Card>
      ) : view === 'grid' ? (
        <WorldHost>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
            {status.isPending
              ? Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-[23rem] rounded-card" />)
              : bots.map((b) => <BotCard key={b.name} bot={b} node={hive.get(b.name)} />)}
          </div>
          {!status.isPending && bots.length === 0 ? (
            <Card>
              <EmptyState title="No bots online" hint="Bodies appear here as soon as they connect." />
            </Card>
          ) : null}
        </WorldHost>
      ) : view === 'tree' ? (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
          <HiveTreeView />
          <EventsCard />
        </div>
      ) : (
        <Card className="overflow-hidden">
          <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-2.5">
            <label htmlFor="follow" className="text-xs font-medium text-muted">
              Follow
            </label>
            <Select id="follow" value={followed} onChange={(e) => setFollow(e.target.value)} className="h-8 w-auto min-w-44">
              {bots.map((b) => (
                <option key={b.name} value={b.name}>
                  {b.name}
                </option>
              ))}
            </Select>
            <Badge>follow camera</Badge>
          </div>
          <div className="h-[min(70vh,48rem)]">
            <WorldHost>{followed ? <WorldPane follow={followed} /> : null}</WorldHost>
          </div>
        </Card>
      )}
    </PageBody>
  );
}

export default function LivePage() {
  return (
    <Suspense fallback={null}>
      <LiveInner />
    </Suspense>
  );
}
