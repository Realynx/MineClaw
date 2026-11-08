'use client';

import { useState } from 'react';
import { WebTlsSettings, type WebTlsSettings as TlsSettings } from '@mineclaw/contracts';
import { Card, CardHeader, Field, Input, InlineError, Button } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { api, ApiError } from '@/lib/api/client';
import { ReviewBar } from './ReviewBar';
import { StepUpCancelled, useStepUp } from './useStepUp';
import { formatValue, type DiffRow } from './diff';

const labels: Record<keyof TlsSettings, string> = {
  enabled: 'Enable HTTPS',
  hostname: 'Hostname',
  port: 'HTTPS port',
  certFile: 'Certificate chain path',
  keyFile: 'Private key path',
};

export function WebHostingCard() {
  const query = useApi('admin.webHosting');
  const stepUp = useStepUp();
  const [draft, setDraft] = useState<Partial<TlsSettings>>({});
  const [reason, setReason] = useState('');
  const [review, setReview] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const displayError =
    error instanceof ApiError && error.details?.length
      ? new Error(
          error.details
            .map((detail) => `${labels[detail.path as keyof TlsSettings] ?? detail.path}: ${detail.message}`)
            .join('; '),
        )
      : error;
  const data = query.data;
  const value = { ...(data?.saved ?? WebTlsSettings.parse({})), ...draft };
  const invalid =
    !WebTlsSettings.safeParse(value).success ||
    (value.enabled && (!value.hostname || !value.certFile || !value.keyFile));
  const rows: DiffRow[] = data
    ? (Object.keys(labels) as Array<keyof TlsSettings>)
        .filter((key) => value[key] !== data.saved[key])
        .map((key) => ({
          key,
          label: labels[key],
          group: 'web-hosting',
          section: 'Web HTTPS',
          apply: 'restart',
          kind: 'change',
          from: formatValue(data.saved[key]),
          to: formatValue(value[key]),
        }))
    : [];
  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await stepUp.run(() => api('admin.webHostingSave', { body: { tls: value, reason: reason.trim() } }));
      setDraft({});
      setReview(false);
      setReason('');
      await query.refetch();
    } catch (e) {
      if (!(e instanceof StepUpCancelled)) setError(e);
    } finally {
      setSaving(false);
    }
  };
  return (
    <Card id="web-hosting" className="mb-5" role="region" aria-label="Web HTTPS and certificates">
      <CardHeader
        title="Web HTTPS and certificates"
        subtitle="Use a certificate for your chosen hostname to install MineClaw as an app. Certificates and keys stay on the web server."
      />
      <div className="space-y-4 p-4">
        {query.error ? (
          <>
            <InlineError error={query.error} />
            <Button onClick={() => void query.refetch()}>Retry</Button>
          </>
        ) : null}
        {data ? (
          <>
            <p className="text-sm text-muted">
              {data.httpsOrigin ? (
                <>
                  Running at{' '}
                  <a className="text-accent underline" href={data.httpsOrigin}>
                    {data.httpsOrigin}
                  </a>
                  .
                </>
              ) : (
                'Direct HTTPS is currently off. HTTPS provided by an existing reverse proxy also supports installation.'
              )}
            </p>
            {data.pendingRestart ? (
              <p role="status" className="rounded-md border border-warn/30 bg-warn/5 p-3 text-sm">
                Saved. Restart the <strong>web API service</strong> to apply these settings.{' '}
                {data.savedOrigin ? (
                  <>
                    Then open <span className="font-mono">{data.savedOrigin}</span>.
                  </>
                ) : (
                  'The extra HTTPS listener will be disabled.'
                )}{' '}
                The bot restart button below does not apply web settings.
              </p>
            ) : null}
            {!data.editable ? (
              <p className="text-sm text-warn">
                HTTPS is controlled by environment variables. Update those on the server before using this editor.
              </p>
            ) : null}
            <fieldset disabled={!data.editable || saving} className="grid gap-4 sm:grid-cols-2">
              <label className="flex items-center gap-2 text-sm sm:col-span-2">
                <input
                  type="checkbox"
                  checked={value.enabled}
                  onChange={(e) => setDraft((d) => ({ ...d, enabled: e.target.checked }))}
                />
                Enable an additional HTTPS listener after restart
              </label>
              <Field
                label="Hostname"
                htmlFor="web-tls-hostname"
                hint="The exact DNS name or IPv4 address covered by the certificate."
              >
                <Input
                  id="web-tls-hostname"
                  placeholder="mineclaw.example.com"
                  value={value.hostname}
                  onChange={(e) => setDraft((d) => ({ ...d, hostname: e.target.value }))}
                />
              </Field>
              <Field
                label="HTTPS port"
                htmlFor="web-tls-port"
                hint="Use a free port from 1024 to 65535, different from the HTTP port."
              >
                <Input
                  id="web-tls-port"
                  type="number"
                  min={1024}
                  max={65535}
                  value={value.port}
                  onChange={(e) => setDraft((d) => ({ ...d, port: Number(e.target.value) }))}
                />
              </Field>
              <Field
                label="Certificate chain path"
                htmlFor="web-tls-cert"
                hint="Absolute path on the web server to the PEM full chain."
              >
                <Input
                  id="web-tls-cert"
                  value={value.certFile}
                  placeholder="/etc/mineclaw-web/tls/fullchain.pem"
                  onChange={(e) => setDraft((d) => ({ ...d, certFile: e.target.value }))}
                />
              </Field>
              <Field
                label="Private key path"
                htmlFor="web-tls-key"
                hint="Matching unencrypted PEM key, readable only by the service and administrators."
              >
                <Input
                  id="web-tls-key"
                  value={value.keyFile}
                  placeholder="/etc/mineclaw-web/tls/privkey.pem"
                  onChange={(e) => setDraft((d) => ({ ...d, keyFile: e.target.value }))}
                />
              </Field>
            </fieldset>
            <p className="text-xs text-muted">
              Saving checks the files, hostname, expiration and key match. Your browser must trust the certificate
              issuer. The existing HTTP listener stays available for local health checks and recovery.
            </p>
            <ReviewBar
              rows={rows}
              invalid={invalid ? 1 : 0}
              reason={reason}
              onReason={setReason}
              onSave={() => void save()}
              onDiscard={() => {
                setDraft({});
                setError(null);
              }}
              saving={saving}
              error={displayError}
              open={review}
              onOpenChange={setReview}
              description="Save the gateway HTTPS override and keep the previous file as a backup. Apply it by restarting only mineclaw-web-api; this does not restart Minecraft or the bots."
            />
            {!review && error ? <InlineError error={displayError} /> : null}
          </>
        ) : !query.error ? (
          <p className="text-sm text-muted">Loading web settings…</p>
        ) : null}
      </div>
      {stepUp.dialog}
    </Card>
  );
}
