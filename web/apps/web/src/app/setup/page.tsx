'use client';

import { useQueryClient } from '@tanstack/react-query';
import { PASSWORD_MIN_LENGTH, PLAYER_PATTERN, USERNAME_PATTERN, type Role, type TotpEnrollResponse } from '@mineclaw/contracts';
import { ArrowRight, CheckCircle2, Copy, KeyRound, ShieldAlert, ShieldCheck, Smartphone, UserPlus } from 'lucide-react';
import { useRouter } from 'next/navigation';
import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { api, ApiError, ensureCsrf, errorText, setCsrfToken } from '@/lib/api/client';
import { meKey } from '@/lib/auth';
import { FIRST_ACCOUNT_ROLES, passwordStrength, ROLE_INFO, setupKey } from '@/lib/setupFlow';
import { cn, copyText } from '@/lib/utils';
import { AuthFrame } from '@/components/shell/AuthFrame';
import { Button, Checkbox, Field, Input, Meter, Spinner, type Tone } from '@/components/ui/primitives';
import { QrCode } from '@/components/ui/QrCode';

/**
 * First-run setup: shown while the users file holds no users (the server sends every page here and turns sign-in off).
 * 1. Create the first account (it is signed in straight away).
 * 2. Optionally add an authenticator (TOTP: enrol + verify with the existing routes, QR drawn in the browser).
 * 3. Into the app.
 * Once an account exists the server redirects /setup to /login, and the API refuses a second setup.
 */

type Phase = 'checking' | 'closed' | 'account' | 'secure' | 'done';

const STEPS: Array<{ key: Phase; label: string }> = [
  { key: 'account', label: 'Your account' },
  { key: 'secure', label: 'Authenticator' },
  { key: 'done', label: 'Done' },
];

const STRENGTH_TONE: Tone[] = ['danger', 'danger', 'warn', 'ok', 'ok'];
const STRENGTH_TEXT = ['text-danger', 'text-danger', 'text-warn', 'text-ok', 'text-ok'];

function Stepper({ phase }: { phase: Phase }) {
  const at = Math.max(0, STEPS.findIndex((s) => s.key === phase));
  return (
    <ol className="mb-6 flex items-center gap-2" aria-label="Setup steps">
      {STEPS.map((s, i) => (
        <li key={s.key} className="flex flex-1 items-center gap-2" aria-current={i === at ? 'step' : undefined}>
          <span
            className={cn(
              'grid size-6 shrink-0 place-items-center rounded-full text-[11px] font-semibold tabular',
              i < at ? 'bg-ok text-white' : i === at ? 'bg-accent text-accent-fg' : 'bg-surface-3 text-subtle',
            )}
          >
            {i < at ? <CheckCircle2 className="size-3.5" aria-hidden /> : i + 1}
          </span>
          <span className={cn('truncate text-xs', i === at ? 'font-medium text-fg' : 'text-subtle')}>{s.label}</span>
          {i < STEPS.length - 1 ? <span aria-hidden className="h-px flex-1 bg-border" /> : null}
        </li>
      ))}
    </ol>
  );
}

function Notice({ tone, icon, children }: { tone: 'warn' | 'info' | 'danger'; icon: ReactNode; children: ReactNode }) {
  const cls = { warn: 'bg-warn-soft text-warn', info: 'bg-info-soft text-info', danger: 'bg-danger-soft text-danger' }[tone];
  return (
    <div className={cn('flex gap-2.5 rounded-lg px-3 py-2.5 text-sm', cls)}>
      <span className="mt-0.5 shrink-0">{icon}</span>
      <div className="space-y-1">{children}</div>
    </div>
  );
}

function AccountStep({ allowedHere, onCreated }: { allowedHere: boolean; onCreated: (password: string) => void }) {
  const qc = useQueryClient();
  const router = useRouter();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [player, setPlayer] = useState('');
  const [roles, setRoles] = useState<Role[]>([...FIRST_ACCOUNT_ROLES]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const name = username.trim().toLowerCase();
  const nameOk = USERNAME_PATTERN.test(name);
  const playerOk = player === '' || PLAYER_PATTERN.test(player);
  const strength = passwordStrength(password, [name, player]);
  const match = confirm.length > 0 && confirm === password;
  const ready = allowedHere && nameOk && playerOk && password.length >= PASSWORD_MIN_LENGTH && match && roles.length > 0;

  const toggle = (role: Role, on: boolean) =>
    setRoles((r) => (on ? [...new Set([...r, role])] : r.filter((x) => x !== role)));

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!ready) return;
    setBusy(true);
    setError(null);
    try {
      await ensureCsrf();
      const res = await api('setup.create', { body: { username: name, password, player: player || null, roles } });
      setCsrfToken(res.csrfToken);
      qc.clear();
      qc.setQueryData(meKey, res.user);
      qc.setQueryData(setupKey, { needed: false, allowedHere: false });
      onCreated(password);
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        // Someone else finished first (or an account was added from the terminal).
        router.replace('/login');
        return;
      }
      if (err instanceof ApiError && err.status === 403 && err.code === 'forbidden') {
        setError('Setup can only be completed from the local network, not through the tunnel.');
      } else {
        setError(errorText(err));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} className="space-y-4" noValidate>
      <Notice tone="warn" icon={<ShieldAlert className="size-4" aria-hidden />}>
        <p className="font-medium">Whoever completes this page first becomes the administrator.</p>
        <p className="text-xs opacity-90">
          The explorer has no accounts yet, so this page is open to anyone who can reach it on your network. Finish it now; once
          an account exists it closes for good and sign-in takes over.
        </p>
      </Notice>
      {!allowedHere ? (
        <Notice tone="danger" icon={<ShieldAlert className="size-4" aria-hidden />}>
          <p className="font-medium">Not from here.</p>
          <p className="text-xs opacity-90">
            This request came through a proxy or tunnel. Open the explorer directly on the local network (for example
            http://10.1.2.73:8080) to complete the setup.
          </p>
        </Notice>
      ) : null}

      <Field label="Username" htmlFor="setup-username" hint="Lower case letters, digits, dot, dash or underscore.">
        <Input
          id="setup-username"
          autoComplete="username"
          autoFocus
          autoCapitalize="none"
          spellCheck={false}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          aria-invalid={username !== '' && !nameOk}
          placeholder="fox"
        />
      </Field>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Password" htmlFor="setup-password">
          <Input
            id="setup-password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
        <Field label="Confirm password" htmlFor="setup-confirm">
          <Input
            id="setup-confirm"
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            aria-invalid={confirm !== '' && !match}
          />
        </Field>
      </div>
      <div className="-mt-1 space-y-1.5" aria-live="polite">
        <Meter value={password ? strength.score : 0} max={4} tone={STRENGTH_TONE[strength.score]} label="Password strength" />
        <p className="flex justify-between gap-3 text-[11px]">
          <span className={cn('font-medium', password ? STRENGTH_TEXT[strength.score] : 'text-subtle')}>
            {password ? strength.label : `At least ${PASSWORD_MIN_LENGTH} characters`}
          </span>
          <span className="text-right text-subtle">
            {confirm && !match ? <span className="text-danger">The passwords do not match.</span> : password ? strength.hint : null}
          </span>
        </p>
      </div>

      <Field
        label={
          <>
            Minecraft player <span className="font-normal text-subtle">(optional)</span>
          </>
        }
        htmlFor="setup-player"
        hint="Your in-game name. The bots hear your chat from the explorer as this player; without one you can watch but not chat."
      >
        <Input
          id="setup-player"
          autoCapitalize="none"
          spellCheck={false}
          value={player}
          onChange={(e) => setPlayer(e.target.value.trim())}
          aria-invalid={!playerOk}
          placeholder="poofimafox"
          maxLength={16}
        />
      </Field>

      <fieldset className="space-y-2">
        <legend className="mb-1.5 text-xs font-medium text-muted">Roles</legend>
        <div className="grid gap-2 sm:grid-cols-2">
          {ROLE_INFO.map((r) => (
            <div key={r.role} className="rounded-lg border border-border px-3 py-2">
              <Checkbox label={r.label} checked={roles.includes(r.role)} onChange={(on) => toggle(r.role, on)} />
              <p className="mt-0.5 pl-6 text-[11px] text-subtle">{r.hint}</p>
            </div>
          ))}
        </div>
        {!roles.includes('admin') ? (
          <p className="text-[11px] text-warn">
            Without Admin, nobody can edit memory or read the audit log until you add an admin from the terminal.
          </p>
        ) : null}
        {roles.length === 0 ? <p className="text-[11px] text-danger">Pick at least one role.</p> : null}
      </fieldset>

      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      <Button type="submit" variant="primary" loading={busy} disabled={!ready} className="h-10 w-full justify-center">
        <UserPlus className="size-4" /> Create account and sign in
      </Button>
    </form>
  );
}

function SecureStep({ password, onDone }: { password: string; onDone: (enrolled: boolean) => void }) {
  const [pending, setPending] = useState<TotpEnrollResponse | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      setPending(await api('auth.totpEnroll', { body: { password } }));
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const verify = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api('auth.totpVerify', { body: { code } });
      if (r.ok) onDone(true);
      else {
        setError('That code did not match. Codes change every 30 seconds; try the current one.');
        setCode('');
      }
    } catch (err) {
      setError(err instanceof ApiError && err.status === 400 ? 'That code did not match. Try the current one.' : errorText(err));
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  if (!pending) {
    return (
      <div className="space-y-4">
        <div className="flex gap-3">
          <span className="grid size-10 shrink-0 place-items-center rounded-lg bg-accent-soft text-accent">
            <Smartphone className="size-5" aria-hidden />
          </span>
          <div>
            <h2 className="text-sm font-semibold text-fg">Secure it with an authenticator</h2>
            <p className="mt-1 text-sm text-muted">
              Recommended, especially before the explorer is reachable from outside. Sign-in will also ask for a 6-digit code
              from an app such as Aegis, 2FAS, Google Authenticator or 1Password.
            </p>
          </div>
        </div>
        {error ? (
          <p role="alert" className="text-sm text-danger">
            {error}
          </p>
        ) : null}
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="ghost" onClick={() => onDone(false)} className="justify-center">
            Skip for now
          </Button>
          <Button variant="primary" loading={busy} onClick={() => void start()} className="justify-center">
            <KeyRound className="size-4" /> Set up authenticator
          </Button>
        </div>
        <p className="text-[11px] text-subtle">You can add one later from the account menu.</p>
      </div>
    );
  }

  return (
    <form onSubmit={(e) => void verify(e)} className="space-y-4" noValidate>
      <p className="text-sm text-muted">Scan this code with your authenticator app, then enter the 6-digit code it shows.</p>
      <div className="flex flex-col items-center gap-4 sm:flex-row sm:items-start">
        <div className="rounded-xl border border-border bg-white p-2">
          <QrCode value={pending.otpauthUrl} label="Authenticator QR code" />
        </div>
        <div className="w-full min-w-0 space-y-3">
          <div>
            <p className="mb-1 text-xs font-medium text-muted">Can&apos;t scan? Enter this key</p>
            <div className="flex items-center gap-1.5 rounded-lg border border-border bg-code p-2">
              <code className="flex-1 font-mono text-xs break-all text-fg" data-testid="totp-secret">
                {pending.secret}
              </code>
              <Button
                size="icon-sm"
                variant="ghost"
                aria-label="Copy key"
                onClick={() => void copyText(pending.secret).then((ok) => setCopied(ok))}
              >
                {copied ? <CheckCircle2 className="size-3.5 text-ok" /> : <Copy className="size-3.5" />}
              </Button>
            </div>
            <p className="mt-1 text-[11px] text-subtle">Shown once. Time-based, 6 digits, 30 seconds.</p>
          </div>
          <Field label="6-digit code" htmlFor="setup-code">
            <Input
              id="setup-code"
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              placeholder="123456"
              value={code}
              onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
              className="tabular text-center text-lg tracking-[0.4em]"
            />
          </Field>
        </div>
      </div>
      {error ? (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      ) : null}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button variant="ghost" onClick={() => onDone(false)} className="justify-center">
          Skip for now
        </Button>
        <Button type="submit" variant="primary" loading={busy} disabled={code.length !== 6} className="justify-center">
          <ShieldCheck className="size-4" /> Verify and turn on
        </Button>
      </div>
    </form>
  );
}

function DoneStep({ enrolled }: { enrolled: boolean }) {
  const router = useRouter();
  return (
    <div className="space-y-4 text-center">
      <span className="mx-auto grid size-12 place-items-center rounded-full bg-ok-soft text-ok">
        <CheckCircle2 className="size-6" aria-hidden />
      </span>
      <div>
        <h2 className="text-base font-semibold text-fg">You&apos;re all set</h2>
        <p className="mt-1 text-sm text-muted">
          {enrolled
            ? 'Your account is ready and two-factor sign-in is on.'
            : 'Your account is ready. You can add an authenticator later from the account menu.'}
        </p>
      </div>
      <Button variant="primary" autoFocus onClick={() => router.replace('/live')} className="h-10 w-full justify-center">
        Open the explorer <ArrowRight className="size-4" />
      </Button>
      <p className="text-[11px] text-subtle">
        More accounts: <code className="font-mono">/usr/local/sbin/mineclaw-web-users</code> on the server.
      </p>
    </div>
  );
}

export default function SetupPage() {
  const router = useRouter();
  const [phase, setPhase] = useState<Phase>('checking');
  const [allowedHere, setAllowedHere] = useState(true);
  const [enrolled, setEnrolled] = useState(false);
  const [checkError, setCheckError] = useState<string | null>(null);
  // Kept only between creating the account and the optional TOTP step (enrolment re-checks it), then dropped.
  const [password, setPassword] = useState('');

  useEffect(() => {
    let live = true;
    api('setup.status').then(
      (s) => {
        if (!live) return;
        if (!s.needed) {
          setPhase('closed');
          router.replace('/login');
          return;
        }
        setAllowedHere(s.allowedHere);
        setPhase('account');
      },
      (err: unknown) => {
        if (live) setCheckError(errorText(err));
      },
    );
    return () => {
      live = false;
    };
  }, [router]);

  const finishSecure = (on: boolean) => {
    setPassword('');
    setEnrolled(on);
    setPhase('done');
  };

  return (
    <AuthFrame
      wide
      title="Welcome to MineClaw Explorer"
      subtitle={
        phase === 'done'
          ? 'Setup complete.'
          : phase === 'secure'
            ? 'First-time setup: secure your account.'
            : 'First-time setup: create the administrator account.'
      }
    >
      {checkError ? (
        <p role="alert" className="text-sm text-danger">
          The explorer server is not answering: {checkError}
        </p>
      ) : phase === 'checking' || phase === 'closed' ? (
        <div className="flex justify-center py-8" aria-busy="true">
          <Spinner className="size-5" />
        </div>
      ) : (
        <>
          <Stepper phase={phase} />
          {phase === 'account' ? (
            <AccountStep
              allowedHere={allowedHere}
              onCreated={(pw) => {
                setPassword(pw);
                setPhase('secure');
              }}
            />
          ) : phase === 'secure' ? (
            <SecureStep password={password} onDone={finishSecure} />
          ) : (
            <DoneStep enrolled={enrolled} />
          )}
        </>
      )}
    </AuthFrame>
  );
}
