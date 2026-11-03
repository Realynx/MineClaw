'use client';

/**
 * Generic memory edit form, built from `GET /api/admin/schema` (MemoryEditSchema): add / update / delete one record through the
 * bot. Two steps: fill → review (a field-by-field diff of exactly what will be sent; update sends only changed fields, "" clears)
 * → apply. A refusal comes back as a 400 with a `MemoryEditResult` body and is shown as such.
 */
import { useQueryClient } from '@tanstack/react-query';
import { MemoryEditResult, type EditField, type EditKind, type EditOp } from '@mineclaw/contracts';
import { ArrowLeft, CheckCircle2, XCircle } from 'lucide-react';
import { useMemo, useState } from 'react';
import { ApiError, errorText } from '@/lib/api/client';
import { useApi, useApiMutation } from '@/lib/api/query';
import { cn } from '@/lib/utils';
import { Badge, Button, ErrorState, Field, Input, Select, SkeletonLines, Textarea } from '@/components/ui/primitives';

export function changedFields(prefill: Record<string, string | null | undefined>, values: Record<string, string>): Record<string, string | null> {
  const out: Record<string, string | null> = {};
  for (const [k, v] of Object.entries(values)) {
    const before = prefill[k] ?? '';
    if (v !== before) out[k] = v;
  }
  return out;
}

function FieldInput({ f, value, onChange, disabled }: { f: EditField; value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const id = `edit-${f.name}`;
  const input =
    f.type === 'multiline' ? (
      <Textarea id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} rows={3} />
    ) : f.type === 'enum' ? (
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} className="w-full">
        <option value="">—</option>
        {(f.options ?? []).map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </Select>
    ) : f.type === 'bool' ? (
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} className="w-full">
        <option value="">—</option>
        <option value="true">yes</option>
        <option value="false">no</option>
      </Select>
    ) : (
      <Input id={id} value={value} inputMode={f.type === 'int' || f.type === 'number' ? 'decimal' : undefined} onChange={(e) => onChange(e.target.value)} disabled={disabled} />
    );
  return (
    <Field
      label={
        <>
          {f.label}
          {f.required ? <span className="text-danger"> *</span> : null}
        </>
      }
      hint={f.help}
      htmlFor={id}
    >
      {input}
    </Field>
  );
}

export function EditForm({
  kind,
  op,
  id,
  prefill = {},
  onDone,
  onCancel,
}: {
  kind: EditKind;
  op: EditOp;
  id?: string | null;
  prefill?: Record<string, string | null | undefined>;
  onDone?: () => void;
  onCancel?: () => void;
}) {
  const qc = useQueryClient();
  const schema = useApi('admin.schema');
  const spec = schema.data?.kinds.find((k) => k.kind === kind);
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(Object.entries(prefill).map(([k, v]) => [k, v ?? ''])),
  );
  const [reason, setReason] = useState('');
  const [step, setStep] = useState<'fill' | 'review'>(op === 'delete' ? 'review' : 'fill');
  const edit = useApiMutation('admin.memoryEdit', {
    onSuccess: (r) => {
      if (r.ok) void qc.invalidateQueries({ predicate: (q) => String(q.queryKey[0]).startsWith('memory') || q.queryKey[0] === 'overview' });
    },
  });

  const fields = useMemo(() => spec?.fields ?? [], [spec]);
  const send = useMemo(() => {
    if (op === 'delete') return {};
    if (op === 'add') return Object.fromEntries(Object.entries(values).filter(([, v]) => v !== ''));
    return changedFields(prefill, values);
  }, [op, values, prefill]);
  const missing = op === 'add' ? fields.filter((f) => f.required && !values[f.name]) : [];

  if (schema.isPending) return <SkeletonLines lines={5} />;
  if (schema.error) return <ErrorState error={schema.error} />;
  if (!spec) return <p className="text-sm text-muted">This kind cannot be edited.</p>;

  const refused = edit.error instanceof ApiError ? MemoryEditResult.safeParse(edit.error.body) : null;
  const result = edit.data ?? (refused?.success ? refused.data : null);

  if (result) {
    return (
      <div className="space-y-4">
        <div className={cn('flex items-start gap-2 rounded-lg p-3 text-sm', result.ok ? 'bg-ok-soft text-ok' : 'bg-danger-soft text-danger')}>
          {result.ok ? <CheckCircle2 className="mt-0.5 size-4 shrink-0" /> : <XCircle className="mt-0.5 size-4 shrink-0" />}
          <div>
            <div className="font-medium">{result.message}</div>
            {result.ok && result.reembedded ? <div className="text-xs opacity-80">Re-embedded and updated in the RAPTOR tree.</div> : null}
          </div>
        </div>
        {result.ok ? <p className="text-xs text-muted">The bot applied the change; the views refresh from its db.</p> : null}
        <div className="flex justify-end gap-2">
          {!result.ok ? (
            <Button onClick={() => edit.reset()}>
              <ArrowLeft className="size-3.5" /> Back
            </Button>
          ) : null}
          <Button variant="primary" onClick={onDone}>
            Done
          </Button>
        </div>
      </div>
    );
  }

  if (step === 'review') {
    const entries = Object.entries(send);
    return (
      <div className="space-y-4">
        <p className="text-sm text-muted">
          {op === 'delete' ? (
            <>
              Delete this <strong className="text-fg">{kind}</strong>
              {id ? ` (${id})` : ''}? The bot removes it from its index, vectors and the RAPTOR tree.
            </>
          ) : (
            <>
              Review what will be {op === 'add' ? 'added' : 'changed'} on the bot:
            </>
          )}
        </p>
        {op === 'delete' && Object.keys(prefill).length ? (
          <div className="rounded-lg border border-border bg-surface-2 p-3 text-xs text-muted">
            {Object.entries(prefill)
              .filter(([, v]) => v)
              .map(([k, v]) => (
                <div key={k}>
                  <span className="text-subtle">{k}:</span> {v}
                </div>
              ))}
          </div>
        ) : null}
        {op !== 'delete' ? (
          entries.length === 0 ? (
            <p className="rounded-lg bg-surface-2 p-3 text-sm text-muted">Nothing changed.</p>
          ) : (
            <table className="w-full text-sm">
              <thead className="text-left text-xs text-subtle">
                <tr>
                  <th className="pb-1.5 font-medium">Field</th>
                  {op === 'update' ? <th className="pb-1.5 font-medium">Before</th> : null}
                  <th className="pb-1.5 font-medium">{op === 'update' ? 'After' : 'Value'}</th>
                </tr>
              </thead>
              <tbody>
                {entries.map(([k, v]) => (
                  <tr key={k} className="border-t border-border align-top">
                    <td className="py-2 pr-3 text-muted">{fields.find((f) => f.name === k)?.label ?? k}</td>
                    {op === 'update' ? <td className="py-2 pr-3 text-danger line-through decoration-danger/40">{prefill[k] || <span className="no-underline">—</span>}</td> : null}
                    <td className="py-2 text-ok">{v === '' ? <Badge>cleared</Badge> : v}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        ) : null}
        <Field label="Reason (recorded in the audit)" htmlFor="edit-reason">
          <Input id="edit-reason" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. correction from Robin" />
        </Field>
        {edit.error && !refused?.success ? <p className="text-xs text-danger">{errorText(edit.error)}</p> : null}
        <div className="flex justify-end gap-2">
          {op === 'delete' ? (
            <Button variant="ghost" onClick={onCancel}>
              Cancel
            </Button>
          ) : (
            <Button variant="ghost" onClick={() => setStep('fill')}>
              <ArrowLeft className="size-3.5" /> Edit
            </Button>
          )}
          <Button
            variant={op === 'delete' ? 'danger' : 'primary'}
            loading={edit.isPending}
            disabled={op !== 'delete' && entries.length === 0}
            onClick={() => edit.mutate({ body: { op, kind, id: id ?? null, fields: send, reason: reason || null } })}
          >
            {op === 'delete' ? 'Delete' : op === 'add' ? 'Add' : 'Apply change'}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <form
      className="space-y-4"
      onSubmit={(e) => {
        e.preventDefault();
        setStep('review');
      }}
    >
      {fields
        .filter((f) => !(op === 'update' && f.help?.includes('add only')))
        .map((f) => (
          <FieldInput key={f.name} f={f} value={values[f.name] ?? ''} onChange={(v) => setValues((s) => ({ ...s, [f.name]: v }))} />
        ))}
      <div className="flex justify-end gap-2">
        <Button variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" disabled={missing.length > 0}>
          Review
        </Button>
      </div>
    </form>
  );
}
