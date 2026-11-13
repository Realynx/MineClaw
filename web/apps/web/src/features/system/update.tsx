'use client';

/**
 * Update checker UI pieces shared by the shell banner and Administration → Version and updates: the status query (polled
 * every few seconds while an update runs), the admin "Update now" confirm flow (systemd), the copyable upgrade command (docker,
 * helm) and the updater's run status.
 */
import type { SystemUpdateStatus, UpdateRunStatus } from '@mineclaw/contracts';
import { useQueryClient } from '@tanstack/react-query';
import { Check, Copy } from 'lucide-react';
import { useMemo, useState, useSyncExternalStore } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button, InlineError, Spinner } from '@/components/ui/primitives';
import { api } from '@/lib/api/client';
import { qk, useApi } from '@/lib/api/query';
import { cn } from '@/lib/utils';

const MIN = 60_000;
export const UPDATE_POLL_MS = 3000;
const IDLE_REFRESH_MS = 30 * MIN;
/** How long a finished run stays on the banner. */
export const RESULT_SHOWN_MS = 30 * MIN;

/** After "Update now", keep polling across the gap between the updater taking the request and writing status.json. */
let pollUntil = 0;
export function pollForAWhile(ms = 10 * MIN): void {
  pollUntil = Date.now() + ms;
}

/** A request is waiting or the updater is running. */
export function updateActive(s: SystemUpdateStatus | undefined): boolean {
  return !!s?.update.request || s?.update.status?.state === 'running';
}

export function useUpdateStatus(poll: boolean) {
  return useApi(
    'system.update',
    {},
    {
      staleTime: 5 * MIN,
      refetchInterval: (q) => (poll && (updateActive(q.state.data) || Date.now() < pollUntil) ? UPDATE_POLL_MS : IDLE_REFRESH_MS),
      // The web restarts during an update: keep polling through the errors.
      refetchIntervalInBackground: poll,
    },
  );
}

/** A finished run worth showing (recent), else null. */
export function recentResult(s: SystemUpdateStatus | undefined, now = Date.now()): UpdateRunStatus | null {
  const st = s?.update.status;
  if (!st || st.state === 'running' || updateActive(s)) return null;
  const at = Date.parse(st.finishedAt ?? st.startedAt ?? '');
  return Number.isFinite(at) && now - at < RESULT_SHOWN_MS ? st : null;
}

// ---------- per-browser dismissals (localStorage; storage may be unavailable) ----------

const DISMISS_KEY = 'mineclaw.updates.dismissed';
type Dismissed = { version?: string; result?: string };

/** Dismissals for this page load when storage is unavailable (private mode, blocked site data). */
let memoryDismissed: string | null = null;
const dismissListeners = new Set<() => void>();

function rawDismissed(): string | null {
  try {
    return window.localStorage.getItem(DISMISS_KEY);
  } catch {
    return memoryDismissed;
  }
}

function parseDismissed(raw: string | null): Dismissed {
  try {
    const v = raw ? (JSON.parse(raw) as unknown) : null;
    return v && typeof v === 'object' ? v : {};
  } catch {
    return {};
  }
}

function subscribeDismissed(cb: () => void): () => void {
  dismissListeners.add(cb);
  window.addEventListener('storage', cb);
  return () => {
    dismissListeners.delete(cb);
    window.removeEventListener('storage', cb);
  };
}

/** What this browser dismissed: a release version (until the next release) and a finished run. */
export function useDismissed(): [Dismissed, (patch: Dismissed) => void] {
  const raw = useSyncExternalStore(subscribeDismissed, rawDismissed, () => null);
  const dismissed = useMemo(() => parseDismissed(raw), [raw]);
  const update = (patch: Dismissed) => {
    const next = JSON.stringify({ ...parseDismissed(rawDismissed()), ...patch });
    try {
      window.localStorage.setItem(DISMISS_KEY, next);
    } catch {
      memoryDismissed = next; // private mode / blocked storage: kept in memory for this page load
    }
    for (const l of dismissListeners) l();
  };
  return [dismissed, update];
}

// ---------- admin actions ----------

/** systemd: confirm, then ask the root updater for the latest release. */
export function UpdateNowButton({ status, size = 'sm' }: { status: SystemUpdateStatus; size?: 'sm' | 'md' }) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const latest = status.latest;
  if (!latest || status.update.mode !== 'trigger' || !status.outdated) return null;
  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      const next = await api('system.updateRequest', { body: { version: latest.version } });
      pollForAWhile();
      qc.setQueryData(qk('system.update'), next);
      setOpen(false);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <Button
        variant="primary"
        size={size}
        disabled={updateActive(status)}
        onClick={() => {
          setError(null);
          setOpen(true);
        }}
      >
        Update now
      </Button>
      <Dialog
        open={open}
        onOpenChange={setOpen}
        size="sm"
        title={`Update MineClaw to v${latest.version}?`}
        description={`This server runs v${status.current}.`}
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)} disabled={busy}>
              Cancel
            </Button>
            <Button variant="primary" loading={busy} onClick={() => void start()}>
              Update to v{latest.version}
            </Button>
          </>
        }
      >
        <div className="space-y-2 text-sm text-muted">
          <p>
            The server downloads the release from GitHub, checks its checksums and installs it. The bot and the web
            restart, so this page may stop responding for a minute.
          </p>
          <p>If the new version does not start correctly, the server rolls back to the version that runs now.</p>
          {error ? <InlineError error={error} /> : null}
        </div>
      </Dialog>
    </>
  );
}

/** docker / helm: the command to run, with a copy button. */
export function UpgradeCommand({ command, className }: { command: string; className?: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      setCopied(false);
    }
  };
  return (
    <div className={cn('flex items-start gap-2 rounded-md border border-border bg-surface px-2 py-1.5', className)}>
      <code className="min-w-0 flex-1 font-mono text-xs break-all text-fg select-all">{command}</code>
      <Button variant="ghost" size="icon-sm" onClick={() => void copy()} aria-label={copied ? 'Copied' : 'Copy command'}>
        {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
      </Button>
    </div>
  );
}

const RESULT_TEXT: Record<UpdateRunStatus['state'], string> = {
  running: 'is being installed',
  succeeded: 'was installed',
  failed: 'failed',
  'rolled-back': 'failed and was rolled back',
};

/** The updater's progress or result (systemd). */
export function RunStatus({ status }: { status: SystemUpdateStatus }) {
  const st = status.update.status;
  const req = status.update.request;
  if (req && st?.state !== 'running') {
    return (
      <p className="flex items-center gap-2 text-sm text-muted" role="status">
        <Spinner className="size-3.5" /> Update to v{req.version} requested; waiting for the updater to start.
      </p>
    );
  }
  if (!st) return null;
  const tone = st.state === 'succeeded' ? 'text-ok' : st.state === 'running' ? 'text-fg' : 'text-danger';
  const lines = (st.log ?? []).slice(-5);
  return (
    <div className="space-y-1" role="status">
      <p className={cn('flex items-center gap-2 text-sm font-medium', tone)}>
        {st.state === 'running' ? <Spinner className="size-3.5" /> : null}
        Update{st.version ? ` to v${st.version}` : ''} {RESULT_TEXT[st.state]}
        {st.message ? <span className="font-normal text-muted">: {st.message}</span> : null}
      </p>
      {st.state === 'running' && lines.length ? (
        <pre className="max-h-28 overflow-auto rounded-md bg-surface-2 p-2 font-mono text-[11px] text-muted">{lines.join('\n')}</pre>
      ) : null}
    </div>
  );
}
