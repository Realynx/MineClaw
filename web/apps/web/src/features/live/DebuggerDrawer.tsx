'use client';

/**
 * Live script debugger detail (one click from the script card). It polls `GET /api/bodies/:body/script` only while open
 * (the bot keeps fast sampling on through that lease; there is no stream topic for it). Calmer than the desktop panel
 * (explorer-spec §17.4): a status header, the source with the running line, and the state behind tabs; no dead Pause button
 * (shown only when the bot offers controls).
 */
import type { ScriptDebugSnapshot } from '@mineclaw/contracts';
import { AlertTriangle, CheckCircle2, PauseCircle, PlayCircle } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useApi } from '@/lib/api/query';
import { useTicker } from '@/lib/live/ticker';
import { usePrefs } from '@/lib/prefs';
import { cn, duration, fixed, toMs } from '@/lib/utils';
import { Badge, Button, EmptyState, ErrorState, KeyValues, Meter, SkeletonLines } from '@/components/ui/primitives';
import { Drawer, TabPanel, Tabs } from '@/components/ui/overlays';
import { CodeView } from '@/components/code/CodeView';

function preview(v: unknown): string {
  const s = typeof v === 'string' ? v : JSON.stringify(v);
  return s.length > 120 ? `${s.slice(0, 117)}…` : s;
}

function StatusLine({ s }: { s: ScriptDebugSnapshot }) {
  const Icon = s.status === 'paused' ? PauseCircle : s.status === 'failed' ? AlertTriangle : s.status === 'done' ? CheckCircle2 : PlayCircle;
  const tone = s.status === 'failed' ? 'text-danger' : s.status === 'paused' ? 'text-warn' : s.status === 'done' ? 'text-ok' : 'text-info';
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
      <span className={cn('inline-flex items-center gap-1.5 font-medium', tone)}>
        <Icon className="size-4" />
        {s.pausePending ? 'pausing at the next safe point' : s.status}
      </span>
      {s.line ? <span className="tabular text-muted">line {s.line}</span> : null}
      {s.action ? (
        <span className="text-muted">
          {s.action.label}
          {s.action.args ? <span className="text-subtle"> {s.action.args}</span> : null}
          <span className="tabular text-subtle"> · {fixed(s.action.seconds, 1)} s</span>
        </span>
      ) : null}
      {s.patching ? <Badge tone="warn">patching · {s.patching}</Badge> : null}
    </div>
  );
}

export function DebuggerDrawer({ open, onOpenChange, body }: { open: boolean; onOpenChange: (o: boolean) => void; body: string }) {
  const { devDetails } = usePrefs();
  const [tab, setTab] = useState('state');
  // ?code=1 once per run (keyed session:name:version), then without.
  const codeKey = useRef<string | null>(null);
  const [wantCode, setWantCode] = useState(true);
  const q = useApi('bodies.script', { params: { body }, query: wantCode ? { code: true } : {} }, { enabled: open, refetchInterval: open ? 1000 : false, refetchIntervalInBackground: false });
  const [code, setCode] = useState<string | null>(null);
  const s = q.data?.snapshot;
  useEffect(() => {
    if (!s) return;
    const key = `${s.sessionId}:${s.name}:${s.version ?? 0}`;
    if (s.code) {
      setCode(s.code);
      codeKey.current = key;
      setWantCode(false);
    } else if (codeKey.current !== key) {
      setWantCode(true);
    }
  }, [s]);
  const now = useTicker(1000, open);
  const age = s ? (now - (toMs(s.at) ?? now)) / 1000 : 0;
  const stale = age > 4;

  return (
    <Drawer
      open={open}
      onOpenChange={onOpenChange}
      title={s ? `${s.name}${s.version ? ` v${s.version}` : ''}` : 'Script debugger'}
      subtitle={s ? [body, s.role ? `role ${s.role}` : null, s.crew ? `crew ${s.crew}` : null].filter(Boolean).join(' · ') : body}
      actions={stale && s ? <Badge tone="warn">stale {Math.round(age)} s</Badge> : null}
      width="xl"
    >
      {q.isPending ? (
        <SkeletonLines lines={10} className="p-5" />
      ) : q.error ? (
        <ErrorState error={q.error} onRetry={() => void q.refetch()} />
      ) : !s ? (
        <EmptyState title="No script is running" hint={q.data.note ?? undefined} />
      ) : (
        <div className="grid h-full min-h-0 grid-cols-1 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]">
          <div className="flex min-h-0 flex-col border-border lg:border-r">
            <div className="space-y-3 border-b border-border p-5">
              <StatusLine s={s} />
              {s.paused && s.pauseReason ? (
                <p className="text-xs text-warn">
                  Paused ({s.pauseKind}): {s.pauseReason}
                  {s.pausedSeconds ? ` · ${duration(Number(s.pausedSeconds))}` : ''}
                </p>
              ) : null}
              {s.progressPct !== null && s.progressPct !== undefined ? (
                <div>
                  <div className="mb-1 flex justify-between text-xs">
                    <span className="text-muted">{s.progressNote ?? 'Progress'}</span>
                    <span className="tabular text-fg">{fixed(s.progressPct, 0)}%</span>
                  </div>
                  <Meter value={Number(s.progressPct)} max={100} label="progress" />
                </div>
              ) : null}
              <div className="flex flex-wrap gap-x-5 gap-y-1 text-xs text-muted">
                <span>
                  Elapsed <span className="tabular text-fg">{duration(Number(s.elapsedSeconds))}</span>
                </span>
                <span>
                  Actions <span className="tabular text-fg">{s.actions}</span>
                </span>
                {s.checkpoint ? (
                  <span>
                    Checkpoint <span className="text-fg">{s.checkpoint}</span> ({s.checkpointsPassed}/{s.checkpointTotal})
                  </span>
                ) : null}
              </div>
              {s.lastFailure ? <p className="text-xs text-danger">Last failure: {s.lastFailure}</p> : null}
              {s.controls.length ? (
                <div className="flex gap-2">
                  {s.controls.map((c) => (
                    <Button key={c} size="sm">
                      {c}
                    </Button>
                  ))}
                </div>
              ) : null}
            </div>
            <div className="min-h-64 flex-1">
              {code ? <CodeView code={code} lang={s.lang} runningLine={s.line || null} className="h-full" /> : <SkeletonLines lines={10} className="p-5" />}
            </div>
          </div>
          <Tabs
            value={tab}
            onValueChange={setTab}
            listClassName="px-4"
            tabs={[
              { value: 'state', label: 'State' },
              { value: 'activity', label: 'Activity' },
              { value: 'stack', label: 'Stack' },
              { value: 'crew', label: 'Crew', hidden: s.lanes.length === 0 },
            ]}
          >
            <TabPanel value="state" className="overflow-auto p-5">
              {s.machine ? (
                <section className="mb-5">
                  <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">State machine</h3>
                  <p className="mb-2 text-sm">
                    <span className="font-medium text-fg">{s.machine.state}</span>
                    <span className="text-subtle"> · {s.machine.transitions} transitions</span>
                  </p>
                  <ul className="space-y-1 text-xs">
                    {[...s.machine.history].reverse().slice(0, 8).map((h, i) => (
                      <li key={i} className="flex gap-2 text-muted">
                        <span className="text-fg">
                          {h.from} → {h.to}
                        </span>
                        <span className="text-subtle">({h.why})</span>
                      </li>
                    ))}
                  </ul>
                </section>
              ) : null}
              <section className="mb-5">
                <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Blackboard</h3>
                {s.machine?.blackboard && typeof s.machine.blackboard === 'object' ? (
                  <KeyValues className="font-mono text-xs" rows={Object.entries(s.machine.blackboard as Record<string, unknown>).map(([k, v]) => [k, preview(v)])} />
                ) : (
                  <p className="text-xs text-subtle">Empty</p>
                )}
              </section>
              <section className="mb-5">
                <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Params</h3>
                <KeyValues
                  className="font-mono text-xs"
                  rows={[
                    ...Object.entries((s.params as Record<string, unknown> | null) ?? {}).map(([k, v]): [string, string] => [k, preview(v)]),
                    ...Object.entries((s.args as Record<string, unknown> | null) ?? {}).map(([k, v]): [string, string] => [`${k} (arg)`, preview(v)]),
                  ]}
                />
              </section>
              {s.locals?.length ? (
                <section>
                  <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">
                    Locals {s.localsAgeSeconds ? <span className="font-normal normal-case">· sampled {fixed(s.localsAgeSeconds, 1)} s ago</span> : null}
                  </h3>
                  <KeyValues className="font-mono text-xs" rows={s.locals.map((l) => [`${l.name}${devDetails ? ` (${l.scope})` : ''}`, l.value])} />
                </section>
              ) : null}
            </TabPanel>
            <TabPanel value="activity" className="overflow-auto p-5">
              <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Time per action</h3>
              <table className="mb-5 w-full text-xs">
                <thead className="text-left text-subtle">
                  <tr>
                    <th className="py-1 font-medium">Action</th>
                    <th className="py-1 text-right font-medium">Calls</th>
                    <th className="py-1 text-right font-medium">Failed</th>
                    <th className="py-1 text-right font-medium">Total</th>
                    <th className="py-1 text-right font-medium">Avg</th>
                  </tr>
                </thead>
                <tbody className="tabular">
                  {s.timings.map((t) => (
                    <tr key={t.action} className="border-t border-border">
                      <td className="py-1.5 text-fg">{t.action}</td>
                      <td className="py-1.5 text-right text-muted">{t.calls}</td>
                      <td className={cn('py-1.5 text-right', t.fails ? 'text-danger' : 'text-muted')}>{t.fails}</td>
                      <td className="py-1.5 text-right text-muted">{duration(Number(t.seconds))}</td>
                      <td className="py-1.5 text-right text-muted">{fixed(t.avgMs, 0)} ms</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Log</h3>
              <ul className="space-y-1 font-mono text-xs text-muted">
                {[...s.logs].reverse().map((l, i) => (
                  <li key={i}>{l}</li>
                ))}
              </ul>
            </TabPanel>
            <TabPanel value="stack" className="overflow-auto p-5">
              <ul className="space-y-1.5 font-mono text-xs">
                {s.stack.map((f, i) => (
                  <li key={i} className={cn('flex gap-2', f.script ? 'text-fg' : 'text-subtle')}>
                    <span>{f.function}</span>
                    <span className="text-subtle">
                      {f.file}:{f.line}
                    </span>
                  </li>
                ))}
              </ul>
            </TabPanel>
            <TabPanel value="crew" className="overflow-auto p-5">
              <p className="mb-3 text-sm text-muted">
                {s.crew ?? 'Crew'} · coordinated by {s.coordinator ?? '—'}
              </p>
              <ul className="space-y-1 text-sm">
                {s.lanes.map((l) => (
                  <li key={l.id} className="flex gap-2">
                    <span className="tabular text-subtle">lane {l.id}</span>
                    <span className="text-fg">{l.body}</span>
                    <Badge>{l.status}</Badge>
                  </li>
                ))}
              </ul>
            </TabPanel>
          </Tabs>
        </div>
      )}
    </Drawer>
  );
}
