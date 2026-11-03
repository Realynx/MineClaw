'use client';

/**
 * History, one click away (never on screen by default): the brain's turn timeline, the full context window on demand, and a
 * turn's detail. Fixes explorer-spec §17.1–3: lane filter (background lanes hidden by default), turns as compact rows with
 * numbers in columns (dev details), "latest turn only" for the context with the full window behind a toggle, and every
 * message collapsible.
 */
import type { BrainContextPage, CtxBlock, CtxMessage, CtxTurnSummary } from '@mineclaw/contracts';
import { ArrowLeft, Brain, ChevronRight, Copy, Wrench } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { clock, cn, compact, copyText, num, usd } from '@/lib/utils';
import { Badge, Button, EmptyState, ErrorState, Input, Meter, SkeletonLines } from '@/components/ui/primitives';
import { Drawer, Segmented, Switch, TabPanel, Tabs } from '@/components/ui/overlays';

const MAIN_LANES = new Set(['brain', 'reply']);
const LANE_TONE: Record<string, 'accent' | 'info' | 'neutral' | 'warn'> = { brain: 'accent', reply: 'info', compaction: 'warn' };

function BlockView({ block }: { block: CtxBlock }) {
  const label = block.label ?? (block.kind === 'tool_use' ? `tool · ${block.name ?? ''}` : block.kind.replace('_', ' '));
  let body: string = block.text ?? '';
  if (block.kind === 'tool_use') {
    try {
      body = JSON.stringify(JSON.parse(block.input ?? '{}'), null, 2);
    } catch {
      body = block.input ?? '';
    }
  }
  if (block.kind === 'redacted_thinking') body = `(encrypted, ${num(block.bytes ?? 0)} bytes)`;
  if (block.kind === 'image') body = block.label ?? '(image)';
  return (
    <div className={cn('rounded-lg border px-3 py-2', block.isError ? 'border-danger/40 bg-danger-soft' : 'border-border bg-surface')}>
      <div className="mb-1 flex items-center gap-1.5 text-[11px] font-medium text-subtle">
        {block.kind === 'tool_use' || block.kind === 'tool_result' ? <Wrench className="size-3" /> : null}
        {label}
        {block.target ? <Badge tone="accent">this body</Badge> : null}
      </div>
      {body ? (
        <pre className={cn('font-mono text-xs leading-relaxed whitespace-pre-wrap text-fg', block.kind === 'thinking' && 'text-muted italic')}>{body}</pre>
      ) : null}
    </div>
  );
}

function MessageView({ m, defaultOpen }: { m: CtxMessage; defaultOpen: boolean }) {
  const preview = m.blocks.map((b) => b.text || b.name || b.kind).join(' · ').slice(0, 120);
  return (
    <details open={defaultOpen} className="group rounded-lg border border-border bg-surface-2/50">
      <summary className="flex cursor-pointer list-none items-center gap-2 px-3 py-2 text-[13px]">
        <ChevronRight className="size-3.5 shrink-0 text-subtle transition-transform group-open:rotate-90" />
        <Badge tone={m.role === 'assistant' ? 'accent' : 'neutral'}>{m.role}</Badge>
        {m.tag ? <span className="text-[11px] text-subtle">{m.tag.replace('_', ' ')}</span> : null}
        <span className="min-w-0 flex-1 truncate text-muted">{preview}</span>
        <span className="text-[11px] text-subtle">#{m.index}</span>
      </summary>
      <div className="space-y-2 px-3 pb-3">
        {m.blocks.map((b, i) => (
          <BlockView key={i} block={b} />
        ))}
      </div>
    </details>
  );
}

function ContextWindowView({ page }: { page: BrainContextPage }) {
  const { devDetails } = usePrefs();
  const [scope, setScope] = useState<'latest' | 'full'>('latest');
  const [q, setQ] = useState('');
  const w = page.window;
  if (!w) return <EmptyState title="No context captured yet" hint={page.note ?? 'The brain has not made a call since telemetry started.'} />;
  // "Latest turn" = from the last user message that opened a turn.
  const lastTurn = [...w.messages].reverse().findIndex((m) => m.role === 'user' && m.tag === 'turn');
  const start = lastTurn < 0 ? 0 : w.messages.length - 1 - lastTurn;
  const shown = (scope === 'latest' ? w.messages.slice(start) : w.messages).filter(
    (m) => !q || m.blocks.some((b) => `${b.text ?? ''} ${b.name ?? ''} ${b.input ?? ''}`.toLowerCase().includes(q.toLowerCase())),
  );
  const used = w.estimatedTokens || w.promptTokens;
  return (
    <div className="space-y-4 p-5">
      <div className="rounded-xl border border-border p-4">
        <div className="mb-2 flex items-baseline justify-between text-xs">
          <span className="font-medium text-fg">Context size</span>
          <span className="tabular text-muted">
            {compact(used)} / {compact(w.limit)} tokens · compacts at {compact(w.compactAt)}
          </span>
        </div>
        <div className="relative">
          <Meter value={used} max={w.limit} tone={used / w.limit > 0.85 ? 'danger' : 'accent'} label="context used" />
          <span aria-hidden className="absolute top-[-3px] h-3 w-px bg-warn" style={{ left: `${(w.compactAt / w.limit) * 100}%` }} />
        </div>
        {devDetails ? (
          <p className="tabular mt-2 text-[11px] text-subtle">
            cached prefix ~{compact(w.cachedPrefixTokens)} · {w.toolCount} tools ~{compact(w.toolTokens)} · {w.model}
            {w.usage ? ` · this call read ${compact(w.usage.cacheRead)} / wrote ${compact(w.usage.cacheWrite)} · ${usd(w.usage.cost)}` : ''}
          </p>
        ) : null}
      </div>
      <details className="group rounded-lg border border-border">
        <summary className="flex cursor-pointer list-none items-center gap-2 px-3 py-2 text-[13px] font-medium text-fg">
          <ChevronRight className="size-3.5 text-subtle transition-transform group-open:rotate-90" />
          System prompt
          <span className="font-normal text-subtle">· {w.system.length} blocks · ~{compact(w.system.reduce((s, b) => s + b.tokens, 0))} tok</span>
        </summary>
        <div className="space-y-2 px-3 pb-3">
          {w.system.map((b) => (
            <div key={b.index} className="rounded-lg border border-border bg-surface p-3">
              <div className="mb-1 text-[11px] text-subtle">
                #{b.index} · ~{compact(b.tokens)} tok {b.cache ? `· cache breakpoint${b.ttl ? ` (${b.ttl})` : ''}` : ''} · {b.inCachedPrefix ? 'cached prefix' : 'uncached'}
              </div>
              <pre className="font-mono text-xs whitespace-pre-wrap text-fg">{b.text}</pre>
            </div>
          ))}
        </div>
      </details>
      <div className="flex flex-wrap items-center gap-2">
        <Segmented
          label="Scope"
          size="sm"
          value={scope}
          onChange={setScope}
          options={[
            { value: 'latest', label: 'Latest turn' },
            { value: 'full', label: `Full context (${w.messages.length})` },
          ]}
        />
        <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter messages…" className="h-8 max-w-56 flex-1" aria-label="Filter messages" />
      </div>
      <div className="space-y-2">
        {shown.map((m, i) => (
          <MessageView key={m.index} m={m} defaultOpen={scope === 'latest' && i >= shown.length - 2} />
        ))}
        {shown.length === 0 ? <p className="py-6 text-center text-sm text-muted">No messages match.</p> : null}
      </div>
    </div>
  );
}

function TurnRow({ t, onOpen, dev }: { t: CtxTurnSummary; onOpen: () => void; dev: boolean }) {
  return (
    <li>
      <button onClick={onOpen} className="flex w-full items-start gap-3 px-5 py-3 text-left hover:bg-surface-2 focus-visible:outline-2">
        <span className="tabular w-16 shrink-0 pt-0.5 text-[11px] text-subtle">{clock(t.start)}</span>
        <div className="min-w-0 flex-1">
          <div className="mb-0.5 flex items-center gap-1.5">
            <Badge tone={LANE_TONE[t.lane] ?? 'neutral'}>{t.lane}</Badge>
            {t.inFlight ? <Badge tone="info">in flight</Badge> : null}
            {t.error ? <Badge tone="danger">error</Badge> : null}
            {t.tools.map((tool) => (
              <Badge key={tool}>{tool}</Badge>
            ))}
          </div>
          <p className="truncate text-[13px] text-fg">{t.preview}</p>
          {dev ? (
            <p className="tabular mt-0.5 text-[11px] text-subtle">
              {t.tier} · {t.calls} call{t.calls === 1 ? '' : 's'} · in {compact(t.in)} · out {compact(t.out)} · cache {compact(t.cacheRead)}/{compact(t.cacheWrite)} · {usd(t.cost)} · {(t.latencyMs / 1000).toFixed(1)} s
            </p>
          ) : null}
        </div>
        <ChevronRight className="mt-1 size-4 shrink-0 text-subtle" />
      </button>
    </li>
  );
}

function TurnDetailView({ brain, turn, body, onBack }: { brain: string; turn: number; body?: string; onBack: () => void }) {
  const { devDetails } = usePrefs();
  const d = useApi('brains.turn', { params: { brain, n: turn }, query: body ? { body } : {} });
  return (
    <div className="p-5">
      <div className="mb-4 flex items-center gap-2">
        <Button size="sm" variant="ghost" onClick={onBack}>
          <ArrowLeft className="size-3.5" /> All turns
        </Button>
        <span className="flex-1 text-sm font-medium text-fg">Turn #{turn}</span>
        {d.data ? (
          <Button size="sm" variant="ghost" onClick={() => void copyText(JSON.stringify(d.data, null, 2))}>
            <Copy className="size-3.5" /> Copy JSON
          </Button>
        ) : null}
      </div>
      {d.isPending ? (
        <SkeletonLines lines={8} />
      ) : d.error ? (
        <ErrorState error={d.error} onRetry={() => void d.refetch()} />
      ) : (
        <div className="space-y-5">
          <p className="text-sm text-fg">{d.data.turn.preview}</p>
          {d.data.calls.map((c) => (
            <section key={c.seq} className="space-y-2">
              <h3 className="flex flex-wrap items-center gap-2 text-xs font-medium text-muted">
                <span className="text-fg">Call {c.round + 1}</span>
                <span>{clock(c.at)}</span>
                {c.stopReason ? <Badge>{c.stopReason}</Badge> : null}
                {c.inFlight ? <Badge tone="info">in flight</Badge> : null}
                {devDetails ? (
                  <span className="tabular text-subtle">
                    {c.model} · +{c.added.length} msgs on {c.context} · {c.usage ? `in ${compact(c.usage.in)} out ${compact(c.usage.out)} · ${usd(c.usage.cost)}` : ''}
                    {c.effort ? ` · effort ${c.effort}` : ''}
                  </span>
                ) : null}
              </h3>
              {c.error ? <p className="text-xs text-danger">{c.error}</p> : null}
              <div className="space-y-2">
                {c.added.map((m) => (
                  <MessageView key={`a${m.index}`} m={m} defaultOpen={false} />
                ))}
                <div className="space-y-2 rounded-lg border border-accent/30 bg-accent-soft/40 p-2">
                  <div className="px-1 text-[11px] font-medium text-accent">Response</div>
                  {c.response.map((b, i) => (
                    <BlockView key={i} block={b} />
                  ))}
                </div>
              </div>
            </section>
          ))}
          {d.data.children.length ? (
            <section>
              <h3 className="mb-2 text-xs font-medium text-muted">Sub-entries</h3>
              <ul className="divide-y divide-border rounded-lg border border-border">
                {d.data.children.map((c) => (
                  <li key={c.id} className="flex items-center gap-2 px-3 py-2 text-[13px]">
                    <Badge>{c.lane}</Badge>
                    <span className="min-w-0 flex-1 truncate text-fg">{c.preview}</span>
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
        </div>
      )}
    </div>
  );
}

export function HistoryDrawer({
  open,
  onOpenChange,
  brain,
  body,
  subhive,
  initialTab = 'turns',
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  brain: string | null | undefined;
  body: string;
  subhive: boolean;
  initialTab?: 'turns' | 'context';
}) {
  const { devDetails } = usePrefs();
  const [tab, setTab] = useState<string>(initialTab);
  const [allLanes, setAllLanes] = useState(false);
  const [onlyBody, setOnlyBody] = useState(subhive);
  const [turn, setTurn] = useState<number | null>(null);
  // On demand only: polled while this drawer is open (the stream carries no context pages).
  const ctx = useApi(
    'brains.context',
    { params: { brain: brain ?? '' }, query: onlyBody ? { body } : {} },
    { enabled: open && !!brain, refetchInterval: open ? 1500 : false },
  );
  const turns = useMemo(() => {
    const list = [...(ctx.data?.turns ?? [])].sort((a, b) => b.id - a.id);
    return list.filter((t) => (allLanes || MAIN_LANES.has(t.lane)) && (!onlyBody || !t.body || t.body === body || t.targets));
  }, [ctx.data, allLanes, onlyBody, body]);
  const hidden = (ctx.data?.turns.length ?? 0) - turns.length;

  return (
    <Drawer
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o);
        if (!o) setTurn(null);
      }}
      title={`History · ${body}`}
      subtitle={brain ? `brain ${brain}${subhive ? ' (shared with the owner)' : ''}` : 'no brain'}
      width="lg"
    >
      {!brain ? (
        <EmptyState icon={<Brain className="size-5" />} title="This body has no brain" hint="Script-only bodies have no turns or context. Open the script debugger instead." />
      ) : turn !== null ? (
        <TurnDetailView brain={brain} turn={turn} body={onlyBody ? body : undefined} onBack={() => setTurn(null)} />
      ) : (
        <Tabs
          value={tab}
          onValueChange={setTab}
          tabs={[
            { value: 'turns', label: 'Turns', count: turns.length },
            { value: 'context', label: 'Context window' },
          ]}
          listClassName="px-4"
        >
          <TabPanel value="turns">
            <div className="flex flex-wrap items-center gap-4 border-b border-border px-5 py-2.5">
              <Switch checked={allLanes} onCheckedChange={setAllLanes} label={<span className="text-xs text-muted">Background lanes{!allLanes && hidden > 0 ? ` (${hidden} hidden)` : ''}</span>} />
              {subhive ? <Switch checked={onlyBody} onCheckedChange={setOnlyBody} label={<span className="text-xs text-muted">Only this body’s turns</span>} /> : null}
            </div>
            {ctx.isPending ? (
              <SkeletonLines lines={8} className="p-5" />
            ) : ctx.error ? (
              <ErrorState error={ctx.error} onRetry={() => void ctx.refetch()} />
            ) : !ctx.data.enabled ? (
              <EmptyState title="Telemetry is off on the bot" hint={ctx.data.note ?? 'Turn on Telemetry:Enabled to see turns.'} />
            ) : turns.length === 0 ? (
              <EmptyState title="No turns yet" />
            ) : (
              <ul className="divide-y divide-border">
                {turns.map((t) => (
                  <TurnRow key={t.id} t={t} dev={devDetails} onOpen={() => setTurn(t.id)} />
                ))}
              </ul>
            )}
          </TabPanel>
          <TabPanel value="context">
            {ctx.isPending ? <SkeletonLines lines={8} className="p-5" /> : ctx.error ? <ErrorState error={ctx.error} /> : <ContextWindowView page={ctx.data} />}
          </TabPanel>
        </Tabs>
      )}
    </Drawer>
  );
}
