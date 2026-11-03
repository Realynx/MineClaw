'use client';

/** The node's settings audit: who changed which keys when (never values), tests and restarts. */
import { FileClock } from 'lucide-react';
import { Badge, Card, CardHeader, EmptyState, ErrorState, SkeletonLines, type Tone } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { ago, dateTime } from '@/lib/utils';

const ACTION: Record<string, { text: string; tone: Tone }> = {
  update: { text: 'changed', tone: 'accent' },
  restart: { text: 'restart', tone: 'warn' },
  restart_refused: { text: 'restart refused', tone: 'danger' },
  test: { text: 'test', tone: 'info' },
};

export function AuditCard({ node, labels }: { node: string; labels: Record<string, string> }) {
  const q = useApi('admin.settingsAudit', { params: { node }, query: { max: 50 } });
  return (
    <Card id="audit">
      <CardHeader
        title="Audit"
        icon={<FileClock className="size-4" />}
        subtitle="Who changed what on this node (keys only; values are never recorded)"
      />
      {q.isPending ? (
        <div className="p-4">
          <SkeletonLines lines={4} />
        </div>
      ) : q.error ? (
        <ErrorState error={q.error} onRetry={() => void q.refetch()} />
      ) : q.data.entries.length === 0 ? (
        <EmptyState title="No changes yet" />
      ) : (
        <ul className="divide-y divide-border" aria-label="Settings audit">
          {q.data.entries.map((e, i) => {
            const a = ACTION[e.action] ?? { text: e.action, tone: 'neutral' as Tone };
            return (
              <li key={`${e.at}-${i}`} className="space-y-1 px-4 py-2.5 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <Badge tone={e.ok ? a.tone : 'danger'}>{e.ok ? a.text : `${a.text} · failed`}</Badge>
                  <span className="font-medium text-fg">{e.who}</span>
                  <span className="text-xs text-muted" title={dateTime(e.at)}>
                    {ago(e.at)}
                  </span>
                </div>
                {e.keys.length ? (
                  <p className="text-xs break-words text-fg">{e.keys.map((k) => labels[k] ?? k).join(', ')}</p>
                ) : null}
                {e.reason ? <p className="text-xs text-muted">“{e.reason}”</p> : null}
                {e.note ? <p className="text-xs text-subtle">{e.note}</p> : null}
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}
