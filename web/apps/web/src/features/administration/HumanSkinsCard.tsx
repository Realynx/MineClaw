'use client';

import { useState } from 'react';
import { Card, CardHeader, Field, Input, InlineError, Button } from '@/components/ui/primitives';
import { useApi } from '@/lib/api/query';
import { api } from '@/lib/api/client';
import { StepUpCancelled, useStepUp } from './useStepUp';

export function HumanSkinsCard() {
  const query = useApi('admin.humanSkins');
  const stepUp = useStepUp();
  const [draft, setDraft] = useState<boolean | null>(null);
  const [reason, setReason] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const enabled = draft ?? query.data?.enabled ?? false;
  const save = async () => {
    setSaving(true); setError(null);
    try {
      await stepUp.run(() => api('admin.humanSkinsSave', { body: { enabled, reason: reason.trim() } }));
      setDraft(null); setReason(''); await query.refetch();
    } catch (e) { if (!(e instanceof StepUpCancelled)) setError(e); }
    finally { setSaving(false); }
  };
  return <Card id="human-skins" className="mb-5" role="region" aria-label="Player skins">
    <CardHeader title="Player skins" subtitle="Show Minecraft account skins in Controller and other browser world views." />
    <div className="space-y-3 p-4">
      {query.error ? <InlineError error={query.error} /> : null}
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={enabled} disabled={!query.data || saving} onChange={(e) => setDraft(e.target.checked)} /> Enable player skins</label>
      <p className="text-sm text-muted">Skins load when the web service starts and once when each new player joins. They stay in memory until the web service restarts. Accounts without an available skin use the default appearance.</p>
      <p className="text-sm text-muted">Only human browser views show these skins. AI vision always uses default player textures.</p>
      {query.data ? <p className="text-xs text-muted">{query.data.cached} skins loaded · {query.data.unavailable} unavailable. Changes appear in world views within a few seconds.</p> : null}
      {draft !== null && enabled !== query.data?.enabled ? <>
        <Field label="Reason for change"><Input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} placeholder="For the administration audit log" /></Field>
        <Button disabled={saving || reason.trim().length < 3} onClick={() => void save()}>{saving ? 'Saving…' : 'Save skin setting'}</Button>
      </> : null}
      {error ? <InlineError error={error} /> : null}
    </div>
    {stepUp.dialog}
  </Card>;
}
