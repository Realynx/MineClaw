'use client';

import { AlertTriangle, Bell, Box, LayoutGrid, Network } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useCallback, useMemo, useState } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Card, CardHeader, EmptyState, ErrorState, PageHeader, Select, Skeleton } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { HiveGrid } from '@/features/live/HiveGrid';
import { useHiveIndex } from '@/features/live/common';
import { HiveTreeView } from '@/features/live/HiveTreeView';
import { BodyPanel } from '@/features/live/inventory/BodyPanel';
import { WorldHost } from '@/features/world/integration';
import { WorldPane } from '@/features/world/WorldPane';
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

type Status = ReturnType<typeof useStatus>['data'];
type Bot = NonNullable<Status>['bots'][number];

/** The bodies with the main one first, then by name. */
function mainFirst(data: Status): Bot[] {
  const main = data?.main;
  return [...(data?.bots ?? [])].sort((a, b) => (a.name === main ? -1 : b.name === main ? 1 : a.name.localeCompare(b.name)));
}

/** The bot cards (skeletons while the hive answers; a note when no body is online). */
function BotsGrid({ pending, bots, main, tree }: { pending: boolean; bots: Bot[]; main: string; tree: ReturnType<typeof useHiveIndex> }) {
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

/** The 3D world following a body (the first by default); a body clicked in it opens its panel. */
function WorldView(p: {
  bots: Bot[];
  follow: string;
  setFollow: (name: string) => void;
  panel: string | null;
  setPanel: (name: string | null) => void;
}) {
  const { bots, panel, setPanel, setFollow } = p;
  const followed = p.follow || bots[0]?.name || '';
  const closePanel = useCallback(() => setPanel(null), [setPanel]);
  return (
    <Card className="h-[min(74vh,52rem)] overflow-hidden">
      <WorldHost>
        {followed ? (
          <WorldPane
            follow={followed}
            onBodyClick={setPanel}
            overlay={panel ? <BodyPanel bot={panel} onClose={closePanel} className="absolute right-3 bottom-3 z-10 max-h-[calc(100%-4.5rem)]" /> : null}
            toolbarStart={
              <Select aria-label="Follow bot" value={followed} onChange={(e) => setFollow(e.target.value)} className="h-8 min-w-40">
                {bots.map((b) => (
                  <option key={b.name} value={b.name}>
                    {b.name}
                  </option>
                ))}
              </Select>
            }
          />
        ) : null}
      </WorldHost>
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
  const bots = useMemo(() => mainFirst(status.data), [status.data]);
  const [follow, setFollow] = useState<string>('');
  // The body clicked in the world view: its panel (inventory polled only while it is open).
  const [panel, setPanel] = useState<string | null>(null);
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
        <BotsGrid pending={status.isPending} bots={bots} main={status.data?.main ?? ''} tree={hive} />
      ) : view === 'tree' ? (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
          <HiveTreeView />
          <EventsCard />
        </div>
      ) : (
        <WorldView bots={bots} follow={follow} setFollow={setFollow} panel={panel} setPanel={setPanel} />
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
