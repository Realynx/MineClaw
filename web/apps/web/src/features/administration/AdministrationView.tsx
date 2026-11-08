'use client';

/**
 * Administration: the bot's own settings on one node (one today), grouped like the bot's registry. Edits collect in a draft;
 * the sticky bar reviews and saves them all-or-nothing behind a step-up; restart settings raise the restart notice.
 */
import { useQueryClient } from '@tanstack/react-query';
import type { AdminSetting, AdminSettingsSaveResponse, SettingValue } from '@mineclaw/contracts';
import { CheckCircle2, Server } from 'lucide-react';
import { useCallback, useMemo, useState } from 'react';
import {
  Badge,
  Card,
  CardHeader,
  ErrorState,
  InlineError,
  PageHeader,
  Select,
  SkeletonLines,
} from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { ago } from '@/lib/utils';
import { AuditCard } from './AuditCard';
import { AdminFormContext, fieldId, type AdminForm } from './context';
import { buildDiff, toChanges, withoutKeys } from './diff';
import { serverErrors, validateDraft, type Draft } from './fieldState';
import { RestartNotice } from './RestartPanel';
import { ReviewBar } from './ReviewBar';
import { SettingField } from './SettingField';
import { StepUpCancelled, useStepUp } from './useStepUp';
import { WebHostingCard } from './WebHostingCard';
import { HumanSkinsCard } from './HumanSkinsCard';

function groupSections(list: readonly AdminSetting[]): Array<[string, AdminSetting[]]> {
  const m = new Map<string, AdminSetting[]>();
  for (const s of list) {
    const arr = m.get(s.section) ?? [];
    arr.push(s);
    m.set(s.section, arr);
  }
  return [...m.entries()];
}

export function AdministrationView() {
  const qc = useQueryClient();
  const nodes = useApi('admin.nodes');
  const [node, setNode] = useState('local');
  const q = useApi('admin.settings', { params: { node } });
  const stepUp = useStepUp();
  const [draft, setDraft] = useState<Draft>({});
  const [reason, setReason] = useState('');
  const [server, setServer] = useState<Record<string, string>>({});
  const [reviewOpen, setReviewOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [lastSave, setLastSave] = useState<AdminSettingsSaveResponse | null>(null);

  const all = useMemo(() => q.data?.settings ?? [], [q.data]);
  const allowedClaude = useMemo(() => q.data?.allowedClaudeModels ?? [], [q.data]);
  const labels = useMemo(() => Object.fromEntries(all.map((s) => [s.key, `${s.section} · ${s.label}`])), [all]);

  const setValue = useCallback((key: string, value: SettingValue | undefined) => {
    setDraft((d) => {
      const next: Record<string, SettingValue> = { ...d };
      if (value === undefined) delete next[key];
      else next[key] = value;
      return next;
    });
    setServer((e) => {
      if (!(key in e)) return e;
      const { [key]: _drop, ...rest } = e;
      void _drop;
      return rest;
    });
  }, []);

  const clientErrors = useMemo(() => validateDraft(all, draft, allowedClaude), [all, draft, allowedClaude]);
  const errors = useMemo(() => ({ ...server, ...clientErrors }), [server, clientErrors]);
  const rows = useMemo(() => buildDiff(all, draft), [all, draft]);

  const form: AdminForm = { node, all, allowedClaude, draft, setValue, errors, stepUp };

  const save = async () => {
    const changes = toChanges(rows, draft);
    setSaving(true);
    setSaveError(null);
    try {
      const res = await stepUp.run(() =>
        api('admin.settingsSave', { params: { node }, body: { changes, reason: reason.trim() } }),
      );
      setLastSave(res);
      setDraft((d) =>
        withoutKeys(
          d,
          changes.map((c) => c.key),
        ),
      );
      setReason('');
      setServer({});
      setReviewOpen(false);
      await qc.invalidateQueries({ queryKey: ['admin.settings'] });
      void qc.invalidateQueries({ queryKey: ['admin.settingsAudit'] });
    } catch (e) {
      if (e instanceof StepUpCancelled) return;
      if (e instanceof ApiError && e.status === 400) {
        const byKey = serverErrors(
          e.details,
          changes.map((c) => c.key),
        );
        setServer(byKey);
        if (Object.keys(byKey).length) setReviewOpen(false);
      }
      setSaveError(e);
    } finally {
      setSaving(false);
    }
  };

  const restartKeys = useMemo(() => {
    const keys = new Set<string>(q.data?.restartPending ?? []);
    for (const k of lastSave?.restartRequired ?? []) keys.add(k);
    return [...keys];
  }, [q.data, lastSave]);

  const nodeList = nodes.data?.nodes ?? (q.data ? [q.data.node] : []);
  const invalid = Object.keys(clientErrors).length;

  return (
    <AdminFormContext.Provider value={form}>
      <PageHeader
        title="Administration"
        subtitle="Configure web access and the bot's settings. Changes are reviewed before saving; secret contents stay on the server."
        actions={<NodePicker node={node} setNode={setNode} nodeList={nodeList} />}
      />
      <WebHostingCard />
      <HumanSkinsCard />
      {q.isPending ? (
        <Card className="p-5">
          <SkeletonLines lines={8} />
        </Card>
      ) : q.error ? (
        <Card>
          <ErrorState error={q.error} onRetry={() => void q.refetch()} title="Could not load the bot's settings" />
        </Card>
      ) : (
        <>
          <NodeSummary data={q.data} />
          {lastSave ? <SavedNote save={lastSave} labels={labels} /> : null}
          <RestartNotice
            node={node}
            keys={restartKeys}
            labels={labels}
            stepUp={stepUp}
            onRestarted={() => setLastSave((x) => (x ? { ...x, restartRequired: [] } : x))}
          />
          <div className="grid grid-cols-1 gap-6 xl:grid-cols-[13rem_minmax(0,1fr)]">
            <nav aria-label="Setting groups" className="hidden xl:block">
              <ul className="sticky top-4 space-y-0.5 text-sm">
                {q.data.groups.map((g) => (
                  <li key={g.id}>
                    <a
                      href={`#group-${g.id}`}
                      className="block rounded-md px-2.5 py-1.5 text-muted hover:bg-surface-2 hover:text-fg"
                    >
                      {g.title}
                    </a>
                  </li>
                ))}
                <li>
                  <a
                    href="#audit"
                    className="block rounded-md px-2.5 py-1.5 text-muted hover:bg-surface-2 hover:text-fg"
                  >
                    Audit
                  </a>
                </li>
              </ul>
            </nav>
            <div className="min-w-0 space-y-4">
              {q.data.groups.map((g) => {
                const list = all.filter((s) => s.group === g.id);
                if (!list.length) return null;
                return (
                  <Card key={g.id} id={`group-${g.id}`} role="region" aria-label={g.title} className="scroll-mt-4">
                    <CardHeader title={g.title} subtitle={g.description} />
                    {groupSections(list).map(([section, items]) => (
                      <section
                        key={section}
                        aria-labelledby={`${fieldId(g.id + section)}-h`}
                        className="border-b border-border last:border-0"
                      >
                        <h3
                          id={`${fieldId(g.id + section)}-h`}
                          className="bg-surface-2/50 px-4 py-1.5 text-[11px] font-semibold tracking-wide text-subtle uppercase"
                        >
                          {section}
                        </h3>
                        <div className="divide-y divide-border/70">
                          {items.map((s) => (
                            <SettingField key={s.key} s={s} />
                          ))}
                        </div>
                      </section>
                    ))}
                  </Card>
                );
              })}
              <AuditCard node={node} labels={labels} />
              <ReviewBar
                rows={rows}
                invalid={invalid}
                reason={reason}
                onReason={setReason}
                onSave={() => void save()}
                onDiscard={() => {
                  setDraft({});
                  setServer({});
                  setSaveError(null);
                }}
                saving={saving}
                error={saveError}
                open={reviewOpen}
                onOpenChange={setReviewOpen}
              />
              {!reviewOpen && saveError ? <InlineError error={saveError} /> : null}
            </div>
          </div>
        </>
      )}
      {stepUp.dialog}
    </AdminFormContext.Provider>
  );
}

/** Which node's settings (a choice only when there are several). */
function NodePicker({ node, setNode, nodeList }: { node: string; setNode: (n: string) => void; nodeList: ReadonlyArray<{ id: string; name: string }> }) {
  return (
    <label className="flex items-center gap-2 text-xs text-muted">
      <Server className="size-3.5" aria-hidden />
      <span>Node</span>
      <Select aria-label="Node" value={node} onChange={(e) => setNode(e.target.value)} disabled={nodeList.length <= 1} className="h-8 min-w-36 text-xs">
        {nodeList.length ? (
          nodeList.map((n) => (
            <option key={n.id} value={n.id}>
              {n.name} ({n.id})
            </option>
          ))
        ) : (
          <option value="local">local</option>
        )}
      </Select>
    </label>
  );
}

/** Saved: what runs now, and where the previous file was kept. */
function SavedNote({ save, labels }: { save: AdminSettingsSaveResponse; labels: Record<string, string> }) {
  return (
    <p role="status" className="mb-4 flex flex-wrap items-center gap-2 text-sm text-ok">
      <CheckCircle2 className="size-4" /> Saved.
      {save.applied.length ? <span className="text-muted">Running now: {save.applied.map((k) => labels[k] ?? k).join(', ')}.</span> : null}
      {save.backup ? <span className="text-xs text-subtle">Previous file kept as {save.backup}.</span> : null}
    </p>
  );
}

function NodeSummary({ data }: { data: NonNullable<ReturnType<typeof useApi<'admin.settings'>>['data']> }) {
  const n = data.node;
  return (
    <div className="mb-4 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted">
      <span className="inline-flex items-center gap-1.5">
        <span className={n.reachable === false ? 'text-danger' : 'text-ok'}>●</span> {n.name}
      </span>
      {n.version ? <span>version {n.version}</span> : null}
      {n.gameVersion ? <span>{n.gameVersion}</span> : null}
      {n.startedAt ? <span>started {ago(n.startedAt)}</span> : null}
      {n.supervisorUrl ? <span className="font-mono">{n.supervisorUrl}</span> : null}
      <span className="inline-flex items-center gap-1.5">
        override file <span className="font-mono">{data.override.path}</span>
        {data.override.exists ? (
          <Badge tone="accent">{data.override.backups} backups</Badge>
        ) : (
          <Badge>not created yet</Badge>
        )}
      </span>
    </div>
  );
}
