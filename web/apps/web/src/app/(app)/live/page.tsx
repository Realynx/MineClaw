'use client';

import { AlertTriangle, Bell, LayoutGrid, Network } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useMemo } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Card, CardHeader, EmptyState, ErrorState, PageHeader, Skeleton } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { HiveGrid } from '@/features/live/HiveGrid';
import { useHiveIndex } from '@/features/live/common';
import { HiveTreeView } from '@/features/live/HiveTreeView';
import { PreviewFpsSlider } from '@/features/live/PreviewFpsSlider';
import { WorldHost } from '@/features/world/integration';
import { useEvents, useStatus } from '@/lib/live/hooks';
import { ago, cn } from '@/lib/utils';

type View = 'grid' | 'tree';

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
                <AlertTriangle
                  className={cn('mt-0.5 size-3.5 shrink-0', e.level === 'error' ? 'text-danger' : 'text-warn')}
                />
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

type Status = ReturnType<typeof useStatus>['data'];
type Bot = NonNullable<Status>['bots'][number];

/** The bodies with the main one first, then by name. */
function mainFirst(data: Status): Bot[] {
  const main = data?.main;
  return [...(data?.bots ?? [])].sort((a, b) =>
    a.name === main ? -1 : b.name === main ? 1 : a.name.localeCompare(b.name),
  );
}

/** The bot cards (skeletons while the hive answers; a note when no body is online). */
function BotsGrid({
  pending,
  bots,
  main,
  tree,
}: {
  pending: boolean;
  bots: Bot[];
  main: string;
  tree: ReturnType<typeof useHiveIndex>;
}) {
  return (
    <WorldHost>
      {pending ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {Array.from({ length: 4 }, (_, i) => (
            <Skeleton key={i} className="h-[23rem] rounded-card" />
          ))}
        </div>
      ) : (
        <HiveGrid bots={bots} main={main} tree={tree} />
      )}
      {!pending && bots.length === 0 ? (
        <Card>
          <EmptyState title="No bots online" hint="Bodies appear here as soon as they connect." />
        </Card>
      ) : null}
    </WorldHost>
  );
}

function LiveInner() {
  const params = useSearchParams();
  const router = useRouter();
  const view: View = params.get('view') === 'tree' ? 'tree' : 'grid';
  const setView = (v: View) => router.replace(v === 'grid' ? '/live' : `/live?view=${v}`, { scroll: false });
  const status = useStatus();
  const hive = useHiveIndex();
  const bots = useMemo(() => mainFirst(status.data), [status.data]);
  const online = bots.filter((b) => b.status === 'online').length;

  return (
    <PageBody wide>
      <PageHeader
        title="Live hive"
        subtitle={
          status.data
            ? `${online} of ${bots.length} bodies online · main is ${status.data.main}`
            : 'Connecting to the hive…'
        }
        actions={
          <div className="flex flex-wrap items-center gap-4">
            <PreviewFpsSlider />
            <Segmented<View>
              label="View"
              value={view}
              onChange={setView}
              options={[
                {
                  value: 'grid',
                  label: (
                    <>
                      <LayoutGrid className="size-3.5" /> Bots
                    </>
                  ),
                },
                {
                  value: 'tree',
                  label: (
                    <>
                      <Network className="size-3.5" /> Hive tree
                    </>
                  ),
                },
              ]}
            />
          </div>
        }
      />
      {status.error ? (
        <Card>
          <ErrorState error={status.error} onRetry={() => void status.refetch()} title="Could not reach the hive" />
        </Card>
      ) : view === 'grid' ? (
        <BotsGrid pending={status.isPending} bots={bots} main={status.data?.main ?? ''} tree={hive} />
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
          <HiveTreeView />
          <EventsCard />
        </div>
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
