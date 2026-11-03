'use client';

/**
 * RAPTOR rebuild flow (explorer-spec §11): estimate → confirm the cost with a hard cap (max(0.05, ceil(usd·1.5 cents))) →
 * backup → start → follow the job (poll 1.5 s while open) → summary diffs. Cancel while running.
 */
import { useQueryClient } from '@tanstack/react-query';
import { rebuildCap, type RebuildRequest } from '@mineclaw/contracts';
import { CheckCircle2, XCircle } from 'lucide-react';
import { useEffect, useState } from 'react';
import { api, errorText } from '@/lib/api/client';
import { useApi, useApiMutation } from '@/lib/api/query';
import { compact, usd } from '@/lib/utils';
import { Badge, Button, Checkbox, InlineError, KeyValues, Meter, SkeletonLines } from '@/components/ui/primitives';
import { Dialog } from '@/components/ui/overlays';

export function JobProgress({ jobId, onDone }: { jobId: string; onDone?: () => void }) {
  const job = useApi('admin.rebuildJob', { params: { jobId } }, { refetchInterval: (q) => (q.state.data && q.state.data.state !== 'running' ? false : 1500) });
  const cancel = useApiMutation('admin.rebuildCancel');
  const j = job.data;
  useEffect(() => {
    if (j && j.state !== 'running') onDone?.();
  }, [j, onDone]);
  if (!j) return <SkeletonLines lines={2} />;
  return (
    <div className="space-y-3">
      {j.state === 'running' && j.progress ? (
        <div>
          <div className="mb-1 flex justify-between text-xs">
            <span className="text-muted">
              {j.progress.stage} · {j.progress.done}/{j.progress.total}
              {j.progress.message ? ` · ${j.progress.message}` : ''}
            </span>
            <Button size="sm" variant="ghost" loading={cancel.isPending} onClick={() => cancel.mutate({ params: { jobId }, body: {} })}>
              Cancel
            </Button>
          </div>
          <Meter value={j.progress.done} max={Math.max(1, j.progress.total)} label="rebuild progress" />
        </div>
      ) : null}
      {j.state !== 'running' ? (
        <div className="flex items-center gap-2 text-sm">
          {j.state === 'done' && j.result?.ok ? <CheckCircle2 className="size-4 text-ok" /> : <XCircle className="size-4 text-danger" />}
          <span className="text-fg">
            {j.state === 'cancelled' ? 'Cancelled' : j.state === 'failed' ? `Failed: ${j.error ?? j.result?.message ?? ''}` : (j.result?.message ?? 'Done')}
          </span>
          {j.result ? <span className="text-xs text-muted">· {usd(j.result.spentUsd)} · {String(j.result.elapsed).replace(/\.\d+$/, '')}</span> : null}
        </div>
      ) : null}
      {j.result?.diffs.length ? (
        <div className="max-h-72 space-y-2 overflow-auto">
          {j.result.diffs.map((d) => (
            <div key={d.index} className="rounded-lg border border-border p-3 text-xs">
              <div className="mb-1.5 font-medium text-muted">
                L{d.layer} #{d.index} {d.old === null || d.old === undefined ? <Badge tone="ok">new</Badge> : null}
              </div>
              {d.old ? <p className="mb-1 text-danger line-through decoration-danger/50">{d.old}</p> : null}
              {d.new ? <p className="text-ok">{d.new}</p> : null}
            </div>
          ))}
        </div>
      ) : null}
    </div>
  );
}

export function RebuildDialog({
  open,
  onOpenChange,
  request,
  label,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  request: Pick<RebuildRequest, 'scope' | 'node' | 'kind'>;
  label: string;
}) {
  const qc = useQueryClient();
  const estimate = useApiMutation('admin.rebuildEstimate');
  const [backup, setBackup] = useState(true);
  const [jobId, setJobId] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { mutate } = estimate;

  const reqKey = JSON.stringify(request);
  useEffect(() => {
    if (open) mutate({ body: { ...(JSON.parse(reqKey) as typeof request), maxUsd: 0.5 } });
  }, [open, mutate, reqKey]);

  const e = estimate.data;
  const cap = e ? rebuildCap(e.usd) : 0;
  const start = async () => {
    setStarting(true);
    setError(null);
    try {
      if (backup) await api('admin.backup', { body: {} });
      const r = await api('admin.rebuildStart', { body: { ...request, maxUsd: cap } });
      setJobId(r.jobId);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setStarting(false);
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={`Rebuild ${label}`}
      description="Re-cluster and re-summarise this part of the RAPTOR tree on the bot (cheap model)."
      footer={
        jobId ? (
          <Button onClick={() => onOpenChange(false)}>Close</Button>
        ) : (
          <>
            <Button variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button variant="primary" onClick={() => void start()} loading={starting} disabled={!e || e.summaries === 0}>
              Rebuild (cap {usd(cap)})
            </Button>
          </>
        )
      }
    >
      {jobId ? (
        <JobProgress jobId={jobId} onDone={() => void qc.invalidateQueries({ predicate: (q) => String(q.queryKey[0]).startsWith('raptor') || q.queryKey[0] === 'overview' })} />
      ) : estimate.isPending ? (
        <SkeletonLines lines={3} />
      ) : estimate.error ? (
        <InlineError error={estimate.error} />
      ) : e ? (
        e.summaries === 0 ? (
          <p className="text-sm text-muted">Nothing to rebuild: {e.note}</p>
        ) : (
          <div className="space-y-4">
            <KeyValues
              rows={[
                ['Summaries', e.summaries],
                ['Tokens', `${compact(e.inputTokens)} in · ${compact(e.outputTokens)} out · ${compact(e.embeddingTokens)} embedding`],
                ['Model', e.model],
                ['Estimate', usd(e.usd)],
                ['Hard cap', <span key="cap" className="font-medium text-fg">{usd(cap)}</span>],
              ]}
            />
            <p className="text-xs text-muted">{e.note}</p>
            <Checkbox checked={backup} onChange={setBackup} label="Take a backup on the bot first" />
            {error ? <p className="text-xs text-danger">{error}</p> : null}
          </div>
        )
      ) : null}
    </Dialog>
  );
}
