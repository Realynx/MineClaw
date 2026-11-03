'use client';

import { SearchKind, toNumber, type SearchHit } from '@mineclaw/contracts';
import { ChevronDown, Copy, Network, Search as SearchIcon } from 'lucide-react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { Suspense, useState, type FormEvent } from 'react';
import { PageBody } from '@/components/shell/Shell';
import { Badge, Button, Card, EmptyState, ErrorState, Input, Select, SkeletonLines } from '@/components/ui/primitives';
import { Segmented } from '@/components/ui/overlays';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { ago, cn, copyText, fixed } from '@/lib/utils';

function Hit({ h, hits, q }: { h: SearchHit; hits: number[]; q: string }) {
  const { devDetails } = usePrefs();
  const grand = h.raptorPath.length >= 3 ? h.raptorPath[h.raptorPath.length - 3] : h.raptorPath[0];
  return (
    <li className="rounded-card border border-border bg-surface p-4 shadow-card">
      <div className="mb-1.5 flex flex-wrap items-center gap-2">
        <span className="tabular grid size-6 place-items-center rounded-md bg-surface-2 text-[11px] font-semibold text-muted">{h.rank}</span>
        <Badge>
          {h.kind}
          {h.tag ? `/${h.tag}` : ''}
        </Badge>
        <h3 className="min-w-0 flex-1 truncate text-[14px] font-semibold text-fg">{h.title}</h3>
        <span className="tabular text-xs text-subtle">{fixed(h.score, 4)}</span>
      </div>
      <p className="text-[13px] leading-relaxed text-muted">{h.snippet}</p>
      <div className="mt-2.5 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-subtle">
        {h.updated ? <span>{ago(h.updated)}</span> : null}
        {h.where ? <span>{h.where}</span> : null}
        {devDetails ? (
          <span className="tabular">
            bm25 {fixed(h.signals.bm25, 2)} · cos {fixed(h.signals.cos, 3)}
            {h.signals.distance !== null && h.signals.distance !== undefined ? ` · ${fixed(h.signals.distance, 0)} m` : ''}
          </span>
        ) : null}
      </div>
      {h.raptorPath.length ? (
        <nav aria-label="Place in the RAPTOR tree" className="mt-2.5 flex flex-wrap items-center gap-1 text-xs">
          <Network className="size-3 text-subtle" />
          {h.raptorPath.map((c, i) => (
            <span key={c.index} className="inline-flex items-center gap-1">
              {i > 0 ? <span className="text-subtle">›</span> : null}
              <Link href={`/raptor?view=tree&node=${c.index}`} className="max-w-44 truncate rounded px-1 text-muted hover:bg-surface-2 hover:text-fg">
                {c.label}
              </Link>
            </span>
          ))}
        </nav>
      ) : null}
      <div className="mt-3 flex gap-2">
        <Button size="sm" variant="ghost" onClick={() => void copyText(`${h.title}\n${h.snippet}`)}>
          <Copy className="size-3.5" /> Copy
        </Button>
        {grand ? (
          <Link
            href={`/raptor?focus=${grand.index}&node=${h.raptorLeaf ?? grand.index}&hits=${hits.join(',')}&q=${encodeURIComponent(q)}`}
            className="inline-flex h-7 items-center gap-1.5 rounded-md px-2.5 text-xs font-medium text-muted hover:bg-surface-2 hover:text-fg"
          >
            <Network className="size-3.5" /> Show in graph
          </Link>
        ) : null}
      </div>
    </li>
  );
}

function SearchInner() {
  const params = useSearchParams();
  const router = useRouter();
  const q = params.get('q') ?? '';
  const mode = (params.get('mode') as 'auto' | 'lexical' | null) ?? 'auto';
  const kind = (params.get('kind') as SearchKind | null) ?? 'all';
  const dim = (params.get('dim') as 'any' | 'overworld' | 'nether' | null) ?? 'any';
  const [text, setText] = useState(q);
  const [treeOpen, setTreeOpen] = useState(false);
  const set = (patch: Record<string, string | null>) => {
    const sp = new URLSearchParams(params.toString());
    for (const [k, v] of Object.entries(patch)) {
      if (!v) sp.delete(k);
      else sp.set(k, v);
    }
    router.replace(`/search?${sp.toString()}`, { scroll: false });
  };
  const res = useApi('search', { query: { q, mode, kind, dim, k: 30 } }, { enabled: q.length > 0 });
  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    set({ q: text.trim() || null });
  };
  const hitIds = (res.data?.hits ?? []).map((h) => h.raptorLeaf).filter((x): x is number => x !== null && x !== undefined);

  return (
    <PageBody>
      <form onSubmit={onSubmit} className="mb-4 flex flex-wrap items-center gap-2">
        <div className="relative min-w-64 flex-1">
          <SearchIcon className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-subtle" />
          <Input value={text} onChange={(e) => setText(e.target.value)} placeholder="What does the bot remember about…" aria-label="Search memories" className="h-11 pl-10 text-[15px]" autoFocus />
        </div>
        <Button type="submit" variant="primary" className="h-11 px-5">
          Search
        </Button>
      </form>
      <div className="mb-5 flex flex-wrap items-center gap-2">
        <Segmented
          label="Mode"
          size="sm"
          value={mode}
          onChange={(v) => set({ mode: v === 'auto' ? null : v })}
          options={[
            { value: 'auto', label: 'Like the bot', title: 'Hybrid when embeddings match, else lexical' },
            { value: 'lexical', label: 'Words only', title: 'FTS only' },
          ]}
        />
        <Select aria-label="Kind" value={kind} onChange={(e) => set({ kind: e.target.value === 'all' ? null : e.target.value })} className="h-8">
          {SearchKind.options.map((k) => (
            <option key={k} value={k}>
              {k === 'all' ? 'All kinds' : k}
            </option>
          ))}
        </Select>
        <Select aria-label="Dimension" value={dim} onChange={(e) => set({ dim: e.target.value === 'any' ? null : e.target.value })} className="h-8">
          <option value="any">Any dimension</option>
          <option value="overworld">Overworld</option>
          <option value="nether">Nether</option>
        </Select>
        {res.data ? (
          <span className="ml-auto text-xs text-muted">
            {res.data.hits.length} hits in {Math.round(res.data.elapsedMs)} ms · {res.data.modeNote}
            {res.data.embedded ? ' · query embedded' : ''}
          </span>
        ) : null}
      </div>
      {!q ? (
        <Card>
          <EmptyState icon={<SearchIcon className="size-5" />} title="Search the bot's memory" hint="This runs the same recall pipeline the bot uses: words, meaning and the RAPTOR tree, fused." />
        </Card>
      ) : res.isPending ? (
        <SkeletonLines lines={8} />
      ) : res.error ? (
        <Card>
          <ErrorState error={res.error} onRetry={() => void res.refetch()} />
        </Card>
      ) : res.data.hits.length === 0 ? (
        <Card>
          <EmptyState title={`Nothing for “${q}”`} hint="Try other words, or switch to all kinds." />
        </Card>
      ) : (
        <>
          <ol className="space-y-3">
            {res.data.hits.map((h) => (
              <Hit key={h.docId} h={h} hits={hitIds} q={q} />
            ))}
          </ol>
          {res.data.treeHits.length ? (
            <Card className="mt-5">
              <button onClick={() => setTreeOpen((o) => !o)} aria-expanded={treeOpen} className="flex w-full items-center gap-2 px-4 py-3 text-left text-[13px] font-medium text-fg">
                <ChevronDown className={cn('size-4 text-subtle transition-transform', treeOpen && 'rotate-180')} />
                Tree candidates before fusion <span className="font-normal text-subtle">({res.data.treeHits.length})</span>
              </button>
              {treeOpen ? (
                <ul className="divide-y divide-border border-t border-border text-xs">
                  {res.data.treeHits.map((t, i) => (
                    <li key={i} className="flex items-center gap-3 px-4 py-2">
                      <Badge>{t.list}</Badge>
                      <Link href={`/raptor?view=tree&node=${t.index}`} className="w-20 text-muted hover:text-fg">
                        {t.layer === 0 ? `leaf #${t.index}` : `L${t.layer} #${t.index}`}
                      </Link>
                      <span className="tabular w-14 text-subtle">{toNumber(t.score).toFixed(3)}</span>
                      <span className="min-w-0 flex-1 truncate text-fg">{t.preview}</span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </Card>
          ) : null}
        </>
      )}
    </PageBody>
  );
}

export default function SearchPage() {
  return (
    <Suspense fallback={null}>
      <SearchInner />
    </Suspense>
  );
}
