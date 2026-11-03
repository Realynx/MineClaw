'use client';

import { EditKind, GroomSource, MEMORY_REBUILD_KIND, WebAuditKind, type EditKind as EditKindT, type GroomSource as GroomSourceT, type RebuildRequest } from '@mineclaw/contracts';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Archive, ChevronLeft, ChevronRight, FileClock, Hammer, Pencil, ScrollText, Shrink, Sparkles } from 'lucide-react';
import { useRef, useState } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Button, Card, CardHeader, EmptyState, ErrorState, Field, InlineError, Input, KeyValues, PageHeader, Select, SkeletonLines, Textarea } from '@/components/ui/primitives';
import { Dialog, Segmented } from '@/components/ui/overlays';
import { EditForm } from '@/features/admin/EditForm';
import { RebuildDialog } from '@/features/admin/RebuildDialog';
import { useApi, useApiMutation } from '@/lib/api/query';
import { useAuth } from '@/lib/auth';
import { ago, cn, dateTime, num } from '@/lib/utils';

const REBUILD_KINDS = [...new Set(Object.values(MEMORY_REBUILD_KIND))];

function BackupCard() {
  const backup = useApiMutation('admin.backup');
  return (
    <Card>
      <CardHeader title="Backup" icon={<Archive className="size-4" />} subtitle="A copy of the bot's db, taken on the bot" />
      <div className="space-y-3 p-5">
        <p className="text-sm text-muted">Edits, rebuilds and imports take one automatically before their first write. Take one by hand before anything risky.</p>
        <Button onClick={() => backup.mutate({ body: {} })} loading={backup.isPending}>
          Back up now
        </Button>
        {backup.data ? <p className="text-xs text-ok">Saved {backup.data.path}</p> : null}
        <InlineError error={backup.error} />
      </div>
    </Card>
  );
}

function RebuildCard() {
  const [scope, setScope] = useState<'kind' | 'node'>('kind');
  const [kind, setKind] = useState(REBUILD_KINDS[0] ?? 'fact');
  const [node, setNode] = useState('');
  const [req, setReq] = useState<Pick<RebuildRequest, 'scope' | 'node' | 'kind'> | null>(null);
  return (
    <Card>
      <CardHeader title="RAPTOR rebuild" icon={<Hammer className="size-4" />} subtitle="Re-summarise part of the tree (estimate first, capped)" />
      <div className="space-y-3 p-5">
        <Segmented<'kind' | 'node'>
          label="Scope"
          size="sm"
          value={scope}
          onChange={setScope}
          options={[
            { value: 'kind', label: 'A memory kind' },
            { value: 'node', label: 'A subtree' },
          ]}
        />
        {scope === 'kind' ? (
          <Select aria-label="Kind" value={kind} onChange={(e) => setKind(e.target.value)} className="w-full">
            {REBUILD_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </Select>
        ) : (
          <Input value={node} onChange={(e) => setNode(e.target.value.replace(/\D/g, ''))} placeholder="Node number, e.g. 481" aria-label="Node number" inputMode="numeric" />
        )}
        <Button onClick={() => setReq(scope === 'kind' ? { scope, kind } : { scope, node: Number(node) })} disabled={scope === 'node' && !node}>
          Estimate…
        </Button>
      </div>
      {req ? <RebuildDialog open onOpenChange={(o) => !o && setReq(null)} request={req} label={req.scope === 'kind' ? `${req.kind ?? ''} memories` : `subtree #${req.node ?? ''}`} /> : null}
    </Card>
  );
}

function CompactCard() {
  const compact = useApiMutation('admin.compact');
  const [confirm, setConfirm] = useState(false);
  const r = compact.data;
  return (
    <Card>
      <CardHeader title="Compact activity" icon={<Shrink className="size-4" />} subtitle="Fold old activity rows into episodes" />
      <div className="space-y-3 p-5">
        <div className="flex gap-2">
          <Button onClick={() => compact.mutate({ body: { dryRun: true } })} loading={compact.isPending && compact.variables?.body?.dryRun !== false}>
            Preview
          </Button>
          <Button variant="primary" onClick={() => setConfirm(true)} disabled={!r?.dryRun}>
            Run…
          </Button>
        </div>
        <InlineError error={compact.error} />
        {r ? (
          <div className="space-y-2 rounded-lg border border-border p-3 text-sm">
            <p className={r.ok ? 'text-fg' : 'text-danger'}>{r.message}</p>
            <KeyValues
              className="text-xs"
              rows={[
                ['Activity rows', `${num(r.activityBefore)} → ${num(r.activityAfter)}`],
                ['Docs', `${num(r.docsBefore)} → ${num(r.docsAfter)}`],
                ['Episodes', num(r.episodes)],
                ['Dirty summaries', num(r.dirtySummaries)],
                ...(r.backup ? ([['Backup', r.backup]] as Array<[string, string]>) : []),
              ]}
            />
            {r.samples.map((s, i) => (
              <p key={i} className="text-xs text-muted">
                {s}
              </p>
            ))}
          </div>
        ) : null}
      </div>
      <Dialog
        open={confirm}
        onOpenChange={setConfirm}
        title="Compact the activity log?"
        description="The bot backs up first, then rewrites old activity into episodes. This cannot be undone except from the backup."
        size="sm"
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirm(false)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={() => {
                setConfirm(false);
                compact.mutate({ body: { dryRun: false } });
              }}
            >
              Compact
            </Button>
          </>
        }
      />
    </Card>
  );
}

function AddMemoryCard() {
  const [kind, setKind] = useState<EditKindT>('fact');
  const [open, setOpen] = useState(false);
  return (
    <Card>
      <CardHeader title="Add a memory" icon={<Pencil className="size-4" />} subtitle="To change or delete one, open it in the Memory browser" />
      <div className="flex items-end gap-2 p-5">
        <Field label="Kind" htmlFor="add-kind" className="flex-1">
          <Select id="add-kind" value={kind} onChange={(e) => setKind(e.target.value as EditKindT)} className="w-full">
            {EditKind.options
              .filter((k) => k !== 'observation')
              .map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
          </Select>
        </Field>
        <Button onClick={() => setOpen(true)}>New {kind}…</Button>
      </div>
      {open ? (
        <Dialog open onOpenChange={setOpen} title={`Add ${kind}`}>
          <EditForm kind={kind} op="add" onCancel={() => setOpen(false)} onDone={() => setOpen(false)} />
        </Dialog>
      ) : null}
    </Card>
  );
}

function GroomCard() {
  const [source, setSource] = useState<GroomSourceT>('paste');
  const [text, setText] = useState('');
  const groom = useApiMutation('groom.preview');
  return (
    <Card>
      <CardHeader title="Groom preview" icon={<Sparkles className="size-4" />} subtitle="What the bot's grooming would make of some text (dry run)" />
      <div className="space-y-3 p-5">
        <div className="flex gap-2">
          <Select aria-label="Source" value={source} onChange={(e) => setSource(e.target.value as GroomSourceT)}>
            {GroomSource.options.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
          <Button onClick={() => groom.mutate({ body: { source, ...(source === 'paste' ? { text } : { docRef: text }), rewrite: false } })} loading={groom.isPending} disabled={!text}>
            Preview
          </Button>
        </div>
        {source === 'paste' ? (
          <Textarea value={text} onChange={(e) => setText(e.target.value)} placeholder="Paste log lines or notes…" aria-label="Text to groom" rows={3} />
        ) : (
          <Input value={text} onChange={(e) => setText(e.target.value)} placeholder="Doc ref, e.g. act:120 or ep:31" aria-label="Doc ref" />
        )}
        <InlineError error={groom.error} />
        {groom.data ? (
          <div className="space-y-2">
            <p className="text-xs text-muted">
              {groom.data.inCount} in → {groom.data.outCount} out
            </p>
            {groom.data.docs.map((d, i) => (
              <div key={i} className="rounded-lg border border-border p-3 text-xs">
                <div className="mb-1 text-subtle">
                  [{d.kind} · {d.source} · {d.ref}] <span className="font-medium text-fg">{d.title}</span>
                </div>
                <p className="whitespace-pre-wrap text-fg">{d.body}</p>
              </div>
            ))}
          </div>
        ) : null}
      </div>
    </Card>
  );
}

function BotAudit() {
  const audit = useApi('admin.audit', { query: { max: 300 } });
  const ref = useRef<HTMLDivElement>(null);
  const rows = audit.data?.entries ?? [];
  const v = useVirtualizer({ count: rows.length, getScrollElement: () => ref.current, estimateSize: () => 52, overscan: 8 });
  return (
    <Card>
      <CardHeader title="Bot audit trail" icon={<ScrollText className="size-4" />} subtitle="Edits, rebuilds, backups and imports, as the bot recorded them" />
      {audit.isPending ? (
        <SkeletonLines lines={6} className="p-4" />
      ) : audit.error ? (
        <ErrorState error={audit.error} onRetry={() => void audit.refetch()} />
      ) : rows.length === 0 ? (
        <EmptyState title="No entries yet" />
      ) : (
        <div ref={ref} className="max-h-[28rem] overflow-auto">
          <div style={{ height: v.getTotalSize(), position: 'relative' }}>
            {v.getVirtualItems().map((it) => {
              const e = rows[it.index]!;
              return (
                <div key={it.key} className="absolute inset-x-0 flex items-start gap-3 border-b border-border px-4 py-2" style={{ top: it.start, height: 52 }}>
                  <Badge tone={e.ok ? 'neutral' : 'danger'} className="mt-0.5 w-16 justify-center">
                    {e.op}
                  </Badge>
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13px] text-fg">
                      {e.kind}
                      {e.id ? ` ${e.id}` : ''} · {e.message}
                    </p>
                    <p className="truncate text-[11px] text-subtle">
                      {e.who} · {e.target}
                      {e.reason ? ` · ${e.reason}` : ''}
                      {e.before || e.after ? ` · ${e.before ?? '∅'} → ${e.after ?? '∅'}` : ''}
                    </p>
                  </div>
                  <span className="shrink-0 text-[11px] text-subtle" title={dateTime(e.at)}>
                    {ago(e.at)}
                  </span>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </Card>
  );
}

function WebAudit() {
  const [offset, setOffset] = useState(0);
  const [user, setUser] = useState('');
  const [kind, setKind] = useState('');
  const [ok, setOk] = useState('');
  const limit = 50;
  const q = useApi('audit.list', {
    query: { offset, limit, ...(user ? { user } : {}), ...(kind ? { kind: kind as WebAuditKind } : {}), ...(ok ? { ok: ok as 'true' | 'false' } : {}) },
  });
  const total = q.data?.total ?? 0;
  return (
    <Card>
      <CardHeader
        title="Web audit log"
        icon={<FileClock className="size-4" />}
        subtitle="Sign-ins, chat lines and writes made through this site"
      />
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2.5">
        <Input value={user} onChange={(e) => (setUser(e.target.value), setOffset(0))} placeholder="User" aria-label="Filter by user" className="h-8 w-32" />
        <Select aria-label="Kind" value={kind} onChange={(e) => (setKind(e.target.value), setOffset(0))} className="h-8">
          <option value="">All kinds</option>
          {WebAuditKind.options.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </Select>
        <Select aria-label="Result" value={ok} onChange={(e) => (setOk(e.target.value), setOffset(0))} className="h-8">
          <option value="">Any result</option>
          <option value="true">OK</option>
          <option value="false">Failed</option>
        </Select>
      </div>
      {q.isPending ? (
        <SkeletonLines lines={8} className="p-4" />
      ) : q.error ? (
        <ErrorState error={q.error} onRetry={() => void q.refetch()} />
      ) : (
        <>
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-xs text-subtle">
                <tr className="border-b border-border">
                  <th className="px-4 py-2 font-medium">When</th>
                  <th className="py-2 font-medium">User</th>
                  <th className="py-2 font-medium">Kind</th>
                  <th className="py-2 font-medium">What</th>
                  <th className="py-2 pr-4 font-medium">IP</th>
                </tr>
              </thead>
              <tbody>
                {q.data.items.map((e) => (
                  <tr key={e.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-2 whitespace-nowrap text-muted" title={dateTime(e.at)}>
                      {ago(e.at)}
                    </td>
                    <td className="py-2 text-fg">{e.username ?? <span className="text-subtle">unknown</span>}</td>
                    <td className="py-2">
                      <Badge tone={e.ok ? 'neutral' : 'danger'}>{e.kind}</Badge>
                    </td>
                    <td className="max-w-md truncate py-2 text-muted">
                      {e.target ? <span className="text-subtle">{e.target} · </span> : null}
                      {e.summary}
                    </td>
                    <td className="tabular py-2 pr-4 text-xs text-subtle">{e.ip}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between border-t border-border px-4 py-2.5 text-xs text-muted">
            <span className="tabular">
              {total ? `${num(offset + 1)}–${num(Math.min(offset + limit, total))} of ${num(total)}` : 'No entries'}
            </span>
            <div className="flex gap-1">
              <Button size="icon-sm" variant="ghost" aria-label="Previous page" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - limit))}>
                <ChevronLeft className="size-4" />
              </Button>
              <Button size="icon-sm" variant="ghost" aria-label="Next page" disabled={offset + limit >= total} onClick={() => setOffset(offset + limit)}>
                <ChevronRight className="size-4" />
              </Button>
            </div>
          </div>
        </>
      )}
    </Card>
  );
}

export default function AdminPage() {
  const { isAdmin } = useAuth();
  if (!isAdmin) {
    return (
      <PageBody>
        <Card>
          <EmptyState title="Edits & audit needs the admin role" />
        </Card>
      </PageBody>
    );
  }
  return (
    <PageBody wide>
      <PageHeader title="Edits & audit" subtitle="Writes go to the live bot, which applies them and records them in its audit trail." />
      <div className={cn('grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4')}>
        <BackupCard />
        <RebuildCard />
        <CompactCard />
        <AddMemoryCard />
      </div>
      <div className="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]">
        <div className="space-y-4">
          <GroomCard />
          <BotAudit />
        </div>
        <WebAudit />
      </div>
    </PageBody>
  );
}
