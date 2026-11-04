'use client';

import { ShardKind, type ShardExportRequest, type ShardSelection } from '@mineclaw/contracts';
import { Download, Eye } from 'lucide-react';
import { useState } from 'react';
import { apiBlob, errorText } from '@/lib/api/client';
import { useApiMutation } from '@/lib/api/query';
import { bytes, num } from '@/lib/utils';
import { Badge, Button, Checkbox, Field, Input, KeyValues, Select, Textarea } from '@/components/ui/primitives';
import { Dialog, Segmented } from '@/components/ui/overlays';

type Mode = ShardSelection['mode'];

export interface ExportPreset {
  mode?: Mode;
  node?: number;
  kinds?: string[];
  query?: string;
  people?: string[];
}

/** The export form's fields. */
export interface ExportForm {
  mode: Mode;
  name: string;
  description: string;
  kinds: string[];
  dimension: string;
  query: string;
  semantic: boolean;
  people: string;
  packVersion: string;
  knowledgeDocs: boolean;
  includeRules: boolean;
  includeWorld: boolean;
  embeddings: boolean;
  summaries: boolean;
}

function initialName(preset: ExportPreset): string {
  if (preset.people?.length) return `about-${preset.people[0]}`;
  return preset.node !== undefined ? `subtree-${preset.node}` : 'export';
}

export function initialForm(preset: ExportPreset): ExportForm {
  return {
    mode: preset.mode ?? 'filter',
    name: initialName(preset),
    description: '',
    kinds: preset.kinds ?? ['fact', 'claim', 'place'],
    dimension: '',
    query: preset.query ?? '',
    semantic: false,
    people: (preset.people ?? []).join(', '),
    packVersion: 'b1.8.1',
    knowledgeDocs: false,
    includeRules: false,
    includeWorld: false,
    embeddings: true,
    summaries: true,
  };
}

/** The filter selection's fields (null / off outside filter mode). */
function filterSelection(f: ExportForm) {
  const filter = f.mode === 'filter';
  return {
    kinds: filter ? (f.kinds as ShardSelection['kinds']) : null,
    dimension: filter && f.dimension !== '' ? Number(f.dimension) : null,
    query: filter && f.query ? f.query : null,
    semantic: filter && f.semantic,
  };
}

/** The export request the form describes (each mode sends only its own selection fields). */
export function exportRequest(f: ExportForm, preset: ExportPreset): ShardExportRequest {
  const { mode } = f;
  return {
    name: f.name,
    description: f.description || null,
    includeEmbeddings: f.embeddings,
    includeSummaries: f.summaries,
    selection: {
      mode,
      node: mode === 'subtree' ? (preset.node ?? null) : null,
      ...filterSelection(f),
      people: mode === 'person' ? f.people.split(',').map((p) => p.trim()).filter(Boolean) : null,
      packVersion: mode === 'pack' ? f.packVersion : null,
      includeKnowledgeDocs: mode === 'pack' && f.knowledgeDocs,
      includeRules: f.includeRules,
      includeWorld: f.includeWorld,
    },
  };
}

type Update = (patch: Partial<ExportForm> | ((f: ExportForm) => Partial<ExportForm>)) => void;

/** Export a shard: choose what goes in, preview the exact counts and size, then download the `.mcshard`. */
export function ExportDialog({ open, onOpenChange, preset = {} }: { open: boolean; onOpenChange: (o: boolean) => void; preset?: ExportPreset }) {
  const [f, setForm] = useState<ExportForm>(() => initialForm(preset));
  const update: Update = (patch) => setForm((cur) => ({ ...cur, ...(typeof patch === 'function' ? patch(cur) : patch) }));
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const preview = useApiMutation('shards.exportPreview');
  const request = () => exportRequest(f, preset);

  const download = async () => {
    setDownloading(true);
    setError(null);
    try {
      const { blob, filename } = await apiBlob('shards.export', { body: request() });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename ?? `${f.name}.mcshard`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 5000);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setDownloading(false);
    }
  };

  const p = preview.data;
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Export a shard"
      description="A portable .mcshard of memories (secrets are redacted). Exports are recorded in the audit log."
      size="lg"
      footer={
        <>
          <Button variant="ghost" onClick={() => preview.mutate({ body: request() })} loading={preview.isPending} disabled={!f.name}>
            <Eye className="size-4" /> Preview
          </Button>
          <Button variant="primary" onClick={() => void download()} loading={downloading} disabled={!f.name || !p || p.total === 0}>
            <Download className="size-4" /> Export {p ? `(${bytes(p.bytes)})` : ''}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-1 gap-5 md:grid-cols-2">
        <div className="space-y-4">
          <Field label="Name" htmlFor="sh-name">
            <Input id="sh-name" value={f.name} onChange={(e) => update({ name: e.target.value })} />
          </Field>
          <Field label="Description" htmlFor="sh-desc">
            <Textarea id="sh-desc" value={f.description} onChange={(e) => update({ description: e.target.value })} rows={2} />
          </Field>
          <Segmented<Mode>
            label="What to export"
            size="sm"
            value={f.mode}
            onChange={(m) => {
              update({ mode: m });
              preview.reset();
            }}
            options={[
              { value: 'filter', label: 'By filter' },
              { value: 'person', label: 'About a person' },
              { value: 'pack', label: 'Knowledge pack' },
              ...(preset.node !== undefined ? [{ value: 'subtree' as const, label: 'Subtree' }] : []),
            ]}
          />
          <ModeFields f={f} update={update} node={preset.node} />
          <fieldset className="space-y-1.5 border-t border-border pt-3">
            <Checkbox label="Include standing rules" checked={f.includeRules} onChange={(v) => update({ includeRules: v })} />
            <Checkbox label="Include world memory (places, structures)" checked={f.includeWorld} onChange={(v) => update({ includeWorld: v })} />
            <Checkbox label="Include embeddings" checked={f.embeddings} onChange={(v) => update({ embeddings: v })} />
            <Checkbox label="Include RAPTOR summaries as a cache" checked={f.summaries} onChange={(v) => update({ summaries: v })} />
          </fieldset>
        </div>
        <div className="rounded-xl border border-border bg-surface-2/50 p-4">
          <h3 className="mb-3 text-sm font-semibold text-fg">Preview</h3>
          {preview.error ? <p className="text-sm text-danger">{errorText(preview.error)}</p> : null}
          {error ? <p className="text-sm text-danger">{error}</p> : null}
          <ExportPreview p={p} />
        </div>
      </div>
    </Dialog>
  );
}

/** The selection fields of the chosen mode. */
function ModeFields({ f, update, node }: { f: ExportForm; update: Update; node: number | undefined }) {
  if (f.mode === 'filter')
    return (
      <div className="space-y-3">
        <fieldset>
          <legend className="mb-1.5 text-xs font-medium text-muted">Kinds</legend>
          <div className="grid grid-cols-2 gap-1.5">
            {ShardKind.options.map((k) => (
              <Checkbox key={k} label={k} checked={f.kinds.includes(k)} onChange={(v) => update((cur) => ({ kinds: v ? [...cur.kinds, k] : cur.kinds.filter((x) => x !== k) }))} />
            ))}
          </div>
        </fieldset>
        <Field label="Dimension" htmlFor="sh-dim">
          <Select id="sh-dim" value={f.dimension} onChange={(e) => update({ dimension: e.target.value })} className="w-full">
            <option value="">Any</option>
            <option value="0">Overworld</option>
            <option value="-1">Nether</option>
          </Select>
        </Field>
        <Field label="Text filter" htmlFor="sh-q">
          <Input id="sh-q" value={f.query} onChange={(e) => update({ query: e.target.value })} placeholder="optional" />
        </Field>
        <Checkbox label="Semantic (use the bot's hybrid search)" checked={f.semantic} onChange={(v) => update({ semantic: v })} />
      </div>
    );
  if (f.mode === 'person')
    return (
      <Field label="People (logins and nicknames, comma separated)" htmlFor="sh-people">
        <Input id="sh-people" value={f.people} onChange={(e) => update({ people: e.target.value })} placeholder="kiwi, Robin" />
      </Field>
    );
  if (f.mode === 'pack')
    return (
      <div className="space-y-3">
        <Field label="Minecraft version" htmlFor="sh-ver">
          <Select id="sh-ver" value={f.packVersion} onChange={(e) => update({ packVersion: e.target.value })} className="w-full">
            <option value="b1.8.1">b1.8.1</option>
            <option value="1.12.2">1.12.2</option>
          </Select>
        </Field>
        <Checkbox label="Also carry docs / knowledge chunks" checked={f.knowledgeDocs} onChange={(v) => update({ knowledgeDocs: v })} />
      </div>
    );
  return <p className="text-sm text-muted">Every leaf under RAPTOR node #{node}.</p>;
}

type ExportPreviewData = NonNullable<ReturnType<typeof useApiMutation<'shards.exportPreview'>>['data']>;

/** What the export will hold: counts per kind, records, vectors, summaries, size, redactions, samples and notes. */
function ExportPreview({ p }: { p: ExportPreviewData | undefined }) {
  if (!p) return <p className="text-sm text-muted">Preview to see exactly what will be written. Nothing is written by a preview.</p>;
  return (
    <div className="space-y-3 text-sm">
      <div className="flex flex-wrap gap-1.5">
        {Object.entries(p.counts).map(([k, n]) => (
          <Badge key={k}>
            {k} {num(n)}
          </Badge>
        ))}
      </div>
      <KeyValues
        className="text-xs"
        rows={[
          ['Records', num(p.total)],
          ['Vectors', p.vectors ? `${num(p.vectors)} · ${p.embeddingModel} @ ${p.dims}` : 'none'],
          ['Summaries', num(p.summaries)],
          ['File size', bytes(p.bytes)],
          ['Secrets redacted', p.secretsRedacted],
        ]}
      />
      {p.samples.length ? (
        <ul className="space-y-1 text-xs text-muted">
          {p.samples.map((s, i) => (
            <li key={i} className="truncate">
              {s}
            </li>
          ))}
        </ul>
      ) : null}
      {p.notes.map((n, i) => (
        <p key={i} className="text-xs text-warn">
          {n}
        </p>
      ))}
    </div>
  );
}
