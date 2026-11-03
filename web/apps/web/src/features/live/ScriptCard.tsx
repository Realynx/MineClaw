'use client';

import type { BotNow } from '@mineclaw/contracts';
import { Bug, Code2, ExternalLink } from 'lucide-react';
import Link from 'next/link';
import { useMemo, useState } from 'react';
import { useApi } from '@/lib/api/query';
import { Badge, Button, Card, CardHeader, ErrorState, Skeleton } from '@/components/ui/primitives';
import { Switch } from '@/components/ui/overlays';
import { CodeView } from '@/components/code/CodeView';

/** The current script with its running line highlighted (line from the `now` stream; source fetched once per version). */
export function ScriptCard({ script, onDebug }: { script: NonNullable<BotNow['script']>; onDebug: () => void }) {
  const [follow, setFollow] = useState(true);
  const detail = useApi('scripts.get', {
    params: { name: script.name },
    query: { source: 'live', ...(script.version ? { v: script.version } : {}) },
  });
  const lint = useMemo(
    () => (detail.data?.lint.findings ?? []).map((f) => ({ line: f.line, severity: f.severity, message: `${f.rule}: ${f.message}` })),
    [detail.data],
  );
  const statusTone = script.paused ? 'warn' : script.status === 'failed' ? 'danger' : script.status === 'running' ? 'info' : 'neutral';
  return (
    <Card className="flex min-h-0 flex-col">
      <CardHeader
        icon={<Code2 className="size-4" />}
        title={
          <span className="flex items-center gap-2">
            {script.name}
            {script.version ? <span className="font-normal text-subtle">v{script.version}</span> : null}
            <Badge tone={statusTone}>{script.paused ? 'paused' : script.status}</Badge>
          </span>
        }
        subtitle={script.line ? `Running line ${script.line}` : undefined}
        actions={
          <>
            <Switch checked={follow} onCheckedChange={setFollow} label={<span className="text-xs text-muted">Follow</span>} />
            <Button size="sm" variant="ghost" onClick={onDebug}>
              <Bug className="size-3.5" /> Debugger
            </Button>
            <Link href={`/scripts?name=${encodeURIComponent(script.name)}`} aria-label="Open in the script library" className="inline-flex size-7 items-center justify-center rounded-md text-muted hover:bg-surface-2 hover:text-fg">
              <ExternalLink className="size-3.5" />
            </Link>
          </>
        }
      />
      <div className="h-80 min-h-0 overflow-hidden rounded-b-card">
        {detail.isPending ? (
          <div className="space-y-2 p-4">
            {Array.from({ length: 9 }, (_, i) => (
              <Skeleton key={i} className="h-3.5" />
            ))}
          </div>
        ) : detail.error ? (
          <ErrorState error={detail.error} onRetry={() => void detail.refetch()} title="Could not load the source" />
        ) : (
          <CodeView code={detail.data.code} lang={detail.data.version.lang} runningLine={script.line ?? null} follow={follow} lint={lint} className="h-full" />
        )}
      </div>
    </Card>
  );
}
