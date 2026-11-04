'use client';

import { toNumber, type CountCard } from '@mineclaw/contracts';
import { Database, Network, Package, Sparkles } from 'lucide-react';
import Link from 'next/link';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Card, CardHeader, ErrorState, KeyValues, Meter, PageHeader, Skeleton } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { ago, bytes, compact, dateTime, fixed, num } from '@/lib/utils';

function CountTile({ c }: { c: CountCard }) {
  const body = (
    <>
      <div className="text-xs font-medium text-muted">{c.label}</div>
      <div className="tabular mt-1 text-2xl font-semibold tracking-tight text-fg">{compact(c.count)}</div>
      {c.detail ? <div className="mt-1 truncate text-xs text-subtle">{c.detail}</div> : <div className="mt-1 h-4" />}
    </>
  );
  const cls = 'block rounded-card border border-border bg-surface p-4 shadow-card';
  return c.memoryKind ? (
    <Link href={`/memory?kind=${c.memoryKind}`} className={`${cls} transition-colors hover:border-border-strong focus-visible:outline-2`}>
      {body}
    </Link>
  ) : (
    <div className={cls}>{body}</div>
  );
}

export default function OverviewPage() {
  const q = useApi('overview');
  const o = q.data;

  return (
    <PageBody>
      <PageHeader
        title="Overview"
        subtitle={o ? `${o.db.file} · ${bytes(o.db.sizeBytes)} · read ${ago(o.db.readAt)}` : 'What the bot remembers'}
        actions={<PackBadge pack={o?.knowledgePack} />}
      />
      {q.error ? (
        <Card>
          <ErrorState error={q.error} onRetry={() => void q.refetch()} />
        </Card>
      ) : (
        <div className="space-y-6">{o ? <OverviewBody o={o} /> : <OverviewSkeleton />}</div>
      )}
    </PageBody>
  );
}

type Overview = NonNullable<ReturnType<typeof useApi<'overview'>>['data']>;

function PackBadge({ pack }: { pack: Overview['knowledgePack'] | undefined }) {
  if (!pack) return null;
  return (
    <Badge tone="accent" title={pack.detail ?? undefined}>
      <Package className="size-3" /> knowledge {pack.label}
    </Badge>
  );
}

function OverviewSkeleton() {
  return Array.from({ length: 3 }, (_, i) => (
    <div key={i} className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
      {Array.from({ length: 5 }, (_, j) => (
        <Skeleton key={j} className="h-[6.25rem] rounded-card" />
      ))}
    </div>
  ));
}

/** The count tiles by group, the database and RAPTOR cards, and a warning when the stored vectors do not match. */
function OverviewBody({ o }: { o: Overview }) {
  const groups = [...new Set(o.cards.map((c) => c.group))];
  return (
    <>
      <div className="grid grid-cols-1 gap-x-6 gap-y-6 xl:grid-cols-2">
        {groups.map((g) => (
          <section key={g}>
            <h2 className="mb-2.5 text-xs font-semibold tracking-wider text-subtle uppercase">{g}</h2>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              {o.cards
                .filter((c) => c.group === g)
                .map((c) => (
                  <CountTile key={c.key} c={c} />
                ))}
            </div>
          </section>
        ))}
      </div>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader title="Database" icon={<Database className="size-4" />} />
          <div className="p-5">
            <KeyValues
              rows={[
                ['File', `${o.db.file} · ${bytes(o.db.sizeBytes)}`],
                ['Schema', `v${o.db.schemaVersion} · FTS5 ${o.db.fts5 ? 'yes' : 'no'} · R*Tree ${o.db.rtree ? 'yes' : 'no'}`],
                ['Vectors', o.db.vectors.map((v) => `${num(v.count)} × ${v.model} @ ${v.dims}`).join(', ') || 'none'],
                ['Search', <span key="s">{o.db.searchNote}</span>],
                ['Game version', o.db.serverVersion],
                ['Embeddings', o.embeddings.status],
              ]}
            />
          </div>
        </Card>
        <Card>
          <CardHeader title="RAPTOR tree" subtitle={o.raptor?.headline ?? o.raptorNote ?? undefined} icon={<Network className="size-4" />} />
          {o.raptor ? <RaptorSummary raptor={o.raptor} /> : <p className="p-5 text-sm text-muted">{o.raptorNote ?? 'No RAPTOR tree stored in this db.'}</p>}
        </Card>
      </div>
      {!o.embeddings.matchesStored ? (
        <p className="flex items-center gap-2 text-xs text-warn">
          <Sparkles className="size-3.5" /> Stored vectors use a different model than the configured one, so search runs lexical only.
        </p>
      ) : null}
    </>
  );
}

/** The tree's layers (nodes, average tokens, dirty, share) and its build facts (more with developer details). */
function RaptorSummary({ raptor }: { raptor: NonNullable<Overview['raptor']> }) {
  const { devDetails } = usePrefs();
  return (
      <div className="space-y-4 p-5">
        <table className="w-full text-sm">
          <thead className="text-left text-xs text-subtle">
            <tr>
              <th className="pb-2 font-medium">Layer</th>
              <th className="pb-2 text-right font-medium">Nodes</th>
              <th className="pb-2 text-right font-medium">Avg tok</th>
              <th className="pb-2 text-right font-medium">Dirty</th>
              <th className="w-28 pb-2 pl-4 font-medium">Share</th>
            </tr>
          </thead>
          <tbody className="tabular">
            {raptor.layers.map((l) => (
              <tr key={l.layer} className="border-t border-border">
                <td className="py-1.5 text-fg">{l.name}</td>
                <td className="py-1.5 text-right text-muted">{num(l.nodes)}</td>
                <td className="py-1.5 text-right text-muted">{fixed(l.avgTokens, 0)}</td>
                <td className={l.dirty ? 'py-1.5 text-right text-warn' : 'py-1.5 text-right text-muted'}>{l.dirty}</td>
                <td className="py-1.5 pl-4">
                  <Meter value={toNumber(l.share)} max={1} label={`${l.name} share`} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <KeyValues
          className="text-xs"
          rows={[
            ['Built', `${dateTime(raptor.meta.builtAt)} (${ago(raptor.meta.builtAt)})`],
            ['Since build', `+${raptor.meta.insertedSinceBuild} inserted · −${raptor.meta.removedSinceBuild} removed`],
            ['Pending', `${raptor.dirtySummaries} dirty · ${raptor.pendingSummaries} pending summaries`],
            ...(devDetails
              ? ([
                  ['Embedding', `${raptor.meta.embeddingModel} @ ${raptor.meta.dimensions}`],
                  ['Leaf→parent cos', `${fixed(raptor.meta.baselineLeafParentSimilarity, 3)} ± ${fixed(raptor.meta.baselineLeafParentSimilarityStd, 3)} at build · ${fixed(raptor.insertedCosMean, 3)} inserted`],
                  ['Insert τ', fixed(raptor.meta.insertTemperature, 2)],
                  ['Build', `#${String(raptor.meta.buildId)} · ${raptor.storedLayers} stored layers · ${num(raptor.meta.leavesAtBuild)} leaves at build`],
                ] as Array<[string, string]>)
              : []),
          ]}
        />
      </div>
  );
}
