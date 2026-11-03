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
  const input = useRef<HTMLInputElement>(null);
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
        <div
          onDragOver={(e) => {
            e.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
          className={cn('flex flex-col items-center justify-center gap-2 rounded-xl border-2 border-dashed px-4 py-6 text-center transition-colors', dragging ? 'border-accent bg-accent-soft' : 'border-border')}
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
          <input ref={input} type="file" accept=".mcshard,application/zip" className="hidden" onChange={(e) => pick(e.target.files?.[0])} />
          {tooBig ? <p className="text-xs text-danger">Too big: the limit is {bytes(SHARD_UPLOAD_MAX_BYTES)}.</p> : null}
        </div>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <Field label="Mode" htmlFor="im-mode" hint={mode === 'merge' ? 'Add and update records; nothing else is touched.' : mode === 'replace-section' ? 'Replace the shard’s kinds wholesale.' : 'Switch knowledge packs or game modes.'}>
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
          {diff ? (
            <Button
              variant="primary"
              onClick={() => file && apply.mutate({ body: options, file })}
              loading={apply.isPending}
              disabled={!!diff.refused || diff.writes === 0 || !!result}
            >
              {diff.rebuild.length ? 'Import and rebuild' : 'Import'}
            </Button>
          ) : null}
        </div>
        <InlineError error={preview.error ?? apply.error} />

        {diff && preview.data ? (
          <div className="space-y-3 border-t border-border pt-4">
            <p className="text-sm text-fg">
              <span className="font-medium">{preview.data.manifest.name}</span>
              <span className="text-muted">
                {' '}
                · {preview.data.manifest.kind} · {num(preview.data.manifest.total)} records · made by {preview.data.manifest.createdBy ?? 'unknown'}
              </span>
            </p>
            {diff.refused ? <p className="rounded-lg bg-danger-soft px-3 py-2 text-sm text-danger">Refused: {diff.refused}</p> : null}
            <div className="flex flex-wrap gap-1.5">
              {(['added', 'changed', 'conflicts', 'removed', 'unchanged', 'skipped'] as const).map((k) => (
                <Badge key={k} tone={k === 'conflicts' ? 'warn' : k === 'removed' ? 'danger' : k === 'added' ? 'ok' : 'neutral'}>
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
            <Switch checked={showUnchanged} onCheckedChange={setShowUnchanged} label={<span className="text-xs text-muted">Show unchanged</span>} />
            <ul className="max-h-72 divide-y divide-border overflow-auto rounded-lg border border-border text-sm">
              {items.map((i) => (
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
        ) : null}

        {result ? (
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
        ) : null}
      </div>
    </Card>
  );
}
