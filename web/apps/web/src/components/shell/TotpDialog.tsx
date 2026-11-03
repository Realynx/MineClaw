'use client';

import { useQueryClient } from '@tanstack/react-query';
import type { TotpEnrollResponse } from '@mineclaw/contracts';
import { CheckCircle2, Copy } from 'lucide-react';
import { useState } from 'react';
import { useApiMutation } from '@/lib/api/query';
import { meKey, useAuth } from '@/lib/auth';
import { copyText } from '@/lib/utils';
import { Button, Field, InlineError, Input } from '@/components/ui/primitives';
import { Dialog } from '@/components/ui/overlays';

/** TOTP enrolment: re-enter the password, add the secret to an authenticator, confirm a code. */
export function TotpDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const { me } = useAuth();
  const qc = useQueryClient();
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [pending, setPending] = useState<TotpEnrollResponse | null>(null);
  const enroll = useApiMutation('auth.totpEnroll', { onSuccess: (r) => setPending(r) });
  const verify = useApiMutation('auth.totpVerify', {
    onSuccess: () => void qc.invalidateQueries({ queryKey: meKey }),
  });

  const close = (o: boolean) => {
    if (!o) {
      setPassword('');
      setCode('');
      setPending(null);
      enroll.reset();
      verify.reset();
    }
    onOpenChange(o);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={close}
      title="Two-factor authentication"
      description={me.totpEnrolled ? 'An authenticator app is enrolled. Enrolling again replaces it.' : 'Add a time-based code to your sign-in.'}
      size="sm"
    >
      {verify.data?.ok ? (
        <div className="flex items-center gap-2 rounded-lg bg-ok-soft p-3 text-sm text-ok">
          <CheckCircle2 className="size-4" /> Two-factor authentication is on.
        </div>
      ) : !pending ? (
        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault();
            enroll.mutate({ body: { password } });
          }}
        >
          <Field label="Your password" htmlFor="totp-pw">
            <Input id="totp-pw" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </Field>
          <InlineError error={enroll.error} />
          <Button type="submit" variant="primary" loading={enroll.isPending} disabled={!password} className="w-full justify-center">
            Continue
          </Button>
        </form>
      ) : (
        <form
          className="space-y-3"
          onSubmit={(e) => {
            e.preventDefault();
            verify.mutate({ body: { code } });
          }}
        >
          <p className="text-sm text-muted">Add this key to your authenticator app. It is shown only once.</p>
          <div className="flex items-center gap-2 rounded-lg border border-border bg-code p-2.5">
            <code className="flex-1 font-mono text-sm break-all text-fg">{pending.secret}</code>
            <Button size="icon-sm" variant="ghost" aria-label="Copy key" onClick={() => void copyText(pending.secret)}>
              <Copy className="size-3.5" />
            </Button>
          </div>
          <a href={pending.otpauthUrl} className="block text-xs text-accent underline-offset-2 hover:underline">
            Open in an authenticator on this device
          </a>
          <Field label="6-digit code" htmlFor="totp-code">
            <Input
              id="totp-code"
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="\d{6}"
              maxLength={6}
              value={code}
              onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
              className="tabular text-center text-lg tracking-[0.4em]"
            />
          </Field>
          <InlineError error={verify.error} />
          <Button type="submit" variant="primary" loading={verify.isPending} disabled={code.length !== 6} className="w-full justify-center">
            Verify and turn on
          </Button>
        </form>
      )}
    </Dialog>
  );
}
