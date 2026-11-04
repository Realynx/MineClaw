'use client';

/** "Restart required" notice and the restart dialog: the safety check first, then a graceful restart (forced only on purpose). */
import { useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, CheckCircle2, Power, RotateCw } from 'lucide-react';
import { useState } from 'react';
import { Dialog } from '@/components/ui/overlays';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  ErrorState,
  Field,
  InlineError,
  Input,
  SkeletonLines,
} from '@/components/ui/primitives';
import { api } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { plural } from '@/lib/utils';
import { reasonError } from './diff';
import { StepUpCancelled, type StepUp } from './useStepUp';

export function RestartNotice({
  node,
  keys,
  labels,
  stepUp,
  onRestarted,
}: {
  node: string;
  keys: readonly string[];
  labels: Record<string, string>;
  stepUp: StepUp;
  onRestarted: () => void;
}) {
  const [open, setOpen] = useState(false);
  const dialog = open ? (
    <RestartDialog node={node} onClose={() => setOpen(false)} stepUp={stepUp} onRestarted={onRestarted} />
  ) : null;
  // After a restart the list empties while the dialog still shows its result.
  if (keys.length === 0) return dialog;
  return (
    <Card className="mb-4 border-warn/50" role="region" aria-label="Restart required">
      <div className="flex flex-wrap items-start gap-3 p-4">
        <RotateCw className="mt-0.5 size-4 text-warn" aria-hidden />
        <div className="min-w-0 flex-1 space-y-1">
          <p className="text-sm font-medium text-fg">Restart required</p>
          <p className="text-xs text-muted">
            {plural(keys.length, 'saved setting')} {keys.length === 1 ? 'runs' : 'run'} only after the bot restarts:
          </p>
          <ul className="flex flex-wrap gap-1.5 pt-1">
            {keys.map((k) => (
              <li key={k}>
                <Badge tone="warn" title={k}>
                  {labels[k] ?? k}
                </Badge>
              </li>
            ))}
          </ul>
        </div>
        <Button variant="secondary" onClick={() => setOpen(true)}>
          <Power className="size-4" /> Restart bot…
        </Button>
      </div>
      {dialog}
    </Card>
  );
}

function RestartDialog({
  node,
  onClose,
  stepUp,
  onRestarted,
}: {
  node: string;
  onClose: () => void;
  stepUp: StepUp;
  onRestarted: () => void;
}) {
  const qc = useQueryClient();
  const safety = useApi('admin.restartSafety', { params: { node } }, { staleTime: 0, refetchOnMount: 'always' });
  const [reason, setReason] = useState('');
  const [force, setForce] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<string | null>(null);
  const s = safety.data;
  const unsafe = s ? !s.safe : false;
  const rErr = reasonError(reason);

  const restart = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await stepUp.run(() =>
        api('admin.restart', {
          params: { node },
          body: { reason: reason.trim(), ...(unsafe && force ? { force: true } : {}) },
        }),
      );
      setDone(r.message ?? 'Restart requested. The bot comes back by itself in a few seconds.');
      onRestarted();
      setTimeout(() => {
        void qc.invalidateQueries({ queryKey: ['admin.settings'] });
        void qc.invalidateQueries({ queryKey: ['admin.settingsAudit'] });
      }, 1500);
    } catch (e) {
      if (!(e instanceof StepUpCancelled)) setError(e);
      void safety.refetch();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title="Restart the bot"
      description="A graceful restart: bodies stop what they do, the bot logs out and systemd starts it again."
      footer={
        <RestartFooter
          done={!!done}
          unsafe={unsafe}
          busy={busy}
          disabled={!s || !!rErr || (unsafe && !force)}
          onClose={onClose}
          onRestart={() => void restart()}
        />
      }
    >
      {done ? (
        <p role="status" className="flex items-start gap-2 text-sm text-ok">
          <CheckCircle2 className="mt-0.5 size-4 shrink-0" /> {done}
        </p>
      ) : (
        <div className="space-y-4">
          <section aria-label="Safety check" className="space-y-2">
            <SafetyVerdict safety={safety} />
            {s?.bodies.length ? <BodiesTable bodies={s.bodies} /> : null}
          </section>
          <Field label="Reason" htmlFor="restart-reason" hint="Recorded in the node's audit.">
            <Input
              id="restart-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="e.g. pick up the new memory model"
              maxLength={300}
            />
          </Field>
          {unsafe ? (
            <div className="space-y-1.5 rounded-lg border border-warn/50 p-3">
              <Checkbox label="Restart anyway (force)" checked={force} onChange={setForce} />
              <p className="text-xs text-muted">
                A body may die or fall while it is offline. Only force it when you know the bodies are fine, and say why
                above.
              </p>
            </div>
          ) : null}
          <InlineError error={error} />
        </div>
      )}
    </Dialog>
  );
}

/** Close once it is done; else Cancel and Restart (anyway, when the check says it is not safe). */
function RestartFooter(p: { done: boolean; unsafe: boolean; busy: boolean; disabled: boolean; onClose: () => void; onRestart: () => void }) {
  if (p.done)
    return (
      <Button variant="primary" onClick={p.onClose}>
        Close
      </Button>
    );
  return (
    <>
      <Button variant="ghost" onClick={p.onClose}>
        Cancel
      </Button>
      <Button variant={p.unsafe ? 'danger' : 'primary'} loading={p.busy} disabled={p.disabled} onClick={p.onRestart}>
        <Power className="size-4" /> {p.unsafe ? 'Restart anyway' : 'Restart now'}
      </Button>
    </>
  );
}

type Safety = ReturnType<typeof useApi<'admin.restartSafety'>>;

/** The safety check's answer: loading, could not check, safe, or not safe with its reasons. */
function SafetyVerdict({ safety }: { safety: Safety }) {
  if (safety.isPending) return <SkeletonLines lines={2} />;
  if (safety.error)
    return <ErrorState error={safety.error} onRetry={() => void safety.refetch()} title="Could not check whether a restart is safe" />;
  const s = safety.data;
  if (s.safe)
    return (
      <p className="flex items-start gap-2 text-sm text-ok">
        <CheckCircle2 className="mt-0.5 size-4 shrink-0" /> Safe to restart now: no body is outside at night or
        at the edge of a high build.
      </p>
    );
  return (
    <div className="space-y-2 rounded-lg border border-danger/40 bg-danger-soft/40 p-3">
      <p className="flex items-start gap-2 text-sm font-medium text-danger">
        <AlertTriangle className="mt-0.5 size-4 shrink-0" /> Not safe to restart now
      </p>
      <ul className="list-disc space-y-0.5 pl-6 text-sm text-fg">
        {s.reasons.map((r) => (
          <li key={r}>{r}</li>
        ))}
      </ul>
    </div>
  );
}

/** Each body and why it is (or is not) at risk. */
function BodiesTable({ bodies }: { bodies: NonNullable<Safety['data']>['bodies'] }) {
  return (
    <table className="w-full text-xs" aria-label="Bodies">
      <tbody>
        {bodies.map((b) => (
          <tr key={b.name} className="border-b border-border/60 last:border-0">
            <td className="py-1 pr-2 font-medium text-fg">{b.name}</td>
            <td className="py-1 pr-2 text-muted">{b.reason ?? 'fine'}</td>
            <td className="py-1 text-right">
              {b.exposed ? <Badge tone="danger">exposed</Badge> : null}{' '}
              {b.highBuild ? <Badge tone="danger">high build</Badge> : null}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
