'use client';

/**
 * Shard import (admin): pick or drop a `.mcshard` → options → preview the diff against the bot's db (read-only) → apply on the
 * bot (it takes a backup first) → follow the scoped RAPTOR rebuild job.
 */
import { useQueryClient } from '@tanstack/react-query';
import { SHARD_UPLOAD_MAX_BYTES, type ShardChange, type ShardConflictPolicy, type ShardImportMode } from '@mineclaw/contracts';
import { CheckCircle2, FileUp, XCircle } from 'lucide-react';
import { useMemo, useRef, useState, type DragEvent } from 'react';
import { useApiMutation } from '@/lib/api/query';
import { bytes, cn, num, usd } from '@/lib/utils';
import { Badge, Button, Card, CardHeader, Checkbox, Field, InlineError, Select } from '@/components/ui/primitives';
import { Switch } from '@/components/ui/overlays';
import { JobProgress } from '@/features/admin/RebuildDialog';

const ORDER: ShardChange[] = ['removed', 'conflict', 'changed', 'added', 'skipped', 'unchanged'];
const TONE: Record<ShardChange, 'danger' | 'warn' | 'info' | 'ok' | 'neutral'> = {
  removed: 'danger',
  conflict: 'warn',
  changed: 'info',
  added: 'ok',
  skipped: 'neutral',
  unchanged: 'neutral',
};

export function ImportPanel({ initialMode }: { initialMode?: ShardImportMode }) {
  const qc = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [dragging, setDragging] = useState(false);
  const [mode, setMode] = useState<ShardImportMode>(initialMode ?? 'merge');
  const [conflict, setConflict] = useState<ShardConflictPolicy>('keep-newer');
  const [includeRules, setIncludeRules] = useState(false);
  const [includeWorld, setIncludeWorld] = useState(false);
  const [showUnchanged, setShowUnchanged] = useState(false);
  const preview = useApiMutation('shards.importPreview');
  const apply = useApiMutation('admin.shardImport', {
    onSuccess: () => void qc.invalidateQueries({ predicate: (q) => ['memory', 'overview', 'shards', 'raptor'].some((p) => String(q.queryKey[0]).startsWith(p)) }),
  });
  const options = { mode, conflict, includeRules, includeWorld, maxUsd: 0.25 };
  const tooBig = file ? file.size > SHARD_UPLOAD_MAX_BYTES : false;

  const pick = (f: File | null | undefined) => {
    if (!f) return;
    setFile(f);
    preview.reset();
    apply.reset();
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragging(false);
    pick(e.dataTransfer.files[0]);
  };

  const diff = preview.data?.diff;
  const items = useMemo(
    () => (diff?.items ?? []).filter((i) => showUnchanged || i.change !== 'unchanged').sort((a, b) => ORDER.indexOf(a.change) - ORDER.indexOf(b.change)),
    [diff, showUnchanged],
  );
  const result = apply.data;

  return (
    <Card>
      <CardHeader title="Import a shard" subtitle="Preview first; the bot backs up before it writes." icon={<FileUp className="size-4" />} />
      <div className="space-y-4 p-5">
        <DropZone file={file} dragging={dragging} setDragging={setDragging} onDrop={onDrop} onPick={pick} tooBig={tooBig} />
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Mode" htmlFor="im-mode" hint={MODE_HINT[mode] ?? OTHER_MODE_HINT}>
            <Select id="im-mode" value={mode} onChange={(e) => setMode(e.target.value as ShardImportMode)} className="w-full">
              <option value="merge">Merge</option>
              <option value="replace-section">Replace section</option>
              <option value="swap-pack">Swap version pack</option>
              <option value="add-mode">Add mode</option>
              <option value="remove-mode">Remove mode</option>
            </Select>
          </Field>
          <Field label="On conflict" htmlFor="im-conf">
            <Select id="im-conf" value={conflict} onChange={(e) => setConflict(e.target.value as ShardConflictPolicy)} className="w-full">
              <option value="keep-newer">Keep the newer copy</option>
              <option value="prefer-shard">Prefer the shard</option>
              <option value="keep-mine">Keep mine</option>
            </Select>
          </Field>
        </div>
        <div className="flex flex-wrap gap-4">
          <Checkbox label="Include rules" checked={includeRules} onChange={setIncludeRules} />
          <Checkbox label="Include world" checked={includeWorld} onChange={setIncludeWorld} />
        </div>
        <div className="flex gap-2">
          <Button onClick={() => file && preview.mutate({ body: options, file })} disabled={!file || tooBig} loading={preview.isPending}>
            Preview diff
          </Button>
          <ImportButton diff={diff} done={!!result} pending={apply.isPending} onImport={() => file && apply.mutate({ body: options, file })} />
        </div>
        <InlineError error={preview.error ?? apply.error} />

        {diff && preview.data ? <DiffView data={preview.data} diff={diff} items={items} showUnchanged={showUnchanged} setShowUnchanged={setShowUnchanged} /> : null}

        {result ? <ImportResult result={result} /> : null}
      </div>
    </Card>
  );
}

/** Import, once there is a preview (and it allows writes; once only). */
function ImportButton({ diff, done, pending, onImport }: { diff: Diff | undefined; done: boolean; pending: boolean; onImport: () => void }) {
  if (!diff) return null;
  return (
    <Button variant="primary" onClick={onImport} loading={pending} disabled={!!diff.refused || diff.writes === 0 || done}>
      {diff.rebuild.length ? 'Import and rebuild' : 'Import'}
    </Button>
  );
}

const MODE_HINT: Partial<Record<ShardImportMode, string>> = {
  merge: 'Add and update records; nothing else is touched.',
  'replace-section': 'Replace the shard’s kinds wholesale.',
};
const OTHER_MODE_HINT = 'Switch knowledge packs or game modes.';

/** The drop target and file picker, with the chosen file and the size limit. */
function DropZone(p: {
  file: File | null;
  dragging: boolean;
  setDragging: (on: boolean) => void;
  onDrop: (e: DragEvent) => void;
  onPick: (f: File | null | undefined) => void;
  tooBig: boolean;
}) {
  const input = useRef<HTMLInputElement>(null);
  const { file } = p;
  return (
    <div
      onDragOver={(e) => {
        e.preventDefault();
        p.setDragging(true);
      }}
      onDragLeave={() => p.setDragging(false)}
      onDrop={p.onDrop}
      className={cn('flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed px-4 py-6 text-center transition-colors', p.dragging ? 'border-accent bg-accent-soft' : 'border-border')}
    >
      <FileUp className="size-6 text-subtle" />
      {file ? (
        <p className="text-sm text-fg">
          {file.name} <span className="text-subtle">· {bytes(file.size)}</span>
        </p>
      ) : (
        <p className="text-sm text-muted">Drop a .mcshard here</p>
      )}
      <Button size="sm" onClick={() => input.current?.click()}>
        Choose file…
      </Button>
      <input ref={input} type="file" accept=".mcshard,application/zip" className="hidden" onChange={(e) => p.onPick(e.target.files?.[0])} />
      {p.tooBig ? <p className="text-xs text-danger">Too big: the limit is {bytes(SHARD_UPLOAD_MAX_BYTES)}.</p> : null}
    </div>
  );
}

type Preview = NonNullable<ReturnType<typeof useApiMutation<'shards.importPreview'>>['data']>;
type Diff = Preview['diff'];

const COUNT_TONE: Partial<Record<string, 'warn' | 'danger' | 'ok'>> = { conflicts: 'warn', removed: 'danger', added: 'ok' };

/** The preview: the shard's manifest, a refusal, the counts, the rebuild estimate, notes, and the items (unchanged hidden). */
function DiffView(p: { data: Preview; diff: Diff; items: Diff['items']; showUnchanged: boolean; setShowUnchanged: (on: boolean) => void }) {
  const { diff } = p;
  const m = p.data.manifest;
  return (
    <div className="space-y-3 border-t border-border pt-4">
      <p className="text-sm text-fg">
        <span className="font-medium">{m.name}</span>
        <span className="text-muted">
          {' '}
          · {m.kind} · {num(m.total)} records · made by {m.createdBy ?? 'unknown'}
        </span>
      </p>
      {diff.refused ? <p className="rounded-lg bg-danger-soft px-3 py-2 text-sm text-danger">Refused: {diff.refused}</p> : null}
      <div className="flex flex-wrap gap-1.5">
        {(['added', 'changed', 'conflicts', 'removed', 'unchanged', 'skipped'] as const).map((k) => (
          <Badge key={k} tone={COUNT_TONE[k] ?? 'neutral'}>
            {num(diff[k])} {k}
          </Badge>
        ))}
        <Badge tone="accent">{num(diff.writes)} writes</Badge>
      </div>
      {diff.estimate ? (
        <p className="text-xs text-muted">
          Rebuild after import: {diff.estimate.summaries} summaries · about {usd(diff.estimate.usd)} · {num(diff.reusableVectors)} vectors reused, {num(diff.toEmbed)} to embed
        </p>
      ) : null}
      {diff.notes.map((n, i) => (
        <p key={i} className="text-xs text-subtle">
          {n}
        </p>
      ))}
      <Switch checked={p.showUnchanged} onCheckedChange={p.setShowUnchanged} label={<span className="text-xs text-muted">Show unchanged</span>} />
      <ul className="max-h-72 divide-y divide-border overflow-auto rounded-lg border border-border text-sm">
        {p.items.map((i) => (
          <li key={`${i.kind}:${i.id}`} className="flex items-center gap-2 px-3 py-2">
            <Badge tone={TONE[i.change]} className="w-20 justify-center">
              {i.change}
            </Badge>
            <span className="text-xs text-subtle">{i.kind}</span>
            <span className="min-w-0 flex-1 truncate text-fg">{i.label}</span>
            {!i.apply ? <span className="text-xs text-subtle">(kept)</span> : null}
            {i.note ? <span className="truncate text-xs text-muted">{i.note}</span> : null}
          </li>
        ))}
      </ul>
    </div>
  );
}

type ImportOutcome = NonNullable<ReturnType<typeof useApiMutation<'admin.shardImport'>>['data']>;

/** What the import did, and the rebuild it started (followed). */
function ImportResult({ result }: { result: ImportOutcome }) {
  return (
    <div className="space-y-3 border-t border-border pt-4">
      <div className={cn('flex items-center gap-2 text-sm', result.ok ? 'text-ok' : 'text-danger')}>
        {result.ok ? <CheckCircle2 className="size-4" /> : <XCircle className="size-4" />}
        {result.message}
      </div>
      <p className="text-xs text-muted">
        {num(result.written)} written · {num(result.removed)} removed · {num(result.reusedVectors)} vectors reused · {num(result.embedded)} embedded
        {result.activePack ? ` · active pack ${result.activePack}` : ''}
      </p>
      {result.jobId ? <JobProgress jobId={result.jobId} /> : null}
    </div>
  );
}
