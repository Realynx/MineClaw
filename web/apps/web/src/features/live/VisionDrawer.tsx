'use client';

/**
 * What the AI saw: the exact vision sheets the model was sent (a debugging artifact, not a world view). A compact strip of
 * thumbnails and one selected sheet; long request/cost text sits behind developer details (explorer-spec §17.5).
 */
import { Copy, Download, Eye, Minus, Plus } from 'lucide-react';
import { useEffect, useState } from 'react';
import { apiUrl } from '@/lib/api/client';
import { useApi } from '@/lib/api/query';
import { usePrefs } from '@/lib/prefs';
import { ago, bytes, clock, cn, copyText, num } from '@/lib/utils';
import { Badge, Button, EmptyState, ErrorState, KeyValues, SkeletonLines } from '@/components/ui/primitives';
import { Drawer } from '@/components/ui/overlays';

export function VisionDrawer({ open, onOpenChange, body }: { open: boolean; onOpenChange: (o: boolean) => void; body: string }) {
  const { devDetails } = usePrefs();
  const list = useApi('vision.list', { params: { body } }, { enabled: open, refetchInterval: open ? 4000 : false });
  const [selected, setSelected] = useState<number | null>(null);
  const [zoom, setZoom] = useState(1);
  const sheets = list.data?.sheets ?? [];
  const sheet = sheets.find((s) => s.id === selected) ?? sheets[0];
  useEffect(() => {
    setZoom(1);
  }, [selected]);

  return (
    <Drawer open={open} onOpenChange={onOpenChange} title={`What ${body} saw`} subtitle="The exact images sent to the model" width="xl">
      {list.isPending ? (
        <SkeletonLines lines={6} className="p-5" />
      ) : list.error ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : sheets.length === 0 ? (
        <EmptyState icon={<Eye className="size-5" />} title="No vision sheets yet" hint={list.data.note ?? 'Sheets appear when the bot uses its `see` tool.'} />
      ) : (
        <div className="flex h-full min-h-0 flex-col">
          <div className="flex gap-2 overflow-x-auto border-b border-border px-5 py-3" role="listbox" aria-label="Vision sheets">
            {sheets.map((s) => (
              <button
                key={s.id}
                role="option"
                aria-selected={s.id === sheet?.id}
                onClick={() => setSelected(s.id)}
                className={cn('w-36 shrink-0 overflow-hidden rounded-lg border text-left transition-colors', s.id === sheet?.id ? 'border-accent ring-2 ring-[var(--ring)]' : 'border-border hover:border-border-strong')}
              >
                {/* eslint-disable-next-line @next/next/no-img-element -- exact bytes from the bot; no optimisation */}
                <img src={apiUrl('vision.png', { body, id: s.id })} alt="" className="aspect-[16/10] w-full bg-canvas object-cover" loading="lazy" />
                <div className="px-2 py-1.5">
                  <div className="tabular text-[11px] font-medium text-fg">{clock(s.at)}</div>
                  <div className="truncate text-[11px] text-subtle">
                    {s.kind} → {s.mode}
                  </div>
                </div>
              </button>
            ))}
          </div>
          {sheet ? (
            <div className="grid min-h-0 flex-1 grid-cols-1 gap-5 overflow-auto p-5 lg:grid-cols-[minmax(0,1fr)_18rem]">
              <div className="min-w-0">
                <div className="mb-2 flex items-center gap-1">
                  <Button size="icon-sm" variant="ghost" aria-label="Zoom out" onClick={() => setZoom((z) => Math.max(0.25, z / 1.25))}>
                    <Minus className="size-3.5" />
                  </Button>
                  <button className="tabular w-12 text-center text-xs text-muted" onClick={() => setZoom(1)} aria-label="Reset zoom">
                    {Math.round(zoom * 100)}%
                  </button>
                  <Button size="icon-sm" variant="ghost" aria-label="Zoom in" onClick={() => setZoom((z) => Math.min(6, z * 1.25))}>
                    <Plus className="size-3.5" />
                  </Button>
                  <span className="flex-1" />
                  <a href={apiUrl('vision.png', { body, id: sheet.id })} download={`vision-${body}-${sheet.id}.png`} className="inline-flex h-7 items-center gap-1.5 rounded-md px-2.5 text-xs font-medium text-muted hover:bg-surface-2 hover:text-fg">
                    <Download className="size-3.5" /> Save PNG
                  </a>
                  {sheet.call ? (
                    <Button size="sm" variant="ghost" onClick={() => void copyText(`see ${sheet.call ?? ''}`)}>
                      <Copy className="size-3.5" /> Copy as see{'{…}'}
                    </Button>
                  ) : null}
                </div>
                <div className="overflow-auto rounded-lg border border-border bg-canvas" style={{ maxHeight: '60vh' }}>
                  {/* eslint-disable-next-line @next/next/no-img-element -- exact bytes from the bot */}
                  <img
                    src={apiUrl('vision.png', { body, id: sheet.id })}
                    alt={`Vision sheet ${sheet.id}: ${sheet.legend}`}
                    style={{ width: `${sheet.width * zoom * 0.6}px`, maxWidth: zoom === 1 ? '100%' : 'none' }}
                    className="block"
                  />
                </div>
              </div>
              <div className="space-y-4 text-sm">
                <div>
                  <div className="mb-1 flex items-center gap-2">
                    <Badge tone={sheet.mode === 'perception' ? 'info' : 'accent'}>{sheet.mode}</Badge>
                    <span className="text-xs text-subtle">{ago(sheet.at)}</span>
                  </div>
                  {sheet.question ? <p className="text-fg">“{sheet.question}”</p> : null}
                </div>
                <div>
                  <div className="mb-1 text-[11px] font-medium tracking-wide text-subtle uppercase">Answer</div>
                  <p className={cn('text-fg', !sheet.answer && 'text-muted italic')}>
                    {sheet.answer ?? (sheet.mode === 'perception' ? 'Waiting for the answer…' : 'Direct mode: the brain saw the picture itself.')}
                  </p>
                </div>
                <div>
                  <div className="mb-1 text-[11px] font-medium tracking-wide text-subtle uppercase">Legend</div>
                  <p className="text-muted">{sheet.legend}</p>
                </div>
                {devDetails ? (
                  <KeyValues
                    className="text-xs"
                    rows={[
                      ['Views', sheet.views.join(' · ')],
                      ['Focus', sheet.focus ? `${sheet.focus.join(',')} r${sheet.radius}` : 'hive'],
                      ['Size', `${sheet.width}×${sheet.height}px · ${bytes(sheet.bytes)}`],
                      ['Image tokens', `~${num(sheet.tokens)}`],
                      ['Model', sheet.model ? `${sheet.model} · in ${num(sheet.modelInputTokens ?? 0)} / out ${num(sheet.modelOutputTokens ?? 0)}` : null],
                      ['sha256', <code key="sha" className="font-mono">{sheet.sha256.slice(0, 12)}</code>],
                    ]}
                  />
                ) : null}
              </div>
            </div>
          ) : null}
        </div>
      )}
    </Drawer>
  );
}
