'use client';

import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Badge, Button, Card, CardHeader, InlineError, KeyValues } from '@/components/ui/primitives';
import { api } from '@/lib/api/client';
import { qk, useApi } from '@/lib/api/query';
import { ago, dateTime } from '@/lib/utils';
import { RunStatus, UpdateNowButton, UpgradeCommand, useUpdateStatus } from '@/features/system/update';

const KIND_TEXT = {
  systemd: 'Linux service (systemd)',
  docker: 'Docker Compose',
  helm: 'Kubernetes (Helm)',
  manual: 'Manual install',
} as const;

/** Administration → Version and updates: what runs here, the newest release and how to update. */
export function UpdatesCard() {
  const qc = useQueryClient();
  const version = useApi('system.version', {}, { staleTime: Infinity });
  const q = useUpdateStatus(true);
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const s = q.data;
  const v = version.data;
  const check = async () => {
    setChecking(true);
    setError(null);
    try {
      qc.setQueryData(qk('system.update'), await api('system.updateCheck'));
    } catch (e) {
      setError(e);
    } finally {
      setChecking(false);
    }
  };
  const latest = s?.latest;
  return (
    <Card id="updates" className="mb-5" role="region" aria-label="Version and updates">
      <CardHeader
        title="Version and updates"
        subtitle={s?.enabled === false ? 'Update checks are turned off (Web:Updates:Enabled).' : 'Checks GitHub for new MineClaw releases.'}
        actions={
          s?.enabled ? (
            <Button size="sm" loading={checking} onClick={() => void check()}>
              Check now
            </Button>
          ) : null
        }
      />
      <div className="space-y-3 p-4">
        <KeyValues
          rows={[
            [
              'Running',
              v ? (
                <span>
                  v{v.version}
                  {v.commit ? <span className="ml-2 font-mono text-xs text-muted">{v.commit.slice(0, 12)}</span> : null}
                </span>
              ) : null,
            ],
            ['Installed as', v ? KIND_TEXT[v.installKind] : null],
            [
              'Latest release',
              s?.enabled ? (
                latest ? (
                  <span className="inline-flex flex-wrap items-center gap-2">
                    <a href={latest.url} target="_blank" rel="noopener noreferrer" className="text-accent hover:underline">
                      v{latest.version}
                    </a>
                    {latest.publishedAt ? <span className="text-xs text-muted">{dateTime(latest.publishedAt)}</span> : null}
                    {s.outdated ? <Badge tone="accent">Update available</Badge> : <Badge tone="ok">Up to date</Badge>}
                  </span>
                ) : (
                  <span className="text-muted">{s.checkedAt ? 'No release found' : 'Not checked yet'}</span>
                )
              ) : null,
            ],
            ['Last checked', s?.checkedAt ? ago(s.checkedAt) : null],
          ]}
        />
        {s?.lastError ? <p className="text-sm text-warn">Last check failed: {s.lastError}</p> : null}
        {s ? <RunStatus status={s} /> : null}
        {s?.outdated && s.update.mode === 'trigger' ? <UpdateNowButton status={s} size="md" /> : null}
        {s?.outdated && s.update.mode === 'command' && s.update.command ? (
          <div className="space-y-1">
            <p className="text-sm text-muted">
              Run this on the {s.installKind === 'helm' ? 'machine with cluster access' : 'Docker host'} to update:
            </p>
            <UpgradeCommand command={s.update.command} />
          </div>
        ) : null}
        {s?.outdated && s.update.mode === 'none' ? (
          <p className="text-sm text-muted">This install was set up by hand: update it the same way.</p>
        ) : null}
        {q.error ? <InlineError error={q.error} /> : null}
        {error ? <InlineError error={error} /> : null}
      </div>
    </Card>
  );
}
