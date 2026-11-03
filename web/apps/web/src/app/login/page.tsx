'use client';

import { useQueryClient } from '@tanstack/react-query';
import { Lock } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, ApiError, ensureCsrf, errorText, setCsrfToken } from '@/lib/api/client';
import { meKey } from '@/lib/auth';
import { needsTotp, safeNext } from '@/lib/loginFlow';
import { setupNeeded } from '@/lib/setupFlow';
import { AuthFrame } from '@/components/shell/AuthFrame';
import { Button, Field, Input } from '@/components/ui/primitives';

/**
 * Sign-in. CSRF first (GET /api/auth/csrf creates the pre-session), then POST login with X-CSRF-Token. The server answers a
 * generic 401 whether the password or the TOTP code was wrong (or the code was missing), so the code field is always on the
 * form, marked optional for users without an authenticator. With no users yet (first-run setup) the page hands over to /setup.
 */
function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const qc = useQueryClient();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [totp, setTotp] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const codeRef = useRef<HTMLInputElement>(null);
  const expired = params.get('reason') === 'expired';

  useEffect(() => {
    void ensureCsrf().catch(() => undefined);
    let live = true;
    void setupNeeded().then((needed) => {
      if (live && needed) router.replace('/setup');
    });
    return () => {
      live = false;
    };
  }, [router]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await ensureCsrf();
      const res = await api('auth.login', { body: { username, password, ...(totp.length === 6 ? { totp } : {}) } });
      setCsrfToken(res.csrfToken);
      qc.clear();
      qc.setQueryData(meKey, res.user);
      router.replace(safeNext(params.get('next')));
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        router.replace('/setup'); // no account exists yet: first-run setup
      } else if (err instanceof ApiError && err.status === 423) {
        setError('Too many attempts. Sign-in is locked for a while; try again later.');
      } else if (err instanceof ApiError && (err.status === 401 || err.status === 400)) {
        if (!totp && needsTotp(err)) {
          setError('Enter the code from your authenticator app.');
          codeRef.current?.focus();
        } else {
          setError(totp ? 'Wrong username, password or code. Codes change every 30 seconds.' : 'Wrong username or password. If you use an authenticator, add its code.');
          setTotp('');
        }
      } else {
        setError(errorText(err));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} className="space-y-4" noValidate>
      {expired ? <p className="rounded-lg bg-info-soft px-3 py-2 text-sm text-info">Your session ended. Sign in to continue.</p> : null}
      <Field label="Username" htmlFor="username">
        <Input id="username" autoComplete="username" autoFocus value={username} onChange={(e) => setUsername(e.target.value)} required />
      </Field>
      <Field label="Password" htmlFor="password">
        <Input id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
      </Field>
      <Field
        label={
          <>
            Authenticator code <span className="font-normal text-subtle">(if you use one)</span>
          </>
        }
        htmlFor="totp"
      >
        <Input
          ref={codeRef}
          id="totp"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={6}
          placeholder="123456"
          value={totp}
          onChange={(e) => setTotp(e.target.value.replace(/\D/g, ''))}
          className="tabular tracking-[0.3em]"
        />
      </Field>
      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      <Button
        type="submit"
        variant="primary"
        loading={busy}
        disabled={!username || !password || (totp.length > 0 && totp.length !== 6)}
        className="h-10 w-full justify-center"
      >
        <Lock className="size-4" /> Sign in
      </Button>
    </form>
  );
}

export default function LoginPage() {
  return (
    <AuthFrame title="MineClaw Explorer" subtitle="Sign in to watch and talk to the hive.">
      <Suspense fallback={null}>
        <LoginForm />
      </Suspense>
    </AuthFrame>
  );
}
