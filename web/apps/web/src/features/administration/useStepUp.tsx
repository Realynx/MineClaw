'use client';

/**
 * Step-up for the Administration writes: `run(fn)` asks for the TOTP code (or the password without TOTP) when the session's
 * step-up is missing or stale, and again when the server still answers 403 `step_up_required`, then runs `fn` once more.
 */
import { useQueryClient } from '@tanstack/react-query';
import type { Me } from '@mineclaw/contracts';
import { ShieldCheck } from 'lucide-react';
import { useCallback, useRef, useState, type ReactNode } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button, Field, InlineError, Input } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api/client';
import { meKey, useAuth } from '@/lib/auth';
import { stepUpFresh } from './fieldState';

export class StepUpCancelled extends Error {
  constructor() {
    super('Confirmation cancelled');
    this.name = 'StepUpCancelled';
  }
}

export function isStepUpRequired(e: unknown): boolean {
  return e instanceof ApiError && e.status === 403 && e.code === 'step_up_required';
}

export interface StepUp {
  run: <T>(fn: () => Promise<T>) => Promise<T>;
  dialog: ReactNode;
}

export function useStepUp(): StepUp {
  const { me } = useAuth();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const pending = useRef<{ resolve: () => void; reject: (e: Error) => void } | null>(null);

  const ask = useCallback(
    () =>
      new Promise<void>((resolve, reject) => {
        pending.current = { resolve, reject };
        setOpen(true);
      }),
    [],
  );

  const run = useCallback(
    async <T,>(fn: () => Promise<T>): Promise<T> => {
      if (!stepUpFresh(qc.getQueryData<Me>(meKey))) await ask();
      try {
        return await fn();
      } catch (e) {
        if (!isStepUpRequired(e)) throw e;
        await ask();
        return fn();
      }
    },
    [ask, qc],
  );

  const done = (until: string) => {
    const current = qc.getQueryData<Me>(meKey);
    if (current) qc.setQueryData<Me>(meKey, { ...current, stepUpUntil: until });
    setOpen(false);
    pending.current?.resolve();
    pending.current = null;
  };
  const cancel = () => {
    setOpen(false);
    pending.current?.reject(new StepUpCancelled());
    pending.current = null;
  };

  return { run, dialog: open ? <StepUpDialog totp={me.totpEnrolled} onDone={done} onCancel={cancel} /> : null };
}

function StepUpDialog({
  totp,
  onDone,
  onCancel,
}: {
  totp: boolean;
  onDone: (until: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const valid = totp ? /^\d{6}$/.test(value) : value.length > 0;

  const submit = async () => {
    if (!valid || busy) return;
    setBusy(true);
    setError(null);
    try {
      const r = await api('auth.stepUp', { body: totp ? { code: value } : { password: value } });
      onDone(r.stepUpUntil);
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 401
          ? new Error(totp ? 'That code did not match.' : 'That password did not match.')
          : e,
      );
      setValue('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onCancel()}
      size="sm"
      title={
        <span className="inline-flex items-center gap-2">
          <ShieldCheck className="size-4 text-accent" /> Confirm it's you
        </span>
      }
      description={
        totp
          ? 'Changing the bot needs a fresh check. Enter the current code from your authenticator; it lasts 5 minutes.'
          : 'Changing the bot needs a fresh check. Enter your password; it lasts 5 minutes.'
      }
      footer={
        <>
          <Button variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="primary" onClick={() => void submit()} disabled={!valid} loading={busy}>
            Confirm
          </Button>
        </>
      }
    >
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
        className="space-y-3"
      >
        <Field label={totp ? 'Authenticator code' : 'Password'} htmlFor="step-up-input">
          <Input
            id="step-up-input"
            autoFocus
            type={totp ? 'text' : 'password'}
            inputMode={totp ? 'numeric' : undefined}
            autoComplete={totp ? 'one-time-code' : 'current-password'}
            maxLength={totp ? 6 : 256}
            value={value}
            onChange={(e) => setValue(totp ? e.target.value.replace(/\D/g, '') : e.target.value)}
          />
        </Field>
        <InlineError error={error} />
      </form>
    </Dialog>
  );
}
