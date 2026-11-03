'use client';

import type { ShardImportMode, ShardSelection } from '@mineclaw/contracts';
import { AlertTriangle, CheckCircle2, History, Package, PackageOpen, Upload } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useState } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Button, Card, CardHeader, EmptyState, ErrorState, PageHeader, SkeletonLines } from '@/components/ui/primitives';
import { ExportDialog, type ExportPreset } from '@/features/shards/ExportDialog';
import { ImportPanel } from '@/features/shards/ImportPanel';
import { useApi } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { ago, bytes, num } from '@/lib/utils';

function ShardHistory() {
  const audit = useApi('admin.audit', { query: { max: 300 } });
  const rows = (audit.data?.entries ?? []).filter((e) => e.kind === 'shard' || e.op === 'import' || e.op === 'export').slice(0, 200);
  return (
    <Card>
      <CardHeader title="History" subtitle="Imports and exports on the bot" icon={<History className="size-4" />} />
      {audit.isPending ? (
        <SkeletonLines lines={4} className="p-4" />
      ) : audit.error ? (
        <ErrorState error={audit.error} />
      ) : rows.length === 0 ? (
        <p className="px-4 py-6 text-center text-sm text-muted">No shard imports or exports yet.</p>
      ) : (
        <ul className="divide-y divide-border text-sm">
          {rows.map((e, i) => (
            <li key={i} className="flex items-center gap-3 px-4 py-2.5">
              <Badge tone={e.op === 'import' ? 'info' : 'neutral'}>{e.op}</Badge>
              <span className="min-w-0 flex-1 truncate text-fg">{e.message}</span>
              <span className="text-xs text-subtle">
                {e.who} · {ago(e.at)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

function ShardsInner() {
  const params = useSearchParams();
  const router = useRouter();
  const { isAdmin } = useAuth();
  const packs = useApi('shards.packs');
  const exportMode = params.get('export') as ShardSelection['mode'] | null;
  const preset: ExportPreset = {
    mode: exportMode ?? undefined,
    node: params.get('node') ? Number(params.get('node')) : undefined,
    kinds: params.get('kind') ? [params.get('kind')!] : undefined,
    query: params.get('q') ?? undefined,
    people: params.get('people') ? params.get('people')!.split(',') : undefined,
  };
  const [exportOpen, setExportOpen] = useState(!!exportMode);
  const [importMode, setImportMode] = useState<ShardImportMode | undefined>(undefined);
  const closeExport = (o: boolean) => {
    setExportOpen(o);
    if (!o && exportMode) router.replace('/shards', { scroll: false });
  };

  return (
    <PageBody>
      <PageHeader
        title="Shards"
        subtitle={packs.data?.activePack ? `This db runs ${packs.data.activePack}` : 'Knowledge packs and memory exports'}
        actions={
          <Button variant="primary" onClick={() => setExportOpen(true)}>
            <PackageOpen className="size-4" /> Export…
          </Button>
        }
      />
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <div className="space-y-4">
          <Card>
            <CardHeader title="Default packs" subtitle="Version packs (game data) and mode packs (play styles)" icon={<Package className="size-4" />} />
            {packs.isPending ? (
              <SkeletonLines lines={6} className="p-4" />
            ) : packs.error ? (
              <ErrorState error={packs.error} onRetry={() => void packs.refetch()} />
            ) : packs.data.packs.length === 0 ? (
              <EmptyState title="No packs found" />
            ) : (
              <ul className="divide-y divide-border">
                {packs.data.packs.map((p) => (
                  <li key={p.file} className="px-4 py-3.5">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-[13px] font-semibold text-fg">{p.kindLabel}</span>
                      {p.active ? (
                        <Badge tone="ok">
                          <CheckCircle2 className="size-3" /> active
                        </Badge>
                      ) : null}
                      {p.error ? (
                        <Badge tone="danger">
                          <AlertTriangle className="size-3" /> problem
                        </Badge>
                      ) : null}
                      {isAdmin && !p.error ? (
                        <Button
                          size="sm"
                          variant="ghost"
                          className="ml-auto"
                          onClick={() => setImportMode(p.kind === 'version' ? 'swap-pack' : p.active ? 'remove-mode' : 'add-mode')}
                        >
                          <Upload className="size-3.5" /> {p.kind === 'version' ? 'Preview swap' : p.active ? 'Preview remove' : 'Preview add'}
                        </Button>
                      ) : null}
                    </div>
                    {p.description ? <p className="mt-0.5 text-xs text-muted">{p.description}</p> : null}
                    <p className="mt-1 text-[11px] text-subtle">
                      {num(p.records)} records · {bytes(p.bytes)} · {p.vectors ? `${num(p.vectors)} vectors ${p.embeddingModel}@${p.dims}` : 'no vectors'}
                      {p.needsEmbedding ? ` · ${num(p.needsEmbedding)} to embed` : ''}
                      {p.builder ? ` · ${p.builder}` : ''}
                    </p>
                    {p.error ? <p className="mt-1 text-xs text-danger">{p.error}</p> : null}
                  </li>
                ))}
                {packs.data.extra.map((f) => (
                  <li key={f.file} className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <span className="text-[13px] font-medium text-fg">{f.file}</span>
                      <Badge>{f.kindLabel}</Badge>
                    </div>
                    <p className="text-[11px] text-subtle">
                      {bytes(f.bytes)}
                      {f.error ? ` · ${f.error}` : ''}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </Card>
          {isAdmin ? <ShardHistory /> : null}
        </div>
        {isAdmin ? (
          <ImportPanel key={importMode ?? 'merge'} initialMode={importMode} />
        ) : (
          <Card>
            <EmptyState title="Importing needs the admin role" hint="You can still export shards and read the packs." />
          </Card>
        )}
      </div>
      {exportOpen ? <ExportDialog open onOpenChange={closeExport} preset={preset} /> : null}
    </PageBody>
  );
}

export default function ShardsPage() {
  return (
    <Suspense fallback={null}>
      <ShardsInner />
    </Suspense>
  );
}
