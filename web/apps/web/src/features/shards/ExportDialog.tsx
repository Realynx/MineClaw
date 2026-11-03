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

/** Export a shard: choose what goes in, preview the exact counts and size, then download the `.mcshard`. */
export function ExportDialog({ open, onOpenChange, preset = {} }: { open: boolean; onOpenChange: (o: boolean) => void; preset?: ExportPreset }) {
  const [mode, setMode] = useState<Mode>(preset.mode ?? 'filter');
  const [name, setName] = useState(preset.people?.length ? `about-${preset.people[0]}` : preset.node !== undefined ? `subtree-${preset.node}` : 'export');
  const [description, setDescription] = useState('');
  const [kinds, setKinds] = useState<string[]>(preset.kinds ?? ['fact', 'claim', 'place']);
  const [dimension, setDimension] = useState<string>('');
  const [query, setQuery] = useState(preset.query ?? '');
  const [semantic, setSemantic] = useState(false);
  const [people, setPeople] = useState((preset.people ?? []).join(', '));
  const [packVersion, setPackVersion] = useState('b1.8.1');
  const [knowledgeDocs, setKnowledgeDocs] = useState(false);
  const [includeRules, setIncludeRules] = useState(false);
  const [includeWorld, setIncludeWorld] = useState(false);
  const [embeddings, setEmbeddings] = useState(true);
  const [summaries, setSummaries] = useState(true);
  const [downloading, setDownloading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const preview = useApiMutation('shards.exportPreview');

  const request = (): ShardExportRequest => ({
    name,
    description: description || null,
    includeEmbeddings: embeddings,
    includeSummaries: summaries,
    selection: {
      mode,
      node: mode === 'subtree' ? (preset.node ?? null) : null,
      kinds: mode === 'filter' ? (kinds as ShardSelection['kinds']) : null,
      dimension: mode === 'filter' && dimension !== '' ? Number(dimension) : null,
      query: mode === 'filter' && query ? query : null,
      semantic: mode === 'filter' && semantic,
      people: mode === 'person' ? people.split(',').map((p) => p.trim()).filter(Boolean) : null,
      packVersion: mode === 'pack' ? packVersion : null,
      includeKnowledgeDocs: mode === 'pack' && knowledgeDocs,
      includeRules,
      includeWorld,
    },
  });

  const download = async () => {
    setDownloading(true);
    setError(null);
    try {
      const { blob, filename } = await apiBlob('shards.export', { body: request() });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename ?? `${name}.mcshard`;
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
          <Button variant="ghost" onClick={() => preview.mutate({ body: request() })} loading={preview.isPending} disabled={!name}>
            <Eye className="size-4" /> Preview
          </Button>
          <Button variant="primary" onClick={() => void download()} loading={downloading} disabled={!name || !p || p.total === 0}>
            <Download className="size-4" /> Export {p ? `(${bytes(p.bytes)})` : ''}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-1 gap-5 md:grid-cols-2">
        <div className="space-y-4">
          <Field label="Name" htmlFor="sh-name">
            <Input id="sh-name" value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="Description" htmlFor="sh-desc">
            <Textarea id="sh-desc" value={description} onChange={(e) => setDescription(e.target.value)} rows={2} />
          </Field>
          <Segmented<Mode>
            label="What to export"
            size="sm"
            value={mode}
            onChange={(m) => {
              setMode(m);
              preview.reset();
            }}
            options={[
              { value: 'filter', label: 'By filter' },
              { value: 'person', label: 'About a person' },
              { value: 'pack', label: 'Knowledge pack' },
              ...(preset.node !== undefined ? [{ value: 'subtree' as const, label: 'Subtree' }] : []),
            ]}
          />
          {mode === 'filter' ? (
            <div className="space-y-3">
              <fieldset>
                <legend className="mb-1.5 text-xs font-medium text-muted">Kinds</legend>
                <div className="grid grid-cols-2 gap-1.5">
                  {ShardKind.options.map((k) => (
                    <Checkbox key={k} label={k} checked={kinds.includes(k)} onChange={(v) => setKinds((s) => (v ? [...s, k] : s.filter((x) => x !== k)))} />
                  ))}
                </div>
              </fieldset>
              <Field label="Dimension" htmlFor="sh-dim">
                <Select id="sh-dim" value={dimension} onChange={(e) => setDimension(e.target.value)} className="w-full">
                  <option value="">Any</option>
                  <option value="0">Overworld</option>
                  <option value="-1">Nether</option>
                </Select>
              </Field>
              <Field label="Text filter" htmlFor="sh-q">
                <Input id="sh-q" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="optional" />
              </Field>
              <Checkbox label="Semantic (use the bot's hybrid search)" checked={semantic} onChange={setSemantic} />
            </div>
          ) : mode === 'person' ? (
            <Field label="People (logins and nicknames, comma separated)" htmlFor="sh-people">
              <Input id="sh-people" value={people} onChange={(e) => setPeople(e.target.value)} placeholder="kiwi, Robin" />
            </Field>
          ) : mode === 'pack' ? (
            <div className="space-y-3">
              <Field label="Minecraft version" htmlFor="sh-ver">
                <Select id="sh-ver" value={packVersion} onChange={(e) => setPackVersion(e.target.value)} className="w-full">
                  <option value="b1.8.1">b1.8.1</option>
                  <option value="1.12.2">1.12.2</option>
                </Select>
              </Field>
              <Checkbox label="Also carry docs / knowledge chunks" checked={knowledgeDocs} onChange={setKnowledgeDocs} />
            </div>
          ) : (
            <p className="text-sm text-muted">Every leaf under RAPTOR node #{preset.node}.</p>
          )}
          <fieldset className="space-y-1.5 border-t border-border pt-3">
            <Checkbox label="Include standing rules" checked={includeRules} onChange={setIncludeRules} />
            <Checkbox label="Include world memory (places, structures)" checked={includeWorld} onChange={setIncludeWorld} />
            <Checkbox label="Include embeddings" checked={embeddings} onChange={setEmbeddings} />
            <Checkbox label="Include RAPTOR summaries as a cache" checked={summaries} onChange={setSummaries} />
          </fieldset>
        </div>
        <div className="rounded-xl border border-border bg-surface-2/50 p-4">
          <h3 className="mb-3 text-sm font-semibold text-fg">Preview</h3>
          {preview.error ? <p className="text-sm text-danger">{errorText(preview.error)}</p> : null}
          {error ? <p className="text-sm text-danger">{error}</p> : null}
          {!p ? (
            <p className="text-sm text-muted">Preview to see exactly what will be written. Nothing is written by a preview.</p>
          ) : (
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
          )}
        </div>
      </div>
    </Dialog>
  );
}
