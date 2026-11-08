'use client';

import { useState } from 'react';
import { ImagePlus, LoaderCircle, Search } from 'lucide-react';
import Link from 'next/link';
import { useApi } from '@/lib/api/query';
import { errorText } from '@/lib/api/client';
import { Input } from '@/components/ui/primitives';
import { DeleteDesign } from '@/features/studio/DeleteDesign';

export const DESIGN_DRAG_TYPE = 'application/x-mineclaw-design';

/** Saved designs share the same placement path as the Designs page. Drag only carries the id, never untrusted cells. */
export function DesignLibrary({ onPick, onDeleted }: { onPick?: (id: number) => void; onDeleted?: (id: number) => void }) {
  const [q, setQ] = useState('');
  const [offset, setOffset] = useState(0);
  const designs = useApi('designs.list', { query: { q: q || undefined, limit: 24, offset } });
  return (
    <div className="space-y-2" data-testid="architect-designs">
      <label className="flex items-center gap-2">
        <Search className="size-4 text-muted" />
        <Input
          aria-label="Search saved designs"
          placeholder="Find a saved design…"
          value={q}
          onChange={(e) => {
            setQ(e.target.value);
            setOffset(0);
          }}
        />
      </label>
      <p className="text-[11px] text-muted">
        Drag a design onto the map, or select it and click to place. Q/E turns it.
      </p>
      {designs.isPending ? (
        <p className="flex items-center gap-2 text-xs text-muted">
          <LoaderCircle className="size-3 animate-spin" />
          Loading designs…
        </p>
      ) : null}
      {designs.error ? (
        <p role="alert" className="text-xs text-danger">
          {errorText(designs.error)}
        </p>
      ) : null}
      {designs.data && !designs.data.items.length ? (
        <p className="text-xs text-muted">No saved designs found.</p>
      ) : null}
      <div className="grid grid-cols-2 gap-2">
        {designs.data?.items.map((d) => (
          <article key={d.id} className="overflow-hidden rounded-md border border-border bg-surface-2">
            <button
              type="button"
              className="block w-full cursor-grab text-left active:cursor-grabbing"
              disabled={!onPick}
              draggable={!!onPick}
              onClick={() => onPick?.(d.id)}
              title={`Place ${d.name} (#${d.id})`}
              onDragStart={(e) => {
                e.dataTransfer.setData(DESIGN_DRAG_TYPE, String(d.id));
                e.dataTransfer.effectAllowed = 'copy';
              }}
            >
              {d.hasPicture ? (
                // Same-origin authenticated image endpoint; a design save invalidates its cache key.
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={`/api/designs/${d.id}/picture.png?v=${d.updated ?? d.rev}`}
                  alt={d.name}
                  loading="lazy"
                  draggable={false}
                  className="aspect-[8/5] w-full bg-black/20 object-contain"
                />
              ) : (
                <span className="flex aspect-[8/5] items-center justify-center gap-2 bg-surface-1 text-xs text-muted">
                  <ImagePlus className="size-5" />
                  No picture yet
                </span>
              )}
              <span className="block truncate px-2 pt-1.5 text-xs font-semibold">{d.name}</span>
              <span className="block px-2 pb-1.5 text-[10px] text-muted">
                #{d.id} · r{d.rev}
                {d.kind === 'mine' ? ' · Mine' : ''}
              </span>
            </button>
            <Link
              className="block border-t border-border px-2 py-1 text-[10px] text-muted hover:text-fg"
              href={`/designs/studio?id=${d.id}&picture=1`}
            >
              {d.hasPicture ? 'Change picture' : 'Take picture'} · Open designer
            </Link>
            <DeleteDesign id={d.id} name={d.name} onDeleted={() => { setOffset(0); onDeleted?.(d.id); }} />
          </article>
        ))}
      </div>
      {designs.data && designs.data.total > 24 ? (
        <div className="flex items-center justify-between text-xs">
          <button type="button" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 24))}>
            Previous
          </button>
          <span>
            {offset + 1}–{Math.min(offset + 24, designs.data.total)} of {designs.data.total}
          </span>
          <button type="button" disabled={offset + 24 >= designs.data.total} onClick={() => setOffset(offset + 24)}>
            Next
          </button>
        </div>
      ) : null}
    </div>
  );
}
