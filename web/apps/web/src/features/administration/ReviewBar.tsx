'use client';

/** The sticky "Review changes" bar and its dialog: the diff (secrets only as changed / cleared), a reason, Save. */
import { ArrowRight, ListChecks } from 'lucide-react';
import { useState } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Badge, Button, Field, InlineError, Textarea } from '@/components/ui/primitives';
import { cn, plural } from '@/lib/utils';
import { reasonError, type DiffRow } from './diff';

export function ReviewBar({
  rows,
  invalid,
  reason,
  onReason,
  onSave,
  onDiscard,
  saving,
  error,
  open,
  onOpenChange,
  description,
}: {
  rows: readonly DiffRow[];
  invalid: number;
  reason: string;
  onReason: (r: string) => void;
  onSave: () => void;
  onDiscard: () => void;
  saving: boolean;
  error: unknown;
  open: boolean;
  onOpenChange: (o: boolean) => void;
  description?: string;
}) {
  const [touched, setTouched] = useState(false);
  if (rows.length === 0 && invalid === 0) return null;
  const restart = rows.filter((r) => r.apply === 'restart').length;
  const rErr = reasonError(reason);
  return (
    <>
      <div className="sticky bottom-0 z-20 mt-4 pb-4" role="region" aria-label="Unsaved changes">
        <div className="flex flex-wrap items-center gap-3 rounded-card border border-accent/40 bg-surface px-4 py-3 shadow-pop">
          <ListChecks className="size-4 text-accent" aria-hidden />
          <p className="min-w-0 flex-1 text-sm text-fg">
            {rows.length ? `${plural(rows.length, 'unsaved change')}` : 'No changes yet'}
            {restart ? <span className="text-muted"> · {restart} need a restart</span> : null}
            {invalid ? <span className="text-danger"> · fix {plural(invalid, 'field')} first</span> : null}
          </p>
          <Button variant="ghost" size="sm" onClick={onDiscard}>
            Discard
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => onOpenChange(true)}
            disabled={rows.length === 0 || invalid > 0}
          >
            Review changes
          </Button>
        </div>
      </div>
      <Dialog
        open={open}
        onOpenChange={onOpenChange}
        size="lg"
        title="Review changes"
        description={
          description ??
          "Saved to the node's override file (the previous one is kept as a backup). Live settings apply at once; the rest after a restart."
        }
        footer={
          <>
            <Button variant="ghost" onClick={() => onOpenChange(false)}>
              Keep editing
            </Button>
            <Button
              variant="primary"
              loading={saving}
              disabled={!!rErr || invalid > 0}
              onClick={() => {
                setTouched(true);
                if (!rErr) onSave();
              }}
            >
              Save {plural(rows.length, 'change')}
            </Button>
          </>
        }
      >
        <div className="space-y-4">
          <table className="w-full text-sm" aria-label="Changes">
            <thead>
              <tr className="border-b border-border text-left text-[11px] tracking-wide text-subtle uppercase">
                <th className="py-1.5 pr-3 font-medium">Setting</th>
                <th className="py-1.5 pr-3 font-medium">Change</th>
                <th className="py-1.5 font-medium">Applies</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.key} className="border-b border-border/60 align-top last:border-0">
                  <td className="py-2 pr-3">
                    <div className="font-medium text-fg">
                      {r.section} · {r.label}
                    </div>
                    <div className="font-mono text-[11px] text-subtle">{r.key}</div>
                  </td>
                  <td className="py-2 pr-3">
                    <span className="inline-flex flex-wrap items-center gap-1.5 break-all">
                      <span className="text-muted line-through decoration-subtle/60">{r.from}</span>
                      <ArrowRight className="size-3.5 shrink-0 text-subtle" aria-label="to" />
                      <span className={cn('font-medium', r.kind === 'secret-cleared' ? 'text-danger' : 'text-fg')}>
                        {r.to}
                      </span>
                    </span>
                  </td>
                  <td className="py-2">
                    <Badge tone={r.apply === 'live' ? 'ok' : 'warn'}>{r.apply === 'live' ? 'Live' : 'Restart'}</Badge>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <Field
            label="Reason"
            htmlFor="admin-save-reason"
            hint="Recorded in the node's audit with the keys (never the values)."
          >
            <Textarea
              id="admin-save-reason"
              rows={2}
              value={reason}
              onChange={(e) => onReason(e.target.value)}
              onBlur={() => setTouched(true)}
              placeholder="e.g. try the local model for the memory tier"
              maxLength={300}
            />
          </Field>
          {touched && rErr ? <p className="text-xs text-danger">{rErr}</p> : null}
          <InlineError error={error} />
        </div>
      </Dialog>
    </>
  );
}
