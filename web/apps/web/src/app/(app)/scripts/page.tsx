'use client';

import { toNumber, type ScriptLibraryEntry, type ScriptSource } from '@mineclaw/contracts';
import { Bug, Code2, Copy, Search } from 'lucide-react';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useMemo, useState } from 'react';
import { Badge, Button, Card, EmptyState, ErrorState, Input, Select, Skeleton, SkeletonLines } from '@/components/ui/primitives';
import { Segmented, TabPanel, Tabs } from '@/components/ui/overlays';
import { CodeView } from '@/components/code/CodeView';
import { DebuggerDrawer } from '@/features/live/DebuggerDrawer';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { ago, cn, copyText, dateTime, duration, pct } from '@/lib/utils';

function LintChip({ s }: { s: ScriptLibraryEntry }) {
  if (s.lint.status === 'errors') return <Badge tone="danger">{s.lint.errors} error{s.lint.errors === 1 ? '' : 's'}</Badge>;
  if (s.lint.status === 'warnings') return <Badge tone="warn">{s.lint.warnings} warning{s.lint.warnings === 1 ? '' : 's'}</Badge>;
  if (s.lint.status === 'ok') return <Badge tone="ok">lint ok</Badge>;
  return null;
}

function DiffView({ name, source, versions, current }: { name: string; source: ScriptSource; versions: number[]; current: number }) {
  const [a, setA] = useState(versions.find((v) => v < current) ?? current);
  const diff = useApi('scripts.diff', { params: { name }, query: { source, a, b: current } }, { enabled: a !== current });
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2.5 text-sm">
        <label htmlFor="cmp" className="text-muted">
          Compare
        </label>
        <Select id="cmp" value={a} onChange={(e) => setA(Number(e.target.value))} className="h-8">
          {versions
            .filter((v) => v !== current)
            .map((v) => (
              <option key={v} value={v}>
                v{v}
              </option>
            ))}
        </Select>
        <span className="text-muted">→ v{current}</span>
        {diff.data ? (
          <>
            <span className="tabular text-xs">
              <span className="text-ok">+{diff.data.added}</span> <span className="text-danger">−{diff.data.removed}</span>
            </span>
            <Button size="sm" variant="ghost" className="ml-auto" onClick={() => void copyText(diff.data.unified)}>
              <Copy className="size-3.5" /> Copy diff
            </Button>
          </>
        ) : null}
      </div>
      <div className="min-h-0 flex-1 overflow-auto bg-code font-mono text-[12.5px] leading-relaxed">
        {a === current ? (
          <p className="p-5 font-sans text-sm text-muted">There is only one version.</p>
        ) : diff.isPending ? (
          <SkeletonLines lines={10} className="p-5" />
        ) : diff.error ? (
          <ErrorState error={diff.error} />
        ) : diff.data.hunks.length === 0 ? (
          <p className="p-5 font-sans text-sm text-muted">No differences.</p>
        ) : (
          diff.data.hunks.map((h, i) => (
            <div key={i} className="border-b border-border">
              <div className="bg-info-soft px-4 py-1 text-[11px] text-info">
                @@ −{h.aStart},{h.aLines} +{h.bStart},{h.bLines} @@
              </div>
              {h.lines.map((l, j) => (
                <div key={j} className={cn('flex', l.op === '+' ? 'bg-ok-soft' : l.op === '-' ? 'bg-danger-soft' : '')}>
                  <span className="tabular w-10 shrink-0 pr-2 text-right text-subtle select-none">{l.aLine ?? ''}</span>
                  <span className="tabular w-10 shrink-0 pr-2 text-right text-subtle select-none">{l.bLine ?? ''}</span>
                  <span className={cn('w-4 shrink-0 select-none', l.op === '+' ? 'text-ok' : l.op === '-' ? 'text-danger' : 'text-subtle')}>{l.op}</span>
                  <span className="pr-4 whitespace-pre text-fg">{l.text}</span>
                </div>
              ))}
            </div>
          ))
        )}
      </div>
    </div>
  );
}

function Detail({ name, source, v, onVersion }: { name: string; source: ScriptSource; v: number | undefined; onVersion: (v: number) => void }) {
  const [tab, setTab] = useState('source');
  const [debugBody, setDebugBody] = useState<string | null>(null);
  const d = useApi('scripts.get', { params: { name }, query: { source, ...(v ? { v } : {}) } });
  const running = useApi('bodies.scripts', {}, { staleTime: 15_000 });
  const runningHere = (running.data?.bodies ?? []).filter((b) => b.script === name);
  const lint = useMemo(() => (d.data?.lint.findings ?? []).map((f) => ({ line: f.line, severity: f.severity, message: `${f.rule}: ${f.message}` })), [d.data]);

  if (d.isPending) return <SkeletonLines lines={12} className="p-6" />;
  if (d.error) return <ErrorState error={d.error} onRetry={() => void d.refetch()} />;
  const s = d.data;
  const handlers = s.script.handlers ?? [];
  return (
    <div className="flex h-full min-h-0 flex-col">
      <ScriptHeader s={s} runningHere={runningHere} onDebug={setDebugBody} />
      <Tabs
        value={tab}
        onValueChange={setTab}
        className="min-h-0 flex-1"
        listClassName="px-3"
        tabs={[
          { value: 'source', label: 'Source' },
          { value: 'history', label: 'History', count: s.history.length },
          { value: 'diff', label: 'Diff', hidden: s.history.length < 2 },
          { value: 'params', label: 'Params & lint' },
          { value: 'runs', label: 'Runs', count: s.runs.length },
          { value: 'handlers', label: 'Handlers', count: handlers.length, hidden: !handlers.length },
        ]}
      >
        <TabPanel value="source" className="relative min-h-0">
          <Button size="sm" variant="secondary" className="absolute top-2 right-4 z-10" onClick={() => void copyText(s.code)}>
            <Copy className="size-3.5" /> Copy
          </Button>
          <CodeView code={s.code} lang={s.version.lang} lint={lint} className="h-full" />
        </TabPanel>
        <TabPanel value="history" className="overflow-auto">
          <HistoryTab s={s} onVersion={onVersion} />
        </TabPanel>
        <TabPanel value="diff" className="min-h-0">
          <DiffView name={name} source={source} versions={s.history.map((h) => h.v)} current={s.version.v} />
        </TabPanel>
        <TabPanel value="params" className="overflow-auto p-5">
          <ParamsTab s={s} />
        </TabPanel>
        <TabPanel value="runs" className="overflow-auto">
          <RunsTab s={s} />
        </TabPanel>
        <TabPanel value="handlers" className="overflow-auto p-5">
          <HandlersTab handlers={handlers} />
        </TabPanel>
      </Tabs>
      {debugBody ? <DebuggerDrawer open onOpenChange={(o) => !o && setDebugBody(null)} body={debugBody} /> : null}
    </div>
  );
}

type Script = NonNullable<ReturnType<typeof useApi<'scripts.get'>>['data']>;
type RunningBody = NonNullable<ReturnType<typeof useApi<'bodies.scripts'>>['data']>['bodies'][number];

/** The script's name, version, language, lint and state badges, purpose, provenance, notes, and the bodies running it now. */
function ScriptHeader({ s, runningHere, onDebug }: { s: Script; runningHere: readonly RunningBody[]; onDebug: (body: string) => void }) {
  return (
  <div className="space-y-2 border-b border-border px-5 py-4">
    <div className="flex flex-wrap items-center gap-2">
      <h2 className="text-lg font-semibold text-fg">{s.script.name}</h2>
      <Badge>
        v{s.version.v} of {s.script.versions}
      </Badge>
      <Badge>{s.version.lang}</Badge>
      <LintChip s={s.script} />
      {s.script.archived ? <Badge tone="warn">archived</Badge> : null}
      {s.script.promotedAs ? <Badge tone="accent">{s.script.promotedAs}</Badge> : null}
    </div>
    {s.script.purpose ? <p className="text-sm text-muted">{s.script.purpose}</p> : null}
    <p className="text-xs text-subtle">
      by {s.version.author ?? s.script.authorKind} · saved {ago(s.version.savedAt)} · {s.version.runs} runs, {s.version.successes} ok
      {s.script.usedBy.length ? ` · used by ${s.script.usedBy.join(', ')}` : ''}
    </p>
    {s.revertNote ? <p className="text-xs text-warn">{s.revertNote}</p> : null}
    {s.script.migration ? <p className="text-xs text-info">{s.script.migration}</p> : null}
    {runningHere.length ? (
      <div className="flex flex-wrap items-center gap-2 pt-1">
        <span className="text-xs text-muted">Running now:</span>
        {runningHere.map((b) => (
          <Button key={b.body} size="sm" variant="subtle" onClick={() => onDebug(b.body)}>
            <Bug className="size-3.5" /> {b.body}
            {b.line ? <span className="tabular text-subtle">L{b.line}</span> : null}
          </Button>
        ))}
      </div>
    ) : null}
  </div>
  );
}

function HistoryTab({ s, onVersion }: { s: Script; onVersion: (v: number) => void }) {
  return (
    <ul className="divide-y divide-border">
      {s.history.map((h) => (
        <li key={h.v}>
          <button onClick={() => onVersion(h.v)} className={cn('flex w-full items-center gap-3 px-5 py-3 text-left hover:bg-surface-2', h.v === s.version.v && 'bg-accent-soft')}>
            <span className="w-10 font-medium text-fg">v{h.v}</span>
            <Badge>{h.lang}</Badge>
            {h.active ? <Badge tone="ok">active</Badge> : null}
            <span className="min-w-0 flex-1 truncate text-xs text-muted">
              {h.author ?? h.authorKind} · saved {dateTime(h.savedAt)} · {h.successes}/{h.runs} ok · {duration(toNumber(h.avgSeconds))} avg · {h.lines} lines
              {h.topFailures.length ? ` · ${h.topFailures[0]}` : ''}
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}

/** The declared parameters (default and tuned) and the lint findings. */
function ParamsTab({ s }: { s: Script }) {
  return (
    <>
      <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Parameters</h3>
      {s.params.length ? (
        <table className="mb-6 w-full max-w-lg text-sm">
          <thead className="text-left text-xs text-subtle">
            <tr>
              <th className="pb-1.5 font-medium">Name</th>
              <th className="pb-1.5 font-medium">Default</th>
              <th className="pb-1.5 font-medium">Tuned</th>
            </tr>
          </thead>
          <tbody className="font-mono text-xs">
            {s.params.map((p) => (
              <tr key={p.name} className="border-t border-border">
                <td className="py-1.5 text-fg">{p.name}</td>
                <td className="py-1.5 text-muted">{p.default ?? '—'}</td>
                <td className="py-1.5 text-accent">{p.tuned ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <p className="mb-6 text-sm text-muted">No declared parameters.</p>
      )}
      <h3 className="mb-2 text-xs font-semibold tracking-wide text-subtle uppercase">Lint</h3>
      {s.lint.findings.length ? (
        <ul className="space-y-1.5 text-sm">
          {s.lint.findings.map((f, i) => (
            <li key={i} className="flex gap-2">
              <Badge tone={f.severity === 'error' ? 'danger' : 'warn'}>L{f.line}</Badge>
              <span className="text-fg">{f.message}</span>
              <span className="text-xs text-subtle">{f.rule}</span>
            </li>
          ))}
        </ul>
      ) : (
        <p className="text-sm text-ok">No findings.</p>
      )}
      {s.lint.note ? <p className="mt-2 text-xs text-subtle">{s.lint.note}</p> : null}
    </>
  );
}

function RunsTab({ s }: { s: Script }) {
  const { devDetails } = usePrefs();
  return (
    <table className="w-full text-sm">
      <thead className="sticky top-0 bg-surface text-left text-xs text-subtle">
        <tr>
          <th className="px-5 py-2 font-medium">When</th>
          <th className="py-2 font-medium">Result</th>
          <th className="py-2 text-right font-medium">Time</th>
          <th className="py-2 pr-5 pl-4 font-medium">Outcome</th>
        </tr>
      </thead>
      <tbody>
        {s.runs.map((r, i) => (
          <tr key={i} className="border-t border-border align-top">
            <td className="px-5 py-2 whitespace-nowrap text-muted">{ago(r.at)}</td>
            <td className="py-2">
              <Badge tone={r.ok ? 'ok' : 'danger'}>{r.ok ? 'ok' : (r.status ?? 'failed')}</Badge>
            </td>
            <td className="tabular py-2 text-right text-muted">{duration(toNumber(r.seconds))}</td>
            <td className="py-2 pr-5 pl-4 text-xs text-muted">
              {r.outcome}
              {r.failLine ? ` (line ${r.failLine})` : ''}
              {devDetails ? (
                <span className="block text-subtle">
                  {r.actions ?? 0} actions · {r.body ?? ''} · {r.params ?? ''} {r.timings ? `· ${r.timings}` : ''}
                </span>
              ) : null}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The failure handlers the script learned: what they catch, what they do, how they did. */
function HandlersTab({ handlers }: { handlers: NonNullable<Script['script']['handlers']> }) {
  return (
    <ul className="space-y-3">
      {handlers.map((h) => (
        <li key={h.signature} className={cn('rounded-lg border border-border p-4', h.demoted && 'opacity-60')}>
          <div className="mb-1 flex flex-wrap items-center gap-2">
            <Badge tone="accent">{h.kind}</Badge>
            <span className="text-sm font-medium text-fg">{h.signature.split('|')[1]}</span>
            <span className="text-xs text-subtle">then {h.then}</span>
            <span className="ml-auto text-xs text-muted">
              {h.successes} ok / {h.failures} failed
            </span>
          </div>
          <p className="text-xs text-muted">
            {h.note ? `${h.note} · ` : ''}learned on v{h.version} {ago(h.learnedAt)} · last used {ago(h.lastUsed)} · where {h.signature.split('|')[2]}
          </p>
          <pre className="mt-2 rounded-md bg-code p-2 font-mono text-xs text-fg">{h.js}</pre>
        </li>
      ))}
    </ul>
  );
}

function ScriptsInner() {
  const params = useSearchParams();
  const router = useRouter();
  const source = (params.get('source') as ScriptSource | null) ?? 'live';
  const name = params.get('name');
  const v = params.get('v') ? Number(params.get('v')) : undefined;
  const [filter, setFilter] = useState('');
  const lib = useApi('scripts.list', { query: { source } });
  const set = (patch: Record<string, string | null>) => {
    const sp = new URLSearchParams(params.toString());
    for (const [k, val] of Object.entries(patch)) {
      if (!val) sp.delete(k);
      else sp.set(k, val);
    }
    router.replace(`/scripts?${sp.toString()}`, { scroll: false });
  };
  const scripts = useMemo(() => {
    const f = filter.toLowerCase();
    return (lib.data?.scripts ?? []).filter((s) =>
      !f ? true : [s.name, s.purpose ?? '', s.author ?? '', ...s.usedBy, ...(s.handlers ?? []).map((h) => h.kind)].join(' ').toLowerCase().includes(f),
    );
  }, [lib.data, filter]);
  const all = lib.data?.scripts ?? [];
  const lua = all.filter((s) => s.lang === 'lua').length;

  return (
    <div className="flex h-full min-h-0 flex-col lg:flex-row">
      <aside className="flex max-h-[45vh] shrink-0 flex-col border-b border-border bg-surface lg:max-h-none lg:w-96 lg:border-r lg:border-b-0">
        <div className="space-y-2.5 border-b border-border p-4">
          <div className="flex items-center justify-between">
            <h1 className="text-[15px] font-semibold text-fg">Scripts</h1>
            <Segmented<ScriptSource>
              label="Source"
              size="sm"
              value={source}
              onChange={(s) => set({ source: s === 'live' ? null : s, v: null })}
              options={[
                { value: 'live', label: 'Live bot', title: 'The running library, real lint' },
                { value: 'db', label: 'Database', title: 'Offline lint: game functions not checked' },
              ]}
            />
          </div>
          <div className="relative">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-subtle" />
            <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter by name, purpose, user…" aria-label="Filter scripts" className="h-8 pl-8" />
          </div>
          {lib.data ? (
            <p className="text-xs text-muted">
              {all.length} saved scripts · {all.length - lua} JS, {lua} Lua · {all.filter((s) => s.usedBy.length).length} in use
            </p>
          ) : null}
        </div>
        <ul className="min-h-0 flex-1 overflow-y-auto" aria-label="Scripts">
          {lib.isPending ? (
            <li className="space-y-2 p-4">
              {Array.from({ length: 6 }, (_, i) => (
                <Skeleton key={i} className="h-14" />
              ))}
            </li>
          ) : lib.error ? (
            <li>
              <ErrorState error={lib.error} onRetry={() => void lib.refetch()} />
            </li>
          ) : (
            scripts.map((s) => (
              <li key={s.name}>
                <button
                  onClick={() => set({ name: s.name, v: null })}
                  aria-current={s.name === name ? 'true' : undefined}
                  className={cn('block w-full border-b border-border px-4 py-3 text-left', s.name === name ? 'bg-accent-soft' : 'hover:bg-surface-2', s.archived && 'opacity-60')}
                >
                  <div className="flex items-center gap-2">
                    <Code2 className="size-3.5 text-subtle" />
                    <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-fg">{s.name}</span>
                    <span className="text-[11px] text-subtle">
                      v{s.version}/{s.versions}
                    </span>
                    <LintChip s={s} />
                  </div>
                  <p className="mt-0.5 truncate pl-5.5 text-xs text-muted">{s.purpose}</p>
                  <p className="mt-0.5 truncate pl-5.5 text-[11px] text-subtle">
                    {s.lang} · {s.runs} runs · {pct(s.successRate)} ok · last {ago(s.lastRun)}
                  </p>
                </button>
              </li>
            ))
          )}
        </ul>
      </aside>
      <section className="min-h-0 min-w-0 flex-1 bg-surface">
        {name ? (
          <Detail key={`${name}:${source}`} name={name} source={source} v={v} onVersion={(n) => set({ v: String(n) })} />
        ) : (
          <Card className="m-6">
            <EmptyState icon={<Code2 className="size-5" />} title="Pick a script" hint="Read its source, versions, diffs, lint, runs and learned handlers." />
          </Card>
        )}
      </section>
    </div>
  );
}

export default function ScriptsPage() {
  return (
    <Suspense fallback={null}>
      <ScriptsInner />
    </Suspense>
  );
}
